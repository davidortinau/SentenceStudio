## Active Decisions

(Most recent decisions below. Archived decisions in `decisions-archive-YYYY-MM-DD.md`)

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

