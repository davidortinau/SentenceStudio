# Support policy — Plugin.Maui.HelpKit

## Supported target frameworks

HelpKit ships binaries for these TFMs:

- `net10.0-android`, `net10.0-ios`, `net10.0-maccatalyst`, `net10.0-windows10.0.19041.0` — **transitional incubation grace target** so SentenceStudio dogfood (net10) can ProjectReference HelpKit. Will be dropped at Alpha extract; do not depend on these TFMs for community use.
- `net11.0-android`, `net11.0-ios`, `net11.0-maccatalyst`, `net11.0-windows10.0.19041.0` — **forward-looking primary**. Opt-in at build time via `-p:IncludeNet11Targets=true` while we incubate against the net10 SDK.

No `netstandard2.x`. No `net8.0`, `net9.0`. The net10 grace target exists for one consumer (SentenceStudio incubation) and will be removed when HelpKit extracts to its own repo. See `.squad/decisions/inbox/zoe-helpkit-multitarget.md`.

## Supported host frameworks

- .NET MAUI on the TFMs above
- Shell, plain `NavigationPage`, and MauiReactor hosts via `IHelpKitPresenter`

MauiReactor is a first-class host because SentenceStudio uses it, but HelpKit
itself has no hard dependency on MauiReactor.

## Support policy by release stream

| Stream | Response | Fix | Breakage |
|---|---|---|---|
| Alpha (`0.x.0-alpha.*`) | Best-effort via GitHub issues. No SLA. | None promised. | APIs CAN break between alphas. |
| Beta (`0.x.0-beta.*`) | Response target: 5 business days for bugs, 10 for features. | Fix target: next beta for regressions. | We try not to break. Breaks require a migration note. |
| v1.0+ | Response target: 3 business days for regressions, 5 for other bugs. | Fix in next patch for regressions; next minor otherwise. | Strict semver. Breaks only in major versions. |

No paid support tier. This is a community-maintained MIT library. Sponsorship
via GitHub Sponsors is welcome once the standalone repo is live.

## Breaking-change policy

- **Alpha**: anything can change. Read the CHANGELOG before upgrading.
- **Beta**: breaking changes require a CHANGELOG entry under a
  `### Breaking` subheader plus a migration note in `docs/migrations/`.
- **v1.0+**: strict [semver](https://semver.org/):
  - Patch (`1.0.x`) — bug fixes only, no API surface change
  - Minor (`1.x.0`) — additive APIs, no removals
  - Major (`x.0.0`) — breaking changes permitted with migration notes

## Deprecation workflow

1. Mark the symbol with `[Obsolete("Use X instead. Removed in vN.", false)]`
   in the first minor release where the replacement lands.
2. Bump the `Obsolete` error flag to `true` in the next major release.
3. Remove the symbol in the major release after that.
4. Migration notes live in `docs/migrations/vN.md`.

## Security

See [SECURITY.md](SECURITY.md). Prefer GitHub private security advisories
for reporting vulnerabilities.

## Long-term support strategy

HelpKit tracks the current .NET LTS that overlaps with the MAUI LTS train.

- When the `net11` LTS ships (late in the net11 lifecycle), HelpKit will
  commit to LTS backports for security and regression fixes for the remaining
  LTS window.
- Pre-LTS net11 previews receive best-effort support only.
- Non-LTS .NET releases (odd-numbered STS) are supported at the current
  release stream's SLA but not backported.

## Pinning guidance

- Pin exact alpha versions (`0.1.0-alpha.12`) — floating ranges will surprise
  you because alphas can break.
- Pin minor floor + major ceiling for Beta (`[0.2.0,1.0.0)`).
- Pin major floor + next-major ceiling for v1.0+ (`[1.0.0,2.0.0)`).

## What is and is not in scope

In scope:
- The HelpKit library APIs, DI extensions, overlay UI, storage schema, and
  ingestion pipeline.
- Compatibility with current-preview and current-stable .NET 11 SDK + MAUI
  workload.

Out of scope:
- Bugs in your registered `IChatClient` or `IEmbeddingGenerator` implementation.
- Hallucinations from the LLM. HelpKit validates citations; it does not
  re-check factual content.
- Custom `IHelpKitPresenter` implementations written by consumers.
- Performance of `sqlite-vec` or other opt-in storage backends once shipped
  as companion packages — they will have their own support docs.
