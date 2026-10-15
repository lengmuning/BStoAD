using System.Text;
using System.Text.Json;
using Bstoad.Core.Models;
using Bstoad.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bstoad.Worker.Alerts;

public interface IAlertService
{
    /// <summary>同步结束后调用。永不抛异常：告警失败只记日志，不影响同步结果与退出码。</summary>
    /// <param name="mode">消息里显示的运行方式（如「事件触发」）；为空时按配置显示「全量」/「增量」。</param>
    Task NotifyAsync(SyncResult? result, Exception? fatal, CancellationToken ct = default, string? mode = null);
}

public sealed class AlertService : IAlertService
{
    private const int MaxItemsPerCategory = 5;

    private readonly AlertOptions _opt;
    private readonly SyncOptions _sync;
    private readonly AlertSender _sender;
    private readonly ILogger<AlertService> _logger;

    public AlertService(IOptions<AlertOptions> opt, IOptions<SyncOptions> sync, AlertSender sender, ILogger<AlertService> logger)
    {
        _opt = opt.Value;
        _sync = sync.Value;
        _sender = sender;
        _logger = logger;
    }

    public async Task NotifyAsync(SyncResult? result, Exception? fatal, CancellationToken ct = default, string? mode = null)
    {
        try
        {
            if (!_opt.Enabled || !AlertSender.AnyChannelEnabled(_opt)) return;
            if (_sync.DryRun && !_opt.AlertOnDryRun) return;

            var events = result?.Events.ToList() ?? new List<SyncEvent>();
            if (fatal != null)
                events.Add(new SyncEvent
                {
                    Level = SyncEventLevel.Critical, Category = SyncEventCategory.Fatal, Key = "fatal",
                    Name = "同步程序", Detail = "同步异常中止：" + fatal.Message,
                });

            var min = ParseLevel(_opt.MinLevel);
            var relevant = events.Where(e => e.Level >= min).ToList();

            var state = LoadState();
            var now = DateTime.UtcNow;
            var window = TimeSpan.FromHours(Math.Max(0, _opt.RepeatSuppressHours));
            var fresh = relevant
                .Where(e => !state.TryGetValue(Fingerprint(e), out var last) || now - last >= window)
                .ToList();

            var summary = _opt.SendFullSyncSummary && _sync.FullPull && result != null;
            if (fresh.Count == 0 && !summary) return;

            var title = fresh.Any(e => e.Level == SyncEventLevel.Critical) ? $"{_opt.Title} · 严重告警"
                      : fresh.Count > 0 ? $"{_opt.Title} · 需要关注"
                      : $"{_opt.Title} · 同步汇总";
            var body = Compose(result, fresh, relevant.Count - fresh.Count, events, summary, mode);

            var results = await _sender.SendAsync(_opt, title, body, ct);
            foreach (var r in results)
            {
                if (r.Ok) _logger.LogInformation("告警已发送: {Channel}", r.Channel);
                else _logger.LogWarning("告警发送失败: {Channel} — {Msg}", r.Channel, r.Message);
            }

            // 至少一个渠道成功才记为「已提醒」，否则下一轮重试
            if (results.Any(r => r.Ok))
            {
                foreach (var e in fresh) state[Fingerprint(e)] = now;
                var keep = TimeSpan.FromDays(7) > window * 2 ? TimeSpan.FromDays(7) : window * 2;
                foreach (var k in state.Where(kv => now - kv.Value > keep).Select(kv => kv.Key).ToList())
                    state.Remove(k);
                SaveState(state);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "告警处理异常（不影响同步）");
        }
    }

    private string Compose(SyncResult? r, List<SyncEvent> fresh, int suppressed, List<SyncEvent> all, bool summary, string? mode)
    {
        var sb = new StringBuilder();
        mode ??= _sync.FullPull ? "全量" : "增量";
        sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm}（{mode}{(_sync.DryRun ? "·干跑" : "")}）");
        sb.AppendLine($"服务器：{Environment.MachineName}");
        // 事件监听自身的告警（如连不上事件服务）不是一次同步，没有概况可言
        if (r != null && (r.OrgsFetched > 0 || r.EmployeesFetched > 0 || r.Failures > 0))
            sb.AppendLine($"概况：新建 {r.UsersCreated} · 更新 {r.UsersUpdated} · 移动 {r.UsersMoved} · 禁用 {r.UsersDisabled + r.LeaversReconciled} · 失败 {r.Failures}");

        foreach (var level in new[] { SyncEventLevel.Critical, SyncEventLevel.Warning, SyncEventLevel.Info })
        {
            var items = fresh.Where(e => e.Level == level).ToList();
            if (items.Count == 0) continue;
            sb.AppendLine();
            sb.AppendLine($"【{LevelText(level)}】");
            foreach (var g in items.GroupBy(e => e.Category))
            {
                var list = g.ToList();
                sb.AppendLine($"· {g.Key}（{list.Count}）");
                foreach (var e in list.Take(MaxItemsPerCategory))
                    sb.AppendLine($"    {e.Name}：{e.Detail}");
                if (list.Count > MaxItemsPerCategory)
                    sb.AppendLine($"    ……其余 {list.Count - MaxItemsPerCategory} 条");
            }
        }

        if (summary)
        {
            var open = all.GroupBy(e => e.Category).Select(g => $"{g.Key} {g.Count()}").ToList();
            sb.AppendLine();
            sb.AppendLine(open.Count == 0 ? "待处理事项：无" : "待处理事项：" + string.Join(" · ", open));
        }

        if (suppressed > 0)
            sb.AppendLine($"\n另有 {suppressed} 条在 {_opt.RepeatSuppressHours} 小时内已提醒过，未重复列出。");
        sb.AppendLine("\n详情：在服务器上打开 Bstoad.Worker.exe →「待处理」页。");
        return sb.ToString();
    }

    public static SyncEventLevel ParseLevel(string s) => s?.Trim().ToLowerInvariant() switch
    {
        "info" => SyncEventLevel.Info,
        "critical" => SyncEventLevel.Critical,
        _ => SyncEventLevel.Warning,
    };

    private static string LevelText(SyncEventLevel l) => l switch
    {
        SyncEventLevel.Critical => "严重",
        SyncEventLevel.Warning => "需要关注",
        _ => "提示",
    };

    private static string Fingerprint(SyncEvent e) => e.Category + "|" + e.Key;

    private static string StatePath()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "state");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "alert-state.json");
    }

    private static Dictionary<string, DateTime> LoadState()
    {
        try
        {
            var f = StatePath();
            if (File.Exists(f))
                return JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(f)) ?? new();
        }
        catch { /* 损坏则从空开始 */ }
        return new();
    }

    private static void SaveState(Dictionary<string, DateTime> state)
    {
        try { File.WriteAllText(StatePath(), JsonSerializer.Serialize(state)); }
        catch { /* 写失败最多导致重复提醒 */ }
    }
}
