using System.Text.Json;

namespace Bstoad.Worker.Events;

/// <summary>事件监听的处理进度（state/event-cursor.json）。图形界面「事件订阅」页也读它来显示积压。</summary>
public sealed class EventCursor
{
    /// <summary>游标所属的事件服务地址：地址变了游标就作废（不同服务的序号互不相干）。</summary>
    public string Url { get; set; } = "";
    /// <summary>已处理完的最大事件序号。</summary>
    public long Seq { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class EventCursorStore
{
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "state", "event-cursor.json");

    public static EventCursor? Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<EventCursor>(File.ReadAllText(FilePath));
        }
        catch { /* 损坏视为没有游标：从服务端当前最新位置开始 */ }
        return null;
    }

    public static void Save(string url, long seq)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new EventCursor { Url = url, Seq = seq, UpdatedAt = DateTime.Now }));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
