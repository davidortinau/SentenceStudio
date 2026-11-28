# App install log

What each of the Captain's phones runs, and since when. One line per install of the native
iOS Release app, oldest first, written by `scripts/ios-phone.sh` once the install has
succeeded. It is the phones' counterpart of `docs/deploy-log.md`: a fault in what the server
answers is dated against the deploy log, a fault in a native screen against this one.

Each line gives:

- the time the install finished, in UTC, by this Mac's clock;
- the commit the app was built from, and its subject. `git show <commit>:<path>` reads the
  code that build ran, and `git merge-base --is-ancestor <fix> <commit>` says whether it had a
  fix. The script keeps the commit it built in the checkout's git directory, so the usual
  second run (`--yes --no-build`, after the Captain approves) names it even when the checkout
  has moved since the build. An app the script did not build itself (built another way, or
  built again since) is recorded as that checkout's `HEAD`, which the script cannot check
  against the app, and it prints a note to correct the line if that is not what was built;
- the phone, in brackets, and `built with uncommitted changes` when the build took in tracked
  changes that no commit holds: that app is the commit plus something this log cannot name,
  so do not conclude from the commit alone that it had or lacked a fix.

A phone runs a line's commit from its time until that phone's next line.

This is a history of installs, kept by the script that makes them, and not proof of which
build showed a given screen. It knows nothing of an install made another way (Xcode,
`xcrun devicectl` by hand, TestFlight), of the MAUI app, or of any other device, and a line
with no successor only says that this script has not installed on that phone since. A line's
commit may be on a branch that was never pushed: when `git cat-file -e <commit>` fails, the
log cannot say what that build had. Say so when a conclusion rests on it.

The script appends the line and does not commit it: commit and push it with the next push,
because a reader finds it on `main`. Do not reorder or reword the lines; the script adds each
new one at the end of this file. Every install gets a line, the same build installed again
included. An install made another way is added by hand in the same form. If the line cannot
be written, the script says so, leaves this file as it was, prints the line to add by hand
and exits non-zero; the app is installed all the same.

Nothing recorded the installs made before this log: the first line below is the first
install after `scripts/ios-phone.sh` began to write it.

## Installs

