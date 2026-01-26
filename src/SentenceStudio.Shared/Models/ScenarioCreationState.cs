namespace SentenceStudio.Shared.Models;

/// <summary>
/// Tracks the state during conversational scenario creation or editing.
/// </summary>
public class ScenarioCreationState
{
    /// <summary>
    /// The scenario name being created/edited.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// The name of the AI persona.
    /// </summary>
    public string? PersonaName { get; set; }

    /// <summary>
    /// Description of the AI persona role.
    /// </summary>
    public string? PersonaDescription { get; set; }

    /// <summary>
    /// Description of the conversation situation.
    /// </summary>
    public string? SituationDescription { get; set; }

    /// <summary>
    /// Whether the conversation is open-ended or finite.
    /// </summary>
    public ConversationType? ConversationType { get; set; }

    /// <summary>
    /// Optional question bank for the scenario.
    /// </summary>
    public string? QuestionBank { get; set; }

    /// <summary>
    /// The current step in the creation/edit flow.
    /// </summary>
    public ScenarioCreationStep CurrentStep { get; set; } = ScenarioCreationStep.AskName;

    /// <summary>
    /// Whether we are editing an existing scenario (vs creating new).
    /// </summary>
    public bool IsEditing { get; set; }

    /// <summary>
    /// The ID of the scenario being edited (if IsEditing is true).
    /// </summary>
    public int? EditingScenarioId { get; set; }

    /// <summary>
    /// Whether all required fields are filled.
    /// </summary>
    public bool IsComplete =>
        !string.IsNullOrEmpty(Name) &&
        !string.IsNullOrEmpty(PersonaName) &&
        !string.IsNullOrEmpty(PersonaDescription) &&
        !string.IsNullOrEmpty(SituationDescription) &&
        ConversationType.HasValue;

    /// <summary>
    /// Resets the state for a new creation flow.
    /// </summary>
    public void Reset()
    {
        Name = null;
        PersonaName = null;
        PersonaDescription = null;
        SituationDescription = null;
        ConversationType = null;
        QuestionBank = null;
        CurrentStep = ScenarioCreationStep.AskName;
        IsEditing = false;
        EditingScenarioId = null;
    }

    /// <summary>
    /// Initializes state from an existing scenario for editing.
    /// </summary>
    public void InitializeFromScenario(ConversationScenario scenario)
    {
        Name = scenario.Name;
        PersonaName = scenario.PersonaName;
        PersonaDescription = scenario.PersonaDescription;
        SituationDescription = scenario.SituationDescription;
        ConversationType = scenario.ConversationType;
        QuestionBank = scenario.QuestionBank;
        CurrentStep = ScenarioCreationStep.Confirm;
        IsEditing = true;
        EditingScenarioId = scenario.Id;
    }
}

/// <summary>
/// The steps in the scenario creation/edit flow.
/// </summary>
public enum ScenarioCreationStep
{
    /// <summary>Ask for the scenario name.</summary>
    AskName,
    
    /// <summary>Ask for the AI persona details.</summary>
    AskPersona,
    
    /// <summary>Ask for the situation description.</summary>
    AskSituation,
    
    /// <summary>Ask whether it's open-ended or finite.</summary>
    AskConversationType,
    
    /// <summary>Confirm the scenario details before saving.</summary>
    Confirm
}
