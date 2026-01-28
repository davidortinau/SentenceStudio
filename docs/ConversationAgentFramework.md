# Conversation Multi-Agent Framework

## Overview

The Warmup (Conversation) feature uses the **Microsoft Agent Framework** to orchestrate multiple AI agents working in parallel. This provides a realistic Korean conversation practice experience with real-time grammar feedback.

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        WarmupPage                                │
│                   (MauiReactor Component)                        │
└──────────────────────────┬──────────────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────────────┐
│                  IConversationAgentService                       │
│               (ConversationAgentService.cs)                      │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│   StartConversationAsync()                                       │
│         │                                                        │
│         ├─► Initialize ConversationMemory                        │
│         ├─► Create Conversation Partner Agent (with tools)       │
│         ├─► Create Grading Agent                                 │
│         └─► Generate opening message                             │
│                                                                  │
│   ContinueConversationAsync()                                    │
│         │                                                        │
│         ├─► ┌────────────────────────────────────┐              │
│         │   │     PARALLEL EXECUTION             │              │
│         │   │                                    │              │
│         │   │  ┌──────────────┐ ┌────────────┐  │              │
│         │   │  │ Conversation │ │  Grading   │  │              │
│         │   │  │   Partner    │ │   Agent    │  │              │
│         │   │  │    Agent     │ │            │  │              │
│         │   │  └──────────────┘ └────────────┘  │              │
│         │   └────────────────────────────────────┘              │
│         │                                                        │
│         └─► Combine results into Reply                           │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

## Components

### 1. Conversation Partner Agent

**Purpose**: Acts as a Korean conversation partner, responding naturally to user input.

**Tools Available**:
- `VocabularyLookupTool` - Can search the user's vocabulary database to reference words they're learning

**System Prompt**: Loaded from `Conversation.scenario.scriban-txt` (scenario-based) or `Conversation.system.scriban-txt` (default)

```csharp
_conversationAgent = _chatClient.AsAIAgent(
    instructions: systemPrompt,
    name: "ConversationPartner",
    tools: [_vocabularyTool.CreateFunction()]);
```

### 2. Grading Agent

**Purpose**: Evaluates the user's Korean input for comprehension and grammar.

**Output**: Structured `GradeResult` with:
- `ComprehensionScore` (0.0 - 1.0)
- `ComprehensionNotes` (feedback text)
- `GrammarCorrections` (list of corrections with explanations)

```csharp
_gradingAgent = _chatClient.AsAIAgent(
    instructions: gradingPrompt,
    name: "GradingAgent");
```

### 3. Conversation Memory

**Purpose**: Maintains context across conversation turns, persisted to SQLite.

**Implements**: `AIContextProvider` from Microsoft.Agents.AI

**Tracks**:
- Conversation topics discussed
- Vocabulary words used
- User's detected proficiency level
- Conversation summary (updated every 5 turns)
- Turn count

```csharp
public sealed class ConversationMemory : AIContextProvider
{
    public override ValueTask<AIContext> InvokingAsync(...) 
    {
        // Injects context before agent runs
    }
    
    public override async ValueTask InvokedAsync(...) 
    {
        // Extracts memories after agent responds
    }
}
```

### 4. Vocabulary Lookup Tool

**Purpose**: Allows the conversation agent to reference the user's vocabulary.

**Methods**:
- `LookupVocabularyAsync(searchTerm, limit)` - Search by Korean or English term
- `SearchByTagAsync(tag, limit)` - Search by category/tag

```csharp
[Description("Look up vocabulary words from the user's learning resources")]
public async Task<VocabularyLookupResult> LookupVocabularyAsync(
    [Description("The Korean or English term to search for")] string searchTerm,
    [Description("Maximum number of results to return")] int limit = 5)
```

## Data Flow

### Starting a Conversation

```
User selects scenario
        │
        ▼
WarmupPage.StartConversationWithScenario()
        │
        ├─► Create new Conversation entity
        ├─► Save to database
        │
        ▼
agentService.StartConversationAsync(scenario)
        │
        ├─► Initialize ConversationMemory
        ├─► Create ConversationPartner agent with VocabularyLookupTool
        ├─► Create GradingAgent
        ├─► Get new AgentThread
        │
        ▼
conversationAgent.RunAsync(openingPrompt)
        │
        ▼
Return opening message (e.g., "안녕하세요! 커피 주문하시겠어요?")
```

### User Sends Message

```
User types Korean message: "네, 아메리카노 주세요"
        │
        ▼
WarmupPage.SendMessage()
        │
        ├─► Save user's chunk to database
        ├─► Add to conversation history
        │
        ▼
WarmupPage.GetReply()
        │
        ▼
agentService.ContinueConversationAsync(userMessage, history, scenario)
        │
        │   ┌─────────────────────────────────────────────┐
        │   │         PARALLEL EXECUTION                  │
        │   │                                             │
        ├─► │  RunConversationAgentAsync() ──────────┐   │
        │   │         │                               │   │
        │   │         ▼                               │   │
        │   │  "사이즈는 어떻게 하시겠어요?"         │   │
        │   │                                         │   │
        ├─► │  RunGradingAgentAsync() ───────────┐   │   │
        │   │         │                           │   │   │
        │   │         ▼                           │   │   │
        │   │  GradeResult {                      │   │   │
        │   │    Score: 0.95,                     │   │   │
        │   │    Notes: "Clear and natural!",    │   │   │
        │   │    Corrections: []                  │   │   │
        │   │  }                                  │   │   │
        │   └─────────────────────────────────────────────┘
        │
        ▼
Combine into Reply:
{
    Message: "사이즈는 어떻게 하시겠어요?",
    Comprehension: 0.95,
    ComprehensionNotes: "Clear and natural!",
    GrammarCorrections: []
}
        │
        ▼
WarmupPage updates UI:
  - Display partner's response
  - Show grammar icon on user's message (if corrections exist)
  - Save memory state to database
```

## User Interaction Examples

### Example 1: Basic Conversation (Ordering Coffee)

**Scenario**: Coffee Shop

```
┌─────────────────────────────────────────────────────────────────┐
│  [Conversation Partner]                                          │
│  안녕하세요! 커피숍에 오신 것을 환영합니다.                      │
│  뭘 드릴까요?                                                    │
│                                                                  │
│  (Hello! Welcome to the coffee shop. What can I get you?)        │
└─────────────────────────────────────────────────────────────────┘

User types: "아메리카노 하나 주세요"

┌─────────────────────────────────────────────────────────────────┐
│  [User]                                             ✓ 95%       │
│  아메리카노 하나 주세요                                          │
│                                                                  │
│  Tap to see: "Clear and polite request! Good use of -주세요"    │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│  [Conversation Partner]                                          │
│  네, 아메리카노 하나요. 사이즈는 어떻게 하시겠어요?              │
│  톨, 그란데, 벤티 있어요.                                        │
│                                                                  │
│  (One Americano. What size would you like? We have tall,         │
│   grande, and venti.)                                            │
└─────────────────────────────────────────────────────────────────┘
```

### Example 2: Grammar Correction

```
User types: "저 이름은 David예요"  (incorrect - should be 제)

┌─────────────────────────────────────────────────────────────────┐
│  [User]                                             ✨ 80%       │
│  저 이름은 David예요                                             │
│                                                                  │
│  ✨ Tap for feedback                                             │
└─────────────────────────────────────────────────────────────────┘

User taps the ✨ icon:

┌─────────────────────────────────────────────────────────────────┐
│  Comprehension Score: 80%                                        │
│                                                                  │
│  The message is understood, but contains a common error.         │
│                                                                  │
│  📝 Grammar Corrections:                                         │
│                                                                  │
│  ❌ 저 이름은                                                    │
│  ✅ 제 이름은                                                    │
│  💡 Use 제 (my) instead of 저 (I) when indicating possession.   │
│     저 is the humble form of "I", while 제 is "my".             │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### Example 3: Vocabulary Tool in Action

The conversation partner can look up words from the user's vocabulary:

```
User's vocabulary database contains: 비빔밥 (bibimbap)

User types: "비빔밥 먹고 싶어요"

Behind the scenes, the agent may call:
  VocabularyLookupTool.LookupVocabularyAsync("비빔밥")
  
Returns:
  {
    TargetTerm: "비빔밥",
    NativeTerm: "bibimbap (mixed rice bowl)",
    Examples: ["오늘 점심은 비빔밥 먹을까요?"]
  }

The agent can now respond with awareness that the user knows this word
and potentially introduce related vocabulary.
```

### Example 4: Memory Persistence

```
Session 1:
  - User discusses food ordering
  - Topics tracked: ["food", "ordering", "restaurant"]
  - Vocabulary used: ["비빔밥", "맛있다", "주문하다"]
  - Proficiency detected: "intermediate"

User closes app, returns next day

Session 2:
  Memory is loaded from database
  
  Agent receives context:
  "Previous conversation context: User practiced ordering food at a restaurant.
   Topics discussed so far: food, ordering, restaurant
   Vocabulary words used in conversation: 비빔밥, 맛있다, 주문하다
   User's detected Korean proficiency level: intermediate
   This is turn 1 of the conversation."
   
  Agent can reference previous topics naturally:
  "지난번에 음식 주문 연습했죠? 오늘은 다른 상황을 해볼까요?"
  (Last time we practiced ordering food, right? Shall we try a different situation today?)
```

## Performance Benefits

### Parallel Execution

The conversation partner and grading agent run **simultaneously**:

```csharp
var conversationTask = RunConversationAgentAsync(contextMessages);
var gradingTask = RunGradingAgentAsync(userMessage, conversationHistory);

await Task.WhenAll(conversationTask, gradingTask);
```

**Result**: User sees the partner's response quickly (~300-500ms), while grading happens in the background. The grading indicator appears shortly after.

### Structured Output

The grading agent uses `GetResponseAsync<GradeResult>` for reliable JSON parsing:

```csharp
var response = await _chatClient.GetResponseAsync<GradeResult>(
    gradingPrompt,
    new ChatOptions { Instructions = gradingInstructions });
```

A `FlexibleStringConverter` handles edge cases where the AI returns unexpected formats.

## Database Schema

### ConversationMemoryState

```sql
CREATE TABLE ConversationMemoryState (
    Id INTEGER PRIMARY KEY,
    ConversationId INTEGER NOT NULL,
    SerializedState TEXT,           -- JSON of ConversationMemoryInfo
    ConversationSummary TEXT,
    DiscussedVocabulary TEXT,       -- Comma-separated words
    DetectedProficiencyLevel TEXT,
    CreatedAt TEXT,
    UpdatedAt TEXT
);
```

### ConversationChunk (Extended)

```sql
-- Existing columns plus:
GrammarCorrectionsJson TEXT,  -- JSON array of corrections
Comprehension REAL,           -- 0.0 to 1.0
ComprehensionNotes TEXT
```

## Service Registration

```csharp
// In ServiceCollectionExtensions.cs
public static IServiceCollection AddConversationAgentServices(this IServiceCollection services)
{
    services.AddSingleton<VocabularyLookupTool>();
    services.AddScoped<IConversationAgentService, ConversationAgentService>();
    return services;
}

// In MauiProgram.cs
services.AddConversationAgentServices();
```

## Key Files

| File | Purpose |
|------|---------|
| `Services/Agents/IConversationAgentService.cs` | Service interface |
| `Services/Agents/ConversationAgentService.cs` | Multi-agent orchestrator |
| `Services/Agents/ConversationMemory.cs` | AIContextProvider for memory |
| `Services/Agents/VocabularyLookupTool.cs` | AI function tool |
| `Shared/Models/GradeResult.cs` | Structured grading output |
| `Shared/Models/GrammarCorrectionDto.cs` | Grammar correction DTO |
| `Shared/Models/ConversationMemoryState.cs` | SQLite entity for memory |
| `Pages/Warmup/WarmupPage.cs` | UI integration |

## Dependencies

- `Microsoft.Extensions.AI` - Core AI abstractions
- `Microsoft.Agents.AI` - Agent framework (AIAgent, AIContextProvider)
- `Scriban` - Template rendering for prompts
