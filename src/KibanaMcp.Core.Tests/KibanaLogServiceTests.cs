using System.Net;
using System.Text;
using KibanaMcp.Core;
using Microsoft.Extensions.Configuration;
using Moq;
using Moq.Protected;
using Xunit;

namespace KibanaMcp.Tests;

/// <summary>
/// Verifies graceful degradation when the Kibana gateway answers with HTML / malformed bodies for
/// otherwise-successful status codes, and that response shapes carry the expected new fields.
/// </summary>
public class KibanaLogServiceTests
{
    private const string KibanaUrl = "https://kibana-elk-pe-prod-dc2-jj.everymatrix.local";

    private sealed class ServiceHarness
    {
        public Mock<HttpMessageHandler> Handler { get; }
        public KibanaLogService Service { get; }

        public ServiceHarness()
        {
            Handler = new Mock<HttpMessageHandler>();
            Handler.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                });
            var client = new KibanaRestClient(Handler.Object);
            Service = new KibanaLogService(new KibanaEnvironmentProvider(BindConfiguration()), client, TimeProvider.System);
        }
    }

    private static IConfiguration BindConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Environments:prod:KibanaBaseUrl"] = KibanaUrl,
                ["Environments:prod:UserName"] = "filebeat_writer",
                ["Environments:prod:Password"] = "secret",
                ["DefaultTimeZone"] = "Asia/Shanghai"
            })
            .Build();
    }

    [Fact]
    public async Task CountLogs_WithEmptyBody_ReportsEsResponseInvalidNotParserCrash()
    {
        var harness = new ServiceHarness();
        harness.Handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("", Encoding.UTF8, "application/json") });

        ToolTextResponse response = await harness.Service.CountLogsAsync(new CountLogsInput
        {
            Env = "prod",
            Index = "ubs-lottery-draw*",
            TimeRange = new PresetTimeRangeInput { Preset = "last_1_hours" }
        });

        Assert.True(response.IsError);
        string yaml = response.Text;
        Assert.Contains("empty response body", yaml, StringComparison.Ordinal);
        Assert.Contains("ES_RESPONSE_INVALID", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CountLogs_WithHtml2xx_ReportsEsResponseInvalidWithRawBody()
    {
        var harness = new ServiceHarness();
        harness.Handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<oops>proxy page", Encoding.UTF8, "text/html") });

        ToolTextResponse response = await harness.Service.CountLogsAsync(new CountLogsInput
        {
            Env = "prod",
            Index = "ubs-lottery-draw*",
            TimeRange = new PresetTimeRangeInput { Preset = "last_1_hours" }
        });

        Assert.True(response.IsError);
        Assert.Contains("not valid JSON", response.Text, StringComparison.Ordinal);
        Assert.Contains("ES_RESPONSE_INVALID", response.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CountLogs_WithAutheliaLoginPage_ReportsAuthRequired()
    {
        // End-to-end: a 200 + Authelia login page (the production failure) must degrade to AUTH_REQUIRED
        // with an actionable message at the tool level, not an opaque JSON parser error.
        var harness = new ServiceHarness();
        harness.Handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<!doctype html><html><head><base href=\"https://authelia.cek8jj02-p3kt.everymatrix.local/\"></head><body>login form</body></html>",
                    Encoding.UTF8,
                    "text/html")
            });

        ToolTextResponse response = await harness.Service.CountLogsAsync(new CountLogsInput
        {
            Env = "prod",
            Index = "ubs-lottery-draw*",
            TimeRange = new PresetTimeRangeInput { Preset = "last_1_hours" }
        });

        Assert.True(response.IsError);
        Assert.Contains("AUTH_REQUIRED", response.Text, StringComparison.Ordinal);
        Assert.Contains("SSO", response.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchIndex_EmitsManagementStyleReviewLinkForPattern()
    {
        var harness = new ServiceHarness();
        harness.Handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[{\"index\":\"ubs-lottery-draw-2026.08.14\"},{\"index\":\"ubs-lottery-draw-2026.08.16\"}]", Encoding.UTF8, "application/json")
            });

        ToolTextResponse response = await harness.Service.SearchIndexAsync(new SearchIndexInput
        {
            Env = "prod",
            Pattern = "ubs-lottery-draw*"
        });

        Assert.False(response.IsError);
        Assert.Contains("reviewLinks:", response.Text, StringComparison.Ordinal);
        // The pattern is embedded in the Discover app state; the link carries the relative now-7d window
        // and the management-style index reference rather than a saved data-view id.
        Assert.Contains("/app/discover#/", response.Text, StringComparison.Ordinal);
        Assert.Contains("ubs-lottery-draw*", response.Text, StringComparison.Ordinal);
        Assert.Contains("now-7d", response.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AggregateLogs_TextFieldTerms_RetriesWithKeywordSuffix()
    {
        // A terms aggregation on a mapped text field is rejected by ES ("Text fields are not optimised").
        // The service must transparently retry with ".keyword" appended and succeed.
        var harness = new ServiceHarness();
        int searchCalls = 0;
        harness.Handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                if (request.RequestUri!.ToString().Contains(".kibana"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"hits\":{\"hits\":[]}}", Encoding.UTF8, "application/json")
                    };
                }

                searchCalls++;
                string body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
                if (body.Contains("\"programType\"") && !body.Contains("programType.keyword"))
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent(
                            "{\"error\":{\"type\":\"illegal_argument_exception\",\"reason\":\"Text fields are not optimised for operations that require per-document field data like aggregations and sorting, so these operations are disabled by default. Please use a keyword field instead. Alternatively, set fielddata=true on [programType] in order to load field data by uninverting the inverted index.\"}}",
                            Encoding.UTF8)
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"hits\":{\"total\":{\"value\":3,\"relation\":\"eq\"}},\"aggregations\":{\"g0_programType_keyword\":{\"doc_count_error_upper_bound\":0,\"sum_other_doc_count\":0,\"buckets\":[{\"key\":\"wheelOfFortune\",\"doc_count\":2},{\"key\":\"unspecific\",\"doc_count\":1}]}}}",
                        Encoding.UTF8,
                        "application/json")
                };
            });

        ToolTextResponse response = await harness.Service.AggregateLogsAsync(new AggregateLogsInput
        {
            Env = "prod",
            Index = "ubs-lottery-draw*",
            TimeRange = new PresetTimeRangeInput { Preset = "last_1_hours" },
            Groups = [new AggregateTermsGroup { Type = "terms", Field = "programType", Size = 10 }]
        });

        Assert.False(response.IsError);
        Assert.Equal(2, searchCalls); // original + keyword retry
        Assert.Contains("wheelOfFortune", response.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AggregateLogs_MultipleTerms_OnlyUpgradesRejectedField()
    {
        // When several terms groups are sent and Elasticsearch rejects only one text field, the retry
        // must append ".keyword" to that field alone — a sibling boolean field (success) is aggregatable
        // as-is and must not be renamed to a nonexistent success.keyword.
        var harness = new ServiceHarness();
        int searchCalls = 0;
        harness.Handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                if (request.RequestUri!.ToString().Contains(".kibana"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"hits\":{\"hits\":[]}}", Encoding.UTF8, "application/json")
                    };
                }

                searchCalls++;
                string body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
                // First attempt targets bare actionType (text) -> reject, but the retry must keep success bare.
                if (body.Contains("\"actionType\"") && !body.Contains("actionType.keyword"))
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent(
                            "{\"error\":{\"type\":\"illegal_argument_exception\",\"reason\":\"Text fields are not optimised for operations that require per-document field data like aggregations and sorting, so these operations are disabled by default. Please use a keyword field instead. Alternatively, set fielddata=true on [actionType] in order to load field data by uninverting the inverted index.\"}}",
                            Encoding.UTF8)
                    };
                }

                string responseJson = body.Contains("success.keyword")
                    ? "{\"hits\":{\"total\":{\"value\":0,\"relation\":\"eq\"}},\"aggregations\":{}}"
                    : "{\"hits\":{\"total\":{\"value\":3,\"relation\":\"eq\"}},\"aggregations\":{\"g0_success\":{\"doc_count_error_upper_bound\":0,\"sum_other_doc_count\":0,\"buckets\":[{\"key\":1,\"doc_count\":2,\"g1_actionType_keyword\":{\"doc_count_error_upper_bound\":0,\"sum_other_doc_count\":0,\"buckets\":[{\"key\":\"assign\",\"doc_count\":2}]}}]}}}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                };
            });

        ToolTextResponse response = await harness.Service.AggregateLogsAsync(new AggregateLogsInput
        {
            Env = "prod",
            Index = "ubs-lottery-draw*",
            TimeRange = new PresetTimeRangeInput { Preset = "last_1_hours" },
            Groups =
            [
                new AggregateTermsGroup { Type = "terms", Field = "success", Size = 5 },
                new AggregateTermsGroup { Type = "terms", Field = "actionType", Size = 10 }
            ]
        });

        Assert.False(response.IsError);
        Assert.Equal(2, searchCalls);
        // success stayed as success (boolean, no keyword suffix) while actionType got upgraded.
        Assert.DoesNotContain("success.keyword", response.Text, StringComparison.Ordinal);
        Assert.Contains("actionType.keyword", response.Text, StringComparison.Ordinal);
        Assert.Contains("assign", response.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorResponse_CarriesReviewLinkToDiscover()
    {
        // The requirement: every Kibana log search output ends with a Kibana URL, including failures,
        // so the caller can jump straight into manual investigation of what went wrong.
        var harness = new ServiceHarness();
        harness.Handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("", Encoding.UTF8, "application/json")
            });

        ToolTextResponse response = await harness.Service.CountLogsAsync(new CountLogsInput
        {
            Env = "prod",
            Index = "ubs-lottery-draw*",
            TimeRange = new PresetTimeRangeInput { Preset = "last_1_hours" }
        });

        Assert.True(response.IsError);
        Assert.Contains("ES_RESPONSE_INVALID", response.Text, StringComparison.Ordinal);
        // The error envelope now carries a review link into Kibana Discover for the requested index.
        Assert.Contains("reviewLinks:", response.Text, StringComparison.Ordinal);
        Assert.Contains("/app/discover#/", response.Text, StringComparison.Ordinal);
        Assert.Contains("ubs-lottery-draw*", response.Text, StringComparison.Ordinal);
    }
}
