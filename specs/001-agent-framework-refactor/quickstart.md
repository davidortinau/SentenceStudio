# Quickstart: Microsoft Agent Framework Migration

**Feature**: 001-agent-framework-refactor  
**Date**: 2026-01-27

This guide provides step-by-step instructions for migrating from Microsoft.Extensions.AI to Microsoft Agent Framework.

---

## Prerequisites

- .NET 10.0 SDK installed
- OpenAI API key configured in `appsettings.json`
- Familiarity with existing AI service implementations

---

## Step 1: Update NuGet Packages

Edit `src/SentenceStudio/SentenceStudio.csproj`:

```xml
<!-- Remove these packages -->
<!-- <PackageReference Include="Microsoft.Extensions.AI" Version="10.2.0" /> -->
<!-- <PackageReference Include="Microsoft.Extensions.AI.OpenAI" Version="10.2.0-preview.1.26063.2" /> -->

<!-- Add these packages -->
<PackageReference Include="Microsoft.Agents.AI" Version="X.X.X-preview" />
<PackageReference Include="Microsoft.Agents.AI.OpenAI" Version="X.X.X-preview" />
```

Run:
```bash
cd src
dotnet restore SentenceStudio/SentenceStudio.csproj
```

---

## Step 2: Update Using Statements

Replace imports in affected service files:

```csharp
// BEFORE
using Microsoft.Extensions.AI;

// AFTER
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.ChatClient;
```

---

## Step 3: Migrate Simple Structured Output Calls

For services using `GetResponseAsync<T>()`:

```csharp
// BEFORE
IChatClient client = new OpenAIClient(apiKey)
    .GetChatClient("gpt-4o-mini")
    .AsIChatClient();

var response = await client.GetResponseAsync<MyDto>(prompt);
return response.Result;

// AFTER
var agent = new ChatClientAgent(_chatClient, new ChatClientAgentOptions
{
    Name = "MyAgent"
});

var response = await agent.RunAsync<MyDto>(prompt);
return response.Result;
```

---

## Step 4: Migrate Multi-Turn Conversations

For `ConversationService` with chat history:

```csharp
// BEFORE
private List<ChatMessage> _messages = new();

public async Task<string> SendMessage(string userMessage)
{
    _messages.Add(new ChatMessage(ChatRole.User, userMessage));
    var response = await _client.GetResponseAsync<string>(_messages);
    _messages.Add(new ChatMessage(ChatRole.Assistant, response.Result));
    return response.Result;
}

// AFTER
private AgentThread _thread = new();
private ChatClientAgent _agent;

public async Task<string> SendMessage(string userMessage)
{
    var response = await _agent.RunAsync(userMessage, _thread);
    return response.Messages.LastOrDefault()?.Content ?? string.Empty;
}

public void ResetConversation()
{
    _thread = new AgentThread();
}
```

---

## Step 5: Update Dependency Injection

In `MauiProgram.cs`, the `IChatClient` registration can remain similar:

```csharp
// IChatClient registration (shared across agents)
builder.Services.AddSingleton<IChatClient>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var apiKey = config.GetRequiredSection("Settings").Get<Settings>().OpenAIKey;
    return new OpenAIClient(apiKey)
        .GetChatClient("gpt-4o-mini")
        .AsIChatClient();
});

// Optional: Register a default ChatClientAgent
builder.Services.AddSingleton<ChatClientAgent>(sp =>
{
    var chatClient = sp.GetRequiredService<IChatClient>();
    return new ChatClientAgent(chatClient, new ChatClientAgentOptions
    {
        Name = "DefaultAgent",
        Instructions = "You are a helpful Korean language learning assistant."
    });
});
```

---

## Step 6: Verify Build

```bash
cd src
dotnet build SentenceStudio/SentenceStudio.csproj -f net10.0-maccatalyst
```

---

## Step 7: Test Functionality

Run the app and verify:

1. **Cloze exercises**: Start a vocabulary activity and verify sentences generate
2. **Translations**: Submit a translation and verify grading works
3. **Conversations**: Start a conversation and verify multi-turn context

```bash
cd src
dotnet build -t:Run -f net10.0-maccatalyst SentenceStudio/SentenceStudio.csproj
```

---

## API Comparison Reference

| Microsoft.Extensions.AI | Microsoft Agent Framework |
|------------------------|--------------------------|
| `IChatClient` | `IChatClient` (unchanged) |
| `GetResponseAsync<T>(prompt)` | `agent.RunAsync<T>(prompt)` |
| `GetResponseAsync<T>(messages)` | `agent.RunAsync<T>(prompt, thread)` |
| `ChatMessage` | `ChatMessage` (unchanged) |
| `ChatRole.User/Assistant` | `ChatRole.User/Assistant` (unchanged) |
| Manual history management | `AgentThread` automatic management |

---

## Troubleshooting

### "Type 'IChatClient' could not be found"
Ensure you've added `Microsoft.Agents.AI` package and the using statement.

### "Method 'GetResponseAsync' does not exist"
Replace with `RunAsync<T>()` on a `ChatClientAgent` instance.

### Conversation history lost
Ensure you're passing the same `AgentThread` instance to `RunAsync()` calls.

### Structured output parsing fails
Verify your DTO still has `[Description]` attributes on properties. The Agent Framework uses the same schema generation.
