using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;
using System.ComponentModel;
using KibanaMcp.Core;

namespace KibanaMcp;

[McpServerToolType]
public sealed class KibanaLogTools
{
    private const string EnvDescription = "Target environment.";
    private const string IndexDescription = "Elasticsearch index target. Pass only the raw index target, for example ubs-lottery-api*,-ubs-lottery-draw*, ubs-lottery-ticket-release*, or ubs-lottery-ticket-source*.";

    [McpServerTool(Name = "count_logs", ReadOnly = true), Description("Counts Elasticsearch log documents in the selected environment and index target. Use it when you need an exact read-only count before aggregating.")]
    public static async Task<CallToolResult> CountLogs(
        KibanaLogService service,
        [Description(EnvDescription)] string? env,
        [Description(IndexDescription)] string index,
        [Description("Time window, supporting a preset string, a preset object, or a custom gt/gte/lt/lte object.")] TimeRangeInput? timeRange = null,
        [Description("Elasticsearch query_string business filter expression.")] string? query = null,
        CancellationToken cancellationToken = default)
    {
        return ToCallToolResult(await service.CountLogsAsync(new CountLogsInput
        {
            Env = env,
            Index = index,
            TimeRange = timeRange,
            Query = query
        }, cancellationToken));
    }

    [McpServerTool(Name = "aggregate_logs", ReadOnly = true), Description("Runs guarded structured aggregations over PE Elasticsearch logs. Use it for one or more field groups, time buckets, per-day Top N, multi-metric summaries, slow-request ratio investigations, or other cases that still do not require raw Elasticsearch DSL. Prefer field-scoped queries to narrow the business scope; call discover_fields first when fields are uncertain.")]
    public static async Task<CallToolResult> AggregateLogs(
        KibanaLogService service,
        [Description(EnvDescription)] string? env,
        [Description(IndexDescription)] string index,
        [Description("Time window, supporting a preset string, a preset object, or a custom gt/gte/lt/lte object.")] TimeRangeInput? timeRange = null,
        [Description("Elasticsearch query_string business filter expression.")] string? query = null,
        [Description("Aggregation group array, supporting terms and date_histogram, up to 2 levels.")] List<AggregateGroupInput>? groups = null,
        [Description("Metric array; defaults to count.")] List<MetricInput>? metrics = null,
        CancellationToken cancellationToken = default)
    {
        return ToCallToolResult(await service.AggregateLogsAsync(new AggregateLogsInput
        {
            Env = env,
            Index = index,
            TimeRange = timeRange,
            Query = query,
            Groups = groups,
            Metrics = metrics
        }, cancellationToken));
    }

    [McpServerTool(Name = "search_index", ReadOnly = true), Description("Finds PE Elasticsearch log index families matching an index pattern in the selected environment. Lists live indices grouped by logical prefix (date segments removed) and annotates each family with a description from the bundled index catalog. Use it to pick an index target before querying other tools; use discover_fields for field-level detail.")]
    public static async Task<CallToolResult> SearchIndex(
        KibanaLogService service,
        [Description(EnvDescription)] string? env,
        [Description("Index pattern to explore, for example ubs-lottery-api*,ubs-lottery-draw*, ubs-lottery-ticket-release*, -ubs-lottery-ticket-source*. Defaults to ubs-lottery-*.")] string? pattern = null,
        [Description("Maximum number of index families to return, up to 2000.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        return ToCallToolResult(await service.SearchIndexAsync(new SearchIndexInput
        {
            Env = env,
            Pattern = pattern,
            Limit = limit
        }, cancellationToken));
    }

    [McpServerTool(Name = "time_series", ReadOnly = true), Description("Aggregates PE Elasticsearch logs into time buckets. Use it to find peaks, valleys, daily distributions, hourly patterns, or to narrow an incident window; it can also split by a field. For slow-request investigations, use a query such as milliseconds:>10000.")]
    public static async Task<CallToolResult> TimeSeries(
        KibanaLogService service,
        [Description(EnvDescription)] string? env,
        [Description(IndexDescription)] string index,
        [Description("Time window, supporting a preset string, a preset object, or a custom gt/gte/lt/lte object.")] TimeRangeInput? timeRange = null,
        [Description("Time bucket interval: 1m, 5m, 15m, 30m, 1h, or 1d.")] string? interval = null,
        [Description("Elasticsearch query_string business filter expression.")] string? query = null,
        [Description("Optional field split configuration.")] TimeSeriesSplitBy? splitBy = null,
        [Description("Metric configuration; defaults to count.")] MetricInput? metric = null,
        [Description("Whether to include empty time buckets; defaults to false.")] bool? includeEmptyBuckets = null,
        CancellationToken cancellationToken = default)
    {
        return ToCallToolResult(await service.TimeSeriesAsync(new TimeSeriesInput
        {
            Env = env,
            Index = index,
            TimeRange = timeRange,
            Interval = interval ?? string.Empty,
            Query = query,
            SplitBy = splitBy,
            Metric = metric,
            IncludeEmptyBuckets = includeEmptyBuckets
        }, cancellationToken));
    }

    [McpServerTool(Name = "compare_windows", ReadOnly = true), Description("Compares PE Elasticsearch log counts or metrics across two time windows. Use it to find abnormal increases, decreases, new request types, traffic distribution changes, or differences between today and yesterday.")]
    public static async Task<CallToolResult> CompareWindows(
        KibanaLogService service,
        [Description(EnvDescription)] string? env,
        [Description(IndexDescription)] string index,
        [Description("Current time window, supporting a preset string, a preset object, or a custom gt/gte/lt/lte object.")] TimeRangeInput? current = null,
        [Description("Baseline time window, supporting a preset string, a preset object, or a custom gt/gte/lt/lte object.")] TimeRangeInput? baseline = null,
        [Description("Elasticsearch query_string business filter expression.")] string? query = null,
        [Description("Optional grouping field; omit it to compare full-window metrics.")] string? groupBy = null,
        [Description("Number of groups to return, up to 1000.")] int? size = null,
        [Description("Metric configuration; defaults to count.")] MetricInput? metric = null,
        [Description("Minimum current/baseline metric value filter; defaults to 0.")] int? minCount = null,
        [Description("Whether to include keys missing from baseline but present in current; defaults to true.")] bool? includeMissingBaseline = null,
        CancellationToken cancellationToken = default)
    {
        return ToCallToolResult(await service.CompareWindowsAsync(new CompareWindowsInput
        {
            Env = env,
            Index = index,
            Current = current,
            Baseline = baseline,
            Query = query,
            GroupBy = groupBy,
            Size = size,
            Metric = metric,
            MinCount = minCount,
            IncludeMissingBaseline = includeMissingBaseline
        }, cancellationToken));
    }

    [McpServerTool(Name = "search_samples", ReadOnly = true), Description("Fetches a small number of matching PE Elasticsearch log samples and returns only selected source fields. Use it after aggregation to inspect concrete examples while avoiding full payload downloads.")]
    public static async Task<CallToolResult> SearchSamples(
        KibanaLogService service,
        [Description(EnvDescription)] string? env,
        [Description(IndexDescription)] string index,
        [Description("Time window, supporting a preset string, a preset object, or a custom gt/gte/lt/lte object.")] TimeRangeInput? timeRange = null,
        [Description("Elasticsearch query_string business filter expression.")] string? query = null,
        [Description("List of _source fields to return; when omitted, defaults to @timestamp, method, milliseconds, hostname, domainID, and details.")] List<string>? sourceFields = null,
        [Description("Number of samples, up to 100.")] int? size = null,
        [Description("Sort field list.")] List<SortInput>? sort = null,
        [Description("Whether to enable exact total hits.")] bool? trackTotalHits = null,
        [Description("Sort values from the previous page's last hit, used for search_after pagination.")] List<RawEsValue>? searchAfter = null,
        CancellationToken cancellationToken = default)
    {
        return ToCallToolResult(await service.SearchSamplesAsync(new SearchSamplesInput
        {
            Env = env,
            Index = index,
            TimeRange = timeRange,
            Query = query,
            SourceFields = sourceFields,
            Size = size,
            Sort = sort,
            TrackTotalHits = trackTotalHits,
            SearchAfter = searchAfter
        }, cancellationToken));
    }

    // export_raw_es_response is temporarily disabled by un-registering it: the tool attribute is
    // commented out, so the method below stays compilable and testable but is not exposed as an MCP tool.
    // [McpServerTool(Name = "export_raw_es_response", ReadOnly = true), Description("Runs a guarded read-only Elasticsearch search, count, or field_caps request against PE logs in the selected environment and exports the raw Elasticsearch JSON response to a location the caller can read directly. Use it only when dedicated tools cannot express the investigation or when the full raw response must be preserved for offline review.")]
    public static async Task<CallToolResult> ExportRawEsResponse(
        KibanaLogService service,
        [Description(EnvDescription)] string? env,
        [Description(IndexDescription)] string index,
        [Description("Read-only request type: search, count, or field_caps.")] string? method = null,
        [Description("Elasticsearch request body.")] Dictionary<string, RawEsValue>? body = null,
        [Description("Elasticsearch query string parameters.")] Dictionary<string, RawEsValue>? queryString = null,
        CancellationToken cancellationToken = default)
    {
        return ToCallToolResult(await service.ExportRawEsResponseAsync(new ExportRawEsResponseInput
        {
            Env = env,
            Method = method,
            Index = index,
            Body = body,
            QueryString = queryString
        }, cancellationToken));
    }

    [McpServerTool(Name = "discover_fields", ReadOnly = true), Description("Discovers available Elasticsearch fields and field capabilities in PE log indices. Use it when querying unfamiliar fields, fields with uncertain casing, or before aggregating on unknown fields.")]
    public static async Task<CallToolResult> DiscoverFields(
        KibanaLogService service,
        [Description(EnvDescription)] string? env,
        [Description(IndexDescription)] string index,
        [Description("Elasticsearch field_caps fields pattern; defaults to *.")] string? fieldPattern = null,
        [Description("List of field name prefixes to filter by.")] List<string>? prefixes = null,
        [Description("Return only aggregatable fields.")] bool? onlyAggregatable = null,
        [Description("Return only searchable fields.")] bool? onlySearchable = null,
        [Description("List of field types to filter by, such as keyword, long, or text.")] List<string>? types = null,
        [Description("Whether to include uppercase variant fields not confirmed in samples; defaults to false.")] bool? includeUnconfirmedFields = null,
        [Description("Maximum number of fields to return, up to 2000.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        return ToCallToolResult(await service.DiscoverFieldsAsync(new DiscoverFieldsInput
        {
            Env = env,
            Index = index,
            FieldPattern = fieldPattern,
            Prefixes = prefixes,
            OnlyAggregatable = onlyAggregatable,
            OnlySearchable = onlySearchable,
            Types = types,
            IncludeUnconfirmedFields = includeUnconfirmedFields,
            Limit = limit
        }, cancellationToken));
    }

    private static CallToolResult ToCallToolResult(ToolTextResponse response)
    {
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = response.Text }],
            IsError = response.IsError
        };
    }
}
