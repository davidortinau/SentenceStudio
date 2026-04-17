# HelpKitSample.MauiReactor

This shows MauiReactor integration. The library itself uses plain MAUI
controls for the help page, but hosts cleanly inside a MauiReactor app.

The sample is a single `Component<MainPageState>` with a label and an
**Ask Help** button. Tapping the button resolves `IHelpKit` from the MAUI
service provider and calls `ShowAsync()`.

## Run

```bash
dotnet build -t:Run -f net11.0-maccatalyst
dotnet build -t:Run -f net11.0-ios
dotnet build -t:Run -f net11.0-android
dotnet build -t:Run -f net11.0-windows10.0.19041.0
```

Requires .NET 11 preview SDK + MAUI workload. The MauiReactor package
version pinned in the csproj (`Reactor.Maui` 3.1.0) may need to be bumped
to whichever stable release best matches your net11 preview.

## What it shows

- `builder.UseMauiReactorApp<MainPage>(...)` — MauiReactor's host pattern.
- `builder.AddHelpKit(...)` registers HelpKit identically to the other
  samples — no MauiReactor-specific wiring required.
- The MauiReactor `Component` calls `IHelpKit.ShowAsync()` from a
  fluent button handler. HelpKit's default presenter selector resolves
  whichever presenter fits at runtime (Shell when MauiReactor renders one,
  Window otherwise). If the default selection is wrong for your app,
  register `MauiReactorPresenter` explicitly:

  ```csharp
  builder.Services.AddSingleton<IHelpKitPresenter,
      Plugin.Maui.HelpKit.Presenters.MauiReactorPresenter>();
  ```

- MauiReactor conventions used: `VStart()`, `HStart()`, `Center()`,
  fluent `Padding`, `OnClicked`. No `HorizontalOptions` /
  `VerticalOptions` calls.

## Stub providers — how to replace

See `MauiProgram.cs`. SentenceStudio's main app uses Azure AI Foundry
via Microsoft.Extensions.AI; that wiring is the closest production
analogue.
