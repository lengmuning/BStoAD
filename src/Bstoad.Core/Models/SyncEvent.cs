namespace Bstoad.Core.Models;

public enum SyncEventLevel { Info = 0, Warning = 1, Critical = 2 }

/// <summary>
/// 同步过程中需要人关注的事项。同时用于：图形界面的「待处理」面板（state/pending.json）
/// 和告警推送（按 Category+Key 去重）。
/// </summary>
public sealed class SyncEvent
{
    public SyncEventLevel Level { get; set; }
    public string Category { get; set; } = "";
    /// <summary>去重键（工号 / OrgId / DN 等），同类别内唯一。</summary>
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";
    public string? Dn { get; set; }
}

public static class SyncEventCategory
{
    public const string AdBindFailed = "AD绑定失败";
    public const string PullSentinel = "拉取哨兵";
    public const string CircuitBreaker = "差集熔断";
    public const string Fatal = "同步异常";
    public const string Failure = "处理失败";
    public const string ScenarioE = "复职待启用";
    public const string OrgAbandoned = "部门撤销";
    public const string Protected = "保护跳过";
    public const string SamConflict = "SAM冲突";
    public const string ScenarioARejected = "接管拒绝";
    public const string ScenarioDSuspect = "疑似错填工号";
    public const string EmptyEmployeeId = "工号为空";
    public const string PendingHireSkipped = "待入职未建号";
    public const string EventFeedDown = "事件订阅中断";
    public const string EventsExpired = "事件已过期";
}

/// <summary>最近一次全量同步写出的待处理清单（state/pending.json）。</summary>
public sealed class PendingReport
{
    public DateTime GeneratedAt { get; set; }
    public bool DryRun { get; set; }
    public List<SyncEvent> Events { get; set; } = new();
}
