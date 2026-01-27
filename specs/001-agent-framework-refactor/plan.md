# Implementation Plan: Microsoft Agent Framework Refactor

**Branch**: `001-agent-framework-refactor` | **Date**: 2026-01-27 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/001-agent-framework-refactor/spec.md`

## Summary

Refactor the SentenceStudio AI service layer from Microsoft.Extensions.AI (`IChatClient`) to Microsoft Agent Framework (`ChatClientAgent`). This migration provides a clean upgrade path to Microsoft's next-generation agent framework while maintaining all existing functionality including structured output parsing, multi-turn conversations, and prompt template rendering. The approach uses `ChatClientAgent` wrapping the existing OpenAI client with `RunAsync<T>()` for structured responses.

## Technical Context

**Language/Version**: .NET 10.0 (net10.0-android, net10.0-ios, net10.0-maccatalyst, net10.0-windows10.0.19041.0)  
**Primary Dependencies**: .NET MAUI 10.0.20+, MauiReactor, SQLite, **Microsoft.Agents.AI** (replacing Microsoft.Extensions.AI), ElevenLabs SDK  
**Storage**: SQLite local database with optional CoreSync synchronization  
**Testing**: xUnit for unit tests, platform-specific testing for UI  
**Target Platform**: iOS 12.2+, Android API 21+, macOS 15.0+, Windows 10.0.17763.0+  
**Project Type**: Multi-platform MAUI application (mobile + desktop)  
**Performance Goals**: <3s startup, <100ms UI response, <500ms AI API calls with loading indicators  
**Constraints**: Must work offline, all platforms tested, ILogger for production logs, Theme-first styling  
**Scale/Scope**: Single-user language learning app with custom curriculum support

### Migration-Specific Context

**Current Packages**:
- `Microsoft.Extensions.AI` Version 10.2.0
- `Microsoft.Extensions.AI.OpenAI` Version 10.2.0-preview.1.26063.2

**New Packages**:
- `Microsoft.Agents.AI` (latest preview)
- `Microsoft.Agents.AI.OpenAI` (latest preview)

**Affected Services** (8 files):
1. `AiService.cs` - Central AI service with `IChatClient`
2. `TranslationService.cs` - Translation exercise generation
3. `ClozureService.cs` - Cloze (fill-in-blank) exercise generation
4. `ConversationService.cs` - Multi-turn conversation management
5. `ShadowingService.cs` - Shadowing exercise generation
6. `LlmPlanGenerationService.cs` - Daily plan generation
7. `MauiProgram.cs` - DI registration
8. `SentenceStudio.csproj` - Package references

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Verify alignment with SentenceStudio Constitution (`.specify/memory/constitution.md`):

- [x] **User-Centric AI-Powered Learning**: Does this feature support custom curriculum or AI-powered learning?
  - ✅ Yes - Maintains all AI-powered learning features (cloze, translation, conversation)
- [x] **Cross-Platform Native**: Will this work on iOS, Android, macOS, and Windows?
  - ✅ Yes - Agent Framework is .NET Standard compatible, no platform-specific code
- [x] **MauiReactor MVU**: Does UI use semantic methods (`.HStart()`, `.VCenter()`, etc.) not `HorizontalOptions`?
  - ✅ N/A - Backend refactor only, no UI changes
- [x] **Theme-First UI**: Does styling use `.ThemeKey()` or theme constants (no hardcoded colors/sizes)?
  - ✅ N/A - Backend refactor only, no UI changes
- [x] **Localization by Default**: Are all strings using `$"{_localize["Key"]}"` pattern?
  - ✅ N/A - Backend refactor only, no user-facing strings changed
- [x] **Observability**: Is `ILogger<T>` used for production logging?
  - ✅ Yes - Existing ILogger usage maintained, Agent Framework has built-in telemetry
- [x] **Documentation in docs/**: Are specs/guides placed in `docs/` folder?
  - ✅ Yes - Specs in `specs/`, user docs would go in `docs/` if needed

**Violations requiring justification**: None - This is a backend service layer refactor with no constitution violations.

## Project Structure

### Documentation (this feature)

```text
specs/001-agent-framework-refactor/
├── spec.md              # Feature specification ✅
├── plan.md              # This file ✅
├── research.md          # Phase 0 output ✅
├── data-model.md        # Phase 1 output (N/A - no new data models)
├── quickstart.md        # Phase 1 output ✅
├── checklists/          # Validation checklists
│   └── requirements.md  # Spec quality checklist ✅
└── tasks.md             # Phase 2 output (/speckit.tasks command)
```

### Source Code (repository root)

```text
src/
├── SentenceStudio/              # Main MAUI application
│   ├── Pages/                   # MauiReactor page components
│   ├── Services/                # Business logic and AI integration
│   ├── Data/                    # SQLite repositories and models
│   ├── Models/                  # Domain models and DTOs
│   ├── Resources/               # Assets, localization, themes
│   │   ├── Strings/             # Resources.resx, Resources.ko.resx
│   │   └── Styles/              # MyTheme.cs
│   └── Platforms/               # Platform-specific code
├── SentenceStudio.Shared/       # Shared models and utilities
└── SentenceStudio.ServiceDefaults/ # Service configuration

tests/
├── SentenceStudio.Tests/        # Unit tests (xUnit)
└── SentenceStudio.UITests/      # Platform-specific UI tests
```

**Structure Decision**: MAUI multi-platform application with shared business logic and platform-specific implementations. All features must work across iOS, Android, macOS, and Windows.

## Complexity Tracking

> **No violations - table not required**

## Implementation Approach

### Phase 1: Package Migration

1. Update `SentenceStudio.csproj` to replace packages:
   ```xml
   <!-- Remove -->
   <PackageReference Include="Microsoft.Extensions.AI" Version="10.2.0" />
   <PackageReference Include="Microsoft.Extensions.AI.OpenAI" Version="10.2.0-preview.1.26063.2" />
   
   <!-- Add -->
   <PackageReference Include="Microsoft.Agents.AI" Version="X.X.X-preview" />
   <PackageReference Include="Microsoft.Agents.AI.OpenAI" Version="X.X.X-preview" />
   ```

2. Update `MauiProgram.cs` DI registration:
   ```csharp
   // Keep IChatClient registration (Agent Framework still uses it)
   builder.Services.AddSingleton<IChatClient>(sp => {
       var config = sp.GetRequiredService<IConfiguration>();
       var apiKey = config.GetRequiredSection("Settings").Get<Settings>().OpenAIKey;
       return new OpenAIClient(apiKey)
           .GetChatClient("gpt-4o-mini")
           .AsIChatClient();
   });
   ```

### Phase 2: Service Migration

**Pattern for each service**:

```csharp
// BEFORE (Microsoft.Extensions.AI)
IChatClient client = new OpenAIClient(apiKey).GetChatClient("gpt-4o-mini").AsIChatClient();
var response = await client.GetResponseAsync<TranslationResponse>(prompt);
return response.Result;

// AFTER (Microsoft Agent Framework)
var agent = new ChatClientAgent(_chatClient, new ChatClientAgentOptions 
{
    Name = "TranslationAgent",
    Instructions = "You are a Korean language translation assistant."
});
var response = await agent.RunAsync<TranslationResponse>(prompt);
return response.Result;
```

### Phase 3: ConversationService Special Handling

The `ConversationService` requires `AgentThread` for multi-turn conversation state:

```csharp
// Create thread for conversation persistence
private AgentThread _thread = new();

public async Task<string> SendMessage(string userMessage)
{
    var response = await _agent.RunAsync(userMessage, _thread);
    return response.Messages.Last().Content;
}

public void ResetConversation()
{
    _thread = new AgentThread();
}
```

### Migration Order (by dependency)

1. **SentenceStudio.csproj** - Package references
2. **MauiProgram.cs** - DI registration (maintain `IChatClient` singleton)
3. **AiService.cs** - Central service (other services depend on this)
4. **TranslationService.cs** - Standalone, uses direct client
5. **ClozureService.cs** - Standalone, uses direct client
6. **ConversationService.cs** - Requires AgentThread migration
7. **ShadowingService.cs** - Uses AiService indirectly
8. **LlmPlanGenerationService.cs** - Uses injected IChatClient

### Verification Checklist

- [ ] Build succeeds for all TFMs (iOS, Android, macOS, Windows)
- [ ] Unit tests pass
- [ ] Manual testing: Cloze exercise generation works
- [ ] Manual testing: Translation exercise generation works
- [ ] Manual testing: Conversation responds correctly
- [ ] Manual testing: Multi-turn conversation maintains context
- [ ] Performance: Response times within 5s for text generation
