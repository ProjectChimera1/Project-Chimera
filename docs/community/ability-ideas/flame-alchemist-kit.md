# Ability Idea — Flame Alchemist Hero Kit

*Captured from brainstorm session 2026-07-14 (Alec + Claude). First entry in the community ideas pile.*

---

## The kit

### 1. Combustion Theorem (active — the signature)

- Hero snaps fingers / claps hands. Every enemy within **5m** is engulfed **sequentially, nearest → farthest**, detonations spaced on **Fibonacci timing gaps** (drumroll at the start, countdown at the end: poof-poof..poof...poof.....BOOM).
- **Damage climbs the sequence** — the spiral gathers heat as it travels. Front-line eats chip damage; the farthest target eats the monster hit *after the longest pause* (built-in dread beat = built-in counterplay: move or die).
- **He's conducting, not casting.** The sequence continues only while the hero stands channeling. Stun or kill him mid-sequence and the remaining flames collapse into black smoke. Vs. good players it's a duel: kill the conductor before the crescendo.
- Every hit leaves an **ignited burn** (DoT) that doubles as a **soot-mark**.
- **Re-snap:** casting again while enemies are still marked skips the sequence — marked targets **flash-detonate instantly** wherever they ran to. First cast is the setup, second is the punishment.

### 2. Living Flames (the spawn mechanic)

- Hitting an **already-ignited** enemy has a **chance** to spawn a living flame. Its power scales with **where in the Fibonacci sequence it was born** (order-1 = candle-wisp, order-3 = man-sized stalker, rare order-5 = slow armor-melting giant).
- **Life = fuel gauge.** Lifespan and health are one always-draining bar — the flame visibly melts from roaring figure to guttering stub. Damage drains it faster; **feeding refills it** (time spent clinging to a burning enemy, or a killing blow, buys seconds). Fed fire rampages; starved fire slumps into slag.
- **Uncommandable — you aim them with arson.** Loose fire takes no orders; it always chases the nearest *burning* thing. Micro skill = choosing what's on fire.
- **They melt, not fight.** Contact stacks **armor-melt** + burn — they're the corrosive that makes the next Theorem cast hit harder, not the damage themselves.
- **Re-snap turns them into ordnance:** each living flame sprints at the nearest marked enemy and detonates at its birth-order's power.
- *Optional Covenant cruelty:* a **starving** flame with no burning enemy in reach bites whatever's closest — including friendly units. Fire keeps its own accounts.

**Kit loop:** sequence → burns → hits spawn flames → flames spread burns + strip armor → re-snap → everything marked pops and every flame becomes a missile.

---

## Triage (what it takes to build)

### Buildable today — Epic 2 ability composition
- Burn DoT on hit (on-hit rider + damage-over-time modifier)
- Stacking armor-melt debuff (apply/stack/refresh modifiers)
- Plain radius damage nova + health self-cost
- A plain temporary summon (summons are in the effect vocabulary)

### Trigger-floor — prototypable once Epic 7 lands
- Much of the living-flame *brain* can be faked at scenario level to playtest the fantasy first: unit-damaged events + spawn action + timers + variables; weighted RandomChoice (7.13) covers the spawn chance. Prototype in triggers → promote to engine bricks if it feels great.

### New-brick requests (engine-first — the roadmap fuel)
1. **Staggered cascade execution** — distance-ordered sequential hits with per-step delay + per-step damage scaling (the Fibonacci engine). Today's Sequence is same-instant; Persistent is fixed-period.
2. **Channeled / interruptible casts** (the conductor mechanic).
3. **Ability-level proc chance** (chance-on-hit spawn — dice exist in the sim, but not yet as an ability brick).
4. **Conditional branch on target state** inside an ability graph ("if already burning → flash-detonate instead of sequencing").
5. **Custom summon behaviors** — seek-nearest-burning targeting, life-as-fuel decay (HP = duration, feed to extend), become-seeker-bomb on command. The 3.6 behavior-component model anticipates these; the specific behaviors are new.
6. *(Optional)* **State-based friendly-fire** — the starving-flame bite.

---

*Name candidate on record: "Combustion Theorem." Add future community submissions as sibling files in this folder.*
