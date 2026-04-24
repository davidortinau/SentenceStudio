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
