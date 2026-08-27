# Sam Opportunity Digest — production reviewer runbook

**Operational owner:** River (Backend/Operations) — owns the tool, the script, the workflow, and
the guard tests. Fix requests go here.
**Review owner:** Zoe (Product/UX) — reads the digest every week, triages, and records decisions in
[`docs/sam-future-opportunities.md`](./sam-future-opportunities.md).
**Approver:** Captain — signs off anything policy-gated per RFC §6.5 and the Learning Value Gate.

**Cadence:** weekly, Mondays. On-demand any time.

---

## Why this exists

`Coach:Reports:Enabled` ships **true** in Production. A learner who presses the flag control on one
of Sam's responses is told the report goes somewhere a person looks — so a person has to actually
look, or the sentence is false and every test still passes.

The obvious place to look is the operator review surface (`/operator/sam-opportunities`). It stays
**Development-only**: it can decrypt learner messages, and this codebase has no admin authorization
primitive. Shipping it to Production would mean inventing one under time pressure, which is the
trade the surface was gated to avoid in the first place.

This digest is the honest half. It answers everything triage actually needs — *what problem, how
often, how many learners, since when, what has been decided* — and it is structurally incapable of
answering *who*.

| Carried | Never carried |
|---|---|
| Content-free fingerprint | Learner text, coach text, any decrypted evidence |
| Kind, disposition, capability code, offer link | Owner / user profile id |
| Registered tool name, closed failure code | Conversation id, message id, turn id, write operation id |
| Occurrence counts, bucket counts | Emails, tokens, tool arguments |
| **Distinct learner count** (a number) | Any list of learners |
| Review statuses, first/last observed | Reviewer prose (there is no free-text column anywhere) |
| Learner-report counts by reason | Which learner filed which report |

The projection lives in `src/SentenceStudio.Api/Coach/Opportunities/Digest/CoachOpportunityDigestReader.cs`.
`tests/SentenceStudio.Api.Tests/Coach/Opportunities/CoachOpportunityDigestTests.cs` asserts the
guarantee against **the SQL the provider actually emits** and against a seeded fixture whose
identifiers must not appear in the rendered output — not against the comments above.

---

## What is switched on in Production

`src/SentenceStudio.Api/appsettings.Production.json`:

| Key | Value | Why |
|---|---|---|
| `Coach:Reports:Enabled` | `true` | The learner-facing control ships. |
| `Coach:Reports:RetentionDays` | `180` | A deployment that accepts reports has to have chosen how long it keeps them. |
| `Coach:Reports:RetentionSweepEnabled` | `true` | **Startup fails if this is false while reporting is on.** |
| `Coach:Opportunities:Enabled` | `false` | Automatic capture stays off — unchanged, still awaiting Captain's approval after `SAM-OPP-01…10`. |
| `Coach:Opportunities:RetentionDays` | `180` | Reports raise `UserReportedResponse` ledger rows **even with capture off**, so those rows must age out. |
| `Coach:Opportunities:RetentionSweepEnabled` | `true` | Same reason. |
| `Coach:Opportunities:OperatorSurface:Enabled` | `false` | Development-only; `CoachOpportunityOptionsValidator` fails startup otherwise. |
| `Coach:Opportunities:OperatorSurface:AllowCrossOwnerEvidence` | `false` | Development-only, and off there too. |

Both switches can be overridden per deployment without a redeploy —
`Coach__Reports__Enabled`, `Coach__Opportunities__Enabled`, and the two `__RetentionDays` keys are
forwarded by `AppHost.cs`. **The operator surface is deliberately not forwardable**: an environment
variable that could enable it is an environment variable somebody can set on the wrong host.

---

## Running it

### Locally (the normal path)

```bash
# last 7 days, markdown, to stdout
./scripts/sam-opportunity-digest.sh

# a wider window, written to a file (git-ignored — see below)
./scripts/sam-opportunity-digest.sh --days 30 --output .digest/sam-2026-08-24.md

# everything still retained, as JSON
./scripts/sam-opportunity-digest.sh --days 0 --json
```

Supply exactly one credential. Nothing is stored in this repository, nothing is echoed, and the
tool refuses to start rather than falling back to a default connection.

**1. A connection string you already have:**

```bash
export COACH_DIGEST_CONNECTION_STRING='Host=...;Database=sentencestudio;Username=...;Password=...'
```

**2. From Key Vault.** The production connection string is stored as
`connectionstrings--sentencestudio` in `dbkv-rsn72awybem6s` (the same secret
`docs/deploy-runbook.md` Step 6 pulls). Confirm the name before relying on it — this script hard-codes
none:

```bash
export COACH_DIGEST_KEYVAULT=dbkv-rsn72awybem6s
az keyvault secret list --vault-name "$COACH_DIGEST_KEYVAULT" -o table   # confirm the name
export COACH_DIGEST_KEYVAULT_SECRET=connectionstrings--sentencestudio
./scripts/sam-opportunity-digest.sh --days 7
```

**3. With an Entra token — no password anywhere:**

```bash
az login
export COACH_DIGEST_AZURE_IDENTITY=1
export COACH_DIGEST_HOST=db-rsn72awybem6s.postgres.database.azure.com
export COACH_DIGEST_DATABASE=sentencestudio
export COACH_DIGEST_USER=<your Entra principal, as configured on the server>
./scripts/sam-opportunity-digest.sh --days 7
```

Prerequisites for options 2 and 3: the Azure CLI, `az login` against subscription
`66f9fa8f-604f-4688-bec1-16ff9a86a8e5`, **VPN off** (management.azure.com times out on VPN — see
`docs/deploy-runbook.md`), and a firewall rule on `db-rsn72awybem6s` admitting your client IP:

```bash
az postgres flexible-server firewall-rule list \
  --resource-group rg-sstudio-prod-biz --name db-rsn72awybem6s -o table
```

### Safety properties

- **Read-only at the server.** The tool sends `default_transaction_read_only=on` as a PostgreSQL
  startup option on every pooled connection, so a write is refused by the database rather than by
  the program's good intentions.
- **Fixed queries.** Three aggregate queries built from LINQ with no raw SQL, no interpolated
  predicate, and no caller-supplied text — only a UTC instant and a bounded take.
- **No secret ever reaches a command line.** Credentials travel in the child process's environment,
  never in `argv` (where they would land in the process table) and never in shell history.
- **Truncation is reported, not hidden.** More than 500 distinct problems in a window renders a
  `**Truncated:**` line telling the reviewer to narrow the window.

### Do not commit the output

Write digests under a scratch path and keep them out of git. The digest is content-free, but an
artifact with a date on it is still an operational record and does not belong in source control.
`docs/sam-future-opportunities.md` is where the *decision* goes, by fingerprint.

---

## Running it in CI

`.github/workflows/sam-opportunity-digest.yml` runs `workflow_dispatch` (with a `days` input) and
weekly on Mondays at 13:00 UTC. It uploads `sam-opportunity-digest.md` as a 30-day artifact and
mirrors it into the job summary.

**It needs two prerequisites that are not configured today, and it says so rather than pretending:**

1. A repository secret `COACH_DIGEST_CONNECTION_STRING`. **When the secret is absent the job skips
   with a notice and exits 0.** It never fabricates a digest and never uploads an artifact it did
   not read from the database.
2. Network reachability from a GitHub-hosted runner to `db-rsn72awybem6s`. That server is
   firewalled and runner IPs are dynamic, so this needs either a firewall rule (broad, and worth
   thinking about before adding) or a self-hosted runner.

Until both exist, **the local script is the reviewer path** and the workflow is a skipping stub with
an accurate explanation in its summary. That is deliberate: a green scheduled job that read nothing
is worse than no job at all.

---

## Running it as a scheduled app workflow

The app's scheduled-workflow system can drive the same script on Captain's machine, where the Azure
CLI is already logged in and the firewall already admits the client IP — the two things CI lacks.
Create it from the workflow editor (or ask an agent to `save_workflow` with these values):

| Field | Value |
|---|---|
| **Name** | Sam opportunity digest |
| **Project** | SentenceStudio |
| **Interval** | Weekly, Monday 09:00 local |
| **Mode** | interactive |
| **Prompt** | See below |

```text
Run the Sam opportunity digest for the last 7 days and summarise it for review.

1. Confirm the credential is configured: COACH_DIGEST_CONNECTION_STRING, or
   COACH_DIGEST_KEYVAULT + COACH_DIGEST_KEYVAULT_SECRET, or COACH_DIGEST_AZURE_IDENTITY=1 with
   COACH_DIGEST_HOST/DATABASE/USER. If none is set, stop and say so — do not guess a connection.
2. Run: ./scripts/sam-opportunity-digest.sh --days 7
3. Summarise: total learner reports, the reason breakdown, any problem whose distinct-learner
   count is greater than 1, and anything whose status set is still only [New].
4. For each line worth acting on, propose an entry (or a Fingerprint row on an existing entry) for
   docs/sam-future-opportunities.md. Do NOT commit it — Zoe triages and Captain approves anything
   policy-gated, and a bot committing to that file bypasses the gate the file exists to enforce.
5. Never paste raw output containing anything other than counts, codes, statuses, timestamps, and
   fingerprints. There is nothing else in the digest; if you see anything else, that is a bug —
   report it instead of pasting it.
```

---

## Reading a digest

```
## Learner reports by reason
| Reason | Reports | Distinct learners | First | Last |
| `IncorrectOrMisleading` | 6 | 3 | 2026-08-18 09:12 UTC | 2026-08-24 17:40 UTC |

## Problems by frequency
| Fingerprint | Kind | Capability | Tool | Failure | Occurrences | Learners | Buckets | ... | Statuses |
| `coach-opportunity://a41f8c2b91d7…` | UserReportedResponse | `learner_reported_incorrect` | — | — | 6 | 3 | 4 | ... | New |
```

Triage order that has held up so far:

1. **Distinct learners > 1** first. One learner hitting something ten times is one learner's
   workflow; three learners hitting it once is a product gap.
2. **Statuses still `[New]`** — nobody has looked.
3. **`UserReportedResponse` before automatic rows.** A report arrived with a human's deliberate
   intent behind it; an automatic row is the server noticing itself refuse.

Then record the decision in `docs/sam-future-opportunities.md` against the fingerprint, by hand.
No bot writes that file.

**An empty digest is not evidence that nothing is wrong.** Automatic capture is off in Production,
so today the only rows are learner reports — and the ledger's known under-counts (unknown tool
names, referent loss on completed turns, unattributable turn-level tool failures) are listed in
`docs/sam-future-opportunities.md` § "Known gaps in the runtime ledger". Read them before
concluding an absence.

### When counts are not enough

Reading the *encrypted evidence* behind a row still requires the Development-only operator surface,
the learner's own scope, an explicit acknowledgement literal, and a durable key ring — and it
increments a reveal counter on the row it read. That has not changed and is not reachable from this
digest. If a fingerprint genuinely cannot be triaged without it, that is a finding to raise with
Captain, not a reason to widen this tool.
