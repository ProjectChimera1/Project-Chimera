# Game Architecture — RESUME NOTE (Step 4 in progress)

> Working-state sidecar for the **gds-game-architecture** workflow. The clean deliverable is
> `game-architecture.md` (Steps 1–3 saved; frontmatter `stepsCompleted: [1, 2, 3]`).
> This note exists so a NEW session can resume Step 4 at D1 without reconstructing context.
> Delete this file once Step 4 is appended to the deliverable and `stepsCompleted` reaches `[…,4]`.

## Where we are
- Workflow: **gds-game-architecture, Step 4 of 9 — Architectural Decisions.**
- Mode: **INTERACTIVE / facilitator.** Autonomous mode is OFF — `project-intent.md` was renamed to
  `project-intent.md.disabled` on 2026-06-20 at the user's request (rename back to re-enable).
- Step 3 engine decision locked: **adopt Godot 4.6.3** for 1.0; defer 4.7 to post-1.0.
- MCP: `godot-mcp` v3.14.0 (`@satelliteoflove/godot-mcp`), min Godot 4.5 → 4.6.3 supported, no change.
- **D1 DECIDED 2026-06-20** — Bounded Effect-Graph (Option C). Full record appended to
  `game-architecture.md` → *Architectural Decisions (Step 4)*. Calls: Option C; Modifier in MVP;
  cut line = MVP + `Switch` + `NamedEffectReference` (first stretch); sealed `EffectNode` hierarchy;
  caps as named constants (depth≤8, Seq≤8, Search≤64, Spawn≤64, Persist≤256). New prereqs surfaced:
  `SimRng` (build now), `Energy`/`Mana` SoA arrays, data-drive `DamageMatrix`→JSON (feeds D3).
- ▶ **NEXT ACTION: open D2 (trigger-DSL design) — consumes D1's effect vocabulary.**

## Agreed approach (confirmed by Alec)
Deep-dive **D1 → D2 → D3** one at a time (novel, coupled, highest-stakes — facilitate carefully,
user makes each call), then **batch D4–D6** as recommend-and-confirm. Testing → Step 5
(cross-cutting); `MainScene` decomposition → Step 6 (structure).

## Settled as-built — record as decisions (with rationale), do NOT re-open
SoA/ECS sim + command-intents · `Fixed` 16.16 determinism (30 Hz, ascending-ID, seeded RNG) ·
MultiMesh + `*Bridge` rendering · deterministic `SpatialHash` (Jolt presentation-only) · flow-field
+ NavServer3D-direct pathfinding · utility-AI opponent · `Control`+`Theme`+Claude Design UI · Godot
`ResourceLoader` + `.chimera.zip` packaging · server-authoritative lockstep transport (ENet).

## OPEN decisions queue
**CRITICAL (PRD-deferred levers, coupled):**
- **D1. Effects-primitive vocabulary** — ✅ **DECIDED 2026-06-20** (Bounded Effect-Graph, Option C).
  Recorded in `game-architecture.md` → *Architectural Decisions (Step 4)*.
- **D2. Trigger-DSL design** — variables / arithmetic / boolean / arrays / loops / timers / custom
  events / custom runtime UI, bounded so the server can STATICALLY validate. Consumes D1.
  (PRD §4.6; FR-24–28; decision #12)
- **D3. Data-driven definition schema & loader** — the `Definitions` (de)serialization architecture;
  resolves pillar debt: `DamageMatrix`→JSON (+ add the `Hero` damage/armor type), N-resources,
  authorable tech-tree schema. Serializes D1/D2. (GDD §1; FR-12/14/15; arch §5)

**IMPORTANT (recorded choice needed):**
- **D4. Hero persistence model** — HOW to build the WC3 "-load" init-time-deterministic,
  server-validated profile system (the *what* is set by decisions #19/#20). (FR-7a–e)
- **D5. >2-player lockstep + matchmaking** — `Faction` Player1–4 → up to 8; Nakama 1v1 → N-player;
  command routing / host topology. (FR-39/40; decision #13)
- **D6. LLM provider abstraction** — provider config (OpenRouter/Claude/Ollama) Inspector→settings;
  hard authoring-layer-only boundary (never in the sim tick). (FR-29; decision #8; addendum §D)

## Constraints to honor in EVERY decision
- Sim/Presentation boundary is sacred; no Godot types / no `float` gameplay state in sim.
- Determinism: `Fixed` 16.16, 30 Hz, ascending-ID iteration, seeded RNG, no wall-clock.
- Everything shared must be **statically server-validatable** before MP execution (this bounds the DSL).
- Data-driven pillar: no balance/logic hardcoded in a creator-unreachable path.
- Entity cap 4096 (do NOT raise for benchmark reasons).

## Inputs already digested (don't re-read unless a decision needs detail)
- Deliverable so far: `_bmad-output/game-architecture.md` (Steps 1–3)
- Brownfield as-built: `_bmad-output/architecture.md` ; `_bmad-output/project-context.md`
- Intent (source of truth): `Project_Chimera_GDD.md`
- PRD: `_bmad-output/planning-artifacts/prds/prd-Project_Chimera-2026-06-05/` (prd, addendum, decision-log)
- UX (canonical): `_bmad-output/planning-artifacts/ux-designs/ux-Project_Chimera-2026-06-20/` (DESIGN, EXPERIENCE)

## How to resume in the new session
Say something like: **"continue the game architecture workflow — D1"** (or re-invoke
`/gds-game-architecture`). First action: read this note + `game-architecture.md`, then open **D1**.
