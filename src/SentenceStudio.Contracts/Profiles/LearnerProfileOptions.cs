using System.Collections.ObjectModel;

namespace SentenceStudio.Contracts.Profiles;

/// <summary>
/// The one definition of the learner-profile choices: the languages, display languages,
/// session lengths and CEFR levels that the web Profile and Onboarding pages, native
/// onboarding and profile, and <c>PUT /api/v1/profile/me</c> offer and accept.
/// </summary>
/// <remarks>
/// A profile can already hold a value outside these lists (an older language name, a session
/// length such as 25, different casing). Saving must not force that value to change when the
/// learner edits something else, so <see cref="IsAcceptable(IReadOnlyList{string}, string?, string?)"/>
/// accepts a value that equals the stored one, and only a changed value must be a listed choice.
/// Values compare ordinally because stored language names feed language logic elsewhere.
/// </remarks>
public static class LearnerProfileOptions
{
    /// <summary>Native- and target-language choices in the web Profile page order.</summary>
    public static IReadOnlyList<string> Languages { get; } = new ReadOnlyCollection<string>(
        ["English", "Korean", "French", "German", "Spanish", "Japanese", "Chinese"]);

    /// <summary>The same languages, Korean first, for the onboarding target-language picker.</summary>
    public static IReadOnlyList<string> TargetLanguages { get; } = new ReadOnlyCollection<string>(
        ["Korean", .. Languages.Where(language => language != "Korean")]);

    /// <summary>App display languages.</summary>
    public static IReadOnlyList<string> DisplayLanguages { get; } =
        new ReadOnlyCollection<string>(["English", "Korean"]);

    /// <summary>Preferred session lengths, in minutes.</summary>
    public static IReadOnlyList<int> SessionMinutes { get; } =
        new ReadOnlyCollection<int>([5, 10, 15, 20, 30, 45]);

    /// <summary>Target CEFR levels.</summary>
    public static IReadOnlyList<string> CefrLevels { get; } =
        new ReadOnlyCollection<string>(["A1", "A2", "B1", "B2", "C1", "C2"]);

    /// <summary>
    /// True when <paramref name="submitted"/> is a listed choice, or is non-empty and equals the
    /// value already stored (an unchanged field is kept even when it is not listed).
    /// </summary>
    public static bool IsAcceptable(IReadOnlyList<string> choices, string? submitted, string? stored)
    {
        ArgumentNullException.ThrowIfNull(choices);
        if (string.IsNullOrWhiteSpace(submitted))
            return false;
        return choices.Contains(submitted, StringComparer.Ordinal)
            || string.Equals(submitted, stored, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when <paramref name="submitted"/> is a listed session length, or is positive and
    /// equals the stored length.
    /// </summary>
    public static bool IsAcceptableSessionMinutes(int submitted, int stored) =>
        submitted > 0 && (SessionMinutes.Contains(submitted) || submitted == stored);
}
