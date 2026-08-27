using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// The C# half of the follow-the-latest-message contract: which interop calls the conversation
/// makes, and when.
/// </summary>
/// <remarks>
/// <para>
/// The decisions themselves are pure JavaScript and are covered in
/// <c>tests/js/coach-autoscroll.test.js</c>. What can only be asserted from here is the wiring: the
/// observer is started against the conversation element, the jump control is only reachable when
/// there is something to jump to, and the prepend boundary is announced around loading older
/// messages.
/// </para>
/// <para>
/// That boundary is the part most easily lost in a refactor. A page of history grows the content by
/// exactly as much as a long new answer does, so without the explicit begin/end the reader is told
/// there are new messages below when what actually happened is that they asked to see older ones.
/// </para>
/// </remarks>
public class CoachAutoScrollInteropTests
{
    private sealed class Harness : IAsyncDisposable
    {
        private readonly Microsoft.Extensions.DependencyInjection.ServiceProvider _provider;

        public Harness()
        {
            Client = new FakeCoachApiClient { DurableHistoryAvailable = true };
            Directory = new CoachConversationDirectory(Client);
            Workspace = new CoachWorkspaceState(Client, Directory);
            Js = new StubJSRuntime();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<BlazorLocalizationService>();
            services.AddScoped<CoachPersona>();
            services.AddScoped<Microsoft.JSInterop.IJSRuntime>(_ => Js);
            services.AddScoped(_ => Workspace);
            services.AddScoped(_ => Directory);

            _provider = services.BuildServiceProvider();
            Renderer = new InteractiveTestRenderer(
                _provider, _provider.GetRequiredService<ILoggerFactory>());
        }

        public FakeCoachApiClient Client { get; }

        public CoachConversationDirectory Directory { get; }

        public CoachWorkspaceState Workspace { get; }

        public StubJSRuntime Js { get; }

        public InteractiveTestRenderer Renderer { get; }

        public async ValueTask DisposeAsync()
        {
            Renderer.Dispose();
            await _provider.DisposeAsync();
        }
    }

    // ================================================================ starting to follow

    [Fact]
    public async Task TheConversationStartsFollowingItselfOnFirstRender()
    {
        await using var harness = new Harness();

        await harness.Renderer.RenderAsync<CoachChatPane>();

        harness.Js.Invocations.Should().Contain("initCoachAutoScroll",
            "nothing follows the conversation until the observer is attached to it");
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    [Fact]
    public async Task TheConversationCarriesTheIdTheObserverIsGiven()
    {
        await using var harness = new Harness();

        var id = await harness.Renderer.RenderAsync<CoachChatPane>();

        harness.Renderer.RenderedElementIds(id).Should().Contain(CoachElementIds.Messages,
            "the element the observer watches has to be addressable");
    }

    // ================================================================ the jump control

    [Fact]
    public async Task TheJumpControlIsPresentButUnreachableUntilThereIsSomethingToJumpTo()
    {
        await using var harness = new Harness();

        var id = await harness.Renderer.RenderAsync<CoachChatPane>();

        harness.Renderer.HasElementWithId(id, CoachElementIds.JumpToLatest).Should().BeTrue(
            "mounting it on demand would change the content height, which is the very signal "
            + "the observer reads as a new message arriving");

        // Blazor omits a false boolean attribute entirely, so presence is the assertion.
        harness.Renderer.AttributesOfElementWithId(id, CoachElementIds.JumpToLatest)
            .Should().Contain("hidden");
        harness.Renderer.AttributeValue(id, CoachElementIds.JumpToLatest, "tabindex")
            .Should().Be("-1", "a hidden control must not be a tab stop");
    }

    [Fact]
    public async Task TheJumpControlBecomesReachableWhenTheConversationMovedOnWithoutTheReader()
    {
        await using var harness = new Harness();

        var id = await harness.Renderer.RenderAsync<CoachChatPane>();
        var pane = (CoachChatPane)harness.Renderer.LastRootComponent!;

        await harness.Renderer.Dispatcher.InvokeAsync(() => pane.OnJumpAffordanceChanged(true));

        harness.Renderer.AttributesOfElementWithId(id, CoachElementIds.JumpToLatest)
            .Should().NotContain("hidden");
        harness.Renderer.AttributeValue(id, CoachElementIds.JumpToLatest, "tabindex")
            .Should().Be("0");
        harness.Renderer.AttributeValue(id, CoachElementIds.JumpToLatest, "aria-label")
            .Should().NotBeNullOrWhiteSpace("a screen-reader user needs the same way back");
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    [Fact]
    public async Task ActivatingTheJumpControlScrollsToTheLatestMessage()
    {
        await using var harness = new Harness();

        var id = await harness.Renderer.RenderAsync<CoachChatPane>();
        var pane = (CoachChatPane)harness.Renderer.LastRootComponent!;
        await harness.Renderer.Dispatcher.InvokeAsync(() => pane.OnJumpAffordanceChanged(true));

        await harness.Renderer.ClickButtonByIdAsync(id, CoachElementIds.JumpToLatest);

        harness.Js.Invocations.Should().Contain("scrollCoachToLatest");
        harness.Renderer.AttributeValue(id, CoachElementIds.JumpToLatest, "tabindex")
            .Should().Be("-1", "the control withdraws once it has done its job");
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    // ================================================================ history prepend

    [Fact]
    public async Task LoadingOlderMessagesSuspendsFollowingAroundThePrepend()
    {
        await using var harness = new Harness();
        harness.Client.AddConversation("c-1");

        for (var i = 1; i <= 60; i++)
        {
            harness.Client.Seed("c-1", CoachMessageRole.Learner, $"message {i}");
        }

        await harness.Workspace.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        var id = await harness.Renderer.RenderAsync<CoachChatPane>();
        harness.Workspace.HasEarlierMessages.Should().BeTrue("the fixture has more than one page");

        harness.Js.Invocations.Clear();

        await harness.Renderer.ClickButtonAsync(
            id, new BlazorLocalizationService()["Coach_LoadEarlier"]);

        var calls = harness.Js.Invocations;

        calls.Should().Contain("beginCoachHistoryPrepend");
        calls.Should().Contain("endCoachHistoryPrepend");
        calls.IndexOf("beginCoachHistoryPrepend")
            .Should().BeLessThan(calls.IndexOf("endCoachHistoryPrepend"),
                "the boundary has to be open across the insertion, not after it");

        calls.Should().Contain("restoreScrollAnchor",
            "the reader keeps looking at the message they were reading");
        calls.IndexOf("restoreScrollAnchor")
            .Should().BeLessThan(calls.IndexOf("endCoachHistoryPrepend"),
                "position is restored before following resumes, or the re-baseline reads the "
                + "pre-restore offset");

        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    /// <summary>
    /// A failed page load must not leave following switched off for the rest of the session.
    /// </summary>
    [Fact]
    public async Task FollowingResumesEvenWhenTheOlderPageFailsToLoad()
    {
        await using var harness = new Harness();
        harness.Client.AddConversation("c-1");

        for (var i = 1; i <= 60; i++)
        {
            harness.Client.Seed("c-1", CoachMessageRole.Learner, $"message {i}");
        }

        await harness.Workspace.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        var id = await harness.Renderer.RenderAsync<CoachChatPane>();
        harness.Js.Invocations.Clear();

        harness.Client.OnGetConversationMessages = (_, _, _) =>
            throw new InvalidOperationException("page unavailable");

        await harness.Renderer.ClickButtonAsync(
            id, new BlazorLocalizationService()["Coach_LoadEarlier"]);

        harness.Js.Invocations.Should().Contain("endCoachHistoryPrepend",
            "a suspended observer that is never resumed stops the conversation keeping up");
    }

    // ================================================================ teardown

    [Fact]
    public async Task DisposingTheConversationStopsTheObserver()
    {
        await using var harness = new Harness();

        var id = await harness.Renderer.RenderAsync<CoachChatPane>();
        harness.Js.Invocations.Clear();

        await harness.Renderer.DisposeRootComponentAsync(id);

        harness.Js.Invocations.Should().Contain("disposeCoachAutoScroll",
            "an observer left running holds the element and the circuit reference");
        harness.Renderer.Unhandled.Should().BeEmpty();
    }
}
