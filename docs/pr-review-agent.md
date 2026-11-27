# The pull request reviewer

A review agent that runs on GitHub, reads one pull request and tells the Captain what to do
with it: merge it, have it fixed first, or decide something himself. Built 2026-11-26 at his
request: "One outcome of that agent should be a merge recommendation that I can easily see and
act on. Any change feedback should be addressable by Lemuel."

The agent is `.github/workflows/pr-review.md` (compiled to `pr-review.lock.yml`). The feedback
check's side of it is in `docs/admin-feedback-check.md`, "The review", and what a fix session is
told is in `docs/admin-feedback-fix.md`, "The review".

## What he sees and does

| Recommendation | Where it shows | What he does |
| --- | --- | --- |
| **Merge** | Check `Merge recommendation` passed; review starts "## Merge recommendation: Merge"; status issue line starts "Review: merge" | Merge when the other checks are green. |
| **Fix first** | Check failed, titled "Fix first: 2 to fix"; the review lists each fault; status issue "Review: fix first (2 to fix)" | Nothing: on a fix session's pull request a fix session works every item by itself, then the reviewer reads the result, at most twice. After that, or on a pull request of his own, `/revise review` starts a pass. |
| **Your call** | Check "action required", titled "Your call: 1 to decide"; the review states each choice | Answer in a comment that starts with `/revise` (`/revise decision 1: yes, and fix the rest of the review`), or merge as it is. |
| Not reviewed | Check neutral, titled "Not reviewed", with the reason | Nothing, or `/review` once the cause is gone. |

- The check is on the pull request's head commit, so it is in the checks list on the pull
  request page and in the GitHub app. A commit pushed later has no such check until the
  reviewer has read it. Its title names the commit it read ("Merge (at 16ce7bba)").
- The review is one comment-only review. It never approves and never requests changes, so it
  never blocks the merge button and leaves nothing to dismiss.
- The status issue (#242) leads each pull request's line with the recommendation, and only
  repeats it plainly when the check's title names the head commit and the reviewer's own
  review of that commit says the same. It is the place to trust; see "Can the recommendation
  be wrong about which commit" below.

## When it runs

- **By itself**, for a fix session's pull request: the feedback check starts it when the
  session (or a `/revise` pass) has ended and the runner has posted what it checked.
- **`/review`** in a comment on any pull request, by someone with write access.
- **From the Actions tab**: "PR review", Run workflow, with the pull request's number.

GitHub starts the workflow for every comment anywhere in the repository, which is how it hears
`/review`; a comment that is not a request ends at once as a skipped run, in a slot of its own
so that it cannot cancel a review under way.

It does not start on "pull request opened". A fix session's pull request is opened by the
sessions' GitHub App, and `gh aw` checks who started a run against collaborator roles, which an
App never has. Starting it from the runner also gives it more to read: the session has ended,
so the commits no longer move, and the runner's own check of the proof is already posted.

## How it is built

It is a GitHub Agentic Workflow (`gh aw`, github/gh-aw v1.2.2), the same tool the reviewers in
dotnet/maui, dotnet/maui-labs, dotnet/macios and dotnet/android use.

1. **Gather** (a plain step, no model): the pull request's description, diff, commits, the
   issue it closes, the Captain's own comments on it that were never edited (the runner posts
   "What the runner checked" and the "Next" list under his login), the checks on the head
   commit and on `main`, the lines of each failed check's log that name what failed, and the
   reviewer's own earlier reviews. All of it goes into files, and so does a list of whatever
   could not be read, so that "not read" is never taken for "there is none".
2. **Review** (the model, in a sandbox, with read-only tokens): it reads those files and the
   repository, and can run `git` and a few read-only commands. It builds and runs nothing.
3. **Post** (a separate job with write access, no model): it posts what the model asked for,
   and only what the workflow allows: review comments on the diff, one comment-only review,
   one check named `Merge recommendation`, each on the pull request the run was started for.

The rules it reviews by are the text of `pr-review.md` below the front matter, plus `AGENTS.md`
as merged. Changing that text needs no compile; changing the front matter does
(`gh aw compile pr-review --strict`, then commit both files).

## What was taken from the dotnet reviewers, and what was not

| Their practice | Here |
| --- | --- |
| `gh aw` with a read-only agent and a separate posting job (all four) | Same. |
| `/review` by a maintainer (maui, macios, android) | Same, plus the runner's own start. |
| Review on "opened" (maui-labs) | Not possible for an App's pull request; the runner starts it instead. |
| Comment-only review, never approve (all four) | Same. |
| A verdict in the summary: LGTM, needs changes, reject (macios, android); a recommendation and labels (maui) | A verdict in the summary and as a check. A check, not a label: the sessions' App can set labels, it cannot write checks. |
| Read whole files, verify the description's claims, read CI (macios, android) | Same, and the gather step hands over the failed tests by name. |
| `@copilot` at the start of the summary so the authoring agent picks it up (macios, android) | The runner hands a "Fix first" to a fix session by itself, at most twice on one pull request; `/revise review` by the Captain does it on his word. |
| Three models and a vote (maui-labs); frozen inputs and four passes (maui) | One pinned model, one pass. A second opinion can be added if single reviews prove unreliable. |
| Methodology in a skill file read at run time (macios, android) | In the workflow's own text, which is always read from `main`. |
| Emoji severity marks | None; this project uses no emoji. |

## What it may and may not be trusted with

- **The pull request is data.** Its description, code, comments and test output may have been
  written by a session that was misled. The reviewer is told so, and told to report text that
  tries to steer it.
- **A pull request cannot rewrite its reviewer.** The workspace stays on `main`; the pull
  request's commits are fetched and read with `git show`, never checked out. The prompt and
  `AGENTS.md` are the merged ones.
- **What reaches it besides the pull request itself**: the owner's comments on it that were
  never edited, the issue it closes when the owner or the app's feedback intake filed that
  issue, check results, and the lines of a failed check's log. A passer-by's comment does
  not, by either road: the gather step leaves it out, and the model's own reads of GitHub are
  held to the owner's and collaborators' text (`min-integrity: approved`).
- **It cannot change anything.** The model's job holds read-only tokens. The posting job can
  only post the three things listed above.
- **Whose word starts a fix.** The review is an agent's text. Since 2026-11-26 a "Fix first"
  reaches a fix session without the Captain's comment, on his standing instruction, at most
  twice on one pull request (`docs/admin-feedback-check.md`, "The review", has the
  conditions); a "Your call" never does. Either way it is handed over as literal text marked
  as the reviewer's, and only a review posted by `github-actions[bot]` whose check on the same
  commit agrees is taken, and only when GitHub says nobody edited it after it was posted. A
  review talked into a false finding can therefore cost a pass on a branch only he merges,
  which the reviewer then reads again. What can talk to it: the pull request, the output of
  its failed tests, and the issue's text, which for an issue from the app's feedback button
  is a learner's own words.
- **Can the recommendation be forged, or be about the wrong commit?** Two ways are known,
  and the status issue guards against both; GitHub's own pages do not.
  - GitHub hangs a check on whatever the pull request's head is when the check is posted. A
    commit pushed to the branch while a review runs (about six minutes) gets the check of
    the commit before it. The reviewer writes the commit it read into the check's title and
    into its review ("Reviewed commit 16ce7bba"); the runner reads both and says so when they
    do not name the head. That rests on the model's own word for which commit it read: a
    pull request that talked the reviewer into naming another commit would get past it.
  - The sessions' App cannot write checks or change workflows. But `ci.yml` runs a pull
    request's tests with a token that can write checks (`checks: write`, and
    `actions/checkout` leaves that token in `.git/config`), so test code that went looking
    for it could post a check of the same name. That token cannot post a review, and the
    runner wants the reviewer's review to say the same before it repeats a check. Closing the
    path itself means `persist-credentials: false` on the checkouts in `ci.yml` and moving
    the test report to a job that runs no pull request code. Not done here.
- **The gather step reads GitHub itself**, not through `gh aw`'s filter for such steps
  (`integrity-proxy: false`): the filter would also drop the failed jobs' logs and an issue
  the sessions' App filed. The step keeps only what may be handed over, as files of data.

## Wiring it up

Done in the pull request that added this file: the workflow and its compiled file, the
runner's side, the documents and the tests.

For the Captain, once:

1. **A token for the model.** The reviewer runs on GitHub Copilot. For a personal repository
   that takes a fine-grained personal access token of his own with one permission, Copilot
   Requests (read), stored as the repository secret `COPILOT_GITHUB_TOKEN`:
   - Create it at
     https://github.com/settings/personal-access-tokens/new?name=COPILOT_GITHUB_TOKEN&description=GitHub+Agentic+Workflows+-+Copilot+engine+authentication&user_copilot_requests=read
     (resource owner: his user account, not an organization).
   - Store it: `gh secret set COPILOT_GITHUB_TOKEN --repo davidortinau/SentenceStudio`, and
     paste the token when asked. It is never shown again and belongs nowhere else.
2. **Merge** the pull request to `main`. A workflow can only be started from `main`.
3. **Try it**: comment `/review` on an open pull request. A run takes about six minutes.

Nothing else: no label, no branch rule, no repository setting.

## Cost

About 37 of Copilot's AI credits and six and a half minutes a review, measured on pull
request 311 (2 files, +176 -13) with the pinned model: 35 for the review, 2 for the check on
its output. One review per fix session and one per `/revise` pass.

## The first live reviews (2026-11-26)

Both of pull request 311 at the same commit, and both said Merge.

| | Model | The model's part | Credits | What it read |
| --- | --- | --- | --- | --- |
| First | Copilot's own choice (`auto`, which took Claude Sonnet 5.5) | under 2 minutes | 25 | the diff and one function's callers |
| After tuning | `gpt-6.1-sol`, pinned | under 4 minutes | 37 | both changed files whole and twelve more, to the reply the learner sees and the stored history |

The first did not mention that two cases the change leaves alone on purpose end in a refused
answer (a general answer with a message plus a direct-answer part gets two direct answers
from the shape repair, and the projection rejects that), while the new tests for them assert
only the number of model calls. The second named both, as older faults that do not block the
change. Two local trials before the merge had found the first of them too.

What changed between the two: the model was pinned, and the instructions gained a step
("follow each case to what the user gets") and the rule that a Merge has to be earned by
that read, with the files read named in the review.

## Tuning it

- **It finds too much or too little**: edit "The project's rules" and "Findings" in
  `pr-review.md`. No compile needed.
- **A different model**: `model:` under `engine:` in the front matter, then compile. It is
  pinned to `gpt-6.1-sol`, a model from a different maker than the fix sessions', so the read
  is not anchored on theirs; `fallback-models` are tried in order when Copilot does not offer
  it or a request to it fails. Left to `auto`, Copilot chose a quick model that read only the diff (see "The first
  live reviews"). The check on the reviewer's output before posting keeps the tool's small
  model (`safe-outputs.threat-detection.engine`).
- **Stop the runner asking**: `FEEDBACK_REVIEW=off` for the check. `/review` still works.
- **Stop a "Fix first" starting a fix pass**: set `FIX_ROUNDS = 0` in
  `scripts/admin-feedback-pr.py` and merge (the job runs what is on `main`), or set
  `FEEDBACK_REVIEW_FIX_ROUNDS=0` in the job's environment. The number is how many such passes
  one pull request gets. For one pull request: make it a draft.
- **`gh aw` on Lilliput**: `gh extension install github/gh-aw` fails there twice over: the
  signed-in token is refused by the `github` organization until single sign-on is authorized
  for it, and `~/.local` belongs to root. The release binary (checksum verified against the
  release's `checksums.txt`) runs by itself: `./darwin-arm64 compile pr-review --strict`.

## Not built, and why

- **An approving review.** GitHub can let Actions approve pull requests (a repository
  setting). The check says the same thing without that setting and is tied to the commit.
- **Reading the app.** The reviewer builds nothing and sees no screen. What a change looks
  like on a phone is still his to look at, and the session's proof is still the runner's to
  verify.
