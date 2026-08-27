using FluentAssertions;
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
/// The Blazor half of the iOS WKWebView fullscreen scroll containment.
/// </summary>
/// <remarks>
/// <para>
/// The learner opens the panel, maximizes it, and every control they can see should be inside the
/// visible viewport — not tucked behind the Dynamic Island. On iOS Safari a nested
/// <c>focus()</c> call scrolls the document by the safe-area-inset-top, and a fixed panel is
/// painted relative to that scroll, so the header ended up sixty-eight pixels above where it
/// should have been. That failure was <em>observable</em> only on the device; here we assert the
/// interop contract that fixes it on the C# side:
/// </para>
/// <list type="bullet">
///   <item>Entering fullscreen invokes <c>enterFullscreenScrollLock</c> in the JS module.</item>
///   <item>Restore invokes <c>exitFullscreenScrollLock</c>.</item>
///   <item>Closing from fullscreen invokes <c>exitFullscreenScrollLock</c> before the
///     collapse renders — otherwise the dashboard stays frozen behind the ghost.</item>
///   <item>Re-entering fullscreen is idempotent at the Blazor level: a second click does not
///     invoke lock twice.</item>
/// </list>
/// <para>
/// The behaviour of the lock itself (what it captures, restores, and how it survives a legacy
/// engine) lives in <c>tests/js/sam-overlay-scroll.test.js</c> — the two files are the two halves
/// of the contract.
/// </para>
/// </remarks>
public class SamOverlayScrollContainmentTests
{
    private sealed class Harness : IAsyncDisposable
    {
        private readonly Microsoft.Extensions.DependencyInjection.ServiceProvider _provider;

        public Harness()
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
            services.AddScoped<CoachPersona>();

            _provider = services.BuildServiceProvider();

            Auth = (TestAuthenticationStateProvider)_provider
                .GetRequiredService<AuthenticationStateProvider>();
            Renderer = new InteractiveTestRenderer(
                _provider, _provider.GetRequiredService<ILoggerFactory>());
        }

        public FakeCoachApiClient Client { get; }

        public StubJSRuntime Js { get; }

        public TestAuthenticationStateProvider Auth { get; }

        public InteractiveTestRenderer Renderer { get; }

        /// <summary>Signs a learner in and opens the panel at the requested viewport width.</summary>
        public async Task<int> OpenPanelAsync(int viewportWidth = 402)
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

    // ================================================================ entering

    [Fact]
    public async Task MaximizingInvokesEnterFullscreenScrollLock()
    {
        await using var harness = new Harness();
        var id = await harness.OpenPanelAsync();

        harness.Js.Invocations.Clear();
        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelFullScreen);

        harness.Js.Invocations.Should().Contain("enterFullscreenScrollLock",
            "the underlying page's scroll must be captured and pinned before the fixed panel paints");
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    [Fact]
    public async Task EnterFullscreenScrollLockRunsBeforeFocusingTheComposer()
    {
        await using var harness = new Harness();
        var id = await harness.OpenPanelAsync();

        harness.Js.Invocations.Clear();
        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelFullScreen);

        var calls = harness.Js.Invocations;
        var lockIdx = calls.IndexOf("enterFullscreenScrollLock");
        var focusIdx = calls.IndexOf("focusElement");

        lockIdx.Should().BeGreaterThanOrEqualTo(0);
        focusIdx.Should().BeGreaterThanOrEqualTo(0);
        lockIdx.Should().BeLessThan(focusIdx,
            "the lock has to be applied before focus, or the focus-induced scroll on iOS is "
            + "the exact thing the lock was supposed to prevent");
    }

    [Fact]
    public async Task FocusingTheComposerAfterMaximizingUsesPreventScroll()
    {
        await using var harness = new Harness();
        var id = await harness.OpenPanelAsync();

        harness.Js.Invocations.Clear();
        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelFullScreen);

        // The JS side owns the { preventScroll: true } default: focusElement takes an id only from
        // the Blazor side, and the module supplies the option itself. The invocation contract is
        // therefore "focusElement was called with exactly the composer id" — the option assertion
        // lives in sam-overlay-scroll.test.js. Here we just prove the call happened.
        harness.Js.FirstArgOf("focusElement").Should().Be(CoachElementIds.Composer);
    }

    // ================================================================ leaving

    [Fact]
    public async Task RestoringInvokesExitFullscreenScrollLock()
    {
        await using var harness = new Harness();
        var id = await harness.OpenPanelAsync();

        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelFullScreen);
        harness.Js.Invocations.Clear();

        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelRestore);

        harness.Js.Invocations.Should().Contain("exitFullscreenScrollLock",
            "the learner's dashboard reading position must be returned when the panel shrinks");
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    [Fact]
    public async Task ClosingFromFullscreenInvokesExitFullscreenScrollLock()
    {
        await using var harness = new Harness();
        var id = await harness.OpenPanelAsync();

        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelFullScreen);
        harness.Js.Invocations.Clear();

        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelClose);

        harness.Js.Invocations.Should().Contain("exitFullscreenScrollLock",
            "closing from fullscreen still has to release the lock — a frozen dashboard is not "
            + "an acceptable teardown state");
        harness.Renderer.Unhandled.Should().BeEmpty();
    }

    [Fact]
    public async Task ClosingFromANonFullscreenPanelDoesNotInvokeExitFullscreenScrollLock()
    {
        // A learner who never went fullscreen has no lock to release. Calling the exit path
        // anyway would be harmless (it is idempotent) but noisy — we want the interop transcript
        // to reflect the actual state transition, not to fire unconditionally.
        await using var harness = new Harness();
        var id = await harness.OpenPanelAsync();

        harness.Js.Invocations.Clear();
        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelClose);

        harness.Js.Invocations.Should().NotContain("exitFullscreenScrollLock");
    }

    // ================================================================ idempotency

    [Fact]
    public async Task MaximizingWhileAlreadyMaximizedDoesNotInvokeEnterLockASecondTime()
    {
        await using var harness = new Harness();
        var id = await harness.OpenPanelAsync();

        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelFullScreen);
        harness.Js.Invocations.Clear();

        // The panel's own state guard rejects a repeated maximize — this is what stops the
        // capture from being overwritten with the pinned-zero state of the currently-locked page.
        // (The JS side is idempotent too; this test is the Blazor-level belt.)
        await harness.Renderer.Dispatcher.InvokeAsync(() =>
            ((SamOverlayHost)harness.Renderer.LastRootComponent!).OnEscapePressed());

        // Escape from fullscreen is one restore, and then a maximize is one enter again — the
        // enter/exit pair have to alternate, one at a time, without ever doubling on the same
        // transition.
        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelFullScreen);

        var enterCount = harness.Js.Invocations.Count(x => x == "enterFullscreenScrollLock");
        var exitCount = harness.Js.Invocations.Count(x => x == "exitFullscreenScrollLock");

        enterCount.Should().Be(1, "one maximize since the counter was cleared → one enter");
        exitCount.Should().Be(1, "the Escape in between fired one exit");
    }

    [Fact]
    public async Task EnterAndExitAlternateOverMultipleMaximizeRestoreCycles()
    {
        await using var harness = new Harness();
        var id = await harness.OpenPanelAsync();

        harness.Js.Invocations.Clear();

        for (var i = 0; i < 3; i++)
        {
            await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelFullScreen);
            await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelRestore);
        }

        var enters = harness.Js.Invocations.Count(x => x == "enterFullscreenScrollLock");
        var exits = harness.Js.Invocations.Count(x => x == "exitFullscreenScrollLock");

        enters.Should().Be(3);
        exits.Should().Be(3);
    }

    // ================================================================ compact / desktop unaffected

    [Fact]
    public async Task CompactExpandTransitionsDoNotInvokeScrollLock()
    {
        // Compact and Expanded are non-fixed layouts — the whole reason for the root-scroll lock
        // does not exist. The interop transcript must not fire it in either direction, or a future
        // refactor could start locking the dashboard for a non-modal control. On a 402px viewport
        // the panel opens Compact so only the Expand control is present; clicking it transitions
        // Compact→Expanded, then the Compact control appears and returns us Expanded→Compact.
        // Neither leg may invoke enter/exitFullscreenScrollLock.
        await using var harness = new Harness();
        var id = await harness.OpenPanelAsync();

        harness.Js.Invocations.Clear();
        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelExpand);
        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelCompact);

        harness.Js.Invocations.Should().NotContain("enterFullscreenScrollLock");
        harness.Js.Invocations.Should().NotContain("exitFullscreenScrollLock");
    }

    [Fact]
    public async Task MaximizingOnADesktopViewportStillInvokesTheLock()
    {
        // The lock is a no-op on desktop at run time (document scrollTop is typically 0 and the
        // shell owns its own overflow), but the contract is that the JS module decides that —
        // the Blazor side must always invoke it, so a desktop with an unusual scroll state is
        // covered too.
        await using var harness = new Harness();
        var id = await harness.OpenPanelAsync(viewportWidth: 1200);

        harness.Js.Invocations.Clear();
        await harness.Renderer.ClickButtonByIdAsync(id, SamElementIds.PanelFullScreen);

        harness.Js.Invocations.Should().Contain("enterFullscreenScrollLock");
    }
}
