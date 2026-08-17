using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace KibanaMcp;

public static class KibanaMcpToolSchema
{
    public static void ApplyEnvironmentSchema(Tool tool, KibanaEnvironmentProvider? environments)
    {
        var schema = JsonNode.Parse(tool.InputSchema.GetRawText())?.AsObject();
        if (schema is null)
        {
            return;
        }

        StrengthenEnvironmentProperty(schema, environments);
        tool.InputSchema = JsonSerializer.Deserialize<JsonElement>(schema.ToJsonString(), JsonDefaults.McpWireOptions);
    }

    private static void StrengthenEnvironmentProperty(JsonObject schema, KibanaEnvironmentProvider? environments)
    {
        IReadOnlyList<string> names = environments?.GetEnvironmentNames() ?? [];
        if (names.Count == 0 ||
            schema["properties"] is not JsonObject properties ||
            properties["env"] is not JsonObject env)
        {
            return;
        }

        string? description = env.TryGetPropertyValue("description", out JsonNode? descriptionNode) &&
            descriptionNode is not null
            ? descriptionNode.GetValue<string>()
            : null;

        properties["env"] = new JsonObject
        {
            ["description"] = description,
            ["oneOf"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(names.Select(name => (JsonNode)name).ToArray())
                },
                new JsonObject
                {
                    ["type"] = "null"
                }
            },
            ["default"] = null
        };
    }
}
