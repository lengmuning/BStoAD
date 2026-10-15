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

        Console.WriteLine("按 userId 精确查询 = 员工信息 + 任职记录两个接口，按 userID 合并。");
        var probe = await client.ProbeByUserIdsAsync(userIds);
        var allOk = true;
        foreach (var call in probe.Calls)
        {
            Console.WriteLine();
            Console.WriteLine($"── {call.Label}：POST {o.BaseUrl.TrimEnd('/')}{call.Path} ──");
            Console.WriteLine($"请求: {call.RequestBody}");
            Console.WriteLine($"HTTP {call.Status}");
            Console.WriteLine("响应结构（值已隐去，可以放心转发）：");
            Console.WriteLine(DescribeShape(call.Body));
            if (call.Status is < 200 or >= 300 || call.Body.Contains("\"code\":\"4", StringComparison.Ordinal) ||
                call.Body.Contains("\"code\":\"5", StringComparison.Ordinal))
                allOk = false;
        }

        var emps = probe.Employees;
        Console.WriteLine();
        Console.WriteLine($"── 合并解析结果（含个人信息，仅本机查看）：{emps.Count} 名员工 ──");
        foreach (var e in emps)
            Console.WriteLine($"  userId={e.UserId}  工号={e.EmployeeId}  姓名={e.Name}  邮箱={(string.IsNullOrEmpty(e.Email) ? "<空>" : "有")}  " +
                              $"手机={(string.IsNullOrEmpty(e.Mobile) ? "<空>" : "有")}  部门OrgId={e.OrgId}  人员状态={e.EmpStatus}");
        var requested = userIds.Where(id => int.TryParse(id, out var n) && n > 0).Distinct().ToList();
        var missing = requested.Where(id => emps.All(e => e.UserId != id)).ToList();
        if (missing.Count > 0) Console.WriteLine("  未返回（两个接口没有同时查到）: " + string.Join(",", missing));

        var problems = new List<string>();
        if (!allOk) problems.Add("有接口返回错误（看上面的 HTTP 状态和 code / message）");
        if (emps.Count == 0) problems.Add("没有解析出任何员工");
        if (emps.Any(e => string.IsNullOrEmpty(e.EmployeeId))) problems.Add("有员工缺少工号（任职记录的查询列或权限不全）");
        if (emps.Any(e => e.EmpStatus == 0)) problems.Add("有员工的人员状态无法识别");
        if (emps.Any(e => string.IsNullOrEmpty(e.Name))) problems.Add("有员工缺少姓名（员工信息的查询列或权限不全）");

        Console.WriteLine();
        if (problems.Count == 0 && missing.Count == 0)
        {
            Console.WriteLine("✓ 接口可用：可以在图形界面「事件订阅」页把员工查询方式切换为「按 userId 精确查询」，保存后重启事件监听。");
            Console.WriteLine("  说明：北森已下线接口内的名称翻译，部门名称由组织树补齐；职位名称在每日全量同步时更新。");
            return 0;
        }
        foreach (var p in problems) Console.WriteLine("✗ " + p);
        if (problems.Count == 0) Console.WriteLine("✗ 部分 userId 未返回：确认这些 userId 存在且未被删除（已删除的员工不会返回）");
        Console.WriteLine("  在此之前请保持「按时间窗回查」。可把上面两段「响应结构」发给维护人员排查（不要发「合并解析结果」）。");
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
