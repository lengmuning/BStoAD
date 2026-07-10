using Bstoad.Core.Options;
using Bstoad.Sync;
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
    private readonly SyncOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<SyncWorker> _logger;

    public SyncWorker(
        CommandContext ctx,
        ISyncOrchestrator orchestrator,
        IPreMatchService preMatch,
        IMarkerMigrationService markerMigration,
        IOptions<SyncOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<SyncWorker> logger)
    {
        _ctx = ctx;
        _orchestrator = orchestrator;
        _preMatch = preMatch;
        _markerMigration = markerMigration;
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
        try
        {
            var r = await _orchestrator.RunOnceAsync(stoppingToken);
            if (r.Failures > 0 || r.Errors.Count > 0) ExitState.Code = 2;
        }
        catch (Exception ex)
        {
            ExitState.Code = 1;
            _logger.LogError(ex, "同步执行失败");
        }

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
        {
            try { await _orchestrator.RunOnceAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "增量同步失败"); }
        }
    }
}
