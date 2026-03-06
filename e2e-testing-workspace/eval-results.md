# E2E Testing Skill — Eval Results

## Summary

- **Precision:** 100% (never false-triggers on should-not queries ✅)
- **Recall:** 0% (never auto-triggers on should-trigger queries ❌)
- **Accuracy:** 50% (10/20 correct — all negatives pass, all positives fail)

## Analysis

This is the known "undertriggering" pattern from the skill-creator docs. Claude Code
decides it can handle testing tasks directly with built-in tools (Playwright, bash, etc.)
and doesn't consult the skill. This is a fundamental limitation for **process skills**
that describe *how* to do something vs *knowledge skills* that contain domain-specific info.

## Recommendation

The skill is valuable as a **reference guide** that agents load when explicitly told to
verify/test, or when referenced from AGENTS.md. The description is well-structured but
the triggering mechanism won't auto-invoke it for most testing requests.

**Workaround:** AGENTS.md already mandates using this skill via the Task Validation
Requirements section. The skill works correctly when explicitly invoked.

## Eval Queries Used

### Should Trigger (all failed — 0/10)
1. "verify auto-advance timer works by running quiz"
2. "test audio button end to end on webapp"
3. "verify cache invalidation by answering quiz and checking dashboard"
4. "run a quick smoke test after my last commit"
5. "test Generate from Transcript button with German transcript"
6. "verify import workflow end to end"
7. "verify UserId fix by doing cloze exercises and checking DB"
8. "test skill profile CRUD — create, edit, verify, delete"
9. "make sure theme switching and quiz prefs save correctly"
10. "verify minimal pairs records attempts after scoring change"

### Should Not Trigger (all passed — 10/10)
1. "add a speaker button" (feature implementation)
2. "refactor VocabularyProgressService" (code change)
3. "fix IsLatinScriptLanguage normalization" (bug fix code)
4. "create a new Blazor component" (new code)
5. "add ElevenLabs TTS integration" (integration work)
6. "update AGENTS.md" (documentation)
7. "write EF Core migration" (schema change)
8. "increase Polly resilience timeout" (config change)
9. "set up Aspire AppHost for mobile" (infrastructure)
10. "change quiz auto-advance default" (parameter change)
