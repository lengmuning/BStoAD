using System.Text;
using Bstoad.ActiveDirectory;
using Bstoad.Beisen;
using Bstoad.Core.Options;
using Bstoad.Worker.Alerts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bstoad.Worker.Gui;

/// <summary>
/// 用界面上「当前填写的值」（可能尚未保存）构建北森 / AD 客户端，
/// 供测试连接、浏览 OU、选择组织等使用。与命令行共用同一套客户端代码。
/// </summary>
internal static class GuiServices
{
    public static ServiceProvider Build(ConfigDocument doc)
    {
        var cfg = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(doc.ToJson())))
            .Build();

        var s = new ServiceCollection();
        s.AddLogging();
        s.AddBeisenClients(cfg);
        s.AddActiveDirectory(cfg);
        s.PostConfigure<BeisenOptions>(o => o.AppSecret = ConfigProtector.Unprotect(o.AppSecret));
        s.PostConfigure<ActiveDirectoryOptions>(o => o.ServicePassword = ConfigProtector.Unprotect(o.ServicePassword));
        s.Configure<AlertOptions>(cfg.GetSection(AlertOptions.SectionName));
        s.PostConfigure<AlertOptions>(AlertSecrets.Decrypt);
        return s.BuildServiceProvider();
    }
}
