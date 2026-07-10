using Bstoad.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bstoad.Sync;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSyncOrchestration(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SyncOptions>(config.GetSection(SyncOptions.SectionName));
        services.AddSingleton<ISyncOrchestrator, SyncOrchestrator>();
        services.AddSingleton<IPreMatchService, PreMatchService>();
        services.AddSingleton<IMarkerMigrationService, MarkerMigrationService>();
        return services;
    }
}
