using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.LearnerMemory;
using SentenceStudio.Services.Api;
using SentenceStudio.WebUI.Services;
using SentenceStudio.WebUI.Shared.Coach;

namespace SentenceStudio.UI.Tests.Coach;

/// <summary>
/// What Sam remembers: the directory that talks to the memory routes, and the two surfaces that
/// render it. The rules being protected here are that a learner can always tell what is
/// remembered and undo it, that a refusal never explains itself in terms of the refused value,
/// and that the bookkeeping the server needs never reaches the page.
/// </summary>
public class CoachMemoryTests
{
    private static (CoachMemoryDirectory Directory, FakeCoachApiClient Client) Create()
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        return (new CoachMemoryDirectory(client), client);
    }

    private static async Task<string> RenderPanelAsync(
        CoachMemoryDirectory memory,
        string culture = "en")
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(culture);

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScoped<BlazorLocalizationService>();
            // The coach's name comes from the learner's study language, so every component that
            // names it needs the resolver. The all-optional constructor makes this a one-liner:
            // with no language source it answers with the default persona.
            services.AddScoped<CoachPersona>();
            services.AddScoped<Microsoft.JSInterop.IJSRuntime>(_ => new StubJSRuntime());
            services.AddScoped(_ => memory);

            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var output = await renderer.RenderComponentAsync<CoachMemoryPanel>(ParameterView.Empty);
                return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
            });
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = previous;
        }
    }

    // ---------------------------------------------------------------- availability

    [Fact]
    public async Task ARouteGroupThatAnswers404LeavesTheFeatureUnavailable()
    {
        var (memory, client) = Create();
        client.OnListActiveMemories = () => null;

        await memory.EnsureLoadedAsync();

        memory.Availability.Should().Be(CoachMemoryAvailability.Unavailable);

        // Candidates are not asked for at all. Once the route group is gone there is nothing to
        // ask, and a second call would only produce a second 404 in the log.
        client.ListMemoryCandidatesCalls.Should().Be(0);
    }

    [Fact]
    public async Task AnUnavailableFeatureRendersNothingAtAll()
    {
        var (memory, client) = Create();
        client.OnListActiveMemories = () => null;
        await memory.EnsureLoadedAsync();

        var html = await RenderPanelAsync(memory);

        // The whole surface is absent, not disabled. A disabled control would tell a learner the
        // feature exists for them, which is the same thing the server's 404 refuses to say.
        html.Should().NotContain("What Sam remembers");
        html.Should().NotContain("coach-memory-panel");
    }

    [Fact]
    public async Task LoadingOnceDoesNotRefetchOnEveryRender()
    {
        var (memory, client) = Create();
        client.ActiveFacts.Add(FakeCoachApiClient.Fact());

        await memory.EnsureLoadedAsync();
        await memory.EnsureLoadedAsync();

        client.ListActiveMemoriesCalls.Should().Be(1);
    }

    // ---------------------------------------------------------------- reading the lists

    [Fact]
    public async Task ActiveAndCandidateFactsAreReadIntoSeparateLists()
    {
        var (memory, client) = Create();
        client.ActiveFacts.Add(FakeCoachApiClient.Fact("fact-active"));
        client.CandidateFacts.Add(FakeCoachApiClient.Fact(
            "fact-candidate", status: CoachMemoryStatus.Candidate));

        await memory.EnsureLoadedAsync();

        memory.Active.Should().ContainSingle(f => f.Id == "fact-active");
        memory.Candidates.Should().ContainSingle(f => f.Id == "fact-candidate");
    }

    [Fact]
    public async Task AnEmptyActiveListIsExplainedRatherThanLeftBlank()
    {
        var (memory, _) = Create();
        await memory.EnsureLoadedAsync();

        memory.IsPaused.Should().BeTrue();

        var html = await RenderPanelAsync(memory);

        // Nothing remembered is a real state, not a failed load. Saying so is what keeps a
        // learner from reading an empty list as a broken screen.
        html.Should().Contain("Sam is not using any saved preferences");
    }

    // ---------------------------------------------------------------- writes

    [Fact]
    public async Task ApprovingACandidateEchoesTheVersionTheLearnerSaw()
    {
        var (memory, client) = Create();
        var candidate = FakeCoachApiClient.Fact(
            "fact-1", status: CoachMemoryStatus.Candidate, version: 7);
        client.CandidateFacts.Add(candidate);
        await memory.EnsureLoadedAsync();

        var outcome = await memory.ApproveAsync(candidate);

        outcome.Should().Be(CoachMemoryOutcome.Saved);
        client.ObservedExpectedVersions.Should().ContainSingle().Which.Should().Be(7);
    }

    [Fact]
    public async Task ApprovingWithAnEditSendsOneRequestCarryingTheEditedValue()
    {
        var (memory, client) = Create();
        var candidate = FakeCoachApiClient.Fact("fact-1", status: CoachMemoryStatus.Candidate);
        client.CandidateFacts.Add(candidate);
        await memory.EnsureLoadedAsync();

        var edited = new CoachMemoryValueDto
        {
            Kind = CoachMemoryKind.PersistentStudyGoal,
            StudyGoalText = "Wants to order coffee in Korean"
        };

        await memory.ApproveAsync(candidate, edited);

        // One call, not approve-then-edit. Two calls would leave the unedited value briefly
        // eligible for a prompt, which is exactly what the learner declined.
        client.ApproveMemoryCalls.Should().Be(1);
        client.EditMemoryCalls.Should().Be(0);
        client.ObservedEditedValues.Should().ContainSingle()
            .Which!.StudyGoalText.Should().Be("Wants to order coffee in Korean");
    }

    [Fact]
    public async Task RejectingACandidateLeavesNothingBehind()
    {
        var (memory, client) = Create();
        var candidate = FakeCoachApiClient.Fact("fact-1", status: CoachMemoryStatus.Candidate);
        client.CandidateFacts.Add(candidate);
        await memory.EnsureLoadedAsync();

        await memory.RejectAsync(candidate);

        memory.Candidates.Should().BeEmpty();
        memory.Active.Should().BeEmpty();
    }

    [Fact]
    public async Task ForgettingOneFactRemovesOnlyThatFact()
    {
        var (memory, client) = Create();
        client.ActiveFacts.Add(FakeCoachApiClient.Fact("fact-1"));
        client.ActiveFacts.Add(FakeCoachApiClient.Fact("fact-2", displayText: "Prefers short answers"));
        await memory.EnsureLoadedAsync();

        await memory.ForgetAsync(memory.Active.First(f => f.Id == "fact-1"));

        memory.Active.Should().ContainSingle().Which.Id.Should().Be("fact-2");
    }

    [Fact]
    public async Task ForgettingEverythingReportsHowMuchWasRemoved()
    {
        var (memory, client) = Create();
        client.ActiveFacts.Add(FakeCoachApiClient.Fact("fact-1"));
        client.ActiveFacts.Add(FakeCoachApiClient.Fact("fact-2"));
        client.CandidateFacts.Add(FakeCoachApiClient.Fact("fact-3", status: CoachMemoryStatus.Candidate));
        await memory.EnsureLoadedAsync();

        var (outcome, forgotten) = await memory.ForgetAllAsync();

        outcome.Should().Be(CoachMemoryOutcome.Saved);
        forgotten.Should().Be(3);
        memory.Active.Should().BeEmpty();
        memory.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task EveryWriteRereadsBothListsBecauseTheServerRotatesItsCheckpoint()
    {
        var (memory, client) = Create();
        var candidate = FakeCoachApiClient.Fact("fact-1", status: CoachMemoryStatus.Candidate);
        client.CandidateFacts.Add(candidate);
        await memory.EnsureLoadedAsync();

        var readsBefore = client.ListActiveMemoriesCalls;

        await memory.ApproveAsync(candidate);

        // A locally patched list would drift from the context Sam actually uses, and the drift
        // would be invisible until a prompt behaved unexpectedly.
        client.ListActiveMemoriesCalls.Should().BeGreaterThan(readsBefore);
        client.ListMemoryCandidatesCalls.Should().BeGreaterThan(1);
    }

    // ---------------------------------------------------------------- refusals

    [Fact]
    public async Task AVersionConflictRefetchesInsteadOfOverwriting()
    {
        var (memory, client) = Create();
        var fact = FakeCoachApiClient.Fact("fact-1", version: 2);
        client.ActiveFacts.Add(fact);
        await memory.EnsureLoadedAsync();

        var attempted = false;
        client.OnEditMemory = (_, _) =>
        {
            attempted = true;
            client.OnEditMemory = null;
            throw new CoachApiException(
                System.Net.HttpStatusCode.Conflict,
                CoachMemoryProblemTypes.Conflict,
                "Conflict.",
                detail: null);
        };

        var outcome = await memory.EditAsync(fact, new CoachMemoryValueDto
        {
            Kind = CoachMemoryKind.PersistentStudyGoal,
            StudyGoalText = "Something else"
        });

        attempted.Should().BeTrue();
        outcome.Should().Be(CoachMemoryOutcome.Conflict);
        memory.NoticeKey.Should().Be("Coach_MemoryConflict");

        // The learner is shown the list as it actually is now, so their next decision is made
        // against the truth rather than against the value they were holding.
        client.ListActiveMemoriesCalls.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task ARejectedValueIsRefusedWithoutNamingTheValueOrThePolicy()
    {
        var (memory, client) = Create();
        var candidate = FakeCoachApiClient.Fact(
            "fact-1", status: CoachMemoryStatus.Candidate, displayText: "unspeakable secret");
        client.CandidateFacts.Add(candidate);
        await memory.EnsureLoadedAsync();

        client.OnApproveMemory = (_, _) => throw new CoachApiException(
            System.Net.HttpStatusCode.UnprocessableEntity,
            CoachMemoryProblemTypes.ValueRejected,
            "Rejected.",
            detail: null);

        var outcome = await memory.ApproveAsync(candidate);

        outcome.Should().Be(CoachMemoryOutcome.ValueRejected);
        memory.NoticeKey.Should().Be("Coach_MemoryValueRejected");
    }

    [Fact]
    public async Task AnOutageIsSaidPlainlyAndChangesNothing()
    {
        var (memory, client) = Create();
        var fact = FakeCoachApiClient.Fact("fact-1");
        client.ActiveFacts.Add(fact);
        await memory.EnsureLoadedAsync();

        client.OnForgetMemory = _ => throw new CoachApiException(
            System.Net.HttpStatusCode.ServiceUnavailable,
            CoachMemoryProblemTypes.Unavailable,
            "Unavailable.",
            detail: null);

        var outcome = await memory.ForgetAsync(fact);

        outcome.Should().Be(CoachMemoryOutcome.Unavailable);
        memory.NoticeKey.Should().Be("Coach_MemoryUnavailable");
    }

    // ---------------------------------------------------------------- what reaches the page

    [Fact]
    public async Task TheFactValueIsShownButTheIdentifierAndVersionAreNot()
    {
        var (memory, client) = Create();
        client.ActiveFacts.Add(FakeCoachApiClient.Fact(
            "fact-abc-123", displayText: "Wants to order food in Korean", version: 9));
        await memory.EnsureLoadedAsync();

        var html = await RenderPanelAsync(memory);

        html.Should().Contain("Wants to order food in Korean");

        // The identifier and the version are bookkeeping the server needs and the learner cannot
        // act on. Putting them on screen only invites a support conversation about numbers that
        // mean nothing to the person reading them.
        html.Should().NotContain("fact-abc-123");
        html.Should().NotContain("\"version\"");
    }

    [Fact]
    public async Task AFactThatTriesToBeMarkupIsRenderedAsText()
    {
        var (memory, client) = Create();
        client.ActiveFacts.Add(FakeCoachApiClient.Fact(
            "fact-1", displayText: "<script>alert('x')</script>"));
        await memory.EnsureLoadedAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<BlazorLocalizationService>();
        // The coach's name comes from the learner's study language, so every component that
        // names it needs the resolver. The all-optional constructor makes this a one-liner:
        // with no language source it answers with the default persona.
        services.AddScoped<CoachPersona>();
        services.AddScoped<Microsoft.JSInterop.IJSRuntime>(_ => new StubJSRuntime());
        services.AddScoped(_ => memory);

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var raw = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<CoachMemoryPanel>(ParameterView.Empty);
            return output.ToHtmlString();
        });

        // Asserted on the undecoded output: a decoded string cannot tell an escaped tag from a
        // live one, so decoding first would make this test pass on a real injection.
        raw.Should().NotContain("<script>");
        raw.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public async Task ScopeProvenanceAndDatesAreShownSoAFactCanBeJudged()
    {
        var (memory, client) = Create();
        client.ActiveFacts.Add(FakeCoachApiClient.Fact("fact-1", targetLanguageCode: "ko"));
        await memory.EnsureLoadedAsync();

        var html = await RenderPanelAsync(memory);

        html.Should().Contain("Applies to");
        html.Should().Contain("Source");
        html.Should().Contain("Saved");
    }

    [Fact]
    public async Task ForgettingEverythingPromisesNotToUndoPlansOrProgress()
    {
        var (memory, client) = Create();
        client.ActiveFacts.Add(FakeCoachApiClient.Fact());
        await memory.EnsureLoadedAsync();

        var html = await RenderPanelAsync(memory);

        // The same promise the conversation delete dialog makes. A learner clearing what Sam
        // remembers must not have to guess whether they are also clearing their work.
        html.Should().Contain("does not undo Today's Plan");
        html.Should().Contain("does not change your account");
    }

    [Fact]
    public async Task SavedPreferencesAreDistinguishedFromSettingsAndFromTodaysPlan()
    {
        var (memory, client) = Create();
        client.ActiveFacts.Add(FakeCoachApiClient.Fact());
        await memory.EnsureLoadedAsync();

        var html = await RenderPanelAsync(memory);

        html.Should().Contain("separate from your app settings");
    }

    // ---------------------------------------------------------------- accessibility and language

    [Fact]
    public async Task TheTabsAndTheListCarryNamesAndRoles()
    {
        var (memory, client) = Create();
        client.ActiveFacts.Add(FakeCoachApiClient.Fact());
        await memory.EnsureLoadedAsync();

        var html = await RenderPanelAsync(memory);

        html.Should().Contain("role=\"tablist\"");
        html.Should().Contain("role=\"tab\"");
        html.Should().Contain("role=\"tabpanel\"");
        html.Should().Contain("aria-selected=\"true\"");
        html.Should().Contain("aria-labelledby=\"coach-memory-heading\"");
    }

    [Fact]
    public async Task TheNoticeIsAnnouncedRatherThanJustDrawn()
    {
        var (memory, client) = Create();
        var fact = FakeCoachApiClient.Fact("fact-1");
        client.ActiveFacts.Add(fact);
        await memory.EnsureLoadedAsync();

        client.OnForgetMemory = _ => throw new CoachApiException(
            System.Net.HttpStatusCode.ServiceUnavailable,
            CoachMemoryProblemTypes.Unavailable,
            "Unavailable.",
            detail: null);
        await memory.ForgetAsync(fact);

        var html = await RenderPanelAsync(memory);

        html.Should().Contain("role=\"status\"");
    }

    [Fact]
    public async Task KoreanCallsTheCoachSsamAndTranslatesTheActions()
    {
        var (memory, client) = Create();
        client.ActiveFacts.Add(FakeCoachApiClient.Fact());
        await memory.EnsureLoadedAsync();

        var html = await RenderPanelAsync(memory, culture: "ko");

        html.Should().Contain("쌤");
        html.Should().Contain("전부 지우기");
        html.Should().NotContain("What Sam remembers");
    }

    // ---------------------------------------------------------------- the inline candidate

    [Fact]
    public async Task ATurnCandidateIsHeldSeparatelyFromThePlanSuggestion()
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = false };
        var directory = new CoachConversationDirectory(client);
        var state = new CoachWorkspaceState(client, directory);

        var candidate = FakeCoachApiClient.Fact("fact-1", status: CoachMemoryStatus.Candidate);
        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn().WithMemoryCandidate(candidate);

        await state.OpenAsync(CoachPresentation.Overlay);
        state.Draft = "I want to order food in Korean";
        await state.SendDraftAsync();

        // Accepting a change to today's plan and agreeing to be remembered are different
        // consents. One control for both would let a learner who wanted the first silently give
        // the second.
        state.PendingMemoryCandidate.Should().NotBeNull();
        state.PendingMemoryCandidate!.Id.Should().Be("fact-1");
        state.PendingSuggestion.Should().BeNull();
    }

    [Fact]
    public async Task DecidingOnTheCandidateClearsItWithoutTouchingThePlan()
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = false };
        var directory = new CoachConversationDirectory(client);
        var state = new CoachWorkspaceState(client, directory);

        client.OnSubmitTurn = _ => CoachStateMachineTests.Turn()
            .WithMemoryCandidate(FakeCoachApiClient.Fact("fact-1", status: CoachMemoryStatus.Candidate));

        await state.OpenAsync(CoachPresentation.Overlay);
        state.Draft = "I want to order food in Korean";
        await state.SendDraftAsync();

        state.ClearMemoryCandidate();

        state.PendingMemoryCandidate.Should().BeNull();
        state.PendingMemoryTurn.Should().BeNull();
    }

    [Fact]
    public void ClearingTheCandidateAlsoClearsTheTurnItBelongedTo()
    {
        var client = new FakeCoachApiClient { DurableHistoryAvailable = true };
        var directory = new CoachConversationDirectory(client);
        var state = new CoachWorkspaceState(client, directory);

        state.ClearMemoryCandidate();

        state.PendingMemoryCandidate.Should().BeNull();
        state.PendingMemoryTurn.Should().BeNull();
    }
}
