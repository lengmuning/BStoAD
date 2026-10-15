using System.Text;
using System.Text.Json;
using Bstoad.Beisen;
using Bstoad.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bstoad.Worker;

/// <summary>
/// probe-employee：核对「按 userId 精确查询」用的北森接口（只读北森，不碰 AD）。
/// 打印请求、响应结构（值已隐去，可放心转发）与解析结果（含工号姓名，仅本机查看）。
/// </summary>
public static class ProbeEmployeeCommand
{
    // 这些键的值原样显示：判断接口是否调用成功要看它们
    private static readonly HashSet<string> ShowValueKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "message", "msg", "success", "status", "errorCode", "errorMessage", "errMsg", "total", "count",
    };

    public static async Task<int> RunAsync(string configPath, string[] userIds)
    {
        if (userIds.Length == 0)
        {
            Console.Error.WriteLine("用法: bstoad probe-employee --user-id 645506318[,645470856]");
            Console.Error.WriteLine("userId 可以从事件服务器的事件记录或 logs/bstoad-listen-*.log 中获得。");
            return 2;
        }

        var cfg = new ConfigurationBuilder().AddJsonFile(configPath, optional: false).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBeisenClients(cfg);
        services.PostConfigure<BeisenOptions>(o => o.AppSecret = ConfigProtector.Unprotect(o.AppSecret));
        using var sp = services.BuildServiceProvider();
        var o = sp.GetRequiredService<IOptions<BeisenOptions>>().Value;
        var client = sp.GetRequiredService<IBeisenEmployeeClient>();

        Console.WriteLine($"接口: POST {o.BaseUrl.TrimEnd('/')}{o.EmployeeByIdsPath}");
        Console.WriteLine($"参数: {o.EmployeeByIdsParam} = [{string.Join(",", userIds)}]（按{(o.EmployeeByIdsAsNumbers ? "数字" : "字符串")}发送）" +
                          "，另带 IsWithDeleted=true、EnableTranslate=true" +
                          (o.EmployeeByIdsExtraParams.Count > 0 ? "、" + string.Join("、", o.EmployeeByIdsExtraParams.Keys) : ""));

        var (status, body) = await client.ProbeByUserIdsAsync(userIds);
        Console.WriteLine($"HTTP {status}");
        Console.WriteLine();
        Console.WriteLine("── 响应结构（值已隐去，可以放心转发）──");
        Console.WriteLine(DescribeShape(body));

        if (status is >= 200 and < 300)
        {
            var emps = client.ParseEmployees(body);
            Console.WriteLine();
            Console.WriteLine($"── 解析结果（含个人信息，仅本机查看）：{emps.Count} 名员工 ──");
            foreach (var e in emps)
                Console.WriteLine($"  userId={e.UserId}  工号={e.EmployeeId}  姓名={e.Name}  部门={e.OrgName ?? e.OrgId}  状态={e.EmpStatus}");
            var missing = userIds.Distinct().Where(id => emps.All(e => e.UserId != id)).ToList();
            if (missing.Count > 0) Console.WriteLine("  未返回的 userId: " + string.Join(",", missing));

            Console.WriteLine();
            if (emps.Count > 0 && missing.Count == 0 && emps.All(e => !string.IsNullOrEmpty(e.EmployeeId)))
            {
                Console.WriteLine("✓ 接口可用：可以在图形界面「事件订阅」页把员工查询方式切换为「按 userId 精确查询」，保存后重启事件监听。");
                return 0;
            }
        }

        Console.WriteLine();
        Console.WriteLine("✗ 接口不可用或返回不完整。可调整 appsettings.json 的 Beisen.EmployeeByIdsPath / EmployeeByIdsParam / " +
                          "EmployeeByIdsAsNumbers 后重试；在此之前请保持「按时间窗回查」。");
        return 2;
    }

    /// <summary>JSON 结构：字段名 + 类型（字符串显示长度），值隐去；数组只展开第一个元素。</summary>
    private static string DescribeShape(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var sb = new StringBuilder();
            Walk(doc.RootElement, "", 0, sb);
            return sb.ToString().TrimEnd();
        }
        catch (JsonException)
        {
            return "（不是 JSON）长度 " + body.Length;
        }

        static void Walk(JsonElement el, string key, int depth, StringBuilder sb)
        {
            var pad = new string(' ', depth * 2);
            var label = key.Length > 0 ? key + ": " : "";
            if (depth > 8) { sb.AppendLine(pad + label + "…"); return; }
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    sb.AppendLine(pad + label + "{");
                    foreach (var p in el.EnumerateObject()) Walk(p.Value, p.Name, depth + 1, sb);
                    sb.AppendLine(pad + "}");
                    break;
                case JsonValueKind.Array:
                    var n = el.GetArrayLength();
                    sb.AppendLine(pad + label + $"[ 共 {n} 项" + (n > 0 ? "，第一项：" : " ]"));
                    if (n > 0)
                    {
                        Walk(el[0], "", depth + 1, sb);
                        sb.AppendLine(pad + "]");
                    }
                    break;
                case JsonValueKind.String:
                    var s = el.GetString() ?? "";
                    sb.AppendLine(pad + label + (ShowValueKeys.Contains(key) ? $"\"{Truncate(s, 200)}\"" : $"string({s.Length})"));
                    break;
                case JsonValueKind.Number:
                    sb.AppendLine(pad + label + (ShowValueKeys.Contains(key) ? el.GetRawText() : "number"));
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    sb.AppendLine(pad + label + (ShowValueKeys.Contains(key) ? el.GetRawText() : "bool"));
                    break;
                default:
                    sb.AppendLine(pad + label + "null");
                    break;
            }
        }

        static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    }
}
