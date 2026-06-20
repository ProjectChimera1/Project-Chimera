# Imports — developer-supplied visuals

| Item | Path | Notes |
|------|------|-------|
| **Claude Design UI (latest)** | `../../ux-Project_Chimera-2026-06-05/mockups/project-chimera/` | Confirmed latest by Alec 2026-06-20. Referenced in place, not copied (large). Authoritative visual source for `DESIGN.md`. |

### What the import contains
- `project/chimera.css` — **the design system** (tokens + Godot Control–mappable component kit). Primary source for `DESIGN.md`.
- `project/shell.css`, `editor.css`, `browser.css` — per-surface layout/styles (Shell, Creation Suite, Content Browser).
- `project/theme.js` — theme/accent persistence + the "Chimera Seal" SVG mark + transmute-spinner sprites.
- `project/index.html` — UI System Hub entry.
- `project/{HUD,Shell,Creation Suite,Content Browser,Design System}.html` — screen mockups (source for `EXPERIENCE.md` IA/behavior).
- `project/editor-data.js`, `stage.js` — Creation Suite data model + stage behavior.
- `standalone/` — standalone screen exports. `screenshots/`, `uploads/` — reference imagery.

Spines win on conflict with these mockups.
