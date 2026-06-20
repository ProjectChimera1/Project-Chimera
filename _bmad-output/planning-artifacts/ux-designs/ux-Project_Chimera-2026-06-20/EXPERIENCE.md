---
status: draft
updated: 2026-06-20
project: Project Chimera
kind: game-ux-experience
design_ref: ./DESIGN.md
sources:
  - Project_Chimera_GDD.md
  - _bmad-output/planning-artifacts/prds/prd-Project_Chimera-2026-06-05/prd.md
  - Snapshot.md
  - _bmad-output/project-context.md
# Behavioral source screens (to distill): ../ux-Project_Chimera-2026-06-05/mockups/project-chimera/project/{UI System Hub,HUD,Shell,Creation Suite,Content Browser}.html
---

# Project Chimera — EXPERIENCE.md

> Behavioral spine: information architecture, menu & HUD behavior, states, interactions, input
> schemes, game feel, accessibility, and named player journeys. References `DESIGN.md` tokens by
> `{path.to.token}`. Owns *how it works*; `DESIGN.md` owns *how it looks*. Both spines win on conflict.
>
> **Foundation (inherited):** Godot 4.6.2 Control-node UI at runtime; PC desktop (keyboard + mouse,
> primary); visual identity per `DESIGN.md`.

> **STATUS — Stage 2 (next).** `DESIGN.md` (visual spine) is distilled and ready for review.
> This file is bound but not yet authored. Stage 2 distills the sections below from the shipped
> screen mockups, then adds the 7 gap-surface flows + NFR-1/2/3 acceptance criteria. Section
> skeleton (canonical order):

## Foundation
_<to author: form-factor PC desktop; input modalities kbd+mouse; engine Godot Control nodes; the editor↔player dual-user model (NFR-3).>_

## Information Architecture
_<to author from `UI System Hub` + `Shell` mockups: Main Menu → Skirmish / Create / Browse / Generate / Settings; the Shell that wraps the Creation Suite; HUD information hierarchy from the `HUD` mockup.>_

## Voice and Tone
_<microcopy rules; brand voice lives in DESIGN.md Brand & Style.>_

## Component Patterns (behavioral)
_<behavioral deltas for the DESIGN.md kit: tooltip trigger timing (NFR-2), readout update cadence, list-row selection, dialog focus traps.>_

## State Patterns
_<Edit vs Play mode; construction/loading via transmute spinner; multiplayer stall banner; locked/prereq states.>_

## HUD & Diegetic UI
_<non-diegetic overlay inventory; what fades/hides during play; HUD vs editor-panel surfaces; NFR-3 "invisible to Commanders".>_

## Input Schemes
_<kbd+mouse RTS scheme; hotkey glyph system; the existing F5/Tab/B/U/N/O/L/M map; remappability (FR-51).>_

## Interaction Primitives
_<select / box-select / command / drag; edit→play round-trip (NFR-1); placement ghost; undo/redo.>_

## Game Feel & Juice
_<combat feedback, camera shake, construction progress, mode-switch feel; 130ms motion language.>_

## Accessibility Floor
_<behavioral: remappable keys, colorblind-safe team colors (FR-51), UI scaling, subtitles; visual contrast lives in DESIGN.md.>_

## Key Flows (named-protagonist journeys)
_<UJ-1..UJ-6 from the PRD, as named-protagonist journeys with a climax beat; + the 7 gap-surface flows.>_

## Gap Surfaces (new design — Stage 2)
_<Unit Card Editor (FR-2) · Ability Editor (FR-8–12) · Building/Tech-Tree editor (FR-13–14) · Faction Definer wizard (FR-17) · Trigger T2/T3 editors (FR-23–28) · Save/Load hero-picker (FR-7d/e) · custom runtime UI (FR-26).>_
