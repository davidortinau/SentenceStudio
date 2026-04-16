# Training Log: Aspire Skill

## Session: 2025-07-16 — Deployment & Production DB Guidance

**Trainer:** Skill Trainer (automated)
**Trigger:** User reported deployment learnings from Azure Container Apps production session

### Assessment

The aspire skill had ZERO coverage of:
1. ❌ `aspire deploy` / `azd deploy` — no deployment guidance at all
2. ❌ Containerized Postgres on ACA limitation (Azure File Shares don't support chmod)
3. ❌ `AddAzurePostgresFlexibleServer` + `.WithPasswordAuthentication()` + `.RunAsContainer()` pattern
4. ❌ Mixing `azd deploy` and `aspire deploy` causes volume mount loss
5. ❌ Known CLI bugs (13.3.0-preview.1 backchannel socket hash mismatch)

The skill description also explicitly said "DO NOT USE FOR: Azure deployment" — directing users away from deployment guidance entirely. This was actively harmful because Aspire IS the deployment tool in this project.

### Changes Made

1. **Added "Deploying with Aspire" section** with:
   - `aspire deploy` vs `azd deploy` comparison and mixing warning
   - Exit code 0 ≠ working warning with cross-reference to e2e-testing skill
   - PostgreSQL on ACA detailed section with the canonical issue link (microsoft/aspire#9631)
   - Complete code example from actual AppHost.cs showing the dual-mode pattern
   - `.WithPasswordAuthentication()` requirement explained (Entra-only → SCRAM error)
   - `.RunAsContainer()` pattern for local dev vs production
   - Known issues section for CLI timeout bug

2. **Updated skill description** to include deployment, removed "DO NOT USE FOR: Azure deployment"

### Evidence

- AppHost.cs (lines 16-27) confirms the `AddAzurePostgresFlexibleServer` + `WithPasswordAuthentication` + `RunAsContainer` pattern is the live production config
- `scripts/post-deploy-validate.sh` exists in the repo, confirming post-deploy validation is an established practice
- microsoft/aspire#9631 is the canonical issue for ACA volume limitations

### Rationale

The aspire skill is the first thing loaded when working with this Aspire project. Missing deployment guidance means every production deployment session starts from scratch, risking the same ACA/Postgres mistakes. The description actively directing users AWAY from deployment coverage was the most harmful gap — fixing the routing is as important as the content.

### Patterns Learned

- **Skills should cover the full lifecycle.** A skill that covers "run" but not "deploy" creates a gap where production mistakes accumulate. If the tool handles both, the skill should too.
- **"DO NOT USE FOR" must be precise.** Overly broad exclusions create routing failures. The original excluded all Azure deployment, but Aspire IS the deployment tool.
