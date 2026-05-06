# Skill: Number Content Seeding with Irregular Metadata

**Domain:** Language learning content authoring (number systems)  
**Applies to:** Korean, Japanese, Mandarin, Spanish number drill seed files  
**Created:** 2026-05-04 (River, Phase 2 NumberDrill expansion)

## When to Use This Skill

Use this pattern when extending `lib/content/numbers/{languageCode}.json` with new number contexts that have:
- **Irregular forms** (Korean 유월/시월, Japanese rendaku, Spanish apocope)
- **Dual patterns** (Korean ordinal 째/번째, Spanish ordinal -o/-a + placement)
- **Cultural place-value groupings** (Korean 만/억 vs. Western thousand/million)
- **Context-specific metadata** that doesn't fit the generic `counters` array schema

## Schema Pattern

```json
{
  "languageCode": "ko",
  "version": 1,
  "contexts": [
    {
      "code": "ContextName",
      "displayName": "Display Name",
      "icon": "🎯",
      "defaultSystem": "Native|Sino|Mixed|Lexical",
      "sortOrder": 10,
      "isActive": true
    }
  ],
  "subModes": [ /* unchanged */ ],
  "counters": [ /* unchanged */ ],
  "contextNotes": {
    "ContextName": {
      "system": "Native|Sino|Mixed",
      "irregularForms": { /* key: value mappings */ },
      "patterns": { /* multi-pattern documentation */ },
      "sampleData": [ /* examples with context tags */ ],
      "notes": "Guidance for generator implementer"
    }
  }
}
```

## Key Principles

### 1. `contextNotes` Is Optional and Ignored by Seeder

- **Seeder (`NumberContentSeeder.cs`) only deserializes `contexts`, `subModes`, `counters`** via typed DTOs
- `contextNotes` is NOT in the DTO schema — seeder ignores it (no breaking change)
- Generator reads raw JSON for context-specific logic
- This enables **additive schema extensions** without DTO migrations

### 2. Irregular Forms Must Be Explicitly Flagged

Don't rely on generator to "know" irregularities. Document them in seed metadata:

**Korean Date Irregularities:**
```json
"Date": {
  "irregularMonths": {
    "6": "유월",
    "10": "시월"
  },
  "months": [
    { "number": 6, "korean": "유월", "irregular": true },
    { "number": 10, "korean": "시월", "irregular": true }
  ]
}
```

**Why explicit?**
- Generator uses correct forms in item generation
- Grader detects learner errors ("육월" when "유월" expected) as dedicated error class
- Pedagogical tips reference irregularity ("June uses the irregular form 유월, not 육월")

### 3. Sample Data Should Include Conversational Context

Numbers aren't abstract — tag them with real-world usage contexts:

**Korean Money Ranges:**
```json
"Money": {
  "ranges": [
    { "value": 1000, "korean": "천 원", "context": "coffee" },
    { "value": 10000, "korean": "만 원", "context": "lunch" },
    { "value": 1000000, "korean": "백만 원", "context": "rent" }
  ]
}
```

**Why contexts?**
- Generator can sample realistic values (coffee-priced vs. rent-priced items)
- Learner sees numbers in semantic frames (not just abstract drills)
- Enables context-based sub-modes (e.g., "Shopping" vs. "Bill-paying")

### 4. Dual Patterns Need Selection Criteria

If a context has multiple productive patterns, document when to use each:

**Korean Ordinals:**
```json
"Ordinal": {
  "patterns": {
    "째": {
      "description": "Native + 째 for ranking, family birth order, sequence",
      "examples": [ { "number": 1, "korean": "첫째", "context": "first child" } ]
    },
    "번째": {
      "description": "Native + 번째 for occurrences, 'Nth time/turn'",
      "examples": [ { "number": 1, "korean": "첫 번째", "context": "first time" } ]
    }
  }
}
```

**Selection strategy:** Generator biases by sub-mode or prompt context:
- Ranking contexts → 째
- Occurrence contexts → 번째

### 5. Cultural Place-Value Groupings Are Explicit

Don't assume Western thousand/million grouping:

**Korean Money:**
```json
"Money": {
  "placeValues": {
    "만": 10000,
    "억": 100000000
  },
  "notes": "Korean currency groups by 4 digits (만, 억) not 3 like Western."
}
```

**Why explicit?**
- Generator teaches Korean-native thinking patterns (not transliterated Western ones)
- Grader can detect place-value errors (e.g., "십만 원" when "백만 원" expected)
- Learner builds cultural number sense

## Implementation Workflow

### Step 1: Define Context Entry
```json
{
  "code": "Money",
  "displayName": "Money",
  "icon": "💰",
  "defaultSystem": "Sino",
  "sortOrder": 40,
  "isActive": true
}
```

### Step 2: Add contextNotes Section
```json
"contextNotes": {
  "Money": {
    "system": "Sino",
    "particle": "원",
    "placeValues": { "만": 10000, "억": 100000000 },
    "ranges": [ /* sample data */ ],
    "notes": "Generator guidance here"
  }
}
```

### Step 3: Validate JSON Syntax
```bash
cat lib/content/numbers/ko.json | python3 -m json.tool > /dev/null
```

### Step 4: Build Shared Project
```bash
dotnet build src/SentenceStudio.Shared/SentenceStudio.Shared.csproj -f net10.0
```

**Verify:**
- No new build errors (warnings are OK if pre-existing)
- No "Failed to deserialize" log warnings from seeder

### Step 5: Generator Reads contextNotes

```csharp
// In KoreanNumberItemGenerator.cs
private static readonly Dictionary<int, string> IrregularMonths = LoadIrregularMonths();

private static Dictionary<int, string> LoadIrregularMonths()
{
    var assembly = typeof(NumberContentSeeder).Assembly;
    using var stream = assembly.GetManifestResourceStream("SentenceStudio.Shared.Numbers.ko.json");
    using var reader = new StreamReader(stream);
    var json = reader.ReadToEnd();
    var doc = JsonDocument.Parse(json);
    
    var result = new Dictionary<int, string>();
    if (doc.RootElement.TryGetProperty("contextNotes", out var notes) &&
        notes.TryGetProperty("Date", out var dateNotes) &&
        dateNotes.TryGetProperty("irregularMonths", out var irregulars))
    {
        foreach (var prop in irregulars.EnumerateObject())
        {
            result[int.Parse(prop.Name)] = prop.Value.GetString();
        }
    }
    return result;
}

private NumberItem GenerateDateItem(NumberItemRequest request, Random random)
{
    var month = random.Next(1, 13);
    var monthKorean = IrregularMonths.ContainsKey(month)
        ? IrregularMonths[month]
        : ConvertMonthToKorean(month); // standard Sino
    // ...
}
```

## Generalization to Other Languages

### Japanese (Potential Phase 3+)
```json
"contextNotes": {
  "Date": {
    "irregularReadings": {
      "1": "ついたち",
      "2": "ふつか",
      "8": "ようか",
      "10": "とおか",
      "20": "はつか"
    },
    "notes": "Days 1-10, 20 use kun-yomi; others use on-yomi"
  }
}
```

### Mandarin (Potential Phase 3+)
```json
"contextNotes": {
  "Money": {
    "currencyVariants": {
      "formal": "元",
      "colloquial": "块"
    },
    "toneSandhi": {
      "一": "yī → yì before 4th tone (e.g., 一块 yíkuài)"
    }
  }
}
```

### Spanish (Potential Phase 3+)
```json
"contextNotes": {
  "Ordinal": {
    "genderAgreement": {
      "masculine": "-o",
      "feminine": "-a"
    },
    "apocope": {
      "primero": "primer (before masculine noun)",
      "tercero": "tercer (before masculine noun)"
    },
    "placement": {
      "1-10": "can precede or follow noun",
      "11+": "usually follow noun"
    }
  }
}
```

## Error Classes to Support Irregular Metadata

When irregular forms are flagged in seed, extend grader with new error classes:

1. **`IrregularFormMissed`** — learner used regular form when irregular expected
   - Example: "육월" when "유월" expected
   - Tip: "June uses the irregular form 유월, not 육월"

2. **`PlaceValueError`** — magnitude error via cultural place-value grouping
   - Example: "십만 원" when "백만 원" expected (10x but via 만/억 boundary)
   - Tip: "Korean groups by 10,000 (만). 1,000,000 = 백만 (100 × 만)"

3. **`PatternMismatch`** — wrong pattern variant in dual-pattern context
   - Example: "첫 번째" when ranking context expects "첫째"
   - Tip: "Use 첫째 (not 첫 번째) for rankings and birth order"

## Validation Checklist

- [ ] JSON syntax valid (`python3 -m json.tool`)
- [ ] Build succeeds with no new errors
- [ ] `contextNotes` section added for each new context
- [ ] Irregular forms explicitly flagged (`irregular: true`)
- [ ] Sample data includes conversational context tags
- [ ] Dual patterns documented with selection criteria
- [ ] Cultural place-values mapped explicitly
- [ ] Generator implementer guidance in `notes` field
- [ ] Error classes updated to detect irregular form misuse

## Files to Modify

1. **Seed file:** `lib/content/numbers/{languageCode}.json`
2. **Generator:** `src/SentenceStudio.AppLib/Services/Numbers/{Language}NumberItemGenerator.cs`
3. **Grader:** `src/SentenceStudio.AppLib/Services/Numbers/{Language}NumberAnswerGrader.cs`
4. **Tests:** `tests/SentenceStudio.AppLib.Tests/Services/Numbers/{Language}NumberItemGeneratorTests.cs`

## Anti-Patterns to Avoid

❌ **DON'T rely on generator to "know" irregular forms** — document them in seed metadata  
❌ **DON'T add abstract sample data** — tag with real-world usage contexts  
❌ **DON'T assume Western place-value grouping** — document cultural groupings explicitly  
❌ **DON'T skip pattern selection criteria** — learner needs to know when to use which variant  
❌ **DON'T change DTOs for context-specific metadata** — use `contextNotes` for additive extensions  

## References

- **Phase 1 Implementation:** `src/SentenceStudio.AppLib/Services/Numbers/KoreanNumberItemGenerator.cs`
- **Phase 2 Seed Extension:** `lib/content/numbers/ko.json` (Money/Date/Ordinal contexts)
- **Seeder Schema:** `src/SentenceStudio.Shared/Services/Numbers/NumberContentSeeder.cs` (DTOs lines 171-201)
- **Decision Docs:** `.squad/decisions/inbox/river-numberdrill-phase2-seed.md`
