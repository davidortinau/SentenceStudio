# Plugin.Maui.HelpKit

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Status: Alpha](https://img.shields.io/badge/status-alpha-orange.svg)](CHANGELOG.md)
[![NuGet](https://img.shields.io/badge/nuget-pending-lightgrey.svg)](https://www.nuget.org/packages/Plugin.Maui.HelpKit)

Conversational in-app help for .NET MAUI apps, grounded in markdown you provide.
Bring your own `IChatClient` and `IEmbeddingGenerator`.

> **Alpha 0.1.0** — APIs may change. Production use not recommended yet.

---

## What this is (honest)

Plugin.Maui.HelpKit embeds an AI-assisted help experience into any .NET MAUI
app. You supply markdown docs and a `Microsoft.Extensions.AI` chat/embedding
pair. HelpKit ingests the content locally, retrieves relevant chunks at query
time, streams grounded answers, validates citations against your content, and
persists chat history per user with a clear action.

We deliberately avoid "offline-first" and "zero hallucination" framing. Whether
the experience is offline depends entirely on the `IChatClient` you wire in,
and whether the model hallucinates depends on that model. What HelpKit does
guarantee: citations that reference a non-existent path or anchor are stripped
before the message yields, and the eval harness fails CI on fabricated cites.

---

## What ships in Alpha

- `AddHelpKit(...)` + `AddHelpKitShellFlyout(...)` + `IHelpKit.ShowAsync()`
- Markdown ingestion pipeline with content-filter hook (secret redaction by
  default)
- Non-AI stub page scanner that emits one `.md` per detected page
- Native MAUI chat overlay with streaming responses
- `IHelpKitPresenter` abstraction with Shell, Window/NavigationPage, and
  MauiReactor implementations
- Keyed DI (`[FromKeyedServices("helpkit")]`) with unkeyed fallback
- In-memory `Microsoft.Extensions.VectorData` store with JSON disk persistence
- Chat history with 30-day default retention, per-user scoping via
  `CurrentUserProvider`, and clear-history action
- Per-session rate limit (default 10 q/min) and query/answer cache
- Citation validator that strips fabricated references
- Eval harness (`Plugin.Maui.HelpKit.Eval`) with CI gate at >=85% correct and
  0 fabricated citations
- Chrome localization: English + Korean
- Telemetry via `System.Diagnostics.Metrics`
- Day-one `schema_version` and documented migration policy

## What does NOT ship in Alpha

- AI-enriched source scanner (deferred to Beta)
- `Plugin.Maui.HelpKit.Blazor` companion UI (deferred; native is primary)
- `sqlite-vec` storage backend (deferred to v1 — custom SQLitePCLRaw builds
  across all MAUI RIDs is weeks of work and not Alpha-worthy)
- Multi-language content ingestion beyond EN/KO chrome strings
- Cloud-sync history
- Voice input / TTS

---

## Install

```bash
dotnet add package Plugin.Maui.HelpKit --prerelease
```

> The package is not yet published to NuGet.org. During incubation, consume
> via `ProjectReference` to `lib/Plugin.Maui.HelpKit/src/Plugin.Maui.HelpKit`
> from this repo, or wait for the first GitHub release at Alpha close.

Supported target frameworks:

- `net11.0-android`
- `net11.0-ios`
- `net11.0-maccatalyst`
- `net11.0-windows10.0.19041.0`

No netstandard. No net8/9/10. See [SUPPORT.md](SUPPORT.md).

---

## Quickstart

### 1. Azure AI Foundry / Azure OpenAI

```csharp
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Plugin.Maui.HelpKit;

var builder = MauiApp.CreateBuilder();

var azure = new AzureOpenAIClient(
    new Uri("https://YOUR-FOUNDRY-RESOURCE.openai.azure.com"),
    new AzureKeyCredential(azureKey));

builder.Services.AddKeyedSingleton<IChatClient>("helpkit",
    (_, _) => azure.GetChatClient("gpt-4o-mini").AsIChatClient());

builder.Services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    "helpkit",
    (_, _) => azure.GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator());

builder.AddHelpKit(options =>
{
    options.ContentDirectories.Add("Resources/Help");
    options.HistoryRetention = TimeSpan.FromDays(30);
    options.MaxQuestionsPerMinute = 10;
    options.Language = "en";
});
```

### 2. OpenAI

```csharp
using Microsoft.Extensions.AI;
using OpenAI;
using Plugin.Maui.HelpKit;

var openAi = new OpenAIClient(openAiKey);

builder.Services.AddKeyedSingleton<IChatClient>("helpkit",
    (_, _) => openAi.GetChatClient("gpt-4o-mini").AsIChatClient());

builder.Services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    "helpkit",
    (_, _) => openAi.GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator());

builder.AddHelpKit(options =>
{
    options.ContentDirectories.Add("Resources/Help");
});
```

### 3. Ollama (local models)

```csharp
using Microsoft.Extensions.AI;
using OllamaSharp;
using Plugin.Maui.HelpKit;

var ollama = new OllamaApiClient(new Uri("http://localhost:11434"));

builder.Services.AddKeyedSingleton<IChatClient>("helpkit",
    (_, _) => new OllamaApiClient(new Uri("http://localhost:11434"), "llama3.2").AsIChatClient());

builder.Services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    "helpkit",
    (_, _) => new OllamaApiClient(new Uri("http://localhost:11434"), "nomic-embed-text").AsIEmbeddingGenerator());

builder.AddHelpKit();
```

### 4. Unkeyed DI (simplest)

If your app only has one `IChatClient` and one `IEmbeddingGenerator`, you can
skip keyed registration. HelpKit falls back to the unkeyed service when the
keyed one is absent.

```csharp
builder.Services.AddSingleton<IChatClient>(sp => /* your chat client */);
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp => /* your generator */);
builder.AddHelpKit();
```

---

## Content authoring

HelpKit reads plain Markdown. Ingest-time rules:

- Each file becomes one or more retrieval chunks, split on headings.
- Section anchors are derived from heading text (GitHub-style slugs).
- Citations in streamed answers use the format `[cite:path/to/file.md#anchor]`.
- Non-markdown files in `ContentDirectories` are ignored.
- Front-matter blocks (`---` fenced at file start) are parsed as metadata and
  excluded from embedding input.

Example file `Resources/Help/vocabulary.md`:

```markdown
---
title: Vocabulary
route: /vocabulary
---

# Resetting vocabulary

To reset your vocabulary list, open Settings and tap "Reset progress".
This deletes your review schedule but preserves your imported words.

## Exporting before reset

Use Settings > Export to save a CSV copy first.
```

Answers will cite the heading anchor, e.g.
`[cite:Resources/Help/vocabulary.md#resetting-vocabulary]`.

---

## Surfacing help

Three supported patterns. Pick what fits your app.

### Shell flyout

```csharp
builder.AddHelpKit();
builder.AddHelpKitShellFlyout(title: "Help", icon: "help_icon.png");
```

### ToolbarItem or button

```csharp
var helpKit = serviceProvider.GetRequiredService<IHelpKit>();
await helpKit.ShowAsync();
```

Wire that call to any `Button`, `ToolbarItem`, menu entry, or gesture.

### Custom presenter

Implement `IHelpKitPresenter` if none of the built-ins fit your host:

```csharp
public sealed class MyCustomPresenter : IHelpKitPresenter
{
    public Task PresentAsync(Page page, CancellationToken ct) { /* ... */ }
    public Task DismissAsync(CancellationToken ct) { /* ... */ }
}

// Register AFTER AddHelpKit to override the default.
builder.Services.AddSingleton<IHelpKitPresenter, MyCustomPresenter>();
```

---

## Options reference

All configuration lives on `HelpKitOptions`:

| Option | Default | Purpose |
|---|---|---|
| `ContentDirectories` | empty | MauiAsset paths to ingest. |
| `HistoryRetention` | `TimeSpan.FromDays(30)` | When chat history rolls off. `TimeSpan.MaxValue` = keep forever. |
| `MaxQuestionsPerMinute` | `10` | Per-session rate limit. `0` = unlimited. |
| `CurrentUserProvider` | `null` | Resolves the user id for per-user history scoping. |
| `ContentFilter` | `DefaultSecretRedactor` | Ingestion-time `IHelpKitContentFilter`. |
| `Language` | `"en"` | Chrome localization. `"en"` and `"ko"` ship in Alpha. |
| `HelpKitServiceKey` | `"helpkit"` | Keyed-DI discriminator. |
| `RetrievalTopK` | `5` | Number of chunks retrieved per query. |
| `SimilarityThresholdOverride` | `null` | `null` uses the per-model default table. |
| `EnableAnswerCache` | `true` | Cache by `(query, chunk-ids)`. |
| `AnswerCacheTtl` | `TimeSpan.FromDays(7)` | Cached-answer lifetime. |
| `StoragePath` | `null` | `null` = `{FileSystem.AppDataDirectory}/helpkit/`. |
| `MaxHistoryTokens` | `null` | Token ceiling for conversation context. `null` = unlimited. |
| `MaxRetrievalTokens` | `null` | Token ceiling for retrieved chunks per turn. |

---

## Honest messaging FAQ

**Does HelpKit run offline?**
No. The library stores content and history locally, but answer generation
calls your `IChatClient` — which is typically cloud (OpenAI, Azure AI
Foundry) unless you use a local model like Ollama.

**Does HelpKit eliminate hallucinations?**
No. It grounds answers in your markdown via retrieval and a citation
validator that strips fabricated citations. The LLM can still be wrong. The
eval harness enforces >=85% correctness and 0 fabricated cites in CI.

**What embedding models are supported?**
Any `IEmbeddingGenerator<string, Embedding<float>>` from
`Microsoft.Extensions.AI`. HelpKit ships per-model similarity thresholds for
common models (`text-embedding-3-small`, `text-embedding-3-large`,
`all-MiniLM-L6-v2`, `bge-small-en`). Tune via
`HelpKitOptions.SimilarityThresholdOverride`.

**Can I ship a model in my app instead of calling a cloud service?**
Yes. Wire any local-model `IChatClient` (Ollama, ONNX, llama.cpp bindings) the
same way you would wire OpenAI. HelpKit does not care which implementation you
register.

**Does HelpKit send my docs or user questions anywhere?**
HelpKit itself never makes outbound network calls. All outbound traffic comes
from your registered `IChatClient` / `IEmbeddingGenerator`. Inspect those
implementations to understand data flow.

---

## Samples

Three sample apps ship under `samples/` and demonstrate every supported host:

- `HelpKitSample.Shell/` — MAUI Shell flyout integration
- `HelpKitSample.Plain/` — plain `NavigationPage`, no Shell
- `HelpKitSample.MauiReactor/` — MauiReactor MVU host

---

## Telemetry

HelpKit emits counters and histograms via `System.Diagnostics.Metrics` under
the meter name `Plugin.Maui.HelpKit`. No PII, no content, no keys.

| Instrument | Type | Meaning |
|---|---|---|
| `helpkit.ingest.documents` | Counter | Documents ingested. |
| `helpkit.ingest.chunks` | Counter | Chunks produced. |
| `helpkit.ingest.duration` | Histogram | Ingest wall time (ms). |
| `helpkit.retrieval.queries` | Counter | Queries executed. |
| `helpkit.retrieval.duration` | Histogram | Retrieval wall time (ms). |
| `helpkit.llm.requests` | Counter | Calls to `IChatClient`. |
| `helpkit.llm.duration` | Histogram | Streaming wall time (ms). |
| `helpkit.cache.hits` | Counter | Answer-cache hits. |
| `helpkit.cache.misses` | Counter | Answer-cache misses. |
| `helpkit.ratelimit.rejections` | Counter | Requests rejected by rate limit. |
| `helpkit.citations.fabricated_stripped` | Counter | Citations stripped by the validator. |

Hook up any `IMeterFactory` consumer (OpenTelemetry, App Insights, custom).

---

## Privacy and safety

- **Content filter at ingest**: `DefaultSecretRedactor` redacts common secret
  patterns (`sk-*`, `ghp_*`, `xox*`, `AKIA*`, `key/password/token:value`,
  emails, GUIDs adjacent to secret-looking tokens). Replace it by registering
  your own `IHelpKitContentFilter`.
- **Prompt-injection defenses**: retrieved chunks are delimiter-fenced before
  being handed to the model; user messages are always outside the retrieval
  envelope; system prompt is non-overridable from user input.
- **Rate limit**: default 10 questions per minute per session.
- **History retention**: 30 days by default. Per-user scoping via
  `CurrentUserProvider`. Clear action wired into the overlay.
- **Storage**: plain-file JSON under `FileSystem.AppDataDirectory`. For
  encrypted storage, point `StoragePath` at your own encrypted container (the
  SQLCipher integration hook lands in a follow-up).

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Short version: install .NET 11
preview + MAUI workload, run tests + eval, open a PR. The eval gate must pass.

---

## License

MIT — see [LICENSE](LICENSE).

---

## Roadmap

**Alpha (0.1.0)** — you are here. Feature-complete for the "bring your own
model, markdown in, grounded answers out" story.

**Beta (0.2.0)** — AI-enriched source scanner, expanded golden Q&A set (50+),
SQLCipher storage hook, content-change auto-reingest, response quality
telemetry hooks.

**v1.0** — `sqlite-vec` companion package for large content corpora, optional
`Plugin.Maui.HelpKit.Blazor` companion UI, multi-TFM (net10 + net11) if the
community asks, semantic-versioning guarantees.
