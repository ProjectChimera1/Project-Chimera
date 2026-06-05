# Addendum — Project Chimera 1.0 PRD

Technical depth and as-built detail that supports `prd.md` but doesn't belong in the PRD narrative.
Source of truth for as-built claims: `_bmad-output/{component-inventory,data-models,architecture,
state-management}.md`. Source of truth for status: `Snapshot.md` (STATUS.md/CONTEXT.md deprecated
2026-04-16).

## A. As-Built Gap Map (Done / Partial / Missing vs. GDD intent)

| Area | As-built reality | 1.0 gap | PRD ref |
|---|---|---|---|
| Sim core | Done — `Fixed`, SoA `EntityWorld`, 30 Hz loop, SpatialHash, MultiMesh, interpolation | Regression-guard with tests | §4.0, FR-44/47 |
| Combat | Done, but **`DamageMatrix` hardcoded in C#** (in-code TODO to move to JSON) | Migrate matrix to data (pillar violation) | §B, FR-12 |
| Economy | Done, but **resources hardcoded Ore+Crystal** | Make resources data-driven | §4.3, FR-15 |
| Navigation | Done — flow fields live & deterministic; `PathRequestSystem` is dead-weight fallback | Verify on LAN | §4.9, FR-39 |
| Unit framework | Done (JSON `UnitDefinition`); `Hero` damage/armor class in GDD **not** in enums | Add hero systems; reconcile enums | §4.1, FR-5 |
| Multiplayer | Code-complete, **never LAN-verified**; Nakama **1v1-only** | LAN determinism test; >2-player TBD | §4.9, FR-39/40 |
| Creation Suite | Thin — terrain brush, trigger editor (T2), AI map gen (T4), undo/redo. **No unit/building/faction/ability/hero editors** | Build the missing editors | §4.1–4.6 |
| Trigger DSL | T2 (ECA) + T4 (NL) exist; **no variables/loops/arrays/custom events/custom UI**; T3 node graph status unclear | Expand DSL expressiveness | §4.6, FR-24–28 |
| AI authoring | Claude + Ollama; NL triggers (5-pass), map gen (7-pass) | Add OpenRouter; add balance analysis | §4.7, FR-29/33 |
| UGC/share | Plumbing present (`.chimera.zip`, mod.io REST, browser), **not e2e verified**; mod.io Game ID/key unconfigured | Verify e2e; configure | §4.8 |
| Tests | **`tests/` empty** — zero committed tests | Build GdUnit4 suite | §4.10, FR-44 |
| Content | 2 factions (**Iron Pact = reskin**), 12 maps, audio system wired (no `.ogg`), Iron Pact art = box placeholders | Asymmetric Iron Pact; audio; art | §4.9/4.11 |
| Platform | Windows primary; Linux export = template only | Linux build | §4.11, FR-50 |

## B. Data-Driven Debt (pillar violations to remediate)
The project's #1 pillar is "data-driven everything." Three as-built violations:
1. **`DamageMatrix`** hardcoded in C# — explicit in-code comment to load from JSON. Highest-priority fix; combat balance must be creator-reachable data.
2. **Resources** hardcoded to Ore + Crystal. Two-resource *default* is a deliberate ceiling, but creators must be able to add/replace resource types as data (FR-15).
3. **Tech trees** only via prereq string arrays + `TechTreeChecker` — no rich authorable schema; FR-14 needs a real tech-tree data model + visual editor.

## C. Trigger DSL Expressiveness Target (architecture input)
"General enough to be anything" was bounded (decision-log #12) to a **rich declarative DSL, no
arbitrary scripts.** To approach WC3 GUI-trigger range the DSL needs, at minimum:
- Typed, scoped **variables** + arrays/collections.
- **Arithmetic / boolean expression** evaluation (deterministic, fixed-point).
- **Control flow**: conditionals, bounded loops, **timers**.
- **Custom events** (raise/handle) for decoupled logic modules.
- **Custom runtime UI** primitives (text, counters, buttons) — required for TD/RPG/scoreboard modes.
- **Server validation**: every construct must be statically validatable so the server can reject
  malformed/cheating scenarios before they run. This *bounds* the DSL — anything not statically
  validatable is out.
This is the single largest technical risk in the Creation Suite. It is an architecture-phase
deliverable; the PRD records the requirement (FR-24–FR-28) and the determinism/validation constraint
that bounds it. **The breadth of the shared effect-primitive set (abilities + triggers) is the lever
that decides which genres become buildable.**

## D. LLM Provider Plumbing
- Current: `LLMService` = Claude API + Ollama local fallback; key via `AnthropicApiKey` Inspector export on MainScene.
- 1.0 requirement (decision-log #8, FR-29): add **OpenRouter (free tier)** as a selectable provider; generalize key/provider config to a settings surface (provider dropdown + key field), never hardcoded/committed.
- When consulting/modifying any Anthropic model ids/params/SDK usage, use the `claude-api` skill (per project-context) rather than memory.
- AI runs in the authoring/editor layer only — never inside the deterministic sim tick.

## E. Verification Floor Detail
- **P2.4 LAN determinism test** (FR-39): two machines, full match, checksums in sync 300+ ticks, zero desync, with `FlowFieldBridge` active. NavServer3D is non-deterministic cross-machine; flow fields are the mitigation but **unproven on real LAN** — this is the #1 ship risk.
- **Four unverified systems** (FR-45) each have a smoke-test checklist in `Snapshot.md`: Utility AI (tech/supply/attack-wave/rebuild loops), Adaptive Input Delay (RTT log, 4→2 on LAN, 300+ ticks in sync), LLM Trigger System (L panel, inline events, validation rejection), AI Map Generator (M panel, load-vs-save, 7-pass validation).
- **Tests** (FR-44): GdUnit4 in `godot/tests/` (currently empty). Sim is designed to run headless without Godot — leverage that for fast deterministic unit tests + a replay/checksum regression guard (FR-47).
- Known non-fatal noise: `TerrainBrush._store_undo` push_error per stroke; `NativeCalls.cs` cascade (no EditorPlugin at runtime) — documented, decide whether to suppress for 1.0.

## F. GDD ↔ Code Drift (trust as-built docs over GDD where they conflict)
- GDD references `Hero` damage/armor class and `.NET 9 AOT` — **neither present** (desktop is net8; android-only net9). Treat as future/aspirational.
- GDD faction roadmap (3 by Phase 4) vs. §11 "realistic scope" (3 = stretch) — **resolved: 2 asymmetric at 1.0** (decision-log #6).
- GDD LLM authoring core-vs-stretch contradiction — **resolved: full AI suite in 1.0** (decision-log #7).
- `project.godot` main scene `res://scenes/main.tscn`; composition root `godot/src/UI/MainScene.cs` (~2,200 LOC, ~25 `SetupXxx()` methods) — large; **the single integration chokepoint** every new system wires into. M2/M3 add ~6 editors + hero sim + DSL expansion through it — decompose the wiring first (PRD §6.2 M0).

## G. Save-State Persistence (creator-toggled) — FR-7a
Persistence is a **per-scenario creator toggle**, but enabling it is **net-new build work**, not just a flag:
- The engine has **no mid-game world snapshot today.** Replays (`.chmr`) and lockstep both work by *re-simulating from initial state + commands* — there is no serialized world state. So FR-7a requires a brand-new **deterministic full-world serializer** covering `EntityWorld`/`BuildingStore`/`ResourceStore`/fog (and any other authored persistent state — to be enumerated).
- **Enabled** → scenario progress (hero levels/XP, world state) serializes across play sessions → RPG-campaign scenarios.
- **Disabled** → state is match-only (RTS/MOBA model); no save files.
- **1.0 scope:** single-player/co-op campaigns. Persistent *competitive/multiplayer* state is `[v2 — out of 1.0]` — round-tripping persistence through lockstep is a determinism hazard. Sequence the serializer in M2 (PRD §6.2) before scenarios depend on it.

## H. AI Generation Clamps (relax for non-RTS) — FR-31/FR-32
The AI map/scenario generator's 7-pass validator currently **hard-clamps** output to the showcase RTS (≤6 combat units/faction, ≤6 player slots, forced faction paths). For UJ-1/UJ-2 ("build any game" — TD waves, RPG parties), these clamps are too tight and must be **parameterized by scenario type**, not hardcoded to RTS conventions.
