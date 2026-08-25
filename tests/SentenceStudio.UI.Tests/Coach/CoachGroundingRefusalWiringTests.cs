using System.Globalization;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;
using Xunit;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// W9: what a learner sees when the coach withholds an answer it could not verify.
/// </summary>
/// <remarks>
/// <para>
/// A grounding refusal is not an error and not a boundary refusal. Nothing failed, and the learner
/// did not ask for something out of scope — the coach tried to answer, could not stand behind what
/// it found, and said so. Three things follow, and each is a test below: the refusal is announced
/// politely rather than shouted, the evidence it was judged against is on screen rather than behind
/// a toggle, and no hint ladder appears, because the learner did not ask for a nudge.
/// </para>
/// <para>
/// Everything here drives the real <c>ApplyTurn</c> and <c>ApplySession</c> paths through
/// <c>FakeCoachApiClient</c>. Nothing writes workspace state directly — the W8 review established
/// that a wiring suite which sets state by reflection proves only that the component renders.
/// </para>
/// </remarks>
public class CoachGroundingRefusalWiringTests
{
    private const string WebBaseUri = "https://sentencestudio.example/";
    private const string WebViewBaseUri = "app://0.0.0.0/";

    // ---------------------------------------------------------------- turn path

    [Fact]
    public async Task A_withheld_turn_puts_the_refusal_and_its_evidence_on_screen()
    {
        var (state, client) = await WorkspaceAsync();

        await RefuseAsync(state, client);

        state.Limitation.Should().NotBeNull("ApplyTurn must have taken the limitation off the turn");
        state.Limitation!.Code.Should().Be(CoachLimitationCode.UnverifiedClaimWithheld);
        state.HasGroundingRefusal.Should().BeTrue();

        var html = await RenderPaneAsync(state);

        html.Should().Contain("coach-refusal", "the refusal region must be in the tree");
        html.Should().Contain("data-coach-limitation=\"UnverifiedClaimWithheld\"");
        html.Should().Contain("coach-evidence", "the evidence the refusal was judged against goes with it");
    }

    /// <summary>The clearing rule. Must fail if the assignment is ever made conditional.</summary>
    [Fact]
    public async Task The_next_turn_clears_the_refusal_even_when_it_reports_none()
    {
        var (state, client) = await WorkspaceAsync();
        await RefuseAsync(state, client);

        state.Limitation.Should().NotBeNull();
        (await RenderPaneAsync(state)).Should().Contain("coach-refusal",
            "the clearing assertion below is vacuous unless something was showing first");

        client.OnSubmitTurn = _ => TurnWith(limitation: null, evidence: []);
        state.Draft = "What about this week?";
        await state.SendDraftAsync();

        client.SubmitTurnCalls.Should().Be(2, "both turns must have gone through ApplyTurn");
        state.Limitation.Should().BeNull(
            "Limitation is replaced from every turn, null included. Made conditional, a refusal "
            + "outlives the turn that caused it and keeps hedging an answer since given plainly");
        state.HasGroundingRefusal.Should().BeFalse();

        (await RenderPaneAsync(state)).Should().NotContain("coach-refusal");
    }

    // ---------------------------------------------------------------- resume

    /// <summary>
    /// Resume is restored from the latest completed turn, and only that one.
    /// </summary>
    /// <remarks>
    /// A grounding refusal reaches the client on <c>CoachTurnResponse.Limitation</c> and nowhere
    /// else: history is rebuilt from ledger messages, and <c>CoachTurnOperationDto.Result</c> — the
    /// only stored turn response — comes back on the submit path only. So restoring across a reload
    /// needs <c>CoachSessionResponse.Limitation</c>, which the session projection supplies from the
    /// stored outcome of the latest completed turn. The lookback is one row on purpose: a refusal
    /// the learner has already moved past is a claim about the present, so it is not resurrected.
    /// The evidence rows are not restored with it, so a resumed refusal states its withheld count
    /// and reason from the limitation's own fields.
    /// </remarks>
    [Fact]
    public async Task A_refusal_is_restored_from_the_session_when_a_host_sends_one()
    {
        var client = new FakeCoachApiClient();
        client.OnStartSession = () => SessionWith(Refusal(CoachRouteCatalog.Build(CoachRouteName.Vocabulary)));

        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        state.Limitation.Should().NotBeNull(
            "the setter is private and nothing here can reach it, so ApplySession must have restored it");
        state.Limitation!.Code.Should().Be(CoachLimitationCode.UnverifiedClaimWithheld);

        (await RenderPaneAsync(state)).Should().Contain("coach-refusal");
    }

    [Fact]
    public async Task A_session_that_reports_no_refusal_restores_none()
    {
        var (state, _) = await WorkspaceAsync();

        state.Limitation.Should().BeNull();
        (await RenderPaneAsync(state)).Should().NotContain("coach-refusal");
    }

    // ---------------------------------------------------------------- hosts

    [Fact]
    public async Task Both_hosts_render_the_refusal_identically()
    {
        var (web, webClient) = await WorkspaceAsync();
        await RefuseAsync(web, webClient);

        var (view, viewClient) = await WorkspaceAsync();
        await RefuseAsync(view, viewClient);

        var webHtml = await RenderPaneAsync(web, baseUri: WebBaseUri);
        var viewHtml = await RenderPaneAsync(view, baseUri: WebViewBaseUri);

        Region(webHtml).Should().Be(Region(viewHtml),
            "the refusal region reads no base URI, so the two hosts cannot differ");
        Region(webHtml).Should().Contain("data-coach-limitation",
            "the parity assertion must be over real markup");
    }

    // ---------------------------------------------------------------- copy

    /// <summary>L1 closure counterpart: the refusal copy resolves per language on the client.</summary>
    [Theory]
    [InlineData("en", "Sam couldn\u2019t check this one")]
    [InlineData("ko", "\uD655\uC778\uD560 \uC218 \uC5C6\uC5B4\uC11C")]
    public async Task L1_the_refusal_copy_resolves_per_language(string culture, string expected)
    {
        var (state, client) = await WorkspaceAsync();
        await RefuseAsync(state, client);

        var region = VisibleText(Region(await RenderPaneAsync(state, culture)));

        region.Should().Contain(expected, "the refusal reads in the learner's language");
    }

    [Fact]
    public async Task A_korean_learner_reads_no_english_in_the_refusal()
    {
        var (state, client) = await WorkspaceAsync();
        await RefuseAsync(state, client);

        var region = VisibleText(Region(await RenderPaneAsync(state, "ko")));

        foreach (var english in new[]
        {
            "Sam couldn\u2019t check this one",
            "You can look for yourself here:",
            "Withheld answer",
            "Vocabulary"
        })
        {
            region.Should().NotContain(english, $"'{english}' is English copy in a Korean refusal");
        }

        region.Should().Contain("\uD655\uC778\uD560 \uC218 \uC5C6\uC5B4\uC11C",
            "the Korean control proves the region rendered rather than came back empty");
    }

    /// <summary>The server's own English must never be the thing rendered.</summary>
    [Theory]
    [InlineData("en")]
    [InlineData("ko")]
    public async Task The_raw_server_notice_is_never_rendered(string culture)
    {
        var (state, client) = await WorkspaceAsync();
        await RefuseAsync(state, client, notice: "GROUNDING_REFUSAL: claim unverified (rule R7)");

        var html = await RenderPaneAsync(state, culture);
        var markup = Region(html);
        var region = VisibleText(markup);

        markup.Should().Contain("data-coach-limitation",
            "the exclusions below must be measured on a rendered refusal");

        // Bounded to the refusal. The pane's own message list renders Notice-kind messages, which
        // is pre-existing behaviour this workstream does not change; what W9 must not do is pipe
        // that raw string into the refusal itself, where it would sit untranslated beside copy the
        // learner can actually read.
        region.Should().NotContain("GROUNDING_REFUSAL",
            "an internal notice string is diagnostics, not copy, and has no translation");
        region.Should().NotContain("rule R7");
    }

    // ---------------------------------------------------------------- announcement

    /// <summary>L5 closure counterpart: a withheld answer announces itself, politely.</summary>
    [Fact]
    public async Task L5_a_withheld_answer_announces_that_it_was_withheld()
    {
        var (state, client) = await WorkspaceAsync();
        await RefuseAsync(state, client);

        state.PoliteAnnouncementKey.Should().Be("Coach_Announce_ClaimWithheld",
            "falling back to Ready in silence is the defect: the learner gets a shorter answer and "
            + "no reason for it");
        state.AlertKey.Should().BeNull("nothing failed, so nothing is an error");
        state.State.Should().NotBe(CoachUiState.Failed);
    }

    [Fact]
    public async Task The_refusal_region_is_a_polite_status_not_an_alert()
    {
        var (state, client) = await WorkspaceAsync();
        await RefuseAsync(state, client);

        var region = Region(await RenderPaneAsync(state));

        region.Should().Contain("role=\"status\"");
        region.Should().Contain("aria-live=\"polite\"");
        region.Should().NotContain("role=\"alert\"");
        region.Should().Contain("aria-label=", "the region is named for a screen reader");
    }

    // ---------------------------------------------------------------- evidence

    /// <summary>L2 closure counterpart: the refused turn keeps the evidence it read, visible.</summary>
    [Fact]
    public async Task L2_a_refused_turn_still_shows_the_evidence_it_read()
    {
        var (state, client) = await WorkspaceAsync();
        await RefuseAsync(state, client);

        state.Evidence.Should().ContainSingle("the turn's evidence must have survived ApplyTurn");

        var visible = VisibleText(await RenderPaneAsync(state));

        // Expanded, not behind a disclosure: on a refused turn the evidence is the answer.
        visible.Should().Contain("Rows read");
        visible.Should().Contain("Rows matched");
        visible.Should().Contain("Rows withheld");
    }

    [Fact]
    public async Task The_due_review_embargo_is_named_as_the_reason_something_was_withheld()
    {
        var (state, client) = await WorkspaceAsync();
        await RefuseAsync(state, client);

        var html = await RenderPaneAsync(state);

        html.Should().Contain("data-coach-scope=\"withheld\"",
            "the learner must be able to tell a short answer from a censored one");
        html.Should().Contain("data-coach-scope=\"coverage\"",
            "coverage says whether the coach looked at everything or only part of it");
    }

    // ---------------------------------------------------------------- destination

    /// <summary>L3 closure counterpart: the destination names a real screen and resolves.</summary>
    [Theory]
    [InlineData(CoachRouteName.Vocabulary)]
    [InlineData(CoachRouteName.ActivityLog)]
    [InlineData(CoachRouteName.Skills)]
    public async Task L3_a_refusal_names_a_real_screen_the_learner_can_go_to(CoachRouteName route)
    {
        var (state, client) = await WorkspaceAsync();
        await RefuseAsync(state, client, destination: CoachRouteCatalog.Build(route));

        var region = Region(await RenderPaneAsync(state));

        region.Should().Contain($"data-coach-limitation-destination=\"{route}\"");
        region.Should().Contain("data-coach-limitation-effect=",
            "naming a screen without naming what can change there is the W7 undisclosed consequence");
    }

    [Fact]
    public async Task A_refusal_with_no_destination_offers_none()
    {
        var (state, client) = await WorkspaceAsync();
        await RefuseAsync(state, client, withDestination: false);

        var region = Region(await RenderPaneAsync(state));

        region.Should().Contain("data-coach-limitation=\"UnverifiedClaimWithheld\"");
        region.Should().NotContain("data-coach-limitation-destination",
            "no destination is rendered when the server named none");
    }

    [Fact]
    public async Task An_unknown_route_is_dropped_rather_than_guessed()
    {
        var (state, client) = await WorkspaceAsync();
        await RefuseAsync(state, client, destination: new CoachDestinationDto(
            (CoachRouteName)9999, [], CoachRouteSideEffect.EditsLearnerData));

        var region = Region(await RenderPaneAsync(state));

        region.Should().NotContain("data-coach-limitation-destination",
            "a route this build cannot resolve must not become a link the learner cannot follow");
        region.Should().Contain("data-coach-limitation=\"UnverifiedClaimWithheld\"",
            "dropping the destination must not drop the refusal");
    }

    [Fact]
    public async Task An_unknown_limitation_code_stays_neutral()
    {
        var (state, client) = await WorkspaceAsync();
        await RefuseAsync(state, client, code: (CoachLimitationCode)9999);

        var visible = VisibleText(Region(await RenderPaneAsync(state)));

        visible.Should().Contain("Something Sam can\u2019t do here");
        visible.Should().NotContain("Sam couldn\u2019t check this one",
            "an unknown code must not borrow the grounding refusal's reason");
    }

    // ---------------------------------------------------------------- boundary

    [Fact]
    public async Task A_grounding_refusal_offers_no_ladder_no_alternatives_and_no_shorter_session()
    {
        var (state, client) = await WorkspaceAsync();
        await RefuseAsync(state, client);

        var region = Region(await RenderPaneAsync(state));

        region.Should().NotContain("coach-limitation-hints",
            "the learner did not ask for a nudge; offering one implies they did");
        region.Should().NotContain("coach-limitation-alternatives");
        region.Should().NotContain("coach-limitation-shorter");
    }

    [Fact]
    public async Task No_emoji_reaches_the_refusal()
    {
        var (state, client) = await WorkspaceAsync();
        await RefuseAsync(state, client);

        foreach (var culture in new[] { "en", "ko" })
        {
            var region = Region(await RenderPaneAsync(state, culture));

            region.EnumerateRunes()
                .Where(r => r.Value is (>= 0x1F300 and <= 0x1FAFF) or (>= 0x2600 and <= 0x27BF) or 0xFE0F)
                .Should().BeEmpty("this app uses Bootstrap icons or plain text, never emoji");
        }
    }


    // ---------------------------------------------------------------- LVG-W9-8: turn-scoped rows

    /// <summary>
    /// The defect this closes: a refusal rendered above the previous question's evidence.
    /// </summary>
    [Fact]
    public async Task A_refusal_after_an_answered_turn_shows_none_of_the_earlier_rows()
    {
        var (state, client) = await WorkspaceAsync();

        // A normal turn that cited evidence, so there is something stale to survive.
        client.OnSubmitTurn = _ => TurnWith(limitation: null, evidence: [WithheldEvidence()]);
        state.Draft = "How am I doing?";
        await state.SendDraftAsync();
        state.Evidence.Should().ContainSingle("the first turn must have left rows behind");

        // Then a refusal that read nothing at all.
        client.OnSubmitTurn = _ => TurnWith(Refusal(withheldCount: 4), evidence: []);
        state.Draft = "And this week?";
        await state.SendDraftAsync();

        state.Evidence.Should().BeEmpty(
            "a refusal is judged against what this turn read. Earlier rows surviving would put the "
            + "refusal above evidence from a question that was already answered");

        var region = Region(await RenderPaneAsync(state));
        VisibleText(region).Should().NotContain("Rows read",
            "no evidence panel may render for a read that returned nothing");
    }

    [Fact]
    public async Task A_no_read_refusal_never_promises_evidence_that_is_not_there()
    {
        var (state, client) = await WorkspaceAsync();
        client.OnSubmitTurn = _ => TurnWith(Refusal(withheldCount: 4), evidence: []);
        state.Draft = "Am I improving?";
        await state.SendDraftAsync();

        var visible = VisibleText(Region(await RenderPaneAsync(state)));

        visible.Should().Contain("couldn\u2019t get a reading it could stand behind");
        visible.Should().NotContain("The evidence below shows what it looked at",
            "promising a panel that is not on screen sends the learner looking for it");

        state.PoliteAnnouncementKey.Should().Be("Coach_Announce_ClaimWithheldNoEvidence");
    }

    [Fact]
    public async Task A_refusal_that_did_read_keeps_the_evidence_wording()
    {
        var (state, client) = await WorkspaceAsync();
        await RefuseAsync(state, client);

        var visible = VisibleText(Region(await RenderPaneAsync(state)));

        visible.Should().Contain("The evidence below shows what it looked at");
        visible.Should().NotContain("couldn\u2019t get a reading it could stand behind");
        state.PoliteAnnouncementKey.Should().Be("Coach_Announce_ClaimWithheld");
    }

    // ---------------------------------------------------------------- LVG-W9-8: resumed refusal

    [Fact]
    public async Task A_resumed_refusal_states_what_was_withheld_from_the_limitation_itself()
    {
        var client = new FakeCoachApiClient();
        client.OnStartSession = () => SessionWith(Refusal(
            CoachRouteCatalog.Build(CoachRouteName.Vocabulary), withheldCount: 4));

        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        state.Limitation.Should().NotBeNull();
        state.Evidence.Should().BeEmpty("the rows are not restored with the limitation");

        var region = Region(await RenderPaneAsync(state));

        region.Should().Contain("data-coach-limitation-withheld=\"4\"",
            "the disclosure has to survive the reload even though the rows did not");
        VisibleText(region).Should().Contain("couldn\u2019t get a reading it could stand behind");
    }

    [Fact]
    public async Task A_live_refusal_with_rows_does_not_state_the_withheld_total_twice()
    {
        var (state, client) = await WorkspaceAsync();
        await RefuseAsync(state, client);

        var region = Region(await RenderPaneAsync(state));

        region.Should().NotContain("data-coach-limitation-withheld",
            "the evidence panel already states this; two totals for one fact invites the reader to "
            + "look for the difference between them");
        region.Should().Contain("data-coach-scope=\"withheld\"", "the panel does state it");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task An_incoherent_withheld_pair_states_nothing(int? count)
    {
        var client = new FakeCoachApiClient();
        client.OnStartSession = () => SessionWith(Refusal(
            CoachRouteCatalog.Build(CoachRouteName.Vocabulary), withheldCount: count));

        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        Region(await RenderPaneAsync(state)).Should().NotContain("data-coach-limitation-withheld",
            "a reason with no count is a sentence with a hole in it, and zero is not a disclosure");
    }

    [Fact]
    public async Task An_unknown_withheld_reason_still_states_the_count()
    {
        var client = new FakeCoachApiClient();
        client.OnStartSession = () => SessionWith(Refusal(
            CoachRouteCatalog.Build(CoachRouteName.Vocabulary),
            withheldCount: 4,
            withheldReason: (CoachWithheldReason)9999));

        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        var region = Region(await RenderPaneAsync(state));

        region.Should().Contain("data-coach-limitation-withheld=\"4\"",
            "the number is the disclosure; the explanation is the courtesy");
        VisibleText(region).Should().NotContain("9999");
    }

    [Theory]
    [InlineData("en", "couldn\u2019t get a reading it could stand behind")]
    [InlineData("ko", "\uBBFF\uACE0 \uB9D0\uC500\uB4DC\uB9B4 \uB9CC\uD55C \uACB0\uACFC")]
    public async Task The_no_evidence_copy_reads_in_both_languages(string culture, string expected)
    {
        var (state, client) = await WorkspaceAsync();
        client.OnSubmitTurn = _ => TurnWith(Refusal(withheldCount: 4), evidence: []);
        state.Draft = "Am I improving?";
        await state.SendDraftAsync();

        var visible = VisibleText(Region(await RenderPaneAsync(state, culture)));

        visible.Should().Contain(expected);
        if (culture == "ko")
        {
            visible.Should().NotContain("couldn\u2019t get a reading",
                "a Korean learner must not read the English fallback");
        }
    }

    [Fact]
    public async Task Both_hosts_render_a_no_read_refusal_identically()
    {
        var (web, webClient) = await WorkspaceAsync();
        webClient.OnSubmitTurn = _ => TurnWith(Refusal(withheldCount: 4), evidence: []);
        web.Draft = "Am I improving?";
        await web.SendDraftAsync();

        var (view, viewClient) = await WorkspaceAsync();
        viewClient.OnSubmitTurn = _ => TurnWith(Refusal(withheldCount: 4), evidence: []);
        view.Draft = "Am I improving?";
        await view.SendDraftAsync();

        Region(await RenderPaneAsync(web, baseUri: WebBaseUri))
            .Should().Be(Region(await RenderPaneAsync(view, baseUri: WebViewBaseUri)));
    }

    [Fact]
    public async Task A_no_read_refusal_leaks_no_learner_content()
    {
        var (state, client) = await WorkspaceAsync();
        client.OnSubmitTurn = _ => TurnWith(Refusal(withheldCount: 4), evidence: []);
        state.Draft = "\uC740/\uB294 marks the topic?";
        await state.SendDraftAsync();

        var region = Region(await RenderPaneAsync(state));

        region.Should().NotContain("\uC740/\uB294 marks the topic",
            "the learner's words live in the conversation, not in the refusal");
        region.Should().NotContain("coach-limitation-hints");
        region.Should().NotContain("coach-limitation-alternatives");
    }

    // ---------------------------------------------------------------- fixtures

    private static async Task RefuseAsync(
        CoachWorkspaceState state,
        FakeCoachApiClient client,
        CoachDestinationDto? destination = null,
        bool withDestination = true,
        CoachLimitationCode code = CoachLimitationCode.UnverifiedClaimWithheld,
        string? notice = null)
    {
        // Explicit, because `destination: null` has to be able to mean "the server named none"
        // rather than "use the default" — the no-destination case is one of the tests.
        var resolved = withDestination
            ? destination ?? CoachRouteCatalog.Build(CoachRouteName.Vocabulary)
            : null;

        client.OnSubmitTurn = _ => TurnWith(
            Refusal(resolved, code),
            [WithheldEvidence()],
            notice);

        state.Draft = "Am I getting better at 은/는?";
        await state.SendDraftAsync();
    }

    private static CoachLimitationDto Refusal(
        CoachDestinationDto? destination = null,
        CoachLimitationCode code = CoachLimitationCode.UnverifiedClaimWithheld,
        bool withDestination = true,
        int? withheldCount = null,
        CoachWithheldReason? withheldReason = CoachWithheldReason.DueReviewEmbargo) => new()
    {
        Code = code,
        Coverage = CoachEvidenceCoverage.PageOfOwnedSet,
        AsOfUtc = new DateTime(2026, 1, 7, 9, 30, 0, DateTimeKind.Utc),
        WindowStartDate = new DateOnly(2026, 1, 1),
        WindowEndDate = new DateOnly(2026, 1, 7),
        AffectedCount = 14,
        Destination = destination,

        WithheldCount = withheldCount,
        WithheldReason = withheldCount is null ? null : withheldReason,

        // The server's projection sends all three empty for a grounding refusal.
        Alternatives = [],
        HintLadder = [],
        ShorterSession = null
    };

    private static CoachEvidenceDto WithheldEvidence() => new()
    {
        Kind = CoachEvidenceKind.VocabularyDue,
        Label = "Vocabulary",
        Summary = "Your words that are not currently due for review.",
        WindowStartDate = new DateOnly(2026, 1, 1),
        WindowEndDate = new DateOnly(2026, 1, 7),
        DefinitionCode = CoachDefinitionCode.UndueVocabularySearch,
        Coverage = CoachEvidenceCoverage.PageOfOwnedSet,
        Order = CoachEvidenceOrder.MasteryDescending,
        WithheldReason = CoachWithheldReason.DueReviewEmbargo,
        WithheldCount = 4,
        MatchedCount = 14,
        ReturnedCount = 10,
        Values =
        [
            new CoachEvidenceValueDto
            {
                Code = CoachEvidenceValueCode.RowsRead, Label = "Rows read",
                Value = 10, Unit = CoachEvidenceUnit.Items
            },
            new CoachEvidenceValueDto
            {
                Code = CoachEvidenceValueCode.RowsMatched, Label = "Rows matched",
                Value = 14, Unit = CoachEvidenceUnit.Items
            },
            new CoachEvidenceValueDto
            {
                Code = CoachEvidenceValueCode.RowsWithheld, Label = "Rows withheld",
                Value = 4, Unit = CoachEvidenceUnit.Items
            }
        ]
    };

    private static async Task<(CoachWorkspaceState State, FakeCoachApiClient Client)> WorkspaceAsync()
    {
        var client = new FakeCoachApiClient();
        client.OnStartSession = () => SessionWith(limitation: null);

        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        return (state, client);
    }

    private static CoachSessionResponse SessionWith(CoachLimitationDto? limitation)
    {
        var session = FakeCoachApiClient.Session();

        return new CoachSessionResponse
        {
            SessionId = session.SessionId,
            Status = session.Status,
            Messages = session.Messages,
            ActiveConstraints = session.ActiveConstraints,
            PlanState = session.PlanState,
            PendingSuggestion = session.PendingSuggestion,
            Evidence = session.Evidence,
            Dispute = session.Dispute,
            Limitation = limitation,
            Revisions = session.Revisions,
            ClarificationsRemaining = session.ClarificationsRemaining,
            RunsRemainingToday = session.RunsRemainingToday,
            CreatedAtUtc = session.CreatedAtUtc,
            ExpiresAtUtc = session.ExpiresAtUtc
        };
    }

    private static CoachTurnResponse TurnWith(
        CoachLimitationDto? limitation,
        IReadOnlyList<CoachEvidenceDto> evidence,
        string? notice = null)
    {
        var turn = CoachStateMachineTests.Turn(
            messages: notice is null
                ? null
                : [new CoachMessageDto
                    {
                        MessageId = "msg-notice-1",
                        Role = CoachMessageRole.Coach,
                        Kind = CoachMessageKind.Notice,
                        Text = notice,
                        CreatedAtUtc = new DateTime(2026, 1, 7, 9, 30, 0, DateTimeKind.Utc)
                    }],
            evidence: evidence);

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
            Dispute = turn.Dispute,
            Limitation = limitation,
            ClarifyingQuestion = turn.ClarifyingQuestion,
            ClarificationsRemaining = turn.ClarificationsRemaining,
            RunsRemainingToday = turn.RunsRemainingToday,
            ExpiresAtUtc = turn.ExpiresAtUtc,
            MemoryCandidate = turn.MemoryCandidate,
            WriteOperation = turn.WriteOperation
        };
    }

    /// <summary>The refusal region's markup, so assertions are not measured against the whole pane.</summary>
    /// <remarks>
    /// Nesting-aware on purpose. The limitation card renders its own &lt;section&gt;, so stopping at
    /// the first closing tag silently cut the region off before the evidence panel and quietly
    /// weakened every assertion measured against it.
    /// </remarks>
    private static string Region(string html)
    {
        var start = html.IndexOf("<section class=\"coach-refusal", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "the refusal region must be present for its markup to be checked");

        var depth = 0;
        var cursor = start;

        while (cursor < html.Length)
        {
            var open = html.IndexOf("<section", cursor, StringComparison.Ordinal);
            var close = html.IndexOf("</section>", cursor, StringComparison.Ordinal);

            close.Should().BeGreaterThan(-1, "the region must be closed");

            if (open >= 0 && open < close)
            {
                depth++;
                cursor = open + "<section".Length;
                continue;
            }

            depth--;
            cursor = close + "</section>".Length;

            if (depth == 0)
            {
                return html[start..cursor];
            }
        }

        throw new InvalidOperationException("the refusal region is not balanced");
    }

    private static string VisibleText(string html) =>
        System.Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", " ");

    private static async Task<string> RenderPaneAsync(
        CoachWorkspaceState state,
        string culture = "en",
        string baseUri = WebBaseUri)
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
            services.AddSingleton<NavigationManager>(new RefusalNavigationManager(baseUri));

            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var output = await renderer.RenderComponentAsync<CoachChatPane>(ParameterView.Empty);
                return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
            });
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private sealed class RefusalNavigationManager : NavigationManager
    {
        public RefusalNavigationManager(string baseUri) => Initialize(baseUri, baseUri);

        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
}
