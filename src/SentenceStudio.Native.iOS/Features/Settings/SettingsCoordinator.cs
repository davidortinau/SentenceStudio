using Microsoft.Extensions.Logging;
using SentenceStudio.Abstractions;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Data;
using SentenceStudio.Native.iOS.Styling;
using SentenceStudio.Services;

namespace SentenceStudio.Native.iOS.Features.Settings;

internal static class SettingsOwnerAccess
{
    internal const string FixtureBundleId =
        "com.simplyprofound.sentencestudio.native.fixture";

    internal static string? Resolve(
        NativeAuthState auth, bool preview, bool debugBuild, string? bundleId,
        Func<string?> validatedPreviewOwner, DeviceUserScopeProvider scope,
        IPreferencesService preferences, ILogger logger)
    {
        string? owner = null;
        if (preview)
        {
#if DEBUG
            if (debugBuild && bundleId == FixtureBundleId)
            {
                try
                {
                    owner = validatedPreviewOwner();
                }
                catch (Exception ex) when (ex is InvalidOperationException
                    or ArgumentException)
                {
                    logger.LogWarning(ex,
                        "Settings refused invalid Debug fixture metadata.");
                }
            }
#endif
        }
        else if (auth is {
            Phase: NativePhase.Ready,
            Destination: NativeDestination.Dashboard,
            UserId: { Length: > 0 } authenticated
        })
            owner = authenticated;

        var stamp = scope.CaptureStamp();
        if (string.IsNullOrWhiteSpace(owner)
            || stamp.PublishedOwner != owner || !scope.IsCurrent(stamp)
            || !scope.TryGetUserProfileId(out var active)
            || !string.Equals(active, owner, StringComparison.Ordinal)
            || !HasPersistedOwner(preferences, owner, logger))
        {
            logger.LogWarning(
                "Settings navigation refused an absent or untrusted owner.");
            return null;
        }
        return owner;
    }

    private static bool HasPersistedOwner(
        IPreferencesService preferences, string owner, ILogger logger)
    {
        try
        {
            return preferences.Get(UserProfileRepository.ActiveProfileIdKey,
                string.Empty) == owner;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Settings navigation could not read the local owner.");
            return false;
        }
    }
}

internal sealed record SettingsState(
    bool IsOpen, bool OwnerBlocked, QuizSettingsDraft? Quiz,
    bool VoiceLoading, string? VoiceErrorKey, string? FeedbackKey);

internal sealed class SettingsCoordinator : IDisposable
{
    private readonly object _gate = new();
    private readonly DeviceUserScopeProvider _scope;
    private readonly IPreferencesService _preferences;
    private readonly NativeQuizSettings _quiz;
    private readonly ILogger<SettingsCoordinator> _logger;
    private readonly bool _allowVoiceDiscovery;
    private string? _owner;
    private DeviceUserScopeProvider.ScopeStamp _stamp;
    private QuizSettingsDraft? _draft;
    private string? _feedbackKey;
    private string? _voiceErrorKey;
    private bool _voiceLoading;
    private bool _open;
    private bool _blocked;
    private bool _disposed;
    private long _voiceGeneration;
    private int _viewVersion;

    internal SettingsCoordinator(
        DeviceUserScopeProvider scope, IPreferencesService preferences,
        NativeQuizSettings quiz, ILogger<SettingsCoordinator> logger,
        bool allowVoiceDiscovery = true)
    {
        _scope = scope;
        _preferences = preferences;
        _quiz = quiz;
        _logger = logger;
        _allowVoiceDiscovery = allowVoiceDiscovery;
    }

    internal event Action? Changed;
    internal bool IsOpen
    {
        get { lock (_gate) return _open; }
    }
    internal int ViewVersion
    {
        get { lock (_gate) return _viewVersion; }
    }
    internal SettingsState State
    {
        get
        {
            SettingsState state;
            bool revoked;
            lock (_gate)
            {
                revoked = RevokeIfStaleLocked();
                state = new(_open, _blocked, _draft, _voiceLoading,
                    _voiceErrorKey, _feedbackKey);
            }
            if (revoked)
                Changed?.Invoke();
            return state;
        }
    }

    internal bool Open(string? expectedOwner)
    {
        lock (_gate)
        {
            if (_disposed || _open)
                return false;
        }
        if (string.IsNullOrWhiteSpace(expectedOwner)
            || !_scope.TryGetUserProfileId(out var active)
            || !string.Equals(expectedOwner, active, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Settings refused an absent or untrusted owner.");
            return false;
        }
        var stamp = _scope.CaptureStamp();
        if (stamp.PublishedOwner != expectedOwner || !_scope.IsCurrent(stamp))
        {
            _logger.LogWarning("Settings refused a changed owner.");
            return false;
        }
        NativeQuizSettingsState? current;
        try
        {
            current = _quiz.Read(expectedOwner);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Owned Settings could not be read.");
            return false;
        }
        if (current is null)
        {
            _logger.LogWarning(
                "Settings refused an untrusted persisted owner.");
            return false;
        }
        lock (_gate)
        {
            if (_disposed || _open || !_scope.IsCurrent(stamp)
                || !_scope.TryGetUserProfileId(out active)
                || active != expectedOwner
                || !HasPersistedOwner(expectedOwner))
                return false;
            _owner = expectedOwner;
            _stamp = stamp;
            _draft = QuizSettingsDraft.FromState(current);
            _voiceLoading = _allowVoiceDiscovery;
            _voiceErrorKey = _allowVoiceDiscovery
                ? null : "NativeSettings_VoicesRequireSignIn";
            _open = true;
            _blocked = false;
            ++_viewVersion;
            _scope.ScopeChanged += OnScopeChanged;
        }
        OnScopeChanged();
        lock (_gate)
            return _open && !_blocked;
    }

    internal bool Edit(Func<QuizSettingsDraft, QuizSettingsDraft> change)
    {
        bool revoked;
        bool updated;
        bool notify = false;
        lock (_gate)
        {
            revoked = RevokeIfStaleLocked();
            updated = !revoked && _draft is not null
                && IsCurrentOwnerLocked();
            if (updated)
            {
                _draft = change(_draft!);
                if (_feedbackKey is not null)
                {
                    _feedbackKey = null;
                    ++_viewVersion;
                    notify = true;
                }
            }
        }
        if (revoked || notify)
            Changed?.Invoke();
        return updated;
    }

    internal string? SelectVoiceLanguage(string language)
    {
        if (!NativeQuizSettings.Languages.Contains(language))
            return null;
        bool revoked;
        bool notifyError = false;
        string? owner = null;
        lock (_gate)
        {
            revoked = RevokeIfStaleLocked();
            if (!revoked && _draft is not null && IsCurrentOwnerLocked())
            {
                try
                {
                    var current = _quiz.Read(_owner!, language);
                    if (current is not null)
                    {
                        owner = _owner;
                        _draft = _draft with {
                            VoiceLanguage = language, VoiceId = current.VoiceId,
                            VoiceOptions = []
                        };
                        _voiceErrorKey = _allowVoiceDiscovery
                            ? null : "NativeSettings_VoicesRequireSignIn";
                        _feedbackKey = null;
                        _voiceLoading = _allowVoiceDiscovery;
                        ++_voiceGeneration;
                        ++_viewVersion;
                    }
                    else
                        revoked = RevokeIfStaleLocked();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Owned voice choice could not be read.");
                    if (IsCurrentOwnerLocked())
                    {
                        _voiceErrorKey = NativeText.ConnectionError;
                        _voiceLoading = false;
                        ++_viewVersion;
                        notifyError = true;
                    }
                    else
                        revoked = RevokeIfStaleLocked();
                }
            }
        }
        if (revoked || owner is not null || notifyError)
            Changed?.Invoke();
        return owner;
    }

    internal async Task LoadVoicesAsync(
        string owner, string language, CancellationToken cancellationToken)
    {
        DeviceUserScopeProvider.ScopeStamp stamp;
        long generation;
        bool revoked;
        bool loadingStarted = false;
        lock (_gate)
        {
            revoked = RevokeIfStaleLocked();
            if (revoked || !IsCurrentOwnerLocked()
                || _owner != owner || _draft?.VoiceLanguage != language)
            {
                stamp = default;
                generation = 0;
            }
            else
            {
                stamp = _stamp;
                generation = ++_voiceGeneration;
                if (_allowVoiceDiscovery && !_voiceLoading)
                {
                    _voiceLoading = true;
                    ++_viewVersion;
                    loadingStarted = true;
                }
            }
        }
        if (revoked || loadingStarted)
            Changed?.Invoke();
        if (generation == 0)
            return;

        NativeQuizSettingsState? result = null;
        try
        {
            result = _allowVoiceDiscovery
                ? await _quiz.GetAsync(owner, language, cancellationToken)
                : _quiz.Read(owner, language);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Native voice options could not be loaded.");
        }

        bool changed;
        lock (_gate)
        {
            changed = RevokeIfStaleLocked();
            if (!changed && generation == _voiceGeneration
                && _stamp == stamp && IsCurrentOwnerLocked()
                && _owner == owner && _draft?.VoiceLanguage == language)
            {
                if (result is not null)
                {
                    var voiceId = result.VoiceOptions.Any(option =>
                        option.Id == _draft.VoiceId)
                        ? _draft.VoiceId
                        : result.VoiceOptions.IsDefaultOrEmpty
                            ? result.VoiceId : result.VoiceOptions[0].Id;
                    if (voiceId != _draft.VoiceId)
                        _feedbackKey = null;
                    _draft = _draft with {
                        VoiceId = voiceId,
                        VoiceOptions = result.VoiceOptions
                    };
                }
                _voiceErrorKey = result is null
                    ? NativeText.ConnectionError
                    : _allowVoiceDiscovery
                        ? result.ErrorKey
                        : "NativeSettings_VoicesRequireSignIn";
                _voiceLoading = false;
                ++_viewVersion;
                changed = true;
            }
        }
        if (changed)
            Changed?.Invoke();
    }

    internal SourceUiAppearance? SaveAppearance(
        AppearanceSelection selection)
    {
        bool changed;
        SourceUiAppearance? saved = null;
        lock (_gate)
        {
            changed = RevokeIfStaleLocked();
            if (!changed && IsCurrentOwnerLocked())
            {
                try
                {
                    var next = SourceUiAppearance.Save(_preferences, selection);
                    if (IsCurrentOwnerLocked()
                        && _preferences.Get(SourceUiAppearance.PreferenceKey,
                            string.Empty) == selection.ToToken())
                        saved = next;
                    else if (IsCurrentOwnerLocked())
                    {
                        _feedbackKey = NativeText.SaveFailed;
                        ++_viewVersion;
                        changed = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Owned appearance preference could not be saved.");
                    if (IsCurrentOwnerLocked())
                    {
                        _feedbackKey = NativeText.SaveFailed;
                        ++_viewVersion;
                        changed = true;
                    }
                }
            }
        }
        if (changed)
            Changed?.Invoke();
        return saved;
    }

    internal void SaveQuiz()
    {
        bool changed;
        lock (_gate)
        {
            changed = RevokeIfStaleLocked();
            if (!changed && !_voiceLoading && _draft is not null
                && IsCurrentOwnerLocked())
            {
                var owner = _owner!;
                var original = _draft;
                var draft = original.WithValidModalities();
                NativeQuizSettingsState? saved = null;
                try
                {
                    saved = _quiz.Save(owner, draft.ToState(), _stamp);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Owned quiz preferences could not be saved.");
                }
                if (IsCurrentOwnerLocked())
                {
                    if (saved is { ErrorKey: null })
                    {
                        _feedbackKey = draft.TextPrompt != original.TextPrompt
                            ? "Settings_ModalityRequired"
                            : "Settings_PreferencesSaved";
                        _draft = QuizSettingsDraft.FromState(saved) with {
                            VoiceOptions = original.VoiceOptions
                        };
                    }
                    else
                        _feedbackKey = saved?.ErrorKey
                            == NativeText.QuizUnavailable
                            ? NativeText.SaveFailed
                            : saved?.ErrorKey ?? NativeText.SaveFailed;
                    ++_viewVersion;
                    changed = true;
                }
                else
                    changed = RevokeIfStaleLocked();
            }
        }
        if (changed)
            Changed?.Invoke();
    }

    internal void Close()
    {
        lock (_gate)
            CloseLocked();
    }

    private void CloseLocked()
    {
        if (_open)
            _scope.ScopeChanged -= OnScopeChanged;
        _open = false;
        _owner = null;
        _stamp = default;
        _draft = null;
        _feedbackKey = null;
        _voiceErrorKey = null;
        _voiceLoading = false;
        _blocked = false;
        ++_voiceGeneration;
        ++_viewVersion;
    }

    private bool IsCurrentOwnerLocked()
    {
        if (_disposed || !_open || _blocked || _owner is null
            || _stamp.PublishedOwner != _owner
            || !_scope.IsCurrent(_stamp)
            || !_scope.TryGetUserProfileId(out var active)
            || active != _owner)
            return false;
        return HasPersistedOwner(_owner);
    }

    private bool HasPersistedOwner(string owner)
    {
        try
        {
            return _preferences.Get(UserProfileRepository.ActiveProfileIdKey,
                string.Empty) == owner;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Settings owner preference could not be read.");
            return false;
        }
    }

    private void OnScopeChanged()
    {
        bool revoked;
        lock (_gate)
            revoked = RevokeIfStaleLocked();
        if (revoked)
            Changed?.Invoke();
    }

    private bool RevokeIfStaleLocked()
    {
        if (!_open || _disposed || _blocked || IsCurrentOwnerLocked())
            return false;
        _logger.LogWarning(
            "Settings draft discarded after owner scope changed.");
        _owner = null;
        _stamp = default;
        _draft = null;
        _feedbackKey = null;
        _voiceErrorKey = null;
        _voiceLoading = false;
        _blocked = true;
        ++_voiceGeneration;
        ++_viewVersion;
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            CloseLocked();
            _disposed = true;
            Changed = null;
        }
    }
}
