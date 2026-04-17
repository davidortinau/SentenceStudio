# Plugin.Maui.HelpKit — Eval Harness

Golden-set Q&A evaluation for the HelpKit RAG pipeline. Designed to catch regressions
before they ship, and in particular to hold the line on the one non-negotiable quality
bar: **zero fabricated citations**.

## What it checks

For every item in `golden-qa.json` the harness verifies:

1. **Keyword coverage** — the response contains every `expected_answer_keywords` entry
   (case-insensitive).
2. **Citation overlap** — at least one cited path in the response matches
   `required_citation_paths`.
3. **Refusal compliance** — when `must_refuse: true`, the response includes a refusal
   marker (English or Korean).
4. **No fabricated citations** — every cited path exists in `test-corpus/`. This is the
   hard gate.

At the end, the `CiGate_MustPass` test aggregates verdicts and enforces:

> correct >= 85%  AND  fabricated_citations == 0

Fail either condition and the build fails.

## Running

Default (deterministic, CI-safe) mode uses the scripted `FakeChatClient`:

```
dotnet test tests/Plugin.Maui.HelpKit.Eval
```

Live mode hits your real `IChatClient` (currently OpenAI; swap in Zoe's wiring when it
lands):

```
HELPKIT_EVAL_LIVE=1 \
OPENAI_API_KEY=sk-... \
HELPKIT_EVAL_MODEL=gpt-4o-mini \
dotnet test tests/Plugin.Maui.HelpKit.Eval
```

Environment variables:

| Var | Meaning | Default |
| --- | --- | --- |
| `HELPKIT_EVAL_LIVE` | `1` = call live IChatClient; anything else = FakeChatClient | unset (fake) |
| `OPENAI_API_KEY` | Required when `HELPKIT_EVAL_LIVE=1` | — |
| `HELPKIT_EVAL_MODEL` | Model id passed to the live client | `gpt-4o-mini` |

## Output

Per-item lines show `PASS` / `FAIL` with reason, plus the question and a truncated
response. The final `CiGate_MustPass` test prints the summary:

```
==== HelpKit Eval Summary ====
Mode         : FAKE
Items        : 30
Correct      : 30 (100.0%)
Fabricated   : 0
Threshold    : correct >= 85% AND fabricated == 0
```

## Layout

```
Plugin.Maui.HelpKit.Eval/
  golden-qa.json          30 Q&A items grounded in SentenceStudio features
  GoldenSet.cs            loader (embedded resource with disk fallback)
  FakeChatClient.cs       scripted IChatClient keyed by SHA-256 of question
  EvalRunner.cs           xUnit [Theory] harness + CI gate [Fact]
  test-corpus/            minimal .md corpus the retrieval stub uses
    dashboard/
    activities/
    vocabulary/
    profiles/
    resources/
    settings/
    sync/
  README.md               this file
```

## Adding a new golden item

1. Append a new object to `items` in `golden-qa.json`. Use the next `qa-NNN` id.
2. Choose a `category` that matches the feature area.
3. Fill in `expected_answer_keywords` — keep them tight and objective (they are matched
   case-insensitively as substrings).
4. Fill in `required_citation_paths` with the relative `.md` paths the answer should
   cite. Leave empty for `must_refuse: true` items.
5. If the feature isn't covered in `test-corpus/`, add a short stub `.md` so the
   retrieval set has something to return.
6. Run `dotnet test`. The `FakeChatClient` auto-generates canned responses from the
   golden item at construction time, so fake-mode stays deterministic without any
   manual edits.

## Modes of failure to watch for

- **Live mode drift** — the live IChatClient can pass fake-mode and still fail the
  real gate. Always run `HELPKIT_EVAL_LIVE=1` before tagging a release.
- **Korean keyword escaping** — ensure JSON source is valid UTF-8; don't let your
  editor re-encode it. Korean keywords are matched by substring, so include the
  exact Hangul you expect in the answer.
- **Corpus drift** — if you rename a `.md` file in `test-corpus/`, update every
  `required_citation_paths` entry that references it, otherwise the overlap check
  will fail.

## Notes for Zoe

- The `.csproj` should declare `golden-qa.json` as `<EmbeddedResource>`. The loader
  falls back to disk so things still work during local iteration if the embed is
  forgotten, but CI should rely on the embedded copy.
- The `test-corpus/` folder should be copied to output (`CopyToOutputDirectory`) so
  `EvalRunner.EnumerateCorpusPaths()` can find it next to the test assembly.
- Live mode currently throws `NotImplementedException` until an OpenAI `IChatClient`
  is referenced. Wire it in `EvalRunner.BuildChatClient()` — see the inline TODO.
- xUnit parallelism: `CiGate_MustPass` relies on the `Item_meets_contract` theory
  having populated the verdict ledger first. If parallelism gets turned on, move
  the gate test into its own test collection that runs after the theory collection.
