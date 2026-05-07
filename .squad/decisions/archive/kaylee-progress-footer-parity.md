
## Update 2026-05-18: Layout Parity Fix (Commit 577852ff)

**Problem:** Footer was not sticking to the bottom edge of the page on NumberDrill despite having the correct footer markup. VocabQuiz footer correctly hugs the bottom on all surfaces.

**Root Cause:** PageHeader component (`.page-header`) was missing `flex-shrink: 0` in CSS. Without this, the PageHeader could shrink in the flex container (`activity-page-wrapper`), throwing off the flex layout calculation and preventing the footer from reaching the bottom edge.

**Fix:** Added `flex-shrink: 0` to `.page-header` CSS (line 1390 in `app.css`). This ensures PageHeader maintains its height in the flex container, allowing `activity-content` (with `flex: 1`) to grow and push the `activity-footer` (with `flex-shrink: 0`) to the bottom.

**Complete Layout Pattern for Activity Pages:**

```css
.activity-page-wrapper {
    display: flex;
    flex-direction: column;
    height: calc(100% + 2rem);
    /* ... */
}

.page-header {
    position: sticky;
    flex-shrink: 0;  /* ← CRITICAL: prevents shrinking in flex container */
    /* ... */
}

.activity-content {
    flex: 1;  /* ← CRITICAL: grows to fill available space */
    overflow-y: auto;
    min-height: 0;
    /* ... */
}

.activity-footer {
    flex-shrink: 0;  /* ← CRITICAL: pins to bottom, doesn't shrink */
    padding-bottom: calc(0.75rem + env(safe-area-inset-bottom, 0px));  /* ← safe-area for iOS */
    /* ... */
}
```

**Markup Pattern:**
```html
<div class="activity-page-wrapper">
    <PageHeader ... />
    
    <div class="activity-content">
        @* main content here *@
    </div> @* end activity-content *@
    
    @if (state == State.InSession && currentItem != null)
    {
        <div class="activity-footer d-flex justify-content-between align-items-center">
            <span class="ss-body1 text-secondary-ss">X / Y</span>
            <span class="badge bg-success rounded-pill px-3"><i class="bi bi-check-lg me-1"></i>N correct</span>
        </div>
    }
    
    @if (state == State.InSession && !showFeedback)
    {
        <div class="activity-input-bar">
            @* optional input bar *@
        </div>
    }
</div> @* end activity-page-wrapper *@
```

**Key Insight:** Activity progress footers require the FULL Quiz page layout pattern (outer flex column + PageHeader flex-shrink + flex-grow content + flex-shrink footer + safe-area), not just the inner footer markup. All three flex-shrink/flex-grow declarations are critical for the footer to pin to the bottom edge correctly.

**Files Changed:**
- `app.css`: Added `flex-shrink: 0` to `.page-header`
- `NumberDrill.razor`: Added closing comments for clarity (no functional change)

## Update 2026-05-18: Card Wrapper Removal (Commit d09c233c)

**Problem:** NumberDrill had a `<div class="card card-ss p-4">` wrapper around the session content, creating visual elevation/boxing that VocabQuiz doesn't have. VocabQuiz uses flat full-bleed layout for active session content.

**Root Cause:** NumberDrill was designed with card chrome around the active drill item, while VocabQuiz shows prompts/choices directly on the page background without card wrapping. This created visual inconsistency between activity pages.

**Fix:** Removed `<div class="card card-ss p-4">` wrapper from lines 116-417 in NumberDrill.razor. Session content now renders flat against page background, matching VocabQuiz. Cards are still used appropriately for:
- Setup screen (configuration UI deserves visual grouping)
- Summary screen (results card, same as VocabQuiz)

**Complete Visual Parity Pattern:**

Activity pages use flat layout for ACTIVE SESSION CONTENT:
- ❌ NO card wrapper around quiz items / drill prompts / activity UI
- ❌ NO elevation / shadow / border chrome during active session
- ✅ YES cards for setup screens (configuration, filters, preferences)
- ✅ YES cards for summary screens (results, statistics, completion)

**Why This Matters:**
Card wrappers create visual weight and padding that:
1. Reduces available screen space for large prompts (3rem display text needs room)
2. Competes with activity-footer for bottom-edge positioning
3. Adds visual hierarchy where focus should be on the content itself

Flat layout keeps attention on the prompt/question/drill, not the container.

**Files Changed:**
- `NumberDrill.razor`: Removed card wrapper div from session content (lines 116, 417)

**Result:** NumberDrill now has identical outer page structure to VocabQuiz - flat flex column, no card chrome, footer pins to bottom edge, safe-area handling.
