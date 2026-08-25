using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Progress;
using MsDi = Microsoft.Extensions.DependencyInjection;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// The focus in its real hosts: the plan canvas that owns it, and the receipt that reports a
/// change to it.
/// </summary>
/// <remarks>
/// The focus is workspace state, not message state. It is shown once, where the plan lives —
/// repeating it under every answer would read as though each turn re-picked the words.
/// </remarks>
public class CoachVocabularyFocusIntegrationTests
{
    private static Microsoft.Extensions.DependencyInjection.ServiceProvider Provider(CoachWorkspaceState state)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BlazorLocalizationService>();
        // The coach's name comes from the learner's study language, so every component that
        // names it needs the resolver. The all-optional constructor makes this a one-liner:
        // with no language source it answers with the default persona.
        services.AddScoped<CoachPersona>();
        services.AddScoped<Microsoft.JSInterop.IJSRuntime>(_ => new StubJSRuntime());
        services.AddScoped(_ => state);
        services.AddScoped<IProgressService>(_ => new StubProgressService());
        return services.BuildServiceProvider();
    }

    private static async Task<string> RenderAsync<TComponent>(CoachWorkspaceState state)
        where TComponent : IComponent
    {
        await using var provider = Provider(state);
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<TComponent>(ParameterView.Empty);
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }

    private static CoachConstraintSetDto ConstraintsWith(CoachVocabularyFocusDto? focus)
    {
        var baseline = CoachStateMachineTests.Constraints();

        return new CoachConstraintSetDto
        {
            AvailableMinutes = baseline.AvailableMinutes,
            AudioAllowed = baseline.AudioAllowed,
            SpeechAllowed = baseline.SpeechAllowed,
            TypingAllowed = baseline.TypingAllowed,
            SkillEmphasis = baseline.SkillEmphasis,
            GoalTag = baseline.GoalTag,
            GoalHorizonDays = baseline.GoalHorizonDays,
            EnergyLevel = baseline.EnergyLevel,
            VocabularyFocus = focus
        };
    }

    /// <summary>
    /// A receipt carrying an explicit focus outcome. The status is what the UI reads; the
    /// changed-fields list is deliberately left alone, so a test that starts depending on it
    /// again would fail.
    /// </summary>
    private static CoachChangeReceiptDto ReceiptWithFocus(
        CoachVocabularyFocusStatus status,
        CoachVocabularyFocusDto? focus,
        CoachRevisionSource source = CoachRevisionSource.DirectRequest)
    {
        var receipt = CoachStateMachineTests.Receipt(source);

        return new CoachChangeReceiptDto
        {
            ReceiptId = receipt.ReceiptId,
            Revision = receipt.Revision,
            Summary = receipt.Summary,
            AppliedDelta = receipt.AppliedDelta,
            Diff = receipt.Diff,
            ReplacedItemCount = receipt.ReplacedItemCount,
            PreservedCompletedItemCount = receipt.PreservedCompletedItemCount,
            PreservedInProgressItemCount = receipt.PreservedInProgressItemCount,
            PreservedMinutesSpent = receipt.PreservedMinutesSpent,
            CanUndo = receipt.CanUndo,
            UndoLabel = receipt.UndoLabel,
            VocabularyFocus = new CoachVocabularyFocusChangeDto { Status = status, Focus = focus }
        };
    }

    private static async Task<CoachWorkspaceState> StateAfterTurnAsync(
        CoachVocabularyFocusDto? focus,
        CoachChangeReceiptDto? receipt = null,
        CoachRevisionSource source = CoachRevisionSource.DirectRequest)
    {
        var client = new FakeCoachApiClient();
        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        var turn = CoachStateMachineTests.Turn(receipt: receipt);
        client.OnSubmitTurn = _ => new CoachTurnResponse
        {
            SessionId = turn.SessionId,
            TurnId = turn.TurnId,
            Status = turn.Status,
            StopReason = turn.StopReason,
            SessionStatus = turn.SessionStatus,
            ActiveConstraints = ConstraintsWith(focus),
            PlanState = turn.PlanState,
            Messages = turn.Messages,
            Evidence = turn.Evidence,
            ChangeReceipt = receipt,
            PendingSuggestion = turn.PendingSuggestion,
            ClarificationsRemaining = turn.ClarificationsRemaining,
            ExpiresAtUtc = turn.ExpiresAtUtc
        };

        state.Draft = "focus today on action verbs";
        await state.SendDraftAsync();

        _ = source;
        return state;
    }

    // ---------------------------------------------------------------- canvas

    [Fact]
    public async Task ThePlanCanvasShowsTheFocusInForce()
    {
        var state = await StateAfterTurnAsync(CoachVocabularyFocusRenderTests.ActionVerbs());
        var html = await RenderAsync<CoachPlanCanvas>(state);

        html.Should().Contain("Today's focus");
        html.Should().Contain("달리다").And.Contain("가다").And.Contain("먹다");
    }

    [Fact]
    public async Task ThePlanCanvasShowsTheFocusExactlyOnce()
    {
        var state = await StateAfterTurnAsync(CoachVocabularyFocusRenderTests.ActionVerbs());
        var html = await RenderAsync<CoachPlanCanvas>(state);

        Regex.Matches(html, "달리다").Count.Should().Be(1,
            "the focus is workspace state, shown once, not restated per item or per turn");
        Regex.Matches(html, "coach-focus-words").Count.Should().Be(1);
    }

    [Fact]
    public async Task ThePlanCanvasShowsNoFocusSectionWhenThereIsNoFocus()
    {
        var state = await StateAfterTurnAsync(focus: null);
        var html = await RenderAsync<CoachPlanCanvas>(state);

        html.Should().NotContain("Today's focus");
        html.Should().NotContain("coach-focus-words");
    }

    [Fact]
    public async Task TheConversationDoesNotRepeatTheFocus()
    {
        var state = await StateAfterTurnAsync(CoachVocabularyFocusRenderTests.ActionVerbs());
        var html = await RenderAsync<CoachChatPane>(state);

        html.Should().NotContain("coach-focus-words",
            "the chat cites the canvas; it does not duplicate it");
    }

    [Fact]
    public async Task TheSameSetSurvivesAReload()
    {
        var client = new FakeCoachApiClient();
        var focus = CoachVocabularyFocusRenderTests.ActionVerbs();

        client.OnGetSession = id =>
        {
            var session = FakeCoachApiClient.Session(id);
            return new CoachSessionResponse
            {
                SessionId = session.SessionId,
                Status = session.Status,
                Messages = session.Messages,
                Evidence = session.Evidence,
                Revisions = session.Revisions,
                ActiveConstraints = ConstraintsWith(focus),
                PlanState = session.PlanState,
                PendingSuggestion = session.PendingSuggestion,
                ClarificationsRemaining = session.ClarificationsRemaining,
                CreatedAtUtc = session.CreatedAtUtc,
                ExpiresAtUtc = session.ExpiresAtUtc
            };
        };

        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay, "session-1");

        var html = await RenderAsync<CoachPlanCanvas>(state);

        html.Should().Contain("달리다").And.Contain("가다").And.Contain("먹다");
        html.Should().Contain("3 of 12 matching words", "a reload shows the set they were given, exactly");
    }

    // ---------------------------------------------------------------- receipt

    [Fact]
    public async Task AnAppliedFocusReceiptShowsTheAppliedSet()
    {
        var state = await StateAfterTurnAsync(
            focus: null,
            ReceiptWithFocus(CoachVocabularyFocusStatus.Applied, CoachVocabularyFocusRenderTests.ActionVerbs()));

        var html = await RenderAsync<CoachChangeReceipt>(state);

        html.Should().Contain("Focus applied");
        html.Should().Contain("달리다").And.Contain("가다").And.Contain("먹다");
    }

    [Fact]
    public async Task ARestoredFocusReceiptShowsTheRestoredSet()
    {
        var state = await StateAfterTurnAsync(
            focus: null,
            ReceiptWithFocus(
                CoachVocabularyFocusStatus.Restored,
                CoachVocabularyFocusRenderTests.ActionVerbs(),
                CoachRevisionSource.Undo));

        var html = await RenderAsync<CoachChangeReceipt>(state);

        html.Should().Contain("Focus restored", "an undo returns a set rather than applying a new one");
        html.Should().Contain("달리다");
    }

    [Fact]
    public async Task AClearedFocusIsReportedAsRemovedNotAsAnEmptyList()
    {
        var state = await StateAfterTurnAsync(
            focus: null,
            ReceiptWithFocus(CoachVocabularyFocusStatus.Cleared, focus: null));

        var html = await RenderAsync<CoachChangeReceipt>(state);

        html.Should().Contain("Focus removed");
        html.Should().NotContain("coach-focus-words", "there is no set to list");
    }

    [Fact]
    public async Task AnUnchangedFocusReceiptShowsNoFocusAtAll()
    {
        // A minutes change did not re-pick the words and must not look as if it did — even
        // though a focus is still in force and still carried on the change.
        var state = await StateAfterTurnAsync(
            CoachVocabularyFocusRenderTests.ActionVerbs(),
            new CoachChangeReceiptDto
            {
                ReceiptId = "r-unchanged",
                Revision = CoachStateMachineTests.Receipt(CoachRevisionSource.DirectRequest).Revision,
                Summary = "Updated 2 items",
                AppliedDelta = new CoachConstraintDeltaDto(),
                Diff = CoachStateMachineTests.Receipt(CoachRevisionSource.DirectRequest).Diff,
                ReplacedItemCount = 2,
                PreservedCompletedItemCount = 1,
                PreservedInProgressItemCount = 0,
                PreservedMinutesSpent = 12,
                CanUndo = true,
                UndoLabel = "Undo",
                VocabularyFocus = CoachVocabularyFocusChangeDto.Unchanged(
                    CoachVocabularyFocusRenderTests.ActionVerbs())
            });

        var html = await RenderAsync<CoachChangeReceipt>(state);

        html.Should().NotContain("Focus applied");
        html.Should().NotContain("Focus removed");
        html.Should().NotContain("달리다", "an untouched focus is not this receipt's business");
    }

    [Fact]
    public async Task AHistoricalReceiptKeepsItsOwnOutcome()
    {
        // The whole point of a per-receipt status: a later change must not relabel an earlier
        // one. The current state here says "cleared", and the old receipt must ignore it.
        var state = await StateAfterTurnAsync(focus: null);

        var historical = ReceiptWithFocus(
            CoachVocabularyFocusStatus.Applied,
            CoachVocabularyFocusRenderTests.ActionVerbs());

        await using var provider = Provider(state);
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CoachChangeReceipt.Receipt)] = historical
            });

            var output = await renderer.RenderComponentAsync<CoachChangeReceipt>(parameters);
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });

        html.Should().Contain("Focus applied", "the receipt describes its own change, not today's state");
        html.Should().Contain("달리다");
    }

    [Fact]
    public async Task AnUndoShowsTheRestoredSetWithoutAReload()
    {
        // The restored set rides on the undo response itself. Nothing may depend on a later
        // session read landing first.
        var client = new FakeCoachApiClient();
        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        var restored = ReceiptWithFocus(
            CoachVocabularyFocusStatus.Restored,
            CoachVocabularyFocusRenderTests.ActionVerbs(),
            CoachRevisionSource.Undo);

        // Active constraints deliberately carry NO focus, so a client reading current state
        // instead of the receipt would render nothing here.
        var turn = CoachStateMachineTests.Turn(receipt: restored);
        client.OnUndo = () => new CoachTurnResponse
        {
            SessionId = turn.SessionId,
            TurnId = turn.TurnId,
            Status = turn.Status,
            StopReason = turn.StopReason,
            SessionStatus = turn.SessionStatus,
            ActiveConstraints = ConstraintsWith(null),
            PlanState = turn.PlanState,
            Messages = turn.Messages,
            Evidence = turn.Evidence,
            ChangeReceipt = restored,
            PendingSuggestion = null,
            ClarificationsRemaining = turn.ClarificationsRemaining,
            ExpiresAtUtc = turn.ExpiresAtUtc
        };

        await state.UndoAsync("rev-1");

        var html = await RenderAsync<CoachChangeReceipt>(state);

        html.Should().Contain("Focus restored");
        html.Should().Contain("달리다", "the set comes from the receipt, not from a later read");
    }

    [Fact]
    public async Task NoInternalIdentifierReachesTheReceipt()
    {
        var state = await StateAfterTurnAsync(
            focus: null,
            ReceiptWithFocus(CoachVocabularyFocusStatus.Applied, CoachVocabularyFocusRenderTests.ActionVerbs()));

        var html = await RenderAsync<CoachChangeReceipt>(state);

        html.Should().NotContain("grammar.action-verb");
    }

    // ---------------------------------------------------------------- invariants that must hold

    [Fact]
    public async Task TheSuggestionCardStillOffersExactlyOneActionPair()
    {
        var client = new FakeCoachApiClient();
        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            sessionStatus: CoachSessionStatus.SuggestionPending,
            suggestion: CoachStateMachineTests.Suggestion());

        state.Draft = "focus on action verbs";
        await state.SendDraftAsync();

        var html = await RenderAsync<CoachSuggestionCard>(state);

        Regex.Matches(html, Regex.Escape("Not now")).Count.Should().Be(1);
        Regex.Matches(html, "<button").Count.Should().Be(2, "exactly one accept and one reject");
    }

    [Fact]
    public async Task LearnerTurnsStillLeadTheConversation()
    {
        var state = await StateAfterTurnAsync(CoachVocabularyFocusRenderTests.ActionVerbs());
        var html = await RenderAsync<CoachChatPane>(state);

        html.Should().Contain("focus today on action verbs", "the learner's own words are still shown");

        var learner = html.IndexOf("focus today on action verbs", StringComparison.Ordinal);
        var role = html.IndexOf(">You<", StringComparison.Ordinal);
        role.Should().BeGreaterThan(-1);
        role.Should().BeLessThan(learner, "the speaker label precedes the message it labels");
    }

    [Fact]
    public async Task SamKeepsHerNameAlongsideTheFocusWork()
    {
        var state = await StateAfterTurnAsync(
            CoachVocabularyFocusRenderTests.ActionVerbs(),
            ReceiptWithFocus(CoachVocabularyFocusStatus.Applied, CoachVocabularyFocusRenderTests.ActionVerbs()));

        var html = await RenderAsync<CoachChatPane>(state);

        html.Should().Contain("Conversation with Sam");
        Regex.Matches(html, ">Coach<").Count.Should().Be(0);
    }
}
