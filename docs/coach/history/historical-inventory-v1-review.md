# Historical Coach Failure Inventory v1 Review

## Review status

This packet covers 13 proposed rows through the cutoff
`2026-09-02T20:20:09.969-05:00`: 11 are `Include` and 2 are
`PendingCaptainReview`. It does not claim completeness. Captain approval is
required before this version can support the historical-coverage guarantee.

Privacy rule: do not add production transcripts, email addresses, profile IDs,
tokens, vocabulary, diary content, or user identifiers. Exact prompt text is
present only where Captain supplied it for this task or where an existing
repository acceptance artifact already contains it.

## DecisionRecord source pinning

All eight `DecisionRecord` rows use the same immutable, pre-cutoff repository
snapshot rather than the mutable working copy of `.squad/decisions.md`:

- commit `5f60a38282beb12c76e9a65704f878df5ab01f94`, committed
  `2026-09-02T20:14:24-05:00`;
- blob `6a00e5e0d3f406e81684596f69013f9663f93045`;
- whole-source SHA-256
  `49c5798bf7ba01f9b2912a49d7c84da9bc3bd45d24f31aee71f50cb7626cdaa4`.

The current working-copy digest is deliberately not cited. Each row names an
exact heading and immutable inclusive line range in that blob. Its excerpt
digest uses `canonical-line-excerpt/v1`: decode UTF-8; normalize CRLF and CR to
LF; select the declared 1-based inclusive lines; trim trailing spaces and tabs
from each selected line; remove trailing blank lines; append one LF; hash the
UTF-8 bytes with SHA-256. The manifest stores headings and digests, not excerpt
bodies.

Captain or the reviewer can reproduce every pin without reading or copying
private content into the manifest:

```bash
git show 5f60a38282beb12c76e9a65704f878df5ab01f94:.squad/decisions.md \
  | shasum -a 256
git rev-parse \
  5f60a38282beb12c76e9a65704f878df5ab01f94:.squad/decisions.md
python3 - <<'PY'
import hashlib
import json
import re
import subprocess

path = "docs/coach/history/historical-inventory-v1.json"
manifest = json.load(open(path, encoding="utf-8"))
for row in manifest["rows"]:
    if row["sourceKind"] != "DecisionRecord":
        continue
    match = re.fullmatch(r"git:([0-9a-f]{40}):(.+)#L(\d+)-L(\d+)",
                         row["sourceReference"])
    assert match, row["scenarioId"]
    commit, source_path, first, last = match.groups()
    raw = subprocess.check_output(
        ["git", "show", f"{commit}:{source_path}"])
    whole = hashlib.sha256(raw).hexdigest()
    assert row["sourceDigest"] == f"sha256:{whole}", row["scenarioId"]
    lines = raw.decode("utf-8").replace("\r\n", "\n").replace("\r", "\n").split("\n")
    excerpt = [line.rstrip(" \t") for line in lines[int(first)-1:int(last)]]
    while excerpt and not excerpt[-1]:
        excerpt.pop()
    canonical = ("\n".join(excerpt) + "\n").encode()
    assert excerpt[0] == row["sourcePin"]["heading"], row["scenarioId"]
    digest = hashlib.sha256(canonical).hexdigest()
    assert row["sourcePin"]["excerptDigest"] == f"sha256:{digest}", row["scenarioId"]
    print(f'{row["scenarioId"]}: PASS')
PY
```

## Captain sign-off procedure

1. Review every row below against the JSON manifest.
2. Confirm `Include` or choose another allowed disposition. Resolve each of
   the two `PendingCaptainReview` rows only when its missing immutable evidence
   is available.
3. For any exact prompt marked unavailable, either authorize a content-free
   source reference or provide an approved redacted fixture. Do not paste a
   private transcript into the repository.
4. Confirm the cutoff and the known missing sources.
5. Hash the exact approved JSON bytes:
   `shasum -a 256 docs/coach/history/historical-inventory-v1.json`.
6. Record the hash, cutoff, approver, and approval time in the external approval
   record. The manifest intentionally does not self-hash.
7. Only then promote each included row to a permanent scenario fixture and prove
   it red-first.

## Row review

### 1. `COACH-HIST-001`

| Field | Review value |
|---|---|
| Failure | Coach missing after login because API startup failed on a duplicate allowlist entry |
| Source | commit `e9d920455759647b8ad21ca3e73d19c994c4f3f1`, `docs/incidents/2026-08-22-sam-missing-and-recovery-rca.md`, lines 99–129 |
| Exact prompt | Not applicable |
| Expected | Healthy availability and persistent entry, or an explicit recoverable unavailable state |
| Disposition | `Include` |
| Gap / next source | Clean-runtime fault fixture missing; capture a content-free startup trace plus availability and entry-surface evidence |

### 2. `COACH-HIST-002`

| Field | Review value |
|---|---|
| Failure | Saved conversation list missing |
| Source | commit `5f60a38282beb12c76e9a65704f878df5ab01f94`, lines 97–99, heading `CONVERSATION SHELF RESTORE (Kaylee, 2026-08-22)`, excerpt SHA-256 `a1275e8ba955502b024f2ba98ea501dbc7e954b26d4f926bcc22feabd39358f5` |
| Exact prompt | Not applicable |
| Expected | Owner-scoped newest-first list resumes the selected thread |
| Disposition | `Include` |
| Gap / next source | Original capture is unhashed; obtain Captain-authorized screenshot hash or originating issue/turn reference |

### 3. `COACH-HIST-003`

| Field | Review value |
|---|---|
| Failure | Unrequested duplicate dashboard resume card |
| Source | commit `5f60a38282beb12c76e9a65704f878df5ab01f94`, lines 114–133, heading `ZOE REVIEW — DASHBOARD CoachEntryCard RETIREMENT (Zoe, 2026-08-22)`, excerpt SHA-256 `7b583ced420a5d619ba6fa4850cfbfced2cb23183f4281f4fd27bf0a3099b056` |
| Exact prompt | Not applicable |
| Expected | No dashboard duplicate; one persistent teacher control |
| Disposition | `Include` |
| Gap / next source | Original screenshot missing; obtain its hash or the originating issue/review comment |

### 4. `COACH-HIST-004`

| Field | Review value |
|---|---|
| Failure | Teaching request forced toward plan behavior |
| Source | Clean session plan, initial recovered inventory |
| Exact prompt | Not retained |
| Expected | Complete pedagogical answer and coherent follow-up with no plan effect |
| Disposition | `PendingCaptainReview` |
| Gap / next source | The external plan digest cannot be resolved from this worktree; obtain a Captain-approved immutable plan export or owner-authorized conversation and turn references with the failed response |

### 5. `COACH-HIST-005`

| Field | Review value |
|---|---|
| Failure | Latest-study answer incorrect, unavailable, or routed through the model |
| Source | commit `5f60a38282beb12c76e9a65704f878df5ab01f94`, lines 260–262, heading `RIVER RCA — LATEST-STUDY LIVE iOS FAILURE (2026-08-25)`, excerpt SHA-256 `e1758f0a0b0c8f17439b9d1dbf8f087862e0e800ab10f1564031fa6558a71d84` |
| Exact prompt | Repository acceptance artifact: `When was the last time I studied?` |
| Expected | Deterministic owner-scoped history read with correct local date, evidence, and zero model calls |
| Disposition | `Include` |
| Gap / next source | Attach the content-free 2026-08-25 iOS route, stop-reason, call, and persisted-message trace |

### 6. `COACH-HIST-006`

| Field | Review value |
|---|---|
| Failure | Correction or dispute follow-up did not reliably re-read history |
| Source | commit `5f60a38282beb12c76e9a65704f878df5ab01f94`, lines 277–285, heading `JAYNE VERDICTS (2026-08-25)`, excerpt SHA-256 `5db55aa0432913f20ae8e69fc417ceb4e33354f9a1c5a19163d989cabb86ceaa` |
| Exact prompt | Historical wording not established; repository artifacts contain variants |
| Expected | Re-read, acknowledge, correct or reaffirm from evidence, never invent |
| Disposition | `Include` |
| Gap / next source | Obtain the owner-authorized authoritative disputed-turn export |

### 7. `COACH-MOB-001`

| Field | Review value |
|---|---|
| Failure | Dialog blended into the underlying app and was not modal |
| Source | commit `5f60a38282beb12c76e9a65704f878df5ab01f94`, lines 190–192, heading `CAPTAIN DIRECTIVE (2026-08-25T20:15 CDT)`, excerpt SHA-256 `926db0c9e0994237606511968d5fb5f2591cd0c3faeda5b1119cf98a9ec169ca` |
| Exact prompt | Not applicable |
| Expected | Scrim, dialog semantics, inert underlying app, usable focus and dismissal |
| Disposition | `Include` |
| Gap / next source | Obtain the original screenshot hash and matching accessibility snapshot |

### 8. `COACH-MOB-002`

| Field | Review value |
|---|---|
| Failure | Fullscreen controls rendered under the status area |
| Source | commit `5f60a38282beb12c76e9a65704f878df5ab01f94`, lines 213–217, heading `JAYNE LIVE iOS VERDICT — MOBILE MODAL (2026-08-25)`, excerpt SHA-256 `7c6c995930f344a15e173e9982f21c605344c8142404adc9d8baf2fa86fd7493` |
| Exact prompt | Not applicable |
| Expected | Safe-area-visible and tappable controls across focus and rotation |
| Disposition | `Include` |
| Gap / next source | Obtain the rejected fullscreen screenshot hash and control bounds |

### 9. `COACH-MOB-003`

| Field | Review value |
|---|---|
| Failure | Header moved with the transcript; composer was not reliably fixed |
| Source | commit `5f60a38282beb12c76e9a65704f878df5ab01f94`, lines 219–231, heading `ZOE — FULLSCREEN SCROLL CONTAINMENT REVISION (2026-08-25)`, excerpt SHA-256 `6d537c47ebc66cc1c79619501048a67e7f5b43f51968628343a3a3fbd70517da` |
| Exact prompt | Not applicable |
| Expected | Fixed header and composer; transcript alone scrolls |
| Disposition | `Include` |
| Gap / next source | Obtain the original long-thread recording or screenshot-sequence hash with scroll offsets |

### 10. `COACH-MOB-004`

| Field | Review value |
|---|---|
| Failure | Duplicate page-header navigation inset produced extra space above the content |
| Source | commit `5f60a38282beb12c76e9a65704f878df5ab01f94`, lines 194–204, heading `ZOE DESIGN REVIEW — SAM MOBILE & HISTORY (2026-08-25)`, excerpt SHA-256 `e233c2a7ae4d2e86eb57ad4a20f2695ce815367f6e520c499cdc0d10031b96db` |
| Exact prompt | Not applicable |
| Expected | Single navigation-inset owner; no duplicate page-header padding or extra navigation-bar gap |
| Disposition | `Include` |
| Gap / next source | Obtain the original screenshot hash and page-header/navigation bounds for the affected state |

### 11. `COACH-CANON-001`

| Field | Review value |
|---|---|
| Failure | Household-topic vocabulary intent with typo was misunderstood or forced into plan behavior |
| Source | Captain's `coach-failure-manifest` request |
| Exact prompt | Retained in the JSON under explicit task authorization |
| Expected | Understand topic; offer plan-versus-direct choice; no effect before choice |
| Disposition | `Include` |
| Gap / next source | Obtain authorized failed-turn evidence or run a faithful fault-injected red fixture using the exact prompt |

### 12. `COACH-CANON-002`

| Field | Review value |
|---|---|
| Failure | Direct food vocabulary review request did not safely reach activity launch |
| Source | Captain's `coach-failure-manifest` request |
| Exact prompt | Retained in the JSON under explicit task authorization |
| Expected | Owned food set proposal, structured acceptance, saved-settings launch, no plan mutation |
| Disposition | `Include` |
| Gap / next source | Obtain authorized failed-turn evidence or run a faithful fault-injected red fixture using the exact prompt |

### 13. `COACH-HIST-007`

| Field | Review value |
|---|---|
| Failure | Possible duplicate or out-of-order message in the second screenshot |
| Source | Clean session plan, second-screenshot note |
| Exact prompt | Not retained |
| Expected | One learner node per turn and chronological history across failure, resume, and reload |
| Disposition | `PendingCaptainReview` |
| Gap / next source | Obtain the original screenshot attachment hash, capture sequence, and adjacent authoritative turn IDs |

## Known missing sources

- Owner-authorized Coach conversation export.
- Learner reports joined to authoritative conversation and turn references.
- Original screenshots, attachment hashes, and capture sequences.
- Copilot project-session and chat export.
- Exhaustive GitHub issue, pull request, and review-comment enumeration.
- Clean-runtime red-first fixtures and their positive controls.

These omissions are explicit. This inventory must not be described as complete
until Captain approves both its source boundary and cutoff.

## Append-only versioning

After approval, `historical-inventory/v1` is immutable. Do not remove, rewrite,
or renumber an old row. A newly found source or corrected disposition creates a
new manifest version that carries every prior row, adds a supersession note when
needed, records a new cutoff, and receives a new Captain-approved external
SHA-256 digest.

## Reader check

This packet answers the four approval questions: what failed, what evidence is
safe to retain, what behavior must replace it, and what evidence is still
missing. It does not answer whether the clean runtime passes these scenarios;
that requires the later red-first fixture and implementation gates.
