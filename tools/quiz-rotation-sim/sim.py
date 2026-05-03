#!/usr/bin/env python3
"""
Vocabulary Quiz rotation simulator — Wash, Stream B Step 2 (#191).

Mirrors:
  • VocabularyProgressService.RecordAttemptAsync correct-path math
    (src/SentenceStudio.Shared/Services/VocabularyProgressService.cs:119-180)
  • VocabularyQuizItem.ReadyToRotateOut tiered rule
    (src/SentenceStudio.Shared/Models/VocabularyQuizItem.cs:33-55)
  • VocabQuiz.razor mode selection (streak>=3 OR mastery>=0.50 → Text)

Two rules implemented:
  • CURRENT — production code today
  • PROPOSED — Wash's proposal for #191

Run: python3 sim.py
"""
from dataclasses import dataclass, field

# --- Constants from VocabularyProgressService.cs ---
CURRENT_DIVISOR = 7.0
PROPOSED_DIVISOR = 12.0
RECOVERY_BOOST = 0.02
MC_WEIGHT = 1.0
TEXT_WEIGHT = 1.5

@dataclass
class Item:
    mastery: float = 0.0
    streak: float = 0.0
    prod_in_streak: int = 0
    total: int = 0
    correct: int = 0
    sess_correct: int = 0
    sess_mc: int = 0
    sess_text: int = 0
    pending_rec: bool = False

def choose_mode(it: Item) -> str:
    if it.pending_rec:
        return "MC"
    return "Text" if (it.streak >= 3.0 or it.mastery >= 0.50) else "MC"

def record_correct(it: Item, mode: str, divisor: float):
    weight = TEXT_WEIGHT if mode == "Text" else MC_WEIGHT
    is_prod = (mode == "Text")
    it.total += 1
    it.correct += 1
    it.streak += weight
    if is_prod:
        it.prod_in_streak += 1
    eff = it.streak + 0.5 * it.prod_in_streak
    streak_score = min(eff / divisor, 1.0)
    boost = RECOVERY_BOOST if it.mastery > streak_score else 0.0
    it.mastery = min(max(streak_score, it.mastery) + boost, 1.0)
    it.sess_correct += 1
    if mode == "MC":
        it.sess_mc += 1
    else:
        it.sess_text += 1

def ready_current(it: Item) -> tuple[bool, str]:
    m, s = it.mastery, it.streak
    if m >= 0.80 or s >= 8.0:
        return (it.sess_text >= 1 and not it.pending_rec, "Tier1")
    if m >= 0.50 or s >= 3.0:
        return (it.sess_correct >= 2 and it.sess_text >= 1, "Tier2")
    return (it.sess_mc >= 3 and it.sess_text >= 3, "Tier3")

def ready_proposed(it: Item) -> tuple[bool, str]:
    """Proposed rule:
    Tier 1 (high): mastery>=0.80 OR streak>=8 → SessText>=1 AND !PendingRec  (UNCHANGED)
    Tier 2 (mid):  mastery>=0.50 AND streak>=3 → SessCorrect>=4 AND SessText>=2
    Tier 3 (low):  else → SessMC>=3 AND SessText>=3                           (UNCHANGED)

    Key changes from current:
      • Tier 2 trigger uses AND (not OR) — must have BOTH mid-mastery AND streak.
      • Tier 2 floor raised from (2 corr, 1 text) → (4 corr, 2 text).
    """
    m, s = it.mastery, it.streak
    if m >= 0.80 or s >= 8.0:
        return (it.sess_text >= 1 and not it.pending_rec, "Tier1")
    if m >= 0.50 and s >= 3.0:
        return (it.sess_correct >= 4 and it.sess_text >= 2, "Tier2")
    return (it.sess_mc >= 3 and it.sess_text >= 3, "Tier3")

def walk(rule_name: str, divisor: float, ready_fn, max_turns=12, seed_mastery=0.0, seed_streak=0.0, seed_prod=0):
    it = Item(mastery=seed_mastery, streak=seed_streak, prod_in_streak=seed_prod)
    rows = []
    rotated_at = None
    for turn in range(1, max_turns + 1):
        mode = choose_mode(it)
        record_correct(it, mode, divisor)
        ready, tier = ready_fn(it)
        rows.append((turn, mode, it.streak, it.prod_in_streak, it.total, it.correct,
                     it.sess_correct, it.sess_mc, it.sess_text, it.mastery, tier, ready))
        if ready and rotated_at is None:
            rotated_at = turn
    return rows, rotated_at

def fmt_table(title: str, rows):
    print(f"\n### {title}")
    print("| Turn | Mode | Streak | ProdInStreak | TotalAtt | CorrectAtt | SessCorrect | SessMC | SessText | Mastery | Tier  | ReadyToRotateOut |")
    print("|------|------|--------|--------------|----------|------------|-------------|--------|----------|---------|-------|------------------|")
    for (t, mode, st, pis, ta, ca, sc, smc, stx, m, tier, ready) in rows:
        print(f"| {t}    | {mode:<4} | {st:>5.2f}  | {pis:>12} | {ta:>8} | {ca:>10} | {sc:>11} | {smc:>6} | {stx:>8} | {m:>5.3f}   | {tier} | {'**YES**' if ready else 'no':<16} |")

def run_scenario(label: str, **seed):
    print(f"\n## Scenario: {label}")
    print(f"_Seed: mastery={seed.get('seed_mastery',0.0)}, streak={seed.get('seed_streak',0.0)}, prod_in_streak={seed.get('seed_prod',0)}_")
    cur_rows, cur_at = walk("CURRENT", CURRENT_DIVISOR, ready_current, **seed)
    new_rows, new_at = walk("PROPOSED", PROPOSED_DIVISOR, ready_proposed, **seed)
    fmt_table("CURRENT (divisor=7.0, Tier 2 = streak≥3 OR m≥0.5 + SessC≥2 + ST≥1)", cur_rows)
    print(f"\n→ **First rotation: turn {cur_at}**\n")
    fmt_table("PROPOSED (divisor=12.0, Tier 2 = streak≥3 AND m≥0.5 + SessC≥4 + ST≥2)", new_rows)
    print(f"\n→ **First rotation: turn {new_at}**")

if __name__ == "__main__":
    print("# Vocab Quiz Rotation Simulation")
    run_scenario("Fresh word, all-correct, 12 turns")
    run_scenario("Half-mastered word entering session (mastery=0.50, streak=3, prod=1), all-correct",
                 seed_mastery=0.50, seed_streak=3.0, seed_prod=1)
    run_scenario("Already-known word entering session (mastery=0.85, streak=6, prod=2), all-correct",
                 seed_mastery=0.85, seed_streak=6.0, seed_prod=2)
