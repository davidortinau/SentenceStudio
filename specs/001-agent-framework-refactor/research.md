# Research: Microsoft Agent Framework Migration

**Feature**: 001-agent-framework-refactor  
**Date**: 2026-01-27

## Executive Summary

Migration from Microsoft.Extensions.AI to Microsoft Agent Framework is feasible and recommended. The Agent Framework builds on top of `Microsoft.Extensions.AI.Abstractions` and provides a clean upgrade path with `ChatClientAgent` as the direct replacement for `IChatClient` usage patterns.

---

## Key Decisions

### Decision 1: Primary Agent Type Selection

**Decision**: Use `ChatClientAgent` as the primary agent type for all AI services.

**Rationale**: 
- `ChatClientAgent` wraps `IChatClient` and provides equivalent functionality
- Supports structured output via `RunAsync<T>()` (direct replacement for `GetResponseAsync<T>()`)
- Maintains compatibility with existing OpenAI client setup
- Does not require server-side agent storage (unlike `PersistentAgentsClient`)

**Alternatives Considered**:
| Option | Why Rejected |
|--------|--------------|
| `OpenAIAssistantAgent` | Requires OpenAI Assistants API, overkill for simple completions |
| `AzureAIAgent` | Requires Azure AI Foundry, adds unnecessary Azure dependency |
| Raw `IChatClient` | Still supported but doesn't leverage Agent Framework benefits |

### Decision 2: NuGet Package Selection

**Decision**: Replace current packages with Agent Framework equivalents.

**Current Packages**:
```xml
<PackageReference Include="Microsoft.Extensions.AI" Version="10.2.0" />
<PackageReference Include="Microsoft.Extensions.AI.OpenAI" Version="10.2.0-preview.1.26063.2" />
```

**New Packages**:
```xml
<PackageReference Include="Microsoft.Agents.AI" Version="X.X.X-preview" />
<PackageReference Include="Microsoft.Agents.AI.OpenAI" Version="X.X.X-preview" />
```

**Rationale**: 
- `Microsoft.Agents.AI` provides `ChatClientAgent` and structured output support
- `Microsoft.Agents.AI.OpenAI` provides OpenAI client integration
- Both packages build on `Microsoft.Extensions.AI.Abstractions` (shared types)

**Alternatives Considered**:
| Option | Why Rejected |
|--------|--------------|
| Semantic Kernel | More complex, Agent Framework is successor |
| Keep Microsoft.Extensions.AI | Missing agentic features, eventual deprecation |

### Decision 3: Structured Output Migration Pattern

**Decision**: Use `AgentResponse<T>` with `RunAsync<T>()` method.

**Current Pattern**:
```csharp
IChatClient client = new OpenAIClient(apiKey).GetChatClient("gpt-4o-mini").AsIChatClient();
var response = await client.GetResponseAsync<TranslationResponse>(prompt);
var result = response.Result;
```

**New Pattern**:
```csharp
ChatClientAgent agent = new ChatClientAgent(chatClient, options);
AgentResponse<TranslationResponse> response = await agent.RunAsync<TranslationResponse>(prompt);
var result = response.Result;
```

**Rationale**:
- Maintains same DTO pattern with `[Description]` attributes
- JSON schema automatically generated from type
- Similar API surface minimizes refactoring effort

### Decision 4: Dependency Injection Strategy

**Decision**: Register `IChatClient` as singleton, create `ChatClientAgent` instances per-service or per-request.

**New DI Registration**:
```csharp
// MauiProgram.cs
builder.Services.AddSingleton<IChatClient>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var apiKey = config.GetRequiredSection("Settings").Get<Settings>().OpenAIKey;
    return new AzureOpenAIClient(new Uri(endpoint), new ApiKeyCredential(apiKey))
        .GetChatClient("gpt-4o-mini")
        .AsIChatClient();
});

// Or for direct OpenAI:
builder.Services.AddSingleton<IChatClient>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var apiKey = config.GetRequiredSection("Settings").Get<Settings>().OpenAIKey;
    return new OpenAIClient(apiKey)
        .GetChatClient("gpt-4o-mini")
        .AsIChatClient();
});
```

**Rationale**:
- `IChatClient` is thread-safe and stateless (singleton appropriate)
- `ChatClientAgent` can be created per-service with specific instructions/tools
- Allows different agents with different system prompts per service

### Decision 5: Audio and Image Generation

**Decision**: Keep direct OpenAI SDK usage for audio (`AudioClient`) and image (`ImageClient`).

**Rationale**:
- Agent Framework focuses on chat/reasoning, not audio/image generation
- `OpenAI.Audio.AudioClient` and `OpenAI.Images.ImageClient` are already optimal
- ElevenLabs SDK for TTS remains unchanged

**No changes needed for**:
- `AiService.TextToSpeechAsync()` - uses `AIClient` (ElevenLabs)
- `SceneImageService` - uses `ImageClient` (OpenAI)

---

## Migration Impact Analysis

### High Impact (Core Refactoring)

| Service | Current Usage | Migration Complexity |
|---------|---------------|---------------------|
| `AiService` | `IChatClient.GetResponseAsync<T>()` | Medium - Central service, affects all callers |
| `TranslationService` | Direct `IChatClient` creation | Medium - Creates own client instance |
| `ClozureService` | Direct `IChatClient` creation | Medium - Creates own client instance |
| `ConversationService` | Multi-turn chat with history | High - Requires `AgentThread` for history |

### Medium Impact (Straightforward Replacement)

| Service | Current Usage | Migration Complexity |
|---------|---------------|---------------------|
| `ShadowingService` | Uses `AiService` | Low - Indirect via AiService |
| `LlmPlanGenerationService` | Injected `IChatClient` | Low - Just change type |

### No Impact (Unchanged)

| Component | Reason |
|-----------|--------|
| `ElevenLabsSpeechService` | Uses ElevenLabs SDK, not Microsoft.Extensions.AI |
| `SceneImageService` | Uses OpenAI.Images.ImageClient directly |
| All prompt templates | Scriban templates work with any AI service |
| All DTOs | `[Description]` attributes work with Agent Framework |

---

## ConversationService Special Consideration

The `ConversationService` currently manages multi-turn conversations with `ChatHistory`. Agent Framework provides `AgentThread` for this purpose.

**Current Pattern**:
```csharp
private List<ChatMessage> _messages = new();

public async Task<string> SendMessage(string userMessage)
{
    _messages.Add(new ChatMessage(ChatRole.User, userMessage));
    var response = await _client.GetResponseAsync<string>(_messages);
    _messages.Add(new ChatMessage(ChatRole.Assistant, response.Result));
    return response.Result;
}
```

**New Pattern with AgentThread**:
```csharp
private AgentThread _thread;

public async Task<string> SendMessage(string userMessage)
{
    var response = await _agent.RunAsync(userMessage, _thread);
    return response.Messages.Last().Content;
}
```

**Benefits**:
- Automatic history management
- Better state isolation per conversation
- Future support for checkpointing/resumption

---

## Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| Package version instability (preview) | Medium | Medium | Pin specific version, test thoroughly |
| API changes before GA | Low | Medium | Abstract agent creation, easy to update |
| Performance regression | Low | Low | Benchmark before/after |
| Breaking changes in DTOs | Very Low | High | No changes expected to DTO pattern |

---

## References

- [Microsoft Agent Framework Overview](https://learn.microsoft.com/en-us/agent-framework/overview/agent-framework-overview)
- [Agent Framework GitHub](https://github.com/microsoft/agent-framework)
- [Migration from Semantic Kernel](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/SemanticKernelMigration)
- [Structured Output Sample](https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/GettingStarted/FoundryAgents/FoundryAgents_Step05_StructuredOutput)
