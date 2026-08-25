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
/// P5/P6 closure: the learner is told when the grounding layer changed their answer, beside the
/// answer, and is never pointed at evidence that is not there.
/// </summary>
/// <remarks>
/// <para>
/// These replace the scan in <c>CoachLearningValueGateClosureTests</c> that asserted no client
/// source referenced <c>RepairDisclosure</c>. That scan existed because the disclosure reached the
/// wire and stopped there: the server knew part of an answer had been rewritten and the learner
/// never found out. It was deliberately written to go red the moment any client file touched the
/// property, which is exactly what happened, so it has been removed and these stand in its place.
/// </para>
/// <para>
/// Two defects were then found in the first landing of that closure, and both are held here.
/// <b>B1:</b> the two states that mention the evidence promised it unconditionally, while the
/// workspace evidence list is sticky by design — so a turn that read nothing inherited the
/// previous turn's rows and told the learner to go and look at them. <b>B2:</b> the notice was
/// mounted once above the log, which after auto-scroll is off screen and attached to no answer in
/// particular.
/// </para>
/// <para>
/// Everything drives the real <c>ApplyTurn</c> and <c>ApplySession</c> paths. Nothing writes
/// workspace state directly.
/// </para>
/// </remarks>
public class CoachRepairDisclosureWiringTests
{
    private const string WebBaseUri = "https://sentencestudio.example/";
    private const string WebViewBaseUri = "app://0.0.0.0/";

    private const string AlteredEn = "Sam adjusted part of this answer";
    private const string SuppressedEn = "Sam found something here worth checking";
    private const string UnknownEn = "can\u2019t describe how Sam handled a verification issue";
    private const string AlteredKo = "\uB2F5\uBCC0\uC758 \uC77C\uBD80\uB97C \uACE0\uCCE4\uC5B4\uC694";
    private const string SuppressedKo = "\uD45C\uD604\uC740 \uADF8\uB300\uB85C \uB450\uC5C8\uC5B4\uC694";
    private const string UnknownKo = "\uC124\uBA85\uD560 \uC218 \uC5C6\uC5B4\uC694";

    /// <summary>The sentence that sends the learner looking for the evidence, in each language.</summary>
    private const string PointerEn = "Have a look at the evidence.";
    private const string PointerKo = "\uADFC\uAC70\uB97C \uD55C\uBC88 \uBD10 \uC8FC\uC138\uC694.";

    // ---------------------------------------------------------------- the two disclosed states

    [Fact]
    public async Task An_altered_answer_says_so_and_announces_it_politely()
    {
        var (state, _) = await AfterTurnAsync(CoachRepairDisclosure.AnswerAltered);

        state.VisibleRepairDisclosure.Should().Be(CoachRepairDisclosure.AnswerAltered,
            "ApplyTurn must have taken the disclosure off the turn");

        var notice = Notice(await RenderPaneAsync(state));

        VisibleText(notice).Should().Contain(AlteredEn);
        state.PoliteAnnouncementKey.Should().Be("Coach_Announce_AnswerAltered",
            "shipping a rewritten answer in silence is the defect this closes");
        state.AlertKey.Should().BeNull("the answer is still an answer, not a failure");
    }

    [Fact]
    public async Task A_suppressed_repair_says_the_wording_was_left_alone()
    {
        var (state, _) = await AfterTurnAsync(
            CoachRepairDisclosure.RepairSuppressedForLanguage, withEvidence: true);

        var notice = VisibleText(Notice(await RenderPaneAsync(state)));

        notice.Should().Contain(SuppressedEn);
        notice.Should().NotContain("would have",
            "the coach did not adjust anything here, so it must not imply it nearly did");
        notice.Should().NotContain("adjusted part of this answer",
            "that is the other state, and it says the opposite about the words on screen");

        state.PoliteAnnouncementKey.Should().Be("Coach_Announce_RepairSuppressed");
    }

    [Theory]
    [InlineData(CoachRepairDisclosure.AnswerAltered, AlteredKo, AlteredEn)]
    [InlineData(CoachRepairDisclosure.RepairSuppressedForLanguage, SuppressedKo, SuppressedEn)]
    public async Task A_korean_learner_reads_the_disclosure_in_korean(
        CoachRepairDisclosure disclosure, string korean, string english)
    {
        var (state, _) = await AfterTurnAsync(disclosure, withEvidence: true);

        var notice = VisibleText(Notice(await RenderPaneAsync(state, "ko")));

        notice.Should().Contain(korean);
        notice.Should().NotContain(english, "a Korean learner must not read the English fallback");
    }

    // ------------------------------------------------- B1: the evidence pointer is conditional
    //
    // The full acceptance matrix Zoe named:
    //   {RepairSuppressedForLanguage, Unknown} x {evidence, no evidence} x {en, ko}
    //
    // Both states point at the evidence when there is some, and say the same thing minus the
    // pointer when there is none. The pointer is the whole assertion: a learner told to look at
    // the evidence for an answer that read nothing either finds nothing, or — worse, because the
    // workspace list is sticky — finds the previous turn's rows and reads the wrong answer's
    // working.

    [Theory]
    [InlineData(CoachRepairDisclosure.RepairSuppressedForLanguage, true, "en", SuppressedEn, PointerEn)]
    [InlineData(CoachRepairDisclosure.RepairSuppressedForLanguage, false, "en", SuppressedEn, PointerEn)]
    [InlineData(CoachRepairDisclosure.RepairSuppressedForLanguage, true, "ko", SuppressedKo, PointerKo)]
    [InlineData(CoachRepairDisclosure.RepairSuppressedForLanguage, false, "ko", SuppressedKo, PointerKo)]
    [InlineData(CoachRepairDisclosure.Unknown, true, "en", UnknownEn, PointerEn)]
    [InlineData(CoachRepairDisclosure.Unknown, false, "en", UnknownEn, PointerEn)]
    [InlineData(CoachRepairDisclosure.Unknown, true, "ko", UnknownKo, PointerKo)]
    [InlineData(CoachRepairDisclosure.Unknown, false, "ko", UnknownKo, PointerKo)]
    public async Task The_evidence_pointer_appears_only_when_this_turn_read_something(
        CoachRepairDisclosure disclosure,
        bool withEvidence,
        string culture,
        string alwaysSaid,
        string pointer)
    {
        var (state, _) = await AfterTurnAsync(disclosure, withEvidence);

        var markup = Notice(await RenderPaneAsync(state, culture));
        var text = VisibleText(markup);

        text.Should().Contain(alwaysSaid,
            "the state itself is disclosed either way; only the pointer is conditional");

        if (withEvidence)
        {
            text.Should().Contain(pointer, "this turn read something, so there is somewhere to look");
            markup.Should().Contain("data-coach-repair-evidence=\"true\"");
        }
        else
        {
            text.Should().NotContain(pointer,
                "this turn read nothing, and an instruction the learner cannot follow is worse "
                + "than saying less — it sends them to the previous turn's rows");
            markup.Should().Contain("data-coach-repair-evidence=\"false\"");
        }
    }

    /// <summary>The announcement promises exactly what the visible sentence promises.</summary>
    [Theory]
    [InlineData(CoachRepairDisclosure.RepairSuppressedForLanguage, true, "Coach_Announce_RepairSuppressed")]
    [InlineData(CoachRepairDisclosure.RepairSuppressedForLanguage, false, "Coach_Announce_RepairSuppressedNoEvidence")]
    [InlineData(CoachRepairDisclosure.Unknown, true, "Coach_Announce_RepairUnknown")]
    [InlineData(CoachRepairDisclosure.Unknown, false, "Coach_Announce_RepairUnknownNoEvidence")]
    [InlineData((CoachRepairDisclosure)9999, true, "Coach_Announce_RepairUnknown")]
    [InlineData((CoachRepairDisclosure)9999, false, "Coach_Announce_RepairUnknownNoEvidence")]
    public async Task The_announcement_matches_the_evidence_the_turn_actually_produced(
        CoachRepairDisclosure disclosure, bool withEvidence, string expectedKey)
    {
        var (state, _) = await AfterTurnAsync(disclosure, withEvidence);

        state.PoliteAnnouncementKey.Should().Be(expectedKey,
            "a screen-reader user and a reader must be promised the same thing in every cell");
        state.AlertKey.Should().BeNull("a disclosed repair is not a failure");
    }

    /// <summary>Neither announcement variant names evidence the turn did not produce.</summary>
    [Theory]
    [InlineData(CoachRepairDisclosure.RepairSuppressedForLanguage)]
    [InlineData(CoachRepairDisclosure.Unknown)]
    public async Task The_no_evidence_announcement_text_promises_no_evidence(
        CoachRepairDisclosure disclosure)
    {
        var (state, _) = await AfterTurnAsync(disclosure, withEvidence: false);

        var english = Resource("AppResources.resx", state.PoliteAnnouncementKey!);
        var korean = Resource("AppResources.ko.resx", state.PoliteAnnouncementKey!);

        english.ToLowerInvariant().Should().NotContain("evidence");
        korean.Should().NotContain("\uADFC\uAC70", "the Korean word for evidence must be absent too");
    }

    /// <summary>
    /// The regression B1 names directly: a no-evidence turn after an evidence-bearing one.
    /// </summary>
    /// <remarks>
    /// The workspace evidence list is sticky on purpose — a turn that cites nothing leaves the
    /// previous turn's rows standing, because the learner may still be reading them. That is
    /// correct and stays. What was wrong was reading it as "is there evidence for the answer this
    /// disclosure describes", which it never was.
    /// </remarks>
    [Fact]
    public async Task A_no_evidence_turn_after_an_evidence_bearing_turn_promises_nothing()
    {
        var (state, client) = await AfterTurnAsync(
            CoachRepairDisclosure.RepairSuppressedForLanguage, withEvidence: true);

        VisibleText(Notice(await RenderPaneAsync(state))).Should().Contain(PointerEn,
            "the assertion below is vacuous unless the first turn really did point at evidence");

        client.OnSubmitTurn = _ => TurnWith(
            CoachRepairDisclosure.RepairSuppressedForLanguage, evidence: NoEvidence);
        state.Draft = "And what about reading?";
        await state.SendDraftAsync();

        state.Evidence.Should().NotBeEmpty(
            "the sticky workspace list is deliberate and must not have been quietly changed");
        state.RepairEvidenceOnScreen.Should().BeFalse(
            "the turn this disclosure describes read nothing, whatever the workspace is still holding");

        var html = await RenderPaneAsync(state);
        var notices = AllNotices(html);

        notices.Should().HaveCount(2, "each answer keeps its own note");
        VisibleText(notices[0]).Should().Contain(PointerEn, "the first answer really did read something");
        VisibleText(notices[1]).Should().NotContain(PointerEn,
            "the second answer read nothing, and the older rows are not its working");
        notices[1].Should().Contain("data-coach-repair-evidence=\"false\"");

        state.PoliteAnnouncementKey.Should().Be("Coach_Announce_RepairSuppressedNoEvidence");
    }

    // ------------------------------------------------- B2: the note sits beside its own answer

    [Fact]
    public async Task The_disclosure_renders_inside_the_coach_message_after_the_answer()
    {
        var (state, _) = await AfterTurnAsync(CoachRepairDisclosure.AnswerAltered);

        var html = await RenderPaneAsync(state);
        var message = CoachMessageBlock(html);

        message.Should().Contain("coach-repair-disclosure",
            "the note belongs to the message that carries the answer it describes");
        message.IndexOf(AnswerText, StringComparison.Ordinal)
            .Should().BeLessThan(message.IndexOf("coach-repair-disclosure", StringComparison.Ordinal),
                "it reads after the answer body, not before it");
    }

    [Fact]
    public async Task The_disclosure_is_not_mounted_above_the_log()
    {
        var (state, _) = await AfterTurnAsync(CoachRepairDisclosure.AnswerAltered);

        var html = await RenderPaneAsync(state);

        var firstMessage = html.IndexOf("class=\"coach-message", StringComparison.Ordinal);
        var disclosure = html.IndexOf("coach-repair-disclosure", StringComparison.Ordinal);

        firstMessage.Should().BeGreaterThan(-1, "there must be a message for the note to sit in");
        disclosure.Should().BeGreaterThan(firstMessage,
            "mounted above the log it is off screen the moment the pane auto-scrolls, and it is "
            + "attached to no answer in particular");
    }

    /// <summary>A turn that produced no answer has nowhere to put a note, and puts it nowhere.</summary>
    [Fact]
    public async Task A_turn_with_no_answer_on_screen_renders_no_disclosure()
    {
        var (state, client) = await WorkspaceAsync();

        client.OnSubmitTurn = _ => TurnWith(CoachRepairDisclosure.AnswerAltered, messages: []);
        state.Draft = "How am I doing?";
        await state.SendDraftAsync();

        state.RepairDisclosure.Should().Be(CoachRepairDisclosure.AnswerAltered,
            "the workspace still records what the server said");
        (await RenderPaneAsync(state)).Should().NotContain("coach-repair-disclosure",
            "a note about an answer the learner cannot see is a note about nothing");
    }

    // ---------------------------------------------------------------- the silent states

    [Theory]
    [InlineData(null)]
    [InlineData(CoachRepairDisclosure.None)]
    public async Task A_clean_or_unchecked_answer_says_nothing(CoachRepairDisclosure? disclosure)
    {
        var (state, _) = await AfterTurnAsync(disclosure);

        var html = await RenderPaneAsync(state);

        html.Should().NotContain("coach-repair-disclosure",
            "None is checked-and-clean and null is not-checked. A notice on every ordinary turn is "
            + "a notice nobody reads");
        foreach (var repairKey in new[]
        {
            "Coach_Announce_AnswerAltered",
            "Coach_Announce_RepairSuppressed", "Coach_Announce_RepairSuppressedNoEvidence",
            "Coach_Announce_RepairUnknown", "Coach_Announce_RepairUnknownNoEvidence"
        })
        {
            state.PoliteAnnouncementKey.Should().NotBe(repairKey);
        }
    }

    // ---------------------------------------------------------------- unknown is not silent

    /// <summary>
    /// A state this build cannot name is disclosed neutrally, never inferred and never hidden.
    /// </summary>
    /// <remarks>
    /// This replaces an earlier case here that asserted Unknown rendered nothing. That contradicted
    /// the frozen enum, whose own remarks read "Render a neutral note; never infer one of the
    /// states below", for a reason the silent version missed: one of the two real states means part
    /// of the answer was rewritten, so staying quiet would hide a rewrite behind a version gap.
    /// </remarks>
    [Theory]
    [InlineData(CoachRepairDisclosure.Unknown, true)]
    [InlineData(CoachRepairDisclosure.Unknown, false)]
    [InlineData((CoachRepairDisclosure)9999, true)]
    [InlineData((CoachRepairDisclosure)9999, false)]
    public async Task An_undescribable_state_gets_a_neutral_note_rather_than_silence(
        CoachRepairDisclosure disclosure, bool withEvidence)
    {
        var (state, _) = await AfterTurnAsync(disclosure, withEvidence);

        VisibleText(Notice(await RenderPaneAsync(state))).Should().Contain(UnknownEn);
    }

    [Theory]
    [InlineData(CoachRepairDisclosure.Unknown, true)]
    [InlineData(CoachRepairDisclosure.Unknown, false)]
    [InlineData((CoachRepairDisclosure)9999, true)]
    [InlineData((CoachRepairDisclosure)9999, false)]
    public async Task An_undescribable_state_never_claims_the_answer_changed(
        CoachRepairDisclosure disclosure, bool withEvidence)
    {
        var (state, _) = await AfterTurnAsync(disclosure, withEvidence);

        var notice = VisibleText(Notice(await RenderPaneAsync(state)));

        notice.Should().NotContain("adjusted part of this answer",
            "inferring AnswerAltered from a state this build cannot read would tell the learner "
            + "their words were rewritten on no evidence");
        notice.Should().NotContain("left the wording as it is",
            "and inferring the other state would claim the opposite, equally baselessly");
        notice.Should().NotMatchRegex(@"\d", "the neutral note is count-free like the rest");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_korean_neutral_note_carries_no_english(bool withEvidence)
    {
        var (state, _) = await AfterTurnAsync(CoachRepairDisclosure.Unknown, withEvidence);

        var notice = VisibleText(Notice(await RenderPaneAsync(state, "ko")));

        notice.Should().Contain(UnknownKo);
        notice.Should().NotContain("can\u2019t describe how Sam handled");
        notice.Should().NotContain("verification issue");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_neutral_note_is_a_polite_status_with_a_name_in_both_hosts(bool withEvidence)
    {
        var (web, _) = await AfterTurnAsync(CoachRepairDisclosure.Unknown, withEvidence);
        var (view, _) = await AfterTurnAsync(CoachRepairDisclosure.Unknown, withEvidence);

        var webNotice = Notice(await RenderPaneAsync(web, baseUri: WebBaseUri));

        webNotice.Should().Contain("role=\"status\"");
        webNotice.Should().Contain("aria-live=\"polite\"");
        webNotice.Should().Contain("aria-label=");
        webNotice.Should().NotContain("role=\"alert\"");
        webNotice.EnumerateRunes()
            .Where(r => r.Value is (>= 0x1F300 and <= 0x1FAFF) or (>= 0x2600 and <= 0x27BF) or 0xFE0F)
            .Should().BeEmpty("no emoji");

        webNotice.Should().Be(Notice(await RenderPaneAsync(view, baseUri: WebViewBaseUri)),
            "the notice reads no base URI, so the hosts cannot differ");
    }

    /// <summary>A state a future server invents arrives as Unknown and is disclosed as such.</summary>
    [Fact]
    public async Task A_future_state_off_the_wire_collapses_to_the_neutral_note()
    {
        // Built by serialising a real response and swapping in a state this build has never heard
        // of, rather than hand-writing JSON — the response has required members, and a hand-written
        // payload would be testing my typing rather than the converter.
        var payload = System.Text.Json.JsonSerializer
            .Serialize(TurnWith(CoachRepairDisclosure.AnswerAltered),
                SentenceStudio.Contracts.Wire.WireJson.Client)
            .Replace("\"AnswerAltered\"", "\"SomethingFromNextYear\"", StringComparison.Ordinal);

        payload.Should().Contain("SomethingFromNextYear", "the substitution must have landed");

        var decoded = System.Text.Json.JsonSerializer.Deserialize<CoachTurnResponse>(
            payload, SentenceStudio.Contracts.Wire.WireJson.Client);

        decoded!.RepairDisclosure.Should().Be(CoachRepairDisclosure.Unknown,
            "the tolerant converter collapses an unreadable state rather than throwing");

        var (state, client) = await WorkspaceAsync();
        client.OnSubmitTurn = _ => TurnWith(decoded.RepairDisclosure);
        state.Draft = "How am I doing?";
        await state.SendDraftAsync();

        VisibleText(Notice(await RenderPaneAsync(state))).Should().Contain(UnknownEn);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ko")]
    public async Task A_clean_turn_shows_no_disclosure_in_either_language(string culture)
    {
        var (state, _) = await AfterTurnAsync(CoachRepairDisclosure.None);

        (await RenderPaneAsync(state, culture)).Should().NotContain("coach-repair-disclosure");
    }

    // ---------------------------------------------------------------- refusal precedence

    [Theory]
    [InlineData(CoachRepairDisclosure.AnswerAltered)]
    [InlineData(CoachRepairDisclosure.RepairSuppressedForLanguage)]
    [InlineData(CoachRepairDisclosure.Unknown)]
    public async Task A_refused_turn_shows_the_limitation_and_no_disclosure(
        CoachRepairDisclosure disclosure)
    {
        var (state, client) = await WorkspaceAsync();

        // Deliberately contradictory: a limitation AND a disclosure on the same turn. The server
        // does not send this, and the client must not render both if it ever does.
        client.OnSubmitTurn = _ => TurnWith(disclosure, Refusal());
        state.Draft = "Am I improving?";
        await state.SendDraftAsync();

        state.RepairDisclosure.Should().Be(disclosure,
            "the raw field still holds what the server sent");
        state.VisibleRepairDisclosure.Should().BeNull(
            "a refused turn produced no answer, so there is nothing to disclose about");

        var html = await RenderPaneAsync(state);

        html.Should().Contain("coach-refusal", "the refusal is what speaks for this turn");
        html.Should().NotContain("coach-repair-disclosure",
            "the attach path reads VisibleRepairDisclosure, so a refused turn's message never "
            + "carries one — refusal precedence is enforced once, not re-derived per renderer");
        state.PoliteAnnouncementKey.Should().StartWith("Coach_Announce_ClaimWithheld",
            "the refusal owns the announcement; two notices for one turn is one too many");
    }

    /// <summary>A refusal after a disclosed repair does not retract the earlier answer's note.</summary>
    [Fact]
    public async Task A_later_refusal_leaves_the_earlier_answers_note_standing()
    {
        var (state, client) = await AfterTurnAsync(CoachRepairDisclosure.AnswerAltered);

        client.OnSubmitTurn = _ => TurnWith(disclosure: null, limitation: Refusal());
        state.Draft = "Am I improving?";
        await state.SendDraftAsync();

        var html = await RenderPaneAsync(state);

        html.Should().Contain("coach-refusal");
        AllNotices(html).Should().HaveCount(1,
            "the earlier answer really was altered, and that stays true after a later refusal");
    }

    // ---------------------------------------------------------------- lifecycle

    /// <summary>
    /// A later turn gets its own note or none; it never inherits the previous turn's.
    /// </summary>
    /// <remarks>
    /// This is the successor to a case that asserted the next ordinary turn cleared the notice
    /// outright. That was the right rule for a single banner at the head of the log, where the one
    /// notice on screen would otherwise describe whichever answer was newest. Now that the note
    /// lives on the message it describes, the older answer keeps its own — it really was altered —
    /// and the assertion is the stronger one: exactly one note, on the first answer, none on the
    /// second.
    /// </remarks>
    [Fact]
    public async Task The_next_ordinary_turn_carries_no_disclosure_of_its_own()
    {
        var (state, client) = await AfterTurnAsync(CoachRepairDisclosure.AnswerAltered);
        (await RenderPaneAsync(state)).Should().Contain("coach-repair-disclosure",
            "the assertions below are vacuous unless something was showing first");

        client.OnSubmitTurn = _ => TurnWith(disclosure: null);
        state.Draft = "And what about reading?";
        await state.SendDraftAsync();

        client.SubmitTurnCalls.Should().Be(2, "both turns must have gone through ApplyTurn");
        state.RepairDisclosure.Should().BeNull(
            "the workspace-level field still clears, because it governs the announcement");
        state.PoliteAnnouncementKey.Should().NotBe("Coach_Announce_AnswerAltered",
            "the second turn altered nothing and must not re-announce the first turn's repair");

        var html = await RenderPaneAsync(state);
        var messages = AllCoachMessageBlocks(html);

        messages.Should().HaveCount(2);
        messages[0].Should().Contain("coach-repair-disclosure");
        messages[1].Should().NotContain("coach-repair-disclosure",
            "a disclosure that outlives its answer describes a different answer");
    }

    [Fact]
    public async Task A_disclosure_is_restored_on_resume()
    {
        var client = new FakeCoachApiClient();
        client.OnStartSession = () => SessionWith(CoachRepairDisclosure.AnswerAltered);

        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        state.RepairDisclosure.Should().Be(CoachRepairDisclosure.AnswerAltered,
            "the setter is private and nothing here can reach it, so ApplySession restored it");

        // And it renders nowhere, because a session read carries no plaintext transcript: there is
        // no answer on screen for the note to describe. The old pane-level banner rendered it
        // anyway, above an empty log.
        (await RenderPaneAsync(state)).Should().NotContain("coach-repair-disclosure");
    }

    /// <summary>
    /// With the ledger on screen the restored disclosure lands on the newest answer.
    /// </summary>
    [Fact]
    public async Task A_restored_disclosure_attaches_to_the_newest_answer_after_a_reload()
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        client.AddConversation("c-1");
        client.Seed("c-1", CoachMessageRole.Learner, "How am I doing this week?");
        client.Seed("c-1", CoachMessageRole.Coach, AnswerText);
        client.OnStartSession = () => SessionWith(CoachRepairDisclosure.AnswerAltered);

        var directory = new CoachConversationDirectory(client);
        var state = new CoachWorkspaceState(client, directory);

        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        state.RepairDisclosure.Should().Be(CoachRepairDisclosure.AnswerAltered);

        var html = await RenderPaneAsync(state);

        AllNotices(html).Should().HaveCount(1,
            "the note the learner saw before the reload describes an answer that is still on screen");
        CoachMessageBlock(html).Should().Contain("coach-repair-disclosure");
    }

    [Fact]
    public async Task A_session_whose_latest_turn_disclosed_nothing_restores_nothing()
    {
        var client = new FakeCoachApiClient();
        client.OnStartSession = () => SessionWith(disclosure: null);

        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        state.RepairDisclosure.Should().BeNull();
        state.RepairEvidenceOnScreen.Should().BeFalse();
        (await RenderPaneAsync(state)).Should().NotContain("coach-repair-disclosure");
    }

    // --------------------------------- B3: a restored note may not out-promise the restored thread
    //
    // The defect: after a durable reload the two states that point at the evidence rendered the
    // pointer anyway, and marked themselves data-coach-repair-evidence="true", beside an answer
    // with no evidence under it at all.
    //
    // Two independent ways in, and both are covered below. The session read can answer with an
    // evidence list — the shape is legal and CoachEvidenceDto names no turn it belongs to, so it
    // cannot be shown to be the restored answer's working. And a live turn that really did read
    // something leaves the workspace flag standing while ClearTranscript throws away the entries
    // that carried the rows, so the very next transcript load rebuilds the thread without them.
    //
    // Either way the ledger has no per-turn evidence to give back — CoachHistoryMessageDto has no
    // member for it — so the honest claim is "none", and the claim is now read off the entry the
    // note lands on rather than off the workspace.

    [Theory]
    [InlineData(CoachRepairDisclosure.RepairSuppressedForLanguage, SuppressedEn)]
    [InlineData(CoachRepairDisclosure.Unknown, UnknownEn)]
    public async Task A_restored_note_promises_no_evidence_even_when_the_session_read_carried_some(
        CoachRepairDisclosure disclosure, string copy)
    {
        var (state, _) = await AfterReloadAsync(disclosure, sessionEvidence: SomeEvidence);

        var answer = NewestAnswer(state);

        answer.RepairDisclosure.Should().Be(disclosure,
            "the note the learner saw before the reload still describes the answer on screen");
        answer.Evidence.Should().BeEmpty(
            "durable history carries no per-turn evidence, so the pane has no rows to render here "
            + "- this is the fact the copy is allowed to depend on");
        answer.RepairEvidenceOnScreen.Should().BeFalse(
            "the claim is read off the entry, and the entry has nothing beside it");

        state.Evidence.Should().NotBeEmpty(
            "the sticky workspace list is a separate, deliberate behaviour and must not be "
            + "collateral damage");
        state.RepairEvidenceOnScreen.Should().BeFalse(
            "the announcement channel must promise exactly what the visible copy promises");

        var html = await RenderPaneAsync(state);
        var notice = Notice(html);

        VisibleText(notice).Should().Contain(copy, "the state itself is still disclosed");
        VisibleText(notice).Should().NotContain(PointerEn,
            "sending the learner to look at evidence that is not on screen is the defect");
        notice.Should().Contain("data-coach-repair-evidence=\"false\"");

        AllNotices(html).Should().HaveCount(1, "one answer, one note");
        CoachMessageBlock(html).Should().Contain("coach-repair-disclosure",
            "message-level placement survives the reload");
    }

    [Theory]
    [InlineData(CoachRepairDisclosure.RepairSuppressedForLanguage, SuppressedEn)]
    [InlineData(CoachRepairDisclosure.Unknown, UnknownEn)]
    public async Task A_restored_note_reads_the_same_when_the_session_read_carried_nothing(
        CoachRepairDisclosure disclosure, string copy)
    {
        var (state, _) = await AfterReloadAsync(disclosure, sessionEvidence: NoEvidence);

        var answer = NewestAnswer(state);

        answer.RepairDisclosure.Should().Be(disclosure);
        answer.Evidence.Should().BeEmpty();
        answer.RepairEvidenceOnScreen.Should().BeFalse();
        state.RepairEvidenceOnScreen.Should().BeFalse();

        var notice = Notice(await RenderPaneAsync(state));

        VisibleText(notice).Should().Contain(copy);
        VisibleText(notice).Should().NotContain(PointerEn);
        notice.Should().Contain("data-coach-repair-evidence=\"false\"");
    }

    /// <summary>The restored no-evidence copy is localized, not an English fallback.</summary>
    [Theory]
    [InlineData(CoachRepairDisclosure.RepairSuppressedForLanguage, SuppressedKo, SuppressedEn)]
    [InlineData(CoachRepairDisclosure.Unknown, UnknownKo, UnknownEn)]
    public async Task A_korean_learner_reads_the_restored_note_in_korean(
        CoachRepairDisclosure disclosure, string korean, string english)
    {
        var (state, _) = await AfterReloadAsync(disclosure, sessionEvidence: SomeEvidence);

        var notice = VisibleText(Notice(await RenderPaneAsync(state, "ko")));

        notice.Should().Contain(korean);
        notice.Should().NotContain(english, "a Korean learner must not read the English fallback");
        notice.Should().NotContain(PointerKo, "there is still no evidence beside the answer");
    }

    /// <summary>A restored refusal outranks a restored disclosure, exactly as a live one does.</summary>
    [Fact]
    public async Task A_restored_refusal_still_suppresses_the_restored_note()
    {
        var (state, _) = await AfterReloadAsync(
            CoachRepairDisclosure.RepairSuppressedForLanguage,
            sessionEvidence: SomeEvidence,
            limitation: Refusal());

        state.VisibleRepairDisclosure.Should().BeNull(
            "a refused turn produced no answer, so there is nothing to disclose about");
        NewestAnswer(state).RepairDisclosure.Should().BeNull();
        (await RenderPaneAsync(state)).Should().NotContain("coach-repair-disclosure");
    }

    /// <summary>
    /// The stale-evidence sequence: a live turn that really read something, then a reload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the path that needs no unusual server at all. The turn read something, so the
    /// workspace flag went true; the reload then rebuilds the timeline from ledger rows that carry
    /// no evidence, while that flag survives untouched — <c>ClearTranscript</c> drops the entries,
    /// not the workspace. Reading the flag at re-attach time is what put the pointer back on an
    /// answer that no longer had anything under it.
    /// </para>
    /// <para>
    /// The precondition is asserted on the entry as well as on the flag: the live turn really does
    /// put its rows and its note on the ledger's own answer now, so this reads the same thing the
    /// learner was looking at before the reload took it away.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(CoachRepairDisclosure.RepairSuppressedForLanguage, SuppressedEn)]
    [InlineData(CoachRepairDisclosure.Unknown, UnknownEn)]
    public async Task A_reload_takes_back_the_pointer_the_live_turn_had_earned(
        CoachRepairDisclosure disclosure, string copy)
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        client.AddConversation("c-1");
        client.OnStartSession = () => SessionWith(disclosure: null);
        client.OnSubmitConversationTurn = (conversationId, request) =>
        {
            var learner = client.Seed(
                conversationId, CoachMessageRole.Learner, request.Turn.Text ?? string.Empty);
            var reply = client.Seed(conversationId, CoachMessageRole.Coach, AnswerText);

            return new CoachTurnOperationDto
            {
                OperationId = request.OperationId,
                ConversationId = conversationId,
                State = CoachTurnOperationState.Completed,
                Result = TurnWith(disclosure, evidence: SomeEvidence),
                Messages = [learner, reply],
                FirstResponseSequence = learner.Sequence,
                LastResponseSequence = reply.Sequence,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
        };

        var state = new CoachWorkspaceState(client, new CoachConversationDirectory(client));
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        state.Draft = "How am I doing this week?";
        await state.SendDraftAsync();

        // The assertions after the reload are vacuous unless the live turn really did read
        // something and leave that fact behind on the workspace.
        state.RepairDisclosure.Should().Be(disclosure);
        state.RepairEvidenceOnScreen.Should().BeTrue(
            "the turn read something, and this is the flag the restore path used to trust");
        state.Evidence.Should().NotBeEmpty();

        var live = NewestAnswer(state);

        live.Evidence.Should().NotBeEmpty("the live turn puts its rows on its own answer");
        live.RepairEvidenceOnScreen.Should().BeTrue(
            "and the note beside that answer may point at them while they are there");

        await state.LoadTranscriptAsync();

        var restored = NewestAnswer(state);

        restored.RepairDisclosure.Should().Be(disclosure, "the note survives the reload");
        restored.Evidence.Should().BeEmpty(
            "the ledger rebuilt the thread without the rows the live turn had read");
        restored.RepairEvidenceOnScreen.Should().BeFalse(
            "and the note may not keep pointing at rows the rebuilt thread does not have");
        state.RepairEvidenceOnScreen.Should().BeFalse("announcement parity");
        state.Evidence.Should().NotBeEmpty(
            "the sticky workspace list is untouched by this - it is not what the copy reads");

        var notice = Notice(await RenderPaneAsync(state));

        VisibleText(notice).Should().Contain(copy);
        VisibleText(notice).Should().NotContain(PointerEn);
        notice.Should().Contain("data-coach-repair-evidence=\"false\"");
    }

    /// <summary>An altered answer reads identically before and after a reload: it points at nothing.</summary>
    [Fact]
    public async Task An_altered_answer_is_unaffected_by_the_restored_evidence_rule()
    {
        var (state, _) = await AfterReloadAsync(
            CoachRepairDisclosure.AnswerAltered, sessionEvidence: SomeEvidence);

        VisibleText(Notice(await RenderPaneAsync(state))).Should().Contain(AlteredEn,
            "this state reports what happened to the answer and points at nothing, so the "
            + "evidence rule has no copy of its own to change");
    }

    // ------------------------------------------------------------------------
    // B4: a live durable turn says it on the spot, not after a reload
    //
    // A conversation the ledger owns renumbers every entry as its rows are merged, because the
    // timeline is read in server order and arrival order stops being server order the moment an
    // older page is fetched. The turn counter the client minted for the turn survives nowhere on
    // the entry, so an attach that compares against it reaches nothing: the evidence stayed in the
    // plan canvas and the repair note rendered nowhere at all until a reload rebuilt the thread.
    // These drive the real ledger-authoritative path - OpenConversationAsync, then SendDraftAsync
    // against an operation that carries its own canonical rows - and read the entry, not the
    // workspace.
    // ------------------------------------------------------------------------

    /// <summary>
    /// The turn that just ran cited something, and the learner can see both the rows and the note
    /// under the answer that cited them, without reloading anything.
    /// </summary>
    [Theory]
    [InlineData(CoachRepairDisclosure.RepairSuppressedForLanguage, SuppressedEn)]
    [InlineData(CoachRepairDisclosure.Unknown, UnknownEn)]
    public async Task A_live_durable_turn_shows_its_evidence_beside_its_own_answer(
        CoachRepairDisclosure disclosure, string copy)
    {
        var (state, _) = await AfterDurableTurnAsync(Durable(disclosure, SomeEvidence));

        var answer = NewestAnswer(state);

        answer.Evidence.Should().NotBeEmpty(
            "the rows this turn read belong under the answer that read them, and the ledger row "
            + "is the only copy of that answer on screen");
        answer.RepairDisclosure.Should().Be(disclosure,
            "the note describes that same answer and has to render beside it now, not after a "
            + "reload the learner has no reason to perform");
        answer.RepairEvidenceOnScreen.Should().BeTrue();

        var notice = Notice(await RenderPaneAsync(state));

        VisibleText(notice).Should().Contain(copy);
        VisibleText(notice).Should().Contain(PointerEn);
        notice.Should().Contain("data-coach-repair-evidence=\"true\"");
        state.RepairEvidenceOnScreen.Should().BeTrue("announcement parity");
    }

    /// <summary>The same live turn with nothing read: the note renders, and promises nothing.</summary>
    [Theory]
    [InlineData(CoachRepairDisclosure.RepairSuppressedForLanguage, SuppressedEn)]
    [InlineData(CoachRepairDisclosure.Unknown, UnknownEn)]
    public async Task A_live_durable_turn_that_cited_nothing_still_shows_its_note(
        CoachRepairDisclosure disclosure, string copy)
    {
        var (state, _) = await AfterDurableTurnAsync(Durable(disclosure, NoEvidence));

        var answer = NewestAnswer(state);

        answer.RepairDisclosure.Should().Be(disclosure,
            "a state that means part of the answer may have been rewritten is not something a "
            + "durable thread gets to stay quiet about");
        answer.Evidence.Should().BeEmpty();
        answer.RepairEvidenceOnScreen.Should().BeFalse();

        var notice = Notice(await RenderPaneAsync(state));

        VisibleText(notice).Should().Contain(copy);
        VisibleText(notice).Should().NotContain(PointerEn);
        notice.Should().Contain("data-coach-repair-evidence=\"false\"");
    }

    /// <summary>A Korean learner reads the live durable note in Korean, pointer and all.</summary>
    [Theory]
    [InlineData(CoachRepairDisclosure.RepairSuppressedForLanguage, SuppressedKo)]
    [InlineData(CoachRepairDisclosure.Unknown, UnknownKo)]
    public async Task A_korean_learner_reads_the_live_durable_note_in_korean(
        CoachRepairDisclosure disclosure, string copy)
    {
        var (state, _) = await AfterDurableTurnAsync(Durable(disclosure, SomeEvidence));

        var notice = Notice(await RenderPaneAsync(state, culture: "ko"));

        VisibleText(notice).Should().Contain(copy);
        VisibleText(notice).Should().Contain(PointerKo);
    }

    /// <summary>
    /// A later clean turn on the same durable thread leaves the earlier answer's note alone.
    /// </summary>
    /// <remarks>
    /// The earlier answer really was checked, and un-saying it once a newer question has been
    /// asked is a second untruth. The new answer says nothing, because nothing was disclosed
    /// about it.
    /// </remarks>
    [Fact]
    public async Task A_later_clean_durable_turn_leaves_the_earlier_note_where_it_was()
    {
        var (state, _) = await AfterDurableTurnAsync(
            Durable(CoachRepairDisclosure.RepairSuppressedForLanguage, SomeEvidence),
            Durable(disclosure: null, NoEvidence));

        var answers = Answers(state);

        answers.Should().HaveCount(2);
        answers[0].RepairDisclosure.Should().Be(CoachRepairDisclosure.RepairSuppressedForLanguage);
        answers[0].Evidence.Should().NotBeEmpty("the first answer really did read something");
        answers[1].RepairDisclosure.Should().BeNull("nothing was disclosed about the second");
        answers[1].Evidence.Should().BeEmpty();

        AllNotices(await RenderPaneAsync(state)).Should().ContainSingle(
            "one note, on the one answer it describes");
    }

    /// <summary>
    /// The stale-pointer case on the durable path: a second disclosed turn that read nothing may
    /// not send the learner to the first turn's rows.
    /// </summary>
    [Fact]
    public async Task A_second_durable_turn_that_read_nothing_does_not_borrow_the_first_answers_rows()
    {
        var (state, _) = await AfterDurableTurnAsync(
            Durable(CoachRepairDisclosure.RepairSuppressedForLanguage, SomeEvidence),
            Durable(CoachRepairDisclosure.RepairSuppressedForLanguage, NoEvidence));

        var answers = Answers(state);

        answers.Should().HaveCount(2);
        answers[0].RepairEvidenceOnScreen.Should().BeTrue("unchanged - it really did read something");
        answers[0].Evidence.Should().NotBeEmpty();
        answers[1].RepairEvidenceOnScreen.Should().BeFalse(
            "the panel still on screen belongs to the first question, and sending the learner "
            + "there is sending them to another answer's working");
        answers[1].Evidence.Should().BeEmpty();

        var notices = AllNotices(await RenderPaneAsync(state));

        notices.Should().HaveCount(2);
        VisibleText(notices[0]).Should().Contain(PointerEn);
        VisibleText(notices[1]).Should().NotContain(PointerEn);
        state.Evidence.Should().NotBeEmpty("the sticky workspace list is still sticky");
    }

    /// <summary>A refusal on a durable turn attaches no note to the ledger's answer.</summary>
    [Fact]
    public async Task A_refused_durable_turn_hangs_no_note_on_the_ledger_answer()
    {
        var (state, _) = await AfterDurableTurnAsync(
            Durable(CoachRepairDisclosure.RepairSuppressedForLanguage, SomeEvidence, Refusal()));

        state.Timeline.Should().OnlyContain(e => e.RepairDisclosure == null,
            "a refused turn produced no answer, so there is nothing to disclose about");
        AllNotices(await RenderPaneAsync(state)).Should().BeEmpty();
    }

    /// <summary>
    /// A durable turn whose only reply was a notice hangs nothing on the answer above it.
    /// </summary>
    /// <remarks>
    /// This is the guard on the rule that replaced the turn-counter comparison. "Newest coach
    /// message on the list" would hand back the previous exchange's answer on a turn that added no
    /// answer of its own, and pin this turn's evidence and note to a question the learner already
    /// had answered. Being above the mark taken when the turn was submitted is what the newest
    /// entry has to prove, and a notice cannot.
    /// </remarks>
    [Fact]
    public async Task A_durable_turn_that_only_produced_a_notice_leaves_the_earlier_answer_alone()
    {
        var (state, _) = await AfterDurableTurnAsync(
            Durable(CoachRepairDisclosure.RepairSuppressedForLanguage, SomeEvidence),
            Durable(CoachRepairDisclosure.Unknown, SomeEvidence, replyKind: CoachMessageKind.Notice));

        var answers = Answers(state);

        answers.Should().ContainSingle("the second turn answered with a notice, not an answer");
        answers[0].RepairDisclosure.Should().Be(CoachRepairDisclosure.RepairSuppressedForLanguage,
            "the earlier answer keeps its own note and is not overwritten by a later turn's");

        AllNotices(await RenderPaneAsync(state)).Should().ContainSingle(
            "the second turn's note has no answer of its own to sit under, so it renders nowhere "
            + "rather than on somebody else's answer");
    }

    /// <summary>
    /// A turn with no message of its own hangs nothing on the answer above it.
    /// </summary>
    /// <remarks>
    /// Accepting a suggestion runs a turn the learner never typed into: there is no question to
    /// mark the boundary, and the ledger row on top is the previous exchange's answer. "Newest
    /// coach message on the list" would give this turn's evidence and its note to that answer,
    /// which is the stale-pointer defect one rung up — a true sentence pointing at the wrong
    /// exchange. Provenance is what stops it: that row arrived on an earlier turn and says so.
    /// </remarks>
    [Fact]
    public async Task A_durable_turn_with_no_message_of_its_own_leaves_the_answer_above_it_alone()
    {
        var (state, client) = await AfterDurableTurnAsync(
            Durable(disclosure: null, NoEvidence, suggestion: CoachStateMachineTests.Suggestion()));

        var before = NewestAnswer(state);

        before.RepairDisclosure.Should().BeNull("nothing has been disclosed yet");
        state.PendingSuggestion.Should().NotBeNull("the accept below needs a card to accept");

        client.OnAccept = () => TurnWith(
            CoachRepairDisclosure.Unknown,
            evidence: SomeEvidence,
            messages: Array.Empty<CoachMessageDto>());

        await state.AcceptSuggestionAsync();

        var after = NewestAnswer(state);

        after.RepairDisclosure.Should().BeNull(
            "the accepted turn produced no answer, so its note has nothing of its own to sit "
            + "under and may not borrow the answer to an earlier question");
        after.Evidence.Should().BeEmpty(
            "and neither may the rows it read - they belong to the plan, not to that sentence");

        AllNotices(await RenderPaneAsync(state)).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- shape

    [Fact]
    public async Task The_notice_is_a_polite_status_with_a_name()    {
        var (state, _) = await AfterTurnAsync(CoachRepairDisclosure.AnswerAltered);

        var notice = Notice(await RenderPaneAsync(state));

        notice.Should().Contain("role=\"status\"");
        notice.Should().Contain("aria-live=\"polite\"");
        notice.Should().Contain("aria-label=");
        notice.Should().NotContain("role=\"alert\"");

        notice.EnumerateRunes()
            .Where(r => r.Value is (>= 0x1F300 and <= 0x1FAFF) or (>= 0x2600 and <= 0x27BF) or 0xFE0F)
            .Should().BeEmpty("this app uses Bootstrap icons or plain text, never emoji");
    }

    [Theory]
    [InlineData("en", true)]
    [InlineData("en", false)]
    [InlineData("ko", true)]
    [InlineData("ko", false)]
    public async Task The_notice_carries_no_counts_rule_codes_or_learner_content(
        string culture, bool withEvidence)
    {
        var (state, client) = await WorkspaceAsync();
        client.OnSubmitTurn = _ => TurnWith(
            CoachRepairDisclosure.AnswerAltered,
            evidence: withEvidence ? SomeEvidence : NoEvidence);
        state.Draft = "\uC740/\uB294 marks the topic?";
        await state.SendDraftAsync();

        var notice = Notice(await RenderPaneAsync(state, culture));
        var text = VisibleText(notice);

        text.Should().NotMatchRegex(@"\d", "a count of findings is an audit log, not a disclosure");
        foreach (var forbidden in new[] { "rule", "R7", "finding", "span", "grounding", "claim rule" })
        {
            text.ToLowerInvariant().Should().NotContain(forbidden.ToLowerInvariant(),
                $"'{forbidden}' is operator vocabulary the learner cannot act on");
        }

        notice.Should().NotContain("\uC740/\uB294 marks the topic",
            "the learner's words live in the conversation, not in a status note");

        // The one enum name that reaches the DOM is a closed, harmless status marker.
        notice.Should().Contain("data-coach-repair=\"AnswerAltered\"");
    }

    [Theory]
    [InlineData(CoachRepairDisclosure.AnswerAltered, true)]
    [InlineData(CoachRepairDisclosure.AnswerAltered, false)]
    [InlineData(CoachRepairDisclosure.RepairSuppressedForLanguage, true)]
    [InlineData(CoachRepairDisclosure.RepairSuppressedForLanguage, false)]
    [InlineData(CoachRepairDisclosure.Unknown, true)]
    [InlineData(CoachRepairDisclosure.Unknown, false)]
    public async Task Both_hosts_render_the_disclosure_identically(
        CoachRepairDisclosure disclosure, bool withEvidence)
    {
        var (web, _) = await AfterTurnAsync(disclosure, withEvidence);
        var (view, _) = await AfterTurnAsync(disclosure, withEvidence);

        Notice(await RenderPaneAsync(web, baseUri: WebBaseUri))
            .Should().Be(Notice(await RenderPaneAsync(view, baseUri: WebViewBaseUri)));
    }

    /// <summary>Altered says the same thing either way: it points at nothing to begin with.</summary>
    [Theory]
    [InlineData("en")]
    [InlineData("ko")]
    public async Task An_altered_answer_reads_the_same_with_or_without_evidence(string culture)
    {
        var (withEvidence, _) = await AfterTurnAsync(CoachRepairDisclosure.AnswerAltered, true);
        var (without, _) = await AfterTurnAsync(CoachRepairDisclosure.AnswerAltered, false);

        VisibleText(Notice(await RenderPaneAsync(withEvidence, culture)))
            .Should().Be(VisibleText(Notice(await RenderPaneAsync(without, culture))),
                "this state reports what happened to the answer and points nowhere, so there is "
                + "nothing for the evidence flag to change");

        withEvidence.PoliteAnnouncementKey.Should().Be(without.PoliteAnnouncementKey);
    }

    /// <summary>An older server simply omits the property, and the client reads what it always did.</summary>
    [Fact]
    public async Task An_old_client_payload_without_the_property_renders_nothing()
    {
        var (state, client) = await WorkspaceAsync();

        // CoachStateMachineTests.Turn() predates the property and never sets it.
        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn();
        state.Draft = "How am I doing?";
        await state.SendDraftAsync();

        state.RepairDisclosure.Should().BeNull();
        (await RenderPaneAsync(state)).Should().NotContain("coach-repair-disclosure");
    }

    // ---------------------------------------------------------------- copy

    /// <summary>Every key the matrix can select exists in both languages.</summary>
    [Theory]
    [InlineData("Coach_Repair_AnswerAltered")]
    [InlineData("Coach_Repair_SuppressedForLanguage")]
    [InlineData("Coach_Repair_SuppressedForLanguage_NoEvidence")]
    [InlineData("Coach_Repair_Unknown")]
    [InlineData("Coach_Repair_Unknown_NoEvidence")]
    [InlineData("Coach_Announce_AnswerAltered")]
    [InlineData("Coach_Announce_RepairSuppressed")]
    [InlineData("Coach_Announce_RepairSuppressedNoEvidence")]
    [InlineData("Coach_Announce_RepairUnknown")]
    [InlineData("Coach_Announce_RepairUnknownNoEvidence")]
    public void Every_disclosure_string_exists_in_both_languages(string key)
    {
        Resource("AppResources.resx", key).Should().NotBeNullOrWhiteSpace(
            "a missing English string renders as the key itself");
        Resource("AppResources.ko.resx", key).Should().NotBeNullOrWhiteSpace(
            "an untranslated disclosure falls back to English mid-sentence for a Korean learner");
    }

    /// <summary>
    /// The Korean disclosure copy names the coach as 쌤, never a near-miss syllable.
    /// </summary>
    /// <remarks>
    /// <c>CoachPersonaCopyGuardTests</c> scans the whole coach family for the two syllables that
    /// have actually shipped in place of 쌤. This names the six strings of this feature that do
    /// carry the subject, so a rewrite that drops or mangles it fails here with the key in the
    /// message rather than in a family-wide scan.
    /// </remarks>
    [Theory]
    [InlineData("Coach_Repair_AnswerAltered")]
    [InlineData("Coach_Repair_SuppressedForLanguage")]
    [InlineData("Coach_Repair_SuppressedForLanguage_NoEvidence")]
    [InlineData("Coach_Repair_Unknown")]
    [InlineData("Coach_Repair_Unknown_NoEvidence")]
    [InlineData("Coach_Announce_RepairSuppressedNoEvidence")]
    public void The_korean_disclosure_copy_names_the_persona_correctly(string key)
    {
        var korean = Resource("AppResources.ko.resx", key);

        korean.Should().Contain("\uC324", "the coach is \uC324 in Korean copy");
        korean.Should().NotContain("\uC300", "'\uC300' is rice, and has shipped for the persona twice");
        korean.Should().NotContain("\uC30D", "'\uC30D' is a pair");
    }

    // ---------------------------------------------------------------- fixtures

    /// <summary>The text of the one coach answer every fixture turn produces.</summary>
    private const string AnswerText = "You have been reading more than speaking.";

    private static readonly IReadOnlyList<CoachEvidenceDto> NoEvidence = Array.Empty<CoachEvidenceDto>();

    private static readonly IReadOnlyList<CoachEvidenceDto> SomeEvidence =
    [
        new CoachEvidenceDto
        {
            Kind = CoachEvidenceKind.PracticeBalance,
            Label = "Practice balance",
            Summary = "Mostly reading this week.",
            WindowStartDate = new DateOnly(2026, 8, 14),
            WindowEndDate = new DateOnly(2026, 8, 20),
            Values = []
        }
    ];

    private static async Task<(CoachWorkspaceState State, FakeCoachApiClient Client)> AfterTurnAsync(
        CoachRepairDisclosure? disclosure,
        bool withEvidence = false)
    {
        var (state, client) = await WorkspaceAsync();

        client.OnSubmitTurn = _ => TurnWith(
            disclosure, evidence: withEvidence ? SomeEvidence : NoEvidence);
        state.Draft = "How am I doing this week?";
        await state.SendDraftAsync();

        return (state, client);
    }

    private static async Task<(CoachWorkspaceState State, FakeCoachApiClient Client)> WorkspaceAsync()
    {
        var client = new FakeCoachApiClient();
        client.OnStartSession = () => SessionWith(disclosure: null);

        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        return (state, client);
    }

    /// <summary>
    /// A durable thread read back cold: real <c>ApplySession</c>, then real
    /// <c>LoadTranscriptAsync</c>, with one of Sam's answers in the ledger for the restored note to
    /// land on.
    /// </summary>
    /// <remarks>
    /// The evidence is put on the <em>session read</em> deliberately. That is the only place a
    /// restore has ever been able to see any, and it is exactly the list the restored note used to
    /// read its promise from.
    /// </remarks>
    private static async Task<(CoachWorkspaceState State, FakeCoachApiClient Client)> AfterReloadAsync(
        CoachRepairDisclosure? disclosure,
        IReadOnlyList<CoachEvidenceDto>? sessionEvidence = null,
        CoachLimitationDto? limitation = null)
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        client.AddConversation("c-1");
        client.Seed("c-1", CoachMessageRole.Learner, "How am I doing this week?");
        client.Seed("c-1", CoachMessageRole.Coach, AnswerText);
        client.OnStartSession = () => SessionWith(disclosure, sessionEvidence, limitation);

        var state = new CoachWorkspaceState(client, new CoachConversationDirectory(client));
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        return (state, client);
    }

    /// <summary>
    /// The newest of Sam's answers on the timeline — the entry the disclosure attaches to.
    /// </summary>
    /// <remarks>
    /// The same three rules the attach path uses, so a test that asks "what does the learner see
    /// beside this note" is reading the entry the note is actually on. Notices and receipts are
    /// excluded because neither is an answer.
    /// </remarks>
    private static CoachTimelineEntry NewestAnswer(CoachWorkspaceState state) =>
        state.Timeline.Last(e =>
            e.Kind == CoachTimelineKind.CoachMessage
            && e.Message is not { Kind: CoachMessageKind.Notice or CoachMessageKind.Receipt });

    /// <summary>Every one of Sam's answers on the timeline, in read order.</summary>
    private static IReadOnlyList<CoachTimelineEntry> Answers(CoachWorkspaceState state) =>
        state.Timeline
            .Where(e => e.Kind == CoachTimelineKind.CoachMessage
                        && e.Message is not
                        {
                            Kind: CoachMessageKind.Notice
                                or CoachMessageKind.Receipt
                                or CoachMessageKind.Suggestion
                        })
            .ToList();

    /// <summary>One turn's worth of what a ledger-authoritative operation will answer with.</summary>
    /// <param name="Disclosure">The repair state the turn discloses, or null for silence.</param>
    /// <param name="Evidence">What the turn read, which is what its answer may point at.</param>
    /// <param name="Limitation">A refusal, when the turn refused.</param>
    /// <param name="ReplyKind">
    /// The kind of the coach row the ledger carries. A notice is a turn that did not answer, which
    /// is the case that must not borrow the previous answer.
    /// </param>
    private sealed record DurableTurn(
        CoachRepairDisclosure? Disclosure,
        IReadOnlyList<CoachEvidenceDto> Evidence,
        CoachLimitationDto? Limitation,
        CoachMessageKind ReplyKind,
        PendingCoachSuggestionDto? Suggestion);

    private static DurableTurn Durable(
        CoachRepairDisclosure? disclosure,
        IReadOnlyList<CoachEvidenceDto> evidence,
        CoachLimitationDto? limitation = null,
        CoachMessageKind replyKind = CoachMessageKind.Text,
        PendingCoachSuggestionDto? suggestion = null) =>
        new(disclosure, evidence, limitation, replyKind, suggestion);

    /// <summary>
    /// One or more live turns on a conversation whose transcript the ledger owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The operation carries its own canonical rows, which is what puts the workspace into
    /// ledger-authoritative mode: the response body is no longer the transcript, so the answer,
    /// the evidence and the note all have to find the server's row instead of a locally appended
    /// one. Nothing here writes timeline state — it is <c>OpenConversationAsync</c> and
    /// <c>SendDraftAsync</c> the whole way down.
    /// </para>
    /// <para>
    /// No reload is performed, deliberately. Every assertion above is about what the learner sees
    /// while the turn is still the newest thing on screen.
    /// </para>
    /// </remarks>
    private static async Task<(CoachWorkspaceState State, FakeCoachApiClient Client)> AfterDurableTurnAsync(
        params DurableTurn[] turns)
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        client.AddConversation("c-1");
        client.OnStartSession = () => SessionWith(disclosure: null);

        var next = 0;

        client.OnSubmitConversationTurn = (conversationId, request) =>
        {
            var turn = turns[Math.Min(next++, turns.Length - 1)];

            var learner = client.Seed(
                conversationId, CoachMessageRole.Learner, request.Turn.Text ?? string.Empty);
            var reply = client.Seed(
                conversationId, CoachMessageRole.Coach, AnswerText, kind: turn.ReplyKind,
                noticeReasonCode: turn.ReplyKind == CoachMessageKind.Notice
                    ? CoachNoticeReasonCodes.Default
                    : null);

            return new CoachTurnOperationDto
            {
                OperationId = request.OperationId,
                ConversationId = conversationId,
                State = CoachTurnOperationState.Completed,
                Result = TurnWith(
                    turn.Disclosure, turn.Limitation, turn.Evidence, suggestion: turn.Suggestion),
                Messages = [learner, reply],
                FirstResponseSequence = learner.Sequence,
                LastResponseSequence = reply.Sequence,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
        };

        var state = new CoachWorkspaceState(client, new CoachConversationDirectory(client));
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        for (var i = 0; i < turns.Length; i++)
        {
            state.Draft = "How am I doing this week?";
            await state.SendDraftAsync();
        }

        return (state, client);
    }

    private static CoachLimitationDto Refusal() => new()
    {
        Code = CoachLimitationCode.UnverifiedClaimWithheld,
        Coverage = CoachEvidenceCoverage.PageOfOwnedSet,
        Destination = CoachRouteCatalog.Build(CoachRouteName.Vocabulary),
        Alternatives = [],
        HintLadder = [],
        ShorterSession = null
    };

    private static CoachSessionResponse SessionWith(
        CoachRepairDisclosure? disclosure,
        IReadOnlyList<CoachEvidenceDto>? evidence = null,
        CoachLimitationDto? limitation = null)
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
            Evidence = evidence ?? session.Evidence,
            Dispute = session.Dispute,
            Limitation = limitation ?? session.Limitation,
            RepairDisclosure = disclosure,
            Revisions = session.Revisions,
            ClarificationsRemaining = session.ClarificationsRemaining,
            RunsRemainingToday = session.RunsRemainingToday,
            CreatedAtUtc = session.CreatedAtUtc,
            ExpiresAtUtc = session.ExpiresAtUtc
        };
    }

    /// <summary>
    /// A turn carrying one of Sam's answers, so the disclosure has the thing it describes to sit
    /// under. A turn with no answer is its own case above, deliberately.
    /// </summary>
    private static CoachTurnResponse TurnWith(
        CoachRepairDisclosure? disclosure,
        CoachLimitationDto? limitation = null,
        IReadOnlyList<CoachEvidenceDto>? evidence = null,
        IReadOnlyList<CoachMessageDto>? messages = null,
        PendingCoachSuggestionDto? suggestion = null)
    {
        var turn = CoachStateMachineTests.Turn();

        return new CoachTurnResponse
        {
            SessionId = turn.SessionId,
            TurnId = turn.TurnId,
            Status = turn.Status,
            StopReason = turn.StopReason,
            SessionStatus = turn.SessionStatus,
            Messages = messages ??
            [
                new CoachMessageDto
                {
                    MessageId = "m-" + Guid.NewGuid().ToString("N"),
                    Role = CoachMessageRole.Coach,
                    Kind = CoachMessageKind.Text,
                    Text = AnswerText,
                    CreatedAtUtc = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc)
                }
            ],
            ActiveConstraints = turn.ActiveConstraints,
            PlanState = turn.PlanState,
            PendingSuggestion = suggestion ?? turn.PendingSuggestion,
            ChangeReceipt = turn.ChangeReceipt,
            Answer = turn.Answer,
            Evidence = evidence ?? turn.Evidence,
            Dispute = turn.Dispute,
            Limitation = limitation,
            RepairDisclosure = disclosure,
            ClarifyingQuestion = turn.ClarifyingQuestion,
            ClarificationsRemaining = turn.ClarificationsRemaining,
            RunsRemainingToday = turn.RunsRemainingToday,
            ExpiresAtUtc = turn.ExpiresAtUtc,
            MemoryCandidate = turn.MemoryCandidate,
            WriteOperation = turn.WriteOperation
        };
    }

    private static string Notice(string html)
    {
        var notices = AllNotices(html);

        notices.Should().NotBeEmpty("the notice must be present for its markup to be checked");
        return notices[0];
    }

    /// <summary>Every disclosure on the page, in reading order.</summary>
    private static List<string> AllNotices(string html)
    {
        var found = new List<string>();
        var cursor = 0;

        while (true)
        {
            var start = html.IndexOf("<div class=\"coach-repair-disclosure", cursor, StringComparison.Ordinal);

            if (start < 0)
            {
                return found;
            }

            var end = html.IndexOf("</div>", start, StringComparison.Ordinal);
            end.Should().BeGreaterThan(start, "the notice must be closed");

            var notice = html[start..(end + "</div>".Length)];
            notice.IndexOf("<div", 1, StringComparison.Ordinal)
                .Should().Be(-1, "a nested div would mean this is truncated at the wrong close tag");

            found.Add(notice);
            cursor = end;
        }
    }

    /// <summary>The first of Sam's message elements, from its opening tag to the next one.</summary>
    private static string CoachMessageBlock(string html) => AllCoachMessageBlocks(html)[0];

    /// <summary>
    /// Sam's message elements, sliced at their opening tags. Learner messages are excluded.
    /// </summary>
    /// <remarks>
    /// Sliced rather than parsed on purpose: the message element nests, so matching close tags
    /// would need a parser, and the question these answer — "is the note inside this message and
    /// after its answer, rather than at the head of the log" — is answered by the span between one
    /// message's opening tag and the next message's.
    /// </remarks>
    private static List<string> AllCoachMessageBlocks(string html)
    {
        var starts = new List<int>();
        var cursor = 0;

        while (true)
        {
            var start = html.IndexOf("<div class=\"coach-message ", cursor, StringComparison.Ordinal);

            if (start < 0)
            {
                break;
            }

            starts.Add(start);
            cursor = start + 1;
        }

        var blocks = new List<string>();

        for (var i = 0; i < starts.Count; i++)
        {
            var start = starts[i];
            var end = i + 1 < starts.Count ? starts[i + 1] : html.Length;
            var tagEnd = html.IndexOf('>', start);

            // The learner's own words are not an answer, and nothing about grounding attaches to
            // them. Reading the opening tag is what tells the two apart: Sam's messages carry the
            // bare class, the learner's carry the modifier.
            if (tagEnd > start
                && html[start..tagEnd].Contains("coach-message-learner", StringComparison.Ordinal))
            {
                continue;
            }

            blocks.Add(html[start..end]);
        }

        return blocks;
    }

    private static string VisibleText(string html) =>
        System.Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", " ");

    /// <summary>One string from a shipped resource file, read as the build ships it.</summary>
    private static string Resource(string fileName, string key)
    {
        var path = Path.Combine(
            RepoRoot(), "src", "SentenceStudio.Shared", "Resources", "Strings", fileName);

        var document = System.Xml.Linq.XDocument.Load(path);

        return document.Root!
            .Elements("data")
            .Where(e => string.Equals((string?)e.Attribute("name"), key, StringComparison.Ordinal))
            .Select(e => (string?)e.Element("value") ?? string.Empty)
            .FirstOrDefault() ?? string.Empty;
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the tests run from inside the repository");
        return directory!.FullName;
    }

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
            services.AddSingleton<NavigationManager>(new DisclosureNavigationManager(baseUri));

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

    private sealed class DisclosureNavigationManager : NavigationManager
    {
        public DisclosureNavigationManager(string baseUri) => Initialize(baseUri, baseUri);

        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
}
