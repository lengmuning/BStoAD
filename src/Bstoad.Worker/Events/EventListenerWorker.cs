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
/// 轮询事件服务 → 收到北森事件后合并一小段时间 → 以「本批最早事件时间」为起点触发一次增量同步。
/// 北森事件只说明「谁变了」，数据仍按时间窗回查北森，与每小时增量同一套决策树。
/// 同步成功才推进进度；失败的事件保留并退避重试。每日全量同步照常运行，兜底丢失或处理失败的事件。
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

        var url = EventFeedClient.NormalizeUrl(_opt.Url);
        using var client = new EventFeedClient(url, _opt.Token);

        var poll = TimeSpan.FromSeconds(Math.Clamp(_opt.PollIntervalSeconds, 2, 600));
        // 合并等待必须超过北森时间窗的安全偏移，否则回查时窗口终点还没覆盖到这次变更
        var settle = TimeSpan.FromSeconds(Math.Max(_opt.SettleSeconds, _sync.TimeWindowSafetyOffsetSeconds + 15));
        var maxWait = TimeSpan.FromSeconds(Math.Max(_opt.MaxBatchWaitSeconds, settle.TotalSeconds));
        var lookback = TimeSpan.FromMinutes(Math.Clamp(_opt.LookbackMinutes, 1, 1440));
        var pageSize = Math.Clamp(_opt.PageSize, 1, 1000);
        var alertAfter = TimeSpan.FromMinutes(Math.Max(1, _opt.AlertAfterMinutes));

        _logger.LogInformation("事件监听启动：{Url} · 轮询 {Poll}s · 合并等待 {Settle}s · {Mode}",
            url, poll.TotalSeconds, settle.TotalSeconds, _sync.DryRun ? "干跑（不写 AD、不保存进度）" : "正式写入 AD");
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

        var pending = new List<FeedEvent>();
        DateTime firstSeen = default, lastSeen = default;
        DateTime? downSince = null;
        var downAlerted = false;
        var batchFailures = 0;
        var retryAt = DateTime.MinValue;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
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
                    var after = pending.Count > 0 ? pending[^1].Seq : cursor.Value;
                    var page = await client.GetEventsAsync(after, pageSize, ct);
                    if (page.MaxSeq < after)
                    {
                        _logger.LogWarning("事件服务的最新序号 {Max} 小于本地进度 {After}，服务端数据可能被重建，改为从头读取它保留的事件",
                            page.MaxSeq, after);
                        cursor = 0;
                        pending.Clear();
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
                            if (pending.Count == 0) firstSeen = now;
                            lastSeen = now;
                            pending.AddRange(page.Events);
                            _logger.LogInformation("收到 {Count} 条事件（序号 {From}~{To}）：{Summary}",
                                page.Events.Count, page.Events[0].Seq, page.Events[^1].Seq, Summarize(page.Events));
                            morePages = page.Events.Count >= pageSize;
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

            // ---- 处理：最后一条事件之后安静了 settle，或从第一条起已等了 maxWait ----
            if (pending.Count > 0 && DateTime.UtcNow >= retryAt)
            {
                var now = DateTime.UtcNow;
                if (now - lastSeen >= settle || now - firstSeen >= maxWait)
                {
                    if (await ProcessBatchAsync(pending, lookback, ct))
                    {
                        cursor = pending[^1].Seq;
                        SaveCursor(url, cursor.Value);
                        pending.Clear();
                        batchFailures = 0;
                        retryAt = DateTime.MinValue;
                    }
                    else
                    {
                        batchFailures++;
                        var delay = TimeSpan.FromMinutes(Math.Min(30, 1 << Math.Min(batchFailures - 1, 5)));
                        retryAt = DateTime.UtcNow + delay;
                        _logger.LogWarning("本批事件未处理成功（第 {N} 次），{Min} 分钟后重试；事件保留，不会丢失", batchFailures, delay.TotalMinutes);
                    }
                }
            }

            if (!configChangeLogged && SafeWriteTime(configPath) != configStamp)
            {
                configChangeLogged = true;
                _logger.LogWarning("检测到配置文件已修改：新配置需要重启事件监听才生效（图形界面保存配置后会提示重启）");
            }

            await Task.Delay(poll, ct);
        }
    }

    /// <summary>触发一次事件时间窗的增量同步。返回 false 表示这批事件需要稍后重试。</summary>
    private async Task<bool> ProcessBatchAsync(List<FeedEvent> batch, TimeSpan lookback, CancellationToken ct)
    {
        var start = batch.Min(e => e.ReceivedAtUtc) - lookback;
        var includeEmployees = batch.Any(e => !e.IsOrgEvent);
        _logger.LogInformation("处理 {Count} 条事件（序号 {From}~{To}）：{Summary} → 回查北森 {Start:yyyy-MM-dd HH:mm:ss} 以来的变更",
            batch.Count, batch[0].Seq, batch[^1].Seq, Summarize(batch), start.ToLocalTime());

        SyncResult? result = null;
        Exception? fatal = null;
        try
        {
            result = await SyncLock.RunExclusiveAsync(
                () => _orchestrator.RunEventBatchAsync(start, includeEmployees, ct), LockWait, ct);
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning("{Msg}", ex.Message);
            return false;
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
        if (fatal != null || result == null) return false;

        // AD 绑定失败时本轮所有 AD 操作都被跳过：不能确认这批事件，等 AD 恢复后重试
        if (result.Events.Any(e => e.Category == SyncEventCategory.AdBindFailed)) return false;

        if (includeEmployees && result.EmployeesFetched == 0)
            _logger.LogWarning("本批有员工事件，但北森时间窗内没有查到变动的员工（可能是北森写入延迟，或变更不涉及员工主数据）；每日全量同步会兜底");
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

    private static string Summarize(IEnumerable<FeedEvent> events) =>
        string.Join("，", events
            .GroupBy(e => (e.IsOrgEvent ? "组织" : e.Category == "EmpEvent" ? "员工" : e.Category) + "·" + (e.Type ?? "?"))
            .Select(g => $"{g.Key}×{g.Count()}"));

    private static DateTime SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }
}
