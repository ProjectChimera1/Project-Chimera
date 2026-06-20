# Mockups — run 2026-06-20 (self-contained)

This run is **self-contained**: the shipped Claude Design kit + all screens were copied in on
2026-06-20, and the new gap-surface mockups were relinked to **same-directory** CSS. There is no
longer any cross-folder dependency on the 2026-06-05 run (which remains as the historical source).

**Everything lives in `project-chimera/project/`** — open any screen there; the kit (`chimera.css`,
`editor.css`, `shell.css`, `browser.css`, `theme.js`, `stage.js`) is alongside it.

## Screens

| Screen | File (`project-chimera/project/…`) | Origin |
|--------|-------------------------------------|--------|
| Title / Mode / Lobby / Settings | `Shell.html` | shipped (06-05) |
| In-game HUD | `HUD.html` | shipped (06-05) |
| Creation Suite (Unit Card · Ability · Trigger · Faction wizard · Terrain · AI Gen) | `Creation Suite.html` | shipped (06-05) |
| Content Browser (mod.io) | `Content Browser.html` | shipped (06-05) |
| Design System reference | `Design System.html` | shipped (06-05) |
| UI System Hub (index) | `index.html` | shipped (06-05) |
| **Tech-Tree visual editor** (FR-14) | `tech-tree-editor.html` | **new this run** |
| **Hero Save/Load picker** (FR-7d/e) | `hero-picker.html` | **new this run** |
| **Custom runtime UI builder** (FR-26) | `custom-ui-builder.html` | **new this run** |

The 4 editor surfaces the readiness report listed as "missing" (Unit Card, Ability, Trigger,
Faction wizard) already existed inside `Creation Suite.html` — distilled into `../EXPERIENCE.md`, not
re-mocked.

## View live
```
python -m http.server 8778 --directory "<this run>/mockups/project-chimera/project"
```
Then open `http://localhost:8778/<file>.html` (e.g. `tech-tree-editor.html`). Serving from the
`project/` dir is enough — every screen links its CSS same-dir.
