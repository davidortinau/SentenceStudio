namespace SentenceStudio.Shared.Models;

/// <summary>
/// Represents the detected intent from user messages regarding scenario management.
/// </summary>
public enum ScenarioIntent
{
    /// <summary>
    /// No scenario-related intent detected.
    /// </summary>
    None,
    
    /// <summary>
    /// User wants to create a new scenario.
    /// Triggers: "create a scenario", "new scenario", "I want to practice", "let's practice", "시나리오 만들기"
    /// </summary>
    CreateScenario,
    
    /// <summary>
    /// User wants to edit an existing scenario.
    /// Triggers: "edit scenario", "change scenario", "modify scenario", "시나리오 수정"
    /// </summary>
    EditScenario,
    
    /// <summary>
    /// User wants to delete a scenario.
    /// Triggers: "delete scenario", "remove scenario", "시나리오 삭제"
    /// </summary>
    DeleteScenario,
    
    /// <summary>
    /// User wants to select/switch to a scenario.
    /// Triggers: "switch to", "let's do [scenario name]", "change to [scenario name]"
    /// </summary>
    SelectScenario
}
