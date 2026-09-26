# RAI Policy

> Responsible AI policy for SentenceStudio. Rai enforces these standards; `.squad/templates/rai-policy.md` is the upgrade template, not the project policy.

## Principles

1. **Safety first** — No output should cause harm to individuals or groups.
2. **Transparency** — Users should know when content is AI-generated.
3. **Fairness** — Systems should not discriminate based on protected characteristics.
4. **Privacy** — Handle learner and account data with minimal exposure and explicit consent.
5. **Accountability** — Every finding has an owner and a remediation path.

## Critical Violations (Red — Always Blocked)

- Hardcoded passwords, API keys, tokens, private keys, or credential-bearing connection strings. A public telemetry ingestion identifier is not an authentication secret; review its exposure separately.
- SQL injection, command injection, or path traversal from unsanitized input.
- Hate speech or content promoting violence, self-harm, or exploitation.
- Fabricated citations or measurements presented as verified facts; instructions that bypass safety rules.
- Cross-tenant learner-data exposure, destructive data recovery without its required safeguards, or unconsented deletion of device data.

These cannot be opted out of or shipped without remediation and re-review.

## Advisory Concerns (Yellow — Flagged, Not Blocked)

- PII in logs or responses; excessive collection or unclear retention.
- Demographic bias or proxy attributes in learning recommendations without justification.
- Exclusionary language, insufficient contrast, missing alt text or accessible labels.
- Missing rate limits, overly permissive CORS, or insufficient validation on public endpoints.

## Terminology

Prefer `allowlist/blocklist` over `whitelist/blacklist`, `primary/replica` over `master/slave`, `validation` over `sanity check`, and inclusive terms over gendered shorthand.

## Review Scope

| Change type | Minimum review |
|-------------|----------------|
| App or API feature | Privacy, safety, credentials, injection, accessible content |
| Bug fix | Credentials, injection, affected data boundaries |
| Documentation | Claims, content, terminology |
| Tests or configuration | Credential exposure and sensitive data |
| Dependency update | Always check credentials, package provenance, known advisories, and newly introduced dependency risk; skip only unrelated full-suite checks when those gates pass |

SentenceStudio has a multi-tenant ASP.NET Core API and WebApp, plus .NET MAUI heads with device-local SQLite; review each named runtime surface without substituting a different one. Never include a learner's raw data, cookies, credentials, or secret values in a verdict.

## Escalation and Audit

- **Green:** No findings; work proceeds.
- **Yellow:** Findings are advisory; attach remediation recommendations.
- **Red:** Block shipping. Apply the Reviewer Rejection Protocol: the original author does not revise their rejected artifact, Rai names a fix agent, pairs on remediation, and re-reviews.
- Record only the file path/range, category, severity, fingerprint where needed, and remediation status in `.squad/rai/audit-trail.md`; never paste raw secrets, harmful content, or PII.
- Changes to this policy need team acknowledgment through `.squad/decisions/inbox/` and an append-only audit-trail entry. Red checks cannot be disabled. Advisory checks may be temporarily opted down with a recorded justification and automatic re-enablement after 30 days.
