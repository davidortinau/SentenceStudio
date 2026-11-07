#pragma warning disable MEAI001 // IRealtimeClient is experimental in Microsoft.Extensions.AI.

using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Ai;
using SentenceStudio.Services.Realtime;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

/// <summary>A realtime client secret minted by the API for the signed-in owner.</summary>
public interface IOwnerBoundNativeRealtimeSecrets
{
    Task<RealtimeClientSecretResponse> CreateRealtimeClientSecretAsync(
        string owner, CancellationToken cancellationToken);
}

/// <summary>
/// Voice mode's session (the Techorama demo's ConversationVoiceSession): the API mints a
/// short-lived client secret for the owner, then the device opens the realtime websocket with it.
/// The instructions come from the same scenario, language and recent turns as the text chat; the
/// vocabulary tool reads only the owner's words. Nothing here writes learner data.
/// </summary>
public sealed class NativeConversationVoiceService(
    IOwnerBoundNativeRealtimeSecrets secrets,
    IServiceProvider services,
    ILoggerFactory loggers) : INativeConversationVoice
{
    /// <summary>Turns of the thread the partner sees when voice starts.</summary>
    public const int RecentTurnLimit = 8;

    public async Task<IConversationVoiceSession> StartAsync(
        string owner, NativeConversationVoiceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var instructions = await InstructionsAsync(request, cancellationToken);
        var secret = await secrets.CreateRealtimeClientSecretAsync(owner, cancellationToken);
        var credential = new RealtimeCredential(secret.ClientSecret, new Uri(secret.Endpoint), secret.Model);
        var client = RealtimeVoiceClients.Create(credential, services);
        // The session owns the client from here, including when it cannot open.
        return await ConversationVoiceSession.StartAsync(
            client,
            instructions,
            request.TargetLanguage,
            secret.TranscriptionModel,
            request.Vocabulary,
            loggers.CreateLogger<ConversationVoiceSession>(),
            cancellationToken,
            disposeClient: true,
            pace: request.Pace);
    }

    /// <summary>Renders Conversation.voice.scriban-txt with the model the Techorama page used.</summary>
    internal static Task<string> InstructionsAsync(
        NativeConversationVoiceRequest request, CancellationToken cancellationToken)
    {
        var scenario = request.Scenario;
        var persona = string.IsNullOrWhiteSpace(scenario?.PersonaName)
            ? NativeConversationAi.DefaultPersonaName : scenario.PersonaName;
        var recentTurns = string.Join("\n", request.RecentTurns
            .Where(turn => !string.IsNullOrWhiteSpace(turn.Text))
            .TakeLast(RecentTurnLimit)
            .Select(turn => $"{(turn.Role == ConversationRole.User ? "Learner" : persona)}: {turn.Text}"));
        return NativeConversationAi.RenderAsync("Conversation.voice.scriban-txt", new
        {
            scenario = new
            {
                persona_name = persona,
                persona_description = scenario?.PersonaDescription ?? "a friendly conversation partner",
                situation_description = scenario?.SituationDescription
                    ?? "A casual conversation to practise speaking.",
                conversation_type = (scenario?.ConversationType ?? ConversationType.OpenEnded).ToString(),
                question_bank = scenario?.QuestionBank
            },
            target_language = request.TargetLanguage,
            // Scriban treats "" as true, so no turns must be null to leave the section out.
            recent_turns = recentTurns.Length == 0 ? null : recentTurns
        }, cancellationToken);
    }
}
