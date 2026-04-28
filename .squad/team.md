# Squad Team

> SentenceStudio — a .NET MAUI Blazor Hybrid language learning app

## Coordinator

| Name | Role | Notes |
|------|------|-------|
| Squad | Coordinator | Routes work, enforces handoffs and reviewer gates. Does not generate domain artifacts. |

## Members

| Name | Role | Charter | Status |
|------|------|---------|--------|
| Zoe | Lead | `.squad/agents/zoe/charter.md` | ✅ Active |
| Kaylee | Full-stack Dev | `.squad/agents/kaylee/charter.md` | ✅ Active |
| Wash | Backend Dev | `.squad/agents/wash/charter.md` | ✅ Active |
| River | AI/Prompt Engineer | `.squad/agents/river/charter.md` | ✅ Active |
| Jayne | Tester | `.squad/agents/jayne/charter.md` | ✅ Active |
| Simon | Backend Specialist (Escalation) | `.squad/agents/simon/charter.md` | ✅ Active |
| Scribe | Session Logger | `.squad/agents/scribe/charter.md` | 📋 Silent |
| Ralph | Work Monitor | — | 🔄 Monitor |

## Coding Agent

<!-- copilot-auto-assign: false -->

| Name | Role | Charter | Status |
|------|------|---------|--------|
| @copilot | Coding Agent | — | 🤖 Coding Agent |

### Capabilities

**🟢 Good fit — auto-route when enabled:**
- Bug fixes with clear reproduction steps
- Test coverage (adding missing tests, fixing flaky tests)
- Dependency updates and version bumps
- Small isolated features with clear specs
- Documentation fixes and README updates

**🟡 Needs review — route to @copilot but flag for squad member PR review:**
- Medium features with clear specs and acceptance criteria
- Refactoring with existing test coverage
- API endpoint additions following established patterns
- Migration scripts with well-defined schemas

**🔴 Not suitable — route to squad member instead:**
- Architecture decisions and system design
- Multi-system integration requiring coordination
- Ambiguous requirements needing clarification
- Security-critical changes (auth, encryption, access control)
- AI prompt design and grading logic
- Changes requiring cross-team discussion

## Project Context

- **Owner:** David Ortinau
- **Stack:** .NET 10, MAUI, Blazor Hybrid, MauiReactor (MVU), .NET Aspire, EF Core, SQLite, OpenAI
- **Description:** Language learning app with activities (Cloze, Writing, Translation, Word Association, etc.) targeting mobile and desktop
- **Created:** 2026-03-07
