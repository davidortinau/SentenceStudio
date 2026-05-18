namespace SentenceStudio.Services.Plans;

/// <summary>
/// Helpers for converting between Windows / IANA timezone identifiers and
/// resolving a robust <see cref="TimeZoneInfo"/> from a string.
/// </summary>
public static class TimeZoneResolver
{
    /// <summary>
    /// Attempts to resolve a timezone id (IANA preferred, Windows accepted)
    /// into a <see cref="TimeZoneInfo"/>. Falls back to
    /// <see cref="TimeZoneInfo.Utc"/> when the id is null, empty, or unknown.
    /// </summary>
    /// <returns><c>true</c> when the id matched; <c>false</c> when UTC fallback was used.</returns>
    public static bool TryResolve(string? id, out TimeZoneInfo resolved)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            resolved = TimeZoneInfo.Utc;
            return false;
        }

        try
        {
            resolved = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }

        // Windows ↔ IANA cross-mapping (works in both directions on .NET 8+).
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana))
        {
            try
            {
                resolved = TimeZoneInfo.FindSystemTimeZoneById(iana);
                return true;
            }
            catch { }
        }

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windows))
        {
            try
            {
                resolved = TimeZoneInfo.FindSystemTimeZoneById(windows);
                return true;
            }
            catch { }
        }

        resolved = TimeZoneInfo.Utc;
        return false;
    }
}
