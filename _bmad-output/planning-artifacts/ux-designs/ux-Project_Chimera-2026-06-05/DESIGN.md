---
status: draft
updated: 2026-06-05
project: Project Chimera
kind: game-ux-design
sources:
  - Project_Chimera_GDD.md
  - _bmad-output/planning-artifacts/prds/prd-Project_Chimera-2026-06-05/prd.md
  - Snapshot.md
colors: {}
typography: {}
rounded: {}
spacing: {}
components: {}
---

# Project Chimera — DESIGN.md

> Visual identity spine. Frontmatter tokens + body distilled at Finalize from `.decision-log.md`,
> `.working/`, `imports/`, and confirmed sources. This file owns *how it looks*; `EXPERIENCE.md`
> owns *how it works* and references these tokens by `{path.to.token}`. Both spines win on conflict
> with any mock or import.

## Brand & Style _(draft — confirmed directions; token values pending producer output)_

Project Chimera is an **RTS creation platform** — a game and a creation tool in one. The UI must
serve both a player in a fast match and a creator in a long authoring session, and read clearly at
zoomed-out RTS camera distances.

- **HUD diegesis — non-diegetic overlay.** The in-game HUD is honest, flat UI floating over the 3D
  world (classic-RTS / SC2 convention), prioritizing clarity over in-world illusion.
- **Aesthetic — stylized low-poly echo.** UI chrome echoes the 3D art direction: faceted/angular
  panel shapes, flat cel-shaded color blocks, geometric icons — so UI and world feel like one
  piece. The 3D art reference band is Mindustry (utilitarian clarity) ↔ Northgard (painterly
  warmth); the UI leans toward the clarity end while keeping the faceted, hand-made character.
- **Theme — dark primary.** Dark panels by default (eye comfort for long creation sessions,
  editor-tool convention), light text, a single brand accent for interactive/highlight states.
- **Faction colors are sacred and reserved.** Team identity colors live on units in the world; UI
  chrome accents must never compete with or be confused for them. (Accessibility: faction colors
  must be colorblind-safe — see EXPERIENCE.md Accessibility Floor.)

> Token tables (colors, typography, rounded, spacing, components) are populated at Finalize from the
> producer's output and reconciled against these confirmed directions. Where they conflict, this
> spine wins.

_Body below frontmatter is a captured-decisions draft; full distillation occurs at Finalize once
the design-handoff output returns._
