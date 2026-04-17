# HelpKitSample.Plain

Demonstrates **NavigationPage-hosted** integration of Plugin.Maui.HelpKit —
for apps that do not use Shell. The developer controls when help opens by
calling `IHelpKit.ShowAsync()` from a `ToolbarItem` or button. HelpKit's
default presenter selector falls back to `WindowPresenter` in this scenario.

Use this pattern for apps without Shell.

## Run

```bash
dotnet build -t:Run -f net11.0-maccatalyst
dotnet build -t:Run -f net11.0-ios
dotnet build -t:Run -f net11.0-android
dotnet build -t:Run -f net11.0-windows10.0.19041.0
```

Requires .NET 11 preview SDK + MAUI workload. Without those, `dotnet restore`
will fail with `NETSDK1139`.

## What it shows

- Plain `NavigationPage(new MainPage())` as `App.MainPage` — no Shell.
- `IHelpKit` injected into `MainPage` via constructor.
- `ToolbarItem` labelled "Help" and a body button that both invoke
  `_helpKit.ShowAsync()`.
- `HelpKitShellFlyoutOptions` deliberately **not** configured — that helper
  only makes sense for Shell hosts.
- `WindowPresenter` is selected automatically at runtime.

## Stub providers — how to replace

See `MauiProgram.cs` for the registration pattern and the commented examples
for OpenAI, Azure OpenAI, Azure AI Foundry, and Ollama. The stubs return
canned text and deterministic fake embeddings so the sample runs offline.
