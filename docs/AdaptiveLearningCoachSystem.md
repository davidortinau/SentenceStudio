# Adaptive Learning Coach System - Final Design

## Design Principles (Confirmed)

1. **Visible yet subtle adaptation**: When system adapts silently, show a small indicator. User can tap to see reasoning.

2. **Productive struggle vs frustration**: Track difficulty like a "mood" - intervene only when struggle becomes unproductive.

3. **Helpful vs creepy line**: Observations about LEARNING are helpful. Observations about LIFE are surveillance.

---

## Theoretical Foundation

### Flow Theory (Csikszentmihalyi)
The **flow channel** is where challenge matches skill:
- Too easy → boredom
- Too hard → anxiety
- Just right → flow state (deep engagement, time flies)

**Eight dimensions of flow**:
1. Clear goals
2. Immediate feedback
3. Challenge-skill balance ← Key for DDA
4. Merged action and awareness
5. Deep concentration
6. Sense of control
7. Loss of self-consciousness
8. Altered sense of time

### Desirable Difficulty (Bjork)
Struggle that feels hard but leads to success creates stronger memories than easy success.

**Key insight**: The goal is NOT to eliminate difficulty. The goal is to keep difficulty *desirable* (productive) rather than *undesirable* (frustrating).

### When to Intervene (Research-based)

| Signal | Productive Struggle | Unproductive Struggle |
|--------|---------------------|----------------------|
| Progress | Making incremental progress | Stuck, no movement |
| Errors | Trying different strategies | Repeating same mistake |
| Affect | Engaged, determined | Frustrated, giving up |
| Time | Spending time thinking | Staring, disengaged |
| Requests | None or specific questions | "I don't get any of this" |

**Intervention threshold**: When struggle stops producing learning and starts producing frustration, offer a *choice* (not automatic rescue).

---

## The "Difficulty Mood" System

### Concept
Track a rolling **difficulty score** (0.0 - 1.0) during activity:
- 0.0 = Too easy (boredom risk)
- 0.5 = Optimal challenge (flow zone)
- 1.0 = Too hard (frustration risk)

### Calculation Inputs
```
difficulty_score = weighted_average(
    recent_accuracy,           // Last 5-10 questions
    response_time_trend,       // Getting slower = harder
    streak_pattern,            // All wrong = harder
    self_correction_rate,      // Backspacing a lot = uncertain
    skip_rate                  // If skipping allowed
)
```

### Thresholds and Actions

```
┌────────────────────────────────────────────────────────────┐
│                    DIFFICULTY SPECTRUM                      │
├────────────────────────────────────────────────────────────┤
│                                                             │
│  0.0    0.2    0.4    0.6    0.8    1.0                   │
│   │      │      │      │      │      │                     │
│   ▼      ▼      ▼      ▼      ▼      ▼                     │
│  TOO   ┌─────────────────────────┐  TOO                    │
│  EASY  │     FLOW ZONE           │  HARD                   │
│        │   (0.3 - 0.7)           │                         │
│        │   No intervention       │                         │
│        └─────────────────────────┘                         │
│                                                             │
│  < 0.25: "You're crushing this! Want harder?"              │
│  > 0.75 for 3+ questions: "Want to try a different angle?" │
│                                                             │
└────────────────────────────────────────────────────────────┘
```

### The Intervention UX

When threshold crossed, show subtle indicator + optional prompt:

```
┌─────────────────────────────────────────────────────────────┐
│                                                              │
│   Question 14/20                           [💡] ← indicator │
│                                                              │
│   What does "어렵다" mean?                                   │
│                                                              │
│   ┌──────────────────────────────────────────────────────┐  │
│   │  💡 Noticing this section is tricky.                 │  │
│   │                                                       │  │
│   │  These verb conjugations are hard! Want to:          │  │
│   │                                                       │  │
│   │  • Keep going (you're building the muscle)           │  │
│   │  • See a quick hint for this pattern                 │  │
│   │  • Switch to recognition mode for now                │  │
│   │                                                       │  │
│   │              [Keep Pushing]  [Show Options]          │  │
│   └──────────────────────────────────────────────────────┘  │
│                                                              │
└─────────────────────────────────────────────────────────────┘
```

**Key UX principles**:
- Indicator appears first (subtle 💡)
- Tapping reveals reasoning + choices
- "Keep pushing" is always an option
- Reframe struggle positively ("building the muscle")

---

## Silent Adaptations (with Indicator)

For adaptations that don't require choice, show indicator that user can investigate:

### Examples

**Conversation partner simplified vocabulary**:
```
┌─────────────────────────────────────────────────┐
│                                          [🔄]   │
│                                                  │
│  Partner: 오늘 뭐 했어요?                        │
│           (What did you do today?)              │
│                                                  │
│  ─────────────────────────────────────────────  │
│                                                  │
│  [🔄] tapped:                                   │
│  "I used simpler phrasing because the last     │
│   exchange seemed challenging. Let me know     │
│   if you want more complexity!"                │
│                                                  │
└─────────────────────────────────────────────────┘
```

**Quiz distractor difficulty lowered**:
```
[🔄] "I made the wrong answers more obviously wrong 
     to help you focus on the right pattern."
```

**Words reordered by difficulty**:
```
[🔄] "I'm starting with words you know well to 
     build momentum before the harder ones."
```

---

## Conversation Partner Adaptation

### The Style Advisor Agent

Observes conversation and provides guidance to partner:

```
User message analysis:
- Length: Short (3 words)
- Response time: Slow (45 seconds)
- Content: Simple vocabulary used
- Tone: Uncertain ("음...")

Style Advisor output:
{
  "vocabularyLevel": "reduce",
  "sentenceComplexity": "simple",
  "pacing": "patient",
  "encouragement": "subtle",
  "suggestedApproach": "Ask a yes/no question to rebuild confidence"
}
```

Partner's system prompt is dynamically updated:
```
[Previous turn analysis indicates learner is struggling. 
 Use simpler vocabulary. Ask a closed question. 
 Be patient with response time.]
```

### Visible Indicator
Small 🔄 appears when adaptation occurred. Tapping shows:
> "I noticed you're thinking carefully - I'll keep things simple for now. Say '더 어려운 거 해봐요' if you want more challenge!"

---

## Implementation Architecture

### New Components

```
src/SentenceStudio/Services/Agents/
├── Coaching/
│   ├── IDifficultyTracker.cs        // Tracks difficulty mood
│   ├── DifficultyTracker.cs
│   ├── FlowStateAnalyzer.cs         // Determines if in flow
│   ├── ISessionCoach.cs             // Mid-session coaching
│   ├── SessionCoach.cs
│   └── StyleAdvisorAgent.cs         // For conversation
│
├── Planning/
│   ├── SkillDiagnosticianAgent.cs
│   ├── PlanEnhancerAgent.cs
│   └── LearningCoachAgent.cs        // Plan-level personality

src/SentenceStudio/Pages/Controls/
├── AdaptationIndicator.cs           // The 💡 or 🔄 icon
└── CoachSuggestionCard.cs           // The intervention prompt
```

### Integration Points

**VocabularyQuizPage**:
```csharp
// After each answer
await _difficultyTracker.RecordAttempt(wasCorrect, responseTime);

if (_difficultyTracker.ShouldIntervene())
{
    var suggestion = await _sessionCoach.GenerateSuggestionAsync(
        _difficultyTracker.CurrentState,
        State.VocabularyItems.ToList()
    );
    ShowInterventionPrompt(suggestion);
}
```

**WarmupPage (Conversation)**:
```csharp
// Before generating partner response
var styleGuidance = await _styleAdvisor.AnalyzeAndAdvise(
    userMessage,
    conversationHistory,
    _difficultyTracker.CurrentState
);

// Inject into partner's context
response = await _agentService.ContinueConversationAsync(
    userMessage,
    conversationHistory,
    scenario,
    styleGuidance  // NEW parameter
);
```

---

## Phased Implementation

### Phase 1: Difficulty Tracking Infrastructure (3-4 hours)
- [ ] `DifficultyTracker` - rolling score calculation
- [ ] `FlowStateAnalyzer` - threshold detection
- [ ] `AdaptationIndicator` component
- [ ] Integration with VocabularyQuizPage (tracking only, no intervention yet)

### Phase 2: Session Coach + Interventions (4-5 hours)
- [ ] `SessionCoach` agent with prompt
- [ ] `CoachSuggestionCard` component
- [ ] Intervention logic in VocabularyQuizPage
- [ ] Track user responses to suggestions

### Phase 3: Conversation Style Advisor (4-5 hours)
- [ ] `StyleAdvisorAgent` with analysis prompt
- [ ] Integration with ConversationAgentService
- [ ] Partner prompt dynamic injection
- [ ] Silent adaptation indicator in WarmupPage

### Phase 4: Plan-Level Integration (4-5 hours)
- [ ] Connect session difficulty data to plan generation
- [ ] Skill Diagnostician uses real performance data
- [ ] Plan Enhancer considers recent struggle patterns
- [ ] Learning Coach references recent sessions

### Phase 5: Preference Learning (3-4 hours)
- [ ] Track intervention acceptance/rejection
- [ ] Track indicator tap rate (curiosity signal)
- [ ] Build preference profile
- [ ] Adjust intervention frequency per user

---

## Total Estimate: 18-23 hours

---

## Success Metrics

**Quantitative**:
- Session completion rate (don't quit mid-activity)
- Return rate (come back tomorrow)
- Struggle-to-success ratio (overcome challenges)

**Qualitative**:
- "The app gets me" feeling
- Challenges feel achievable, not arbitrary
- Adaptations feel helpful, not patronizing

---

## The Creepy Line (Codified)

### ✅ Acceptable observations (about LEARNING)
- "You study better in the mornings" (learning pattern)
- "Verb conjugations are your challenge area" (skill gap)
- "You're faster at recognition than production" (learning style)
- "This resource matches your level well" (content fit)

### ❌ Unacceptable observations (about LIFE)
- "You usually quit after 12 minutes" (personal habit)
- "You seem tired today" (physical state inference)
- "You check your phone between questions" (behavior surveillance)
- "Your scores drop after lunch" (life pattern correlation)

### The Test
Before surfacing any observation, ask: "Would this feel helpful coming from a tutor, or invasive coming from an app?"
