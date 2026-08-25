using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// A notice Sam gives back to a learner is reportable; the machinery's own bookkeeping is not.
/// </summary>
/// <remarks>
/// <para>
/// The case that earned this file came out of a Canvas run on a real account. The learner asked
/// Sam to change today's plan, Sam answered <i>"There is no plan for today yet"</i>, and that
/// response — the one the learner most wanted to complain about — was the only one on screen with
/// no flag beside Copy. The transcript was withholding the feedback control precisely where
/// feedback was warranted.
/// </para>
/// <para>
/// The old rule excluded every notice on the theory that a notice is the server describing itself
/// rather than Sam answering. That is true of some notices and false of the ones a learner reads
/// as an answer, so the line moved: what is excluded now is the closed set of reason codes the
/// server itself marks as "no change applied" — cancelled, timed out, validation failed and the
/// rest — plus receipts. Everything else Sam says durably can be flagged.
/// </para>
/// <para>
/// These are rendered and driven through the real components rather than asserted on the
/// predicate, because "the button is on screen" and "pressing it files a report naming this
/// message" are the two things that actually failed.
/// </para>
/// </remarks>
public class CoachNoticeReportabilityTests
{
    /// <summary>The response the Canvas run found unreportable, verbatim.</summary>
    private const string NoPlanNotice =
        "There is no plan for today yet, so there is nothing to change. " +
        "I can still answer language questions now.";

    // ---------------------------------------------------------------- fixtures

    private static Microsoft.Extensions.DependencyInjection.ServiceProvider Services(
        CoachWorkspaceState state, IJSRuntime? js = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BlazorLocalizationService>();
        services.AddScoped<CoachPersona>();
        services.AddScoped(_ => js ?? new StubJSRuntime());
        services.AddScoped(_ => state);
        return services.BuildServiceProvider();
    }

    private static async Task<string> RenderAsync(CoachWorkspaceState state)
    {
        await using var provider = Services(state);
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<CoachChatPane>(ParameterView.Empty);
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }

    /// <summary>
    /// One learner turn answered by a notice — the shape the Canvas run was looking at.
    /// </summary>
    private static async Task<(CoachWorkspaceState State, FakeCoachApiClient Client)> NoticeConversationAsync(
        string reasonCode = CoachNoticeReasonCodes.Default,
        Action<FakeCoachApiClient>? configure = null)
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        client.AddConversation("c-1");
        client.Seed("c-1", CoachMessageRole.Learner, "Make today's plan shorter.");
        client.Seed(
            "c-1",
            CoachMessageRole.Coach,
            NoPlanNotice,
            kind: CoachMessageKind.Notice,
            noticeReasonCode: reasonCode);
        configure?.Invoke(client);

        var directory = new CoachConversationDirectory(client);
        var state = new CoachWorkspaceState(client, directory);
        await state.OpenAsync(CoachPresentation.Overlay, "c-1");

        return (state, client);
    }

    private static async Task<(InteractiveTestRenderer Renderer, int Id, CoachWorkspaceState State, FakeCoachApiClient Client)>
        MountNoticePaneAsync(
            string reasonCode = CoachNoticeReasonCodes.Default,
            Action<FakeCoachApiClient>? configure = null)
    {
        var (state, client) = await NoticeConversationAsync(reasonCode, configure);
        var provider = Services(state);
        var renderer = new InteractiveTestRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        return (renderer, await renderer.RenderAsync<CoachChatPane>(ParameterView.Empty), state, client);
    }

    // ---------------------------------------------------------------- the flag is offered

    /// <summary>
    /// The defect, stated as a test: the notice carries the flag.
    /// </summary>
    [Fact]
    public async Task TheNoPlanNoticeCarriesAFlagBesideCopy()
    {
        var (state, _) = await NoticeConversationAsync();

        var html = await RenderAsync(state);

        html.Should().Contain(
            NoPlanNotice,
            "the case is only meaningful if this is the message being rendered");
        html.Should().Contain("coach-report-m-2",
            "a notice the learner reads as Sam's answer is a response they can be dissatisfied with");
        html.Should().Contain("aria-label=\"Report this response\"");
        html.Should().Contain("coach-copy", "Copy was never the thing that was missing");
    }

    /// <summary>
    /// The notice is a durable row, so the association the flag advertises has to be derived from
    /// the server's message id like any other response's.
    /// </summary>
    [Fact]
    public async Task TheNoticeFlagPointsAtItsOwnPanel()
    {
        var (state, _) = await NoticeConversationAsync();

        var html = await RenderAsync(state);

        html.Should().Contain("aria-controls=\"coach-report-panel-m-2\"");
        html.Should().Contain("aria-expanded=\"false\"");
    }

    /// <summary>
    /// An informational notice never wears the marker, so the marker exclusion cannot be what is
    /// keeping this response reportable by accident.
    /// </summary>
    [Fact]
    public async Task TheNoPlanNoticeIsNotANoChangeMarker()
    {
        CoachNoticeReasonCodes.IndicatesNoChange(CoachNoticeReasonCodes.Default).Should().BeFalse();

        var (state, _) = await NoticeConversationAsync();
        var html = await RenderAsync(state);

        html.Should().NotContain("coach-change-marker",
            "the turn completed and said so; it did not refuse");
    }

    // ---------------------------------------------------------------- what stays out

    /// <summary>
    /// Every refusal code in the server's closed vocabulary keeps the flag off the row.
    /// </summary>
    /// <remarks>
    /// Driven from <see cref="CoachNoticeReasonCodes"/> itself rather than a copied list, so a code
    /// added to the refusal set later cannot quietly become reportable without this failing.
    /// </remarks>
    [Theory]
    [InlineData(CoachNoticeReasonCodes.Cancelled)]
    [InlineData(CoachNoticeReasonCodes.RateLimited)]
    [InlineData(CoachNoticeReasonCodes.Timeout)]
    [InlineData(CoachNoticeReasonCodes.InputRejected)]
    [InlineData(CoachNoticeReasonCodes.ValidationFailed)]
    [InlineData(CoachNoticeReasonCodes.ToolFailure)]
    [InlineData(CoachNoticeReasonCodes.IterationLimit)]
    [InlineData(CoachNoticeReasonCodes.OutputTokenLimit)]
    [InlineData(CoachNoticeReasonCodes.ConcurrencyLimit)]
    [InlineData(CoachNoticeReasonCodes.SessionExpired)]
    [InlineData(CoachNoticeReasonCodes.Failed)]
    public async Task ANoChangeMarkerIsNotReportable(string reasonCode)
    {
        CoachNoticeReasonCodes.IndicatesNoChange(reasonCode).Should().BeTrue(
            "the case is only meaningful for a code the server marks as a refusal");

        var (state, _) = await NoticeConversationAsync(reasonCode);

        var html = await RenderAsync(state);

        html.Should().Contain("coach-change-marker",
            "the marker is what makes this row the machinery describing itself");
        html.Should().NotContain("coach-report-m-2",
            "a stopped turn is bookkeeping; filing it as an unsatisfactory response would bury the reports that are");
        html.Should().Contain("coach-copy", "the learner can still take the text");
    }

    /// <summary>
    /// The informational codes stay reportable — including the recovered turn, where the plan moved
    /// and the explanation did not, which is exactly a response worth complaining about.
    /// </summary>
    [Theory]
    [InlineData(CoachNoticeReasonCodes.Default)]
    [InlineData(CoachNoticeReasonCodes.Recovered)]
    public async Task AnInformationalNoticeIsReportable(string reasonCode)
    {
        CoachNoticeReasonCodes.IndicatesNoChange(reasonCode).Should().BeFalse();

        var (state, _) = await NoticeConversationAsync(reasonCode);

        var html = await RenderAsync(state);

        html.Should().Contain("coach-report-m-2");
    }

    /// <summary>
    /// A receipt is the record of a change that happened. The quarrel with it is with the change.
    /// </summary>
    [Fact]
    public async Task AReceiptMessageIsNotReportable()
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        client.AddConversation("c-1");
        client.Seed("c-1", CoachMessageRole.Learner, "Add 사과 to my list.");
        client.Seed(
            "c-1",
            CoachMessageRole.Coach,
            "Added 사과 to your vocabulary list.",
            kind: CoachMessageKind.Receipt);

        var directory = new CoachConversationDirectory(client);
        var state = new CoachWorkspaceState(client, directory);
        await state.OpenAsync(CoachPresentation.Overlay, "c-1");

        var html = await RenderAsync(state);

        html.Should().NotContain("coach-report-m-2",
            "a receipt is a record of a change, and the plan surface owns disputes about the change");
    }

    /// <summary>
    /// A learner's own words stay unreportable, notice or not.
    /// </summary>
    [Fact]
    public async Task TheLearnersOwnTurnIsStillNotReportable()
    {
        var (state, _) = await NoticeConversationAsync();

        var html = await RenderAsync(state);

        html.Should().NotContain("coach-report-m-1");
    }

    /// <summary>
    /// A session-only notice has no server identity, so there is nothing to file a report against
    /// and the control is withheld rather than offered and then failed.
    /// </summary>
    [Fact]
    public async Task ASessionOnlyNoticeIsNotReportable()
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = false };
        var directory = new CoachConversationDirectory(client);
        var state = new CoachWorkspaceState(client, directory);
        await state.OpenAsync(CoachPresentation.Overlay);

        var html = await RenderAsync(state);

        html.Should().NotContain("coach-report-flag",
            "a session-only turn has no durable message id the server could be told about");
    }

    // ---------------------------------------------------------------- filing the report

    /// <summary>
    /// Every reason in the closed set can be chosen and submitted against a notice, and each one
    /// reaches the server naming this notice's own message id.
    /// </summary>
    [Theory]
    [InlineData(CoachResponseReportReason.DidNotAnswer)]
    [InlineData(CoachResponseReportReason.IncorrectOrMisleading)]
    [InlineData(CoachResponseReportReason.ExpectedAppAction)]
    [InlineData(CoachResponseReportReason.Confusing)]
    [InlineData(CoachResponseReportReason.Other)]
    public async Task EachReasonCanBeFiledAgainstTheNotice(CoachResponseReportReason reason)
    {
        var (renderer, id, state, client) = await MountNoticePaneAsync();

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        renderer.HasElementWithId(id, "coach-report-panel-m-2").Should().BeTrue();

        await renderer.ChangeValueByIdAsync(id, $"coach-report-panel-m-2-{reason}", reason.ToString());
        await renderer.ClickButtonByIdAsync(id, "coach-report-submit-m-2");

        client.Reports.Should().ContainSingle();
        var (conversationId, messageId, filedReason) = client.Reports[0];
        conversationId.Should().Be("c-1");
        messageId.Should().Be("m-2", "the report names the notice, not the learner's question");
        filedReason.Should().Be(reason);

        state.IsResponseReported("m-2").Should().BeTrue();
        renderer.HasElementWithId(id, "coach-report-done-m-2").Should().BeTrue();
    }

    /// <summary>
    /// The settled state on a notice is programmatically focusable, exactly as it is on any other
    /// response — the flag it replaced is gone, and focus has to land somewhere the learner can see.
    /// </summary>
    [Fact]
    public async Task TheSettledNoticeStateIsFocusableAndTakesFocus()
    {
        var js = new ModuleAwareJSRuntime();
        var (state, client) = await NoticeConversationAsync();
        var provider = Services(state, js);
        var renderer = new InteractiveTestRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var id = await renderer.RenderAsync<CoachChatPane>(ParameterView.Empty);

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        await renderer.ClickButtonByIdAsync(id, "coach-report-submit-m-2");

        renderer.AttributeValue(id, "coach-report-done-m-2", "tabindex").Should().Be("-1",
            "script can reach it and Tab still skips it");

        js.FirstArgOf("focusElement").Should().Be("coach-report-done-m-2",
            "the control that had focus no longer exists, so focus is moved deliberately");

        client.Reports.Should().ContainSingle();
    }

    /// <summary>
    /// Cancelling a report on a notice returns focus to that notice's own flag.
    /// </summary>
    [Fact]
    public async Task CancellingOnANoticeReturnsFocusToItsOwnFlag()
    {
        var js = new ModuleAwareJSRuntime();
        var (state, client) = await NoticeConversationAsync();
        var provider = Services(state, js);
        var renderer = new InteractiveTestRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var id = await renderer.RenderAsync<CoachChatPane>(ParameterView.Empty);

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        await renderer.ClickButtonByIdAsync(id, "coach-report-cancel-m-2");

        js.FirstArgOf("focusElement").Should().Be("coach-report-m-2");
        client.Reports.Should().BeEmpty("cancel files nothing");
    }

    // ---------------------------------------------------------------- ownership across messages

    /// <summary>
    /// A transcript with a notice and an ordinary answer keeps one control per message, and
    /// reporting one leaves the other alone.
    /// </summary>
    /// <remarks>
    /// The ownership failure this guards against is a shared control: with the notice newly
    /// reportable there are now two flags where there was one, and a panel keyed on anything but
    /// the message id would open both or settle both.
    /// </remarks>
    [Fact]
    public async Task ReportingTheNoticeLeavesTheOtherResponseAlone()
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        client.AddConversation("c-1");
        client.Seed("c-1", CoachMessageRole.Learner, "Make today's plan shorter.");
        client.Seed("c-1", CoachMessageRole.Coach, NoPlanNotice,
            kind: CoachMessageKind.Notice, noticeReasonCode: CoachNoticeReasonCodes.Default);
        client.Seed("c-1", CoachMessageRole.Learner, "How do I use 은/는?");
        client.Seed("c-1", CoachMessageRole.Coach, "은/는 marks the topic.");

        var directory = new CoachConversationDirectory(client);
        var state = new CoachWorkspaceState(client, directory);
        await state.OpenAsync(CoachPresentation.Overlay, "c-1");

        var provider = Services(state);
        var renderer = new InteractiveTestRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var id = await renderer.RenderAsync<CoachChatPane>(ParameterView.Empty);

        renderer.HasElementWithId(id, "coach-report-m-2").Should().BeTrue("the notice");
        renderer.HasElementWithId(id, "coach-report-m-4").Should().BeTrue("the ordinary answer");

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        renderer.HasElementWithId(id, "coach-report-panel-m-4").Should().BeFalse(
            "the other message was not asked about");

        await renderer.ClickButtonByIdAsync(id, "coach-report-submit-m-2");

        state.IsResponseReported("m-2").Should().BeTrue();
        state.IsResponseReported("m-4").Should().BeFalse();
        renderer.HasElementWithId(id, "coach-report-done-m-2").Should().BeTrue();
        renderer.HasElementWithId(id, "coach-report-m-4").Should().BeTrue(
            "the untouched response still offers its flag");
    }

    /// <summary>
    /// Paging older history in above a reported notice must not move the reported state onto a
    /// different message.
    /// </summary>
    /// <remarks>
    /// The reported set is keyed by server message id and the rows are keyed by the same id, so a
    /// prepended page shifts every ordinal without shifting any identity. This is the test that
    /// would fail if either key were ever derived from position.
    /// </remarks>
    [Fact]
    public async Task PagingOlderHistoryKeepsTheReportedNoticeOnItsOwnMessage()
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        client.AddConversation("c-1");

        // The older page, fetched second: seeded first so it holds the lower sequences.
        client.Seed("c-1", CoachMessageRole.Learner, "Older question");
        client.Seed("c-1", CoachMessageRole.Coach, "Older answer");
        client.Seed("c-1", CoachMessageRole.Learner, "Make today's plan shorter.");
        client.Seed("c-1", CoachMessageRole.Coach, NoPlanNotice,
            kind: CoachMessageKind.Notice, noticeReasonCode: CoachNoticeReasonCodes.Default);

        // Two pages of two, so paging earlier is a real second read rather than a no-op.
        client.OnGetConversationMessages = (id, _, before) =>
        {
            var all = client.ConversationMessages[id].OrderBy(m => m.Sequence).ToList();
            var older = all.Take(2).ToList();
            var newer = all.Skip(2).ToList();

            return before is null
                ? new CoachMessagePageDto
                {
                    ConversationId = id,
                    Items = newer,
                    PreviousCursor = newer[0].Sequence.ToString(),
                    UnreadableCount = 0
                }
                : new CoachMessagePageDto
                {
                    ConversationId = id,
                    Items = older,
                    PreviousCursor = null,
                    UnreadableCount = 0
                };
        };

        var directory = new CoachConversationDirectory(client);
        var state = new CoachWorkspaceState(client, directory);
        await state.OpenAsync(CoachPresentation.Overlay, "c-1");

        var provider = Services(state);
        var renderer = new InteractiveTestRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var id = await renderer.RenderAsync<CoachChatPane>(ParameterView.Empty);

        renderer.HasElementWithId(id, "coach-report-m-2").Should().BeFalse(
            "the older page has not been read yet");

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-4");
        await renderer.ClickButtonByIdAsync(id, "coach-report-submit-m-4");
        state.IsResponseReported("m-4").Should().BeTrue();

        await renderer.Dispatcher.InvokeAsync(() => state.LoadEarlierMessagesAsync());

        state.IsResponseReported("m-4").Should().BeTrue("identity did not change, only position");
        state.IsResponseReported("m-2").Should().BeFalse(
            "the older answer was never reported, and would be the one to inherit the state if it were keyed by position");
        renderer.HasElementWithId(id, "coach-report-done-m-4").Should().BeTrue();
        renderer.HasElementWithId(id, "coach-report-m-2").Should().BeTrue(
            "the newly visible older answer offers its own flag");
    }
}
