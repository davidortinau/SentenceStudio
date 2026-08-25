using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// The cross-account leak, reproduced the way it actually happens: ONE dependency-injection scope
/// that outlives sign-out.
/// </summary>
/// <remarks>
/// <para>
/// Every coach service is registered scoped, which is per-learner in Blazor Server because a
/// circuit is one visit. In the MAUI BlazorWebView it is not: the scope is built once when the app
/// starts and survives sign-out, token expiry, and signing in as somebody else. Tests that build a
/// fresh <see cref="CoachWorkspaceState"/> per account can never see this — the leak is not in
/// what the services do, it is in how long they live — so every test in this file resolves its
/// services from a single scope and never rebuilds one.
/// </para>
/// <para>
/// What must not survive the boundary is content, not just identifiers: the decrypted transcript,
/// the conversation title, the proposal cards and their approval controls, the one-use
/// confirmation in hand, and the availability answers that decide whether any of those may be
/// drawn at all.
/// </para>
/// </remarks>
public class CoachAccountBoundaryTests
{
    private const string LearnerA = "profile-a";
    private const string LearnerB = "profile-b";

    private const string ConversationA = "conversation-a";
    private const string ConversationB = "conversation-b";

    private const string TitleA = "Refund letter for my landlord";
    private const string TitleB = "Ordering coffee politely";

    private const string TranscriptA = "How do I say 'I would like a refund' politely?";
    private const string TranscriptB = "How do I order an iced americano?";

    private const string WriteA = "op-a";

    // ================================================================ harness

    /// <summary>
    /// One provider, one scope, one set of coach services. The scope is deliberately never
    /// recreated: that is the whole point.
    /// </summary>
    private sealed class PersistentScope : IAsyncDisposable
    {
        private readonly Microsoft.Extensions.DependencyInjection.ServiceProvider _provider;
        private readonly IServiceScope _scope;

        public PersistentScope()
        {
            Client = new FakeCoachApiClient { DurableHistoryAvailable = true };

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ICoachApiClient>(Client);
            services.AddScoped<BlazorLocalizationService>();
            // The coach's name comes from the learner's study language, so every component that
            // names it needs the resolver. The all-optional constructor makes this a one-liner:
            // with no language source it answers with the default persona.
            services.AddScoped<CoachPersona>();
            services.AddScoped<Microsoft.JSInterop.IJSRuntime>(_ => new StubJSRuntime());
            services.AddScoped<AuthenticationStateProvider, TestAuthenticationStateProvider>();
            services.AddScoped<CoachFeatureFlags>();
            services.AddScoped<CoachConversationDirectory>();
            services.AddScoped<CoachMemoryDirectory>();
            services.AddScoped<CoachWorkspaceState>();
            services.AddScoped<CoachAccountBoundary>();

            _provider = services.BuildServiceProvider();
            _scope = _provider.CreateScope();

            Auth = (TestAuthenticationStateProvider)_scope.ServiceProvider
                .GetRequiredService<AuthenticationStateProvider>();
            Flags = _scope.ServiceProvider.GetRequiredService<CoachFeatureFlags>();
            Directory = _scope.ServiceProvider.GetRequiredService<CoachConversationDirectory>();
            Memory = _scope.ServiceProvider.GetRequiredService<CoachMemoryDirectory>();
            Workspace = _scope.ServiceProvider.GetRequiredService<CoachWorkspaceState>();
            Boundary = _scope.ServiceProvider.GetRequiredService<CoachAccountBoundary>();
        }

        public FakeCoachApiClient Client { get; }

        public TestAuthenticationStateProvider Auth { get; }

        public CoachFeatureFlags Flags { get; }

        public CoachConversationDirectory Directory { get; }

        public CoachMemoryDirectory Memory { get; }

        public CoachWorkspaceState Workspace { get; }

        public CoachAccountBoundary Boundary { get; }

        public IServiceProvider Services => _scope.ServiceProvider;

        public async ValueTask DisposeAsync()
        {
            _scope.Dispose();
            await _provider.DisposeAsync();
        }
    }

    private static void SeedLearnerA(FakeCoachApiClient client)
    {
        client.Availability = Availability();
        client.AddConversation(ConversationA, title: TitleA, owner: LearnerA);

        var write = client.AddWrite(ConversationA, WriteA, requiresConfirmation: true);
        client.Seed(ConversationA, CoachMessageRole.Learner, TranscriptA);
        client.Seed(ConversationA, CoachMessageRole.Coach, "Try 환불하고 싶은데요.", writeOperation: write);
    }

    private static void SeedLearnerB(FakeCoachApiClient client)
    {
        client.AddConversation(ConversationB, title: TitleB, owner: LearnerB);
        client.Seed(ConversationB, CoachMessageRole.Learner, TranscriptB);
        client.Seed(ConversationB, CoachMessageRole.Coach, "아이스 아메리카노 주세요.");
    }

    private static CoachAvailabilityResponse Availability() => new()
    {
        IsAvailable = true,
        State = CoachAvailabilityState.Available,
        CanEditPlan = true,
        IsDurableHistoryAvailable = true,
        IsMemoryAvailable = true,
        IsSamOverlayAvailable = true,
        IsSamWriteAvailable = true
    };

    /// <summary>Signs learner A in and gets the workspace into the state the defect leaked from.</summary>
    private static async Task OpenLearnerAAsync(PersistentScope scope)
    {
        scope.Client.Owner = LearnerA;
        SeedLearnerA(scope.Client);

        scope.Auth.SignIn(LearnerA, "a@example.test");
        await scope.Boundary.AttachAsync();

        await scope.Flags.EnsureLoadedAsync();
        await scope.Workspace.RefreshAvailabilityAsync();
        await scope.Directory.EnsureLoadedAsync();
        await scope.Workspace.OpenConversationAsync(CoachPresentation.Overlay, ConversationA);
        await scope.Workspace.BeginWriteConfirmationAsync(WriteA);
    }

    private static string TimelineText(CoachWorkspaceState state) =>
        string.Join("\n", state.Timeline.Select(e => e.ReadableText()));

    // ================================================================ the leak

    /// <summary>
    /// Sign in A, load a real conversation, sign out, sign in B: nothing of A's may be readable at
    /// any point, and B resumes only B's thread.
    /// </summary>
    [Fact]
    public async Task Signing_out_and_signing_in_as_somebody_else_leaves_nothing_of_the_first_learner()
    {
        await using var scope = new PersistentScope();

        await OpenLearnerAAsync(scope);

        // ---- what A has on screen -------------------------------------------------
        TimelineText(scope.Workspace).Should().Contain(TranscriptA);
        scope.Workspace.ConversationId.Should().Be(ConversationA);
        scope.Workspace.ConfirmingWriteOperationId.Should().Be(WriteA);
        scope.Workspace.ConfirmationExpiresAtUtc.Should().NotBeNull();
        scope.Workspace.ActiveWriteOperation.Should().NotBeNull();
        scope.Directory.Conversations.Should().ContainSingle(c => c.Title == TitleA);
        scope.Directory.SelectedConversationId.Should().Be(ConversationA);
        scope.Flags.HasLoaded.Should().BeTrue();
        scope.Workspace.Availability.Should().NotBeNull();

        // ---- sign out --------------------------------------------------------------
        scope.Client.Owner = null;
        scope.Auth.SignOut();

        scope.Workspace.Timeline.Should().BeEmpty("the transcript is A's and A has gone");
        scope.Workspace.Messages.Should().BeEmpty();
        scope.Workspace.ConversationId.Should().BeNull();
        scope.Workspace.Conversation.Should().BeNull();
        scope.Workspace.SessionId.Should().BeNull();
        scope.Workspace.IsOpen.Should().BeFalse();
        scope.Workspace.ActiveWriteOperation.Should().BeNull();
        scope.Workspace.ConfirmingWriteOperationId.Should().BeNull(
            "a one-use confirmation outliving the account it was issued to is a credential in hand");
        scope.Workspace.ConfirmationExpiresAtUtc.Should().BeNull();
        scope.Workspace.PendingConfirmation.Should().Be(CoachConfirmation.None);
        scope.Workspace.Draft.Should().BeEmpty();
        scope.Workspace.PendingSuggestion.Should().BeNull();
        scope.Workspace.Availability.Should().BeNull("availability was an answer about A");
        scope.Workspace.IsWriteSurfaceEnabled.Should().BeFalse();

        scope.Directory.Conversations.Should().BeEmpty("a title names a conversation as surely as its text does");
        scope.Directory.SelectedConversationId.Should().BeNull();
        scope.Directory.HasLoaded.Should().BeFalse();
        scope.Directory.IsDurableHistoryAvailable.Should().BeFalse();
        scope.Flags.HasLoaded.Should().BeFalse();
        scope.Flags.IsSamOverlayAvailable.Should().BeFalse();
        scope.Flags.IsSamWriteAvailable.Should().BeFalse();

        // ---- and Sam is not on screen ----------------------------------------------
        scope.Boundary.IsAuthenticated.Should().BeFalse();
        CoachSurfaceGate.Decide(
                scope.Boundary.IsAuthenticated,
                isOnboarding: false,
                isSyncing: false,
                scope.Flags.HasLoaded,
                scope.Flags.IsSamOverlayAvailable)
            .Should().Be(CoachSurface.None);

        // ---- sign in as B ----------------------------------------------------------
        scope.Client.Owner = LearnerB;
        SeedLearnerB(scope.Client);
        scope.Client.MessagePageRequests.Clear();

        scope.Auth.SignIn(LearnerB, "b@example.test");

        await scope.Flags.EnsureLoadedAsync();
        await scope.Workspace.RefreshAvailabilityAsync();
        await scope.Workspace.ResumeMostRecentAsync(CoachPresentation.Overlay);

        scope.Workspace.ConversationId.Should().Be(ConversationB);
        TimelineText(scope.Workspace).Should().Contain(TranscriptB);

        var text = TimelineText(scope.Workspace);
        text.Should().NotContain(TranscriptA);
        text.Should().NotContain(TitleA);
        scope.Directory.Conversations.Should().OnlyContain(c => c.ConversationId == ConversationB);
        scope.Directory.Conversations.Should().NotContain(c => c.Title == TitleA);
        scope.Workspace.Timeline.Should().NotContain(e => e.WriteOperation != null
            && e.WriteOperation.OperationId == WriteA);

        // Only B's thread was ever asked for after the switch.
        scope.Client.MessagePageRequests.Should().OnlyContain(id => id == ConversationB);
        scope.Client.MessagePageRequests.Should().NotBeEmpty();
    }

    /// <summary>
    /// The same clearing, but nothing rendered anywhere either. The assertion is on emitted HTML
    /// because "the state was cleared" and "the previous learner's words are off the screen" are
    /// different claims, and only the second one is the defect.
    /// </summary>
    [Fact]
    public async Task After_the_switch_no_markup_carries_the_previous_learners_content()
    {
        await using var scope = new PersistentScope();

        await OpenLearnerAAsync(scope);

        var beforeSwitch = await RenderChatPaneAsync(scope);
        beforeSwitch.Should().Contain(TranscriptA, "the harness must be showing A's thread to begin with");

        scope.Client.Owner = null;
        scope.Auth.SignOut();

        var signedOut = await RenderChatPaneAsync(scope);
        signedOut.Should().NotContain(TranscriptA);
        signedOut.Should().NotContain(TitleA);
        signedOut.Should().NotContain(WriteA);

        scope.Client.Owner = LearnerB;
        SeedLearnerB(scope.Client);
        scope.Auth.SignIn(LearnerB, "b@example.test");

        await scope.Flags.EnsureLoadedAsync();
        await scope.Workspace.RefreshAvailabilityAsync();
        await scope.Workspace.ResumeMostRecentAsync(CoachPresentation.Overlay);

        var asLearnerB = await RenderChatPaneAsync(scope);
        asLearnerB.Should().Contain(TranscriptB);
        asLearnerB.Should().NotContain(TranscriptA);
        asLearnerB.Should().NotContain(TitleA);
        asLearnerB.Should().NotContain($"sam-write-{WriteA}");
        asLearnerB.Should().NotContain("one-use-" + WriteA,
            "the confirmation value is never rendered, for anybody, ever");
    }

    // ================================================================ how the account ends

    /// <summary>
    /// A rejected refresh token ends the account without anybody pressing anything. The MAUI
    /// provider publishes an empty principal from a background task, and that is the whole signal.
    /// </summary>
    [Fact]
    public async Task An_expired_session_clears_as_hard_as_a_deliberate_sign_out()
    {
        await using var scope = new PersistentScope();

        await OpenLearnerAAsync(scope);
        TimelineText(scope.Workspace).Should().Contain(TranscriptA);

        // No SignOut() call: this is the refresh-rejected path, which publishes anonymous.
        scope.Auth.SignOut();

        scope.Workspace.Timeline.Should().BeEmpty();
        scope.Workspace.ConversationId.Should().BeNull();
        scope.Workspace.ConfirmingWriteOperationId.Should().BeNull();
        scope.Directory.Conversations.Should().BeEmpty();
        scope.Boundary.IsAuthenticated.Should().BeFalse();
    }

    /// <summary>
    /// Signing in as a different learner with no signed-out step in between. A defence that only
    /// hooks the logout button misses this entirely.
    /// </summary>
    [Fact]
    public async Task Signing_in_as_another_account_without_signing_out_still_clears()
    {
        await using var scope = new PersistentScope();

        await OpenLearnerAAsync(scope);
        TimelineText(scope.Workspace).Should().Contain(TranscriptA);

        scope.Client.Owner = LearnerB;
        scope.Auth.SignIn(LearnerB, "b@example.test");

        scope.Workspace.Timeline.Should().BeEmpty();
        scope.Workspace.ConversationId.Should().BeNull();
        scope.Directory.Conversations.Should().BeEmpty();
        scope.Flags.HasLoaded.Should().BeFalse();
        scope.Boundary.IsAuthenticated.Should().BeTrue("B is signed in; it is A's content that had to go");
    }

    /// <summary>
    /// The typed-identity rule, end to end: a second learner whose display name happens to be the
    /// first learner's email address is still a second learner, and still gets a cleared surface.
    /// </summary>
    /// <remarks>
    /// Pinned at the boundary as well as on the identity type because this is the shape the leak
    /// would take if the untyped token bucket ever came back — the two accounts would "match", the
    /// boundary would never fire, and A's transcript would render for B with nothing having thrown.
    /// </remarks>
    [Fact]
    public async Task A_display_name_that_copies_the_previous_learners_email_does_not_suppress_the_clear()
    {
        await using var scope = new PersistentScope();

        await OpenLearnerAAsync(scope);
        TimelineText(scope.Workspace).Should().Contain(TranscriptA);

        var crossings = 0;
        scope.Boundary.Crossed += _ => crossings++;

        scope.Client.Owner = LearnerB;
        scope.Auth.SignIn(LearnerB, "b@example.test", displayName: "a@example.test");

        crossings.Should().Be(1, "a display name is not an identity match");
        scope.Workspace.Timeline.Should().BeEmpty();
        scope.Workspace.ConversationId.Should().BeNull();
        scope.Workspace.ConfirmingWriteOperationId.Should().BeNull();
        scope.Directory.Conversations.Should().BeEmpty();
        scope.Flags.HasLoaded.Should().BeFalse();
    }

    /// <summary>
    /// The mirror image: a genuine token refresh for the same learner still costs nothing, even
    /// when the refreshed principal renames the learner.
    /// </summary>
    [Fact]
    public async Task Renaming_the_same_learner_is_not_an_account_change()
    {
        await using var scope = new PersistentScope();

        await OpenLearnerAAsync(scope);

        var crossings = 0;
        scope.Boundary.Crossed += _ => crossings++;

        scope.Auth.SignIn(LearnerA, "a@example.test", displayName: "Jayne (she/her)");

        crossings.Should().Be(0, "the profile id and the email both still say it is A");
        TimelineText(scope.Workspace).Should().Contain(TranscriptA);
        scope.Workspace.ConversationId.Should().Be(ConversationA);
    }

    /// <summary>
    /// The hole a component-owned subscription leaves: the sign-in happened on a screen the coach
    /// shell was never mounted under, so the first principal this ever sees is already the new
    /// learner's.
    /// </summary>
    [Fact]
    public async Task Attaching_after_the_switch_still_clears_what_the_previous_learner_left()
    {
        await using var scope = new PersistentScope();

        await OpenLearnerAAsync(scope);
        TimelineText(scope.Workspace).Should().Contain(TranscriptA);

        // A second shell mount for a scope that has already changed hands underneath it.
        var late = new CoachAccountBoundary(
            scope.Workspace, scope.Directory, scope.Flags, scope.Memory, scope.Auth);

        scope.Client.Owner = LearnerB;
        scope.Auth.SignIn(LearnerB, "b@example.test");
        scope.Workspace.Timeline.Should().BeEmpty();

        // Re-populate as if the switch had gone unobserved, then let the late attach find it.
        await scope.Workspace.OpenConversationAsync(CoachPresentation.Overlay, ConversationA);

        await late.AttachAsync();

        scope.Workspace.Timeline.Should().BeEmpty("a first observation always clears");
        scope.Workspace.ConversationId.Should().BeNull();

        late.Dispose();
    }

    // ================================================================ what must NOT clear

    /// <summary>
    /// A token refresh for the learner already on screen is not an account change. This is the
    /// MAUI cold-start sequence: optimistic principal first, real token second.
    /// </summary>
    [Fact]
    public async Task A_token_refresh_for_the_same_learner_keeps_the_conversation()
    {
        await using var scope = new PersistentScope();

        scope.Client.Owner = LearnerA;
        SeedLearnerA(scope.Client);

        scope.Auth.SignInOptimistically("a@example.test");
        await scope.Boundary.AttachAsync();

        await scope.Flags.EnsureLoadedAsync();
        await scope.Workspace.RefreshAvailabilityAsync();
        await scope.Workspace.OpenConversationAsync(CoachPresentation.Overlay, ConversationA);
        TimelineText(scope.Workspace).Should().Contain(TranscriptA);

        var crossings = 0;
        scope.Boundary.Crossed += _ => crossings++;

        // The background refresh completes and republishes the same learner as a full JWT.
        scope.Auth.SignIn(LearnerA, "a@example.test");

        crossings.Should().Be(0, "the same learner arriving under a richer principal is not a boundary");
        TimelineText(scope.Workspace).Should().Contain(TranscriptA);
        scope.Workspace.ConversationId.Should().Be(ConversationA);
        scope.Flags.HasLoaded.Should().BeTrue();
    }

    /// <summary>A harmless re-notification for an unchanged principal costs nothing.</summary>
    [Fact]
    public async Task Renotifying_the_same_principal_does_not_clear()
    {
        await using var scope = new PersistentScope();

        await OpenLearnerAAsync(scope);

        var crossings = 0;
        scope.Boundary.Crossed += _ => crossings++;

        scope.Auth.Renotify();
        scope.Auth.Renotify();

        crossings.Should().Be(0);
        TimelineText(scope.Workspace).Should().Contain(TranscriptA);
        scope.Workspace.ConfirmingWriteOperationId.Should().Be(WriteA);
    }

    // ================================================================ subscriptions

    /// <summary>
    /// Attaching repeatedly — every layout rebuild does — must not subscribe twice.
    /// </summary>
    [Fact]
    public async Task Attaching_repeatedly_registers_exactly_one_handler()
    {
        await using var scope = new PersistentScope();

        scope.Auth.SignIn(LearnerA, "a@example.test");
        await scope.Boundary.AttachAsync();
        await scope.Boundary.AttachAsync();
        await scope.Boundary.AttachAsync();

        var crossings = 0;
        scope.Boundary.Crossed += _ => crossings++;

        scope.Auth.SignOut();

        crossings.Should().Be(1, "a duplicate subscription is a duplicate reset and a leak per rebuild");
    }

    /// <summary>
    /// Disposal unsubscribes. A scope that has gone must not still be reacting to the next one.
    /// </summary>
    [Fact]
    public async Task Disposing_stops_the_watch()
    {
        await using var scope = new PersistentScope();

        scope.Auth.SignIn(LearnerA, "a@example.test");
        await scope.Boundary.AttachAsync();

        var crossings = 0;
        scope.Boundary.Crossed += _ => crossings++;

        scope.Boundary.Dispose();
        scope.Auth.SignOut();

        crossings.Should().Be(0);
    }

    /// <summary>
    /// The surfaces are cleared before anything is told the account changed, so a handler that
    /// re-renders can only ever observe the empty state.
    /// </summary>
    [Fact]
    public async Task The_crossed_event_fires_after_the_clear_not_before()
    {
        await using var scope = new PersistentScope();

        await OpenLearnerAAsync(scope);

        var timelineAtNotification = -1;
        string? conversationAtNotification = "unset";

        scope.Boundary.Crossed += _ =>
        {
            timelineAtNotification = scope.Workspace.Timeline.Count;
            conversationAtNotification = scope.Workspace.ConversationId;
        };

        scope.Auth.SignOut();

        timelineAtNotification.Should().Be(0);
        conversationAtNotification.Should().BeNull();
    }

    // ================================================================ render helper

    private static async Task<string> RenderChatPaneAsync(PersistentScope scope)
    {
        var loggerFactory = scope.Services.GetRequiredService<ILoggerFactory>();
        await using var renderer = new HtmlRenderer(scope.Services, loggerFactory);

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<CoachChatPane>(ParameterView.Empty);
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }
}
