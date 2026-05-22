using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;
using System.Text.RegularExpressions;

namespace SentenceStudio.Services;

/// <summary>
/// Service for managing conversation scenarios.
/// </summary>
public class ScenarioService : IScenarioService
{
    private readonly ScenarioRepository _repository;
    private readonly ILogger<ScenarioService> _logger;
    private readonly LocalizationManager _localize;

    // Intent detection patterns
    private static readonly string[] CreatePatterns = {
        "create a scenario", "create scenario", "new scenario",
        "I want to practice", "let's practice", "can we practice",
        "시나리오 만들기", "새 시나리오", "연습하고 싶어"
    };

    private static readonly string[] EditPatterns = {
        "edit scenario", "change scenario", "modify scenario",
        "update scenario", "edit my scenario",
        "시나리오 수정", "시나리오 변경"
    };

    private static readonly string[] DeletePatterns = {
        "delete scenario", "remove scenario", "delete my scenario",
        "시나리오 삭제"
    };

    private static readonly string[] SelectPatterns = {
        "switch to", "let's do", "change to", "use scenario"
    };

    public ScenarioService(ScenarioRepository repository, ILogger<ScenarioService> logger)
    {
        _repository = repository;
        _logger = logger;
        _localize = LocalizationManager.Instance;
    }

    #region Queries

    public async Task<List<ConversationScenario>> GetAllScenariosAsync()
    {
        return await _repository.GetAllAsync();
    }

    public async Task<ConversationScenario?> GetScenarioAsync(int id)
    {
        return await _repository.GetByIdAsync(id);
    }

    public async Task<List<ConversationScenario>> GetPredefinedScenariosAsync()
    {
        return await _repository.GetPredefinedAsync();
    }

    public async Task<List<ConversationScenario>> GetUserScenariosAsync()
    {
        return await _repository.GetUserScenariosAsync();
    }

    public async Task<ConversationScenario> GetDefaultScenarioAsync()
    {
        var defaultScenario = await _repository.GetDefaultAsync();
        if (defaultScenario == null)
        {
            // Seed scenarios and try again
            await SeedPredefinedScenariosAsync();
            defaultScenario = await _repository.GetDefaultAsync();
        }
        return defaultScenario!;
    }

    #endregion

    #region Commands

    public async Task<ConversationScenario> CreateScenarioAsync(ConversationScenario scenario)
    {
        scenario.IsPredefined = false;
        scenario.CreatedAt = DateTime.UtcNow;
        scenario.UpdatedAt = DateTime.UtcNow;

        await _repository.SaveAsync(scenario);
        _logger.LogInformation("Created user scenario: {Name}", scenario.Name);

        return scenario;
    }

    public async Task<bool> UpdateScenarioAsync(ConversationScenario scenario)
    {
        if (scenario.IsPredefined)
        {
            _logger.LogWarning("Attempted to update predefined scenario: {Name}", scenario.Name);
            return false;
        }

        scenario.UpdatedAt = DateTime.UtcNow;
        var result = await _repository.SaveAsync(scenario);
        return result > 0;
    }

    public async Task<bool> DeleteScenarioAsync(int id)
    {
        return await _repository.DeleteAsync(id);
    }

    #endregion

    #region Conversational Creation

    public ScenarioIntent DetectScenarioIntent(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return ScenarioIntent.None;

        var lowerMessage = message.ToLowerInvariant();

        // Check create patterns
        if (CreatePatterns.Any(p => lowerMessage.Contains(p.ToLowerInvariant())))
            return ScenarioIntent.CreateScenario;

        // Check edit patterns
        if (EditPatterns.Any(p => lowerMessage.Contains(p.ToLowerInvariant())))
            return ScenarioIntent.EditScenario;

        // Check delete patterns
        if (DeletePatterns.Any(p => lowerMessage.Contains(p.ToLowerInvariant())))
            return ScenarioIntent.DeleteScenario;

        // Check select patterns
        if (SelectPatterns.Any(p => lowerMessage.Contains(p.ToLowerInvariant())))
            return ScenarioIntent.SelectScenario;

        return ScenarioIntent.None;
    }

    public Task<string?> GetNextClarificationQuestionAsync(ScenarioCreationState state)
    {
        string? question = state.CurrentStep switch
        {
            ScenarioCreationStep.AskName =>
                $"{_localize["ScenarioAskName"]}",
            ScenarioCreationStep.AskPersona =>
                $"{_localize["ScenarioAskPersona"]}",
            ScenarioCreationStep.AskSituation =>
                $"{_localize["ScenarioAskSituation"]}",
            ScenarioCreationStep.AskConversationType =>
                $"{_localize["ScenarioAskType"]}",
            ScenarioCreationStep.Confirm =>
                BuildConfirmationMessage(state),
            _ => null
        };

        return Task.FromResult(question);
    }

    public Task<ScenarioCreationState> ParseCreationResponseAsync(string response, ScenarioCreationState state)
    {
        var trimmedResponse = response.Trim();

        switch (state.CurrentStep)
        {
            case ScenarioCreationStep.AskName:
                state.Name = trimmedResponse;
                state.CurrentStep = ScenarioCreationStep.AskPersona;
                break;

            case ScenarioCreationStep.AskPersona:
                // Try to extract name and description
                // Format could be "Name, description" or "Name - description" or just "Name"
                var parts = Regex.Split(trimmedResponse, @"[,\-–—]");
                if (parts.Length >= 2)
                {
                    state.PersonaName = parts[0].Trim();
                    state.PersonaDescription = string.Join(" ", parts.Skip(1)).Trim();
                }
                else
                {
                    state.PersonaName = trimmedResponse;
                    state.PersonaDescription = trimmedResponse;
                }
                state.CurrentStep = ScenarioCreationStep.AskSituation;
                break;

            case ScenarioCreationStep.AskSituation:
                state.SituationDescription = trimmedResponse;
                state.CurrentStep = ScenarioCreationStep.AskConversationType;
                break;

            case ScenarioCreationStep.AskConversationType:
                var lower = trimmedResponse.ToLowerInvariant();
                if (lower.Contains("finite") || lower.Contains("end") || lower.Contains("완료") || lower.Contains("끝"))
                {
                    state.ConversationType = Shared.Models.ConversationType.Finite;
                }
                else
                {
                    state.ConversationType = Shared.Models.ConversationType.OpenEnded;
                }
                state.CurrentStep = ScenarioCreationStep.Confirm;
                break;

            case ScenarioCreationStep.Confirm:
                // User confirmed - state is ready for finalization
                break;
        }

        return Task.FromResult(state);
    }

    public async Task<ConversationScenario> FinalizeScenarioCreationAsync(ScenarioCreationState state)
    {
        if (!state.IsComplete)
            throw new InvalidOperationException("Scenario creation state is not complete");

        ConversationScenario scenario;

        if (state.IsEditing && state.EditingScenarioId.HasValue)
        {
            // Update existing scenario
            scenario = await _repository.GetByIdAsync(state.EditingScenarioId.Value)
                ?? throw new InvalidOperationException("Scenario not found for editing");

            scenario.Name = state.Name!;
            scenario.PersonaName = state.PersonaName!;
            scenario.PersonaDescription = state.PersonaDescription!;
            scenario.SituationDescription = state.SituationDescription!;
            scenario.ConversationType = state.ConversationType!.Value;
            scenario.QuestionBank = state.QuestionBank;
            scenario.UpdatedAt = DateTime.UtcNow;

            await _repository.SaveAsync(scenario);
            _logger.LogInformation("Updated scenario: {Name}", scenario.Name);
        }
        else
        {
            // Create new scenario
            scenario = new ConversationScenario
            {
                Name = state.Name!,
                PersonaName = state.PersonaName!,
                PersonaDescription = state.PersonaDescription!,
                SituationDescription = state.SituationDescription!,
                ConversationType = state.ConversationType!.Value,
                QuestionBank = state.QuestionBank,
                IsPredefined = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _repository.SaveAsync(scenario);
            _logger.LogInformation("Created new scenario: {Name}", scenario.Name);
        }

        return scenario;
    }

    public async Task<ConversationScenario?> GetScenarioForEditingAsync(string name)
    {
        var scenario = await _repository.FindByNameAsync(name);

        if (scenario?.IsPredefined == true)
        {
            _logger.LogWarning("Attempted to edit predefined scenario: {Name}", scenario.Name);
            return null; // Can't edit predefined scenarios
        }

        return scenario;
    }

    private string BuildConfirmationMessage(ScenarioCreationState state)
    {
        var typeText = state.ConversationType == Shared.Models.ConversationType.Finite
            ? $"{_localize["FiniteConversation"]}"
            : $"{_localize["OpenEndedConversation"]}";

        return string.Format($"{_localize["ScenarioConfirmation"]}",
            state.Name,
            state.PersonaName,
            state.PersonaDescription,
            state.SituationDescription,
            typeText);
    }

    #endregion

    #region Seeding

    public async Task SeedPredefinedScenariosAsync()
    {
        if (await _repository.HasPredefinedScenariosAsync())
        {
            _logger.LogDebug("Predefined scenarios already exist, skipping seed");
            return;
        }

        _logger.LogInformation("Seeding predefined conversation scenarios");

        // Source of truth: SentenceStudio.Data.ConversationScenarioSeedData.GetPredefinedScenarios()
        // Shared with the API server-side seeder so the two never drift.
        var predefinedScenarios = ConversationScenarioSeedData.GetPredefinedScenarios();

        foreach (var scenario in predefinedScenarios)
        {
            await _repository.SaveAsync(scenario);
        }

        _logger.LogInformation("Seeded {Count} predefined scenarios", predefinedScenarios.Count);
    }

    #endregion
}
