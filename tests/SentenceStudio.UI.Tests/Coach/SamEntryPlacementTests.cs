using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared;
using SentenceStudio.WebUI.Shared.Sam;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Where the web and MAUI apps show Sam's entry (Captain 2026-11-04, docs/sam-entry.md): the
/// floating button only on the Dashboard and Resources, an icon in the PageHeader everywhere else,
/// under the same gate as the button, gone while the panel is open, opening the panel the same way.
/// </summary>
/// <remarks>
/// The host and a page header are rendered as two roots of one interactive renderer over one
/// service provider, so they share the scoped <see cref="SamEntryState"/> exactly as a layout and
/// a page share it in a circuit or a MAUI web view.
/// </remarks>
public class SamEntryPlacementTests
{
    // ================================================================ the rule

    [Theory]
    [InlineData("", SamEntryPlacement.Floating)]
    [InlineData("/", SamEntryPlacement.Floating)]
    [InlineData("?session=abc", SamEntryPlacement.Floating)]
    [InlineData("resources", SamEntryPlacement.Floating)]
    [InlineData("Resources/", SamEntryPlacement.Floating)]
    [InlineData("resources?filter=video#top", SamEntryPlacement.Floating)]
    [InlineData("resources/add", SamEntryPlacement.Header)]
    [InlineData("resources/edit/42", SamEntryPlacement.Header)]
    [InlineData("settings", SamEntryPlacement.Header)]
    [InlineData("vocab-quiz", SamEntryPlacement.Header)]
    [InlineData("vocabulary/lookups", SamEntryPlacement.Header)]
    [InlineData("profile", SamEntryPlacement.Header)]
    [InlineData("coach", SamEntryPlacement.None)]
    [InlineData("coach?session=abc&pane=plan", SamEntryPlacement.None)]
    [InlineData("coach/anything", SamEntryPlacement.None)]
    [InlineData("coaching-tips", SamEntryPlacement.Header)]
    public void Only_the_main_pages_float_and_Sams_own_page_has_no_entry(
        string path, SamEntryPlacement expected)
    {
        SamEntryPlacementRule.ForPath(path).Should().Be(expected);
    }

    // ================================================================ the floating button

    [Theory]
    [InlineData("/")]
    [InlineData("/resources")]
    public async Task The_main_pages_get_the_floating_button_and_no_header_icon(string page)
    {
        await using var harness = new Harness();
        harness.Nav.GoTo(page);
        var host = await harness.RenderSignedInHostAsync();
        var header = await harness.RenderHeaderAsync();

        harness.Renderer.HasElementWithId(host, SamElementIds.Fab).Should().BeTrue();
        harness.Renderer.HasElementWithId(header, SamElementIds.HeaderEntry).Should().BeFalse(
            "a page with the floating button does not also put Sam in its header");
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/settings")]
    [InlineData("/vocab-quiz")]
    [InlineData("/resources/add")]
    public async Task Every_other_page_gets_the_header_icon_and_no_floating_button(string page)
    {
        await using var harness = new Harness();
        harness.Nav.GoTo(page);
        var host = await harness.RenderSignedInHostAsync();
        var header = await harness.RenderHeaderAsync();

        harness.Renderer.HasElementWithId(host, SamElementIds.Fab).Should().BeFalse();
        harness.Renderer.HasElementWithId(header, SamElementIds.HeaderEntry).Should().BeTrue();
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    [Fact]
    public async Task Sams_own_page_has_neither_entry()
    {
        await using var harness = new Harness();
        harness.Nav.GoTo("/coach");
        var host = await harness.RenderSignedInHostAsync();
        var header = await harness.RenderHeaderAsync();

        harness.Renderer.HasElementWithId(host, SamElementIds.Fab).Should().BeFalse();
        harness.Renderer.HasElementWithId(header, SamElementIds.HeaderEntry).Should().BeFalse();
    }

    [Fact]
    public async Task Navigating_away_from_the_Dashboard_takes_the_floating_button_away_and_resets_the_hide()
    {
        await using var harness = new Harness();
        var host = await harness.RenderSignedInHostAsync();
        harness.Renderer.HasElementWithId(host, SamElementIds.Fab).Should().BeTrue();
        var resetsBefore = harness.Js.Invocations.Count(i => i == "resetSamEntry");

        await harness.Renderer.Dispatcher.InvokeAsync(() => harness.Nav.GoTo("/settings"));
        await harness.Renderer.Dispatcher.InvokeAsync(() => Task.CompletedTask);

        harness.Renderer.HasElementWithId(host, SamElementIds.Fab).Should().BeFalse();
        harness.Js.Invocations.Count(i => i == "resetSamEntry").Should().Be(resetsBefore + 1,
            "a new page starts with the button shown");

        await harness.Renderer.Dispatcher.InvokeAsync(() => harness.Nav.GoTo("/resources"));
        await harness.Renderer.Dispatcher.InvokeAsync(() => Task.CompletedTask);

        harness.Renderer.HasElementWithId(host, SamElementIds.Fab).Should().BeTrue();
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    // ================================================================ the gate

    [Fact]
    public async Task No_host_means_no_header_icon()
    {
        // The layout keeps the host out while signed out, onboarding, syncing or when Sam is
        // unavailable for the learner (CoachSurfaceGate); nothing then attaches to the entry.
        await using var harness = new Harness();
        harness.Nav.GoTo("/settings");
        harness.Auth.SignIn("profile-a", "a@example.test");

        var header = await harness.RenderHeaderAsync();

        harness.Renderer.HasElementWithId(header, SamElementIds.HeaderEntry).Should().BeFalse();
    }

    [Fact]
    public async Task A_signed_out_shell_has_no_header_icon()
    {
        await using var harness = new Harness();
        harness.Nav.GoTo("/settings");
        harness.Auth.SignOut();

        await harness.Renderer.RenderAsync<SamOverlayHost>();
        var header = await harness.RenderHeaderAsync();

        harness.Renderer.HasElementWithId(header, SamElementIds.HeaderEntry).Should().BeFalse();
    }

    [Fact]
    public async Task Signing_out_and_unmounting_the_host_both_take_the_header_icon_away()
    {
        await using var harness = new Harness();
        harness.Nav.GoTo("/settings");
        var host = await harness.RenderSignedInHostAsync();
        var header = await harness.RenderHeaderAsync();
        harness.Renderer.HasElementWithId(header, SamElementIds.HeaderEntry).Should().BeTrue();

        harness.Auth.SignOut();
        await harness.Renderer.Dispatcher.InvokeAsync(() => Task.CompletedTask);
        harness.Renderer.HasElementWithId(header, SamElementIds.HeaderEntry).Should().BeFalse(
            "the header follows the account the host follows");

        harness.Auth.SignIn("profile-b", "b@example.test");
        await harness.Renderer.Dispatcher.InvokeAsync(() => Task.CompletedTask);
        harness.Renderer.HasElementWithId(header, SamElementIds.HeaderEntry).Should().BeTrue();

        await harness.Renderer.DisposeRootComponentAsync(host);
        harness.Renderer.HasElementWithId(header, SamElementIds.HeaderEntry).Should().BeFalse(
            "a host the layout took away can no longer open anything");
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    [Fact]
    public async Task A_host_torn_down_after_its_replacement_attached_leaves_the_entry_on()
    {
        // MAUI rebuilds the layout inside a scope that lives on; the old host's disposal can land
        // after the new host attached.
        await using var harness = new Harness();
        harness.Nav.GoTo("/settings");
        var oldHost = await harness.RenderSignedInHostAsync();
        await harness.Renderer.RenderAsync<SamOverlayHost>();
        var header = await harness.RenderHeaderAsync();

        await harness.Renderer.DisposeRootComponentAsync(oldHost);

        harness.Renderer.HasElementWithId(header, SamElementIds.HeaderEntry).Should().BeTrue();
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    // ================================================================ opening and closing

    [Fact]
    public async Task The_header_icon_names_Sam_and_controls_the_panel_like_the_floating_button()
    {
        await using var harness = new Harness();
        var fabHost = await harness.RenderSignedInHostAsync();
        var fabName = harness.Renderer.AttributeValue(fabHost, SamElementIds.Fab, "aria-label");
        await harness.Renderer.DisposeRootComponentAsync(fabHost);

        harness.Nav.GoTo("/settings");
        await harness.RenderSignedInHostAsync();
        var header = await harness.RenderHeaderAsync();

        var expected = harness.Localize.Get("Coach_OpenNamed", harness.Persona.DisplayName);
        fabName.Should().Be(expected);
        harness.Renderer.AttributeValue(header, SamElementIds.HeaderEntry, "aria-label").Should().Be(expected);
        harness.Renderer.AttributeValue(header, SamElementIds.HeaderEntry, "aria-controls")
            .Should().Be(SamElementIds.Panel);
        harness.Renderer.AttributeValue(header, SamElementIds.HeaderEntry, "aria-expanded")
            .Should().Be("false");
    }

    [Fact]
    public async Task The_header_icon_comes_first_in_the_header_actions()
    {
        await using var harness = new Harness();
        harness.Nav.GoTo("/settings");
        await harness.RenderSignedInHostAsync();

        RenderFragment toolbar = builder =>
        {
            builder.OpenElement(0, "button");
            builder.AddAttribute(1, "id", "page-own-action");
            builder.CloseElement();
        };
        var header = await harness.Renderer.RenderAsync<PageHeader>(ParameterView.FromDictionary(
            new Dictionary<string, object?> { ["Title"] = "Settings", ["ToolbarActions"] = toolbar }));

        var ids = harness.Renderer.RenderedElementIds(header).ToList();
        ids.IndexOf(SamElementIds.HeaderEntry).Should().BeGreaterThanOrEqualTo(0)
            .And.BeLessThan(ids.IndexOf("page-own-action"));
    }

    [Fact]
    public async Task Clicking_the_header_icon_opens_the_panel_through_the_same_path_and_hides_the_icon()
    {
        await using var harness = new Harness();
        harness.Nav.GoTo("/settings");
        var host = await harness.RenderSignedInHostAsync();
        var header = await harness.RenderHeaderAsync();

        await harness.Renderer.ClickButtonByIdAsync(header, SamElementIds.HeaderEntry);

        harness.Renderer.HasElementWithId(host, SamElementIds.Panel).Should().BeTrue();
        harness.Workspace.IsOpen.Should().BeTrue("the panel resumed or started a conversation");
        harness.Workspace.InvokerElementId.Should().Be(SamElementIds.HeaderEntry,
            "the host's own open path was given the control that asked");
        harness.Js.FirstArgOf("focusElement").Should().Be(CoachElementIds.Composer);
        harness.Renderer.HasElementWithId(header, SamElementIds.HeaderEntry).Should().BeFalse(
            "no entry shows while Sam's panel is open");

        await harness.Renderer.ClickButtonByIdAsync(host, SamElementIds.PanelClose);

        harness.Renderer.HasElementWithId(host, SamElementIds.Panel).Should().BeFalse();
        harness.Renderer.HasElementWithId(header, SamElementIds.HeaderEntry).Should().BeTrue();
        harness.Js.ModuleCalls.Last(c => c.Identifier == "focusElement").Args?.FirstOrDefault()
            .Should().Be(SamElementIds.HeaderEntry, "focus returns to the entry on this page");
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    [Fact]
    public async Task Closing_on_a_main_page_returns_focus_to_the_floating_button()
    {
        await using var harness = new Harness();
        var host = await harness.RenderSignedInHostAsync();

        await harness.Renderer.ClickButtonByIdAsync(host, SamElementIds.Fab);
        harness.Workspace.InvokerElementId.Should().Be(SamElementIds.Fab);
        await harness.Renderer.ClickButtonByIdAsync(host, SamElementIds.PanelClose);

        harness.Js.ModuleCalls.Last(c => c.Identifier == "focusElement").Args?.FirstOrDefault()
            .Should().Be(SamElementIds.Fab);
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    /// <summary>
    /// The bottom room belongs to the page (app.css keys it to .sam-entry-room), so opening the
    /// panel, which takes the floating button away, never moves the page.
    /// </summary>
    [Fact]
    public async Task The_main_pages_keep_their_room_while_the_panel_is_open()
    {
        await using var harness = new Harness();
        var host = await harness.RenderSignedInHostAsync();
        harness.Renderer.HasElementWithId(host, SamElementIds.EntryRoom).Should().BeTrue();

        await harness.Renderer.ClickButtonByIdAsync(host, SamElementIds.Fab);

        harness.Renderer.HasElementWithId(host, SamElementIds.Fab).Should().BeFalse();
        harness.Renderer.HasElementWithId(host, SamElementIds.EntryRoom).Should().BeTrue();
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    [Fact]
    public async Task A_header_page_has_no_room_for_a_floating_button()
    {
        await using var harness = new Harness();
        harness.Nav.GoTo("/settings");
        var host = await harness.RenderSignedInHostAsync();

        harness.Renderer.HasElementWithId(host, SamElementIds.EntryRoom).Should().BeFalse();
    }

    [Fact]
    public async Task An_unread_answer_marks_the_header_icon_like_the_floating_button()
    {
        await using var harness = new Harness();
        harness.Nav.GoTo("/settings");
        var host = await harness.RenderSignedInHostAsync();
        var header = await harness.RenderHeaderAsync();
        await harness.Renderer.ClickButtonByIdAsync(header, SamElementIds.HeaderEntry);
        await harness.Renderer.ClickButtonByIdAsync(host, SamElementIds.PanelClose);
        harness.Renderer.RenderedClassesInOrder(header).Should().NotContain("sam-header-entry__badge");

        // Sam's workspace changes while the panel is closed: the host counts that as unread.
        harness.Workspace.Draft = "hello";
        await harness.Renderer.Dispatcher.InvokeAsync(() => harness.Workspace.SendDraftAsync());
        await harness.Renderer.Dispatcher.InvokeAsync(() => Task.CompletedTask);

        harness.Renderer.RenderedClassesInOrder(header).Should().Contain("sam-header-entry__badge");
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    // ================================================================ harness

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        public Harness()
        {
            var client = new FakeCoachApiClient
            {
                DurableHistoryAvailable = true,
                Availability = new CoachAvailabilityResponse
                {
                    IsAvailable = true,
                    State = CoachAvailabilityState.Available,
                    CanEditPlan = true,
                    IsDurableHistoryAvailable = true,
                    IsSamOverlayAvailable = true
                }
            };

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ICoachApiClient>(client);
            services.AddScoped<BlazorLocalizationService>();
            services.AddScoped<CoachPersona>();
            services.AddScoped<Microsoft.JSInterop.IJSRuntime>(_ => Js);
            services.AddScoped<NavigationManager, TestNavigationManager>();
            services.AddScoped<AuthenticationStateProvider, TestAuthenticationStateProvider>();
            services.AddScoped<CoachFeatureFlags>();
            services.AddScoped<CoachConversationDirectory>();
            services.AddScoped<CoachMemoryDirectory>();
            services.AddScoped<CoachWorkspaceState>();
            services.AddScoped<CoachAccountBoundary>();
            services.AddScoped<SamEntryState>();
            _provider = services.BuildServiceProvider();

            Auth = (TestAuthenticationStateProvider)_provider.GetRequiredService<AuthenticationStateProvider>();
            Nav = (TestNavigationManager)_provider.GetRequiredService<NavigationManager>();
            Workspace = _provider.GetRequiredService<CoachWorkspaceState>();
            Localize = _provider.GetRequiredService<BlazorLocalizationService>();
            Persona = _provider.GetRequiredService<CoachPersona>();
            Renderer = new InteractiveTestRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }

        public StubJSRuntime Js { get; } = new();

        public TestAuthenticationStateProvider Auth { get; }

        public TestNavigationManager Nav { get; }

        public CoachWorkspaceState Workspace { get; }

        public BlazorLocalizationService Localize { get; }

        public CoachPersona Persona { get; }

        public InteractiveTestRenderer Renderer { get; }

        public async Task<int> RenderSignedInHostAsync()
        {
            Auth.SignIn("profile-a", "a@example.test");
            var id = await Renderer.RenderAsync<SamOverlayHost>();
            // The panel picks its opening size from the measured width; the stub answers zero.
            await Renderer.Dispatcher.InvokeAsync(() =>
                ((SamOverlayHost)Renderer.LastRootComponent!).OnViewportChanged(1200));
            return id;
        }

        public Task<int> RenderHeaderAsync() =>
            Renderer.RenderAsync<PageHeader>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { ["Title"] = "Page" }));

        public async ValueTask DisposeAsync()
        {
            Renderer.Dispose();
            await _provider.DisposeAsync();
        }
    }
}
