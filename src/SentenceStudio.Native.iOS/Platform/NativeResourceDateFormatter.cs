using System.Globalization;

namespace SentenceStudio.Native.iOS.Platform;

internal static class NativeResourceDateFormatter
{
    internal static string ListDate(DateTime createdAt, bool isSmartResource,
        TimeZoneInfo? deviceTimeZone = null, CultureInfo? culture = null) =>
        Format(createdAt, "d", isSmartResource, deviceTimeZone ?? TimeZoneInfo.Local,
            culture ?? CultureInfo.CurrentUICulture);

    internal static string DetailCreatedDate(DateTime createdAt, bool isSmartResource,
        TimeZoneInfo? deviceTimeZone = null, CultureInfo? culture = null) =>
        Format(createdAt, "g", isSmartResource, deviceTimeZone ?? TimeZoneInfo.Local,
            culture ?? CultureInfo.CurrentUICulture);

    internal static string DetailUpdatedDate(DateTime updatedAt,
        TimeZoneInfo? deviceTimeZone = null, CultureInfo? culture = null) =>
        Format(updatedAt, "g", false, deviceTimeZone ?? TimeZoneInfo.Local,
            culture ?? CultureInfo.CurrentUICulture);

    private static string Format(DateTime timestamp, string format,
        bool isSmartCreatedAt, TimeZoneInfo deviceTimeZone, CultureInfo culture)
    {
        // SmartResourceService writes CreatedAt with DateTime.Now; SaveResourceAsync
        // preserves it but overwrites UpdatedAt with UTC. SQLite loses Kind, so
        // smart CreatedAt must stay local until old and new rows can be distinguished.
        var local = timestamp.Kind switch
        {
            DateTimeKind.Local => timestamp,
            DateTimeKind.Unspecified when isSmartCreatedAt => timestamp,
            DateTimeKind.Utc or DateTimeKind.Unspecified =>
                TimeZoneInfo.ConvertTimeFromUtc(timestamp, deviceTimeZone),
            _ => throw new ArgumentOutOfRangeException(nameof(timestamp))
        };
        return local.ToString(format, culture);
    }
}
