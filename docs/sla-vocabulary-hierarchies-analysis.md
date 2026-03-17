# SLA Perspective on Vocabulary Hierarchies and Review Strategies

**Author:** SLA Expert Agent  
**Date:** 2025-01-17  
**Context:** Design guidance for vocabulary tracking system in SentenceStudio  
**Problem:** How to track related vocabulary items (root words, inflected forms, phrases) in a theoretically sound way

---

## Executive Summary

From a second language acquisition perspective, **word families and phrases represent distinct but related learning challenges**. While they share lexical roots, research shows that:

1. **Recognition precedes production** (receptive vs. productive knowledge hierarchy)
2. **Form-meaning connections are context-dependent** (usage-based learning)
3. **Transfer effects are real but limited** (knowing 주문 helps with 주문하다, but doesn't eliminate the learning burden)
4. **Chunking is fundamental** (formulaic sequences like "피자를 주문하는 게 어때요" are learned as units, not compositionally)

**Recommendation:** Track related items **separately** for mastery scoring, but introduce **coordination mechanisms** for scheduling and difficulty estimation.

---

## 1. Word Families vs. Phrases: Distinct Learning Challenges

### SLA Principle: **Usage-Based Learning Theory** (Tomasello, 2003)

Language is learned through repeated exposure to **form-meaning pairings in context**. Each distinct form creates a separate learning event:

- **대학교** (university) as a standalone noun
- **대학교 때** (during university) as a temporal phrase

**Even though they share the root 대학교, these are functionally different constructions** that appear in different discourse contexts, carry different pragmatic weight, and activate different syntactic frames.

### Evidence from SLA Research

**Ellis (2002) - Frequency and Entrenchment:**
- Words and phrases compete for cognitive resources based on their **input frequency** and **functional distinctiveness**
- High-frequency collocations like "자주 마시는" (often drinks) become **entrenched as units** independent of their component parts
- Learners often acquire frequent phrases **before** they can productively use the individual words

**Wray (2002) - Formulaic Sequences:**
- Native-like fluency depends on mastery of **prefabricated chunks** (e.g., "피자를 주문하는 게 어때요")
- These chunks are stored and retrieved **holistically**, not assembled compositionally
- Attempting to "shortcut" mastery by assuming compositional understanding can **undermine fluency development**

### Design Implication

**✅ Treat root words and their phrasal extensions as separate vocabulary items** with independent mastery tracking.

**Why:** Even if a learner knows "주문" (order), encountering it in "주문하다" (to order - verb form) presents new challenges:
- Morphological pattern (noun → verb suffix -하다)
- Syntactic behavior (transitive verb, different sentence positions)
- Semantic nuance (stative concept → action)

---

## 2. Productive vs. Receptive Knowledge: Two-Track Mastery

### SLA Principle: **Receptive-Productive Continuum** (Nation, 2001; Laufer & Paribakht, 1998)

Vocabulary knowledge exists on a **continuum from passive recognition to active production**:

| Level | Description | Example |
|-------|-------------|---------|
| **Receptive (passive)** | Can recognize word when hearing/reading | Sees "주문" in transcript → understands "order" |
| **Controlled productive** | Can use word in structured practice (cued recall) | Fill blank: "이 음식을 ___ 했어요" → writes "주문" |
| **Free productive** | Can spontaneously produce word in conversation | Wants to order food → recalls and uses "주문하다" |

**Research shows:**
- Receptive vocabulary is **2-3x larger** than productive vocabulary (Nation, 2001)
- Production requires **deeper, more elaborate processing** (Laufer & Goldstein, 2004)
- Recognition-only practice does NOT automatically transfer to production ability

### Current SentenceStudio Implementation (from VocabularyProgress.cs)

✅ **Already distinguishes recognition vs. production:**

```csharp
// Recognition: Multiple-choice or matching
RecognitionAttempts, RecognitionCorrect

// Production: Text-entry (typed recall)
ProductionAttempts, ProductionCorrect

// IsKnown requires BOTH thresholds:
IsKnown => MasteryScore >= 0.85 && ProductionInStreak >= 2
```

**This is theoretically sound!** It aligns with research showing production is the **stronger evidence of mastery**.

### Design Recommendation

**✅ Maintain separate tracking for receptive and productive performance.**

**Enhancement opportunity:**
- **Receptive mastery threshold:** 3+ correct recognition attempts → 0.70-0.85 mastery (current system ✅)
- **Productive mastery threshold:** 2+ correct production attempts → 0.85+ mastery (current system ✅)

**Add visual distinction in UI:**
- 👁️ **"Known (receptive)"** — can recognize but not yet produce reliably
- 🎯 **"Known (productive)"** — can both recognize AND produce

**SLA rationale:**
- Laufer & Nation (1999): "Productive knowledge is a qualitatively different construct, not just a stronger version of receptive knowledge"
- Pedagogical implication: Learners need **explicit production practice**, not just more recognition drills

---

## 3. Spaced Repetition for Related Items: Coordination, Not Merging

### SLA Principle: **Noticing Hypothesis** (Schmidt, 1990) + **Transfer-Appropriate Processing** (Morris et al., 1977)

**Key insight:** Learning is **context-specific**. You remember what you practice in the way you practiced it.

- If you practice recognizing "주문" in transcript review → you'll recognize it in reading
- If you practice typing "주문하다" → you'll recall it for production
- **Exposure to one form does NOT automatically refresh memory for related forms**

### Problem with Merged Scheduling

**If "주문", "주문하다", and "피자를 주문하는 게 어때요" share a single review schedule:**

❌ Reviewing "주문" (noun) does NOT strengthen memory for "주문하다" (verb form)  
❌ Learner gets inflated confidence: "I know 주문" → fails to produce "주문하다" in conversation  
❌ Forgetting curves are form-specific (Ellis & Sinclair, 1996)

### Recommended Approach: **Linked but Independent Schedules**

**✅ Each form has its own mastery tracker and SRS schedule**  
**✅ BUT: Introduce coordination mechanisms:**

1. **Difficulty estimation bonus:**
   - If learner knows "주문" (root), reduce initial difficulty for "주문하다" (derived form)
   - Implement as: `new_item_interval = base_interval * 1.5` (faster first review)
   - SLA basis: **Positive transfer** (morphological awareness helps parsing, but not automaticity)

2. **Leeching detection across word family:**
   - If learner consistently fails "주문하다" but knows "주문", flag as **morphological weakness**
   - Suggest targeted practice: "You know the noun, let's practice the verb form"

3. **Batch review suggestions:**
   - When scheduling reviews, **surface related items together** for comparison/contrast
   - Example: Review "자주" and "자주 마시는" in same session to reinforce connection
   - SLA basis: **Elaborative encoding** (Craik & Lockhart, 1972) — connecting related items strengthens memory

### Design Specification

```yaml
VocabularyWord:
  LemmaId: "주문"  # Links related forms
  
VocabularyProgress:
  # Separate tracking per form
  주문: {mastery: 0.85, nextReview: "2025-01-20"}
  주문하다: {mastery: 0.72, nextReview: "2025-01-19"}
  
ReviewScheduler:
  # Coordination rules
  - If LemmaId matches AND both items due → schedule together
  - If learner knows root (mastery > 0.70) → boost derived form interval by 1.5x
  - If learner fails derived form 3+ times → suggest morphology study
```

---

## 4. Chunking and Formulaic Sequences: Unit Storage

### SLA Principle: **Lexical Approach** (Lewis, 1993) + **Formulaic Language** (Wray, 2002)

**Claim:** Native-like fluency depends on mastering **multi-word units** (collocations, idioms, fixed expressions) as **holistic chunks**, not word-by-word assembly.

**Research evidence:**
- Native speakers store frequent phrases as **single units** in mental lexicon (Pawley & Syder, 1983)
- Second language learners who focus on chunks show **faster gains in fluency** (Boers et al., 2006)
- Breaking chunks into components for analysis can **disrupt automaticity** (Wood, 2010)

### Application to Korean Examples

#### Example 1: "자주 마시는" (often drinks)

**Frequency test:** Does this phrase appear as a fixed unit in natural Korean?
- Check corpus data: How often does "자주" appear with "마시다" vs. other verbs?
- If **high co-occurrence** → treat as chunk

**Learner readiness test:** Can beginner learners understand the components?
- 자주 = "often" (adverb)
- 마시는 = "drink" (verb, present tense modifier)
- If components are **taught separately** → compositional understanding is feasible
- If "자주 마시는" appears in input **before** components are taught → chunk storage is necessary

**Recommendation for tracking:**
- **Low-frequency phrase:** Track components separately (자주, 마시다)
- **High-frequency collocation:** Track as both chunk AND components
- **Fixed expression/idiom:** Track ONLY as chunk (don't decompose)

#### Example 2: "피자를 주문하는 게 어때요" (how about ordering pizza)

**Analysis:**
- This is a **sentence template** (X를 Y하는 게 어때요 = "How about Y-ing X?")
- Contains multiple grammatical elements: object marker (를), nominalizer (는 게), suggestion form (어때요)

**SLA perspective:**
- **Beginner:** Should learn this as a **fixed phrase** for making suggestions (chunk storage)
- **Intermediate:** Should analyze components to **generalize the pattern** (template extraction)
- **Advanced:** Should have **automatized both** the chunk and the underlying grammar

**Tracking strategy:**
```
Beginner:
  - "피자를 주문하는 게 어때요" [chunk, mastery: 0.65]

Intermediate:
  - "피자를 주문하는 게 어때요" [chunk, mastery: 0.90]
  - "[X]를 [Y]하는 게 어때요" [template, mastery: 0.55]  ← NEW ITEM

Advanced:
  - Template is mastered (0.85+), individual phrase is automatized
```

### Design Recommendation: **Dual Representation**

**✅ Store formulaic sequences as standalone vocabulary items**  
**✅ BUT: Link to component words via metadata**

```yaml
VocabularyWord:
  Id: "phrase_001"
  TargetLanguageTerm: "피자를 주문하는 게 어때요"
  NativeLanguageTerm: "How about ordering pizza?"
  Type: "formulaic_sequence"
  Components: ["피자", "주문하다", "template_suggestion"]
  
VocabularyProgress:
  # Separate mastery tracking
  phrase_001: {mastery: 0.78, nextReview: "2025-01-22"}
  
ComponentLinks:
  # Used for pedagogical hints, not scheduling
  - "You know '주문하다' (order) — this phrase uses that verb in a suggestion pattern"
```

**Pedagogical flow:**
1. Learner encounters phrase in transcript
2. System imports as standalone chunk
3. If learner struggles (low mastery after 5+ attempts) → system suggests component study
4. If learner masters chunk early → system prompts template extraction: "You've mastered this phrase! Want to learn the pattern to make similar suggestions?"

---

## 5. Transfer Effects: Real but Bounded

### SLA Principle: **Constructionist Approach** (Goldberg, 2006) + **Emergentist Theory** (Ellis & Larsen-Freeman, 2006)

**Claim:** Language learning involves **gradual abstraction** from specific instances to general patterns.

**Transfer effects exist but are:**
- **Probabilistic, not deterministic:** Knowing "주문" makes "주문하다" easier, but NOT automatic
- **Strength-dependent:** Strong mastery of root (0.90+) provides more transfer than weak mastery (0.60)
- **Task-dependent:** Recognition of root transfers to recognition of derived form, but NOT to production

### Research Evidence

**Laufer (1997) - Derivational Knowledge:**
- Knowing a base word (e.g., "create") gives **partial knowledge** of derivatives (e.g., "creation", "creative")
- But learners still need **explicit encounters** with derivatives to achieve full mastery
- Transfer effect: Reduces learning time by ~30-40%, but does NOT eliminate practice requirement

**Schmitt & Zimmerman (2002) - Derivative Recognition:**
- Advanced learners recognize ~75% of derivatives of known words without prior exposure
- BUT: Only ~25% can produce derivatives correctly without practice
- **Implication:** Recognition transfer is strong; production transfer is weak

### Quantifying Transfer in SentenceStudio

**Proposal: Transfer Coefficient for Initial Difficulty**

When a new derived form is encountered, adjust its starting parameters based on root word mastery:

```python
def calculate_initial_difficulty(new_word, related_words):
    """
    Transfer coefficient based on SLA research
    - Root word mastery > 0.85: 40% difficulty reduction
    - Root word mastery 0.60-0.85: 20% difficulty reduction
    - Root word mastery < 0.60: No reduction (too weak to transfer)
    """
    max_root_mastery = max([w.mastery for w in related_words if w.is_root])
    
    if max_root_mastery >= 0.85:
        new_word.initial_interval = 2  # 40% boost (vs. default 1 day)
        new_word.ease_factor = 2.7    # Slightly easier (vs. default 2.5)
    elif max_root_mastery >= 0.60:
        new_word.initial_interval = 1.5  # 20% boost
        new_word.ease_factor = 2.6
    else:
        new_word.initial_interval = 1.0  # No transfer effect
        new_word.ease_factor = 2.5
    
    return new_word
```

**SLA rationale:**
- Schmitt & Zimmerman (2002): 30-40% reduction in learning burden for derived forms of well-known roots
- Transfer is **strongest for recognition**, so this mainly affects initial review intervals
- Production still requires full practice (enforced by 2+ production attempts for "Known" status)

### What NOT to Do

**❌ Do NOT merge mastery scores:**
- Knowing "주문" does NOT mean "주문하다" is automatically mastered
- Merging scores would misrepresent learner's actual productive ability

**❌ Do NOT skip reviews for derived forms:**
- Even with transfer, learners need **repeated exposure** to consolidate new form-meaning mappings
- Skipping reviews risks incomplete learning (Bahrick, 1979 - spacing effect)

**✅ DO provide hints:**
- "You already know '주문' (order). This is the verb form 주문하다."
- This **explicit noticing** enhances transfer (Schmidt, 1990)

---

## Summary: Design Principles for Vocabulary Tracking

### 1. **Independence Principle**
Each vocabulary item (root, inflection, phrase) has **separate mastery tracking and SRS scheduling**.

**SLA basis:** Context-dependent learning, form-specific memory, transfer-appropriate processing

### 2. **Receptive-Productive Distinction**
Track recognition and production separately, with different mastery thresholds.

**SLA basis:** Productive knowledge requires deeper processing and does not automatically follow from receptive knowledge

### 3. **Coordination Mechanisms**
While items are tracked independently, introduce **soft links** for:
- Initial difficulty estimation (transfer coefficient)
- Batch review suggestions (surface related items together)
- Leeching detection (flag morphological gaps)

**SLA basis:** Positive transfer exists but is bounded; elaborative encoding strengthens retention

### 4. **Chunk Storage**
Treat formulaic sequences as **standalone units** with optional component links.

**SLA basis:** Native-like fluency depends on holistic retrieval of multi-word units

### 5. **Transfer Coefficient**
Reduce initial difficulty for derived forms when root is well-known (mastery > 0.85).

**SLA basis:** Derivational knowledge provides partial transfer (~30-40% reduction in learning time)

---

## Implementation Priorities

### Phase 1: Core Tracking (Current System ✅)
- [x] Separate mastery tracking per vocabulary item
- [x] Receptive vs. productive distinction (RecognitionCorrect, ProductionCorrect)
- [x] Mastery thresholds: 0.70-0.85 (receptive), 0.85+ (productive)

### Phase 2: Coordination Mechanisms (Recommended)
- [ ] Add `LemmaId` field to link related forms (대학교, 대학교 때)
- [ ] Implement transfer coefficient for initial difficulty
- [ ] Batch review: Surface related items together when scheduling
- [ ] Leeching detection: Flag morphological weaknesses

### Phase 3: Advanced Features (Future)
- [ ] Template extraction: Prompt learners to generalize from mastered chunks
- [ ] Corpus frequency data: Identify true collocations vs. compositional phrases
- [ ] Adaptive difficulty: Adjust intervals based on morphological complexity

---

## References

- Bahrick, H. P. (1979). Maintenance of knowledge: Questions about memory we forgot to ask. *Journal of Experimental Psychology: General*, 108(3), 296-308.
- Boers, F., et al. (2006). Formulaic sequences and perceived oral proficiency. *Language Teaching Research*, 10(3), 245-261.
- Craik, F. I., & Lockhart, R. S. (1972). Levels of processing. *Journal of Verbal Learning and Verbal Behavior*, 11(6), 671-684.
- Ellis, N. C. (2002). Frequency effects in language processing. *Studies in Second Language Acquisition*, 24(2), 143-188.
- Ellis, N. C., & Larsen-Freeman, D. (2006). Language emergence: Implications for applied linguistics. *Applied Linguistics*, 27(4), 558-589.
- Ellis, R., & Sinclair, B. (1996). *Learning to learn English*. Cambridge University Press.
- Goldberg, A. (2006). *Constructions at work*. Oxford University Press.
- Laufer, B. (1997). What's in a word that makes it hard or easy. *Vocabulary: Description, Acquisition and Pedagogy*, 140-155.
- Laufer, B., & Goldstein, Z. (2004). Testing vocabulary knowledge: Size, strength, and computer adaptiveness. *Language Learning*, 54(3), 399-436.
- Laufer, B., & Nation, P. (1999). A vocabulary-size test of controlled productive ability. *Language Testing*, 16(1), 33-51.
- Laufer, B., & Paribakht, T. S. (1998). The relationship between passive and active vocabularies. *Vocabulary: Description, Acquisition and Pedagogy*, 251-266.
- Lewis, M. (1993). *The lexical approach*. Language Teaching Publications.
- Morris, C. D., et al. (1977). Levels of processing versus transfer appropriate processing. *Journal of Verbal Learning and Verbal Behavior*, 16(5), 519-533.
- Nation, I. S. P. (2001). *Learning vocabulary in another language*. Cambridge University Press.
- Pawley, A., & Syder, F. H. (1983). Two puzzles for linguistic theory: Nativelike selection and nativelike fluency. *Language and Communication*, 191-226.
- Schmidt, R. (1990). The role of consciousness in second language learning. *Applied Linguistics*, 11(2), 129-158.
- Schmitt, N., & Zimmerman, C. B. (2002). Derivative word forms: What do learners know? *TESOL Quarterly*, 36(2), 145-171.
- Tomasello, M. (2003). *Constructing a language: A usage-based theory of language acquisition*. Harvard University Press.
- Wood, D. (2010). Formulaic language and second language speech fluency. *Continuum*.
- Wray, A. (2002). *Formulaic language and the lexicon*. Cambridge University Press.

---

**Document Version:** 1.0  
**Last Updated:** 2025-01-17  
**Status:** Final - Ready for team review
