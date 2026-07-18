## Active Decisions

(Most recent decisions below. Archived decisions in `decisions/archive/scribe-archive-2026-07-12.md` and earlier archives.)

---

### 2026-07-15 — User directive: SentenceStudio UX must prioritize target-language learning value

**By:** David Ortinau (Captain)
**What:** SentenceStudio UX must create target-language learning value. Vocab Quiz photo behavior may hide native-language terms, but must not hide target-language terms; matching a photo only to the learner's own language has no learning value. The team must proactively flag any UX whose outcome does not support target-language learning.
**Why:** Cross-cutting product and review guardrail.

**Status:** Active — codified in Learning Value Gate skill (see decision below).

---

### 2026-07-15 — Learning Value Gate — earned skill after Vocab Quiz photo-hide-text miss

**Date:** 2026-07-15
**Author:** Zoe (Lead)
**Surface:** Cross-cutting product/review gate for all learning activities
**Requested by:** Captain (David Ortinau)

#### Decision

Adopt a blocking **Learning Value Gate** for every change to a learning activity's modes, directions, prompt/response modalities, toggles, defaults, and empty states. The gate is codified in `.squad/skills/learning-value-gate/SKILL.md`, referenced from `AGENTS.md` under "Task Validation Requirements", and enforced through Zoe review per `.squad/routing.md`. Vocab Quiz's executable acceptance matrix is `§1.2` of `.claude/skills/e2e-testing/references/quiz-activities.md`.

The gate is not advisory. Zoe will refuse to approve merges where:

- Any reachable (direction × prompt-modality × response-modality × toggle) row lands in `native prompt → native response` with no target-language token on screen or in audio.
- A hide/show toggle can hide the target-language artifact when the prompt is target-language.
- The default preference path (no user overrides) lands in a blocked row.
- Author cannot state a one-sentence, non-mechanistic learning objective.
- Acceptance tests do not cover every newly-reachable row.

#### Root-cause analysis of the systemic miss

The 2026-07-15 Vocab Quiz "Show/Hide text with photo" feature added `UserProfile.VocabQuizShowTextWithPhoto` (migration `20260714173027_AddVocabQuizShowTextWithPhoto`, default `false`) and gated hiding on `HasPromptImage && showPhotoPromptControl && showMnemonicImage && !showTextWithPhoto` (`src/SentenceStudio.UI/Pages/VocabQuiz.razor:1940`). The gate omits `promptUsesNativeLanguage`. In the default `DisplayDirection = "TargetToNative"` direction, enabling the photo prompt hides the Korean term and leaves only a picture with English answer options — zero L2 exposure, zero L2 retrieval.

Four gate failures compounded:

1. **Product framing.** The feature was scoped as UX cleanliness ("declutter when a photo is shown"), never as a pedagogical mode change. There was no requirement to enumerate direction × modality × toggle combinations.
2. **Missing review dimension.** The existing skills (`blazor-activity-layout-shell` for layout parity, `activity-audit-checklist` for mode × context coverage) do not govern language-role coverage. That dimension had no guardrail.
3. **Default-path blind spot.** The DB default is `false` (text hidden), the direction default is `TargetToNative`, and the photo-prompt default is `false`. Nobody traced what happens when a user enables the photo prompt for the first time. That path lands directly in the blocked row.
4. **Test coverage.** The `/vocab-quiz` E2E reference enumerated the happy path only. There was no per-direction × per-modality matrix and no answer-leakage check.

Zoe review at merge time asked the wrong questions — schema correctness, EF dual-provider parity, preference model consistency — and did not ask "in each language direction, what is the learner actually retrieving?"

#### Guardrail artifacts

- **New:** `.squad/skills/learning-value-gate/SKILL.md` — the routable skill. Triggers, non-negotiable rule (either prompt or response must be target-language and irreducible), six required author artifacts, six reviewer blocking criteria, incident post-mortem. Sibling to `activity-audit-checklist` — layout skills do not substitute.
- **Updated:** `AGENTS.md` — new subsection under Task Validation Requirements pointing to the gate as blocking review. Placed where every agent will read it before shipping activity work.
- **Updated:** `.squad/routing.md` — new row routing "Learning-activity UX changes (modes, directions, prompts, toggles)" to Zoe with an explicit skill reference and trigger examples so the coordinator does not miss it.
- **Updated:** `.claude/skills/e2e-testing/references/quiz-activities.md` §1.2 — executable direction × modality acceptance matrix for Vocab Quiz. Seven rows (A–G). Row C is the incident-repro row and passes only if the offending state is unreachable. Row F documents the correct pedagogical use of "hide text with photo" (native-prompt direction only). Row G forces `Mixed` mode to satisfy both. Empty-state check calls out `VocabQuizShowTextWithPhoto`'s DB default.

#### Language-direction invariant for Kaylee (implementation contract)

**Authoritative per-turn flag:** `promptUsesNativeLanguage` — declared at `src/SentenceStudio.UI/Pages/VocabQuiz.razor:603`, assigned by `ShouldUseNativePrompt()` at lines 2011-2015, sourced from `VocabularyQuizPreferences.DisplayDirection` (`src/SentenceStudio.AppLib/Services/VocabularyQuizPreferences.cs:47-54` normalized at lines 209-216). Consumed by `GetPromptText`, `GetAnswerText`, `GetPromptAudioText`, `GetPromptAudioLanguage`. Do not introduce a new enum or a new field for this — it is authoritative.

**Invariant to enforce:** `ShouldHideTermForPhoto` (`src/SentenceStudio.UI/Pages/VocabQuiz.razor:1940`) must also require `promptUsesNativeLanguage`. That is: the term may be hidden behind a photo **only when the prompt side is the learner's native language.** Never when the prompt side is target language.

Recommended concrete change (Kaylee — pick one, whichever passes the matrix cleanly):

- Extend `ShouldHideTermForPhoto` to `HasPromptImage && showPhotoPromptControl && showMnemonicImage && !showTextWithPhoto && promptUsesNativeLanguage`.
- Additionally, gate the header dropdown "Show/Hide text" button (`VocabQuiz.razor:19-28`) on `promptUsesNativeLanguage` so target-prompt turns never even expose the toggle. This is the cleaner UX and prevents user confusion about a toggle that "does nothing" in target-prompt turns.
- For `Mixed` mode: re-evaluate the toggle's visibility per turn, since direction can flip per turn.

**Do NOT** invent a new preference (e.g., `HidePromptInNativeDirection`). `DisplayDirection` + `UsePhotoPrompt` + `VocabQuizShowTextWithPhoto` are already the three orthogonal knobs; the invariant is a constraint on their combination, not a new axis.

#### Reviewer acceptance matrix (source of truth: `quiz-activities.md` §1.2)

Kaylee's fix passes review when all of the following DOM/behavior checks hold on a photo-bearing resource:

| # | Direction | UsePhotoPrompt | ShowTextWithPhoto | Target term in prompt DOM | Passes? |
|---|-----------|----------------|-------------------|---------------------------|---------|
| A | TargetToNative | false | — | present | must pass |
| B | TargetToNative | true | true | present | must pass |
| C | TargetToNative | true | false | **present** (toggle unreachable OR does not hide) | must pass — this is the incident-repro |
| D | NativeToTarget | false | — | in options only | must pass |
| E | NativeToTarget | true | true | in options; native prompt visible | must pass |
| F | NativeToTarget | true | false | in options; native prompt may be hidden | must pass |
| G | Mixed | true | false | per-turn: target-prompt turns behave like B; native-prompt turns behave like F | must pass |

Plus answer-leakage sub-checks on every row: `img[alt]` doesn't name the target term, prompt audio matches prompt direction (per #193 anti-cheat), MC options don't leak the answer.

#### Remaining architectural gap

The Vocab Quiz preference model spreads three orthogonal booleans (`UseTextPrompt`, `UseAudioPrompt`, `UsePhotoPrompt`) alongside `DisplayDirection` and per-user `VocabQuizShowTextWithPhoto`. That is a 3 × 3 × 2 combinatorial surface plus `Mixed`. The gate is the right cross-cutting control, but longer-term the preference schema should either (a) narrow to a small enum of named "quiz modes" that each have a validated (direction × modality × toggle) profile, or (b) grow a validator method (`VocabularyQuizPreferences.Validate()`) that refuses to persist illegal combinations. That is a Zoe/Wash follow-up, not a Kaylee task on this fix.

#### Non-goals

- Not editing `VocabQuiz.razor` here (Kaylee owns it — the invariant above is the contract).
- Not touching the migration (default `false` is fine once the invariant is enforced; the toggle simply becomes a no-op or is hidden in target-prompt direction).
- Not adding a Squad workflow/CI job. Captain merges direct to main; the gate lives in the reviewer's obligations and the E2E matrix.

---

### 2026-07-15 — VocabQuiz photo text hiding gated by language direction; fullscreen viewer added

**By:** Kaylee
**Surface:** Blazor WebApp and MAUI heads
**Status:** Implemented + multiple E2E validations passed; pending Captain merge

#### What
VocabQuiz photo text hiding gated by language direction; fullscreen viewer added

#### References
Captain feedback: photo UX requirements, VocabQuizPhotoTextPolicy.cs, VocabQuiz.razor

#### Why

## Context
Captain requested that quiz text hiding with photos only applies to NATIVE-language prompts. Target-language text must always be visible because seeing the script alongside an image builds recognition — hiding the learner's own language is acceptable because matching a photo to a native-language word alone has no learning value.

## Decisions

1. **Language-direction gate**: `ShouldHideTermForPhoto` now requires `promptUsesNativeLanguage == true`. When the quiz direction shows a TARGET-language term (TargetToNative mode), text is NEVER hidden regardless of preference or photo state.

2. **Toolbar eligibility**: The "Show/Hide Text" dropdown action only renders when `IsTextToggleEligible` is true — which requires native-language prompt direction. For target-language prompts the toggle simply does not appear (no confusing disabled state).

3. **Fullscreen image viewer**: Pure CSS + Blazor overlay (`position: fixed; inset: 0; z-index: 1100`). No JS dependency needed. Close via: close button, backdrop click, or Escape key. Safe-area-aware close placement. Stable IDs: `quiz-fullscreen-viewer`, `quiz-fullscreen-close`, `quiz-fullscreen-image`, `quiz-photo-thumbnail`.

4. **Pinch/pan deferred**: No standards-based pointer/touch pan+zoom implementation exists in this repo. Adding one requires either a JS library or custom touch event handling that risks regression on hybrid WebView hosts. Deferred per Captain's "only if reliable" guidance.

5. **Testable helper extracted**: `VocabQuizPhotoTextPolicy` (static, pure) in SentenceStudio.Shared mirrors the Razor inline properties. 13 unit tests cover the decision matrix (target always visible, native default hidden, preference override, toolbar eligibility, no-photo, fullscreen state independence).

## Validation

**Zoe review:** systemic guardrail check via Learning Value Gate (new blocking skill). Gate passed.

**Kaylee implementation:** WebApp + policy tests + 13 unit tests; full build 0 errors; all E2E validations passed.

**Jayne (Reviewer, sync):** rejected fullscreen focus lifecycle because Escape could not receive key events. Kaylee locked out of that revision. Language policy/systemic gate otherwise accepted.

**Zoe (independent revision, sync):** added post-render focus into overlay, immediate Escape behavior, and focus restoration to thumbnail. Build 0 errors. Jayne re-review: VERDICT GO.

**Jayne (WebApp E2E, sync):** verified TargetToNative, NativeToTarget, Mixed, no-photo, fullscreen interactions/focus. Initial visual fixture exposed no new errors but AppKit auth was keychain-gated.

**Jayne (visual reviewer, sync):** rejected CSS because only backdrop enlarged; actual 32x32 image remained ~30x30 under max-only sizing. Kaylee locked out of CSS revision.

**Zoe (independent CSS revision, sync):** gave fullscreen image explicit safe-area-aware viewport width/height with object-fit contain. Build 0 errors.

**Jayne (focused WebApp recheck, sync):** VERDICT GO. Actual visible bitmap enlarged from ~32x32 to 786x786 at 1200x818 and 358x358 at 390x844; no distortion/overflow; all close/focus paths pass.

**Jayne (iOS simulator DevFlow E2E, sync):** dedicated iOS 26.4 simulator; SentenceStudio identity confirmed on port 9224. Target/native directions, photo decode, 160x160 thumbnail to 370x370 visible fullscreen bitmap, safe-area close, image/backdrop/Escape/focus/next-item all passed. Preferences and temporary test image URIs restored. No DX24 touch.

#### Validator authority

Per the Learning Value Gate, authoritative per-turn flag is `promptUsesNativeLanguage` (declared at `VocabQuiz.razor:603`, assigned by `ShouldUseNativePrompt()` at lines 2011-2015, sourced from `VocabularyQuizPreferences.DisplayDirection` in `VocabularyQuizPreferences.cs:47-54` normalized at lines 209-216).

---

### 2026-07-15 — Fixed five compounding defects in validate-mobile-migrations.sh; migration gate now functional

**By:** Simon (Backend Specialist, Escalation)
**Surface:** scripts/validate-mobile-migrations.sh, src/SentenceStudio.MacOS/.mauidevflow
**Status:** VERIFIED — Exit 0, all gates pass

#### Root Cause

Five compounding defects prevented the migration validation gate from functioning:

1. **Invalid CLI command** (`maui devflow MAUI logs`): The `MAUI` token was fuzzy-matched by the CLI parser to the `ui` subcommand, causing `logs` to be treated as an unrecognized argument under `ui`. Exit 1. The correct command is `maui devflow logs`.

2. **Wrong project/TFM**: Script targeted `SentenceStudio.MacCatalyst` with `net11.0-maccatalyst`. Captain's default native surface is macOS AppKit (`SentenceStudio.MacOS` / `net11.0-macos`).

3. **Wrong launch model**: macOS AppKit does not support `dotnet build -t:Run` (that's Catalyst/iOS). Must build then launch the `.app` bundle binary directly.

4. **Missing `ValidateXcodeVersion=false`**: Xcode 26.4 + Preview 5 SDK requires this escape hatch (per 2026-07-14 build-fix decision).

5. **No app identity check; wrong port**: Bare `maui devflow wait` with no `--agent-port` attached to a Comet iOS agent on default port 9223 instead of SentenceStudio on port 9225. Also `.mauidevflow` had port 9224 (stale) while `MacOSMauiProgram.cs` hardcodes 9225.

#### Additional finding: DevFlow logs --limit truncation

DevFlow `logs --source native --limit 500` returns the 500 most recent entries. When 500+ EF Core command log entries are emitted during migration, the earlier `MigrationSanityCheckService` "PASSED" signal is pushed out. Console output (via `ILogger.AddConsole()`) captures everything and is the reliable primary source for the sanity check signal.

#### Fix

##### scripts/validate-mobile-migrations.sh
- Changed TFM to `net11.0-macos` and project to `SentenceStudio.MacOS`
- Added `-p:ValidateXcodeVersion=false` to build command
- Launch app binary directly (not `-t:Run`), capturing console output via `ILogger.AddConsole()`
- Wait for DevFlow agent on explicit port 9225 with polling loop
- Verify agent identity (reject Comet/foreign framework agents)
- Scan both console output and DevFlow logs for errors
- Read sanity check signal from console output (primary) rather than DevFlow logs (may truncate)

##### src/SentenceStudio.MacOS/.mauidevflow
- Fixed port from 9224 to 9225 to match `MacOSMauiProgram.cs:45` hardcoded value

##### .squad/skills/ef-dual-provider-migrations/SKILL.md
- Updated step 5b description to reflect the new script behavior

#### Verification

Script passes end-to-end with exit 0:
- Build: macOS AppKit Debug with ValidateXcodeVersion=false ✅
- App identity: Microsoft.Maui.DevFlow.Agent / .NET MAUI 11.0.0 / SentenceStudio / com.simplyprofound.sentencestudio ✅
- Error scan: clean ✅
- Sanity signal: "Mobile schema sanity check PASSED — 8 tables, 13 columns verified" ✅

#### Upstream Note

`maui devflow logs` works when connected to the MAUI DevFlow agent (returns structured JSON). It returns `{"success":false,"error":"unimplemented"}` when connected to a Comet agent. This is expected behavior (different agent types), not an upstream bug. The earlier "unimplemented" observation during diagnosis was caused by the script's wrong-port attachment to the Comet agent.

---

### 2026-07-15 — iOS `dotnet build -t:Run` failure with space in dotnet install path is upstream regression already fixed in dotnet/macios main; pack not yet shipped

**By:** Simon (Backend Specialist)
**Surface:** iOS device and simulator builds
**Status:** Upstream fixed + verified; workaround documented

#### Problem

Jayne's native iOS E2E hit `dotnet build -t:Run -f net11.0-ios` failing at MSB3073 exit 127. Failing `.exec.cmd` line: `/var/folders/.../tmp*.exec.cmd: line 2: /Users/davidortinau/Library/Application: No such file or directory`. Command:

```
"/Users/davidortinau/Library/Application Support/dotnet/packs/Microsoft.iOS.Sdk.net11.0_26.5/26.5.11546-net11-p5/tools/bin/mlaunch --launchsim bin/Debug/net11.0-ios/iossimulator-arm64/SentenceStudio.iOS.app/ --device :v2:udid=67B0339B-4ACF-4946-88F3-FB961300F38B --wait-for-exit:true -- " exited with code 127.
```

Working directory `/Users/davidortinau/work/SentenceStudio` contains NO spaces. The space is in the **dotnet install root**: `~/Library/Application Support/dotnet` (macOS installer default / `dotnetup` default).

#### Root cause

Two-line chain in installed packs:

1. `Microsoft.iOS.Sdk.net11.0_26.5/26.5.11546-net11-p5/targets/Microsoft.Sdk.Mobile.targets` (`_ComputeMlaunchRunArguments`) sets `<RunCommand>$(MlaunchPath)</RunCommand>` **without** shell quoting.
2. `sdk/11.0.100-preview.5.26302.115/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.Sdk.targets` line 1464: `<Exec Command="$(RunCommand) $(RunArguments)" ... />`. MSBuild's `Exec` writes `Command` verbatim to a bash script. Bash splits on the unquoted space in `Application Support`, tries to exec `/Users/davidortinau/Library/Application`, fails with 127.

#### Upstream status — already tracked and fixed on `main`

- Issue: **[dotnet/macios#22481](https://github.com/dotnet/macios/issues/22481)** — open, but Microsoft engineer `vitek-karas` posted the **exact Preview 5 failure signature** on 2026-06-13 (same SDK `11.0.100-preview.5.26302.115`, same iOS pack `26.5.11546-net11-p5`, same MSBuild target line 1464, same `Library/Application` shell error). Reproduction adds no new signal.
- Fix: **[dotnet/macios#25680](https://github.com/dotnet/macios/pull/25680)** — merged 2026-06-16 into `main`. Quotes `MlaunchPath` in the generated install/run scripts and in `RunCommand`. Merge commit `5f4bb9f`.
- Current installed pack `26.5.11546-net11-p5` is Preview 5 (cut before the fix merged). Fix has not yet flowed to a publicly-installable iOS 26.5 pack; will appear in Preview 6+.

**No new upstream issue filed. No comment added to #22481** — signal is already captured verbatim by a Microsoft engineer using the same Preview 5 stack.

#### Verified workaround (Jayne already used)

Split the failing target into two verified steps that avoid the unquoted MSBuild-generated script:

```bash
# 1. Build (works — no mlaunch shell script)
dotnet build src/SentenceStudio.iOS/SentenceStudio.iOS.csproj \
  -f net11.0-ios -c Debug \
  -p:RuntimeIdentifier=iossimulator-arm64 \
  -p:ValidateXcodeVersion=false -p:MtouchLink=SdkOnly

# 2. Install + launch directly via xcrun simctl (no space-quoting bug)
xcrun simctl install <UDID> \
  src/SentenceStudio.iOS/bin/Debug/net11.0-ios/iossimulator-arm64/SentenceStudio.iOS.app
xcrun simctl launch <UDID> com.davidortinau.SentenceStudio
```

Alternative (untested, but eliminates the class of bug entirely): install .NET SDK to `/usr/local/share/dotnet` (Apple's official location, no spaces) instead of `~/Library/Application Support/dotnet`. This is orthogonal to `dotnetup`'s default.

#### Boundaries respected

- Did not modify SentenceStudio source, `global.json`, workloads, or git state.
- Did not touch the dedicated simulator UDID.
- Did not build a duplicate minimal repro outside the repo — read the exact target source and confirmed PR #25680 already documents the identical Preview 5 pack path failure.
- Did not comment on #22481 or file a duplicate — no new signal beyond what `vitek-karas` already posted.

#### Recommended follow-ups (non-blocking)

1. When Preview 6 iOS pack ships, refresh workloads and re-run `dotnet build -t:Run -f net11.0-ios` to confirm the fix reaches Captain's box.
2. Consider adding a note to `docs/deploy-runbook.md`: "If your dotnet install path contains a space (Application Support), `dotnet build -t:Run -f net11.0-ios` will fail with MSB3073 exit 127 until Preview 6+ iOS pack. Use `xcrun simctl install/launch` as a workaround. Upstream: dotnet/macios#22481, PR #25680."

---

### 2026-07-05 — Transcript example sentence capture and retroactive harvest

**Session:** Capture example sentences from transcripts at import time plus retroactive Settings utility
**Surface:** Shared data/import services, YouTube pipeline, Blazor Settings page, unit + WebApp E2E validation
**Requested by:** Captain (David Ortinau)
**Status:** Implemented and verified by Squad agents; pending Captain review/commit.

#### Decision

Example sentences from reading/transcript resources are now captured through one centralized FromReading path. Import-time capture persists AI-returned `ExampleSentence`/translation values from YouTube and content import flows instead of dropping them during `ToVocabularyWord` mapping. Retroactive capture is handled by `TranscriptSentenceHarvestService`, which deterministically segments transcript-backed resources for the active user, matches vocabulary by term/lemma/light Korean dictionary-form stem, batch-translates candidates through the existing AI service, and persists through the same repository helper.

All FromReading examples are stored as `Source=FromReading`, `Status=Curated`, never core, resource-linked where available, deduplicated on vocabulary word plus normalized target sentence, and capped at two FromReading examples per word/resource. Empty user identifiers fail closed, and resource scans are scoped to the active user's resources. No migration was added because the existing `ExampleSentence` schema already supports the feature.

#### Segmenter ruling

`TranscriptSentenceSegmenter.Split` is now the canonical sentence splitter. The default `splitOnNewlines: false` preserves Reading sentence-index behavior by collapsing newlines before punctuation splitting. Transcript harvesting opts into `splitOnNewlines: true` so caption-style punctuation-less lines become boundaries without changing Reading callers.

#### Settings utility

Settings > Data Management exposes a localized "Harvest example sentences from my resources" action. It resolves the active user via `IPreferencesService` key `active_profile_id`, which works for both MAUI preferences and WebApp claim/circuit-backed preferences.

#### Validation

Jayne added repository, segmenter, harvest service, and import-capture tests. Coordinator E2E verified the WebApp Settings harvest through Aspire + Playwright: FromReading examples grew from 0 to 97 with AI translations, rows were Curated and resource-linked, rerun stayed idempotent at 97 with no duplicates, the cap held, and multi-tenant scoping left other users and other sources untouched. Full unit suite passed: 781/781.

---

### 2026-07-11 — Vocabulary Add preselects no-results search term and profile language

**Session:** Vocabulary no-results Add prefill
**Surface:** Blazor WebApp Vocabulary pages
**Requested by:** Captain (David Ortinau)
**Status:** Implemented and E2E verified by Kaylee; pending Captain review/commit.

#### Decision

When a Vocabulary search has no results and the parsed query contains free-text terms, the Add flow carries the searched term into the Add Vocabulary page through an explicit `initialTargetTerm` query parameter. The new-word form prefills the target-language term from that value and preselects `wordLanguage` from the active user profile `TargetLanguage`, with Korean as a fallback.

The behavior is gated to the no-results Add path only: existing Edit remains unchanged, and empty-search Add continues to open a blank form.

#### Rationale

This matches the learner flow of searching for a missing word and immediately adding it, while avoiding accidental prefill from raw search/filter syntax or from edits of existing vocabulary. Using the active profile target language keeps the new entry aligned with the learner's current language context.

#### Validation

Kaylee validated the WebApp through Aspire E2E with the `squad-jayne@sentencestudio.test` account: searching `zzzqqqxyz` produced no results, Add opened `/vocabulary/edit/0?...initialTargetTerm=zzzqqqxyz` with the target term prefilled and Language set to Korean; empty-search Add opened blank; existing Edit was unaffected. Build validation: `dotnet build` of `SentenceStudio.UI` passed.

---

### 2026-07-12 — Vocab quiz fuzzy grading tightened: length-gated Levenshtein bypass prevents short-word collisions

**Author:** River (AI/Prompt Engineer)
**Requested by:** Captain (David Ortinau)
**Status:** Implemented + E2E verified

#### Decision

Added `MIN_LENGTH_FOR_DISTANCE_BYPASS = 5` to `FuzzyMatcher.EvaluateSingle`. The acceptance condition is now:

```
similarity >= 0.75 (length-relative, always active)
OR (distance <= 2 AND maxLength >= 5) (absolute distance bypass only for 5+ char words)
```

**Effect:** Words 1–4 chars must pass the 75% similarity threshold (prevents "day"→"buy", "big"→"bag" false accepts). Words 5+ chars retain the lenient distance bypass (valid for real typos like "fone"→"phone").

**No phonetic algorithm added:** Levenshtein already handles close phonetic typos on 5+ char words; far rewrites (nite→night) are intentionally rejected to enforce correct spelling in a learning app. Korean would need a separate algorithm — disproportionate complexity.

**Files:** `src/SentenceStudio.Shared/Services/FuzzyMatcher.cs`, `tests/SentenceStudio.UnitTests/Services/FuzzyAnswerMatcherTests.cs` (20 new adversarial tests, 140/140 pass).

---

### 2026-07-12 — E2E verification: FuzzyMatcher length-gate fix confirmed on live Blazor WebApp

**Author:** Jayne (Tester)
**Surface:** Blazor WebApp via Aspire + Playwright
**Status:** PASS

#### Verification Results

| Assertion | Input | Expected | Result |
|-----------|-------|----------|--------|
| Wrong short answer REJECTED | "lark" for 링크/link (4ch) | Reject | PASS — similarity 0.50 < 0.75, bypass blocked |
| Typo on long word ACCEPTED | "to wirte code" for 코드를 짜다 (13ch) | Accept | PASS — distance=2, length>=5, bypass active |
| Exact answer ACCEPTED | "code" for 코드 | Accept | PASS |

Final quiz result: 2/3 Correct. No regressions. DB restored after testing.

---

### 2026-07-14 — macOS AppKit build fix: Xcode version check bypass for P5 SDK + Xcode 26.4

**Author:** Simon (Backend Specialist)
**Surface:** macOS AppKit head build (`SentenceStudio.MacOS`)
**Status:** VERIFIED — No source changes required

#### Root Cause

`Microsoft.Maui.Platforms.MacOS` v0.26.0-dev NuGet package was built with the Preview 5 SDK, resolving its `net11.0-macos` TFM to `net11.0-macos26.5`, while the machine has Preview 4 SDK installed (which resolves to `net11.0-macos26.4`). TFM mismatch: NuGet cannot resolve `net11.0-macos26.5` assets on a P4 environment.

#### Fix

Skip the Xcode version check (which enforces Xcode 26.5) using the documented escape hatch:

```bash
dotnet build -f net11.0-macos -p:ValidateXcodeVersion=false \
  src/SentenceStudio.MacOS/SentenceStudio.MacOS.csproj
```

Xcode 26.4 contains sufficient native tools for MAUI AppKit; the SDK version difference does not impact the build.

**Result:** 0 errors, 325 warnings (all pre-existing). App bundle produced.

#### Scope

- No feature code modified.
- No project file changes required.
- Alternative: Rebuild `Microsoft.Maui.Platforms.MacOS` from `~/work/maui-labs` using P4 SDK (backup `.bak-26.5` already stored).

#### Validation

Simon confirmed build succeeds with zero compilation errors and app bundle produces correctly.


---

### 2026-07-15T19-17-33: Prototype B uses a DEBUG-only iOS native overlay above the mounted BlazorWebView
**By:** Simon
**What:** Prototype B uses a DEBUG-only iOS native overlay above the mounted BlazorWebView
**References:** src/SentenceStudio.iOS/BlazorHostPage.cs, src/SentenceStudio.iOS/NativePhotoViewerOverlayHandler.cs, src/SentenceStudio.iOS/MauiProgram.cs, src/SentenceStudio.AppLib/Controls/NativePhotoViewerOverlay.cs, src/SentenceStudio.Shared/Abstractions/PhotoViewerHostCoordinator.cs, src/SentenceStudio.UI/Pages/VocabQuiz.razor, tests/SentenceStudio.UnitTests/Services/PhotoViewerServiceTests.cs, plan.md Prototype B
**Why:** Implemented the native photo prototype as a reusable MAUI host view in SentenceStudio.AppLib with a UIKit handler in the iOS head. The iOS root remains a Grid with BlazorWebView first and the inactive/input-transparent overlay above it; no Shell, modal page, navigation stack, MauiReactor, or gesture dependency is introduced. DEBUG replaces the default photo-viewer service with the existing preference selector plus NativePhotoViewerService; Release keeps DefaultPhotoViewerService and the Razor viewer. UIScrollView owns pinch anchoring, momentum, bounce, 1x-4x zoom, centering, and double-tap zoom/reset. Only the safe-area close button dismisses; image/backdrop taps intentionally do not. Loading uses cancellable HttpClient requests, stale-generation rejection, explicit loading/error states, and an 8-entry/64 MiB encoded-data LRU cache. Accessibility uses generic labels and stable identifiers without logging image URLs; DEBUG viewport state contains only zoom, offsets, load state, and reset count.

Learning Value Gate: learning objective and language-role behavior are unchanged—the photo remains an optional mnemonic while the existing Vocab Quiz direction/text gates continue to guarantee target-language exposure or retrieval. No new direction, modality, default, answer set, or text-hiding state is introduced. The native request now uses the generic Full screen photo viewer label rather than vocabulary-specific alt text, reducing answer leakage risk. Default/no-preference behavior remains the verified Razor viewer; native is reachable only when debug_photo_viewer_prototype=native in DEBUG.
