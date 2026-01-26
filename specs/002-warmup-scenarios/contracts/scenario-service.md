# Scenario Service Contract

**Feature**: 002-warmup-scenarios  
**Date**: 2026-01-24

## Overview

Internal service API for managing conversation scenarios. This is not an HTTP API - it's a C# service interface for use within the MAUI application.

## IScenarioService Interface

```csharp
public interface IScenarioService
{
    // ============================================
    // QUERIES
    // ============================================
    
    /// <summary>
    /// Gets all available scenarios (predefined + user-created).
    /// </summary>
    /// <returns>List of scenarios ordered by: predefined first, then by name</returns>
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
    /// <param name="scenario">Scenario to create (IsPredefined will be set to false)</param>
    /// <returns>Created scenario with ID</returns>
    Task<ConversationScenario> CreateScenarioAsync(ConversationScenario scenario);
    
    /// <summary>
    /// Updates an existing user scenario.
    /// </summary>
    /// <param name="scenario">Scenario with updates</param>
    /// <returns>True if updated, false if predefined or not found</returns>
    Task<bool> UpdateScenarioAsync(ConversationScenario scenario);
    
    /// <summary>
    /// Deletes a user scenario.
    /// </summary>
    /// <param name="id">Scenario ID</param>
    /// <returns>True if deleted, false if predefined or not found</returns>
    Task<bool> DeleteScenarioAsync(int id);
    
    // ============================================
    // CONVERSATIONAL CREATION
    // ============================================
    
    /// <summary>
    /// Detects if user message indicates intent to create/edit a scenario.
    /// </summary>
    /// <param name="message">User's message</param>
    /// <returns>Detected intent or None</returns>
    ScenarioIntent DetectScenarioIntent(string message);
    
    /// <summary>
    /// Generates clarifying questions for scenario creation.
    /// </summary>
    /// <param name="partialInfo">Information gathered so far</param>
    /// <returns>Next question to ask, or null if complete</returns>
    Task<string?> GetNextClarificationQuestionAsync(ScenarioCreationState state);
    
    /// <summary>
    /// Parses user response to scenario creation question.
    /// </summary>
    /// <param name="response">User's answer</param>
    /// <param name="state">Current creation state</param>
    /// <returns>Updated state</returns>
    Task<ScenarioCreationState> ParseCreationResponseAsync(string response, ScenarioCreationState state);
    
    /// <summary>
    /// Finalizes scenario creation from gathered information.
    /// </summary>
    /// <param name="state">Completed creation state</param>
    /// <returns>Created scenario</returns>
    Task<ConversationScenario> FinalizeScenarioCreationAsync(ScenarioCreationState state);
    
    // ============================================
    // SEEDING
    // ============================================
    
    /// <summary>
    /// Seeds predefined scenarios if not already present.
    /// Called on app startup / migration.
    /// </summary>
    Task SeedPredefinedScenariosAsync();
}
```

## Supporting Types

### ScenarioIntent Enum

```csharp
public enum ScenarioIntent
{
    /// <summary>No scenario-related intent detected</summary>
    None,
    
    /// <summary>User wants to create a new scenario</summary>
    CreateScenario,
    
    /// <summary>User wants to edit an existing scenario</summary>
    EditScenario,
    
    /// <summary>User wants to delete a scenario</summary>
    DeleteScenario,
    
    /// <summary>User wants to select/switch to a scenario</summary>
    SelectScenario
}
```

### ScenarioCreationState Class

```csharp
public class ScenarioCreationState
{
    public string? Name { get; set; }
    public string? PersonaName { get; set; }
    public string? PersonaDescription { get; set; }
    public string? SituationDescription { get; set; }
    public ConversationType? ConversationType { get; set; }
    public string? QuestionBank { get; set; }
    
    public ScenarioCreationStep CurrentStep { get; set; } = ScenarioCreationStep.AskName;
    
    public bool IsComplete => 
        !string.IsNullOrEmpty(Name) &&
        !string.IsNullOrEmpty(PersonaName) &&
        !string.IsNullOrEmpty(PersonaDescription) &&
        !string.IsNullOrEmpty(SituationDescription) &&
        ConversationType.HasValue;
}

public enum ScenarioCreationStep
{
    AskName,
    AskPersona,
    AskSituation,
    AskConversationType,
    Confirm
}
```

## ConversationService Extensions

### Modified Methods

```csharp
public interface IConversationService
{
    // EXISTING (unchanged)
    Task<Conversation> ResumeConversation();
    Task SaveConversationChunk(ConversationChunk chunk);
    Task<Reply> ContinueConversation(List<ConversationChunk> chunks);
    
    // MODIFIED - now accepts optional scenario
    Task<string> StartConversation(ConversationScenario? scenario = null);
    
    // NEW - starts conversation with specific scenario
    Task<Conversation> StartConversationWithScenario(ConversationScenario scenario);
    
    // NEW - gets the scenario for a conversation
    Task<ConversationScenario?> GetConversationScenarioAsync(int conversationId);
}
```

## Intent Detection Patterns

Keywords/phrases that trigger `ScenarioIntent.CreateScenario`:
- "create a scenario"
- "new scenario"
- "I want to practice"
- "let's practice"
- "can we do"
- "시나리오 만들기"

Keywords/phrases that trigger `ScenarioIntent.EditScenario`:
- "edit scenario"
- "change scenario"
- "modify scenario"
- "update the scenario"
- "시나리오 수정"

Keywords/phrases that trigger `ScenarioIntent.SelectScenario`:
- "switch to"
- "let's do [scenario name]"
- "change to [scenario name]"

## Error Handling

| Operation | Error Condition | Response |
|-----------|-----------------|----------|
| UpdateScenario | IsPredefined = true | Return false, log warning |
| DeleteScenario | IsPredefined = true | Return false, log warning |
| DeleteScenario | Scenario has conversations | Set to null on conversations, then delete |
| CreateScenario | Duplicate name | Allow (user can have multiple scenarios with same name) |
| GetDefaultScenario | No scenarios exist | Seed predefined scenarios, then return First Meeting |

## Testing Contract

### Unit Tests Required

1. `GetAllScenariosAsync_ReturnsPredefinedFirst`
2. `CreateScenarioAsync_SetsIsPredefinedFalse`
3. `UpdateScenarioAsync_RejectsPredefined`
4. `DeleteScenarioAsync_RejectsPredefined`
5. `DetectScenarioIntent_IdentifiesCreateKeywords`
6. `DetectScenarioIntent_IdentifiesEditKeywords`
7. `DetectScenarioIntent_ReturnsNoneForNormalMessages`
8. `SeedPredefinedScenariosAsync_CreatesAllFiveScenarios`
9. `SeedPredefinedScenariosAsync_IdempotentOnRerun`
