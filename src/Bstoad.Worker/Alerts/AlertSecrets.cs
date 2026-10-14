using Bstoad.Core.Options;
using Microsoft.Extensions.Configuration;

namespace Bstoad.Worker.Alerts;

public static class AlertSecrets
{
    /// <summary>
    /// 解密告警凭据。与 AD/北森凭据不同：这里解密失败不抛异常，而是把该项置空（等于关闭该渠道），
    /// 保证告警配置出问题时同步本身照常运行。
    /// </summary>
    public static void Decrypt(AlertOptions o)
    {
        o.WeCom.Webhook = Safe(o.WeCom.Webhook);
        o.DingTalk.Webhook = Safe(o.DingTalk.Webhook);
        o.DingTalk.Secret = Safe(o.DingTalk.Secret);
        o.Feishu.Webhook = Safe(o.Feishu.Webhook);
        o.Feishu.Secret = Safe(o.Feishu.Secret);
    }

    private static string Safe(string v)
    {
        try { return ConfigProtector.Unprotect(v); }
        catch { return ""; }
    }

    public static AlertOptions Load(IConfiguration cfg)
    {
        var o = new AlertOptions();
        cfg.GetSection(AlertOptions.SectionName).Bind(o);
        Decrypt(o);
        return o;
    }
}

public static class TestAlertCommand
{
    public static async Task<int> RunAsync(string configPath)
    {
        var cfg = new ConfigurationBuilder().AddJsonFile(configPath, optional: false).Build();
        var o = AlertSecrets.Load(cfg);
        if (!AlertSender.AnyChannelEnabled(o))
        {
            Console.WriteLine("没有已启用且填写完整的告警渠道（企业微信 / 钉钉 / 飞书）。");
            return 2;
        }

        var results = await new AlertSender().SendAsync(o, o.Title + " · 测试消息",
            $"这是一条测试消息，说明告警渠道配置正确。\n服务器：{Environment.MachineName}\n时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        foreach (var r in results)
            Console.WriteLine($"  {(r.Ok ? "✓" : "✗")} {r.Channel}：{r.Message}");
        return results.All(r => r.Ok) ? 0 : 2;
    }
}
