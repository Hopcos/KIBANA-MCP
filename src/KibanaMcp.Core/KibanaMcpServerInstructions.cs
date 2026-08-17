using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace KibanaMcp;

public static class KibanaMcpServerInstructions
{
    public const string Text = """
This is the PE Elasticsearch logs MCP server for read-only log investigation. Every tool takes an environment, a raw index target, and a time range, and returns YAML.

## Environment selection
env is required and must be a configured environment name, not a free-form user phrase. Map the user's wording before calling: "production" or "prod" → prod; business-line terms jojo, jj, or ro → the corresponding prod-* environment (prod-jojo, prod-hr, prod-hu, prod-drcj); "stage" or "test" → stage. When the environment is ambiguous, choose the most likely one and state the assumption.

## Index selection
The index parameter takes only the raw Elasticsearch index target: no URL, endpoint, query string, or leading/trailing slash. Comma-separated wildcard include/exclude patterns are supported, for example ubs-lottery-api*,-ubs-lottery-draw*. For any other scenario, or to see exactly which index families exist in the selected environment with retention, doc counts, and per-family guidance, run search_index and pick from its annotated families. Do not broaden to ubs-lottery-* unless the user explicitly asks for a cross-index investigation. Do not guess the meaning of an unrecognized prefix; let search_index resolve it.

## query_string syntax
The query argument uses Elasticsearch query_string syntax; keep time conditions in the time-range parameters, never in query. Prefer field-scoped clauses such as method:PredictBonusRequest AND details:*InvalidOperationException*. Use uppercase AND/OR/NOT and parentheses for grouping, for example (method:ClaimBonus OR method:ConvertToBonus) AND domainID:2006. Quote or escape values containing spaces, colons, parentheses, slashes, or other query_string special characters.

## Connection path
All Elasticsearch access in this server goes through the Kibana console proxy REST API (/api/console/proxy). The server holds the Kibana URL and Basic credentials per environment; Elasticsearch is never contacted directly, so no 9200 access is required. This also means there is no separate Elasticsearch connection string to configure.

## Staged investigation
Investigate in stages and start with a narrow window (for example today or last_30_minutes) that still fits the question, widening only when needed. Use count_logs for an exact count; aggregate_logs for value rankings, grouped histograms, and multi-metric summaries; time_series to locate peaks and valleys; compare_windows to contrast two windows, such as today versus yesterday; search_samples to inspect a few concrete records with limited _source; and discover_fields before guessing fields. Aggregations can return many rows, so narrow the time range and fields first.
""";

    public static IServiceCollection AddKibanaMcpServerInstructions(this IServiceCollection services)
    {
        services.Configure<McpServerOptions>(options => options.ServerInstructions = Text);
        return services;
    }
}
