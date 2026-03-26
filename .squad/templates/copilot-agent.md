# Copilot Coding Agent Member Reference

> On-demand reference for adding and managing @copilot as a squad member.

## Adding @copilot

1. Add to `team.md` under `## Coding Agent`:
   ```markdown
   ## Coding Agent
   <!-- copilot-auto-assign: false -->
   | Name | Role | Charter | Status |
   |------|------|---------|--------|
   | @copilot | Coding Agent | — | 🤖 Coding Agent |
   ```

2. Add capability profile (🟢/🟡/🔴) below the roster entry

3. Add routing entry to `routing.md`:
   ```
   | Async issue work (bugs, tests, small features) | @copilot 🤖 | Well-defined tasks matching capability profile |
   ```

## Comparison: AI Agent vs @copilot

| Aspect | Squad AI Agent | @copilot |
|--------|---------------|----------|
| Badge | Role emoji | 🤖 Coding Agent |
| Name | Cast name | Always "@copilot" (no casting) |
| Charter | `.squad/agents/{name}/charter.md` | Uses `copilot-instructions.md` |
| Spawnable | ✅ In-session via `task` tool | ❌ Async via issue assignment |
| Interaction | Direct prompt/response | Issue → branch → PR |
| Branch pattern | `squad/{issue}-{slug}` | `copilot/*` |
| Review | In-session reviewer gate | PR review (human or squad) |

## Capability Profile Format

```markdown
**🟢 Good fit — auto-route when enabled:**
- Bug fixes with clear reproduction steps
- Test coverage gaps
- Dependency updates
- Small isolated features

**🟡 Needs review — route but flag for PR review:**
- Medium features with specs
- Refactoring with test coverage
- API additions following patterns

**🔴 Not suitable — route to squad member:**
- Architecture decisions
- Multi-system integration
- Ambiguous requirements
- Security-critical changes
```

## Auto-Assign

Controlled by HTML comment in `team.md`:
- `<!-- copilot-auto-assign: true -->` — @copilot auto-assigned on `squad:copilot` issues
- `<!-- copilot-auto-assign: false -->` — manual assignment only

## Lead Triage for @copilot

When triaging issues, the Lead evaluates:
1. Is it well-defined? Clear steps/criteria → 🟢
2. Does it follow existing patterns? → 🟢
3. Does it need design judgment? → 🔴
4. Is it security-sensitive? → 🔴
5. Medium complexity with specs? → 🟡
