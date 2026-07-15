# Epic 14 Context: Retro Remediation (Epic-5 carryover)

<!-- Generated from planning artifacts. Regenerate with compile-epic-context if planning docs change. -->

## Goal

Epic 14 is a remediation epic: it closes a batch of confirmed defects and safety gaps filed from the Epic-5 and Epic-6 retrospectives, each tracked as a digit-only deferred-work (DW) or retro-finding key rather than a new feature. The work hardens three already-shipped systems — the research/modifier stack, the faction validator/launch gate, and the map-editor save path/tool UX — so that a repeatable +MaxHealth army-heal exploit, an ai_preset validation bypass, dangling faction descriptor ids, structurally-invalid factions reaching a match, silent persistence-manifest injection on save, and overlapping editor tool docks can no longer occur. It matters because these are exploit/integrity classes that quietly undermine determinism, faction validity, and the shipped-scenario contract; the batch is scheduled to run before Epic 7 starts so those foundations are solid before new work builds on them.

## Stories

- Story 14.1: Suppress the +MaxHealth research army-heal on re-apply (DW-85)
- Story 14.2: Close the Advanced-mode ai_preset validation bypass (DW-117)
- Story 14.3: FactionValidator resolves signature + hero descriptor ids (DW-106)
- Story 14.4: Wire FactionValidator.ValidateComplete into the launch gate (DW-97)
- Story 14.5: Editor map-save must not write a default persistence_manifest (Epic-6 retro)
- Story 14.6: Editor tool UX consolidation — single-active-dock arbitration + unified hotkey map (Epic-6 retro)

## Requirements & Constraints

- **Faction playability (FR-18/FR-20):** an AI preset must be assignable to a faction, and every faction reaching a match must be structurally complete. Remediation closes the validation half only — real AI-preset consumption by the runtime AI (AiOpponentSystem, DW-124) stays deferred to Epic 9/10; FR-18 is considered met for 1.0 once the validation bypass is closed.
- **Editor persistence contract (FR-7a / FR-21):** a per-scenario persistence manifest is an explicit creator authoring action. A scenario with no manifest must round-trip as having no manifest; `enabled: true` may only be written by a deliberate action in the persistence authoring UI. A routine editor save must never opt a map into hero persistence or corrupt the shipped-scenario contract.
- **Editor surface parity (FR-21/FR-22/FR-69):** the World-Editor tool set (terrain, region, pathability, camera, water, entity placement, unit/building cards) must be usable and discoverable — one active tool panel at a time, no key-binding collisions, one in-app hotkey reference.
- **Determinism:** every sim-touching fix (notably 14.1) must be a pure, deterministic change; two runs from the same seed stay byte-identical. Golden checksums are re-baselined only if observed sim values actually shift, and each story must state its golden disposition explicitly.
- **Verification bar:** fixes are RED-teeth-test-proven at the correct level (sim behavior, serializer/save-path, validator error), not just asserted.

## Technical Decisions

- **Snapshot-across-reapply (14.1):** research applies cumulative modifiers via remove-then-reapply on a single-cumulative-slot design (StackRule.Refresh, not Stack). The heal-on-apply of current Health is a long-standing, documented engine behavior shared by every +MaxHealth producer. The fix snapshots current Health across the remove/reapply and clamps to the new MaxHealth so a max-health raise preserves current HP instead of full-healing. Do not change the shared clamp semantics for other producers.
- **One validator truth-source (14.3/14.4):** FactionValidator exposes two deliberately-split methods — a lenient `Validate` (wired into load/Save self-checks, tolerates blank mesh_path = box placeholder) and the roster-completeness superset `ValidateComplete` (the gate). New id-resolution checks and the launch gate must reuse `ValidateComplete`; do not wire `ValidateComplete` into lenient editor-Save paths (that resurrects a prior editor regression). Descriptor resolution (signature_mechanic_effect_id, hero_unit_id) needs an AbilityRegistry/registry reference the validator does not currently take — pass it in. When adding new validated fields, add matching error-routing (StepForError) cases so located errors surface in a real wizard step.
- **Launch-gate scope (14.4):** the client-side match-load shadow diagnostic already exists (ScenarioLoadPhase, LoadSelectableFromDirectory). This story adds a fail-closed *block* at the playtest/skirmish launch boundary with an actionable message. The dedicated-server/headless match-load path (ServerBootstrap.Build / BuildHeadlessServerSimHost) is multiplayer-determinism-critical and remains out of scope unless explicitly addressed.
- **Absent-stays-absent serializer contract (14.5):** the map-save/serializer path must treat an absent `persistence_manifest` key as absent on write; an authored manifest round-trips unchanged. Prime suspect is the persistence authoring panel's save-to-file routine writing a default manifest. Document the fix at the write site.
- **Single-active-dock arbitration (14.6):** all editor tool panels share one right-dock slot; activating any tool hides all others (two docks can never overlap). Ctrl+Z/Y and Esc obey the shared editor-history/cancel contract regardless of which tool owns the dock; no tool may swallow them.

## UX & Interaction Patterns

- **Located, actionable errors:** validator rejections (14.2/14.3/14.4) must name the offending field and value/id, and launch-block messages must name what is missing — consistent with the platform's tooltip/onboarding creator-experience posture.
- **Editor discoverability (14.6):** a single in-app hotkey reference surface (overlay or hint strip) must reflect an audited, collision-free binding map; tool switching round-trips cleanly with no orphaned overlays or stuck input modes.

## Cross-Story Dependencies

- 14.1 depends on the ModifierStore and research systems (Stories 2.2b, 4.9).
- 14.2 depends on ai_preset selection (Story 5.6).
- 14.3 depends on the faction schema/validator and signature mechanics (Stories 5.2, 5.4).
- 14.4 depends on FactionValidator (5.2) and skirmish setup launch (11.1); it consumes the same `ValidateComplete` used by 14.3.
- 14.5 depends on persistence-manifest authoring (3.8) and the map save path (6.2).
- 14.6 depends on the tools being consolidated (Stories 6.4, 6.5, 6.6).
- 14.3 and 14.4 both center on FactionValidator and share its two-method contract — coordinate their changes to avoid conflicting edits.
