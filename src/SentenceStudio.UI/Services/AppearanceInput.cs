using System.Globalization;
using SentenceStudio.Contracts.Theme;

namespace SentenceStudio.WebUI.Services;

/// <summary>
/// Turns the raw string a browser sends for an appearance control into a value the appearance
/// service will accept — or into a refusal.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the boundary is genuinely hostile in a boring way. <c>ChangeEventArgs.Value</c>
/// is a string that came from the DOM, and <see cref="double.TryParse"/> with
/// <see cref="NumberStyles.Any"/> happily returns <see langword="true"/> for <c>"NaN"</c> and
/// <c>"Infinity"</c>. <see cref="Math.Clamp(double, double, double)"/> passes NaN straight through
/// rather than clamping it, so a naive parse-then-clamp hands NaN to the setter, which throws —
/// inside a Blazor event handler, which ends the learner's circuit over a slider.
/// </para>
/// <para>
/// Pulled out of the settings page rather than left inline so the refusal can be tested directly,
/// instead of a test asserting that a page's source contains a guard.
/// </para>
/// </remarks>
public static class AppearanceInput
{
    /// <summary>
    /// Parses a text-size value from the browser, clamping in-range and refusing anything that is
    /// not a finite number.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the input is missing, unparseable, or non-finite. Callers do
    /// nothing in that case: the slider keeps its previous position and nothing is persisted.
    /// </returns>
    public static bool TryParseFontScale(string? raw, out double fontScale)
    {
        fontScale = default;

        if (!double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        // Before the clamp, not by it — Math.Clamp(NaN, min, max) is NaN.
        if (!double.IsFinite(parsed))
        {
            return false;
        }

        fontScale = AppearanceSelection.IsValidFontScale(parsed)
            ? parsed
            : Math.Clamp(parsed, AppearanceSelection.MinFontScale, AppearanceSelection.MaxFontScale);

        return true;
    }
}
