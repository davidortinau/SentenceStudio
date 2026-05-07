---
name: "blazor-activity-layout-shell"
description: "Canonical Blazor activity-page layout shell for SentenceStudio webapp. Copy VocabQuiz verbatim; only swap inner content."
domain: "blazor-hybrid-ui"
confidence: "medium"
source: "earned (publishes #5–#9, NumberDrill Phase 1, 2026-05-06 / 2026-05-07)"
---

## Context

When building a **new activity page** in `src/SentenceStudio.UI/Pages/*.razor` (Blazor Hybrid webapp), there is exactly one canonical layout shell. **Copy it verbatim from `VocabQuiz.razor`. Only swap inner content.** Do not invent your own outer structure.

This rule was earned by failing it three times. Across publishes #7, #8, and #9, NumberDrill kept getting rebuilt with subtly-different outer markup; each rebuild produced a different visual defect (footer not pinned, card wrapper around content, empty input-bar div rendering as chrome strip). The fix every time was "make it look like VocabQuiz." There is no good reason to deviate.

**Apply this skill when:** authoring a new activity page, refactoring an existing activity page's outer shell, or diagnosing why an activity page's footer/chrome looks different from VocabQuiz.

## Patterns

### Canonical shell — copy this verbatim

Reference: [`src/SentenceStudio.UI/Pages/VocabQuiz.razor`](../../../src/SentenceStudio.UI/Pages/VocabQuiz.razor) lines 8–27, 382.

```razor
<div class="activity-page-wrapper">

    <PageHeader Title='@Localize["YourActivityTitle"]' ShowBack="true" OnBack="GoBack" />

    <div class="activity-content">
        @* === your scrolling activity content goes here === *@
        @* The .activity-content div is the only thing that scrolls. *@
        @* Everything outside it is fixed chrome. *@
    </div>

    @* OPTIONAL — pinned footer. Omit entirely if you don't need one. *@
    <div class="activity-footer d-flex justify-content-between align-items-center">
        @* footer controls *@
    </div>

    @* OPTIONAL — pinned input bar. Omit entirely if you don't need one. *@
    <div class="activity-input-bar">
        @* input UI *@
    </div>

</div>
```

### CSS contract (do not change)

`src/SentenceStudio.UI/wwwroot/css/app.css` lines ~1381–1424 define the contract:

| Class | Behavior |
|-------|----------|
| `.activity-page-wrapper` | `display: flex; flex-direction: column; height: calc(100% + 2rem)` — fills the main content area edge-to-edge |
| `.activity-content` | `flex: 1; overflow-y: auto; min-height: 0` — the ONLY scrolling region |
| `.activity-footer` | `flex-shrink: 0; border-top; padding-bottom: safe-area-inset-bottom` — pinned to bottom |
| `.activity-input-bar` | `flex-shrink: 0; border-top; padding-bottom: safe-area-inset-bottom` — also pinned, same chrome |

**Both `.activity-footer` and `.activity-input-bar` paint visible chrome unconditionally.** The flex-shrink:0 + border-top + safe-area-inset-bottom triad is what pins them to the bottom edge correctly.

### Decision tree for new activity pages

1. Will the page have a primary action area (Submit / Next / etc.)? → use `.activity-footer`.
2. Will the page have a free-text input or composer? → use `.activity-input-bar`.
3. Need both? → emit both, in that order, both inside `.activity-page-wrapper`.
4. Need neither? → omit both. Do not emit empty placeholders.

## Anti-Patterns

### ❌ #1 — Empty `<div class="activity-input-bar">` left behind

Observed: Publish #9 (NumberDrill).

```razor
@* WRONG — empty div will render a visible chrome strip *@
<div class="activity-input-bar">
    @* nothing here for this activity mode *@
</div>
```

The class paints `border-top` + `padding` + `safe-area-inset-bottom` regardless of children. On iPhone with home-indicator, that's a ~50px visible strip below your footer.

**Fix:** omit the div entirely. Do not leave it "for symmetry."

### ❌ #2 — Outer card wrapper around activity content

Observed: Publishes #7 and #8 (NumberDrill).

```razor
@* WRONG — VocabQuiz does NOT have a card wrapper *@
<div class="activity-page-wrapper">
    <div class="card card-ss">          <!-- ← bug -->
        <div class="activity-content">
            ...
        </div>
        <div class="activity-footer">...</div>
    </div>
</div>
```

A card wrapper breaks the flex-column chain on `.activity-page-wrapper` → `.activity-content` (`flex: 1`) → `.activity-footer` (`flex-shrink: 0`). The footer un-pins from the bottom edge because the card now constrains its sizing.

**Fix:** no card wrapper. Use `.card.card-ss` ONLY for setup screens / inline cards INSIDE `.activity-content`, never around the whole shell.

### ❌ #3 — Footer not pinned / safe-area mishandled

Observed: Publish #7 (NumberDrill).

Causes:
- Footer placed inside `.activity-content` instead of as a sibling.
- Footer using a different class (e.g., a hand-rolled bottom bar) that doesn't include `flex-shrink: 0` or safe-area padding.
- Footer wrapped in another element that forces it back into scroll flow.

**Fix:** footer is a sibling of `.activity-content` inside `.activity-page-wrapper`. Use the canonical `.activity-footer` class. Don't reinvent.

### ❌ #4 — Inventing a new wrapper class to "match VocabQuiz"

If you find yourself naming a new class like `.numberdrill-shell` or `.my-activity-wrapper`, stop. The contract is shared CSS. Diverging means future activity pages won't share the same chrome behavior across iOS / Android / desktop.

## Examples

### Reference (canonical)

[`src/SentenceStudio.UI/Pages/VocabQuiz.razor`](../../../src/SentenceStudio.UI/Pages/VocabQuiz.razor)

### Recently corrected to match canonical

[`src/SentenceStudio.UI/Pages/NumberDrill.razor`](../../../src/SentenceStudio.UI/Pages/NumberDrill.razor) — after Publish #9 fix, lines 26–29 and 465 mirror the VocabQuiz shell.

### How to verify parity

Build and run the new page on iPhone. Take a screenshot. Take a screenshot of VocabQuiz on the same device. Compare side-by-side using the **`maui-visual-review`** skill — focus on the strip immediately below the footer (chrome height + safe-area padding should be identical).

## References

- **Decision log:** `.squad/decisions.md` — Publish #7, #8, #9 entries (2026-05-07)
- **CSS contract:** `src/SentenceStudio.UI/wwwroot/css/app.css` lines 1381–1424 (with anti-pattern #1 warning comment added 2026-05-07)
- **Companion skill:** `~/.copilot/skills/maui-visual-review/SKILL.md` — for side-by-side parity checks
- **Index:** `.squad/skills/available-copilot-skills/SKILL.md`
