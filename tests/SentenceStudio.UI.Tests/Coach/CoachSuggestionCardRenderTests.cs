using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services.Api;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// Renders <see cref="CoachSuggestionCard"/> to real HTML and counts the actions it emits.
/// </summary>
/// <remarks>
/// <para>
/// The companion <c>CoachSuggestionActionsTests</c> pin the resolver that decides the action
/// pair. These pin the markup, because the E2E defect lived there: the resolver could be
/// perfectly correct while the Razor still emitted a second, duplicate pair below the
/// clarification question.
/// </para>
/// <para>
/// Uses ASP.NET Core's own <see cref="HtmlRenderer"/> rather than a test framework, so no
/// package is added — only a FrameworkReference the unit-test project already uses.
/// </para>
/// </remarks>
public class CoachSuggestionCardRenderTests
{
    private static readonly Regex ButtonTag = new("<button\\b", RegexOptions.Compiled);

    private static async Task<string> RenderAsync(
        CoachSessionStatus sessionStatus,
        Func<FakeCoachApiClient, Task<CoachWorkspaceState>>? arrange = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BlazorLocalizationService>();
        // The coach's name comes from the learner's study language, so every component that
        // names it needs the resolver. The all-optional constructor makes this a one-liner:
        // with no language source it answers with the default persona.
        services.AddScoped<CoachPersona>();
        services.AddScoped<Microsoft.JSInterop.IJSRuntime>(_ => new StubJSRuntime());

        var client = new FakeCoachApiClient();
        client.OnGetSession = id => FakeCoachApiClient.Session(
            id, sessionStatus, CoachStateMachineTests.Suggestion());

        var state = arrange is null
            ? await OpenAsync(client)
            : await arrange(client);

        services.AddScoped(_ => state);

        await using var provider = services.BuildServiceProvider();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

        await using var renderer = new HtmlRenderer(provider, loggerFactory);

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<CoachSuggestionCard>(
                ParameterView.Empty);
            // Decoded so assertions can use the literal copy; apostrophes render as &#x27;.
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }

    private static async Task<CoachWorkspaceState> OpenAsync(FakeCoachApiClient client)
    {
        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay, "session-1");
        return state;
    }

    // ---------------------------------------------------------------- counts

    [Fact]
    public async Task ANormalPendingSuggestionRendersExactlyTwoButtons()
    {
        var html = await RenderAsync(CoachSessionStatus.SuggestionPending);

        ButtonTag.Matches(html).Count.Should().Be(2,
            "a pending suggestion offers exactly one accept and one decline");
    }

    [Fact]
    public async Task AClarificationStillRendersExactlyTwoButtons()
    {
        // The defect: four buttons for one binary decision.
        var html = await RenderAsync(CoachSessionStatus.SuggestionPending, async client =>
        {
            var state = await OpenAsync(client);

            client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
                status: CoachTurnStatus.Incomplete,
                stopReason: CoachStopReason.ClarificationRequested,
                sessionStatus: CoachSessionStatus.SuggestionPending,
                suggestion: CoachStateMachineTests.Suggestion(),
                clarifyingQuestion: "Should I add the speaking activity to Today's Plan now?",
                clarificationsRemaining: 1,
                messages:
                [
                    new CoachMessageDto
                    {
                        MessageId = "m-clarify",
                        Role = CoachMessageRole.Coach,
                        Kind = CoachMessageKind.Clarification,
                        Text = "Should I add the speaking activity to Today's Plan now?",
                        CreatedAtUtc = DateTime.UtcNow
                    }
                ]);

            state.Draft = "Maybe.";
            await state.SendDraftAsync();
            state.State.Should().Be(CoachUiState.Clarification);
            return state;
        });

        ButtonTag.Matches(html).Count.Should().Be(2,
            "a clarification re-frames the choice; it must not add a second action pair");
    }

    [Fact]
    public async Task TheClarificationRendersTheQuestionAndKeepsTheRationale()
    {
        var question = "Should I add the speaking activity to Today's Plan now?";

        var html = await RenderAsync(CoachSessionStatus.SuggestionPending, async client =>
        {
            var state = await OpenAsync(client);

            client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
                status: CoachTurnStatus.Incomplete,
                stopReason: CoachStopReason.ClarificationRequested,
                sessionStatus: CoachSessionStatus.SuggestionPending,
                suggestion: CoachStateMachineTests.Suggestion(),
                clarifyingQuestion: question,
                clarificationsRemaining: 1,
                messages:
                [
                    new CoachMessageDto
                    {
                        MessageId = "m-clarify",
                        Role = CoachMessageRole.Coach,
                        Kind = CoachMessageKind.Clarification,
                        Text = question,
                        CreatedAtUtc = DateTime.UtcNow
                    }
                ]);

            state.Draft = "Maybe.";
            await state.SendDraftAsync();
            return state;
        });

        html.Should().Contain(question, "the focused question must be shown");
        html.Should().Contain("Your last 14 days were mostly input", "the rationale is preserved");
        html.Should().Contain("coach-suggestion-rationale");
    }

    [Fact]
    public async Task NoPendingSuggestionRendersNoActions()
    {
        var html = await RenderAsync(CoachSessionStatus.Active, async client =>
        {
            client.OnGetSession = id => FakeCoachApiClient.Session(id);
            return await OpenAsync(client);
        });

        ButtonTag.Matches(html).Count.Should().Be(0);
    }

    [Fact]
    public async Task TheCardIsASingleNamedGroup()
    {
        var html = await RenderAsync(CoachSessionStatus.SuggestionPending);

        Regex.Matches(html, "role=\"group\"").Count.Should().Be(1,
            "one decision, one accessible group");
    }
}
