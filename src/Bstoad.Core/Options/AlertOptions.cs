namespace Bstoad.Core.Options;

/// <summary>
/// 告警推送配置。默认整体关闭；任何渠道发送失败只记日志，不影响同步。
/// Webhook 地址、加签密钥在保存时由 DPAPI 加密（与 AppSecret 相同）。
/// </summary>
public sealed class AlertOptions
{
    public const string SectionName = "Alerts";

    public bool Enabled { get; set; } = false;

    /// <summary>最低告警级别：Info / Warning / Critical。</summary>
    public string MinLevel { get; set; } = "Warning";

    /// <summary>同一事项（类别+对象）在多少小时内不重复提醒。</summary>
    public int RepeatSuppressHours { get; set; } = 24;

    /// <summary>每日全量同步后推送一份汇总（即使没有新问题）。</summary>
    public bool SendFullSyncSummary { get; set; } = false;

    /// <summary>干跑（未加 --apply）时是否也推送。默认否，避免在界面里测试时刷屏。</summary>
    public bool AlertOnDryRun { get; set; } = false;

    /// <summary>消息标题。钉钉机器人若设置了「自定义关键词」，标题里要包含该关键词。</summary>
    public string Title { get; set; } = "BSTOAD 北森→AD 同步";

    public WebhookChannelOptions WeCom { get; set; } = new();
    public SignedWebhookChannelOptions DingTalk { get; set; } = new();
    public SignedWebhookChannelOptions Feishu { get; set; } = new();
}

public sealed class WebhookChannelOptions
{
    public bool Enabled { get; set; }
    public string Webhook { get; set; } = "";
}

public sealed class SignedWebhookChannelOptions
{
    public bool Enabled { get; set; }
    public string Webhook { get; set; } = "";
    /// <summary>机器人「加签」密钥，未开启加签则留空。</summary>
    public string Secret { get; set; } = "";
}
