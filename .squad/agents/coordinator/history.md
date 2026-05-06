# Coordinator — Squad Routing & Handoff Log

> Squad routes work, enforces handoffs and reviewer gates. Does not generate domain artifacts.

---

## 2026-04-29 — iOS Build Recipe Verification Cycle

**Incident:** Coordinator reported 31 Razor errors when building iOS Release with net11p3 SDK per `docs/deploy-runbook.md` Step 2a.

**Captain's Response:** Pushback on root cause. Suspected obj/ contamination (Coordinator built with dirty build tree, no `dotnet clean` between SDK swaps).

**Verification Spawn:** Captain dispatched Wash to re-run build with proper hygiene (full `obj/` + `bin/` wipe, not just `dotnet clean`).

**Wash's Verdict:** Claim **VERIFIED**. With full wipe under net11p3, identical 31 errors reproduced. Razor SG regression in net11 Preview 3 is genuine, NOT contamination.

**Decision Outcome:** Recipe A (net11p3 swap) is broken. Recipe B (net10 GA + `-p:ValidateXcodeVersion=false`) is canonical. Documented in `.squad/decisions.md` 2026-04-29T14:32Z.

**Lesson Learned:**
- ✗ **Error:** Jump to conclusions about obj/ contamination without verifying hygiene first
- ✓ **Correction:** Full `obj/` + `bin/` wipe (not `dotnet clean`) is required between SDK swaps
- ✓ **Process Rule:** Wipe early, test, then wipe again — proper verification requires baseline repetition

**Process Improvement:** New hygiene rule added to decisions.md — when swapping SDKs via `global.json`, ALWAYS wipe `obj/` and `bin/` from affected projects. `dotnet clean` is not sufficient because Razor SG artifacts can collide.

---

## 2026-04-29 — PR #183 Ship: Sentences Smart Resource + ResourceEdit Read-Only + net11p3 Workaround + Full Deploy

**Scope:** Production publish of PR #183 + net11p3 Razor SG workaround (ImportContent.razor) + Azure deploy + iOS to DX24

**Orchestration Arc:**

1. **PR #183 Merge** (commit f8b4567, admin merge via `gh pr merge --squash --admin --delete-branch`)
   - Wash: Sentences smart resource (5th type, LexicalUnitType.Sentence only) + narrowed Phrases (LexicalUnitType.Phrase only)
   - Kaylee: ResourceEdit.razor read-only for IsSmartResource (8 disabled inputs, mutating buttons hidden, 6 server-side guards, ConfirmDelete guard added post-review)
   - 18/18 tests passing

2. **net11p3 Razor SG Workaround Applied** (commit 2359da8)
   - Issue: dotnet/razor#13117 — switch expressions returning RenderFragment lambdas with inline Razor markup trigger SG regression
   - Workaround: tuple-returning meta helpers in ImportContent.razor; file 1168→1145 lines
   - Result: net10 = 0 errors, net11p3 = 0 errors (was 31)
   - Deployed with upstream issue reference + "recheck on each upstream release" comment

3. **Azure Deploy** — `azd deploy` success (2m 6s); all 5 services live; post-validation 16 PASS / 0 FAIL

4. **iOS Device Build (DX24)** — **NEW: net11p3 SDK is now canonical** (not net10 + ValidateXcodeVersion=false)
   - Used net11p3 SDK swap (workaround unblocked dogfooding latest preview)
   - Build clean: 0 errors
   - Install + launch on DX24 (CF4F94E3-A1C9-5617-A089-9ABB0110A09F) successful
   - SDK restored to net10 after build

**Key Decision Update:**
- **Upstream Policy (Codified):** Default = workaround + comment + recheck (vs. upstreaming). Exception = if we have upstream repo locally (maui-labs, maui), PR the fix. When uncertain, ask Captain.
- **iOS Recipe (NEW CANONICAL):** net11p3 SDK swap (supersedes net10 + ValidateXcodeVersion=false). Reason: unblock preview dogfooding + future-proof Xcode 26.3 support. Fallback recipe documented if net11p3 breaks again.

**Cross-agent Notes:**
- Wash: Sentences smart resource shipped in PR #183; ImportContent.razor workaround details for future razor work; iOS recipe updated to net11p3
- Kaylee: ResourceEdit read-only shipped; upstream policy directive captured; iOS recipe updated
- Scribe: Documented all of the above; decisions merged; agent histories updated

---

## 2026-04-29 — Issue #179 Fix + Pre-Deploy Check Rewrite Publish

**Scope:** Production publish (Issue #179 fix, PR #181) + pre-deploy check migration to Flex Server (PR #182)

**Orchestration Arc:**

1. **Pre-Deploy Gate** — Wash's rewritten script (Flex Server validation) PASS (4/4 checks green)
2. **Azure Deploy** — `azd deploy` to production SUCCESS in 2:28
3. **Post-Deploy Validation** — 16 passed / 0 failed / 2 skipped / 2 warnings
4. **iOS Device Build (DX24)** — **New simplified recipe executed successfully:**
   - Stay on net10 GA SDK (no SDK swap needed)
   - Use `-p:ValidateXcodeVersion=false` flag
   - Build succeeded
   - Install to DX24 (CF4F94E3-A1C9-5617-A089-9ABB0110A09F) succeeded
   - Launch succeeded

**Key Innovation:**
- **Before:** Documented workaround required net11p3 SDK swap (`global.json` toggle) to suppress Xcode 26.3 mismatch
- **After:** Pass `-p:ValidateXcodeVersion=false` to net10 build; no swap needed
- **Impact:** Simplified iOS publish workflow. Xcode version assertion is a warning, not a blocker.

**What is Live:**
- Issue #179 fix deployed to production
- Pre-deploy validation now guarding every future deploy
- iOS DX24 running latest code

**New Canonical iOS Release Recipe:**
```bash
services__api__https__0=https://api.livelyforest-b32e7d63.centralus.azurecontainerapps.io \
  dotnet build src/SentenceStudio.iOS/SentenceStudio.iOS.csproj \
    -f net10.0-ios -c Release \
    -p:RuntimeIdentifier=ios-arm64 \
    -p:ValidateXcodeVersion=false
```

**Documented:** `.squad/orchestration-log/2026-04-29T13-26-41Z-coordinator-publish.md`


---

## 2026-05-05: Dogfooding Directive Established

**Status:** DIRECTIVE (Cross-team impact)

Per Captain's directive captured in AGENTS.md under "Project Purpose: Dogfooding .NET MAUI", **tooling friction investigations are HIGHER priority than app features**. When .NET MAUI SDK/DX friction surfaces during normal work:

1. Root-cause systematically (layer elimination, reproduction steps)
2. File upstream with high-quality issue body
3. Document workarounds for team reuse
4. Continue app validation using workaround

This directive was established during NumberDrill Phase 1 work when DevFlow CLI bugs blocked automation. Investigation yielded dotnet/maui-labs#232 — a real upstream issue capturing tooling friction that benefits the .NET MAUI team.

**Impact on your work:** Prioritize tooling investigations over feature delays. Document findings as skills for team reuse.

---

