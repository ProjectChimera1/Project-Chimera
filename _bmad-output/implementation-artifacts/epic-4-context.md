# Epic 4 Context: Author Buildings, Tech Trees & Economy

<!-- Generated from planning artifacts. Regenerate with compile-epic-context if planning docs change. -->

## Goal

A creator authors building definitions as data, drag-builds a visual tech tree that gates production and
research, and configures a scenario's resources and supply model as data — instead of being locked to
four hardcoded building types, two hardcoded resources (Ore/Crystal), and a fixed 10-start/+10-per-
Command-Center supply rule. The epic retires the last major hardcoded-economy tables so "data-driven
everything" holds for buildings/economy the way Epic 3 delivered it for units/heroes, and extends the
tech tree beyond gating into a WC3-class research/upgrade system. Stories 4.1-4.4 retire hardcoded sim
tables first (byte-identical golden checksums preserved, so the build stays always-shippable); 4.5-4.6
add creator-facing editors on that data; 4.7 generalizes resource collection beyond gather-and-carry; 4.8a
-4.9 add faction-wide timed research producing permanent unit upgrades, with the former single research
story split into content/runtime/determinism thirds after two prior dev attempts exceeded a single-session
budget.

## Stories

- Story 4.1: Data-drive the building definition + runtime building store
- Story 4.2: Data-driven tech-prerequisite resolution with import-time cycle + referential lint
- Story 4.3: N-resource registry with sparse cost maps (generalize Ore+Crystal)
- Story 4.4: Data-driven supply / cap model per scenario
- Story 4.5: In-app building definition editor (Unit-Card pattern, right-dock inspector)
- Story 4.6: Visual tech-tree editor (tier-laned graph, drag out-port to wire prerequisites)
- Story 4.7: Per-resource collection models (INCOME / STREAMING / requires_structure) + Crystal production
- Story 4.8a: ResearchDefinition content model + validation
- Story 4.8b: ResearchSystem order path — start/complete/cancel, permanent modifier application, future-spawn catch-up
- Story 4.8c: ResearchStore SimChecksum fold + golden re-baseline
- Story 4.9: Research authoring, command-card research buttons, and upgrade display

## Requirements & Constraints

- Buildings must be creatable purely as data (stats, construction cost/time, supply bonus, produced unit
  category, tech prerequisites), matching Epic 3's unit-authoring pattern.
- Resources aren't limited to Ore+Crystal: a creator declares an ordered scenario resource registry
  (id, display, starting amount, collection model); costs become sparse `{resourceId: amount}` maps
  (omitted = free). The two-resource default must keep identical starting balances to today.
- Tech-tree gating must be a real, visually authorable dependency graph, not hand-edited arrays, and the
  runtime must enforce exactly what was drawn.
- Supply/population cap is configurable per scenario (starting cap, per-building bonus from building
  defs, optional hard ceiling, or disabled); omitting config reproduces today's default exactly.
- Resource nodes support collection models beyond gather-and-carry: INCOME (periodic flat trickle, no
  workers), STREAMING (credited in place, no carry-back leg), and an optional `requires_structure`
  proximity/ownership gate — layered on the existing GATHER round-trip and its `max_gatherers` cap, which
  stay unchanged. Crystal goes from declared-but-unproduced to actually producible.
- Research is a faction-wide, timed, repeatable (leveled) upgrade authored as data (cost map, time,
  prerequisites, per-level modifier deltas) applying a permanent modifier to all current AND future
  faction units (no per-entity copies). Authorable in the tech-tree editor, runnable from building
  command cards with visible cost/time/level/progress and a completion event; a unit's panel shows the
  aggregate upgrade contribution (e.g. "+2 Atk").
- Every retired hardcoded table (BuildingStore's stat switch, TechTreeChecker's id<->enum switches,
  ResourceStore's fixed Ore/Crystal fields, the fixed supply-cap constant, single-model GatheringSystem)
  must be replaced with no behavior change for existing content: golden checksums byte-identical at every
  story boundary, re-baselined only when a story deliberately adds new checksum-covered state (4.7's
  node/Crystal state; 4.8c's research state).
- Research content authoring (4.8a) is deliberately separated from the runtime order path (4.8b) and from
  the checksum fold + golden re-baseline (4.8c) — each is scoped to fit a single dev-agent session after
  the unsplit story twice failed to reach done. 4.8b's `ResearchStore` is mid-match-mutable but explicitly
  NOT yet multiplayer/replay-safe until 4.8c's fold lands immediately after it, with no other story
  sequenced in between.
- All new/changed gameplay state stays Fixed-point, processed in ascending id order, uses no wall-clock
  timing, and adds no Godot types to sim-layer stores/systems.
- A malformed tech tree (cycle, or a prerequisite/cost referencing an unknown id) fails at import with a
  located error naming the offending id(s) and fault kind — never silently passes.
- Definitions load through the single canonical content pipeline and are stamped with `min_game_version`;
  a definition missing a required field is rejected at import with a located error.

## Technical Decisions

- Definitions flow through the single `ContentLoader` + canonical `JsonSerializerOptions` — no bespoke
  `JsonSerializer.*` calls. Content only reaches sim code wrapped in `Validated<T>` (minted solely by the
  validator after cycle/referential-integrity/cap-cost lint) — the same gate Epic 1's damage table and
  Epic 3's units/heroes use. Malformed content is a located error, never a null-swallow.
- BuildingDefinition extends/reuses the UnitDefinition shape (Epic 3's pattern) plus building fields
  (construction_time, supply_bonus, produces_category, construction_cost as a cost map). The building-
  type enum may remain a back-compat alias but no longer gates what can exist.
- Tech-tree prerequisites stay inline `prerequisites: string[]` resolved against a data-driven id
  registry built from loaded definitions, generalizing the existing partial runtime read off hardcoded
  enum switches.
- The N-resource registry/sparse cost maps generalize Ore+Crystal: registry order defines balance-array
  indexing; Ore/Crystal ship as the default registry, and legacy `cost_ore`/`cost_crystal` parse into the
  new cost map for back-compat.
- Research reuses the existing Epic 2 modifier pipeline (ModifierStore/ModifierSystem) as a faction-
  scoped permanent modifier source — no new stat pipeline; future units acquire bonuses via the existing
  Base/Effective recompute at spawn (the same spawn-hook wiring Epic 2's self-passive uses). Research
  orders ride the same shared order-application path already used for unit training (exec-tick spend,
  never at UI/issue-time). A repeatable research keeps one cumulative modifier slot per research
  definition (sum of all completed levels' deltas), never one slot per level.
- New per-store state (supply bonus, resource balances, node collection state, research progress) is a
  new SoA array folded into the generalized SimChecksum — never a Dictionary-backed store (a known
  nondeterminism source elsewhere in the codebase).
- The building and tech-tree editors are presentation-layer only (Godot Control/GraphEdit), reading/
  writing definition JSON via the canonical serializer only, never mutating sim arrays.
  `TriggerEditorPanel` is the canonical editor-panel composition to follow; Epic 3's "Unit-Card" pattern
  is what building cards reuse.

## UX & Interaction Patterns

- Building authoring reuses the Unit-Card pattern (card list + right-dock inspector) plus a raw-JSON
  escape hatch, kept in sync with simple-mode cards on reload (layered complexity).
- The tech-tree editor is a tier-laned graph (Godot GraphEdit): each building is a node; dragging a
  node's out-port onto another node creates a dependency edge appending the source id to the target's
  prerequisites; deleting the edge removes it. An edge that would create a cycle or invalid reference is
  rejected inline at drop time, consistent with the import-time lint. Selecting a node opens the same
  right-dock inspector as the building editor. Research nodes drag into the same graph under the same
  prereq-lint.
- Research runs from building command cards: buttons show cost/time/level, dim when unaffordable/capped/
  prerequisite-missing (existing command-card dimming pattern); in-progress research shows a progress
  indicator; completion emits an event on the existing combat event bus.
- Invalid authoring input (negative cost, blank id, duplicate id) is rejected in-panel with an inline
  located message and never written to disk.

## Cross-Story Dependencies

- Dependency order runs backward: 4.2 needs 4.1's building registry; 4.3 needs 4.1's loader path; 4.4
  needs 4.1 + 4.3; 4.5 needs 4.1 + 4.3; 4.6 needs 4.2 + 4.5; 4.7 needs 4.3; 4.8a needs 4.2 (validation
  gate pattern); 4.8b needs 4.8a + Epic 2's modifier system (Story 2.2b); 4.8c needs 4.8b and must land
  immediately after it with nothing else sequenced in between; 4.9 needs 4.8c + 4.6.
- FR-13 (building authoring) splits across 4.1 (data/runtime) and 4.5 (UI); FR-14 (tech-tree gating)
  splits across 4.2 (runtime gate) and 4.6 (visual editor) — read each pair together for the full
  requirement.
- Follows Epic 3's data-driven unit/hero pattern (same UnitDefinition shape, editor-panel/card
  conventions) and feeds Epic 5 (Faction Definer), which assembles buildings/tech trees/resources
  authored here into complete playable factions.
