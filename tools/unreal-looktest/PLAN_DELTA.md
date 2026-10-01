# PLAN_DELTA — changes to PLAN.md (2026-09-30, main session). Where this and PLAN.md disagree, THIS wins.

## D1. Asset source: Tripo models first (Alec: "We can also use the assets from our tripo_output folder")
`D:\tripo-out\` holds the paid-Tripo roster (the production route since 2026-09-10; the Godot repo's GLBs are the
older Hunyuan3D-Paint set). Per roster entry (from `godot/resources/data/factions/{alpha,beta}_faction.json`,
key by the basename of `mesh_path`):

| Priority | Source | Use |
|---|---|---|
| 1 | `D:\tripo-out\<name>_rigged.glb` (also `pride_colossus-rigged.glb`, note the hyphen) | units with a rig: import as **SkeletalMesh + Skeleton + AnimSequences**, posed by an animation |
| 2 | `D:\tripo-out\<name>.glb` | buildings, and units with no rigged file (crucible_mortar, render_crawler, envy_wraithwing): import as **StaticMesh** |
| 3 | `D:\Projects\Project_Chimera\godot\<mesh_path without res://>` (Hunyuan) | only `covenant_transmuter` (alpha infantry), which has no Tripo GLB |

For `crucible_hall` use `D:\tripo-out\crucible_hall_3d_model.glb` (newer, full PBR, 17,430 tris), not `crucible_hall.glb`.
`sigil_foundry` and `bolt_sanctum` are each used by two buildings at different scales: import once, place twice.
Weapons are deliberately NOT on the models (DW-1024, attachments not built). Do not attach weapons.

Facts measured on the files (glTF JSON chunk):
- Tripo units are normalised so the LONGEST axis is 1.0 (glTF Y-up). Rigged files are in bind pose (arms out), so
  height (Y extent) is ~1.0 and width up to ~1.0. All 12 rigged files share one 41-bone Tripo biped rig.
- Rigged files: 1 image, metallic/roughness factors, no extensions. Clip names are either `preset:biped:<clip>` or
  bare `<clip>` (e.g. slash, fire, shoot, cast_a_spell, chop, dig, idle, walk, run, standing_relax, look_around,
  defeat_03, fall, wait, dance_04, surf, turn).
- Static Tripo files carry `KHR_materials_volume` + `FB_ngon_encoding`. KHR_materials_volume is the glass/volume
  extension and can render a model translucent → it MUST be stripped before import (see D2). Some static files
  (render_crawler) also carry a skin with no animations → strip the skin so it imports as a StaticMesh.
- Buildings: 3 images (base colour, metallic-roughness, normal), 15–21k tris.

## D2. Host-side prep step `tools/prep_roster.py` (new; runs BEFORE any import, plain CPython 3.14, stdlib only)
1. Read both faction JSONs, resolve each entry's source per D1.
2. Compute each entry's **Godot in-game height in metres** = Y extent of the Godot (Hunyuan) GLB's POSITION bounds,
   with node transforms (scale/rotation/translation/matrix down the node tree) applied, × `mesh_scale`.
   This is the size Godot actually renders, so the look test keeps Godot's on-screen sizes.
3. Compute the chosen source GLB's Y extent the same way (`src_height_m`). `scale = godot_height_m / src_height_m`.
4. Stage a sanitised copy of every chosen GLB to `LookTest/staging/<name>[_rigged].glb`:
   - static entries: remove `KHR_materials_volume` from `extensionsUsed`/`extensionsRequired` and from every
     material's `extensions`; remove `skins`, every node's `skin`, every primitive's `JOINTS_n`/`WEIGHTS_n`
     attributes, and `animations`. Keep `FB_ngon_encoding` (harmless).
   - rigged entries: copy unchanged unless they carry KHR_materials_volume (strip it the same way).
   - Rewrite only the JSON chunk; pad it with spaces to a 4-byte boundary; fix the chunk length and total length.
   - Re-parse the written file to prove it is valid.
5. Write `LookTest/run/roster.json`:
```json
{"generated": "<iso time>", "entries": [
  {"faction": "alpha", "kind": "unit", "id": "infantry", "role": "Melee", "name": "covenant_transmuter",
   "source": "godot_hunyuan|tripo_rigged|tripo_static", "glb": "D:/.../LookTest/staging/<file>.glb",
   "asset_key": "<name>", "rigged": false, "anims": [], "mesh_scale": 1.0,
   "godot_height_m": 2.03, "src_height_m": 2.03, "scale": 1.0, "tris": 5800}
]}
```
   `asset_key` is the import folder name (`/Game/LookTest/Roster/<asset_key>`); two entries may share one.
6. Print a table and exit non-zero if any file is missing, any re-parse fails, or any scale is outside 0.2–20.

## D3. Import changes (`lt_import.run`)
- Read `run/roster.json`; import each distinct `asset_key` once from its staged `glb`, to `/Game/LookTest/Roster/<asset_key>`.
- Checks per asset (into `run/roster_report.json`; failures stop the job):
  - static: exactly 1 StaticMesh. rigged: exactly 1 SkeletalMesh, ≥1 AnimSequence (record each anim asset path and
    which glTF clip it came from — match by name after Interchange's sanitising, e.g. `preset:biped:slash` may become
    `preset_biped_slash` or similar; record what actually exists).
  - Imported bounds height (cm) / (src_height_m × 100) within ±5% → proves the m→cm conversion. Record min.z (feet pivot).
  - Material: record the material(s) and whether `BaseColorFactor` is a vector parameter (team tint, PLAN §6.4).
  - Translucency guard: record each material's blend mode; ANY translucent/additive material is a failure.
- Team tint (PLAN §6.4) applies to skeletal meshes too (SkeletalMeshComponent `set_material(0, mi)`).

## D4. Placement changes (`lt_build`)
- Units with `rigged: true` are **SkeletalMeshActor**s: set the skeletal mesh asset, AnimationMode = single node
  (`AnimationSingleNode`), `animation_data.anim_to_play` = the chosen clip, `saved_looping=True`,
  `saved_playing=True`, `saved_position` = a seeded per-unit offset in [0, clip length) so no two neighbours are in
  sync; set `update_animation_in_editor` (bUpdateAnimationInEditor) True if it exists so the editor viewport shows
  the pose. Verify in the probe/preview that the editor shows an animated pose, not the bind (T) pose.
- Clip choice by role, first clip present wins (record the choice in the manifest):
  - Worker: chop, dig, standing_relax, idle
  - Melee (front ranks in contact): slash, run, walk, idle   · heavy/flank: slash, walk, idle
  - Ranged: fire, shoot, cast_a_spell, idle   · Mage: cast_a_spell, fire, idle
  - Scout: run, walk, idle   · Air (rigged greycrest_bonded): idle, standing_relax
- Uniform scale = roster `scale` (NOT `mesh_scale`); place feet at ground z (+ air height for flyers).
- Everything else in PLAN §7 stands (layout, ground, scatter, cameras, sky/post). The manifest hash includes the
  chosen clip and saved_position per unit.

## D5. Facing
Tripo models may face a different axis from the Hunyuan ones. `lt_facing` checks one rigged Tripo unit
(pierce_marksman), one static Tripo unit (crucible_mortar), one Tripo building (covenant_sanctum) and
covenant_transmuter (Hunyuan), and records a per-`source` yaw correction in `run/facing.json`.

## D6. Dropped from PLAN
- §11.3 clash probe with Tripo models (Tripo is now the main roster). Not replaced.
- §6.3 "within ±5% of Godot height / mesh_scale" → replaced by D3's check against `src_height_m`.

## D7. Editor-launch facts learned (main session)
- The first `Start-Process` GUI launch with `-ABSLOG` sat at ~0.8 s CPU, no window, no log, for 5 min; it was killed.
  A commandlet run of the same project initialised fine. The editor is now launched from Bash with
  `-stdout -FullStdOutLogOutput` redirected to `LookTest/logs/editor_2.stdout.log`.
- Git Bash rewrites a leading-slash argument like `/Engine/Maps/Entry` into `C:/Program Files/Git/Engine/Maps/Entry`
  (MSYS path conversion). Any command launched from Bash that passes a `/Game/...` or `/Engine/...` argument must
  set `MSYS_NO_PATHCONV=1` (or be launched from PowerShell). `run_fps.ps1` is PowerShell, so it is unaffected; any
  Bash wrapper around it is not.

## D8. MCP facts measured live (2026-09-30 23:40, editor running)
- `initialize` → 200, `Mcp-Session-Id` header, protocolVersion `2025-11-25`, serverInfo name EMPTY (not `unreal-mcp`).
- Claude Code: `claude mcp add --transport http --scope local unreal-mcp http://127.0.0.1:8000/mcp` → `✔ Connected`;
  headless `claude -p ... --allowedTools=mcp__unreal-mcp__list_toolsets` listed 52 toolsets. Exact names include
  `EditorToolset.EditorAppToolset`, `EditorToolset.LogsToolset`, `editor_toolset.toolsets.scene.SceneTools`,
  `editor_toolset.toolsets.actor.ActorTools`, `editor_toolset.toolsets.asset.AssetTools`,
  `editor_toolset.toolsets.programmatic.ProgrammaticToolset`.
- `call_tool` args: `{"toolset_name", "tool_name", "arguments"}`; tool_name is the SHORT name (e.g. `CaptureViewport`).
- `CaptureViewport` REQUIRES `captureTransform` (location/rotation/scale) and `annotations` (all six fields; pass
  gridSpacing 0, maxLabelDistance 0, classFilter {"refPath":"/Script/Engine.Actor"}, maxLabels 0 to disable) even
  though the schema calls them optional ("input param captureTransform needs a default value").
- The image comes back as a TEXT content block whose JSON is `{"returnValue":{"image":{"mimeType":"image/png","data":"<base64>"}}}`
  — NOT an MCP image block. mcp_call.py --save-images must decode this form too. Capture size was 1014x550 (the
  viewport's size), so MCP previews are low-res; final PNGs come from HighResShot / take_high_res_screenshot.
- Capture works with the editor not in the foreground (Parsec session connected).
- Bridge ping job: ok in 0.02 s; editor world was `Untitled_1` (the map arg was mangled by MSYS); editor RAM 4.2 GB.

## D9. Building size rule (main session decision, after measuring)
Height-matching Tripo buildings to Godot's Hunyuan heights gives nonsense: the Hunyuan buildings are ~2 m cubes ×
mesh_scale (5–6 m square tiles), while the Tripo buildings are real-proportioned long halls and towers (e.g.
sigil_foundry 1.0 × 0.27 × 0.38, thrall_yards 0.98 × 0.35 × 0.52). Rule for the look test:
- Units keep Godot's in-game HEIGHT (godot_height_m / src_height_m) — readability at Chimera's camera is the question.
- Buildings are sized as real buildings: longest bounding-box side = 16 m for command centres, 12 m for every other
  building. `scale = target_longest_m / src_longest_m`. Recorded in roster.json as `size_rule`.
