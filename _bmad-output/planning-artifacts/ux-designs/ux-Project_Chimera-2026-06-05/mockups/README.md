# Finished UI — Project Chimera (Claude Design)

The canonical, finished UI we're implementing against. Exported from Claude Design as a self-contained
handoff bundle in `project-chimera/`. **Leave that bundle as-exported** — it carries its own coding-agent
README and is meant to be read as-is.

## Where things are
- `project-chimera/README.md` — the export's own "implement this" instructions (read first when implementing).
- `project-chimera/project/` — the prototypes (HTML/CSS/JS) + assets:
  - `index.html` — the **UI System hub** (links every screen).
  - **Screens** (linked source; share `chimera.css` / `theme.js` etc.):
    `Design System.html`, `HUD.html`, `Creation Suite.html`, `Shell.html`, `Content Browser.html`.
  - `standalone/` — single-file, open-anywhere copies (`00 UI System Hub`, `01 HUD`, `02 Creation Suite`,
    `03 Shell`, `04 Content Browser`). Best for quick viewing.
  - `screenshots/` — export preview PNGs · `uploads/` — pasted image asset(s).

## Implementing
Per the bundle README: read the primary file top-to-bottom, follow its imports, then recreate
**pixel-perfect in Godot Control nodes** (match the visual output; don't copy the prototype's structure).
Visual tokens reconcile into `../DESIGN.md`; behavior into `../EXPERIENCE.md`.

> Note: `project-chimera/project/` also contains a few redundant standalone HTML variants (different name
> prefixes) beside the clean `standalone/` folder — harmless export duplication, left untouched.
