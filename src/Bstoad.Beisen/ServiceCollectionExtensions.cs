using Bstoad.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bstoad.Beisen;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBeisenClients(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<BeisenOptions>(config.GetSection(BeisenOptions.SectionName));

        services.AddHttpClient(nameof(BeisenTokenProvider));
        services.AddHttpClient(nameof(BeisenOrgClient));
        services.AddHttpClient(nameof(BeisenEmployeeClient));

        services.AddSingleton<IBeisenTokenProvider, BeisenTokenProvider>();
        services.AddSingleton<IBeisenOrgClient, BeisenOrgClient>();
        services.AddSingleton<IBeisenEmployeeClient, BeisenEmployeeClient>();
        return services;
    }
}
