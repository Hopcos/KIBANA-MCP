using System.Text.Json.Nodes;

namespace KibanaMcp;

/// <summary>
/// Resolves the Kibana data-view reference (the id Kibana uses in Discover links) for a logical index
/// pattern. Data-view saved objects live in the <c>.kibana*</c> system indices; when the Elasticsearch
/// user can read them, the catalog is searched through the same console proxy as the main query, so
/// discovery always reflects the current state of the cluster ("live per call" — callers fire this in
/// parallel with the main query, so there is no added latency).
///
/// Falls back to <c>null</c> on any failure — for example a Kibana console proxy security mode that
/// blocks access to system indices — in which case callers simply omit review links.
/// </summary>
public sealed class KibanaDataViewResolver(KibanaRestClient elastic)
{
    public async Task<string?> ResolveAsync(EnvironmentConfig config, string indexTarget, CancellationToken cancellationToken = default)
    {
        string? pattern = KibanaReviews.FirstIncludePattern(indexTarget);
        if (pattern is null)
        {
            return null;
        }

        JsonArray? hits = await SearchDataViewsAsync(config, cancellationToken).ConfigureAwait(false);
        if (hits is null)
        {
            return null;
        }

        foreach (JsonNode? hit in hits)
        {
            if (hit is not JsonObject hitObject || hitObject["_id"] is not JsonValue idValue)
            {
                continue;
            }

            string? title = ReadTitle(hitObject);
            if (string.Equals(title, pattern, StringComparison.Ordinal) && StripTypePrefix(idValue.GetValue<string>()) is { Length: > 0 } dataViewId)
            {
                return dataViewId;
            }
        }

        return null;
    }

    /// <summary>Reads the data-view title from either the newer data-view or legacy index-pattern saved object.</summary>
    private static string? ReadTitle(JsonObject savedObject)
    {
        return savedObject["_source"]?["index-pattern"]?["title"]?.GetValue<string>()
            ?? savedObject["_source"]?["data-view"]?["title"]?.GetValue<string>();
    }

    /// <summary>Saved-object ids are stored as "index-pattern:&lt;uuid&gt;" (or "data-view:..."); Discover links need only the trailing id.</summary>
    private static string StripTypePrefix(string id)
    {
        int colon = id.IndexOf(':');
        return colon >= 0 ? id[(colon + 1)..] : id;
    }

    private async Task<JsonArray?> SearchDataViewsAsync(EnvironmentConfig config, CancellationToken cancellationToken)
    {
        try
        {
            string path = ".kibana*/_search?expand_wildcards=all,hidden&ignore_unavailable=true";
            Dictionary<string, object?> body = new()
            {
                ["size"] = 2000,
                ["query"] = new Dictionary<string, object?>
                {
                    ["bool"] = new Dictionary<string, object?>
                    {
                        ["filter"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["terms"] = new Dictionary<string, object?>
                                {
                                    ["type"] = new[] { "index-pattern", "data-view" }
                                }
                            }
                        }
                    }
                },
                ["_source"] = new[] { "index-pattern.title", "data-view.title" }
            };

            ElasticResponse response = await elastic.PostAsync(config, path, body, cancellationToken).ConfigureAwait(false);
            return JsonNode.Parse(response.Content)?["hits"]?["hits"] as JsonArray;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
