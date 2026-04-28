# Scenario G: Checkbox Validation — Zero Checked

**Status:** AUTHORED, NOT YET RUN  
**Priority:** P1 — v1.1 validation rule enforcement  
**Feature branch:** `feature/import-content-mvp`  
**Decision refs:** `squad-captain-checkboxes.md` — "at least one harvest checkbox must be checked, OR Transcript must be checked. All-unchecked is invalid."

## Preconditions

- Aspire stack running
- Signed in as David (Korean)

## Steps

### Step 1: Navigate and Select a Content Type

1. Navigate to `https://localhost:7071/import-content`
2. Select "Phrases" (starts with Phrases + Words checked)
3. Paste any valid input (e.g., Captain's Korean example)

### Step 2: Advance to Harvest Checkbox Step

1. Click "Next" until the harvest checkboxes are visible
2. **Expected:** Checkboxes shown:
   - [ ] Transcript — unchecked
   - [x] Phrases — checked (default for Phrases)
   - [x] Words — checked (default for Phrases)

### Step 3: Uncheck All Checkboxes

1. Uncheck "Phrases"
2. Uncheck "Words"
3. Leave "Transcript" unchecked
4. **Expected:** All three checkboxes are now unchecked

### Step 4: Try to Advance

1. Click "Next" / "Preview" / "Commit" — attempt to proceed
2. **Expected:** Inline error message displayed (e.g., "Select at least one harvest option")
3. **Expected:** Wizard does NOT advance
4. **Expected:** No spinner, no loading state — immediate inline feedback

### Step 5: Verify Cannot Bypass

1. Try clicking the advance button again
2. **Expected:** Same error persists — button is effectively blocked

### Step 6: Fix and Proceed

1. Check "Words" checkbox
2. **Expected:** Error message disappears
3. Click advance
4. **Expected:** Wizard proceeds normally

## Pass Criteria

- [ ] Inline error shown when all checkboxes unchecked
- [ ] Cannot advance until at least 1 checkbox is checked
- [ ] Error clears when a checkbox is checked
- [ ] No DB writes during the blocked state
- [ ] No errors in logs
