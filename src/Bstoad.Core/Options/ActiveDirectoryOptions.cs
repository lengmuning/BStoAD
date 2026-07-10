namespace Bstoad.Core.Options;

public sealed class ActiveDirectoryOptions
{
    public const string SectionName = "ActiveDirectory";

    public string LdapHost { get; set; } = "dc01.example.com";
    public int Port { get; set; } = 389;
    public bool UseSsl { get; set; } = false;

    public string ServiceAccount { get; set; } = "";
    public string ServicePassword { get; set; } = "";

    public string BaseUserOu { get; set; } = "OU=北森同步,DC=example,DC=com";
    public string LeaverOu { get; set; } = "OU=北森离职,DC=example,DC=com";

    public string UpnSuffix { get; set; } = "EXAMPLE.COM";

    public string DefaultInitialPassword { get; set; } = "123456";

    public bool PasswordNeverExpires { get; set; } = true;
    public bool ForceChangePasswordAtNextLogon { get; set; } = false;

    public string ManagedMarkerAttribute { get; set; } = "adminDescription";
    public string ManagedMarkerValue { get; set; } = "beisen-sync:managed";

    public string OuMarkerAttribute { get; set; } = "description";
    public string OuMarkerPrefix { get; set; } = "beisen-org-id:";

    // 禁区：位于这些容器/OU（含子级）下的账号，sync 永不接管/修改/移动/禁用，只告警。
    // 特权账号（adminCount=1）无论在哪都受同样保护，无需配置。
    public string[] NeverTouchOuDns { get; set; } = Array.Empty<string>();
}
