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

        var predefinedScenarios = new[]
        {
            new ConversationScenario
            {
                Name = "First Meeting",
                NameKorean = "첫 만남",
                PersonaName = "김철수",
                PersonaDescription = "a 25-year-old drama writer from Seoul",
                SituationDescription = "Getting acquainted with a new person",
                ConversationType = Shared.Models.ConversationType.OpenEnded,
                QuestionBank = "몇 살이에요? 성함이 어떻게 되세요? 생일이 언제예요? 나이가 어떻게 되세요? 무슨 일해요? 어디에 살아요? 어릴 때 뭐가 되고 싶었어요? 취미가 뭐예요? 뭐 좋아해요? 취미가 어떻게 되세요? 왜 한국어 배워요? 오늘 뭐 먹었어요? 지난 주말에 뭐 했어요? 내일 뭐 할 거예요? 어느 나라 여행하고 싶어요? 한 주 동안 뭐 했어요? 한국에 가 봤어요? 한국에 가면 뭐 해 보고 싶어요?",
                IsPredefined = true
            },
            new ConversationScenario
            {
                Name = "Ordering Coffee",
                NameKorean = "커피 주문",
                PersonaName = "박지영",
                PersonaDescription = "a friendly barista at a local cafe",
                SituationDescription = "Ordering coffee and snacks at a Korean cafe",
                ConversationType = Shared.Models.ConversationType.Finite,
                QuestionBank = "",
                IsPredefined = true
            },
            new ConversationScenario
            {
                Name = "Ordering Dinner",
                NameKorean = "저녁 식사 주문",
                PersonaName = "이민호",
                PersonaDescription = "a waiter at a Korean BBQ restaurant",
                SituationDescription = "Ordering food at a Korean BBQ restaurant",
                ConversationType = Shared.Models.ConversationType.Finite,
                QuestionBank = "몇 분이세요? 뭐 드시겠어요? 고기는 어떤 거로 하시겠어요? 반찬 더 필요하세요? 음료는요? 디저트는요? 계산은 어떻게 하시겠어요?",
                IsPredefined = true
            },
            new ConversationScenario
            {
                Name = "Asking for Directions",
                NameKorean = "길 찾기",
                PersonaName = "최수진",
                PersonaDescription = "a helpful stranger on the street",
                SituationDescription = "Asking for directions to a destination",
                ConversationType = Shared.Models.ConversationType.Finite,
                QuestionBank = "어디 가세요? 이 근처 아세요? 지하철역이 어디예요? 버스 정류장이 어디예요? 얼마나 걸려요? 걸어서 갈 수 있어요?",
                IsPredefined = true
            },
            new ConversationScenario
            {
                Name = "Weekend Plans",
                NameKorean = "주말 계획",
                PersonaName = "김하나",
                PersonaDescription = "a curious friend asking about your plans",
                SituationDescription = "Discussing weekend activities and plans with a friend",
                ConversationType = Shared.Models.ConversationType.OpenEnded,
                QuestionBank = "주말에 뭐 해요? 어디 가요? 누구랑 가요? 뭐 먹을 거예요? 영화 볼 거예요? 쇼핑할 거예요? 집에서 쉴 거예요?",
                IsPredefined = true
            }
        };

        foreach (var scenario in predefinedScenarios)
        {
            await _repository.SaveAsync(scenario);
        }

        _logger.LogInformation("Seeded {Count} predefined scenarios", predefinedScenarios.Length);
    }

    #endregion
}
