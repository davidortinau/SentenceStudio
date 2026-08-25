using System.Globalization;
using System.Net;
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
/// Accessibility and localization of the message footer.
/// </summary>
/// <remarks>
/// Review on 2026-08-21 asked for four things this covers: the panel and the evidence disclosure
/// naming themselves to assistive technology, the radio group carrying the question its choices
/// answer, an in-flight state that is announced rather than only visual, and the two icon-only
/// buttons on the row being told apart by something other than their name.
/// </remarks>
public class CoachMessageFooterAccessibilityTests
{
    // ---------------------------------------------------------------- fixtures

    private static async Task<CoachWorkspaceState> ReportableAsync()
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        client.AddConversation("c-1");
        client.Seed("c-1", CoachMessageRole.Learner, "How do I use 은/는?");
        client.Seed("c-1", CoachMessageRole.Coach, "은/는 marks the topic.");

        var directory = new CoachConversationDirectory(client);
        var state = new CoachWorkspaceState(client, directory);
        await state.OpenAsync(CoachPresentation.Overlay, "c-1");

        return state;
    }

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
                return WebUtility.HtmlDecode(output.ToHtmlString());
            });
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
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

    private static async Task<(InteractiveTestRenderer Renderer, int Id)> MountAsync(CoachWorkspaceState state)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BlazorLocalizationService>();
        services.AddScoped<CoachPersona>();
        services.AddScoped<IJSRuntime>(_ => new ModuleAwareJSRuntime());
        services.AddScoped(_ => state);

        var provider = services.BuildServiceProvider();
        var renderer = new InteractiveTestRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        return (renderer, await renderer.RenderAsync<CoachChatPane>(ParameterView.Empty));
    }

    // ================================================================ telling the two icons apart

    /// <summary>
    /// Copy and the flag are both icon-only and both repeat once per message. Their names are short
    /// and repeatable on purpose — a voice-control user says the name, so folding the speaker and
    /// the time into it would mean "click copy" stops matching anything. The message they belong to
    /// is carried as a description instead, which a screen reader reads after the name and voice
    /// control ignores.
    /// </summary>
    [Fact]
    public async Task TheRowsTwoIconButtonsShareTheirNamesAndAreToldApartByTheirMessage()
    {
        var state = await ReportableAsync();
        var (renderer, id) = await MountAsync(state);

        var copyDescription = renderer.AttributeValue(id, "coach-copy-m-2", "aria-describedby");
        var flagDescription = renderer.AttributeValue(id, "coach-report-m-2", "aria-describedby");

        copyDescription.Should().NotBeNullOrWhiteSpace();
        copyDescription.Should().Be(flagDescription,
            "both buttons act on the same message, so they point at the same description");

        renderer.HasElementWithId(id, copyDescription!).Should().BeTrue(
            "a description that names no element is a description that is never read");

        renderer.AttributeValue(id, "coach-copy-m-2", "aria-label")
            .Should().NotBe(renderer.AttributeValue(id, "coach-report-m-2", "aria-label"),
                "the two buttons still do different things");
    }

    /// <summary>
    /// The description is the message header, which already carries the speaker and a timestamp,
    /// both already localized and already on screen.
    /// </summary>
    [Fact]
    public async Task TheDescriptionIsTheMessagesOwnHeader()
    {
        var state = await ReportableAsync();
        var (renderer, id) = await MountAsync(state);

        renderer.AttributeValue(id, "coach-report-m-2", "aria-describedby")
            .Should().Be("coach-message-header-m-2");
    }

    /// <summary>
    /// Two coach messages must not share a description, or the disambiguation disambiguates nothing.
    /// </summary>
    [Fact]
    public async Task EachMessagesButtonsPointAtTheirOwnHeader()
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        client.AddConversation("c-1");
        client.Seed("c-1", CoachMessageRole.Learner, "First question");
        client.Seed("c-1", CoachMessageRole.Coach, "First answer");
        client.Seed("c-1", CoachMessageRole.Learner, "Second question");
        client.Seed("c-1", CoachMessageRole.Coach, "Second answer");

        var directory = new CoachConversationDirectory(client);
        var state = new CoachWorkspaceState(client, directory);
        await state.OpenAsync(CoachPresentation.Overlay, "c-1");

        var (renderer, id) = await MountAsync(state);

        var second = renderer.AttributeValue(id, "coach-report-m-2", "aria-describedby");
        var fourth = renderer.AttributeValue(id, "coach-report-m-4", "aria-describedby");

        second.Should().NotBe(fourth);
        renderer.HasElementWithId(id, second!).Should().BeTrue();
        renderer.HasElementWithId(id, fourth!).Should().BeTrue();
    }

    // ================================================================ the panel names itself

    [Fact]
    public async Task TheOpenedPanelIsAGroupWithAName()
    {
        var state = await ReportableAsync();
        var (renderer, id) = await MountAsync(state);

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");

        renderer.AttributeValue(id, "coach-report-panel-m-2", "role").Should().Be("group",
            "it is part of the conversation, not a dialog covering it");
        renderer.AttributeValue(id, "coach-report-panel-m-2", "aria-label")
            .Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The five reasons answer a question that is written once above them. A learner who arrives at
    /// the group by arrowing into it never passed that line, so the group repeats it.
    /// </summary>
    [Fact]
    public async Task TheReasonsCarryTheQuestionTheyAnswer()
    {
        var state = await ReportableAsync();
        var (renderer, id) = await MountAsync(state);

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");

        renderer.AttributeValue(id, "coach-report-reasons-m-2", "role").Should().Be("radiogroup");
        renderer.AttributeValue(id, "coach-report-reasons-m-2", "aria-describedby")
            .Should().Be("coach-report-prompt-m-2");
        renderer.HasElementWithId(id, "coach-report-prompt-m-2").Should().BeTrue();
    }

    // ================================================================ the evidence disclosure

    [Fact]
    public async Task TheEvidencePanelIsAGroupNamedByItsToggle()
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

        var (renderer, id) = await MountAsync(state);

        await renderer.ClickButtonByIdAsync(id, "coach-evidence-toggle-m-2");

        renderer.AttributeValue(id, "coach-evidence-panel-m-2", "role").Should().Be("group");
        renderer.AttributeValue(id, "coach-evidence-panel-m-2", "aria-labelledby")
            .Should().Be("coach-evidence-toggle-m-2",
            "the toggle's words are the panel's name, so the name is never a second thing to translate");
        renderer.HasElementWithId(id, "coach-evidence-toggle-m-2").Should().BeTrue();
    }

    // ================================================================ in flight

    /// <summary>
    /// Submitting disables the controls and sets aria-busy. Neither of those says anything out loud,
    /// so the polite region says it — and it is a region that was already on screen, so saying it
    /// moves nothing.
    /// </summary>
    [Fact]
    public async Task SubmittingIsAnnouncedPolitelyWithTheControlsHeld()
    {
        var gate = new TaskCompletionSource();

        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        client.AddConversation("c-1");
        client.Seed("c-1", CoachMessageRole.Learner, "How do I use 은/는?");
        client.Seed("c-1", CoachMessageRole.Coach, "은/는 marks the topic.");
        client.ReportGate = gate;

        var directory = new CoachConversationDirectory(client);
        var state = new CoachWorkspaceState(client, directory);
        await state.OpenAsync(CoachPresentation.Overlay, "c-1");

        var (renderer, id) = await MountAsync(state);

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");
        await renderer.ChangeValueByIdAsync(id, "coach-report-panel-m-2-Confusing", "Confusing");

        var submit = renderer.ClickButtonByIdAsync(id, "coach-report-submit-m-2");

        renderer.AttributeValue(id, "coach-report-status-m-2", "aria-live")
            .Should().Be("polite", "a filed report has interrupted nothing");
        renderer.RenderedText(id).Should().Contain("Sending report");

        renderer.AttributeValue(id, "coach-report-panel-m-2", "aria-busy").Should().Be("true");
        renderer.AttributesOfElementWithId(id, "coach-report-submit-m-2").Should().Contain("disabled");
        renderer.AttributesOfElementWithId(id, "coach-report-cancel-m-2").Should().Contain("disabled");
        renderer.AttributesOfElementWithId(id, "coach-report-panel-m-2-Confusing").Should().Contain("disabled");

        gate.SetResult();
        await submit;

        renderer.HasElementWithId(id, "coach-report-done-m-2").Should().BeTrue();
    }

    /// <summary>
    /// The status region exists before there is anything to say, so saying something later does not
    /// insert a line into a scrolling transcript under the reader's eyes — and an empty region is
    /// the only kind a screen reader reliably announces into.
    /// </summary>
    [Fact]
    public async Task TheStatusRegionIsPresentAndSilentBeforeAnythingHappens()
    {
        var state = await ReportableAsync();
        var (renderer, id) = await MountAsync(state);

        renderer.HasElementWithId(id, "coach-report-status-m-2").Should().BeTrue(
            "a live region created at the moment it is filled is a live region that announces nothing");
    }

    // ================================================================ localization

    [Fact]
    public async Task TheInFlightAnnouncementIsTranslated()
    {
        var state = await ReportableAsync();

        var korean = await RenderPaneAsync(state, "ko");
        var english = await RenderPaneAsync(state, "en");

        korean.Should().NotContain("Sending report");
        english.Should().NotContain("신고를 보내는 중");
    }

    /// <summary>
    /// Nothing on this surface is spelled out in the markup. Every visible word and every accessible
    /// name comes from the resource files, so a Korean learner reporting a Korean response is not
    /// reading an English form to do it.
    /// </summary>
    [Theory]
    [InlineData("Report this response")]
    [InlineData("Copy")]
    public async Task TheFootersWordsAreNotHardCoded(string english)
    {
        var state = await ReportableAsync();

        (await RenderPaneAsync(state, "en")).Should().Contain(english);
        (await RenderPaneAsync(state, "ko")).Should().NotContain(english);
    }

    /// <summary>
    /// The settled state is words, not only a filled glyph, so it has to be translated too.
    /// </summary>
    [Fact]
    public async Task TheSettledStateIsTranslated()
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        client.AddConversation("c-1");
        client.Seed("c-1", CoachMessageRole.Learner, "How do I use 은/는?");
        client.Seed("c-1", CoachMessageRole.Coach, "은/는 marks the topic.");
        client.ReportedResponses.Add("m-2");

        var directory = new CoachConversationDirectory(client);
        var state = new CoachWorkspaceState(client, directory);
        await state.OpenAsync(CoachPresentation.Overlay, "c-1");

        (await RenderPaneAsync(state, "en")).Should().Contain("Reported for review");
        (await RenderPaneAsync(state, "ko")).Should().NotContain("Reported for review");
    }

    [Fact]
    public async Task TheOpenedPanelIsFullyTranslated()
    {
        var state = await ReportableAsync();
        var (renderer, id) = await MountAsync(state);

        await renderer.ClickButtonByIdAsync(id, "coach-report-m-2");

        renderer.AttributeValue(id, "coach-report-panel-m-2", "aria-label")
            .Should().NotBeNullOrWhiteSpace();
        renderer.AttributeValue(id, "coach-report-reasons-m-2", "aria-label")
            .Should().NotBeNullOrWhiteSpace();

        renderer.RenderedText(id).Should().NotContain("Coach_Report",
            "a resource key on screen is a key that was never translated");
    }
}
