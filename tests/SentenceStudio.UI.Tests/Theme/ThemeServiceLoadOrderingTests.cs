using FluentAssertions;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Services;

namespace SentenceStudio.UI.Tests.Theme;

/// <summary>
/// Hardening amendments applied after the P1 review.
/// </summary>
/// <remarks>
/// Each test here corresponds to a specific way the substrate could still misbehave in a live
/// circuit. They are grouped separately from the main presentation tests so the reason each one
/// exists stays attached to it.
/// </remarks>
public class ThemeServiceLoadOrderingTests
{
    private static ThemeService Circuit(
        out FakeThemePreferenceStore store,
        AppearanceSelection? storedInBrowser = null)
    {
        // synchronousReadAvailable: false is a live Blazor circuit — HttpContext is gone, so the
        // constructor's synchronous read necessarily misses and the state starts at the default.
        store = new FakeThemePreferenceStore(storedInBrowser, synchronousReadAvailable: false);
        return new ThemeService(store);
    }

    private static readonly AppearanceSelection Stored = new("forest", ThemeMode.Light, 1.35);

    // -------------------------------------------------------------------------------------------
    // A preview must not suppress the load
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_preview_before_the_first_load_does_not_suppress_the_load()
    {
        // Preview used to set the loaded flag, which made EnsureLoadedAsync a no-op afterwards.
        // The learner's stored appearance was then never read at all for the life of the circuit.
        var service = Circuit(out var store, Stored);

        service.PreviewTheme("vapor");
        await service.EnsureLoadedAsync();

        store.AsyncLoadCount.Should().Be(1, "the stored appearance must still be read");
    }

    [Fact]
    public async Task Loading_after_a_preview_keeps_the_preview_on_screen()
    {
        var service = Circuit(out _, Stored);

        service.PreviewTheme("vapor");
        await service.EnsureLoadedAsync();

        service.CurrentTheme.Should().Be("vapor", "the learner is still looking at the preview");
    }

    [Fact]
    public async Task Loading_after_a_preview_rebases_the_untouched_fields_onto_the_stored_tuple()
    {
        // The preview changed the theme only. Mode and text size must end up as the learner's
        // stored ones, not as the default's — the preview was measured against a baseline that
        // had not been loaded yet.
        var service = Circuit(out _, Stored);

        service.PreviewTheme("vapor");
        await service.EnsureLoadedAsync();

        service.Current.Should().Be(new AppearanceSelection("vapor", ThemeMode.Light, 1.35));
    }

    [Fact]
    public async Task Loading_after_a_preview_raises_the_rebase_as_a_preview_not_a_commit()
    {
        var service = Circuit(out _, Stored);
        service.PreviewTheme("vapor");

        var changes = new List<ThemeChangedEventArgs>();
        service.ThemeChanged += (_, e) => changes.Add(e);

        await service.EnsureLoadedAsync();

        changes.Should().ContainSingle();
        changes[0].IsPreview.Should().BeTrue("nothing has been persisted; it is still revertable");
        changes[0].Mode.Should().Be("light");
    }

    [Fact]
    public async Task Reverting_a_preview_made_before_load_restores_the_stored_tuple_not_the_default()
    {
        // The whole point of correcting the baseline: revert used to drop the learner onto the
        // default theme, because the default was all the constructor had seen.
        var service = Circuit(out var store, Stored);

        service.PreviewTheme("vapor");
        await service.EnsureLoadedAsync();

        (await service.RevertAsync()).Should().BeTrue();

        service.Current.Should().Be(Stored);
        store.SaveCount.Should().Be(0, "a preview and its revert never persist anything");
    }

    [Fact]
    public async Task A_multi_field_preview_before_load_carries_every_changed_field_across()
    {
        var service = Circuit(out _, Stored);

        service.PreviewTheme("vapor");
        service.PreviewFontScale(1.0);

        await service.EnsureLoadedAsync();

        // Theme and text size were both previewed, so both survive; mode was not, so it comes from
        // storage rather than from the default.
        service.Current.Should().Be(new AppearanceSelection("vapor", ThemeMode.Light, 1.0));
    }

    [Fact]
    public async Task Loading_with_nothing_previewed_still_behaves_as_a_plain_load()
    {
        var service = Circuit(out _, Stored);
        var changes = new List<ThemeChangedEventArgs>();
        service.ThemeChanged += (_, e) => changes.Add(e);

        await service.EnsureLoadedAsync();

        service.Current.Should().Be(Stored);
        changes.Should().ContainSingle().Which.IsPreview.Should().BeFalse();
        service.CanRevert.Should().BeFalse();
    }

    [Fact]
    public async Task An_explicitly_previewed_field_survives_the_rebase_even_when_it_matched_the_pre_load_baseline()
    {
        // The pre-load baseline is the default, which is dark. Previewing dark therefore changes
        // nothing visible — but the caller has still spoken for the mode, and must keep it once the
        // real stored tuple (which is light) arrives. Deriving the preview by diffing against the
        // baseline cannot express this; recording which fields were previewed can.
        var service = Circuit(out _, Stored);

        service.PreviewTheme("vapor");
        service.PreviewMode(ThemeMode.Dark);

        await service.EnsureLoadedAsync();

        service.Current.Should().Be(new AppearanceSelection("vapor", ThemeMode.Dark, 1.35));
        service.FontScale.Should().Be(1.35, "text size was never previewed, so it comes from storage");
    }

    [Fact]
    public async Task A_full_tuple_preview_before_load_overrides_every_stored_field()
    {
        // A caller that supplied a complete tuple has spoken for all three fields.
        var service = Circuit(out _, Stored);
        var whole = new AppearanceSelection("brite", ThemeMode.Dark, 0.85);

        service.Preview(whole);
        await service.EnsureLoadedAsync();

        service.Current.Should().Be(whole);
    }

    [Fact]
    public async Task Reverting_clears_the_previewed_fields_so_a_later_preview_starts_clean()
    {
        var service = Circuit(out _, Stored);

        service.PreviewMode(ThemeMode.Dark);
        await service.RevertAsync();

        service.PreviewTheme("vapor");
        await service.EnsureLoadedAsync();

        // Only the theme was previewed this time round, so the stored light mode survives.
        service.Current.Should().Be(new AppearanceSelection("vapor", ThemeMode.Light, 1.35));
    }

    [Fact]
    public async Task Committing_clears_the_previewed_fields()
    {
        var service = Circuit(out var store, Stored);

        service.PreviewMode(ThemeMode.Dark);
        await service.ApplyAsync(new AppearanceSelection("ocean", ThemeMode.Dark, 1.0));

        service.CanRevert.Should().BeFalse();
        store.Stored.Should().Be(new AppearanceSelection("ocean", ThemeMode.Dark, 1.0));
    }

    // -------------------------------------------------------------------------------------------
    // A commit must not overwrite stored fields it was not asked to change
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task SetTheme_before_the_first_load_preserves_the_stored_mode_and_text_size()
    {
        // Without loading first, the new tuple would be derived from the default — so choosing a
        // theme would silently write dark mode and 100% text over the learner's light mode and
        // 135%, and persist that to their cookie.
        var service = Circuit(out var store, Stored);

        await service.SetThemeAsync("vapor");

        service.Current.Should().Be(new AppearanceSelection("vapor", ThemeMode.Light, 1.35));
        store.Stored.Should().Be(new AppearanceSelection("vapor", ThemeMode.Light, 1.35));
    }

    [Fact]
    public async Task SetMode_before_the_first_load_preserves_the_stored_theme_and_text_size()
    {
        var service = Circuit(out var store, Stored);

        await service.SetModeAsync(ThemeMode.Dark);

        store.Stored.Should().Be(new AppearanceSelection("forest", ThemeMode.Dark, 1.35));
    }

    [Fact]
    public async Task SetFontScale_before_the_first_load_preserves_the_stored_theme_and_mode()
    {
        var service = Circuit(out var store, Stored);

        await service.SetFontScaleAsync(1.0);

        store.Stored.Should().Be(new AppearanceSelection("forest", ThemeMode.Light, 1.0));
    }

    [Fact]
    public async Task A_commit_after_a_pre_load_preview_persists_the_rebased_tuple()
    {
        var service = Circuit(out var store, Stored);

        service.PreviewTheme("vapor");
        await service.SetThemeAsync("vapor");

        store.Stored.Should().Be(new AppearanceSelection("vapor", ThemeMode.Light, 1.35));
        service.CanRevert.Should().BeFalse("committing disarms revert");
    }

    [Fact]
    public async Task A_commit_loads_at_most_once()
    {
        var service = Circuit(out var store, Stored);

        await service.SetThemeAsync("vapor");
        await service.SetModeAsync(ThemeMode.Dark);
        await service.SetFontScaleAsync(1.0);

        store.AsyncLoadCount.Should().Be(1, "the load is idempotent, not repeated per commit");
    }

    [Fact]
    public async Task An_explicit_full_tuple_apply_still_writes_exactly_what_it_was_given()
    {
        // ApplyAsync takes a complete tuple, so there is nothing to preserve and nothing to load.
        var service = Circuit(out var store, Stored);
        var explicitTuple = new AppearanceSelection("brite", ThemeMode.Dark, 0.85);

        await service.ApplyAsync(explicitTuple);

        service.Current.Should().Be(explicitTuple);
        store.Stored.Should().Be(explicitTuple);
    }

    [Fact]
    public async Task A_browser_with_nothing_stored_still_commits_cleanly()
    {
        var service = Circuit(out var store, storedInBrowser: null);

        await service.SetThemeAsync("vapor");

        store.Stored.Should().Be(
            new AppearanceSelection("vapor", ThemeCatalog.DefaultMode, AppearanceSelection.DefaultFontScale));
    }
}
