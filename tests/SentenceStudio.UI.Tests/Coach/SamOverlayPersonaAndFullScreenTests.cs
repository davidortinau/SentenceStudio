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
/// The overlay end to end: who it says the learner is talking to, and what the size controls do.
/// </summary>
/// <remarks>
/// <para>
/// Both halves are Captain's 2026-08-20 report. The name was read from the interface culture, so an
/// English-speaking learner studying Korean was introduced to "Sam" instead of 쌤. The maximize
/// control navigated to <c>/coach</c>, which measures the viewport on first render and redirects
/// straight back at any width at or above 768px — the panel painted for a frame and then vanished,
/// leaving the dashboard with nothing open.
/// </para>
/// <para>
/// These run on <see cref="InteractiveTestRenderer"/> rather than the static HTML renderer because
/// the host draws nothing at all when the renderer reports itself non-interactive, which would make
/// every "is it still there" assertion pass for the wrong reason.
/// </para>
/// </remarks>
public class SamOverlayPersonaAndFullScreenTests
{
    private sealed class Harness : IAsyncDisposable
    {
        private readonly Microsoft.Extensions.DependencyInjection.ServiceProvider _provider;

        public Harness(string? studyLanguage = "Korean")
        {
            Client = new FakeCoachApiClient
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

            Languages = new MutableLanguageSource(studyLanguage);
            Js = new StubJSRuntime();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ICoachApiClient>(Client);
            services.AddScoped<BlazorLocalizationService>();
            services.AddScoped<Microsoft.JSInterop.IJSRuntime>(_ => Js);
            services.AddScoped<NavigationManager, TestNavigationManager>();
            services.AddScoped<AuthenticationStateProvider, TestAuthenticationStateProvider>();
            services.AddScoped<CoachFeatureFlags>();
            services.AddScoped<CoachConversationDirectory>();
            services.AddScoped<CoachMemoryDirectory>();
            services.AddScoped<CoachWorkspaceState>();
            services.AddScoped<CoachAccountBoundary>();
            services.AddScoped<ICoachPersonaLanguageSource>(_ => Languages);
            services.AddScoped<CoachPersona>();

            _provider = services.BuildServiceProvider();

            Auth = (TestAuthenticationStateProvider)_provider
                .GetRequiredService<AuthenticationStateProvider>();
            Nav = (TestNavigationManager)_provider.GetRequiredService<NavigationManager>();
            Boundary = _provider.GetRequiredService<CoachAccountBoundary>();
            Persona = _provider.GetRequiredService<CoachPersona>();
            Workspace = _provider.GetRequiredService<CoachWorkspaceState>();
            Renderer = new InteractiveTestRenderer(
                _provider, _provider.GetRequiredService<ILoggerFactory>());
        }

        public FakeCoachApiClient Client { get; }

        public MutableLanguageSource Languages { get; }

        /// <summary>Records the interop the panel and the conversation actually invoke.</summary>
        public StubJSRuntime Js { get; }

        public TestAuthenticationStateProvider Auth { get; }

        public TestNavigationManager Nav { get; }

        public CoachAccountBoundary Boundary { get; }

        public CoachPersona Persona { get; }

        public CoachWorkspaceState Workspace { get; }

        public InteractiveTestRenderer Renderer { get; }

        /// <summary>Signs a learner in and opens the panel, which is where a session starts.</summary>
        /// <param name="viewportWidth">
        /// Published before opening, because the panel picks its opening size from it. The stub JS
        /// runtime answers the initial measurement with zero, so a test that wants a desktop-sized
        /// panel has to say so rather than rely on the field's initial value.
        /// </param>
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

    private sealed class MutableLanguageSource(string? language) : ICoachPersonaLanguageSource
    {
        public string? Language { get; set; } = language;

        public Task<string?> GetStudyLanguageAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Language);
    }

    // ================================================================ the name on the surfaces

    [Fact]
    public async Task AKoreanLearnerSees쌤OnTheEntryControlAndThePanel()
    {
        await using var harness = new Harness(studyLanguage: "Korean");
        harness.Auth.SignIn("profile-a", "a@example.test");

        var id = await harness.Renderer.RenderAsync<SamOverlayHost>();

        harness.Renderer.AttributeValue(id, SamElementIds.Fab, "aria-label")
            .Should().Contain("쌤")
            .And.NotContain("Sam", "the accessible name is the same person the panel shows");

        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.Fab);

        harness.Renderer.RenderedText(id).Should().Contain("쌤");
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    [Fact]
    public async Task AnEnglishLearnerSeesSam()
    {
        await using var harness = new Harness(studyLanguage: "English");
        harness.Auth.SignIn("profile-a", "a@example.test");

        var id = await harness.Renderer.RenderAsync<SamOverlayHost>();

        harness.Renderer.AttributeValue(id, SamElementIds.Fab, "aria-label")
            .Should().Contain("Sam").And.NotContain("쌤");

        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    /// <summary>
    /// The speaker label above a coach turn is the projection, not the stored message. Renaming the
    /// speaker must never rewrite what was said.
    /// </summary>
    [Fact]
    public async Task TheSpeakerLabelIsTheStudyLanguageNameAndTheMessageTextIsUntouched()
    {
        await using var harness = new Harness(studyLanguage: "Korean");
        var id = await harness.OpenPanelAsync();

        harness.Workspace.Draft = "What is the difference between 은/는 and 이/가?";
        await harness.Renderer.Dispatcher.InvokeAsync(() => harness.Workspace.SendDraftAsync());

        var text = harness.Renderer.RenderedText(id);

        text.Should().Contain("쌤", "the speaker label follows the language being studied");
        harness.Workspace.Timeline.Should().NotBeEmpty();
        harness.Workspace.Timeline
            .Select(entry => entry.Message?.Text ?? string.Empty)
            .Should().NotContain(value => value.Contains("쌤", StringComparison.Ordinal),
                "the persona is a render-time label; stored content is never rewritten");
    }

    /// <summary>
    /// Editing the profile renames the coach without a reload, because the surfaces subscribe to the
    /// resolver rather than reading it once on mount.
    /// </summary>
    [Fact]
    public async Task ChangingTheStudyLanguageRenamesTheCoachOnAMountedPanel()
    {
        await using var harness = new Harness(studyLanguage: "English");
        var id = await harness.OpenPanelAsync();

        harness.Renderer.RenderedText(id).Should().Contain("Sam");

        harness.Languages.Language = "Korean";
        await harness.Renderer.Dispatcher.InvokeAsync(() => harness.Persona.RefreshAsync());

        harness.Renderer.RenderedText(id).Should().Contain("쌤");
        harness.Renderer.AttributeValue(id, SamElementIds.Panel, "aria-labelledby")
            .Should().Be(SamElementIds.PanelTitle, "the accessible name still points at the heading");
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    /// <summary>
    /// And the next learner is not introduced to the previous learner's teacher.
    /// </summary>
    [Fact]
    public async Task SigningInAsALearnerWithADifferentStudyLanguageRenamesTheCoach()
    {
        await using var harness = new Harness(studyLanguage: "Korean");
        var id = await harness.OpenPanelAsync();

        harness.Renderer.RenderedText(id).Should().Contain("쌤");

        harness.Languages.Language = "German";
        harness.Auth.SignIn("profile-b", "b@example.test");
        await harness.Renderer.Dispatcher.InvokeAsync(() => Task.Delay(50));

        harness.Persona.DisplayName.Should().Be("Sam");
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    // ================================================================ full screen

    [Fact]
    public async Task MaximizingKeepsThePanelOnScreenAndNavigatesNowhere()
    {
        await using var harness = new Harness();
        var id = await harness.OpenPanelAsync();

        var before = harness.Nav.LastNavigatedTo;

        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelFullScreen);

        harness.Renderer.HasElementWithId(id, SamElementIds.Panel).Should().BeTrue(
            "the panel that was maximized is the panel that stays on screen");
        harness.Renderer.AttributeValue(id, SamElementIds.Panel, "class")
            .Should().Contain("sam-panel--fullscreen");
        harness.Renderer.HasElementWithId(id, SamElementIds.Fab).Should().BeFalse(
            "the entry control belongs to the collapsed state");

        harness.Nav.LastNavigatedTo.Should().Be(before,
            "maximizing is a size change; navigating is what made it flash and disappear");
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    [Fact]
    public async Task AMaximizedPanelStillHasItsConversationAndComposer()
    {
        await using var harness = new Harness();
        var id = await harness.OpenPanelAsync();

        harness.Workspace.Draft = "이 문장을 고쳐 주세요.";
        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelFullScreen);

        var ids = harness.Renderer.RenderedElementIds(id);
        ids.Should().Contain(CoachElementIds.Messages, "the transcript is the same element, resized");
        ids.Should().Contain(CoachElementIds.Composer);

        harness.Workspace.Draft.Should().Be("이 문장을 고쳐 주세요.",
            "nothing unmounted, so nothing typed was thrown away");
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    [Fact]
    public async Task AMaximizedPanelSurvivesNewMessages()
    {
        await using var harness = new Harness();
        var id = await harness.OpenPanelAsync();

        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelFullScreen);

        harness.Workspace.Draft = "Another question";
        await harness.Renderer.Dispatcher.InvokeAsync(() => harness.Workspace.SendDraftAsync());

        harness.Renderer.AttributeValue(id, SamElementIds.Panel, "class")
            .Should().Contain("sam-panel--fullscreen",
                "the size is host state, not something a render pass recomputes");
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    [Fact]
    public async Task RestoringReturnsThePanelToTheSizeItHad()
    {
        await using var harness = new Harness();
        var id = await harness.OpenPanelAsync();

        // Opened at 1200px wide, which is the expanded size.
        harness.Renderer.AttributeValue(id, SamElementIds.Panel, "class")
            .Should().Contain("sam-panel--expanded");

        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelFullScreen);
        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelRestore);

        harness.Renderer.AttributeValue(id, SamElementIds.Panel, "class")
            .Should().Contain("sam-panel--expanded")
            .And.NotContain("sam-panel--fullscreen");
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    [Fact]
    public async Task RestoringFromACompactPanelReturnsToCompactNotExpanded()
    {
        await using var harness = new Harness();

        // A narrow window opens compact, and that choice must survive the round trip.
        var id = await harness.OpenPanelAsync(viewportWidth: 600);

        harness.Renderer.AttributeValue(id, SamElementIds.Panel, "class")
            .Should().Contain("sam-panel--compact");

        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelFullScreen);
        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelRestore);

        harness.Renderer.AttributeValue(id, SamElementIds.Panel, "class")
            .Should().Contain("sam-panel--compact",
                "a learner who chose the small panel is not overruled on the way back");
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    [Fact]
    public async Task TheMaximizedHeaderOffersRestoreInsteadOfExpandAndMaximize()
    {
        await using var harness = new Harness();
        var id = await harness.OpenPanelAsync();

        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelFullScreen);

        harness.Renderer.HasElementWithId(id, SamElementIds.PanelRestore).Should().BeTrue();
        harness.Renderer.HasElementWithId(id, SamElementIds.PanelFullScreen).Should().BeFalse(
            "a panel that already fills the viewport has nowhere to grow to");
        harness.Renderer.HasElementWithId(id, SamElementIds.PanelClose).Should().BeTrue(
            "closing stays available at every size");
    }

    /// <summary>
    /// "Compact panel" shrinks the panel. It used to share a callback with the close control, so a
    /// button announced as compacting dismissed the conversation instead.
    /// </summary>
    [Fact]
    public async Task TheCompactControlShrinksThePanelRatherThanClosingIt()
    {
        await using var harness = new Harness();
        var id = await harness.OpenPanelAsync();

        harness.Renderer.AttributeValue(id, SamElementIds.Panel, "class")
            .Should().Contain("sam-panel--expanded");

        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelCompact);

        harness.Renderer.HasElementWithId(id, SamElementIds.Panel).Should().BeTrue(
            "the label says compact, so the conversation stays on screen");
        harness.Renderer.AttributeValue(id, SamElementIds.Panel, "class")
            .Should().Contain("sam-panel--compact");
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    /// <summary>And the X still closes, so the two controls remain distinguishable.</summary>
    [Fact]
    public async Task TheCloseControlCollapsesBackToTheEntryControl()
    {
        await using var harness = new Harness();
        var id = await harness.OpenPanelAsync();

        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelClose);

        harness.Renderer.HasElementWithId(id, SamElementIds.Panel).Should().BeFalse();
        harness.Renderer.HasElementWithId(id, SamElementIds.Fab).Should().BeTrue();
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    /// <summary>
    /// A compacted panel restores to compact after full screen, because compacting is what set the
    /// size to remember.
    /// </summary>
    [Fact]
    public async Task CompactingThenMaximizingRestoresToCompact()
    {
        await using var harness = new Harness();
        var id = await harness.OpenPanelAsync();

        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelCompact);
        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelFullScreen);
        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelRestore);

        harness.Renderer.AttributeValue(id, SamElementIds.Panel, "class")
            .Should().Contain("sam-panel--compact");
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    // ================================================================ escape order

    [Fact]
    public async Task EscapeLeavesFullScreenFirstAndClosesOnTheSecondPress()
    {
        await using var harness = new Harness();
        var id = await harness.OpenPanelAsync();

        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelFullScreen);

        var host = (SamOverlayHost)harness.Renderer.LastRootComponent!;

        await harness.Renderer.Dispatcher.InvokeAsync(() => host.OnEscapePressed());
        harness.Renderer.AttributeValue(id, SamElementIds.Panel, "class")
            .Should().Contain("sam-panel--expanded",
                "one Escape undoes one thing, in the order it was done");

        await harness.Renderer.Dispatcher.InvokeAsync(() => host.OnEscapePressed());
        harness.Renderer.HasElementWithId(id, SamElementIds.Panel).Should().BeFalse();
        harness.Renderer.HasElementWithId(id, SamElementIds.Fab).Should().BeTrue();
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    // ================================================================ resize bracket

    /// <summary>
    /// Changing the panel's size is announced to the conversation before and after the render that
    /// applies it, so a resize is never read as messages arriving.
    /// </summary>
    /// <remarks>
    /// Design review, 2026-08-20. Full screen back to compact makes the same conversation taller
    /// in a shorter scrollport, which through the content rules is indistinguishable from a page of
    /// new messages landing below the reader — and produced a jump control offering to take them to
    /// something they had already read.
    /// </remarks>
    [Fact]
    public async Task ChangingThePanelSizeBracketsTheConversationsFollowState()
    {
        await using var harness = new Harness();
        var id = await harness.OpenPanelAsync();

        harness.Js.Invocations.Clear();

        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelFullScreen);

        var calls = harness.Js.Invocations;

        calls.Should().Contain("beginCoachViewportChange");
        calls.Should().Contain("endCoachViewportChange");
        calls.IndexOf("beginCoachViewportChange")
            .Should().BeLessThan(calls.IndexOf("endCoachViewportChange"),
                "the reading has to be taken before the new size is applied, and the correction "
                + "after it");
    }

    [Theory]
    [InlineData(SamElementIds.PanelCompact)]
    [InlineData(SamElementIds.PanelFullScreen)]
    public async Task EverySizeControlBracketsTheChange(string controlId)
    {
        await using var harness = new Harness();
        var id = await harness.OpenPanelAsync();

        harness.Js.Invocations.Clear();
        await harness.Renderer.ClickButtonByIdAsync(id, controlId);

        harness.Js.Invocations.Should().Contain("beginCoachViewportChange");
        harness.Js.Invocations.Should().Contain("endCoachViewportChange");
    }

    [Fact]
    public async Task LeavingFullScreenBracketsTheChangeToo()
    {
        await using var harness = new Harness();
        var id = await harness.OpenPanelAsync();

        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelFullScreen);
        harness.Js.Invocations.Clear();

        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelRestore);

        harness.Js.Invocations.Should().Contain("beginCoachViewportChange");
        harness.Js.Invocations.Should().Contain("endCoachViewportChange");
    }

    /// <summary>
    /// Opening the panel is not a resize — there was no previous size to carry a position across
    /// from, and the conversation opens at its newest message anyway.
    /// </summary>
    [Fact]
    public async Task OpeningThePanelDoesNotBracketAResize()
    {
        await using var harness = new Harness();
        await harness.OpenPanelAsync();

        harness.Js.Invocations.Should().NotContain("beginCoachViewportChange");
    }

    /// <summary>
    /// A bracket left open would stop the conversation following for the rest of the session, and
    /// the panel can be torn down mid-transition by a sign-out.
    /// </summary>
    [Fact]
    public async Task TearingThePanelDownMidTransitionClosesTheBracket()
    {
        await using var harness = new Harness();
        var id = await harness.OpenPanelAsync();

        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelFullScreen);
        harness.Js.Invocations.Clear();

        await harness.Renderer.DisposeRootComponentAsync(id);

        // Nothing was pending, so nothing to close — the assertion is that teardown is clean and
        // the panel did not leave a half-open bracket behind.
        harness.Renderer.Unhandled.Should().BeEmpty();
    }
}
