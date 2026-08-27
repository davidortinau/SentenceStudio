using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;
using Xunit;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Renders the transcript to real HTML and checks that the authoritative "no change applied"
/// marker appears exactly where the operation says a write did not happen.
/// </summary>
/// <remarks>
/// The state assertions elsewhere prove the flag is set; these prove the learner can see it.
/// Both halves are needed - the marker existed as data for a whole revision without ever
/// reaching the screen in session-only mode.
/// </remarks>
public sealed class CoachNoChangeMarkerRenderTests
{
    private const string MarkerText = "No change applied";
    private const string MarkerClass = "coach-change-marker";

    private static async Task<string> RenderAsync(CoachWorkspaceState state)
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

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<CoachChatPane>(ParameterView.Empty);
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }

    /// <summary>
    /// A durable refusal carries a refusal code from the closed vocabulary, and the badge renders
    /// from it.
    /// </summary>
    /// <remarks>
    /// The code has to be one the projection can actually emit. An invented code would pass against
    /// a "code is not null" predicate while proving nothing about production, which is how a
    /// mark-everything bug survived a green suite.
    /// </remarks>
    [Fact]
    public async Task DurableRefusal_RendersTheLocalizedMarker()
    {
        var html = await RenderDurableNoticeAsync(
            "I changed the vocabulary focus to reading.",
            CoachNoticeReasonCodes.ValidationFailed);

        html.Should().Contain(MarkerClass, "the badge element has to be present");
        html.Should().Contain(MarkerText,
            "the learner sees the localized string, not the raw reason code");
        html.Should().NotContain(CoachNoticeReasonCodes.ValidationFailed,
            "the reason code drives the badge but is never shown as-is");
    }

    /// <summary>
    /// An informational durable notice renders no badge, even though it carries a code.
    /// </summary>
    /// <remarks>
    /// This is the case the previous predicate got wrong. The projection stamps every notice, so
    /// the informational code is the reachable negative — a null code never occurs durably.
    /// </remarks>
    [Fact]
    public async Task DurableInformationalNotice_RendersNoMarker()
    {
        var html = await RenderDurableNoticeAsync(
            "You have two clarifications left.",
            CoachNoticeReasonCodes.Default);

        html.Should().Contain("You have two clarifications left.", "the notice itself still renders");
        html.Should().NotContain(MarkerClass, "nothing here says a change was refused");
        html.Should().NotContain(MarkerText);
    }

    /// <summary>
    /// Session-only mode reaches the learner too: the turn is stamped with the same code the ledger
    /// would carry, so the badge renders off one field in both modes.
    /// </summary>
    [Fact]
    public async Task SessionOnlyRefusal_RendersTheMarker()
    {
        var html = await RenderSessionNoticeAsync(
            "I switched your plan to listening practice.",
            CoachTurnStatus.Rejected,
            CoachStopReason.ValidationFailed);

        html.Should().Contain(MarkerClass);
        html.Should().Contain(MarkerText,
            "a session-only refusal is just as authoritative as a durable one");
    }

    [Fact]
    public async Task SessionOnlyInformationalNotice_RendersNoMarker()
    {
        var html = await RenderSessionNoticeAsync(
            "You have two clarifications left.",
            CoachTurnStatus.Completed,
            CoachStopReason.Completed);

        html.Should().Contain("You have two clarifications left.");
        html.Should().NotContain(MarkerClass, "the turn refused nothing");
        html.Should().NotContain(MarkerText);
    }

    /// <summary>
    /// The same outcome, rendered from the ledger and from a live turn, produces the same markup
    /// decision. Parity is the property that was actually broken; asserting it directly is what
    /// stops the two paths drifting again.
    /// </summary>
    [Theory]
    [InlineData(CoachTurnStatus.Rejected, CoachStopReason.ValidationFailed, CoachNoticeReasonCodes.ValidationFailed, true)]
    [InlineData(CoachTurnStatus.Rejected, CoachStopReason.InputRejected, CoachNoticeReasonCodes.InputRejected, true)]
    [InlineData(CoachTurnStatus.Failed, CoachStopReason.ToolFailure, CoachNoticeReasonCodes.ToolFailure, true)]
    [InlineData(CoachTurnStatus.Completed, CoachStopReason.ValidationFailed, CoachNoticeReasonCodes.ValidationFailed, true)]
    [InlineData(CoachTurnStatus.Completed, CoachStopReason.Completed, CoachNoticeReasonCodes.Default, false)]
    [InlineData(CoachTurnStatus.Completed, CoachStopReason.ClarificationRequested, CoachNoticeReasonCodes.Default, false)]
    [InlineData(CoachTurnStatus.Incomplete, CoachStopReason.ClarificationRequested, CoachNoticeReasonCodes.Default, false)]
    public async Task TheSameOutcomeRendersIdenticallyInBothModes(
        CoachTurnStatus status,
        CoachStopReason stopReason,
        string durableCode,
        bool expectsMarker)
    {
        const string Text = "About that plan change.";

        var durable = await RenderDurableNoticeAsync(Text, durableCode);
        var session = await RenderSessionNoticeAsync(Text, status, stopReason);

        durable.Contains(MarkerClass, StringComparison.Ordinal)
            .Should().Be(expectsMarker, "durable mode must agree with the outcome");
        session.Contains(MarkerClass, StringComparison.Ordinal)
            .Should().Be(durable.Contains(MarkerClass, StringComparison.Ordinal),
                "the two modes are two renderings of one outcome, not two policies");
    }

    private static async Task<string> RenderDurableNoticeAsync(string text, string reasonCode)
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        client.AddConversation("c-1");

        var state = new CoachWorkspaceState(client, new CoachConversationDirectory(client));
        await state.OpenConversationAsync(CoachPresentation.Overlay, "c-1");

        client.ConversationMessages["c-1"].Add(new CoachHistoryMessageDto
        {
            Message = Notice("m-notice", text),
            Sequence = 4,
            IsReadable = true,
            NoticeReasonCode = reasonCode
        });

        await state.LoadTranscriptAsync();

        return await RenderAsync(state);
    }

    private static async Task<string> RenderSessionNoticeAsync(
        string text,
        CoachTurnStatus status,
        CoachStopReason stopReason)
    {
        var client = new FakeCoachApiClient();
        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            status: status,
            stopReason: stopReason,
            messages: new[] { Notice("m-notice", text) });

        state.Draft = "change my plan";
        await state.SendDraftAsync();

        return await RenderAsync(state);
    }

    private static CoachMessageDto Notice(string messageId, string text) => new()
    {
        MessageId = messageId,
        Role = CoachMessageRole.Coach,
        Kind = CoachMessageKind.Notice,
        Text = text,
        CreatedAtUtc = DateTime.UtcNow
    };
}
