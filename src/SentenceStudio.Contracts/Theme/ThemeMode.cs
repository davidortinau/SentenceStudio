namespace SentenceStudio.Contracts.Theme;

/// <summary>
/// The light/dark presentation mode. A closed set — there is no third value.
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately <b>no <c>Unknown</c> member</b>. <c>Unknown</c> exists on wire enums so a
/// newer peer can send a value an older peer has never heard of without the deserializer throwing.
/// Nothing sends <see cref="ThemeMode"/> over a wire today: it lives in a browser cookie and in
/// device preferences, both of which are parsed through <see cref="TryParse"/>, which rejects
/// unrecognized input rather than admitting it as a sentinel. Adding <c>Unknown</c> now would mean
/// every <c>switch</c> in the UI has to answer "what colour is Unknown?" for a case that cannot
/// occur. When an appearance action DTO is actually added to the Coach wire protocol, that is the
/// moment to introduce a wire-shaped enum with <c>Unknown</c> — as a separate type, mapped at the
/// boundary.
/// </para>
/// </remarks>
public enum ThemeMode
{
    Light,
    Dark
}

/// <summary>
/// Conversions between <see cref="ThemeMode"/> and the lowercase token used by
/// <c>data-bs-theme</c>, the appearance cookie, and device preferences.
/// </summary>
public static class ThemeModeExtensions
{
    public const string LightToken = "light";
    public const string DarkToken = "dark";

    /// <summary>The token written to <c>data-bs-theme</c> and to the persistence substrate.</summary>
    public static string ToToken(this ThemeMode mode) => mode switch
    {
        ThemeMode.Light => LightToken,
        ThemeMode.Dark => DarkToken,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unrecognized theme mode.")
    };

    /// <summary>
    /// Parses a mode token from untrusted input (cookie, preference file, query string).
    /// Returns <see langword="false"/> for anything outside the closed set — callers fall back to a
    /// default rather than receiving a coerced value.
    /// </summary>
    public static bool TryParse(string? token, out ThemeMode mode)
    {
        if (string.Equals(token, LightToken, StringComparison.OrdinalIgnoreCase))
        {
            mode = ThemeMode.Light;
            return true;
        }

        if (string.Equals(token, DarkToken, StringComparison.OrdinalIgnoreCase))
        {
            mode = ThemeMode.Dark;
            return true;
        }

        mode = default;
        return false;
    }
}
