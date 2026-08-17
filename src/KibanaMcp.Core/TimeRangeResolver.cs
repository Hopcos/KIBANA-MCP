using System.Globalization;
using System.Text.RegularExpressions;

namespace KibanaMcp;

public static partial class TimeRangeResolver
{
    public static ResolvedTimeRange Resolve(TimeRangeInput? input, EnvironmentConfig config, TimeProvider timeProvider)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (input is null)
        {
            throw new ToolException("INVALID_TIME_RANGE", "timeRange is required.");
        }

        if (input is PresetTimeRangeInput preset)
        {
            return ResolvePreset(preset.Preset, preset.TimeZone, config, now);
        }

        if (input is not CustomTimeRangeInput custom)
        {
            throw new ToolException("INVALID_TIME_RANGE", "timeRange must be a preset string or object.");
        }

        if (HasAny(custom.Gt, custom.Gte, custom.Lt, custom.Lte) is false)
        {
            throw new ToolException("INVALID_TIME_RANGE", "custom timeRange requires at least one boundary.");
        }

        string customTimeZoneId = custom.TimeZone ?? config.DefaultTimeZone;
        ResolvedLocalTimeZone customTimeZone = TimeZoneResolver.Resolve(customTimeZoneId);
        return new ResolvedTimeRange
        {
            Input = "custom",
            Gt = NormalizeBoundary(custom.Gt, customTimeZone.TimeZoneInfo),
            Gte = NormalizeBoundary(custom.Gte, customTimeZone.TimeZoneInfo),
            Lt = NormalizeBoundary(custom.Lt, customTimeZone.TimeZoneInfo),
            Lte = NormalizeBoundary(custom.Lte, customTimeZone.TimeZoneInfo),
            TimeZone = customTimeZone.ElasticsearchId
        };
    }

    private static ResolvedTimeRange ResolvePreset(string preset, string? requestedTimeZone, EnvironmentConfig config, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(preset) || !PresetRegex().IsMatch(preset))
        {
            throw new ToolException("INVALID_TIME_RANGE", $"Invalid timeRange preset '{preset}'.");
        }

        string timeZoneId = requestedTimeZone ?? config.DefaultTimeZone;
        ResolvedLocalTimeZone timeZone = TimeZoneResolver.Resolve(timeZoneId);
        DateTimeOffset nowLocal = TimeZoneInfo.ConvertTime(nowUtc, timeZone.TimeZoneInfo);
        DateTimeOffset todayStartLocal = new(nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, nowLocal.Offset);

        DateTimeOffset startLocal;
        DateTimeOffset endLocal;
        var upperIsExclusive = false;

        if (preset == "today")
        {
            startLocal = todayStartLocal;
            endLocal = nowLocal;
        }
        else if (preset == "yesterday")
        {
            startLocal = todayStartLocal.AddDays(-1);
            endLocal = todayStartLocal;
            upperIsExclusive = true;
        }
        else if (preset == "yesterday_same_window")
        {
            startLocal = todayStartLocal.AddDays(-1);
            endLocal = nowLocal.AddDays(-1);
        }
        else
        {
            Match match = RollingPresetRegex().Match(preset);
            var n = int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
            var unit = match.Groups["unit"].Value;
            if (unit == "full_days")
            {
                startLocal = todayStartLocal.AddDays(-n);
                endLocal = todayStartLocal;
                upperIsExclusive = true;
            }
            else if (unit == "full_hours")
            {
                // Last completed n-hour block aligned to local hour boundaries, e.g. "last_1_full_hours"
                // at 16:54 -> [15:00, 16:00).
                DateTimeOffset hourStartLocal = nowLocal.AddTicks(-(nowLocal.Ticks % TimeSpan.TicksPerHour));
                startLocal = hourStartLocal.AddHours(-n);
                endLocal = hourStartLocal;
                upperIsExclusive = true;
            }
            else
            {
                endLocal = nowLocal;
                startLocal = unit switch
                {
                    "minutes" => nowLocal.AddMinutes(-n),
                    "hours" => nowLocal.AddHours(-n),
                    "days" => nowLocal.AddDays(-n),
                    _ => throw new ToolException("INVALID_TIME_RANGE", $"Invalid timeRange preset '{preset}'.")
                };
            }
        }

        return new ResolvedTimeRange
        {
            Input = preset,
            Gte = ToIso(startLocal),
            Lt = upperIsExclusive ? ToIso(endLocal) : null,
            Lte = upperIsExclusive ? null : ToIso(endLocal),
            TimeZone = timeZone.ElasticsearchId
        };
    }

    public static string ToIso(DateTimeOffset value)
    {
        return value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
    }

    public static string ToIso(DateTimeOffset value, TimeZoneInfo timeZone)
    {
        return ToIso(TimeZoneInfo.ConvertTime(value, timeZone));
    }

    private static string? NormalizeBoundary(string? value, TimeZoneInfo timeZone)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
            ? ToIso(parsed, timeZone)
            : value;
    }

    private static bool HasAny(params string?[] values) => values.Any(value => !string.IsNullOrWhiteSpace(value));

    [GeneratedRegex("^(today|yesterday|yesterday_same_window|last_[1-9][0-9]*_(minutes|hours|days|full_days|full_hours))$")]
    private static partial Regex PresetRegex();

    [GeneratedRegex("^last_(?<n>[1-9][0-9]*)_(?<unit>minutes|hours|days|full_days|full_hours)$")]
    private static partial Regex RollingPresetRegex();
}
