using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Bstoad.Core.Models;
using Bstoad.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bstoad.Beisen;

public interface IBeisenOrgClient
{
    /// <summary>全量：从 FullPullStartDate 起按 90 天分片滚动拉取全部组织。</summary>
    Task<IReadOnlyList<BeisenOrg>> GetAllOrgsAsync(CancellationToken ct = default);

    /// <summary>指定时间窗（内部自动按 ≤90 天分片）。</summary>
    Task<IReadOnlyList<BeisenOrg>> GetByTimeWindowAsync(DateTime startUtc, DateTime stopUtc, CancellationToken ct = default);
}

public sealed class BeisenOrgClient : IBeisenOrgClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IBeisenTokenProvider _tokenProvider;
    private readonly BeisenOptions _options;
    private readonly ILogger<BeisenOrgClient> _logger;

    public BeisenOrgClient(
        IHttpClientFactory httpClientFactory,
        IBeisenTokenProvider tokenProvider,
        IOptions<BeisenOptions> options,
        ILogger<BeisenOrgClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _tokenProvider = tokenProvider;
        _options = options.Value;
        _logger = logger;
    }

    public Task<IReadOnlyList<BeisenOrg>> GetAllOrgsAsync(CancellationToken ct = default)
        => GetByTimeWindowAsync(_options.FullPullStartUtc(), DateTime.UtcNow, ct);

    public async Task<IReadOnlyList<BeisenOrg>> GetByTimeWindowAsync(DateTime startUtc, DateTime stopUtc, CancellationToken ct = default)
    {
        // 同一组织在多个窗口变动过会出现多次：后面窗口的（更新的）覆盖前面的
        var byId = new Dictionary<string, BeisenOrg>(StringComparer.Ordinal);
        var url = new Uri(new Uri(_options.BaseUrl), _options.OrgTimeWindowPath);
        var sampleLogged = false;
        string? emptyBody = null;

        foreach (var (chunkStart, chunkStop) in BeisenJson.Chunk(startUtc, stopUtc, _options.TimeWindowChunkDays))
        {
            ct.ThrowIfCancellationRequested();
            string? scrollId = null;

            // 官方约束：滚动查询两次调用间隔 ≤10 秒，必须紧凑地 while 拉完
            while (true)
            {
                var token = await _tokenProvider.GetAccessTokenAsync(ct);
                var client = _httpClientFactory.CreateClient(nameof(BeisenOrgClient));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var body = new Dictionary<string, object?>
                {
                    ["TimeWindowQueryType"] = 1,
                    ["StartTime"] = BeisenJson.FormatBeijing(chunkStart, _options.TimeWindowDateFormat),
                    ["StopTime"] = BeisenJson.FormatBeijing(chunkStop, _options.TimeWindowDateFormat),
                    ["Capacity"] = _options.PageCapacity,
                    ["ScrollId"] = scrollId,
                };
                foreach (var kv in _options.OrgExtraParams)
                    body[kv.Key] = BeisenJson.ParseConfigValue(kv.Value);

                using var resp = await client.PostAsJsonAsync(url, body, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        $"Organization/GetByTimeWindow {(int)resp.StatusCode}: {BeisenJson.Truncate(json, 800)}");

                var (page, nextScrollId) = ParsePage(json);

                if (page.Count > 0 && !sampleLogged)
                {
                    sampleLogged = true;
                    if (_options.LogRawSamples)
                        _logger.LogInformation("组织字段核对样本(原始响应截断 800)：{Body}", BeisenJson.Truncate(json, 800));
                    var s0 = page[0];
                    _logger.LogInformation("首条解析结果核对：OrgId={OrgId} ParentOrgId={Parent} Name={Name}（Parent 为空说明父字段没对上，层级会变平）",
                        s0.OrgId, s0.ParentOrgId ?? "<空>", s0.Name);
                }
                if (page.Count == 0 && byId.Count == 0)
                    emptyBody ??= json;

                foreach (var o in page) byId[o.OrgId] = o;

                if (page.Count == 0 || string.IsNullOrEmpty(nextScrollId)) break;
                scrollId = nextScrollId;
            }
        }

        if (byId.Count == 0 && emptyBody != null)
        {
            if (_options.LogRawSamples)
                _logger.LogWarning("组织时间窗拉取 0 条，原始响应(截断 1000)：{Body}", BeisenJson.Truncate(emptyBody, 1000));
            else
                _logger.LogWarning("组织时间窗拉取 0 条（如需原始响应排查，临时开启 Beisen.LogRawSamples 后重跑）");
        }

        _logger.LogInformation("拉取组织完成，共 {Count} 个（窗口 {Start:yyyy-MM-dd} ~ {Stop:yyyy-MM-dd}，分片 ≤{Days} 天）",
            byId.Count, startUtc, stopUtc, _options.TimeWindowChunkDays);
        return byId.Values.ToList();
    }

    private static (List<BeisenOrg>, string? scrollId) ParsePage(string json)
    {
        var result = new List<BeisenOrg>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var scrollId = BeisenJson.GetScrollId(root);

        var arr = BeisenJson.LocateArray(root);
        if (arr.ValueKind != JsonValueKind.Array) return (result, scrollId);

        foreach (var el in arr.EnumerateArray())
        {
            var org = new BeisenOrg
            {
                // 北森组织主键常见命名 OId；候选覆盖 PascalCase/camelCase
                OrgId = BeisenJson.GetStr(el, "OId", "oId", "OID", "OrgId", "orgId", "Id", "id") ?? "",
                // 北森标准组织模型的上级组织字段是 POIdOrgAdmin（行政线）
                ParentOrgId = BeisenJson.GetStr(el, "POIdOrgAdmin", "pOIdOrgAdmin", "poidOrgAdmin",
                    "POId", "pOId", "POID", "ParentOId", "ParentOrgId", "parentOrgId", "ParentId", "parentId"),
                Name = BeisenJson.GetStr(el, "Name", "name", "OrgName", "orgName") ?? "",
                Code = BeisenJson.GetStr(el, "Code", "code", "OrgCode", "orgCode"),
                IsActive = !(BeisenJson.GetBool(el, "IsDisabled", "isDisabled", "Disabled") ?? false),
            };
            if (!string.IsNullOrEmpty(org.OrgId))
                result.Add(org);
        }
        return (result, scrollId);
    }
}
