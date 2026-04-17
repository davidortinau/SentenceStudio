# Plugin.Maui.HelpKit

Plugin.Maui.HelpKit is incubating inside this repository at
[`lib/Plugin.Maui.HelpKit/`](../../lib/Plugin.Maui.HelpKit/) during Alpha.

For everything about the library — install, quickstart, options, honest
messaging FAQ, roadmap — see the library README:

- [Library README](../../lib/Plugin.Maui.HelpKit/README.md)
- [Changelog](../../lib/Plugin.Maui.HelpKit/CHANGELOG.md)
- [Support policy](../../lib/Plugin.Maui.HelpKit/SUPPORT.md)
- [Security policy](../../lib/Plugin.Maui.HelpKit/SECURITY.md)
- [Contributing](../../lib/Plugin.Maui.HelpKit/CONTRIBUTING.md)
- [Extract-to-standalone-repo runbook](../../lib/Plugin.Maui.HelpKit/EXTRACT-RUNBOOK.md)

At Alpha close the library moves to
`https://github.com/davidortinau/Plugin.Maui.HelpKit` and SentenceStudio
consumes it via NuGet.

## Why it lives here today

The public API is still settling. Incubating inside SentenceStudio lets us
iterate on real consumer code without publishing breaking alphas to NuGet.
See `.squad/decisions.md` for the full Alpha scope rationale.

## CI

The library has its own workflow at
[`.github/workflows/helpkit-ci.yml`](../../.github/workflows/helpkit-ci.yml).
It runs on any PR touching `lib/Plugin.Maui.HelpKit/**` and on pushes to
`main`. Matrix covers all four supported TFMs plus a unit-test job and the
eval gate (>=85% correct, 0 fabricated cites).
