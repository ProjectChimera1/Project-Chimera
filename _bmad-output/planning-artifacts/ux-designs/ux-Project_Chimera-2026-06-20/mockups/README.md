# Mockups — run 2026-06-20

New gap-surface mockups built on the shipped Claude Design kit (`chimera.css` + `editor.css`),
linked by relative path to the 2026-06-05 run so they inherit the design system 1:1.

| File | Surface | FR | Accent | Notes |
|------|---------|----|--------|-------|
| `tech-tree-editor.html` | Tech-Tree visual editor | FR-14 | amber (editor) | Tier lanes T1→T3, building nodes with drag-port prerequisites, dependency edges, right-dock building inspector (reuses `.gnode`/`.sect`/`.fieldrow`). |
| `hero-picker.html` | Save/Load hero-picker | FR-7d/e | teal (player) | Hero slot cards (portrait, level, XP bar, signature ability, faction tag), Deploy/Overwrite/Delete, overwrite/delete confirm dialog, server-side-validation note. |
| `custom-ui-builder.html` | Custom runtime UI builder | FR-26 | amber (editor) | Widget palette → 16:9 screen canvas with safe-area; placed HUD widgets; inspector with `{variable}` bindings, 9-point anchor, style, trigger-driven visibility. Buttons fire triggers on click. |

## Already covered by the shipped Creation Suite (no new mockup needed)
These live as panel-views in `../../ux-Project_Chimera-2026-06-05/mockups/project-chimera/project/Creation Suite.html`
and are distilled into `EXPERIENCE.md`, not re-mocked:
- **Unit Card Editor** (FR-2) — `pv-unit`: model turntable, templates, Combat/Economy/Abilities/Hero sections, Promote-to-Hero, compare, live JSON, Simple/Advanced disclosure.
- **Ability Editor** (FR-8–12) — `pv-ability`: targeting & cost + effect-primitive composition.
- **Trigger editor** (FR-23–28) — `pv-triggers`: list + node-graph + ECA blocks.
- **Faction Definer wizard** (FR-17) — `pv-faction`: 5-step wizard.

## View live
From `_bmad-output/planning-artifacts/ux-designs/`:
```
python -m http.server 8777
```
Then open `http://localhost:8777/ux-Project_Chimera-2026-06-20/mockups/<file>.html`
(serving from `ux-designs/` is required so the `../../ux-Project_Chimera-2026-06-05/...` CSS links resolve).
