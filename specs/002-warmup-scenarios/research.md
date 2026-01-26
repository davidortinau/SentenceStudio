# Research: Warmup Conversation Scenarios

**Feature**: 002-warmup-scenarios  
**Date**: 2026-01-24

## Research Questions

### 1. How to dynamically inject scenario context into AI prompts?

**Decision**: Use Scriban template with scenario object passed as context variable

**Rationale**: 
- Existing system uses Scriban templates (`Conversation.system.scriban-txt`)
- Templates already receive variables via `template.RenderAsync(new { name = personaName })`
- Can extend to pass full scenario object: `template.RenderAsync(new { scenario = activeScenario })`

**Alternatives Considered**:
- String concatenation: Rejected - harder to maintain, loses template reusability
- Multiple template files per scenario: Rejected - doesn't scale, predefined scenarios would need 5+ files
- JSON configuration: Rejected - adds complexity, Scriban already handles variable injection

**Implementation**:
```scriban
You are playing the role of {{ scenario.PersonaName }}, {{ scenario.PersonaDescription }}.
Situation: {{ scenario.SituationDescription }}
Conversation type: {{ scenario.ConversationType }}
{{ if scenario.ConversationType == "Finite" }}
End the conversation naturally when the task is complete.
{{ else }}
Continue the conversation with follow-up questions.
{{ end }}
```

---

### 2. How to detect "create scenario" intent from user messages?

**Decision**: Pattern matching with AI fallback for ambiguous cases

**Rationale**:
- Users explicitly say things like "I want to create a scenario" or "let's practice something else"
- Simple keyword detection handles 90% of cases
- AI can disambiguate edge cases (is "I want to order coffee" a scenario request or roleplay?)

**Alternatives Considered**:
- Always AI classification: Rejected - adds latency to every message, expensive
- Rigid command syntax (`/create scenario`): Rejected - violates "conversational" requirement
- Modal UI for scenario creation: Rejected - spec explicitly says no forms

**Implementation**:
- Check for intent keywords: "create scenario", "new scenario", "edit scenario", "practice [something]"
- If ambiguous, ask user: "Would you like to start a new scenario about [topic]?"
- State machine: Normal → CreatingScenario → ConfirmingScenario → Normal

---

### 3. How to structure predefined scenarios for easy maintenance?

**Decision**: Seed data in ScenarioService with `IsPredefined = true` flag

**Rationale**:
- Predefined scenarios are part of the app, not user data
- Should be created on first launch / migration
- Flag distinguishes editable (user) vs read-only (predefined)

**Alternatives Considered**:
- JSON resource file: Rejected - requires parsing, loses type safety
- Hard-coded in code: Rejected - harder to extend, mixes data with logic
- Separate database table: Rejected - unnecessary complexity, same entity works

**Implementation**:
```csharp
public async Task SeedPredefinedScenariosAsync()
{
    var predefined = new[]
    {
        new ConversationScenario
        {
            Name = "First Meeting",
            PersonaName = "김철수",
            PersonaDescription = "a 25-year-old drama writer from Seoul",
            SituationDescription = "Getting acquainted with a new person",
            ConversationType = ConversationType.OpenEnded,
            IsPredefined = true,
            QuestionBank = "몇 살이에요? 이름이 뭐예요? ..."
        },
        // ... other scenarios
    };
}
```

---

### 4. How to handle scenario switching mid-conversation?

**Decision**: End current conversation, start new one with new scenario

**Rationale**:
- Spec says: "Current conversation is saved and marked as ended"
- Clean separation - each conversation has one scenario
- User can review past conversations by scenario

**Alternatives Considered**:
- Allow scenario change within same conversation: Rejected - confuses AI context, muddies conversation history
- Require explicit "end conversation" first: Rejected - adds friction, spec says seamless switch

**Implementation**:
1. User selects new scenario
2. If `Conversation.Chunks.Count > 0`, save current conversation
3. Create new `Conversation` with new `ScenarioId`
4. Start with scenario-appropriate greeting

---

### 5. Best practices for finite conversation detection?

**Decision**: AI self-reports completion via structured output + heuristics

**Rationale**:
- AI already returns `Reply` DTO with comprehension scores
- Can add `IsConversationComplete` boolean to Reply
- Heuristics backup: detect "thank you/goodbye" patterns

**Alternatives Considered**:
- Turn count limit: Rejected - too rigid, "ordering coffee" might take 3 or 10 turns
- Keyword-only detection: Rejected - misses nuance (user might say "thank you" but continue)
- User explicit end: Rejected - spec says "naturally conclude"

**Implementation**:
```csharp
public class Reply
{
    public string Message { get; set; }
    public int Comprehension { get; set; }
    public string ComprehensionNotes { get; set; }
    
    [Description("True if the conversation has reached a natural conclusion for this scenario")]
    public bool IsConversationComplete { get; set; }
}
```

---

## Technology Decisions

| Area | Decision | Rationale |
|------|----------|-----------|
| **Scenario Storage** | SQLite via EF Core | Consistent with existing entities (Conversation, UserProfile) |
| **Prompt Templating** | Scriban with scenario context | Already used, supports conditionals |
| **Intent Detection** | Keyword patterns + AI fallback | Balance between speed and accuracy |
| **Predefined Scenarios** | Seed data with IsPredefined flag | Easy maintenance, clear separation |
| **Finite Detection** | Structured AI output | AI already returns DTO, extend it |
| **UI Component** | SfBottomSheet for scenario list | Consistent with existing patterns (phrases popup) |

---

## Open Questions (Resolved)

1. ~~How many predefined scenarios initially?~~ → **5** (spec requirement FR-001)
2. ~~Can users duplicate predefined to custom?~~ → **Yes** (spec edge case)
3. ~~What if AI breaks persona?~~ → **Prompt reinforcement + comprehension tracking** (existing pattern)
