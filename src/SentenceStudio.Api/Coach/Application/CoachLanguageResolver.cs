using Microsoft.EntityFrameworkCore;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Data;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>The learner's languages as BCP-47 tags, resolved by the server.</summary>
public sealed record CoachLanguageProfile(string TargetLanguageTag, string DisplayLanguageTag, string NativeLanguageTag)
{
    /// <summary>The tag for one language role.</summary>
    public string Tag(CoachLanguageRole role) => role switch
    {
        CoachLanguageRole.Target => TargetLanguageTag,
        CoachLanguageRole.Native => NativeLanguageTag,
        _ => DisplayLanguageTag
    };

    /// <summary>The fallback used when a learner has no usable profile row.</summary>
    public static CoachLanguageProfile Default { get; } = new("en", "en", "en");
}

/// <summary>Resolves the learner's language tags for an answer.</summary>
public interface ICoachLanguageResolver
{
    Task<CoachLanguageProfile> ResolveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Maps the learner's stored language names onto BCP-47 tags.
/// </summary>
/// <remarks>
/// <para>
/// The model never names a locale. It says which <see cref="CoachLanguageRole"/> a run of text
/// plays, and this turns that into the tag the client uses to choose a script, a font, and a
/// speech voice. An arbitrary model-supplied string in that position would be an unvalidated
/// value driving rendering and text-to-speech.
/// </para>
/// <para>
/// Korean resolves to <c>ko-KR</c> specifically, because the coach's Korean guidance is standard
/// South Korean neutral-polite usage and the voice selection should match it.
/// </para>
/// </remarks>
public sealed class CoachLanguageResolver : ICoachLanguageResolver
{
    private static readonly Dictionary<string, string> Tags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["korean"] = "ko-KR",
        ["ko"] = "ko-KR",
        ["english"] = "en-US",
        ["en"] = "en-US",
        ["japanese"] = "ja-JP",
        ["ja"] = "ja-JP",
        ["chinese"] = "zh-CN",
        ["mandarin"] = "zh-CN",
        ["zh"] = "zh-CN",
        ["spanish"] = "es-ES",
        ["es"] = "es-ES",
        ["french"] = "fr-FR",
        ["fr"] = "fr-FR",
        ["german"] = "de-DE",
        ["de"] = "de-DE",
        ["italian"] = "it-IT",
        ["portuguese"] = "pt-BR",
        ["russian"] = "ru-RU",
        ["vietnamese"] = "vi-VN",
        ["thai"] = "th-TH",
        ["arabic"] = "ar-SA",
        ["hindi"] = "hi-IN"
    };

    private readonly ApplicationDbContext _db;
    private readonly IUserScopeProvider _userScope;

    public CoachLanguageResolver(ApplicationDbContext db, IUserScopeProvider userScope)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _userScope = userScope ?? throw new ArgumentNullException(nameof(userScope));
    }

    public async Task<CoachLanguageProfile> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var userProfileId = _userScope.UserProfileId;

        var row = await _db.UserProfiles
            .AsNoTracking()
            .Where(p => p.Id == userProfileId)
            .Select(p => new { p.TargetLanguage, p.NativeLanguage, p.DisplayLanguage })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return CoachLanguageProfile.Default;
        }

        var target = ToTag(row.TargetLanguage, CoachLanguageProfile.Default.TargetLanguageTag);
        var native = ToTag(row.NativeLanguage, CoachLanguageProfile.Default.NativeLanguageTag);

        // The explanation follows the display language when one is set, and the learner's first
        // language otherwise — never the language being studied.
        var display = ToTag(row.DisplayLanguage, native);

        return new CoachLanguageProfile(target, display, native);
    }

    /// <summary>Maps one stored language name onto a tag, falling back rather than guessing.</summary>
    public static string ToTag(string? languageName, string fallback)
    {
        if (string.IsNullOrWhiteSpace(languageName))
        {
            return fallback;
        }

        var trimmed = languageName.Trim();
        if (Tags.TryGetValue(trimmed, out var tag))
        {
            return tag;
        }

        // A stored value that is already a tag ("ko-KR", "pt-BR") is kept as-is when it is
        // shaped like one. Anything else falls back rather than becoming a made-up locale.
        return IsWellFormedTag(trimmed) ? trimmed : fallback;
    }

    /// <summary>
    /// True when the value is shaped like a BCP-47 tag rather than a language name.
    /// </summary>
    /// <remarks>
    /// The primary subtag must be two or three letters, which is what separates "ko" and
    /// "pt-BR" from "Klingon". Without that rule any unrecognised single word would pass
    /// through as a locale and reach a client that uses it to choose a font and a voice.
    /// </remarks>
    private static bool IsWellFormedTag(string value)
    {
        if (value.Length is < 2 or > 12)
        {
            return false;
        }

        var parts = value.Split('-');
        if (parts.Length > 3)
        {
            return false;
        }

        if (parts[0].Length is < 2 or > 3)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (part.Length is < 2 or > 8)
            {
                return false;
            }

            foreach (var c in part)
            {
                if (!char.IsAsciiLetterOrDigit(c))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
