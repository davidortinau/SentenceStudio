# Working an issue from the feedback check

Your instructions when the feedback check hands you one GitHub issue to diagnose and resolve.
The Captain set this expectation on 2026-11-16: the check triages the flagged answers, files the
issues, **and then performs the diagnosis and the resolution, going as far as it can**, and
delivers through a pull request that carries a log of what was done. How the check finds and
files issues is in `docs/admin-feedback-check.md`.

Read `AGENTS.md` first. It governs everything here. This document adds what an unattended
session on Lilliput needs to know and where it must stop.

## What you are given

- One issue number in `davidortinau/SentenceStudio`. Read it with `gh issue view <n>`.
- Your own git worktree, already created from `origin/main` on the branch
  `feedback/issue-<n>`. It is your working directory. Its `src/NuGet.config` is a local copy
  (Lilliput reaches only a package mirror) and is hidden from git; never commit it.
- A file to keep your activity log in (its path is in your prompt).

The issue was written by the triage step from a learner's flag. It names admin portal report
ids. You may read a report's codes with `scripts/admin-api.sh sam-show <reportId>` (when it
failed, how the turn ended, which tools ran). **You may not open the flagged exchange or a saved
screen state** (`sam-exchange`, `screen-state`): the session that edits code does not read
learner content. If you cannot make progress without the learner's exact words, say so in your
result and stop; the Captain will read them.

## The work

1. **Restate the problem** in one or two sentences at the top of your activity log.
2. **Diagnose before changing anything.** First find out which build answered the learner.
   `sam-show` gives the time of each flag, and a flag can only follow its turn. The deploy
   records under `docs/` say what production ran then
   (`git log -i --grep=deploy --date=short --format='%ad %h %s' -- docs` lists them). A flag
   can be older than the deploy that fixed what it reports: both reports behind issue 226
   were, and `main` already had the fix. Read the code as it was in the build that answered
   (`git show <commit>:<path>`), then as it is now, and find the cause. Prefer a failing
   test that reproduces it. Say plainly what you know and what you are guessing.
3. **Fix the cause**, with a regression test that fails before the fix and passes after it.
   Keep the change as small as the cause allows. Match the surrounding code.
4. **Check it.** Run the focused tests, then the test project the change belongs to. Review
   your own diff as a reviewer would before you push, and fix what you find.
5. **Open a pull request** against `main` (see below), comment on the issue with what you found
   and the pull request's address, and finish.

Go as far as you can. If you can only get part of the way, a pull request with the diagnosis
and a failing test, or a comment on the issue with the diagnosis and the options, is a good
result. An honest "not found, here is what I ruled out" is a good result. So is "already fixed
and deployed": give the flag times, the deploy record and the commit that fixed it, run the
tests that cover it, and say what is left for the Captain to confirm on a device. Do not invent
a change to have something to deliver. A guess presented as a fix is not a good result.

## The activity log

Keep it as you go, not at the end: one short entry per step, with the time, what you ran or
read, what you found and what you decided. Include dead ends. It is the history of your
process, so write what happened, including what went wrong. Read the clock when you write an
entry (`date -u +%H:%M`); the first two runs both wrote times they had not read and had to
correct them.

The pull request carries it under a heading "Activity log". If the log is longer than about
30,000 characters, put a faithful summary in the pull request and the full log in comments on
it. Nothing a learner wrote goes in it (you have read none).

## The pull request

- Branch `feedback/issue-<n>`, base `main`. Push only that branch.
- Title: what the change does. Body: the problem, the cause, the fix, how it was checked (the
  commands and their results, including anything that failed or was skipped), anything that
  needs the Captain's decision, then "Activity log". Refer to the issue with `Fixes #<n>` when
  the change resolves it and `Refs #<n>` when it does not.
- Do not merge it. Merging and deploying are the Captain's.

## Where you stop and hand to the Captain

Do the diagnosis, write up the options, and stop (a pull request marked as a draft, or a
comment on the issue) when the resolution needs any of these:

- A change to what Sam says or does for a learner: prompts, responses, hints, modes, defaults,
  empty states. That goes through the Learning Value Gate and the Captain. A draft pull request
  with the proposed change and its reasoning is welcome; say clearly that it is a proposal.
- A schema change or a migration.
- A new dependency, a new capability for Sam, or a change of architecture.
- Anything in production: settings, data, a deploy.

## Hard limits on this machine

- **Never build, test or run anything in `~/work/SentenceStudio-native-platform-heads`.** The
  Captain's running stack loads its binaries from there. Work only in your own worktree.
- **Never touch the Captain's running stack or its containers** (`db-ceb81fcb`,
  `storage-ceb81fcb`, anything you did not start). Never run `aspire stop --all`.
- No Azure commands (`az`, `azd`), no production database access, no deploy scripts.
- No push to `main` or `native-platform-heads`, no force push, no history rewriting, no merge.
- Do not comment on, close or edit any issue but the one you were given, and do not touch other
  repositories.
- Build with `-p:IncludeMobileTargets=false`. Name the project and target framework.
- The API tests that need PostgreSQL read `COACH_PG_TEST_CONNECTION` and
  `APP_OPERATION_PG_TEST_CONNECTION`; a scratch server runs in the container `ss-test-pg` on
  port 55432 (user and database `postgres`; its password is in the container's own environment).
  Without them those tests skip, which is acceptable; say that they were skipped.
- Running the app is optional and expensive. If the diagnosis truly needs it, follow "Running a
  web check on Lilliput" in `docs/admin-portal-plan.md` (an isolated stack with its own
  volumes, stopped and removed afterwards) and use only the accounts `e2e@test.local` and
  `testsailor@test.local`.
- After 30 minutes on one blocker without progress, write down where you are and try another
  angle; after three failed attempts at the same thing, stop and report.

## Your reply

Your final reply is read by the runner and shown to the Captain. Its first line is exactly one
of:

- `RESULT: pr <address>` when you opened a pull request (say in the next line whether it is a
  fix, a partial fix or a proposal);
- `RESULT: diagnosed` when you found the cause, or narrowed it, and left a comment on the issue
  without a pull request;
- `RESULT: stuck` when you could not make progress (say what would unblock it);
- `RESULT: failed` when something went wrong with the tools or the machine.

Then, in a few plain lines: the cause, what you changed, how it was checked, and what the
Captain needs to decide.
