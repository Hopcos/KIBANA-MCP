namespace KibanaMcp;

internal sealed record ResolvedLocalTimeZone(TimeZoneInfo TimeZoneInfo, string ElasticsearchId);

internal static class TimeZoneResolver
{
    private static readonly Dictionary<string, string> IanaToWindowsFallbacks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Asia/Shanghai"] = "China Standard Time",
        ["Etc/UTC"] = "UTC",
        ["UTC"] = "UTC"
    };

    private static readonly Dictionary<string, string> WindowsToIanaFallbacks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["China Standard Time"] = "Asia/Shanghai",
        ["UTC"] = "Etc/UTC"
    };

    public static ResolvedLocalTimeZone Resolve(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            throw new ToolException("INVALID_TIME_ZONE", "Time zone ID is required.");
        }

        TimeZoneInfo timeZone = FindById(timeZoneId);
        return new ResolvedLocalTimeZone(timeZone, ToElasticsearchId(timeZoneId));
    }

    public static TimeZoneInfo FindById(string timeZoneId)
    {
        if (TryFindSystemTimeZone(timeZoneId, out TimeZoneInfo? timeZone))
        {
            return timeZone!;
        }

        List<string> alternativeIds = AlternativeIds(timeZoneId);
        foreach (string alternativeId in alternativeIds)
        {
            if (TryFindSystemTimeZone(alternativeId, out timeZone))
            {
                return timeZone!;
            }
        }

        throw new ToolException("INVALID_TIME_ZONE", $"Time zone ID '{timeZoneId}' was not found on this computer.");
    }

    private static List<string> AlternativeIds(string timeZoneId)
    {
        List<string> alternativeIds = [];

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZoneId, out string? windowsId) && !string.IsNullOrWhiteSpace(windowsId))
        {
            alternativeIds.Add(windowsId);
        }

        if (IanaToWindowsFallbacks.TryGetValue(timeZoneId, out string? fallbackWindowsId))
        {
            alternativeIds.Add(fallbackWindowsId);
        }

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZoneId, out string? ianaId) && !string.IsNullOrWhiteSpace(ianaId))
        {
            alternativeIds.Add(ianaId);
        }

        if (WindowsToIanaFallbacks.TryGetValue(timeZoneId, out string? fallbackIanaId))
        {
            alternativeIds.Add(fallbackIanaId);
        }

        return alternativeIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ToElasticsearchId(string timeZoneId)
    {
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZoneId, out string? ianaId) && !string.IsNullOrWhiteSpace(ianaId))
        {
            return ianaId;
        }

        return WindowsToIanaFallbacks.TryGetValue(timeZoneId, out string? fallbackIanaId)
            ? fallbackIanaId
            : timeZoneId;
    }

    private static bool TryFindSystemTimeZone(string timeZoneId, out TimeZoneInfo? timeZone)
    {
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            timeZone = null;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            timeZone = null;
            return false;
        }
    }
}
