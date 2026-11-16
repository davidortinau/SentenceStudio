using System.Net;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SentenceStudio.Abstractions;
using SentenceStudio.Services.Progress;
using SentenceStudio.UI.Tests.Activities;
using SentenceStudio.UI.Tests.Coach;
using SentenceStudio.WebApp.Admin;
using SentenceStudio.WebApp.Components.Pages.Admin;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Admin;

/// <summary>
/// The feedback inbox and the agent tokens page in a live circuit, against a scripted API: what an
/// admin reads, that learner content appears only after an explicit click with a confirmed
/// password and goes when its panel closes, and that every refusal is said in words where it
/// happened.
/// </summary>
public sealed class AdminFeedbackComponentTests
{
    private const string SamRoute = "/api/admin/reports/sam";
    private const string GitHubRoute = "/api/admin/reports/github";
    private const string DetailRoute = "/api/admin/reports/sam/r1";
    private const string ExchangeRoute = "/api/admin/reports/sam/r1/exchange";
    private const string TriageRoute = "/api/admin/reports/sam/r1/triage";
    private const string ScreenRoute = "/api/admin/reports/github/screen-states/fs-0123456789ab";
    private const string StepUpRoute = "/api/admin/step-up";

    private const string Row = """
        {"id":"r1","learnerId":"p1","status":"new","needsAttention":true,"reason":"incorrect_or_misleading","flagCount":2,
         "firstFlaggedAtUtc":"2026-11-14T09:00:00Z","lastFlaggedAtUtc":"2026-11-15T10:30:00Z",
         "failureCode":"tool_failed","stopReason":"completed","issueNumber":null,"issueUrl":null,
         "hasExchange":true,"exchangeOpenedCount":0,"exchangeLastOpenedAtUtc":null,"reviewedAtUtc":null}
        """;

    private static readonly string SamBody = $$"""
        {"items":[{{Row}}],"learners":[{"profileId":"p1","displayName":"Alpha","email":"alpha@x.test"}],
         "attentionCount":4,"unlistedFlagCount":0,"truncated":false}
        """;

    private static readonly string DetailBody = $$"""
        {"report":{{Row}},"learner":{"profileId":"p1","displayName":"Alpha","email":"alpha@x.test"},
         "flags":[{"flaggedAtUtc":"2026-11-15T10:30:00Z","reason":"incorrect_or_misleading","responseKind":"pedagogical_answer",
                   "turnStatus":"completed","turnAttempts":2,"turnErrorCode":"tool_failed","toolsInvoked":"get_vocabulary_progress",
                   "writeStatus":"rejected","writeFailureCode":"invalid_arguments","groundingStage":"observe","groundingRefused":false,
                   "groundingAltered":true,"groundingRepairSuppressed":null,"groundingFindingCount":1,"groundingRuleCodes":"UnsupportedClaim",
                   "groundingLimitation":"not_built"}],
         "flagsTruncated":false}
        """;

    private const string GitHubBody = """
        {"items":[{"status":"submitted","issueNumber":77,"issueUrl":"https://github.com/davidortinau/SentenceStudio/issues/77",
                   "issueTitle":"The quiz skipped a word","failureCode":null,"learnerId":"p1","platform":"native","appVersion":"1.4.2",
                   "screen":"activity","createdAtUtc":"2026-11-13T08:00:00Z","screenStateReference":"fs-0123456789ab"},
                  {"status":"claimed","issueNumber":null,"issueUrl":null,"issueTitle":null,"failureCode":null,"learnerId":"p2",
                   "platform":"web","appVersion":"1.4.2","screen":"coach","createdAtUtc":"2026-11-12T08:00:00Z","screenStateReference":null}],
         "learners":[{"profileId":"p1","displayName":"Alpha","email":"alpha@x.test"}],"truncated":false}
        """;

    private const string ExchangeBody = """
        {"reportId":"r1","state":"available","truncated":false,"openedCount":1,
         "exchanges":[{"flaggedAtUtc":"2026-11-15T10:30:00Z","question":"Why is <b>은</b> used here?","answer":"It marks the topic."}]}
        """;

    private static string ProofBody =>
        $$"""{"proof":"sealed-proof","expiresAtUtc":"{{DateTimeOffset.UtcNow.AddMinutes(10):O}}"}""";

    private readonly ScriptedApi _api = new();

    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddScoped(_ => new AdminApiClient(
            new HttpClient(_api) { BaseAddress = new Uri("http://api.test") },
            NullLogger<AdminApiClient>.Instance));
        services.AddScoped<AdminSessionState>();
        services.AddScoped<NavigationManager, SentenceStudio.UI.Tests.Coach.TestNavigationManager>();
        services.AddScoped<NavigationMemoryService>();
        services.AddSingleton<IPreferencesService, SwitchablePreferences>();
        services.AddSingleton<IAppState, AppState>();
        services.AddSingleton<ProgressCacheService>();
        services.AddScoped<AuthenticationStateProvider, SignedIn>();
        services.AddScoped<IJSRuntime>(_ => new ModuleAwareJSRuntime());
        services.AddScoped<BlazorLocalizationService>();
        services.AddScoped<SentenceStudio.WebUI.Shared.Sam.SamEntryState>();
        return services.BuildServiceProvider();
    }

    private static InteractiveTestRenderer Circuit(IServiceProvider services) =>
        new(services, services.GetRequiredService<ILoggerFactory>());

    private void AnswerLists()
    {
        _api.Answer(SamRoute, HttpStatusCode.OK, SamBody);
        _api.Answer(GitHubRoute, HttpStatusCode.OK, GitHubBody);
        _api.Answer(DetailRoute, HttpStatusCode.OK, DetailBody);
    }

    private static async Task ConfirmPasswordAsync(InteractiveTestRenderer circuit, int id)
    {
        await circuit.InputValueByIdAsync(id, "admin-password", "right-password");
        await circuit.SubmitFormByIdAsync(id, "admin-password-form");
    }

    // ------------------------------------------------------------------ the lists

    [Fact]
    public async Task The_inbox_lists_new_flagged_answers_first_and_the_GitHub_reports_in_words()
    {
        AnswerLists();
        await using var services = Services();
        var circuit = Circuit(services);

        var id = await circuit.RenderAsync<AdminFeedback>();

        circuit.Unhandled.Should().BeEmpty();
        var text = circuit.RenderedText(id);
        text.Should().Contain("alpha@x.test").And.Contain("Incorrect or misleading").And.Contain("2026-11-15 10:30")
            .And.Contain("tool_failed").And.Contain("New").And.Contain("to look at in all");
        circuit.AttributeValue(id, "admin-feedback-new-count", "role").Should().Be("status");
        text.Should().Contain("77").And.Contain("The quiz skipped a word").And.Contain("Native").And.Contain("1.4.2")
            .And.Contain("Filed").And.Contain("In doubt: an issue may exist").And.Contain("None saved")
            .And.Contain("p2", "a learner the API sent no name for is shown by id");
        circuit.RenderedHrefs(id).Should().Contain("https://github.com/davidortinau/SentenceStudio/issues/77");
        circuit.HasElementWithId(id, "admin-feedback-sam-table").Should().BeTrue();
        circuit.HasElementWithId(id, "admin-feedback-github-table").Should().BeTrue();
        circuit.HasElementWithId(id, "admin-feedback-detail").Should().BeFalse("no report is open until one is chosen");

        _api.Paths.Should().Equal(SamRoute, GitHubRoute);
        _api.Calls[0].Query.Should().Be("?take=200&attention=true", "the inbox opens on what nobody has looked at yet");
        (await services.GetRequiredService<AdminSessionState>().GetReportAttentionCountAsync()).Should().Be(4,
            "the sidebar's count follows what the inbox just read, with no request of its own");
        _api.Paths.Should().HaveCount(2);
    }

    [Fact]
    public async Task Choosing_another_status_asks_for_that_status_only()
    {
        AnswerLists();
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminFeedback>();

        await circuit.ChangeValueByIdAsync(id, "admin-feedback-status", "all");
        await circuit.ChangeValueByIdAsync(id, "admin-feedback-status", "deferred");
        await circuit.ChangeValueByIdAsync(id, "admin-feedback-status", "made-up");
        await circuit.ChangeValueByIdAsync(id, "admin-feedback-status", "attention");

        circuit.Unhandled.Should().BeEmpty();
        _api.Paths.Should().Equal(SamRoute, GitHubRoute, SamRoute, SamRoute, SamRoute);
        _api.Calls[2].Query.Should().Be("?take=200");
        _api.Calls[3].Query.Should().Be("?take=200&status=deferred");
        _api.Calls[4].Query.Should().Be("?take=200&attention=true");
    }

    [Fact]
    public async Task An_empty_inbox_a_cut_list_and_flags_with_no_row_are_each_said_in_words()
    {
        _api.Answer(SamRoute, HttpStatusCode.OK,
            """{"items":[],"learners":[],"attentionCount":0,"unlistedFlagCount":2,"truncated":false}""");
        _api.Answer(GitHubRoute, HttpStatusCode.OK, """{"items":[],"learners":[],"truncated":false}""");
        await using var services = Services();
        var circuit = Circuit(services);

        var id = await circuit.RenderAsync<AdminFeedback>();

        var text = circuit.RenderedText(id);
        text.Should().Contain("Nothing to look at")
            .And.Contain("flags were").And.Contain("recorded without an inbox row")
            .And.Contain("No report has been filed to GitHub from the app");
        circuit.HasElementWithId(id, "admin-feedback-sam-table").Should().BeFalse();
    }

    [Fact]
    public async Task A_list_that_fails_says_so_and_the_other_still_shows()
    {
        AnswerLists();
        _api.Answer(GitHubRoute, HttpStatusCode.InternalServerError);
        await using var services = Services();
        var circuit = Circuit(services);

        var id = await circuit.RenderAsync<AdminFeedback>();

        circuit.RenderedText(id).Should().Contain("GitHub reports could not be loaded")
            .And.NotContain("No report has been filed");
        circuit.HasElementWithId(id, "admin-feedback-sam-table").Should().BeTrue();
        circuit.HasElementWithId(id, "admin-feedback-github-table").Should().BeFalse();
    }

    [Fact]
    public async Task A_person_the_API_refuses_sees_page_not_found_and_nothing_of_the_inbox()
    {
        _api.Answer(SamRoute, HttpStatusCode.NotFound);
        await using var services = Services();
        var circuit = Circuit(services);

        var id = await circuit.RenderAsync<AdminFeedback>();

        circuit.RenderedText(id).Should().Contain("Page not found").And.NotContain("flagged");
        circuit.HasElementWithId(id, "admin-feedback").Should().BeFalse();
        _api.Paths.Should().Equal(SamRoute);
    }

    // ------------------------------------------------------------------ one report

    [Fact]
    public async Task Opening_a_report_shows_its_flags_in_words_and_no_learner_content()
    {
        AnswerLists();
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminFeedback>();

        await circuit.ClickButtonByIdAsync(id, "admin-feedback-open-r1");

        circuit.Unhandled.Should().BeEmpty();
        var text = circuit.RenderedText(id);
        text.Should().Contain("Pedagogical answer").And.Contain("Completed").And.Contain(", 2 attempts")
            .And.Contain(", tool_failed").And.Contain("get_vocabulary_progress").And.Contain("Rejected")
            .And.Contain(", invalid_arguments")
            .And.Contain("Observe, answer repaired, 1 finding, UnsupportedClaim, not built")
            .And.Contain("2 times, 2026-11-14 09:00 to 2026-11-15 10:30").And.Contain("Never");
        text.Should().NotContain("It marks the topic");
        circuit.HasElementWithId(id, "admin-feedback-exchange").Should().BeFalse();
        _api.Paths.Should().Equal(SamRoute, GitHubRoute, DetailRoute);
    }

    [Fact]
    public async Task The_exchange_cannot_be_opened_until_the_password_is_confirmed()
    {
        AnswerLists();
        _api.Answer(ExchangeRoute, HttpStatusCode.OK, ExchangeBody);
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminFeedback>();
        await circuit.ClickButtonByIdAsync(id, "admin-feedback-open-r1");

        circuit.HasElementWithId(id, "admin-password-card").Should().BeTrue("the page asks for the password where it is needed");
        circuit.AttributesOfElementWithId(id, "admin-feedback-exchange-open").Should().Contain("disabled");
        await circuit.ClickButtonByIdAsync(id, "admin-feedback-exchange-open");

        _api.Paths.Should().NotContain(ExchangeRoute);
        circuit.RenderedText(id).Should().NotContain("It marks the topic");
    }

    [Fact]
    public async Task With_the_password_confirmed_the_exchange_opens_as_plain_text_and_Hide_drops_it()
    {
        AnswerLists();
        _api.Answer(StepUpRoute, HttpStatusCode.OK, ProofBody);
        _api.Answer(ExchangeRoute, HttpStatusCode.OK, ExchangeBody);
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminFeedback>();
        await circuit.ClickButtonByIdAsync(id, "admin-feedback-open-r1");
        await ConfirmPasswordAsync(circuit, id);

        circuit.HasElementWithId(id, "admin-password-card").Should().BeFalse("a confirmed password is not asked for again");
        await circuit.ClickButtonByIdAsync(id, "admin-feedback-exchange-open");

        circuit.Unhandled.Should().BeEmpty();
        var text = circuit.RenderedText(id);
        text.Should().Contain("The learner asked").And.Contain("Why is <b>은</b> used here?",
            "what a learner typed is drawn as text, never as markup").And.Contain("Sam answered").And.Contain("It marks the topic.");
        text.Should().Contain("1 time", "the count of openings follows the API's answer");
        _api.Last(ExchangeRoute).Proof.Should().Be("sealed-proof");

        await circuit.ClickButtonByIdAsync(id, "admin-feedback-exchange-hide");
        circuit.RenderedText(id).Should().NotContain("It marks the topic").And.NotContain("Why is");
        circuit.HasElementWithId(id, "admin-feedback-exchange-open").Should().BeTrue();
    }

    [Fact]
    public async Task Closing_the_report_drops_what_was_read_from_it()
    {
        AnswerLists();
        _api.Answer(StepUpRoute, HttpStatusCode.OK, ProofBody);
        _api.Answer(ExchangeRoute, HttpStatusCode.OK, ExchangeBody);
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminFeedback>();
        await circuit.ClickButtonByIdAsync(id, "admin-feedback-open-r1");
        await ConfirmPasswordAsync(circuit, id);
        await circuit.ClickButtonByIdAsync(id, "admin-feedback-exchange-open");

        await circuit.ClickButtonByIdAsync(id, "admin-feedback-detail-close");
        circuit.RenderedText(id).Should().NotContain("It marks the topic");

        // Opened again, the exchange has to be asked for again: it was not kept.
        await circuit.ClickButtonByIdAsync(id, "admin-feedback-open-r1");
        circuit.RenderedText(id).Should().NotContain("It marks the topic");
        circuit.HasElementWithId(id, "admin-feedback-exchange-open").Should().BeTrue();
    }

    [Fact]
    public async Task A_confirmation_the_API_no_longer_honours_asks_for_the_password_again()
    {
        AnswerLists();
        _api.Answer(StepUpRoute, HttpStatusCode.OK, ProofBody);
        _api.Answer(ExchangeRoute, HttpStatusCode.Forbidden, """{"code":"step_up_required"}""");
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminFeedback>();
        await circuit.ClickButtonByIdAsync(id, "admin-feedback-open-r1");
        await ConfirmPasswordAsync(circuit, id);

        await circuit.ClickButtonByIdAsync(id, "admin-feedback-exchange-open");

        circuit.RenderedText(id).Should().Contain("Confirm your password again to open it");
        circuit.HasElementWithId(id, "admin-password-card").Should().BeTrue();
        services.GetRequiredService<AdminSessionState>().CurrentProof.Should().BeNull();
    }

    [Theory]
    [InlineData("""{"reportId":"r1","state":"unavailable","truncated":false,"openedCount":3,"exchanges":[{"flaggedAtUtc":null,"question":null,"answer":null}]}""",
        "can no longer be read")]
    [InlineData("""{"reportId":"r1","state":"available","truncated":true,"openedCount":1,"exchanges":[{"flaggedAtUtc":null,"question":"Q","answer":null}]}""",
        "This message can no longer be read")]
    public async Task An_exchange_that_cannot_be_read_says_so_instead_of_showing_nothing(string body, string expected)
    {
        AnswerLists();
        _api.Answer(StepUpRoute, HttpStatusCode.OK, ProofBody);
        _api.Answer(ExchangeRoute, HttpStatusCode.OK, body);
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminFeedback>();
        await circuit.ClickButtonByIdAsync(id, "admin-feedback-open-r1");
        await ConfirmPasswordAsync(circuit, id);

        await circuit.ClickButtonByIdAsync(id, "admin-feedback-exchange-open");

        circuit.Unhandled.Should().BeEmpty();
        circuit.RenderedText(id).Should().Contain(expected);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, """{"code":"key_ring_not_durable"}""", "cannot read stored Sam messages reliably")]
    [InlineData(HttpStatusCode.InternalServerError, "", "could not be opened. Try again")]
    public async Task An_exchange_the_API_would_not_open_says_why(HttpStatusCode status, string body, string expected)
    {
        AnswerLists();
        _api.Answer(StepUpRoute, HttpStatusCode.OK, ProofBody);
        _api.Answer(ExchangeRoute, status, body);
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminFeedback>();
        await circuit.ClickButtonByIdAsync(id, "admin-feedback-open-r1");
        await ConfirmPasswordAsync(circuit, id);

        await circuit.ClickButtonByIdAsync(id, "admin-feedback-exchange-open");

        circuit.RenderedText(id).Should().Contain(expected);
        circuit.HasElementWithId(id, "admin-feedback-exchange").Should().BeFalse();
    }

    // ------------------------------------------------------------------ status and issue

    [Fact]
    public async Task Saving_a_status_and_an_issue_number_sends_both_and_reloads_the_list()
    {
        AnswerLists();
        _api.Answer(TriageRoute, HttpStatusCode.OK, Row
            .Replace("\"status\":\"new\"", "\"status\":\"accepted\"")
            .Replace("\"issueNumber\":null,\"issueUrl\":null",
                "\"issueNumber\":412,\"issueUrl\":\"https://github.com/davidortinau/SentenceStudio/issues/412\""));
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminFeedback>();
        await circuit.ClickButtonByIdAsync(id, "admin-feedback-open-r1");

        await circuit.ChangeValueByIdAsync(id, "admin-feedback-triage-status", "accepted");
        await circuit.InputValueByIdAsync(id, "admin-feedback-triage-issue", "#412");
        await circuit.SubmitFormByIdAsync(id, "admin-feedback-triage");

        circuit.Unhandled.Should().BeEmpty();
        _api.Last(TriageRoute).Body.Should().Be("""{"seenFlagCount":2,"status":"accepted","issueNumber":412}""",
            "the save says how many flags the admin was looking at");
        circuit.RenderedText(id).Should().Contain("Saved.");
        _api.Paths.Should().Equal(SamRoute, GitHubRoute, DetailRoute, TriageRoute, SamRoute);
    }

    [Fact]
    public async Task A_status_saved_with_the_issue_field_untouched_sends_no_issue_so_a_link_made_meanwhile_stays()
    {
        AnswerLists();
        _api.Answer(TriageRoute, HttpStatusCode.OK, Row.Replace("\"status\":\"new\"", "\"status\":\"reviewed\""));
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminFeedback>();
        await circuit.ClickButtonByIdAsync(id, "admin-feedback-open-r1");

        await circuit.ChangeValueByIdAsync(id, "admin-feedback-triage-status", "reviewed");
        await circuit.SubmitFormByIdAsync(id, "admin-feedback-triage");

        _api.Last(TriageRoute).Body.Should().Be("""{"seenFlagCount":2,"status":"reviewed"}""");
    }

    [Fact]
    public async Task Clearing_a_linked_issue_number_unlinks_it_and_leaving_it_alone_does_not_send_it()
    {
        const string linkedRow = """
            {"id":"r1","learnerId":"p1","status":"accepted","needsAttention":true,"reason":"confusing","flagCount":3,
             "firstFlaggedAtUtc":"2026-11-14T09:00:00Z","lastFlaggedAtUtc":"2026-11-15T10:30:00Z",
             "failureCode":null,"stopReason":"completed","issueNumber":412,"issueUrl":"https://github.com/davidortinau/SentenceStudio/issues/412",
             "hasExchange":true,"exchangeOpenedCount":0,"exchangeLastOpenedAtUtc":null,"reviewedAtUtc":"2026-11-14T12:00:00Z"}
            """;
        AnswerLists();
        _api.Answer(SamRoute, HttpStatusCode.OK,
            $$"""{"items":[{{linkedRow}}],"learners":[],"attentionCount":1,"unlistedFlagCount":0,"truncated":false}""");
        _api.Answer(DetailRoute, HttpStatusCode.OK, $$"""{"report":{{linkedRow}},"learner":null,"flags":[],"flagsTruncated":false}""");
        _api.Answer(TriageRoute, HttpStatusCode.OK, linkedRow);
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminFeedback>();
        circuit.RenderedText(id).Should().Contain("Flagged again", "a report flagged after it was triaged says so beside its status");
        await circuit.ClickButtonByIdAsync(id, "admin-feedback-open-r1");
        circuit.HasElementWithId(id, "admin-feedback-detail-flagged-again").Should().BeTrue();

        // Saved as it stands: "I have looked". Neither the status nor the link is sent, so
        // neither can undo what somebody else did meanwhile.
        await circuit.SubmitFormByIdAsync(id, "admin-feedback-triage");
        _api.Last(TriageRoute).Body.Should().Be("""{"seenFlagCount":3}""");

        await circuit.InputValueByIdAsync(id, "admin-feedback-triage-issue", "413");
        await circuit.SubmitFormByIdAsync(id, "admin-feedback-triage");
        _api.Last(TriageRoute).Body.Should().Be("""{"seenFlagCount":3,"issueNumber":413}""");

        await circuit.InputValueByIdAsync(id, "admin-feedback-triage-issue", "");
        await circuit.SubmitFormByIdAsync(id, "admin-feedback-triage");
        _api.Last(TriageRoute).Body.Should().Be("""{"seenFlagCount":3,"unlinkIssue":true}""");
    }

    [Fact]
    public async Task An_exchange_that_arrives_after_its_report_was_closed_is_dropped_unshown()
    {
        AnswerLists();
        _api.Answer(StepUpRoute, HttpStatusCode.OK, ProofBody);
        _api.Answer(ExchangeRoute, HttpStatusCode.OK, ExchangeBody);
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminFeedback>();
        await circuit.ClickButtonByIdAsync(id, "admin-feedback-open-r1");
        await ConfirmPasswordAsync(circuit, id);

        var release = _api.Hold(ExchangeRoute);
        var opening = circuit.ClickButtonByIdAsync(id, "admin-feedback-exchange-open");
        await _api.Reached(ExchangeRoute);
        await circuit.ClickButtonByIdAsync(id, "admin-feedback-detail-close");
        release.SetResult();
        await opening;

        circuit.Unhandled.Should().BeEmpty();
        circuit.HasElementWithId(id, "admin-feedback-detail").Should().BeFalse("the report was closed and stays closed");
        circuit.RenderedText(id).Should().NotContain("It marks the topic");
    }

    [Theory]
    [InlineData("twelve")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("4.5")]
    [InlineData("99999999")]
    public async Task An_issue_number_that_is_not_one_is_not_sent(string typed)
    {
        AnswerLists();
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminFeedback>();
        await circuit.ClickButtonByIdAsync(id, "admin-feedback-open-r1");

        await circuit.InputValueByIdAsync(id, "admin-feedback-triage-issue", typed);
        await circuit.SubmitFormByIdAsync(id, "admin-feedback-triage");

        circuit.RenderedText(id).Should().Contain("must be a whole number");
        _api.Paths.Should().NotContain(TriageRoute);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, """{"code":"transition_refused"}""", "An accepted report keeps its status")]
    [InlineData(HttpStatusCode.Conflict, """{"code":"conflict"}""", "The report changed since you opened it")]
    [InlineData(HttpStatusCode.InternalServerError, "", "could not be saved")]
    public async Task A_change_the_API_refuses_says_why_and_is_not_shown_as_saved(
        HttpStatusCode status, string body, string expected)
    {
        AnswerLists();
        _api.Answer(TriageRoute, status, body);
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminFeedback>();
        await circuit.ClickButtonByIdAsync(id, "admin-feedback-open-r1");

        await circuit.ChangeValueByIdAsync(id, "admin-feedback-triage-status", "dismissed");
        await circuit.SubmitFormByIdAsync(id, "admin-feedback-triage");

        circuit.Unhandled.Should().BeEmpty();
        circuit.RenderedText(id).Should().Contain(expected).And.NotContain("Saved.");

        // A refusal that means "what you were looking at is out of date" reads the report again,
        // so the form shows what is true now.
        var reloaded = _api.Calls.Count(c => c.Path == DetailRoute) == 2;
        reloaded.Should().Be(status == HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_refused_save_whose_report_is_closed_while_it_is_read_again_is_said_at_the_top_of_the_page()
    {
        AnswerLists();
        _api.Answer(TriageRoute, HttpStatusCode.Conflict, """{"code":"conflict"}""");
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminFeedback>();
        await circuit.ClickButtonByIdAsync(id, "admin-feedback-open-r1");
        await circuit.ChangeValueByIdAsync(id, "admin-feedback-triage-status", "dismissed");

        // The save is refused at once; the close lands while the report is being read again.
        var release = _api.Hold(DetailRoute);
        var saving = circuit.SubmitFormByIdAsync(id, "admin-feedback-triage");
        await _api.Reached(DetailRoute);
        await circuit.ClickButtonByIdAsync(id, "admin-feedback-detail-close");
        release.SetResult();
        await saving;

        circuit.Unhandled.Should().BeEmpty();
        circuit.HasElementWithId(id, "admin-feedback-detail").Should().BeFalse();
        circuit.RenderedText(id).Should().Contain("was not saved");
    }

    [Fact]
    public async Task A_refused_save_whose_report_cannot_be_read_again_does_not_claim_it_was_reloaded()
    {
        AnswerLists();
        _api.Answer(TriageRoute, HttpStatusCode.Conflict, """{"code":"conflict"}""");
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminFeedback>();
        await circuit.ClickButtonByIdAsync(id, "admin-feedback-open-r1");
        await circuit.ChangeValueByIdAsync(id, "admin-feedback-triage-status", "dismissed");

        _api.Answer(DetailRoute, HttpStatusCode.InternalServerError);
        await circuit.SubmitFormByIdAsync(id, "admin-feedback-triage");

        circuit.Unhandled.Should().BeEmpty();
        circuit.RenderedText(id).Should().Contain("could not be read again").And.NotContain("It has been reloaded");
    }

    [Fact]
    public async Task A_save_that_fails_after_its_report_was_closed_is_said_at_the_top_of_the_page()
    {
        AnswerLists();
        _api.Answer(TriageRoute, HttpStatusCode.InternalServerError);
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminFeedback>();
        await circuit.ClickButtonByIdAsync(id, "admin-feedback-open-r1");
        await circuit.ChangeValueByIdAsync(id, "admin-feedback-triage-status", "dismissed");

        var release = _api.Hold(TriageRoute);
        var saving = circuit.SubmitFormByIdAsync(id, "admin-feedback-triage");
        await _api.Reached(TriageRoute);
        await circuit.ClickButtonByIdAsync(id, "admin-feedback-detail-close");
        release.SetResult();
        await saving;

        circuit.Unhandled.Should().BeEmpty();
        circuit.HasElementWithId(id, "admin-feedback-detail").Should().BeFalse();
        circuit.RenderedText(id).Should().Contain("was not saved");
    }

    [Fact]
    public async Task A_report_that_is_gone_reloads_the_list_and_says_so_without_closing_the_page()
    {
        AnswerLists();
        _api.Answer(DetailRoute, HttpStatusCode.NotFound);
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminFeedback>();

        await circuit.ClickButtonByIdAsync(id, "admin-feedback-open-r1");

        circuit.RenderedText(id).Should().Contain("That report is no longer there").And.NotContain("Page not found");
        circuit.HasElementWithId(id, "admin-feedback-sam-table").Should().BeTrue();
        _api.Paths.Should().Equal(SamRoute, GitHubRoute, DetailRoute, SamRoute, GitHubRoute);
    }

    [Fact]
    public async Task A_404_from_someone_who_is_no_longer_an_admin_closes_the_page()
    {
        AnswerLists();
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminFeedback>();

        _api.Answer(DetailRoute, HttpStatusCode.NotFound);
        _api.Answer(SamRoute, HttpStatusCode.NotFound);
        await circuit.ClickButtonByIdAsync(id, "admin-feedback-open-r1");

        circuit.RenderedText(id).Should().Contain("Page not found").And.NotContain("alpha@x.test");
        circuit.HasElementWithId(id, "admin-feedback").Should().BeFalse();
    }

    // ------------------------------------------------------------------ saved screen state

    [Fact]
    public async Task The_saved_screen_state_needs_a_confirmed_password_and_is_shown_as_text()
    {
        AnswerLists();
        _api.Answer(StepUpRoute, HttpStatusCode.OK, ProofBody);
        _api.Answer(ScreenRoute, HttpStatusCode.OK, """
            {"reference":"fs-0123456789ab","learnerId":"p1","createdAtUtc":"2026-11-13T08:00:00Z",
             "context":{"route":"/quiz/<script>","state":{"word":"사과"}}}
            """);
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminFeedback>();

        await circuit.ClickButtonByIdAsync(id, "admin-feedback-screen-open-fs-0123456789ab");
        circuit.AttributesOfElementWithId(id, "admin-feedback-screen-state-open").Should().Contain("disabled");
        _api.Paths.Should().NotContain(ScreenRoute);

        await ConfirmPasswordAsync(circuit, id);
        await circuit.ClickButtonByIdAsync(id, "admin-feedback-screen-state-open");

        circuit.Unhandled.Should().BeEmpty();
        circuit.RenderedText(id).Should().Contain("\"word\"").And.Contain("/quiz/<script>")
            .And.Contain("사과", "Korean is shown as Korean, not as \\u escapes");
        _api.Last(ScreenRoute).Proof.Should().Be("sealed-proof");

        await circuit.ClickButtonByIdAsync(id, "admin-feedback-screen-state-close");
        circuit.RenderedText(id).Should().NotContain("\"word\"");
    }

    // ---------------------------------------------------------------- doubles

    internal sealed record Call(string Method, string Path, string Query, string? Body, string? Proof);

    /// <summary>Answers by path, or by method and path where the two differ, and records every request.</summary>
    internal sealed class ScriptedApi : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _answers = new(StringComparer.Ordinal);

        public List<Call> Calls { get; } = [];

        public IEnumerable<string> Paths => Calls.Select(c => c.Path);

        /// <summary>The most recent request to <paramref name="path"/> with <paramref name="method"/>.</summary>
        public Call Last(string path, string method = "POST") =>
            Calls.Last(c => c.Path == path && c.Method == method);

        public void Answer(string path, HttpStatusCode status, string body = "") => _answers[path] = (status, body);

        public void AnswerPost(string path, HttpStatusCode status, string body = "") => _answers["POST " + path] = (status, body);

        private readonly Dictionary<string, TaskCompletionSource> _holds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource> _reached = new(StringComparer.Ordinal);

        /// <summary>The next request to <paramref name="path"/> waits until the returned source is completed.</summary>
        public TaskCompletionSource Hold(string path)
        {
            _reached[path] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _holds[path] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>Completes when a held request to <paramref name="path"/> has arrived.</summary>
        public Task Reached(string path) => _reached[path].Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add(new Call(
                request.Method.Method,
                path,
                request.RequestUri.Query,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.TryGetValues(AdminApiClient.ProofHeader, out var values) ? values.Single() : null));

            if (_holds.Remove(path, out var hold))
            {
                _reached[path].SetResult();
                await hold.Task;
            }

            var (status, body) = _answers.TryGetValue($"{request.Method.Method} {path}", out var exact)
                ? exact
                : _answers.TryGetValue(path, out var answer) ? answer : (HttpStatusCode.NotFound, "");
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    internal sealed class SignedIn : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity("test"))));
    }
}

/// <summary>The agent tokens page in a live circuit, against a scripted API.</summary>
public sealed class AdminTokensComponentTests
{
    private const string TokensRoute = "/api/admin/tokens";
    private const string RevokeRoute = "/api/admin/tokens/0123456789abcdef0123456789abcdef/revoke";
    private const string StepUpRoute = "/api/admin/step-up";
    private const string TheToken = "ssadm_0123456789abcdef0123456789abcdef_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private const string LiveRow = """
        {"id":"0123456789abcdef0123456789abcdef","name":"weekday check","permissions":["reports","triage"],"state":"live",
         "createdAtUtc":"2026-11-15T12:00:00Z","createdByUserId":"u1","createdByEmail":"captain@example.test",
         "expiresAtUtc":"2026-12-15T12:00:00Z","revokedAtUtc":null,"lastUsedAtUtc":null}
        """;

    private static readonly string ListBody = $$"""{"items":[{{LiveRow}}],"lifetimeDays":30,"maxLive":10}""";

    private readonly AdminFeedbackComponentTests.ScriptedApi _api = new();

    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddScoped(_ => new AdminApiClient(
            new HttpClient(_api) { BaseAddress = new Uri("http://api.test") },
            NullLogger<AdminApiClient>.Instance));
        services.AddScoped<AdminSessionState>();
        services.AddScoped<NavigationManager, SentenceStudio.UI.Tests.Coach.TestNavigationManager>();
        services.AddScoped<NavigationMemoryService>();
        services.AddSingleton<IPreferencesService, SwitchablePreferences>();
        services.AddSingleton<IAppState, AppState>();
        services.AddSingleton<ProgressCacheService>();
        services.AddScoped<AuthenticationStateProvider, AdminFeedbackComponentTests.SignedIn>();
        services.AddScoped<IJSRuntime>(_ => new ModuleAwareJSRuntime());
        services.AddScoped<BlazorLocalizationService>();
        services.AddScoped<SentenceStudio.WebUI.Shared.Sam.SamEntryState>();
        return services.BuildServiceProvider();
    }

    private static InteractiveTestRenderer Circuit(IServiceProvider services) =>
        new(services, services.GetRequiredService<ILoggerFactory>());

    private static string ProofBody =>
        $$"""{"proof":"sealed-proof","expiresAtUtc":"{{DateTimeOffset.UtcNow.AddMinutes(10):O}}"}""";

    [Fact]
    public async Task The_page_lists_tokens_in_words_and_never_shows_a_token()
    {
        _api.Answer(TokensRoute, HttpStatusCode.OK, ListBody);
        await using var services = Services();
        var circuit = Circuit(services);

        var id = await circuit.RenderAsync<AdminTokens>();

        circuit.Unhandled.Should().BeEmpty();
        var text = circuit.RenderedText(id);
        text.Should().Contain("weekday check").And.Contain("read the inbox, set status and issue").And.Contain("Live")
            .And.Contain("2026-12-15 12:00").And.Contain("captain@example.test").And.Contain("Never")
            .And.Contain("It lasts");
        text.Should().NotContain("ssadm_");
        circuit.HasElementWithId(id, "admin-tokens-created").Should().BeFalse();
    }

    [Fact]
    public async Task Creating_a_token_needs_a_name_a_permission_and_a_confirmed_password_and_says_which_is_missing()
    {
        _api.Answer(TokensRoute, HttpStatusCode.OK, """{"items":[],"lifetimeDays":30,"maxLive":10}""");
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminTokens>();
        circuit.RenderedText(id).Should().Contain("No token has been created");

        await circuit.SubmitFormByIdAsync(id, "admin-tokens-form");
        circuit.RenderedText(id).Should().Contain("Give the token a name");

        await circuit.InputValueByIdAsync(id, "admin-tokens-name", "weekday check");
        await circuit.ChangeValueByIdAsync(id, "admin-tokens-permission-reports", false);
        await circuit.SubmitFormByIdAsync(id, "admin-tokens-form");
        circuit.RenderedText(id).Should().Contain("Choose at least one thing");

        await circuit.ChangeValueByIdAsync(id, "admin-tokens-permission-triage", true);
        await circuit.SubmitFormByIdAsync(id, "admin-tokens-form");
        circuit.RenderedText(id).Should().Contain("Confirm your password first");

        _api.Calls.Should().OnlyContain(c => c.Method == "GET", "nothing was posted");
    }

    [Fact]
    public async Task A_created_token_is_shown_once_and_is_gone_after_it_is_dismissed()
    {
        _api.Answer(TokensRoute, HttpStatusCode.OK, """{"items":[],"lifetimeDays":30,"maxLive":10}""");
        _api.Answer(StepUpRoute, HttpStatusCode.OK, ProofBody);
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminTokens>();
        await circuit.InputValueByIdAsync(id, "admin-password", "right-password");
        await circuit.SubmitFormByIdAsync(id, "admin-password-form");
        circuit.HasElementWithId(id, "admin-password-card").Should().BeFalse();

        await circuit.InputValueByIdAsync(id, "admin-tokens-name", "  weekday check ");
        await circuit.ChangeValueByIdAsync(id, "admin-tokens-permission-learner-content", true);
        _api.AnswerPost(TokensRoute, HttpStatusCode.OK, $$"""{"token":"{{TheToken}}","row":{{LiveRow}}}""");
        _api.Answer(TokensRoute, HttpStatusCode.OK, ListBody);
        await circuit.SubmitFormByIdAsync(id, "admin-tokens-form");

        circuit.Unhandled.Should().BeEmpty();
        var posted = _api.Last(TokensRoute);
        posted.Body.Should().Be("""{"name":"weekday check","permissions":["reports","learner_content"]}""");
        posted.Proof.Should().Be("sealed-proof");
        _api.Calls.Last().Should().Match<AdminFeedbackComponentTests.Call>(
            c => c.Method == "GET" && c.Path == TokensRoute, "the list is read again after the token is made");
        circuit.HasElementWithId(id, "admin-tokens-created").Should().BeTrue();
        circuit.AttributeValue(id, "admin-tokens-created-value", "value").Should().Be(TheToken);
        circuit.RenderedText(id).Should().Contain("shown this once").And.Contain("admin-agent-token");

        await circuit.ClickButtonByIdAsync(id, "admin-tokens-created-dismiss");
        circuit.HasElementWithId(id, "admin-tokens-created").Should().BeFalse();
        circuit.RenderedText(id).Should().NotContain("ssadm_");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, """{"code":"step_up_required"}""", "Confirm your password again")]
    [InlineData(HttpStatusCode.Conflict, """{"code":"too_many_tokens"}""", "Revoke one first")]
    [InlineData(HttpStatusCode.BadRequest, """{"code":"invalid_request"}""", "That name cannot be used")]
    [InlineData(HttpStatusCode.InternalServerError, "", "could not be created")]
    public async Task A_token_the_API_would_not_create_says_why(HttpStatusCode status, string body, string expected)
    {
        _api.Answer(TokensRoute, HttpStatusCode.OK, """{"items":[],"lifetimeDays":30,"maxLive":10}""");
        _api.Answer(StepUpRoute, HttpStatusCode.OK, ProofBody);
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminTokens>();
        await circuit.InputValueByIdAsync(id, "admin-password", "right-password");
        await circuit.SubmitFormByIdAsync(id, "admin-password-form");
        await circuit.InputValueByIdAsync(id, "admin-tokens-name", "weekday check");

        _api.AnswerPost(TokensRoute, status, body);
        await circuit.SubmitFormByIdAsync(id, "admin-tokens-form");

        circuit.Unhandled.Should().BeEmpty();
        circuit.RenderedText(id).Should().Contain(expected);
        circuit.HasElementWithId(id, "admin-tokens-created").Should().BeFalse();
    }

    [Fact]
    public async Task Revoking_asks_first_and_Keep_revokes_nothing()
    {
        _api.Answer(TokensRoute, HttpStatusCode.OK, ListBody);
        _api.Answer(RevokeRoute, HttpStatusCode.OK, LiveRow.Replace("\"state\":\"live\"", "\"state\":\"revoked\""));
        await using var services = Services();
        var circuit = Circuit(services);
        var id = await circuit.RenderAsync<AdminTokens>();

        const string tokenId = "0123456789abcdef0123456789abcdef";
        await circuit.ClickButtonByIdAsync(id, $"admin-tokens-revoke-{tokenId}");
        circuit.HasElementWithId(id, $"admin-tokens-revoke-confirm-{tokenId}").Should().BeTrue("revoking asks first");
        _api.Paths.Should().NotContain(RevokeRoute);

        await circuit.ClickButtonByIdAsync(id, $"admin-tokens-revoke-cancel-{tokenId}");
        circuit.HasElementWithId(id, $"admin-tokens-revoke-confirm-{tokenId}").Should().BeFalse();
        _api.Paths.Should().NotContain(RevokeRoute);

        await circuit.ClickButtonByIdAsync(id, $"admin-tokens-revoke-{tokenId}");
        _api.Answer(TokensRoute, HttpStatusCode.OK,
            $$"""{"items":[{{LiveRow.Replace("\"state\":\"live\"", "\"state\":\"revoked\"").Replace("\"revokedAtUtc\":null", "\"revokedAtUtc\":\"2026-11-16T09:00:00Z\"")}}],"lifetimeDays":30,"maxLive":10}""");
        await circuit.ClickButtonByIdAsync(id, $"admin-tokens-revoke-confirm-{tokenId}");

        circuit.Unhandled.Should().BeEmpty();
        _api.Calls.Select(c => $"{c.Method} {c.Path}").Should().Equal(
            $"GET {TokensRoute}", $"POST {RevokeRoute}", $"GET {TokensRoute}");
        circuit.RenderedText(id).Should().Contain("Revoked").And.Contain("2026-11-16 09:00");
        circuit.HasElementWithId(id, $"admin-tokens-revoke-{tokenId}").Should().BeFalse("a revoked token has nothing left to revoke");
    }

    [Theory]
    [InlineData("creator_not_admin", "Not accepted while its creator is not an admin")]
    [InlineData("creator_password_changed", "its creator changed their password")]
    public async Task A_token_its_creator_no_longer_stands_behind_is_not_shown_as_live_and_can_be_revoked(
        string state, string expected)
    {
        _api.Answer(TokensRoute, HttpStatusCode.OK,
            $$"""{"items":[{{LiveRow.Replace("\"state\":\"live\"", $"\"state\":\"{state}\"")}}],"lifetimeDays":30,"maxLive":10}""");
        await using var services = Services();
        var circuit = Circuit(services);

        var id = await circuit.RenderAsync<AdminTokens>();

        circuit.RenderedText(id).Should().Contain(expected).And.NotContain("Live");
        circuit.HasElementWithId(id, "admin-tokens-revoke-0123456789abcdef0123456789abcdef").Should().BeTrue();
    }

    [Fact]
    public async Task A_person_the_API_refuses_sees_page_not_found_and_no_tokens()
    {
        await using var services = Services();
        var circuit = Circuit(services);

        var id = await circuit.RenderAsync<AdminTokens>();

        circuit.RenderedText(id).Should().Contain("Page not found").And.NotContain("weekday check");
        circuit.HasElementWithId(id, "admin-tokens").Should().BeFalse();
    }
}
