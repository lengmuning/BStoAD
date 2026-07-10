using Bstoad.ActiveDirectory;
using Bstoad.Beisen;
using Bstoad.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bstoad.Sync;

public interface IPreMatchService
{
    Task<PreMatchResult> RunAsync(bool apply, CancellationToken ct = default);
}

public sealed class PreMatchResult
{
    public int BeisenEmployees { get; set; }
    public int P1Backfilled { get; set; }
    public int P2Backfilled { get; set; }
    public int P1Skipped { get; set; }
    public int P2Skipped { get; set; }
    public int P2Ambiguous { get; set; }
    public int Failures { get; set; }
    public List<string> Errors { get; } = new();
}

public sealed class PreMatchService : IPreMatchService
{
    private readonly IBeisenTokenProvider _token;
    private readonly IBeisenEmployeeClient _empClient;
    private readonly IAdProvisioner _ad;
    private readonly ActiveDirectoryOptions _adOpt;
    private readonly ILogger<PreMatchService> _logger;

    public PreMatchService(
        IBeisenTokenProvider token,
        IBeisenEmployeeClient empClient,
        IAdProvisioner ad,
        IOptions<ActiveDirectoryOptions> adOpt,
        ILogger<PreMatchService> logger)
    {
        _token = token;
        _empClient = empClient;
        _ad = ad;
        _adOpt = adOpt.Value;
        _logger = logger;
    }

    public async Task<PreMatchResult> RunAsync(bool apply, CancellationToken ct = default)
    {
        var r = new PreMatchResult();
        _logger.LogInformation("================ PreMatch 开始 Apply={Apply} ================", apply);
        _logger.LogInformation("策略: P1 全域 sAMAccountName==工号 / P2 全域 mail==北森邮箱 → 只回填 employeeID，不打 marker");

        await _token.GetAccessTokenAsync(ct);

        var emps = await _empClient.SearchAllAsync(includeDeleted: false, ct);
        r.BeisenEmployees = emps.Count;
        _logger.LogInformation("北森员工 {Count} 条", emps.Count);

        foreach (var emp in emps)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(emp.EmployeeId)) continue;

            // 已 employeeID 命中 → 跳过
            var existing = _ad.FindUserByEmployeeId(emp.EmployeeId);
            if (existing != null) continue;

            // P1: SAM=工号 + employeeID 空
            var p1 = _ad.FindLegacyBySam(emp.EmployeeId);
            if (p1 != null)
            {
                if (AdProtection.IsProtected(p1, _adOpt, out var whyP1))
                {
                    _logger.LogWarning("  [P1 保护] {Sam} 命中受保护账号 {Dn}（{Why}）→ 跳过回填", emp.EmployeeId, p1.Dn, whyP1);
                    r.P1Skipped++;
                    continue;
                }
                _logger.LogInformation("  [P1] {Sam} 命中老账号 {Dn} → 回填 employeeID={EmpId}",
                    emp.EmployeeId, p1.Dn, emp.EmployeeId);
                if (apply)
                {
                    try { _ad.BackfillEmployeeId(p1.Dn, emp.EmployeeId); r.P1Backfilled++; }
                    catch (Exception ex) { r.Failures++; r.Errors.Add($"P1 {p1.Dn}: {ex.Message}"); }
                }
                else { r.P1Backfilled++; }
                continue;
            }

            // P2: mail 匹配
            if (string.IsNullOrWhiteSpace(emp.Email)) continue;
            var byMail = _ad.FindUsersByMail(emp.Email);
            var candidates = byMail.Where(u => string.IsNullOrEmpty(u.EmployeeId)).ToList();

            if (candidates.Count == 0)
            {
                r.P2Skipped++;
                continue;
            }
            if (candidates.Count > 1)
            {
                _logger.LogWarning("  [P2 歧义] mail={Mail} 在 AD 命中多个无 employeeID 账号 ({Count})，跳过避免误伤",
                    emp.Email, candidates.Count);
                r.P2Ambiguous++;
                continue;
            }

            var hit = candidates[0];
            if (AdProtection.IsProtected(hit, _adOpt, out var whyP2))
            {
                _logger.LogWarning("  [P2 保护] mail={Mail} 命中受保护账号 {Dn}（{Why}）→ 跳过回填", emp.Email, hit.Dn, whyP2);
                r.P2Skipped++;
                continue;
            }
            _logger.LogInformation("  [P2] mail={Mail} → {Dn} 回填 employeeID={EmpId}",
                emp.Email, hit.Dn, emp.EmployeeId);
            if (apply)
            {
                try { _ad.BackfillEmployeeId(hit.Dn, emp.EmployeeId); r.P2Backfilled++; }
                catch (Exception ex) { r.Failures++; r.Errors.Add($"P2 {hit.Dn}: {ex.Message}"); }
            }
            else { r.P2Backfilled++; }
        }

        _logger.LogInformation(
            "================ PreMatch 完成 · P1={P1} P2={P2} P2歧义={A} 失败={F} ================ (Apply={Apply})",
            r.P1Backfilled, r.P2Backfilled, r.P2Ambiguous, r.Failures, apply);
        return r;
    }
}
