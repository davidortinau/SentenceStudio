using SentenceStudio.Contracts.Theme;
using SentenceStudio.Services.Theme;

namespace SentenceStudio.Services;

/// <summary>
/// The <b>mutable presentation state</b> for one device or one browser: which theme, which mode,
/// what text size, right now.
/// </summary>
/// <remarks>
/// <para>
/// This type holds only state. The description of what a theme <i>is</i> — its id, its name, its
/// colours, whether its palette follows the mode — lives in <see cref="ThemeCatalog"/>, which is
/// immutable, static and safe to share by every host and every circuit at once. Splitting the two
/// is what makes the lifetime question answerable: the catalogue can be global because nothing can
/// write to it, and this class must not be, because everything about it is a write.
/// </para>
/// <para>
/// <b>Lifetime is load-bearing, not a detail.</b> On the web this is registered <c>Scoped</c>, so
/// each Blazor circuit gets its own instance, its own tuple, and its own
/// <see cref="ThemeChanged"/> invocation list. It was previously registered <c>Singleton</c> by
/// <c>AddSentenceStudioCoreServices</c>, shared by every host — which on the web meant one process-
/// wide object shared by every signed-in learner. One person switching to Vapor moved every other
/// person's state, and because <c>MainLayout</c> subscribes to <see cref="ThemeChanged"/> for the
/// life of the circuit, the event fired every other learner's handler and repainted their browser.
/// On MAUI the same class stays <c>Singleton</c>, because there the process <i>is</i> the device.
/// See <c>AddDeviceThemePresentation</c> and <c>AddBrowserThemePresentation</c>.
/// </para>
/// <para>
/// <b>Preview versus apply.</b> <see cref="Preview"/> changes state and raises the event so the DOM
/// follows, but writes nothing to the store. <see cref="ApplyAsync"/> does both. The distinction
/// exists so a caller can show a learner what a theme looks like without committing it — and so
/// that a later agent-initiated change can be offered, seen, and taken back.
/// <see cref="RevertAsync"/> restores the tuple captured when the first preview armed it and then
/// disarms; applying anything also disarms, because a committed value has nothing behind it to go
/// back to.
/// </para>
/// </remarks>
public sealed class ThemeService
{
    private readonly IThemePreferenceStore _store;

    private AppearanceSelection _current;
    private AppearanceSelection? _revertPoint;
    private PreviewFields _previewedFields;
    private bool _loaded;

    /// <summary>
    /// Which fields a running preview has spoken for.
    /// </summary>
    /// <remarks>
    /// Recorded rather than derived by diffing the preview against its baseline, because a diff
    /// cannot tell "the caller asked for dark mode" apart from "the caller never mentioned mode and
    /// the baseline happened to be dark". Those two want opposite outcomes when the baseline later
    /// turns out to have been the wrong one — see <see cref="EnsureLoadedAsync"/>.
    /// </remarks>
    [Flags]
    private enum PreviewFields
    {
        None = 0,
        Theme = 1,
        Mode = 2,
        FontScale = 4,
        All = Theme | Mode | FontScale
    }

    /// <summary>
    /// Raised after the presentation state changes, whether by preview, apply or revert. Handlers
    /// re-apply the tuple to the DOM.
    /// </summary>
    /// <remarks>
    /// The invocation list belongs to this instance, so on the web it belongs to one circuit. That
    /// is the mechanism that stops one learner's change reaching another's browser.
    /// </remarks>
    public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

    public ThemeService(IThemePreferenceStore store)
    {
        _store = store;
        _current = AppearanceSelection.Default;

        // Seed synchronously when the substrate is reachable without awaiting: MAUI always, and the
        // web during server-side rendering, where the cookie is on the request. In a circuit this
        // is a miss and EnsureLoadedAsync finishes the job on first render.
        if (_store.TryLoad(out var stored))
        {
            _current = stored;
            _loaded = true;
        }
    }

    /// <summary>The tuple currently in force.</summary>
    public AppearanceSelection Current => _current;

    /// <summary>The descriptor for the current theme.</summary>
    public ThemeDescriptor CurrentDescriptor => _current.Theme;

    /// <summary>Current theme id — the <c>data-ss-theme</c> value.</summary>
    public string CurrentTheme => _current.ThemeId;

    /// <summary>Current mode token — the <c>data-bs-theme</c> value.</summary>
    public string CurrentMode => _current.Mode.ToToken();

    /// <summary>Current mode.</summary>
    public ThemeMode Mode => _current.Mode;

    /// <summary>Current text scale multiplier.</summary>
    public double FontScale => _current.FontScale;

    /// <summary>Convenience for the settings toggle.</summary>
    public bool IsDarkMode => _current.Mode == ThemeMode.Dark;

    /// <summary>Whether <see cref="RevertAsync"/> currently has a tuple to go back to.</summary>
    public bool CanRevert => _revertPoint is not null;

    /// <summary>
    /// Finishes loading in contexts where the store could not be read synchronously — the Blazor
    /// circuit, where the cookie is only reachable over JS interop. Idempotent, and a no-op once
    /// the state has been loaded or committed.
    /// </summary>
    /// <remarks>
    /// A preview that ran before this completed was necessarily measured against the wrong
    /// baseline — the default, because that is all the constructor could see. Loading therefore
    /// corrects the baseline and re-expresses the preview as <i>the fields it changed</i>, applied
    /// to the tuple the learner actually has stored. Without that, reverting a pre-load preview
    /// would drop the learner to the default theme, and committing one would write the default's
    /// mode and text size over their real ones.
    /// </remarks>
    public async ValueTask EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_loaded)
        {
            return;
        }

        var stored = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        _loaded = true;

        if (stored is null)
        {
            return;
        }

        if (_revertPoint is null)
        {
            if (stored == _current)
            {
                return;
            }

            _current = stored;
            Raise(isPreview: false);
            return;
        }

        // A preview is running against a baseline that turned out to be wrong. Correct the
        // baseline, then re-apply the preview's own fields on top of the tuple the learner really
        // has stored — leaving the fields the preview never spoke for as the learner set them.
        var rebased = stored;

        if (_previewedFields.HasFlag(PreviewFields.Theme))
        {
            rebased = rebased.WithTheme(_current.ThemeId);
        }

        if (_previewedFields.HasFlag(PreviewFields.Mode))
        {
            rebased = rebased.WithMode(_current.Mode);
        }

        if (_previewedFields.HasFlag(PreviewFields.FontScale))
        {
            rebased = rebased.WithFontScale(_current.FontScale);
        }

        _revertPoint = stored;

        if (rebased == _current)
        {
            return;
        }

        _current = rebased;
        Raise(isPreview: true);
    }

    // -------------------------------------------------------------------------------------------
    // Preview — state and DOM only, never persisted
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Shows <paramref name="selection"/> without committing it. The first preview after a
    /// committed state captures that state so <see cref="RevertAsync"/> can restore it.
    /// </summary>
    /// <remarks>
    /// Deliberately does <b>not</b> mark the state as loaded. Previewing is a display change, not
    /// evidence about what the browser has stored, and treating it as such would make a preview
    /// before the first <see cref="EnsureLoadedAsync"/> suppress the load entirely — leaving the
    /// learner's real theme unread and the revert baseline pointing at the default.
    /// </remarks>
    public void Preview(AppearanceSelection selection) => Preview(selection, PreviewFields.All);

    /// <summary>Previews a theme change, preserving mode and text size.</summary>
    public void PreviewTheme(string themeId) =>
        Preview(_current.WithTheme(themeId), PreviewFields.Theme);

    /// <summary>Previews a mode change, preserving theme and text size.</summary>
    public void PreviewMode(ThemeMode mode) =>
        Preview(_current.WithMode(mode), PreviewFields.Mode);

    /// <summary>Previews a text-size change, preserving theme and mode.</summary>
    public void PreviewFontScale(double fontScale) =>
        Preview(_current.WithFontScale(fontScale), PreviewFields.FontScale);

    private void Preview(AppearanceSelection selection, PreviewFields fields)
    {
        ArgumentNullException.ThrowIfNull(selection);

        _revertPoint ??= _current;

        // Recorded even when the value does not visibly change, because "asked for dark while
        // already dark" still means the caller has spoken for the mode.
        _previewedFields |= fields;

        if (selection == _current)
        {
            return;
        }

        _current = selection;
        Raise(isPreview: true);
    }

    // -------------------------------------------------------------------------------------------
    // Apply — state, DOM and persistence
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Commits <paramref name="selection"/>: updates state, persists it for this device or browser,
    /// and raises <see cref="ThemeChanged"/>. Disarms any pending revert.
    /// </summary>
    public async ValueTask ApplyAsync(AppearanceSelection selection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);

        var changed = selection != _current;
        _current = selection;
        _loaded = true;
        _revertPoint = null;
        _previewedFields = PreviewFields.None;

        await _store.SaveAsync(selection, cancellationToken).ConfigureAwait(false);

        if (changed)
        {
            Raise(isPreview: false);
        }
    }

    /// <summary>
    /// Commits a theme change. Mode and text size are preserved by construction — the new tuple is
    /// derived from the current one rather than assembled from loose fields.
    /// </summary>
    /// <remarks>
    /// Loads first. Deriving from an unloaded state would take the mode and text size from the
    /// default rather than from what the learner has stored, and then persist them — turning a
    /// theme change into a silent reset of the other two fields. The load is idempotent and a
    /// no-op once it has happened, which is the normal case by the time a learner can click.
    /// </remarks>
    public async ValueTask SetThemeAsync(string themeId, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        await ApplyAsync(_current.WithTheme(themeId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Commits a mode change. Theme and text size are preserved.</summary>
    public async ValueTask SetModeAsync(ThemeMode mode, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        await ApplyAsync(_current.WithMode(mode), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Commits a mode change from a token. Rejects anything outside the closed set rather than
    /// coercing it.
    /// </summary>
    public ValueTask SetModeAsync(string modeToken, CancellationToken cancellationToken = default)
    {
        if (!ThemeModeExtensions.TryParse(modeToken, out var mode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(modeToken),
                modeToken,
                $"Mode must be '{ThemeModeExtensions.LightToken}' or '{ThemeModeExtensions.DarkToken}'.");
        }

        return SetModeAsync(mode, cancellationToken);
    }

    /// <summary>Commits a text-size change. Theme and mode are preserved.</summary>
    public async ValueTask SetFontScaleAsync(double fontScale, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        await ApplyAsync(_current.WithFontScale(fontScale), cancellationToken).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------------------------
    // Revert
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Restores the tuple captured when revert was armed, and disarms.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when nothing was armed — either nothing has been previewed, or the
    /// preview was already committed or already reverted. A second call is a no-op, not an undo of
    /// the undo.
    /// </returns>
    public ValueTask<bool> RevertAsync(CancellationToken cancellationToken = default)
    {
        var target = _revertPoint;
        if (target is null)
        {
            return ValueTask.FromResult(false);
        }

        // Disarmed before raising, so a handler that reacts by previewing again re-arms from the
        // restored tuple instead of stacking a second revert onto the same one.
        _revertPoint = null;
        _previewedFields = PreviewFields.None;

        if (target != _current)
        {
            _current = target;
            Raise(isPreview: false);
        }

        // The store was never written during preview, so the persisted value already equals the
        // restored tuple. Nothing to save.
        return ValueTask.FromResult(true);
    }

    private void Raise(bool isPreview) =>
        ThemeChanged?.Invoke(this, new ThemeChangedEventArgs(_current, isPreview));
}

/// <summary>
/// Payload for <see cref="ThemeService.ThemeChanged"/>.
/// </summary>
public sealed class ThemeChangedEventArgs : EventArgs
{
    public ThemeChangedEventArgs(AppearanceSelection selection, bool isPreview)
    {
        Selection = selection;
        IsPreview = isPreview;
    }

    /// <summary>The tuple now in force.</summary>
    public AppearanceSelection Selection { get; }

    /// <summary>
    /// True when the change has not been persisted and can still be taken back by
    /// <see cref="ThemeService.RevertAsync"/>.
    /// </summary>
    public bool IsPreview { get; }

    /// <summary>Theme id, for handlers that pass it straight to the DOM.</summary>
    public string Theme => Selection.ThemeId;

    /// <summary>Mode token, for handlers that pass it straight to the DOM.</summary>
    public string Mode => Selection.Mode.ToToken();

    /// <summary>Text scale, for handlers that pass it straight to the DOM.</summary>
    public double FontScale => Selection.FontScale;
}
