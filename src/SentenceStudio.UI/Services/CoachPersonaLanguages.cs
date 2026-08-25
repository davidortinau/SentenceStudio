using System.Globalization;

namespace SentenceStudio.WebUI.Services;

/// <summary>
/// Maps the language a learner is <em>studying</em> onto the culture that owns the coach's name.
/// </summary>
/// <remarks>
/// <para>
/// The coach is a person, and the market that person belongs to is the language being learned, not
/// the language the app chrome happens to be drawn in. A learner studying Korean with an English
/// interface is talking to <c>쌤</c>; that is who their teacher is. Deriving the name from the UI
/// culture alone got this backwards — it renamed the person whenever the reader changed the
/// interface language, which is a thing that never happens to a person.
/// </para>
/// <para>
/// Only the proper noun is keyed on the study language. Everything around it — "Ask {0}",
/// "Conversation with {0}" — still comes from the reader's own display culture, because that copy
/// is chrome and chrome follows the reader.
/// </para>
/// <para>
/// Extensibility is deliberately a resource concern rather than a code one: this maps a study
/// language onto a <see cref="CultureInfo"/>, and the name itself is then read from that culture's
/// <c>AppResources</c>. Giving a new market its own persona name means adding a satellite resource
/// file and one row here, not editing any component.
/// </para>
/// </remarks>
public static class CoachPersonaLanguages
{
    /// <summary>
    /// The culture whose resources name the coach when the study language is unknown, unmapped, or
    /// absent.
    /// </summary>
    /// <remarks>
    /// Invariant English rather than the reader's UI culture. A learner studying Spanish has no
    /// Spanish persona name yet, and answering with the Korean one because the interface is Korean
    /// would name their teacher after somebody else's subject.
    /// </remarks>
    public static readonly CultureInfo Fallback = new("en");

    /// <summary>
    /// Study language (however the profile spells it) to the culture that names the coach.
    /// </summary>
    /// <remarks>
    /// Profiles store a display name — "Korean" — while resources, headers and OS settings speak
    /// in tags. Both spellings are accepted because both reach this code: the profile field, and a
    /// tag arriving from a caller that already normalised.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> PersonaCultureByStudyLanguage =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["korean"] = "ko",
            ["ko"] = "ko",
            ["kor"] = "ko",
            ["한국어"] = "ko"
        };

    /// <summary>
    /// The culture that names the coach for a learner studying <paramref name="studyLanguage"/>.
    /// </summary>
    /// <param name="studyLanguage">
    /// The learner's primary target language, as stored on the profile ("Korean") or as a culture
    /// tag ("ko", "ko-KR"). A comma-separated list is read as "the first one", matching
    /// <c>UserProfile.TargetLanguagesList</c>, whose first entry is the primary language.
    /// </param>
    /// <returns>
    /// The mapped culture, or <see cref="Fallback"/> when the language is empty or has no persona
    /// of its own yet.
    /// </returns>
    public static CultureInfo ResolvePersonaCulture(string? studyLanguage)
    {
        var primary = PrimaryLanguage(studyLanguage);

        if (primary.Length == 0)
        {
            return Fallback;
        }

        if (PersonaCultureByStudyLanguage.TryGetValue(primary, out var exact))
        {
            return new CultureInfo(exact);
        }

        // "ko-KR" and "ko_KR" both name the same market as "ko". Try the language subtag before
        // giving up, so a regional profile is never demoted to the fallback name.
        var separator = primary.IndexOfAny(['-', '_']);
        if (separator > 0
            && PersonaCultureByStudyLanguage.TryGetValue(primary[..separator], out var subtag))
        {
            return new CultureInfo(subtag);
        }

        return Fallback;
    }

    /// <summary>
    /// True when this study language has a persona name of its own rather than the fallback.
    /// </summary>
    public static bool HasDedicatedPersona(string? studyLanguage) =>
        !string.Equals(
            ResolvePersonaCulture(studyLanguage).TwoLetterISOLanguageName,
            Fallback.TwoLetterISOLanguageName,
            StringComparison.OrdinalIgnoreCase);

    private static string PrimaryLanguage(string? studyLanguage)
    {
        if (string.IsNullOrWhiteSpace(studyLanguage))
        {
            return string.Empty;
        }

        var comma = studyLanguage.IndexOf(',');
        var primary = comma >= 0 ? studyLanguage[..comma] : studyLanguage;

        return primary.Trim();
    }
}
