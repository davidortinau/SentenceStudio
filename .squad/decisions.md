## Active Decisions

(Most recent decisions below. Archived decisions in `decisions-archive-YYYY-MM-DD.md`)

---

## 2026-04-18 — Phase 1 Display Language Restoration (Complete)

**Date:** 2026-04-18  
**Owner:** Zoe (Lead), Kaylee (Frontend), Wash (Backend), Jayne (Tester)  
**Status:** ✅ COMPLETE — All P0 scenarios pass E2E  
**Tracking:** Phase 1 closed, Phase 2 & Phase 3 backlogged

### Summary

Display Language feature was broken after MauiReactor → Blazor migration. Captain reported: changing language on Profile doesn't update any app strings. Phase 1 scope: restore end-to-end localization infrastructure + localize NavMenu + Profile pages. Korean as priority language. Spanish/French deferred to Phase 2.

**Root causes identified:**
1. Profile save never calls `LocalizationManager.SetCulture` (dead code path)
2. Blazor UI is ~99% hardcoded English literals; only "Help" in NavMenu uses localization
3. No startup culture wiring (`AddLocalization()` / `UseRequestLocalization` missing)
4. Resx file (488 keys) orphaned from Blazor rendering path

### Phase 1 Architecture

**Blazor WebApp path:** Scoped `BlazorLocalizationService` per-circuit (no cross-user leak). On startup, `UseRequestLocalization` reads `.AspNetCore.Culture` cookie (set by last Profile save). `BlazorLocalizationService` holds its own `CultureInfo`, reads resx via new `LocalizationManager.GetString(key, culture)` overload, raises `CultureChanged` event for components to re-render.

When user saves Profile: (1) flips circuit's culture via `Localize.SetCulture`, (2) components subscribed to `CultureChanged` re-render immediately, (3) navigates with `forceLoad:true` to `/account-action/SetCulture?culture=ko&returnUrl=/profile`, which writes `.AspNetCore.Culture` cookie and redirects back—next request everything is Korean.

**MAUI Blazor Hybrid path:** On launch, `LocalizationInitializer` (IMauiInitializeService) reads saved `UserProfile.DisplayLanguage`, calls `LocalizationManager.Instance.SetCulture(culture)`. This sets process-wide `DefaultThreadCurrentUICulture` (fine in single-user client). When user saves from Profile, same sequence runs without cookie redirect (single-user process).

**Database:** `UserProfile.DisplayLanguage` column exists and is migrated (verified Round 1 of this session). No schema risk.

**Culture identifier alignment (critical):** All five touchpoints use neutral `ko` (not `ko-KR`):
- DB: `UserProfile.DisplayLanguage` stores `"ko"`
- Cookie: `.AspNetCore.Culture=c=ko|uic=ko`
- Whitelist: `Program.cs` `SupportedCultures = [new("en"), new("ko")]`
- Endpoint validator: `/account-action/SetCulture` whitelist is `["en", "ko"]`
- Resx: `AppResources.ko.resx` + `AppResources.ko-KR.resx` renamed to `AppResources.ko.resx` (via Wash Round 2)

ResourceManager fallback chain: `ko` → invariant → throws. Previous state had `ko` in code/cookie/DB but `ko-KR` in resx, so fallback walked `ko` → invariant, missing the satellite. Wash's Round 2 fix aligns all five.

**Resx manifest (`<LogicalName>` override, critical):** `AppResources.Designer.cs` hardcodes neutral resource path as `"SentenceStudio.Resources.Strings.AppResources"`, but MSBuild's default embeds as `"SentenceStudio.Shared.Resources.Strings.AppResources"` (includes assembly name). Mismatch → `MissingManifestResourceException` at runtime. Wash's Round 1 fix added `<LogicalName>` override to `.csproj` to force correct stream name. This latent bug was weaponized by Kaylee's increased use of `Localize[]`.

### Files Changed

**Code (Kaylee):**
- `src/SentenceStudio.UI/Services/BlazorLocalizationService.cs` — rewritten scoped, holds `_culture`, raises `CultureChanged`, no process-wide mutation on web path
- `src/SentenceStudio.UI/Services/BlazorUIServiceExtensions.cs` — Singleton → Scoped
- `src/SentenceStudio.MacOS/MacOSMauiProgram.cs` — Singleton → Scoped
- `src/SentenceStudio.Shared/Common/LocalizationManager.cs` — public `GetString(key, CultureInfo?)`
- `src/SentenceStudio.AppLib/Setup/LocalizationInitializer.cs` (NEW)
- `src/SentenceStudio.AppLib/Setup/SentenceStudioAppBuilder.cs`
- `src/SentenceStudio.WebApp/Program.cs`
- `src/SentenceStudio.WebApp/Auth/AccountEndpoints.cs`
- `src/SentenceStudio.UI/Layout/NavMenu.razor` — 11 `Nav_*` keys
- `src/SentenceStudio.UI/Pages/Profile.razor` — 30 `Profile_*` keys
- `src/SentenceStudio.Shared/Resources/Strings/AppResources.resx` — +56 keys (en)
- `src/SentenceStudio.Shared/Resources/Strings/AppResources.ko.resx` — +56 Korean (renamed)

**Csproj (Wash):**
- `src/SentenceStudio.Shared/SentenceStudio.Shared.csproj` — `<LogicalName>` overrides + culture filename rename

**Squad artifacts:**
- `.squad/skills/blazor-localization/SKILL.md` (NEW — Kaylee documented pattern for reuse)

### E2E Testing (3 Rounds)

**Round 1 (Jayne):** REJECT — `MissingManifestResourceException` on every page. Fixed by Wash (R1 manifest).

**Round 2 (Jayne):** REJECT — Page loads, but UI stays English despite `ko` cookie. Fixed by Wash (R2 culture rename).

**Round 3 (Jayne):** ✅ APPROVE — All P0 scenarios pass.
- Scenario 1: Set Display Language → Korean, NavMenu + Profile flip to Hangul (대시보드 / 활동 / 학습 자료 / 어휘 / ... / 프로필 / 설정 / 피드백 / 로그아웃)
- Scenario 2: Revert to English, UI reverts cleanly
- Scenario 3 (P0): Cross-user isolation — Browser A (Korean circuit), Browser B (fresh cookie) stays English. Scoped service architecture confirmed sound. No cross-circuit leak.

**Confidence:** 100% — live runtime validation, multiple language switches, simultaneous browser isolation.

### Decisions

1. **Use scoped `BlazorLocalizationService`** (not singleton) — each circuit gets its own `CultureInfo`, read from cookie at request start. MAUI path additionally calls `LocalizationManager.Instance.SetCulture` for process-wide state (acceptable in single-user client).
2. **Cookie round-trip via `/account-action/SetCulture` endpoint** — SignalR cannot write HTTP cookies directly; endpoint pattern matches existing `/SignOut` / `/AutoSignIn` infrastructure.
3. **Use neutral `ko` culture identifier everywhere** — all five touchpoints (DB, cookie, whitelist, endpoint, resx) must match. ResourceManager fallback chain expects parent-language resources.
4. **`<LogicalName>` override in `.csproj` is required** — latent bug exposed by mass-localization. Designer hardcodes neutral resource path; csproj config forces correct embed stream name.
5. **Phase 2 backlog: Dashboard + remaining pages + es/fr/ja/zh resx** — Phase 1 proves infrastructure, Phase 2 is content localization.
6. **Tech debt (5 follow-ups):**
   - Culture cookie needs `HttpOnly=true` + `Secure` flags
   - `/SetCulture` GET endpoint is CSRF-able by construction (impact bounded, acceptable for Phase 1)
   - `Toast.ShowSuccess` on web path swallowed by `forceLoad:true` redirect (UX nit)
   - Startup `LocalizationInitializer` uses blocking sync-over-async (matches existing patterns)
   - Legacy resx keys (`Dashboard`, `Settings`) orphaned after Phase 2 — Phase 3 cleanup
7. **MauiReactor residue (LocalizationManager, LocalizeExtension, FilterChip) stays** — Phase 3 cleanup per Captain's "don't touch MauiReactor residue" directive.

### Reviewer Lockout Enforcement

Kaylee was locked out after Zoe's code review approval. Both Wash hotfixes honored the lockout:
- Round 1 (manifest fix): Wash applied, Kaylee did NOT touch
- Round 2 (culture rename): Wash applied, Kaylee did NOT touch

Lockout was per-artifact (Phase 1 implementation code), not per-agent.

### Next

- Push Phase 1 to Captain for `/review` gate
- Phase 2 tracking issue: localize Dashboard welcome card + remaining Blazor pages + add es/fr/ja/zh resx
- Phase 3 tracking issue: retire orphaned keys + MauiReactor residue cleanup

---

## 2026-04-18 — Diagnosis: MacCatalyst Post-Login Splash Hang

**Date:** 2026-04-18  
**Owner:** Copilot (Coordinator)  
**Status:** Pending Implementation  
**Tracking:** `maccatalyst-forceload-fix` (todo)

### Context

Captain logged in via MacCatalyst, got stuck on dark-navy splash (translation icon only). Dashboard never loaded.

### Root Cause

`LoginPage.razor:130` and `RegisterPage.razor:172` call `NavManager.NavigateTo(returnUrl, forceLoad: true)` after successful login. In BlazorHybrid (MAUI), `forceLoad: true` forces a full WebView document reload of `app://0.0.0.0/`. The WebView reloads but the Blazor component tree does not cleanly re-bootstrap its auth state + cascade, leaving the user on a rendered-but-empty layout.

### Evidence

- API structured logs: `/api/auth/login` returned 200 with JWT; zero follow-up API calls from Dashboard → no component initialization
- macOS unified log: WebKit alive and looping every 2s, but no Blazor component render events
- Code inspection: `LoginPage.razor:126 & 130` and `RegisterPage.razor:172` all use `forceLoad: true`; pattern identical to `NavMenu.razor:106-107` (isWeb detection already established)

### Fix (Durable, ~2 Files)

1. `LoginPage.razor` + `RegisterPage.razor`: platform-gate `forceLoad` using `isWeb` pattern from `NavMenu.razor`
2. MAUI branch: call `MauiAuthenticationStateProvider.LogInAsync()` (triggers `NotifyAuthenticationStateChanged`) + `NavigateTo(returnUrl)` WITHOUT `forceLoad`
3. Web branch: keep existing `forceLoad: true` (cookie-backed auth survives reload)

### Immediate Unblock (No Code Change)

Restart `maccatalyst` resource from Aspire dashboard. On cold start, `MauiAuthenticationStateProvider` fast-paths to authenticated state from `SecureStorage` → Shell routes to Index normally → Dashboard loads.

### Recommendation

Apply durable fix in next session. High-risk changes (auth + nav) warrant full `/review` gate before merge.

---

## 2026-04-17 — Help Flyout Menu Item for MAUI Hybrid

**Date:** 2026-04-17  
**Owner:** Zoe  
**Status:** Implemented  
**Commit:** 8d71a41

### Context

Captain ordered: "Wire the Help trigger into the SentenceStudio app as a flyout/sidebar menu item."

SentenceStudio is a Blazor Hybrid app:
- MAUI apps (iOS/MacCatalyst) use `BlazorWebView` hosting `SentenceStudio.UI` (Razor class library)
- Standalone WebApp (ASP.NET Core) uses the same `SentenceStudio.UI` components
- HelpKit (Plugin.Maui.HelpKit) is registered ONLY in MAUI apps via `UseHelpKit()` in `MauiProgram.cs`
- WebApp does NOT have HelpKit (it's a MAUI-only library)

The challenge: `SentenceStudio.UI` is a plain Razor class library (`<TargetFramework>net10.0</TargetFramework>`, `<SupportedPlatform>browser</SupportedPlatform>`) and CANNOT reference MAUI packages.

### Decision

1. **Add Help menu item to `SentenceStudio.UI/Layout/NavMenu.razor`** (used by both MAUI and WebApp)
2. **Use dynamic type resolution** to keep the UI project portable:
   - Check at runtime if `Plugin.Maui.HelpKit.IHelpKit` is registered
   - Invoke `ShowAsync()` via reflection if available
   - Gracefully hide the Help button when HelpKit is not registered (WebApp)
3. **Add localization keys** ("Help" / "도움말") to `AppResources.resx` + Korean
4. **Use Bootstrap icon** `bi-question-circle` (no custom icon needed)

### Implementation

```razor
@inject IServiceProvider Services
@inject BlazorLocalizationService Localize

<a class="nav-link nav-link-ss rounded px-3 py-2"
   href="" @onclick="ShowHelpAsync" @onclick:preventDefault>
     <i class="bi bi-question-circle"></i> <span class="nav-label">@Localize["Help"]</span>
</a>

@code {
    private bool _isHelpKitAvailable;

    protected override void OnInitialized()
    {
        var helpKitType = Type.GetType("Plugin.Maui.HelpKit.IHelpKit, Plugin.Maui.HelpKit");
        _isHelpKitAvailable = helpKitType is not null && Services.GetService(helpKitType) is not null;
    }

    private async Task ShowHelpAsync()
    {
        if (!_isHelpKitAvailable) return;
        
        var helpKitType = Type.GetType("Plugin.Maui.HelpKit.IHelpKit, Plugin.Maui.HelpKit");
        var helpKit = Services.GetService(helpKitType);
        var showMethod = helpKitType.GetMethod("ShowAsync");
        await (Task)showMethod.Invoke(helpKit, [default(CancellationToken)])!;
        await CloseOffcanvas();
    }
}
```

### Why Dynamic Resolution?

**Alternative A (rejected):** Add MAUI package reference to `SentenceStudio.UI`
- ❌ Breaks WebApp build (MAUI libs incompatible with browser target)
- ❌ Forces UI project to become multi-targeted
- ❌ Pollutes shared UI with platform concerns

**Alternative B (chosen):** Dynamic resolution via reflection
- ✅ UI project stays plain Razor class library (browser-only)
- ✅ Works in both MAUI (HelpKit present) and WebApp (HelpKit absent)
- ✅ No compile-time dependency on MAUI packages
- ✅ Graceful degradation (Help button hidden when HelpKit not available)

### Files Changed

- `src/SentenceStudio.UI/Layout/NavMenu.razor` — Help button + reflection logic
- `src/SentenceStudio.Shared/Resources/Strings/AppResources.resx` — "Help" key
- `src/SentenceStudio.Shared/Resources/Strings/AppResources.ko-KR.resx` — "도움말" key

### Result

✅ Help menu item appears at bottom of sidebar in MAUI apps (iOS/MacCatalyst)  
✅ Tapping it invokes `IHelpKit.ShowAsync()` → opens HelpKit overlay  
✅ WebApp build unaffected (UI project remains browser-only)  
✅ No emojis used (Bootstrap icon `bi-question-circle` only)  
✅ Localized in English + Korean  
✅ Build succeeded: 0 errors, 97 warnings (pre-existing)

---

## 2026-04-17 — Plugin.Maui.HelpKit Alpha Build Complete

### Public API (Zoe)
Contract frozen at 0.1.0-alpha. `HelpKitOptions`, `IHelpKit` (five methods: Show/Hide/ClearHistory/Ingest/StreamAskAsync), `IHelpKitPresenter`, `IHelpKitContentFilter` (DefaultSecretRedactor). All registrations use `TryAddSingleton` for host customization.

### RAG Pipeline (River)
Wave 1 code landed: `MarkdownChunker` (512/128 token, paragraph-boundary, GitHub slug anchors), `PipelineFingerprint` (SHA-256 of model+chunker+size+overlap), `CitationValidator` (regex parse + fallback to path-only), `SimilarityThresholds` (per-model table, 0.35–0.75 cosine), `SystemPrompt` (delimiter-fenced docs, grounding rules, citation format), `PromptInjectionFilter` (output fingerprint leak detector). Chunker version, threshold table, and phrase set are locked into design doc.

### Storage, Ingestion, Rate Limit & Diagnostics (Wash)
SQLite via sqlite-net-pcl (singular table names, string GUID PKs, UTC ticks). Pipeline fingerprint gates re-ingest; answer cache (7-day TTL) invalidated wholesale on fingerprint change. Rate limiter: concurrent sliding window, 10 q/min default, per-user buckets. Vector store: hand-rolled in-memory + JSON persistence (no Microsoft.Extensions.VectorData concrete pkg in Alpha). Diagnostics: `HelpKitMetrics` exposes counters (ingest, retrieval, LLM tokens, cache, rate limit). Scanner: non-AI XAML walker emitting one `.md` per page (title, route, field names); AI enrichment deferred to Beta.

### UI & Presenters (Kaylee)
`HelpKitPage` (CollectionView chat, streaming mutation, auto-scroll). `DefaultPresenterSelector` resolves at call-time (Shell first, Window fallback). `HelpKitLocalizer` (embedded JSON, en/ko, fallback to key). Accessibility: SemanticProperties + AutomationProperties set; message bubbles include role in a11y name; input disabled while streaming; no color-only role distinction. Twelve localization keys shipped; runtime theme switching deferred. Shell flyout helper wired via `IMauiInitializeService`.

### Samples (Kaylee)
Three runnable projects (Shell, Plain, MauiReactor) with shared stubs. `StubChatClient` (deterministic 4-answer router, 30ms streaming delay, citation tokens). `StubEmbeddingGenerator` (32-dim FNV-1a hash vectors, stable). `SampleHelpContentInstaller` (copies bundled `.md` to AppData on first run). Each sample includes provider-migration comments (Azure OpenAI, OpenAI, Ollama, unkeyed).

### Eval Harness & CI Gate (Jayne)
30 golden Q/A over SentenceStudio help corpus. CI gate: **>= 85% correct AND 0 fabricated citations**. Two modes (deterministic `FakeChatClient` default, live opt-in `HELPKIT_EVAL_LIVE=1`). Seven unit-test files landed: CitationValidator, MarkdownChunker, PipelineFingerprint, PromptInjectionFilter, RateLimiter, AnswerCache, extended DefaultSecretRedactor. Four-TFM smoke-test checklists. 80% line coverage enforced on Rag, Storage, RateLimit, DefaultSecretRedactor folders.

### CI + Documentation (Zoe)
`.github/workflows/helpkit-ci.yml`: matrix build (macOS+iOS, Linux+Android, Windows), unit tests, eval gate. `DOTNET_ROLL_FORWARD=LatestMajor`, net11 preview SDK + prerelease flag. `README.md`: MIT license, honest FAQ (offline caveat, no-hallucination caveat, citations are validated), provider-neutral quickstart (four examples), explicit Alpha defects list. `SUPPORT.md`, `SECURITY.md`, `CONTRIBUTING.md` drafted. `EXTRACT-RUNBOOK.md`: post-Alpha path to standalone repo via `git subtree split`.

### SentenceStudio Dogfood Integration (Wash) — BLOCKED
Code staged dormant under `#if NET11_0_OR_GREATER`. Eleven help articles (Getting Started, Dashboard, Profile, Sync, Settings, six activities). DI wiring: unkeyed + keyed aliases (no-op today). Embedding model: `text-embedding-3-small` from existing OpenAI client. Per-profile history via `Preferences.Get("active_profile_id")`. Content in AppData, copied from bundle on first run. **Blocker: net10 head cannot reference net11-only library.** Three unblock options: (1) Bump SentenceStudio to net11 dev (counter to Captain's intent). (2) Multi-target HelpKit to also include net10 TFMs (Zoe's call; recommended by Wash). (3) Activate only on iOS Release publish path (dev loop dogfooding lost). **Wash recommends Option 2**: lowest-risk, keeps Captain's net10 workflow, enables dogfooding in real builds, reversible at Alpha close.

---

## 2026-04-17 — Plugin.Maui.HelpKit Planning

### 2026-04-17T20:21Z: Plugin.Maui.HelpKit — Alpha scope locked (Captain verdicts)
**By:** Captain (David Ortinau) via Squad coordinator
**Context:** Plan v2 open questions answered, Alpha scope now frozen.

**Decisions:**
1. **UI pivot confirmed** — Native MAUI chat (CollectionView + streaming) is PRIMARY for Alpha. BlazorWebView deferred to post-Alpha optional companion package.
2. **Incubation confirmed** — Develop inside `lib/Plugin.Maui.HelpKit/` in SentenceStudio until end of Alpha. Extract to standalone repo at Alpha close via `git subtree split`.
3. **Storage default confirmed** — `Microsoft.Extensions.VectorData` in-memory + JSON disk persistence. `sqlite-vec` fully deferred to v1 (weeks of native-build work; not Alpha-worthy).
4. **License: MIT.**
5. **AI provider ownership: host app brings the `IChatClient` AND `IEmbeddingGenerator`.** HelpKit does NOT ship, bundle, or recommend a specific model. Samples in SentenceStudio demonstrate wiring to the Captain's existing Foundry-hosted model. README documents "bring your own M.E.AI client" with examples for OpenAI, Azure OpenAI, Foundry, Ollama. No MiniLM ONNX shipping.
6. **Stub scanner: shipped in Alpha.** Non-AI page scanner that emits one `.md` per detected XAML/MauiReactor page (title + route + field names). AI-enriched scanner stays in Beta.
7. **TFMs: `net11.0-*` MAUI targets.** net9 is out of support imminent; Captain is all-in on net11 previews. If community demand surfaces for net10, we can multi-target at Alpha close — but primary target is net11.
8. **Rate limit default: 10 questions/min**, configurable via `HelpKitOptions.MaxQuestionsPerMinute`.

**Implications:**
- R1 (sqlite-vec) is officially shelved for Alpha → gate-zero SPIKE-1 drops the sqlite-vec variant entirely; focus purely on native-first + in-memory VectorData Release-on-device.
- R3 (BlazorWebView) is officially shelved for Alpha → no Blazor spike needed.
- Embedding-dimension handling (Skeptic H1) still requires SPIKE-1 validation since dev-provided embedding generator means dimension is not fixed at package time. Pipeline fingerprint gates re-ingest on model/dimension change.
- net11 preview TFM means CI must use the net11 preview SDK; document global.json handoff for the standalone repo.
- "Bring your own client" messaging becomes central in README alongside the honesty fixes.

**Next:**
- SPIKE-1 and SPIKE-2 unblock (gate-zero).
- Zoe updates plan.md with net11 TFM and "app owns the model" framing.
- README draft incorporates MIT + BYO-IChatClient.


---

## 2026-04-17 — .NET SDK Detection Skill (Reframed)

**Author:** Squad Coordinator  
**Captain Directive:** "the skill I wanted you to write was about how you should determine what version of .net you have installed to use, which ought to include awareness of global.json as a thing that you might encounter. So it should be useful in any .net project environment."

### What Changed

1. **Renamed:** `.squad/skills/dotnet-sdk-pinning/` → `.squad/skills/dotnet-sdk-detection/` with complete `SKILL.md` rewrite.
   - New scope: generic 100-level skill for ANY .NET project — "which SDK is the CLI actually picking?"
   - 4-layer mental model: installed SDKs vs. selected SDK vs. workload manifests vs. project TFMs
   - `global.json` as ONE input among several (env vars, `.gitignore` status, walk-up behavior), not headline
   - Includes "newer SDK can build older TFMs" rule with MAUI-workload + Xcode wrinkles
   - Worked example anonymized (no agent names)

2. **Corrected AGENTS.md:** Replaced "## SDK Pinning via global.json" with "## .NET SDK Selection in This Repo" — accurate facts:
   - `global.json` is gitignored (`.gitignore:412–414`), per-machine, NOT a project convention
   - Captain pins net10 locally because default machine SDK is net11 preview; pin makes `dotnet` commands use matching net10 SDK + workload
   - Other contributors / CI / fresh checkouts do NOT need `global.json`
   - Publish-workflow `global.json` swap is iOS-only, Xcode 26.3-driven (per `docs/deploy-runbook.md` Step 2a) — Azure uninvolved

### Why Prior Version Was Wrong

Earlier iterations guessed at rationale (workload alignment / Azure can't host preview). Captain rejected both with "if you cannot explain it, it's just an impediment we don't need." Real story found in `docs/deploy-runbook.md` Step 2a (Xcode 26.3 mismatch) and `.gitignore:412–414` (file never committed).

### Scope

- `.squad/skills/dotnet-sdk-detection/SKILL.md` — new file (rewrite)
- `.squad/skills/dotnet-sdk-pinning/` — deleted (renamed)
- `AGENTS.md` — section replaced
- No code/csproj/global.json changes

### Standing Rule (Enforced Going Forward)

Before agents write "X SDK isn't installed" / "we need to multi-target" / "build using wrong framework," they MUST run diagnostic order in `.squad/skills/dotnet-sdk-detection/SKILL.md`. Routing-relevant skill — surface in spawn prompts when work touches `dotnet build/restore/test/publish`.

