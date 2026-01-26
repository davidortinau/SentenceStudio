using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

/// <summary>
/// Service interface for managing conversation scenarios.
/// </summary>
public interface IScenarioService
{
    // ============================================
    // QUERIES
    // ============================================

    /// <summary>
    /// Gets all available scenarios (predefined + user-created).
    /// </summary>
    Task<List<ConversationScenario>> GetAllScenariosAsync();

    /// <summary>
    /// Gets a scenario by ID.
    /// </summary>
    Task<ConversationScenario?> GetScenarioAsync(int id);

    /// <summary>
    /// Gets predefined scenarios only.
    /// </summary>
    Task<List<ConversationScenario>> GetPredefinedScenariosAsync();

    /// <summary>
    /// Gets user-created scenarios only.
    /// </summary>
    Task<List<ConversationScenario>> GetUserScenariosAsync();

    /// <summary>
    /// Gets the default scenario (First Meeting).
    /// </summary>
    Task<ConversationScenario> GetDefaultScenarioAsync();

    // ============================================
    // COMMANDS
    // ============================================

    /// <summary>
    /// Creates a new user scenario.
    /// </summary>
    Task<ConversationScenario> CreateScenarioAsync(ConversationScenario scenario);

    /// <summary>
    /// Updates an existing user scenario.
    /// </summary>
    Task<bool> UpdateScenarioAsync(ConversationScenario scenario);

    /// <summary>
    /// Deletes a user scenario.
    /// </summary>
    Task<bool> DeleteScenarioAsync(int id);

    // ============================================
    // CONVERSATIONAL CREATION
    // ============================================

    /// <summary>
    /// Detects if user message indicates intent to create/edit a scenario.
    /// </summary>
    ScenarioIntent DetectScenarioIntent(string message);

    /// <summary>
    /// Generates the next clarifying question for scenario creation.
    /// </summary>
    Task<string?> GetNextClarificationQuestionAsync(ScenarioCreationState state);

    /// <summary>
    /// Parses user response to scenario creation question.
    /// </summary>
    Task<ScenarioCreationState> ParseCreationResponseAsync(string response, ScenarioCreationState state);

    /// <summary>
    /// Finalizes scenario creation from gathered information.
    /// </summary>
    Task<ConversationScenario> FinalizeScenarioCreationAsync(ScenarioCreationState state);

    /// <summary>
    /// Gets a scenario by name for editing.
    /// </summary>
    Task<ConversationScenario?> GetScenarioForEditingAsync(string name);

    // ============================================
    // SEEDING
    // ============================================

    /// <summary>
    /// Seeds predefined scenarios if not already present.
    /// </summary>
    Task SeedPredefinedScenariosAsync();
}
