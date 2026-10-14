using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bstoad.Core.Options;

namespace Bstoad.Worker.Alerts;

public sealed record ChannelResult(string Channel, bool Ok, string Message);

/// <summary>
/// 企业微信 / 钉钉 / 飞书 三个渠道的发送实现。每个渠道独立 try/catch，互不影响；本类不抛异常。
/// </summary>
public sealed class AlertSender
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // 各渠道单条消息长度上限（UTF-8 字节），超出截断
    private const int WeComMaxBytes = 2000;     // 企业微信文本消息上限 2048 字节
    private const int DingFeishuMaxBytes = 18000;

    public async Task<List<ChannelResult>> SendAsync(AlertOptions o, string title, string body, CancellationToken ct = default)
    {
        var results = new List<ChannelResult>();
        var text = title + "\n" + body;

        if (o.WeCom.Enabled && !string.IsNullOrWhiteSpace(o.WeCom.Webhook))
            results.Add(await TryAsync("企业微信", () => SendWeComAsync(o.WeCom, text, ct)));
        if (o.DingTalk.Enabled && !string.IsNullOrWhiteSpace(o.DingTalk.Webhook))
            results.Add(await TryAsync("钉钉", () => SendDingTalkAsync(o.DingTalk, text, ct)));
        if (o.Feishu.Enabled && !string.IsNullOrWhiteSpace(o.Feishu.Webhook))
            results.Add(await TryAsync("飞书", () => SendFeishuAsync(o.Feishu, text, ct)));

        return results;
    }

    public static bool AnyChannelEnabled(AlertOptions o) =>
        (o.WeCom.Enabled && !string.IsNullOrWhiteSpace(o.WeCom.Webhook)) ||
        (o.DingTalk.Enabled && !string.IsNullOrWhiteSpace(o.DingTalk.Webhook)) ||
        (o.Feishu.Enabled && !string.IsNullOrWhiteSpace(o.Feishu.Webhook));

    private static async Task<ChannelResult> TryAsync(string name, Func<Task> send)
    {
        try
        {
            await send();
            return new ChannelResult(name, true, "发送成功");
        }
        catch (Exception ex)
        {
            return new ChannelResult(name, false, ex.Message);
        }
    }

    // ---------------- 企业微信 ----------------

    private static Task SendWeComAsync(WebhookChannelOptions o, string text, CancellationToken ct)
    {
        var payload = new Dictionary<string, object>
        {
            ["msgtype"] = "text",
            ["text"] = new Dictionary<string, object> { ["content"] = TruncateUtf8(text, WeComMaxBytes) },
        };
        return PostAndCheckAsync(o.Webhook.Trim(), payload, ct);
    }

    // ---------------- 钉钉 ----------------

    private static Task SendDingTalkAsync(SignedWebhookChannelOptions o, string text, CancellationToken ct)
    {
        var url = o.Webhook.Trim();
        if (!string.IsNullOrWhiteSpace(o.Secret))
        {
            // 钉钉加签：HmacSHA256(key=secret, data=timestamp+"\n"+secret)，Base64 后 URL 编码
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var h = new HMACSHA256(Encoding.UTF8.GetBytes(o.Secret.Trim()));
            var sign = Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes($"{ts}\n{o.Secret.Trim()}")));
            url += (url.Contains('?') ? "&" : "?") + $"timestamp={ts}&sign={Uri.EscapeDataString(sign)}";
        }
        var payload = new Dictionary<string, object>
        {
            ["msgtype"] = "text",
            ["text"] = new Dictionary<string, object> { ["content"] = TruncateUtf8(text, DingFeishuMaxBytes) },
        };
        return PostAndCheckAsync(url, payload, ct);
    }

    // ---------------- 飞书 ----------------

    private static Task SendFeishuAsync(SignedWebhookChannelOptions o, string text, CancellationToken ct)
    {
        var payload = new Dictionary<string, object>
        {
            ["msg_type"] = "text",
            ["content"] = new Dictionary<string, object> { ["text"] = TruncateUtf8(text, DingFeishuMaxBytes) },
        };
        if (!string.IsNullOrWhiteSpace(o.Secret))
        {
            // 飞书加签：HmacSHA256(key=timestamp+"\n"+secret, data=空)，Base64
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var h = new HMACSHA256(Encoding.UTF8.GetBytes($"{ts}\n{o.Secret.Trim()}"));
            payload["timestamp"] = ts.ToString();
            payload["sign"] = Convert.ToBase64String(h.ComputeHash(Array.Empty<byte>()));
        }
        return PostAndCheckAsync(o.Webhook.Trim(), payload, ct);
    }

    /// <summary>POST JSON 并检查返回码：企业微信/钉钉 errcode，飞书 code 或 StatusCode，0 为成功。</summary>
    private static async Task PostAndCheckAsync(string url, object payload, CancellationToken ct)
    {
        using var resp = await Http.PostAsJsonAsync(url, payload, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {Short(body)}");

        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var key in new[] { "errcode", "code", "StatusCode" })
            {
                if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number)
                {
                    if (v.GetInt32() != 0)
                        throw new InvalidOperationException($"机器人返回错误：{Short(body)}");
                    return;
                }
            }
        }
        catch (JsonException)
        {
            // 非 JSON 响应但 HTTP 成功，视为成功
        }
    }

    // ---------------- 工具 ----------------

    public static string TruncateUtf8(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;
        const string tail = "\n……（内容过长已截断，详情请在服务器上查看）";
        var budget = maxBytes - Encoding.UTF8.GetByteCount(tail);
        var sb = new StringBuilder();
        var used = 0;
        foreach (var ch in text)
        {
            var n = Encoding.UTF8.GetByteCount(new[] { ch });
            if (used + n > budget) break;
            sb.Append(ch);
            used += n;
        }
        return sb + tail;
    }

    private static string Short(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
