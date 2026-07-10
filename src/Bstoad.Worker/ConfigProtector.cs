using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bstoad.Worker;

/// <summary>
/// 凭据静态加密：protect-config 子命令用 DPAPI(LocalMachine) 把配置里的
/// AppSecret / ServicePassword 加密成 dpapi:&lt;base64&gt; 写回；启动时透明解密。
/// 防的是「配置文件被拷走/备份泄露」，不防本机管理员（本机任何进程可解）。
/// 加密值绑定机器：换服务器需重填明文再跑一次 protect-config。
/// </summary>
public static class ConfigProtector
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BSTOAD.v1");

    private static readonly (string Section, string Key)[] SecretKeys =
    {
        ("Beisen", "AppSecret"),
        ("ActiveDirectory", "ServicePassword"),
    };

    public static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal))
            return value;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("dpapi: 加密值仅支持 Windows。");
        try
        {
            var cipher = Convert.FromBase64String(value[Prefix.Length..]);
            var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "DPAPI 解密失败——加密值只能在执行 protect-config 的那台机器上使用；换机器请重填明文后重新运行 protect-config。", ex);
        }
    }

    public static void Run(string configPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("protect-config 仅支持 Windows（依赖 DPAPI）。");
            Environment.Exit(2);
            return;
        }

        var root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject
            ?? throw new InvalidOperationException("appsettings.json 解析失败");

        var changed = 0;
        foreach (var (section, key) in SecretKeys)
        {
            if (root[section] is not JsonObject sec) continue;
            var val = sec[key]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(val))
            {
                Console.WriteLine($"  - {section}.{key} 为空，跳过");
                continue;
            }
            if (val.StartsWith(Prefix, StringComparison.Ordinal))
            {
                Console.WriteLine($"  - {section}.{key} 已加密，跳过");
                continue;
            }
            if (val.StartsWith("<PLACEHOLDER", StringComparison.Ordinal))
            {
                Console.WriteLine($"  - {section}.{key} 还是占位符，跳过（先填真实值）");
                continue;
            }

            var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(val), Entropy, DataProtectionScope.LocalMachine);
            sec[key] = Prefix + Convert.ToBase64String(cipher);
            changed++;
            Console.WriteLine($"  ✓ 已加密 {section}.{key}");
        }

        if (changed > 0)
        {
            File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            Console.WriteLine();
            Console.WriteLine($"已写回 {configPath}（{changed} 项加密，DPAPI LocalMachine，仅本机可解密）");
            Console.WriteLine("注意：换服务器部署时需重填明文并重新运行 protect-config。");
        }
        else
        {
            Console.WriteLine("没有需要加密的项。");
        }
    }
}
