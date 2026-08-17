using Microsoft.Extensions.DependencyInjection;

namespace KibanaMcp;

public static class KibanaMcpServiceCollectionExtensions
{
    public static IServiceCollection AddKibanaMcpCore(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<KibanaEnvironmentProvider>();
        services.AddSingleton<KibanaRestClient>();
        services.AddSingleton<KibanaDataViewResolver>();
        services.AddSingleton<KibanaLogService>();
        services.AddKibanaMcpServerInstructions();
        return services;
    }
}
