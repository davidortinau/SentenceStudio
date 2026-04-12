# Orchestration: Kaylee — maui-ai-debugging Skill Updates

**Date:** 2026-04-12  
**Spawn:** kaylee-skill-update  
**Mode:** Background  
**Charter:** Skill Auditor & Debugger  

## Task

Apply 7 post-mortem fixes to `.claude/skills/maui-ai-debugging/SKILL.md` from wash-simulator-postmortem analysis.

## Status

✅ COMPLETED

## Changes

1. ✅ CLI name verification (Prerequisites) — maui-devflow (hyphen, not space)
2. ✅ TFM-to-runtime mapping (Section 1) — net10.0-ios→iOS 26+, net9.0-ios→iOS 17+
3. ✅ Simulator state tracking (device-state.json) — persist details after each session
4. ✅ CDP limitations (isTrusted, fallback hierarchy) — MAUI tap > MAUI fill > CDP eval > CDP Input
5. ✅ Blazor navigation guide (tap > Blazor.navigateTo() > inspect > UI)
6. ✅ Circuit breaker (15-min hard stop, time limits table, diagnostic commands)
7. ✅ Verification integrity (evidence-based only; platform + screenshot required)

## Outputs

- `.claude/skills/maui-ai-debugging/SKILL.md` — 7 sections updated
- `references/device-state.json` — empty initial state ready for next session

## Decision Record

Merged to decisions.md: Decision: maui-ai-debugging Skill Post-Mortem Updates
