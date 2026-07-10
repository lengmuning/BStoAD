using Bstoad.ActiveDirectory;
using Bstoad.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bstoad.Sync;

public interface IMarkerMigrationService
{
    MigrationResult Run(bool apply);
}

public sealed class MigrationResult
{
    public int Scanned { get; set; }
    public int AlreadyMarked { get; set; }
    public int Marked { get; set; }
    public int SkippedDisabled { get; set; }
    public int SkippedNoEmployeeId { get; set; }
    public int Failures { get; set; }
    public List<string> Errors { get; } = new();
}

public sealed class MarkerMigrationService : IMarkerMigrationService
{
    private readonly IAdProvisioner _ad;
    private readonly ActiveDirectoryOptions _adOpt;
    private readonly ILogger<MarkerMigrationService> _logger;

    public MarkerMigrationService(
        IAdProvisioner ad,
        IOptions<ActiveDirectoryOptions> adOpt,
        ILogger<MarkerMigrationService> logger)
    {
        _ad = ad;
        _adOpt = adOpt.Value;
        _logger = logger;
    }

    public MigrationResult Run(bool apply)
    {
        var r = new MigrationResult();
        _logger.LogInformation("================ MarkerMigration 开始 Apply={Apply} ================", apply);
        _logger.LogInformation("策略: 扫 {Base} 下所有 employeeID 非空 + 启用 的账号补打 {Attr}={Value}",
            _adOpt.BaseUserOu, _adOpt.ManagedMarkerAttribute, _adOpt.ManagedMarkerValue);

        var users = _ad.ListAllUsersUnder(_adOpt.BaseUserOu);
        r.Scanned = users.Count;
        _logger.LogInformation("{Base} 下共 {Count} 个 user 对象", _adOpt.BaseUserOu, users.Count);

        foreach (var u in users)
        {
            if (string.IsNullOrEmpty(u.EmployeeId)) { r.SkippedNoEmployeeId++; continue; }
            if (u.IsDisabled) { r.SkippedDisabled++; continue; }
            if (u.HasManagedMarker(_adOpt.ManagedMarkerValue)) { r.AlreadyMarked++; continue; }

            _logger.LogInformation("  [补 marker] {Sam}/{EmpId} → {Dn}", u.SamAccountName, u.EmployeeId, u.Dn);
            if (apply)
            {
                try { _ad.EnsureManagedMarker(u.Dn); r.Marked++; }
                catch (Exception ex) { r.Failures++; r.Errors.Add($"{u.Dn}: {ex.Message}"); }
            }
            else { r.Marked++; }
        }

        _logger.LogInformation(
            "================ MarkerMigration 完成 · 扫={S} 已有={Am} 补={M} 跳过禁用={Sd} 跳过无EmpId={Sn} 失败={F} ================ (Apply={Apply})",
            r.Scanned, r.AlreadyMarked, r.Marked, r.SkippedDisabled, r.SkippedNoEmployeeId, r.Failures, apply);
        return r;
    }
}
