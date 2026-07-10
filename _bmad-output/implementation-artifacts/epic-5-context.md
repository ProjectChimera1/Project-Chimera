# Epic 5 Context: Faction Definer & the Asymmetric Showcase Factions

<!-- Generated from planning artifacts. Regenerate with compile-epic-context if planning docs change. -->

## Goal

A creator assembles a complete playable faction through a guided wizard, and the two showcase factions (Crucible Covenant / alpha and Sanguine Court / beta) become genuinely asymmetric with their FMA identities landed — not just cosmetically reskinned. This requires centralizing today's hardcoded 2-faction loading into a proper registry, extending the faction schema with validation/AI/hero fields, hardening the showcase content, wiring each faction's unique mechanic into the sim, building the authoring wizard, and making authored factions instantly playable.

Brownfield note: alpha_faction.json and beta_faction.json already carry FMA display names, Okabe-Ito team colors, themed GLB mesh paths, and revised stats, so the content-landing story is mostly verify/harden, not author-from-scratch. What does not yet exist: any FactionRegistry/slot-constant module, faction validation, an ai_preset field, any signature/hero/persistence config on the faction, and the wizard/skirmish setup UI (factions are currently chosen via scenario JSON slots only). Sequencing is strict: registry + validation + schema precede content/mechanics, which precede the wizard, which precedes selectability, which precedes the playtest gate.

## Stories

- Story 5.1: FactionRegistry & canonical faction-slot constants (AR-3)
- Story 5.2: Faction schema extension + validator (AR-39, AR-12, FR-18 data)
- Story 5.3: Land & harden the FMA showcase content as valid Definer outputs (FR-20)
- Story 5.4: Wire the two signature mechanics via D1 Modifiers (FR-20 unique mechanic)
- Story 5.5: Faction Definer guided wizard flow + validator-gated save (FR-17, UX-DR40)
- Story 5.6: AI-preset selection, advanced raw-JSON mode, hero/persistence config + completion target (FR-18, UX-DR80, AR-12)
- Story 5.7: Wizard-authored factions are immediately selectable in playtest & skirmish (FR-19, UX-DR80)
- Story 5.8: Playtest-validate asymmetry & AI playability of the showcase factions (FR-20, FR-18)
- Story 5.9: [ADDED] "Your First Scenario" guided onboarding (<15-min playable)

## Requirements & Constraints

- A creator can create a faction by assembling authored units/heroes/buildings/tech-tree, name, color, and starting conditions through a guided multi-step flow (no hand-edited JSON required for the simple path).
- An Architect can assign an AI preset to a faction so it is playable against/with the AI opponent; a missing/unknown preset must fail validation, named as the cause.
- A completed faction must be immediately selectable in playtest and skirmish/multiplayer setup without an app restart or manual file move.
- The two shipped factions must themselves be valid Faction Definer outputs; the second faction must be upgraded from a 1:1 reskin to a genuinely asymmetric faction: at least one unique core mechanic and a roster differing from the first in role/stat profile (not renamed clones), validated in playtest via measured army-composition divergence and/or the self-play win-rate band, not a subjective judgment.
- Every editor field/button/panel needs a hover tooltip; a guided first-time onboarding flow must take a new creator from empty project to a basic playable scenario in under 15 minutes with no manual and no JSON editing. The faction-authoring completion target specifically is <=12 minutes for a first faction using only simple-mode presets.
- Authoring surfaces (including the Faction Definer) must be opt-in only, never reachable by accident from a pure Play/Skirmish context.
- Team-color pickers must use Okabe-Ito colorblind-safe swatches, and faction/team identity must never rely on color alone — pair every color with a distinguishing glyph/label.
- Determinism: any sim-side mechanic must run in the sim layer only, FixedPoint math only, ascending-id order, seeded RNG only (no wall-clock/unseeded randomness), and reproduce a byte-identical checksum across two runs of the same seed/inputs.

## Technical Decisions

- Faction-count/slot knowledge must live in exactly one place: a `FactionRegistry` (pure C#, sim layer, `src/Core`, no `using Godot`, no Godot Node types) replacing MainScene's hardcoded faction-JSON paths and size-5 faction-def array. Named constants: `PLAYER_COUNT` = 8, `FACTION_ARRAY_SIZE` = 9 (incl. Neutral slot 0). No new code may use a bare hardcoded slot-count literal or the `(Faction)(slot+1)` pattern directly — go through the registry's slot-to-Faction mapping. Out-of-range/unassigned slot lookups return a safe neutral default, never throw or index out of bounds.
- 1.0 ships verified at up to 4 player slots; the 8-slot constant is a deliberate post-1.0 fast-follow, so the registry API must already be `PLAYER_COUNT`-aware.
- `FactionDefinition`/`UnitDefinition` and any new validator must stay Godot-free (pure DTOs), matching the project-wide sim/presentation split; JSON uses `System.Text.Json` with explicit snake_case `[JsonPropertyName]` mapping. New schema fields (ai_preset, signature-mechanic descriptor, hero/persistence config) must default to values that keep the existing alpha/beta JSON valid unchanged — additive, backward-compatible.
- A single fail-closed validation convention is used project-wide (AR-39): a validator returns a structured pass/fail with a located error (which field, which id) rather than throwing/logging; Save/Playtest is blocked until PASS. The Epic-5 `FactionValidator` follows this pattern for: missing required roles, duplicate ids, dangling building-prerequisite ids, unknown/empty ai_preset, a color array not length-4 or with a component outside 0..1, missing mesh_path.
- Hero/persistence config on a faction (AR-12) plugs into the existing hero-persistence backbone from Epic 3: a stable per-hero identity and a `PersistenceManifest`/`PlayerProfile` concept for deciding which hero attributes carry across matches. Epic 5 only surfaces a hero unit reference + persistence flag on the faction definition.
- The signature-mechanic wiring (5.4) depends entirely on Epic 2's D1 Modifier/effect-graph executor (Persistent/Heal for a heal-over-time, and a non-matrix direct-HP stat-delta leaf for a flat, armor-independent self-cost). Epic 5 does not build sim mechanics infrastructure — it only attaches faction data to mechanics Epic 2 provides. The on-death "Glut" regen aura is deferred to Epic 7's on-death trigger seam — descriptor present but unwired here.
- Editors across the project share a Simple/Advanced disclosure pattern: simple mode uses preset pickers built from already-authored content (units/buildings from Epics 2-4); advanced mode is a raw-JSON escape hatch that still runs through the same validator gate. The wizard follows this and the step order: name/color -> roster -> buildings & tech -> starting conditions -> AI-preset.

## UX & Interaction Patterns

- Faction Definer wizard: a guided multi-step right-dock flow (name/color -> roster -> buildings & tech -> starting conditions -> AI preset), reachable from the Creation Suite's left tool palette, with a Simple/Advanced disclosure toggle like other editors (Unit Card, Ability, Trigger).
- Team-color step: Okabe-Ito colorblind-safe swatch picker; each color pairs with a distinguishing glyph/label (project-wide "P1 diamond, P2 triangle…" convention) — team/faction color is reserved for world-unit identity, never reused as generic UI chrome.
- Finished factions are picked up by the skirmish/lobby player-slot UI (faction select per slot, colorblind-safe color dot + glyph, ready state) without restarting the app.
- Every control in the wizard needs a hover/keyboard-focus tooltip (NFR-2), and "Your First Scenario" onboarding walks a first-time creator through the unit-authoring journey and the Faction Definer toward a playable scenario in under 15 minutes.

## Cross-Story Dependencies

- 5.1 (registry) has no epic-5 dependency and lands first; 5.2 (schema+validator) depends on 5.1; 5.3 (content hardening) depends on 5.2; 5.4 (mechanic wiring) depends on 5.3 and hard-depends on Epic 2's D1 Modifier system; 5.5 (wizard flow) depends on 5.2 and 5.3; 5.6 (AI preset/advanced/hero config) depends on 5.5; 5.7 (selectability) depends on 5.1 and 5.6; 5.8 (playtest validation) depends on 5.4 and 5.7. Story 5.9 depends on Epics 2-4's editors, the full 5.x wizard, and the Edit-Play loop from Epic 3.
- 5.2's validator is the single gate reused by every later authoring story (5.3 content check, 5.5/5.6 wizard save-block, 5.7 excludes/flags invalid authored factions) — it must not be duplicated.
