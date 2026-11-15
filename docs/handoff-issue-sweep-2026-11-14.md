# Handoff: GitHub issue sweep, 2026-11-14

Three local branches, none pushed. Each has its own worktree on maxpro.

## `issue-sweep` (`~/work/SentenceStudio-issue-sweep`): six issues, tests pass

Includes `origin/main` at `6e234a35`, merged without conflicts.

| Issue | Commit | Change |
|---|---|---|
| #199 | `a4b7ecb5` | One Vocabulary Quiz attempt weight (`VocabularyMasteryCalculator.QuizAttemptWeight`) for web, MAUI, native and the test helpers. |
| #202 | `e4828d01` | `ClaimNameLiteralTests` fails on a claim name written as a literal. |
| #89 | `fce884dc` | Per-address rate limits on the auth endpoints, account lockout (API and WebApp), `Login_TooManyAttempts` on every client. |
| #221 | `07b030d7` | Sentence grading accepts terms with a parenthetical; `VocabQuiz.razor` uses the shared policy. |
| #169 | `aaf52c20` | Unhandled JavaScript errors in the MAUI WebView go to App Insights. |
| #168 | `7b1f8b0e` | Opt-in managed identity for the server's App Insights exporters. Off until `AzureMonitor:Authentication=ManagedIdentity`; the Azure step is in `docs/deploy-runbook.md` and needs the Captain's go. |

Observed on the merged branch: UnitTests 2518 passed, Api.Tests 5095 passed (473
PostgreSQL-only skipped, no server running), Native.Tests 1537, UI.Tests 2642,
`tests/js/webview-errors.test.js` 6.

**Still to do before a push:**

1. Code review of `git diff origin/main...issue-sweep` (it was stopped twice mid-run).
2. Running-app checks: #89 (sixth login in a minute shows the message on web and native iOS,
   English and Korean; five wrong passwords lock the account), #169 (a thrown JS error in the
   MAUI app reaches the log), #221 (a sentence for a term with a parenthetical is accepted).
3. The 473 skipped PostgreSQL tests, with a server up.

Restore note: `dotnet restore tests/<project> --configfile src/NuGet.config`. Without it the
user-level NuGet config adds an internal feed that answers 401.

## `issue-219-resource-history`: unfinished WIP (`8a1b4712`)

Study history on the resource page. The stopped agent left a shared
`ResourceStudySummaryService`, a Razor component, and iOS and Android sections. Not built as a
whole, not reviewed, not run. Cut from `2159f21d`; merge `main` first.

## `issue-218-toolbar`: unfinished WIP (`299daa22`)

Toolbar changes on Conversation, Scene, Vocabulary Matching and Sam, plus
`NativeToolbarOverflowContractTests`. The agent was stopped while checking the More button on a
simulator, so the fix for #218 is unconfirmed. The #217 audit of toolbar Save buttons was not
written up. Scratch files are in that worktree's `.task-scratch/issue218`.

## Also done this session

- Lilliput can deploy: azd environments copied, azd 1.37.3 installed and signed in, prod
  subscription selected, `prod-deploy.sh check` passed. azd 1.37 rejects a symlinked `.azure`;
  Lilliput's worktree holds a real copy.
- Issues triaged: 19 closed, the rest labelled `go:yes` or answered (#216, #220, #224).
