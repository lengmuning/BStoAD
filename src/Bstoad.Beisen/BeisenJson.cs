using System.Text.Json;

namespace Bstoad.Beisen;

internal static class BeisenJson
{
    /// <summary>定位响应里的数据数组（信封候选：data/Data/result/...）。</summary>
    public static JsonElement LocateArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "data", "Data", "result", "Result", "items", "Items", "list", "List" })
                if (root.TryGetProperty(key, out var prop))
                {
                    if (prop.ValueKind == JsonValueKind.Array) return prop;
                    // 一层嵌套（如 data.items）
                    if (prop.ValueKind == JsonValueKind.Object)
                        foreach (var inner in new[] { "items", "Items", "list", "List", "data", "Data" })
                            if (prop.TryGetProperty(inner, out var ip) && ip.ValueKind == JsonValueKind.Array)
                                return ip;
                }
        }
        return default;
    }

    public static string? GetScrollId(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "scrollId", "ScrollId", "scroll_id", "ScrollID" })
            if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        // 有些接口把 scrollId 放在 data 对象里
        foreach (var key in new[] { "data", "Data" })
            if (root.TryGetProperty(key, out var d) && d.ValueKind == JsonValueKind.Object)
                foreach (var inner in new[] { "scrollId", "ScrollId", "scroll_id" })
                    if (d.TryGetProperty(inner, out var v2) && v2.ValueKind == JsonValueKind.String)
                        return v2.GetString();
        return null;
    }

    public static string? GetStr(JsonElement el, params string[] keys)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var k in keys)
            if (el.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                var s = v.ToString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        return null;
    }

    public static int? GetInt(JsonElement el, params string[] keys)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var k in keys)
            if (el.TryGetProperty(k, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
                if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var m)) return m;
            }
        return null;
    }

    public static bool? GetBool(JsonElement el, params string[] keys)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var k in keys)
            if (el.TryGetProperty(k, out var v))
            {
                if (v.ValueKind == JsonValueKind.True) return true;
                if (v.ValueKind == JsonValueKind.False) return false;
            }
        return null;
    }

    /// <summary>北森租户按北京时间理解时间参数；格式可配（默认 ISO 的 T 分隔）。</summary>
    public static string FormatBeijing(DateTime utc, string format) =>
        utc.AddHours(8).ToString(format);

    /// <summary>把 [start, stop) 切成 ≤days 天的窗口（官方建议单窗 ≤90 天）。</summary>
    public static IEnumerable<(DateTime Start, DateTime Stop)> Chunk(DateTime start, DateTime stop, int days)
    {
        if (days <= 0) days = 90;
        var cur = start;
        while (cur < stop)
        {
            var end = cur.AddDays(days);
            if (end > stop) end = stop;
            yield return (cur, end);
            cur = end;
        }
    }

    public static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + $"...(共{s.Length}字符)";

    /// <summary>配置里的额外参数值：按 JSON 字面量解析（数组/数字/布尔），失败则按字符串发送。</summary>
    public static object? ParseConfigValue(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return raw;
        }
    }
}
