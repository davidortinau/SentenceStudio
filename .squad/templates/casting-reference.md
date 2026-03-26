# Casting Reference

> On-demand reference for universe selection, name allocation, and casting state schemas.

## Universe Allowlist (31 universes)

| Universe | Capacity | Shape | Tone |
|----------|----------|-------|------|
| Firefly | 9 | ensemble | scrappy, loyal |
| Alien/Aliens | 12 | crew-under-pressure | tense, competent |
| The Thing | 8 | isolated-team | paranoid, methodical |
| Blade Runner | 8 | noir-investigation | reflective, precise |
| Mad Max: Fury Road | 10 | road-crew | resourceful, fierce |
| Star Wars OT | 15 | rebellion | hopeful, varied |
| The Matrix | 10 | hacker-cell | philosophical, skilled |
| Jurassic Park | 8 | expedition | curious, cautious |
| Ghostbusters | 8 | startup-team | irreverent, smart |
| The Expanse | 15 | multi-faction | political, gritty |
| Dune | 15 | dynasty | strategic, epic |
| Terminator 1-2 | 8 | survival | urgent, focused |
| Predator | 8 | squad | tactical, macho |
| Moon | 6 | solo-plus | contemplative |
| Arrival | 6 | first-contact | cerebral, patient |
| Interstellar | 8 | mission-crew | scientific, emotional |
| Edge of Tomorrow | 8 | time-loop-squad | iterative, adaptive |
| The Martian | 8 | rescue-mission | resourceful, witty |
| Annihilation | 6 | expedition | eerie, analytical |
| Prospect | 6 | frontier-pair | rugged, resourceful |
| Ex Machina | 6 | lab-team | intellectual, unsettling |
| Primer | 6 | garage-startup | obsessive, precise |
| Sunshine | 8 | mission-crew | sacrificial, focused |
| District 9 | 7 | investigation | documentary, gritty |
| Her | 6 | intimate | thoughtful, warm |
| Coherence | 8 | dinner-party | paranoid, intimate |
| Upgrade | 6 | solo-plus | visceral, technological |
| Snowpiercer | 10 | class-revolt | stratified, intense |
| Everything Everywhere | 10 | family | chaotic, heartfelt |
| Nope | 7 | ranch-crew | observant, stubborn |
| Severance | 8 | office-split | uncanny, procedural |

## Selection Algorithm

Score each universe: `size_fit + shape_fit + resonance_fit + LRU`

1. **size_fit:** Universe capacity >= team size (required). Closest fit scores highest.
2. **shape_fit:** Match project structure to universe shape (ensemble, crew, squad, etc.)
3. **resonance_fit:** Match project domain/mood to universe tone
4. **LRU:** Least recently used universe gets bonus (from history.json)

Same inputs → same choice (deterministic unless LRU changes).

## Casting State Schemas

### policy.json
```json
{
  "universes": ["Firefly", "Alien/Aliens", ...],
  "max_capacity": 25,
  "overflow_strategy": "diegetic-expansion",
  "lru_bonus": 2
}
```

### registry.json
```json
{
  "agents": {
    "{name}": {
      "persistent_name": "{Name}",
      "universe": "{Universe}",
      "role": "{Role}",
      "created_at": "{ISO 8601}",
      "legacy_named": false,
      "status": "active"
    }
  }
}
```

### history.json
```json
{
  "assignments": [
    {
      "assignment_id": "{uuid}",
      "universe": "{Universe}",
      "created_at": "{ISO 8601}",
      "agents": ["{name1}", "{name2}"]
    }
  ]
}
```

## Overflow Handling

When agent count exceeds universe capacity (in order):
1. **Diegetic Expansion:** Minor/peripheral characters from same universe
2. **Thematic Promotion:** Closest parent universe family (e.g., Star Wars OT → prequel)
3. **Structural Mirroring:** Archetype counterparts from the universe family

Existing agents are NEVER renamed during overflow.
