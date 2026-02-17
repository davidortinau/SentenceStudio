# Work Routing

How to decide who handles what.

## Routing Table

| Work Type | Route To | Examples |
|-----------|----------|----------|
| Architecture, scope, decisions | Mal | Branch strategy, library integration approach, page priority |
| UI pages, MauiReactor components, Bootstrap theming | Kaylee | Port Blazor pages to MauiReactor, apply MauiBootstrapTheme styles |
| Services, data layer, DI wiring | Wash | Service registration, API integration, data binding plumbing |
| Testing, quality, verification | Zoe | Build validation, UI verification, edge cases |
| Design mapping, style guides, docs | Inara | Blazor→MAUI design mapping, Bootstrap token mapping, documentation |
| Code review | Mal | Review PRs, check quality, architectural consistency |
| Session logging | Scribe | Automatic — never needs routing |
| Work monitoring | Ralph | Backlog tracking, pipeline status |

## Rules

1. **Eager by default** — spawn all agents who could usefully start work, including anticipatory downstream work.
2. **Scribe always runs** after substantial work, always as `mode: "background"`. Never blocks.
3. **Quick facts → coordinator answers directly.** Don't spawn an agent for "what branch are we on?"
4. **When two agents could handle it**, pick the one whose domain is the primary concern.
5. **"Team, ..." → fan-out.** Spawn all relevant agents in parallel as `mode: "background"`.
6. **Anticipate downstream work.** If Kaylee is porting a page, spawn Zoe to plan validation simultaneously.
7. **Inara first for design questions.** Before Kaylee starts porting a page, Inara should map the Blazor design to MauiReactor equivalents.
