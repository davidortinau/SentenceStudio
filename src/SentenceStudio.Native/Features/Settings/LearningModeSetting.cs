using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Features.Settings;

/// <summary>What Settings shows for the Learning mode: null while it has not been read.</summary>
internal sealed record LearningModeState(LearningMode? Mode, bool Saving, string? ErrorKey);

/// <summary>
/// Settings > Learning mode (docs/known-words-plan.md, Captain 2026-11-14): Learner-led or
/// Practice-led. Native profile sync is download-only, so a change goes to the owner-checked
/// profile API first and is kept on the device only once the server accepted it. Changing the
/// mode saves the setting and nothing else: no word or mark is rewritten.
/// </summary>
internal sealed class LearningModeSetting(
    DeviceUserScopeProvider scope, VocabularyProgressRepository progress,
    UserProfileRepository profiles, INativeProfileRemoteStore remote,
    ILogger<LearningModeSetting> logger) : IDisposable
{
    public const string SaveFailedKey = "LearningMode_SaveFailed";

    private readonly CancellationTokenSource _lifetime = new();
    private string? _owner;
    private LearningMode? _mode;
    private bool _saving;
    private string? _errorKey;
    private bool _disposed;

    internal event Action? Changed;

    internal LearningModeState State => TrustedOwner()
        ? new(_mode, _saving, _errorKey) : new(null, false, null);

    private bool TrustedOwner() => !_disposed && _owner is { } owner
        && scope.TryGetUserProfileId(out var active)
        && string.Equals(active, owner, StringComparison.Ordinal);

    /// <summary>Reads the owner's mode from their profile on the device.</summary>
    internal async Task OpenAsync(string owner)
    {
        if (_disposed || string.IsNullOrWhiteSpace(owner) || _saving)
            return;
        // The same owner's mode stays on screen while it is read again.
        if (_owner != owner)
            _mode = null;
        _owner = owner;
        _errorKey = null;
        if (!TrustedOwner())
            return;
        try
        {
            var mode = await Task.Run(() => progress.GetLearningModeAsync(owner, _lifetime.Token));
            if (_disposed || _owner != owner || _saving || !TrustedOwner())
                return;
            _mode = mode;
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The Learning mode could not be read.");
            _errorKey = "NativeSettings_LearningModeLoadFailed";
        }
        Changed?.Invoke();
    }

    /// <summary>Saves the chosen mode: the server first, then the device.</summary>
    internal async Task SetAsync(LearningMode mode)
    {
        if (_disposed || _saving || _mode is null || _mode == mode || !Enum.IsDefined(mode)
            || !TrustedOwner())
            return;
        var owner = _owner!;
        _saving = true;
        _errorKey = null;
        Changed?.Invoke();
        try
        {
            var saved = await Task.Run(async () =>
                await remote.SaveLearningModeAsync(owner, mode, _lifetime.Token)
                    == NativeProfileRemoteOutcome.Saved
                && await profiles.SaveLearningModeAsync(owner, mode, _lifetime.Token));
            if (_disposed || _owner != owner || !TrustedOwner())
                return;
            if (saved)
                _mode = mode;
            else
                _errorKey = SaveFailedKey;
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The Learning mode could not be saved.");
            _errorKey = SaveFailedKey;
        }
        finally
        {
            _saving = false;
        }
        if (!_disposed)
            Changed?.Invoke();
    }

    internal void Close()
    {
        _owner = null;
        _mode = null;
        _errorKey = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Changed = null;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
