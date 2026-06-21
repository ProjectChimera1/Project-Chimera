---
stepsCompleted: [1, 2]
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
