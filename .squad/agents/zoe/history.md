# Zoe — History Summary

**Summarized by Scribe:** 2026-06-10T03:35:00Z
**Project:** SentenceStudio — .NET MAUI Blazor Hybrid language learning app
**Role focus:** Product architecture, cross-agent strategy, decision framing, dogfooding priority.

## Durable directives and architecture patterns

- Tooling friction outranks app feature velocity. When MAUI, Aspire, DevFlow, Hot Reload, Blazor Hybrid, or build tooling blocks normal work, root-cause and upstream/document the friction before treating it as a workaround.
- One-shot migrations and maintenance endpoints must be challenged. If an operation is non-idempotent, global, or no longer needed, remove it rather than gating it. Future repairs should be scoped one-off EF scripts, not permanent broad endpoints.
- Multi-agent code work should use isolated `git worktree add` checkouts when agents are active in parallel. Shared branch checkouts caused uncommitted work loss during the 2026-05-08 migrate-streak cycle.
- NumberDrill context/sub-mode gating uses a data-driven `SupportedSubModes` matrix: hide incomplete combinations, keep re-enable cheap, and verify picker/start/render/success/failure/audio/no-stub gates across Mac Catalyst, iOS, and WebApp.
- DeterministicPlanBuilder slot-replacement architecture: NumberDrill can replace the STEP 4 closer slot when numbers are due; ResourceId-null plus SkillId decoupling is a reusable activity pattern.
- Dynamic platform features in shared UI should use runtime type resolution/reflection to keep browser UI portable and gracefully degrade when MAUI-only features are absent.
- M.E.AI 10.5 strategy: verify framework claims against Microsoft sources; defer experimental headline features when unstable, but ship debt-reduction work such as Polly resilience, central package management, and config-driven model IDs.
- Mac Catalyst bundle-name mismatch was fixed with an MSBuild symlink target using `$(_AppBundleName)` and avoiding duplicated RuntimeIdentifier segments.

## Recent product decisions

- Today Plan focus vocabulary architecture: the plan preview must be a projection of the same focus set consumed by activities, not an independent sample. Reading/Translation/Shadowing/Listening/Video may include incidental resource vocabulary, but focus words are the through-line.
- DailyPlan must sync across mobile, desktop, and browser. Plan structure and personal progress are cross-device product expectations, so `DailyPlan` CoreSync participation is required.

## Team reminders

- Use decisions inbox for durable choices, then Scribe merges to `decisions.md`.
- Keep AGENTS.md directives as source of truth for data preservation, validation gates, and MAUI dogfooding behavior.

---

- 2026-06-11: **Vocab bootstrap review + wording cascade audit** — Reviewed multi-tenant scoping (matches post-May hotfix), blend logic edges, synthetic VocabularyProgress safety. Bootstrap 🟢 after stale comment removed per AGENTS.md. Wording cascade approved 🟢 — 4 branches exhaustive at boundaries (0/1/30 days). Cascade boundary audits are non-obvious; each threshold needs exact-value verification, not just range checks. Pre-existing latent NRE noted (PrimaryResource.Title dereference in fallback path); not blocking, file follow-up. Zoe notes: smart-resource inclusion (Captain's design call), wordsByResource semantic shift (intentional, document for archaeology), grammar nit "1 days" (non-blocking). Decisions inbox: vocab-bootstrap-review.md, wording-fix-review.md.
