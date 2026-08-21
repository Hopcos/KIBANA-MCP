using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Moq;
using Moq.Protected;
using Xunit;

namespace KibanaMcp.Tests;

/// <summary>
/// Verifies that the Kibana console-proxy client surfaces non-JSON / HTML / empty gateway bodies with
/// an actionable classification instead of a raw JSON parser error, and that successful responses
/// parse normally.
/// </summary>
public class KibanaRestClientTests
{
    private const string KibanaUrl = "https://kibana.local";
    private const string IconToolTip = "&lt;svg xmlns=&quot;http://www.w3.org/2000/svg&quot; width=&quot;16&quot; height=&quot;16&quot;&gt;&lt;text y=&quot;13&quot; font-size=&quot;13&quot; font-family=&quot;sans-serif&quot; fill=&quot;#808080&quot;&gt;i&lt;/text&gt;&lt;/svg&gt;";

    private static readonly EnvironmentConfig Prod = new(
        Env: "prod",
        KibanaBaseUrl: KibanaUrl,
        Username: "filebeat_writer",
        Password: "secret",
        DefaultTimeZone: "Asia/Shanghai",
        RequestTimeoutMs: 120000);

    private static KibanaRestClient CreateClient(Func<HttpResponseMessage> responder)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(responder);
        return new KibanaRestClient(handler.Object);
    }

    private static KibanaRestClient CreateClient(string? insecureHostPattern)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kibana:TlsInsecureHostPattern"] = insecureHostPattern
            })
            .Build();
        return new KibanaRestClient(configuration);
    }

    private static StringContent Html(string body) => new(body, Encoding.UTF8, "text/html");

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<ToolException> PostErrorAsync(HttpStatusCode status, StringContent content)
    {
        KibanaRestClient client = CreateClient(() => new HttpResponseMessage(status) { Content = content });
        try
        {
            await client.PostAsync(Prod, "ubs-lottery-draw*/_count", new Dictionary<string, object?>(), CancellationToken.None);
        }
        catch (ToolException ex)
        {
            return ex;
        }

        throw new UnreachableException();
    }

    [Fact]
    public async Task HtmlErrorPage_IsReportedAsGatewayError()
    {
        // The "never-intercepts-widgets" HTML Intercept/core: middleware reply carries <html>/<head>/<body>
        // but no document title (no Authelia page).
        ToolException ex = await PostErrorAsync(HttpStatusCode.Unauthorized, Html(
            "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><script>window.i18n=\"xx\"</script></head><body marginheight=\"0\"></body></html>"));

        Assert.Equal("KIBANA_GATEWAY_ERROR", ex.Code);
        Assert.True(ex.Retriable);
        Assert.Contains("HTML", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutheliaLoginPage_IsReportedAsAuthRequired()
    {
        // The gateway answered with the Authelia login page itself (here on a 2xx status): the page
        // carries a <base href> on the authelia host and is detected as an SSO page. This is the exact
        // shape seen in production — 200 + HTML login page instead of the ES JSON response.
        ToolException ex = await PostErrorAsync(HttpStatusCode.OK, Html(
            "<!doctype html><html><head><base href=\"https://authelia.example.local/\"></head><body><title>Authelia</title>...</body></html>"));
        Assert.Equal("AUTH_REQUIRED", ex.Code);
        Assert.Contains("authelia", JsonSerializer.Serialize(ex.Details), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SsoRedirect_IsReportedAsAuthRequired()
    {
        // A 302 to the SSO portal is the clearest signal that the Basic credentials did not pass the
        // gateway. With auto-redirect disabled this is exactly what the transport returns.
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Content = new StringContent("", Encoding.UTF8, "text/html")
                };
                response.Headers.Location = new Uri("https://authelia.example.local/?rd=https%3A%2F%2Fkibana...%2Fapi%2Fconsole%2Fproxy%2...");
                return response;
            });
        var client = new KibanaRestClient(handler.Object);

        try
        {
            await client.PostAsync(Prod, "ubs-lottery-draw*/_count", new Dictionary<string, object?>(), CancellationToken.None);
        }
        catch (ToolException ex)
        {
            Assert.Equal("AUTH_REQUIRED", ex.Code);
            Assert.True(ex.Retriable);
            Assert.Contains("redirected", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("authelia", JsonSerializer.Serialize(ex.Details), StringComparison.OrdinalIgnoreCase);
            return;
        }

        throw new UnreachableException();
    }

    [Fact]
    public async Task SessionCookie_IsSentAsCookieHeader()
    {
        EnvironmentConfig config = Prod with { SessionCookie = "authelia_session=<token>" };
        var handler = new Mock<HttpMessageHandler>();
        string capturedCookie = string.Empty;
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback((HttpRequestMessage request, CancellationToken _) =>
            {
                capturedCookie = request.Headers.TryGetValues("Cookie", out var values) ? string.Join(";", values) : string.Empty;
            })
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = Json("{\"count\":42}") });
        var client = new KibanaRestClient(handler.Object);

        await client.PostAsync(config, "ubs-lottery-draw*/_count", new Dictionary<string, object?>(),
            CancellationToken.None);

        Assert.Contains("authelia_session=", capturedCookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EsErrorJson_IsReportedAsElasticsearchError()
    {
        ToolException ex = await PostErrorAsync(HttpStatusCode.BadRequest, Json(
            "{\"error\":{\"root_cause\":[{\"type\":\"index_not_found_exception\",\"reason\":\"no such index [missing]\"}],\"type\":\"index_not_found_exception\",\"reason\":\"no such index [missing]\"},\"status\":404}"));

        Assert.Equal("ELASTICSEARCH_ERROR", ex.Code);
        Assert.Contains("\"type\":\"index_not_found_exception\"", JsonSerializer.Serialize(ex.Details), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BareXmlBody_ThatIsNotALayout_FallsBackToKibanaError()
    {
        // A body whose first character is '<' but which contains no html/head/title/body layout
        // marker must NOT be classified as a gateway page: it falls through to the generic path.
        ToolException ex = await PostErrorAsync(HttpStatusCode.BadGateway, Html("<oops>not a layout"));

        Assert.Equal("KIBANA_ERROR", ex.Code);
    }

    [Fact]
    public async Task SuccessfulJson_BodyIsReturnedVerbatim()
    {
        KibanaRestClient client = CreateClient(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json("[{\"index\":\"ubs-lottery-draw-2026.08.14\"}]")
        });

        ElasticResponse response = await client.GetAsync(Prod, "_cat/indices/ubs-lottery-draw*?format=json", CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("ubs-lottery-draw-2026.08.14", response.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void InsecureHostPattern_IsAppliedWhenConfigured()
    {
        KibanaRestClient client = CreateClient("(^|\\.)corp\\.intern$");

        Assert.True(client.IsInsecureHost("https://kibana-p01.corp.intern"));
        Assert.True(client.IsInsecureHost("https://corp.intern/subpath"));
        Assert.False(client.IsInsecureHost("https://other.example.com"));
    }

    [Fact]
    public void InsecureHostPattern_WhenBlank_ValidatesEveryHostStrictly()
    {
        KibanaRestClient client = CreateClient("");

        Assert.False(client.IsInsecureHost("https://kibana.local"));
        Assert.False(client.IsInsecureHost("https://any.example.com"));
    }

    [Fact]
    public void InsecureHostPattern_WhenInvalid_ValidatesEveryHostStrictly()
    {
        // A regex syntax typo in config must fail closed (strict TLS), not disable validation.
        KibanaRestClient client = CreateClient("(^|.");

        Assert.False(client.IsInsecureHost("https://kibana.local"));
    }
}
