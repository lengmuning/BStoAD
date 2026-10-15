using System.Diagnostics;
using Bstoad.Core.Models;
using Bstoad.Core.Options;
using Bstoad.Sync;
using Bstoad.Worker.Alerts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bstoad.Worker.Events;

/// <summary>
/// 事件监听（listen 命令，常驻，由计划任务 BSTOAD-Events 开机启动）：
/// 拉取事件服务（支持长轮询）→ 合并一小段时间 → 触发一次事件同步。
/// 北森事件只说明「谁变了」：员工数据按 userId 精确查询（ById）或按时间窗回查（TimeWindow），与定时同步同一套决策树。
/// 同步成功才推进进度；失败的事件保留并退避重试。每日全量同步照常运行，兜底丢失或处理失败的事件。
/// 内存：待处理事件只存汇总（userId 集合有上限），并定期自检，超限时告警退出由计划任务拉起。
/// </summary>
public sealed class EventListenerWorker : BackgroundService
{
    private static readonly TimeSpan LockWait = TimeSpan.FromMinutes(30);

    private readonly ISyncOrchestrator _orchestrator;
    private readonly IAlertService _alerts;
    private readonly EventFeedOptions _opt;
    private readonly SyncOptions _sync;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<EventListenerWorker> _logger;

    public EventListenerWorker(
        ISyncOrchestrator orchestrator,
        IAlertService alerts,
        IOptions<EventFeedOptions> options,
        IOptions<SyncOptions> syncOptions,
        IHostApplicationLifetime lifetime,
        ILogger<EventListenerWorker> logger)
    {
        _orchestrator = orchestrator;
        _alerts = alerts;
        _opt = options.Value;
        _sync = syncOptions.Value;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>待处理的一批事件：只保存汇总，不保存原始事件；userId 集合有上限，内存与员工人数同级。</summary>
    private sealed class PendingBatch
    {
        private const int MaxTypeKeys = 50;

        public int Count { get; private set; }
        public long FirstSeq { get; private set; }
        public long LastSeq { get; private set; }
        public DateTime MinReceivedUtc { get; private set; } = DateTime.MaxValue;
        public DateTime FirstSeenUtc { get; private set; }
        public DateTime LastSeenUtc { get; private set; }
        public bool HasOrgEvents { get; private set; }
        public bool HasEmpEvents { get; private set; }
        public HashSet<string> UserIds { get; } = new(StringComparer.Ordinal);
        public bool UserIdsOverflow { get; private set; }
        /// <summary>按 userId 查询不可用：这批改用时间窗回查。</summary>
        public bool ForceWindow { get; set; }
        /// <summary>
        /// 补查：上一批是「等太久被迫处理」的时间窗批次，处理时最近一段（安全偏移内）的变更北森还查不到，
        /// 过了合并等待后再按时间窗回查一次。本身不含事件，处理完不推进进度。
        /// </summary>
        public bool Sweep { get; private set; }
        private readonly Dictionary<string, int> _types = new(StringComparer.Ordinal);

        public bool IsEmpty => Count == 0 && !Sweep;

        public static PendingBatch SweepAfter(PendingBatch processed, DateTime coveredUntilUtc) => new()
        {
            Sweep = true,
            HasEmpEvents = processed.HasEmpEvents,
            FirstSeq = processed.LastSeq,
            LastSeq = processed.LastSeq,
            MinReceivedUtc = coveredUntilUtc,
            FirstSeenUtc = processed.LastSeenUtc,
            LastSeenUtc = processed.LastSeenUtc,
        };

        public void Add(FeedEvent e, DateTime nowUtc, int maxUserIds)
        {
            if (Count == 0) { FirstSeq = e.Seq; if (!Sweep) FirstSeenUtc = nowUtc; }
            Count++;
            LastSeq = e.Seq;
            LastSeenUtc = nowUtc;
            if (e.ReceivedAtUtc < MinReceivedUtc) MinReceivedUtc = e.ReceivedAtUtc;
            if (e.IsOrgEvent) HasOrgEvents = true;
            else
            {
                HasEmpEvents = true; // 未知类别按员工事件处理（宁可多查）
                if (!string.IsNullOrEmpty(e.UserId) && !UserIdsOverflow)
                {
                    if (UserIds.Count < maxUserIds) UserIds.Add(e.UserId);
                    else if (!UserIds.Contains(e.UserId)) UserIdsOverflow = true;
                }
            }
            var key = (e.IsOrgEvent ? "组织" : e.Category == "EmpEvent" ? "员工" : e.Category) + "·" + (e.Type ?? "?");
            if (_types.ContainsKey(key) || _types.Count < MaxTypeKeys) _types[key] = _types.GetValueOrDefault(key) + 1;
            else _types["其他"] = _types.GetValueOrDefault("其他") + 1;
        }

        public string Summary => string.Join("，", _types.Select(kv => $"{kv.Key}×{kv.Value}"));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ListenAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("事件监听已停止");
        }
        catch (Exception ex)
        {
            ExitState.Code = 1;
            _logger.LogError(ex, "事件监听异常退出");
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        if (!_opt.Enabled)
        {
            _logger.LogWarning("事件订阅未启用（EventFeed.Enabled=false），监听退出。可在图形界面「事件订阅」页开启。");
            return;
        }

        var poll = TimeSpan.FromSeconds(Math.Clamp(_opt.PollIntervalSeconds, 2, 600));
        // 时间窗回查的合并等待必须超过北森时间窗的安全偏移，否则窗口终点还没覆盖到这次变更
        var windowSettle = TimeSpan.FromSeconds(Math.Max(_opt.SettleSeconds, _sync.TimeWindowSafetyOffsetSeconds + 15));
        var byIdSettle = TimeSpan.FromSeconds(Math.Clamp(_opt.ByIdSettleSeconds, 1, 600));
        var maxWait = TimeSpan.FromSeconds(Math.Max(_opt.MaxBatchWaitSeconds, windowSettle.TotalSeconds));
        var lookback = TimeSpan.FromMinutes(Math.Clamp(_opt.LookbackMinutes, 1, 1440));
        var pageSize = Math.Clamp(_opt.PageSize, 1, 1000);
        var alertAfter = TimeSpan.FromMinutes(Math.Max(1, _opt.AlertAfterMinutes));
        var longPoll = TimeSpan.FromSeconds(Math.Clamp(_opt.LongPollSeconds, 0, 30));
        var orgCacheMaxAge = TimeSpan.FromMinutes(Math.Max(0, _opt.OrgCacheMinutes));
        var maxMemoryMb = Math.Max(64, _opt.MaxMemoryMB);
        var maxUserIds = Math.Clamp(_opt.MaxPendingUserIds, 1, 100_000);
        var byIdMode = _opt.UseLookupById;

        var url = EventFeedClient.NormalizeUrl(_opt.Url);
        using var client = new EventFeedClient(url, _opt.Token, longPoll + TimeSpan.FromSeconds(20));

        _logger.LogInformation(
            "事件监听启动：{Url} · 员工查询 {Lookup} · 长轮询 {LongPoll}s · 合并等待 {Settle}s · 组织树缓存 {OrgCache} 分钟 · {Mode}",
            url, byIdMode ? "按 userId 精确查询" : "按时间窗回查", longPoll.TotalSeconds,
            (byIdMode ? byIdSettle : windowSettle).TotalSeconds, orgCacheMaxAge.TotalMinutes,
            _sync.DryRun ? "干跑（不写 AD、不保存进度）" : "正式写入 AD");
        if (_sync.OnlyEmployeeIds is { Length: > 0 })
            _logger.LogWarning("[试点模式] 只处理工号 {Ids}", string.Join(",", _sync.OnlyEmployeeIds));

        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var configStamp = SafeWriteTime(configPath);
        var configChangeLogged = false;

        long? cursor = null;
        var saved = EventCursorStore.Load();
        if (saved != null && string.Equals(EventFeedClient.NormalizeUrl(saved.Url), url, StringComparison.OrdinalIgnoreCase))
        {
            cursor = saved.Seq;
            _logger.LogInformation("从上次处理到的位置继续：序号 {Seq}（{At:yyyy-MM-dd HH:mm:ss}）", saved.Seq, saved.UpdatedAt);
        }
        else if (saved != null)
        {
            _logger.LogWarning("事件服务地址已变更（{Old} → {New}），原进度作废，从新地址的当前最新事件开始", saved.Url, url);
        }

        var pending = new PendingBatch();
        DateTime? downSince = null;
        var downAlerted = false;
        var batchFailures = 0;
        var retryAt = DateTime.MinValue;
        var byIdFailures = 0;

        bool UseById(PendingBatch b) =>
            byIdMode && !b.ForceWindow && !b.Sweep && b.HasEmpEvents && !b.HasOrgEvents && !b.UserIdsOverflow && b.UserIds.Count > 0;

        // 本批何时处理：最后一条事件之后安静了 settle，或从第一条起已等了 maxWait；退避期间不早于 retryAt
        DateTime? DueAt(PendingBatch b)
        {
            if (b.IsEmpty) return null;
            var settle = UseById(b) ? byIdSettle : windowSettle;
            var due = b.LastSeenUtc + settle;
            var cap = b.FirstSeenUtc + maxWait;
            if (cap < due) due = cap;
            return due < retryAt ? retryAt : due;
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await MemoryExceededAsync(maxMemoryMb, ct)) return;

            var needDelay = false; // 本轮没能借长轮询等待（出错 / 服务端不支持 / 名额已满）时按轮询间隔等
            var morePages = false;

            // ---- 拉取 ----
            try
            {
                if (cursor == null)
                {
                    var st = await client.GetStatusAsync(ct);
                    cursor = st.MaxSeq;
                    SaveCursor(url, st.MaxSeq);
                    _logger.LogInformation("首次连接事件服务：从当前最新序号 {Seq} 开始监听（此前的变更由定时同步处理）", st.MaxSeq);
                }
                else
                {
                    var after = pending.Count == 0 ? cursor.Value : pending.LastSeq;
                    var wait = longPoll;
                    if (DueAt(pending) is { } due)
                    {
                        var untilDue = due - DateTime.UtcNow;
                        wait = untilDue <= TimeSpan.Zero ? TimeSpan.Zero : untilDue < longPoll ? untilDue : longPoll;
                    }
                    var waitSeconds = (int)Math.Ceiling(wait.TotalSeconds);
                    var sw = Stopwatch.StartNew();
                    var page = await client.GetEventsAsync(after, pageSize, waitSeconds, ct);
                    sw.Stop();

                    if (page.MaxSeq < after)
                    {
                        _logger.LogWarning("事件服务的最新序号 {Max} 小于本地进度 {After}，服务端数据可能被重建，改为从头读取它保留的事件",
                            page.MaxSeq, after);
                        cursor = 0;
                        pending = new PendingBatch();
                        SaveCursor(url, 0);
                        morePages = true;
                    }
                    else
                    {
                        // 序号断档 = 监听停机期间有事件超过保留期被清理，这部分只能靠每日全量补齐
                        if (after > 0 && page.MinSeq > after + 1)
                            await ReportExpiredAsync(page.MinSeq - after - 1, page.RetentionDays, ct);
                        if (page.Events.Count > 0)
                        {
                            var now = DateTime.UtcNow;
                            foreach (var e in page.Events) pending.Add(e, now, maxUserIds);
                            _logger.LogInformation("收到 {Count} 条事件（序号 {From}~{To}）：{Summary}",
                                page.Events.Count, page.Events[0].Seq, page.Events[^1].Seq, SummarizePage(page.Events));
                            morePages = page.Events.Count >= pageSize;
                        }
                        else if (waitSeconds >= 2 && sw.Elapsed < TimeSpan.FromSeconds(waitSeconds - 1.5))
                        {
                            needDelay = true; // 服务端没有等待：旧版本不支持长轮询，或同时等待的名额已满
                        }
                        else if (longPoll == TimeSpan.Zero)
                        {
                            needDelay = true;
                        }
                    }
                }

                if (downSince != null)
                {
                    _logger.LogInformation("事件服务已恢复连接（中断约 {Min} 分钟）", (int)Math.Ceiling((DateTime.UtcNow - downSince.Value).TotalMinutes));
                    downSince = null;
                    downAlerted = false;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                needDelay = true;
                var now = DateTime.UtcNow;
                if (downSince == null)
                {
                    downSince = now;
                    _logger.LogWarning("连接事件服务失败：{Msg}（每 {Poll}s 重试，恢复前不再重复记录）", ex.Message, poll.TotalSeconds);
                }
                if (!downAlerted && now - downSince.Value >= alertAfter)
                {
                    downAlerted = true;
                    await ReportDownAsync(url, now - downSince.Value, ex.Message, ct);
                }
            }

            if (morePages) continue;

            // ---- 处理 ----
            if (DueAt(pending) is { } dueAt && DateTime.UtcNow >= dueAt)
            {
                var byId = UseById(pending);
                var startedUtc = DateTime.UtcNow;
                // 不是因为「安静了 settle」而是因为「等太久」被迫处理的时间窗批次：最近一段变更可能还查不到
                var forcedEarly = !byId && pending.Count > 0 && startedUtc - pending.LastSeenUtc < windowSettle;
                var outcome = await ProcessBatchAsync(pending, byId, lookback, orgCacheMaxAge, ct);
                if (outcome == BatchOutcome.Done)
                {
                    if (pending.Count > 0)
                    {
                        cursor = pending.LastSeq;
                        SaveCursor(url, cursor.Value);
                    }
                    pending = forcedEarly && pending.HasEmpEvents
                        ? PendingBatch.SweepAfter(pending, startedUtc - TimeSpan.FromSeconds(_sync.TimeWindowSafetyOffsetSeconds))
                        : new PendingBatch();
                    batchFailures = 0;
                    retryAt = DateTime.MinValue;
                    if (byId) byIdFailures = 0;
                }
                else if (outcome == BatchOutcome.UseTimeWindow)
                {
                    pending.ForceWindow = true; // 重新按时间窗的合并等待计时，避开北森写入延迟
                    if (++byIdFailures >= 3 && byIdMode)
                    {
                        byIdMode = false;
                        _logger.LogWarning("按 userId 查询连续 {N} 次不可用，本次运行改为按时间窗回查。请用 probe-employee 核对北森接口配置后再重启事件监听", byIdFailures);
                    }
                }
                else
                {
                    batchFailures++;
                    var delay = TimeSpan.FromMinutes(Math.Min(30, 1 << Math.Min(batchFailures - 1, 5)));
                    retryAt = DateTime.UtcNow + delay;
                    _logger.LogWarning("本批事件未处理成功（第 {N} 次），{Min} 分钟后重试；事件保留，不会丢失", batchFailures, delay.TotalMinutes);
                }
            }

            if (!configChangeLogged && SafeWriteTime(configPath) != configStamp)
            {
                configChangeLogged = true;
                _logger.LogWarning("检测到配置文件已修改：新配置需要重启事件监听才生效（图形界面保存配置后会提示重启）");
            }

            if (needDelay)
            {
                var delay = poll;
                if (DueAt(pending) is { } d)
                {
                    var untilDue = d - DateTime.UtcNow;
                    if (untilDue < delay) delay = untilDue < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : untilDue;
                }
                await Task.Delay(delay, ct);
            }
        }
    }

    private enum BatchOutcome { Done, Retry, UseTimeWindow }

    /// <summary>触发一次事件同步。</summary>
    private async Task<BatchOutcome> ProcessBatchAsync(PendingBatch batch, bool byId, TimeSpan lookback,
        TimeSpan orgCacheMaxAge, CancellationToken ct)
    {
        var request = new EventBatchRequest
        {
            WindowStartUtc = batch.MinReceivedUtc - lookback,
            IncludeEmployees = batch.HasEmpEvents,
            HasOrgEvents = batch.HasOrgEvents,
            UserIds = batch.UserIds.ToArray(),
            LookupByIds = byId,
            OrgCacheMaxAge = orgCacheMaxAge,
        };
        if (batch.Sweep && batch.Count == 0)
            _logger.LogInformation("补查：上一批是等待过久被迫处理的，现在回查北森 {Start:yyyy-MM-dd HH:mm:ss} 以来的变更，补上当时还查不到的部分",
                request.WindowStartUtc.ToLocalTime());
        else
            _logger.LogInformation("处理 {Count} 条事件（序号 {From}~{To}）：{Summary} → {How}",
                batch.Count, batch.FirstSeq, batch.LastSeq, batch.Summary,
                byId ? $"按 userId 精确查询 {request.UserIds.Count} 人"
                     : $"回查北森 {request.WindowStartUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} 以来的变更");

        SyncResult? result = null;
        Exception? fatal = null;
        try
        {
            result = await SyncLock.RunExclusiveAsync(() => _orchestrator.RunEventBatchAsync(request, ct), LockWait, ct);
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning("{Msg}", ex.Message);
            return BatchOutcome.Retry;
        }
        catch (EventLookupUnavailableException ex)
        {
            _logger.LogWarning("{Msg}；这批事件改用时间窗回查", ex.Message);
            return BatchOutcome.UseTimeWindow;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            fatal = ex;
            _logger.LogError(ex, "事件触发的同步失败");
        }

        await _alerts.NotifyAsync(result, fatal, ct, "事件触发");
        if (fatal != null || result == null) return BatchOutcome.Retry;

        // AD 绑定失败时本轮所有 AD 操作都被跳过：不能确认这批事件，等 AD 恢复后重试
        if (result.Events.Any(e => e.Category == SyncEventCategory.AdBindFailed)) return BatchOutcome.Retry;

        if (request.IncludeEmployees && result.EmployeesFetched == 0)
            _logger.LogWarning("本批有员工事件，但北森没有返回对应员工（可能是北森写入延迟，或变更不涉及员工主数据）；每日全量同步会兜底");
        return BatchOutcome.Done;
    }

    /// <summary>内存自检：超限时告警并退出（退出码 1），由计划任务在 5 分钟内重新拉起；未处理的事件不会丢。</summary>
    private async Task<bool> MemoryExceededAsync(int maxMemoryMb, CancellationToken ct)
    {
        long usedMb;
        using (var p = Process.GetCurrentProcess()) usedMb = p.WorkingSet64 / 1024 / 1024;
        if (usedMb <= maxMemoryMb) return false;

        ExitState.Code = 1;
        _logger.LogError("事件监听内存占用 {Used}MB 超过上限 {Max}MB，主动退出，由计划任务重新拉起", usedMb, maxMemoryMb);
        var r = new SyncResult();
        r.AddEvent(SyncEventLevel.Critical, SyncEventCategory.ListenerMemory, "memory", "事件监听",
            $"内存占用 {usedMb}MB 超过上限 {maxMemoryMb}MB，已主动退出，计划任务会在 5 分钟内重新拉起；未处理的事件不会丢失。请把 logs/bstoad-listen-*.log 发给维护人员排查。");
        await _alerts.NotifyAsync(r, null, ct, "事件监听");
        return true;
    }

    private void SaveCursor(string url, long seq)
    {
        if (_sync.DryRun) return; // 干跑不保存进度：之后正式运行会重新处理这些事件
        try { EventCursorStore.Save(url, seq); }
        catch (Exception ex) { _logger.LogWarning(ex, "保存事件处理进度失败（重启后会重复处理部分事件，结果不受影响）"); }
    }

    private Task ReportDownAsync(string url, TimeSpan duration, string error, CancellationToken ct)
    {
        var minutes = (int)duration.TotalMinutes;
        _logger.LogError("已连续 {Min} 分钟无法连接事件服务 {Url}：{Err}", minutes, url, error);
        var r = new SyncResult();
        r.AddEvent(SyncEventLevel.Critical, SyncEventCategory.EventFeedDown, "feed", "事件服务",
            $"已连续 {minutes} 分钟无法连接 {url}：{error}。北森的变更暂时只能靠定时同步（每小时增量 / 每日全量）。");
        return _alerts.NotifyAsync(r, null, ct, "事件监听");
    }

    private Task ReportExpiredAsync(long missed, int retentionDays, CancellationToken ct)
    {
        _logger.LogWarning("有 {N} 条事件在处理前已超过保留期（{Days} 天）被事件服务清理，这部分变更由每日全量同步补齐", missed, retentionDays);
        var r = new SyncResult();
        r.AddEvent(SyncEventLevel.Warning, SyncEventCategory.EventsExpired, "expired", "事件服务",
            $"有 {missed} 条事件在处理前已超过保留期（{retentionDays} 天）被清理（事件监听停机过久），这部分变更由每日全量同步补齐。");
        return _alerts.NotifyAsync(r, null, ct, "事件监听");
    }

    private static string SummarizePage(IEnumerable<FeedEvent> events) =>
        string.Join("，", events
            .GroupBy(e => (e.IsOrgEvent ? "组织" : e.Category == "EmpEvent" ? "员工" : e.Category) + "·" + (e.Type ?? "?"))
            .Select(g => $"{g.Key}×{g.Count()}"));

    private static DateTime SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }
}
