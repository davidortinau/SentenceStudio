# Vocabulary Search Syntax Guide

This guide explains how to use the advanced search syntax in the Vocabulary Management page to find specific vocabulary words quickly.

## Quick Reference

| Filter Type | Syntax | Example |
|------------|--------|---------|
| Tag | `tag:value` | `tag:nature` |
| Resource | `resource:value` | `resource:general` |
| Lemma | `lemma:value` | `lemma:가다` |
| Status | `status:value` | `status:learning` |
| Free text | just type | `단풍` |

---

## Basic Text Search

Simply type any text to search across all vocabulary fields:

```
단풍
```

This searches:
- Target language term (Korean word)
- Native language term (English translation)
- Lemma (dictionary form)

Results update automatically as you type (with a brief delay to avoid excessive searches).

---

## Tag Filtering

Filter vocabulary by tags you've assigned:

```
tag:nature
```

### Multiple Tags (AND logic)

Combine multiple tag filters to find words with ALL specified tags:

```
tag:nature tag:season
```

This shows words that have BOTH "nature" AND "season" tags.

### Tag Autocomplete

Type `tag:` and wait briefly to see available tags, or continue typing to filter suggestions:

```
tag:nat
```

Shows tags starting with "nat" (e.g., "nature")

---

## Resource Filtering

Filter vocabulary by learning resource:

```
resource:general
```

### Multiple Resources (OR logic)

Unlike tags, multiple resource filters use OR logic:

```
resource:general resource:textbook
```

This shows words from EITHER "general" OR "textbook" resources.

### Resource Autocomplete

Type `resource:` to see all available learning resources in your library.

---

## Lemma Filtering

Search by dictionary form (lemma) to find all conjugated variations:

```
lemma:가다
```

This finds all words derived from 가다 (to go), including 가요, 갔어요, 가겠습니다, etc.

### Lemma Autocomplete

Type `lemma:` and start typing the dictionary form to see matching lemmas.

---

## Status Filtering

Filter by learning status:

| Status | Description |
|--------|-------------|
| `status:known` | Words you've mastered (✓) |
| `status:learning` | Words you're currently learning (⏳) |
| `status:unknown` | Words you haven't studied yet (?) |

```
status:learning
```

### Status Autocomplete

Type `status:` to see all three status options with their icons.

---

## Combining Filters

Combine different filter types for powerful searches:

```
tag:nature status:learning resource:general 가을
```

This finds words that:
- Have the "nature" tag AND
- Are in "learning" status AND
- Are from the "general" resource AND
- Contain "가을" somewhere

**Filter Combination Rules:**
- Different filter types combine with AND logic
- Multiple tags combine with AND logic
- Multiple resources combine with OR logic
- Free text combines with AND logic to all filters

---

## Filter Chips

Active filters appear as "chips" above the search box:

- Each chip shows the filter type and value
- Tap the **X** on a chip to remove that filter
- Use **Clear all** to remove all filters at once

---

## Quoted Values

For tags or values containing spaces, use quotes:

```
tag:"multi word tag"
```

---

## Tips and Best Practices

### Finding Specific Words Quickly

1. Start with a broad filter (e.g., `status:learning`)
2. Add more specific filters to narrow results
3. Use free text for final refinement

### Reviewing by Topic

Use tags to create study sessions by topic:

```
tag:food status:learning
```

Review all food-related words you're currently learning.

### Finding Untagged Words

Words without tags appear when no tag filter is active. Consider tagging words after study sessions for better organization.

### Keyboard Shortcuts

- **Tab/Enter**: Select highlighted autocomplete suggestion
- **Escape**: Close autocomplete popup
- **Clear**: Remove all filters

---

## Troubleshooting

### Autocomplete Not Appearing

- Make sure you typed the filter prefix correctly (e.g., `tag:` not `tag `)
- Wait briefly after typing the colon for suggestions to load
- Check that you have vocabulary words with the filter type you're searching

### No Results Found

- Remove filters one at a time to identify which is too restrictive
- Check spelling in free text search
- Verify that words exist with the combination of filters applied

### Slow Search Performance

- The search uses database indexes for fast queries
- If searching large vocabulary sets, wait for the debounce delay
- Complex filter combinations may take slightly longer

---

## Examples

### Study Session Setup

Find learning words from a specific resource:
```
resource:"TTMIK Level 1" status:learning
```

### Review by Topic

Find all nature words you know:
```
tag:nature status:known
```

### Grammar Review

Find all conjugations of a verb:
```
lemma:먹다
```

### Multi-topic Search

Find weather or season words you're learning:
```
tag:weather tag:season status:learning
```
