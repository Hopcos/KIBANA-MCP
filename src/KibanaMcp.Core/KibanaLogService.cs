using KibanaMcp.Core;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KibanaMcp;

public sealed partial class KibanaLogService(
    KibanaEnvironmentProvider environments,
    KibanaRestClient elastic,
    TimeProvider timeProvider,
    KibanaDataViewResolver? kibanaDataViews = null)
{
    private readonly KibanaDataViewResolver _kibanaDataViews = kibanaDataViews ?? new KibanaDataViewResolver(elastic);
    private const int MaxAggregateRows = 5000;
    private static readonly HashSet<string> UnconfirmedCaseVariantFields = new(StringComparer.Ordinal)
    {
        "request.GrantedBonusAmount",
        "request.transactions.BonusResult",
        "request.transactions.BonusResult.posting",
        "request.transactions.BonusResult.posting.amount",
        "request.transactions.BonusResult.posting.amountEUR",
        "request.transactions.CreditRealAmount",
        "request.transactions.DebitRealAmount",
        "response.transactions.BonusResult",
        "response.transactions.BonusResult.posting",
        "response.transactions.BonusResult.posting.amount",
        "response.transactions.BonusResult.posting.amountEUR",
        "response.transactions.BonusResult.posting.contribution",
        "response.transactions.CreditRealAmount",
        "response.transactions.DebitRealAmount"
    };

    private static readonly HashSet<string> ConfirmedSampleFields = new(StringComparer.Ordinal)
    {
        "request.grantedBonusAmount",
        "request.transactions.bonusResult",
        "request.transactions.bonusResult.posting",
        "request.transactions.bonusResult.posting.amount",
        "request.transactions.bonusResult.posting.amountEUR",
        "request.transactions.creditRealAmount",
        "request.transactions.debitRealAmount",
        "response.transactions.bonusResult",
        "response.transactions.bonusResult.posting",
        "response.transactions.bonusResult.posting.amount",
        "response.transactions.bonusResult.posting.amountEUR",
        "response.transactions.bonusResult.posting.contribution",
        "response.transactions.creditRealAmount",
        "response.transactions.debitRealAmount"
    };

    public async Task<ToolTextResponse> CountLogsAsync(CountLogsInput input, CancellationToken cancellationToken = default)
    {
        EnvironmentConfig? config = null;
        try
        {
            config = environments.Resolve(input.Env);
            ResolvedTimeRange timeWindow = TimeRangeResolver.Resolve(input.TimeRange, config, timeProvider);
            Dictionary<string, object?> body = new() { ["query"] = BuildQuery(timeWindow, input.Query) };
            Task<ElasticResponse> countTask = elastic.PostAsync(config, Endpoint(input.Index, "_count"), body, cancellationToken);
            Task<string?> dataViewTask = _kibanaDataViews.ResolveAsync(config, input.Index, cancellationToken);

            ElasticResponse response = await countTask;
            JsonObject root = TryParseJsonObject(response.Content);
            var count = root["count"]!.GetValue<long>();
            return YamlResponse.Success(
                config,
                new Dictionary<string, object?> { ["count"] = count },
                timeWindow,
                reviewLinks: KibanaReviews.Build(config, await dataViewTask, input.Query, timeWindow));
        }
        catch (ToolException ex)
        {
            return YamlResponse.Error(ex, config?.Env ?? input.Env, reviewLinks: ErrorReviewLinks(config, input.Index));
        }
        catch (Exception ex)
        {
            return YamlResponse.Unexpected(ex, config?.Env ?? input.Env, reviewLinks: ErrorReviewLinks(config, input.Index));
        }
    }

    public async Task<ToolTextResponse> AggregateLogsAsync(AggregateLogsInput input, CancellationToken cancellationToken = default)
    {
        EnvironmentConfig? config = null;
        try
        {
            config = environments.Resolve(input.Env);
            ResolvedTimeRange timeWindow = TimeRangeResolver.Resolve(input.TimeRange, config, timeProvider);
            List<AggregateGroupInput> groups = NormalizeGroups(input.Groups);
            List<MetricInput> metrics = NormalizeMetrics(input.Metrics);
            ValidateMetrics(metrics);

            Task<string?> dataViewTask = _kibanaDataViews.ResolveAsync(config, input.Index, cancellationToken);
            ElasticResponse response = await SearchAggregationsAsync(
                config,
                input.Index,
                groups,
                metrics,
                timeWindow,
                input.Query,
                extraBody: new Dictionary<string, object?> { ["track_total_hits"] = true },
                cancellationToken);
            JsonObject root = TryParseJsonObject(response.Content);
            var total = ReadTotal(root);
            List<Dictionary<string, object?>> rows = new();
            List<Dictionary<string, object?>> metadata = new();

            if (groups.Count == 0)
            {
                Dictionary<string, object?> row = new()
                {
                    ["keys"] = Array.Empty<object>(),
                    ["count"] = total
                };
                List<Dictionary<string, object?>> metricValues = HasNonCountMetrics(metrics)
                    ? ReadMetrics(ReadRequiredAggregations(root), metrics)
                    : [];
                if (metricValues.Count > 0)
                {
                    row["metrics"] = metricValues;
                }

                rows.Add(row);
            }
            else
            {
                JsonObject aggregations = ReadRequiredAggregations(root);
                FlattenAggregationRows(aggregations, groups, metrics, 0, [], rows, metadata, timeWindow);
            }

            var truncated = rows.Count > MaxAggregateRows;
            if (truncated)
            {
                rows = rows.Take(MaxAggregateRows).ToList();
            }

            string? dataView = await dataViewTask;
            if (dataView is not null)
            {
                foreach (Dictionary<string, object?> row in rows)
                {
                    if (row.TryGetValue("keys", out object? keysObject) && keysObject is IEnumerable<Dictionary<string, object?>> keys)
                    {
                        foreach (Dictionary<string, object?> key in keys)
                        {
                            string? link = KibanaReviews.BuildGroupKeyLink(config, dataView, input.Query, timeWindow, key);
                            if (link is not null)
                            {
                                key["reviewLink"] = link;
                            }
                        }
                    }
                }
            }

            Dictionary<string, object?> data = new()
            {
                ["groups"] = groups.Select(GroupToOutput).ToArray(),
                ["metrics"] = metrics.Select(MetricToOutput).ToArray(),
                ["totalMatched"] = total,
                ["rows"] = rows,
                ["groupMetadata"] = metadata
            };

            return YamlResponse.Success(
                config,
                data,
                timeWindow,
                truncated ? new { returnedRows = rows.Count, truncated = true } : null,
                KibanaReviews.Build(config, dataView, input.Query, timeWindow));
        }
        catch (ToolException ex)
        {
            return YamlResponse.Error(ex, config?.Env ?? input.Env, reviewLinks: ErrorReviewLinks(config, input.Index));
        }
        catch (Exception ex)
        {
            return YamlResponse.Unexpected(ex, config?.Env ?? input.Env, reviewLinks: ErrorReviewLinks(config, input.Index));
        }
    }

    public async Task<ToolTextResponse> SearchIndexAsync(SearchIndexInput input, CancellationToken cancellationToken = default)
    {
        EnvironmentConfig? config = null;
        try
        {
            config = environments.Resolve(input.Env);
            int limit = Math.Min(input.Limit ?? 200, 2000);
            string target = string.IsNullOrWhiteSpace(input.Pattern) ? "ubs-lottery-*" : input.Pattern.Trim();
            string path = $"_cat/indices/{target.TrimEnd('/')}?format=json&bytes=b&h=index,docs.count,store.size&s=index:asc";

            ElasticResponse response = await elastic.GetAsync(config, path, cancellationToken);
            JsonArray root = TryParseJsonArray(response.Content);

            int physicalIndices = 0;
            Dictionary<string, IndexFamily> families = new(StringComparer.Ordinal);
            foreach (JsonObject indexObject in root.Select(node => node?.AsObject()).Where(node => node is not null)!)
            {
                string? name = GetString(indexObject, "index");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                physicalIndices++;
                IndexFamilyParse parsed = ParseIndexFamily(name);
                if (!families.TryGetValue(parsed.Prefix, out IndexFamily? family))
                {
                    family = new IndexFamily { Prefix = parsed.Prefix };
                    families[parsed.Prefix] = family;
                }

                family.IndexCount++;
                family.DocsCount += GetLong(indexObject, "docs.count") ?? 0;
                family.StoreSizeBytes += GetLong(indexObject, "store.size") ?? 0;
                if (parsed.Date is not null)
                {
                    DateOnly date = parsed.Date.Value;
                    if (family.Earliest is null || date < family.Earliest)
                    {
                        family.Earliest = date;
                    }

                    if (family.Latest is null || date > family.Latest)
                    {
                        family.Latest = date;
                    }
                }
            }

            List<Dictionary<string, object?>> rows = families.Values
                .OrderByDescending(family => family.DocsCount)
                .ThenBy(family => family.Prefix, StringComparer.Ordinal)
                .Select(ToIndexFamilyRow)
                .ToList();

            bool truncated = rows.Count > limit;
            List<Dictionary<string, object?>> returned = rows.Take(limit).ToList();
            Dictionary<string, object?> data = new()
            {
                ["totalMatched"] = physicalIndices,
                ["totalFamilies"] = families.Count,
                ["families"] = returned
            };

            IReadOnlyList<string> reviewLinks = KibanaReviews.BuildIndexPatternLinks(config, target);
            return YamlResponse.Success(
                config,
                data,
                limits: truncated ? new { returnedRows = returned.Count, truncated = true } : null,
                reviewLinks: reviewLinks);
        }
        catch (ToolException ex)
        {
            return YamlResponse.Error(ex, config?.Env ?? input.Env, reviewLinks: ErrorReviewLinks(config, input.Pattern ?? string.Empty));
        }
        catch (Exception ex)
        {
            return YamlResponse.Unexpected(ex, config?.Env ?? input.Env, reviewLinks: ErrorReviewLinks(config, input.Pattern ?? string.Empty));
        }
    }

    public async Task<ToolTextResponse> TimeSeriesAsync(TimeSeriesInput input, CancellationToken cancellationToken = default)
    {
        AggregateLogsInput aggregateInput = new()
        {
            Env = input.Env,
            Index = input.Index,
            TimeRange = input.TimeRange,
            Query = input.Query,
            Groups =
            [
                new AggregateDateHistogramGroup
                {
                    Type = "date_histogram",
                    Field = "@timestamp",
                    Interval = input.Interval,
                    IncludeEmptyBuckets = input.IncludeEmptyBuckets
                }
            ],
            Metrics = [input.Metric ?? new MetricInput { Type = "count" }]
        };

        if (input.SplitBy is not null)
        {
            aggregateInput.Groups.Add(new AggregateTermsGroup
            {
                Type = "terms",
                Field = input.SplitBy.Field,
                Size = Math.Min(input.SplitBy.Size ?? 10, 100)
            });
        }

        EnvironmentConfig? config = null;
        try
        {
            config = environments.Resolve(input.Env);
            ResolvedTimeRange timeWindow = TimeRangeResolver.Resolve(input.TimeRange, config, timeProvider);
            List<AggregateGroupInput> groups = NormalizeGroups(aggregateInput.Groups);
            List<MetricInput> metrics = NormalizeMetrics(aggregateInput.Metrics);
            ValidateMetrics(metrics);
            Task<string?> dataViewTask = _kibanaDataViews.ResolveAsync(config, input.Index, cancellationToken);
            ElasticResponse response = await SearchAggregationsAsync(
                config,
                input.Index,
                groups,
                metrics,
                timeWindow,
                input.Query,
                extraBody: null,
                cancellationToken);
            JsonObject root = TryParseJsonObject(response.Content);
            List<Dictionary<string, object?>> rows = new();
            FlattenAggregationRows(ReadRequiredAggregations(root), groups, metrics, 0, [], rows, [], timeWindow);

            string? dataView = await dataViewTask;
            List<Dictionary<string, object?>> points = ToTimeSeriesPoints(rows, input.SplitBy is not null, metrics[0]);
            if (dataView is not null)
            {
                foreach (Dictionary<string, object?> point in points)
                {
                    string? link = KibanaReviews.BuildDiscoverUrl(
                        config,
                        dataView,
                        input.Query,
                        point["bucketStart"]?.ToString() ?? string.Empty,
                        point["bucketEnd"]?.ToString() ?? string.Empty);
                    if (link is not null)
                    {
                        point["reviewLink"] = link;
                    }
                }
            }

            Dictionary<string, object?> data = new()
            {
                ["interval"] = input.Interval,
                ["metric"] = MetricToOutput(metrics[0]),
                ["splitBy"] = input.SplitBy,
                ["points"] = points
            };

            return YamlResponse.Success(
                config,
                data,
                timeWindow,
                reviewLinks: KibanaReviews.Build(config, dataView, input.Query, timeWindow));
        }
        catch (ToolException ex)
        {
            return YamlResponse.Error(ex, config?.Env ?? input.Env, reviewLinks: ErrorReviewLinks(config, input.Index));
        }
        catch (Exception ex)
        {
            return YamlResponse.Unexpected(ex, config?.Env ?? input.Env, reviewLinks: ErrorReviewLinks(config, input.Index));
        }
    }

    public async Task<ToolTextResponse> CompareWindowsAsync(CompareWindowsInput input, CancellationToken cancellationToken = default)
    {
        EnvironmentConfig? config = null;
        try
        {
            config = environments.Resolve(input.Env);
            ResolvedTimeRange current = TimeRangeResolver.Resolve(input.Current, config, timeProvider);
            ResolvedTimeRange baseline = TimeRangeResolver.Resolve(input.Baseline, config, timeProvider);
            MetricInput metric = NormalizeMetrics(input.Metric is null ? null : [input.Metric]).Single();
            ValidateMetrics([metric]);

            int size = Math.Min(input.Size ?? 20, 1000);
            int minCount = input.MinCount ?? 0;
            bool includeMissingBaseline = input.IncludeMissingBaseline ?? true;
            string? groupBy = string.IsNullOrWhiteSpace(input.GroupBy) ? null : input.GroupBy.Trim();

            // Fire all data-fetching requests concurrently: current window, baseline window, and the
            // Kibana data-view lookup. Each tool call pays max(latencies), not the sum.
            Task<string?> dataViewTask = _kibanaDataViews.ResolveAsync(config, input.Index, cancellationToken);
            List<Dictionary<string, object?>> rows;
            if (groupBy is null)
            {
                Task<double> currentTask = ReadWindowMetricAsync(config, input.Index, current, input.Query, metric, cancellationToken);
                Task<double> baselineTask = ReadWindowMetricAsync(config, input.Index, baseline, input.Query, metric, cancellationToken);
                rows = [CompareRow(null, await currentTask, await baselineTask)];
            }
            else
            {
                Task<Dictionary<object, double>> currentTask = ReadGroupedWindowMetricAsync(config, input.Index, current, input.Query, groupBy, size, metric, cancellationToken);
                Task<Dictionary<object, double>> baselineTask = ReadGroupedWindowMetricAsync(config, input.Index, baseline, input.Query, groupBy, size, metric, cancellationToken);
                rows = MergeCompareRows(
                    await currentTask,
                    await baselineTask,
                    minCount,
                    includeMissingBaseline);
            }

            bool truncated = rows.Count > size;
            if (truncated)
            {
                rows = rows.Take(size).ToList();
            }

            string? dataView = await dataViewTask;
            Dictionary<string, object?> data = new()
            {
                ["metric"] = MetricToOutput(metric),
                ["groupBy"] = groupBy,
                ["current"] = WindowWithReviewLink(current, KibanaReviews.BuildWindowLink(config, dataView, input.Query, current)),
                ["baseline"] = WindowWithReviewLink(baseline, KibanaReviews.BuildWindowLink(config, dataView, input.Query, baseline)),
                ["rows"] = rows
            };

            return YamlResponse.ComparisonSuccess(
                config,
                data,
                current,
                baseline,
                truncated ? new { returnedRows = rows.Count, truncated = true } : null);
        }
        catch (ToolException ex)
        {
            return YamlResponse.Error(ex, config?.Env ?? input.Env, reviewLinks: ErrorReviewLinks(config, input.Index));
        }
        catch (Exception ex)
        {
            return YamlResponse.Unexpected(ex, config?.Env ?? input.Env, reviewLinks: ErrorReviewLinks(config, input.Index));
        }
    }

    public async Task<ToolTextResponse> SearchSamplesAsync(SearchSamplesInput input, CancellationToken cancellationToken = default)
    {
        EnvironmentConfig? config = null;
        try
        {
            config = environments.Resolve(input.Env);
            ResolvedTimeRange timeWindow = TimeRangeResolver.Resolve(input.TimeRange, config, timeProvider);
            var size = Math.Min(input.Size ?? 10, 100);
            List<SortInput> sort = input.Sort is { Count: > 0 } ? input.Sort : [new SortInput { Field = "@timestamp", Order = "desc" }];
            List<string> sourceFields = input.SourceFields is { Count: > 0 } ? input.SourceFields : DefaultSourceFields(input.Index);
            Dictionary<string, object?> body = new()
            {
                ["size"] = size,
                ["_source"] = sourceFields,
                ["sort"] = sort.Select(s => new Dictionary<string, object> { [s.Field] = new { order = s.Order } }).ToArray(),
                ["query"] = BuildQuery(timeWindow, input.Query)
            };

            if (input.TrackTotalHits == true)
            {
                body["track_total_hits"] = true;
            }

            if (input.SearchAfter is { Count: > 0 })
            {
                body["search_after"] = input.SearchAfter;
            }

            Task<string?> dataViewTask = _kibanaDataViews.ResolveAsync(config, input.Index, cancellationToken);
            ElasticResponse response = await elastic.PostAsync(config, Endpoint(input.Index, "_search"), body, cancellationToken);
            JsonObject root = TryParseJsonObject(response.Content);
            JsonObject hits = root["hits"]!.AsObject();
            string? dataView = await dataViewTask;
            List<Dictionary<string, object?>> samples = hits["hits"]!.AsArray().Select(hitNode =>
            {
                JsonObject hit = hitNode!.AsObject();
                var item = new Dictionary<string, object?>
                {
                    ["index"] = GetString(hit, "_index"),
                    ["source"] = JsonNodeToObject(hit["_source"])
                };
                string? contextLink = KibanaReviews.BuildContextLink(config, dataView, GetString(hit, "_id") ?? string.Empty, sourceFields);
                if (contextLink is not null)
                {
                    item["reviewLink"] = contextLink;
                }
                return item;
            }).ToList();

            (long Value, string Relation)? totalMatched = ReadTotalMatched(hits);
            Dictionary<string, object?> data = new() { ["samples"] = samples };
            if (totalMatched is not null)
            {
                data["totalMatched"] = totalMatched.Value.Value;
                if (totalMatched.Value.Relation != "eq")
                {
                    data["totalMatchedRelation"] = totalMatched.Value.Relation;
                }
            }

            var nextSearchAfter = hits["hits"]!.AsArray().LastOrDefault()?["sort"];
            bool truncated = HasMoreRows(totalMatched, samples.Count, size, input.SearchAfter is { Count: > 0 }, nextSearchAfter is not null);
            if (truncated && nextSearchAfter is not null)
            {
                data["nextSearchAfter"] = JsonNodeToObject(nextSearchAfter);
            }

            return YamlResponse.Success(
                config,
                data,
                timeWindow,
                truncated ? new { returnedRows = samples.Count, truncated = true } : null,
                KibanaReviews.Build(config, dataView, input.Query, timeWindow));
        }
        catch (ToolException ex)
        {
            return YamlResponse.Error(ex, config?.Env ?? input.Env, reviewLinks: ErrorReviewLinks(config, input.Index));
        }
        catch (Exception ex)
        {
            return YamlResponse.Unexpected(ex, config?.Env ?? input.Env, reviewLinks: ErrorReviewLinks(config, input.Index));
        }
    }

    public async Task<ToolTextResponse> DiscoverFieldsAsync(DiscoverFieldsInput input, CancellationToken cancellationToken = default)
    {
        EnvironmentConfig? config = null;
        try
        {
            config = environments.Resolve(input.Env);
            int limit = Math.Min(input.Limit ?? 200, 2000);
            string fieldPattern = string.IsNullOrWhiteSpace(input.FieldPattern) ? "*" : input.FieldPattern.Trim();
            string path = Endpoint(input.Index, "_field_caps") + "?fields=" + Uri.EscapeDataString(fieldPattern);

            ElasticResponse response = await elastic.GetAsync(config, path, cancellationToken);
            JsonObject root = TryParseJsonObject(response.Content);
            JsonObject fields = root["fields"] as JsonObject ?? new JsonObject();

            List<Dictionary<string, object?>> all = fields
                .Select(field => ToFieldCapability(field.Key, field.Value!.AsObject()))
                .Where(field => MatchesDiscoverFilters(field, input, fieldPattern))
                .OrderBy(field => field["name"]?.ToString(), StringComparer.Ordinal)
                .ToList();

            bool truncated = all.Count > limit;
            List<Dictionary<string, object?>> returned = all.Take(limit).ToList();
            Dictionary<string, object?> data = new()
            {
                ["totalMatched"] = all.Count,
                ["fields"] = returned
            };

            return YamlResponse.Success(
                config,
                data,
                limits: truncated ? new { returnedRows = returned.Count, truncated = true } : null);
        }
        catch (ToolException ex)
        {
            return YamlResponse.Error(ex, config?.Env ?? input.Env, reviewLinks: ErrorReviewLinks(config, input.Index));
        }
        catch (Exception ex)
        {
            return YamlResponse.Unexpected(ex, config?.Env ?? input.Env, reviewLinks: ErrorReviewLinks(config, input.Index));
        }
    }

    public async Task<ToolTextResponse> ExportRawEsResponseAsync(ExportRawEsResponseInput input, CancellationToken cancellationToken = default)
    {
        EnvironmentConfig? config = null;
        try
        {
            config = environments.Resolve(input.Env);
            var method = input.Method?.Trim();
            if (method is not ("search" or "count" or "field_caps"))
            {
                throw new ToolException("UNSAFE_OPERATION_BLOCKED", "Only search, count, and field_caps are allowed.");
            }

            object? body = input.Body?.ToDictionary(kv => kv.Key, kv => kv.Value?.ToPlainObject());
            if (method == "search")
            {
                body = GuardSearchBody(input.Body);
            }

            var path = Endpoint(input.Index, "_" + method) + BuildQueryString(input.QueryString);
            ElasticResponse response = method == "field_caps" && body is null
                ? await elastic.GetAsync(config, path, cancellationToken)
                : await elastic.PostAsync(config, path, body, cancellationToken);

            var directory = Path.Combine(Path.GetTempPath(), "ubs-lottery-kibana-mcp");
            Directory.CreateDirectory(directory);
            var name = $"es-response-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.json";
            var file = Path.Combine(directory, name);
            await File.WriteAllTextAsync(file, response.Content, cancellationToken);
            var length = new FileInfo(file).Length;

            return YamlResponse.ExportSuccess(
                config,
                new
                {
                    statusCode = response.StatusCode,
                    export = new
                    {
                        path = file,
                        name,
                        format = "json",
                        mimeType = response.ContentType ?? "application/json",
                        bytes = length
                    }
                });
        }
        catch (ToolException ex)
        {
            return YamlResponse.Error(ex, config?.Env ?? input.Env, reviewLinks: ErrorReviewLinks(config, input.Index));
        }
        catch (Exception ex)
        {
            return YamlResponse.Unexpected(ex, config?.Env ?? input.Env, reviewLinks: ErrorReviewLinks(config, input.Index));
        }
    }

    private static Dictionary<string, object?> BuildQuery(ResolvedTimeRange timeWindow, string? query)
    {
        List<object> filters = new()
        {
            new Dictionary<string, object?>
            {
                ["range"] = new Dictionary<string, object?>
                {
                    ["@timestamp"] = new Dictionary<string, object?>
                    {
                        ["gt"] = timeWindow.Gt,
                        ["gte"] = timeWindow.Gte,
                        ["lt"] = timeWindow.Lt,
                        ["lte"] = timeWindow.Lte
                    }.Where(kv => kv.Value is not null).ToDictionary()
                }
            }
        };

        // Match-all queries (null, blank, "*", "*:*") must not be sent as query_string: on indices that
        // narrow index.query.default_field a bare "*" matches nothing, while omitting the clause is a
        // true match-all through the time range alone.
        if (!KibanaReviews.IsMatchAllQuery(query))
        {
            filters.Add(new Dictionary<string, object?>
            {
                ["query_string"] = new Dictionary<string, object?>
                {
                    ["query"] = query
                }
            });
        }

        return new Dictionary<string, object?>
        {
            ["bool"] = new Dictionary<string, object?>
            {
                ["filter"] = filters
            }
        };
    }

    private async Task<double> ReadWindowMetricAsync(EnvironmentConfig config, string index, ResolvedTimeRange timeWindow, string? query, MetricInput metric, CancellationToken cancellationToken)
    {
        if (metric.Type == "count")
        {
            ElasticResponse countResponse = await elastic.PostAsync(config, Endpoint(index, "_count"), new Dictionary<string, object?> { ["query"] = BuildQuery(timeWindow, query) }, cancellationToken);
            JsonObject countRoot = TryParseJsonObject(countResponse.Content);
            return countRoot["count"]!.GetValue<double>();
        }

        Dictionary<string, object?> body = new()
        {
            ["size"] = 0,
            ["query"] = BuildQuery(timeWindow, query),
            ["aggs"] = BuildMetricAggs([metric])
        };
        ElasticResponse response = await elastic.PostAsync(config, Endpoint(index, "_search"), body, cancellationToken);
        JsonObject root = TryParseJsonObject(response.Content);
        return ReadMetricValue(ReadRequiredAggregations(root), metric);
    }

    private async Task<Dictionary<object, double>> ReadGroupedWindowMetricAsync(EnvironmentConfig config, string index, ResolvedTimeRange timeWindow, string? query, string groupBy, int size, MetricInput metric, CancellationToken cancellationToken)
    {
        AggregateTermsGroup group = new()
        {
            Type = "terms",
            Field = groupBy,
            Size = size,
            OrderBy = MetricOrderKey(metric),
            Order = "desc"
        };
        AggregateGroupInput[] groups = [group];

        ElasticResponse response = await SearchAggregationsAsync(config, index, groups, [metric], timeWindow, query, extraBody: null, cancellationToken);
        JsonObject root = TryParseJsonObject(response.Content);
        JsonObject aggregations = ReadRequiredAggregations(root);
        string aggName = GroupAggName(group, 0);
        JsonArray buckets = aggregations[aggName]?["buckets"]?.AsArray()
            ?? throw new ToolException("ELASTICSEARCH_ERROR", "Elasticsearch response did not include expected terms buckets.");

        Dictionary<object, double> result = new();
        foreach (JsonNode? bucketNode in buckets)
        {
            JsonObject bucket = bucketNode!.AsObject();
            object key = JsonNodeToObject(bucket["key"]) ?? string.Empty;
            result[key] = metric.Type == "count"
                ? bucket["doc_count"]!.GetValue<double>()
                : ReadMetricValue(bucket, metric);
        }

        return result;
    }

    /// <summary>
    /// Sends a guarded aggregation <c>_search</c>. When Elasticsearch rejects the request because a
    /// terms group references a mapped <c>text</c> field — text fields are not aggregatable without
    /// fielddata, and the conventional sibling "<c>.keyword</c>" sub-field is the intended aggregation
    /// surface — the request is retried once with <c>.keyword</c> appended to the offending terms
    /// field(s). The informative original error is re-thrown if the retry also fails, so no user input
    /// error is masked.
    /// </summary>
    private async Task<ElasticResponse> SearchAggregationsAsync(
        EnvironmentConfig config,
        string indexTarget,
        IReadOnlyList<AggregateGroupInput> groups,
        IReadOnlyList<MetricInput> metrics,
        ResolvedTimeRange timeWindow,
        string? query,
        Dictionary<string, object?>? extraBody = null,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, object?> Build()
        {
            Dictionary<string, object?> body = new()
            {
                ["size"] = 0,
                ["query"] = BuildQuery(timeWindow, query)
            };
            Dictionary<string, object?> aggs = BuildAggregationTree(groups, metrics, timeWindow);
            if (aggs.Count > 0)
            {
                body["aggs"] = aggs;
            }

            if (extraBody is not null)
            {
                foreach (KeyValuePair<string, object?> extra in extraBody)
                {
                    body[extra.Key] = extra.Value;
                }
            }

            return body;
        }

        try
        {
            return await elastic.PostAsync(config, Endpoint(indexTarget, "_search"), Build(), cancellationToken);
        }
        catch (ToolException ex) when (IsTextAggregationRejection(ex, out string? rejectedField)
            && TryAppendKeywordSuffix(groups, rejectedField))
        {
            try
            {
                return await elastic.PostAsync(config, Endpoint(indexTarget, "_search"), Build(), cancellationToken);
            }
            catch (ToolException)
            {
                throw ex;
            }
        }
    }

    /// <summary>True when the ES error is the "text fields are not optimised for aggregations" rejection.
    /// Multi-index searches wrap per-shard failures under <c>error.root_cause[]</c> with the top-level
    /// reason only reporting "all shards failed", so both surfaces are checked. The offending field
    /// (from the "<c>fielddata=true on [&lt;field&gt;]</c>" fragment) is reported so only that field is
    /// retried with <c>.keyword</c> — other terms groups (e.g. an unrelated boolean) must not change.</summary>
    private static bool IsTextAggregationRejection(ToolException ex, out string? rejectedField)
    {
        rejectedField = null;
        if (ex.Details is null)
        {
            return false;
        }

        try
        {
            object details = ex.Details;
            if (ReadDetailReason(details) is { } topReason && TryExtractTextField(topReason, out rejectedField))
            {
                return true;
            }

            if (details.GetType().GetProperty("rootCause")?.GetValue(details) is string[] rootCauses)
            {
                foreach (string rootCause in rootCauses)
                {
                    if (TryExtractTextField(rootCause, out rejectedField))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Extracts the field name from the ES text-field rejection message, e.g. "…fielddata=true
    /// on [programType] in order to…" → <c>programType</c>.</summary>
    private static bool TryExtractTextField(string message, out string? field)
    {
        field = null;
        if (!ContainsTextFieldWarning(message))
        {
            return false;
        }

        Match match = Regex.Match(message, @"\[\s*([^\]]+?)\s*\]");
        if (match.Success)
        {
            field = match.Groups[1].Value;
            return true;
        }

        return false;
    }

    private static string? ReadDetailReason(object details)
    {
        return details.GetType().GetProperty("reason")?.GetValue(details)?.ToString()
            ?? details.GetType().GetProperty("message")?.GetValue(details)?.ToString();
    }

    private static bool ContainsTextFieldWarning(string message)
    {
        return message.Contains("Text fields are not optimised", StringComparison.Ordinal);
    }

    /// <summary>Appends <c>.keyword</c> to the specific terms group whose field Elasticsearch rejected
    /// (the one named in the error), leaving every other group untouched. Returns true only when that
    /// field was actually upgraded so the retry is meaningful.</summary>
    private static bool TryAppendKeywordSuffix(IReadOnlyList<AggregateGroupInput> groups, string? rejectedField)
    {
        if (string.IsNullOrWhiteSpace(rejectedField))
        {
            return false;
        }

        foreach (AggregateGroupInput group in groups)
        {
            if (group is AggregateTermsGroup { Field: not null and not "" } terms
                && string.Equals(terms.Field, rejectedField, StringComparison.Ordinal))
            {
                terms.Field += ".keyword";
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, object?> BuildAggregationTree(IReadOnlyList<AggregateGroupInput> groups, IReadOnlyList<MetricInput> metrics, ResolvedTimeRange timeWindow)
    {
        Dictionary<string, object?> metricAggs = BuildMetricAggs(metrics);
        if (groups.Count == 0)
        {
            return metricAggs;
        }

        Dictionary<string, object?>? childAggs = metricAggs.Count == 0 ? null : metricAggs;
        for (var i = groups.Count - 1; i >= 0; i--)
        {
            var name = GroupAggName(groups[i], i);
            Dictionary<string, object?> groupAgg = BuildGroupAgg(groups[i], timeWindow);
            if (childAggs is not null && childAggs.Count > 0)
            {
                groupAgg["aggs"] = childAggs;
            }

            childAggs = new Dictionary<string, object?> { [name] = groupAgg };
        }

        return childAggs ?? [];
    }

    private static Dictionary<string, object?> BuildMetricAggs(IReadOnlyList<MetricInput> metrics)
    {
        Dictionary<string, object?> aggs = new();
        foreach (MetricInput? metric in metrics.Where(m => m.Type != "count"))
        {
            var name = MetricName(metric);
            aggs[name] = metric.Type switch
            {
                "avg" or "max" or "min" or "sum" or "cardinality" => new Dictionary<string, object?>
                {
                    [metric.Type] = new Dictionary<string, object?> { ["field"] = metric.Field }
                },
                "percentiles" => new Dictionary<string, object?>
                {
                    ["percentiles"] = new Dictionary<string, object?>
                    {
                        ["field"] = metric.Field,
                        ["percents"] = metric.Percents is { Count: > 0 } ? metric.Percents : new List<double> { 50, 95, 99 }
                    }
                },
                _ => throw new ToolException("ELASTICSEARCH_ERROR", $"Unsupported metric type '{metric.Type}'.")
            };
        }

        return aggs;
    }

    private static Dictionary<string, object?> BuildGroupAgg(AggregateGroupInput group, ResolvedTimeRange timeWindow)
    {
        return group switch
        {
            AggregateTermsGroup terms => new Dictionary<string, object?>
            {
                ["terms"] = new Dictionary<string, object?>
                {
                    ["field"] = terms.Field,
                    ["size"] = Math.Min(terms.Size ?? 20, 1000),
                    ["order"] = BuildTermsOrder(terms)
                }
            },
            AggregateDateHistogramGroup histogram => new Dictionary<string, object?>
            {
                ["date_histogram"] = BuildDateHistogram(histogram, timeWindow)
            },
            _ => throw new ToolException("ELASTICSEARCH_ERROR", "Unsupported aggregate group.")
        };
    }

    private static Dictionary<string, object?> BuildTermsOrder(AggregateTermsGroup terms)
    {
        var by = terms.OrderBy ?? "count";
        var direction = terms.Order ?? "desc";
        var esBy = by switch
        {
            "count" => "_count",
            "key" => "_key",
            _ => by
        };
        return new Dictionary<string, object?> { [esBy] = direction };
    }

    private static Dictionary<string, object?> BuildDateHistogram(AggregateDateHistogramGroup histogram, ResolvedTimeRange timeWindow)
    {
        var interval = histogram.Interval;
        var lower = timeWindow.Gte ?? timeWindow.Gt;
        var upper = timeWindow.Lte ?? timeWindow.Lt;
        bool includeEmptyBuckets = histogram.IncludeEmptyBuckets == true;
        Dictionary<string, object?> result = new()
        {
            ["field"] = "@timestamp",
            ["time_zone"] = timeWindow.TimeZone,
            ["min_doc_count"] = includeEmptyBuckets ? 0 : 1
        };

        if (interval == "1d")
        {
            result["calendar_interval"] = "1d";
        }
        else
        {
            result["fixed_interval"] = interval;
        }

        if (includeEmptyBuckets && lower is not null && upper is not null)
        {
            result["extended_bounds"] = new Dictionary<string, object?>
            {
                ["min"] = lower,
                ["max"] = ExtendedBoundsMax(timeWindow, upper, interval)
            };
        }

        return result;
    }

    private static string ExtendedBoundsMax(ResolvedTimeRange timeWindow, string upper, string interval)
    {
        if (timeWindow.Lt is null || !DateTimeOffset.TryParse(upper, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
        {
            return upper;
        }

        TimeZoneInfo timeZone = TimeZoneResolver.FindById(timeWindow.TimeZone);
        DateTimeOffset localUpper = TimeZoneInfo.ConvertTime(parsed, timeZone);
        return IsIntervalBoundary(localUpper, interval)
            ? TimeRangeResolver.ToIso(SubtractInterval(localUpper, interval))
            : upper;
    }

    private static bool IsIntervalBoundary(DateTimeOffset value, string interval)
    {
        return interval switch
        {
            "1m" => value.Second == 0,
            "5m" => value.Minute % 5 == 0 && value.Second == 0,
            "15m" => value.Minute % 15 == 0 && value.Second == 0,
            "30m" => value.Minute % 30 == 0 && value.Second == 0,
            "1h" => value.Minute == 0 && value.Second == 0,
            "1d" => value.Hour == 0 && value.Minute == 0 && value.Second == 0,
            _ => false
        };
    }

    private static DateTimeOffset SubtractInterval(DateTimeOffset value, string interval)
    {
        return interval switch
        {
            "1m" => value.AddMinutes(-1),
            "5m" => value.AddMinutes(-5),
            "15m" => value.AddMinutes(-15),
            "30m" => value.AddMinutes(-30),
            "1h" => value.AddHours(-1),
            "1d" => value.AddDays(-1),
            _ => value
        };
    }

    private static List<AggregateGroupInput> NormalizeGroups(List<AggregateGroupInput>? groups)
    {
        List<AggregateGroupInput> normalized = groups ?? [];
        if (normalized.Count > 2)
        {
            throw new ToolException("RESULT_TOO_LARGE", "aggregate_logs supports up to 2 group levels in version 1.");
        }

        return normalized;
    }

    private static List<MetricInput> NormalizeMetrics(List<MetricInput>? metrics)
    {
        List<MetricInput> normalized = metrics is { Count: > 0 } ? metrics : [new MetricInput { Type = "count" }];
        if (normalized.Count > 10)
        {
            throw new ToolException("RESULT_TOO_LARGE", "At most 10 metrics are allowed.");
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (MetricInput metric in normalized)
        {
            var name = MetricName(metric);
            if (!names.Add(name))
            {
                throw new ToolException("ELASTICSEARCH_ERROR", $"Duplicate metric name '{name}'.");
            }
        }

        return normalized;
    }

    private static void ValidateMetrics(IEnumerable<MetricInput> metrics)
    {
        foreach (MetricInput metric in metrics)
        {
            if (metric.Type != "count" && string.IsNullOrWhiteSpace(metric.Field))
            {
                throw new ToolException("FIELD_NOT_AGGREGATABLE", $"Metric '{metric.Type}' requires field.");
            }
        }
    }

    private static bool HasNonCountMetrics(IEnumerable<MetricInput> metrics)
    {
        return metrics.Any(metric => metric.Type != "count");
    }

    private static JsonObject ReadRequiredAggregations(JsonObject root)
    {
        if (root["aggregations"] is JsonObject aggregations)
        {
            return aggregations;
        }

        throw new ToolException(
            "ELASTICSEARCH_ERROR",
            "Elasticsearch response did not include expected aggregations.");
    }

    private static string MetricName(MetricInput metric)
    {
        if (!string.IsNullOrWhiteSpace(metric.Name))
        {
            return metric.Name;
        }

        return metric.Type == "count"
            ? "count"
            : string.IsNullOrWhiteSpace(metric.Field)
                ? metric.Type
                : $"{metric.Type}_{metric.Field.Replace('.', '_')}";
    }

    private static string MetricOrderKey(MetricInput metric)
    {
        if (metric.Type == "count")
        {
            return "count";
        }

        if (metric.Type == "percentiles")
        {
            double percent = metric.Percents is { Count: > 0 } ? metric.Percents[0] : 95;
            return $"{MetricName(metric)}.{percent.ToString(CultureInfo.InvariantCulture)}";
        }

        return MetricName(metric);
    }

    private static long ReadTotal(JsonObject root)
    {
        if (root["hits"] is not JsonObject hits || hits["total"] is null)
        {
            return 0;
        }

        JsonNode total = hits["total"]!;
        return total is JsonObject totalObject ? totalObject["value"]!.GetValue<long>() : total.GetValue<long>();
    }

    private static (long Value, string Relation)? ReadTotalMatched(JsonObject hits)
    {
        if (hits["total"] is null)
        {
            return null;
        }

        JsonNode total = hits["total"]!;
        if (total is JsonObject totalObject)
        {
            return (totalObject["value"]!.GetValue<long>(), totalObject["relation"]?.GetValue<string>() ?? "eq");
        }

        return (total.GetValue<long>(), "eq");
    }

    private static bool HasMoreRows((long Value, string Relation)? totalMatched, int returnedRows, int requestedRows, bool hasSearchAfter, bool hasNextSearchAfter)
    {
        if (totalMatched is null || hasSearchAfter)
        {
            return returnedRows == requestedRows && hasNextSearchAfter;
        }

        return totalMatched.Value.Relation == "gte" || totalMatched.Value.Value > returnedRows;
    }

    private static void FlattenAggregationRows(JsonObject aggs, IReadOnlyList<AggregateGroupInput> groups, IReadOnlyList<MetricInput> metrics, int level, List<Dictionary<string, object?>> keys, List<Dictionary<string, object?>> rows, List<Dictionary<string, object?>> metadata, ResolvedTimeRange timeWindow)
    {
        var aggName = GroupAggName(groups[level], level);
        if (aggs[aggName] is not JsonObject groupAgg || groupAgg["buckets"] is not JsonArray buckets)
        {
            return;
        }

        metadata.Add(new Dictionary<string, object?>
        {
            ["path"] = GroupPath(groups[level]),
            ["type"] = groups[level].Type,
            ["field"] = GroupField(groups[level]),
            ["returnedBuckets"] = buckets.Count,
            ["sumOtherDocCount"] = groupAgg["sum_other_doc_count"]?.GetValue<long>(),
            ["docCountErrorUpperBound"] = groupAgg["doc_count_error_upper_bound"]?.GetValue<long>()
        });

        foreach (JsonNode? bucketNode in buckets)
        {
            JsonObject bucket = bucketNode!.AsObject();
            List<Dictionary<string, object?>> nextKeys = new(keys) { BucketKey(groups[level], bucket, timeWindow) };
            if (level + 1 < groups.Count)
            {
                FlattenAggregationRows(bucket, groups, metrics, level + 1, nextKeys, rows, metadata, timeWindow);
            }
            else
            {
                Dictionary<string, object?> row = new()
                {
                    ["keys"] = nextKeys,
                    ["count"] = bucket["doc_count"]!.GetValue<long>()
                };
                List<Dictionary<string, object?>> metricValues = ReadMetrics(bucket, metrics);
                if (metricValues.Count > 0)
                {
                    row["metrics"] = metricValues;
                }

                rows.Add(row);
            }
        }
    }

    private static List<Dictionary<string, object?>> ReadMetrics(JsonObject element, IReadOnlyList<MetricInput> metrics)
    {
        List<Dictionary<string, object?>> result = new();
        foreach (MetricInput? metric in metrics.Where(m => m.Type != "count"))
        {
            var name = MetricName(metric);
            if (element[name] is not JsonObject metricElement)
            {
                continue;
            }

            if (metric.Type == "percentiles")
            {
                result.Add(new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["type"] = metric.Type,
                    ["percentiles"] = JsonNodeToObject(metricElement["values"])
                });
            }
            else
            {
                result.Add(new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["type"] = metric.Type,
                    ["value"] = metricElement["value"]?.GetValue<double?>()
                });
            }
        }

        return result;
    }

    private static double ReadMetricValue(JsonObject element, MetricInput metric)
    {
        if (metric.Type == "count")
        {
            return element["doc_count"]?.GetValue<double>() ?? 0;
        }

        var name = MetricName(metric);
        if (element[name] is not JsonObject metricElement)
        {
            return 0;
        }

        if (metric.Type == "percentiles")
        {
            JsonObject values = metricElement["values"]!.AsObject();
            double percent = metric.Percents is { Count: > 0 } ? metric.Percents[0] : 95;
            string key = values.Select(kv => kv.Key).FirstOrDefault(k => PercentKeyMatches(k, percent)) ?? percent.ToString(CultureInfo.InvariantCulture);
            return values[key]?.GetValue<double?>() ?? 0;
        }

        return metricElement["value"]?.GetValue<double?>() ?? 0;
    }

    private static bool PercentKeyMatches(string key, double percent)
    {
        return double.TryParse(key, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && Math.Abs(parsed - percent) < 0.000001;
    }

    private static List<Dictionary<string, object?>> MergeCompareRows(Dictionary<object, double> current, Dictionary<object, double> baseline, int minCount, bool includeMissingBaseline)
    {
        List<object> keys = current.Keys.Concat(baseline.Keys).Distinct().ToList();
        return keys
            .Select(key => CompareRow(key, current.GetValueOrDefault(key), baseline.GetValueOrDefault(key)))
            .Where(row =>
            {
                double currentValue = Convert.ToDouble(row["current"], CultureInfo.InvariantCulture);
                double baselineValue = Convert.ToDouble(row["baseline"], CultureInfo.InvariantCulture);
                return Math.Max(currentValue, baselineValue) >= minCount
                    && (includeMissingBaseline || baselineValue > 0 || currentValue <= 0);
            })
            .OrderByDescending(row => Math.Abs(Convert.ToDouble(row["delta"], CultureInfo.InvariantCulture)))
            .ThenByDescending(row => Convert.ToDouble(row["current"], CultureInfo.InvariantCulture))
            .ToList();
    }

    private static Dictionary<string, object?> CompareRow(object? key, double current, double baseline)
    {
        double delta = current - baseline;
        return new Dictionary<string, object?>
        {
            ["key"] = key,
            ["current"] = current,
            ["baseline"] = baseline,
            ["delta"] = delta,
            ["deltaPct"] = baseline == 0 ? null : Math.Round(delta / baseline, 4),
            ["changeType"] = ChangeType(current, baseline)
        };
    }

    private static string ChangeType(double current, double baseline)
    {
        if (baseline == 0 && current > 0)
        {
            return "new";
        }

        if (current == 0 && baseline > 0)
        {
            return "missing";
        }

        if (current > baseline)
        {
            return "increase";
        }

        if (current < baseline)
        {
            return "decrease";
        }

        return "same";
    }

    private static Dictionary<string, object?> ToFieldCapability(string name, JsonObject byType)
    {
        string[] types = byType.Select(kv => kv.Key).Order(StringComparer.Ordinal).ToArray();
        bool searchable = byType.Any(kv => kv.Value?["searchable"]?.GetValue<bool>() == true);
        bool aggregatable = byType.Any(kv => kv.Value?["aggregatable"]?.GetValue<bool>() == true);
        Dictionary<string, object?> result = new()
        {
            ["name"] = name,
            ["types"] = types,
            ["searchable"] = searchable,
            ["aggregatable"] = aggregatable
        };

        bool? confirmed = FieldConfirmation(name);
        if (confirmed is not null)
        {
            result["confirmedInSamples"] = confirmed.Value;
        }

        AddFieldCapsArray(result, "indices", byType, "indices");
        AddFieldCapsArray(result, "nonSearchableIndices", byType, "non_searchable_indices");
        AddFieldCapsArray(result, "nonAggregatableIndices", byType, "non_aggregatable_indices");
        return result;
    }

    private static bool? FieldConfirmation(string name)
    {
        if (ConfirmedSampleFields.Contains(name))
        {
            return true;
        }

        if (UnconfirmedCaseVariantFields.Contains(name))
        {
            return false;
        }

        return null;
    }

    private static void AddFieldCapsArray(Dictionary<string, object?> target, string outputName, JsonObject byType, string inputName)
    {
        string[] values = byType
            .SelectMany(kv => kv.Value?[inputName]?.AsArray().Select(node => node?.GetValue<string>()).OfType<string>() ?? [])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (values.Length > 0)
        {
            target[outputName] = values;
        }
    }

    private static bool MatchesDiscoverFilters(Dictionary<string, object?> field, DiscoverFieldsInput input, string fieldPattern)
    {
        string name = field["name"]!.ToString()!;
        if (!WildcardMatches(fieldPattern, name))
        {
            return false;
        }

        if (FieldConfirmation(name) == false && input.IncludeUnconfirmedFields != true)
        {
            return false;
        }

        if (input.Prefixes is { Count: > 0 } && !input.Prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return false;
        }

        if (input.OnlyAggregatable == true && field["aggregatable"] is not true)
        {
            return false;
        }

        if (input.OnlySearchable == true && field["searchable"] is not true)
        {
            return false;
        }

        if (input.Types is { Count: > 0 })
        {
            var types = (string[])field["types"]!;
            if (!types.Any(type => input.Types.Contains(type, StringComparer.Ordinal)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool WildcardMatches(string pattern, string value)
    {
        if (pattern == "*")
        {
            return true;
        }

        string regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(value, regex);
    }

    private static Dictionary<string, object?> BucketKey(AggregateGroupInput group, JsonObject bucket, ResolvedTimeRange timeWindow)
    {
        if (group is AggregateDateHistogramGroup histogram)
        {
            TimeZoneInfo timeZone = TimeZoneResolver.FindById(timeWindow.TimeZone);
            string start = bucket["key_as_string"] is JsonNode keyAsString
                ? NormalizeIso(keyAsString.GetValue<string>(), timeZone)
                : TimeRangeResolver.ToIso(DateTimeOffset.FromUnixTimeMilliseconds(bucket["key"]!.GetValue<long>()), timeZone);
            return new Dictionary<string, object?>
            {
                ["type"] = "date_histogram",
                ["field"] = "@timestamp",
                ["key"] = start,
                ["bucketStart"] = start,
                ["bucketEnd"] = AddInterval(start, histogram.Interval, timeWindow.TimeZone)
            };
        }

        AggregateTermsGroup terms = (AggregateTermsGroup)group;
        return new Dictionary<string, object?>
        {
            ["type"] = "terms",
            ["field"] = terms.Field,
            ["key"] = JsonNodeToObject(bucket["key"])
        };
    }

    private static string NormalizeIso(string value, TimeZoneInfo timeZone)
    {
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
            ? TimeRangeResolver.ToIso(parsed, timeZone)
            : value;
    }

    private static string AddInterval(string start, string interval, string timeZone)
    {
        if (!DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
        {
            return start;
        }

        TimeZoneInfo targetTimeZone = TimeZoneResolver.FindById(timeZone);
        DateTimeOffset localStart = TimeZoneInfo.ConvertTime(parsed, targetTimeZone);

        DateTimeOffset end = interval switch
        {
            "1m" => localStart.AddMinutes(1),
            "5m" => localStart.AddMinutes(5),
            "15m" => localStart.AddMinutes(15),
            "30m" => localStart.AddMinutes(30),
            "1h" => localStart.AddHours(1),
            "1d" => localStart.AddDays(1),
            _ => localStart
        };
        return TimeRangeResolver.ToIso(end);
    }

    private static List<Dictionary<string, object?>> ToTimeSeriesPoints(List<Dictionary<string, object?>> rows, bool hasSplit, MetricInput metric)
    {
        if (!hasSplit)
        {
            return rows.Select(row =>
            {
                Dictionary<string, object?> key = ((IEnumerable<Dictionary<string, object?>>)row["keys"]!).First();
                Dictionary<string, object?> point = new()
                {
                    ["bucketStart"] = key["bucketStart"],
                    ["bucketEnd"] = key["bucketEnd"]
                };
                AddMetricPoint(point, row, metric);
                return point;
            }).ToList();
        }

        return rows.GroupBy(row => ((IEnumerable<Dictionary<string, object?>>)row["keys"]!).First()["bucketStart"]?.ToString())
            .Select(group =>
            {
                Dictionary<string, object?> firstKey = ((IEnumerable<Dictionary<string, object?>>)group.First()["keys"]!).First();
                return new Dictionary<string, object?>
                {
                    ["bucketStart"] = firstKey["bucketStart"],
                    ["bucketEnd"] = firstKey["bucketEnd"],
                    ["groups"] = group.Select(row =>
                    {
                        Dictionary<string, object?>[] keys = ((IEnumerable<Dictionary<string, object?>>)row["keys"]!).ToArray();
                        Dictionary<string, object?> item = new() { ["key"] = keys[1]["key"] };
                        AddMetricPoint(item, row, metric);
                        return item;
                    }).ToArray()
                };
            }).ToList();
    }

    private static void AddMetricPoint(Dictionary<string, object?> target, Dictionary<string, object?> row, MetricInput metric)
    {
        if (metric.Type == "count")
        {
            target["count"] = row["count"];
            return;
        }

        IEnumerable<Dictionary<string, object?>>? metrics = row.TryGetValue("metrics", out var value) ? (IEnumerable<Dictionary<string, object?>>?)value : null;
        Dictionary<string, object?>? first = metrics?.FirstOrDefault();
        if (first is null)
        {
            return;
        }

        if (metric.Type == "percentiles")
        {
            target["percentiles"] = first["percentiles"];
        }
        else
        {
            target["value"] = first["value"];
        }
    }

    private static object GroupToOutput(AggregateGroupInput group)
    {
        return group switch
        {
            AggregateTermsGroup terms => new { type = "terms", field = terms.Field, size = Math.Min(terms.Size ?? 20, 1000), orderBy = terms.OrderBy ?? "count", order = terms.Order ?? "desc" },
            AggregateDateHistogramGroup histogram => new { type = "date_histogram", field = "@timestamp", interval = histogram.Interval, includeEmptyBuckets = histogram.IncludeEmptyBuckets ?? false },
            _ => group
        };
    }

    private static object MetricToOutput(MetricInput metric)
    {
        return new { type = metric.Type, name = metric.Name, field = metric.Field, percents = metric.Percents };
    }

    private static string GroupAggName(AggregateGroupInput group, int level)
    {
        return group switch
        {
            AggregateTermsGroup terms => $"g{level}_{terms.Field.Replace('.', '_')}",
            AggregateDateHistogramGroup => $"g{level}_timestamp",
            _ => $"g{level}"
        };
    }

    private static string GroupPath(AggregateGroupInput group)
    {
        return GroupField(group) ?? group.Type;
    }

    private static string? GroupField(AggregateGroupInput group)
    {
        return group switch
        {
            AggregateTermsGroup terms => terms.Field,
            AggregateDateHistogramGroup => "@timestamp",
            _ => null
        };
    }

    private static List<string> DefaultSourceFields(string indexTarget)
    {
        _ = indexTarget;
        return ["@timestamp", "method", "milliseconds", "hostname", "domainID", "details"];
    }

    /// <summary>Renders a resolved window with an optional reviewLink nested inside, so each window
    /// carries its own Discover deep link.</summary>
    private static Dictionary<string, object?> WindowWithReviewLink(ResolvedTimeRange window, string? reviewLink)
    {
        Dictionary<string, object?> result = new() { ["input"] = window.Input };
        if (window.Gt is not null)
        {
            result["gt"] = window.Gt;
        }

        if (window.Gte is not null)
        {
            result["gte"] = window.Gte;
        }

        if (window.Lt is not null)
        {
            result["lt"] = window.Lt;
        }

        if (window.Lte is not null)
        {
            result["lte"] = window.Lte;
        }

        if (reviewLink is not null)
        {
            result["reviewLink"] = reviewLink;
        }

        return result;
    }

    [GeneratedRegex(@"^ubs-lottery-[a-z0-9]+(?:-[a-z0-9]+)*-(?<date>\d{4}\.\d{2}\.\d{2})(?:-.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex IndexDatePattern();

    private static IndexFamilyParse ParseIndexFamily(string indexName)
    {
        Match match = IndexDatePattern().Match(indexName);
        if (match.Success &&
            DateOnly.TryParseExact(match.Groups["date"].Value, "yyyy.MM.dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsedDate))
        {
            string prefix = indexName[..(match.Groups["date"].Index - 1)];
            return new IndexFamilyParse(prefix, parsedDate);
        }

        return new IndexFamilyParse(indexName, null);
    }

    private static Dictionary<string, object?> ToIndexFamilyRow(IndexFamily family)
    {
        string? description = IndexCatalog.TryGetDescription(family.Prefix);
        Dictionary<string, object?> row = new()
        {
            ["pattern"] = family.Prefix + "*",
            ["description"] = description ?? "Unknown index family; not present in the bundled catalog. Inspect the field surface with discover_fields before querying.",
            ["indices"] = family.IndexCount,
            ["docsCount"] = family.DocsCount
        };

        if (family.StoreSizeBytes > 0)
        {
            row["storeSizeBytes"] = family.StoreSizeBytes;
        }

        if (family.Earliest is not null && family.Latest is not null)
        {
            row["earliestDate"] = family.Earliest.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            row["latestDate"] = family.Latest.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return row;
    }

    private sealed class IndexFamily
    {
        public required string Prefix { get; init; }
        public int IndexCount { get; set; }
        public long DocsCount { get; set; }
        public long StoreSizeBytes { get; set; }
        public DateOnly? Earliest { get; set; }
        public DateOnly? Latest { get; set; }
    }

    private readonly record struct IndexFamilyParse(string Prefix, DateOnly? Date);

    private static string Endpoint(string indexTarget, string operation)
    {
        return $"{indexTarget.TrimEnd('/')}/{operation}";
    }

    private static string? GetString(JsonObject element, string property)
    {
        return element[property]?.GetValue<string>();
    }

    private static long? GetLong(JsonObject element, string property)
    {
        string? text = GetString(element, property);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : null;
    }

    private static object? GuardSearchBody(Dictionary<string, RawEsValue>? body)
    {
        if (body is null)
        {
            return new Dictionary<string, object?> { ["size"] = 10 };
        }

        Dictionary<string, object?> plain = body.ToDictionary(kv => kv.Key, kv => kv.Value?.ToPlainObject());
        if (plain.TryGetValue("size", out var sizeValue) && Convert.ToInt32(sizeValue, CultureInfo.InvariantCulture) > 1000)
        {
            throw new ToolException("RESULT_TOO_LARGE", "search size must be <= 1000.");
        }

        if (!plain.ContainsKey("size"))
        {
            plain["size"] = 10;
        }

        return plain;
    }

    private static object? JsonNodeToObject(JsonNode? element)
    {
        return element switch
        {
            null => null,
            JsonObject obj => obj.ToDictionary(property => property.Key, property => JsonNodeToObject(property.Value)),
            JsonArray array => array.Select(JsonNodeToObject).ToArray(),
            JsonValue value => JsonValueToObject(value),
            _ => element.ToJsonString()
        };
    }

    private static object? JsonValueToObject(JsonValue value)
    {
        if (value.TryGetValue<string>(out var stringValue))
        {
            return stringValue;
        }

        if (value.TryGetValue<long>(out var longValue))
        {
            return longValue;
        }

        if (value.TryGetValue<double>(out var doubleValue))
        {
            return doubleValue;
        }

        if (value.TryGetValue<bool>(out var boolValue))
        {
            return boolValue;
        }

        return null;
    }

    private static string BuildQueryString(Dictionary<string, RawEsValue>? values)
    {
        if (values is null || values.Count == 0)
        {
            return string.Empty;
        }

        return "?" + string.Join("&", values.Select(kv =>
        {
            return $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value.ToQueryStringValue())}";
        }));
    }

    /// <summary>
    /// Parses a response body as a JSON object with graceful degradation: a successful Elasticsearch
    /// body is always a JSON object, so anything else (empty body, an HTML error/CSP page, or a raw
    /// parse failure) is an upstream anomaly that must not surface as a raw JSON parser exception.
    /// </summary>
    private static JsonObject TryParseJsonObject(string content) => TryParseJsonNode(content)?.AsObject()
        ?? throw InvalidEsResponse(content);

    /// <summary>Parses a response body as a JSON array, returning an empty array when it is not one
    /// (some versions of <c>_cat</c> answer an empty body for a pattern without matches).</summary>
    private static JsonArray TryParseJsonArray(string content) => TryParseJsonNode(content) is JsonArray array
        ? array
        : new JsonArray();

    private static JsonNode? TryParseJsonNode(string content)
    {
        try
        {
            return JsonNode.Parse(content);
        }
        catch (JsonException ex)
        {
            // The parser message only reports the position. DiagnosticsInJsonException (net8+) carries
            // the offending JSON fragment as well, so surface it when available. The response itself is
            // included in the error either way; a raw parser exception must never leak to the caller.
            string position = $" (at {ex.LineNumber}:{ex.BytePositionInLine})";
            throw InvalidEsResponse(content, position);
        }
    }

    /// <summary>Builds an actionalbe ToolException for an invalid Elasticsearch response body. Using a
    /// ToolException (instead of the raw <see cref="JsonException"/>) keeps the failure inside the
    /// tools' <c>catch (ToolException)</c> envelope rather than the generic unexpected-error path.</summary>
    private static ToolException InvalidEsResponse(string content, string? position = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new ToolException("ES_RESPONSE_INVALID", "Elasticsearch returned an empty response body for this request.", retriable: false, new { statusCode = "2xx" });
        }

        string sample = Compact(content.Trim());
        return new ToolException("ES_RESPONSE_INVALID", $"Elasticsearch response is not valid JSON{position}. Raw body: {sample}", retriable: false, new { statusCode = "2xx" });
    }

    /// <summary>Builds the review links attached to a tool error, so a failed query still yields a
    /// Kibana URL for manual investigation. Uses the management-style pattern links (no data-view read
    /// needed), mirroring what search_index emits on success.</summary>
    private static IReadOnlyList<string> ErrorReviewLinks(EnvironmentConfig? config, string indexTarget)
    {
        if (config is null || string.IsNullOrWhiteSpace(indexTarget))
        {
            return [];
        }

        return KibanaReviews.BuildIndexPatternLinks(config, indexTarget);
    }

    private static string Compact(string message) => message.Length <= 300 ? message : message[..300];
}
