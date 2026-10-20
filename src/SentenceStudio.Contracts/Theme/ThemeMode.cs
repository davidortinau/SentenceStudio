namespace SentenceStudio.Contracts.Theme;

/// <summary>
/// The saved appearance choice: a fixed light or dark override, or the live system appearance.
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately <b>no <c>Unknown</c> member</b>. <c>Unknown</c> exists on wire enums so a
/// newer peer can send a value an older peer has never heard of without the deserializer throwing.
/// Nothing sends <see cref="ThemeMode"/> over a wire today: it lives in a browser cookie and in
/// device preferences, both of which are parsed through <see cref="TryParse"/>, which rejects
/// unrecognized input rather than admitting it as a sentinel. Adding <c>Unknown</c> now would mean
/// every <c>switch</c> in the UI has to answer "what colour is Unknown?" for a case that cannot
/// occur. <see cref="System"/> is a user choice, not an unknown value; each presentation host
/// resolves it against its own OS appearance. When an appearance action DTO is actually added to
/// the Coach wire protocol, introduce a separate wire-shaped enum at that boundary.
/// </para>
/// </remarks>
public enum ThemeMode
{
    Light = 0,
    Dark = 1,
    System = 2
}

/// <summary>
/// Conversions between <see cref="ThemeMode"/> and the lowercase token used by
/// the appearance cookie and device preferences. The effective <c>data-bs-theme</c> is always
/// light or dark, never system.
/// </summary>
public static class ThemeModeExtensions
{
    public const string LightToken = "light";
    public const string DarkToken = "dark";
    public const string SystemToken = "system";

    /// <summary>The saved choice's token, which must be resolved before writing to Bootstrap.</summary>
    public static string ToToken(this ThemeMode mode) => mode switch
    {
        ThemeMode.Light => LightToken,
        ThemeMode.Dark => DarkToken,
        ThemeMode.System => SystemToken,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unrecognized theme mode.")
    };

    /// <summary>Resolves the saved choice to an effective light/dark mode for an OS appearance.</summary>
    public static ThemeMode ResolveEffective(this ThemeMode mode, bool systemIsDark) => mode switch
    {
        ThemeMode.Light => ThemeMode.Light,
        ThemeMode.Dark => ThemeMode.Dark,
        ThemeMode.System => systemIsDark ? ThemeMode.Dark : ThemeMode.Light,
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

        if (string.Equals(token, SystemToken, StringComparison.OrdinalIgnoreCase))
        {
            mode = ThemeMode.System;
            return true;
        }

        mode = default;
        return false;
    }
}
