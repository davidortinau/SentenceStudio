# Changelog

All notable changes to **Plugin.Maui.HelpKit** are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- CI pipeline (`.github/workflows/helpkit-ci.yml` in the SentenceStudio
  incubation repo): matrix build across net11 macOS (iOS + MacCatalyst),
  Linux (Android), and Windows, plus unit tests, eval gate, and
  preview-pack-on-main.
- Eval gate wired into CI: `>=85% correct AND 0 fabricated citations`,
  fake-client mode by default, live mode opt-in via `HELPKIT_EVAL_LIVE=1`.
- `SUPPORT.md` — TFM list, support policy per release stream, deprecation
  workflow, LTS strategy.
- `SECURITY.md` — responsible disclosure via GitHub private advisories,
  scope, response targets.
- `CONTRIBUTING.md` — local build loop, test + eval commands, code style,
  PR process, decision-record conventions.
- `EXTRACT-RUNBOOK.md` — step-by-step plan to move the library from
  SentenceStudio to `davidortinau/Plugin.Maui.HelpKit` via
  `git subtree split`, preserving history.

### Changed
- README rewritten for publication. Removed "offline-first" and
  "zero hallucination" framing; replaced with honest FAQ. Added Azure AI
  Foundry / OpenAI / Ollama / unkeyed-DI quickstart blocks and full
  `HelpKitOptions` table.

### Known limitations (Alpha)
- Not offline unless the consumer wires a local `IChatClient`.
- Does not eliminate LLM hallucinations; citation validator strips
  fabricated references only.
- AI-enriched scanner, Blazor companion UI, and `sqlite-vec` storage are
  deferred to Beta or v1.
- Single-language content ingestion (chrome localized EN + KO; content
  corpus is language-agnostic but retrieval is tuned for one primary
  language at a time).

## [0.1.0-alpha] - 2026-04-17

### Added
- Initial scaffold of `Plugin.Maui.HelpKit` public API surface.
- `HelpKitOptions` with content directories, history retention (30-day default),
  rate limit (10 q/min default), keyed DI service key, retrieval top-K, answer
  cache TTL, and content filter hook.
- `IHelpKit` contract: `ShowAsync`, `HideAsync`, `ClearHistoryAsync`,
  `IngestAsync`, `StreamAskAsync` (streaming Q&A).
- `IHelpKitPresenter` abstraction with three concrete implementations
  (Shell / Window / MauiReactor).
- `IHelpKitContentFilter` with a `DefaultSecretRedactor` regex-based
  implementation for ingestion-time redaction.
- `AddHelpKit(...)` and `AddHelpKitShellFlyout(...)` `MauiAppBuilder`
  extensions with keyed `IChatClient` / `IEmbeddingGenerator` resolution
  and unkeyed fallback.
- Placeholder `HelpKitPage` so presenters have something to show.
- Sample project placeholders (Shell / Plain / MauiReactor) and test
  project placeholders (`.Tests`, `.Eval`).
- MIT license, honest README messaging, pinned net11 preview `global.json`.

### Not yet wired
- Storage, ingestion pipeline, vector store and history DB (**Wash**).
- RAG retrieval, citation validator, streaming prompt pipeline (**River**).
- Native chat UI, localization (EN + KO), presenter resolution,
  Shell flyout wiring (**Kaylee**).
- Eval harness with golden Q&A set (**Jayne** owns `.Eval` project).
