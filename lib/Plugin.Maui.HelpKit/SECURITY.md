# Security policy — Plugin.Maui.HelpKit

## Reporting a vulnerability

**Preferred: GitHub private security advisories.**

Open a private advisory at
<https://github.com/davidortinau/Plugin.Maui.HelpKit/security/advisories/new>
(this repo once the library extracts; during incubation, file against
<https://github.com/davidortinau/SentenceStudio>).

Do not file a public issue for security problems.

If GitHub advisories are unavailable to you, email the maintainer listed in
the repository's `CODEOWNERS` or on the GitHub profile. Use a subject line
starting with `[HelpKit security]`.

## Response targets

- **Alpha**: best-effort. No SLA. Initial acknowledgement within 5 business
  days where possible.
- **Beta**: acknowledgement within 3 business days; triage within 5.
- **v1.0+**: acknowledgement within 2 business days; fix target per severity:
  - Critical (RCE, secret exfiltration, auth bypass): 7 days
  - High (data leak, prompt-injection with persistent effect): 14 days
  - Medium (hardening issues, defense-in-depth bypass): 30 days
  - Low (informational): next scheduled release

## Scope

In scope:
- Plugin.Maui.HelpKit library code and its dependency graph
- Supplied content filters (`DefaultSecretRedactor`) and their bypasses
- Ingestion pipeline (path traversal, ZIP-slip-like issues if we add
  archive support, malformed-markdown DoS)
- Citation validator bypasses (a fabricated citation reaching the user is a
  High finding)
- Storage layer (unauthorized cross-user history access, insecure default
  paths)
- Prompt-injection payloads that successfully override the system prompt via
  ingested content or retrieved chunks
- Rate-limit bypass
- Known-vulnerable transitive dependencies (CVE reports)

Out of scope:
- Bugs in the consumer's `IChatClient` / `IEmbeddingGenerator`
  implementation — those belong to the provider
- LLM hallucinations — the model is not in our trust boundary; HelpKit only
  validates citations
- Social-engineering attacks against maintainers or consumers
- Denial of service against the consumer's cloud LLM provider
- Self-inflicted insecure configuration (disabling the content filter,
  logging user content via a consumer-provided logger, etc.)
- Secrets committed to a consumer's documentation before ingestion — the
  filter is a speed bump, not a guarantee

## What to include in a report

- Affected version(s) and TFM(s)
- Repro steps or proof-of-concept
- Impact assessment (what an attacker gains)
- Suggested fix or mitigation if you have one
- Whether you want credit in the advisory

## Disclosure

We prefer coordinated disclosure. Default embargo: 90 days from acknowledgement
or until a fix ships, whichever is earlier. We will agree on a publication
date with the reporter before publishing the advisory.

## Hall of fame

Reporters who follow responsible disclosure are credited in the published
GitHub security advisory and in the CHANGELOG for the release that contains
the fix, unless they ask to remain anonymous.
