using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bstoad.Worker.Gui;

/// <summary>
/// appsettings.json 的读写封装：只改界面管理的键，其余内容原样保留；
/// 保存前自动备份为 appsettings.json.bak；敏感值写入时 DPAPI 加密。
/// </summary>
internal sealed class ConfigDocument
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string FilePath { get; }
    public JsonObject Root { get; private set; }

    private ConfigDocument(string path, JsonObject root)
    {
        FilePath = path;
        Root = root;
    }

    /// <summary>读取配置。JSON 有语法错误时抛 JsonException（含行号），不会覆盖原文件。</summary>
    public static ConfigDocument Load(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        if (node is not JsonObject obj)
            throw new JsonException("配置文件顶层不是 JSON 对象");
        return new ConfigDocument(path, obj);
    }

    public string ToJson() => Root.ToJsonString(WriteOptions);

    public void Save()
    {
        if (File.Exists(FilePath))
            File.Copy(FilePath, FilePath + ".bak", overwrite: true);
        File.WriteAllText(FilePath, ToJson(), new UTF8Encoding(false));
    }

    // ---------------- 读 ----------------

    public JsonNode? Get(params string[] path)
    {
        JsonNode? cur = Root;
        foreach (var p in path)
        {
            if (cur is not JsonObject o) return null;
            cur = o[p];
        }
        return cur;
    }

    public string GetString(string def, params string[] path) => AsString(Get(path)) ?? def;

    public bool GetBool(bool def, params string[] path)
    {
        var n = Get(path);
        if (n is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            if (bool.TryParse(AsString(n), out b)) return b;
        }
        return def;
    }

    public int GetInt(int def, params string[] path)
    {
        var n = Get(path);
        if (n is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (int.TryParse(AsString(n), out i)) return i;
        }
        return def;
    }

    public double GetDouble(double def, params string[] path)
    {
        var n = Get(path);
        if (n is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (double.TryParse(AsString(n), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out d)) return d;
        }
        return def;
    }

    public string[] GetStringArray(params string[] path) =>
        Get(path) is JsonArray arr
            ? arr.Select(AsString).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToArray()
            : Array.Empty<string>();

    /// <summary>敏感值是否已填写（已加密或明文均算，占位符不算）。</summary>
    public bool IsSecretSet(params string[] path)
    {
        var s = AsString(Get(path));
        return !string.IsNullOrWhiteSpace(s) && !s.StartsWith("<PLACEHOLDER", StringComparison.Ordinal);
    }

    private static string? AsString(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString().Trim('"'),
        _ => n.ToJsonString(),
    };

    // ---------------- 写 ----------------

    public void Set(JsonNode? value, params string[] path)
    {
        var parent = Root;
        foreach (var p in path[..^1])
        {
            if (parent[p] is not JsonObject child)
            {
                child = new JsonObject();
                parent[p] = child;
            }
            parent = child;
        }
        parent[path[^1]] = value;
    }

    public void SetString(string value, params string[] path) => Set(JsonValue.Create(value), path);
    public void SetBool(bool value, params string[] path) => Set(JsonValue.Create(value), path);
    public void SetInt(int value, params string[] path) => Set(JsonValue.Create(value), path);
    public void SetDouble(double value, params string[] path) => Set(JsonValue.Create(Math.Round(value, 4)), path);

    public void SetStringArray(IEnumerable<string> values, params string[] path)
    {
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(JsonValue.Create(v));
        Set(arr, path);
    }

    public void Remove(params string[] path)
    {
        if (Get(path[..^1]) is JsonObject parent) parent.Remove(path[^1]);
    }

    /// <summary>输入框留空表示不修改；有内容则 DPAPI 加密后写入。</summary>
    public void SetSecret(string plain, params string[] path)
    {
        if (string.IsNullOrEmpty(plain)) return;
        SetString(ConfigProtector.Protect(plain), path);
    }
}
