using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("KibanaMcp.Core.Tests")]

namespace KibanaMcp;

/// <summary>
/// Lightweight Elasticsearch client that reaches the cluster exclusively through the Kibana console
/// proxy REST API (<c>/api/console/proxy</c>). This is required for clusters whose 9200 port is not
/// reachable but whose Kibana 443 is. The proxy accepts only POST and forwards the verb selected by
/// the <c>method</c> query parameter.
///
/// Performance: one pooled <see cref="HttpClient"/> is created per Kibana host and reused across every
/// tool call and request thread (environments are few, so a lazily-filled concurrent dictionary bounded
/// by the number of configured environments is the right scale). TCP connections and TLS session data
/// are therefore reused under concurrent load instead of being opened per request. The per-environment
/// client also closes over the host name so the private-CA certificate relaxation is scoped to hosts
/// matching the configured insecure-host pattern rather than applied globally.
///
/// Compatibility: deployed Kibana gateways vary on two proxy parameters, so both are configurable per
/// environment and default to values proven against the target production gateway: the version reported
/// in the <c>kbn-version</c> header (Kibana answers HTTP 400 "Browser client is out of date" when it
/// does not match the running build) and whether the proxy API accepts an <c>apiVersion</c> query
/// parameter (HTTP 400 "definition for this key is missing" when it rejects one).
/// </summary>
public sealed class KibanaRestClient
{
    private readonly Regex? _insecureHostPattern;

    // Proven against the production gateway: the version matched the running Kibana build and the
    // 7.17 console proxy rejected the apiVersion query parameter because no schema key exists for it.
    internal const string DefaultKibanaVersion = "7.17.28";
    internal const string? DefaultProxyApiVersion = null;
    internal const string KibanaVersionHeader = "kbn-version";
    internal const string AntiCsrfHeader = "kbn-xsrf";
    internal const string AntiCsrfValue = "true";

    private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpMessageHandler? _handler;

    public KibanaRestClient()
        : this((IConfiguration?)null)
    {
    }

    public KibanaRestClient(HttpMessageHandler handler)
        : this((IConfiguration?)null)
    {
        _handler = handler;
    }

    public KibanaRestClient(IConfiguration? configuration)
    {
        _insecureHostPattern = CompileInsecureHostPattern(configuration?["Kibana:TlsInsecureHostPattern"]);
    }

    public Task<ElasticResponse> PostAsync(EnvironmentConfig config, string path, object? body, CancellationToken cancellationToken)
        => SendAsync(config, HttpMethod.Post, path, body, cancellationToken);

    public Task<ElasticResponse> GetAsync(EnvironmentConfig config, string path, CancellationToken cancellationToken)
        => SendAsync(config, HttpMethod.Get, path, null, cancellationToken);

    private async Task<ElasticResponse> SendAsync(EnvironmentConfig config, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.KibanaBaseUrl))
        {
            throw new ToolException("ENV_NOT_CONFIGURED", $"{config.Env} Kibana base URL is not configured.");
        }

        if (string.IsNullOrEmpty(config.Username) || string.IsNullOrEmpty(config.Password))
        {
            throw new ToolException("MISSING_CREDENTIALS", $"Kibana credentials are missing for {config.Env}.");
        }

        HttpClient client = ResolveClient(config.KibanaBaseUrl);
        string esPath = NormalizePath(path);
        string url =
            $"{config.KibanaBaseUrl.TrimEnd('/')}/api/console/proxy"
            + $"?path={Uri.EscapeDataString(esPath)}"
            + $"&method={method.ToString().ToUpperInvariant()}"
            + (string.IsNullOrWhiteSpace(config.ProxyApiVersion)
                ? string.Empty
                : $"&apiVersion={Uri.EscapeDataString(config.ProxyApiVersion)}");

        using HttpRequestMessage request = new(HttpMethod.Post, url);
        request.Content = new StringContent(SerializeBody(body), Encoding.UTF8, "application/json");
        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{config.Username}:{config.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        request.Headers.TryAddWithoutValidation(AntiCsrfHeader, AntiCsrfValue);

        // Some deployments require the SSO session cookie (for example Authelia) to reach inside the
        // proxy beyond the Basic header — the gateway re-authenticates the session and not just the
        // Basic credentials. When configured, carry the cookie verbatim on every request.
        if (!string.IsNullOrWhiteSpace(config.SessionCookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", config.SessionCookie.Trim());
        }

        // Kibana validates browser-issued requests against the running build version; whenever the
        // environment pins one (or a default is configured), report it so the proxy does not answer
        // with 400 "Browser client is out of date".
        string kibanaVersion = string.IsNullOrWhiteSpace(config.KibanaVersion) ? DefaultKibanaVersion : config.KibanaVersion;
        request.Headers.TryAddWithoutValidation(KibanaVersionHeader, kibanaVersion);

        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string? contentType = response.Content.Headers.ContentType?.MediaType;
            if (IsSsoRedirect((int)response.StatusCode, response.Headers.Location))
            {
                throw SsoAuthRequired((int)response.StatusCode, response.Headers.Location, esPath, method, config);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw KibanaError((int)response.StatusCode, content, esPath, method, config);
            }

            if (LooksLikeSsoLoginPage(content))
            {
                throw SsoAuthRequired((int)response.StatusCode, null, esPath, method, config);
            }

            return new ElasticResponse((int)response.StatusCode, content, contentType);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ToolException("TIMEOUT", "Kibana proxy request timed out.", retriable: true);
        }
        catch (HttpRequestException ex)
        {
            throw new ToolException("KIBANA_UNREACHABLE", $"Kibana proxy ({config.Env}) request failed: {Compact(ex.Message)}", retriable: true);
        }
    }

    /// <summary>A 3xx with a Location header is the gateway answering that the request was not
    /// authenticated and must be replayed to the SSO portal (302 → Authelia, for example). With auto
    /// redirect disabled this is exactly what the wire returns, so classify it before any HTML parsing.</summary>
    private static bool IsSsoRedirect(int statusCode, Uri? location)
    {
        return statusCode is >= 300 and < 400 && location is not null;
    }

    /// <summary>
    /// Returns the pooled client for a host, creating it on first use. When a handler was injected
    /// for testing, a fresh client is built per call so no pooling happens in tests.
    /// </summary>
    private HttpClient ResolveClient(string kibanaBaseUrl)
    {
        if (_handler is not null)
        {
            return new HttpClient(_handler, disposeHandler: false);
        }

        return _clients.GetOrAdd(kibanaBaseUrl.TrimEnd('/'), CreateEnvironmentClient);
    }

    private HttpClient CreateEnvironmentClient(string baseUrl)
    {
        var allowInsecure = IsInsecureHost(baseUrl);
        var handler = new SocketsHttpHandler
        {
            // Do not follow the gateway's SSO redirects: an Authelia-protected proxy answers a
            // request with a 302 to the login portal, and automatic redirect following would silently
            // return the login HTML page as a 200 that the JSON parser cannot eat. The redirect itself
            // is the signal that the supplied credentials are not accepted at the gateway.
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 20,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            SslOptions = new SslClientAuthenticationOptions
            {
                // Certificates come from a private CA for hosts that match the configured insecure
                // pattern (see "Kibana:TlsInsecureHostPattern"). When the machine does not trust that
                // root CA, skip chain validation only for those hosts; every other host name is
                // validated strictly. Production recommendation: install the enterprise root CA and
                // remove this relaxation.
                RemoteCertificateValidationCallback = (_, _, _, errors) => errors == SslPolicyErrors.None || allowInsecure
            }
        };

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    internal bool IsInsecureHost(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return false;
        }

        string host;
        try
        {
            host = new Uri(baseUrl).Host;
        }
        catch (UriFormatException)
        {
            return false;
        }

        // A blank pattern (blank config key) or invalid pattern (config typo) yields a null regex,
        // i.e. strict TLS for every host rather than an accidental relaxation.
        if (_insecureHostPattern is null)
        {
            return false;
        }

        return _insecureHostPattern.IsMatch(host);
    }

    /// <summary>Builds the insecure-host regex from the configured pattern. A blank or whitespace
    /// value disables the relaxation entirely (strict TLS for every host); an invalid pattern is
    /// suppressed the same way so a config typo cannot accidentally disable certificate validation.</summary>
    private static Regex? CompileInsecureHostPattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string SerializeBody(object? body)
    {
        if (body is null)
        {
            return string.Empty;
        }

        if (body is JsonNode node)
        {
            return node.ToJsonString(JsonDefaults.Options);
        }

        return JsonSerializer.Serialize(body, JsonDefaults.Options);
    }

    private static ToolException KibanaError(int statusCode, string content, string esPath, HttpMethod method, EnvironmentConfig config)
    {
        if (LooksLikeSsoLoginPage(content))
        {
            return SsoAuthRequired(statusCode, null, esPath, method, config);
        }

        // The gateway must be reached through Kibana. When the page-level auth (for example Authelia
        // SSO) or a reverse proxy rejects the request, the response body is HTML, not the expected
        // JSON error or JSON ES error. Classify it so callers see why the request never reached ES.
        if (LooksLikeHtmlErrorPage(content))
        {
            return new ToolException(
                "KIBANA_GATEWAY_ERROR",
                $"Kibana gateway returned an HTML error page (HTTP {statusCode}) for {method} {esPath}; the request did not reach Elasticsearch. Check the Kibana URL and credentials for {EnvironmentHostSummary(config.KibanaBaseUrl)}.",
                retriable: true,
                new
                {
                    statusCode,
                    contentType = DetectLayoutContentType(content),
                    tail = TailHtml(content),
                    hint = "The gateway answered with HTML instead of an Elasticsearch JSON error. This usually means page-level auth (for example Authelia SSO) rejected the request, the Basic credentials are not accepted at the gateway, or the URL points at a walled-off page. Use a Basic-auth user the gateway accepts, and verify the Kibana base URL from a browser."
                });
        }

        if (TryParseElasticsearchError(content, out string? type, out string? reason, out string[]? rootCauses))
        {
            return new ToolException("ELASTICSEARCH_ERROR", $"Elasticsearch rejected the request for {method} {esPath}.", retriable: false, new
            {
                statusCode,
                type,
                reason,
                rootCause = rootCauses is { Length: > 0 } ? rootCauses : null
            });
        }

        return new ToolException("KIBANA_ERROR", $"Kibana proxy returned {statusCode} for {method} {esPath}.", retriable: false, new
        {
            statusCode
        });
    }

    /// <summary>Builds the error surfaced when the proxy replies with a redirect to the SSO portal or
    /// with the SSO login page itself. The request never reaches Elasticsearch; the cause is always
    /// gateway-side authentication, not the query.</summary>
    private static ToolException SsoAuthRequired(int statusCode, Uri? location, string esPath, HttpMethod method, EnvironmentConfig config)
    {
        return new ToolException(
            "AUTH_REQUIRED",
            $"Kibana gateway redirected the request for {method} {esPath} to its SSO login instead of Elasticsearch (HTTP {statusCode}). The configured credentials are not accepted at the gateway or the session has expired.",
            retriable: true,
            new
            {
                statusCode,
                redirectTo = location?.ToString() ?? "the SSO login page body",
                environmentHost = EnvironmentHostSummary(config.KibanaBaseUrl),
                hint = "The gateway sits behind a page-level SSO (for example Authelia) that validates credentials against its own user directory before any request reaches Kibana/Elasticsearch. Fixes: (1) use a Basic-auth user that the gateway's directory accepts, and renew its password if it has rotated; (2) if a session cookie is required, set Environments:<env>:SessionCookie so the proxy request carries it; (3) verify the Kibana base URL opens in a browser without a login wall."
            });
    }

    /// <summary>
    /// True when the body is the SSO login page (Authelia). Authelia pages carry a
    /// <c>&lt;base href="https://authelia.…/&gt;</c> and a login form with a "username"/"password" pair.
    /// Detected on both 2xx responses (a gateway that answers the login page with 200) and non-2xx
    /// responses that never reached Elasticsearch.
    /// </summary>
    private static bool LooksLikeSsoLoginPage(string content)
    {
        ReadOnlySpan<char> span = TrimLeadingWhitespace(content.AsSpan());
        // No HTML marker = not a page we could classify, bail early.
        if (!span.StartsWith("<!DOCTYPE html>", StringComparison.OrdinalIgnoreCase)
            && !span.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return content.Contains("authelia", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the body is an HTML error page/title or an HTML doctype marker at the start.
    /// <c>&lt;!DOCTYPE html&gt;</c> or <c>&lt;html</c> mark a page-level (non-API) response; remembering
    /// that Elasticsearch JSON error bodies are never valid XML, <c>&lt;</c> is otherwise treated as
    /// plain text and only matched after the response turns out not to be JSON.
    /// </summary>
    private static bool LooksLikeHtmlErrorPage(string content)
    {
        ReadOnlySpan<char> span = TrimLeadingWhitespace(content.AsSpan());
        if (span.StartsWith("<!DOCTYPE html>", StringComparison.OrdinalIgnoreCase)
            || span.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The response is not valid JSON (callers only consult this on error status codes) and carries
        // an explicit HTML content type, or the body is empty. Serializing an empty 2xx body is a
        // hardening case (JsonNode.Parse would throw "invalid start of a value"); users only hit this
        // through services that already guard for the legitimately-empty body.
        return span.Length == 0 || (ContainsHtmlLayoutMarker(span)
            && !span.StartsWith('<')
            && (span.Length < 4096 || content.Length < 4096));
    }

    /// <summary>HTML pages (the SSO/login layouts Interim or Authelia render) always carry a
    /// (head|title|link|script|style|body|html) element somewhere in the document.</summary>
    private static bool ContainsHtmlLayoutMarker(ReadOnlySpan<char> span) => span.Contains("<html", StringComparison.OrdinalIgnoreCase)
        || span.Contains("<head", StringComparison.OrdinalIgnoreCase)
        || span.Contains("<title>", StringComparison.OrdinalIgnoreCase)
        || span.Contains("<body", StringComparison.OrdinalIgnoreCase);

    private static ReadOnlySpan<char> TrimLeadingWhitespace(ReadOnlySpan<char> value)
    {
        int i = 0;
        while (i < value.Length && char.IsWhiteSpace(value[i]))
        {
            i++;
        }

        return value[i..];
    }

    private static string DetectLayoutContentType(string content)
    {
        string title = ExtractTitle(content);
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        return string.IsNullOrWhiteSpace(content)
            ? "empty"
            : FirstAsciiLine(content);
    }

    /// <summary>Extracts the document title of an HTML error page, if any, e.g. "Authelia" or "502 Bad Gateway".</summary>
    private static string ExtractTitle(string content)
    {
        const string startTag = "<title";
        int start = content.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return string.Empty;
        }

        start = content.IndexOf('>', start);
        if (start < 0)
        {
            return string.Empty;
        }

        int end = content.IndexOf("</title>", start, StringComparison.OrdinalIgnoreCase);
        return end < 0 ? string.Empty : Compact(content[(start + 1)..end].Trim());
    }

    private static string FirstAsciiLine(string content)
    {
        int newline = content.IndexOfAny('\r', '\n');
        string first = newline >= 0 ? content[..newline] : content;
        return Compact(StripTags(first).Trim());
    }

    private static string StripTags(string value)
    {
        var withoutTags = Regex.Replace(value, "<[^>]*>", string.Empty);
        var collapsed = Regex.Replace(withoutTags, "\\s{2,}", " ");
        return collapsed;
    }

    private static string TailHtml(string content)
    {
        string stripped = StripTags(content);
        return stripped.Length <= 140 ? stripped : stripped[..140];
    }

    private static bool TryParseElasticsearchError(string content, out string? type, out string? reason, out string[]? rootCauses)
    {
        type = null;
        reason = null;
        rootCauses = null;
        try
        {
            JsonObject root = JsonNode.Parse(content)!.AsObject();
            JsonObject? error = root["error"] as JsonObject;
            type = error?["type"]?.GetValue<string>();
            reason = error?["reason"]?.GetValue<string>();

            // The root cause of a multi-shard failure is nested under error.root_cause[], each entry a
            // { type, reason }; the top-level "reason" only says "all shards failed". Surface the actual
            // per-shard causes so callers can distinguish a text-field aggregation rejection from any
            // other per-shard error.
            if (error?["root_cause"] is JsonArray rootCauseArray)
            {
                List<string> causes = [];
                foreach (JsonNode? entry in rootCauseArray)
                {
                    if (entry?["reason"] is JsonValue reasonValue && reasonValue.GetValue<string>() is { Length: > 0 } causeReason)
                    {
                        causes.Add(causeReason);
                    }
                }

                if (causes.Count > 0)
                {
                    rootCauses = causes.Distinct(StringComparer.Ordinal).ToArray();
                }
            }

            return error is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string EnvironmentHostSummary(string kibanaBaseUrl)
    {
        try
        {
            return new Uri(kibanaBaseUrl).Host;
        }
        catch (UriFormatException)
        {
            return kibanaBaseUrl;
        }
    }

    private static string NormalizePath(string path)
    {
        var trimmed = path.Trim();
        return trimmed.StartsWith('/') ? trimmed[1..] : trimmed;
    }

    private static string Compact(string message) => message.Length <= 300 ? message : message[..300];
}
