using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using SentenceStudio.Api.Conversation;
using SentenceStudio.Contracts;
using SentenceStudio.Contracts.Conversation;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api;

/// <summary>
/// HTTP endpoints for the Conversation activity. Thin wrapper over
/// <see cref="IServerConversationService"/> and <see cref="ScenarioRepository"/>.
/// Mirrors the wire contract documented in spec 004.
/// </summary>
public static class ConversationEndpoints
{
    /// <summary>
    /// Subset of BCP-47 tags we recognize; falls through to the original
    /// value when no mapping matches. Mirrors <c>SpeechEndpoints.Bcp47ToLabel</c>
    /// to keep agent prompts addressable by language name.
    /// </summary>
    private static readonly Dictionary<string, string> Bcp47ToLabel = new(StringComparer.OrdinalIgnoreCase)
    {
        { "en", "English" },
        { "fr", "French" },
        { "de", "German" },
        { "ko", "Korean" },
        { "es", "Spanish" },
    };

    public static WebApplication MapConversationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/conversation").RequireAuthorization();

        group.MapGet("/scenarios", GetScenarios);
        group.MapPost("/start", StartConversation);
        group.MapPost("/continue", ContinueConversation);

        return app;
    }

    private static async Task<IResult> GetScenarios(
        ClaimsPrincipal user,
        [FromServices] ScenarioRepository scenarioRepository,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("ConversationEndpoints");

        var userProfileId = user.FindFirstValue(AuthClaimTypes.UserProfileId);
        if (string.IsNullOrEmpty(userProfileId)) return Results.Unauthorized();

        var scenarios = await scenarioRepository.GetAllAsync();
        var dtos = scenarios.Select(MapScenario).ToList();

        logger.LogInformation("GetScenarios: returning {Count} scenarios for user {UserProfileId}",
            dtos.Count, userProfileId);

        return Results.Ok(dtos);
    }

    private static async Task<IResult> StartConversation(
        ConversationStartRequest request,
        ClaimsPrincipal user,
        [FromServices] IServerConversationService? conversationService,
        [FromServices] ScenarioRepository scenarioRepository,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("ConversationEndpoints");

        var userProfileId = user.FindFirstValue(AuthClaimTypes.UserProfileId);
        if (string.IsNullOrEmpty(userProfileId)) return Results.Unauthorized();

        if (conversationService is null)
        {
            logger.LogWarning("ConversationStart: IServerConversationService not registered (no AI:OpenAI:ApiKey)");
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        ConversationScenario? scenario = null;
        if (request.ScenarioId is int id)
        {
            scenario = await scenarioRepository.GetByIdAsync(id);
            if (scenario is null)
            {
                logger.LogWarning("ConversationStart: scenario {ScenarioId} not found", id);
                return Results.NotFound(new { error = $"Scenario {id} not found." });
            }
        }

        var languageLabel = ResolveLanguageLabel(request.TargetLanguage) ?? "Korean";

        var opening = await conversationService.StartAsync(scenario, languageLabel, cancellationToken);

        return Results.Ok(new ConversationStartResponse
        {
            FirstAssistantMessage = opening,
            PersonaName = scenario?.PersonaName,
            ConversationType = (scenario?.ConversationType ?? ConversationType.OpenEnded).ToString(),
        });
    }

    private static async Task<IResult> ContinueConversation(
        ConversationContinueRequest request,
        ClaimsPrincipal user,
        [FromServices] IServerConversationService? conversationService,
        [FromServices] ScenarioRepository scenarioRepository,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("ConversationEndpoints");

        var userProfileId = user.FindFirstValue(AuthClaimTypes.UserProfileId);
        if (string.IsNullOrEmpty(userProfileId)) return Results.Unauthorized();

        if (conversationService is null)
        {
            logger.LogWarning("ConversationContinue: IServerConversationService not registered (no AI:OpenAI:ApiKey)");
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (request.History is null || request.History.Count == 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["history"] = new[] { "history must contain at least one turn (the user's new message)." },
            });
        }

        var lastTurn = request.History[^1];
        if (!string.Equals(lastTurn.Role, "user", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(lastTurn.Text))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["history"] = new[] { "the last history entry must be a non-empty user turn." },
            });
        }

        ConversationScenario? scenario = null;
        if (request.ScenarioId is int id)
        {
            scenario = await scenarioRepository.GetByIdAsync(id);
            if (scenario is null)
            {
                logger.LogWarning("ConversationContinue: scenario {ScenarioId} not found", id);
                return Results.NotFound(new { error = $"Scenario {id} not found." });
            }
        }

        var languageLabel = ResolveLanguageLabel(request.TargetLanguage) ?? "Korean";

        var userMessage = lastTurn.Text;
        var priorHistory = request.History.Take(request.History.Count - 1).ToList();

        var result = await conversationService.ContinueAsync(
            userMessage,
            priorHistory,
            scenario,
            languageLabel,
            cancellationToken);

        return Results.Ok(result);
    }

    private static ConversationScenarioDto MapScenario(ConversationScenario s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        NameKorean = s.NameKorean,
        PersonaName = s.PersonaName,
        PersonaDescription = s.PersonaDescription,
        SituationDescription = s.SituationDescription,
        ConversationType = s.ConversationType.ToString(),
        QuestionBank = s.QuestionBank,
        IsPredefined = s.IsPredefined,
    };

    private static string? ResolveLanguageLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (Bcp47ToLabel.Values.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            return trimmed;
        var primary = trimmed.Split('-')[0];
        return Bcp47ToLabel.TryGetValue(primary, out var label) ? label : trimmed;
    }
}
