using System.Globalization;
using Microsoft.Extensions.Logging;

namespace SentenceStudio.WebUI.Services;

/// <summary>
/// The one place the coach's display name is resolved for a circuit.
/// </summary>
/// <remarks>
/// <para>
/// The name follows the language the learner is <em>studying</em>, not the language the interface
/// is drawn in — see <see cref="CoachPersonaLanguages"/> for why. Every surface that shows the
/// coach by name reads it from here so the header, the speaker labels, the entry control and the
/// accessible names cannot drift apart: a screen-reader user and a sighted user must hear and see
/// the same person.
/// </para>
/// <para>
/// Stored message content is never touched. The speaker label is a projection made at render time;
/// what the coach actually said is the learner's record and stays byte-for-byte as it was written.
/// </para>
/// <para>
/// Scoped, and re-resolved whenever the signed-in account changes, because the next learner studies
/// what they study. Until the profile has been read the fallback name is used rather than a blank:
/// an unnamed speaker label is worse than a name that is about to be corrected, and the correction
/// arrives on the same render pass as the rest of the learner's data.
/// </para>
/// </remarks>
public sealed class CoachPersona : IDisposable
{
    /// <summary>The resource key holding the persona's name in each culture.</summary>
    /// <remarks>
    /// Deliberately the same key the chat speaker label already used. That keeps one string per
    /// market — adding <c>AppResources.ja.resx</c> with this key is the whole of "give Japanese
    /// learners their own persona name".
    /// </remarks>
    public const string NameResourceKey = "Coach_RoleCoach";

    private readonly ICoachPersonaLanguageSource? _languages;
    private readonly CoachAccountBoundary? _boundary;
    private readonly ILogger<CoachPersona>? _logger;
    private readonly object _gate = new();

    private CultureInfo _culture = CoachPersonaLanguages.Fallback;
    private string? _studyLanguage;
    private bool _disposed;

    public CoachPersona(
        ICoachPersonaLanguageSource? languages = null,
        CoachAccountBoundary? boundary = null,
        ILogger<CoachPersona>? logger = null)
    {
        _languages = languages;
        _boundary = boundary;
        _logger = logger;

        if (_boundary is not null)
        {
            // The account boundary is already the one thing watching for "somebody else is signed
            // in now". Subscribing here rather than asking every host to remember to refresh is
            // what makes the name impossible to leave stale on one surface and current on another.
            _boundary.Crossed += OnAccountCrossed;
        }
    }

    /// <summary>Raised when the resolved name changes. Components should re-render.</summary>
    public event Action? Changed;

    /// <summary>The name to show wherever the coach is named.</summary>
    public string DisplayName
    {
        get
        {
            CultureInfo culture;
            lock (_gate)
            {
                culture = _culture;
            }

            var name = SentenceStudio.LocalizationManager.Instance.GetString(NameResourceKey, culture);

            // GetString answers with the key when the resource is missing, which would put
            // "Coach_RoleCoach" on screen as somebody's name. Fall back to the default market
            // instead; a missing satellite resource is a packaging problem, not a rename.
            return string.Equals(name, NameResourceKey, StringComparison.Ordinal)
                ? SentenceStudio.LocalizationManager.Instance.GetString(
                    NameResourceKey, CoachPersonaLanguages.Fallback)
                : name;
        }
    }

    /// <summary>The culture the name is read from.</summary>
    public CultureInfo NameCulture
    {
        get { lock (_gate) { return _culture; } }
    }

    /// <summary>The study language the current name was resolved from, if one is known.</summary>
    public string? StudyLanguage
    {
        get { lock (_gate) { return _studyLanguage; } }
    }

    /// <summary>True once a study language has been read, successfully or not.</summary>
    public bool HasResolved { get; private set; }

    /// <summary>
    /// Applies a study language that the caller already has in hand — the profile editor after a
    /// save, for instance — without a second read.
    /// </summary>
    public void ApplyStudyLanguage(string? studyLanguage)
    {
        var culture = CoachPersonaLanguages.ResolvePersonaCulture(studyLanguage);
        bool changed;

        lock (_gate)
        {
            changed = !string.Equals(_culture.Name, culture.Name, StringComparison.OrdinalIgnoreCase);
            _culture = culture;
            _studyLanguage = studyLanguage;
        }

        HasResolved = true;

        if (changed)
        {
            _logger?.LogDebug(
                "Coach persona culture resolved to {Culture} from study language.", culture.Name);
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Re-reads the learner's study language and updates the name if it moved.
    /// </summary>
    /// <remarks>
    /// Never throws. A profile read that fails leaves the previous answer in place rather than
    /// blanking the name: the coach is still whoever they were a moment ago, and a failed lookup
    /// is not evidence otherwise.
    /// </remarks>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_languages is null || _disposed)
        {
            HasResolved = true;
            return;
        }

        string? language;
        try
        {
            language = await _languages.GetStudyLanguageAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Coach persona could not read the learner's study language.");
            HasResolved = true;
            return;
        }

        if (_disposed)
        {
            return;
        }

        ApplyStudyLanguage(language);
    }

    /// <summary>Forgets the resolved language so the next read asks again.</summary>
    public void Reset()
    {
        bool changed;

        lock (_gate)
        {
            changed = !string.Equals(
                _culture.Name, CoachPersonaLanguages.Fallback.Name, StringComparison.OrdinalIgnoreCase);
            _culture = CoachPersonaLanguages.Fallback;
            _studyLanguage = null;
        }

        HasResolved = false;

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// The account changed, so the study language belongs to somebody else. Drop the old answer
    /// immediately and read the new one.
    /// </summary>
    /// <remarks>
    /// Reset first, await second. The surfaces re-render the moment the boundary is crossed, and a
    /// panel that still named the previous learner's teacher while the profile request was in
    /// flight would be showing one learner's material to another — small, but the same class of
    /// leak the boundary exists to close.
    /// </remarks>
    private void OnAccountCrossed(CoachAccountIdentity identity)
    {
        if (_disposed)
        {
            return;
        }

        Reset();

        if (!identity.IsAuthenticated)
        {
            return;
        }

        _ = RefreshAsync();
    }

    public void Dispose()
    {
        _disposed = true;

        if (_boundary is not null)
        {
            _boundary.Crossed -= OnAccountCrossed;
        }
    }
}

/// <summary>
/// Reads the learner's primary study language for <see cref="CoachPersona"/>.
/// </summary>
/// <remarks>
/// An interface rather than a direct repository dependency so the UI assembly's tests can resolve a
/// name without a database, and so a host with a cheaper source of the same fact can supply it.
/// </remarks>
public interface ICoachPersonaLanguageSource
{
    /// <summary>
    /// The learner's primary target language, or <see langword="null"/> when none is known.
    /// </summary>
    Task<string?> GetStudyLanguageAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The shipped source: the signed-in learner's profile.
/// </summary>
public sealed class UserProfileCoachPersonaLanguageSource(
    SentenceStudio.Data.UserProfileRepository profiles) : ICoachPersonaLanguageSource
{
    public async Task<string?> GetStudyLanguageAsync(CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetAsync().ConfigureAwait(false);

        // TargetLanguagesList is the multi-language field and its first entry is the primary
        // language; TargetLanguage is the legacy single field kept in sync with it. Reading the
        // list first means a multi-language profile resolves against what it is actually studying
        // first rather than against a field it may no longer maintain.
        return profile?.TargetLanguagesList.FirstOrDefault() ?? profile?.TargetLanguage;
    }
}
