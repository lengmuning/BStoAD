using Bstoad.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bstoad.ActiveDirectory;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddActiveDirectory(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ActiveDirectoryOptions>(config.GetSection(ActiveDirectoryOptions.SectionName));
        services.AddSingleton<ILdapConnectionFactory, LdapConnectionFactory>();
        services.AddSingleton<IAdProvisioner, AdProvisioner>();
        return services;
    }
}
