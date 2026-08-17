using System.Globalization;
using System.Text.RegularExpressions;

namespace KibanaMcp;

/// <summary>
/// Builds Kibana Discover deep links from resolved time windows, index targets, and query_string filters.
/// For a Kibana-only server this is the review mechanism of choice: every response carries a URL the
/// caller can open to see the exact result in Discover. Links are emitted whenever the environment's
/// KibanaBaseUrl is configured (which, on a Kibana-only setup, is always) and the data view is known.
/// The Discover state uses Lucene so the tool's query_string expression can pass through unchanged.
/// </summary>
public static class KibanaReviews
{
    public static IReadOnlyList<string> Build(
        EnvironmentConfig config,
        string? dataView,
        string? query,
        params ResolvedTimeRange[] windows)
    {
        string? baseUrl = NormalizeBaseUrl(config.KibanaBaseUrl);
        if (baseUrl is null || dataView is null || windows.Length == 0)
        {
            return [];
        }

        List<string> links = [];
        foreach (ResolvedTimeRange window in windows)
        {
            string? url = BuildDiscover(baseUrl, dataView, query, window);
            if (url is not null)
            {
                links.Add(url);
            }
        }

        return links;
    }

    private static string? BuildDiscover(string baseUrl, string dataView, string? query, ResolvedTimeRange window)
        => BuildDiscover(baseUrl, dataView, query, FirstNonEmpty(window.Gt, window.Gte), FirstNonEmpty(window.Lte, window.Lt));

    internal static string? BuildDiscover(string baseUrl, string dataView, string? query, string? from, string? to)
    {
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
        {
            return null;
        }

        // A bare "*" Lucene query translates to query_string { "*" } which, on indices that narrow
        // index.query.default_field, matches nothing in Kibana Discover. Match-all queries (null,
        // whitespace, "*", "*:*") must be represented by an empty query box so Kibana sends match_all.
        string luceneQuery = IsMatchAllQuery(query) ? string.Empty : query!;

        string globalState = $"(filters:!(),refreshInterval:(pause:!t,value:0),time:(from:'{ToUtcZ(from)}',to:'{ToUtcZ(to)}'))";
        string appState = $"(columns:!(),filters:!(),index:'{EscapeState(dataView)}',interval:auto,query:(language:lucene,query:'{EscapeState(luceneQuery)}'),sort:!(!('@timestamp',desc)))";
        return $"{baseUrl}/app/discover#/?_g={globalState}&_a={appState}";
    }

    /// <summary>
    /// Builds a single unlabeled Discover link for a resolved window, used when the link is nested
    /// inside the window object it describes (for example data.current.reviewLink).
    /// </summary>
    public static string? BuildWindowLink(EnvironmentConfig config, string? dataView, string? query, ResolvedTimeRange window)
    {
        string? baseUrl = NormalizeBaseUrl(config.KibanaBaseUrl);
        if (baseUrl is null || dataView is null)
        {
            return null;
        }

        return BuildDiscover(baseUrl, dataView, query, window);
    }

    /// <summary>
    /// Builds a single Discover deep link for an explicit window, used for per-bucket time_series links.
    /// from and to are ISO boundaries; the window is inclusive of from and exclusive of to.
    /// </summary>
    public static string? BuildDiscoverUrl(EnvironmentConfig config, string? dataView, string? query, string from, string to)
    {
        string? baseUrl = NormalizeBaseUrl(config.KibanaBaseUrl);
        if (baseUrl is null || dataView is null)
        {
            return null;
        }

        return BuildDiscover(baseUrl, dataView, query, from, to);
    }

    /// <summary>
    /// Builds a Discover deep link that isolates one aggregate group bucket. A terms key filters the
    /// link query to field:"value" (the Kibana "filter for value" behavior), and a date_histogram key
    /// narrows the link's time picker to the bucket window instead of the overall window.
    /// </summary>
    public static string? BuildGroupKeyLink(
        EnvironmentConfig config,
        string? dataView,
        string? query,
        ResolvedTimeRange window,
        IReadOnlyDictionary<string, object?> key)
    {
        string? baseUrl = NormalizeBaseUrl(config.KibanaBaseUrl);
        if (baseUrl is null || dataView is null)
        {
            return null;
        }

        string type = key.TryGetValue("type", out object? typeValue) ? typeValue?.ToString() ?? string.Empty : string.Empty;
        if (type == "date_histogram")
        {
            string? start = key.TryGetValue("bucketStart", out object? startValue) ? startValue?.ToString() : null;
            string? end = key.TryGetValue("bucketEnd", out object? endValue) ? endValue?.ToString() : null;
            return BuildDiscover(baseUrl, dataView, query, start, end);
        }

        if (type != "terms")
        {
            return null;
        }

        string field = key.TryGetValue("field", out object? fieldValue) ? fieldValue?.ToString() ?? string.Empty : string.Empty;
        string? value = key.TryGetValue("key", out object? keyValue) ? keyValue?.ToString() : null;
        if (string.IsNullOrEmpty(field) || string.IsNullOrEmpty(value))
        {
            return null;
        }

        string fieldQuery = $"{field}:{EscapeLuceneTerm(value)}";
        return BuildDiscover(baseUrl, dataView, fieldQuery, FirstNonEmpty(window.Gt, window.Gte), FirstNonEmpty(window.Lte, window.Lt));
    }

    /// <summary>
    /// Builds a Discover context-view deep link anchored on one document. Context view shows the
    /// documents immediately around a given _id in the data view, with the requested columns.
    /// </summary>
    public static string? BuildContextLink(EnvironmentConfig config, string? dataView, string documentId, IReadOnlyList<string> columns)
    {
        string? baseUrl = NormalizeBaseUrl(config.KibanaBaseUrl);
        if (baseUrl is null || dataView is null || string.IsNullOrWhiteSpace(documentId))
        {
            return null;
        }

        string encodedColumns = columns.Count == 0
            ? "!()"
            : "!(" + string.Join(",", columns.Select(c => $"'{EscapeState(c)}'")) + ")";
        string appState = $"(columns:{encodedColumns},filters:!(),predecessorCount:2,successorCount:2)";
        return $"{baseUrl}/app/discover#/context/{dataView}/{Uri.EscapeDataString(documentId)}?_a={appState}";
    }

    /// <summary>True when the query expresses "match everything" (null, blank, "*" or "*:*").</summary>
    internal static bool IsMatchAllQuery(string? query)
    {
        string trimmed = query?.Trim() ?? string.Empty;
        return trimmed.Length == 0 || trimmed == "*" || trimmed == "*:*";
    }

    /// <summary>
    /// Builds Discover deep links for an index pattern the way the management app does: the pattern is
    /// passed as the data-view reference so Kibana loads/creates a matching view by title. Unlike the
    /// saved-view links this never reads the <c>.kibana*</c> system indices (which the executing user
    /// often cannot), so it works whenever the environment has a reachable KibanaBaseUrl — including
    /// for tools like <c>search_index</c> that have no data-view-resolving path. The time picker uses
    /// Kibana's relative <c>now-7d</c>/<c>now</c> window so the link shows recent data on open.
    /// </summary>
    internal static IReadOnlyList<string> BuildIndexPatternLinks(EnvironmentConfig config, string indexPattern)
    {
        string? baseUrl = NormalizeBaseUrl(config.KibanaBaseUrl);
        if (baseUrl is null)
        {
            return [];
        }

        // One link per comma-separated include pattern; exclude (leading '-') patterns are advisory
        // in Discover, so they do not get their own link.
        List<string> links = [];
        foreach (string part in indexPattern.Split(','))
        {
            string pattern = part.Trim();
            if (pattern.Length == 0 || pattern.StartsWith('-'))
            {
                continue;
            }

            string? url = BuildDiscover(baseUrl, pattern, string.Empty, "now-7d", "now");
            if (url is not null)
            {
                links.Add(url);
            }
        }

        return links;
    }

    /// <summary>True when a terms value needs no quoting to be a safe Lucene term.</summary>
    private static readonly Regex SafeUnquotedTerm = new("^[A-Za-z0-9_.@-]+$", RegexOptions.Compiled);

    /// <summary>Formats a group-key value as a Lucene term, quoting values that would otherwise be parsed as syntax.</summary>
    private static string EscapeLuceneTerm(string value)
    {
        if (SafeUnquotedTerm.IsMatch(value))
        {
            return value;
        }

        return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    private static string? NormalizeBaseUrl(string? kibanaBaseUrl)
    {
        string? trimmed = kibanaBaseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed == "***")
        {
            return null;
        }

        return trimmed.TrimEnd('/');
    }

    /// <summary>Extracts the first include pattern from an ES index target, e.g. ubs-lottery-api*,ubs-lottery-draw*.</summary>
    internal static string? FirstIncludePattern(string indexTarget)
    {
        foreach (string part in indexTarget.Split(','))
        {
            string trimmed = part.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('-'))
            {
                continue;
            }

            return trimmed;
        }

        return null;
    }

    /// <summary>Converts a bound ISO boundary to UTC Z so no timezone offset characters appear in the URL.</summary>
    private static string ToUtcZ(string boundary)
    {
        if (DateTimeOffset.TryParse(boundary, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
        {
            return parsed.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        return boundary.Replace("+", "%2B");
    }

    /// <summary>Escapes a value for a Kibana rison-style single-quoted state string.</summary>
    private static string EscapeState(string value)
    {
        return value.Replace("\\", "\\\\").Replace("'", "\\'");
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
