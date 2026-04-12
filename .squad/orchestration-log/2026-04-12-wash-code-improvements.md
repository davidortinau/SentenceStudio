# Orchestration: Wash — Code Improvements (Automation & Health)

**Date:** 2026-04-12  
**Spawn:** wash-code-improvements  
**Mode:** Background  
**Charter:** Backend & Integration Developer  

## Task

Implement 2 code-level improvements from simulator testing post-mortem:
1. Stable AutomationIds on VocabQuiz for DevFlow targeting
2. Debug health dashboard page

## Status

✅ COMPLETED

## Changes

### 1. VocabQuiz.razor — AutomationIds

Added 6 `id` attributes to stable elements:
- `quiz-info-button` — info icon
- `quiz-info-panel` — info offcanvas
- `quiz-option-a` through `quiz-option-d` — multiple choice buttons
- `quiz-text-input` — text answer field
- `quiz-progress` — progress indicator
- `quiz-correct-count` — correct count badge

### 2. DebugHealth.razor — New Debug Page

Route: `/debug/health`
- `#if DEBUG` gated (Release builds hide the page)
- DB status: connectivity, provider, table count
- Migrations: applied vs pending
- Auth: user state, active profile, profile count
- Vocabulary: total words, words with progress
- Resources: count
- API: reachability check via IHttpClientFactory
- CoreSync: registration status
- Uses existing DI services (no new dependencies)

## Outputs

- `src/SentenceStudio.UI/Pages/VocabQuiz.razor` — 6 ids added
- `src/SentenceStudio.UI/Pages/DebugHealth.razor` — new debug page

## Build

✅ `dotnet build src/SentenceStudio.UI/SentenceStudio.UI.csproj` — 0 errors, pre-existing warnings only

## Decision Record

Merged to decisions.md: Decision: Post-Mortem Code Improvements
