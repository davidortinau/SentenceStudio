# HelpKitSample.Shell

Demonstrates **Shell-hosted** integration of Plugin.Maui.HelpKit. The Help
pane is reached through the flyout entry that `AddHelpKitShellFlyout` injects
at startup.

## Run

Requires a .NET 11 preview SDK with the MAUI workload installed (see
`lib/Plugin.Maui.HelpKit/global.json`).

```bash
# iOS simulator
dotnet build -t:Run -f net11.0-ios

# Mac Catalyst
dotnet build -t:Run -f net11.0-maccatalyst

# Android emulator
dotnet build -t:Run -f net11.0-android

# Windows
dotnet build -t:Run -f net11.0-windows10.0.19041.0
```

> Until the net11 preview SDK + workload are installed, `dotnet restore` will
> report `NETSDK1139: target platform ... not recognized`. This is an
> environmental gap, not a defect in the sample.

## What it shows

- Three flyout tabs (Dashboard, Profile, About) with dummy content.
- A **Help** flyout entry added by `builder.AddHelpKitShellFlyout("Help")`.
- HelpKit's default presenter selector picking `ShellPresenter` at runtime.
- Sample markdown (`getting-started.md`, `features.md`, `troubleshooting.md`)
  bundled as `MauiAsset` and copied to `{AppDataDirectory}/help-content` on
  first launch so HelpKit can ingest it.

## Stub providers — how to replace

The sample registers **stub** `IChatClient` and `IEmbeddingGenerator`
implementations so it runs without credentials. Replace them in
`MauiProgram.cs`:

```csharp
// Azure OpenAI
builder.Services.AddKeyedSingleton<IChatClient>("helpkit",
    new Azure.AI.OpenAI.AzureOpenAIClient(endpoint, credential)
        .AsChatClient("gpt-4o-mini"));

builder.Services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    "helpkit",
    new Azure.AI.OpenAI.AzureOpenAIClient(endpoint, credential)
        .AsEmbeddingGenerator("text-embedding-3-small"));
```

SentenceStudio (the production host where HelpKit is incubating) wires
**Azure AI Foundry** via `Microsoft.Extensions.AI` and is a good real-world
reference.

## Shared code

Stub providers and the asset-copy installer live in `samples/SharedStubs/`
and are linked via `<Compile Include="..\SharedStubs\*.cs" LinkBase="SharedStubs" />`
in the csproj so all three samples share a single source of truth.
