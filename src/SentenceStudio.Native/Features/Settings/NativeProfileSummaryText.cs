namespace SentenceStudio.Native.Features.Settings;

/// <summary>The Settings tab's profile row text, shared by the native heads.</summary>
internal static class NativeProfileSummaryText
{
    /// <summary>
    /// "Native → Target" with each language named in the UI language (the native heads'
    /// five supported names; any other is shown as stored). One language alone is shown
    /// alone; none gives null.
    /// </summary>
    internal static string? LanguagePair(string native, string target, Func<string, string> text)
    {
        string? Name(string language) => string.IsNullOrWhiteSpace(language) ? null
            : NativeHowDoYouSayText.LanguageKey(language) is { } key ? text(key) : language;
        return (Name(native), Name(target)) switch
        {
            ({ } from, { } to) => $"{from} → {to}",
            ({ } only, null) => only,
            (null, { } only) => only,
            _ => null
        };
    }
}
