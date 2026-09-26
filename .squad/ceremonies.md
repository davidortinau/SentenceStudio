# Ceremonies

> Team meetings that happen before or after work. Each squad configures their own.

## Design Review

| Field | Value |
|-------|-------|
| **Trigger** | auto |
| **When** | before |
| **Condition** | multi-agent task involving 2+ agents modifying shared systems |
| **Facilitator** | lead |
| **Participants** | all-relevant |
| **Time budget** | focused |
| **Enabled** | ✅ yes |

**Agenda:**
1. Review the task and requirements
2. Agree on interfaces and contracts between components
3. Identify risks and edge cases
4. Assign action items

---

## Pre-Ship

| Field | Value |
|-------|-------|
| **Trigger** | auto |
| **When** | after |
| **Condition** | A completed work batch has a new or revised user-facing artifact scheduled for final delivery, merge, or publication; once per artifact revision, before finalization. Not triggered by status replies, ceremony output, or Scribe bookkeeping. |
| **Facilitator** | Fact Checker |
| **Participants** | Rai |
| **Time budget** | focused |
| **Enabled** | yes |

**Agenda:**
1. Fact Checker verifies external and repository claims in the final artifact against primary evidence; mark unsupported claims Unverified, and block Contradicted claims until an independent revision is re-verified.
2. Rai reviews safety, credentials, privacy, and user-facing content; a Red verdict blocks finalization until independently revised and re-reviewed.
3. Record both verdicts and any accepted residual uncertainty before final delivery. A failed or missing review never counts as approval.

---

## Retrospective

| Field | Value |
|-------|-------|
| **Trigger** | auto |
| **When** | after |
| **Condition** | build failure, test failure, or reviewer rejection |
| **Facilitator** | lead |
| **Participants** | all-involved |
| **Time budget** | focused |
| **Enabled** | ✅ yes |

**Agenda:**
1. What happened? (facts only)
2. Root cause analysis
3. What should change?
4. Action items for next iteration
