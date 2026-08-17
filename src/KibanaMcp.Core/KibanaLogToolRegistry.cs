using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace KibanaMcp;

public static class KibanaLogToolRegistry
{
    private const string TimeRangePresetPattern = "^(today|yesterday|yesterday_same_window|last_[1-9][0-9]*(_minutes|_hours|_days|_full_days|_full_hours))$";

    public static IMcpServerBuilder WithKibanaLogTools(this IMcpServerBuilder builder)
    {
        foreach (MethodInfo method in ToolMethods())
        {
            builder.Services.AddSingleton(services =>
            {
                KibanaEnvironmentProvider? environments = services.GetService<KibanaEnvironmentProvider>();
                McpServerTool tool = McpServerTool.Create(method, target: null, CreateToolOptions(services));
                KibanaMcpToolSchema.ApplyEnvironmentSchema(tool.ProtocolTool, environments);
                return tool;
            });
        }

        return builder;
    }

    public static McpServerToolCreateOptions CreateToolOptions(IServiceProvider services)
    {
        return new McpServerToolCreateOptions
        {
            Services = services,
            SerializerOptions = JsonDefaults.Options,
            SchemaCreateOptions = CreateSchemaOptions()
        };
    }

    public static IEnumerable<MethodInfo> ToolMethods()
    {
        return typeof(KibanaLogTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null);
    }

    public static AIJsonSchemaCreateOptions CreateSchemaOptions()
    {
        return new AIJsonSchemaCreateOptions
        {
            TransformSchemaNode = (context, node) =>
            {
                if (context.TypeInfo.Type == typeof(TimeRangeInput))
                {
                    return PreserveDescription(node, CreateTimeRangeSchema());
                }

                if (context.TypeInfo.Type == typeof(RawEsValue))
                {
                    return PreserveDescription(node, CreateRawEsValueSchema());
                }

                return node;
            }
        };
    }

    private static JsonObject CreateTimeRangeSchema()
    {
        return new JsonObject
        {
            ["oneOf"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "string",
                    ["pattern"] = TimeRangePresetPattern
                },
                new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray((JsonNode)"preset"),
                    ["properties"] = new JsonObject
                    {
                        ["preset"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["pattern"] = TimeRangePresetPattern
                        },
                        ["timeZone"] = new JsonObject
                        {
                            ["type"] = "string"
                        }
                    }
                },
                new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["anyOf"] = new JsonArray
                    {
                        RequiredOnly("gt"),
                        RequiredOnly("gte"),
                        RequiredOnly("lt"),
                        RequiredOnly("lte")
                    },
                    ["properties"] = new JsonObject
                    {
                        ["gt"] = new JsonObject { ["type"] = "string" },
                        ["gte"] = new JsonObject { ["type"] = "string" },
                        ["lt"] = new JsonObject { ["type"] = "string" },
                        ["lte"] = new JsonObject { ["type"] = "string" },
                        ["timeZone"] = new JsonObject { ["type"] = "string" }
                    }
                }
            }
        };
    }

    private static JsonObject CreateRawEsValueSchema()
    {
        return new JsonObject
        {
            ["oneOf"] = new JsonArray
            {
                new JsonObject { ["type"] = "string" },
                new JsonObject { ["type"] = "number" },
                new JsonObject { ["type"] = "boolean" },
                new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = true
                },
                new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = (JsonNode)true
                },
                new JsonObject { ["type"] = "null" }
            }
        };
    }

    private static JsonObject RequiredOnly(string propertyName)
    {
        return new JsonObject
        {
            ["required"] = new JsonArray((JsonNode)propertyName)
        };
    }

    private static JsonNode PreserveDescription(JsonNode original, JsonObject replacement)
    {
        if (original is JsonObject originalObject &&
            originalObject.TryGetPropertyValue("description", out JsonNode? description) &&
            description is not null)
        {
            replacement["description"] = description.DeepClone();
        }

        return replacement;
    }
}
