using Bstoad.ActiveDirectory;
using Bstoad.Beisen;
using Bstoad.Core.Models;
using Bstoad.Core.Options;
using Bstoad.Sync.Mapping;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bstoad.Sync;

public interface ISyncOrchestrator
{
    Task<SyncResult> RunOnceAsync(CancellationToken ct = default);
}

public sealed class SyncOrchestrator : ISyncOrchestrator
{
    private readonly IBeisenTokenProvider _tokenProvider;
    private readonly IBeisenOrgClient _orgClient;
    private readonly IBeisenEmployeeClient _empClient;
    private readonly ILdapConnectionFactory _ldapFactory;
    private readonly IAdProvisioner _ad;
    private readonly ActiveDirectoryOptions _adOpt;
    private readonly SyncOptions _sync;
    private readonly ILogger<SyncOrchestrator> _logger;

    public SyncOrchestrator(
        IBeisenTokenProvider tokenProvider,
        IBeisenOrgClient orgClient,
        IBeisenEmployeeClient empClient,
        ILdapConnectionFactory ldapFactory,
        IAdProvisioner ad,
        IOptions<ActiveDirectoryOptions> adOptions,
        IOptions<SyncOptions> syncOptions,
        ILogger<SyncOrchestrator> logger)
    {
        _tokenProvider = tokenProvider;
        _orgClient = orgClient;
        _empClient = empClient;
        _ldapFactory = ldapFactory;
        _ad = ad;
        _adOpt = adOptions.Value;
        _sync = syncOptions.Value;
        _logger = logger;
    }

    public async Task<SyncResult> RunOnceAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new SyncResult();
        var mapper = new EmployeeMapper(_adOpt);

        var scope = _sync.OrgsOnly ? "仅组织架构" : _sync.UsersOnly ? "仅人员" : "组织+人员";
        _logger.LogInformation(
            "================ 同步开始 范围={Scope} DryRun={DryRun} FullPull={Full} ReconcileLeavers={Recon} ================",
            scope, _sync.DryRun, _sync.FullPull, _sync.ReconcileLeavers);

        _logger.LogInformation("[1/6] 取 Beisen access_token");
        await _tokenProvider.GetAccessTokenAsync(ct);

        _logger.LogInformation("[2/6] 测试 AD 绑定 {Host}", _adOpt.LdapHost);
        var adReady = true;
        try { _ldapFactory.TestBind(); }
        catch (Exception ex)
        {
            adReady = false;
            _logger.LogError(ex, "AD 绑定失败，所有 AD 操作将跳过");
            result.Errors.Add("AD bind failed: " + ex.Message);
            result.AddEvent(SyncEventLevel.Critical, SyncEventCategory.AdBindFailed, _adOpt.LdapHost,
                _adOpt.LdapHost, "AD 绑定失败，本轮所有 AD 操作已跳过：" + ex.Message);
        }

        _logger.LogInformation("[3/6] 拉取全部组织");
        var orgs = await _orgClient.GetAllOrgsAsync(ct);
        result.OrgsFetched = orgs.Count;

        if (_sync.DecodeHtmlEntitiesInNames)
            foreach (var o in orgs) o.Name = System.Net.WebUtility.HtmlDecode(o.Name);

        if (!string.IsNullOrWhiteSpace(_sync.RootOrgId))
        {
            var filtered = FilterSubtree(orgs, _sync.RootOrgId);
            _logger.LogInformation("RootOrgId={Root}：取其子树，保留 {Kept}/{Total} 个组织，根的直接子级将挂在 {Base} 下",
                _sync.RootOrgId, filtered.Count, orgs.Count, _adOpt.BaseUserOu);
            if (filtered.Count == 0)
                _logger.LogWarning("RootOrgId={Root} 没有匹配到任何组织，请核对该 OrgId 是否存在", _sync.RootOrgId);
            orgs = filtered;
        }

        var pathBuilder = new OrgPathBuilder(orgs, _adOpt.BaseUserOu);

        var orgIdToDn = new Dictionary<string, string>(StringComparer.Ordinal);

        var ensureOu = adReady && _sync.EnsureOuTree && !_sync.UsersOnly;
        if (ensureOu)
        {
            _logger.LogInformation("[3.1] OU 同步（按 OrgId 锚点匹配 → 改名/移动/新建），共 {Count} 个", orgs.Count);
            EnsureOuRoots();
            SyncOrgs(orgs, orgIdToDn, result);
            DetectAbandonedOus(orgs, result);
        }
        else if (adReady && _sync.UsersOnly)
        {
            _logger.LogInformation("[3.1] --users-only：跳过建 OU；按锚点扫已有 OU 建立 OrgId→DN 映射");
            foreach (var ou in _ad.ListOusUnder(_adOpt.BaseUserOu))
            {
                if (!string.IsNullOrEmpty(ou.OrgId)) orgIdToDn[ou.OrgId] = ou.Dn;
            }
            _logger.LogInformation("  发现已锚定 OU {Count} 个", orgIdToDn.Count);
        }

        if (_sync.OrgsOnly)
        {
            _logger.LogInformation("[4-6/6] --orgs-only：跳过员工拉取/应用/离职差集");
            sw.Stop();
            result.Duration = sw.Elapsed;
            _logger.LogInformation(
                "================ 完成 用时 {Ms}ms · OU 创建 {Cr} · OU 改名/移动 {Rm} · 撤销 {Ab} · 失败 {F} ================",
                sw.ElapsedMilliseconds, result.OuCreated, result.OuRenamedOrMoved, result.OrgsAbandoned, result.Failures);
            AppendStats(result);
            return result;
        }

        _logger.LogInformation("[4/6] 拉取员工 {Mode}",
            _sync.FullPull ? "全量（FullPullStartDate 起分片）" : $"时间窗最近 {_sync.InitialBackfillDays} 天");
        var stop = DateTime.UtcNow.AddSeconds(-_sync.TimeWindowSafetyOffsetSeconds);
        var emps = _sync.FullPull
            ? await _empClient.SearchAllAsync(includeDeleted: true, ct)
            : await _empClient.SearchByTimeWindowAsync(stop.AddDays(-_sync.InitialBackfillDays), stop, includeDeleted: true, ct);
        result.EmployeesFetched = emps.Count;

        // 拉取量哨兵：全量员工数骤降视为数据源异常，本轮拒绝跑差集
        var pullSuspect = false;
        if (_sync.FullPull)
        {
            var last = ReadLastFullPullCount();
            if (last > 0 && emps.Count < last * _sync.MinPullRatioVsLastRun)
            {
                pullSuspect = true;
                _logger.LogError("⚠️ 拉取量哨兵：本轮员工 {Now} 条 < 上轮 {Last} 条 × {Ratio:P0}，疑似北森数据源/权限异常——离职差集本轮拒绝执行",
                    emps.Count, last, _sync.MinPullRatioVsLastRun);
                result.Errors.Add($"pull sentinel: {emps.Count} < {last} x {_sync.MinPullRatioVsLastRun}");
                result.AddEvent(SyncEventLevel.Critical, SyncEventCategory.PullSentinel, "pull", "员工拉取量",
                    $"本轮拉到 {emps.Count} 条，上轮 {last} 条，低于 {_sync.MinPullRatioVsLastRun:P0}，疑似北森数据或权限异常，离职差集已拒绝执行");
            }
            else
            {
                SaveLastFullPullCount(emps.Count);
            }
        }

        foreach (var e in emps)
        {
            if (_sync.DecodeHtmlEntitiesInNames)
            {
                e.Name = System.Net.WebUtility.HtmlDecode(e.Name);
                if (e.OrgName != null) e.OrgName = System.Net.WebUtility.HtmlDecode(e.OrgName);
                if (e.Title != null) e.Title = System.Net.WebUtility.HtmlDecode(e.Title);
            }
            ApplyStatusRules(e);
        }

        // 员工侧 department 文本兜底：北森翻译字段缺失时，用组织树的名称按 OrgId 补
        var orgNameById = orgs
            .Where(o => !string.IsNullOrEmpty(o.OrgId) && !string.IsNullOrEmpty(o.Name))
            .ToDictionary(o => o.OrgId, o => o.Name, StringComparer.Ordinal);

        var only = _sync.OnlyEmployeeIds is { Length: > 0 }
            ? new HashSet<string>(_sync.OnlyEmployeeIds, StringComparer.OrdinalIgnoreCase)
            : null;
        if (only != null)
            _logger.LogWarning("[试点模式] --only 只处理 {Ids}，其他员工跳过，离职差集禁用",
                string.Join(",", _sync.OnlyEmployeeIds));
        if (_sync.LeaversOnly)
            _logger.LogWarning("[离职模式] --leavers-only 只处理离职员工（离职人员部门/离职状态），在职全部跳过，离职差集禁用");

        _logger.LogInformation("[5/6] 应用员工（决策树 C→A→B），共 {Count} 条", emps.Count);
        var skippedByFilter = 0;
        foreach (var emp in emps)
        {
            ct.ThrowIfCancellationRequested();
            if (only != null && !only.Contains(emp.EmployeeId))
            {
                skippedByFilter++;
                continue;
            }
            if (_sync.LeaversOnly)
            {
                var isLeaver = emp.IsLeaver ||
                    (!string.IsNullOrWhiteSpace(_sync.LeaverOrgId) &&
                     string.Equals(emp.OrgId, _sync.LeaverOrgId, StringComparison.Ordinal));
                if (!isLeaver)
                {
                    skippedByFilter++;
                    continue;
                }
            }
            if (string.IsNullOrEmpty(emp.OrgName) && !string.IsNullOrEmpty(emp.OrgId)
                && orgNameById.TryGetValue(emp.OrgId, out var orgName))
            {
                emp.OrgName = orgName;
            }
            try
            {
                ApplyEmployee(emp, mapper, pathBuilder, orgIdToDn, adReady, result);
            }
            catch (Exception ex)
            {
                result.Failures++;
                result.Errors.Add($"emp {emp.EmployeeId}/{emp.UserId}: {ex.Message}");
                result.AddEvent(SyncEventLevel.Warning, SyncEventCategory.Failure, "emp:" + emp.EmployeeId,
                    emp.Name, $"工号 {emp.EmployeeId} 处理失败：{ex.Message}");
                _logger.LogError(ex, "处理员工失败: {EmpId} {Name}", emp.EmployeeId, emp.Name);
            }
        }
        if (only != null || _sync.LeaversOnly)
            _logger.LogInformation("[过滤] 跳过 {Skipped} 条不符合过滤条件的员工", skippedByFilter);

        if (only != null || _sync.LeaversOnly)
        {
            _logger.LogInformation("[6/6] 试点/离职模式：离职差集不运行");
        }
        else if (pullSuspect)
        {
            _logger.LogError("[6/6] 拉取量哨兵触发，离职差集跳过（人工确认北森数据正常后重跑）");
        }
        else if (adReady && _sync.ReconcileLeavers)
        {
            if (!_sync.FullPull)
            {
                _logger.LogWarning("[6/6] 跳过离职差集：仅在 FullPull=true 时安全执行（增量窗口不能跑差集）");
            }
            else
            {
                _logger.LogInformation("[6/6] 离职差集：扫 {Base} 下纳管账号", _adOpt.BaseUserOu);
                ReconcileLeavers(emps, result);
            }
        }
        else
        {
            _logger.LogInformation("[6/6] 跳过离职差集（ReconcileLeavers=false）");
        }

        sw.Stop();
        result.Duration = sw.Elapsed;
        _logger.LogInformation(
            "================ 完成 用时 {Ms}ms · OU新建{OuC}/改名{OuR}/撤销{OuA} · 用户创建{Cr} 更新{Up} 移动{Mv} 禁用{Dis} 接管Legacy{La} 场景E{E} 离职差集{Lr} 保护跳过{P} 失败{F} ================",
            sw.ElapsedMilliseconds, result.OuCreated, result.OuRenamedOrMoved, result.OrgsAbandoned,
            result.UsersCreated, result.UsersUpdated, result.UsersMoved,
            result.UsersDisabled, result.LegacyAdopted, result.ScenarioE, result.LeaversReconciled,
            result.ProtectedSkipped, result.Failures);
        AppendStats(result);

        // 全量、无过滤的运行才代表「全貌」，写出待处理清单供图形界面展示
        if (_sync.FullPull && !_sync.UsersOnly && only == null && !_sync.LeaversOnly)
            WritePendingReport(result);

        return result;
    }

    /// <summary>empStatus 业务口径：4 调出 / 6 退休是否按离职处理（默认与 v1.0 一致）。</summary>
    private void ApplyStatusRules(BeisenEmployee e)
    {
        if (e.EmpStatus == 4) e.IsLeaver = _sync.TreatTransferOutAsLeaver;
        else if (e.EmpStatus == 6) e.IsLeaver = _sync.TreatRetiredAsLeaver;
    }

    private void WritePendingReport(SyncResult r)
    {
        try
        {
            var report = new PendingReport { GeneratedAt = DateTime.Now, DryRun = _sync.DryRun, Events = r.Events };
            var json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            });
            File.WriteAllText(StateFile("pending.json"), json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入待处理清单失败（不影响同步）");
        }
    }

    private void EnsureOuRoots()
    {
        if (_sync.DryRun) return;
        try { _ad.EnsureOu(_adOpt.BaseUserOu); } catch { /* 已存在或无权 */ }
        // LeaverOu 在同步树内（如北森的「离职人员」部门）时不在这里裸建——
        // 由 OU 同步按锚点创建，避免出现无锚点的重名 OU
        var leaverInsideBase = _adOpt.LeaverOu.EndsWith("," + _adOpt.BaseUserOu, StringComparison.OrdinalIgnoreCase);
        if (!leaverInsideBase)
            try { _ad.EnsureOu(_adOpt.LeaverOu); } catch { /* 已存在或无权 */ }
    }

    private void SyncOrgs(IReadOnlyList<BeisenOrg> orgs, Dictionary<string, string> orgIdToDn, SyncResult result)
    {
        var sorted = OrgTopologicalSorter.Sort(orgs);

        foreach (var org in sorted)
        {
            try
            {
                var parentDn = !string.IsNullOrEmpty(org.ParentOrgId)
                    && orgIdToDn.TryGetValue(org.ParentOrgId, out var pDn)
                        ? pDn
                        : _adOpt.BaseUserOu;
                var desiredRdn = $"OU={EscapeRdn(org.Name)}";

                var existing = _ad.FindOuByOrgIdMarker(org.OrgId);
                string actualDn;

                if (existing == null)
                {
                    if (_sync.DryRun)
                    {
                        actualDn = $"{desiredRdn},{parentDn}";
                        _logger.LogInformation("  [新建·dry] OrgId={OrgId} {Name} → {Dn}",
                            org.OrgId, org.Name, actualDn);
                    }
                    else
                    {
                        actualDn = _ad.CreateOuForOrg(parentDn, org.Name, org.OrgId);
                    }
                    result.OuCreated++;
                }
                else
                {
                    var currentRdn = existing.Rdn;
                    var currentParent = existing.ParentDn;
                    var needsMove = !string.Equals(currentParent, parentDn, StringComparison.OrdinalIgnoreCase);
                    var needsRename = !string.Equals(currentRdn, desiredRdn, StringComparison.OrdinalIgnoreCase);

                    if (needsMove || needsRename)
                    {
                        var action = (needsMove, needsRename) switch
                        {
                            (true, true) => "改名+移动",
                            (true, false) => "移动",
                            _ => "改名"
                        };
                        if (_sync.DryRun)
                        {
                            actualDn = $"{desiredRdn},{parentDn}";
                            _logger.LogInformation("  [{Action}·dry] OrgId={OrgId} {Old} → {New}",
                                action, org.OrgId, existing.Dn, actualDn);
                        }
                        else
                        {
                            actualDn = _ad.RenameOrMoveOu(existing.Dn, parentDn, desiredRdn);
                            _logger.LogInformation("  [{Action}] OrgId={OrgId} {Old} → {New}",
                                action, org.OrgId, existing.Dn, actualDn);
                        }
                        result.OuRenamedOrMoved++;
                    }
                    else
                    {
                        actualDn = existing.Dn;
                    }
                }

                orgIdToDn[org.OrgId] = actualDn;
            }
            catch (Exception ex)
            {
                result.Failures++;
                result.Errors.Add($"OU sync {org.OrgId}/{org.Name}: {ex.Message}");
                result.AddEvent(SyncEventLevel.Warning, SyncEventCategory.Failure, "ou:" + org.OrgId,
                    org.Name, $"部门 OU 同步失败（OrgId={org.OrgId}）：{ex.Message}");
                _logger.LogError(ex, "OU 同步失败: OrgId={OrgId} Name={Name}", org.OrgId, org.Name);
            }
        }
    }

    private void DetectAbandonedOus(IReadOnlyList<BeisenOrg> beisenOrgs, SyncResult result)
    {
        var beisenIds = new HashSet<string>(beisenOrgs.Select(o => o.OrgId), StringComparer.Ordinal);
        var adOus = _ad.ListOusUnder(_adOpt.BaseUserOu);
        foreach (var ou in adOus)
        {
            if (string.IsNullOrEmpty(ou.OrgId)) continue; // 没锚点：手动建的，不管
            if (beisenIds.Contains(ou.OrgId)) continue;   // 北森还有
            result.OrgsAbandoned++;
            result.AddEvent(SyncEventLevel.Warning, SyncEventCategory.OrgAbandoned, ou.OrgId,
                ou.Ou, $"北森已不存在该部门（OrgId={ou.OrgId}），OU 留在原地未动", ou.Dn);
            _logger.LogWarning("  [部门撤销] AD OU {Dn} (OrgId={OrgId}) 在北森已不存在 — 留原地不动，请人工 review",
                ou.Dn, ou.OrgId);
        }
    }

    private static string EscapeRdn(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in s)
        {
            if (",+\"\\<>;=".IndexOf(c) >= 0) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>AD 显示名与北森姓名的宽松比对：任一为空视为通过；相等或互为包含视为通过。</summary>
    private static bool NamesRoughlyMatch(string? adName, string beisenName)
    {
        if (string.IsNullOrWhiteSpace(adName) || string.IsNullOrWhiteSpace(beisenName)) return true;
        var a = adName.Replace(" ", "").ToLowerInvariant();
        var b = beisenName.Replace(" ", "").ToLowerInvariant();
        return a.Contains(b) || b.Contains(a);
    }

    private static string StateFile(string name)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "state");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    private int ReadLastFullPullCount()
    {
        try
        {
            var f = StateFile("last-full-pull-count.txt");
            return File.Exists(f) && int.TryParse(File.ReadAllText(f).Trim(), out var n) ? n : 0;
        }
        catch { return 0; }
    }

    private void SaveLastFullPullCount(int count)
    {
        try { File.WriteAllText(StateFile("last-full-pull-count.txt"), count.ToString()); }
        catch (Exception ex) { _logger.LogWarning(ex, "写入拉取量基线失败（不影响同步）"); }
    }

    private void AppendStats(SyncResult r)
    {
        try
        {
            var f = StateFile("stats.csv");
            if (!File.Exists(f))
                File.AppendAllText(f,
                    "time,dryRun,fullPull,orgsFetched,employeesFetched,ouCreated,ouRenamed,orgsAbandoned," +
                    "usersCreated,usersUpdated,usersMoved,usersDisabled,legacyAdopted,scenarioE," +
                    "leaversReconciled,protectedSkipped,failures,durationMs\n");
            File.AppendAllText(f,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{_sync.DryRun},{_sync.FullPull},{r.OrgsFetched},{r.EmployeesFetched}," +
                $"{r.OuCreated},{r.OuRenamedOrMoved},{r.OrgsAbandoned},{r.UsersCreated},{r.UsersUpdated},{r.UsersMoved}," +
                $"{r.UsersDisabled},{r.LegacyAdopted},{r.ScenarioE},{r.LeaversReconciled},{r.ProtectedSkipped}," +
                $"{r.Failures},{(long)r.Duration.TotalMilliseconds}\n");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入统计 CSV 失败（不影响同步）");
        }
    }

    /// <summary>取 rootOrgId 的子树（不含根本身）；根的直接子级 ParentOrgId 清空 → 挂到 BaseUserOu。</summary>
    private static IReadOnlyList<BeisenOrg> FilterSubtree(IReadOnlyList<BeisenOrg> orgs, string rootOrgId)
    {
        var byParent = orgs
            .Where(o => !string.IsNullOrEmpty(o.ParentOrgId))
            .GroupBy(o => o.ParentOrgId!)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var kept = new List<BeisenOrg>();
        var queue = new Queue<string>();
        queue.Enqueue(rootOrgId);
        var seen = new HashSet<string>(StringComparer.Ordinal) { rootOrgId };

        while (queue.Count > 0)
        {
            var parentId = queue.Dequeue();
            if (!byParent.TryGetValue(parentId, out var children)) continue;
            foreach (var child in children)
            {
                if (!seen.Add(child.OrgId)) continue;
                if (string.Equals(child.ParentOrgId, rootOrgId, StringComparison.Ordinal))
                    child.ParentOrgId = null; // 直接子级提升为顶层
                kept.Add(child);
                queue.Enqueue(child.OrgId);
            }
        }
        return kept;
    }

    private void ApplyEmployee(BeisenEmployee emp, EmployeeMapper mapper, OrgPathBuilder pathBuilder,
        Dictionary<string, string> orgIdToDn, bool adReady, SyncResult result)
    {
        if (string.IsNullOrWhiteSpace(emp.EmployeeId))
        {
            _logger.LogWarning("跳过：工号为空 userId={UserId} name={Name}", emp.UserId, emp.Name);
            // 只记录在职人员；离职且无工号的历史数据不打扰
            if (!emp.IsLeaver)
                result.AddEvent(SyncEventLevel.Info, SyncEventCategory.EmptyEmployeeId, "uid:" + emp.UserId,
                    emp.Name, $"北森中该员工没有工号（userId={emp.UserId}），无法建号或匹配，请 HR 补录");
            return;
        }

        // 挂在北森「离职人员」部门 = 离职（用户规则：该 OU 里的账号一律禁用）
        if (!string.IsNullOrWhiteSpace(_sync.LeaverOrgId) &&
            string.Equals(emp.OrgId, _sync.LeaverOrgId, StringComparison.Ordinal))
        {
            emp.IsLeaver = true;
        }

        var attrs = mapper.Map(emp);
        string targetOu;
        if (emp.IsLeaver)
        {
            targetOu = _adOpt.LeaverOu;
        }
        else if (!string.IsNullOrEmpty(emp.OrgId) && orgIdToDn.TryGetValue(emp.OrgId, out var ouDn))
        {
            // 优先用 OU 锚点映射的实际 DN（追改名/移动）
            targetOu = ouDn;
        }
        else
        {
            // fallback：用名字推算（OU 同步阶段没建好或 UsersOnly 模式下锚点缺失时）
            targetOu = pathBuilder.BuildOuDn(emp.OrgId ?? "");
            if (!string.IsNullOrEmpty(emp.OrgId))
                _logger.LogWarning("  [缺锚点] 员工 {Name} 的部门 OrgId={OrgId} 不在锚点映射中，fallback 按名字: {Ou}",
                    emp.Name, emp.OrgId, targetOu);
        }

        // 试点模式下完整打印解析字段 + AD 映射，供逐项人工核对
        if (_sync.OnlyEmployeeIds is { Length: > 0 })
        {
            _logger.LogInformation(
                "  [字段核对] 北森侧: 工号={EmpId} 姓名={Name} 邮箱={Mail} 手机={Mobile} 座机={Phone} 部门={OrgName}(OrgId={OrgId}) 职位={Title} empStatus={Status} 离职={Leaver}",
                emp.EmployeeId, emp.Name, emp.Email ?? "<空>", emp.Mobile ?? "<空>", emp.Phone ?? "<空>",
                emp.OrgName ?? "<空>", emp.OrgId ?? "<空>", emp.Title ?? "<空>", emp.EmpStatus, emp.IsLeaver);
            _logger.LogInformation(
                "  [字段核对] 将写入 AD: SAM={Sam} UPN={Upn} 显示名={Disp} mail={AMail} mobile={AMobile} telephone={ATel} department={ADept} title={ATitle} → OU={Ou}",
                attrs.SamAccountName, attrs.UserPrincipalName, attrs.DisplayName,
                attrs.Mail ?? "<空>", attrs.Mobile ?? "<空>", attrs.TelephoneNumber ?? "<空>",
                attrs.Department ?? "<空>", attrs.Title ?? "<空>", targetOu);
        }

        if (!adReady)
        {
            _logger.LogInformation("  [dry/no-ad] emp [{Code}] {Name} 状态={St} → SAM={Sam} OU={Ou}",
                emp.EmployeeId, emp.Name, emp.EmpStatus, attrs.SamAccountName, targetOu);
            return;
        }

        // 场景 C/D/E：按 employeeID 全域查
        var byEmpId = _ad.FindUserByEmployeeId(emp.EmployeeId);
        if (byEmpId != null)
        {
            if (AdProtection.IsProtected(byEmpId, _adOpt, out var whyC))
            {
                result.ProtectedSkipped++;
                result.AddEvent(SyncEventLevel.Info, SyncEventCategory.Protected, emp.EmployeeId, emp.Name,
                    $"{whyC}，同步不自动管理，离职时需人工回收", byEmpId.Dn);
                _logger.LogWarning("  [保护] employeeID={EmpId} 命中受保护账号 {Dn}（{Why}）→ 不动，请人工核实为何该账号带此工号",
                    emp.EmployeeId, byEmpId.Dn, whyC);
                return;
            }
            if (!NamesRoughlyMatch(byEmpId.DisplayName, emp.Name))
            {
                result.AddEvent(SyncEventLevel.Warning, SyncEventCategory.ScenarioDSuspect, emp.EmployeeId, emp.Name,
                    $"工号 {emp.EmployeeId} 命中的 AD 账号姓名是「{byEmpId.DisplayName}」，与北森「{emp.Name}」不符，请核实是否错填", byEmpId.Dn);
                _logger.LogWarning("  [场景D嫌疑] employeeID={EmpId} 命中 {Dn}，但 AD 姓名「{Ad}」与北森「{Bs}」不符——继续处理，请人工复核是否错填",
                    emp.EmployeeId, byEmpId.Dn, byEmpId.DisplayName, emp.Name);
            }
            ApplyExisting(byEmpId, attrs, targetOu, emp, scenarioHint: "C/D/E", result);
            return;
        }

        // 场景 A：Find-Legacy by SAM=工号（且 employeeID 为空）
        var legacy = _ad.FindLegacyBySam(emp.EmployeeId);
        if (legacy != null)
        {
            if (AdProtection.IsProtected(legacy, _adOpt, out var whyA))
            {
                result.ProtectedSkipped++;
                result.AddEvent(SyncEventLevel.Info, SyncEventCategory.Protected, emp.EmployeeId, emp.Name,
                    $"{whyA}，登录名与工号相同但不自动接管", legacy.Dn);
                _logger.LogWarning("  [保护] SAM={Sam} 命中受保护账号 {Dn}（{Why}）→ 不接管不新建，请人工处理命名冲突",
                    emp.EmployeeId, legacy.Dn, whyA);
                return;
            }
            if (!NamesRoughlyMatch(legacy.DisplayName, emp.Name))
            {
                result.Failures++;
                result.Errors.Add($"场景A拒绝: SAM={emp.EmployeeId} AD姓名「{legacy.DisplayName}」≠北森「{emp.Name}」");
                result.AddEvent(SyncEventLevel.Warning, SyncEventCategory.ScenarioARejected, emp.EmployeeId, emp.Name,
                    $"AD 中登录名 {emp.EmployeeId} 的账号姓名是「{legacy.DisplayName}」，与北森不符，未接管", legacy.Dn);
                _logger.LogError("  [场景A拒绝] SAM={Sam} 撞名但姓名不符：AD「{Ad}」 vs 北森「{Bs}」→ 跳过接管，请人工核实 {Dn}",
                    emp.EmployeeId, legacy.DisplayName, emp.Name, legacy.Dn);
                return;
            }
            _logger.LogInformation("  [场景A] SAM={Sam} 命中老账号，employeeID 空 → 回填 + 接管 {Dn}",
                emp.EmployeeId, legacy.Dn);
            if (!_sync.DryRun) _ad.BackfillEmployeeId(legacy.Dn, emp.EmployeeId);
            result.LegacyAdopted++;
            ApplyExisting(legacy, attrs, targetOu, emp, scenarioHint: "A", result);
            return;
        }

        // 场景 B：New
        if (emp.IsLeaver)
        {
            _logger.LogInformation("  [跳过] 员工已离职但 AD 无账号: {EmpId} {Name}", emp.EmployeeId, emp.Name);
            return;
        }

        // SAM 冲突守卫：employeeID 没匹配上、Legacy 也没接管到，但 SAM 已被占
        // （典型：该账号 employeeID 被错填成别人工号 = 场景 D 的变体）——不能盲建重名账号
        // 待入职（empStatus=1）是否提前建号：默认建（与 v1.0 一致）
        if (emp.EmpStatus == 1 && !_sync.CreateAccountForPendingHire)
        {
            _logger.LogInformation("  [待入职] {EmpId} {Name} 尚未入职，按规则暂不建号", emp.EmployeeId, emp.Name);
            result.AddEvent(SyncEventLevel.Info, SyncEventCategory.PendingHireSkipped, emp.EmployeeId, emp.Name,
                "待入职，按规则暂不建号，入职后自动创建");
            return;
        }

        var samTaken = _ad.FindUserBySam(attrs.SamAccountName);
        if (samTaken != null)
        {
            result.Failures++;
            result.Errors.Add($"SAM 冲突: {attrs.SamAccountName} 已被 {samTaken.Dn} 占用 (其 employeeID={samTaken.EmployeeId})");
            result.AddEvent(SyncEventLevel.Warning, SyncEventCategory.SamConflict, emp.EmployeeId, emp.Name,
                $"登录名 {attrs.SamAccountName} 已被其他账号占用（其 employeeID={samTaken.EmployeeId}），未新建", samTaken.Dn);
            _logger.LogError("  [SAM冲突] {Sam} 已被 {Dn} 占用，其 employeeID={EmpId}（疑似错填）→ 跳过新建，请人工核对",
                attrs.SamAccountName, samTaken.Dn, samTaken.EmployeeId);
            return;
        }

        _logger.LogInformation("  [场景B] 新建账号 SAM={Sam} → {Ou}", attrs.SamAccountName, targetOu);
        if (!_sync.DryRun)
            _ad.CreateUser(targetOu, attrs, _adOpt.DefaultInitialPassword);
        result.UsersCreated++;
    }

    private void ApplyExisting(AdUserSnapshot existing, AdUserAttributes attrs, string targetOu,
        BeisenEmployee emp, string scenarioHint, SyncResult result)
    {
        var currentOu = existing.ParentOu;
        var inLeaverOu = currentOu.Equals(_adOpt.LeaverOu, StringComparison.OrdinalIgnoreCase)
                      || currentOu.EndsWith("," + _adOpt.LeaverOu, StringComparison.OrdinalIgnoreCase);
        var newDn = existing.Dn;
        var scenarioE = false;

        if (inLeaverOu && !emp.IsLeaver && !string.Equals(currentOu, targetOu, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("  [场景E] {Dn} 在离职 OU 但北森状态在职 → 移回 {Ou}，**不自动启用**，请人工 review",
                existing.Dn, targetOu);
            scenarioE = true;
            if (!_sync.DryRun) newDn = _ad.MoveUser(existing.Dn, targetOu, attrs.EmployeeId);
            result.UsersMoved++;
            result.ScenarioE++;
            if (existing.IsDisabled)
                result.AddEvent(SyncEventLevel.Warning, SyncEventCategory.ScenarioE, emp.EmployeeId, emp.Name,
                    "北森显示在职，账号已从离职 OU 移回部门，但仍是禁用状态，请确认后手动启用", newDn);
        }
        else if (!string.Equals(currentOu, targetOu, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("  [场景{Sc}/Move] {Dn} → {Ou}", scenarioHint, existing.Dn, targetOu);
            if (!_sync.DryRun)
            {
                if (emp.IsLeaver && _sync.DisableLeaverEvenIfMoveFails)
                {
                    // 离职账号：移动失败不能连带禁用失败——禁用比归档位置更重要
                    try { newDn = _ad.MoveUser(existing.Dn, targetOu, attrs.EmployeeId); }
                    catch (Exception ex)
                    {
                        result.Failures++;
                        result.Errors.Add($"leaver move {existing.Dn}: {ex.Message}");
                        result.AddEvent(SyncEventLevel.Warning, SyncEventCategory.Failure, "move:" + emp.EmployeeId, emp.Name,
                            $"离职账号移到离职 OU 失败，已在原位置禁用：{ex.Message}", existing.Dn);
                        _logger.LogError(ex, "  离职账号移动失败，改为原位置禁用: {Dn}", existing.Dn);
                    }
                }
                else
                {
                    newDn = _ad.MoveUser(existing.Dn, targetOu, attrs.EmployeeId);
                }
            }
            result.UsersMoved++;
        }
        else
        {
            _logger.LogInformation("  [场景{Sc}/Update] {Dn}", scenarioHint, existing.Dn);
        }

        if (!_sync.DryRun)
            _ad.UpdateUserAttributes(newDn, attrs);
        result.UsersUpdated++;

        if (emp.IsLeaver && !existing.IsDisabled)
        {
            _logger.LogInformation("  [禁用] {Dn} 北森状态离职", newDn);
            if (!_sync.DryRun) _ad.DisableUser(newDn);
            result.UsersDisabled++;
        }
        else if (scenarioE)
        {
            _logger.LogWarning("  [保持原状] 场景 E 不自动改 userAccountControl，当前 disabled={D}", existing.IsDisabled);
        }
    }

    private void ReconcileLeavers(IReadOnlyList<BeisenEmployee> beisenEmps, SyncResult result)
    {
        var activeBeisen = new HashSet<string>(
            beisenEmps.Where(e => !e.IsLeaver && !string.IsNullOrEmpty(e.EmployeeId))
                      .Select(e => e.EmployeeId),
            StringComparer.OrdinalIgnoreCase);

        var managed = _ad.ListManagedUsersUnder(_adOpt.BaseUserOu);
        _logger.LogInformation("  {Base} 下纳管账号 {Count} 个，北森在职 {Active} 个", _adOpt.BaseUserOu, managed.Count, activeBeisen.Count);

        // 先收集拟处理名单，过熔断再动手
        var candidates = new List<AdUserSnapshot>();
        foreach (var m in managed)
        {
            if (string.IsNullOrEmpty(m.EmployeeId)) continue;
            if (activeBeisen.Contains(m.EmployeeId)) continue;
            var inLeaver = m.ParentOu.EndsWith(_adOpt.LeaverOu, StringComparison.OrdinalIgnoreCase);
            if (inLeaver && m.IsDisabled) continue;
            candidates.Add(m);
        }

        // 差集熔断：拟处理数超限 = 疑似数据异常，宁可不做等人工
        var limit = Math.Max(_sync.MaxLeaversPerRun, (int)Math.Ceiling(managed.Count * _sync.MaxLeaverRatio));
        if (candidates.Count > limit)
        {
            result.Failures++;
            result.Errors.Add($"差集熔断: 拟处理 {candidates.Count} > 上限 {limit}");
            result.AddEvent(SyncEventLevel.Critical, SyncEventCategory.CircuitBreaker, "reconcile", "离职差集",
                $"本轮拟禁用 {candidates.Count} 个账号，超过上限 {limit}，差集已中止，请核实北森数据是否正常");
            _logger.LogError(
                "⚠️ 差集熔断：本轮拟归档/禁用 {C} 个账号，超过上限 {L}（=max(MaxLeaversPerRun={A}, 纳管{M}×{R:P0})）——差集中止。请人工核实北森数据/权限正常后再跑",
                candidates.Count, limit, _sync.MaxLeaversPerRun, managed.Count, _sync.MaxLeaverRatio);
            candidates.Clear();
        }

        foreach (var m in candidates)
        {
            if (AdProtection.IsProtected(m, _adOpt, out var why))
            {
                result.ProtectedSkipped++;
                result.AddEvent(SyncEventLevel.Info, SyncEventCategory.Protected, m.EmployeeId ?? m.Dn,
                    m.DisplayName ?? m.SamAccountName, $"{why}，北森已无此在职人员但不自动禁用，请人工回收", m.Dn);
                _logger.LogWarning("  [保护] 差集命中受保护账号 {Dn}（{Why}）→ 不动", m.Dn, why);
                continue;
            }
            var alreadyInLeaverOu = m.ParentOu.EndsWith(_adOpt.LeaverOu, StringComparison.OrdinalIgnoreCase);
            _logger.LogInformation("  [差集] {Sam}/{EmpId} 不在北森在职 → 移到 {LeaverOu} + 禁用",
                m.SamAccountName, m.EmployeeId, _adOpt.LeaverOu);
            if (!_sync.DryRun)
            {
                try
                {
                    var newDn = m.Dn;
                    if (!alreadyInLeaverOu)
                    {
                        if (_sync.DisableLeaverEvenIfMoveFails)
                        {
                            try { newDn = _ad.MoveUser(m.Dn, _adOpt.LeaverOu, m.EmployeeId); }
                            catch (Exception mex)
                            {
                                result.Failures++;
                                result.Errors.Add($"leaver move {m.Dn}: {mex.Message}");
                                result.AddEvent(SyncEventLevel.Warning, SyncEventCategory.Failure, "move:" + m.EmployeeId,
                                    m.DisplayName ?? m.SamAccountName, $"移到离职 OU 失败，已在原位置禁用：{mex.Message}", m.Dn);
                                _logger.LogError(mex, "差集移动失败，改为原位置禁用: {Dn}", m.Dn);
                            }
                        }
                        else
                        {
                            newDn = _ad.MoveUser(m.Dn, _adOpt.LeaverOu, m.EmployeeId);
                        }
                    }
                    if (!m.IsDisabled) _ad.DisableUser(newDn);
                    result.LeaversReconciled++;
                }
                catch (Exception ex)
                {
                    result.Failures++;
                    result.Errors.Add($"leaver {m.Dn}: {ex.Message}");
                    result.AddEvent(SyncEventLevel.Warning, SyncEventCategory.Failure, "leaver:" + m.EmployeeId,
                        m.DisplayName ?? m.SamAccountName, $"离职处理失败，账号可能仍处于启用状态：{ex.Message}", m.Dn);
                    _logger.LogError(ex, "差集处理失败: {Dn}", m.Dn);
                }
            }
            else
            {
                result.LeaversReconciled++;
            }
        }

        // 兜底规则：离职 OU 里的纳管账号必须处于禁用状态
        foreach (var u in _ad.ListManagedUsersUnder(_adOpt.LeaverOu))
        {
            if (u.IsDisabled) continue;
            _logger.LogInformation("  [离职OU兜底] {Sam}/{EmpId} 在离职 OU 但仍启用 → 禁用", u.SamAccountName, u.EmployeeId);
            if (!_sync.DryRun)
            {
                try { _ad.DisableUser(u.Dn); }
                catch (Exception ex)
                {
                    result.Failures++;
                    result.Errors.Add($"leaver-sweep {u.Dn}: {ex.Message}");
                    result.AddEvent(SyncEventLevel.Warning, SyncEventCategory.Failure, "sweep:" + u.Dn,
                        u.DisplayName ?? u.SamAccountName, $"离职 OU 中的账号禁用失败：{ex.Message}", u.Dn);
                    continue;
                }
            }
            result.LeaversReconciled++;
        }
    }

}
