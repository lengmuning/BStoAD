using Bstoad.Core.Models;
using Bstoad.Core.Options;
using Bstoad.Sync;
using Bstoad.Worker.Alerts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bstoad.Worker;

public sealed class SyncWorker : BackgroundService
{
    private readonly CommandContext _ctx;
    private readonly ISyncOrchestrator _orchestrator;
    private readonly IPreMatchService _preMatch;
    private readonly IMarkerMigrationService _markerMigration;
    private readonly IAlertService _alerts;
    private readonly SyncOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<SyncWorker> _logger;

    public SyncWorker(
        CommandContext ctx,
        ISyncOrchestrator orchestrator,
        IPreMatchService preMatch,
        IMarkerMigrationService markerMigration,
        IAlertService alerts,
        IOptions<SyncOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<SyncWorker> logger)
    {
        _ctx = ctx;
        _orchestrator = orchestrator;
        _preMatch = preMatch;
        _markerMigration = markerMigration;
        _alerts = alerts;
        _options = options.Value;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            switch (_ctx.Command)
            {
                case CommandKind.MatchAd:
                    var pm = await _preMatch.RunAsync(_ctx.Apply, stoppingToken);
                    if (pm.Failures > 0) ExitState.Code = 2;
                    _lifetime.StopApplication();
                    return;

                case CommandKind.MigrateMarker:
                    var mg = _markerMigration.Run(_ctx.Apply);
                    if (mg.Failures > 0) ExitState.Code = 2;
                    _lifetime.StopApplication();
                    return;

                case CommandKind.Sync:
                default:
                    await RunSyncLoopAsync(stoppingToken);
                    return;
            }
        }
        catch (Exception ex)
        {
            ExitState.Code = 1;
            _logger.LogError(ex, "命令执行失败: {Cmd}", _ctx.Command);
            _lifetime.StopApplication();
        }
    }

    private async Task RunSyncLoopAsync(CancellationToken stoppingToken)
    {
        await RunOnceWithAlertAsync(stoppingToken, setExitCode: true);

        if (_options.RunOnceAndExit)
        {
            _logger.LogInformation("RunOnceAndExit=true，退出");
            _lifetime.StopApplication();
            return;
        }

        var interval = TimeSpan.FromMinutes(_options.IncrementalIntervalMinutes);
        _logger.LogInformation("进入定时模式，每 {Min} 分钟运行一次", _options.IncrementalIntervalMinutes);
        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            await RunOnceWithAlertAsync(stoppingToken, setExitCode: false);
    }

    private async Task RunOnceWithAlertAsync(CancellationToken ct, bool setExitCode)
    {
        SyncResult? result = null;
        Exception? fatal = null;
        try
        {
            result = await _orchestrator.RunOnceAsync(ct);
            if (setExitCode && (result.Failures > 0 || result.Errors.Count > 0)) ExitState.Code = 2;
        }
        catch (Exception ex)
        {
            fatal = ex;
            if (setExitCode) ExitState.Code = 1;
            _logger.LogError(ex, "同步执行失败");
        }

        // 告警是旁路：内部吞掉所有异常，不改变退出码
        await _alerts.NotifyAsync(result, fatal, ct);
    }
}
