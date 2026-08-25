using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// The learner-facing evidence disclosure under one of Sam's messages.
/// </summary>
/// <remarks>
/// <para>
/// Written against the production component, and <b>pressed</b> rather than only inspected. The
/// defect these replace was invisible to every existing test precisely because the markup was
/// correct: the control rendered, its handler ran, and the handler's only effect —
/// <c>OpenCanvas</c> — was a no-op whenever the canvas was already open, which on the split
/// composition it always is. A test that asserted "the button is there" passed the whole time.
/// </para>
/// <para>
/// So these assert the two things that were actually wrong: the control must belong to the
/// message whose turn cited the evidence, and pressing it must reveal that evidence in place.
/// </para>
/// </remarks>
public class CoachEvidenceDisclosureTests
{
    private static (InteractiveTestRenderer Renderer, IServiceProvider Provider) Interactive()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BlazorLocalizationService>();
        services.AddScoped<CoachPersona>();
        services.AddScoped<Microsoft.JSInterop.IJSRuntime>(_ => new StubJSRuntime());

        var provider = services.BuildServiceProvider();
        return (new InteractiveTestRenderer(provider, provider.GetRequiredService<ILoggerFactory>()), provider);
    }

    private static async Task<(InteractiveTestRenderer Renderer, int Id)> MountAsync(
        IReadOnlyList<CoachEvidenceDto> items,
        string messageId = "m-2")
    {
        var (renderer, _) = Interactive();

        var id = await renderer.RenderAsync<CoachMessageEvidence>(ParameterView.FromDictionary(
            new Dictionary<string, object?>
            {
                [nameof(CoachMessageEvidence.Items)] = items,
                [nameof(CoachMessageEvidence.MessageId)] = messageId
            }));

        return (renderer, id);
    }

    private static async Task<string> RenderPaneAsync(CoachWorkspaceState state, string culture = "en")
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(culture);

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<BlazorLocalizationService>();
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
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = previous;
        }
    }

    private static CoachEvidenceDto PracticeBalance() => new()
    {
        Kind = CoachEvidenceKind.PracticeBalance,
        Label = "Practice balance",
        Summary = "Mostly reading this week.",
        WindowStartDate = new DateOnly(2026, 8, 14),
        WindowEndDate = new DateOnly(2026, 8, 20),
        Values =
        [
            new CoachEvidenceValueDto { Label = "Input minutes", Value = 42, Unit = CoachEvidenceUnit.Minutes },
            new CoachEvidenceValueDto { Label = "Output minutes", Value = 6, Unit = CoachEvidenceUnit.Minutes }
        ]
    };

    private static async Task<CoachWorkspaceState> AfterTurnAsync(
        IReadOnlyList<CoachEvidenceDto>? evidence = null)
    {
        var client = new FakeCoachApiClient();
        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            messages:
            [
                new CoachMessageDto
                {
                    MessageId = "m-2",
                    Role = CoachMessageRole.Coach,
                    Kind = CoachMessageKind.Text,
                    Text = "You have been reading more than speaking.",
                    CreatedAtUtc = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc)
                }
            ],
            evidence: evidence);

        state.Draft = "How am I doing?";
        await state.SendDraftAsync();

        return state;
    }

    // ---------------------------------------------------------------- the control exists only when it can work

    [Fact]
    public async Task AMessageWhoseTurnCitedEvidenceOffersTheDisclosure()
    {
        var state = await AfterTurnAsync([PracticeBalance()]);

        var html = await RenderPaneAsync(state);

        html.Should().Contain("coach-evidence-toggle");
        html.Should().Contain("View evidence");
        html.Should().Contain("aria-expanded=\"false\"");
        html.Should().Contain("aria-controls=\"coach-evidence-panel-m-2\"");
        html.Should().Contain("id=\"coach-evidence-toggle-m-2\"");
    }

    [Fact]
    public async Task AMessageWhoseTurnCitedNothingOffersNoDeadControl()
    {
        var state = await AfterTurnAsync(evidence: null);

        var html = await RenderPaneAsync(state);

        html.Should().NotContain("coach-evidence-toggle",
            "a control that expands to nothing teaches the learner that the app's affordances cannot be trusted");
    }

    /// <summary>
    /// The defect this replaced: evidence lived in one workspace-wide list, so every one of Sam's
    /// messages advertised the newest turn's evidence — including the ones that cited nothing.
    /// </summary>
    [Fact]
    public async Task EvidenceBelongsToTheMessageThatCitedItAndNotToEveryOtherOne()
    {
        var client = new FakeCoachApiClient();
        var state = new CoachWorkspaceState(client);
        await state.OpenAsync(CoachPresentation.Overlay);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            messages:
            [
                new CoachMessageDto
                {
                    MessageId = "m-2",
                    Role = CoachMessageRole.Coach,
                    Kind = CoachMessageKind.Text,
                    Text = "You have been reading more than speaking.",
                    CreatedAtUtc = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc)
                }
            ],
            evidence: [PracticeBalance()]);

        state.Draft = "How am I doing?";
        await state.SendDraftAsync();

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn(
            messages:
            [
                new CoachMessageDto
                {
                    MessageId = "m-4",
                    Role = CoachMessageRole.Coach,
                    Kind = CoachMessageKind.Text,
                    Text = "은/는 marks the topic.",
                    CreatedAtUtc = new DateTime(2026, 8, 20, 12, 5, 0, DateTimeKind.Utc)
                }
            ]);

        state.Draft = "What is 은/는?";
        await state.SendDraftAsync();

        var html = await RenderPaneAsync(state);

        html.Should().Contain("coach-evidence-toggle-m-2",
            "the message that cited the evidence keeps its disclosure");
        html.Should().NotContain("coach-evidence-toggle-m-4",
            "the later message cited nothing and must not advertise somebody else's evidence");
    }

    [Fact]
    public async Task TheDisclosureIsNotOfferedOnTheLearnersOwnMessage()
    {
        var state = await AfterTurnAsync([PracticeBalance()]);

        var html = await RenderPaneAsync(state);

        html.Should().NotContain("coach-evidence-toggle-m-1");
    }

    [Fact]
    public async Task TheCollapsedDisclosureDoesNotLeakItsContents()
    {
        var state = await AfterTurnAsync([PracticeBalance()]);

        var html = await RenderPaneAsync(state);

        html.Should().NotContain("coach-evidence-inline",
            "the panel is not rendered until it is opened");
        html.Should().NotContain("Mostly reading this week.");
    }

    // ---------------------------------------------------------------- pressing it

    [Fact]
    public async Task PressingTheDisclosureRevealsTheEvidenceInPlace()
    {
        var (renderer, id) = await MountAsync([PracticeBalance()]);

        renderer.RenderedText(id).Should().NotContain("Mostly reading this week.");

        await renderer.ClickButtonByIdAsync(id, "coach-evidence-toggle-m-2");

        renderer.HasElementWithId(id, "coach-evidence-panel-m-2").Should().BeTrue(
            "the panel is revealed in place, not navigated to");

        var text = renderer.RenderedText(id);
        text.Should().Contain("Practice balance");
        text.Should().Contain("Mostly reading this week.");
        text.Should().Contain("Input minutes");
        text.Should().Contain("Hide evidence", "the control says what pressing it again will do");

        renderer.AttributeValue(id, "coach-evidence-toggle-m-2", "aria-expanded")
            .Should().Be("true");
        renderer.Unhandled.Should().BeEmpty();
    }

    [Fact]
    public async Task PressingItAgainCollapsesIt()
    {
        var (renderer, id) = await MountAsync([PracticeBalance()]);

        await renderer.ClickButtonByIdAsync(id, "coach-evidence-toggle-m-2");
        await renderer.ClickButtonByIdAsync(id, "coach-evidence-toggle-m-2");

        renderer.HasElementWithId(id, "coach-evidence-panel-m-2").Should().BeFalse();
        renderer.RenderedText(id).Should().NotContain("Mostly reading this week.");
        renderer.RenderedText(id).Should().Contain("View evidence");
        renderer.AttributeValue(id, "coach-evidence-toggle-m-2", "aria-expanded")
            .Should().Be("false");
    }

    [Fact]
    public async Task TheDisclosureCarriesItsPanelAssociationBothWaysAndNeverNavigates()
    {
        var (renderer, id) = await MountAsync([PracticeBalance()]);

        renderer.AttributeValue(id, "coach-evidence-toggle-m-2", "aria-controls")
            .Should().Be("coach-evidence-panel-m-2");

        await renderer.ClickButtonByIdAsync(id, "coach-evidence-toggle-m-2");

        renderer.AttributesOfElementWithId(id, "coach-evidence-toggle-m-2")
            .Should().NotContain("href",
                "this is a disclosure, not a link: the learner keeps the sentence they were reading on screen");
        renderer.Unhandled.Should().BeEmpty();
    }

    [Fact]
    public async Task AMessageWithNoEvidenceRendersNoControlToPress()
    {
        var (renderer, id) = await MountAsync([]);

        renderer.HasElementWithId(id, "coach-evidence-toggle-m-2").Should().BeFalse();
    }

    [Fact]
    public async Task TwoMessagesExpandIndependently()
    {
        var (first, firstId) = await MountAsync([PracticeBalance()], "m-2");
        var (second, secondId) = await MountAsync([PracticeBalance()], "m-9");

        await first.ClickButtonByIdAsync(firstId, "coach-evidence-toggle-m-2");

        first.HasElementWithId(firstId, "coach-evidence-panel-m-2").Should().BeTrue();
        second.HasElementWithId(secondId, "coach-evidence-panel-m-9").Should().BeFalse(
            "one message's disclosure is not the other's");
    }

    /// <summary>
    /// A disclosure handed a different message collapses rather than showing the old evidence
    /// under the new one's heading.
    /// </summary>
    /// <remarks>
    /// The same positional-rebind failure the report control guards against, in the read-only
    /// half. Cheaper here — nothing is written — but it would still tell the learner that one
    /// answer was based on facts that belonged to another.
    /// </remarks>
    [Fact]
    public async Task ARebindToAnotherMessageCollapsesTheDisclosure()
    {
        var (renderer, id) = await MountAsync([PracticeBalance()], "m-2");

        await renderer.ClickButtonByIdAsync(id, "coach-evidence-toggle-m-2");
        renderer.HasElementWithId(id, "coach-evidence-panel-m-2").Should().BeTrue();

        await renderer.SetRootParametersAsync(id, ParameterView.FromDictionary(
            new Dictionary<string, object?>
            {
                [nameof(CoachMessageEvidence.Items)] = new List<CoachEvidenceDto> { PracticeBalance() },
                [nameof(CoachMessageEvidence.MessageId)] = "m-9"
            }));

        renderer.HasElementWithId(id, "coach-evidence-panel-m-9").Should().BeFalse();
        renderer.AttributeValue(id, "coach-evidence-toggle-m-9", "aria-expanded").Should().Be("false");
    }

    // ---------------------------------------------------------------- reload

    /// <summary>
    /// Durable history carries no per-turn evidence, so a message read back after a reload has
    /// none to disclose. Withholding the control there is the truthful outcome, not a regression:
    /// the plan canvas still shows the evidence behind the current plan, which is a claim the
    /// server does make.
    /// </summary>
    [Fact]
    public async Task AReloadedTranscriptOffersNoDisclosureItCannotFill()
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        client.AddConversation("c-1");
        client.Seed("c-1", CoachMessageRole.Learner, "How am I doing?");
        client.Seed("c-1", CoachMessageRole.Coach, "You have been reading more than speaking.");

        var directory = new CoachConversationDirectory(client);
        var state = new CoachWorkspaceState(client, directory);
        await state.OpenAsync(CoachPresentation.Overlay, "c-1");

        var html = await RenderPaneAsync(state);

        html.Should().NotContain("coach-evidence-toggle");
    }

    // ---------------------------------------------------------------- localization

    [Fact]
    public async Task TheDisclosureIsLocalized()
    {
        var state = await AfterTurnAsync([PracticeBalance()]);

        var html = await RenderPaneAsync(state, culture: "ko");

        html.Should().Contain("근거 보기");
        html.Should().NotContain("View evidence");
    }
}
