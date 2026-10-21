# Live model evaluation (Microsoft.Extensions.AI.Evaluation)

`tests/SentenceStudio.Evaluation.Tests` runs the app's real **Import content** vocabulary extraction against a live Azure OpenAI deployment. It scores the result with one custom deterministic evaluator and two built-in LLM-judged evaluators, stores results and cached responses on disk, and renders an HTML report with `dotnet aieval`.

It makes paid model calls, so it never runs unless you opt in.

## Run it

```bash
# Prerequisites: az login (an identity with "Cognitive Services OpenAI User" on the resource)
#                and the .NET 11 SDK the repo already uses.

# 1. Live run (about 4 minutes, about $0.20 from an empty cache, $0.00 from a warm cache)
SS_RUN_LIVE_EVALS=1 SS_EVAL_EXECUTION_NAME=techorama-2026-10-21 \
  dotnet test tests/SentenceStudio.Evaluation.Tests/SentenceStudio.Evaluation.Tests.csproj \
  -p:IncludeMobileTargets=false --filter "Category=LiveEval" \
  --logger "console;verbosity=detailed"

# 2. HTML report (tool pinned in dotnet-tools.json)
dotnet tool restore
DOTNET_AIEVAL_TELEMETRY_OPTOUT=1 dotnet aieval report \
  --path artifacts/evaluation --output artifacts/evaluation/report.html
```

Run a single fixture with `--filter "Category=LiveEval&DisplayName~idiom-trap"`.

The offline checks need no network or credentials and cost nothing. They cover the request wire format, the app import path with a canned model reply, the scoring logic, and the fixture sanity rules:

```bash
dotnet test tests/SentenceStudio.Evaluation.Tests/SentenceStudio.Evaluation.Tests.csproj \
  -p:IncludeMobileTargets=false --filter "Category=Offline"
```

## Gating: why it never runs by accident

- Every live test uses `[LiveEvalTheory]`, which marks the test skipped unless `SS_RUN_LIVE_EVALS=1`. A plain `dotnet test` reports it as skipped and makes no call.
- Live tests carry `[Trait("Category", "LiveEval")]`, and the offline tests carry `Category=Offline`.
- CI never sees the project. `ci.yml` and `test.yml` build and test explicit project paths, and the project is not in `src/SentenceStudio.sln`.

## What is evaluated (the exact code path)

The suite exercises the same code the app runs when a learner pastes Korean text into **Import content** with the page defaults: content type Vocabulary, delimiter auto, words only.

```
ImportContent.razor RunPreview()                       (UI; mirrored by AppImportHarness.ParseAsync)
  ContentImportService.ParseContentAsync(request)        real, SentenceStudio.Shared (project reference)
    DetectFormat(...) -> "FreeText"                      real
    ParseFreeTextContentAsync(...)                       real
      FreeTextToVocab.scriban-txt                        the app's raw asset (AppLib/Resources/Raw), copied to output
      AiService.SendPrompt<FreeTextVocabularyExtractionResponse>(prompt)   real, direct-client branch
        keyed IChatClient (AiTier.Fast).GetResponseAsync<T>(prompt, AiChatOptionsFactory.Create())
    TranslateMissingNativeTermsAsync(...)                real, only if a row has no gloss (never triggered in the first run)
```

The server gateway path (`/api/v1/ai/chat` and `GetTypedResponseCoreAsync<T>`) makes the identical `GetResponseAsync<T>` call with the same null options. The eval client is built the way the API and WebApp hosts build theirs: `AzureOpenAIClient(resource root, Entra credential).GetChatClient(deployment).AsIChatClient()`.

Only the platform edges are replaced:

| App dependency | Eval stand-in | Why |
|---|---|---|
| `IFileSystemService` | Reads the copied raw assets | No MAUI app package |
| `IConnectivityService` | Always online | No device |
| `IChatClient` (fast tier) | Capture, then the MEAI disk response cache, then the output cap, then the spend meter, then Azure OpenAI | Caching, spend control, and observability |
| `DefaultAzureCredential` | `AzureCliCredential` | Resolves to the same `az login` identity locally, without slow probes |

The eval changes one thing about the model request: it adds `max_completion_tokens` (12,000 by default) to bound spend. The app sends no cap on this path. The cap sits below the response cache, so cache keys reflect exactly what the app sent. SDK retries are disabled, where the hosts keep Polly retries.

Out of scope: `EnrichPreviewWithDuplicateInfoAsync` (per-user duplicate flags, which needs a database), commit, and the auto-detect classifier.

## Fixtures

The fixtures live in `tests/SentenceStudio.Evaluation.Tests/Fixtures/import-vocabulary-fixtures.json`. There are six short synthetic Korean excerpts, two to four sentences each, written for this suite. None is third-party text.

| Id | What it tempts |
|---|---|
| `daily-routine` | Baseline |
| `conjugation-trap` | Surface forms instead of dictionary forms (past tense, -아서, -는데, particles) |
| `proper-noun-trap` | Person names, a fictional company whose parts look like ordinary words, a coffee brand |
| `mixed-language` | English words becoming "Korean" vocabulary |
| `idiom-trap` | Splitting idioms (발이 넓다, 귀를 기울이다, 마음이 놓이다) into literal parts |
| `irregular-trap` | Wrong lemmas for irregular honorific forms (추워서 to 추우다, 들으시고 to 들다) |

Each fixture lists:

- **`expected`**: lemmas with a gloss and curated alternates, for example 산책 / 산책하다. These drive recall.
- **`allowed`**: defensible extras, neither required nor penalized.
- **`forbidden`**: the mistakes the excerpt is designed to tempt. Any term containing Latin letters also counts as forbidden.

Excerpts contain no commas on purpose (see "App finding" below). The offline `Fixtures_AreWellFormed` test enforces this.

## Evaluators and thresholds

| Metric | Evaluator | Kind | Pass |
|---|---|---|---|
| Vocabulary Recall | `VocabularyCoverageEvaluator` (custom) | Deterministic, 0 to 1 | >= 0.80 |
| Vocabulary Precision | `VocabularyCoverageEvaluator` (custom) | Deterministic, 0 to 1 | >= 0.80 |
| Forbidden Terms | `VocabularyCoverageEvaluator` (custom) | Deterministic, count | 0 |
| Groundedness | `GroundednessEvaluator` (built-in) | LLM judge, 1 to 5 | >= 4 (library default) |
| Completeness | `CompletenessEvaluator` (built-in) | LLM judge, 1 to 5 | >= 4 (library default) |

How the custom evaluator scores:

- **Rows scored.** It scores the preview rows the app pre-selects, which is what a learner imports by pressing Import without editing. The app auto-deselects low-confidence rows. Those rows are listed in diagnostics but not scored.
- **Matching.** Terms are normalized before comparison: Unicode NFC, parenthetical notes, quotes and punctuation, `-` and `~` markers, and all whitespace are removed, and the result is lower-cased. There is no stemming. Which forms count is decided by the curated fixture, not by heuristics.
- **Recall.** Expected lemmas matched, counting the lemma or any alternate, divided by expected lemmas.
- **Precision.** Pre-selected rows that are expected, alternate, or allowed terms, divided by pre-selected rows.
- **Rating bands** for both ratios: Exceptional >= 0.95, Good >= 0.80, Average >= 0.60, Poor >= 0.40, otherwise Unacceptable.

What the LLM judges see:

- **Query.** The exact prompt the app rendered and sent.
- **Response.** The model's exact typed-JSON reply.
- **Groundedness** gets the excerpt as `GroundednessEvaluatorContext`. It asks whether every term and gloss is supported by the source text.
- **Completeness** gets the reference list as ground truth, alternates included, through `CompletenessEvaluatorContext`.

Why Completeness rather than Equivalence: the reference list is the minimum a learner should get, not the only acceptable answer. Equivalence would penalize legitimate extra vocabulary. Completeness asks whether every reference item is present and correctly glossed, and it does not punish extras.

In the first smoke run the judges were given a rendered list instead of the JSON. Groundedness then marked the reply down for "not returning the requested JSON". The suite now passes the real JSON.

## Models and judge adaptation

- **Model under test:** `gpt-5-mini`, the app's fast-tier default (`AiClientRegistration.FastModel`). It runs at the default reasoning effort, matching the app, which sends none on this path.
- **Judge:** `gpt-5` with reasoning effort `low`, set through the app's own `AiChatOptionsFactory` convention.
- **Judge options.** The built-in evaluators send `Temperature=0`, `TopP`, `Seed`, presence and frequency penalties, and `MaxOutputTokens=800`, all tuned for GPT-4o. `ReasoningJudgeChatClient` removes the sampling parameters, which reasoning deployments reject, and raises the output cap to 3,000. The 800-token cap would otherwise be spent on hidden reasoning. The evaluator prompts are not modified. The offline `JudgeRequest_*` test asserts the exact request body.
- **Environment overrides:** `SS_EVAL_AOAI_ENDPOINT`, `SS_EVAL_TARGET_DEPLOYMENT`, `SS_EVAL_JUDGE_DEPLOYMENT`, `SS_EVAL_JUDGE_REASONING_EFFORT`, `SS_EVAL_TARGET_MAX_OUTPUT_TOKENS`, `SS_EVAL_JUDGE_MAX_OUTPUT_TOKENS`, `SS_EVAL_BUDGET_USD`, `SS_EVAL_STORAGE_ROOT`, `SS_EVAL_EXECUTION_NAME`, and `SS_EVAL_ENFORCE_THRESHOLDS=1`. The last one fails the test on quality thresholds too. By default, quality results go to the report and only infrastructure errors fail the test.

## Budget and spend control

- **Ledger.** Every live call is metered below the caches and appended to `artifacts/evaluation/usage/live-calls.jsonl`, with token counts, latency, and estimated cost.
- **Budget guard.** Before each call, the meter reserves the call's worst case and refuses to send it if recorded spend plus that worst case would pass `SS_EVAL_BUDGET_USD` (default $1.00, across all runs). The worst case is input at one token per character plus schema, and output at the cap.
- **Rates.** Retail estimates, not an invoice. Cached-input discounts are ignored. gpt-5-mini is $0.25 input and $2.00 output per 1M tokens. gpt-5 is $1.25 and $10.00.
- **Worst case for one full run from an empty cache:**

  | Calls | Worst case |
  |---|---|
  | 6 target extractions | $0.15 |
  | 6 possible translation calls | $0.15 |
  | 12 judge calls | $0.45 |
  | **Total** | **$0.75** |

  Observed: $0.20 for the first run, including the smoke run.
- **Response caching** (MEAI disk cache, 60-day TTL) makes reruns free. Cache keys include a pipeline version, the deployments, effort, and caps. Change any of these, or a prompt template, and the affected calls re-execute.

## Output layout (gitignored, under `artifacts/evaluation/`)

| Path | Contents |
|---|---|
| `results/<execution>/<scenario>/1.json` | MEAI `ScenarioRunResult` (read by `dotnet aieval report`) |
| `cache/` | MEAI response cache for target and judge calls |
| `captures/<execution>/<fixture>.json` | Rendered prompt, raw model JSON, preview rows, app warnings |
| `usage/live-calls.jsonl` | Spend ledger |
| `report.html` | Generated report |

## Limits of interpretation

- **Scale.** Six synthetic fixtures are a demo-scale regression signal, not statistical evidence. One fixture flipping is noise until it repeats.
- **Single samples.** Each scenario is one sample of a non-deterministic model. Iterations (`CreateScenarioRunAsync(..., iterationName)`) would be needed to say anything about variance.
- **Judges are imperfect.** The LLM judges are themselves models. In the first run, both judges gave 5/5 to a reply containing a stray Chinese character in a gloss (`to守/observe`). Read judge scores alongside the deterministic metrics and the captures.
- **Curated references.** The deterministic metrics are only as good as the hand-curated references. A legitimate term missing from `expected` or `allowed` lowers precision.

## App finding surfaced while building this suite

In the default Vocabulary mode with delimiter auto, `ContentImportService.DetectFormat` treats a single-paragraph paste that contains one comma as CSV. That's because one line with one comma counts as "consistent". The text before the comma becomes the Korean term, the rest becomes the English translation, the row status is Ok, and no AI extraction runs. Multi-line prose with a comma on 60% or more of lines behaves the same way. This was reproduced offline with a canned client and is not fixed here.
