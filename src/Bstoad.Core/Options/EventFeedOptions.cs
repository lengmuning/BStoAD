namespace Bstoad.Core.Options;

/// <summary>
/// 事件订阅（v1.2 起）：从事件服务拉取北森事件，有变动时触发增量同步。默认关闭，关闭时行为与 v1.1 完全一致。
/// 事件服务即北森事件接收端的内网拉取接口（如 http://事件服务器:8601），地址可在图形界面「事件订阅」页修改。
/// </summary>
public sealed class EventFeedOptions
{
    public const string SectionName = "EventFeed";

    public bool Enabled { get; set; } = false;

    /// <summary>事件服务地址，形如 http://主机:8601（不含 /v1/...）。修改后监听从新地址的当前最新事件开始。</summary>
    public string Url { get; set; } = "";

    /// <summary>访问令牌（事件服务 /etc/beisen-webhook.env 中的 BEISEN_FEED_TOKEN）。保存时 DPAPI 加密。</summary>
    public string Token { get; set; } = "";

    /// <summary>轮询间隔。没有新事件时不写日志。</summary>
    public int PollIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// 收到事件后等这么久没有新事件再同步，把 HR 的一组连续操作合并成一次。
    /// 必须大于 Sync.TimeWindowSafetyOffsetSeconds，否则回查北森时可能还查不到这次变更。
    /// </summary>
    public int SettleSeconds { get; set; } = 90;

    /// <summary>事件持续不断时，最长等这么久也要同步一次。</summary>
    public int MaxBatchWaitSeconds { get; set; } = 300;

    /// <summary>回查北森的时间窗从「本批最早事件时间 - 此值」开始，容忍两边时钟偏差与北森写入延迟。</summary>
    public int LookbackMinutes { get; set; } = 10;

    /// <summary>每次最多拉取的事件条数。</summary>
    public int PageSize { get; set; } = 200;

    /// <summary>连续这么多分钟连不上事件服务时告警（走「告警」页的渠道）。</summary>
    public int AlertAfterMinutes { get; set; } = 10;
}
