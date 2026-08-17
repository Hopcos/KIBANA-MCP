using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KibanaMcp.Core;

public sealed record ToolTextResponse(string Text, bool IsError)
{
    public static implicit operator string(ToolTextResponse response) => response.Text;
}

public static class YamlResponse
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .DisableAliases()
        .Build();

    public static ToolTextResponse Success<T>(EnvironmentConfig config, T data, ResolvedTimeRange? timeWindow = null, object? limits = null, IReadOnlyList<string>? reviewLinks = null)
    {
        Dictionary<string, object?> response = new();

        if (timeWindow is not null)
        {
            response["timeWindow"] = timeWindow;
        }

        response["data"] = data;
        if (reviewLinks is { Count: > 0 })
        {
            response["reviewLinks"] = reviewLinks;
        }

        if (limits is not null)
        {
            response["limits"] = limits;
        }

        return new ToolTextResponse(Serializer.Serialize(response), false);
    }

    public static ToolTextResponse ComparisonSuccess<T>(EnvironmentConfig config, T data, ResolvedTimeRange current, ResolvedTimeRange baseline, object? limits = null)
    {
        Dictionary<string, object?> response = new()
        {
            ["comparisonTimeWindow"] = new Dictionary<string, object?>
            {
                ["current"] = current,
                ["baseline"] = baseline
            },
            ["data"] = data
        };

        if (limits is not null)
        {
            response["limits"] = limits;
        }

        return new ToolTextResponse(Serializer.Serialize(response), false);
    }

    public static ToolTextResponse ExportSuccess(EnvironmentConfig config, object data)
    {
        var response = Serializer.Serialize(new Dictionary<string, object?>
        {
            ["data"] = data
        });
        return new ToolTextResponse(response, false);
    }

    public static ToolTextResponse Error(ToolException exception, string? env = null, object? limits = null, IReadOnlyList<string>? reviewLinks = null)
    {
        Dictionary<string, object?> response = new();

        response["error"] = new Dictionary<string, object?>
        {
            ["code"] = exception.Code,
            ["message"] = exception.Message,
            ["retriable"] = exception.Retriable,
            ["details"] = exception.Details
        };

        if (reviewLinks is { Count: > 0 })
        {
            response["reviewLinks"] = reviewLinks;
        }

        if (limits is not null)
        {
            response["limits"] = limits;
        }

        return new ToolTextResponse(Serializer.Serialize(response), true);
    }

    public static ToolTextResponse Unexpected(Exception exception, string? env = null, IReadOnlyList<string>? reviewLinks = null)
    {
        return Error(new ToolException("ELASTICSEARCH_ERROR", exception.Message), env, reviewLinks: reviewLinks);
    }
}
