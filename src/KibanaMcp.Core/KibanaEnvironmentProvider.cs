using Microsoft.Extensions.Configuration;

namespace KibanaMcp;

/// <summary>
/// Reads environment entries from the <c>Environments</c> configuration section and resolves the
/// current environment's connection settings for each tool call. The Kibana-only configuration model
/// has exactly one URL per environment: <c>KibanaBaseUrl</c> (with <c>UserName</c>/<c>Password</c>
/// used for HTTP Basic authentication).
/// </summary>
public sealed class KibanaEnvironmentProvider(IConfiguration configuration)
{
    public const string EnvironmentsSectionPath = "Environments";

    public IReadOnlyList<string> GetEnvironmentNames()
    {
        return configuration.GetSection(EnvironmentsSectionPath)
            .GetChildren()
            .Select(section => section.Key)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public EnvironmentConfig Resolve(string? env)
    {
        if (string.IsNullOrWhiteSpace(env))
        {
            throw new ToolException("ENV_REQUIRED", "env is required.");
        }

        var envName = env.Trim();
        var section = configuration.GetSection($"{EnvironmentsSectionPath}:{envName}");
        if (!section.Exists())
        {
            throw new ToolException("ENV_NOT_CONFIGURED", $"Environment '{envName}' is not configured.");
        }

        return new EnvironmentConfig(
            envName,
            section["KibanaBaseUrl"] ?? string.Empty,
            section["UserName"],
            section["Password"],
            configuration["DefaultTimeZone"] ?? "Asia/Shanghai",
            configuration.GetValue("RequestTimeoutMs", 120000),
            section["KibanaVersion"],
            section["ProxyApiVersion"],
            section["SessionCookie"]);
    }
}
