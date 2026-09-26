# Work Routing

How to decide who handles what.

## Routing Table

| Work Type | Route To | Examples |
|-----------|----------|----------|
| UI pages, Blazor components, layout, styling | Kaylee | Build activity page, fix button alignment, add dashboard card |
| MauiReactor pages, native MAUI UI | Kaylee | Convert page to MauiReactor, add native gesture handler |
| API endpoints, services, data layer | Wash | Add API route, create service class, write repository method |
| Database schema, EF Core migrations | Wash | Add model, create migration, fix schema issue |
| DI registration, Aspire config | Wash | Register service, configure AppHost, add environment variable |
| AI prompts, LLM integration, grading | River | Design grading prompt, tune AI response, add new prompt template |
| AI response models, structured output | River | Create response DTO, adjust JSON schema, fix parsing |
| Code review, quality gates | Zoe | Review PRs, check quality, suggest improvements |
| Responsible AI, privacy, safety, credential review | Rai | Review user-facing output and sensitive configuration before shipping |
| Claim verification, external references, Devil's Advocate | Fact Checker | Validate package versions and API claims; challenge architecture assumptions |
| Architecture, system design | Zoe | Project structure, dependency flow, API contracts |
| Learning-activity UX changes (modes, directions, prompts, toggles) | Zoe (Learning Value Gate) | New activity, new mode, show/hide toggle, prompt direction, photo/audio prompt — see `.squad/skills/learning-value-gate/SKILL.md` |
| Scope & priorities | Zoe | What to build next, trade-offs, decisions |
| Issue triage | Zoe | Analyze issues, assign labels, evaluate @copilot fit |
| Testing, E2E verification | Jayne | Run Playwright tests, verify database, check logs |
| Bug verification, regression checks | Jayne | Confirm fix works, check for side effects |
| Async issue work (bugs, tests, small features) | Squad member | @copilot issue assignment is blocked until all three model settings are enforceable |
| Session logging | Scribe | Automatic — never needs routing |

## Issue Routing

| Label | Action | Who |
|-------|--------|-----|
| `squad` | Triage: analyze issue, evaluate @copilot fit, assign `squad:{member}` label | Lead |
| `squad:{name}` | Pick up issue and complete the work | Named member |
| `squad:copilot` | Block: no GitHub issue assignment can enforce model, max effort, and long_context together | No assignment |

### How Issue Assignment Works

1. When a GitHub issue gets the `squad` label, the **Lead** triages it — analyzing content, assigning the right human/Squad `squad:{member}` label, and commenting with triage notes.
2. **@copilot evaluation:** The capability profile is informational only; even a good fit must be routed to a Squad member rather than `squad:copilot`.
3. When a `squad:{member}` label is applied, that member picks up the issue in their next session.
4. When `squad:copilot` is applied (manually or automatically), assignment fails closed. Do not manually assign via the GitHub UI, CLI, or API until an interface can set and verify GPT model, maximum reasoning effort, and long_context together.
5. Members can reassign by removing their label and adding another member's label.
6. The `squad` label is the "inbox" — untriaged issues waiting for Lead review.

### @copilot Capability Profile (Informational Only)

When triaging, the Lead may use these questions to select a Squad member, not to assign @copilot:

1. Clear title and acceptance criteria help a member pick up work.
2. Familiar patterns and tests help a member bound the scope.
3. Design or security concerns require the appropriate specialist.

## Rules

1. **Eager by default** — spawn all agents who could usefully start work, including anticipatory downstream work.
2. **Scribe always runs** after substantial work, always as `mode: "background"`. Never blocks.
3. **Quick facts → coordinator answers directly.** Don't spawn an agent for "what port does the server run on?"
4. **When two agents could handle it**, pick the one whose domain is the primary concern.
5. **"Team, ..." → fan-out.** Spawn all relevant agents in parallel as `mode: "background"`.
6. **Anticipate downstream work.** If a feature is being built, spawn the tester to write test cases from requirements simultaneously.
7. **Issue-labeled work** — when a `squad:{member}` label is applied to an issue, route to that member. The Lead handles all `squad` (base label) triage.
8. **@copilot issue assignment gate** — no automated or manual @copilot issue assignment while GitHub's issue interface cannot enforce the GPT model, maximum reasoning effort, and long_context together. Fail closed on `squad:copilot` labels and dispatches; route issues to Squad members instead. A model-only request does not meet the policy.
