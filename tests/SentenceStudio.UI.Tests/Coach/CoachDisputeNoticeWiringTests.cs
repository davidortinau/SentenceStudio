using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using FluentAssertions;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;
using Xunit;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// The dispute notice reaching a learner through the shared conversation tree.
/// </summary>
/// <remarks>
/// <para>
/// <c>CoachDisputeNoticeRenderTests</c> proves the component: both hosts, both cultures, every
/// status, no learner text. Every one of those passed while the component was never placed in any
/// tree — a correctly rendered notice nobody could see. These tests render the chat pane, which is
/// the component both hosts actually mount, and read the workspace state the server populates.
/// </para>
/// <para>
/// <b>Nothing here writes <c>CoachWorkspaceState.Dispute</c> directly.</b> An earlier version of
/// this file set it by reflection, which made every test below pass without
/// <c>ApplySession</c> or <c>ApplyTurn</c> ever running — a wiring suite that proved the component
/// and nothing about the wiring. The property has a private setter, so after that helper was
/// deleted the only way it can hold a value is the real server path. Each test states which path
/// it drove and asserts the state landed there.
/// </para>
/// <para>
/// <b>The clearing test is the one to keep honest.</b> Six lines above the dispute assignment,
/// <c>LatestAnswer</c> is written conditionally (<c>if (turn.Answer is not null)</c>) because an
/// answerless turn must not erase the last answer. Dispute is the opposite: it is replaced from
/// every turn, null included, or a resolved correction goes on telling the learner the coach is
/// still constrained. That is Case D, the failure the feature exists to close.
/// <c>A_turn_that_reports_no_dispute_clears_the_notice</c> is what makes copying the neighbouring
/// pattern a red build instead of a silent regression.
/// </para>
/// </remarks>
public class CoachDisputeNoticeWiringTests
{
    private const string SeededCoachSentence = "은/는 marks the topic.";
    private const string SeededLearnerSentence = "How do I use 은/는?";
    private const string DisputedMessageId = "msg-coach-1";

    [Theory]
    [InlineData("en")]
    [InlineData("ko")]
    public async Task An_open_dispute_reaches_the_learner_through_the_chat_pane(string culture)
    {
        var (state, _) = await WorkspaceAsync(Dispute(CoachDisputeStatus.Open));

        var html = await RenderPaneAsync(state, culture);

        html.Should().Contain("coach-dispute",
            "the notice has to be in the tree both hosts mount, not merely renderable in isolation");
    }

    /// <summary>The resume path: the server reports the dispute and the client restores it.</summary>
    [Fact]
    public async Task An_open_dispute_survives_a_session_resume()
    {
        var (state, _) = await WorkspaceAsync(Dispute(CoachDisputeStatus.Open));

        // Proof the real path ran: the setter is private and nothing in this file can reach it,
        // so a populated Dispute can only have come from ApplySession.
        state.Dispute.Should().NotBeNull(
            "the session response carried a dispute, so ApplySession must have restored it");
        state.Dispute!.Status.Should().Be(CoachDisputeStatus.Open);
        state.Dispute.DisputedMessageId.Should().Be(DisputedMessageId,
            "the restored dispute is the server's, field for field");

        (await RenderPaneAsync(state)).Should().Contain("coach-dispute",
            "a learner who closes the app mid-dispute comes back to the same constraint");
    }

    /// <summary>
    /// The clearing path. This is the test that must fail if the assignment becomes conditional.
    /// </summary>
    [Fact]
    public async Task A_turn_that_reports_no_dispute_clears_the_notice()
    {
        var (state, client) = await WorkspaceAsync(Dispute(CoachDisputeStatus.Open));

        // Precondition, asserted rather than assumed: there is a notice on screen to clear.
        state.Dispute.Should().NotBeNull();
        (await RenderPaneAsync(state)).Should().Contain("coach-dispute",
            "the clearing assertion below is vacuous unless something was showing first");

        client.OnSubmitTurn = _ => TurnWith(dispute: null);
        state.Draft = "Thanks — that one is right now.";
        await state.SendDraftAsync();

        client.SubmitTurnCalls.Should().Be(1, "the turn must actually have gone through ApplyTurn");
        state.Dispute.Should().BeNull(
            "Dispute is replaced from every turn, null included. If this assignment is ever made "
            + "conditional the way LatestAnswer is, a resolved correction keeps telling the learner "
            + "the coach is still constrained");

        (await RenderPaneAsync(state)).Should().NotContain("coach-dispute",
            "the banner must leave the screen when the server stops reporting the dispute");
    }

    /// <summary>A turn may also close the dispute rather than drop it. Both must land.</summary>
    [Fact]
    public async Task A_turn_that_resolves_the_dispute_replaces_the_open_notice()
    {
        var (state, client) = await WorkspaceAsync(Dispute(CoachDisputeStatus.Open));

        client.OnSubmitTurn = _ => TurnWith(Dispute(CoachDisputeStatus.ResolvedByReRead));
        state.Draft = "Try that again.";
        await state.SendDraftAsync();

        state.Dispute!.Status.Should().Be(CoachDisputeStatus.ResolvedByReRead);

        var html = await RenderPaneAsync(state);
        html.Should().Contain("ResolvedByReRead");
        html.Should().NotContain("coach-dispute-open", "the dispute is no longer open");
    }

    [Fact]
    public async Task No_dispute_renders_no_notice()
    {
        var (state, _) = await WorkspaceAsync(dispute: null);

        state.Dispute.Should().BeNull();
        (await RenderPaneAsync(state)).Should().NotContain("coach-dispute",
            "a conversation with no correction shows no banner");
    }

    [Fact]
    public async Task A_resolved_dispute_stops_claiming_the_coach_is_constrained()
    {
        var (state, _) = await WorkspaceAsync(Dispute(CoachDisputeStatus.ResolvedByReRead));

        var html = await RenderPaneAsync(state);

        // Still shown — the learner is owed the acknowledgement that their correction landed — but
        // as a resolved state rather than an open one.
        html.Should().Contain("coach-dispute");
        html.Should().Contain("ResolvedByReRead");
    }

    [Fact]
    public async Task An_unknown_status_stays_neutral_in_the_pane()
    {
        var (state, _) = await WorkspaceAsync(new CoachDisputeDto
        {
            Signal = (CoachDisputeSignal)99,
            Status = (CoachDisputeStatus)99,
            DisputedMessageId = DisputedMessageId
        });

        var html = await RenderPaneAsync(state);
        var visible = VisibleText(html);

        // The ordinal survives in a data attribute, which is diagnostic and never read aloud or
        // displayed. What must not happen is a learner seeing a number instead of a sentence.
        visible.Should().NotContain("99", "an unreadable code is never shown to a learner");
        html.Should().Contain("coach-dispute",
            "an unknown status still renders a neutral banner rather than nothing");
    }

    /// <summary>
    /// The leak check, bounded to the notice itself.
    /// </summary>
    /// <remarks>
    /// Scanning the whole pane cannot express this: the conversation legitimately renders the
    /// coach's sentence one message down, so a pane-wide "must not contain" would either fail on
    /// correct output or be quietly dropped. The claim is about the notice's own subtree.
    /// </remarks>
    [Theory]
    [InlineData("en", "Your correction")]
    [InlineData("ko", "지적하신 내용")]
    public async Task The_notice_carries_no_learner_or_coach_text(string culture, string heading)
    {
        var (state, client) = await WorkspaceAsync(Dispute(CoachDisputeStatus.Open));

        // Drive the disputed exchange through the real turn path so both sentences are genuinely in
        // the document: the learner's from the composer, the coach's from the turn response.
        client.OnSubmitTurn = _ => TurnWith(Dispute(CoachDisputeStatus.Open), DisputedAnswer);
        state.Draft = SeededLearnerSentence;
        await state.SendDraftAsync();

        var html = await RenderPaneAsync(state, culture);
        var pane = VisibleText(html);

        // Both sentences are on screen, so every exclusion below is about text that exists in this
        // render rather than text that was never there.
        pane.Should().Contain(SeededCoachSentence,
            "the conversation shows the coach's answer; that is the text the notice must not repeat");
        pane.Should().Contain(SeededLearnerSentence,
            "the learner's correction is in the conversation, which is the only place it belongs");

        var notice = DisputeSubtree(html);
        var noticeText = VisibleText(notice);

        noticeText.Should().NotContain(SeededCoachSentence,
            "the notice is a receipt for a correction, not a second copy of the disputed answer");
        noticeText.Should().NotContain(SeededLearnerSentence,
            "the learner's words live in the conversation once, in the encrypted ledger");
        notice.Should().NotContain(DisputedMessageId,
            "the ledger identifier is bookkeeping; showing it tells the learner nothing and leaks a "
            + "correlatable key into the DOM. Asserted over the raw subtree so an attribute counts");

        noticeText.Should().Contain(heading,
            "the notice rendered its own localized copy, so the exclusions above are not passing "
            + "against an empty subtree");
    }

    // ---------------------------------------------------------------- fixtures

    /// <summary>
    /// The coach's disputed answer, delivered by a turn so it reaches the rendered timeline.
    /// </summary>
    /// <remarks>
    /// The pane renders <c>Coach.Timeline</c>, which a session response does not populate — session
    /// <c>Messages</c> land in <c>Coach.Messages</c> and never appear here. Putting the sentence on
    /// screen therefore means driving a turn, which is also the honest shape: this is the answer the
    /// learner is disputing. Its id is the disputed message id, so that value is genuinely in the
    /// document and its absence from the notice subtree is a real exclusion.
    /// </remarks>
    private static IReadOnlyList<CoachMessageDto> DisputedAnswer { get; } =
    [
        new CoachMessageDto
        {
            MessageId = DisputedMessageId,
            Role = CoachMessageRole.Coach,
            Kind = CoachMessageKind.Text,
            Text = SeededCoachSentence,
            CreatedAtUtc = new DateTime(2026, 1, 1, 9, 0, 1, DateTimeKind.Utc)
        }
    ];

    private static CoachDisputeDto Dispute(CoachDisputeStatus status) => new()
    {
        Signal = CoachDisputeSignal.WrongClaim,
        Status = status,
        DisputedMessageId = DisputedMessageId
    };

    /// <summary>
    /// A workspace opened against a server that reports <paramref name="dispute"/> on the session.
    /// </summary>
    /// <remarks>
    /// Deliberately the plain session path, with no conversation directory and no durable history.
    /// With those on, the workspace submits through the conversation endpoint instead of
    /// <c>SubmitTurnAsync</c>, and the clearing test below could not observe the turn it drove.
    /// Both routes converge on the same single <c>ApplyTurn</c> call site, so this one is enough
    /// to pin the assignment.
    /// </remarks>
    private static async Task<(CoachWorkspaceState State, FakeCoachApiClient Client)> WorkspaceAsync(
        CoachDisputeDto? dispute)
    {
        var client = new FakeCoachApiClient();
        client.OnStartSession = () => SessionWith(dispute);

        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        return (state, client);
    }

    private static CoachSessionResponse SessionWith(CoachDisputeDto? dispute, string sessionId = "session-1")
    {
        var session = FakeCoachApiClient.Session(sessionId);

        return new CoachSessionResponse
        {
            SessionId = session.SessionId,
            Status = session.Status,
            Messages = session.Messages,
            ActiveConstraints = session.ActiveConstraints,
            PlanState = session.PlanState,
            PendingSuggestion = session.PendingSuggestion,
            Evidence = session.Evidence,
            Dispute = dispute,
            Revisions = session.Revisions,
            ClarificationsRemaining = session.ClarificationsRemaining,
            RunsRemainingToday = session.RunsRemainingToday,
            CreatedAtUtc = session.CreatedAtUtc,
            ExpiresAtUtc = session.ExpiresAtUtc
        };
    }

    private static CoachTurnResponse TurnWith(
        CoachDisputeDto? dispute,
        IReadOnlyList<CoachMessageDto>? messages = null)
    {
        var turn = CoachStateMachineTests.Turn(messages: messages);

        return new CoachTurnResponse
        {
            SessionId = turn.SessionId,
            TurnId = turn.TurnId,
            Status = turn.Status,
            StopReason = turn.StopReason,
            SessionStatus = turn.SessionStatus,
            Messages = turn.Messages,
            ActiveConstraints = turn.ActiveConstraints,
            PlanState = turn.PlanState,
            PendingSuggestion = turn.PendingSuggestion,
            ChangeReceipt = turn.ChangeReceipt,
            Answer = turn.Answer,
            Evidence = turn.Evidence,
            Dispute = dispute,
            ClarifyingQuestion = turn.ClarifyingQuestion,
            ClarificationsRemaining = turn.ClarificationsRemaining,
            RunsRemainingToday = turn.RunsRemainingToday,
            ExpiresAtUtc = turn.ExpiresAtUtc,
            MemoryCandidate = turn.MemoryCandidate,
            WriteOperation = turn.WriteOperation
        };
    }

    /// <summary>
    /// The notice's own markup, from its opening div to its close.
    /// </summary>
    /// <remarks>
    /// The notice contains only paragraphs and a button, so the first <c>&lt;/div&gt;</c> is its
    /// own. That assumption is asserted rather than trusted: a nested div would silently truncate
    /// the subtree and weaken every exclusion measured against it.
    /// </remarks>
    private static string DisputeSubtree(string html)
    {
        var start = html.IndexOf("<div class=\"coach-dispute", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "the notice must be present for its subtree to be checked");

        var end = html.IndexOf("</div>", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, "the notice must be closed");

        var subtree = html[start..(end + "</div>".Length)];

        subtree.IndexOf("<div", 1, StringComparison.Ordinal)
            .Should().Be(-1, "a nested div would mean this subtree is truncated at the wrong close tag");

        return subtree;
    }

    /// <summary>The text a learner reads, with markup and attributes removed.</summary>
    private static string VisibleText(string html) =>
        System.Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", " ");

    private static async Task<string> RenderPaneAsync(CoachWorkspaceState state, string culture = "en")
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(culture);

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<BlazorLocalizationService>();
            services.AddScoped<CoachPersona>();
            services.AddScoped<IJSRuntime>(_ => new StubJSRuntime());
            services.AddScoped(_ => state);

            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var output = await renderer.RenderComponentAsync<CoachChatPane>(ParameterView.Empty);

                // Decoded, as the sibling Coach render tests do. Without this every non-ASCII
                // assertion in this file compares against numeric entities and can never fail —
                // which is how the Korean copy went unchecked here.
                return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
            });
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
