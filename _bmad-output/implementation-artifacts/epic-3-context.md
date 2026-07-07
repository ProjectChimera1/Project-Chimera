# Epic 3 Context: Author Units & Heroes (incl. Save/Load)

<!-- Generated from planning artifacts. Regenerate with compile-epic-context if planning docs change. -->

## Goal

A creator builds and edits units and heroes entirely in-app — no JSON — inside one consolidated WC3-style Unit Card Editor, while players save and load hero progression between custom games. This epic also delivers the shared UI design-system (a single canonical Godot Theme resource plus a broad, reusable component kit) that every later editor composes from, so the whole creation suite reads as one on-brand product rather than a placeholder. It closes the North-Star "build a game without JSON" gate for the unit/hero half, establishes the fast Edit↔Play creation loop, and adds the deterministic hero-runtime systems (XP/leveling, death/revival, items/inventory) that make authored heroes actually progress.

## Stories

- Story 3.1a: Resolve open design decisions + author the canonical Godot Theme resource
- Story 3.1b: Core reusable component kit (simple controls) styled from the Theme
- Story 3.1c: Composite + feedback components, tooltip/switch foundations, and demo gallery
- Story 3.2: HeroStore SoA + stable hero identity folded into SimChecksum & startStateHash
- Story 3.3: Read-only Unit Card panel: stats, combat type, archetype, model preview
- Story 3.4: Unit Card Editor: edit/create/duplicate/delete with inline validation, persisted to faction data
- Story 3.5: In-editor model assignment & preview (box placeholder + GLB browse)
- Story 3.6: Archetype + orthogonal ability/behavior composition (no subclassing)
- Story 3.7: Promote-to-Hero: leveling curve, XP, signature/ultimate abilities
- Story 3.8: Persistence manifest authoring (which attributes carry forward)
- Story 3.9: Offline hero persistence rail + Save/Load hero-picker (deterministic init-time apply)
- Story 3.10: Edit↔Play round-trip loop (no-restart playtest)
- Story 3.11: Apply the design system to the front-end shell (Title, Mode Select, Settings)
- Story 3.12: Authorable attack-delivery flag (hitscan vs projectile) + per-unit projectile speed
- Story 3.13: HeroXpSystem — kill-credit XP, leveling, and stat growth at runtime
- Story 3.14: Hero death & revival
- Story 3.15: Item & inventory sim — pickups, slots, stat effects, charges
- Story 3.16: Item authoring, shop buildings, and inventory UI
- Story 3.17: Editor undo/RestoreUnit fidelity — widen UnitSnapshot to full authored state

## Requirements & Constraints

- Creators must create/edit/duplicate/delete units and heroes in-app with zero JSON editing; every authored change persists into the scenario's faction data.
- All authoring is gated by a fail-closed validator: out-of-range/missing stats, missing/invalid model paths, undefined ability references, and invalid archetype compositions must block Save and Playtest with a located, actionable error on the offending field. Valid content saves with no errors and is immediately playable.
- Every field, button, and panel carries a hover tooltip (also revealed on keyboard focus); every authoring surface offers a Simple mode (presets, advanced fields hidden) and an Advanced mode one click away that exposes all fields plus a raw-JSON escape hatch.
- The entering-playtest loop must reflect authoring edits with no app restart and no export/build step, round-tripping in ≤2s; returning to Edit resets match state to the authored start (positions, resources, supply, fired triggers, timers; playtest hero XP discarded unless a persistence-test mode is on).
- Hero progression must be deterministic, MP-safe initial state only — never a mid-game snapshot — folded into the match start-state hash so identical inputs reproduce byte-identical hashes and checksums.
- Determinism is sacred in any sim-layer work: fixed-point (16.16) math only, no float/wall-clock, ascending-id iteration, seeded RNG; every new per-entity/per-hero store folds into the checksum with an explicit golden re-baseline. Mid-match hero XP/level, revival state, and item/inventory state are the mutable sim additions.
- Content must be server-validatable (structural, no scripting escape hatch) so malformed/cheating scenarios can be rejected before running.

## Technical Decisions

- Two open design decisions must be resolved first and documented in-code: the runtime accent-switch mechanism (teal/amber/violet), and the chamfer StyleBox implementation (faceted StyleBoxFlat vs Texture vs NinePatch/shader — NOT Godot `corner_radius`). The chamfer choice blocks every chamfer-dependent component.
- All design tokens map 1:1 into a single committed `.theme` resource that is the sole styling source of truth; no component may hardcode a color or size that exists as a token. Live numbers use a mono tabular-number role.
- The component kit is deliberately broad and editor-grade (panel, btn, icon-btn, kbd, chip, readout, tag, progress, slider, input, tabs, list-row, num-input, menu, tooltip, dialog, toast, spinner, mark, switch). A demo/gallery scene must instantiate the full kit for in-engine visual verification. The Unit Card Editor and hero-picker compose from this kit with no new primitives unless a missing one is logged.
- Presentation and simulation stay separated: UI is Godot Control nodes with no sim coupling; the simulation layer stays Godot-free pure C#. Editor controls live in the UI layer; sim reads only.
- Persistence uses one PlayerProfile + a per-scenario PersistenceManifest through the validate gate, with a sparse HeroStore SoA keyed by a stable cross-match hero identity (never the recycled entity id). Offline rail only in this epic (online rail is Epic 9). Only init-time-eligible attributes may be selected; creators can disable persistence per scenario.
- Composition over inheritance: a unit is exactly one of 6 archetypes plus zero-or-more orthogonal ability/behavior components stored as additive data on the definition class — no per-unit subclassing. New definition classes deserialize from JSON through the single content-loading choke point.
- Attack delivery becomes an explicit authored enum (Hitscan | Projectile) with an optional per-unit projectile speed, decoupling delivery from attack range; the legacy range-threshold inference is removed. Both new per-entity arrays fold into the checksum (re-baseline). Legacy JSON omitting the field must default so every shipped unit keeps current behavior.
- Reuse existing brownfield infrastructure rather than rebuilding: the unit definition already carries stats/combat/model/scale/prereqs; existing GLB loading with box-placeholder fallback and the asset preview path already exist; the editor undo/redo history stack already exists; the scenario simulation host / applier from Epic 1 drive playtest. Hero XP/leveling and item effects reuse the existing modifier system and effect executor — no new execution engines.

## UX & Interaction Patterns

- Brand: low-poly echo — chamfered (clipped) corners, thin cel-shade top-edge highlight, flat color blocks; dark cool-desaturated default with a single interaction accent; depth via surface elevation + edge + shadow, not blur. Motion is quick and mechanical (~130ms). Team/faction colors are reserved for world units and must never appear in UI chrome; semantic colors never convey state by color alone.
- Unit Card Editor is a single consolidated panel (WC3 model): model preview + stats + combat type + archetype + economy + attached abilities in one view; a Promote-to-Hero switch reveals hero-only leveling/ultimate fields inline and clears them when toggled off. Validation surfaces located error badges; edits route through undo/redo (Ctrl+Z).
- Hero Save/Load picker: creator-enabled per scenario; slot cards show portrait, level, XP, signature ability, faction; Deploy/Overwrite/Delete actions with a confirmation dialog for destructive/overwrite acts; multiple heroes per player.
- Front-end shell restyle: Title (nav Play/Create/Browse/Settings/Quit, version footer, tagline "Build the game. Then play it."), Mode Select (Skirmish 1–4 offline / Multiplayer / Campaign & Tutorial bound to real mission count / Create / My Content — no element may advertise an unbuilt system: no ranked/MMR/live-online-count placeholders), and a Settings overlay (Gameplay/Graphics/Audio/Controls/Accessibility) reachable from both Commander and Creator branches.

## Cross-Story Dependencies

- The design system is foundational and strictly ordered: 3.1a (decisions + Theme) → 3.1b (core kit) → 3.1c (composite/feedback kit + gallery). Every later editor and the front-end shell (3.11) depend on this kit.
- Unit Card chain: 3.3 (read-only panel, needs 3.1c) → 3.4 (editable + validation + persistence) → 3.5 (model assign) and 3.6 (archetype/ability composition), both on 3.4; 3.7 (Promote-to-Hero) needs 3.6 and 3.2; 3.12 (delivery flag) and 3.17 (undo fidelity) need 3.4.
- Persistence chain: 3.2 (HeroStore identity + hashes, requires Epic 1's generalized SimChecksum — specifically the 1.3 checksum, not "all of Epic 1") → 3.8 (manifest, needs 3.7) → 3.9 (offline rail + picker, needs 3.8 and 3.2).
- Hero runtime chain: 3.13 (XP/leveling, needs 3.7, 3.2, and Epic 2's ability/effect work) → 3.14 (death/revival, shares 3.13's checksum fold — fields reserved to avoid a second version bump) → 3.15 (items/inventory sim, needs 3.13 plus Epic 2 effect/executor) → 3.16 (item authoring/shop/inventory UI, needs 3.15 and 3.4).
- 3.10 (Edit↔Play loop) depends on Epic 1's SimulationHost/ScenarioApplier and the 3.x design-system + Unit Card Editor; recommended EARLY despite its number since it is the creation-loop spine. 3.11 depends on the design system.
