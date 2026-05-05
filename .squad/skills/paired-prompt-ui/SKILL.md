# Paired-Prompt UI Pattern

**Status:** Draft (extract after NumberDrill Phase 2 Wave 2 ships)  
**Context:** Blazor activity pages that require comparing two items side-by-side  
**Origin:** NumberDrill Disambiguate-the-system sub-mode (2026-05-04)  

---

## When to Use This Pattern

Use the paired-prompt UI pattern when:

1. **Comparative learning:** The activity requires the learner to compare two items and make a choice for each (e.g., "which number system for each context?")
2. **Contrast pedagogy:** The pedagogical value is in the **contrast** between the two items, not in answering each independently
3. **Mobile-first design:** The activity must work well on narrow viewports (≤640px) without horizontal scrolling

**Do NOT use** when:

- Items are independent (no pedagogical contrast) — use separate cards instead
- More than 2 items need to be compared — this pattern is optimized for pairs
- Horizontal side-by-side layout is required by design (this pattern is vertical-first)

---

## Pattern Components

### 1. Layout: Vertical Stacking (Mobile-First)

```razor
<div class="activity-content">
    <div class="paired-prompt-container">
        <!-- Prompt Card A -->
        <div class="paired-prompt-card @(showFeedback && isCorrectA ? "correct" : "") 
                                       @(showFeedback && !isCorrectA ? "incorrect" : "")">
            <div class="prompt-text">@promptA</div>
            <div class="choice-strip">
                @foreach (var option in optionsA)
                {
                    <button class="btn @(selectedA == option ? "btn-ss-primary" : "btn-outline-secondary")"
                            @onclick="() => selectedA = option"
                            disabled="@showFeedback">
                        @option
                    </button>
                }
            </div>
        </div>

        <!-- Prompt Card B -->
        <div class="paired-prompt-card @(showFeedback && isCorrectB ? "correct" : "") 
                                       @(showFeedback && !isCorrectB ? "incorrect" : "")">
            <div class="prompt-text">@promptB</div>
            <div class="choice-strip">
                @foreach (var option in optionsB)
                {
                    <button class="btn @(selectedB == option ? "btn-ss-primary" : "btn-outline-secondary")"
                            @onclick="() => selectedB = option"
                            disabled="@showFeedback">
                        @option
                    </button>
                }
            </div>
        </div>

        <!-- Submit button -->
        @if (!showFeedback)
        {
            <button class="btn btn-ss-primary w-100 mt-3"
                    @onclick="SubmitBoth"
                    disabled="@(selectedA == null || selectedB == null)">
                Submit Both
            </button>
        }
    </div>

    <!-- Explanation Panel (slide-up after submit) -->
    @if (showFeedback)
    {
        <div class="paired-explanation-panel">
            <div class="paired-explanation-grid">
                <!-- Explanation A -->
                <div class="explanation-item">
                    <i class="bi bi-info-circle"></i>
                    <strong>Prompt A:</strong> @explanationA
                </div>
                <!-- Explanation B -->
                <div class="explanation-item">
                    <i class="bi bi-info-circle"></i>
                    <strong>Prompt B:</strong> @explanationB
                </div>
            </div>
            <button class="btn btn-ss-primary w-100 mt-3" @onclick="NextPair">Next</button>
        </div>
    }
</div>
```

### 2. CSS (Responsive Grid for Explanations)

```css
/* Prompt cards */
.paired-prompt-card {
    border: 2px solid var(--bs-border-color);
    border-radius: 0.5rem;
    padding: 1rem;
    margin-bottom: 1rem;
}

.paired-prompt-card.correct {
    border-color: var(--bs-success);
    background-color: rgba(var(--bs-success-rgb), 0.1);
}

.paired-prompt-card.incorrect {
    border-color: var(--bs-danger);
}

/* Choice strip */
.choice-strip {
    display: flex;
    gap: 0.5rem;
    margin-top: 0.75rem;
}

.choice-strip .btn {
    flex: 1;  /* equal width buttons */
}

/* Explanation panel */
.paired-explanation-panel {
    margin-top: 1rem;
    padding: 1rem;
    border-top: 1px solid var(--bs-border-color);
    background-color: var(--bs-secondary-bg);
}

/* Desktop: 2-column grid */
@media (min-width: 640px) {
    .paired-explanation-grid {
        display: grid;
        grid-template-columns: 1fr 1fr;
        gap: 1.5rem;
    }
}

/* Mobile: vertically stacked (default) */
.paired-explanation-grid {
    display: flex;
    flex-direction: column;
    gap: 1rem;
}
```

### 3. State Model

```csharp
// Item data
private PairedPromptItem? currentItem;  // { PromptA, PromptB, OptionsA, OptionsB, CorrectA, CorrectB, ExplanationA, ExplanationB }

// User selections
private string? selectedA;
private string? selectedB;

// UI state
private bool showFeedback;

// Grading result
private bool isCorrectA;
private bool isCorrectB;
```

### 4. Grading Logic

**Strict mode (both-or-nothing):** Count as correct only if both prompts answered correctly.

```csharp
private async Task SubmitBoth()
{
    isCorrectA = selectedA == currentItem.CorrectA;
    isCorrectB = selectedB == currentItem.CorrectB;
    
    var bothCorrect = isCorrectA && isCorrectB;
    
    // Record attempt with mastery service
    await RecordAttempt(bothCorrect);
    
    showFeedback = true;
}
```

**Partial credit mode (per-prompt):** Track each prompt's correctness separately.

```csharp
private async Task SubmitBoth()
{
    isCorrectA = selectedA == currentItem.CorrectA;
    isCorrectB = selectedB == currentItem.CorrectB;
    
    // Record two separate attempts
    await RecordAttempt(promptId: "A", isCorrectA);
    await RecordAttempt(promptId: "B", isCorrectB);
    
    showFeedback = true;
}
```

---

## Key Principles

1. **Vertical stacking > Horizontal side-by-side:** Mobile readability + desktop parity; no responsive reflow needed.
2. **Per-prompt choice UI:** Each prompt gets its own choice strip (not shared).
3. **Paired grading:** Submit both together, grade together (prevents spoiling the answer).
4. **Responsive explanation:** 2-column grid on desktop, 1-column on mobile.
5. **Feedback hierarchy:** Prompt-level correctness (border color) + explanation panel (why).

---

## Accessibility

- **NO color-only differentiation:** Correct/incorrect states include icons (`bi-check-circle-fill` / `bi-x-circle-fill`) + border colors
- **WCAG AA contrast:** All text on colored backgrounds must meet 4.5:1 ratio
- **Keyboard navigation:** Choice buttons must be focusable and activatable via Enter/Space

---

## Variants

### Variant A: Audio Per Prompt

Add audio play buttons to each prompt card:

```razor
<div class="paired-prompt-card">
    <div class="d-flex justify-content-between align-items-start">
        <div class="prompt-text">@promptA</div>
        <button class="btn btn-sm btn-outline-secondary" @onclick="PlayAudioA">
            <i class="bi bi-volume-up"></i>
        </button>
    </div>
    <!-- choice strip below -->
</div>
```

### Variant B: More Than 2 Choices

If the choice strip has 3-4 options, allow wrapping:

```css
.choice-strip {
    display: flex;
    flex-wrap: wrap;  /* wrap to multiple rows on mobile */
    gap: 0.5rem;
}

.choice-strip .btn {
    flex: 1 1 calc(50% - 0.25rem);  /* 2 columns on mobile, auto-expand on desktop */
    min-width: 100px;
}
```

---

## Known Uses

1. **NumberDrill Disambiguate-the-system** (2026-05-04) — Compare two prompts with same numeral, different contexts (Sino vs Native system choice)

---

## Related Patterns

- **activity-page-wrapper shell** — Standard layout for all activity pages
- **card-ss styling** — Theme-aware card borders + padding
- **Feedback panel slide-up** — Used in Phase 1 single-prompt sub-modes

---

**End of Pattern**
