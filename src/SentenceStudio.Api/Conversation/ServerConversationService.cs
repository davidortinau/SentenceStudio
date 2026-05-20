using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Conversation;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Conversation;

/// <summary>
/// HTTP-shaped, stateless implementation of <see cref="IServerConversationService"/>.
/// Builds the conversation-partner system prompt and grading instructions
/// inline (port of the four scriban templates the MAUI agent service loads
/// from <c>Resources/Raw</c>) so the API does not need a MAUI file-system
/// package source.
/// </summary>
public sealed class ServerConversationService : IServerConversationService
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<ServerConversationService> _logger;

    public ServerConversationService(
        IChatClient chatClient,
        ILogger<ServerConversationService> logger)
    {
        _chatClient = chatClient;
        _logger = logger;
    }

    public async Task<string> StartAsync(
        ConversationScenario? scenario,
        string targetLanguageLabel,
        CancellationToken cancellationToken)
    {
        var systemPrompt = BuildConversationPartnerPrompt(scenario, targetLanguageLabel);
        var openingPrompt = BuildOpeningPrompt(scenario, targetLanguageLabel);

        _logger.LogInformation(
            "ConversationStart: scenario={ScenarioName} language={Language}",
            scenario?.Name ?? "(default)", targetLanguageLabel);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, openingPrompt),
        };

        var response = await _chatClient.GetResponseAsync(
            messages,
            new ChatOptions { Instructions = systemPrompt },
            cancellationToken);

        return (response.Text ?? string.Empty).Trim();
    }

    public async Task<ConversationContinueResponse> ContinueAsync(
        string userMessage,
        IReadOnlyList<ConversationHistoryItemDto> conversationHistory,
        ConversationScenario? scenario,
        string targetLanguageLabel,
        CancellationToken cancellationToken)
    {
        var systemPrompt = BuildConversationPartnerPrompt(scenario, targetLanguageLabel);
        var gradingInstructions = BuildGradingInstructions(targetLanguageLabel);

        // Build partner context: replay prior history (oldest -> newest), then the user's new turn.
        var partnerMessages = new List<ChatMessage>(conversationHistory.Count + 1);
        foreach (var item in conversationHistory)
        {
            var role = string.Equals(item.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? ChatRole.Assistant
                : ChatRole.User;
            partnerMessages.Add(new ChatMessage(role, item.Text ?? string.Empty));
        }

        partnerMessages.Add(new ChatMessage(ChatRole.User, userMessage));

        var gradingPrompt = BuildGradingPrompt(userMessage, conversationHistory);

        // Run partner + grader in parallel — they don't share state.
        var partnerTask = _chatClient.GetResponseAsync(
            partnerMessages,
            new ChatOptions { Instructions = systemPrompt },
            cancellationToken);

        var gradingTask = _chatClient.GetResponseAsync<GradeResult>(
            gradingPrompt,
            new ChatOptions { Instructions = gradingInstructions },
            cancellationToken: cancellationToken);

        await Task.WhenAll(partnerTask, gradingTask);

        var partnerText = ((await partnerTask).Text ?? string.Empty).Trim();
        var grade = (await gradingTask)?.Result ?? new GradeResult { ComprehensionScore = 0.5 };

        return new ConversationContinueResponse
        {
            AssistantMessage = partnerText,
            ComprehensionScore = NormalizeComprehensionScore(grade.ComprehensionScore),
            ComprehensionNotes = grade.ComprehensionNotes,
            GrammarCorrections = (grade.GrammarCorrections ?? new())
                .Select(g => new ConversationGrammarCorrectionDto
                {
                    Original = g.Original ?? string.Empty,
                    Corrected = g.Corrected ?? string.Empty,
                    Explanation = g.Explanation ?? string.Empty,
                })
                .ToList(),
            // v1: vocabulary analysis is not produced by the grader prompt yet.
            // Spec defers vocabulary scoring write-back; emit an empty array
            // so the Flutter parser sees a stable shape.
            VocabularyAnalysis = new List<ConversationVocabularyAnalysisDto>(),
            // v1: isComplete derivation is out of scope (spec §Behavior). The
            // Flutter UI handles `false` gracefully.
            IsComplete = false,
        };
    }

    /// <summary>
    /// Normalizes a grader score into [0, 100]. The MAUI service treats the
    /// score as 0.0-1.0, but at least one scenario template prompts the model
    /// for 0-100; we accept either by detecting the magnitude.
    /// </summary>
    public static int NormalizeComprehensionScore(double raw)
    {
        if (double.IsNaN(raw) || double.IsInfinity(raw)) return 0;
        var asPercent = raw <= 1.0 ? raw * 100.0 : raw;
        var rounded = (int)Math.Round(asPercent, MidpointRounding.AwayFromZero);
        if (rounded < 0) return 0;
        if (rounded > 100) return 100;
        return rounded;
    }

    private static string BuildConversationPartnerPrompt(
        ConversationScenario? scenario,
        string targetLanguage)
    {
        if (scenario is null)
        {
            return $@"You are playing the role of a friendly {targetLanguage} native speaker.

## Your Role
You are meeting me for conversation practice. Make up your backstory as needed to answer my questions naturally.

## Conversation Style
This is an open-ended conversation. Keep exploring topics with follow-up questions.
Never end abruptly - always leave room for continuation.

## Rules:
- Speak naturally in {targetLanguage} as a native speaker would
- Keep responses conversational and friendly
- If the user's message is hard to understand or could be phrased more clearly, ask for clarification or confirm your understanding
- If the user asks a question, respond to it before asking a new question
- In your response, only include your newest reply in {targetLanguage} - do not repeat your character name";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"You are playing the role of {scenario.PersonaName}, {scenario.PersonaDescription}.");
        sb.AppendLine();
        sb.AppendLine("## Situation");
        sb.AppendLine(scenario.SituationDescription);
        sb.AppendLine();
        sb.AppendLine("## Conversation Style");
        if (scenario.ConversationType == ConversationType.Finite)
        {
            sb.AppendLine("This is a transactional conversation. Complete the interaction naturally when the task is done.");
            sb.AppendLine("When the conversation reaches its natural conclusion (e.g., payment complete, directions given, order placed), wrap up gracefully.");
        }
        else
        {
            sb.AppendLine("This is an open-ended conversation. Keep exploring topics with follow-up questions.");
            sb.AppendLine("Never end abruptly - always leave room for continuation.");
        }
        sb.AppendLine();
        sb.AppendLine("## Rules:");
        sb.AppendLine($"- Speak naturally in {targetLanguage} as a native speaker would");
        sb.AppendLine($"- Stay in character as {scenario.PersonaName}");
        sb.AppendLine("- Keep responses conversational and friendly");
        sb.AppendLine("- If the user's message is hard to understand or could be phrased more clearly, ask for clarification or confirm your understanding");
        sb.AppendLine("- If the user asks a question, respond to it before asking a new question");
        sb.AppendLine($"- In your response, only include your newest reply in {targetLanguage} - do not repeat your character name");

        if (!string.IsNullOrWhiteSpace(scenario.QuestionBank))
        {
            sb.AppendLine();
            sb.AppendLine("## Suggested topics/phrases (work these into the conversation naturally):");
            sb.AppendLine(scenario.QuestionBank);
        }

        return sb.ToString();
    }

    private static string BuildOpeningPrompt(
        ConversationScenario? scenario,
        string targetLanguage)
    {
        if (scenario is null)
        {
            return $"You will play the role of a friendly {targetLanguage} native speaker who I am meeting for conversation practice. " +
                   "Make up your backstory as needed to answer my questions naturally. " +
                   $"Let's have a conversation in {targetLanguage}. Start by greeting me and asking for my name in a natural, friendly way.";
        }

        var styleLine = scenario.ConversationType == ConversationType.Finite
            ? $"This is a transactional conversation, so ask something relevant to completing the transaction in {targetLanguage}."
            : $"This is an open-ended conversation, so ask something to get to know them or explore the topic in {targetLanguage}.";

        return
            "Start the conversation with an appropriate greeting for this scenario.\n\n" +
            $"As {scenario.PersonaName}, {scenario.PersonaDescription}, begin by greeting the customer/person naturally in {targetLanguage} and asking an appropriate opening question for this situation:\n\n" +
            $"Situation: {scenario.SituationDescription}\n\n" +
            styleLine + "\n\n" +
            $"Only respond with your greeting and question in {targetLanguage} - nothing else.";
    }

    private static string BuildGradingInstructions(string targetLanguage)
    {
        return $@"You are a {targetLanguage} language grading assistant. Your job is to:

1. Evaluate the user's {targetLanguage} message for comprehension (0.0 to 1.0 scale):
   - 1.0: Perfect, native-like communication
   - 0.8-0.9: Minor errors but message is clear
   - 0.6-0.7: Understandable with some effort
   - 0.4-0.5: Partially understandable
   - 0.2-0.3: Difficult to understand
   - 0.0-0.1: Not understandable

2. Provide brief comprehension notes explaining what worked well or what was unclear.

3. Identify grammar corrections:
   - Find grammar mistakes in the user's {targetLanguage}
   - Provide the corrected version
   - Explain the grammar rule simply

Focus on being helpful and encouraging while providing accurate corrections.
Only identify significant errors that affect meaning or are common learner mistakes.
Do not nitpick minor stylistic preferences.";
    }

    private static string BuildGradingPrompt(
        string userMessage,
        IReadOnlyList<ConversationHistoryItemDto> history)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Conversation context:");
        foreach (var chunk in history.TakeLast(6))
        {
            var speaker = string.Equals(chunk.Role, "user", StringComparison.OrdinalIgnoreCase)
                ? "User"
                : "Partner";
            sb.AppendLine($"{speaker}: {chunk.Text}");
        }

        sb.AppendLine();
        sb.AppendLine($"User's latest message to grade: {userMessage}");
        return sb.ToString();
    }
}
