using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bstoad.Worker;

/// <summary>
/// 凭据静态加密：用 DPAPI(LocalMachine) 把配置里的敏感值加密成 dpapi:&lt;base64&gt;；启动时透明解密。
/// 防的是「配置文件被拷走/备份泄露」，不防本机管理员（本机任何进程可解）。
/// 加密值绑定机器：换服务器需重填明文再加密一次（protect-config 或图形界面保存）。
/// </summary>
public static class ConfigProtector
{
    public const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BSTOAD.v1");

    /// <summary>需要加密的配置路径。</summary>
    public static readonly string[][] SecretPaths =
    {
        new[] { "Beisen", "AppSecret" },
        new[] { "ActiveDirectory", "ServicePassword" },
        new[] { "Alerts", "WeCom", "Webhook" },
        new[] { "Alerts", "DingTalk", "Webhook" },
        new[] { "Alerts", "DingTalk", "Secret" },
        new[] { "Alerts", "Feishu", "Webhook" },
        new[] { "Alerts", "Feishu", "Secret" },
    };

    public static bool IsProtected(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Protect(string plain)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI 加密仅支持 Windows。");
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.LocalMachine);
        return Prefix + Convert.ToBase64String(cipher);
    }

    public static string Unprotect(string value)
    {
        if (!IsProtected(value))
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
                "DPAPI 解密失败——加密值只能在加密时的那台机器上使用；换机器请重填明文后重新加密。", ex);
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
        foreach (var path in SecretPaths)
        {
            var name = string.Join(".", path);
            var parent = Navigate(root, path[..^1]);
            if (parent == null) continue;
            var key = path[^1];
            var val = parent[key]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(val))
                continue;
            if (IsProtected(val))
            {
                Console.WriteLine($"  - {name} 已加密，跳过");
                continue;
            }
            if (val.StartsWith("<PLACEHOLDER", StringComparison.Ordinal))
            {
                Console.WriteLine($"  - {name} 还是占位符，跳过（先填真实值）");
                continue;
            }

            parent[key] = Protect(val);
            changed++;
            Console.WriteLine($"  ✓ 已加密 {name}");
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

    private static JsonObject? Navigate(JsonObject root, string[] path)
    {
        JsonObject? cur = root;
        foreach (var p in path)
        {
            cur = cur?[p] as JsonObject;
            if (cur == null) return null;
        }
        return cur;
    }
}
