# Zoe — History Summary

**Summarized by Scribe:** 2026-07-17T20-10-00-0500
**Project:** SentenceStudio — .NET MAUI Blazor Hybrid language learning app
**Role focus:** product architecture, learning-value review, release gates, dogfooding strategy.

## Durable directives and architecture patterns

- Tooling friction outranks app feature velocity. MAUI, Aspire, DevFlow, Hot Reload, Blazor Hybrid, and build-tool blocks should be root-caused and documented/upstreamed rather than worked around silently.
- Challenge non-idempotent maintenance endpoints and one-shot repair paths; prefer scoped EF/script repairs to permanent broad endpoints.
- Use isolated git worktrees for parallel agent work to avoid shared-checkout state loss.
- Dynamic platform features in shared UI should use runtime type resolution/reflection so browser UI stays portable and degrades gracefully when MAUI-only types are absent.
- DailyPlan and focus vocabulary are cross-device product state; plan preview must project the same focus set activities consume.

## Learning-value guardrails

- Learning-activity UX changes route through Zoe. Apply the Learning Value Gate: no reachable native-prompt/native-answer zero-target-language state, no toggle may hide target-language artifacts, defaults must be safe, and acceptance tests must enumerate reachable language-role rows.
- Photo/text preference incident produced a durable guardrail, a 7-row quiz acceptance matrix, and the exact-default-state regression-test pattern.
- Native text reveal during Vocab Quiz feedback is approved only after retrieval: hidden pre-answer, revealed for correct/incorrect feedback, reset on next item, and target-language invariance preserved.
- Sentence hints are target-language only, optional, max three, reset-safe, CEFR-ranked with honest fallback, and never expose native translations or answer-side artifacts.

## Recent release and validation decisions

- Simulator evidence can close optional physical-device gates when Captain explicitly accepts it; physical-device availability does not authorize optional validation.
- Photo viewer WebView architecture was finalized from WebApp + iOS simulator gesture evidence; native prototype remains DEBUG-only/no-release default.
- Cross-profile disclosure and timer isolation release gates were approved after review/verification; Simon owns the hardening layer follow-up.
- Main CI green fix-forward patterns: use `IncludeMobileTargets=false` for CI server/test lanes, disable machine-local `localnugets` in CI via `dotnet nuget disable source`, and guard gitignored AppLib `appsettings.json` items with `Exists(...)`. Baseline correction: the stale 534/535 note is obsolete; this CI arc verified 979/979 passing.

## Carry-forward

- Review docs-only updates that correct TFM/toolchain guidance when they arrive.
- Review WebAppPlanDateContext integration tests when Jayne files them.
- Preserve optional-vs-required gate classification in future cross-platform validation.
