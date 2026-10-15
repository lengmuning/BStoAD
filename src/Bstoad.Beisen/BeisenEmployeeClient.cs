using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bstoad.Core.Models;
using Bstoad.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bstoad.Beisen;

/// <summary>probe-employee 的一次接口调用（原始响应，未解析）。</summary>
public sealed record ByIdsProbeCall(string Label, string Path, string RequestBody, int Status, string Body);

/// <summary>probe-employee 的结果：两个接口的原始响应 + 合并解析出的员工。</summary>
public sealed record ByIdsProbeResult(IReadOnlyList<ByIdsProbeCall> Calls, IReadOnlyList<BeisenEmployee> Employees);

public interface IBeisenEmployeeClient
{
    /// <summary>全量：从 FullPullStartDate 起按 90 天分片滚动拉取全部员工。</summary>
    Task<IReadOnlyList<BeisenEmployee>> SearchAllAsync(bool includeDeleted, CancellationToken ct = default);

    /// <summary>指定时间窗（内部自动按 ≤90 天分片）。</summary>
    Task<IReadOnlyList<BeisenEmployee>> SearchByTimeWindowAsync(
        DateTime startUtc, DateTime stopUtc, bool includeDeleted, CancellationToken ct = default);

    /// <summary>
    /// 按 userId 精确查询：员工信息 + 任职记录两个接口，按 userID 合并（两边都查到的才返回）。
    /// HTTP 失败或北森返回业务错误码时抛异常。
    /// </summary>
    Task<IReadOnlyList<BeisenEmployee>> GetByUserIdsAsync(IReadOnlyCollection<string> userIds, CancellationToken ct = default);

    /// <summary>probe-employee 用：发与 GetByUserIdsAsync 相同的请求，返回原始响应与合并解析结果，不抛 HTTP 错误。</summary>
    Task<ByIdsProbeResult> ProbeByUserIdsAsync(IReadOnlyCollection<string> userIds, CancellationToken ct = default);
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
        var result = new List<BeisenEmployee>();
        foreach (var chunk in ValidIds(userIds).Chunk(300)) // 两个接口都要求 ID 个数 ≤ 300
        {
            var basic = await PostByIdsAsync(_options.EmployeeBasicByIdsPath, chunk, _options.EmployeeBasicByIdsColumns, option: null, ct);
            EnsureSuccess(_options.EmployeeBasicByIdsPath, basic.Status, basic.Body);
            var service = await PostByIdsAsync(_options.EmployeeServiceByIdsPath, chunk, _options.EmployeeServiceByIdsColumns,
                _options.EmployeeServiceByIdsOption, ct);
            EnsureSuccess(_options.EmployeeServiceByIdsPath, service.Status, service.Body);
            result.AddRange(ParsePage(MergeBasicAndService(basic.Body, service.Body), _options.TitleFields).Item1);
        }
        return result;
    }

    public async Task<ByIdsProbeResult> ProbeByUserIdsAsync(IReadOnlyCollection<string> userIds, CancellationToken ct = default)
    {
        var ids = ValidIds(userIds).Take(300).ToArray();
        var basic = await PostByIdsAsync(_options.EmployeeBasicByIdsPath, ids, _options.EmployeeBasicByIdsColumns, option: null, ct);
        var service = await PostByIdsAsync(_options.EmployeeServiceByIdsPath, ids, _options.EmployeeServiceByIdsColumns,
            _options.EmployeeServiceByIdsOption, ct);
        var calls = new List<ByIdsProbeCall>
        {
            new("员工信息", _options.EmployeeBasicByIdsPath, basic.RequestBody, basic.Status, basic.Body),
            new("任职记录", _options.EmployeeServiceByIdsPath, service.RequestBody, service.Status, service.Body),
        };
        IReadOnlyList<BeisenEmployee> employees = Array.Empty<BeisenEmployee>();
        if (IsSuccess(basic.Status, basic.Body) && IsSuccess(service.Status, service.Body))
            employees = ParsePage(MergeBasicAndService(basic.Body, service.Body), _options.TitleFields).Item1;
        return new ByIdsProbeResult(calls, employees);
    }

    /// <summary>北森 userID 是正整数（接口参数为 int32 数组）；非数字的 ID 直接丢弃。</summary>
    private static IEnumerable<string> ValidIds(IEnumerable<string> userIds) =>
        userIds.Where(id => int.TryParse(id, out var n) && n > 0).Distinct(StringComparer.Ordinal);

    private async Task<(string RequestBody, int Status, string Body)> PostByIdsAsync(
        string path, IReadOnlyCollection<string> ids, string[] columns, string? option, CancellationToken ct)
    {
        var token = await _tokenProvider.GetAccessTokenAsync(ct);
        var client = _httpClientFactory.CreateClient(nameof(BeisenEmployeeClient));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var body = new Dictionary<string, object?>
        {
            ["oIds"] = ids.Select(int.Parse).ToArray(),
            ["isWithDeleted"] = false, // 已删除的员工不建号、不改号；查不到时由每日全量处理
            ["columns"] = columns.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        };
        if (!string.IsNullOrWhiteSpace(option)) body["option"] = option;
        var requestBody = JsonSerializer.Serialize(body);

        var url = new Uri(new Uri(_options.BaseUrl), path);
        using var content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync(url, content, ct);
        return (requestBody, (int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    /// <summary>HTTP 2xx 且北森响应信封 code 为 200（北森参数错误时信封 code 为 417 等）。</summary>
    private static bool IsSuccess(int status, string body)
    {
        if (status < 200 || status >= 300) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return !doc.RootElement.TryGetProperty("code", out var code) || code.ToString() == "200";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void EnsureSuccess(string path, int status, string body)
    {
        if (!IsSuccess(status, body))
            throw new InvalidOperationException($"{path} HTTP {status}: {BeisenJson.Truncate(body, 800)}");
    }

    /// <summary>
    /// 把员工信息与任职记录按 userID 合并成 [{employeeInfo, recordInfo}]，交给 ParsePage 按现有规则解析。
    /// 只有两边都查到的员工才保留：缺姓名会建出空名字的账号，缺任职信息无法定位账号与部门。
    /// </summary>
    private static string MergeBasicAndService(string basicJson, string serviceJson)
    {
        static JsonArray DataArray(string json) => JsonNode.Parse(json)?["data"] as JsonArray ?? new JsonArray();
        static string? UserIdOf(JsonNode? n) => n is JsonObject o && o.TryGetPropertyValue("userID", out var v) && v != null ? v.ToString() : null;

        var services = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var s in DataArray(serviceJson))
        {
            var id = UserIdOf(s);
            if (id == null || s == null) continue;
            // 同一人多条任职记录（未用 option 时可能出现）：优先当前生效的那条
            var isCurrent = s is JsonObject so && so.TryGetPropertyValue("isCurrentRecord", out var cur) && cur?.ToString() == "true";
            if (!services.ContainsKey(id) || isCurrent) services[id] = s;
        }

        var merged = new JsonArray();
        foreach (var b in DataArray(basicJson))
        {
            var id = UserIdOf(b);
            if (id == null || b == null || !services.TryGetValue(id, out var s)) continue;
            merged.Add(new JsonObject { ["employeeInfo"] = b.DeepClone(), ["recordInfo"] = s.DeepClone() });
        }
        return new JsonObject { ["data"] = merged, ["code"] = "200" }.ToJsonString();
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
                Phone = BeisenJson.GetStr(basic, "officeTel", "OfficeTel", "officePhone", "OfficePhone", "Phone", "phone"),
                OrgId = BeisenJson.GetStr(service, "oIdDepartment", "OIdDepartment", "DepartmentId", "departmentId", "OrgId", "orgId"),
                OrgName = BeisenJson.GetStr(service, "oIdDepartment_TranslateResult", "OIdDepartment_TranslateResult",
                    "DepartmentName", "departmentName", "OrgName", "orgName")
                          ?? TranslateText(service, "OIdDepartmentText"),
                Title = BeisenJson.GetStr(service, titleFields) ?? BeisenJson.GetStr(basic, titleFields)
                        ?? TranslateText(service, "OIdJobPostText"),
                EmpStatus = ParseEmpStatus(service) ?? ParseEmpStatus(basic) ?? 0,
            };
            emp.IsLeaver = emp.EmpStatus is 4 or -1 or 8;

            if (!string.IsNullOrEmpty(emp.UserId) || !string.IsNullOrEmpty(emp.EmployeeId))
                list.Add(emp);
        }
        return (list, scrollId);
    }

    // 北森人员状态枚举名 → 状态码（与 CoreHr EmployeeStatus 枚举顺序一致：1 待入职 2 试用 3 正式 4 调出 5 待调入 6 退休 8 离职 12 非正式）
    private static readonly Dictionary<string, int> EmpStatusNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ForEntry"] = 1, ["OnProbation"] = 2, ["InService"] = 3, ["TransferredOut"] = 4, ["TransferredIn"] = 5,
        ["Retirement"] = 6, ["Reemploy"] = 7, ["Dimission"] = 8, ["Die"] = 9, ["StopReemploy"] = 10,
        ["ForReemoloy"] = 11, ["Informal"] = 12,
    };

    /// <summary>人员状态：数字或数字字符串（"3"），也接受枚举名（"InService"）。解析不了返回 null。</summary>
    private static int? ParseEmpStatus(JsonElement el)
    {
        var n = BeisenJson.GetInt(el, "empStatus", "EmpStatus", "employeeStatus", "EmployeeStatus", "serviceStatus");
        if (n != null) return n;
        var name = BeisenJson.GetStr(el, "empStatus", "EmpStatus", "employeeStatus", "EmployeeStatus");
        return name != null && EmpStatusNames.TryGetValue(name.Trim(), out var code) ? code : null;
    }

    /// <summary>translateProperties 里的翻译文本（如 OIdDepartmentText）；北森 v5 已下线动态翻译，多数情况下为空。</summary>
    private static string? TranslateText(JsonElement el, string key) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty("translateProperties", out var tp)
            ? BeisenJson.GetStr(tp, key)
            : null;

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
