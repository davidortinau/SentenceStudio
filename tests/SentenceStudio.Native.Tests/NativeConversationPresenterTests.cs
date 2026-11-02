using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Services.Progress;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Conversation source contract (activities.md 1.1-1.4 and 9) against real
/// SQLite, the shared progress service and the seeded source scenarios, with
/// fake owner-bound AI and speech ports.
/// </summary>
public sealed class NativeConversationPresenterTests
{
    private const string Conversation = NativeActivityCatalog.Conversation;

    [Fact]
    public void Catalog_RegistersConversationForTheConversationPlanType()
    {
        Assert.True(NativeActivityCatalog.IsImplemented(Conversation));
        var definition = NativeActivityCatalog.ForPlanType(PlanActivityType.Conversation)!;
        Assert.Equal(Conversation, definition.Route);
        Assert.Equal(PlanActivityType.Conversation, definition.TimerType);
        Assert.True(definition.TimerRecordsSkill);
        Assert.False(NativeActivityCatalog.IsVocabularyDriven(PlanActivityType.Conversation));
        Assert.Contains(NativeDashboardPresenter.ChooseOwnActivities, tile => tile.Route == Conversation);
    }

    [Fact]
    public async Task Journey_SeedsScenariosOpensChatsScoresAndWritesNoUserActivity()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 3);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = new FakeConversationAi();
        using var conversation = Presenter(fixture, ai);

        var opened = await conversation.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));
        var state = await conversation.Opening;

        // Source predefined scenarios, ordered predefined-first then by name.
        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.Equal(["Asking for Directions", "First Meeting", "Ordering Coffee",
            "Ordering Dinner", "Weekend Plans"], state.Scenarios.Select(item => item.DisplayName));
        Assert.Equal(state.Scenarios[0].Id, state.ActiveScenarioId);
        Assert.Equal("Korean", state.TargetLanguage);
        var opening = Assert.Single(state.Messages);
        Assert.False(opening.IsUser);
        Assert.Equal("최수진", opening.Author);
        Assert.Equal("opening 1", opening.Text);
        Assert.Equal(("최수진", "Korean"), (ai.Starts[0].Scenario?.PersonaName, ai.Starts[0].Language));
        Assert.False(state.CanSend);

        conversation.SetDraft("  target-native-owner-a-0 좋아요  ");
        ai.Analysis =
        [
            new VocabularyAnalysis { DictionaryForm = "target-native-owner-a-0", UsedForm = "target-native-owner-a-0", UsageCorrect = true },
            new VocabularyAnalysis { DictionaryForm = "unknown", UsedForm = "unknown", UsageCorrect = true },
        ];
        state = await conversation.SendAsync();

        Assert.Equal(3, state.Messages.Length);
        var user = state.Messages[1];
        Assert.True(user.IsUser);
        Assert.Equal("target-native-owner-a-0 좋아요", user.Text);
        Assert.Equal(85, user.Comprehension);
        Assert.True(user.ShowsComprehension);
        var correction = Assert.Single(user.Corrections);
        Assert.Equal(("wrong", "right", "why"), (correction.Original, correction.Corrected, correction.Explanation));
        Assert.Equal("reply 1", state.Messages[2].Text);
        Assert.Equal(string.Empty, state.Draft);
        Assert.False(state.IsWaitingForReply);
        // The whole history (with the newest user turn) goes to the AI, authored "Me".
        var history = ai.Continues.Single().History;
        Assert.Equal([("최수진", "opening 1"), ("Me", "target-native-owner-a-0 좋아요")], history);

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var attempt = await db.VocabularyLearningContexts.AsNoTracking()
                .Where(context => context.Activity == NativeConversationPresenter.ScoringActivity)
                .Join(db.VocabularyProgresses, context => context.VocabularyProgressId,
                    progress => progress.Id, (context, progress) => new { context, progress })
                .SingleAsync();
            Assert.Equal(NativeSliceFixture.UserA, attempt.progress.UserId);
            Assert.Equal(fixture.WordId(NativeSliceFixture.UserA, 0), attempt.progress.VocabularyWordId);
            Assert.True(attempt.context.WasCorrect);
            Assert.Equal("Text", attempt.context.InputMode);
            // Correct attempts add the activity weight to the streak.
            Assert.Equal(NativeConversationPresenter.DifficultyWeight, attempt.progress.CurrentStreak, 3);
            // Source page writes no UserActivity and saves no transcript.
            Assert.False(await db.UserActivities.AnyAsync());
            Assert.False(await db.Conversations.AnyAsync());
            Assert.False(await db.ConversationChunks.AnyAsync());
        }

        var exit = await conversation.ExitAsync();
        Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
        Assert.Null(exit.ErrorKey);
        Assert.True(exit.HasPendingSync);
        conversation.SetDraft("late");
        Assert.Equal(NativeDestination.TodaysPlan, (await conversation.SendAsync()).Destination);
        Assert.Single(ai.Continues);

        using var rows = fixture.Scope();
        var row = await rows.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .DailyPlanCompletions.AsNoTracking().SingleAsync(item => item.PlanItemId.StartsWith("adhoc-"));
        Assert.Equal(NativeSliceFixture.UserA, row.UserProfileId);
        Assert.Equal(nameof(PlanActivityType.Conversation), row.ActivityType);
    }

    [Fact]
    public async Task WrongUsage_UsesTheSofterConversationPenalty()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1, known: true);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = new FakeConversationAi
        {
            Analysis = [new VocabularyAnalysis { DictionaryForm = "target-native-owner-a-0", UsageCorrect = false }]
        };
        using var conversation = Presenter(fixture, ai);
        await conversation.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));
        await conversation.Opening;
        conversation.SetDraft("target-native-owner-a-0");
        var sent = await conversation.SendAsync();
        Assert.Empty(sent.Notices);
        Assert.Equal(1, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));

        using var scope = fixture.Scope();
        var progress = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .VocabularyProgresses.AsNoTracking().SingleAsync();
        Assert.Equal(0.9f * NativeConversationPresenter.PenaltyOverride, progress.MasteryScore, 3);
    }

    [Fact]
    public async Task ScenarioAndLanguage_RestartTheChatWithoutChangingTheProfile()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = new FakeConversationAi();
        using var conversation = Presenter(fixture, ai);
        await conversation.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));
        var state = await conversation.Opening;
        conversation.SetDraft("hello");
        state = await conversation.SendAsync();
        Assert.Equal(3, state.Messages.Length);

        var coffee = state.Scenarios.Single(item => item.DisplayName == "Ordering Coffee");
        state = await conversation.SelectScenarioAsync(coffee.Id);
        Assert.Equal(coffee.Id, state.ActiveScenarioId);
        Assert.Equal("박지영", Assert.Single(state.Messages).Author);
        Assert.Equal("박지영", ai.Starts[^1].Scenario?.PersonaName);
        // Same scenario again is a no-op.
        await conversation.SelectScenarioAsync(coffee.Id);
        Assert.Equal(2, ai.Starts.Count);

        state = await conversation.SelectLanguageAsync("Spanish");
        Assert.Equal("Spanish", state.TargetLanguage);
        Assert.Equal("Spanish", ai.Starts[^1].Language);
        Assert.Single(state.Messages);
        Assert.Equal("Spanish", (await conversation.SelectLanguageAsync("Klingon")).TargetLanguage);

        conversation.SetDraft("hola");
        await conversation.SendAsync();
        Assert.Equal("Spanish", ai.Continues[^1].Language);
        using var scope = fixture.Scope();
        var profile = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .UserProfiles.AsNoTracking().SingleAsync(item => item.Id == NativeSliceFixture.UserA);
        Assert.Equal("Korean", profile.TargetLanguage);
    }

    [Fact]
    public async Task AiFailures_ShowSourceErrorsAndWriteNothing()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = new FakeConversationAi { FailStart = true };
        using var conversation = Presenter(fixture, ai);
        await conversation.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));
        var state = await conversation.Opening;
        Assert.Equal(NativePhase.Ready, state.Phase);
        Assert.Empty(state.Messages);
        Assert.Equal(NativeConversationText.StartFailed, Assert.Single(state.Notices).Key);

        ai.FailStart = false;
        ai.OpeningText = "";
        var other = state.Scenarios[1];
        state = await conversation.SelectScenarioAsync(other.Id);
        Assert.Equal(NativeConversationText.StartFailed, Assert.Single(state.Notices).Key);

        ai.FailReply = true;
        ai.Analysis = [new VocabularyAnalysis { DictionaryForm = "target-native-owner-a-0", UsageCorrect = true }];
        conversation.SetDraft("target-native-owner-a-0");
        state = await conversation.SendAsync();
        Assert.Equal(NativeConversationText.ReplyFailed, Assert.Single(state.Notices).Key);
        Assert.True(Assert.Single(state.Messages).IsUser);
        Assert.False(state.IsWaitingForReply);

        ai.FailReply = false;
        ai.ReplyText = "";
        conversation.SetDraft("again");
        state = await conversation.SendAsync();
        Assert.Equal(NativeConversationText.ReplyFailed, Assert.Single(state.Notices).Key);
        Assert.All(state.Messages, message => Assert.True(message.IsUser));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        state = await conversation.DismissNoticesAsync();
        Assert.Empty(state.Notices);
    }

    [Fact]
    public async Task HangingAi_ExitReturnsPromptlySavesTheTimerAndDiscardsLateResults()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = new FakeConversationAi { HangStart = new() };
        using var conversation = Presenter(fixture, ai);

        var opened = await conversation.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));
        Assert.Equal(NativePhase.Loading, opened.Phase);
        var clock = Stopwatch.StartNew();
        var exit = await conversation.ExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
        Assert.Null(exit.ErrorKey);
        using (var scope = fixture.Scope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .DailyPlanCompletions.AnyAsync(row => row.UserProfileId == NativeSliceFixture.UserA
                    && row.ActivityType == nameof(PlanActivityType.Conversation)));
        // The hung opening finishing later changes nothing.
        ai.HangStart.SetResult();
        Assert.Equal(NativeDestination.TodaysPlan, (await conversation.Opening).Destination);
        Assert.Empty(conversation.State.Messages.IsDefault ? [] : conversation.State.Messages);

        // A hung reply: Exit still returns at once and the late reply scores nothing.
        using var second = Presenter(fixture, ai);
        ai.HangStart = null;
        await second.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));
        await second.Opening;
        ai.HangReply = new();
        ai.Analysis = [new VocabularyAnalysis { DictionaryForm = "target-native-owner-a-0", UsageCorrect = true }];
        second.SetDraft("target-native-owner-a-0");
        var sending = second.SendAsync();
        Assert.True(second.State.IsWaitingForReply);
        await second.ExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        ai.HangReply.SetResult();
        Assert.Equal(NativeDestination.TodaysPlan, (await sending).Destination);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task Back_ReturnsItsDestinationToTheOpeningAndReply_EvenWhenTheyResumeFirst()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = new FakeConversationAi { HangStart = new() };
        using (var conversation = Presenter(fixture, ai))
        {
            var published = new List<NativeConversationState>();
            conversation.StateChanged += published.Add;
            await conversation.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));
            // The opening line arrives while Back is between its cancel and its gate.
            ExitRace.HoldExitUntil(conversation.ExitFence, () =>
            {
                ai.HangStart.SetResult();
                return conversation.Opening;
            });
            var exit = await conversation.ExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var late = await conversation.Opening.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(NativeDestination.TodaysPlan, exit.Destination);
            Assert.Equal(NativeDestination.TodaysPlan, late.Destination);
            Assert.Equal(NativeDestination.TodaysPlan, published[^1].Destination);
        }

        ai.HangStart = null;
        using var second = Presenter(fixture, ai);
        await second.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));
        await second.Opening;
        ai.HangReply = new();
        second.SetDraft("target-native-owner-a-0");
        var sending = second.SendAsync();
        ExitRace.HoldExitUntil(second.ExitFence, () =>
        {
            ai.HangReply.SetResult();
            return sending;
        });
        Assert.Equal(NativeDestination.TodaysPlan,
            (await second.ExitAsync().WaitAsync(TimeSpan.FromSeconds(10))).Destination);
        Assert.Equal(NativeDestination.TodaysPlan,
            (await sending.WaitAsync(TimeSpan.FromSeconds(10))).Destination);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task RestartWhileReplyPending_DiscardsTheStaleReply()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = new FakeConversationAi();
        using var conversation = Presenter(fixture, ai);
        await conversation.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));
        var state = await conversation.Opening;
        ai.HangReply = new();
        ai.Analysis = [new VocabularyAnalysis { DictionaryForm = "target-native-owner-a-0", UsageCorrect = true }];
        conversation.SetDraft("target-native-owner-a-0");
        var sending = conversation.SendAsync();
        state = await conversation.SelectScenarioAsync(state.Scenarios[2].Id);
        ai.HangReply.SetResult();
        await sending;
        state = conversation.State;
        Assert.Single(state.Messages);
        Assert.False(state.IsWaitingForReply);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task QuickPhrases_ToggleAndReplaceTheDraft()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        using var conversation = Presenter(fixture, new FakeConversationAi());
        await conversation.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));
        await conversation.Opening;

        Assert.Equal(8, NativeConversationState.QuickPhrases.Length);
        var state = await conversation.ToggleQuickPhrasesAsync();
        Assert.True(state.ShowQuickPhrases);
        var version = state.DraftVersion;
        conversation.SetDraft("typed");
        state = await conversation.UseQuickPhraseAsync(3);
        Assert.Equal("다시 말해 주세요.", state.Draft);
        Assert.False(state.ShowQuickPhrases);
        Assert.Equal(version + 1, state.DraftVersion);
        Assert.True(state.CanSend);
    }

    [Fact]
    public async Task Listen_PlaysPersonaRepliesInTheSelectedLanguageAndReportsFailures()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var speech = new ScratchShadowingSpeech();
        var player = new ScratchShadowingPlayer();
        using var conversation = Presenter(fixture, new FakeConversationAi(), speech, player);
        await conversation.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));
        await conversation.Opening;

        var state = await conversation.ListenAsync(0);
        Assert.True(state.IsPlayingAudio);
        Assert.Equal((NativeSliceFixture.UserA, "opening 1", "Korean"), speech.Requests.Single());
        Assert.Equal([1f], player.Rates);
        player.Finish();
        await WaitForAsync(() => !conversation.State.IsPlayingAudio);

        await conversation.ListenAsync(0);
        state = await conversation.StopAudioAsync();
        Assert.False(state.IsPlayingAudio);
        Assert.False(player.IsPlaying);

        conversation.SetDraft("hi");
        await conversation.SendAsync();
        // User bubbles have no Listen.
        Assert.Equal(2, speech.Requests.Count);
        await conversation.ListenAsync(1);
        Assert.Equal(2, speech.Requests.Count);

        speech.Status = NativeSpeechClipStatus.Offline;
        state = await conversation.ListenAsync(2);
        Assert.False(state.IsPlayingAudio);
        Assert.Equal(NativeConversationText.AudioOffline, Assert.Single(state.Notices).Key);
        speech.Status = NativeSpeechClipStatus.Ready;
        player.Decodes = false;
        state = await conversation.ListenAsync(2);
        Assert.Equal(NativeConversationText.AudioFailed, Assert.Single(state.Notices).Key);
    }

    [Fact]
    public async Task AbsentOrChangedOwner_FailsClosedWithoutRows()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await fixture.SeedAsync(NativeSliceFixture.UserB, wordCount: 1);
        await SignInAsync(fixture, NativeSliceFixture.EmailA);
        var ai = new FakeConversationAi();

        using (var foreign = Presenter(fixture, ai))
            Assert.Equal(NativePhase.Blocked,
                (await foreign.OpenAsync(Launch(fixture, NativeSliceFixture.UserB))).Phase);
        Assert.Empty(ai.Starts);

        using var conversation = Presenter(fixture, ai);
        await conversation.OpenAsync(Launch(fixture, NativeSliceFixture.UserA));
        await conversation.Opening;
        ai.Analysis = [new VocabularyAnalysis { DictionaryForm = "target-native-owner-a-0", UsageCorrect = true }];
        conversation.SetDraft("target-native-owner-a-0");
        ai.OnContinue = fixture.LoseOwner;
        Assert.Equal(NativePhase.Blocked, (await conversation.SendAsync()).Phase);
        Assert.Equal(NativePhase.Blocked, (await conversation.ToggleQuickPhrasesAsync()).Phase);
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserB));
        using var scope = fixture.Scope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .DailyPlanCompletions.AnyAsync(row => row.UserProfileId == NativeSliceFixture.UserB));
    }

    [Fact]
    public async Task ConversationAi_RendersTheSourceScenarioPrompts()
    {
        var gateway = new CapturingGateway();
        var services = new ServiceCollection()
            .AddSingleton<IOwnerBoundNativeAiGateway>(gateway).BuildServiceProvider();
        var ai = new NativeConversationAi(services);
        var scenario = new ConversationScenario
        {
            Name = "Ordering Coffee", PersonaName = "박지영",
            PersonaDescription = "a friendly barista", SituationDescription = "Ordering coffee",
            ConversationType = ConversationType.Finite, QuestionBank = "뭐 드릴까요?"
        };

        await ai.StartAsync("owner", scenario, "Korean", CancellationToken.None);
        // The source system prompt goes as system instructions, the task as the message.
        Assert.Empty(gateway.Prompts);
        var (instructions, start) = gateway.Instructed.Single();
        Assert.StartsWith("You are playing the role of 박지영, a friendly barista.", instructions);
        Assert.Contains("This is a transactional conversation.", instructions);
        Assert.Contains("뭐 드릴까요?", instructions);
        Assert.StartsWith("Start the conversation", start);
        Assert.Contains("naturally in Korean", start);

        await ai.ContinueAsync("owner",
        [
            new ConversationChunk { Author = "박지영", Text = "안녕하세요" },
            new ConversationChunk { Author = "Me", Text = "커피 주세요" },
        ], scenario, "Korean", CancellationToken.None);
        var next = gateway.Prompts.Single();
        Assert.Contains("박지영 said \"안녕하세요\".", next);
        Assert.Contains("Me said \"커피 주세요\".", next);
        Assert.DoesNotContain("뭐 드릴까요?", next);
        Assert.Contains("vocabulary_analysis", next);
        Assert.Equal(["owner", "owner"], gateway.Owners);
    }

    private static NativeConversationPresenter Presenter(NativeSliceFixture fixture,
        INativeConversationAi ai, INativeShadowingSpeech? speech = null,
        INativeShadowingPlayer? player = null) =>
        ActivatorUtilities.CreateInstance<NativeConversationPresenter>(fixture.Provider, ai,
            speech ?? new ScratchShadowingSpeech(), player ?? new ScratchShadowingPlayer());

    private static NativeActivityLaunch Launch(NativeSliceFixture fixture, string owner) =>
        new(Conversation, owner, new DateOnly(2026, 9, 28), null,
            [fixture.ResourceId(owner)], null, [], false, fixture.ResourceId(owner),
            PlanActivityType.Conversation);

    private static async Task SignInAsync(NativeSliceFixture fixture, string email) =>
        Assert.Equal(NativePhase.Ready,
            (await fixture.Auth.SignInAsync(email, "fixture-password")).Phase);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition());
    }

    private sealed class FakeConversationAi : INativeConversationAi
    {
        private int _replies;
        public List<(ConversationScenario? Scenario, string Language)> Starts { get; } = [];
        public List<(List<(string?, string?)> History, string Language)> Continues { get; } = [];
        public bool FailStart { get; set; }
        public bool FailReply { get; set; }
        public string? OpeningText { get; set; }
        public string? ReplyText { get; set; }
        public TaskCompletionSource? HangStart { get; set; }
        public TaskCompletionSource? HangReply { get; set; }
        public List<VocabularyAnalysis>? Analysis { get; set; }
        public Action? OnContinue { get; set; }

        public async Task<string?> StartAsync(string owner, ConversationScenario? scenario,
            string targetLanguage, CancellationToken cancellationToken)
        {
            Starts.Add((scenario, targetLanguage));
            if (HangStart is { } hang)
                await hang.Task; // ignores cancellation on purpose
            if (FailStart)
                throw new HttpRequestException("Synthetic AI offline.");
            return OpeningText ?? $"opening {Starts.Count}";
        }

        public async Task<Reply?> ContinueAsync(string owner, IReadOnlyList<ConversationChunk> chunks,
            ConversationScenario? scenario, string targetLanguage, CancellationToken cancellationToken)
        {
            Continues.Add((chunks.Select(chunk => (chunk.Author, chunk.Text)).ToList(), targetLanguage));
            OnContinue?.Invoke();
            if (HangReply is { } hang)
                await hang.Task;
            if (FailReply)
                throw new HttpRequestException("Synthetic AI offline.");
            return new Reply
            {
                Message = ReplyText ?? $"reply {++_replies}",
                Comprehension = 85,
                GrammarCorrections = [new GrammarCorrectionDto { Original = "wrong", Corrected = "right", Explanation = "why" }],
                VocabularyAnalysis = Analysis
            };
        }
    }

    private sealed class CapturingGateway : IOwnerBoundNativeAiGateway
    {
        public List<string> Prompts { get; } = [];
        public List<string> Owners { get; } = [];
        public List<(string Instructions, string Message)> Instructed { get; } = [];

        public Task<T?> SendWithInstructionsAsync<T>(string owner, string instructions, string message,
            CancellationToken cancellationToken)
        {
            Owners.Add(owner);
            Instructed.Add((instructions, message));
            return Task.FromResult((T?)(object?)"안녕하세요");
        }

        public Task<T?> SendPromptAsync<T>(string owner, string prompt, CancellationToken cancellationToken)
        {
            Owners.Add(owner);
            Prompts.Add(prompt);
            object? result = typeof(T) == typeof(string) ? "안녕하세요" : new Reply { Message = "네" };
            return Task.FromResult((T?)result);
        }
    }
}
