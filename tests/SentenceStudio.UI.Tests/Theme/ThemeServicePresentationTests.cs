using FluentAssertions;
using SentenceStudio.Contracts.Theme;
using SentenceStudio.Services;

namespace SentenceStudio.UI.Tests.Theme;

/// <summary>
/// The presentation state: what preview, apply and revert each promise.
/// </summary>
public class ThemeServicePresentationTests
{
    private static ThemeService Build(
        out FakeThemePreferenceStore store,
        AppearanceSelection? initial = null,
        bool synchronousReadAvailable = true)
    {
        store = new FakeThemePreferenceStore(initial, synchronousReadAvailable);
        return new ThemeService(store);
    }

    // -------------------------------------------------------------------------------------------
    // Loading
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Seeds_from_the_store_synchronously_when_the_context_allows_it()
    {
        // MAUI always, and the web during SSR where the cookie is on the request. This is what lets
        // App.razor put the right attributes on <html> before the first byte goes out.
        var stored = new AppearanceSelection("vapor", ThemeMode.Light, 1.2);
        var service = Build(out _, stored);

        service.Current.Should().Be(stored);
        service.CurrentTheme.Should().Be("vapor");
        service.CurrentMode.Should().Be("light");
        service.FontScale.Should().Be(1.2);
    }

    [Fact]
    public void Falls_back_to_the_default_when_nothing_is_stored()
    {
        var service = Build(out _);

        service.Current.Should().Be(AppearanceSelection.Default);
    }

    [Fact]
    public async Task Finishes_loading_asynchronously_when_the_context_cannot_read_synchronously()
    {
        // Stands in for a Blazor circuit: HttpContext is gone, so the cookie is only reachable over
        // JS interop and the constructor's synchronous read necessarily misses.
        var stored = new AppearanceSelection("forest", ThemeMode.Light, 1.1);
        var service = Build(out var store, stored, synchronousReadAvailable: false);

        service.Current.Should().Be(AppearanceSelection.Default, "the sync read could not see it");

        var changes = new List<ThemeChangedEventArgs>();
        service.ThemeChanged += (_, e) => changes.Add(e);

        await service.EnsureLoadedAsync();

        service.Current.Should().Be(stored);
        store.AsyncLoadCount.Should().Be(1);
        changes.Should().ContainSingle().Which.IsPreview.Should().BeFalse();
    }

    [Fact]
    public async Task EnsureLoadedAsync_is_idempotent_and_does_not_re_read_after_a_successful_sync_load()
    {
        var service = Build(out var store, new AppearanceSelection("ocean", ThemeMode.Dark, 1.0));

        await service.EnsureLoadedAsync();
        await service.EnsureLoadedAsync();

        store.AsyncLoadCount.Should().Be(0, "the constructor already had the value");
    }

    [Fact]
    public async Task EnsureLoadedAsync_does_not_clobber_a_change_already_made_in_this_scope()
    {
        var service = Build(out _, new AppearanceSelection("ocean", ThemeMode.Dark, 1.0),
            synchronousReadAvailable: false);

        await service.SetThemeAsync("vapor");
        await service.EnsureLoadedAsync();

        service.CurrentTheme.Should().Be("vapor");
    }

    // -------------------------------------------------------------------------------------------
    // Preview
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Preview_changes_state_and_raises_the_event_but_persists_nothing()
    {
        var service = Build(out var store, new AppearanceSelection("ocean", ThemeMode.Dark, 1.0));
        var changes = new List<ThemeChangedEventArgs>();
        service.ThemeChanged += (_, e) => changes.Add(e);

        service.PreviewTheme("sunset");

        service.CurrentTheme.Should().Be("sunset");
        changes.Should().ContainSingle();
        changes[0].IsPreview.Should().BeTrue();
        changes[0].Theme.Should().Be("sunset");

        store.SaveCount.Should().Be(0, "preview must not write to the substrate");
        store.Stored!.ThemeId.Should().Be("ocean", "the persisted value is untouched");
    }

    [Fact]
    public void Preview_preserves_the_fields_it_is_not_changing()
    {
        var service = Build(out _, new AppearanceSelection("ocean", ThemeMode.Light, 1.35));

        service.PreviewTheme("slate");

        service.Mode.Should().Be(ThemeMode.Light);
        service.FontScale.Should().Be(1.35);
    }

    [Fact]
    public void Preview_arms_revert_even_when_the_previewed_value_matches_the_current_one()
    {
        // Otherwise a caller that previews the already-selected theme, then previews a different
        // one, would have nothing to go back to.
        var service = Build(out _, new AppearanceSelection("ocean", ThemeMode.Dark, 1.0));

        service.PreviewTheme("ocean");

        service.CanRevert.Should().BeTrue();
    }

    [Fact]
    public void Previewing_an_unknown_theme_throws_rather_than_falling_back()
    {
        var service = Build(out _);

        var act = () => service.PreviewTheme("not-a-theme");
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // -------------------------------------------------------------------------------------------
    // Apply
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Apply_changes_state_persists_and_raises_a_non_preview_event()
    {
        var service = Build(out var store, new AppearanceSelection("ocean", ThemeMode.Dark, 1.0));
        var changes = new List<ThemeChangedEventArgs>();
        service.ThemeChanged += (_, e) => changes.Add(e);

        await service.SetThemeAsync("sunset");

        service.CurrentTheme.Should().Be("sunset");
        store.SaveCount.Should().Be(1);
        store.Stored!.ThemeId.Should().Be("sunset");
        changes.Should().ContainSingle().Which.IsPreview.Should().BeFalse();
    }

    [Fact]
    public async Task SetTheme_preserves_mode_and_font_scale()
    {
        var service = Build(out var store, new AppearanceSelection("ocean", ThemeMode.Light, 1.4));

        await service.SetThemeAsync("brite");

        service.CurrentTheme.Should().Be("brite");
        service.Mode.Should().Be(ThemeMode.Light);
        service.FontScale.Should().Be(1.4);
        store.Stored.Should().Be(new AppearanceSelection("brite", ThemeMode.Light, 1.4));
    }

    [Fact]
    public async Task SetMode_preserves_theme_and_font_scale()
    {
        var service = Build(out var store, new AppearanceSelection("ocean", ThemeMode.Light, 1.4));

        await service.SetModeAsync(ThemeMode.Dark);

        service.CurrentTheme.Should().Be("ocean");
        service.Mode.Should().Be(ThemeMode.Dark);
        service.FontScale.Should().Be(1.4);
        store.Stored.Should().Be(new AppearanceSelection("ocean", ThemeMode.Dark, 1.4));
    }

    [Fact]
    public async Task SetFontScale_preserves_theme_and_mode()
    {
        var service = Build(out _, new AppearanceSelection("vapor", ThemeMode.Light, 1.0));

        await service.SetFontScaleAsync(0.85);

        service.CurrentTheme.Should().Be("vapor");
        service.Mode.Should().Be(ThemeMode.Light);
        service.FontScale.Should().Be(0.85);
    }

    [Fact]
    public async Task Apply_still_persists_when_the_value_did_not_change_but_raises_no_event()
    {
        // Re-committing the current tuple is how a caller repairs a substrate that lost the value —
        // a cleared cookie, say — so it must write. It must not repaint.
        var service = Build(out var store, new AppearanceSelection("ocean", ThemeMode.Dark, 1.0));
        var raised = 0;
        service.ThemeChanged += (_, _) => raised++;

        await service.SetThemeAsync("ocean");

        store.SaveCount.Should().Be(1);
        raised.Should().Be(0);
    }

    [Fact]
    public async Task Applying_an_unknown_theme_throws_rather_than_falling_back()
    {
        var service = Build(out var store);

        var act = async () => await service.SetThemeAsync("not-a-theme");

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        store.SaveCount.Should().Be(0);
    }

    [Theory]
    [InlineData("sepia")]
    [InlineData("auto")]
    [InlineData("")]
    public async Task Applying_an_unknown_mode_token_throws_rather_than_falling_back(string token)
    {
        var service = Build(out var store);

        var act = async () => await service.SetModeAsync(token);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        store.SaveCount.Should().Be(0);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(3.0)]
    [InlineData(double.NaN)]
    public async Task Applying_an_out_of_range_font_scale_throws_rather_than_clamping_silently(double scale)
    {
        var service = Build(out var store);

        var act = async () => await service.SetFontScaleAsync(scale);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        store.SaveCount.Should().Be(0);
    }

    // -------------------------------------------------------------------------------------------
    // Revert
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Revert_restores_the_tuple_captured_by_the_first_preview()
    {
        var start = new AppearanceSelection("ocean", ThemeMode.Dark, 1.0);
        var service = Build(out var store, start);

        service.PreviewTheme("vapor");
        service.PreviewMode(ThemeMode.Light);
        service.PreviewFontScale(1.5);

        (await service.RevertAsync()).Should().BeTrue();

        service.Current.Should().Be(start, "revert goes back to the last committed tuple, not one step");
        store.SaveCount.Should().Be(0, "nothing was ever persisted during preview");
    }

    [Fact]
    public async Task Revert_raises_a_non_preview_event_so_the_dom_follows()
    {
        var service = Build(out _, new AppearanceSelection("ocean", ThemeMode.Dark, 1.0));
        service.PreviewTheme("vapor");

        var changes = new List<ThemeChangedEventArgs>();
        service.ThemeChanged += (_, e) => changes.Add(e);

        await service.RevertAsync();

        changes.Should().ContainSingle();
        changes[0].IsPreview.Should().BeFalse();
        changes[0].Theme.Should().Be("ocean");
    }

    [Fact]
    public async Task Revert_self_disarms_so_a_second_call_does_nothing()
    {
        var service = Build(out _, new AppearanceSelection("ocean", ThemeMode.Dark, 1.0));
        service.PreviewTheme("vapor");

        (await service.RevertAsync()).Should().BeTrue();
        service.CanRevert.Should().BeFalse();

        service.PreviewTheme("brite");
        (await service.RevertAsync()).Should().BeTrue();

        // The second revert went back to ocean, not forward to vapor.
        service.CurrentTheme.Should().Be("ocean");
        (await service.RevertAsync()).Should().BeFalse("nothing is armed any more");
    }

    [Fact]
    public async Task Revert_is_not_armed_before_anything_is_previewed()
    {
        var service = Build(out _, new AppearanceSelection("ocean", ThemeMode.Dark, 1.0));

        service.CanRevert.Should().BeFalse();
        (await service.RevertAsync()).Should().BeFalse();
        service.CurrentTheme.Should().Be("ocean");
    }

    [Fact]
    public async Task Applying_disarms_revert_because_a_committed_value_has_nothing_behind_it()
    {
        var service = Build(out _, new AppearanceSelection("ocean", ThemeMode.Dark, 1.0));

        service.PreviewTheme("vapor");
        service.CanRevert.Should().BeTrue();

        await service.SetThemeAsync("vapor");

        service.CanRevert.Should().BeFalse();
        (await service.RevertAsync()).Should().BeFalse();
        service.CurrentTheme.Should().Be("vapor", "the applied value stands");
    }

    [Fact]
    public async Task A_preview_after_a_revert_re_arms_from_the_restored_tuple()
    {
        var start = new AppearanceSelection("ocean", ThemeMode.Dark, 1.0);
        var service = Build(out _, start);

        service.PreviewTheme("vapor");
        await service.RevertAsync();

        service.PreviewTheme("slate");
        service.CanRevert.Should().BeTrue();
        (await service.RevertAsync()).Should().BeTrue();

        service.Current.Should().Be(start);
    }
}
