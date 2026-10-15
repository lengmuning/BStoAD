using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Bstoad.Worker.Events;

/// <summary>事件服务返回的一条事件。北森事件只是通知（谁、什么类型），不含员工/组织数据。</summary>
public sealed class FeedEvent
{
    public long Seq { get; set; }
    public string? BizId { get; set; }
    /// <summary>EmpEvent / OrgEvent。</summary>
    public string Category { get; set; } = "";
    /// <summary>北森事件类型，如 Entry、EditInfo。</summary>
    public string? Type { get; set; }
    public string? UserId { get; set; }
    public string? OrgId { get; set; }
    public long ReceivedAtMs { get; set; }

    public DateTime ReceivedAtUtc => DateTimeOffset.FromUnixTimeMilliseconds(ReceivedAtMs).UtcDateTime;
    public bool IsOrgEvent => string.Equals(Category, "OrgEvent", StringComparison.OrdinalIgnoreCase);
}

/// <summary>/v1/status 与 /v1/events 的响应（status 时 Events 为空）。</summary>
public sealed class FeedPage
{
    public long MaxSeq { get; set; }
    public long MinSeq { get; set; }
    public long Count { get; set; }
    public int RetentionDays { get; set; }
    public long? LatestReceivedAtMs { get; set; }
    public List<FeedEvent> Events { get; set; } = new();
}

/// <summary>事件服务（北森事件接收端 8601 内网拉取接口）的客户端。</summary>
public sealed class EventFeedClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly Uri _base;

    public EventFeedClient(string baseUrl, string token, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new InvalidOperationException("事件服务地址未填写");
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("事件服务访问令牌未填写");
        if (!Uri.TryCreate(NormalizeUrl(baseUrl) + "/", UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("事件服务地址格式不正确，应形如 http://事件服务器:8601");
        _base = uri;
        _http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
    }

    /// <summary>去掉首尾空白和结尾的 /，用于比较地址是否变更。</summary>
    public static string NormalizeUrl(string url) => url.Trim().TrimEnd('/');

    public Task<FeedPage> GetStatusAsync(CancellationToken ct = default) => GetAsync("v1/status", ct);

    /// <param name="waitSeconds">长轮询：没有新事件时让服务端最多挂起这么久（旧版服务端忽略此参数，立即返回）。</param>
    public Task<FeedPage> GetEventsAsync(long after, int limit, int waitSeconds = 0, CancellationToken ct = default) =>
        GetAsync($"v1/events?after={after}&limit={limit}" + (waitSeconds > 0 ? $"&wait={waitSeconds}" : ""), ct);

    private async Task<FeedPage> GetAsync(string relative, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(new Uri(_base, relative), ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("访问令牌不正确（HTTP 401）");
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"事件服务返回 HTTP {(int)resp.StatusCode}");
        var body = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<FeedPage>(body, Json)
               ?? throw new InvalidOperationException("事件服务返回了空内容");
    }

    public void Dispose() => _http.Dispose();
}
