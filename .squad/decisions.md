## Active Decisions

(Most recent decisions below. Archived decisions in `decisions-archive-YYYY-MM-DD.md`)

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

