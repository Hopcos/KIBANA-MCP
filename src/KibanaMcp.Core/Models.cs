using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace KibanaMcp;

/// <summary>
/// An environment entry from the <c>Environments</c> configuration section, plus the global defaults
/// the server resolves per tool call. <see cref="KibanaBaseUrl"/> and <see cref="KibanaVersion"/> are
/// used (per-env override falling back to global defaults); the direct Elasticsearch base URL is
/// intentionally not part of the Kibana-only configuration model.
/// </summary>
public sealed record EnvironmentConfig(
    string Env,
    string KibanaBaseUrl,
    string? Username,
    string? Password,
    string DefaultTimeZone,
    int RequestTimeoutMs,
    string? KibanaVersion = null,
    string? ProxyApiVersion = null,
    string? SessionCookie = null);

public sealed class CountLogsInput
{
    public string? Env { get; set; }
    public required string Index { get; set; }
    public TimeRangeInput? TimeRange { get; set; }
    public string? Query { get; set; }
}

public sealed class AggregateLogsInput
{
    public string? Env { get; set; }
    public required string Index { get; set; }
    public TimeRangeInput? TimeRange { get; set; }
    public string? Query { get; set; }
    public List<AggregateGroupInput>? Groups { get; set; }
    public List<MetricInput>? Metrics { get; set; }
}

public sealed class SearchIndexInput
{
    public string? Env { get; set; }
    public string? Pattern { get; set; }
    public int? Limit { get; set; }
}

[JsonConverter(typeof(TimeRangeInputJsonConverter))]
public abstract class TimeRangeInput
{
    public string? TimeZone { get; init; }
}

public sealed class PresetTimeRangeInput : TimeRangeInput
{
    public required string Preset { get; init; }
}

public sealed class CustomTimeRangeInput : TimeRangeInput
{
    public string? Gt { get; init; }
    public string? Gte { get; init; }
    public string? Lt { get; init; }
    public string? Lte { get; init; }
}

[JsonConverter(typeof(AggregateGroupInputJsonConverter))]
public abstract class AggregateGroupInput
{
    public required string Type { get; set; }
}

public sealed class AggregateTermsGroup : AggregateGroupInput
{
    public required string Field { get; set; }
    public int? Size { get; set; }
    public string? OrderBy { get; set; }
    public string? Order { get; set; }
}

public sealed class AggregateDateHistogramGroup : AggregateGroupInput
{
    public string? Field { get; set; }
    public required string Interval { get; set; }
    public bool? IncludeEmptyBuckets { get; set; }
}

public sealed class TimeSeriesInput
{
    public string? Env { get; set; }
    public required string Index { get; set; }
    public TimeRangeInput? TimeRange { get; set; }
    public required string Interval { get; set; }
    public string? Query { get; set; }
    public TimeSeriesSplitBy? SplitBy { get; set; }
    public MetricInput? Metric { get; set; }
    public bool? IncludeEmptyBuckets { get; set; }
}

public sealed class CompareWindowsInput
{
    public string? Env { get; set; }
    public required string Index { get; set; }
    public TimeRangeInput? Current { get; set; }
    public TimeRangeInput? Baseline { get; set; }
    public string? Query { get; set; }
    public string? GroupBy { get; set; }
    public int? Size { get; set; }
    public MetricInput? Metric { get; set; }
    public int? MinCount { get; set; }
    public bool? IncludeMissingBaseline { get; set; }
}

public sealed class TimeSeriesSplitBy
{
    public required string Field { get; set; }
    public int? Size { get; set; }
}

public sealed class SearchSamplesInput
{
    public string? Env { get; set; }
    public required string Index { get; set; }
    public TimeRangeInput? TimeRange { get; set; }
    public string? Query { get; set; }
    public List<string>? SourceFields { get; set; }
    public int? Size { get; set; }
    public List<SortInput>? Sort { get; set; }
    public bool? TrackTotalHits { get; set; }
    public List<RawEsValue>? SearchAfter { get; set; }
}

public sealed class SortInput
{
    public required string Field { get; set; }
    public required string Order { get; set; }
}

public sealed class ExportRawEsResponseInput
{
    public string? Env { get; set; }
    public string? Method { get; set; }
    public required string Index { get; set; }
    public Dictionary<string, RawEsValue>? Body { get; set; }
    public Dictionary<string, RawEsValue>? QueryString { get; set; }
}

public sealed class DiscoverFieldsInput
{
    public string? Env { get; set; }
    public required string Index { get; set; }
    public string? FieldPattern { get; set; }
    public List<string>? Prefixes { get; set; }
    public bool? OnlyAggregatable { get; set; }
    public bool? OnlySearchable { get; set; }
    public List<string>? Types { get; set; }
    public bool? IncludeUnconfirmedFields { get; set; }
    public int? Limit { get; set; }
}

public sealed class MetricInput
{
    public required string Type { get; set; }
    public string? Name { get; set; }
    public string? Field { get; set; }
    public List<double>? Percents { get; set; }
}

public sealed class ResolvedTimeRange
{
    public required string Input { get; init; }
    public string? Gt { get; init; }
    public string? Gte { get; init; }
    public string? Lt { get; init; }
    public string? Lte { get; init; }
    internal string TimeZone { get; init; } = string.Empty;
}

public sealed class ToolException(string code, string message, bool retriable = false, object? details = null) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retriable { get; } = retriable;
    public object? Details { get; } = details;
}

public sealed class ElasticResponse(int statusCode, string content, string? contentType)
{
    public int StatusCode { get; } = statusCode;
    public string Content { get; } = content;
    public string? ContentType { get; } = contentType;
}

internal sealed class AggregateGroupInputJsonConverter : JsonConverter<AggregateGroupInput>
{
    public override AggregateGroupInput? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        JsonObject obj = JsonNode.Parse(ref reader)!.AsObject();
        if (obj["type"] is not JsonNode typeNode)
        {
            if (obj["field"] is not JsonNode)
            {
                throw new JsonException("Aggregate group requires type.");
            }

            obj["type"] = "terms";
            typeNode = obj["type"]!;
        }

        string type = typeNode.GetValue<string>();
        return type switch
        {
            "terms" => obj.Deserialize<AggregateTermsGroup>(options),
            "date_histogram" => obj.Deserialize<AggregateDateHistogramGroup>(options),
            _ => throw new JsonException($"Unsupported aggregate group type '{type}'.")
        };
    }

    public override void Write(Utf8JsonWriter writer, AggregateGroupInput value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, (object)value, options);
    }
}

internal sealed class TimeRangeInputJsonConverter : JsonConverter<TimeRangeInput>
{
    public override TimeRangeInput? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return new PresetTimeRangeInput { Preset = reader.GetString() ?? string.Empty };
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("timeRange must be a preset string or object.");
        }

        string? preset = null;
        string? gt = null;
        string? gte = null;
        string? lt = null;
        string? lte = null;
        string? timeZone = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (preset is not null)
                {
                    return new PresetTimeRangeInput { Preset = preset, TimeZone = timeZone };
                }

                return new CustomTimeRangeInput { Gt = gt, Gte = gte, Lt = lt, Lte = lte, TimeZone = timeZone };
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Invalid timeRange object.");
            }

            var propertyName = reader.GetString();
            reader.Read();
            var value = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
            switch (propertyName)
            {
                case "preset":
                    preset = value;
                    break;
                case "gt":
                    gt = value;
                    break;
                case "gte":
                    gte = value;
                    break;
                case "lt":
                    lt = value;
                    break;
                case "lte":
                    lte = value;
                    break;
                case "timeZone":
                    timeZone = value;
                    break;
                default:
                    throw new JsonException($"Unsupported timeRange property '{propertyName}'.");
            }
        }

        throw new JsonException("Invalid timeRange object.");
    }

    public override void Write(Utf8JsonWriter writer, TimeRangeInput value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case PresetTimeRangeInput preset:
                writer.WriteString("preset", preset.Preset);
                break;
            case CustomTimeRangeInput custom:
                WriteStringIfNotNull(writer, "gt", custom.Gt);
                WriteStringIfNotNull(writer, "gte", custom.Gte);
                WriteStringIfNotNull(writer, "lt", custom.Lt);
                WriteStringIfNotNull(writer, "lte", custom.Lte);
                break;
        }

        WriteStringIfNotNull(writer, "timeZone", value.TimeZone);
        writer.WriteEndObject();
    }

    private static void WriteStringIfNotNull(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }
}

[JsonConverter(typeof(RawEsValueJsonConverter))]
public sealed class RawEsValue
{
    private RawEsValue(object? value) => Value = value;

    public object? Value { get; }

    public static RawEsValue FromString(string? value) => new(value);

    public static RawEsValue FromNumber(decimal value) => new(value);

    public static RawEsValue FromBoolean(bool value) => new(value);

    public static RawEsValue FromArray(List<RawEsValue?> value) => new(value);

    public static RawEsValue FromObject(Dictionary<string, RawEsValue?> value) => new(value);

    public static RawEsValue Null { get; } = new(null);

    public object? ToPlainObject()
    {
        return Value switch
        {
            Dictionary<string, RawEsValue?> obj => obj.ToDictionary(kv => kv.Key, kv => kv.Value?.ToPlainObject()),
            List<RawEsValue?> arr => arr.Select(item => item?.ToPlainObject()).ToArray(),
            _ => Value
        };
    }

    public string ToQueryStringValue()
    {
        return Value switch
        {
            null => string.Empty,
            string value => value,
            bool value => value ? "true" : "false",
            decimal value => value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => JsonSerializer.Serialize(ToPlainObject(), JsonDefaults.Options)
        };
    }
}

internal sealed class RawEsValueJsonConverter : JsonConverter<RawEsValue>
{
    public override RawEsValue? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => RawEsValue.Null,
            JsonTokenType.String => RawEsValue.FromString(reader.GetString()),
            JsonTokenType.Number => RawEsValue.FromNumber(reader.GetDecimal()),
            JsonTokenType.True => RawEsValue.FromBoolean(true),
            JsonTokenType.False => RawEsValue.FromBoolean(false),
            JsonTokenType.StartArray => ReadArray(ref reader, options),
            JsonTokenType.StartObject => ReadObject(ref reader, options),
            _ => throw new JsonException($"Unsupported raw Elasticsearch value token '{reader.TokenType}'.")
        };
    }

    public override void Write(Utf8JsonWriter writer, RawEsValue value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value.ToPlainObject(), JsonDefaults.Options);
    }

    private static RawEsValue ReadArray(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        List<RawEsValue?> values = [];

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return RawEsValue.FromArray(values);
            }

            values.Add(JsonSerializer.Deserialize<RawEsValue>(ref reader, options));
        }

        throw new JsonException("Invalid array value.");
    }

    private static RawEsValue ReadObject(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        Dictionary<string, RawEsValue?> values = new(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return RawEsValue.FromObject(values);
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Invalid object value.");
            }

            var name = reader.GetString() ?? string.Empty;
            reader.Read();
            values[name] = JsonSerializer.Deserialize<RawEsValue>(ref reader, options);
        }

        throw new JsonException("Invalid object value.");
    }
}
