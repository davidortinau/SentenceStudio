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

Now also includes `origin/main` at `c65e4657` (Known words), merged without conflicts.

**Review and checks, 2026-11-15 (commit `21a10484`):**

- Code review of the diff. Two fixes: the MAUI sign-in client retried a 429 through the MAUI
  HTTP defaults and would have timed out with a generic error, so `AuthClient` now has no
  resilience handlers (`AuthClientRateLimitTests`); the WebApp can use the managed identity for
  telemetry too (#168), and the runbook step names it.
- Tests: Api.Tests 5589 passed with a PostgreSQL 18 server in UTC (10 skipped: Azurite, Docker
  attestor, live identity), UnitTests 2538, Native.Tests 1543, UI.Tests 2642, AppLib.Tests 221,
  WebApp.Tests 47, `tests/js/webview-errors.test.js` 6. A PostgreSQL server in a local time zone
  fails 9 timestamp tests; start it with `-c timezone=UTC`.
- Running, on a clone of the local database (`sentencestudio-agent-issue-sweep-db-data`):
  - #89 API: the sixth login in a minute answers 429 with `Retry-After`; five wrong passwords
    lock the account and the correct password then answers 429.
  - #89 web: the locked account shows `Login_TooManyAttempts` in English and Korean.
  - #89 native iOS (fresh iPad simulator, iOS 27.0): the sixth sign-in shows the message in
    English and Korean.
  - #221 web: "이 김치는 정말 맛있어요." for "맛있다 (맛있어요)" counted toward mastery (live grading).
  - #169 MAUI iOS (simulator, Debug): a thrown error and a rejected Promise in the WebView were
    logged by `LoggingWebViewErrorSink`. The App Insights export is Release-only and was not
    exercised.

Not checked: Android and macOS sign-in screens for #89 (they share `NativeAuthPresenter`), and
the App Insights arrival for #169 and #168 (needs a Release build and the Azure step).

**Left to do:** the Captain's go to push and merge.

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
