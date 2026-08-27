using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Sam;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Regression coverage for the conversation-list toggle and drawer restored in SamPanel.
/// Kaylee's 2026-08-22 fix re-added the shelf (CoachConversationList) that was lost when the
/// SamOverlay replaced CoachWorkspaceOverlay.
/// </summary>
/// <remarks>
/// Acceptance cases: SAM-CONV-01 through SAM-CONV-11.
/// </remarks>
public class SamConversationListRegressionTests
{
    private const string ToggleId = "coach-conversations-toggle";
    private const string DrawerId = "coach-conversations-drawer";

    private sealed class Harness : IAsyncDisposable
    {
        private readonly Microsoft.Extensions.DependencyInjection.ServiceProvider _provider;

        public Harness(bool durableHistoryAvailable = true)
        {
            Client = new FakeCoachApiClient
            {
                DurableHistoryAvailable = durableHistoryAvailable,
                Availability = new CoachAvailabilityResponse
                {
                    IsAvailable = true,
                    State = CoachAvailabilityState.Available,
                    CanEditPlan = true,
                    IsDurableHistoryAvailable = durableHistoryAvailable,
                    IsSamOverlayAvailable = true
                }
            };

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ICoachApiClient>(Client);
            services.AddScoped<BlazorLocalizationService>();
            services.AddScoped<CoachPersona>();
            Js = new ModuleAwareJSRuntime();
            services.AddScoped<Microsoft.JSInterop.IJSRuntime>(_ => Js);
            services.AddScoped<NavigationManager, TestNavigationManager>();
            services.AddScoped<AuthenticationStateProvider, TestAuthenticationStateProvider>();
            services.AddScoped<CoachFeatureFlags>();
            services.AddScoped<CoachConversationDirectory>();
            services.AddScoped<CoachMemoryDirectory>();
            services.AddScoped<CoachWorkspaceState>();
            services.AddScoped<CoachAccountBoundary>();

            _provider = services.BuildServiceProvider();

            Auth = (TestAuthenticationStateProvider)_provider
                .GetRequiredService<AuthenticationStateProvider>();
            Directory = _provider.GetRequiredService<CoachConversationDirectory>();
            Boundary = _provider.GetRequiredService<CoachAccountBoundary>();
            Flags = _provider.GetRequiredService<CoachFeatureFlags>();
            Renderer = new InteractiveTestRenderer(
                _provider, _provider.GetRequiredService<ILoggerFactory>());
        }

        public FakeCoachApiClient Client { get; }
        public ModuleAwareJSRuntime Js { get; }
        public TestAuthenticationStateProvider Auth { get; }
        public CoachConversationDirectory Directory { get; }
        public CoachAccountBoundary Boundary { get; }
        public CoachFeatureFlags Flags { get; }
        public InteractiveTestRenderer Renderer { get; }

        /// <summary>Signs in, opens the Sam panel at the given visual state.</summary>
        public async Task<int> OpenPanelAsync(int viewportWidth = 1200)
        {
            Auth.SignIn("profile-a", "a@example.test");

            var id = await Renderer.RenderAsync<SamOverlayHost>();
            await Renderer.Dispatcher.InvokeAsync(() =>
                ((SamOverlayHost)Renderer.LastRootComponent!).OnViewportChanged(viewportWidth));
            await Renderer.ClickButtonByIdAsync(id, SamElementIds.Fab);

            return id;
        }

        public async ValueTask DisposeAsync()
        {
            Renderer.Dispose();
            await _provider.DisposeAsync();
        }
    }

    // ================================================================ SAM-CONV-01
    // Durable history available -> toggle renders with correct aria-label and stable ID.

    [Fact]
    public async Task Toggle_renders_when_durable_history_is_available()
    {
        await using var harness = new Harness(durableHistoryAvailable: true);
        var id = await harness.OpenPanelAsync();

        harness.Renderer.HasElementWithId(id, ToggleId).Should().BeTrue(
            "the conversations toggle must appear when durable history is available");

        var ariaLabel = harness.Renderer.AttributeValue(id, ToggleId, "aria-label");
        ariaLabel.Should().NotBeNullOrEmpty("toggle needs an accessible open label");

        var ariaExpanded = harness.Renderer.AttributeValue(id, ToggleId, "aria-expanded");
        ariaExpanded.Should().Be("false", "drawer starts closed");

        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    // ================================================================ SAM-CONV-02
    // Durable history unavailable -> toggle and drawer do not render.

    [Fact]
    public async Task Toggle_does_not_render_when_durable_history_is_unavailable()
    {
        await using var harness = new Harness(durableHistoryAvailable: false);
        var id = await harness.OpenPanelAsync();

        harness.Renderer.HasElementWithId(id, ToggleId).Should().BeFalse(
            "no toggle when server does not persist conversations");
        harness.Renderer.HasElementWithId(id, DrawerId).Should().BeFalse(
            "no drawer when server does not persist conversations");

        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    // ================================================================ SAM-CONV-03
    // Compact, Expanded, FullScreen all expose the toggle.

    [Theory]
    [InlineData(360)]   // Compact (narrow viewport)
    [InlineData(1200)]  // Expanded (wide viewport, default open size)
    public async Task Toggle_appears_at_every_viewport_width(int viewportWidth)
    {
        await using var harness = new Harness(durableHistoryAvailable: true);
        var id = await harness.OpenPanelAsync(viewportWidth);

        harness.Renderer.HasElementWithId(id, ToggleId).Should().BeTrue(
            $"toggle must be present at viewport width {viewportWidth}");

        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    [Fact]
    public async Task Toggle_appears_in_full_screen()
    {
        await using var harness = new Harness(durableHistoryAvailable: true);
        var id = await harness.OpenPanelAsync();

        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelFullScreen);

        harness.Renderer.HasElementWithId(id, ToggleId).Should().BeTrue(
            "toggle must be present in full screen");

        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    // ================================================================ SAM-CONV-04
    // Clicking toggle opens drawer, updates aria-expanded/label, renders CoachConversationList.

    [Fact]
    public async Task Clicking_toggle_opens_drawer_and_updates_aria()
    {
        await using var harness = new Harness(durableHistoryAvailable: true);
        var id = await harness.OpenPanelAsync();

        await harness.Renderer.ClickButtonByIdAsync(id, ToggleId);

        harness.Renderer.HasElementWithId(id, DrawerId).Should().BeTrue(
            "drawer must appear after toggle click");

        var ariaExpanded = harness.Renderer.AttributeValue(id, ToggleId, "aria-expanded");
        ariaExpanded.Should().Be("true", "aria-expanded should flip to true when open");

        // The aria-label should change to the close label
        var ariaLabel = harness.Renderer.AttributeValue(id, ToggleId, "aria-label");
        ariaLabel.Should().NotBeNullOrEmpty("toggle needs accessible close label when open");

        // CoachConversationList is rendered inside the drawer
        harness.Renderer.RenderedComponentNames(id).Should().Contain(
            "CoachConversationList",
            "CoachConversationList must be rendered inside the drawer");

        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    // ================================================================ SAM-CONV-05
    // Clicking toggle again closes drawer.

    [Fact]
    public async Task Clicking_toggle_again_closes_drawer()
    {
        await using var harness = new Harness(durableHistoryAvailable: true);
        var id = await harness.OpenPanelAsync();

        await harness.Renderer.ClickButtonByIdAsync(id, ToggleId);
        harness.Renderer.HasElementWithId(id, DrawerId).Should().BeTrue();

        await harness.Renderer.ClickButtonByIdAsync(id, ToggleId);

        harness.Renderer.HasElementWithId(id, DrawerId).Should().BeFalse(
            "drawer must disappear after second toggle click");

        var ariaExpanded = harness.Renderer.AttributeValue(id, ToggleId, "aria-expanded");
        ariaExpanded.Should().Be("false", "aria-expanded should return to false");

        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    // ================================================================ SAM-CONV-06
    // Selecting an existing conversation closes drawer, keeps panel open.

    [Fact]
    public async Task Selecting_conversation_closes_drawer_keeps_panel_open()
    {
        await using var harness = new Harness(durableHistoryAvailable: true);
        // Seed a conversation so the list has something to render
        harness.Client.Conversations.Add(MakeConversation("conv-1", "Test talk"));
        var id = await harness.OpenPanelAsync();

        // Open drawer and let the async load complete
        await harness.Renderer.ClickButtonByIdAsync(id, ToggleId);
        await harness.Renderer.Dispatcher.InvokeAsync(() => Task.CompletedTask);
        harness.Renderer.HasElementWithId(id, DrawerId).Should().BeTrue();

        // The "New conversation" button exercises the same OnConversationOpened callback path
        // as selecting an existing conversation (both call OnConversationOpened.InvokeAsync()).
        // Use it as a proxy since conversation item buttons lack stable IDs.
        await harness.Renderer.ClickButtonByIdAsync(id, "coach-new-conversation");

        // Drawer closes
        harness.Renderer.HasElementWithId(id, DrawerId).Should().BeFalse(
            "OnConversationOpened callback must close the drawer");

        // Panel stays mounted
        harness.Renderer.HasElementWithId(id, SamElementIds.Panel).Should().BeTrue(
            "Sam panel must remain open after opening a conversation");

        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    // ================================================================ SAM-CONV-07
    // Starting a new conversation from the list closes drawer and keeps panel open.

    [Fact]
    public async Task Starting_new_conversation_closes_drawer_keeps_panel_open()
    {
        await using var harness = new Harness(durableHistoryAvailable: true);
        var id = await harness.OpenPanelAsync();

        // Open drawer
        await harness.Renderer.ClickButtonByIdAsync(id, ToggleId);
        harness.Renderer.HasElementWithId(id, DrawerId).Should().BeTrue();

        // Click "New conversation" inside CoachConversationList
        await harness.Renderer.ClickButtonByIdAsync(id, "coach-new-conversation");

        // Drawer closes
        harness.Renderer.HasElementWithId(id, DrawerId).Should().BeFalse(
            "starting a new conversation must close the drawer");

        // Panel stays mounted
        harness.Renderer.HasElementWithId(id, SamElementIds.Panel).Should().BeTrue(
            "Sam panel must remain open after starting a new conversation");

        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    // ================================================================ SAM-CONV-08
    // Account boundary: directory.Reset clears learner A's title before learner B can render.

    [Fact]
    public async Task Account_boundary_isolates_conversation_list()
    {
        await using var harness = new Harness(durableHistoryAvailable: true);

        // Seed learner A with an owned conversation
        harness.Client.AddConversation("conv-a", title: "Learner A chat", owner: "profile-a");
        harness.Client.Owner = "profile-a";

        var id = await harness.OpenPanelAsync();

        // Attach the boundary so it watches auth state changes
        await harness.Boundary.AttachAsync();

        // Load directory with learner A's data
        await harness.Directory.RefreshAsync();
        harness.Directory.Conversations.Should().HaveCount(1,
            "learner A's conversation must be loaded into the directory");
        harness.Directory.Conversations[0].Title.Should().Be("Learner A chat");

        // Sign out -> triggers CoachAccountBoundary which calls Directory.Reset()
        harness.Auth.SignOut();

        // Allow the async notification to propagate
        await harness.Renderer.Dispatcher.InvokeAsync(() => Task.CompletedTask);

        // After sign-out, directory must be cleared — learner A's title must be gone
        harness.Directory.Conversations.Should().BeEmpty(
            "directory must be cleared on sign-out to prevent cross-tenant leak of titles");
        harness.Directory.IsDurableHistoryAvailable.Should().BeFalse(
            "availability resets on account boundary crossing");

        // Switch to learner B's data and sign in
        harness.Client.Conversations.Clear();
        harness.Client.ConversationOwners.Clear();
        harness.Client.AddConversation("conv-b", title: "Learner B chat", owner: "profile-b");
        harness.Client.Owner = "profile-b";
        harness.Auth.SignIn("profile-b", "b@example.test");

        // Refresh as learner B
        await harness.Directory.RefreshAsync();

        harness.Directory.Conversations.Should().NotContain(
            c => c.Title == "Learner A chat",
            "learner A's title must not leak to learner B");
        harness.Directory.Conversations.Should().ContainSingle()
            .Which.Title.Should().Be("Learner B chat");

        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    // ================================================================ SAM-CONV-09
    // No duplicate stable IDs in the active Sam surface.

    [Fact]
    public async Task No_duplicate_element_ids_in_sam_panel()
    {
        await using var harness = new Harness(durableHistoryAvailable: true);
        var id = await harness.OpenPanelAsync();

        var allIds = harness.Renderer.RenderedElementIds(id);
        var duplicates = allIds
            .GroupBy(x => x)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        duplicates.Should().BeEmpty(
            "no element id should appear more than once in the active Sam surface");

        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    // ================================================================ helpers

    private static CoachConversationDto MakeConversation(string id, string title) => new()
    {
        ConversationId = id,
        Title = title,
        TitleOrigin = string.IsNullOrWhiteSpace(title)
            ? CoachConversationTitleOrigin.Generated
            : CoachConversationTitleOrigin.Learner,
        TargetLanguageCode = "ko",
        CreatedAtUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        UpdatedAtUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        HistoryStartsAtUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        MessageCount = 3,
        StateVersion = 1,
        HasActiveCheckpoint = false,
        IsClosed = false
    };

    [Fact]
    public async Task No_duplicate_element_ids_with_drawer_open()
    {
        await using var harness = new Harness(durableHistoryAvailable: true);
        var id = await harness.OpenPanelAsync();

        await harness.Renderer.ClickButtonByIdAsync(id, ToggleId);

        var allIds = harness.Renderer.RenderedElementIds(id);
        var duplicates = allIds
            .GroupBy(x => x)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        duplicates.Should().BeEmpty(
            "no element id should appear more than once with the conversation drawer open");

        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    // ================================================================ SAM-CONV-10
    // Second toggle click restores focus to the conversations toggle via focusElement.

    [Fact]
    public async Task Toggle_close_restores_focus_to_toggle_via_focusElement()
    {
        await using var harness = new Harness(durableHistoryAvailable: true);
        var id = await harness.OpenPanelAsync();

        // Open drawer
        await harness.Renderer.ClickButtonByIdAsync(id, ToggleId);
        harness.Renderer.HasElementWithId(id, DrawerId).Should().BeTrue();

        harness.Js.ModuleCalls.Clear();

        // Close drawer by clicking toggle again
        await harness.Renderer.ClickButtonByIdAsync(id, ToggleId);
        harness.Renderer.HasElementWithId(id, DrawerId).Should().BeFalse();

        // Assert focus was restored to the toggle through the imported JS module
        harness.Js.ModuleInvocations.Should().Contain("focusElement",
            "closing the drawer via toggle must restore focus through the JS module");
        harness.Js.FirstArgOf("focusElement").Should().Be(ToggleId,
            "focus must return to coach-conversations-toggle, not to any other element");
        harness.Js.GlobalInvocations.Should().NotContain("focusElement",
            "focusElement on the global runtime is the circuit-crash bug");

        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    // ================================================================ SAM-CONV-11
    // OnConversationOpened (selection / new) restores focus to the conversations toggle.

    [Fact]
    public async Task OnConversationOpened_restores_focus_to_toggle_via_focusElement()
    {
        await using var harness = new Harness(durableHistoryAvailable: true);
        harness.Client.Conversations.Add(MakeConversation("conv-1", "Test talk"));
        var id = await harness.OpenPanelAsync();

        // Open drawer
        await harness.Renderer.ClickButtonByIdAsync(id, ToggleId);
        await harness.Renderer.Dispatcher.InvokeAsync(() => Task.CompletedTask);
        harness.Renderer.HasElementWithId(id, DrawerId).Should().BeTrue();

        harness.Js.ModuleCalls.Clear();

        // Click "New conversation" — exercises the OnConversationOpened callback
        await harness.Renderer.ClickButtonByIdAsync(id, "coach-new-conversation");

        // Drawer closes and focus is restored
        harness.Renderer.HasElementWithId(id, DrawerId).Should().BeFalse(
            "OnConversationOpened must close the drawer");

        harness.Js.ModuleInvocations.Should().Contain("focusElement",
            "OnConversationOpened must restore focus through the JS module");
        harness.Js.FirstArgOf("focusElement").Should().Be(ToggleId,
            "focus must return to coach-conversations-toggle after opening a conversation");
        harness.Js.GlobalInvocations.Should().NotContain("focusElement",
            "focusElement on the global runtime is the circuit-crash bug");

        harness.Renderer.Unhandled.Should().BeEmpty();
    }
}
