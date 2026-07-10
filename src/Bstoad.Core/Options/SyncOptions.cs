namespace Bstoad.Core.Options;

public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    public bool DryRun { get; set; } = true;
    public bool RunOnceAndExit { get; set; } = true;

    public int IncrementalIntervalMinutes { get; set; } = 60;
    public string FullSyncCron { get; set; } = "0 0 2 * * ?";

    public int TimeWindowSafetyOffsetSeconds { get; set; } = 60;
    public int InitialBackfillDays { get; set; } = 7;

    public bool EnsureOuTree { get; set; } = true;
    public bool ReconcileLeavers { get; set; } = false;
    public bool FullPull { get; set; } = false;
    public int SampleLogCount { get; set; } = 5;

    public bool OrgsOnly { get; set; } = false;
    public bool UsersOnly { get; set; } = false;

    // 选定某个北森组织作为同步根：只同步它的子树，其直接子级挂在 BaseUserOu 下
    // （去掉「组织机构」之类的顶层空壳）。留空 = 同步整棵树。
    public string RootOrgId { get; set; } = "";

    // 北森「离职人员」部门的 OrgId：员工挂在该部门 = 离职（移入对应 OU + 禁用），
    // 不依赖 empStatus。留空则只按 empStatus 判断。
    public string LeaverOrgId { get; set; } = "";

    // 试点过滤：非空时只处理这些工号，其他员工全部跳过；离职差集自动禁用
    public string[] OnlyEmployeeIds { get; set; } = Array.Empty<string>();

    // 只处理离职员工（挂在 LeaverOrgId 部门或 empStatus 为离职值），在职员工全部跳过
    public bool LeaversOnly { get; set; } = false;

    // ---- 安全阀 ----
    // 差集熔断：单轮拟禁用/归档数超过 max(MaxLeaversPerRun, 纳管总数×MaxLeaverRatio) 时中止差集
    public int MaxLeaversPerRun { get; set; } = 10;
    public double MaxLeaverRatio { get; set; } = 0.05;
    // 拉取量哨兵：全量员工数低于上一轮的该比例时，视为数据源异常，拒绝跑差集
    public double MinPullRatioVsLastRun { get; set; } = 0.7;
}
