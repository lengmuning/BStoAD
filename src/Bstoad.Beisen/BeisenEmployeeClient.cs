using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Bstoad.Core.Models;
using Bstoad.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bstoad.Beisen;

public interface IBeisenEmployeeClient
{
    /// <summary>全量：从 FullPullStartDate 起按 90 天分片滚动拉取全部员工。</summary>
    Task<IReadOnlyList<BeisenEmployee>> SearchAllAsync(bool includeDeleted, CancellationToken ct = default);

    /// <summary>指定时间窗（内部自动按 ≤90 天分片）。</summary>
    Task<IReadOnlyList<BeisenEmployee>> SearchByTimeWindowAsync(
        DateTime startUtc, DateTime stopUtc, bool includeDeleted, CancellationToken ct = default);

    /// <summary>按 userId 精确查询（接口与参数见 BeisenOptions.EmployeeByIds*）。HTTP 失败时抛异常。</summary>
    Task<IReadOnlyList<BeisenEmployee>> GetByUserIdsAsync(IReadOnlyCollection<string> userIds, CancellationToken ct = default);

    /// <summary>probe-employee 用：发与 GetByUserIdsAsync 相同的请求，返回 HTTP 状态与原始响应，不解析、不抛 HTTP 错误。</summary>
    Task<(int Status, string Body)> ProbeByUserIdsAsync(IReadOnlyCollection<string> userIds, CancellationToken ct = default);

    /// <summary>按与时间窗接口相同的规则解析响应（供 probe-employee 展示解析结果）。</summary>
    IReadOnlyList<BeisenEmployee> ParseEmployees(string json);
}

public sealed class BeisenEmployeeClient : IBeisenEmployeeClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IBeisenTokenProvider _tokenProvider;
    private readonly BeisenOptions _options;
    private readonly ILogger<BeisenEmployeeClient> _logger;

    public BeisenEmployeeClient(
        IHttpClientFactory httpClientFactory,
        IBeisenTokenProvider tokenProvider,
        IOptions<BeisenOptions> options,
        ILogger<BeisenEmployeeClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _tokenProvider = tokenProvider;
        _options = options.Value;
        _logger = logger;
    }

    public Task<IReadOnlyList<BeisenEmployee>> SearchAllAsync(bool includeDeleted, CancellationToken ct = default)
        => SearchByTimeWindowAsync(_options.FullPullStartUtc(), DateTime.UtcNow, includeDeleted, ct);

    public async Task<IReadOnlyList<BeisenEmployee>> SearchByTimeWindowAsync(
        DateTime startUtc, DateTime stopUtc, bool includeDeleted, CancellationToken ct = default)
    {
        // 同一员工多窗口变动会出现多次：后面（更新的）覆盖前面的
        var byKey = new Dictionary<string, BeisenEmployee>(StringComparer.Ordinal);
        var url = new Uri(new Uri(_options.BaseUrl), _options.EmployeeTimeWindowPath);
        var sampleLogged = false;
        string? emptyBody = null;

        foreach (var (chunkStart, chunkStop) in BeisenJson.Chunk(startUtc, stopUtc, _options.TimeWindowChunkDays))
        {
            ct.ThrowIfCancellationRequested();
            string? scrollId = null;

            // 官方约束：滚动两次调用间隔 ≤10 秒，紧凑 while 循环拉完再做业务
            while (true)
            {
                var token = await _tokenProvider.GetAccessTokenAsync(ct);
                var client = _httpClientFactory.CreateClient(nameof(BeisenEmployeeClient));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var body = new Dictionary<string, object?>
                {
                    ["TimeWindowQueryType"] = 1,
                    ["StartTime"] = BeisenJson.FormatBeijing(chunkStart, _options.TimeWindowDateFormat),
                    ["StopTime"] = BeisenJson.FormatBeijing(chunkStop, _options.TimeWindowDateFormat),
                    ["Capacity"] = _options.PageCapacity,
                    ["ScrollId"] = scrollId,
                    ["IsWithDeleted"] = includeDeleted,
                    ["EnableTranslate"] = true,
                };
                foreach (var kv in _options.EmployeeExtraParams)
                    body[kv.Key] = BeisenJson.ParseConfigValue(kv.Value);

                using var resp = await client.PostAsJsonAsync(url, body, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        $"Employee/GetByTimeWindow {(int)resp.StatusCode}: {BeisenJson.Truncate(json, 800)}");

                var (page, nextScrollId) = ParsePage(json, _options.TitleFields);

                if (page.Count > 0 && !sampleLogged)
                {
                    sampleLogged = true;
                    if (_options.LogRawSamples)
                        _logger.LogInformation("员工字段核对样本(原始响应截断 1500)：{Body}", BeisenJson.Truncate(json, 1500));
                    var e0 = page[0];
                    _logger.LogInformation("首条解析核对：工号={Emp} 姓名={Name} 邮箱解析={HasMail} 手机解析={HasMob} 部门OrgId={Org}",
                        e0.EmployeeId, e0.Name,
                        string.IsNullOrEmpty(e0.Email) ? "无" : "有",
                        string.IsNullOrEmpty(e0.Mobile) ? "无" : "有",
                        e0.OrgId ?? "<空>");
                }
                if (page.Count == 0 && byKey.Count == 0)
                    emptyBody ??= json;

                foreach (var e in page)
                {
                    var key = !string.IsNullOrEmpty(e.UserId) ? e.UserId : e.EmployeeId;
                    byKey[key] = e;
                }

                if (page.Count == 0 || string.IsNullOrEmpty(nextScrollId)) break;
                scrollId = nextScrollId;

                _logger.LogInformation("员工分页 {ThisPage} 条，累计 {Total}", page.Count, byKey.Count);
            }
        }

        if (byKey.Count == 0 && emptyBody != null)
        {
            if (_options.LogRawSamples)
                _logger.LogWarning("员工时间窗拉取 0 条，原始响应(截断 1000)：{Body}", BeisenJson.Truncate(emptyBody, 1000));
            else
                _logger.LogWarning("员工时间窗拉取 0 条（如需原始响应排查，临时开启 Beisen.LogRawSamples 后重跑）");
        }

        _logger.LogInformation("拉取员工完成，共 {Count} 条（窗口 {Start:yyyy-MM-dd} ~ {Stop:yyyy-MM-dd}）",
            byKey.Count, startUtc, stopUtc);
        return byKey.Values.ToList();
    }

    public async Task<IReadOnlyList<BeisenEmployee>> GetByUserIdsAsync(IReadOnlyCollection<string> userIds, CancellationToken ct = default)
    {
        var byKey = new Dictionary<string, BeisenEmployee>(StringComparer.Ordinal);
        foreach (var chunk in userIds.Distinct(StringComparer.Ordinal).Chunk(Math.Clamp(_options.PageCapacity, 1, 300)))
        {
            var (status, json) = await PostByIdsAsync(chunk, ct);
            if (status < 200 || status >= 300)
                throw new InvalidOperationException($"{_options.EmployeeByIdsPath} {status}: {BeisenJson.Truncate(json, 800)}");
            var (page, _) = ParsePage(json, _options.TitleFields);
            foreach (var e in page)
                byKey[!string.IsNullOrEmpty(e.UserId) ? e.UserId : e.EmployeeId] = e;
        }
        return byKey.Values.ToList();
    }

    public Task<(int Status, string Body)> ProbeByUserIdsAsync(IReadOnlyCollection<string> userIds, CancellationToken ct = default)
        => PostByIdsAsync(userIds.ToArray(), ct);

    public IReadOnlyList<BeisenEmployee> ParseEmployees(string json) => ParsePage(json, _options.TitleFields).Item1;

    private async Task<(int Status, string Body)> PostByIdsAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        var token = await _tokenProvider.GetAccessTokenAsync(ct);
        var client = _httpClientFactory.CreateClient(nameof(BeisenEmployeeClient));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        object idList = _options.EmployeeByIdsAsNumbers
            ? ids.Select(id => long.TryParse(id, out var n) ? (object)n : id).ToArray()
            : ids.ToArray();
        var body = new Dictionary<string, object?>
        {
            [_options.EmployeeByIdsParam] = idList,
            ["IsWithDeleted"] = true,
            ["EnableTranslate"] = true,
        };
        foreach (var kv in _options.EmployeeByIdsExtraParams)
            body[kv.Key] = BeisenJson.ParseConfigValue(kv.Value);

        var url = new Uri(new Uri(_options.BaseUrl), _options.EmployeeByIdsPath);
        using var resp = await client.PostAsJsonAsync(url, body, ct);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    private static (List<BeisenEmployee>, string? scrollId) ParsePage(string json, string[] titleFields)
    {
        var list = new List<BeisenEmployee>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var scrollId = BeisenJson.GetScrollId(root);

        var arr = BeisenJson.LocateArray(root);
        if (arr.ValueKind != JsonValueKind.Array) return (list, scrollId);

        foreach (var el in arr.EnumerateArray())
        {
            // 官方结构（apifox 文档确认）：employeeInfo（基本信息）+ recordInfo/recordInfoList（任职记录）
            // 兼容旧候选 BasicInfos/ServiceInfos 与平铺结构
            var basic = FirstObject(el, "employeeInfo", "EmployeeInfo", "BasicInfos", "basicInfos", "BasicInfo") ?? el;
            var service = FirstObject(el, "recordInfo", "RecordInfo", "ServiceInfos", "serviceInfos", "ServiceInfo")
                       ?? FirstArrayItem(el, "recordInfoList", "RecordInfoList")
                       ?? el;

            var emp = new BeisenEmployee
            {
                UserId = BeisenJson.GetStr(basic, "userID", "UserID", "UserId", "userId", "Id", "id")
                       ?? BeisenJson.GetStr(service, "userID", "UserID", "userId") ?? "",
                EmployeeId = BeisenJson.GetStr(service, "jobNumber", "JobNumber")
                           ?? BeisenJson.GetStr(basic, "jobNumber", "JobNumber", "EmployeeCode", "employeeCode", "empNo") ?? "",
                Name = BeisenJson.GetStr(basic, "name", "Name", "FullName", "fullName") ?? "",
                GivenName = BeisenJson.GetStr(basic, "GivenName", "givenName"),
                Surname = BeisenJson.GetStr(basic, "Surname", "surname", "FamilyName"),
                Email = BeisenJson.GetStr(basic, "email", "Email", "WorkEmail", "workEmail", "companyEmail"),
                Mobile = BeisenJson.GetStr(basic, "mobilePhone", "MobilePhone", "Mobile", "mobile"),
                Phone = BeisenJson.GetStr(basic, "officePhone", "OfficePhone", "Phone", "phone"),
                OrgId = BeisenJson.GetStr(service, "oIdDepartment", "OIdDepartment", "DepartmentId", "departmentId", "OrgId", "orgId"),
                OrgName = BeisenJson.GetStr(service, "oIdDepartment_TranslateResult", "OIdDepartment_TranslateResult",
                    "DepartmentName", "departmentName", "OrgName", "orgName"),
                Title = BeisenJson.GetStr(service, titleFields) ?? BeisenJson.GetStr(basic, titleFields),
                EmpStatus = BeisenJson.GetInt(service, "empStatus", "EmpStatus", "employeeStatus", "EmployeeStatus", "serviceStatus")
                          ?? BeisenJson.GetInt(basic, "empStatus", "EmpStatus", "employeeStatus", "EmployeeStatus") ?? 0,
            };
            emp.IsLeaver = emp.EmpStatus is 4 or -1 or 8;

            if (!string.IsNullOrEmpty(emp.UserId) || !string.IsNullOrEmpty(emp.EmployeeId))
                list.Add(emp);
        }
        return (list, scrollId);
    }

    private static JsonElement? FirstObject(JsonElement el, params string[] keys)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var k in keys)
            if (el.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Object)
                return v;
        return null;
    }

    private static JsonElement? FirstArrayItem(JsonElement el, params string[] keys)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var k in keys)
            if (el.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0)
                return v[0];
        return null;
    }
}
