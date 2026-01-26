# Quickstart: Warmup Conversation Scenarios

**Feature**: 002-warmup-scenarios  
**Date**: 2026-01-24

## Integration Scenarios

This document describes how the conversation scenarios feature integrates with the existing WarmupPage and conversation flow.

---

## Scenario 1: User Selects Predefined Scenario

### Flow

```
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│  WarmupPage     │    │ ScenarioService │    │ ConversationSvc │
│  (opens)        │    │                 │    │                 │
└────────┬────────┘    └────────┬────────┘    └────────┬────────┘
         │                      │                      │
         │ GetAllScenariosAsync │                      │
         │─────────────────────►│                      │
         │◄─────────────────────│                      │
         │  [5 predefined +     │                      │
         │   user scenarios]    │                      │
         │                      │                      │
         │ User taps "Ordering  │                      │
         │ Coffee" scenario     │                      │
         │                      │                      │
         │ StartConversationWithScenario              │
         │────────────────────────────────────────────►│
         │                                             │
         │◄────────────────────────────────────────────│
         │  New Conversation with                      │
         │  ScenarioId set,                           │
         │  AI greets as barista                      │
         │                      │                      │
```

### Code Example

```csharp
// In WarmupPage.cs - Add scenario selection to toolbar
ToolbarItem($"{_localize["ChooseScenario"]}").OnClicked(ShowScenarioSelection),

// Show scenario selection bottom sheet
async void ShowScenarioSelection()
{
    var scenarios = await _scenarioService.GetAllScenariosAsync();
    SetState(s => {
        s.AvailableScenarios = scenarios;
        s.IsScenarioSelectionShown = true;
    });
}

// When user selects a scenario
async Task SelectScenario(ConversationScenario scenario)
{
    SetState(s => s.IsScenarioSelectionShown = false);
    
    // End current conversation if any
    if (_conversation?.Chunks?.Count > 0)
    {
        await _conversationService.SaveConversation(_conversation);
    }
    
    // Start new conversation with scenario
    _conversation = await _conversationService.StartConversationWithScenario(scenario);
    
    SetState(s => {
        s.Chunks.Clear();
        s.ActiveScenario = scenario;
    });
    
    // Get opening line from AI
    await GetReply();
}
```

---

## Scenario 2: AI Adapts to Scenario Context

### Flow

```
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│   WarmupPage    │    │ ConversationSvc │    │   AI (OpenAI)   │
└────────┬────────┘    └────────┬────────┘    └────────┬────────┘
         │                      │                      │
         │ ContinueConversation │                      │
         │ (with chunks)        │                      │
         │─────────────────────►│                      │
         │                      │                      │
         │                      │ GetSystemPromptAsync │
         │                      │ (includes scenario)  │
         │                      │─────────────────────►│
         │                      │                      │
         │                      │ "You are 박지영,    │
         │                      │  a barista..."       │
         │                      │◄─────────────────────│
         │                      │                      │
         │◄─────────────────────│                      │
         │  Reply with barista  │                      │
         │  persona             │                      │
```

### Dynamic Prompt Template

```scriban
{{# Conversation.scenario.scriban-txt #}}
You are playing the role of {{ scenario.persona_name }}, {{ scenario.persona_description }}.

## Situation
{{ scenario.situation_description }}

## Conversation Style
{{ if scenario.conversation_type == "Finite" }}
This is a transactional conversation. Complete the interaction naturally when the task is done.
Typical completion signals: payment confirmed, directions given, order placed.
{{ else }}
This is an open-ended conversation. Keep exploring topics with follow-up questions.
Never end abruptly - always leave room for continuation.
{{ end }}

## Rules:
- Speak naturally in Korean as a native speaker would
- Stay in character as {{ scenario.persona_name }}
- Score your comprehension of the user's last message (0-100)
{{ if scenario.conversation_type == "Finite" }}
- When the conversation reaches its natural conclusion, include is_complete: true in your response
{{ end }}

{{ if scenario.question_bank }}
## Suggested topics/phrases:
{{ scenario.question_bank }}
{{ end }}
```

---

## Scenario 3: User Creates Custom Scenario via Conversation

### Flow

```
User: "I want to create a scenario about buying medicine at a pharmacy"

System: "Great! What should I call this scenario?"
User: "Pharmacy Visit"

System: "Who will you be talking to? (their name and role)"
User: "김약사, a pharmacist"

System: "What's the situation? Describe it briefly."
User: "I have a cold and need to buy medicine"

System: "Should this conversation end when you get your medicine (finite), 
        or continue with follow-up questions (open-ended)?"
User: "Finite"

System: "Got it! I've created 'Pharmacy Visit' scenario:
        - You'll talk to 김약사, a pharmacist
        - Situation: Buying cold medicine
        - Type: Finite (ends when complete)
        Ready to practice? [Start] [Edit] [Cancel]"
```

### State Machine

```
                        ┌────────────────┐
                        │    Normal      │
                        │ Conversation   │
                        └───────┬────────┘
                                │
                   User says "create scenario"
                                │
                        ┌───────▼────────┐
                        │   AskName      │◄──────┐
                        └───────┬────────┘       │
                                │                │
                        ┌───────▼────────┐       │
                        │  AskPersona    │       │
                        └───────┬────────┘       │
                                │            "edit"
                        ┌───────▼────────┐       │
                        │  AskSituation  │       │
                        └───────┬────────┘       │
                                │                │
                        ┌───────▼────────┐       │
                        │ AskConvType    │       │
                        └───────┬────────┘       │
                                │                │
                        ┌───────▼────────┐       │
                        │    Confirm     │───────┘
                        └───────┬────────┘
                                │
                           "confirm"
                                │
                        ┌───────▼────────┐
                        │ Start New Conv │
                        │ with Scenario  │
                        └────────────────┘
```

---

## Scenario 4: Finite Conversation Completion

### Flow

```
Scenario: "Ordering Coffee" (Finite)

AI: "안녕하세요! 무엇을 드릴까요?" (Welcome! What can I get you?)
User: "아메리카노 한 잔 주세요." (One Americano please)
AI: "네, 아이스로 드릴까요, 핫으로 드릴까요?" (Ice or hot?)
User: "핫으로 주세요." (Hot please)
AI: "핫 아메리카노 한 잔 4,500원입니다. 카드로 결제하시겠어요?"
    (One hot Americano is 4,500 won. Card payment?)
User: "네, 카드로요." (Yes, card)
AI: "결제 완료됐습니다. 잠시만 기다려주세요!" 
    (Payment complete. Please wait!)
    { is_complete: true }

System: [Conversation Complete] 
        "Great job! You successfully ordered coffee in 4 exchanges.
         [Start New] [Try Again] [Different Scenario]"
```

### Code Example

```csharp
async Task GetReply()
{
    // ... existing code ...
    
    Reply response = await _conversationService.ContinueConversation(State.Chunks.ToList());
    
    // Check if finite conversation is complete
    if (response.IsConversationComplete && State.ActiveScenario?.ConversationType == ConversationType.Finite)
    {
        SetState(s => s.IsConversationComplete = true);
        ShowCompletionDialog(response);
    }
    
    // ... rest of existing code ...
}
```

---

## UI Components

### Scenario Selection Bottom Sheet

```csharp
VisualNode RenderScenarioSelectionSheet() =>
    new SfBottomSheet(
        Grid("Auto,*,Auto", "*",
            Label($"{_localize["ChooseScenario"]}")
                .ThemeKey(MyTheme.Title2)
                .GridRow(0),
            
            CollectionView()
                .ItemsSource(State.AvailableScenarios)
                .ItemTemplate(scenario => RenderScenarioItem(scenario))
                .GridRow(1),
            
            Button($"{_localize["CreateNewScenario"]}")
                .ThemeKey(MyTheme.Secondary)
                .OnClicked(StartScenarioCreation)
                .GridRow(2)
        )
        .Padding(MyTheme.LayoutPadding)
    )
    .IsOpen(State.IsScenarioSelectionShown);

VisualNode RenderScenarioItem(ConversationScenario scenario) =>
    Border(
        VStack(spacing: MyTheme.MicroSpacing,
            HStack(
                Label(scenario.Name).ThemeKey(MyTheme.Body1Strong),
                scenario.IsPredefined 
                    ? Label("📌").FontSize(12) 
                    : null
            ),
            Label(scenario.SituationDescription)
                .ThemeKey(MyTheme.Caption1)
                .LineBreakMode(LineBreakMode.TailTruncation),
            Label(scenario.ConversationType == ConversationType.Finite 
                ? $"{_localize["FiniteConversation"]}" 
                : $"{_localize["OpenEndedConversation"]}")
                .ThemeKey(MyTheme.Caption2)
        )
    )
    .ThemeKey(MyTheme.CardStyle)
    .OnTapped(() => SelectScenario(scenario));
```

---

## Localization Keys Required

| Key | English | Korean |
|-----|---------|--------|
| `ChooseScenario` | Choose Scenario | 시나리오 선택 |
| `CreateNewScenario` | Create New Scenario | 새 시나리오 만들기 |
| `FiniteConversation` | Ends when complete | 완료 시 종료 |
| `OpenEndedConversation` | Continues indefinitely | 계속 진행 |
| `ScenarioCreated` | Scenario created! | 시나리오가 생성되었습니다! |
| `ConversationComplete` | Conversation Complete | 대화 완료 |
| `WhatToCallScenario` | What should I call this scenario? | 이 시나리오의 이름은 무엇인가요? |
| `WhoWillYouTalkTo` | Who will you be talking to? | 누구와 대화하시겠습니까? |
| `WhatsSituation` | What's the situation? | 상황이 무엇인가요? |
| `FiniteOrOpenEnded` | Should this end when complete? | 완료 시 종료해야 하나요? |
