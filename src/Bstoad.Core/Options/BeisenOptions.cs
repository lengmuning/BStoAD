namespace Bstoad.Core.Options;

public sealed class BeisenOptions
{
    public const string SectionName = "Beisen";

    public string BaseUrl { get; set; } = "https://openapi.italent.cn";
    public string TokenEndpoint { get; set; } = "/token";

    public string AppKey { get; set; } = "";
    public string AppSecret { get; set; } = "";

    // 官方接口（2026-07 文档确认）；留成配置项，路径变更时不用重新出包
    public string OrgTimeWindowPath { get; set; } = "/TenantBaseExternal/api/v5/Organization/GetByTimeWindow";
    public string EmployeeTimeWindowPath { get; set; } = "/TenantBaseExternal/api/v5/Employee/GetByTimeWindow";

    // 全量拉取的起始日期（时间窗接口无「拉全部」语义，用大窗口分片覆盖历史）
    public string FullPullStartDate { get; set; } = "2018-01-01";
    // 官方建议单窗 ≤90 天
    public int TimeWindowChunkDays { get; set; } = 90;
    // 官方要求每批 ≤300
    public int PageCapacity { get; set; } = 300;

    // 时间窗参数格式；服务端报 invalid datetime format 时在此调整（无需重新出包）
    public string TimeWindowDateFormat { get; set; } = "yyyy-MM-ddTHH:mm:ss";

    // 追加到请求体的额外参数（值写 JSON 字面量，如 "[0,2]"、"true"、"1"；解析失败按字符串发送）。
    // 用于补接口的必填过滤参数而无需重新出包。
    public Dictionary<string, string> OrgExtraParams { get; set; } = new();
    public Dictionary<string, string> EmployeeExtraParams { get; set; } = new();

    public int TokenRefreshSkewSeconds { get; set; } = 300;

    // 是否在日志输出北森原始响应样本（含身份证号/住址等 PII）。
    // 默认关闭；仅在排查字段映射问题时临时开启，排查完立即关闭并清理日志。
    public bool LogRawSamples { get; set; } = false;

    // 职位字段的候选键（按顺序取第一个非空值）。北森租户字段命名不一时在此调整，无需重新出包
    public string[] TitleFields { get; set; } =
    {
        "oIdJobPost_TranslateResult", "OIdJobPost_TranslateResult", "jobPostName", "JobPostName",
        "positionName", "PositionName", "Position", "position", "Title", "title", "jobTitle",
    };

    public DateTime FullPullStartUtc()
    {
        if (DateTime.TryParse(FullPullStartDate, out var d))
            return DateTime.SpecifyKind(d, DateTimeKind.Utc).AddHours(-8); // 配置按北京时间理解
        return new DateTime(2018, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }
}
