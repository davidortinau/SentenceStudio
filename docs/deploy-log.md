# Deploy log

What production ran, and since when. One line per production deploy, oldest first, written by
`scripts/prod-deploy.sh after` when its post-deploy checks pass (so `deploy` writes it too).

Each line gives:

- the time the API's new revision was created, in UTC, as Azure reports it. That revision
  answers from a minute or two after that time, so a turn within a few minutes of it may have
  been answered by either build;
- the commit that was deployed, and its subject. `git show <commit>:<path>` reads the code
  that build ran, and `git merge-base --is-ancestor <fix> <commit>` says whether it had a fix;
- the name of the API revision in Azure.

Production runs a line's commit from its time until the next line's time.

The script appends the line and does not commit it: commit and push it with the next push,
because a reader finds it on `main`. Do not reorder or reword the lines; the script adds each
new one at the end of this file, and skips a revision that is already here. A deploy made
another way (the runbook's steps by hand, or `aspire deploy`) is added by hand in the same form.

Before this log, deploys were written up one by one, and not all of them. Those records are
`docs/release-2026-11-native-platform-heads.md` (2026-11-05, 2026-11-06, 2026-11-12),
`docs/handoff-lilliput.md` (2026-11-14) and `docs/admin-portal-plan.md`, "Deployed"
(2026-11-16). Production was deployed after 2026-11-16 and before the first line below (it
ran the 2026-11-24 fix for issue 258 on 2026-11-25); those deploys are not recorded anywhere
under `docs/`.

A deploy whose post-deploy checks failed has no line until `after` passes: if production
keeps that build meanwhile, the log is behind by that deploy. The same holds when the checks
passed and the line could not be written (this file cannot be written, the disk is full):
`after` says so, leaves this file as it was and exits non-zero, and rerunning it adds the line.

## Deploys

