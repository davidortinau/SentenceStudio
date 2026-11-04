using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using SentenceStudio.WebUI.Services;
using ConversationPage = SentenceStudio.WebUI.Pages.Conversation;

namespace SentenceStudio.UI.Tests.Activities;

/// <summary>
/// Backports B14 (an empty AI reply added an empty bubble; a failed opening or reply was silent
/// because ConversationService swallowed it) and B15 (the opening pasted "SYSTEM INSTRUCTIONS ...
/// TASK ..." into one user message, which Azure's content filter rejects, so the chat opened
/// empty; the persona prompt now goes as the system instructions through
/// /api/v1/ai/chat-messages).
/// </summary>
public sealed class ConversationStartAndReplyTests
{
    private const string StartFailed = "Error starting conversation.";
    private const string ReplyFailed = "Error getting response. Please try again.";

    [Fact]
    public async Task Start_SendsThePersonaPromptAsSystemInstructions_NotPastedIntoTheMessage()
    {
        await using var fixture = await CreateFixtureAsync();
        fixture.Ai.Respond = type => type == typeof(string) ? "안녕하세요!" : null;

        var (_, page) = await fixture.Renderer.RenderAsync<ConversationPage>();

        var call = fixture.Ai.MessageCalls.Should().ContainSingle().Subject;
        call.Instructions.Should().StartWith("Conversation.system.scriban-txt");
        var message = call.Messages.Should().ContainSingle().Subject;
        message.Role.Should().Be("user");
        message.Content.Should().StartWith("StartConversation.scriban-txt")
            .And.NotContain("SYSTEM INSTRUCTIONS").And.NotContain("Conversation.system");
        fixture.Ai.Prompts.Should().BeEmpty("the opening no longer goes as one combined prompt");
        Members.Get<List<ConversationChunk>>(page, "chunks").Should().ContainSingle()
            .Which.Text.Should().Be("안녕하세요!");
    }

    [Fact]
    public async Task Start_WithAScenario_SendsTheScenarioPromptAsSystemInstructions()
    {
        var scenario = new ConversationScenario
        {
            Name = "Cafe",
            PersonaName = "민지",
            PersonaDescription = "a barista",
            SituationDescription = "ordering coffee",
            ConversationType = ConversationType.Finite,
        };
        await using var fixture = await CreateFixtureAsync(scenario);
        fixture.Ai.Respond = type => type == typeof(string) ? "어서 오세요!" : null;

        var (_, page) = await fixture.Renderer.RenderAsync<ConversationPage>();

        var call = fixture.Ai.MessageCalls.Should().ContainSingle().Subject;
        call.Instructions.Should().StartWith("Conversation.scenario.scriban-txt");
        call.Messages.Should().ContainSingle().Which.Content.Should().StartWith("StartConversation.scenario.scriban-txt");
        var opening = Members.Get<List<ConversationChunk>>(page, "chunks").Should().ContainSingle().Subject;
        opening.Text.Should().Be("어서 오세요!");
        opening.Author.Should().Be("민지");
    }

    [Fact]
    public async Task Start_AiFails_TellsTheLearner()
    {
        await using var fixture = await CreateFixtureAsync();
        fixture.Ai.Respond = _ => throw new HttpRequestException("content_filter");

        var (_, page) = await fixture.Renderer.RenderAsync<ConversationPage>();

        fixture.Toasts.Should().Contain(t => t.Text == StartFailed && t.Type == ToastType.Error);
        fixture.Toasts.Should().NotContain(t => t.Text.Contains("content_filter"));
        Members.Get<List<ConversationChunk>>(page, "chunks").Should().BeEmpty();
    }

    [Fact]
    public async Task Start_EmptyOpening_TellsTheLearner_NotAnEmptyChat()
    {
        await using var fixture = await CreateFixtureAsync();
        fixture.Ai.Respond = type => type == typeof(string) ? "" : null;

        var (_, page) = await fixture.Renderer.RenderAsync<ConversationPage>();

        fixture.Toasts.Should().Contain(t => t.Text == StartFailed && t.Type == ToastType.Error);
        Members.Get<List<ConversationChunk>>(page, "chunks").Should().BeEmpty();
    }

    [Fact]
    public async Task Reply_Empty_TellsTheLearnerAndAddsNoEmptyBubble()
    {
        await using var fixture = await CreateFixtureAsync();
        var page = await OpenAsync(fixture);
        fixture.Ai.Respond = type => type == typeof(Reply) ? new Reply { Message = "  " } : null;

        await SendAsync(fixture, page, "커피 주세요.");

        fixture.Toasts.Should().Contain(t => t.Text == ReplyFailed && t.Type == ToastType.Error);
        var chunks = Members.Get<List<ConversationChunk>>(page, "chunks");
        chunks.Should().HaveCount(2, "the opening and the learner's message; no empty reply bubble");
        chunks.Should().NotContain(chunk => string.IsNullOrWhiteSpace(chunk.Text));
        chunks[^1].Role.Should().Be(ConversationRole.User);
    }

    [Fact]
    public async Task Reply_AiFails_TellsTheLearner()
    {
        await using var fixture = await CreateFixtureAsync();
        var page = await OpenAsync(fixture);
        fixture.Ai.Respond = _ => throw new HttpRequestException("AI unavailable");

        await SendAsync(fixture, page, "커피 주세요.");

        fixture.Toasts.Should().Contain(t => t.Text == ReplyFailed && t.Type == ToastType.Error);
        Members.Get<List<ConversationChunk>>(page, "chunks").Should().HaveCount(2);
        Members.Get<bool>(page, "isWaitingForReply").Should().BeFalse();
    }

    [Fact]
    public async Task Reply_ScoresTheOwnersWords()
    {
        await using var fixture = await CreateFixtureAsync();
        var (_, words) = await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("커피", "coffee"));
        await fixture.SeedResourceAsync(ActivityPageFixture.OwnerB, ("주스", "juice"));
        var page = await OpenAsync(fixture);
        fixture.Ai.Respond = type => type == typeof(Reply)
            ? new Reply
            {
                Message = "네, 커피 드릴게요.",
                Comprehension = 90,
                VocabularyAnalysis =
                [
                    new VocabularyAnalysis { DictionaryForm = "커피", UsedForm = "커피", UsageCorrect = true },
                    new VocabularyAnalysis { DictionaryForm = "주스", UsedForm = "주스", UsageCorrect = true },
                ]
            }
            : null;

        await SendAsync(fixture, page, "커피와 주스 주세요.");

        Members.Get<List<ConversationChunk>>(page, "chunks")[^1].Text.Should().Be("네, 커피 드릴게요.");
        var progress = (await fixture.RowsAsync<VocabularyProgress>()).Should().ContainSingle().Subject;
        progress.UserId.Should().Be(ActivityPageFixture.OwnerA);
        progress.VocabularyWordId.Should().Be(words[0].Id);
    }

    [Fact]
    public async Task Reply_InFlightWhenTheConversationRestarts_LandsNowhere()
    {
        await using var fixture = await CreateFixtureAsync();
        var page = await OpenAsync(fixture);
        fixture.Ai.Respond = type => type == typeof(Reply)
            ? new Reply { Message = "늦은 답장" }
            : type == typeof(string) ? "새 대화" : null;
        fixture.Ai.Hold();
        await fixture.Renderer.Dispatcher.InvokeAsync(() => Members.Set(page, "userInput", "안녕하세요"));

        var sending = fixture.Renderer.InvokeAsync(page, "SendMessage");
        await fixture.Ai.Started;
        var restarting = fixture.Renderer.InvokeAsync(page, "ChangeLanguage", "Japanese");
        fixture.Ai.Release();
        await Task.WhenAll(sending, restarting);

        Members.Get<List<ConversationChunk>>(page, "chunks").Should().ContainSingle(
            "the late reply belongs to the conversation that was replaced")
            .Which.Text.Should().Be("새 대화");
    }

    private static async Task<ConversationPage> OpenAsync(ActivityPageFixture fixture)
    {
        fixture.Ai.Respond = type => type == typeof(string) ? "안녕하세요!" : null;
        var (_, page) = await fixture.Renderer.RenderAsync<ConversationPage>();
        Members.Get<List<ConversationChunk>>(page, "chunks").Should().ContainSingle();
        fixture.Toasts.Clear();
        return page;
    }

    private static async Task SendAsync(ActivityPageFixture fixture, ConversationPage page, string text)
    {
        await fixture.Renderer.Dispatcher.InvokeAsync(() => Members.Set(page, "userInput", text));
        await fixture.Renderer.InvokeAsync(page, "SendMessage");
    }

    private static Task<ActivityPageFixture> CreateFixtureAsync(params ConversationScenario[] scenarios) =>
        ActivityPageFixture.CreateAsync(services =>
        {
            services.AddSingleton<ConversationService>();
            services.AddSingleton<SpeechVoicePreferences>();
            services.AddSingleton<IScenarioService>(new FixedScenarios(scenarios.ToList()));
        });

    private sealed class FixedScenarios(List<ConversationScenario> scenarios) : IScenarioService
    {
        public Task<List<ConversationScenario>> GetAllScenariosAsync() => Task.FromResult(scenarios.ToList());
        public Task<ConversationScenario?> GetScenarioAsync(string id) => throw new NotSupportedException();
        public Task<List<ConversationScenario>> GetPredefinedScenariosAsync() => throw new NotSupportedException();
        public Task<List<ConversationScenario>> GetUserScenariosAsync() => throw new NotSupportedException();
        public Task<ConversationScenario> GetDefaultScenarioAsync() => throw new NotSupportedException();
        public Task<ConversationScenario> CreateScenarioAsync(ConversationScenario scenario) => throw new NotSupportedException();
        public Task<bool> UpdateScenarioAsync(ConversationScenario scenario) => throw new NotSupportedException();
        public Task<bool> DeleteScenarioAsync(string id) => throw new NotSupportedException();
        public Task<string?> GetNextClarificationQuestionAsync(ScenarioCreationState state) => throw new NotSupportedException();
        public Task<ScenarioCreationState> ParseCreationResponseAsync(string response, ScenarioCreationState state) => throw new NotSupportedException();
        public Task<ConversationScenario> FinalizeScenarioCreationAsync(ScenarioCreationState state) => throw new NotSupportedException();
        public Task<ConversationScenario?> GetScenarioForEditingAsync(string name) => throw new NotSupportedException();
        public Task SeedPredefinedScenariosAsync() => throw new NotSupportedException();
        public ScenarioIntent DetectScenarioIntent(string message) => throw new NotSupportedException();
    }
}
