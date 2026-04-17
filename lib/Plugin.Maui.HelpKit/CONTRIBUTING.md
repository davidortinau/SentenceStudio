# Contributing to Plugin.Maui.HelpKit

Thanks for the interest. This doc covers the local loop during Alpha
incubation inside the SentenceStudio monorepo; it will be updated when the
library extracts to its own repo.

## Prerequisites

- .NET 11 preview SDK (see `global.json` — `rollForward: latestFeature`,
  `allowPrerelease: true`)
- .NET MAUI workload: `dotnet workload install maui`
- Platform prerequisites for the TFMs you plan to build:
  - iOS/MacCatalyst: macOS with Xcode matching the MAUI workload
  - Android: Android SDK + platform-tools
  - Windows: Windows 10+ with Windows App SDK workload

Verify your setup:

```bash
dotnet --version          # should resolve to a net11 preview
dotnet workload list      # maui (and/or maui-android on Linux CI) present
```

## Build

From the library folder:

```bash
cd lib/Plugin.Maui.HelpKit
dotnet restore src/Plugin.Maui.HelpKit/Plugin.Maui.HelpKit.csproj
dotnet build src/Plugin.Maui.HelpKit/Plugin.Maui.HelpKit.csproj -c Debug -f net11.0-maccatalyst
```

Other TFMs: substitute `-f net11.0-ios`, `-f net11.0-android`, or
`-f net11.0-windows10.0.19041.0`.

## Run tests

```bash
dotnet test tests/Plugin.Maui.HelpKit.Tests/Plugin.Maui.HelpKit.Tests.csproj -c Release
```

## Run the eval

CI runs the eval with a scripted fake client. To replicate locally:

```bash
HELPKIT_EVAL_LIVE=0 dotnet test \
  tests/Plugin.Maui.HelpKit.Eval/Plugin.Maui.HelpKit.Eval.csproj \
  --filter "FullyQualifiedName~CiGate_MustPass"
```

To run the live-model eval (release gate), set `HELPKIT_EVAL_LIVE=1` and
supply `OPENAI_API_KEY` (or equivalent for your provider). Do not commit
secrets.

## Code style

- `Nullable` enabled; no `#nullable disable` without justification
- No emojis in code, logs, docs, or commit messages
- Minimal transitive dependencies; prefer BCL + `Microsoft.Extensions.*`
- Public APIs need XML doc comments
- Internal helpers should not surface in the public type list — use
  `internal` aggressively
- No `System.Diagnostics.Debug.WriteLine` in library code. Use `ILogger<T>`
  with the category `Plugin.Maui.HelpKit`.
- Async: cancellable all the way down. Every public async method takes a
  `CancellationToken`.

## PR process

1. Fork or branch.
2. Before pushing: build all four TFMs locally (macOS + Windows split is
   acceptable), run unit tests, run the fake-mode eval.
3. Open the PR against `main`. Include a CHANGELOG entry under
   `[Unreleased]`.
4. CI runs the full matrix plus the eval gate (>=85% correct, 0 fabricated
   cites). Both must pass.
5. Lead (Zoe during incubation; Captain post-extract) reviews the PR. Breaking
   changes require explicit approval and a migration note.
6. Squash-merge is the default.

## Decision records

During incubation: drop a memo into
`.squad/decisions/inbox/<author>-<topic>.md` for any non-trivial technical
decision. The coordinator folds accepted memos into `.squad/decisions.md`.

After extraction to the standalone repo: move to Architecture Decision
Records at `docs/adr/NNNN-title.md` using the lightweight
[MADR](https://adr.github.io/madr/) format.

## Releasing

Captain / Lead tag the release. CI `pack-preview` job produces the
`.nupkg` artifact; the first Alpha release uploads it manually to NuGet.org
via `dotnet nuget push` with a scoped API key. Subsequent releases automate
push via a protected GitHub Actions workflow gated on tag pattern.

## Getting unstuck

- Open a Draft PR early and ping Lead for guidance.
- For architectural questions, write a decision memo under
  `.squad/decisions/inbox/` and tag it in the PR description.
- For CI flakes, rerun once; if it flakes twice, open an issue labelled
  `ci-flake`.
