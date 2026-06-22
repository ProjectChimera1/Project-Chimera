---
stepsCompleted: [1, 2, 3, 4]
status: complete
epicCount: 10
storyCount: 97
validation: "FR coverage 60/60; NFR-1..6 covered; no residual placeholders; no within-epic forward dependencies (verified 2026-06-21)"
inputDocuments:
  - "_bmad-output/planning-artifacts/prds/prd-Project_Chimera-2026-06-05/prd.md"   # FR/NFR source (Road-to-1.0 PRD)
  - "_bmad-output/planning-artifacts/prds/prd-Project_Chimera-2026-06-05/addendum.md"
  - "_bmad-output/planning-artifacts/prds/prd-Project_Chimera-2026-06-05/decision-log.md"
  - "_bmad-output/game-architecture.md"                                            # Forward 1.0 architecture (Additional Requirements)
  - "_bmad-output/planning-artifacts/ux-designs/ux-Project_Chimera-2026-06-20/DESIGN.md"     # UX-DR source (visual spine)
  - "_bmad-output/planning-artifacts/ux-designs/ux-Project_Chimera-2026-06-20/EXPERIENCE.md" # UX-DR source (behavioral spine)
  - "Project_Chimera_GDD.md"                                                       # design context (not a numbered-req source)
  - "_bmad-output/fma-faction-design.md"                                           # showcase-faction content context
frNumberingNote: "FR IDs preserved verbatim from the PRD (FR-1..FR-52; lettered inserts FR-7a..e, FR-12a, FR-38a, FR-49a are each their own requirement). NFR-1..NFR-6 are the PRD's canonical cross-cutting NFRs."
scope: "gap-to-1.0 (brownfield). §4.0 PRD Built Foundation is reference-only, NOT 1.0 scope."
---

# Project Chimera — Epic Breakdown

## Overview

This document decomposes the **Road-to-1.0 requirements** into implementable epics and stories. It is
**brownfield and gap-framed**: the requirements below are the *remaining work to reach 1.0*, not the
whole game. Already-built systems (PRD §4.0 "Built Foundation") are listed once for context and are
**out of scope except where a requirement explicitly hardens or verifies them**.

Sources: FRs/NFRs from the **PRD** (`prd.md` + `addendum.md`), technical/foundational requirements from
the **forward architecture** (`game-architecture.md`, complete 2026-06-21), and UX Design Requirements
from the **UX run** (`ux-Project_Chimera-2026-06-20`). GDD + FMA faction design supply scoping context.

> **FR IDs are preserved from the PRD** for traceability (FR-1…FR-52, with intentional lettered inserts).
> The PRD's **M0–M6 build sequencing** (recorded at the end of this inventory) is the recommended epic order.

---

## Requirements Inventory

### Built Foundation *(reference only — NOT 1.0 scope; 1.0 hardens/verifies these via §4.10)*

- Deterministic sim core — `Fixed` 16.16, SoA `EntityWorld` (4096), 30 Hz `SimulationLoop`, `SpatialHash`, MultiMesh rendering, interpolation (~350 FPS @ 1000 units, single-machine).
- Combat — damage/armor matrix, cooldowns, projectiles, splash, combat feedback, death/cleanup.
- Economy — resource nodes, gatherer FSM, dynamic supply cap, resource HUD.
- Unit framework — JSON `UnitDefinition`, 6 archetypes, command system, `SelectionSystem` *(control groups + formation movement: VERIFY before treating as done — may become a §4.10 task)*.
- Navigation — flow-field pathfinding (live, deterministic), fog of war, NavServer3D direct API.
- Base building — placement, construction, production queues, data-driven tech-tree gating, rally points.
- AI opponent — utility-AI (Easy/Normal/Hard).
- Multiplayer **(code-complete, UNVERIFIED on LAN)** — lockstep, adaptive input delay, ENet + relay, command serialization, checksums/desync detection, `.chmr` replays, spectator, Nakama 1v1, content hashing, chat.
- Editor basics — Terrain3D sculpt/paint, entity/start-position/resource-node placers, win-condition setter, undo/redo, NL trigger system, AI map generator.
- UGC plumbing — `.chimera.zip` packager, content browser (Local + mod.io), mod.io REST, creator profiles.
- Content — 2 factions (Alpha + Iron Pact *reskin*), 12 maps, full HUD/menus/settings/audio system (assets pending).

---

### Functional Requirements

*Grouped by PRD feature area (§4.1–§4.11). Tags: `[headline]` core value · `[polish]`/`[verify]`/`[infrastructure]`/`[finishing]` per PRD.*

#### Epic-area 4.1 — Creation Suite: Unit & Hero Authoring `[headline]`
- **FR-1** — An Architect can create, edit, duplicate, and delete a unit definition in-app without editing JSON, persisted to the scenario's data.
- **FR-2** — The Unit Card Editor presents stats, archetype, combat type (damage/armor), model assignment, and attached abilities in a single panel.
- **FR-3** — An Architect can assign a 3D model (or box placeholder) to a unit and preview it in-editor.
- **FR-4** — An Architect can compose a unit from the 6 archetypes plus orthogonal ability/behavior components (composition over inheritance — no subclassing).
- **FR-5** — An Architect can designate a unit as a **Hero**, configuring leveling curve, experience gain, and signature/ultimate abilities.
- **FR-6** — Simple mode offers preset/templated units; advanced mode exposes every authorable field.
- **FR-7** — Validation surfaces authoring errors inline before save/playtest: missing/invalid model, out-of-range/missing required stat, reference to an undefined ability, invalid archetype composition.
- **FR-7a** — Author **persistent artifacts** (WC3 save-code model): a per-scenario **persistence manifest** selecting which hero/unit/player attributes (level/XP, inventory, skill tree, currency) carry to the *next* custom game. Persisted per player.
- **FR-7b** — Players **load** their persisted profile at custom-game start, applied as **deterministic initial state** (works in MP/online; init-time data, never a mid-game snapshot). Creator can disable persistence (match-only).
- **FR-7c** — Online save profiles are **stored/validated server-side** (not raw client save-codes) to prevent tampering. `[ASSUMPTION: server-side storage vs signed client codes — confirm.]`
- **FR-7d** — A **Save/Load Interface** (hero-picker menu, not text codes) lets a player manage saved heroes/profiles per scenario/family; each entry shows hero icon + basic info; creator enables/configures per scenario; FR-7a manifest drives contents.
- **FR-7e** — From the Save/Load Interface a player can **load** a saved hero into a compatible game, **save** a new profile, and **overwrite** a slot (with confirmation). Multiple saved heroes per player.

#### Epic-area 4.2 — Creation Suite: Ability / Skill Authoring `[headline]`
- **FR-8** — Create an active ability (targeting type, cost, cooldown, ≥1 effect) in-app without code.
- **FR-9** — Create a passive ability (auras, on-hit effects, stat modifiers) with trigger conditions.
- **FR-10** — Compose an ability from multiple effect primitives (advanced mode) or configurable presets (simple mode).
- **FR-11** — Abilities attach to units/heroes (FR-4, FR-5) and appear on the runtime command card.
- **FR-12** — Ability definitions are data-driven, deterministic, and server-validatable (no float math, no arbitrary scripts).
- **FR-12a** — Abilities/units carry a **combat-feedback profile** (hit particles, impact sound, screen shake, hit-freeze, death effect). A tuned default ships ("juice"); creators override per unit/ability.

#### Epic-area 4.3 — Creation Suite: Building, Tech Tree & Economy Authoring
- **FR-13** — Create/edit building definitions (stats, production options, construction cost/time, tech prerequisites) in-app.
- **FR-14** — Build a faction's tech tree visually (drag dependencies); runtime gates production by it.
- **FR-15** — Define a scenario's resource set as data (type, model, starting amount, gather model) beyond the two defaults.
- **FR-16** — Configure the supply/cap model per scenario.

#### Epic-area 4.4 — Creation Suite: Faction Definer
- **FR-17** — Create a faction by assembling authored units/heroes/buildings/tech-tree, name, color, and starting conditions through a guided multi-step flow.
- **FR-18** — Assign an AI preset to a faction so it is playable against/with the AI opponent.
- **FR-19** — A completed faction is immediately selectable in playtest and skirmish/multiplayer setup.
- **FR-20** — The two shipped factions are valid Faction Definer outputs, and **Iron Pact is upgraded from reskin to genuinely asymmetric**. *Acceptance:* ≥1 unique core mechanic + a roster differing from Alpha in role/stat profile (not 1:1 renames), validated in playtest.

#### Epic-area 4.5 — Creation Suite: Map / Terrain Editor `[polish]`
- **FR-21** — Sculpt and texture-paint terrain in-app; **painted textures persist correctly on save/load** (fixes current procedural-texture defect).
- **FR-22** — Place entities, start positions, resource nodes, and set win conditions on the map *(existing — verify to ship bar)*.

#### Epic-area 4.6 — Creation Suite: Trigger / Rules System (Rich Declarative DSL) `[headline]`
- **FR-23** — Author scenario logic as ECA triggers in-app *(built — verify to ship bar)*.
- **FR-24** — DSL supports variables (typed, scoped), arithmetic/boolean expressions, arrays/collections, conditional loops, and timers.
- **FR-25** — Define custom events and raise them from triggers (decoupled game-logic modules).
- **FR-26** — Define custom runtime UI elements (text, counters, buttons) driven by triggers (TD waves, RPG dialog, scoreboards).
- **FR-27** — All DSL constructs are deterministic, fixed-point, and server-validatable before a scenario runs in MP (no scripting escape hatch).
- **FR-28** — Author the same logic at T1 (preset), T2 (ECA), T3 (visual node graph), or T4 (natural language); tiers interoperate on one underlying DSL representation.

#### Epic-area 4.7 — AI-Assisted Creation (cross-cutting) `[headline]`
- **FR-29** — Select/configure an LLM provider (OpenRouter, Claude, or local Ollama) by supplying an API key via settings; never hardcoded/committed. *Includes migrating key storage from the `AnthropicApiKey` Inspector export into the persisted settings system — more than a dropdown.*
- **FR-30** — Generate triggers from a natural-language prompt; review/edit before applying *(built — verify, extend to new DSL constructs)*.
- **FR-31** — Generate a map from a prompt with validation before load *(built — verify)*. *The 7-pass validator's hard clamps (≤6 combat units/faction, ≤6 slots, forced faction paths) must be relaxed/parameterized for non-RTS scenario types.*
- **FR-32** — Generate a unit, ability, hero, or faction draft (stats + name + lore) from a prompt, as editable data *(generation must not assume RTS-only conventions)*.
- **FR-33** — Request **AI balance analysis** of a faction/scenario and receive actionable, editable suggestions *(new build work)*.
- **FR-34** — When no provider/key is available, AI features **degrade gracefully** with a clear message; the suite remains fully usable manually.

#### Epic-area 4.8 — Share & Discover (UGC) `[verify + polish]`
- **FR-35** — Package a scenario as `.chimera.zip` and publish to mod.io in-app *(verify end-to-end; needs mod.io Game ID + API key configured)*.
- **FR-36** — A creator must pass the **proof-of-play gate** (win their own scenario) before publishing.
- **FR-37** — Players can browse, search, tag-filter, sort, subscribe to, and rate scenarios in the content browser.
- **FR-38** — Published packages are content-hashed and integrity-verified on download.
- **FR-38a** — **Creators retain full ownership** of their content; the platform takes only a non-exclusive host/distribute right. Surfaced at publish time.

#### Epic-area 4.9 — The Showcase Game & Multiplayer `[verify + harden + content]`
- **FR-39** — Two players complete a full MP match on **separate machines (LAN)** with checksums in sync for **300+ ticks and zero desync** — the **P2.4 LAN determinism test must pass** *(never run; #1 risk)*.
- **FR-40** — Matchmake (Nakama) or join via LAN/lobby with in-game chat; a `.chmr` replay is saved **and viewable/shareable**. *Target up to 8 players; pre-match party grouping assumed 1.0 (confirm); spectator included in the MP verification set.*
- **FR-41** — Adaptive input delay verifiably adjusts to RTT on real networks (4→2 on LAN, clamped [2,12]) without desync.
- **FR-42** — The two shipped factions are balanced. *Acceptance:* neither win rate outside ~45–55% across a representative AI self-play / playtest sample.
- **FR-43** — A solo player can play skirmish vs. the AI opponent across difficulties on the shipped maps.

#### Epic-area 4.10 — Verification Floor & Quality `[infrastructure]`
- **FR-44** — An automated test suite covers the deterministic sim, combat formula, economy, and pathfinding, runnable **headless without Godot**. *(Architecture supersedes the PRD's "GdUnit4-only" wording → **two tiers**: Tier-1 xUnit Godot-free + Tier-2 GdUnit4 — see Additional Requirements.)*
- **FR-45** — The four unverified systems (Utility AI, Adaptive Input Delay, LLM Trigger System, AI Map Generator) each pass their smoke-test checklists.
- **FR-46** — A performance pass confirms the 500–2,000 units @ 60 FPS render / 30 Hz target on representative scenarios.
- **FR-47** — `[ASSUMPTION]` Determinism is regression-guarded by a replay/checksum test in CI so future changes can't silently break multiplayer.

#### Epic-area 4.11 — Release Readiness `[finishing]`
- **FR-48** — Audio assets (`.ogg`) are present and wired through the existing audio system.
- **FR-49** — Iron Pact's 8 placeholder GLBs are replaced with real art (external Hunyuan3D/Tripo pipeline).
- **FR-49a** — An **art-style consistency layer** keeps AI-generated/creator art visually coherent — at minimum a shared material library + a global post-process (e.g. cel-shading) shader. `[confirm 1.0 enforcement depth.]`
- **FR-50** — A Linux export (dedicated-server and/or client) builds and runs.
- **FR-51** — Accessibility 1.0 **baseline**: remappable keys, colorblind-safe team colors, UI scaling, subtitles for any voice-over.
- **FR-52** — The build ships to **Steam and a direct DRM-free channel** (e.g. site/Gumroad): both pipelines release-ready.

---

### NonFunctional Requirements

#### Canonical Cross-Cutting NFRs (PRD §4.12)
- **NFR-1 — Fast edit→play loop (UJ-3).** Entering playtest reflects authoring changes **without an app restart**, target **≤ a few seconds**. The core creation-loop quality bar.
- **NFR-2 — Creator experience / discoverability.** Every field/button/panel has a hover tooltip; a guided **"Your First Scenario"** onboarding exists; a creator reaches a basic playable scenario in **< 15 min**.
- **NFR-3 — Editor invisible to Commanders.** Players who never create are never exposed to authoring UI; creation surfaces are opt-in.
- **NFR-4 — Determinism is sacred (system-wide).** No feature (editors, hero/save, custom UI, AI) introduces float gameplay math, wall-clock dependence, or nondeterministic iteration into the sim. AI/LLM runs only in the authoring layer.
- **NFR-5 — Performance.** The 500–2,000 units @ 60 FPS render / 30 Hz sim target holds on shipped **and community** scenarios (verified by FR-46).
- **NFR-6 — Server-validatable content.** Every shareable construct (units, abilities, triggers, DSL logic, custom UI) is statically validatable so the server can reject malformed/cheating scenarios before they run — this **bounds** DSL expressiveness.

#### Hard Gates & Additional System-Wide Quality Attributes *(derived from PRD §5/§7 + addendum + architecture; de-duplicated against the FRs above)*
- **HARD GATE — Zero desyncs** in ≥95% of MP matches; FR-39 LAN determinism test passes *(non-negotiable, §7)*.
- **HARD GATE — Build a game without JSON**: a creator builds a complete novel custom game (units + heroes + abilities + faction + rules) in-app without editing JSON *(North Star, §7)*.
- **Determinism specifics.** `Fixed` 16.16 end-to-end (one quantization boundary at the JSON converter); ascending-id iteration; **seeded `SimRng` only**; no wall-clock; **`SimChecksum` covers all active factions + all sim stores**; canonical **model-level start-state hash** (FNV-64); **server-enforced peer agreement** (server stops being a pure relay).
- **Security model.** Server authority + command validation only; **no client/kernel anti-cheat**; server-authoritative lockstep only (no P2P/WebRTC); UGC safety is **structural** (no code in content) + fail-closed validation + content hashing.
- **IP & commercial.** Creators retain IP (non-exclusive host/distribute right, surfaced at publish — FR-38a); **premium one-time purchase ($15–25)**; no F2P/microtransactions; no real-money creator marketplace at 1.0.
- **Platform.** PC desktop only (Windows primary, Linux for servers); **no web/mobile/console / no C# web export**; ships to Steam + a DRM-free channel; Linux export builds/runs.
- **Maintainability.** Data-driven everything (incl. DamageMatrix→JSON, resources, tech trees); composition over inheritance; **decompose the ~2,200-LOC `MainScene` composition root before piling on M2/M3**; migrate `PathRequestSystem` Move→Stop logic to `FlowFieldBridge` before removal.
- **Quality gates / health signals.** Factions balanced (45–55% win rate, FR-42); a tuned default **combat-feedback "juice"** ships; Steam reviews >80% positive; ≥50 community scenarios published within month 1.
- **Content scope.** Each scenario is **fully self-contained** (no cross-package dependencies) for 1.0.

---

### Additional Requirements *(from the forward architecture — `game-architecture.md`)*

> **Starter template?** **NONE — this is brownfield (Phase 5 → 1.0).** In place of a greenfield scaffold, the
> architecture mandates a **foundational refactor**: the *"Shrinking Composition Root + Sim-Spine Strangler"* —
> decompose the 2,223-LOC `MainScene.cs` god object into a ≤250-LOC ordered `ISetupPhase[]` root constructing a
> **Godot-free `SimulationHost` + `ScenarioApplier` behind a fail-closed `ScenarioValidator`** gate, via a
> **14-step golden-checksum-gated, always-shippable strangler migration**. **This replaces "Epic 1 Story 1
> starter setup" — the first foundational stories are M1 (the test/determinism/CI spine) + Migration Steps 0–6.**
> **M1 must be GREEN before the D1 strangler can start.**

**Infrastructure**
- **AR-1** — Engine bump **4.6.2 → Godot 4.6.3** (.NET) as a 1.0 prerequisite; verify the `godot_mcp` (MCPGameBridge autoload) and `terrain_3d` addons still connect after the bump. *(4.7 deferred post-1.0.)*
- **AR-2** — Pin the sole shipped NuGet dep to **NakamaClient 3.13.0**; test-only deps (xUnit) live **only** in the Godot-free test project (keeps sim AOT-eligible).
- **AR-3** — Composition-root becomes an asserted `ISetupPhase[]` literal run by a `ScenePhaseRunner`, pinned by a `PhaseOrderTest`; cross-phase deps constructor-injected. `FactionRegistry` localizes all faction-count knowledge (`PLAYER_COUNT=8`, `FACTION_ARRAY_SIZE=9` incl. Neutral; never bare `FACTION_COUNT` in new slot loops).
- **AR-4** — Net-new `ILogSink` deterministic logging seam: sim/Godot-free code logs only through an injected sink (never `GD.Print`/Console), so the headless server/test project runs without perturbing the tick. Relocate `StressTest.cs` out of `src/Core`.
- **AR-5** — `SettingsData` becomes a versioned subsystem (own `schema_version` + migration registry) **before** D6 provider fields and accessibility fields land; config has three homes by lifetime (immutable per-match `GameConfig`, versioned `SettingsData`, `ISecretStore`).

**Sim spine (Godot-free, headless-testable)**
- **AR-6** — Net-new **`SimulationHost`**: single owner of the 9-system tick order, with **`ModifierSystem` inserted at index 3 (before `CombatSystem`)**; owns `EnableChecksums` + a single checksum sink. Constructed by both the test project and `ServerBootstrap`.
- **AR-7** — Net-new **`ScenarioApplier`**: sole writer of sim truth (absorbs ApplyScenario/SpawnScenarioUnit/etc.); Godot path resolution hoisted to a presentation pre-pass; `SpawnUnit` allocation-free.
- **AR-8** — **D1 single Effect-Graph surface** (`src/Effects`, pure sim): a closed, typed, statically-bounded vocabulary of sealed `EffectNode` leaves + exactly three composition nodes (`Sequence`/`SearchArea`/`Persistent`) + a first-class `Modifier`, executed via a pre-allocated **work-stack (not recursion)**, depth ≤8 / fan-out capped. **No scripting escape hatch ever.** This one vocabulary is the only effect surface for abilities, the trigger DSL, and AI balance.
- **AR-9** — **D1 Modifier subsystem**: net-new SoA `ModifierStore` + `ModifierSystem` + `Energy`/`Mana` SoA arrays; `Base*`/`Effective*` paired stat arrays with dirty-flag recompute before combat. **The keystone primitive — MOBA/TD/RPG genres are unbuildable without it; on the 1.0 MVP critical path.**
- **AR-10** — **D2 Trigger DSL** as a single typed event/dataflow graph IR (`src/Dsl`): exec + typed data edges, persistent integer node ids, dense-index store; the trigger graph is a **superset that contains D1 effect subgraphs** (extends D1's validator/executor, not a second system). Graph-canonical serialization from migration step 1; GraphEdit/T3 is a later additive view.
- **AR-11** — **D2 bounded-loop/termination model** (layered hybrid L0–L3): only `ForEach`/`ForEachBatched` over snapshotted ascending-id collections; **no While/recursion/goto**; acyclic custom-event DAG proven at load; static cost rejection; checksummed per-tick fuel budget. Named caps (`MaxLoopIterations≈256`, `MaxEventCascadeDepth≈8`, `MaxArrayCapacity≈256`, `MaxWidgets≈256`, …) corpus-validated as a **gate** on D2 before lock.
- **AR-12** — **D4 hero persistence (two-rail)**: one `PlayerProfile` + `PersistenceManifest` through the D3 validate gate; sparse `HeroStore` SoA keyed by a **stable cross-match hero identity** (not the recycled entity id), folded into `SimChecksum` + a new canonical `startStateHash`. M2 = offline `LocalProfileSource` + hero-picker UI; M5 = online drop-in (Nakama storage, server-attested). **Prereq: generalize `SimChecksum` to all factions before the HeroStore fold.**

**Determinism**
- **AR-13** — Net-new **seeded `SimRng`** (deterministic PCG/xorshift over `Fixed`/int) as first-class sim state, advanced in one canonical order, folded into `SimChecksum` + replay. **The validator must reject any random effect until `SimRng` ships and is checksummed.**
- **AR-14** — **Fixed-point end-to-end**: `FixedJsonConverter` quantizes + rejects NaN/Inf/over-range at deserialize (the single quantization boundary); validator checks `Fixed.Raw` ranges; hash/tick use `.Raw` with no second conversion. `Fixed.FromFloat` allow-listed **only** in the converter (+ AI quantize step). Zero float in the 30 Hz tick.
- **AR-15** — **Generalized `SimChecksum`**: widen from Ore[P1]/Ore[P2]-only to Crystal/Ore for all slots, Supply, and every net-new store (Modifier/Energy/DslVar/Hero) for all active factions in ascending order. Bump `checksum_algo_version` once (its own re-baseline step). Add a `SimChecksumCoverageGuardTest` that fails if a new per-faction/per-entity store lacks coverage.
- **AR-16** — Fix three latent as-built nondeterminisms (negative-test-gated): in-tick Fixed→float→string→parse round-trip in `ScenarioDirector` → Fixed-vs-Fixed + `InvariantCulture`; unstable `Array.Sort` → total order (Priority desc, ascending node-id tiebreak); Dictionary-backed `_timers`/`_variables` → dense-index SoA folded into `SimChecksum`.

**Networking & server authority**
- **AR-17** — **D5 N-aware dedicated relay → stateful server authority**: server-built/broadcast `TickCommandsMerged` (re-stamps faction from authoritative slot), server-side checksum collector with **majority-vote desync attribution + fail-closed HALT on no majority**, Ready-COUNT state machine, server-dictated adaptive input delay. Ship/verify **N≤4** in 1.0; 8 is a constant-bump fast-follow (built N-shaped).
- **AR-18** — **Canonical-model multi-hash handshake**: the Ready packet carries `{scenarioHash, rulesetHash, startStateHash}`; the server **stores/attests/gates** against its own canonical values (never relays self-reported hashes). `hash==0`-skip tolerance becomes a hard reject; add missing `PROTOCOL_VERSION` rejection at both endpoints. **Reconcile the 50-vs-64 spawn-cap divergence to one named constant.**
- **AR-19** — **Replay format v2**: bump version (hard-reject v1), add DSL-event/tagged-record body + parse/apply branch, embed canonical `scenarioHash` + algo-version, re-gate scenario on playback. Thread DSL-event application through all four command-apply sites + replay (first unify the three `ApplyOrders` copies). *"Replays are free" is false — real scoped work.*
- **AR-20** — Disconnect = deterministic **freeze-and-continue** (server broadcasts "faction K idle at applyTick", ACK-gated); stall detection is **tick-counted, not wall-clock**. Pre-match parties: parameterize the Nakama matchmaker + party APIs now; the parties lobby UI is the deferrable slice.

**Data / serialization / migration**
- **AR-21** — **D3 single `ContentLoader` choke point** + one canonical `JsonSerializerOptions` (`UnmappedMemberHandling.Disallow`, source-gen, `FixedJsonConverter`, `NodeBaseJsonConverter`). Unify the 5–7 divergent options; a lenient ingest exists **only** for the T4 LLM path. Forbid bespoke `JsonSerializer.*` calls.
- **AR-22** — Custom **`JsonConverter<NodeBase>` + closed type registry** (discriminator `kind`) for the polymorphic D1/D2 node IR; `[JsonPolymorphic]`/`[JsonDerivedType]` **forbidden project-wide**. Unknown kind / dangling ref / missing required field is a hard fail-closed error.
- **AR-23** — **Canonical-model hash** (FNV-64) over the parsed model's `Fixed.Raw` integers (never float text), fixed field order, enums as stable names, sorted collections, authoring annotations excluded, covering all gameplay files. Re-point the match start-state hash from `ComputeFileHash(ScenarioPath)` to this canonical hash (fixes the AI-gen stale-file desync). **Wire hashes 32-bit; canonical model hash 64-bit.**
- **AR-24** — **Versioning subsystem**: integer `schema_version` on documents + a JsonNode-DOM `vN→vN+1` migration registry (pure migrations: `InvariantCulture`, no dict enumeration); strict gameplay region uses Disallow+throw; explicit `_editor`/`_ext` namespace round-trips verbatim under a gameplay-key denylist. Migration round-trip + replay-reject tests required.
- **AR-25** — Enforce **`min_game_version`** (written today, never read): `CurrentGameVersion` constant + semver-prefix compare **before** strict deserialize; auto-stamp from registry; hard-refuse documents newer than the engine. A version-stamp coherence check moves all version fields together.
- **AR-26** — **Data-drive the hardcoded tables**: lift the 4×5 `DamageMatrix` → `damage_table.json`, **extended to 5×6 by inserting Hero damage/armor types before COUNT**; N-resources → a top-level ordered registry with sparse maps; tech-tree → inline `prerequisites:string[]` against a data-driven id registry (retires the hardcoded switches); named-effect catalog → an id-keyed map, cycle-linted with referential integrity at import.

**UGC / presentation**
- **AR-27** — **P2 runtime binary-asset ingest** (non-editor builds): `GLTFDocument.AppendFromFile→GenerateScene` for `.glb` (**not** `GD.Load<PackedScene>`), `Image.LoadFromFile`, `AudioStreamOggVorbis.LoadFromFile`; assets extracted from `.chimera.zip` into a `user://` cache; per-asset validation (allow-list, size, dims, vertex/submesh caps); invalid → box placeholder, never crash. Net-new `AssetRegistry` (logical id → runtime mesh into the MultiMesh); asset bytes folded into the content hash. **Required for ANY non-editor build to show custom art.**
- **AR-28** — **P1 art-style consistency layer** (FR-49a): shared `StandardMaterial3D` `.tres` preset library via MultiMesh material-override + one global `WorldEnvironment` post-process (cel-shading + tonemap). Runs after external AI-art generation.
- **AR-29** — **P3 `CombatFeedbackProfile`** (FR-12a): a presentation-domain DTO in `src/Core/Definitions`, serialized through D3, **excluded from `SimChecksum`/canonical hash**, driving D1 presentation leaves (`PlayVfx`/`PlaySound`/`ShakeScreen`). **Hit-freeze is presentation-only — never pauses the sim tick.**
- **AR-30** — **P4 proof-of-play + pre-publish quality gate** (FR-36): capture a signed completion token (canonical hash + outcome + timestamp) from the D1 Victory leaf into the `.chimera.zip` manifest; the publish path **refuses upload** without a valid token + minimum-quality fields (thumbnail, description ≥100 chars, ≥1 screenshot).
- **AR-31** — **P5 content browser + IP-ownership surfacing** (FR-37/38a): browse/search/tag/sort/subscribe/rate delegate **entirely to mod.io-native features** (no parallel rating/search system); FR-38a consent is an explicit publish-flow checkbox recorded in the manifest, required before upload.
- **AR-32** — **Two-rail custom UI** (FR-26): **READ rail** = double-buffered version-stamped `DslVarReadback` published once per tick (strings never enter the tick); **WRITE rail** = a net-new `DslEventCommand` on the lockstep command bus with per-event allowed-raiser authorization. UI is a declarative widget tree in `ScenarioData` (closed vocabulary: Panel/Label/Counter/ProgressBar/Button/Timer/Leaderboard/FloatingText/ItemList); every bind resolves + type-matches at load.

**LLM & secrets**
- **AR-33** — **D6 hand-rolled Godot-free `ILLMProvider`** (`GenerateAsync(NormalizedRequest)→NormalizedResult`) over three adapters (Anthropic Messages, Ollama `/api/chat`, OpenRouter `/chat/completions`); **no vendor SDK** (keeps AOT clean). Blocking v1; selected provider authoritative (replaces implicit Claude→Ollama fallback); curated data-driven provider/model lists + free-text override. **Authoring-layer only — zero sim coupling**; AI output validated by the **same D3 gate** with float→Fixed quantize before the canonical hash. FR-34 four-state UI + Test-connection button. Default model `claude-sonnet-4-6`; provider endpoints treated as untrusted (pin cloud hosts, cap buffered bytes).
- **AR-34** — **D6 secrets via a net-new `ISecretStore`** over a gitignored `user://secrets/llm.key` (plaintext floor; DPAPI/libsecret later). **Rip out the two `[Export]` plaintext-secret fields** (`AnthropicApiKey`, `ModIoApiKey`); move provider/model/baseUrl into versioned `SettingsData`. A `SecretExclusionTest` asserts no key string in build output.

**Testing & CI**
- **AR-35** — **Two-tier testing from zero** (repo has zero tests; M1 must be GREEN before the D1 strangler starts). **Tier-1** = a net-new Godot-free plain-.NET `ProjectChimera.Sim.Tests` (**xUnit**): golden-checksum replay regression harness (byte-identical `SimChecksum`, exact uint equality), `SimRng`/ascending-id/zero-alloc-in-tick tests, D1/D2/D3 negative-validation, coverage-guard, migration round-trip, secret-exclusion, `PhaseOrderTest`. This same project is the home the deferred AOT server compiles from. **Tier-2** = GdUnit4 (`godot/tests`) for presentation/integration only.
- **AR-36** — **Banned-API Roslyn analyzer** over the sim layer (bans `using Godot`, float gameplay math/`FromFloat`/`ToFloat`, `System.Random`/Godot RNG, `DateTime`/`Stopwatch`, Dictionary enumeration driving sim order, unstable `Array.Sort`, `GD.Print`, `[JsonPolymorphic]`, `[Export]` holding a key, bare cap literals). Composes with an AOT-eligibility analyzer. **Advisory on master** (preserves the hourly `[AutoSave]` loop); **hard enforcement release-branch only.**
- **AR-37** — **Cross-platform determinism gate**: the golden-checksum harness runs on **both Windows and Linux** and the two `Fixed`-checksum sequences are diffed (the only real proof Fixed-point holds). Run by an AI-orchestrated check-workflow runner (triggered/scheduled, not always-on). **Prereq: install .NET inside the existing WSL/Ubuntu** (also the Linux dedicated-server build host). FR-39 is the #1 ship risk and a hard gate.
- **AR-38** — **`ServerBootstrap` as a peer composition root** (not a client branch): the headless branch builds `SimulationHost` + `ScenarioValidator` + `ScenarioApplier` with no presentation, creating the shared sim path the server lacks today. Server-authority core lives in a Godot-free `ServerHost` extracted from `DedicatedServer`. *NativeAOT project-split extraction is deferred post-1.0 — only the AOT-analyzer gate + Godot-free discipline now.*

**Validation gate & open forks**
- **AR-39** — **Fail-closed `ScenarioValidator`**: `Validate(ScenarioModel) → Validated<ScenarioModel>` as the single pre-tick gate on **all five entry paths** (file, AI-gen, fallback, editor-in-memory, replay); bounds-checks every value, rejects NaN/Inf and `slot>=FACTION_COUNT`. `Validated<T>` has an assembly-internal ctor minted **only** by the validator, so no `Fixed.FromFloat` on external data compiles before validation. Ships shadow/log-only on master; the fail-closed flip happens on a release branch after a corpus run proves every shipped scenario passes.
- **AR-40** — **Two M1 checksum forks must be pinned before their subsystem ships** (promote to story-entry gates in sprint planning): cross-faction same-tick event tie-break (recommend ascending faction slot) and server >2-player quorum model. *(Other tracked leaf forks carry recommended defaults and don't change pattern shape.)*
- **AR-41** — **Telemetry: NO analytics in 1.0.** Dev-only diagnostics (logs, `.chmr` replay capture) + an opt-in crash/desync report. Desync-diagnosis tooling (per-system sub-checksums, replay-diff) is homed structurally as a fast-follow, not 1.0-blocking.

---

### UX Design Requirements

*From `ux-Project_Chimera-2026-06-20` (DESIGN.md visual spine + EXPERIENCE.md behavioral spine), both authoritative.
Items flagged ⚠ are under-specified in the source and need a concrete value/decision before they're fully story-ready
(captured here so the gap is tracked, not hidden). Godot mapping note: chamfers map to **faceted `StyleBox`**, not `corner_radius`.*

**Design system — tokens → one Godot `Theme`**
- **UX-DR1** — Surface color stack: `void #0a0c0f`, `surface-0 #0f1216`, `surface-1 #14181d`, `surface-2 #1a1f26`, `surface-3 #222831`, `surface-4 #2c333d`. Adjacent elements never skip >1 elevation level.
- **UX-DR2** — Line/edge tokens: `line #2a3038`, `line-strong #3a424d`, `edge-light #4a5562` (cel-shade top-edge highlight base).
- **UX-DR3** — Text-tier tokens locked to WCAG AA on `surface-1`: `text-hi #eef2f6`, `text-mid #aeb7c2`, `text-lo #727c88`, `text-disabled #4b545f`. `text-lo` never on `surface-3`+.
- **UX-DR4** — Teal-default accent set + three runtime-switchable accents (teal/amber/violet): `accent`, `accent-bright`, `accent-dim`, `accent-ink`, `accent-glow`, `accent-wash` (oklch values per DESIGN.md). Accent marks active/primary/selected states only — never decoration. ⚠ *Godot accent-switch mechanism unspecified (swap Theme? override named colors? shader uniform?) — decide in implementation.*
- **UX-DR5** — Semantic tokens: `ok`, `warn`, `danger`, `info`. `danger` reserved for destructive/irreversible actions; semantic color never used color-alone (always icon/label).
- **UX-DR6** — 8 **Okabe-Ito colorblind-safe team colors** (`team-1`…`team-8`) reserved for world units/minimap/team identity **only** — never UI chrome.
- **UX-DR7** — Typography roles: `font-display` Chakra Petch (headings/buttons/tabs/labels/tags; uppercase + tracking), `font-ui` Space Grotesk (body, 1.45 line-height), `font-mono` JetBrains Mono (all live numbers; tabular-nums always).
- **UX-DR8** — 1.250-ratio type scale: `t-2xs 11` … `t-5xl 72` (px), plus `.eyebrow` 11px/uppercase/0.22em.
- **UX-DR9** — Chamfer-cut tokens (45° clip, faceted StyleBox): `cut 8`, `cut-sm 5`, `cut-lg 14`. Effective radius ~0 everywhere except `.kbd`. ⚠ *StyleBox mechanism (Flat vs Texture vs NinePatch vs shader) is an open architecture decision — blocks chamfer-dependent component stories until chosen.*
- **UX-DR10** — 8-pt-ish spacing scale `s1 4` … `s8 64` (px). Control padding ≈ s2/s3; panel ≈ s4/s5; section gaps ≈ s5/s6.
- **UX-DR11** — Elevation/shadow tokens: `shadow-1` (resting), `shadow-2` (raised), `shadow-pop` (menus/tooltips/dialogs). Depth = surface step + cel-shade edge + shadow, not blur.
- **UX-DR12** — Map all DESIGN.md tokens **1:1 into a single Godot `Theme` resource** so the HUD and every editor surface inherit one source of truth.

**Component kit** *(each is a reusable component — build all of them)*
- **UX-DR13** — `Panel` (faceted surface-1, cel-shade hairline border, shadow-1; variants `--2`/`--flat`/`--accent`).
- **UX-DR14** — `btn` (chamfered `cut-sm`, Chakra Petch uppercase; variants primary/secondary/ghost/danger; sizes sm/lg/block; active depresses 1px).
- **UX-DR15** — `icon-btn` (36×36 faceted, 18px glyph; `is-active` = accent fill).
- **UX-DR16** — `kbd` keyboard-glyph (JetBrains Mono, surface-3, 2px bottom border, radius 3px — the **only** radiused element). Used for every shortcut hint on-screen and in tooltips.
- **UX-DR17** — `chip` (faceted readout, surface-2, inset line, holds a `.num`).
- **UX-DR18** — `readout` (top-bar resource style: 22px faceted icon + mono tabular value + uppercase label; updates live from sim arrays).
- **UX-DR19** — `tag` (uppercase pill, cut 3px; variants `--lock`/`--ok`/`--accent`/`--danger`).
- **UX-DR20** — `progress` (8px track, accent gradient + glow; variants `--ok`/`--xp` striped).
- **UX-DR21** — `slider` (6px track, faceted accent thumb, paired `.num-input`; advanced mode reveals min/max).
- **UX-DR22** — `input` (surface-3, inset line, chamfered; focus = accent ring + wash; `.select` chevron variant; uppercase field label).
- **UX-DR23** — `menu` (popover surface-2, shadow-pop; hover = surface-4, `is-active` = accent).
- **UX-DR24** — `tabs` (underline accent or `--boxed`; segment = pill group used as the **Simple/Advanced disclosure toggle**).
- **UX-DR25** — `list-row` (surface-1 inset, chamfered; `is-selected` = accent ring + wash; `is-locked` = 0.6 opacity, non-interactive; single-select).
- **UX-DR26** — `tooltip` (`tip__pop`/`.f-tip`: above target, surface-3, shadow-pop; term = accent). Implements the **NFR-2 tooltip-on-every-control** mandate.
- **UX-DR27** — `dialog` (scrim blur + centered faceted `cut-lg`, head/body/foot; traps focus; requires explicit confirm for destructive acts).
- **UX-DR28** — `toast` (faceted, left accent bar; variants `--danger`/`--warn`/`--ok`; includes `banner-stall` = the multiplayer stall indicator).
- **UX-DR29** — `spinner` (transmute spinner: 3 layered SVGs; sizes sm/default/lg; gates on `prefers-reduced-motion`).
- **UX-DR30** — `mark` (Chimera Seal alchemical sigil; `.triad` heavy-stroke variant ≤24px). **The only alchemy motif that ships.**
- **UX-DR31** — `switch` (flips a boolean and **reveals dependent fields inline**, e.g. Promote-to-Hero reveals leveling fields).
- **UX-DR32** — `num-input` (mono, right-aligned, paired with sliders).

**Visual standardization**
- **UX-DR33** — All **7 gap surfaces** (Unit Card Editor, Ability Editor, Tech-Tree editor, Faction Definer wizard, Trigger T2/T3 editors, hero-picker, custom-UI builder) compose from the existing kit — **no new primitives** unless a gap surface proves one is missing (and it must be logged).
- **UX-DR34** — Render **all live numbers** (resources, supply, tick, hash, stats, slider values, hotkeys) in `font-mono` with tabular-nums (no jitter).
- **UX-DR35** — Use the chamfer language everywhere (faceted StyleBox, no `corner_radius`); `.kbd` radius 3px is the sole exception.
- **UX-DR36** — Non-diegetic flat HUD: nothing rendered in-world, HUD floats over the 3D battlefield; hidden during Edit where it would mislead, hidden during Play for editor chrome.
- **UX-DR37** — Provide a **warm-paper light theme** as a first-class peer to the cool-dark default. ⚠ *No light-theme token values exist in DESIGN.md — a full second token set must be defined; story is blocked pending values.*
- **UX-DR38** — Do **not** reintroduce the shelved bio-alchemy "Transmutation Lab" retheme; the Chimera Seal is the agreed extent of the alchemical motif.

**Accessibility** *(realizes FR-51 baseline)*
- **UX-DR39** — WCAG AA contrast floor for text tiers on dark surfaces + a **Text-contrast-boost** setting guaranteeing AA.
- **UX-DR40** — Colorblind-safe team identity: Okabe-Ito palette + **team meaning never by color alone** (every team also carries a glyph + label, e.g. "P1 ◆"); optional deuteranopia/protanopia/tritanopia filters.
- **UX-DR41** — Fully remappable keyboard bindings with reset-to-defaults (Settings → Controls).
- **UX-DR42** — UI scaling **80–150%** across 1080p/1440p/4K (scales all HUD panels + text).
- **UX-DR43** — Subtitles for briefings and unit voice, with S/M/L size options.
- **UX-DR44** — Honor **`prefers-reduced-motion`** via an accessibility setting that disables/reduces motion (130ms transitions, button depress, toggle snap, camera shake). ⚠ *Reduced-state target values not specified (0ms vs reduced? shake off vs attenuated?) — define per effect.*
- **UX-DR45** — Keyboard focus must **reveal tooltips** (not only mouse hover); dialogs trap focus for keyboard nav.

**Responsive / form factor**
- **UX-DR46** — Single PC-desktop form factor: adaptation via **UI scale + resolution only**, not layout reflow. Targets 1080p/1440p/4K. No web/mobile/console/VR.
- **UX-DR47** — HUD anchors to screen edges leaving the battlefield center clear; editor panels dock to left/right rails keeping the 3D viewport the protagonist.
- **UX-DR48** — In-game custom-UI canvas authors against a **16:9 safe-area with 9-point anchors + offsets**.
- **UX-DR49** — Dedicated/headless server renders no UI (detected via `DisplayServer.GetName()=="headless"`).

**Interaction patterns**
- **UX-DR50** — Mechanical motion timing: 130ms transitions, `cubic-bezier(0.4,0.1,0.2,1)`; buttons depress 1px; toggles snap.
- **UX-DR51** — Combat feedback: pooled hit-flashes (orange melee / yellow ranged / red splash / white kill) + brief camera shake on kills (per as-built `CombatFeedbackBridge`).
- **UX-DR52** — Construction/generation feedback: growing progress bar + glow for building; transmute spinner for any async "working" state (AI gen labeled "Transmuting…", scenario loads).
- **UX-DR53** — Tooltip interaction: short-hover reveal on every field/button/panel, dismiss on leave; keyboard-focus also reveals (NFR-2).
- **UX-DR54** — Simple/Advanced disclosure: dock segment toggles `.is-advanced`, revealing extra fields, slider min/max, and a **raw-JSON escape hatch**. Simple default; Advanced one click away on every authoring surface (NFR-2 AC4).
- **UX-DR55** — Validation feedback: valid/bound badges on editor surfaces; inline errors block save/playtest (FR-7); tech nodes show "locked" until prerequisites met.
- **UX-DR56** — Placement interaction: ghost preview follows cursor, `G` grid-snap; left-click places, right-click/Esc cancels.
- **UX-DR57** — Graph editing: drag a node's out-port to another node to wire a dependency (Tech-Tree prerequisites + Trigger node graph).
- **UX-DR58** — Direct-manipulation UI authoring: drag widgets onto the 16:9 canvas, snap to safe-area/anchors, bind to `{variables}`; buttons fire triggers on click.
- **UX-DR59** — Undo/redo everywhere in the editor (`Ctrl+Z`/`Ctrl+Y`).
- **UX-DR60** — Context-sensitive controls strip: Edit shortcuts in Edit, command shortcuts in Play, placement hints during build/placement.
- **UX-DR61** — Selection operates on **Player-faction units only** (click/box-select). *(EXPERIENCE.md interaction constraint — was missing from the first pass.)*
- **UX-DR62** — **Edit↔Play central duality**: `F5` toggles; Edit shows authoring chrome, Play hides it and runs the sim. NFR-1 ACs: no app restart / no export step (AC1); round-trip ≤2s on target hardware (AC2); a unit/stat/trigger edit is observably changed on the next Play without reload (AC3). ⚠ *"Returning to Edit resets match state" is under-specified — define the reset scope (positions, resources, fired triggers, playtest hero XP) given persistence elsewhere.*
- **UX-DR63** — **Editor-invisible-to-Commanders** (NFR-3 ACs): a Play/Skirmish/MP-only user never sees an authoring surface (AC1); all creation entry points are opt-in via Create or the Edit toggle (AC2); no authoring control reachable by accident from the in-match HUD (AC3).
- **UX-DR64** — Multiplayer state — **split into independently-testable gates**: (a) lobby ready/not-ready/AI states; (b) version-match ok/mismatch (gates Start); (c) content-synced (independently gates Start); (d) in-game **stall banner** for a lagging peer; (e) **desync → terminal HALT with a clear user-facing message** (distinct from silent drift). *(Was bundled in the first pass; the desync-halt behavior is its own requirement.)*
- **UX-DR65** — Voice/microcopy standard: address the player as "Commander"; confident/terse/mechanical; button verbs (Deploy·Publish·Rebind·Generate); mono status strings ("Version match · #a3f9c1e", "All content synced"); tooltips teach never scold (bold term + one plain sentence). Includes the ownership line **"you own what you make"** (FR-38a surfacing).
- **UX-DR66** — Default keybindings to implement (all remappable): Camera `WASD` pan / scroll zoom / MMB orbit / Space center / `E` edge-scroll; Mode `F5`; Commands `M` Move, `Q` Attack-Move (A reserved for pan), `S` Stop, `H` Hold, `P` Patrol; Selection/groups click/box, `Ctrl+1–9` assign, `1–9` recall, `Shift+#` add, `F2` select army; Build/editor `B`/`U`/`T`/`G`/`N`/`O`/`L`/`M`/`Y`, `Ctrl+Z/Y`.

**Screen flows / information architecture**
- **UX-DR67** — Title Screen: grand Chimera seal over a low-poly vista; nav Play · Create · Browse · Settings · Quit; footer version/build + patch-notes; tagline "Build the game. Then play it."
- **UX-DR68** — Mode Select ("Where to, Commander?"): Skirmish vs AI (1–8, offline), Multiplayer (ranked/LAN/private + live online count), Campaign & Tutorial (progress N/12), Create, My Content; header breadcrumb + account chip + Settings.
- **UX-DR69** — Lobby/Matchmaking: scenario header + version-match hash check (FR-39/40 determinism gate); player slots (host/peer/AI, faction select, colorblind color dots + glyphs, ready pills, ping); lobby chat (All/Team); footer (X of Y ready, "All content synced", Toggle Ready, Start disabled until all ready).
- **UX-DR70** — Creation Suite shell: top toolbar (brand, prominent Edit/Play toggle, tool tabs, undo/redo, Save, Publish); left palette (Select/Terrain/Entities/Resources/Triggers/Ability/Faction Definer/Tech Tree/AI Generate with hotkey tooltips); center 3D world or graph canvas; right dock active editor panel with Simple/Advanced toggle.
- **UX-DR71** — HUD information hierarchy (as-built): top-left status line (FPS·mode·tick·sim-hash) → unit counts → resource strip (per-faction, mono tabular) → context controls strip (bottom) → minimap (bottom-right) → command card → selection feedback (ring + HP); stall banner (top-center) only when a peer is behind.
- **UX-DR72** — Content Browser (mod.io, FR-37): browse/search/tag/sort/subscribe/rate, reachable from both Commander and Creator branches.
- **UX-DR73** — Settings overlay: tabs Gameplay/Graphics/Audio/Controls/Accessibility, reachable from both branches.
- **UX-DR74** — Tech-Tree editor (FR-13/14): tier-laned graph; drag out-port→building sets a prerequisite; right-dock inspector edits building stats; runtime gates production; building defs reuse the Unit-Card pattern.
- **UX-DR75** — Hero Save/Load picker (FR-7d/e): creator-enabled per scenario; slot cards (portrait, level, XP, signature ability, faction); Deploy/Overwrite/Delete with confirm; multiple heroes per player; server-validated online.
- **UX-DR76** — Custom runtime UI builder (FR-26): widget palette (Label/Counter/Bar/Button/Timer/Image/Panel) → drag onto 16:9 canvas; per-widget inspector ({variable} binding, 9-point anchor + offsets, style, trigger-driven visibility); buttons fire triggers.
- **UX-DR77** — Unit Card Editor (FR-2): consolidated single-entity editing (WC3 model) — model, stats, abilities, economy, hero in one panel; Promote-to-Hero switch reveals leveling fields + ultimate selection.
- **UX-DR78** — Ability Editor (FR-8–12) including the **passive-ability path** (FR-9).
- **UX-DR79** — Trigger list + node graph (FR-23–28): typed/scoped variables, arithmetic/boolean expressions, collections, loops, custom events (FR-24/25).
- **UX-DR80** — Faction Definer wizard (FR-17): name/color → roster → buildings & tech → start → AI preset; output instantly selectable in skirmish.

**Key user journeys** *(map to PRD §2.5 UJ-1…UJ-6 — added per the UX completeness check; reconcile UJ numbering against the PRD at finalize)*
- **UX-DR81** — "Your First Scenario" guided onboarding for first-time creators (NFR-2 AC2); a first-timer produces a basic playable scenario in **<15 min** with no manual or JSON (NFR-2 AC3).
- **UX-DR82** — **Unit-authoring journey (UJ-2, "Kai")**: open Unit Card Editor from a template → tune Combat/Economy sliders with explaining tooltips → Promote-to-Hero + pick ultimate → Play and the retuned unit fights immediately, raw JSON hidden throughout.
- **UX-DR83** — **Fast-loop journey (UJ-3)**: mid-build, repeatedly `F5` Play → see live → `F5` Edit with no perceptible build step (the repeated round-trip as an experience, distinct from the NFR-1 mechanism).
- **UX-DR84** — **LAN lockstep journey (UJ, two friends; FR-39/40)**: a full match on two machines with the climax beat **"300+ ticks with checksums in lockstep, zero desync"** as a concrete, testable target.

**Implementation directive**
- **UX-DR85** — Runtime UI is built with **Godot 4.6.2 Control nodes** (EXPERIENCE.md Foundation) — an explicit, story-relevant tech constraint for every UI surface above. *(Note AR-1 bumps the engine to 4.6.3; Control-node usage is unaffected.)*

---

### FR Coverage Map

*Every FR maps to exactly one epic. (NFRs and the AR-* technical requirements are cross-cutting and woven into the relevant epic's stories; they are not single-epic-owned.)*

| FR | Epic | Capability |
|----|------|-----------|
| FR-1 | Epic 3 | Create/edit/duplicate/delete units in-app (no JSON) |
| FR-2 | Epic 3 | Unit Card Editor — single consolidated panel |
| FR-3 | Epic 3 | Assign + preview a 3D model/placeholder |
| FR-4 | Epic 3 | Compose from 6 archetypes (composition, no subclassing) |
| FR-5 | Epic 3 | Designate Hero — leveling/XP/signature & ultimate |
| FR-6 | Epic 3 | Simple (presets) + advanced (all fields) modes |
| FR-7 | Epic 3 | Inline authoring validation before save/playtest |
| FR-7a | Epic 3 | Persistence manifest (which attrs carry forward) |
| FR-7b | Epic 3 | Load profile as deterministic init state (MP-safe) |
| FR-7c | Epic 9 | Server-side profile storage/validation (online) |
| FR-7d | Epic 3 | Save/Load Interface (hero-picker menu) |
| FR-7e | Epic 3 | Load/save/overwrite saved heroes (multi per player) |
| FR-8 | Epic 2 | Author active abilities (targeting/cost/cooldown/effects) |
| FR-9 | Epic 2 | Author passive abilities (auras/on-hit/modifiers) |
| FR-10 | Epic 2 | Compose abilities from multiple effect primitives |
| FR-11 | Epic 2 | Attach abilities to units/heroes → command card |
| FR-12 | Epic 2 | Abilities data-driven/deterministic/validatable |
| FR-12a | Epic 2 | Combat-feedback profile ("juice"), override per unit |
| FR-13 | Epic 4 | Author building definitions in-app |
| FR-14 | Epic 4 | Visual tech-tree editor + runtime production gating |
| FR-15 | Epic 4 | Data-driven resource set (beyond Ore+Crystal) |
| FR-16 | Epic 4 | Per-scenario supply/cap model |
| FR-17 | Epic 5 | Faction Definer guided multi-step wizard |
| FR-18 | Epic 5 | Assign AI preset to a faction |
| FR-19 | Epic 5 | Faction instantly selectable in playtest/skirmish/MP |
| FR-20 | Epic 5 | Iron Pact upgraded reskin → genuinely asymmetric |
| FR-21 | Epic 6 | Terrain sculpt/paint with persistent textures |
| FR-22 | Epic 6 | Place entities/start/resources + set win conditions |
| FR-23 | Epic 7 | ECA triggers in-app (verify to ship bar) |
| FR-24 | Epic 7 | DSL variables/arithmetic/arrays/loops/timers |
| FR-25 | Epic 7 | Custom events raised from triggers |
| FR-26 | Epic 7 | Custom runtime UI elements driven by triggers |
| FR-27 | Epic 7 | All DSL constructs deterministic + server-validatable |
| FR-28 | Epic 7 | Four interoperating tiers (T1/T2/T3/T4) on one DSL |
| FR-29 | Epic 8 | Select/configure LLM provider via settings (no hardcoded keys) |
| FR-30 | Epic 8 | NL trigger generation (verify, extend to new DSL) |
| FR-31 | Epic 8 | Map generation + relaxed/parameterized clamps |
| FR-32 | Epic 8 | Generate unit/ability/hero/faction drafts as editable data |
| FR-33 | Epic 8 | AI balance analysis with actionable suggestions |
| FR-34 | Epic 8 | Graceful degradation with no provider/key |
| FR-35 | Epic 9 | Package `.chimera.zip` + publish to mod.io in-app |
| FR-36 | Epic 9 | Proof-of-play gate before publishing |
| FR-37 | Epic 9 | Browse/search/filter/sort/subscribe/rate content |
| FR-38 | Epic 9 | Content-hashed + integrity-verified on download |
| FR-38a | Epic 9 | Creators retain IP ownership (surfaced at publish) |
| FR-39 | Epic 1 | LAN determinism test passes — 300+ ticks, zero desync |
| FR-40 | Epic 9 | Matchmake/lobby/chat + viewable replays, up to 8 |
| FR-41 | Epic 9 | Adaptive input delay verifiably adjusts on real nets |
| FR-42 | Epic 10 | Two shipped factions balanced (45–55% win rate) |
| FR-43 | Epic 10 | Solo skirmish vs AI across difficulties (verify) |
| FR-44 | Epic 1 | Automated test suite, headless-runnable |
| FR-45 | Epic 1 | Smoke-test the 4 unverified systems |
| FR-46 | Epic 10 | Performance pass (500–2,000 units @ 60/30) |
| FR-47 | Epic 1 | Determinism CI regression guard (replay/checksum) |
| FR-48 | Epic 10 | Audio assets present + wired |
| FR-49 | Epic 10 | Iron Pact placeholder GLBs → real art |
| FR-49a | Epic 10 | Art-style consistency layer |
| FR-50 | Epic 10 | Linux export builds + runs |
| FR-51 | Epic 10 | Accessibility 1.0 baseline |
| FR-52 | Epic 10 | Ship to Steam + DRM-free channel |

## Epic List

### Epic 1: Trustworthy Foundation & Desync-Free Multiplayer
Multiplayer provably stays in sync across two machines (the #1 ship risk, retired), the deterministic sim is covered by automated tests and CI-regression-guarded, and the ~2,200-LOC `MainScene` god-object is decomposed into a Godot-free sim spine ready to absorb six editors. *(PRD M0+M1; arch foundational program — strangler refactor, SimRng, generalized SimChecksum, fail-closed ScenarioValidator + `Validated<T>`, two-tier testing, banned-API/AOT analyzers, cross-platform determinism gate, engine bump → 4.6.3, DamageMatrix→JSON. Realizes NFR-4.)*
**FRs covered:** FR-39, FR-44, FR-45, FR-47

### Epic 2: Living Combat — Effect Engine, Modifiers & Ability Authoring
The sim engine can express buffs, auras, DoT/HoT, summons, and multi-effect abilities deterministically; creators author active and passive abilities in-app; and the showcase factions' signature mechanics finally *run* with satisfying combat feedback. *(PRD M2; arch D1 — single Effect-Graph surface + Modifier subsystem + Energy/Mana SoA; unblocks the combat-reachability gaps the FMA factions need: per-unit production-selection UI, Air building/category, anti-air/ground TargetFilter, anti-building combat, worker-cast path, crystal-spend wiring; `CombatFeedbackProfile`.)*
**FRs covered:** FR-8, FR-9, FR-10, FR-11, FR-12, FR-12a

### Epic 3: Author Units & Heroes (incl. Save/Load)
A creator builds units and heroes in one consolidated Unit Card Editor with no JSON, and players save/load hero progression between custom games via a hero-picker interface. *(PRD M2; arch D4 offline hero-persistence rail. The shared **UI design-system Godot Theme + reusable component kit** (UX-DR1–32) is delivered here and reused by every later editor surface. FR-7c online rail → Epic 9.)*
**FRs covered:** FR-1, FR-2, FR-3, FR-4, FR-5, FR-6, FR-7, FR-7a, FR-7b, FR-7d, FR-7e

### Epic 4: Author Buildings, Tech Trees & Economy
A creator authors building definitions, drag-builds a visual tech tree that gates production at runtime, and configures resources and the supply model as data — closing the hardcoded-economy gap so creators aren't locked to Ore+Crystal. *(PRD M2; arch D3 data-drive tables — N-resource registry, tech-tree prerequisites, supply/cap.)*
**FRs covered:** FR-13, FR-14, FR-15, FR-16

### Epic 5: Faction Definer & the Asymmetric Showcase Factions
A creator assembles authored units/heroes/buildings/tech into a complete, playable faction through a guided wizard, and the two showcase factions become genuinely asymmetric with their FMA identities landed and playtest-validated. *(PRD M2; consumes Epics 2–4; lands revised FMA stats, `display_names`, and themed mesh filenames into the alpha/beta faction JSON.)*
**FRs covered:** FR-17, FR-18, FR-19, FR-20

### Epic 6: Map & Terrain Editor
A creator sculpts and texture-paints terrain with textures that persist correctly on save/load (fixing the current procedural-texture defect), and places entities, start positions, resource nodes, and win conditions to a ship-quality bar. *(PRD §4.5 polish; largely verification + the terrain-persistence fix.)*
**FRs covered:** FR-21, FR-22

### Epic 7: Rich Trigger DSL & Custom Runtime UI
"Build any game": the trigger DSL gains variables, arithmetic, collections, loops, timers, and custom events, plus trigger-driven custom runtime UI — authored across four interoperating tiers (preset / ECA / visual node-graph / natural-language) on one underlying representation, all deterministic and server-validatable. *(PRD M3; arch D2 typed event/dataflow graph IR containing D1 effect subgraphs + bounded-loop model + two-rail custom UI; arch D3 serialization contract — single ContentLoader, `NodeBase` converter, canonical-model hash, versioning/migration.)*
**FRs covered:** FR-23, FR-24, FR-25, FR-26, FR-27, FR-28

### Epic 8: AI-Assisted Creation
An AI collaborator is available across every editor — provider configuration (OpenRouter / Claude / local Ollama), generation of triggers, maps, units, abilities, heroes and factions as editable data, and faction/scenario balance analysis — degrading gracefully to a fully-usable manual suite when no provider is available. *(PRD M4; arch D6 hand-rolled `ILLMProvider` + `ISecretStore`; relax the AI-gen validator clamps for non-RTS scenario types; all AI output passes the same content-validation gate.)*
**FRs covered:** FR-29, FR-30, FR-31, FR-32, FR-33, FR-34

### Epic 9: Share, Discover & Multiplayer at Scale
Creators package and publish scenarios to mod.io (gated by proof-of-play, with IP ownership surfaced), players browse/subscribe/rate them, and multiplayer scales to a verified ≤4 players (8 as a fast-follow) with matchmaking, parties, viewable/shareable replays, and server-validated online hero persistence. *(PRD M5; arch D5 — invert the pure relay into stateful server authority with canonical multi-hash handshake, majority-vote desync attribution, replay v2; arch D4 online hero rail = FR-7c.)*
**FRs covered:** FR-35, FR-36, FR-37, FR-38, FR-38a, FR-40, FR-41, FR-7c

### Epic 10: Release Readiness — Content, Balance, Performance & Ship
The concrete finishing work to ship: audio assets wired, Iron Pact's placeholder art replaced and held coherent by an art-style consistency layer, the performance pass, final faction balance, the Linux export, the accessibility baseline, and release to both Steam and a direct DRM-free channel. *(PRD M6; arch P1 art-style layer; closes the two primary 1.0 quality gates alongside Epics 1 & 5.)*
**FRs covered:** FR-42, FR-43, FR-46, FR-48, FR-49, FR-49a, FR-50, FR-51, FR-52

---

# Epic Details — Stories & Acceptance Criteria

## Epic 1: Trustworthy Foundation & Desync-Free Multiplayer

_Multiplayer provably stays in sync across two machines, the deterministic sim is test-covered and CI-regression-guarded, and the MainScene god-object is decomposed into a Godot-free sim spine ready to absorb six editors._

**Sequencing note:** Epic structured along the architecture's golden-checksum-gated strangler (migration Steps 0-4 of D1 + the M1 testing/determinism/MP/decomposition spine). Effects/Modifier/Trigger steps 5-9 are deliberately deferred to later epics. Stories are ordered so each is completable using only earlier Epic-1 stories. The hard milestone gate M1 (1.1-1.10 green) must land before any later epic's engine work; 1.9 (FR-39 LAN green) is the #1 ship-risk climax and depends on the full determinism spine + server quorum being in place first. Brownfield: 1.3-1.6 are "fix/extend/harden existing systems" (SimChecksum, FixedPoint, DamageMatrix all exist); 1.1, 1.5, 1.7, 1.8 are genuinely net-new (test project, SimRng, ScenarioValidator, SimulationHost/Applier). Floor on 8-player is built into the registry/checksum/enum work (FACTION_ARRAY_SIZE=9, PLAYER_COUNT=8) even though ship ceiling is 4-now/8-fast-follow.

**Coverage note (added by review):** **AR-4** (net-new `ILogSink` deterministic logging seam so sim/Godot-free code never calls `GD.Print`/Console; relocate `StressTest.cs` out of `src/Core`) is folded into the Godot-free sim-spine story (**1.8 SimulationHost/ScenarioApplier**) — it is a headless-server/test prerequisite. **AR-41** (telemetry posture: **NO analytics in 1.0**; dev-only diagnostics via logs + `.chmr` replay capture; an opt-in crash/desync report bundling the `.chmr` + checksum log) is folded into the CI/regression-guard story (**1.10**); per-system sub-checksums + a replay-diff tool are homed as a structural fast-follow, not 1.0-blocking.

### Story 1.1: Engine bump 4.6.3 + Godot-free Tier-1 xUnit test project scaffold

As a solo developer hardening Project Chimera for multiplayer,
I want the engine bumped to 4.6.3 and a net-new Godot-free xUnit test project (ProjectChimera.Sim.Tests) that compiles and runs the existing pure-sim source headless,
So that I have a fast, Godot-independent place to assert determinism before I touch any sim code.

**Acceptance Criteria:**

**Given** the project on Godot 4.6.2 **When** I bump the editor to 4.6.3 and open it **Then** the project builds and runs, and both the godot_mcp and terrain_3d addons still connect and report ready **And** no new build errors or addon load failures appear in the editor log

**Given** the pure-sim source under src/Core, Combat, Economy, Navigation, Effects, Dsl **When** I create ProjectChimera.Sim.Tests as a Godot-SDK-free .NET 8 xUnit project that references that source (no Godot SDK, no presentation) **Then** the test project compiles and `dotnet test` runs headless with zero reference to Godot types **And** a trivial smoke test (e.g. Fixed arithmetic round-trips) passes **And** test-only NuGet deps (xUnit, Nakama 3.13.0 pin if needed) live only in this project, never in godot.csproj

**Given** any sim source file **When** the test project is built **Then** no `using Godot` is reachable from the test assembly's sim references (a compile-time proof that the sim layer is Godot-free at the test boundary)

_Covers: FR-44, AR-35, AR-1, AR-2. Depends on: — (none / earlier epics only)._

> Net-new (zero tests today). AR-1 engine bump, AR-2 Nakama 3.13.0 pin / test-only deps, AR-35 Tier-1 project foundation. This is migration Step 0 — nothing else can be checksum-gated until this exists.

### Story 1.2: Golden-checksum replay harness pinning current sim behavior

As a solo developer protecting determinism during refactors,
I want a golden-checksum harness in the Tier-1 project that runs a fixed scenario N ticks through SimulationLoop and records/asserts the SimChecksum sequence against a committed golden file,
So that any future change that alters sim behavior is caught immediately as a checksum diff.

**Acceptance Criteria:**

**Given** a committed fixed scenario and a recorded golden checksum sequence **When** the harness runs the scenario for 300+ ticks twice in one process **Then** both runs produce byte-identical checksum sequences and both match the committed golden file

**Given** the same scenario run in two separate process invocations **When** checksum sequences are compared **Then** they are byte-identical (no static/mutable-state leakage between runs)

**Given** a deliberate one-tick perturbation injected into a unit's Fixed health **When** the harness runs **Then** the test FAILS with a located tick + expected-vs-actual checksum, proving the guard detects drift

_Covers: FR-44, FR-47, AR-35, AR-15. Depends on: 1.1._

> Migration Step 1 — pins current behavior before any change. Establishes the replay regression harness that AR-47/Story 1.10 later wires into CI. Uses today's SimChecksum as-is; 1.3 then widens coverage and re-baselines the golden once.

### Story 1.3a: FactionRegistry — localize all faction-count knowledge

As a solo developer hardening the sim for N players,
I want a FactionRegistry that centralizes PLAYER_COUNT=8, FACTION_ARRAY_SIZE=9 (incl. Neutral), and the one (Faction)(slot+1) cast, replacing the scattered 2-faction hardcodes,
So that every checksum, slot loop, and new subsystem iterates factions one correct way (never a bare FACTION_COUNT).

**Acceptance Criteria:**

**Given** FactionRegistry with PLAYER_COUNT=8 and FACTION_ARRAY_SIZE=9 (incl Neutral) **When** checksum and any new slot loop iterate factions **Then** they iterate active factions ascending via the registry and never use a bare FACTION_COUNT constant **And** a 3–4-faction golden scenario exercises the span path

_Covers: AR-3. Depends on: 1.2._

> Split from former 1.3 (FactionRegistry is a separable concern from the checksum/algo-version work). AR-3 localizes faction-count knowledge so 1.3b and Epic 5's 5.1 build on one source of truth. Disambiguation: as-built FACTION_COUNT=5 is enum cardinality incl. Neutral — new slot loops use PLAYER_COUNT.

### Story 1.3b: Generalize SimChecksum coverage + coverage-guard + ScenarioDirector locale fix

As a solo developer closing the #1 latent desync hole,
I want SimChecksum widened from Ore[P1]/Ore[P2] to Ore+Crystal+SupplyUsed+SupplyCap (and every per-faction store) for ALL active factions in ascending slot order, a guard test that fails if a new per-faction array is added without coverage, and the ScenarioDirector float-threshold leak removed,
So that the checksum reflects full sim truth and desync detection cannot silently miss divergence.

**Acceptance Criteria:**

**Given** the current Ore-only SimChecksum **When** I extend it to hash Crystal, SupplyUsed, SupplyCap and every per-faction store across all active factions in ascending faction-slot order (via the 1.3a registry) and bump checksum_algo_version exactly once **Then** the golden harness from 1.2 is re-baselined to the new algo version and passes, and old replays under the prior algo version do not spuriously desync

**Given** the new SimChecksumCoverageGuardTest **When** a per-faction array is added to a store without being hashed **Then** the guard test FAILS, naming the uncovered array

**Given** the ScenarioDirector threshold loop that today uses ore.ToFloat()/ToString("F2") **When** it is converted to Fixed.Raw integer compares **Then** the golden checksum is byte-identical to the pre-conversion baseline (no behavior change, only locale/float leak removed)

_Covers: FR-39, FR-44, AR-15, AR-16. Depends on: 1.3a._

> Split from former 1.3. Brownfield fix to SimChecksum.cs (Ore[P1]/Ore[P2] only at :53-54) and ScenarioDirector.cs (:165-172 float threshold). Bumps checksum_algo_version once. Partially addresses AR-16 (locale leak); the other two AR-16 nondeterminisms land in 1.4.

### Story 1.4: Fixed end-to-end: FixedJsonConverter quantization + nondeterminism negative tests

As a solo developer guaranteeing cross-platform bit-identical sim,
I want a single FixedJsonConverter that quantizes at deserialize and rejects NaN/Inf, Fixed.FromFloat allow-listed only inside the converter, zero float in the 30Hz tick, and negative tests for the three latent nondeterminisms (in-tick Fixed->float->string round-trip, unstable Array.Sort, Dictionary-iteration-order),
So that there is exactly one quantization boundary and no float or unordered-collection can leak into gameplay math.

**Acceptance Criteria:**

**Given** JSON content with a fractional numeric value **When** it is deserialized via FixedJsonConverter **Then** it is quantized to Fixed at the parse boundary, and any NaN/Inf is rejected with a located error

**Given** the sim tick path **When** a banned-API/float audit runs over the 30Hz tick code **Then** no float gameplay math and no Fixed.FromFloat call exist outside the converter allow-list

**Given** a negative test for an unstable Array.Sort over equal keys **When** two runs sort the same set **Then** the test asserts a stable ordering (e.g. ascending id tiebreak) and FAILS if an unstable sort is reintroduced

**Given** a negative test exercising Dictionary-backed timers/variables and an in-tick Fixed->float->string round-trip **When** the scenario runs twice **Then** checksums are byte-identical, and the test FAILS if iteration order or the float round-trip is reintroduced into the tick

_Covers: FR-44, FR-47, AR-14, AR-16. Depends on: 1.3._

> Brownfield hardening of FixedPoint.cs and the deserialize path. AR-14 single quantization boundary; AR-16 the three latent nondeterminisms each gated by a negative test. Golden checksum must stay byte-identical (or be re-baselined intentionally if a sort tiebreak changes output).

### Story 1.5: Seeded deterministic SimRng folded into checksum + replay

As a solo developer enabling random effects without breaking determinism,
I want a net-new Godot-free SimRng (deterministic PCG/xorshift over int/Fixed) threaded through systems by ref, its state folded into SimChecksum and ReplayRecorder/Player, with the validator forbidding any random effect until SimRng is present,
So that randomness is reproducible across machines and replays, and no non-deterministic Random can enter the sim.

**Acceptance Criteria:**

**Given** a SimRng seeded with a fixed seed **When** the same sequence of draws is requested in two separate runs **Then** the outputs are bit-identical and use only integer/Fixed math (no System.Random, no float)

**Given** SimRng state **When** a tick completes **Then** the RNG state is folded into SimChecksum and recorded/restored by ReplayRecorder/Player so a replay reproduces the exact RNG stream

**Given** a scenario that records a checksum sequence with RNG-driven behavior **When** the golden harness runs it twice and across a replay **Then** checksum sequences are byte-identical

**Given** the current absence of SimRng-gated random effects **When** a definition declares a random effect before SimRng is wired **Then** validation rejects it (forbidden-until-SimRng rule)

_Covers: FR-39, FR-44, FR-47, AR-13, AR-15. Depends on: 1.4._

> Net-new (SimRng does not exist; grep shows only System.Random in StressTest/AudioManager). Migration Step 2. Hard prerequisite for later random effects/Modifier patterns; folded into checksum + replay now per AR-13. Re-baselines golden once RNG state enters the hash.

### Story 1.6: Data-drive DamageMatrix to damage_table.json (5x6 with Hero) + DamageResolver

As a solo developer removing hardcoded balance constants,
I want the hardcoded 4x5 DamageMatrix lifted into resources/data/damage_table.json (extended to 5x6 with Hero damage/armor types), loaded and quantized via FixedJsonConverter at scenario-apply, plus a DamageResolver.Apply(in ctx, amount, type) that unifies the formula + death/RecordKill/event sequence across the three verified call sites,
So that later authoring builds on data not constants, and there is one damage code path proven checksum-identical.

**Acceptance Criteria:**

**Given** the hardcoded DamageMatrix._table (4 damage x 5 armor) **When** it is moved to damage_table.json extended to 5x6 (adding Hero damage type and Hero armor type) and loaded via FixedJsonConverter **Then** DamageType/ArmorType enums remain stable keys and the loaded table matches the prior values bit-for-bit for the original 4x5 cells

**Given** DamageResolver.Apply(in ctx, amount, type) re-pointed from CombatSystem.cs:271, ProjectileSystem.cs:76, and ProjectileSystem.cs:121 **When** the golden scenario runs **Then** the checksum sequence is byte-identical to the pre-refactor baseline (formula + death + RecordKill + event order unchanged)

**Given** the Tier-1 project **When** the combat-formula test suite runs **Then** it asserts DamageResolver output for representative damage/armor pairs (incl Hero) against expected Fixed values, satisfying the combat-formula coverage of FR-44

**Given** a malformed damage_table.json (NaN, wrong dimensions) **When** it is loaded **Then** loading is rejected with a located error rather than silently using defaults

_Covers: FR-44, AR-26, AR-14. Depends on: 1.4._

> Brownfield: DamageMatrix.cs is hardcoded float[,] 4x5 today. Migration Steps 3+4 combined (data-drive table + DamageResolver re-point of the 3 verified call sites). AR-26 clears the data-driven debt now (5x6 Hero). Adds the combat-formula slice of FR-44. Independent of SimRng so depends only on 1.4.

### Story 1.7: Fail-closed ScenarioValidator + Validated<T> single pre-tick gate (canonical start-state hash)

As a solo developer ensuring no unvalidated state can ever reach the tick loop,
I want a ScenarioValidator exposing Validate(model)->Validated<T> as the single pre-tick gate on all five entry paths, with the Validated<T> constructor mintable only by the validator, plus the canonical-model start-state hash (FNV-64 over Fixed.Raw, sorted, annotations excluded) re-pointing the match start-state hash away from ComputeFileHash(path),
So that every match starts from validated, hash-agreed truth and AI-generated stale-file desyncs are eliminated.

**Acceptance Criteria:**

**Given** the five scenario entry paths **When** any path attempts to tick a model **Then** it must pass through ScenarioValidator.Validate producing a Validated<T>, and Validated<T> cannot be constructed anywhere except inside the validator

**Given** an invalid model (e.g. random effect before SimRng, out-of-range value, dangling reference) **When** Validate runs **Then** it returns a failure with a located error and the tick loop never sees the model

**Given** the canonical start-state hash (FNV-64 over Fixed.Raw, fields sorted, annotations excluded) **When** two semantically identical models from different files (different whitespace/path) are hashed **Then** the hashes are equal, and the match start-state hash uses this canonical hash, not ComputeFileHash(path)

**Given** the validator on master **When** it encounters an invalid model **Then** it runs in shadow/log-only mode (logs the rejection without halting), with the fail-closed flip documented as a release-branch toggle after a corpus run

_Covers: FR-39, FR-44, AR-39, AR-23, AR-13. Depends on: 1.5, 1.6._

> Net-new. AR-39 single pre-tick gate, shadow/log-only on master with release-branch fail-closed flip. AR-23 canonical start-state hash fixes the AI-gen stale-file desync (re-points off ComputeFileHash). Depends on 1.5 (validator forbids random effects until SimRng) and 1.6 (validates the damage table). Negative-validation tests added to Tier-1.

### Story 1.8: Strangle MainScene into Godot-free SimulationHost + ScenarioApplier + asserted ScenePhaseRunner

As a solo developer decomposing the 2,234-LOC MainScene god-object,
I want a net-new Godot-free SimulationHost owning the 9-system tick order (ModifierSystem reserved at index 3 before CombatSystem, single checksum sink), a Godot-free ScenarioApplier as the sole writer of sim truth with allocation-free SpawnUnit and Godot path resolution hoisted to a presentation pre-pass, and an asserted ISetupPhase[] run by ScenePhaseRunner pinned by a PhaseOrderTest,
So that the sim spine is testable headless, reused verbatim by the server, and ready to absorb the six editors without touching presentation.

**Acceptance Criteria:**

**Given** the tick logic currently inside MainScene **When** it is extracted into a Godot-free SimulationHost **Then** SimulationHost has no `using Godot`, owns the canonical 9-system tick order with a reserved index-3 slot for ModifierSystem before CombatSystem, exposes a single checksum sink, and the golden scenario run through it is byte-identical to the pre-extraction baseline

**Given** ScenarioApplier as the sole writer of sim truth **When** a scenario is applied **Then** all Godot path resolution happens in a presentation pre-pass before the Applier runs, SpawnUnit allocates zero per call, and the Applier compiles in the Godot-free test project

**Given** the composition root as an ISetupPhase[] run by ScenePhaseRunner **When** PhaseOrderTest runs **Then** it asserts the exact phase order and FAILS if a phase is reordered, added, or removed

**Given** MainScene after the strangle **When** I diff it **Then** MainScene is materially smaller and contains only presentation/wiring, with sim mutation flowing exclusively through ScenarioApplier

_Covers: FR-39, FR-44, AR-6, AR-7, AR-3. Depends on: 1.7._

> Net-new SimulationHost (AR-6) + ScenarioApplier (AR-7); AR-3 ScenePhaseRunner/ISetupPhase[]/PhaseOrderTest. Strangles MainScene.cs (2234 LOC). ModifierSystem is reserved at index 3 but built in a later epic. This creates the shared sim path the server (1.9) reuses. Golden checksum must stay byte-identical.

### Story 1.9a: ServerBootstrap headless peer + server checksum collector with quorum + HALT (loopback)

As a solo developer building the server authority the sim needs,
I want a ServerBootstrap peer composition root (headless branch builds SimulationHost+Validator+Applier, no presentation) and a server-side checksum collector with majority-vote desync attribution and the M1 tie-break/quorum forks pinned, all loopback-testable on one machine,
So that the server holds real sim state and can detect/attribute desync and HALT cleanly, before I attempt a two-machine run.

**Acceptance Criteria:**

**Given** the headless branch **When** ServerBootstrap runs **Then** it builds SimulationHost + ScenarioValidator + ScenarioApplier with no presentation/Godot Node tree, reusing the exact sim spine from 1.8

**Given** the cross-faction same-tick event tie-break and the server >2-player quorum model **When** two events resolve on the same tick from different faction slots **Then** the pinned ascending-faction-slot tie-break is applied, and the server declares a strict-majority canonical hash (or HALTs as 'global desync, no canonical' on no majority)

**Given** an induced divergence on one loopback peer **When** the server's collector detects a mismatch **Then** it broadcasts a DesyncAlert naming the diverged peer and the match terminates with a clear user-facing HALT message distinct from silent drift (UX-DR64e)

_Covers: AR-38, AR-40, UX-DR64. Depends on: 1.8._

> Split from former 1.9. AR-38 ServerBootstrap peer root; AR-40 pins the two M1 checksum forks (ascending-faction-slot tie-break, >2-player quorum = true majority). UX-DR64e desync->terminal HALT. Brownfield: extends DedicatedServer.cs (opaque checksum relay at :148-156) and LockstepManager. Single-machine / loopback — no second machine required.

### Story 1.9b: FR-39 two-machine LAN determinism green (the #1 ship-risk gate)

As a solo developer who must prove multiplayer never desyncs,
I want the P2.4 LAN determinism test to pass on two separate physical machines,
So that two players complete a full match in lockstep for 300+ ticks with ZERO desync — the gate the whole 1.0 rests on.

**Acceptance Criteria:**

**Given** two machines on a LAN running the P2.4 determinism scenario against the 1.9a server **When** they play a full match for 300+ ticks **Then** the server-collected slot-tagged checksums stay in lockstep every comparison window with ZERO desync, and the P2.4 LAN test passes (UX-DR84)

**Given** the two-machine match **When** a real desync would occur **Then** the 1.9a DesyncAlert/HALT path fires with the clear message (verified end-to-end, not just unit-tested)

_Covers: FR-39, UX-DR84. Depends on: 1.9a._

> Split from former 1.9 — THE #1 ship risk, isolated as a dedicated physical-machine gate so the engine work (1.9a) isn't buried under the manual two-machine run. UX-DR84 LAN lockstep journey (300+ ticks zero desync). Requires two physical machines.

### Story 1.10: CI determinism regression guard + banned-API/AOT analyzers + cross-platform (WSL) gate

As a solo developer who must keep determinism from silently breaking,
I want the golden-checksum replay regression harness wired into CI, a banned-API Roslyn analyzer plus an AOT-eligibility analyzer over the sim layer (advisory on master, hard-enforced on a release branch), and the golden-checksum harness also running on Linux via the existing WSL/Ubuntu with the Windows and Linux sequences diffed,
So that any future change that breaks determinism or cross-platform reproducibility fails CI before it can ship.

**Acceptance Criteria:**

**Given** the replay/checksum regression harness from 1.2 (now at the current algo version) **When** CI runs on every push **Then** it executes the golden scenarios headless and FAILS the build on any checksum diff against the committed golden file

**Given** the banned-API analyzer over src/Core,Combat,Economy,Navigation,Effects,Dsl **When** sim code uses `using Godot`, float gameplay math, System.Random, or Fixed.FromFloat outside the converter allow-list **Then** it reports advisory on master and hard-fails on the release branch; the AOT-eligibility analyzer flags AOT-incompatible patterns the same way

**Given** the existing WSL/Ubuntu with .NET installed **When** the golden-checksum harness runs on both Windows and Linux **Then** the two checksum sequences are diffed and are byte-identical, and a mismatch fails the gate

**Given** the deps **When** CI builds **Then** NakamaClient is pinned at 3.13.0 and test-only deps remain isolated to the Godot-free test project

_Covers: FR-47, FR-44, AR-36, AR-37, AR-2, AR-35. Depends on: 1.9._

> AR-36 banned-API + AOT analyzers (advisory master / enforce release). AR-37 cross-platform gate via WSL (.NET-in-WSL prereq per memory note). FR-47 regression guard closes the loop so 1.9's LAN-green cannot regress silently. Hard milestone M1 completes when 1.1-1.10 are green.

### Story 1.11: Smoke-test the four unverified systems (Utility AI, Adaptive Input Delay, LLM Trigger, AI Map Generator)

As a solo developer needing the four never-verified systems proven against their checklists,
I want each of the four unverified systems (Utility AI, Adaptive Input Delay, LLM Trigger System, AI Map Generator) to pass a documented smoke-test checklist, with sim-touching paths asserted deterministic via the golden harness,
So that I know these systems actually function and do not introduce nondeterminism before later epics build on them.

**Acceptance Criteria:**

**Given** the Utility AI **When** its smoke-test checklist runs in the Tier-1/Tier-2 harness **Then** it produces decisions deterministically (golden checksum byte-identical across two runs) and the checklist passes

**Given** the Adaptive Input Delay in LockstepManager **When** its smoke-test checklist runs **Then** delay negotiation is deterministic and agreed across peers (no desync from delay change) and the checklist passes

**Given** the LLM Trigger System **When** its smoke-test checklist runs **Then** any LLM output that reaches the sim is funneled through the ScenarioValidator (validated-only) so non-deterministic generation never mutates the tick directly, and the checklist passes

**Given** the AI Map Generator **When** its smoke-test checklist runs with a fixed seed via SimRng **Then** it generates a byte-identical map across two runs and the checklist passes

_Covers: FR-45, AR-13, AR-39. Depends on: 1.10._

> FR-45 the four unverified systems pass smoke-test checklists. Leans on SimRng (1.5) for the map generator seed and ScenarioValidator (1.7) to keep LLM-trigger output validated-only. Runs after M1 is green so any nondeterminism these surface is caught against an already-trustworthy baseline.

> ⚠ Quality-review: define the concrete smoke-test checklist per system; for the LLM-Trigger and Utility-AI items specify exactly which sim-touching outputs are checksummed (not 'decisions deterministically').

## Epic 2: Living Combat — Effect Engine, Modifiers & Ability Authoring

_The sim engine can express buffs/auras/DoT/summons/multi-effect abilities deterministically; creators author abilities in-app; the showcase factions' signature mechanics run with satisfying combat feedback._

**Sequencing note:** Brownfield grounding from live source: no src/Effects or src/Dsl directory exists yet (D1 is net-new); CombatSystem.cs targets via SpatialHash with NO TargetFilter/anti-air/anti-building distinction and resolves damage only against EntityWorld (buildings live in a separate BuildingStore); EntityWorld SoA has Health/MaxHealth/AttackDamage/AttackRange/etc. but NO Energy/Mana and NO Base*/Effective* paired arrays; BuildingSystem.GetProductionUnit trains only the FIRST unit per category (GetUnitByCategory) and there is no Air producer; CommandCardSystem.cs shows building/worker cards only (no ability buttons); CombatFeedbackBridge.cs is hardcoded (orange/yellow/red/white pools + fixed camera shake), not profile-driven. SimRng and the Validated<T> gate are EPIC-1 deliverables — this epic depends on Epic 1 for them (AR-13/AR-39: validator forbids random effects until SimRng exists; all defs flow through Validated<T>). System registration order matters: ModifierSystem must register BEFORE CombatSystem in SimulationLoop. The Court's on-death 'Glut' needs the D2 trigger seam (Epic 7) — Story 2.10 ships ONLY the D1-expressible Sanguine Furnace passive HoT + Equal Exchange self-cost, and explicitly flags Glut as enabled-by-Epic-7 with NO forward dependency created here. AR-29: CombatFeedbackProfile is presentation-domain and MUST be excluded from SimChecksum (verify the hash in SimChecksum.cs never reads it); hit-freeze is presentation-only and must never pause the sim tick.

### Story 2.1: D1 Effect-Graph vocabulary and work-stack executor (pure sim keystone)

As a engine developer,
I want a single closed, typed, statically-bounded Effect-Graph surface in src/Effects with a pre-allocated work-stack executor,
So that every ability, the DSL, and AI balance share one deterministic effect vocabulary that cannot recurse, overflow, or call float math.

**Acceptance Criteria:**

**Given** the closed EffectNode vocabulary defined in src/Effects **When** the type set is reviewed **Then** it contains only sealed leaf nodes plus exactly three composition nodes (Sequence, SearchArea, Persistent) and a first-class Modifier, with no open/virtual extension point and no scripting hook **And** no leaf or composition type references Godot, float, double, System.Random, or wall-clock time

**Given** an effect graph with composition depth or fan-out exceeding the cap (depth>8 or fan-out beyond the configured limit) **When** the executor or its load-time bound check runs **Then** it is rejected/clamped at a statically-bounded limit and never recurses or grows the work-stack beyond its pre-allocated size **And** the executor uses a pre-allocated work-stack and performs zero heap allocation per execution

**Given** an identical effect graph executed against an identical EntityWorld snapshot twice (and, if it contains a random leaf, with the same SimRng seed) **When** each run completes and a golden checksum is taken of the resulting world state **Then** the two checksums are byte-identical **And** nodes that mutate entities iterate targets in ascending entity-id order

**Given** a unit test harness for the executor **When** a Sequence of {DirectHpDelta -10, Heal +25} runs on one entity **Then** the non-matrix DirectHpDelta applies a flat armor-independent HP change (never routed through DamageMatrix) and the Heal applies after it, proving the Equal-Exchange-shaped self-cost primitive works

_Covers: FR-12, AR-8, AR-13, NFR-4. Depends on: Epic 1._

> Net-new src/Effects directory — confirmed absent in live source. Pure C#, no 'using Godot', Fixed 16.16 only. AR-8: sealed EffectNode leaves (closed set, e.g. Damage [matrix], DirectHpDelta [non-matrix flat, for Equal Exchange self-cost], Heal, ApplyModifier, SetVariable, FireProjectile, TargetFilter) + EXACTLY three composition nodes (Sequence, SearchArea, Persistent) + a first-class Modifier descriptor. Executed via pre-allocated work-stack (NOT recursion), depth<=8, fan-out capped. NO scripting escape hatch. Random-effect leaves must route through Epic-1 SimRng (no System.Random, no wall-clock). This is the keystone; build it first.

### Story 2.2: D1 Modifier subsystem: ModifierStore, ModifierSystem, Energy/Mana, Base/Effective stats

As a engine developer,
I want a net-new SoA ModifierStore plus a ModifierSystem registered before CombatSystem, new Energy/Mana arrays, and Base*/Effective* paired stat arrays with dirty-flag recompute,
So that buffs, debuffs, auras, DoT/HoT and stat modifiers can be expressed deterministically — the primitive without which MOBA/TD/RPG content is unbuildable.

**Acceptance Criteria:**

**Given** the SimulationLoop system list **When** systems are registered **Then** ModifierSystem appears strictly before CombatSystem and ProjectileSystem in the tick order **And** CombatSystem reads Effective* stat arrays, not the raw Base* arrays

**Given** an entity with BaseAttackDamage and an active +damage Modifier **When** ModifierSystem ticks with the entity's dirty flag set **Then** EffectiveAttackDamage recomputes to Base + modifier and the dirty flag clears; a subsequent tick with no change performs no recompute **And** recompute is order-independent and produces identical results regardless of modifier insertion order for commutative modifiers

**Given** a HoT modifier applied via a Persistent node (periodEffect = Heal) and a DoT modifier (periodEffect = DirectHpDelta) **When** the modifier's period elapses over several ticks **Then** health changes by the expected Fixed amount per period and the modifier expires after its duration **And** an Energy/Mana cost can be debited from the new Energy SoA array and an ability is refused when Energy is insufficient

**Given** two runs of a fixed scenario that applies, stacks, refreshes, and expires modifiers **When** a golden SimChecksum is computed at matching ticks **Then** the checksums are byte-identical across both runs **And** ModifierStore state is included in the determinism path (no float, ascending-id iteration)

_Covers: FR-12, AR-9, AR-8, NFR-4. Depends on: 2.1._

> AR-9 keystone. EntityWorld today has flat AttackDamage/AttackRange/AttackSpeed/MaxHealth and NO Energy/Mana — add Base*/Effective* pairs (e.g. BaseAttackDamage/EffectiveAttackDamage) + Energy/Mana SoA arrays + a per-entity dirty flag. ModifierSystem.Tick recomputes Effective* from Base* + active modifiers ONLY when dirty, BEFORE CombatSystem reads them — so ModifierSystem must be inserted earlier in the SimulationLoop _systems array than CombatSystem. CombatSystem must be repointed to read Effective* (do not regress existing combat). The ApplyModifier leaf from 2.1 now resolves against this store; Persistent(periodEffect) drives DoT/HoT. Modifiers carry stack/refresh policy (needed later for the Glut). Pure C#, Fixed only.

### Story 2.3: AbilityDefinition data model and Validated<T> gate with static validator rules

As a creator-platform developer,
I want a data-driven AbilityDefinition in src/Core/Definitions that compiles to a 2.1 effect graph and passes through the Validated<T> gate with located error messages,
So that ability definitions are JSON-authored, deterministic, and server-validatable before any tick — with no float math and no scripts ever reaching the sim.

**Acceptance Criteria:**

**Given** a valid ability JSON (targeting type + cost + cooldown + >=1 effect node) **When** it is loaded and passed through the Validated<T> gate **Then** it deserializes into an AbilityDefinition, compiles to a 2.1 effect graph, and the gate returns a valid result **And** the compiled graph executes deterministically (golden checksum identical across two runs)

**Given** an ability JSON containing a float gameplay value, an unknown node type, or a graph exceeding the depth/fan-out cap **When** it is validated **Then** the gate rejects it with a located error naming the ability and the offending node/field **And** no rejected definition can be instantiated into a runnable graph

**Given** an ability JSON containing a random effect leaf **When** it is validated in an environment where Epic-1 SimRng is available **Then** the random leaf is accepted and seeded from SimRng; and the validator would reject it if SimRng were absent (per AR-13) **And** no ability definition can carry an executable script or arbitrary code payload

_Covers: FR-12, FR-10, AR-8, AR-13, AR-39, NFR-6. Depends on: 2.1, 2.2._

> FR-12 is the spine. AbilityDefinition fields: id, display name, targeting type, cost (energy/ore/crystal), cooldown, and an effect-graph payload composed of 2.1 leaves/composition nodes. Deserialized by a C# class in src/Core/Definitions (mirrors UnitDefinition pattern). All defs flow through Epic-1 Validated<T> gate (AR-39). Validator rules (AR-13): reject random leaves unless SimRng present; reject float gameplay values; reject graphs over depth/fan-out cap; reject unknown node types; reject unresolvable modifier/ability references — each with a LOCATED error (which ability, which node). No scripting payload accepted. This story builds only the model + validator + loader, not the editor UI.

### Story 2.4: Attach abilities to units/heroes and surface them on the runtime command card

As a player,
I want abilities attached to a unit type to appear as activatable buttons on the in-game command card with cooldown, cost, and targeting feedback,
So that I can cast a unit's ability during a match and see it resolve through the deterministic effect engine.

**Acceptance Criteria:**

**Given** a unit type whose definition lists one active ability **When** an entity of that type is selected in Play mode **Then** the command card shows an ability button labeled with the ability, its cost, and its cooldown state **And** the button is disabled when on cooldown or when Energy/ore/crystal is insufficient, matching the sim-side affordability check

**Given** an off-cooldown affordable ability with a target-unit targeting type **When** the player clicks it and selects a valid target **Then** an ability-cast intent is queued and consumed by the sim, the effect graph executes, the cost is debited, and the cooldown begins **And** the presentation layer issued only an intent and never wrote sim arrays directly

**Given** the same cast performed in two identical replays **When** a golden checksum is taken after the cast resolves **Then** the checksums are byte-identical **And** per-ability cooldown ticks down in Fixed time and the ability re-enables exactly when cooldown reaches zero

_Covers: FR-11, AR-8, AR-9, NFR-4. Depends on: 2.3._

> FR-11. UnitDefinition gains an abilities reference list (ability ids). EntityWorld/per-entity needs per-ability cooldown tracking (sim-side, Fixed) and the cast path issues an INTENT from presentation (CommandCardSystem) that the sim consumes — presentation NEVER mutates sim state directly (the sacred boundary). Extend CommandCardSystem.cs (today shows only building/worker cards) to render an ability section for a selected combat unit: button enabled/disabled by cooldown + Energy/ore/crystal affordability (reuse the existing cost/affordability display pattern). Targeting type from 2.3 drives target-select vs instant-cast vs ground-cast. The actual effect runs via the 2.1 executor + 2.2 ModifierSystem on the sim tick.

### Story 2.5: Ability Editor — author active abilities with simple-preset and advanced multi-effect modes

As a creator,
I want an in-app Ability Editor to author an active ability (targeting, cost, cooldown, >=1 effect) using either configurable presets or full multi-effect composition with a raw-JSON escape hatch,
So that I can build a new active ability without writing code, simply or deeply, and have it validated before it can break a match.

**Acceptance Criteria:**

**Given** the Ability Editor in simple mode **When** the creator picks the 'targeted damage' preset, sets a damage value and cooldown, and saves **Then** a valid AbilityDefinition is produced, passes the validator, and is immediately attachable to a unit (2.4) and castable in a match **And** the creator never edited JSON or wrote code to reach this result

**Given** the Ability Editor in advanced mode **When** the creator composes a multi-effect graph (e.g. DirectHpDelta self-cost + SearchArea + Heal allies) and saves **Then** the composed multi-primitive ability validates and executes as one Sequence on cast **And** a raw-JSON view of the same ability is available and round-trips (edit JSON, reparse, identical graph) per UX-DR54

**Given** an ability authored with an invalid configuration (e.g. depth over cap or a float value) **When** the creator attempts to save **Then** the save is blocked and the editor shows the validator's located error naming the offending node/field **And** no invalid ability is ever written to a loadable definition file

_Covers: FR-8, FR-10, AR-8, AR-13, UX-DR54, NFR-6. Depends on: 2.3, 2.4._

> FR-8 + FR-10 + UX-DR54. Presentation-layer editor (src/CreationSuite or src/UI, follows TriggerEditorPanel pattern). Layered complexity: SIMPLE mode = pick a preset (e.g. 'damage nuke', 'targeted heal', 'buff') and tune a few numbers; ADVANCED mode = compose multiple 2.1 effect primitives into a Sequence/SearchArea/Persistent graph; plus a raw-JSON escape hatch (UX-DR54 simple/advanced disclosure). Every save runs through the 2.3 Validated<T> gate and surfaces located errors inline. Editor outputs the same AbilityDefinition JSON that 2.3 loads and 2.4 attaches — no new runtime path. Sized to active abilities only; passive path is 2.6.

### Story 2.6: Ability Editor — passive ability path (auras, on-hit effects, stat modifiers, trigger conditions)

As a creator,
I want to author a passive ability (auras, on-hit effects, persistent stat modifiers) with trigger conditions in the Ability Editor,
So that I can give a unit a continuously-running or condition-triggered effect without code, reusing the same effect vocabulary as active abilities.

**Acceptance Criteria:**

**Given** the Ability Editor passive mode **When** the creator authors a 'while-alive aura' passive (SearchArea allies in radius -> ApplyModifier +armor) and attaches it to a unit **Then** every tick the owning unit grants the modifier to nearby allies and removes it when they leave the radius or the unit dies **And** the passive validates through the gate and runs in ModifierSystem before CombatSystem reads Effective* stats

**Given** a passive with an on-hit trigger condition (run effect when this unit's attack lands) **When** the unit deals an attack in a match **Then** the passive's effect graph fires deterministically on the hit and not otherwise **And** the trigger condition is selected from the closed D1 condition set and the editor offers no scripting or free-text logic

**Given** a passive 'Furnace Trickle'-style continuous self-regen authored as a Persistent(periodEffect = Heal) modifier **When** the owning unit is alive over several ticks **Then** its health regenerates by the configured Fixed amount per period up to MaxHealth **And** two identical runs produce byte-identical golden checksums

_Covers: FR-9, FR-10, AR-8, AR-9, UX-DR78, NFR-6. Depends on: 2.5._

> FR-9 + UX-DR78 (passive-ability path). Extends the 2.5 editor with a passive mode. Passives are modeled as a 2.2 Modifier + a Persistent/SearchArea graph plus an on-trigger condition drawn from a CLOSED, statically-validatable condition set available at D1 (e.g. on-hit, while-alive/aura, periodic) — NOT the full D2 event graph (Epic 7) and NOT introducing scripting. Auras = SearchArea + ApplyModifier each period; on-hit = a rider that runs when this unit's attack lands; passive stat mods = a permanent Modifier on the owning entity. Validated through the same gate; sim runs them via ModifierSystem before CombatSystem.

### Story 2.7: CombatFeedbackProfile — profile-driven feedback, presentation-only, excluded from checksum

As a creator,
I want abilities and units to carry a CombatFeedbackProfile (hit particles, impact sound, screen shake, hit-freeze, death effect) with a tuned default that I can override per unit/ability,
So that combat reads as satisfying and is art-directable, without any feedback ever affecting the deterministic simulation.

**Acceptance Criteria:**

**Given** a unit/ability with no explicit feedback override **When** it deals a melee hit, a ranged hit, a splash hit, and a kill **Then** the tuned default profile plays the existing pooled flash colors (orange/yellow/red/white) and a brief camera shake on kill, matching today's CombatFeedbackBridge behavior **And** the CombatFeedbackProfile field is excluded from SimChecksum and the canonical hash

**Given** a unit or ability with a custom CombatFeedbackProfile override (different hit particle/sound/shake/death effect) **When** combat events occur for it **Then** the bridge renders the overridden feedback instead of the default, driven by the profile rather than hardcoded constants

**Given** an ability configured with a hit-freeze **When** the freeze plays during combat **Then** the simulation tick continues advancing on schedule (sim time is unaffected) and two runs with and without rendering produce identical golden checksums

_Covers: FR-12a, AR-29, UX-DR51. Depends on: 2.1, 2.4._

> FR-12a + AR-29 + UX-DR51. Add CombatFeedbackProfile as a presentation-domain DTO in src/Core/Definitions, EXCLUDED from SimChecksum/canonical hash (verify SimChecksum.cs never reads it). It drives the D1 presentation leaves (PlayVfx/PlaySound/ShakeScreen) and upgrades CombatFeedbackBridge.cs from its current hardcoded pools (orange melee / yellow ranged / red splash / white kill + fixed shake) to profile-driven, preserving the existing pooled-flash behavior as the shipped default (UX-DR51: pooled hit-flashes + brief camera shake on kills). Hit-freeze is presentation-ONLY and must NEVER pause the sim tick — sim continues advancing while the visual freeze plays.

### Story 2.8: Combat reachability I — per-unit production selection UI and Air production building/category

As a player,
I want to choose which unit a production building trains (not just the first of its category) and to build/train Air units via an Air production building,
So that the full showcase rosters are actually reachable through the command card so the factions can be played as designed.

**Acceptance Criteria:**

**Given** a production building whose category has more than one unit defined in the faction **When** the player selects the building **Then** the command card lists each unit in that category and the player can pick which one to train **And** TrainUnit trains the selected unit (not just the first) while preserving the existing cost, supply, and prerequisite checks

**Given** an Air production building placed and an Air-category unit defined in the faction **When** the player selects the Air building **Then** the Air unit appears as a trainable option and trains correctly **And** no previously-trainable Melee/Ranged/Siege unit regresses

_Covers: FR-11, AR-8. Depends on: 2.4._

> Combat-reachability gap (non-FR, design-driven; advances FR-11 by surfacing the full roster on the command card). LIVE SOURCE: BuildingSystem.GetProductionUnit uses GetUnitByCategory which returns only the FIRST unit per category, and there is NO Air producer (faction-design doc calls this out explicitly). This story: (1) extend the production card to list every unit in the building's category and let the player pick which to train (extend CommandCardSystem.cs production section + BuildingSystem.TrainUnit to take a chosen unit id); (2) add an Air production building type + map the 'Air' category to it so Air units become trainable. Keep TrainUnit's existing prereq/cost/supply checks intact (no regression).

### Story 2.9a: Combat reachability — air/ground TargetFilter + anti-building combat

As a player,
I want units to honor anti-air/anti-ground target filters and to be able to attack enemy buildings,
So that the showcase factions' combat resolves against the right targets and armies can destroy structures.

**Acceptance Criteria:**

**Given** an anti-air-only unit and both a flying and a ground enemy in range **When** CombatSystem selects a target **Then** it engages only the flying enemy (and a ground-only unit engages only the ground enemy), honoring the TargetFilter **And** target selection remains deterministic and ascending-id stable

**Given** a combat unit ordered to attack an enemy building **When** it is in range **Then** it deals damage to the building in BuildingStore via the DamageMatrix and can destroy it **And** friendly buildings are never targeted

_Covers: FR-11, FR-12, AR-8, NFR-4. Depends on: 2.4, 2.8._

> Split from former 2.9. LIVE SOURCE: CombatSystem targets purely by nearest-enemy via SpatialHash (no air/ground or unit/building distinction) and resolves damage only against EntityWorld. Adds an air/ground tag + TargetFilter honored by CombatSystem/SpatialHash, and lets CombatSystem damage enemy buildings (BuildingStore). All sim changes stay Fixed/deterministic and ascending-id.

### Story 2.9b: Combat reachability — worker-cast ability path + multi-resource (crystal) cost

As a player,
I want workers to cast abilities and units/abilities to charge multi-resource (ore + crystal) costs,
So that signature abilities can be paid for and cast as designed.

**Acceptance Criteria:**

**Given** a worker unit with an active ability and a multi-resource cost (ore + crystal) **When** the player casts it and the faction can afford both resources **Then** the worker casts via the new worker-cast path, both ore and crystal are debited, and the ability is refused if either resource is insufficient **And** the cast resolves deterministically and the worker's gather/build loop is not corrupted by the cast

_Covers: FR-11, FR-12, AR-9, NFR-4. Depends on: 2.9a._

> Split from former 2.9. Gatherers are currently skipped by CombatSystem and have no ability-activation route — this adds the worker-cast ability path and wires crystal-spend / multi-resource cost so ability and unit costs can charge crystal as well as ore. Enables Equal Exchange's matter/crystal cost option (2.10). Stays Fixed/deterministic and ascending-id.

### Story 2.10: Showcase faction signature mechanics — Equal Exchange and Sanguine Furnace passive HoT (D1-only)

As a player,
I want the Crucible Covenant's Equal Exchange self-cost abilities and the Sanguine Court's passive soul-fed regeneration to actually run in combat,
So that the two showcase factions finally play with their signature feel instead of resolving as pure stat sheets.

**Acceptance Criteria:**

**Given** a Covenant unit with an Equal Exchange signature ability authored in the editor **When** the player casts it **Then** the beneficial effect applies AND a flat armor-independent HP (or matter/crystal) self-cost is deducted in the same Sequence, never both resources, and the cost does not scale with the caster's armor (DirectHpDelta, not the matrix Damage leaf) **And** the cast plays its CombatFeedbackProfile and resolves identically across two golden-checksum runs

**Given** Court units carrying the Sanguine Furnace passive (trickle for pawns, larger for elites) **When** they are alive and below max HP over several ticks **Then** each regenerates HP per period via the Persistent(Heal) modifier at its configured rate, capped at MaxHealth **And** the regen runs in ModifierSystem before CombatSystem and is byte-identical across two runs

**Given** the Court faction definition in this story **When** the on-death 'Glut' accelerated-regen aura is reviewed **Then** it is documented as deferred/enabled-by-Epic-7 and is NOT wired into the sim here, with no code dependency on a later epic **And** the faction remains always-shippable: with abilities present it plays with its signature feel, and nothing regresses the existing stat-sheet behavior

_Covers: FR-8, FR-9, FR-10, FR-12a, AR-8, AR-9, AR-29, UX-DR51. Depends on: 2.5, 2.6, 2.7, 2.9._

> Caps the epic by authoring the showcase mechanics with the now-shipped tools (no new engine code). Equal Exchange (Covenant): each signature active ability deducts a FLAT armor-independent HP cost via the non-matrix DirectHpDelta leaf (per faction-design: a self-Damage leaf would wrongly scale by armor) OR a matter/crystal cost — never both; authored in 2.5, paid via 2.9 multi-resource cost, felt via 2.7 feedback. Sanguine Furnace (Court): passive per-unit regen as a Persistent(periodEffect=Heal) Modifier (pawns trickle, immortals pour) authored in 2.6. SCOPE LIMIT: the on-death 'Glut' accelerated-regen aura needs the D2 on-death trigger seam and is ENABLED-BY-EPIC-7 — do NOT build it here and do NOT create a dependency on Epic 7; only the D1-expressible passive HoT + Equal Exchange ship in this story. Flag Glut as deferred in the ability data with a note.

> ⚠ Quality-review (OWNERSHIP): Epic 2 OWNS the Equal Exchange (flat self-cost) and Sanguine Furnace (Persistent HoT) D1 mechanics. Epic 5's 5.4 is a verify/integration consumer, not a re-wire. The on-death 'Glut' aura is deferred to Epic 7's D2 on-death seam.

## Epic 3: Author Units & Heroes (incl. Save/Load)

_A creator builds units and heroes in one consolidated Unit Card Editor with no JSON, players save/load hero progression, and the shared UI design-system (Godot Theme + component kit) is delivered for reuse by every later editor._

**Coverage note:** Design-system story also covers (low-materiality fold-ins): UX-DR35 (chamfer everywhere, .kbd radius sole exception), UX-DR36 (non-diegetic flat HUD trait), UX-DR38 (do NOT reintroduce the bio-alchemy 'Transmutation Lab' retheme — guardrail), UX-DR45 (keyboard-focus reveals tooltips + dialogs trap focus), UX-DR50 (130ms motion timing / cubic-bezier / 1px button depress / toggle snap), UX-DR85 (runtime UI built with Godot Control nodes).

### Story 3.1: Shared UI Design-System: Godot Theme + reusable component kit

As a creator-tools developer,
I want one canonical Godot Theme resource encoding every design token, plus a reusable kit of pre-styled UI components,
So that every editor in this and later epics composes a consistent, on-brand UI without re-styling controls.

**Acceptance Criteria:**

**Given** the two open design decisions UX-DR4 (accent-switch mechanism) and UX-DR9 (chamfer StyleBox choice) **When** the design-system work begins **Then** both decisions are resolved and documented in-code as the canonical choice **And** UX-DR4 picks a single accent-switching mechanism (e.g. one Theme type-variation swapped at runtime vs per-control override) and UX-DR9 picks the chamfer implementation (StyleBoxFlat corner config vs StyleBoxTexture) with rationale

**Given** the resolved tokens **When** the Theme resource is authored **Then** all of UX-DR1..UX-DR12 (surface/line/text/accent/semantic/team colors, typography roles, type scale, chamfer cuts, spacing, shadows) map 1:1 into a single .theme resource committed to the repo **And** UX-DR34 mono tabular-number font role is defined for numeric readouts

**Given** the Theme resource **When** the component kit is built **Then** each of UX-DR13..UX-DR32 (panel, btn, icon-btn, kbd, chip, readout, tag, progress, slider, input, menu, tabs, list-row, tooltip, dialog, toast, spinner, mark, switch, num-input) exists as a reusable component (scene or factory) styled only from the Theme **And** a demo/gallery scene instantiates every component so the kit is visually verifiable in-engine **And** no component hardcodes a color or size that exists as a token

**Given** the tooltip and switch components **When** they are used by a control **Then** the tooltip component supports a hover tooltip on any control (UX-DR53 foundation for NFR-2) and the switch component exposes a clean on/off toggle reusable for simple/advanced disclosure (UX-DR54 foundation)

_Covers: UX-DR1, UX-DR2, UX-DR3, UX-DR4, UX-DR5, UX-DR6, UX-DR7, UX-DR8, UX-DR9, UX-DR10, UX-DR11, UX-DR12, UX-DR13, UX-DR14, UX-DR15, UX-DR16, UX-DR17, UX-DR18, UX-DR19, UX-DR20, UX-DR21, UX-DR22, UX-DR23, UX-DR24, UX-DR25, UX-DR26, UX-DR27, UX-DR28, UX-DR29, UX-DR30, UX-DR31, UX-DR32, UX-DR33, UX-DR34, UX-DR53, UX-DR54. Depends on: — (none / earlier epics only)._

> Foundational UI epic deliverable reused by every later editor (UX-DR33). No Theme/.theme/component kit exists in repo today (verified). Presentation layer only — Godot Nodes/Control; no sim coupling. First AC MUST resolve the two open decisions UX-DR4 and UX-DR9 before tokens are finalized.

### Story 3.2: HeroStore SoA + stable hero identity folded into SimChecksum & startStateHash

As a simulation engineer,
I want a sparse HeroStore (Struct-of-Arrays) keyed by a stable cross-match hero identity, folded into the determinism checksums,
So that hero progression state is part of deterministic, MP-safe initial state and cannot silently desync.

**Acceptance Criteria:**

**Given** the generalized SimChecksum delivered in Epic 1 (prerequisite) **When** HeroStore is implemented **Then** HeroStore is a pure-C# sparse SoA (no using Godot, Fixed 16.16 for any numeric gameplay state, indexed/iterated in ascending order) keyed by a stable hero identity that is NOT the recycled EntityWorld id **And** the hero identity is a stable, content-defined key that survives across matches and EntityWorld id recycling

**Given** a populated HeroStore **When** SimChecksum.Compute runs **Then** HeroStore contents are folded into the checksum deterministically in ascending-identity order alongside EntityWorld/BuildingStore/ResourceStore **And** two runs with identical HeroStore input produce a byte-identical checksum (golden checksum test passes)

**Given** an initial match state including loaded heroes **When** a new canonical startStateHash is computed **Then** startStateHash is a deterministic hash over the full init-time state including HeroStore, computed once at match start **And** two runs from identical init data produce a byte-identical startStateHash

_Covers: AR-12. Depends on: Epic 1._

> AR-12 OFFLINE-rail backbone; the online rail (FR-7c) is Epic 9. PREREQ per AR-12: Epic 1 must have generalized SimChecksum before this fold. Pure sim layer — determinism is sacred; prefer concrete golden-checksum ACs. This story builds the data substrate ONLY; manifest authoring (3.8) and load UI (3.9) come later.

> ⚠ Quality-review: make the dependency 1.3-specific (the generalized SimChecksum), not the whole 'Epic 1', to avoid a same-batch ordering hazard.

### Story 3.3: Read-only Unit Card panel: stats, combat type, archetype, model preview

As a creator,
I want a single consolidated panel that displays an existing unit's stats, archetype, combat type, model, and attached abilities,
So that I can see everything about a unit type in one WC3-style card before I start editing it.

**Acceptance Criteria:**

**Given** a faction JSON with existing UnitDefinitions **When** I open the Unit Card panel for a unit **Then** one panel (built from the 3.1 component kit) shows stats (hp/speed/attack/range/attack-speed/cost/supply/vision), combat type (damage_type + armor_type), archetype/category, model reference, and any attached ability list — all in a single view (UX-DR77 layout, read-only this story) **And** numeric fields use the UX-DR34 mono tabular readout component

**Given** a unit whose mesh_path resolves to a GLB **When** the card is shown **Then** the assigned 3D model renders in an in-panel preview viewport reusing the existing AssetPreviewScene/MeshLoader path (FR-3 display half) **And** a unit with null/missing mesh_path shows the box placeholder instead of failing

_Covers: FR-2, FR-3, UX-DR77, UX-DR34, UX-DR53. Depends on: 3.1._

> Brownfield: UnitDefinition (verified) already has stats/combat/model/scale/prereqs; AssetPreviewScene.cs + MeshLoader.cs already load GLB with box fallback — REUSE them, do not rebuild. This story is the display shell of the Unit Card Editor (UX-DR77); editing/persistence/validation arrive in 3.4. No new editable fields yet.

### Story 3.4: Unit Card Editor: edit/create/duplicate/delete with inline validation, persisted to faction data

As a creator,
I want to create, edit, duplicate, and delete unit definitions in the Unit Card Editor with no JSON, validated inline before save,
So that I can author units safely and have them persist into my scenario's faction data.

**Acceptance Criteria:**

**Given** the read-only Unit Card panel from 3.3 **When** I edit stat/combat/cost/model fields and click Save **Then** the UnitDefinition is updated and written to the faction JSON referenced by the scenario (no manual JSON editing), and Create/Duplicate/Delete operations add, clone, and remove unit definitions in that faction **And** edits route through the existing EditorHistory undo/redo stack so Ctrl+Z reverts a unit edit

**Given** an invalid edit (out-of-range or missing stat, missing/invalid model path, undefined ability reference, invalid archetype/category) **When** I attempt to Save or Playtest **Then** inline validation (AR-39, fail-closed) blocks the action and shows a located error badge (UX-DR55) on the offending field describing the problem **And** a unit with all-valid fields saves with no badges and is immediately usable in playtest

**Given** the simple/advanced disclosure switch (UX-DR54) **When** I toggle to advanced mode **Then** every authorable field is exposed including a raw-JSON escape hatch for the unit definition, and simple mode hides advanced fields behind the disclosure **And** every control carries a tooltip (UX-DR53 / NFR-2)

_Covers: FR-1, FR-2, FR-6, FR-7, AR-39, AR-3, UX-DR77, UX-DR54, UX-DR55, UX-DR53. Depends on: 3.3._

> FR-7 inline authoring errors come from the AR-39 validator (located, fail-closed). FR-1 persistence targets FactionDefinition.Units in the faction JSON (verified path: ScenarioData.PlayerSlots[].FactionJson). Reuse EditorHistory.cs for undo. AR-3 FactionRegistry/slot usage to resolve which faction is being edited. Ability-ref and archetype validation are surfaced here but the fields themselves are fully authored in 3.6; this story validates whatever fields exist.

### Story 3.5: In-editor model assignment & preview (box placeholder + GLB browse)

As a creator,
I want to assign a 3D model or a box placeholder to a unit and preview it live in the editor,
So that I can give units the right look (or a working stand-in) without leaving the Unit Card Editor.

**Acceptance Criteria:**

**Given** the Unit Card Editor model field **When** I browse for and select a GLB asset **Then** mesh_path (and mesh_scale) update on the UnitDefinition and the in-panel preview re-renders the chosen model immediately **And** I can explicitly choose 'box placeholder' which clears mesh_path and the preview shows the placeholder

**Given** a selected GLB that fails to load or is missing **When** the preview attempts to render it **Then** the preview falls back to the box placeholder and an inline validation badge (UX-DR55) flags the invalid model (feeding FR-7's missing/invalid model case) **And** the editor does not crash and other fields remain editable

_Covers: FR-3, UX-DR55, UX-DR77, AR-5. Depends on: 3.4._

> Completes the assign half of FR-3 (3.3 delivered display). Reuse MeshLoader.cs box fallback + AssetPreviewScene.cs preview (verified). AR-5 SettingsData can remember last-used asset folder as an editor pref. Live preview update on assignment is the new behavior beyond 3.3's static display.

### Story 3.6: Archetype + orthogonal ability/behavior composition (no subclassing)

As a creator,
I want to compose a unit from the 6 archetypes plus orthogonal ability and behavior components,
So that I can build any unit (e.g. a healer = ranged + heal ability + support behavior) without code subclassing.

**Acceptance Criteria:**

**Given** the Unit Card Editor **When** I author a unit **Then** I pick exactly one of the 6 archetypes and attach zero or more orthogonal ability/behavior components, persisted as data on the UnitDefinition (new fields, deserialized by a C# definition class) **And** composition is purely additive data — there is no per-unit subclass; a healer is expressed as ranged archetype + a heal ability + a support behavior component

**Given** an attached ability/behavior set **When** I validate before save (AR-39) **Then** an undefined ability reference, an invalid archetype, or an invalid composition (e.g. an archetype-incompatible component) is rejected with a located badge (UX-DR55), satisfying FR-7's undefined-ability-ref and invalid-archetype-composition cases **And** a valid composition saves and round-trips through the raw-JSON advanced view unchanged

**Given** simple vs advanced mode (UX-DR54) **When** I compose in simple mode **Then** I use preset ability bundles, while advanced mode exposes every component field and the raw-JSON escape hatch (FR-6)

_Covers: FR-4, FR-6, FR-7, AR-39, UX-DR54, UX-DR55, UX-DR77. Depends on: 3.4._

> Composition over inheritance is a core architecture rule. Verified there is NO ability/archetype field on UnitDefinition today and NO ability runtime — this story adds the AUTHORING DATA model + validation only; running abilities (e.g. FMA Equal Exchange / Sanguine Furnace mechanics) is later-epic combat work. Ability/behavior component definitions are new C# classes in src/Core/Definitions deserialized from JSON. Depends on 3.4 (the editable card + validator), not on 3.5.

### Story 3.7: Promote-to-Hero: leveling curve, XP, signature/ultimate abilities

As a creator,
I want a Promote-to-Hero switch that reveals hero-only authoring fields on a unit,
So that I can designate a unit as a Hero with a leveling curve, XP gain, and signature/ultimate abilities.

**Acceptance Criteria:**

**Given** the Unit Card Editor for a composed unit **When** I flip the Promote-to-Hero switch (UX-DR77) **Then** hero-only fields appear — leveling curve, XP-gain rule, and signature + ultimate ability slots — and are persisted on the UnitDefinition as hero data **And** flipping the switch off hides and clears the hero fields, leaving a valid non-hero unit

**Given** a hero with authored leveling/abilities **When** I validate before save (AR-39) **Then** missing/out-of-range leveling values, an undefined signature/ultimate ability ref, or a hero composition that violates rules is rejected with a located badge (UX-DR55) **And** a valid hero saves and its hero identity matches the stable HeroStore key contract from 3.2

**Given** simple vs advanced mode (UX-DR54) **When** I author hero progression **Then** simple mode offers leveling-curve presets while advanced mode exposes every hero field plus the raw-JSON escape hatch (FR-6)

_Covers: FR-5, FR-6, FR-7, AR-12, AR-39, UX-DR77, UX-DR54, UX-DR55. Depends on: 3.6, 3.2._

> UX-DR77 Promote-to-Hero switch reveals leveling fields + ultimate. Hero data fields are new on UnitDefinition (verified none exist today). The hero's stable identity must align with the 3.2 HeroStore key contract so progression persists deterministically. Depends on 3.6 (composition exists) and 3.2 (identity contract). Authoring only — XP/leveling runtime is later-epic work.

### Story 3.8: Persistence manifest authoring (which attributes carry forward) through the validate gate

As a creator,
I want to author a persistence manifest declaring which hero/unit/player attributes carry to the next custom game, with the option to disable persistence,
So that I control exactly what progression travels between my custom games, MP-safely.

**Acceptance Criteria:**

**Given** a scenario with heroes/units authored **When** I open the persistence manifest editor (built from the 3.1 kit) **Then** I select which hero/unit/player attributes are persisted, producing a PersistenceManifest (one per scenario) plus the PlayerProfile shape it implies **And** a creator toggle can disable persistence entirely for the scenario (FR-7b)

**Given** an authored manifest **When** it passes through the D3 validate gate (AR-39) **Then** the manifest is validated fail-closed (e.g. references only attributes that exist, no persistence of mid-game-only state) and rejected with a located error if invalid **And** only init-time-eligible attributes may be selected — nothing that would imply a mid-game snapshot

_Covers: FR-7a, AR-12, AR-39. Depends on: 3.7._

> AR-12: one PlayerProfile + PersistenceManifest through the D3 validate gate. This is the authoring side; the load/apply side is 3.9. Manifest is data (new C# definition class). Disable-persistence toggle here satisfies the creator-can-disable clause shared with FR-7b. Depends on 3.7 so hero attributes exist to select.

### Story 3.9: Offline hero persistence rail + Save/Load hero-picker (deterministic init-time apply)

As a player,
I want a hero-picker Save/Load interface to load a saved hero into a compatible custom game, save new profiles, and overwrite slots,
So that my heroes persist between custom games as deterministic initial state, with multiple heroes per player.

**Acceptance Criteria:**

**Given** the offline rail (LocalProfileSource over the PlayerProfile/manifest from 3.8) and the HeroStore from 3.2 **When** I load a saved profile at custom-game start into a compatible scenario **Then** the profile is applied as DETERMINISTIC INITIAL STATE only (folded into startStateHash; never a mid-game snapshot) and is rejected/greyed-out if the scenario is incompatible or has persistence disabled (FR-7b) **And** loading the same profile into the same scenario twice yields a byte-identical startStateHash (MP-safe)

**Given** the hero-picker menu (UX-DR75, built from the 3.1 kit — not text codes) **When** I view my saved heroes **Then** each slot card shows hero icon/portrait + basic info (level, XP, signature, faction) and offers Deploy / Overwrite / Delete actions (FR-7d, FR-7e) **And** multiple heroes per player are listed and selectable

**Given** an occupied save slot **When** I save a new profile over it (Overwrite) or Delete it **Then** the action requires a confirmation dialog (from the 3.1 kit) before committing, and a fresh save into an empty slot writes a new LocalProfileSource entry **And** the creator's per-scenario enable/configure setting governs whether the picker is available at all (FR-7d)

_Covers: FR-7b, FR-7d, FR-7e, AR-12, UX-DR75, AR-5, AR-39. Depends on: 3.8, 3.2._

> AR-12 M2: offline LocalProfileSource + hero-picker UI (online rail FR-7c is Epic 9). Apply is INIT-TIME ONLY and folded into the 3.2 startStateHash — determinism is sacred. UX-DR75 slot cards: portrait/level/XP/signature/faction with Deploy/Overwrite/Delete + confirm. Depends on 3.8 (manifest/profile shape) and 3.2 (HeroStore + startStateHash). AR-5 SettingsData stores local save location/prefs.

### Story 3.10: [ADDED] Edit↔Play round-trip loop (no-restart playtest)

As a creator iterating on a scenario,
I want an F5 Edit↔Play toggle that runs my in-editor scenario through the live sim with no app restart and no export step, and resets match state cleanly when I return to Edit,
So that I can test a change and be back editing in seconds — the core creation loop.

**Acceptance Criteria:**

**Given** a scenario open in the editor **When** I press F5 to enter Play **Then** the authoring chrome (palettes, docks, win-condition panel) hides and the scenario runs through the SimulationHost **And** no application restart and no export/build step occurs **And** the round-trip completes in ≤2s wall-clock on the target hardware

**Given** a unit stat, ability, or trigger edited in Edit **When** I press F5 to Play **Then** the change is observably live on this Play with no manual reload

**Given** a playtest in progress **When** I press F5 to return to Edit **Then** match state resets to the authored start state per a defined reset scope: unit positions, resources, supply, fired triggers, and timers reset; hero XP gained during the playtest is discarded unless an explicit persistence-test mode is enabled **And** repeatedly toggling Play↔Edit shows no perceptible build step (UX-DR83)

_Covers: NFR-1, UX-DR62, UX-DR83. Depends on: Epic 1 (SimulationHost/ScenarioApplier), 3.x design-system + Unit Card Editor._

> ADDED by coverage review — NFR-1 was unowned. Resolves the under-specified UX-DR62 'reset match state' scope. Recommended EARLY in Epic 3 sequencing (it is the creation-loop spine) despite being listed last here.

### Story 3.11: [ADDED] Apply the design system to the front-end shell (Title, Mode Select, Settings)

As a player or creator launching the game,
I want the existing Title, Mode Select, and Settings screens restyled to the shared Godot Theme and component kit with the documented information architecture,
So that the product front end is coherent and shippable, not a placeholder.

**Acceptance Criteria:**

**Given** the Title screen **When** the game launches **Then** it renders to the Theme with nav (Play / Create / Browse / Settings / Quit), a version/build footer, and the tagline 'Build the game. Then play it.' (UX-DR67)

**Given** the Mode Select screen **When** I choose 'Play' **Then** it shows Skirmish (1–8, offline) / Multiplayer / Campaign & Tutorial (N/12) / Create / My Content with a breadcrumb + account chip (UX-DR68)

**Given** the Settings overlay **When** I open it from either the Commander or Creator branch **Then** it presents the tabs Gameplay / Graphics / Audio / Controls / Accessibility and is reachable from both branches (UX-DR73)

_Covers: UX-DR67, UX-DR68, UX-DR73. Depends on: 3.x design-system + component kit._

> ADDED by coverage review. Built-foundation HUD/menus exist ('assets pending') — this is verify + restyle to the design system, not net-new screens.

## Epic 4: Author Buildings, Tech Trees & Economy

_A creator authors building definitions, drag-builds a visual tech tree that gates production, and configures resources and the supply model as data._

**Sequencing note:** Brownfield reality drove the split: the hardcoded-economy gap is in the SIM layer (BuildingStore stat switch, TechTreeChecker enum switches, ResourceStore Ore/Crystal fields, STARTING_SUPPLY_CAP constant), while the authoring gap is in the PRESENTATION layer (no building or tech-tree editor; TriggerEditorPanel is the only editor pattern). Stories 4.1-4.4 retire the hardcoded sim tables (AR-26) data-first, each preserving byte-identical golden checksums and today's showcase behavior so the build stays always-shippable. Stories 4.5-4.6 add the creator UIs on top, reusing the existing kit and writing only definition JSON via the canonical serializer (sim/presentation boundary intact). Dependency order is strictly backward: 4.2 needs 4.1's building registry; 4.3 needs 4.1's loader path; 4.4 needs 4.1 (per-building supply) + 4.3 (scenario-config loader); 4.5 needs 4.1 (schema) + 4.3 (cost map); 4.6 needs 4.2 (prerequisite data + lint) + 4.5 (building nodes/inspector). FR-13 is intentionally split across 4.1 (data/runtime) and 4.5 (UI); FR-14 across 4.2 (runtime gate) and 4.6 (visual editor). Each story is one focused dev-agent session.

### Story 4.1: Data-drive the building definition + runtime building store

As a RTS creator,
I want buildings to be fully defined by data (stats, construction cost/time, supply bonus, production category) instead of a hardcoded enum with baked-in stats,
So that I am no longer locked to the four built-in building types and can author new buildings purely as data.

**Acceptance Criteria:**

**Given** a faction JSON whose buildings array uses the existing UnitDefinition shape plus building-specific fields (construction_time, supply_bonus, produces_category, construction_cost as a cost map) **When** the faction is loaded through the canonical ContentLoader / shared JsonSerializerOptions **Then** a BuildingDefinition (or extended UnitDefinition) is produced for each building with all fields populated and stamped with min_game_version **And** a building def missing required fields is rejected at import with a located error naming the building id and the missing field

**Given** the runtime BuildingStore previously baked HP/supply/construction-duration into a per-BuildingType switch **When** a building is placed from a loaded definition **Then** the store reads those values from the BuildingDefinition (resolved by string id) rather than the hardcoded switch, and a building type with no enum entry can still be created and placed **And** the four showcase buildings (command_center, barracks, archery_range, siege_workshop) place with byte-identical stats to before this story (no balance regression)

**Given** two identical runs that place the same data-defined buildings in the same tick order **When** the sim runs for N ticks and a golden checksum is taken **Then** the checksums are byte-identical (determinism preserved; all building gameplay state stays in Fixed, processed in ascending id order, no Godot types in the sim layer)

_Covers: FR-13, AR-26, AR-21, AR-25. Depends on: — (none / earlier epics only)._

> FR-13 / AR-26 / AR-21 / AR-25. Brownfield: faction JSON already carries a buildings array (reusing UnitDefinition) but BuildingStore.Create() hardcodes per-BuildingType stats and TechTreeChecker hardcodes id<->enum mapping. This story moves the source of truth into data and retires the stat switch in BuildingStore; the enum may remain as a back-compat alias for the four showcase types but must no longer be the gate to existence. Use the canonical JsonSerializerOptions (the one in ScenarioSerializer/FactionDefinition) and stamp min_game_version. Pure-sim constraint: no float for gameplay state, no using Godot in the store.

### Story 4.2: Data-driven tech-prerequisite resolution with import-time cycle + referential lint

As a RTS creator,
I want production and building placement to be gated by data-defined prerequisites resolved against an id registry, with bad trees caught at import,
So that my tech tree actually works at runtime without me touching code, and I get told immediately if I reference a building that doesn't exist or create a dependency loop.

**Acceptance Criteria:**

**Given** a faction whose units/buildings declare prerequisites:string[] of building ids **When** the runtime checks whether a unit can be trained or a building placed **Then** prerequisites are resolved against the data-driven building id registry (built from the loaded definitions) and the hardcoded TechTreeChecker ParseBuildingType/BuildingTypeId switches are retired **And** a prerequisite that names a building id not in the registry fails resolution rather than silently passing

**Given** a faction definition where building A requires B and B requires A (or any longer cycle), or where a prerequisite references an unknown id **When** the content is imported/validated **Then** import fails with a located error naming the offending ids and whether the fault is a cycle or a dangling reference (referential integrity) **And** a valid acyclic tree imports cleanly and the showcase factions (barracks->archery_range->siege_workshop chain) still gate exactly as before

**Given** the existing showcase scenario run **When** the same match is played twice with the new resolver **Then** the golden checksum is byte-identical to a run on the old hardcoded checker (no gating behavior change for existing content)

_Covers: FR-14, AR-26, AR-21. Depends on: 4.1._

> FR-14 (runtime gating half) / AR-26 / AR-21. Brownfield: TechTreeChecker.AreMet already consumes prerequisites:string[] but maps ids via hardcoded switches limited to the four enum buildings; BuildingSystem.TrainUnit and QueueWorkerBuild already call it. This story builds the data-driven id registry (from 4.1's definitions), rewrites TechTreeChecker.HasCompletedBuilding to match on building-def id rather than enum, and adds the cycle + referential-integrity lint at import. Sim stays pure C#. The visual editor that produces these prerequisites is 4.6.

### Story 4.3: N-resource registry with sparse cost maps (generalize Ore+Crystal)

As a RTS creator,
I want to define a scenario's resource set as data (id, display, model, starting amount, gather model) beyond the two hardcoded resources, with costs expressed as sparse {resourceId:amount} maps,
So that my scenario isn't locked to Ore and Crystal and can use any resources I invent.

**Acceptance Criteria:**

**Given** a scenario JSON declaring an ordered top-level resource registry (e.g. ore, crystal, plus a custom 'aether') each with id/display/starting_amount/gather model **When** the scenario loads through the canonical ContentLoader **Then** ResourceStore holds a balance array keyed by the registry order (not fixed Ore/Crystal fields) and starting amounts are seeded per resource per faction **And** a scenario with only the two default resources loads with identical starting balances to today (Ore default preserved)

**Given** unit and building definitions whose cost is a sparse map {resourceId:amount} (omitted resources cost zero) **When** an affordability check or spend runs **Then** CanAfford/Spend evaluate every entry in the cost map against the registry balances, succeed only if all are affordable, and deduct all atomically (no partial spend on failure) **And** a cost map referencing a resourceId absent from the scenario registry is rejected at import with a located error

**Given** two runs of a scenario using a 3-resource registry with gathering **When** the sim runs for N ticks and a golden checksum is taken **Then** checksums are byte-identical (all balances in Fixed, ascending id processing, no wall-clock, no Godot types in ResourceStore/GatheringSystem)

_Covers: FR-15, AR-26, AR-21, AR-25. Depends on: 4.1._

> FR-15 / AR-26 / AR-21 / AR-25. Brownfield: ResourceStore hardcodes Fixed[] Ore and Fixed[] Crystal and the only spend path is SpendOre; UnitDefinition has cost_ore/cost_crystal ints; ScenarioResourceNode gathers ore only. This story introduces the ordered resource registry + sparse cost maps and migrates ResourceStore to a registry-indexed balance array. Keep ore/crystal as the default registry entries so existing factions/scenarios are unchanged; provide back-compat parsing of legacy cost_ore/cost_crystal into the cost map. GatheringSystem must deposit into the node's resource id. Sim-pure, deterministic.

### Story 4.4: Data-driven supply / cap model per scenario

As a RTS creator,
I want to configure the supply (population cap) model per scenario as data — starting cap, per-building cap bonus, hard ceiling, or disable supply entirely,
So that my scenario isn't locked to the hardcoded 10-start / +10-per-CommandCenter rule.

**Acceptance Criteria:**

**Given** a scenario declaring a supply model (starting_cap, max_cap ceiling, and per-building supply_bonus already sourced from building defs) or a flag to disable supply **When** the scenario loads **Then** ResourceStore starting SupplyCap and the cap-recalc in BuildingSystem read these data values instead of the STARTING_SUPPLY_CAP constant, and supply_bonus comes from each building's definition (4.1) rather than the hardcoded switch **And** with supply disabled, HasSupply always returns true and no unit is ever blocked by cap

**Given** a scenario that omits the supply model **When** it loads **Then** it defaults to the current behavior (start 10, +10 per command_center, no explicit ceiling) so existing scenarios are unchanged **And** a max_cap ceiling clamps the recalculated cap even when building bonuses would exceed it

**Given** two runs of a scenario with a custom supply model **When** the sim runs for N ticks and a golden checksum is taken **Then** checksums are byte-identical (supply counts are integers recalculated in ascending id order by SupplySystem/BuildingSystem; no Godot types)

_Covers: FR-16, AR-26, AR-21. Depends on: 4.1, 4.3._

> FR-16 / AR-26 / AR-21. Brownfield: ResourceStore.STARTING_SUPPLY_CAP=10 and BuildingSystem.RecalculateSupplyCaps reset to that constant then add hardcoded SupplyBonus; SupplyCost lives on entities. This story makes the supply model scenario-data. Depends on 4.1 (building defs supply per-type) and 4.3 (resource registry / scenario-config loading path) so the supply config rides the same canonical loader. Defaults must reproduce today's behavior exactly. Sim-pure, deterministic.

### Story 4.5: In-app building definition editor (Unit-Card pattern, right-dock inspector)

As a RTS creator,
I want an in-app panel to create and edit building definitions — stats, construction cost/time, supply bonus, and which unit category they produce — reusing the existing unit-card UI,
So that I can author buildings without hand-editing JSON, while still being able to drop to raw JSON for advanced control.

**Acceptance Criteria:**

**Given** the building editor panel built from the existing kit (same Control composition as TriggerEditorPanel, building cards reusing the Unit-Card pattern) **When** the creator adds a building, edits its stats / construction cost (as a cost map over the scenario resource registry) / construction time / supply bonus / produced category in the right-dock inspector, and saves **Then** a valid BuildingDefinition is written to the faction JSON via the canonical serializer (the same data 4.1 consumes) and reloads identically **And** invalid input (e.g. negative cost, blank id, duplicate id) is rejected in-panel with an inline located message, not written to disk

**Given** a creator who prefers raw control **When** they open the advanced/raw-JSON escape hatch for the building def **Then** they can edit the underlying JSON and the simple-mode cards reflect the change on reload (layered-complexity: simple cards + advanced JSON) **And** the panel only reads/writes definition data and issues no direct mutation of sim state (presentation/sim boundary respected)

**Given** a building saved through the editor **When** a match loads that faction and places the building **Then** it places with exactly the stats authored in the panel (round-trips through 4.1's loader)

_Covers: FR-13, UX-DR74, UX-DR33, AR-21. Depends on: 4.1, 4.3._

> FR-13 (authoring UI half) / UX-DR74 (right-dock inspector + building defs reuse Unit-Card pattern) / UX-DR33 (compose from existing kit) / AR-21. Brownfield: TriggerEditorPanel.cs is the canonical editor-panel pattern to copy; no building editor exists yet. This is presentation layer (Godot Control nodes) and must only read/write definition JSON via the canonical serializer — never touch sim arrays. Depends on 4.1 (BuildingDefinition schema) and 4.3 (cost map uses the resource registry).

### Story 4.6: Visual tech-tree editor (tier-laned graph, drag out-port to wire prerequisites)

As a RTS creator,
I want to build a faction's tech tree visually — a tier-laned graph where I drag a building's out-port onto another building to set a prerequisite — and have it write the prerequisite data that gates production at runtime,
So that I can design progression visually instead of editing prerequisite arrays by hand, and the runtime enforces exactly what I drew.

**Acceptance Criteria:**

**Given** the tech-tree editor showing each building (from 4.5's defs) as a node in a tier-laned graph **When** the creator drags a building's out-port and drops it onto another building node **Then** a dependency edge is created and the target building's prerequisites array (4.2 data) gains the source building id; deleting the edge removes it **And** selecting a node opens the right-dock inspector to edit that building's stats (same inspector as 4.5)

**Given** a creator who attempts to wire an edge that would create a cycle or reference is otherwise invalid **When** they drop the connection **Then** the editor rejects it inline (consistent with the 4.2 import lint) and no invalid prerequisite is written **And** a valid graph saves prerequisites that, when the scenario runs, gate production/placement exactly as drawn (e.g. a unit/building stays unbuildable until its prerequisite building is completed)

**Given** a tech tree authored and saved in the editor **When** the faction JSON is reloaded into the editor **Then** the graph redraws with the same nodes, tiers, and edges (round-trip), and the raw-JSON escape hatch shows the matching prerequisites arrays (layered complexity)

_Covers: FR-14, UX-DR74, UX-DR57, UX-DR33, AR-26. Depends on: 4.2, 4.5._

> FR-14 (visual authoring half) / UX-DR74 (tier-laned tech-tree graph) / UX-DR57 (drag out-port to wire a dependency) / UX-DR33 / AR-26. Brownfield: no graph-editing UI exists; Godot GraphEdit/GraphNode is the natural kit, and the right-dock inspector is shared with 4.5. Presentation layer only — it edits prerequisite DATA consumed by 4.2's resolver; it never mutates sim state. Depends on 4.5 (building nodes/inspector) and 4.2 (prerequisite data model + cycle/referential lint reused for in-editor edge validation).

## Epic 5: Faction Definer & the Asymmetric Showcase Factions

_A creator assembles a complete playable faction through a guided wizard, and the two showcase factions become genuinely asymmetric with their FMA identities landed._

**Sequencing note:** Brownfield reality verified against the codebase: alpha_faction.json (The Crucible Covenant) and beta_faction.json (The Sanguine Court) ALREADY carry FMA display_names, Okabe-Ito-ish team colors, themed GLB mesh_paths, and the revised FMA stat sheets (applied by _bmad-output/scripts/apply_fma_faction_stats.py in commit 4558950). So FR-20's content-landing work is mostly VERIFY/HARDEN, not author-from-scratch. What genuinely does NOT exist yet: any FactionRegistry/slot-constant module (AR-3 — MainScene hardcodes paths + a size-5 array), faction validation (AR-39), an ai_preset field, any signature/hero/persistence config on the faction (AR-12), the D1 Modifier/Effects system (owned by Epic 2 — 5.4 hard-depends on Epic 2 and is the ONLY place sim mechanics get wired; the on-death Glut aura is explicitly OUT (Epic 7 D2 seam)), and the Faction Definer wizard + any skirmish/lobby setup scene (no setup/skirmish .tscn exists; factions are chosen via scenario JSON slots today). FactionDefinition.cs treats Buildings as List<UnitDefinition> and has no abilities field. Sequencing honors no-forward-deps: registry+validation+schema precede content/mechanics, which precede the wizard, which precedes selectability, which precedes the playtest gate.

### Story 5.1: FactionRegistry & canonical faction-slot constants (AR-3)

As a engine developer,
I want a single FactionRegistry that owns all faction-count/slot knowledge and the loaded faction definitions,
So that faction loading is centralized for 8 players (not hardcoded in MainScene) and every later faction feature has one place to register and look up factions.

**Acceptance Criteria:**

**Given** the simulation composition root **When** factions are loaded at match start **Then** a FactionRegistry holds per-slot FactionDefinition lookups keyed by slot index **And** PLAYER_COUNT is exposed as 8 and FACTION_ARRAY_SIZE as 9 (incl. Neutral) as named constants **And** no new slot loop uses a bare hardcoded count literal

**Given** the existing alpha and beta faction JSONs **When** the match boots with the default scenario **Then** P1 resolves to alpha (The Crucible Covenant) and P2 to beta (The Sanguine Court) through the registry **And** the rendered/playable result is byte-for-behavior identical to before the refactor (no regression to a working match)

**Given** a registry lookup for an unassigned or out-of-range slot **When** code requests that slot's faction **Then** it returns a safe empty/neutral default rather than throwing or indexing out of bounds

**Given** the FactionRegistry source file **When** inspected **Then** it lives under src/Core (sim layer) and contains no 'using Godot' and no Godot Node types

_Covers: FR-19, AR-3. Depends on: — (none / earlier epics only)._

> Brownfield: MainScene.cs currently hardcodes P1_FACTION_JSON/P2_FACTION_JSON and uses a size-5 _slotFactionDefs array; this story extracts that into a pure-C# registry. AR-3: PLAYER_COUNT=8, FACTION_ARRAY_SIZE=9 (incl. Neutral); no bare FACTION_COUNT in new loops. Sim-layer purity: registry is pure C# (no using Godot); path globalization stays in the presentation caller. This story only builds the registry + migrates existing two-faction load; it does NOT add validation or new fields (those are 5.2).

### Story 5.2: Faction schema extension + validator (AR-39, AR-12, FR-18 data)

As a creator and engine developer,
I want the FactionDefinition schema extended with an AI-preset field, a signature-mechanic field, and hero/persistence config, plus a validator that accepts/rejects a faction with located errors,
So that a faction carries everything needed to be AI-playable and identity-bearing, and malformed factions are caught before they reach a match.

**Acceptance Criteria:**

**Given** the two existing showcase faction JSONs unchanged **When** deserialized after the schema extension **Then** they still load successfully and the new fields take valid defaults **And** the FactionValidator returns PASS for both

**Given** a faction JSON with a building prerequisite that names a non-existent building id **When** validated **Then** the validator returns FAIL with an error that names the offending field and the dangling id

**Given** a faction JSON whose ai_preset is empty or names an unknown preset **When** validated **Then** the validator returns FAIL identifying ai_preset as the cause (covering FR-18 data prerequisite)

**Given** a faction JSON with a color array not of length 4 or with a component outside 0..1, or a duplicate unit id **When** validated **Then** each case returns FAIL with a distinct located error message

**Given** the extended FactionDefinition and FactionValidator **When** the source files are inspected **Then** both reside in the sim layer with no 'using Godot' and the new fields use FixedPoint or plain data only (no float gameplay state introduced into the sim path)

_Covers: FR-18, FR-20, AR-39, AR-12. Depends on: 5.1._

> Adds JSON-deserialized fields to FactionDefinition.cs (sim-layer pure C#): ai_preset (string id, FR-18), signature mechanic descriptor stub (id + display text + a slot to reference a D1 modifier/effect-graph id; the WIRING is 5.4), and hero/persistence config (AR-12 — at least a hero unit reference + persistence flag). AR-39: a FactionValidator returns a structured pass/fail with a located error (which field/which unit id) for: missing required roles, duplicate ids, dangling building prerequisites, unknown ai_preset, color array not length-4 / out of 0..1, missing mesh_path. Validation logic is pure C#. Defaults must keep existing alpha/beta JSON valid (backward-compatible — new fields optional with sane defaults). This story does NOT build the wizard or run any mechanic.

### Story 5.3: Land & harden the FMA showcase content as valid Definer outputs (FR-20)

As a designer,
I want the Crucible Covenant (alpha) and Sanguine Court (beta) confirmed to carry their revised FMA stats, display_names, themed mesh filenames, and a roster that is genuinely asymmetric, all passing faction validation,
So that both shipped factions are demonstrably valid Faction Definer outputs and beta is upgraded from a 1:1 reskin into a distinct role/stat profile.

**Acceptance Criteria:**

**Given** alpha_faction.json and beta_faction.json **When** loaded and run through the 5.2 FactionValidator **Then** both return PASS with zero errors

**Given** the two rosters side by side **When** compared unit-for-unit **Then** at least the worker, a melee, a ranged, and the heavy/siege units differ measurably in stat profile (hp/speed/armor/supply/cost), not merely in display_name **And** alpha's identity reads as faster/lower-hp burst and beta's as slower/higher-hp/tankier, matching fma-faction-design.md

**Given** each unit and building entry **When** inspected **Then** display_name is the FMA themed name and mesh_path points to the faction's themed GLB under assets/models/factions/{alpha|beta}/ that exists on disk

**Given** both faction JSONs **When** inspected for the 5.2 fields **Then** each declares an ai_preset and a signature-mechanic descriptor (alpha=Equal Exchange self-cost; beta=Sanguine Furnace passive HoT) referencing a modifier/effect id

**Given** a match launched with these two factions via the existing scenario path **When** it runs **Then** units render with their per-type meshes and team tint as before (no regression to the working render path)

_Covers: FR-20, AR-39. Depends on: 5.2._

> Brownfield: the FMA display_names, colors, themed GLB mesh_paths, and revised stat sheets are ALREADY in alpha_faction.json/beta_faction.json (commit 4558950). This story is VERIFY + HARDEN + close gaps against _bmad-output/fma-faction-design.md, NOT author-from-scratch. Confirm asymmetry: alpha = fast/fragile/burst (e.g. Quicksilver Runner spd 6.5, lower hp); beta = tanky/slower (e.g. Pride Colossus 340hp spd 2.0, supply re-costs) — roster differs in role/stat profile, not just renames. Reconcile any building hp / supply / cost deltas the design doc specifies that the JSON still misses. Add the signature-mechanic descriptor + ai_preset values to both JSONs using the 5.2 schema (descriptor only — runtime wiring is 5.4). Run the 5.2 validator on both as the gate. Sim-layer rule: no new float gameplay state; data only.

### Story 5.4: Wire the two signature mechanics via D1 Modifiers (FR-20 unique mechanic)

As a designer,
I want Equal Exchange (flat self-HP/matter cost on Covenant signature actions) and Sanguine Furnace (Court passive heal-over-time) to actually run in the simulation,
So that each showcase faction has at least one unique core mechanic that executes, making the asymmetry real rather than cosmetic.

**Acceptance Criteria:**

**Given** Epic 2's D1 Modifier system is present **When** a Court (beta) unit exists in a running sim **Then** it receives the Sanguine Furnace Persistent Heal modifier and its HP regenerates over time toward max at the designed rate **And** a Covenant (alpha) unit in the same sim does NOT regenerate from this mechanic

**Given** a Covenant signature action that costs Equal Exchange **When** it resolves **Then** the caster pays a FLAT self-HP (or matter) price that is independent of the caster's armor type **And** the price is never double-charged across both resources for one action

**Given** the same scenario, seed, and inputs run twice **When** both runs complete the same number of ticks **Then** the end-state golden checksum is byte-identical across the two runs (mechanics are deterministic)

**Given** the on-death Glut aura **When** a Court unit dies in this epic's build **Then** no on-death regen aura fires and its descriptor is flagged as deferred to the Epic 7 D2 seam (documented, not wired)

**Given** the mechanic source files **When** inspected **Then** they contain no 'using Godot', no float gameplay state, and no wall-clock or unseeded randomness

_Covers: FR-20, AR-39. Depends on: 5.3, Epic 2._

> HARD DEPENDS on Epic 2's D1 Modifier system + effect-graph executor (Persistent/Heal + a NON-MATRIX direct-HP stat-delta leaf so Equal Exchange self-cost is flat and armor-independent). This is the ONLY Epic-5 story that touches the sim mechanics path. SCOPE: (a) Sanguine Furnace = a Persistent Heal (HoT) modifier applied to Court units from their faction signature descriptor; (b) Equal Exchange = a flat self-HP (or matter) deduction leaf attached to a Covenant signature action. EXPLICITLY OUT OF SCOPE: the on-death 'Glut' regen aura (depends on the Epic 7 D2 on-death trigger seam) — leave its descriptor present but unwired and note the dependency. Determinism: mechanics run in the sim layer, ascending-id order, FixedPoint only, seeded RNG only; provide a golden-checksum determinism check. Reads the 5.2 signature descriptor; uses the 5.1 registry to know which units belong to which faction.

> ⚠ Quality-review (OWNERSHIP): re-scope as VERIFY/INTEGRATION — confirm the Epic-2 signature mechanics (Equal Exchange, Sanguine Furnace) are correctly wired to the Court/Covenant faction data; do NOT re-implement them here.

### Story 5.5: Faction Definer guided wizard (FR-17, FR-18, UX-DR80, UX-DR40)

As a creator,
I want a guided multi-step flow to assemble a faction (name & color, roster, buildings & tech, starting conditions, AI preset) that writes a valid faction definition,
So that I can author a complete, playable faction in one sitting (target <=12 min) without hand-editing JSON.

**Acceptance Criteria:**

**Given** the Faction Definer entry point **When** a creator steps name/color -> roster -> buildings & tech -> start -> AI preset and finishes **Then** a faction definition file is written containing the chosen name, color, assembled roster from authored units, buildings/tech, starting conditions, and the selected ai_preset

**Given** the color step **When** the creator picks a team color **Then** the swatches are Okabe-Ito colorblind-safe and a distinguishing glyph/label is assigned (UX-DR40)

**Given** a finished faction that fails validation (e.g. a dangling prerequisite or no AI preset chosen) **When** the creator clicks save/finish **Then** save is blocked and the offending step/field is identified by the 5.2 validator's located error

**Given** a creator who wants full control **When** they open the advanced mode **Then** they can edit the raw JSON and the same validator still gates the result (simple + advanced both supported)

**Given** a first-time creator following the simple-mode presets **When** they assemble a faction start to finish **Then** the flow is completable within the <=12 min first-faction target with no step requiring hand-editing JSON

**Given** a player in Play/Skirmish-only context **When** they navigate the in-match HUD **Then** no Faction Definer/authoring control is reachable by accident (creation entry is opt-in only)

_Covers: FR-17, FR-18, AR-12, AR-39. Depends on: 5.2, 5.3._

> Presentation layer (src/UI + a snake_case .tscn) — no setup/wizard scene exists yet, build new. UX-DR80 steps: name/color -> roster -> buildings & tech -> start conditions -> AI preset. UX-DR40: team-color picker offers Okabe-Ito colorblind-safe swatches plus a glyph/label per faction (alpha/beta colors already follow this). Simple mode = preset pickers from authored units/buildings (Epics 2-4 content); advanced mode = raw-JSON escape hatch (layered complexity). On finish, the wizard serializes a FactionDefinition and runs the 5.2 FactionValidator, blocking save with the located error(s) until PASS (AR-39). AI-preset step selects from available presets and writes ai_preset (FR-18). Hero/persistence config surfaced as part of the flow (AR-12). The wizard only PRODUCES a valid faction file; making it selectable in skirmish is 5.6. UI must respect editor-invisible-to-Commanders (creation surface is opt-in).

### Story 5.6: Wizard-authored factions are immediately selectable in playtest & skirmish (FR-19, UX-DR80)

As a creator,
I want a faction I just finished in the Definer to appear and be choosable in playtest and skirmish/MP setup without a restart or manual file move,
So that I get an instant author->play loop and can validate my faction right away.

**Acceptance Criteria:**

**Given** a faction just saved via the Faction Definer **When** the creator opens playtest or skirmish setup without restarting the app **Then** the new faction appears in the selectable faction list

**Given** the new faction is assigned to a player slot and a match starts **When** the match boots **Then** that slot loads the chosen FactionDefinition through the FactionRegistry and its units/buildings are produced/rendered correctly

**Given** the skirmish setup with up to 8 player slots **When** factions are assigned per slot **Then** assignment uses the registry's PLAYER_COUNT-aware API (no hardcoded 2-slot assumption) and the showcase factions remain selectable alongside authored ones

**Given** a faction file that fails validation present in the data folder **When** the setup list is built **Then** it is either excluded or shown as non-selectable with a reason (a broken faction cannot be launched into a match)

_Covers: FR-19, AR-3, UX-DR80. Depends on: 5.1, 5.5._

> Wires the 5.5 wizard output into the runtime selection path. Uses the 5.1 FactionRegistry as the single source of selectable factions; the skirmish/playtest faction picker enumerates the registry (which now includes user-authored factions discovered from the data folder) and assigns the chosen FactionDefinition to a player slot (replacing/extending today's scenario-JSON-slot mechanism). On match boot the selected faction must flow through to the existing per-slot load that 5.1 centralized. UX-DR80 acceptance: output instantly selectable in skirmish. AR-3: slot assignment goes through the registry's PLAYER_COUNT-aware API. Presentation-layer selection UI sends an intent; it does not mutate sim state directly.

### Story 5.7: Playtest-validate asymmetry & AI playability of the showcase factions (FR-20, FR-18)

As a designer,
I want an end-to-end playtest confirming the Covenant and Court play differently, each runs its unique mechanic, and each is playable with/against the AI via its assigned preset,
So that FR-20's 'genuinely asymmetric, validated in playtest' bar and FR-18's AI-playable bar are objectively met for both shipped factions.

**Acceptance Criteria:**

**Given** a skirmish of alpha vs beta with each side run by its ai_preset **When** the match plays out **Then** both AI factions gather, build, train, and engage in combat using their own rosters (FR-18 satisfied for each)

**Given** the running match **When** observed **Then** Court units regenerate HP via the Sanguine Furnace HoT and a Covenant signature action deducts a flat Equal Exchange self-cost — each faction's unique mechanic is observed firing live (FR-20 unique-mechanic bar)

**Given** the two factions in play **When** their behavior/army profiles are compared **Then** they are observably asymmetric (different stat-driven pacing and at least one distinct mechanic), not 1:1 mirrors — captured as a playtest validation note

**Given** the same skirmish seed run twice **When** both runs complete equal ticks **Then** the golden checksum is byte-identical (the playtest path did not break determinism)

_Covers: FR-20, FR-18, AR-39, UX-DR80. Depends on: 5.4, 5.6._

> Integration/validation story — no new systems, it exercises 5.1-5.6 together. Uses the godot-verify / in-engine MCP path to run a skirmish: alpha vs beta, each driven by its ai_preset. Confirms (a) each faction's signature mechanic from 5.4 actually fires in a live match (Court units visibly regenerate via Sanguine Furnace; a Covenant signature action pays Equal Exchange flat self-cost), (b) the AI builds/trains and fights with each faction (FR-18), and (c) the factions feel/play asymmetrically (different effective army composition/behavior, not mirror). The on-death Glut remains out (Epic 7). Capture a brief playtest note as the FR-20 'validated in playtest' artifact. Confirm no determinism regression (golden checksum still byte-identical across two seeded runs).

> ⚠ Quality-review: replace 'observably asymmetric' with an objective criterion (e.g. divergent army composition or a win-rate/behavior delta beyond a threshold) so the asymmetry bar is testable.

### Story 5.8: [ADDED] 'Your First Scenario' guided onboarding (<15-min playable)

As a first-time creator,
I want a guided onboarding flow that walks me from an empty project to a basic playable scenario without reading a manual or touching JSON,
So that I reach the North-Star 'build a game without JSON' moment fast (NFR-2).

**Acceptance Criteria:**

**Given** a first-time creator opening the Creation Suite **When** the onboarding is offered **Then** following it produces a basic playable scenario in <15 minutes with no manual and no JSON editing (NFR-2)

**Given** the unit-authoring journey (UJ-2) **When** the creator opens a unit from a template, tunes Combat/Economy sliders using the explaining tooltips, promotes it to a Hero and picks an ultimate, then presses Play **Then** the retuned unit fights immediately and raw JSON is hidden throughout (UX-DR82)

**Given** any editor field, button, or panel **When** the creator hovers or keyboard-focuses it **Then** a tooltip is shown (tooltip-on-every-control, NFR-2)

_Covers: NFR-2, UX-DR81, UX-DR82. Depends on: Epics 2–4 (the editors), 5.x Faction Definer, 3.x Edit↔Play loop._

> ADDED by coverage review — NFR-2 onboarding + the UJ-2 journey were unowned.

## Epic 6: Map & Terrain Editor

_A creator sculpts and texture-paints terrain with persistent textures, and places entities/start-positions/resources/win-conditions to a ship-quality bar._

**Sequencing note:** Grounded by reading TerrainBrush.cs, EntityPlacer.cs, ScenarioData.cs, ScenarioSerializer.cs, and the terrain setup in MainScene.cs. Headline-defect finding: terrain is regenerated as a flat procedural 256x256 heightmap (import_images) on every SetupTerrain, ScenarioData.TerrainRef is empty, and Terrain3D's sculpted height + painted control maps live only in unsaved in-memory region storage — so neither sculpt nor paint persists across save/load. Story 6.2 fixes this by saving Terrain3D data to the map package and wiring TerrainRef. Ordering 6.1 -> 6.2 -> 6.3 -> 6.4 has no forward dependencies. Sim/Presentation boundary respected: all changes are presentation-layer (TerrainBrush, EntityPlacer, MainScene) plus a TerrainRef string on the pure-C# ScenarioData. Relevant files: D:/Projects/Project_Chimera/godot/src/CreationSuite/TerrainBrush.cs, D:/Projects/Project_Chimera/godot/src/UI/EntityPlacer.cs, D:/Projects/Project_Chimera/godot/src/Core/Definitions/ScenarioData.cs, D:/Projects/Project_Chimera/godot/src/Core/Definitions/ScenarioSerializer.cs, D:/Projects/Project_Chimera/godot/src/Core/MainScene.cs.

### Story 6.1: Verify and harden in-app terrain sculpt + texture-paint

As a map creator,
I want to sculpt terrain height and paint texture layers in-app with a responsive brush and live-updating panel,
So that I can shape and dress a playable map without leaving the creation suite.

**Acceptance Criteria:**

**Given** the project is opened in Godot 4.6.3 after the AR-1 engine bump **When** MainScene runs and SetupTerrain/SetupTerrainBrush execute **Then** the editor log shows '[TerrainBrush] Terrain3DEditor wired to terrain.' and NO 'Terrain3DEditor class not available' / GDExtension load error appears **And** the terrain renders as a real Terrain3D node (not the flat PlaneMesh fallback path)

**Given** Edit mode is active and the terrain brush is toggled on with T **When** I press 1/2/3/4 and left-drag over the terrain **Then** the surface raises, lowers, smooths, and flattens respectively, and the panel mode label updates to match each mode **And** [ and ] change brush size and the size slider value moves in sync

**Given** the brush is in Paint mode (key 5) with the layer picker visible **When** I select each of Grass/Dirt/Rock/Snow and paint **Then** the painted area visibly changes to the selected layer's color and the mode label reads 'Paint layer: {name}' **And** no Terrain3DAssets-acceptance WARNING is logged (placeholder texture assets round-trip onto the terrain successfully)

**Given** the brush panel is shown **When** I click a slider or button inside the panel **Then** the click adjusts the control and does NOT paint terrain underneath the panel (IsOverPanel guard holds)

_Covers: FR-21, AR-1, UX-DR70. Depends on: Epic 3._

> Brownfield: TerrainBrush.cs already wraps Terrain3DEditor via dynamic GDExtension dispatch (Raise/Lower/Smooth/Flatten + Paint with Grass/Dirt/Rock/Snow layers, 1-5 keys, [ ] resize, T toggle, brush panel reusing the Epic 3 creation-suite shell). VERIFY + HARDEN only — confirm the Terrain3D addon still connects after the AR-1 4.6.2->4.6.3 engine bump (SetupEditor / ClassDB.ClassExists('Terrain3DEditor') succeeds, no fallback-to-PlaneMesh, no GDExtension load error). Presentation layer only; do not touch sim. Advances FR-21 (sculpt + paint in-app). UX-DR70 reuse of existing shell, no redesign.

### Story 6.2: Persist sculpted height + painted textures across save/load (headline defect fix)

As a map creator,
I want my sculpted terrain and painted texture layers to be written to disk on save and restored exactly on load,
So that the procedural-texture defect is gone and a saved map looks identical when I reopen it.

**Acceptance Criteria:**

**Given** I have sculpted hills and painted Grass+Rock onto a map in the editor **When** I save the map and then reload it (reopen the scenario) **Then** the heightfield and the painted texture layers are visually identical to before the save (no flat reset, no procedural-only texture) **And** ScenarioData.TerrainRef is written with a non-empty res:// path pointing at the saved terrain data, and that data file/dir exists on disk

**Given** a freshly created map with no terrain edits and an empty TerrainRef **When** it loads **Then** the existing flat-region fallback path is used and the map loads without error (no regression for new maps)

**Given** a saved map with sculpted terrain is loaded twice in separate runs **When** the NavMesh is baked from the reloaded Terrain3D geometry each time **Then** the two bakes produce the same walkable surface (identical source-geometry face count / bounds), confirming height persistence is deterministic across loads

**Given** a map exported as a .chimera.zip package **When** the package is imported on a fresh install **Then** the terrain data referenced by TerrainRef is included in the package and the imported map shows the saved sculpt + paint (persistence survives packaging)

_Covers: FR-21, AR-1. Depends on: 6.1._

> HEADLINE FIX. Root cause: ScenarioData.TerrainRef is empty and SetupTerrain always regenerates a flat procedural 256x256 heightmap via import_images on every load, while Terrain3D's sculpted heightmap AND painted control maps live only in the in-memory Terrain3DData/region storage that is never serialized — so height and textures vanish on reload. Fix in the PRESENTATION layer only: on map save, save Terrain3D data (data_directory / region files or the Terrain3DData resource) into the map package directory and write its res:// path into ScenarioData.TerrainRef; on load, when TerrainRef is non-empty, load that terrain data instead of regenerating the flat region. Keep the empty-TerrainRef flat-plane fallback intact (don't regress new maps). ScenarioData/ScenarioSerializer stay pure C#/JSON — only TerrainRef wiring changes, no sim-state types added. Include a reload-equality check because terrain height feeds the NavMesh bake. Advances FR-21 persistence.

### Story 6.3: Terrain stroke undo/redo and _store_undo push_error cleanup

As a map creator,
I want Ctrl+Z / Ctrl+Y to undo and redo my terrain sculpt and paint strokes without console error spam,
So that I can experiment with terrain edits safely and the editor log stays clean for 1.0.

**Acceptance Criteria:**

**Given** Edit mode with the terrain brush active and I have completed a sculpt or paint stroke **When** I press Ctrl+Z **Then** the terrain reverts to its pre-stroke height/texture state, and pressing Ctrl+Y reapplies the stroke **And** multiple sequential strokes undo/redo in last-in-first-out order

**Given** a normal sculpting session of several strokes in-engine (no EditorPlugin host) **When** I watch the editor/console log **Then** no per-stroke _store_undo push_error red lines appear (the documented noise is suppressed or designed out) **And** the chosen disposition is explained in a code comment in TerrainBrush

**Given** terrain undo/redo and the existing EntityPlacer entity undo/redo both exist **When** I interleave a unit placement and a terrain stroke and press Ctrl+Z twice **Then** each action is undone correctly with no crash and no corruption of either the entity arrays or the terrain data

_Covers: FR-21, AR-1, UX-DR59. Depends on: 6.1, 6.2._

> Two parts. (1) UX-DR59 undo/redo for terrain: EntityPlacer already has EditorHistory on Ctrl+Z/Y for entities, but terrain strokes are NOT on an undo stack. Capture a before/after of the affected Terrain3D region/control data per completed stroke (in EndPaint) and push undo/redo onto the shared editor history so Ctrl+Z reverts a sculpt/paint stroke and Ctrl+Y reapplies it. Presentation-layer only. (2) AR-1 cleanup: the documented non-fatal _store_undo push_error noise (Terrain3DEditor.start_operation calls _store_undo which errors at runtime with no EditorPlugin host) fires once per stroke — decide and implement the 1.0 disposition (suppress/route around it so a normal session produces no per-stroke red error lines) and document the choice in code comments. Advances FR-21.

### Story 6.4: Verify entity, start-position, resource-node, and win-condition placement to ship bar

As a map creator,
I want to place units, buildings, resource nodes, and start positions and set the win condition with a polished ghost-preview placement flow,
So that I can author a complete, playable map that meets a ship-quality bar.

**Acceptance Criteria:**

**Given** Edit mode with a placement type selected in the palette **When** I move the cursor over the terrain **Then** a semi-transparent ghost mesh follows the cursor with the correct shape and color for the selected type (unit/building/ore/start-pos) **And** pressing G toggles grid-snap and the ghost snaps to the 1-unit grid, with the snap button reflecting ON/OFF

**Given** the ghost preview is following the cursor in a placement mode **When** I left-click **Then** the entity/node/start-position is placed at the ghost location **And** right-click or Esc cancels placement: it exits the active placement mode and hides the ghost without placing anything (UX-DR56)

**Given** I have placed units, a building, a resource node, and moved both start positions, and I delete one item **When** I press Ctrl+Z then Ctrl+Y **Then** the delete is undone (the item reappears with its original stats) and redone, matching UX-DR59 **And** resource node supply/rate and per-slot start ore set via the spinners are applied to the created entities

**Given** I set the win condition to EliminateAllUnits in the win-condition panel and save the map **When** I reload the map **Then** ScenarioData.WinCondition is EliminateAllUnits and the panel reflects the saved choice **And** all placed buildings, units, resource nodes, and start positions round-trip through save/load with correct positions, owners, and types

_Covers: FR-22, UX-DR56, UX-DR59, UX-DR70. Depends on: 6.1._

> Brownfield: EntityPlacer.cs already implements the full palette (P1/P2 unit, Ore Node, Building, Start Pos), a ghost mesh that follows the cursor, G grid-snap, Delete with undo, EditorHistory for Ctrl+Z/Y, configurable node supply/rate and per-slot start ore; the win-condition panel exists in MainScene. VERIFY-TO-SHIP-BAR + close UX gaps against UX-DR56. Headline gap: UX-DR56 specifies left-click place / RIGHT-CLICK or ESC to CANCEL — verify/add a cancel that exits the active placement mode and hides the ghost (currently only left-click place exists; no explicit right-click/Esc cancel). Confirm ghost shape/color previews each mode and snaps on G. Confirm win-condition selection persists into ScenarioData (DestroyAllBuildings / EliminateAllUnits) on save/load. Reuses the Epic 3 creation-suite shell (UX-DR70). Advances FR-22.

## Epic 7: Rich Trigger DSL & Custom Runtime UI

_'Build any game': the trigger DSL gains variables/arithmetic/collections/loops/timers/custom-events plus trigger-driven custom UI, across four interoperating tiers, all deterministic and server-validatable._

### Story 7.1a: Trigger-layer determinism prerequisites (ordering, Fixed, culture)

As a platform engineer hardening the trigger layer for multiplayer,
I want the as-built trigger ordering, float round-trips, and culture-sensitivity nondeterminisms removed and the golden re-pinned,
So that the trigger system is deterministic before it is rebuilt onto the graph IR.

**Acceptance Criteria:**

**Given** the as-built unstable Array.Sort by Priority (ScenarioDirector :192) and Dictionary timer enumeration (:149) **When** trigger evaluation order is replaced with an explicit total order (Priority desc, then ascending persistent node-id) and timer/var stores become dense index-keyed arrays **Then** two equal-priority triggers writing a shared variable produce a byte-identical SimChecksum across two headless runs and across runtime versions **And** two timers expiring on the same tick fire in declaration-index order deterministically

**Given** the in-tick Fixed->float->"F2"->float.TryParse round trip (ScenarioDirector :168/:170/:252) and the float-epsilon Compare (:364) **When** FiredEvent payloads are retyped to carry Fixed.Raw ints, all threshold compares become Fixed-vs-Fixed, OnSpawnUnit X/Z and TriggerDefinition numeric fields are retyped to Fixed/int at the deserialization boundary, and InvariantCulture is pinned process-wide **Then** a threshold trigger fires identically on two clients with zero float or culture-sensitive code in the tick path **And** the existing scenarios still produce identical observable outcomes (golden harness green); the checksum baseline re-pin is recorded as a named expected event

_Covers: FR-27, AR-13, AR-16. Depends on: — (none / earlier epics only)._

> Split from former 7.1 — the determinism fixes (D3.4 A17 ordering/float) pulled out as a standalone, golden-re-pinning prerequisite. Depends on the prior D1 effect-graph epic (SimRng, golden harness). Must land before the IR rebuild so the migration starts from a deterministic baseline.

### Story 7.1b: Graph-canonical DSL IR foundation + lossless migration

As a platform engineer unifying the trigger representation,
I want the trigger system rebuilt onto a single editor-agnostic graph IR (persistent integer node ids, typed exec + data edges) that embeds D1 effect subgraphs, with a closed-registry NodeBase converter and lossless migration from the flat format,
So that every later DSL feature lands on one deterministic, server-validatable representation instead of the flat polled ECA.

**Acceptance Criteria:**

**Given** the existing flat TriggerDefinition[] in ScenarioData and ScenarioDirector **When** graph IR DTOs are introduced in src/Dsl with an id-keyed node list, two sparse typed edge-lists (exec, data) and persistent integer node ids **Then** the trigger graph is a superset that embeds D1 EffectDef action subgraphs unchanged (no second executor), and a NodeBase JsonConverter over a CLOSED type registry round-trips every node by id with [JsonPolymorphic] forbidden **And** an unknown node kind is rejected at parse with a located error naming the kind **And** the graph section serializes canonically (nodes sorted by id, edges by (src,srcPort,dst,dstPort))

**Given** an existing flat-format scenario JSON on disk **When** it is loaded **Then** it migrates losslessly into the graph IR (T2 sentence list is a linear projection of an exec-edge chain) with no second serialization path

_Covers: FR-23, FR-27, FR-28, AR-10, AR-21, AR-22. Depends on: 7.1a._

> Split from former 7.1. Graph-canonical serialization established from step one even though only T2/T4 author it. AR-10/AR-21/AR-22, FR-27, FR-28.

### Story 7.2: Typed scoped variables, deterministic timers, and verify-to-ship ECA

As a scenario creator authoring game logic,
I want typed, scoped variables (Int/Fixed/Bool/EntityRef/FactionRef/Point/TimerRef/Array) and deterministic named timers, declared in ScenarioData and edited in the ECA trigger list,
So that I can hold scoreboard/economy/per-player state and schedule delayed logic, and the basic ECA authoring works to a shippable bar.

**Acceptance Criteria:**

**Given** the dense-index var/timer stores from 7.1 **When** a DslVarTable is hoisted into a top-level sim store (sibling of BuildingStore/ResourceStore) with a closed value-type set and scopes Global / Per-player(0..7) / Trigger-local **Then** SimChecksum.Compute is widened to fold every live global/per-player value (and timers) in declaration-index order, and a variable round-trips through the schema (name/type/scope/initial as Fixed.Raw) **And** loop counters and trigger-local values are lexically scoped and freed at trigger end, never engine-global

**Given** a scenario that previously used set_variable / variable_comparison / create_timer / timer_expires **When** it is migrated to the typed table and timers expressed as integer ticks (no float->int truncation) **Then** observable behavior is identical to the legacy flat path (golden harness green)

**Given** the existing Trigger Editor panel (FR-23, basic, verify-to-ship) **When** a creator adds/edits/enables/deletes an ECA trigger whose actions embed a D1 effect subgraph and reads/writes a declared variable **Then** the trigger persists into the graph IR and fires correctly in a running match **And** the editor surfaces a simple preset entry AND the raw-IR escape hatch (layered complexity)

_Covers: FR-23, FR-24, FR-27, AR-10, AR-21, UX-DR79. Depends on: 7.1._

> D2 D1s var table + ECA verify (FR-23). Variables are the 'typed, scoped' half of FR-24. UX-DR79 trigger list with typed/scoped variables. Depends only on 7.1.

### Story 7.3: Fixed-point arithmetic and boolean expression layer

As a scenario creator writing trigger conditions and computed values,
I want a CEL-shaped pure, typed, side-effect-free expression sublanguage over my variables (Fixed-point arithmetic, OR/NOT/grouping, bounded built-ins) that compiles at load and evaluates cheaply in the tick,
So that conditions can be richer than flat ANDed comparisons and variable assignments can take computed right-hand sides, all deterministically.

**Acceptance Criteria:**

**Given** the typed variables from 7.2 and the as-built pure-AND AllConditionsMet (:263) **When** a two-phase expression evaluator is added (parse + type-check + cost-estimate at load; evaluate pre-checked Fixed-only AST in the tick) supporting + - * / mod, > < >= <= == !=, && || !, and bounded built-ins count/distance/min/max/abs **Then** a condition tree using OR/NOT/grouping evaluates correctly and a SetVariable node assigns from an expression RHS **And** division by zero is rejected at load (validation), not at runtime **And** no float type exists anywhere in an expression; all fractional numerics are Fixed 16.16

**Given** an authored expression with a type mismatch (e.g. Bool + Fixed) **When** it is loaded **Then** it is rejected at load with a located error **And** a well-typed expression produces a byte-identical SimChecksum across two headless runs

**Given** the same data subgraph rendered as a typed expression tree **When** it is serialized **Then** it lives in the graph IR as data edges (wire color = type), preserving the single-IR contract

_Covers: FR-24, FR-27, AR-10, UX-DR79. Depends on: 7.2._

> D2 D2s expression layer. The arithmetic/boolean-expression half of FR-24. Strict extension of D1's bounded grammar. Depends on 7.2 (needs typed variables to operate on).

### Story 7.4: Custom events: define, raise, subscribe with acyclic same-tick dispatch

As a scenario creator building decoupled game-logic modules,
I want to define named custom events (with typed params), raise them from triggers, and subscribe handler triggers, with same-tick dispatch proven acyclic at load and bounded by named caps,
So that I can build decoupled modules (and the Sanguine Court's on-death Glut seam) without the within-tick recursion that crashes WC3-style engines.

**Acceptance Criteria:**

**Given** the single priority sweep in EvaluateTriggers **When** a closed custom-event registry (names + typed params + per-event allowed-raiser set) and a RaiseEvent node are added, and the eval loop is rewritten as a same-tick work-list drain **Then** a raised event invokes its subscribed handlers within the tick, run-once fires at most once per match even if re-raised, and cooldown suppresses same-tick re-entry **And** the event-dispatch graph is proven a DAG at load and a cycle is rejected with a located error **And** the eval/event path performs zero per-tick heap allocation (allocate-at-load)

**Given** a deep/wide custom-event cascade **When** the load-time cost estimator sums cost over the transitive closure of the event DAG **Then** a cascade exceeding MaxCascadeOps/MaxEventFanOut/MaxEventCascadeDepth is rejected AT LOAD (not by runtime fuel) **And** legitimate state-machine feedback (A->B->A) is expressed via RaiseEventNextTick bounded by MaxNextTickEventQueue and folded into SimChecksum

**Given** the built-in unit_dies event carrying only victim slot **When** the combat layer's killer/last-hit attribution is threaded onto the death event payload (typed EntityRef/FactionRef) **Then** an on-death handler can credit the killer faction (enabling the Court Glut on-death regen aura and kill-credit logic) **And** the named caps are corpus-validated as a gate before lock, not treated as a free tuning dial

_Covers: FR-25, FR-27, AR-10, AR-11, UX-DR79. Depends on: 7.3._

> D2 D3s(payloads)+D4s(custom events). Completes the D2 On-death seam for Epic 5's Court Glut. AR-11 acyclic-DAG/cascade-cost; FR-25. Depends on 7.3 (RaiseEvent args use expressions; handlers gate on expression conditions).

> ⚠ Quality-review: ship the GENERIC on-death event seam only (event payload + killer-attribution dispatch). It must be testable without Epic-5 faction content; the Court 'Glut' aura is faction data that consumes this seam.

### Story 7.5: Bounded ForEach / ForEachBatched loops, arrays, and Layer-3 fuel

As a scenario creator building wave/AoE/iteration logic,
I want static-capacity arrays/collections and the only sanctioned loop forms (ForEach over a snapshotted ascending-id collection, ForEachBatched for large sets) plus a checksummed per-tick fuel seatbelt,
So that I can build TD waves, autochess pools, and AoE-over-units patterns while the simulation stays provably bounded per tick.

**Acceptance Criteria:**

**Given** the expression layer and event model **When** Array<scalar> variables (capacity <= MaxArrayCapacity) and ForEach/ForEachBatched/Branch nodes are added, iterating a collection snapshotted at loop entry in ascending entity-id order **Then** no While/recursion/goto form exists in the grammar (cannot be expressed), and a single-tick ForEach over a finite group produces identical results across two headless runs **And** nesting whose cap-product worst-case exceeds MaxDslOpsPerTrigger is rejected AT LOAD

**Given** a group whose provable max size exceeds the loop cap **When** the scenario is loaded **Then** it is a load-time validator error directing the author to ForEachBatched or an explicit ForEachUpTo(cap) (a loud authored choice), never a silent runtime truncation **And** the as-built Math.Min(count,50) clamp is removed; the spawn cap reconciles to one named constant (64)

**Given** a malformed/hand-edited definition that escapes the load gate **When** the per-tick fuel budget (MaxDslOpsPerTick, folded into SimChecksum) is exhausted **Then** execution halts deterministically at a whole-trigger boundary (never mid-Sequence) identically on two headless clients with no torn state **And** the fuel-counter checksum re-pin is recorded as a named expected event

_Covers: FR-24, FR-27, AR-10, AR-11, AR-13, UX-DR79. Depends on: 7.4._

> D2 D5s loops/fuel. Completes FR-24 (collections + loops). AR-11 Layer-0/Layer-3, AR-13 SimRng for any loop randomness. Depends on 7.4 (ForEachBatched cross-tick drip rides the next-tick event queue; loops may raise events).

### Story 7.6: Authoritative server-side load-time validator gate (no escape hatch)

As a multiplayer host loading a creator scenario before any tick,
I want the type-checker + graph-linter + cap/cost validator promoted to a mandatory pre-tick gate at the ApplyScenario / LoadScenario boundary on every load path, with the canonical-model hash and versioning underneath it,
So that no hand-edited, AI-generated, or replay-loaded scenario can enter the deterministic sim unchecked, and the four tiers share one validatable representation.

**Acceptance Criteria:**

**Given** LoadScenario does zero validation today and the LLM validator is advisory-only inside the generation Task **When** Validate(model) is invoked over the ScenarioData model at the ApplyScenario boundary (covering file, AI-gen, fallback, and replay-loaded paths) **Then** a malformed hand-crafted scenario AND a malformed AI-generated scenario are each rejected pre-tick with the correct specific located error, and all valid scenarios pass **And** there is no scripting escape hatch: every construct is from the closed registry/grammar and is statically checkable

**Given** the graph IR in ScenarioData **When** the canonical-model hash (FNV-64 over Fixed.Raw, fixed field order, enums as stable name, _editor annotations excluded) is computed and a schema_version + checksum_algo_version are added with legacy amnesty (absent => v1) **Then** a cosmetic-only edit yields the same scenarioHash and a sim-semantic edit yields a different hash, and a re-save / key-reorder / whitespace change does not change the hash **And** hash 0 is treated as not-computed => block; a legitimate canonical-0 remaps to 1

**Given** the four authoring tiers (T1 preset, T2 ECA, T3 graph, T4 NL) **When** any tier emits a scenario **Then** it is validated by this same gate so AI authoring is no more dangerous than human authoring (safe-by-construction) **And** the worst-case canonical hash on a max-caps scenario completes within the lobby-handshake budget (low tens of ms)

_Covers: FR-27, FR-28, AR-21, AR-22, AR-23, AR-24. Depends on: 7.5._

> D2 D6s + D3.1/D3.6. The D3 serialization/versioning contract (single ContentLoader path, NodeBase converter, closed registry, canonical-model hash, schema_version/migration). FR-27 (server-validatable, no escape hatch) + FR-28 (one validatable IR all tiers share). Depends on 7.5 (validator must cover loop/fuel/array caps).

### Story 7.7: Custom runtime UI read rail: declarative widget tree + version-stamped readback

As a scenario creator showing live game state to the player,
I want a declarative closed-vocabulary widget tree in ScenarioData (Panel/Label/Counter/ProgressBar/Timer/Leaderboard/FloatingText/ItemList) bound to DSL variables, fed by a double-buffered version-stamped DslVarReadback published once per tick,
So that I can build scoreboards, TD wave counters, and RPG-style readouts that update live without strings ever entering the deterministic tick.

**Acceptance Criteria:**

**Given** the validated DslVarTable from 7.6 **When** a CustomUiBridge (modeled on FogOfWarBridge) publishes a read-only, per-variable version/dirty-stamped DslVarReadback at the tick boundary and presentation widgets pull in _Process and re-format only on version change **Then** a Counter/Leaderboard bound via {variable} updates live, formatting (int->string, Fixed->mm:ss) happens presentation-side, and strings never enter the tick **And** the readback is a copy of already-checksummed state and is NOT in SimChecksum; it cannot desync

**Given** the closed widget vocabulary with an ItemList data-bound repeater **When** a creator authors widgets on a 16:9 canvas via the widget-palette builder with 9-point anchors, {variable} binding, and trigger-driven visibility **Then** the widget tree persists in ScenarioData, is covered by scenarioHash (divergent UIs refuse to start), and renders inside the 16:9 safe area **And** widget count/depth/list-rows caps (<=256 / <=8 / <=64) are rejected at load and asserted at runtime, never clamped **And** every BindVar resolves and type-matches the closed variable registry at load

_Covers: FR-26, FR-27, AR-32, AR-21, UX-DR76, UX-DR58, UX-DR48. Depends on: 7.6._

> D2 D8s read path. AR-32 READ rail; UX-DR76 widget-palette builder, UX-DR58 direct-manipulation authoring, UX-DR48 16:9 safe-area. Depends on 7.6 (binds resolve against the validated variable registry; UI schema rides the load gate).

### Story 7.8: Custom runtime UI write rail: Button-raised DslEventCommand on the lockstep bus

As a scenario creator building interactive runtime UI,
I want Buttons that raise custom events through a net-new DslEventCommand on the lockstep command bus (with per-event allowed-raiser authorization), plus local-only buttons on a closed presentation-action whitelist,
So that players can vote, buy from a shop, or trigger waves via custom UI in both single-player and multiplayer, replayed identically.

**Acceptance Criteria:**

**Given** the custom events from 7.4 and the read-rail UI from 7.7 **When** a Button.Pressed handler calls LockstepManager.EnqueueDslEvent(eventId, args) capturing LocalFaction as raiser, riding TickCommandPacket (parallel capped event list, MaxDslEventsPerTick) **Then** a button press defers by _currentDelay and applies at the same exec tick on two headless clients, and ApplyDslEvents enforces the per-event allowed-raiser set as sim-side authorization (never client-side button-disable) **And** the pinned tick-phase order is apply DSL events -> sim systems tick -> ScenarioDirector drains the bus

**Given** the replay format is hardwired to UnitOrder (VERSION=1) **When** the write path is wired **Then** ReplayRecorder.VERSION bumps to 2 with a DSL-event record kind, ReplayPlayer gains a parse+apply branch, and DSL-event application is threaded through all four command-application sites (live, spectator, ReplayPlayer.ApplyOrders, recorder) **And** a recorded match with a button press replays bit-identically under v2 and a v1 replay is hard-rejected

**Given** a local-only button (toggle a panel, open a sub-panel) **When** it uses the closed presentation-action whitelist (ToggleWidgetVisible/OpenSubPanel/CloseSelf/SetLocalUiVar) **Then** the validator proves it cannot touch any DSL variable or event (disjoint sim/local-UI namespaces) and provably cannot affect SimChecksum

_Covers: FR-26, FR-27, AR-32, AR-23, AR-24. Depends on: 7.7, 7.4._

> D2 D9s + D3.9 write path: network + replay-v2 + four apply-sites + net-new authorization. AR-32 WRITE rail. Depends on 7.7 (buttons live in the widget tree) and 7.4 (buttons raise registered custom events).

### Story 7.9: T3 visual node-graph editor view (additive) over the shared IR

As a scenario creator who prefers visual authoring,
I want a GraphEdit-based node-graph editor that renders and edits the already-graph-canonical IR (typed exec + data wires, on-node error rendering) as a replaceable view, with the T2 sentence editor showing a graph-only fallback,
So that I can author the same logic visually as T1/T2/T4, interoperating on one underlying representation, with no content migration.

**Acceptance Criteria:**

**Given** the graph-canonical IR shipped since 7.1 (no GraphEdit/Godot types in the IR) **When** a GraphEdit view is built that renders nodes, typed exec/data edges (wire color = type), variables side table, and routes the load-time validator errors onto the offending node **Then** a round-trip T2 -> T3 -> T2 preserves the IR by persistent-node-id equality with NO content migration step **And** full bidirectional editing is the IR-native tier; the T2 sentence editor shows a non-destructive read-only 'edit in graph view' fallback for graph-only constructs

**Given** T3 node positions and other authoring affordances **When** the graph is saved **Then** they persist in the excluded _editor annotation channel (verbatim) and are NOT in the scenarioHash, so a cosmetic layout move yields the same hash **And** if GraphEdit proves inadequate it can be swapped for a custom view without touching the IR or the other three tiers

_Covers: FR-28, AR-10, AR-21, UX-DR79, UX-DR57. Depends on: 7.6._

> D2 D7s additive T3 view + D3.7 annotation channel. FR-28 (T3 tier on the one IR), UX-DR57 graph wiring, UX-DR79 node graph. GraphEdit is 'Experimental' (briefing residual risk) - mitigated by editor-agnostic IR + replaceable view. Depends on 7.6 (renders errors from the validator gate) and the graph IR from 7.1.

## Epic 8: AI-Assisted Creation

_An AI collaborator across every editor — provider config, generation of triggers/maps/units/factions, and balance analysis — degrading gracefully to a fully-usable manual suite._

**Sequencing note:** Brownfield reality verified in code: only LLMService.cs (two generate methods, hardcoded Claude->Ollama fallback, plaintext [Export] AnthropicApiKey on MainScene line 206, ModIoApiKey line 200), SettingsData.cs (no provider/secret fields), SettingsManager.cs (user://settings.json load/save/apply), and two editor panels (TriggerEditorPanel, MapGeneratorPanel) exist. No Unit/Ability/Hero/Faction editor and no balance analysis exist. Sequencing rationale: secrets+settings plumbing (8.1, 8.2) must land before the provider abstraction (8.3) that consumes them; the four-state UI / graceful degrade (8.3) must exist before generation features route through it (8.4-8.7); trigger (8.4) and map (8.5) are verify/extend of existing code; unit/ability/hero/faction draft generation (8.6) is mostly new; balance analysis (8.7) is fully new and depends on the provider stack and the draft data shapes. ENTIRE epic is authoring-layer only (AR-33): zero sim coupling, AI float output passes through the SAME validation gate with float->Fixed quantize before any canonical hash. Earlier-epic dependencies (editor scaffolds for units/abilities/heroes/factions, the D3 validation gate, the trigger DSL) are assumed delivered by earlier epics; if an editor host is missing, 8.6's draft can still land as editable JSON into the existing data/file flow.

### Story 8.1: ISecretStore + rip out plaintext [Export] secret fields

As a creator configuring AI features,
I want my API keys stored in a gitignored per-user secret file instead of committed Inspector fields,
So that my key is never hardcoded, committed, or shipped in a build.

**Acceptance Criteria:**

**Given** a fresh project with no secret file **When** the game reads a provider key **Then** ISecretStore returns empty and no exception is thrown **And** nothing is written to disk until a key is explicitly saved

**Given** a key saved via ISecretStore.Set **When** the game restarts **Then** the key is read back from user://secrets/llm.key **And** the secrets path and *.key are gitignored

**Given** the codebase after this story **When** I grep MainScene and all [Export] members **Then** no [Export] field of type string named AnthropicApiKey or ModIoApiKey exists **And** LLMService and ModIoService obtain their key from ISecretStore

**Given** a release export of the project **When** SecretExclusionTest scans the build output for any stored key string **Then** no key string is found in the build output **And** the test fails loudly if a key leaks into the PCK

_Covers: FR-29, AR-34, AR-33. Depends on: — (none / earlier epics only)._

> AR-34. Net-new ISecretStore (pure C#, Godot-free interface) with a file-backed impl over a gitignored user://secrets/llm.key. RIP OUT both [Export] plaintext fields on MainScene: AnthropicApiKey (line ~206) and ModIoApiKey (line ~200); migrate any existing AnthropicApiKey value into the secret store on first run. LLMService.AnthropicApiKey and ModIoService construction must read from the secret store. Add user://secrets/ (and *.key) to .gitignore. Add SecretExclusionTest asserting no key string appears in the build output / exported PCK.

### Story 8.2: Provider/model/baseUrl fields in versioned SettingsData

As a creator with an LLM account,
I want to pick my provider, model, and base URL in persisted settings,
So that my choice survives restarts and is editable without recompiling.

**Acceptance Criteria:**

**Given** an existing settings.json with no provider fields **When** SettingsManager loads it **Then** provider defaults to a curated provider, model defaults to claude-sonnet-4-6, and no error is raised **And** a schema version field is present and migrated forward

**Given** I select a provider and a curated model and save **When** I restart **Then** the same provider/model/baseUrl are restored from settings.json **And** the key is read from ISecretStore, never written into settings.json

**Given** a provider with a curated model list **When** I type a model name not in the list into the free-text override **Then** the override value is persisted and used **And** switching providers shows that provider's curated model list

_Covers: FR-29, AR-5, AR-34, AR-33. Depends on: 8.1._

> AR-5 (versioned SettingsData must exist before provider fields land) + AR-34 (move provider/model/baseUrl into versioned SettingsData). Add a schema version field and provider/model/baseUrl fields to SettingsData (currently has none). Curated data-driven provider list (Anthropic, Ollama, OpenRouter) and per-provider curated model list PLUS a free-text override field. Default model claude-sonnet-4-6. Settings continue to load/save via SettingsManager (user://settings.json) with safe defaults so older save files still load. The API key itself stays in ISecretStore (8.1), NOT in settings.json.

### Story 8.3a: Godot-free ILLMProvider over three adapters (Anthropic / Ollama / OpenRouter)

As a creator using AI features,
I want one Godot-free provider abstraction that talks to my selected provider with no vendor SDK,
So that the selected provider is authoritative and the sim stays AOT-clean and uncoupled from AI.

**Acceptance Criteria:**

**Given** a NormalizedRequest and each of the three providers selected in turn **When** GenerateAsync runs against a stub endpoint **Then** the correct adapter (Anthropic messages / Ollama chat / OpenRouter chat completions) is used and returns a NormalizedResult **And** no vendor SDK is referenced; only System.Net.Http is used

**Given** a selected provider that fails **When** GenerateAsync runs **Then** it does NOT silently fall back to another provider **And** the selected provider is authoritative

**Given** a configured reachable provider **When** a minimal round-trip runs **Then** it succeeds **And** buffered response bytes are capped and the cloud host is on the pinned allowlist; keys are read via the ISecretStore seam, never an [Export] field

_Covers: FR-29, AR-33, AR-34. Depends on: 8.1, 8.2._

> Split from former 8.3. Hand-rolled Godot-free ILLMProvider GenerateAsync(NormalizedRequest)->NormalizedResult over three adapters: Anthropic /v1/messages, Ollama /api/chat, OpenRouter /chat/completions. NO vendor SDK (AOT-clean). Blocking v1. SELECTED PROVIDER IS AUTHORITATIVE — REPLACES the implicit Claude->Ollama fallback in LLMService (~118-124, ~455-459). Pin/allowlist cloud hosts; cap buffered bytes. Authoring-layer only. AR-34 ISecretStore key storage established in 8.1/8.2; this stack reads keys via the secret seam.

### Story 8.3b: FR-34 four-state availability UI + Test-connection

As a creator,
I want the AI panels to clearly tell me when AI is unavailable and let me test my connection,
So that the suite stays fully usable manually in every state.

**Acceptance Criteria:**

**Given** no provider configured / provider-but-no-key / unreachable host / response that fails validation **When** I open an AI panel or hit Test-connection **Then** each case shows its own distinct four-state message **And** the editor remains fully usable manually in every state

**Given** a configured reachable provider **When** I press Test-connection **Then** a minimal round-trip via 8.3a succeeds and reports a healthy state

_Covers: FR-34, AR-33, UX-DR52. Depends on: 8.3a._

> Split from former 8.3. FR-34 four states: (1) no provider, (2) provider set but no key, (3) unreachable, (4) returned-but-failed-validation — each a distinct clear message. Test-connection performs a minimal round-trip and reports which state applies. When unavailable, AI affordances disable/explain but every editor stays usable manually (UX-DR52 voice/microcopy).

### Story 8.4: Trigger generation on the new provider stack + new DSL constructs

As a scenario author,
I want to generate a trigger from a natural-language prompt, including newer DSL constructs, and review/edit it before applying,
So that I get a correct, editable trigger no matter which provider I use.

**Acceptance Criteria:**

**Given** a configured provider and an NL trigger prompt **When** I generate **Then** the request goes through ILLMProvider for the selected provider and a draft trigger appears for review/edit before apply **And** a 'Transmuting...' spinner shows during the call

**Given** a DSL construct added since the original prompt (an event/condition/action not in the old schema) **When** I prompt for it **Then** the system prompt advertises it and the validator accepts a correct instance of it **And** an unknown construct is rejected with a located error

**Given** a generated trigger containing float numeric fields **When** it passes validation and is applied **Then** floats are quantized to Fixed before persistence so two runs hash identically **And** the edited-then-applied trigger round-trips through the same validation gate

**Given** no provider available **When** I open the trigger generator **Then** a four-state message explains AI is unavailable **And** I can still author triggers manually in the panel

_Covers: FR-30, AR-33, UX-DR52, UX-DR65. Depends on: 8.3._

> FR-30 (built — verify, extend). Re-point LLMService.GenerateTriggerAsync from its hardcoded Claude/Ollama calls to ILLMProvider (8.3). VERIFY the existing 5-pass trigger Validate still gates output and the review/edit-before-apply flow in TriggerEditorPanel still works. EXTEND the prompt schema + validator to cover DSL constructs added by earlier epics that the current prompt omits (audit the live DSL event/condition/action set vs the hardcoded schema in BuildSystemPrompt). UX-DR52 'Transmuting...' spinner during generation; UX-DR65 microcopy. Authoring-layer only: any float in generated values is quantized to Fixed by the same validation gate before persistence/hash.

### Story 8.5: Verify map generation + relax/parameterize the 7-pass clamps

As a scenario author building non-RTS scenarios,
I want map generation that validates before load and does not force RTS-only limits,
So that I can generate maps for scenario types that need more than 6 units, more slots, or different faction paths.

**Acceptance Criteria:**

**Given** an RTS-preset scenario **When** I generate a map **Then** validation behaves as today (RTS clamps still apply) and the map loads only after passing **And** the request routes through ILLMProvider

**Given** a non-RTS scenario type that permits more than 6 units and more than 2 slots **When** I generate **Then** the validator uses the scenario-type parameters instead of the hardcoded 6-unit / 2-slot / forced-faction-path limits **And** a >6-unit non-RTS map validates successfully where the old code would have rejected it

**Given** a generated map that violates the active scenario-type's limits **When** validation runs **Then** it is rejected with a located, human-readable error before any load **And** positions/spacing/bounds checks still run

**Given** no provider available **When** I open the map generator **Then** a four-state message explains AI is unavailable and I can still build/place maps manually **And** a 'Transmuting...' spinner shows when a provider is generating

_Covers: FR-31, AR-33, UX-DR52. Depends on: 8.3._

> FR-31 (built — verify) + the AR-33 requirement to relax/parameterize the hard-clamps. Re-point LLMService.GenerateScenarioAsync to ILLMProvider. VERIFY the 7-pass ValidateScenario still runs and the map loads only after validation. RELAX/PARAMETERIZE the RTS-only constraints currently hardcoded: <=6 combat units/faction (pass 7), the exactly-2 player-slots assumption (pass 2), and forced faction-JSON paths (pass 2 overwrites slot.FactionJson) — these limits become parameters driven by the scenario type rather than hardcoded, so non-RTS scenarios are not wrongly clamped. RTS presets keep today's defaults to avoid regression. UX-DR52 spinner.

### Story 8.6: Generate unit / ability / hero / faction drafts as editable data

As a content creator,
I want to generate a unit, ability, hero, or faction draft (stats, name, lore) from a prompt as editable data,
So that I get a fast starting point I can fully edit, not a black box.

**Acceptance Criteria:**

**Given** a configured provider and a prompt for a unit (and likewise ability, hero, faction) **When** I generate **Then** a draft with stats + name + lore appears as fully editable data matching the existing definition schema **And** a 'Transmuting...' spinner shows during generation

**Given** a generated draft **When** it is validated and saved **Then** it passes the same validation gate as hand-authored data and float stats are quantized to Fixed before hashing **And** the draft is reopenable and editable, never locked

**Given** a prompt that implies non-RTS conventions **When** I generate **Then** the draft expresses behavior via archetype + ability composition rather than RTS-only assumptions **And** invalid generated fields are reported with a located error rather than silently accepted

**Given** no provider available **When** I open a draft generator **Then** a four-state message explains AI is unavailable and the entity can still be authored manually **And** the manual editor flow is unaffected

_Covers: FR-32, AR-33, UX-DR52, UX-DR65. Depends on: 8.3._

> FR-32 (mostly new). Add provider-backed draft generation for the four entity kinds, each emitting JSON matching the existing Core/Definitions data classes (unit, ability, hero, faction). Output lands as EDITABLE data into the existing data/file flow (and into an entity editor host if one exists from an earlier epic; if not, drafts save as editable JSON the manual editor already consumes). Must NOT assume RTS-only conventions — composition over inheritance: a generated draft references archetype + abilities, not bespoke subclasses. Validated by the SAME D3 gate; float stats quantized to Fixed before the canonical hash (authoring-layer only, zero sim coupling). UX-DR52 'Transmuting...' + UX-DR65 voice/microcopy; all output editable.

### Story 8.7: AI balance analysis of a faction/scenario with editable suggestions

As a designer tuning a faction or scenario,
I want to request AI balance analysis and get actionable suggestions I can apply and edit,
So that I can iterate on balance without the AI mutating my data behind my back.

**Acceptance Criteria:**

**Given** a faction (or scenario) and a configured provider **When** I request balance analysis **Then** I receive a list of actionable, human-readable suggestions tied to specific fields, as editable data **And** a 'Transmuting...' spinner shows during analysis

**Given** a returned suggestion **When** I choose to apply it **Then** the change is shown for review/edit first and only persisted after passing the validation gate with float->Fixed quantize **And** I can edit or discard any suggestion; nothing is auto-applied

**Given** an analysis response that fails to parse into structured suggestions **When** validation runs **Then** it surfaces the failed-validation four-state message instead of corrupting data **And** the underlying faction/scenario data is left unchanged

**Given** no provider available **When** I open balance analysis **Then** a four-state message explains AI is unavailable **And** manual balance editing remains fully available

_Covers: FR-33, AR-33, UX-DR52, UX-DR65. Depends on: 8.3, 8.6._

> FR-33 (new build). New balance-analysis flow: gather the target faction/scenario data (using the definition schemas exercised in 8.6), send a NormalizedRequest via ILLMProvider, and return structured, actionable suggestions (e.g. proposed stat deltas + rationale) as EDITABLE data — never an auto-applied opaque change. The creator reviews each suggestion and chooses to apply/edit/discard; applied changes go through the same validation gate with float->Fixed quantize. Authoring-layer only, zero sim coupling. UX-DR52 spinner; UX-DR65 microcopy; FR-34 four-state degrade.

## Epic 9: Share, Discover & Multiplayer at Scale

_Creators publish to mod.io (proof-of-play gated, IP surfaced), players browse/subscribe/rate, and multiplayer scales to a verified <=4 players (8 fast-follow) with matchmaking, viewable replays, and online hero persistence._

**Sequencing note:** Ordering follows the D5 strangler migration (briefing section 5): low-FR-39-risk SimChecksum widen + server-side checksum collector first (9.1), then faction-model expansion (9.2), then the server-authoritative merged tick packet + ready-count + spectator-demux rewrite (9.3), then handshake/delay/start-state gates (9.4), then deterministic freeze-and-continue (9.5), then Nakama N + parties + lobby UI (9.6), then replay v2 (9.11 sits after the merged-envelope is frozen). UGC rail (9.7-9.10) and online hero rail (9.12) layer on top; proof-of-play (9.7) depends only on the pre-existing ScenarioDirector.OnVictory D1 leaf and earlier UGC packaging, NOT on the D5 backbone, so it can land independently after 9.1 but is sequenced here to keep the publish flow contiguous. Every story is golden-checksum-gated at N=2 where it touches the wire. Brownfield: SimChecksum.cs only hashes Ore[P1/P2]; Faction enum stops at Player4; DedicatedServer uses int other=1-slot and P2P-local checksum compare; ReplayRecorder VERSION=1; ContentPackager/ModIoService exist but lack proof-of-play/IP-consent/min-quality/asset-hash; no runtime GLB ingest exists (only editor-side MeshLoader). 8-player is an explicit constant-bump fast-follow (SD-8); parties-lobby-UI is the deferrable slice (SD-9).

### Story 9.1: Widen SimChecksum + add server-side checksum collector with majority-vote desync attribution

As a multiplayer engineer,
I want the desync checksum widened to all active factions across all per-faction sim arrays, and the checksum path inverted from P2P-local compare to a stateful server-side collector that majority-votes and fail-closed halts on no majority,
So that N>2 desync detection is not blind to Crystal/Supply divergence and the server (not each peer) is the single authority that detects and attributes desync.

**Acceptance Criteria:**

**Given** a running sim with 2 active factions **When** SimChecksum.Compute runs **Then** it hashes Crystal, SupplyUsed, and SupplyCap (not just Ore) for every active faction across all per-faction SoA arrays, in ascending faction order **And** a checksum_algo_version constant is bumped **And** a guard unit test asserts a known world state produces a fixed expected hash

**Given** two clients in a live match emitting Checksum packets **When** each peer sends its 60-tick-window checksum **Then** the DedicatedServer parses the slot-tagged checksum (slot taken from the transport, never a client byte), buffers per-slot per-window, and majority-votes the canonical value **And** a minority peer is named in a server-emitted DesyncAlert **And** on no majority the server HALTs fail-closed and broadcasts the halt

**Given** the existing 2-player golden replay **When** the widened checksum + collector run at N=2 **Then** the golden checksum is byte-identical across two runs of the same input and the match completes without false desync

_Covers: AR-17, NFR-determinism. Depends on: Epic 8._

> SD-5 + SD-7 from D5 briefing. Pure additions, no tick-wire change, lands first per migration step 1. SimChecksum.cs:53-54 today only hashes Ore[P1]/Ore[P2]; ConstructionTimer is already hashed. Slot is transport-authoritative (ServerTransport.cs:170). Golden-gated at N=2.

### Story 9.2: Expand the faction/player model to 8 and audit every (int)Faction site

As a multiplayer engineer,
I want the Faction enum extended to Player8, FACTION_COUNT raised, every (int)Faction index site and 2-player loop audited, and ScenarioDirector's victory-threshold loop converted from float to Fixed,
So that the sim can address up to 8 players deterministically with no latent float/locale leaks and no hardcoded 2-player assumptions.

**Acceptance Criteria:**

**Given** the Faction enum stopping at Player4 **When** the model is expanded **Then** Faction is extended to Player8, FACTION_COUNT (ResourceStore) is raised accordingly, and all per-faction arrays are sized to the new count **And** every (int)Faction index site is audited and corrected **And** every literal 2-player loop is generalized

**Given** ScenarioDirector's threshold loop using .ToFloat()/ToString("F2") **When** the conversion is applied **Then** the threshold comparison uses Fixed end-to-end with no float arithmetic or locale-dependent formatting in the sim path

**Given** the widened checksum from 9.1 **When** a new N=3 and N=4 deterministic harness runs identical inputs twice **Then** checksums are byte-identical across runs and across the two N values' own re-runs (no desync introduced by the expansion)

_Covers: AR-17, NFR-determinism. Depends on: 9.1._

> SD-6. EntityWorld.cs:49-54 stops at Player4; ResourceStore FACTION_COUNT=5. ScenarioDirector float leak verified at :168/:170. Requires 9.1's widened checksum as the safety net (briefing section 6). Faction==player for 1.0; decoupled playerSlot deferred.

### Story 9.3: Server-authoritative merged tick packet + ready-count state machine + spectator-demux rewrite

As a multiplayer engineer,
I want a new server-built TickCommandsMerged packet that re-stamps faction from the authoritative slot, a one-slot client gate, a ready-COUNT server state machine, and the spectator ingest path rewritten to consume server-built merged output,
So that the relay becomes stateful N-player server authority with a single deterministic apply order, closing the faction-spoof and merged-from-client tamper holes.

**Acceptance Criteria:**

**Given** client TickCommands packets (client->server only, single-faction) **When** the server fans them in **Then** it builds a distinct TickCommandsMerged type (server->client only) with faction re-stamped from SLOT_FACTION[sourceSlot], sub-bundles sorted ascending by faction id, dropping bundles on faction mismatch or over-count **And** a merged-shape packet received FROM a client is hard-rejected **And** MERGED_MAX_BYTES and per-sub-bundle MAX_ORDERS ceilings are enforced (drop, not clamp)

**Given** the merged packet on the client **When** the client gates a tick **Then** it waits on a single merged-arrival flag and applies per faction ascending (unit orders in wire order), replacing the _ready[0]&&_ready[1] / 1-slot logic with a connected==expected && ready==expected count machine

**Given** the in-scope spectator path that demuxed single-faction packets by cmdFaction **When** a spectator joins **Then** it consumes only the server-built merged output and renders all factions correctly

**Given** the N=2 golden replay **When** the merged-packet path runs as N=2 of the new format **Then** the golden checksum is byte-identical to the pre-rewrite baseline (FR-39 regression gate)

_Covers: AR-17, AR-18, NFR-determinism. Depends on: 9.2._

> SD-1/SD-2/SD-3. The #1 FR-39 regression gate (briefing section 8). DedicatedServer.cs int other=1-slot at :116/:155/:218 and _ready[0]&&_ready[1] at :179 are the seams. Co-design the tagged envelope layout with the DSL event record NOW so 9.11 replay v2 can reuse it. Also overwrite chat faction = SLOT_FACTION[fromSlot] (chat spoof fix). Golden-gated at N=2.

### Story 9.4: Server-dictated adaptive input delay + start-state agreement + PROTOCOL_VERSION/rulesetHash gates

As a multiplayer engineer,
I want server-side RTT collection driving an ACK-gated server-dictated input-delay broadcast with receipt-side [2,12] re-clamp, plus a tick-0 start-state hash agreement and inbound PROTOCOL_VERSION + multi-hash handshake gates,
So that input delay verifiably adapts to real-network RTT without desync, and the match cannot start unless all peers agree on protocol, ruleset, scenario, and start state.

**Acceptance Criteria:**

**Given** peers on a LAN with low RTT **When** the server collects RTT and dictates delay **Then** the delay verifiably adjusts (e.g. 4->2 on LAN) via an all-N-ACK commit, and any received delay is re-clamped to [2,12] on receipt (fixing the unclamped Math.Max at LockstepManager.cs:495) **And** a forged DelayProposal cannot push delay outside [2,12] **And** delay is accepted only from the authoritative server channel

**Given** a client Ready packet **When** it reaches the server **Then** the packet carries {scenarioHash, rulesetHash, startStateHash} and the server gates each against ITS own canonical values, with hash==0 treated as a hard reject (not skip) **And** a mismatched PROTOCOL_VERSION is rejected at both endpoints **And** the server dictates the initial delay and a single start-state hash {roster+faction-count+initial-delay+rulesetHash+scenarioHash} is compared fail-closed before tick 0

**Given** two runs of an identical match with a mid-match RTT change **When** the server re-dictates delay **Then** both runs produce byte-identical golden checksums (no desync from the delay change)

_Covers: FR-41, AR-17, AR-18, FR-41, NFR-determinism, UX-DR28. Depends on: 9.3._

> SD-4 + SD-13. Verified the server has NO Ping/Pong/DelayProposal cases today (DedicatedServer.cs:133-166) — this is net-new server RTT collection. Server ignores client Hello today (:135-137). Generalize SeedInitialTicks (LockstepManager.cs:579-590). UX-DR28 stall banner surfaces clamp/RTT changes. Golden-gated at N=2.

### Story 9.5: Deterministic disconnect freeze-and-continue drop policy

As a multiplayer engineer,
I want a server-dictated, tick-counted freeze-and-continue policy where a dropped slot has empty commands injected each tick while the passive sim continues identically on all peers,
So that a disconnect degrades deterministically (not on wall-clock) and the remaining peers stay in sync rather than desyncing or hard-crashing.

**Acceptance Criteria:**

**Given** a live N-player match **When** one peer disconnects **Then** the server dictates idle-at-applyTick (ACK-gated like a delay change), empty commands are injected for the dropped slot each tick, and the dropped slot is NOT removed from the sim or the checksum **And** the freeze trigger is tick-counted, never wall-clock

**Given** a mid-match drop **When** the sim continues 300+ ticks past the drop **Then** all remaining peers' checksums remain in sync (covered by a mid-match-drop desync test that drops, then asserts identical checksums 300+ ticks later)

**Given** a frozen idle unit **When** the freeze is in effect **Then** the unit sits motionless and the passive sim (regen, projectiles in flight, etc.) continues bit-identically on all peers

_Covers: AR-20, NFR-determinism. Depends on: 9.4._

> SD-10. Drop-to-AI is a D4 fast-follow, explicitly out of scope (briefing section 7). 'Idle' = empty commands + passive sim continues. Golden/mid-match-drop test is the gate.

### Story 9.6: Nakama N-player matchmaking, parties API, server-side slot assignment + lobby/matchmaking UI

As a player,
I want to matchmake into an N-player game (or join via LAN/lobby), see a full lobby with player slots, chat, ping, and ready state, and optionally pre-group into a party before matchmaking,
So that I can find and start scaled multiplayer matches with friends through a clear, version-checked lobby.

**Acceptance Criteria:**

**Given** the Nakama matchmaker hardcoded to minCount/maxCount=2 **When** N-player matchmaking is parameterized **Then** minCount/maxCount/countMultiple are configurable, slot assignment is server-side (not the lexicographic pick at NakamaService.cs:186-194), and a distinct parties API groups players pre-match **And** the single-static GameServerIp/Port routing assumption is resolved or an explicit static-endpoint-for-1.0 decision is recorded

**Given** a lobby of matched/LAN players **When** the lobby UI renders (UX-DR69) **Then** it shows the scenario header with a version-match hash check, per-slot colorblind dots+glyphs, ready pills, and ping, plus lobby chat, and the Start button is gated until all slots are ready

**Given** the LAN journey at scale (UX-DR84) **When** players join a LAN lobby and chat **Then** the full join->chat->ready->start flow works for up to 4 players and the parameterization supports 8 as a constant bump

_Covers: FR-40, AR-20, AR-17, FR-40, UX-DR69, UX-DR84, UX-DR28. Depends on: 9.5._

> SD-9. ServerTransport hard-splits MAX_SLOTS=4 into 2 players + 2 spectators (ServerTransport.cs:22-24) — the N-player lobby must reallocate that split dynamically; spectator capacity competes with player slots. Parties-lobby-UI is the deferrable slice (can ship parties API + minimal UI; full parties UI fast-follow). Existing LobbyUi.cs + MatchChatOverlay.cs are the brownfield base. Ship/verify N<=4; 8 is the fast-follow constant bump (SD-8).

### Story 9.7: Proof-of-play signed completion token from the Victory leaf

As a creator,
I want winning my own scenario to mint a signed completion token carrying the canonical scenario hash, outcome, and timestamp, persisted for use at publish time,
So that I can prove I have actually played and beaten my scenario before I am allowed to publish it.

**Acceptance Criteria:**

**Given** a scenario whose D1 Victory leaf fires for the local player **When** ScenarioDirector.OnVictory signals a win **Then** a signed completion token {canonical scenarioHash, outcome=win, timestamp} is generated and stored against that scenario **And** a loss or a win by another faction does NOT mint a winning token **And** the token's scenarioHash matches the canonical hash of the exact scenario played

**Given** a tampered or hand-edited token **When** it is validated **Then** the signature check fails and the token is rejected

**Given** a scenario edited after a token was minted **When** its canonical hash is recomputed **Then** the stored token no longer matches and is treated as invalid for that edited scenario

_Covers: FR-36, AR-30, FR-36. Depends on: 9.1._

> AR-30 (token half). Hooks the pre-existing ScenarioDirector.OnVictory(winnerFactionSlot) D1 leaf (ScenarioDirector.cs:56). Depends on 9.1 only for the canonical-hash discipline (checksum_algo_version); does NOT depend on the D5 wire backbone. The token is consumed by 9.8.

### Story 9.8: Pre-publish quality gate + IP-ownership consent + publish .chimera.zip to mod.io

As a creator,
I want to package my scenario as a .chimera.zip with a proof-of-play token, thumbnail, description, and screenshots, explicitly consent to a non-exclusive host/distribute right, and publish it to mod.io in-app,
So that I can share my finished scenario knowing I retain ownership and that only quality, proven content is published.

**Acceptance Criteria:**

**Given** the publish flow **When** I attempt to upload **Then** publish REFUSES without a valid proof-of-play token (from 9.7) AND the min-quality fields: a thumbnail, a description >=100 chars, and >=1 screenshot **And** the token + min-quality fields are written into the .chimera.zip manifest

**Given** the publish flow **When** I reach the consent step (FR-38a) **Then** an explicit IP-ownership checkbox states the creator retains full ownership and the platform takes only a non-exclusive host/distribute right, and the recorded consent is written into the manifest **And** upload is blocked until consent is checked

**Given** a complete, consented, quality-passing package **When** I publish **Then** the package is created and uploaded to mod.io end-to-end via ModIoService.UploadModAsync (requires configured Game ID + API key) and the new modId is surfaced on success

_Covers: FR-35, FR-36, FR-38a, AR-30, AR-31, FR-35, FR-36, FR-38a. Depends on: 9.7._

> AR-30 (gate half) + AR-31 (FR-38a consent half). Extends ContentPackager/ContentPackageManifest (add token, screenshots, ip_consent fields) and ModIoService.UploadModAsync (already exists). FR-35 needs mod.io Game ID + API key configured — verify end-to-end. contentBrowser browse/rate is 9.10.

### Story 9.9: Content-hash integrity verification on download + runtime binary-asset ingest

As a player,
I want downloaded community packages to be content-hash verified (including any bundled art bytes) and custom .glb assets ingested at runtime in non-editor builds, falling back to a box placeholder on invalid assets,
So that I can trust downloaded content has not been corrupted or tampered with and community art actually renders in shipped builds.

**Acceptance Criteria:**

**Given** a published package whose hash now folds in the asset bytes **When** it is downloaded **Then** the full content hash (scenario + bundled asset bytes) is verified on download and a mismatch is rejected with a located error (extending the existing scenario_hash check in ContentPackager.Unpack)

**Given** a .glb in a downloaded package in a non-editor build **When** the runtime ingests it **Then** it loads via GLTFDocument.AppendFromFile->GenerateScene (NOT GD.Load<PackedScene>), runs per-asset validation, and registers it in a net-new AssetRegistry **And** an invalid/unsafe asset falls back to a box placeholder instead of crashing

**Given** the content hash **When** asset bytes are tampered post-publish **Then** the integrity check fails on download

_Covers: FR-38, AR-27, FR-38. Depends on: 9.8._

> FR-38 + AR-27. ContentPackager.Unpack already verifies scenario_hash (ContentPackager.cs:175-182) — extend to fold asset bytes into the hash. Net-new runtime AssetRegistry + GLTFDocument ingest; today only editor-side MeshLoader.cs exists. Box placeholder on invalid.

### Story 9.10: Content browser delegating browse/search/tag/sort/subscribe/rate to mod.io

As a player,
I want to browse, search, tag-filter, sort, subscribe to, and rate published scenarios from inside the game,
So that I can discover and curate community content without leaving the app.

**Acceptance Criteria:**

**Given** the content browser (UX-DR72) **When** I browse **Then** browse/search/tag-filter/sort/subscribe/rate all delegate ENTIRELY to mod.io-native features (no parallel local rating/search index), surfaced through ModIoService **And** each result card shows name, author, thumbnail, tags, and mod.io rating/download stats **And** the author's IP-ownership / profile is surfaced from the mod.io entry

**Given** a logged-in user **When** I subscribe to or rate a package **Then** the action is sent via ModIoService.SubscribeAsync/RateAsync and reflected in the UI on success **And** a not-logged-in user is prompted to authenticate before subscribe/rate

**Given** a subscribed package **When** I trigger download **Then** it downloads via ModIoService and is then integrity-verified per 9.9 before becoming playable

_Covers: FR-37, AR-31, FR-37, UX-DR72. Depends on: 9.9._

> FR-37 + AR-31 (delegate-to-mod.io half). ModIoService already exposes BrowseModsAsync/SubscribeAsync/RateAsync/UnsubscribeAsync. ContentBrowserPanel.cs is the brownfield UI base. No parallel rating/search system — mod.io-native only.

### Story 9.11: Replay v2: versioned tagged-record body, scenario re-gate on playback, and viewable replays

As a player,
I want replays recorded in a v2 format (hard-rejecting v1) that embeds the canonical scenarioHash + algo-version and a DSL-event/tagged-record body, that I can view and share,
So that I can re-watch and share matches with confidence the replay is bound to the exact scenario and ruleset it was recorded against.

**Acceptance Criteria:**

**Given** the v1 ReplayRecorder format **When** v2 is introduced **Then** the replay VERSION is bumped, the header carries roster + faction-count + rulesetHash + canonical scenarioHash + checksum_algo-version, and a v1 file is hard-rejected on load **And** the record body uses a tagged envelope mirroring the merged-packet layout from 9.3, including DSL/tagged events with an apply branch

**Given** a v2 replay whose embedded scenarioHash no longer matches the canonical scenario **When** playback is attempted **Then** the scenario is re-gated and playback is refused with a located error

**Given** a recorded match **When** I open a replay **Then** it is VIEWABLE (plays back through the deterministic sim, not merely recorded) and the file can be shared **And** playing the same v2 replay twice yields byte-identical golden checksums

_Covers: FR-40, AR-19, FR-40, NFR-determinism. Depends on: 9.3, 9.7._

> SD-12 / AR-19. ReplayRecorder.cs VERSION=1 (no DSL events, no scenarioHash gate); ReplayPlayer.cs already replays the command stream (viewable foundation exists). Reuses the tagged envelope frozen in 9.3. Depends on 9.7 for canonical-hash embedding. FR-40 requires .chmr replay saved AND viewable/shareable.

### Story 9.12: Server-validated online hero persistence rail

As a player,
I want my online hero/profile stored server-side as the sole source of truth, validated by a server RPC, with the server attesting my profile before a game starts,
So that online persistence cannot be tampered with via client save-codes and only a server-attested profile can enter an online match.

**Acceptance Criteria:**

**Given** an authenticated (email-auth) player **When** their hero/profile is persisted **Then** it is stored as a Nakama storage object with Owner-Read / No-Client-Write permissions, written only via a validating server RPC (never a raw client save-code)

**Given** a client attempting to write the profile directly **When** the write is attempted **Then** it is rejected (No-Client-Write) and only the server RPC's validated mutation succeeds

**Given** the online hero picker (UX-DR75) at StartGame **When** a player selects a hero for an online match **Then** the server attests the profile and StartGame is gated on that attestation; an unattested/invalid profile cannot enter the match

_Covers: FR-7c, AR-12, FR-7c, UX-DR75. Depends on: 9.6._

> AR-12. Sole source of truth = Nakama storage object (Owner-Read, No-Client-Write) + validating server RPC + server attestation gating StartGame + email-auth. NakamaService already supports email/device auth (NakamaService.cs:76-115). Depends on 9.6 for the N-player matchmaking/StartGame surface to gate on.

## Epic 10: Release Readiness — Content, Balance, Performance & Ship

_The finishing work to ship: audio, real art + style-consistency, the performance pass, faction balance, Linux export, accessibility baseline, and release to Steam + a DRM-free channel._

**Sequencing note:** Brownfield-grounded against actual code: AudioManager.cs already loads SFX from res://resources/audio/sfx/ with graceful silence fallback (FR-48 = supply+verify assets, not build system). SettingsManager/SettingsData/SettingsPanel already exist with audio buses + a basic ColorblindMode P2-tint toggle (FR-51 = extend/harden). alpha (Crucible Covenant) + beta (Sanguine Court) already ship real GLBs; FR-49 "Iron Pact" is a third placeholder faction needing real art. SimChecksum.cs + ReplayRecorder/Player exist — reused as the determinism gate for Linux export (10.7) and the deterministic substrate for the self-play balance harness (10.2). No self-play/headless/balance harness exists yet (new in 10.2). Ordering: verification + balance + perf first (highest risk to ship quality), accessibility mid, store/export pipelines last. Accessibility (FR-51) split into 4 single-session slices 10.8/10.8a/10.8b/10.8c since it spans 7 UX-DR items. Art-style consistency layer (10.5) precedes Iron Pact art replacement (10.6) so new art lands already coherent under the shared material+post-process layer (AR-28 "runs AFTER external AI-art generation" — the layer is built first, then art is dropped in and re-tinted).

### Story 10.1: Verify solo skirmish vs AI across difficulties on all shipped maps

As a solo player,
I want to start and complete a skirmish against the AI opponent at Easy/Normal/Hard on every shipped map,
So that I can confirm the core single-player experience works end-to-end before ship.

**Acceptance Criteria:**

**Given** the shipped map list in resources/data/scenarios **When** the tester launches a skirmish on each map at each of Easy/Normal/Hard **Then** every (map, difficulty) combination loads, the AI builds and attacks, and the match can reach a win or loss condition without a crash or soft-lock **And** a pass/fail matrix is recorded with every cell marked

**Given** a difficulty selection in the skirmish setup UI **When** the player picks Easy, Normal, or Hard **Then** the selected AiDifficulty is applied to AiOpponentSystem for that match **And** Hard is observably more aggressive (earlier/larger attacks) than Easy

**Given** any (map, difficulty) cell that fails **When** the failure is a ship-blocker (crash, unloadable map, AI never produces) **Then** the root cause is fixed and the cell re-tested to pass **And** non-blocking polish issues are filed but not necessarily fixed here

_Covers: FR-43, NFR-5. Depends on: Epic 9._

> FR-43 verify-only. Maps already exist in resources/data/scenarios (alpha_map_01 + map_02..map_12). AiDifficulty enum (Easy/Normal/Hard) + AiOpponentSystem already wired via MainScene [Export] AiLevel. This story is a structured playability pass, not new systems. Produce a pass/fail matrix (map x difficulty) and file located bugs; fix only ship-blocking breakage discovered (e.g. a map that fails to load, an AI that never attacks).

> ⚠ Quality-review: add an objective difficulty metric (e.g. first-attack tick or army-size delta at tick N) — 'observably more aggressive' is untestable as written.

### Story 10.2: AI self-play balance harness + tune the two showcase factions to 45-55%

As a game developer shipping a balanced game,
I want a deterministic headless self-play harness that runs many alpha-vs-beta AI matches and reports win rates,
So that I can confirm and tune the two shipped factions so neither sits outside a ~45-55% win rate.

**Acceptance Criteria:**

**Given** a seed and a (faction-A, faction-B, map) tuple **When** the harness runs a match headless on the sim layer only **Then** the match runs to a deterministic result and the same seed reproduces a byte-identical SimChecksum result across two runs **And** no Godot Node / presentation code is required to run a match

**Given** a representative batch of alpha-vs-beta matches across the shipped maps at a fixed AI difficulty **When** the batch completes **Then** the harness reports per-faction win rate, draw rate, and average match length **And** mirror-side bias is controlled by alternating which faction starts on which side

**Given** an initial win-rate report outside 45-55% for either faction **When** the developer adjusts only data fields in the faction JSON and re-runs the batch **Then** both factions land within ~45-55% on the final batch **And** the final report is saved as the balance baseline artifact and no gameplay code constants were changed

_Covers: FR-42, NFR-5. Depends on: 10.1, Epic 9._

> FR-42, informed by FR-33 AI balance analysis. New harness (none exists today). MUST reuse the existing deterministic substrate: SimRng + fixed 30Hz SimulationLoop + SimChecksum so each match is reproducible from a seed. Run the sim layer without presentation (no MultiMesh/Godot rendering per the sacred boundary). Tuning is data-only edits to alpha_faction.json / beta_faction.json — no code balance constants. Capture a representative sample across the shipped maps from 10.1.

### Story 10.3: Performance pass: 500-2,000 units at 60 FPS render / 30 Hz sim

As a player on representative hardware,
I want the game to hold 60 FPS rendering and a stable 30 Hz simulation with 500-2,000 active units,
So that large battles stay smooth on the shipped scenarios and community-scale scenarios.

**Acceptance Criteria:**

**Given** a stress scenario spawning 500, then 1000, then 2000 active units in combat **When** the scenario runs on representative hardware **Then** render stays at >=60 FPS and the simulation holds a stable 30 Hz tick (no tick overrun/spiral) at 500 and 1000 units **And** the 2000-unit case meets the target or any shortfall is documented with the bottleneck identified

**Given** a profiler capture of a heavy frame **When** the developer reviews sim-tick cost vs render cost **Then** the two budgets are reported separately and the dominant cost centers are named **And** at least one identified bottleneck is optimized and re-measured to show improvement

**Given** an optimization applied to the sim layer **When** the determinism check from 10.2 is re-run **Then** the SimChecksum result is still byte-identical across two runs from the same seed **And** a representative community-scale scenario is also measured to confirm the target holds beyond the shipped maps

_Covers: FR-46, AR-37, NFR-5. Depends on: 10.1._

> FR-46 + AR-37 (perf holds on community scenarios too, NFR-5). MultiMesh rendering + spatial-hash combat + NavServer rate limiting already exist. This is a measurement + targeted-optimization pass, not a rewrite. Build/extend a stress scenario at 500/1000/2000 units. Keep the sim 30Hz tick budget separate from the 60FPS render budget in measurement. Use the godot profiler. Do not break determinism while optimizing the sim layer.

> ⚠ Quality-review: define a minimum acceptable degraded target for the 2,000-unit case (not 'shortfall documented') and specify the representative-hardware spec so the perf gate is reproducible.

### Story 10.4: Author and wire real .ogg audio assets through the existing audio system

As a player,
I want to hear combat, building, training, and UI sounds during play,
So that the game feels responsive and complete instead of silent.

**Acceptance Criteria:**

**Given** the 7 SFX paths AudioManager expects under res://resources/audio/sfx/ **When** real .ogg files are placed at those paths and the project is loaded **Then** AudioManager reports 7/7 streams loaded and each combat/building/training/UI event plays its corresponding sound in-game **And** a missing file still falls back to silence without error (graceful path preserved)

**Given** the SFX/Master/Music volume sliders in settings **When** the player lowers SFX volume **Then** in-game sound effects attenuate accordingly and a 0 value mutes the SFX bus **And** settings persist across a restart

**Given** a supplied background music track **When** a match starts **Then** the track plays on the Music bus and obeys the Music volume setting **And** if no music track is supplied this sub-criterion is recorded as deferred without leaving a broken player node

_Covers: FR-48. Depends on: Epic 9._

> FR-48. AudioManager.cs already drains CombatEventQueue and loads 7 named .ogg files from res://resources/audio/sfx/ (melee_hit, ranged_hit, explosion, unit_killed, building_placed, training_complete, ui_click) with silent fallback when absent. SettingsManager already controls Master/SFX/Music buses. This story = supply real .ogg files at the expected paths, confirm import settings, and verify each event triggers its sound + respects bus volume. Add music bus playback hookup if a music track is supplied (Music bus already exists in SettingsData).

> ⚠ Quality-review: exercise the Music bus path with at least a test stream so the wiring (not just the asset) is verified.

### Story 10.5: Art-style consistency layer: shared material library + global cel-shade post-process

As a creator and player,
I want a shared material preset library and one global post-process applied across all unit/building art,
So that AI-generated and creator-made art reads as one coherent visual style.

**Acceptance Criteria:**

**Given** the shared material preset library of StandardMaterial3D .tres files **When** unit and building MultiMeshes render **Then** each uses a shared preset via material_override and existing per-team color tint still applies correctly **And** both alpha and beta factions render with consistent surface response

**Given** the global WorldEnvironment with the cel-shading + tonemap post-process enabled **When** the scene renders **Then** all on-screen art shows the unified cel-shaded look and the existing GLBs do not appear broken (validated with godot_validate_meshes if any surface looks wrong) **And** toggling the post-process off restores the prior look, proving it is the consistency layer doing the work

**Given** a flat-grey placeholder mesh dropped in unmodified **When** it renders under the consistency layer **Then** it is tinted and shaded to match the shipped factions' visual style without per-asset hand-tuning **And** the perf pass from 10.3 still meets its FPS target with the post-process active

_Covers: FR-49a, AR-28. Depends on: 10.1._

> FR-49a + AR-28 (P1). Built BEFORE Iron Pact art replacement (10.6) so new art lands already coherent. Implement: a library of shared StandardMaterial3D .tres presets applied via MultiMesh material_override (current rendering already uses material_override for team tint per the mesh-rendering memory), plus one global WorldEnvironment post-process (cel-shading + tonemap). Presentation layer only — no sim changes. Team-tint behavior from the existing GLB renderer must be preserved.

### Story 10.6: Replace Iron Pact's 8 placeholder GLBs with real art

As a player,
I want the Iron Pact faction to use real unit models instead of placeholder boxes/greyboxes,
So that all shipped factions look finished.

**Acceptance Criteria:**

**Given** 8 real Iron Pact unit GLBs delivered from the external pipeline **When** they are placed at their asset paths and the faction JSON mesh_path/mesh_scale entries point to them **Then** all 8 load through MeshLoader without falling back to the placeholder box and render at correct scale/orientation (feet on ground) **And** no placeholder greybox remains for any of the 8 units

**Given** the Iron Pact units rendered in-engine **When** viewed alongside alpha/beta units under the 10.5 consistency layer **Then** they share the unified cel-shaded style and apply team tint correctly **And** godot_validate_meshes reports no corrupt geometry for the new GLBs

**Given** an Iron Pact GLB that is missing or fails to import **When** the game loads **Then** MeshLoader falls back to the placeholder box without crashing and logs the offending path **And** the failure is fixed before this story is considered done

_Covers: FR-49, AR-28. Depends on: 10.5._

> FR-49. External Hunyuan3D/Tripo pipeline produces the GLBs offline; this story imports and wires them. MeshLoader.cs already does GLB->Mesh with box fallback and reads mesh_path/mesh_scale from faction JSON (as alpha/beta already do). Follow the established pivot/scale conventions (feet-pivoted) from the mesh-rendering memory. The 8 new GLBs must render coherently under the 10.5 consistency layer (that is why 10.5 precedes this).

### Story 10.7: Linux export (client and/or dedicated server) builds and runs via WSL host

As a developer shipping cross-platform,
I want a Linux export that builds on the existing WSL/Ubuntu host and runs with identical simulation results,
So that Linux players and dedicated servers are supported without determinism drift.

**Acceptance Criteria:**

**Given** the WSL/Ubuntu host with .NET installed and a Linux export preset **When** the developer runs the export **Then** a Linux build artifact is produced and launches/runs on Linux (client and/or dedicated server, as scoped) **And** the export preset is committed so the build is reproducible

**Given** a fixed match seed from the 10.2 harness **When** the same match is run on the Windows build and the Linux build **Then** the final SimChecksum is byte-identical across both platforms **And** any cross-platform divergence is traced to a non-deterministic construct (float/wall-clock) and fixed

**Given** a shipped scenario run on the Linux build **When** it plays through **Then** it reaches a normal end state without platform-specific crashes **And** the target (client vs dedicated server) is documented in the build notes

_Covers: FR-50, AR-37. Depends on: 10.3._

> FR-50 + AR-37 (rides existing WSL/Ubuntu build host + cross-platform determinism gate). Needs .NET-in-WSL set up (per user memory: WSL/Ubuntu already present, only .NET-in-WSL + running the check is new). Create/configure export_presets.cfg Linux preset (currently absent). The cross-platform determinism gate reuses SimChecksum: a match seed run on Windows and on Linux must produce identical checksums. Decide and document client vs dedicated-server target.

### Story 10.8: Accessibility baseline: colorblind-safe team colors + WCAG AA contrast

As a player with a color-vision or contrast need,
I want colorblind-safe team colors with selectable filters and AA-contrast UI,
So that I can distinguish teams and read the interface clearly.

**Acceptance Criteria:**

**Given** the accessibility settings **When** the player selects a colorblind filter mode **Then** team colors switch to a palette that remains mutually distinguishable under that mode and the choice persists across restart **And** the existing simple ColorblindMode toggle remains functional or is superseded cleanly

**Given** the core HUD and menus **When** audited against WCAG AA contrast **Then** primary text and interactive controls meet AA contrast ratios, and a contrast-boost option raises any deficient elements to compliance **And** the audit results are recorded per screen

**Given** a match with two teams using a colorblind palette **When** viewed by a tester simulating each color-vision type **Then** the two teams are reliably distinguishable in-world and on the minimap **And** team color is not the only differentiator where feasible (shape/label backup noted)

_Covers: FR-51, UX-DR40, UX-DR39. Depends on: 10.1._

> FR-51 (slice 1 of 4) + UX-DR40 + UX-DR39. SettingsData already has a basic ColorblindMode toggle (P2 red->orange). Extend to a proper colorblind team-color palette + selectable filter modes (e.g. Deuteranopia/Protanopia/Tritanopia) and ensure team colors stay distinguishable. Audit core HUD/menu text+control contrast to WCAG AA and add a contrast-boost option. Presentation/settings layer only.

### Story 10.8a: Accessibility baseline: fully remappable keybindings with reset

As a player,
I want to remap any game keybinding and reset to defaults,
So that I can play with controls that suit me.

**Acceptance Criteria:**

**Given** the keybinding settings screen listing all remappable actions **When** the player rebinds an action to a new key **Then** the new binding takes effect immediately in-game and persists across restart **And** the bound action is one of the real gameplay actions, not a placeholder

**Given** a rebind that collides with an existing binding **When** the player confirms it **Then** the conflict is detected and the player is warned or the prior binding is cleared deterministically **And** no two actions silently share a key without notice

**Given** customized bindings **When** the player chooses reset (single or all) **Then** the affected bindings return to defaults and persist **And** the InputMap is the single source of truth, not hardcoded key checks scattered in code

_Covers: FR-51, UX-DR41. Depends on: 10.8._

> FR-51 (slice 2 of 4) + UX-DR41. Build a keybinding remap UI in the settings panel over Godot's InputMap; persist bindings (extend SettingsData/SettingsManager which already load+save user://settings.json). Must cover the actual gameplay actions (camera, select, move, attack-move, stop, hold, control groups, building mode). Provide per-binding reset and reset-all. Detect and warn on conflicts.

### Story 10.8b-1: Accessibility baseline: UI scaling 80-150% + reduced-motion

As a player at various resolutions and motion sensitivities,
I want to scale the UI from 80% to 150% and enable a reduced-motion mode,
So that the interface is readable and comfortable on 1080p/1440p/4K.

**Acceptance Criteria:**

**Given** no defined reduced-motion target values yet **When** this story starts **Then** the reduced-state target values are defined and recorded as the canonical reference (satisfying the ⚠ open item UX-DR44)

**Given** the UI scale setting from 80% to 150% **When** the player changes it at 1080p, 1440p, and 4K **Then** HUD and menus scale without clipping or overlap and the choice persists across restart **And** layout stays usable at the 80% and 150% extremes

**Given** reduced-motion enabled **When** events that normally animate occur (camera shake on kills, panel transitions) **Then** motion is suppressed to the defined reduced-state values (e.g. RtsCameraController.SetShake becomes a no-op, transition durations cut to ~0)

_Covers: FR-51, UX-DR42, UX-DR44, UX-DR46, UX-DR47, UX-DR48, UX-DR49. Depends on: 10.8._

> Split from former 10.8b. FR-51 (slice 3 of 4). UX-DR42/46-49 UI scaling 80-150% across 1080p/1440p/4K (responsive). UX-DR44 reduced-motion: first AC DEFINES the reduced-state target values. Persist via SettingsData. Camera shake hook (RtsCameraController.SetShake) already exists and must respect reduced-motion.

### Story 10.8b-2: Accessibility baseline: warm-paper light theme token set

As a player who prefers a light interface,
I want a warm-paper light theme defined and selectable as a first-class peer to the cool-dark default,
So that I can use the UI comfortably in a bright environment.

**Acceptance Criteria:**

**Given** no warm-paper light-theme token values exist in DESIGN.md **When** this story starts **Then** the full light-theme token set (bg/surface/text/accent paper-tone values, WCAG-AA on light) is defined and recorded as the canonical reference (satisfying the ⚠ open item UX-DR37)

**Given** the defined warm-paper token set **When** the player selects the light theme **Then** it applies across the HUD and every editor surface via the one Godot Theme resource and persists across restart **And** contrast still meets WCAG AA on the light surfaces

_Covers: UX-DR37. Depends on: 10.8b-1._

> Split from former 10.8b. UX-DR37 warm-paper light theme is a first-class peer to cool-dark; its token VALUES are not in DESIGN.md, so the first AC defines them. Applies through the single Godot Theme resource built in Epic 3 (UX-DR12). Persist via SettingsData.

### Story 10.8c: Accessibility baseline: subtitles (S/M/L) for voice-over

As a player who is deaf or hard of hearing,
I want subtitles at small/medium/large sizes for any voice-over,
So that I can follow spoken content.

**Acceptance Criteria:**

**Given** subtitles enabled with a chosen size (S/M/L) **When** a voice-over line (or test cue) plays **Then** the caption text is displayed at the chosen size and clears when the line ends **And** the size choice persists across restart

**Given** subtitles disabled **When** a VO line plays **Then** no caption is shown and there is no error **And** enabling subtitles mid-session begins captioning subsequent lines

**Given** the shipped content set **When** subtitles are reviewed **Then** every voice-over line present has a matching caption cue, or it is documented that the build currently ships no VO and the system is verified via a test cue **And** caption text respects the warm-paper/contrast settings from 10.8/10.8b-1/10.8b-2 for readability

_Covers: FR-51, UX-DR43. Depends on: 10.8b-1, 10.8b-2._

> FR-51 (slice 4 of 4) + UX-DR43. Add a subtitle display layer + a setting toggle with three size presets (S/M/L) persisted in SettingsData. Drive it from a simple subtitle-cue source so any voice-over line shows a caption. If the shipped build has no VO lines yet, the system must still be demonstrable with a test cue and degrade to no-op when no VO plays.

### Story 10.9: Release pipelines: ship to Steam and a direct DRM-free channel

As a developer launching the game,
I want both a Steam release pipeline and a direct DRM-free distribution pipeline ready,
So that players can buy/download through Steam or a direct channel at launch.

**Acceptance Criteria:**

**Given** the finished build artifacts (Windows + the Linux export from 10.7) **When** the Steam pipeline is run **Then** a depot upload succeeds to a Steam test/default branch and the build is installable+launchable from the Steam client **And** the pipeline is repeatable (documented/scripted), not a one-off manual upload

**Given** the same build artifacts **When** the direct DRM-free pipeline is run **Then** a downloadable DRM-free package is produced and runs without any Steam/DRM dependency **And** the package launches on a machine without Steam installed

**Given** both pipelines configured **When** a launch dry-run is performed **Then** both channels are confirmed release-ready (store/page metadata + build present) with a documented release checklist **And** version/build numbers match across both channels

_Covers: FR-52. Depends on: 10.4, 10.5, 10.6, 10.7, 10.8c._

> FR-52, last because it packages the finished build (audio, art, accessibility, Linux export all done). Set up the Steam app/depot upload pipeline (Steamworks) and a DRM-free package (e.g. site/Gumroad) for the platform builds produced in 10.7. No DRM on the direct channel. This is build/packaging/release config work plus a dry-run upload, not gameplay code.

### Story 10.10: [ADDED] Gameplay HUD, controls strip, selection & default keybindings verify/harden

As a player in a match,
I want the in-match HUD, context controls strip, selection rules, and the canonical default keybindings to be correct, restyled, and remappable,
So that the shipped game plays cleanly and a non-creator never sees authoring UI (NFR-3).

**Acceptance Criteria:**

**Given** an active match **When** the HUD renders **Then** the information hierarchy is present and Theme-styled: status line (FPS/mode/tick/sim-hash) → unit counts → resource strip → controls strip → minimap → command card → stall banner (UX-DR71)

**Given** Edit vs Play mode **When** the context controls strip updates **Then** it shows Edit shortcuts in Edit, command shortcuts in Play, and placement hints during build (UX-DR60)

**Given** a click or box-select over mixed units **When** the selection resolves **Then** only the player's own faction units are selected (UX-DR61)

**Given** the input system **When** a fresh install is launched **Then** the canonical default keybindings are bound (camera WASD/zoom/orbit/Space/E; F5 mode; M/Q/S/H/P commands; Ctrl+1–9 groups + 1–9 recall + F2 army; B/U/T/G/N/O/L/M/Y editor; Ctrl+Z/Y) and every binding is remappable with reset-to-defaults (UX-DR66)

**Given** a Play/Skirmish/Multiplayer-only user **When** they navigate any in-match surface **Then** no authoring control (palette, dock, editor toolbar) is reachable by accident (NFR-3 / UX-DR63)

_Covers: UX-DR60, UX-DR61, UX-DR66, UX-DR71, UX-DR63, NFR-3. Depends on: 3.x design-system, 10.8a remap UI._

> ADDED by coverage review — in-match HUD/input verify, default keybinding set, and the NFR-3 acceptance gate were unowned.
