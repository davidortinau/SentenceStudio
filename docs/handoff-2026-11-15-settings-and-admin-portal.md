# Handoff, 2026-11-15: Settings work shipped to main; admin portal planned

## On `main` (aff928c8), not deployed

- iOS Settings is a split view; Android Settings is a list-detail layout. Both have About
  (version and What's New), and Logout is the last row of Profile.
- One product version for the web UI and the native heads: `src/SentenceStudio.Version.props`
  (1.2.0). Release notes `docs/release-notes/v1.2.md` are a draft.
- The web What's New button finds its notes again (B93 in `docs/native-port-backports.md`).
- Production still runs api--0000042, built before these changes and before the #221 fix.
- Duotopia has native build 30dd82ad: it has the split view and About, but not the Logout move
  or the later merges from `main`.

## Waiting on the Captain

1. Read the 1.2 release notes draft before the web deploys (the What's New popup shows them).
2. Deploy `main` to production (carries the #221 fix).
3. Sam's GitHub reports, all open:
   - #225 (2026-11-15): landscape quiz summary buttons off screen; and show quiz results only
     by highlighting the choices. Not started. The second part changes quiz feedback, so it
     needs the Learning Value Gate and a decision for typed answers.
   - #223, #222 (2026-11-14): merging duplicates across languages. The rules still keep copies
     apart when language, kind or part of speech differ; no override exists.
   - #221: fixed on `main`, not deployed.
   - #217: superseded by the one-Save rule. Android Profile and YouTube channel still keep Save
     inside the page (`docs/toolbar-save-audit-2026-11-15.md`).
4. Sam's flagged answers in production have never been read. A one-time read through
   `scripts/prod-db-access.sh` was offered and not approved; the admin portal replaces it.

## Admin portal

Plan, decisions, team and status: `docs/admin-portal-plan.md`. Branch `admin-portal`. Next is
phase 0. The Captain's decisions are also in Squad state.

## Local environment (Lilliput)

- Backend: the AppHost at `http://localhost:5081` was up all day.
- iPad simulator "SS iPad" and the Android emulator `SS_Fold` are signed in as
  `testsailor@test.local`. The emulator may still be running.
- Android Debug builds need the two `-p:` flags recorded in the memory note for Lilliput.
  Ailoha reads the Android tree but its taps and typing fail; drive it with `uiautomator dump`
  and `adb shell input`.
- Five `SentenceStudio.Native.iOS.Tests` fail on `main` and did before this work
  (`docs/native-review-backlog.md`, "Stale iOS source-contract tests").
