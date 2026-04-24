# Project Context

- **Project:** SentenceStudio
- **Created:** 2026-03-07

## Core Context

Agent Scribe initialized and ready for work.

## Recent Updates

📌 Team initialized on 2026-03-07
📌 Squad — Word/Phrase Plan Review checkpoint logged 2026-07-26 (5-agent consensus, 3 Captain decisions, implementation ready)
📌 **2026-04-24** Bookkeeping pass: Merged 3 Wash decisions from inbox (DB reconcile Option A + smart-resource idempotency + wireup), logged session for WoC push (7cddc6e, ff0bb25), pushed to origin/main

## Learnings

Initial setup complete.
- For Azure publish logs, capture environment + region, successful resource list, public webapp URL, Aspire dashboard URL, and note custom-domain follow-up separately from deploy status.
- **BlazorHybrid NavigateTo patterns (2026-04-18):** `LoginPage.razor` and `RegisterPage.razor` use `forceLoad: true`, which works for Web (cookie-backed) but breaks MAUI (loses in-memory auth state). Platform-gating needed via `isWeb` pattern. See `NavMenu.razor:106-107` for existing pattern to borrow.

---

## 2026-04-24 — Data Import Architecture Session Orchestration

Scribed multi-agent session for data-import-architecture-plan. Deployed Zoe (architect), Wash (data scout), River (AI strategy), Kaylee (UI scout). Recorded Captain directive on scope separation.

**Orchestration tasks completed:**
1. ✅ 5 orchestration logs (Zoe, Wash, River, Kaylee, Copilot directive)
2. ✅ Session log with key outcomes + next steps
3. ✅ Merged 6 inbox decisions into decisions.md, deleted inbox files
4. ✅ Updated all 4 agent histories with import-plan context
5. ✅ Decisions.md now 169KB (will archive if >20KB trigger next run)
6. ✅ Git staged for commit with proper trailers

**Key outcomes recorded:**
- MVP architecture: `/import-content` page, ContentImportService in Shared, no new DB tables
- Placement: separate Video Subscriptions from generic import (per Captain directive)
- AI strategy: heuristics-first, 5 prompt templates, confidence thresholds
- UI patterns: reuse ResourceAdd card, InputFile, preview table
- Data integrity: dedup by TargetLanguageTerm (case-insensitive trimmed)

**Next:** Implementation team begins service + UI build. River engineers prompts.

