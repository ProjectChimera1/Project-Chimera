# Execution plan: Unreal MCP + look test (UE 5.8.3, RTX 3060, 1080p)

**Verification basis.** I checked the dossier read-only against the installed engine on 2026-09-30.
- "Confirmed" means I read it at the cited file and line. E = `D:\Epic Games\UE_5.8\Engine`.
- "CHECK" means the plan verifies it at run time before anything depends on it.
- I dropped every dossier claim marked UNCONFIRMED unless a CHECK covers it (see section 13).
- Nothing below runs until the current Build.bat has finished.

---

## 0. Decisions and reasons

### 0.1 Who drives each step

| Step | Driver | Why |
|---|---|---|
| Enable plugins, edit ini | Text edits to `.uproject` and `DefaultEngine.ini`, then one Build.bat | The MCP server does not exist yet. These edits need no editor. |
| Start editor and MCP server | Launch flags `-ModelContextProtocolStartServer` and `-ExecutePythonScript=…/ue_bridge.py` | Both confirmed in source (MCP: `ModelContextProtocolSettings.cpp:34`; Python: `EditorPythonExecuter.cpp:144-210`). The `bAutoStartServer` ini route is not used. |
| GLB import, team-tint materials, level build, lights, post | Python jobs run inside the live editor through a file-queue bridge (game thread) | MCP cannot import our GLBs: `StaticMeshTools.import_file` pins `unreal.FbxFactory()`, and `require_factory_supports` rejects `.glb` (`…/editor_toolset/toolsets/static_mesh.py:42-54`, `asset.py:12-20`). MCP has no run-any-Python tool and no console-command tool. A map holds about 1,000 actors, which would mean about 1,000 MCP round trips. Job files can be rerun whenever the assets change. |
| Inspection, preview images, log checks, camera nudges | MCP: `EditorAppToolset.CaptureViewport`, `GetVisibleActors`, `LogsToolset.GetLogEntries`, `SearchCVars` | Images come back straight to Claude, with an optional grid and labels. This use also proves the MCP link works. |
| Final PNGs, gameplay camera | `HighResShot` inside the standalone `-game` frame-rate run | It is exactly what a player sees, and it is the frame the fps number belongs to. |
| Final PNGs, close camera; fallback for all PNGs | `unreal.AutomationLibrary.take_high_res_screenshot` in the editor | It locks the viewport to the CameraActor and flushes shaders and streaming (implementation read). |
| Frame rate | Standalone `UnrealEditor.exe -game` with the CSV profiler | Editor-viewport fps is skewed by the editor's own overhead. |
| Composite | Host Python 3.14 with Pillow 12.2 (both installed, checked) | It pastes the renders untouched; labels go in a bar outside the image. |

### 0.2 Plugins
- Enable ModelContextProtocol + ToolsetRegistry + **AllToolsets**, as Alec asked (HANDOFF step 2). Also enable EditorToolset explicitly, plus PythonScriptPlugin, EditorScriptingUtilities and PCGBiomeSample.
- Verified today:
  - All 21 AllToolsets dependencies, plus ToolsetRegistry and ModelContextProtocol, have `Binaries\Win64\UnrealEditor.modules` with BuildId 55116800.
  - The transitive runtime dependencies all have binaries (SemanticSearch, CommonConversation, DataRegistry, GeometryCollectionPlugin, GameFeatures, GameplayAbilities, StateTree, WorldConditions, FileSandbox and the rest).
  - None of them declares `PythonRequirements`.
  - So there is no engine compile and no pip download.
- **Kill rule** (metric: editor private bytes):
  - If the editor at idle on `/Engine/Maps/Entry` uses more than 9 GB private, or
  - if any toolset logs a load error,
  - then replace AllToolsets with EditorToolset only, relaunch, and report both numbers.

### 0.3 A bridge, not one editor launch per script
- `ue_bridge.py` is started by `-ExecutePythonScript`.
- It sets `unreal.EditorPythonScripting.set_keep_python_script_alive(True)` and registers `unreal.register_slate_post_tick_callback`. On each tick it runs job files from `LookTest\queue\`.
- This means one editor session (one shader compile) and fast iteration. MCP stays live alongside it.
- Jobs are generators that `yield` between items, so the editor keeps ticking and MCP calls are not starved.
- Every job module also runs standalone through `-ExecutePythonScript` (fallback F1).
- Chosen over UDP remote execution (`bRemoteExecution`): that needs an ini change, uses multicast, and is untested here.
- Chosen over `Content/Python/init_unreal.py`: that would run in every future editor session, including Alec's own.

### 0.4 Ray tracing off
- Change `r.RayTracing=True` to `False` in `DefaultEngine.ini` **before the first editor launch**, so shaders compile only once.
- Effects: Lumen falls back to software Lumen (the realistic case on a 3060), compile time and VRAM go down, and the 16 GB RAM machine is less likely to page.
- `r.Substrate=True` stays on: it is the template default and what production would use. This is noted as a caveat.

### 0.5 Same assets in every look
Units, buildings, ground textures, trees, layout and camera are identical in every map. Only these change:
- sun angle and softness
- sky light, clouds and fog
- GI and reflection method
- post-process grade

That isolates the look from the assets, so the redo-cost judgement is meaningful. Three maps:
- **A**: Manor-Lords-style, Lumen.
- **A_noLumen**: identical to A but with screen-space GI and reflections. Manor Lords reportedly ships without Lumen (secondary source), so this is the faithful fps number.
- **B**: Northgard-style.

### 0.6 Camera
Two cameras, both shared by all three maps.

**CAM_Gameplay** reproduces Chimera's real default RTS camera:
- From `RtsCameraController.cs:45-46`: pitch 50°, distance 80 m, camera at pivot + (0, 80·sin50, 80·cos50).
- Godot FOV: no `.tscn` contains a Camera3D, and the controller never sets an FOV, so it is Godot's default. CHECK that the default is 75°, vertical (KEEP_HEIGHT).
- In UE: location (0, -5142, 6128) cm, rotation (pitch -50, yaw 90, roll 0).
- Horizontal FOV = 2·atan(tan37.5°·16/9) = **107.5°**. `bConstrainAspectRatio=False`. `AutoActivateForPlayer=Player0`.
- This is the readability shot: units are about 11–18 px tall at this zoom.

**CAM_Close** shows material and lighting detail:
- Pivot (-2, 2, 0) m, distance 30 m, pitch -40.
- Location (-200, -2098, 1928) cm, rotation (-40, 90, 0), horizontal FOV 65.8° (40° vertical).
- No auto-activate.

### 0.7 Honesty rules
- Renders are never retouched. The composite only pastes the images and adds labels outside them.
- SHA-256 of every source PNG is recorded.
- Each look gets the same tuning budget: at most 3 preview iterations, with fixed knobs (section 8).
- Every recipe value is logged to `results.json`.

---

## 1. File layout (all new; nothing in Project_Chimera changes until the checkpoint commit)

`D:\Projects\Chimera-Unreal\ProjectChimera\LookTest\` (no spaces anywhere in the path; on purpose):

| Folder | Contents |
|---|---|
| `tools\` | Editor-side: `ue_bridge.py`, `lt_common.py`, `lt_probe.py`, `lt_import.py`, `lt_facing.py`, `lt_build.py`, `lt_capture.py`, `lt_admin.py`. Host-side: `ue_job.py`, `mcp_call.py`, `run_fps.ps1`, `parse_csv.py`, `composite.py`. Fallbacks: `glb2fbx_blender.py`, `ground_glb.py`. |
| `queue\` | `*.job.json` in; `.running` and `.result.json` out |
| `run\` | `bridge_ready.json`, `probe.json`, `toolsets.json`, `facing.json`, `roster_report.json`, `manifest_<look>.json` |
| `logs\` | editor and game logs, `nvidia-smi` samples |
| `out\` | PNGs, `csv\`, `results.json`, composites |

UE content goes under `/Game/LookTest/{Roster/<name>, Ground, Materials, Maps}`.

Back up both config files first (the project is **not** a git repo):
`Copy-Item <file> <file>.bak-20260930` for `ProjectChimera.uproject` and `Config\DefaultEngine.ini`.

---

## 2. Preflight (PowerShell, after Build.bat exits)

1. Build result:
   - The background Build.bat ended `Result: Succeeded` (exit 0).
   - `Test-Path D:\Projects\Chimera-Unreal\ProjectChimera\Binaries\Win64\UnrealEditor-ProjectChimera.dll` is true.
   - Note whether it needed `-NoUBA`.
2. Nothing engine-related is running: `Get-Process UnrealEditor*,UnrealBuildTool,ShaderCompileWorker -ErrorAction SilentlyContinue` returns nothing.
3. Port 8000 is free: `Get-NetTCPConnection -LocalPort 8000 -State Listen -ErrorAction SilentlyContinue` returns nothing. If it is taken, use 8765 everywhere (`-ModelContextProtocolPort=8765` and the URL).
4. GPU is idle: `nvidia-smi --query-compute-apps=pid,process_name,used_memory --format=csv`. ComfyUI (`D:\tools\ComfyUI_windows_portable`) must not be running for the fps runs.
5. Display:
   - Already confirmed as 1920×1080 on the RTX 3060, with a Parsec Virtual Display Adapter present. So the game runs use `-fullscreen` (a 1920×1080 window will not fit inside the desktop).
   - Record whether a Parsec client is connected during the fps runs: `Get-Process parsecd`.
6. Memory and disk:
   - `Get-CimInstance Win32_OperatingSystem | Select FreePhysicalMemory,TotalVirtualMemorySize`. The page file was set to 16–32 GB after the last build failure.
   - At least 25 GB free on D: (DDC plus Saved).

## 3. Config edits

### 3.1 `.uproject`
Replace the `Plugins` array of `D:\Projects\Chimera-Unreal\ProjectChimera\ProjectChimera.uproject`. Use the Write tool, keep tab indent, and leave every other key unchanged.
```json
"Plugins": [
	{ "Name": "ModelingToolsEditorMode", "Enabled": true, "TargetAllowList": ["Editor"] },
	{ "Name": "PythonScriptPlugin", "Enabled": true },
	{ "Name": "EditorScriptingUtilities", "Enabled": true },
	{ "Name": "ModelContextProtocol", "Enabled": true, "TargetAllowList": ["Editor"] },
	{ "Name": "ToolsetRegistry", "Enabled": true, "TargetAllowList": ["Editor"] },
	{ "Name": "AllToolsets", "Enabled": true, "TargetAllowList": ["Editor"] },
	{ "Name": "EditorToolset", "Enabled": true, "TargetAllowList": ["Editor"] },
	{ "Name": "PCGBiomeSample", "Enabled": true }
]
```
- `TargetAllowList: Editor` keeps these NoRedist experimental plugins out of any packaged game. `UnrealEditor.exe -game` is still the Editor target, so they still load there.
- EditorToolset's `.uplugin` has no `EnabledByDefault`, so it must be listed. AllToolsets would also pull it in.

### 3.2 `DefaultEngine.ini`
- Change one line in `D:\Projects\Chimera-Unreal\ProjectChimera\Config\DefaultEngine.ini`: `r.RayTracing=True` → `r.RayTracing=False`. Use the Edit tool, as text. Nothing else changes.
- Fallback, only if the first launch pages heavily: add `[DevOptions.Shaders]` with `NumUnusedShaderCompilingThreads=6`. The base value is 3 (`E\Config\BaseEngine.ini:2181`).

### 3.3 Relink after the plugin list changes
```
"D:\Epic Games\UE_5.8\Engine\Build\BatchFiles\Build.bat" ProjectChimeraEditor Win64 Development -Project="D:\Projects\Chimera-Unreal\ProjectChimera\ProjectChimera.uproject" -WaitMutex
```
- Add `-NoUBA` only if the first build needed it.
- Expect a short run and exit 0. Engine plugins are precompiled in an installed build.
- This removes the "modules missing, rebuild?" dialog. Under `-unattended` that dialog would answer No and quit.

## 4. Tooling skeletons (written with the Write tool; scripts over 15 lines live in files)

### 4.1 `tools\ue_bridge.py`: editor side; the `-ExecutePythonScript` entry point
```python
import unreal, sys, os, json, time, traceback, importlib, io, contextlib
LT = 'D:/Projects/Chimera-Unreal/ProjectChimera/LookTest'; sys.path.insert(0, LT + '/tools')
unreal.EditorPythonScripting.set_keep_python_script_alive(True)      # else the editor quits next tick (EditorPythonExecuter.cpp:73)
cdo = unreal.get_default_object(unreal.EditorPerformanceSettings)    # session-only, not saved
cdo.set_editor_property('throttle_cpu_when_not_foreground', False)   # CHECK name in probe
S = {'gen': None, 'job': None, 'buf': None, 't': 0}
def _atomic_write(path, obj): ...tmp file + os.replace
def _claim():   # oldest queue/*.job.json -> rename .running -> import/reload module, call func(**args)
    # if the result is a generator, keep it in S['gen'], else wrap it as an iterator
def _tick(dt):
    # no job: poll the queue every 0.5 s -> _claim()
    # job running: next(S['gen']) under redirect_stdout(S['buf'])
    #   StopIteration(e.value) -> write <job>.result.json {ok, value, stdout, secs}
    #   Exception -> write {ok: False, traceback}
H = unreal.register_slate_post_tick_callback(_tick)
_atomic_write(LT + '/run/bridge_ready.json', {'pid': os.getpid(), 'engine': unreal.SystemLibrary.get_engine_version(), 't': time.time()})
```
Shared helpers in `lt_common.py`:
- `wait_frames(n)`: a generator that yields n times.
- `wait_seconds(s)`
- `png_size(path)`: reads the IHDR with `struct`, no Pillow inside UE.
- Roster loader: reads `D:\Projects\Project_Chimera\godot\resources\data\factions\{alpha,beta}_faction.json`, keys `units[]`/`buildings[]` → `id`, `mesh_path` (`res://` → `D:/Projects/Project_Chimera/godot/`), `mesh_scale`. No hand-copied numbers.
- Team colours and the tint maths (§6).
- `layout(seed=20260930)` (§7).
- `CAMS` (§0.6) and `RECIPES` (§8).

`lt_admin.py`:
- `ping()`
- `quit()`: `set_keep_python_script_alive(False)`. The executer then calls `QUIT_EDITOR` (`EditorPythonExecuter.cpp:71-77,113-121`).

### 4.2 `tools\ue_job.py`: host side
- Usage: `python ue_job.py <module> [func=run] ['{json args}'] [--timeout 1800]`.
- Writes `queue\<ts>_<module>.job.json` atomically, then waits in-script (1 s loop, bounded by the timeout) for `.result.json`.
- Prints it; exits 1 when `ok:false`.
- Long jobs run with `run_in_background`. No harness sleep-polling.

### 4.3 `tools\mcp_call.py`: host side, stdlib only, talks to the Unreal endpoint directly
- `rpc(method, params, sid)`:
  - POSTs JSON-RPC to `http://127.0.0.1:8000/mcp`.
  - Headers: `Content-Type: application/json`, `Accept: application/json, text/event-stream`, `MCP-Protocol-Version: 2025-11-25`, `Mcp-Session-Id: <sid>` after initialize.
  - Parses a plain JSON body, or SSE `data:` lines.
- `session()`: `initialize` {protocolVersion 2025-11-25, capabilities {}, clientInfo}, then reads the `Mcp-Session-Id` response header, then sends the notification `notifications/initialized`.
- Subcommands:
  - `init`
  - `tools`
  - `toolsets`
  - `describe <toolset>`
  - `call <toolset> <tool> '<json>' --save-images DIR`: wraps `call_tool{toolset_name, tool_name, arguments}`, base64-decodes each `type:"image"` content block to PNG (`ModelContextProtocolToolResults.cpp:49-50`), and prints the text blocks.
- Why it exists:
  - It works from this session even before Claude Code's native MCP tools load.
  - It makes MCP steps scriptable.

### 4.4 Job modules (editor side)
- `lt_probe.run()`: CHECKs every Python spelling the plan relies on (§13 list). Uses `hasattr` / try-get on the classes, CDOs, struct fields and enums, plus `does_asset_exist` for every content path. Writes `run/probe.json` with `{name: bool}` and a filtered `dir()` for each failure, so a wrong spelling is fixed from data, not guessed.
- `lt_import.run()`: §6.
- `lt_facing.run()`: §6.4.
- `lt_build.run(look)`: §7.
- `lt_capture.run(look)`: §9.

## 5. First editor launch, probe, MCP connect

### 5.1 Launch (PowerShell, detached)
```powershell
Start-Process -FilePath 'D:\Epic Games\UE_5.8\Engine\Binaries\Win64\UnrealEditor.exe' -ArgumentList '"D:\Projects\Chimera-Unreal\ProjectChimera\ProjectChimera.uproject" /Engine/Maps/Entry -ModelContextProtocolStartServer -ExecutePythonScript=D:/Projects/Chimera-Unreal/ProjectChimera/LookTest/tools/ue_bridge.py -unattended -nosplash -nosound -ABSLOG=D:/Projects/Chimera-Unreal/ProjectChimera/LookTest/logs/editor_1.log'
```
- `/Engine/Maps/Entry` avoids loading the World-Partition `OpenWorld` default map.
- Use Monitor (load it via ToolSearch) with an until-loop on `run\bridge_ready.json` existing, or on `UnrealEditor` no longer running (= failure). Allow up to 120 min: the first shader compile with Substrate + SM6 on 6 cores.

### 5.2 Checks after the launch
- **Log** (`editor_1.log`):
  - Plugin mount lines for ModelContextProtocol, ToolsetRegistry, EditorToolset and PCGBiomeSample.
  - No `failed to load`.
  - No `modules are missing`.
  - Look for an MCP listening line, but the HTTP probe below is the real gate.
- **Memory**: `Get-Process UnrealEditor | Select WorkingSet64,PrivateMemorySize64`. Apply the AllToolsets kill rule (§0.2).
- **Probe**: `python ue_job.py lt_probe`. Any false entry gets fixed in `lt_common` before going on.

### 5.3 MCP verification
1. `python mcp_call.py init`: serverInfo name `unreal-mcp`, protocol `2025-11-25`, a session id. Loop on this in Monitor until it succeeds, because the server starts after engine init (`ModelContextProtocolEditor.cpp:64-68`).
2. `python mcp_call.py tools`: exactly `list_toolsets`, `describe_toolset`, `call_tool` (tool search is on by default).
3. `python mcp_call.py toolsets > run/toolsets.json`: record the **exact** toolset strings for EditorAppToolset, LogsToolset, SceneTools, ActorTools and AssetTools. Expected forms are `EditorToolset.EditorAppToolset` and `editor_toolset.toolsets.scene.SceneTools`; CHECK them here.
4. `python mcp_call.py call <EditorAppToolset> CaptureViewport '{"bShowUI":false}' --save-images out/mcp_smoke`: Claude opens the PNG. This proves capture works while the editor is not the foreground window.

### 5.4 Register with Claude Code
Run from the project the sessions use, local scope (least invasive: only `D:\Projects\Project_Chimera` sessions see it):
```bash
cd /d/Projects/Project_Chimera && claude mcp add --transport http --scope local unreal-mcp http://127.0.0.1:8000/mcp
cd /d/Projects/Project_Chimera && claude mcp list      # expect: unreal-mcp ... ✓ Connected (health-checked)
cd /d/Projects/Project_Chimera && claude -p --model claude-sonnet-5-5 --allowedTools "mcp__unreal-mcp__list_toolsets" "Call the unreal-mcp list_toolsets tool and print only the toolset names."
```
- The headless run is the end-to-end proof that Claude Code itself calls the server. Its names must match `run/toolsets.json`.
- The current main session sees native `mcp__unreal-mcp__*` tools only after a restart (CHECK `/mcp`), so it keeps using `mcp_call.py`.
- When the editor is closed, the server shows as failed. That is harmless.
- An editor restart invalidates the session, so call `init` again.

## 6. Import the roster (`lt_import.run`): Python bridge job

### 6.1 Ground textures first
- Import `D:\Projects\Project_Chimera\godot\assets\textures\terrain\grass.png` and `dirt.png` to `/Game/LookTest/Ground`, with `AssetImportTask`, automated.
- CHECK sRGB is on.

### 6.2 The 24 GLBs
For each distinct GLB (16 units + 8 buildings; the aviaries reuse `sigil_foundry`/`bolt_sanctum`), one folder each:
```python
at = unreal.AssetToolsHelpers.get_asset_tools()
t = unreal.AssetImportTask(); t.filename = <abs .glb>; t.destination_path = f'/Game/LookTest/Roster/{name}'
t.destination_name = name; t.automated = True; t.replace_existing = True; t.save = True   # no factory -> Interchange glTF
at.import_asset_tasks([t]); objs = [unreal.load_asset(p) for p in t.get_editor_property('imported_object_paths')]
sm = [o for o in objs if isinstance(o, unreal.StaticMesh)]   # expect exactly 1
yield  # let the editor tick between files
```
- One folder per file because every GLB names its node `LP_baked` and its material `chimera_baked`; separate folders make the collisions harmless.
- Interchange uses the per-translator glTF pipelines (`E\Config\BaseEngine.ini:3785`). Under Substrate the material parent is `/InterchangeAssets/gltf/Substrate/M_GLTF` (`E\Plugins\Interchange\Runtime\Config\DefaultInterchange.ini:60`).

### 6.3 Checks per mesh (written to `run/roster_report.json`; any failure stops the job)
- Exactly 1 StaticMesh.
- Bounds height (`get_bounding_box()`) within ±5% of (Godot in-game height / `mesh_scale`) × 100 cm. For example, `covenant_transmuter` should be about 203 cm. CHECKs the ×100 m→cm conversion.
- `min.z` within ±1 cm of 0. CHECKs the feet pivot.
- 1 material slot.
- Nanite flag recorded.
- `'BaseColorFactor' in unreal.MaterialEditingLibrary.get_vector_parameter_names(mi)`.

### 6.4 Team tint, identical in maths to Godot
Godot's shader (`TeamTintMaterial.cs`) does `mix(art, art*team, 0.35) * value_scale`, and `team_color` is `source_color` (sRGB → linear).
- glTF base colour is factor × texture, so a per-team MI with `BaseColorFactor = (0.65 + 0.35·srgb_to_linear(team)) × value` reproduces it exactly.
- Alpha uses P1 blue (0.2, 0.5, 1.0) → factor (0.662, 0.725, 1.000).
- Beta uses P2 red (1.0, 0.3, 0.2) → factor (1.000, 0.676, 0.662).
- Buildings are multiplied by 0.88.
- Create the MI with `create_asset(f'MI_{name}_team', folder, unreal.MaterialInstanceConstant, unreal.MaterialInstanceConstantFactoryNew())`, then `MaterialEditingLibrary.set_material_instance_parent(mi, imported_mi)`, `set_material_instance_vector_parameter_value(mi, 'BaseColorFactor', unreal.LinearColor(...))`, `update_material_instance(mi)`.
- Godot's Fresnel rim term (0.25 / power 5) is **not** reproduced. This is noted as a caveat.

### 6.5 Facing (`lt_facing.run`)
- Build a temporary map `/Game/LookTest/Maps/LT_Facing` with `pierce_marksman` and `covenant_transmuter` at the origin, yaw 0, and a `/Engine/BasicShapes/Cone` pointing +X.
- Capture top and side views with MCP `CaptureViewport` (`CaptureTransform` + `Annotations`).
- Claude records the model-front axis in `run/facing.json`. Expected from source: glTF +Z → UE +Y, so alpha yaw is -90 (faces +X) and beta is +90.

## 7. Build the maps (`lt_build.run(look)` for `A`, `A_noLumen`, `B`)

Map path: `/Game/LookTest/Maps/LT_<look>`.
- If the map exists, load Entry, `EditorAssetSubsystem.delete_asset`, then create it fresh.
- `les = unreal.get_editor_subsystem(unreal.LevelEditorSubsystem)`; `les.new_level(path)` (non-partitioned).
- `eas = unreal.get_editor_subsystem(unreal.EditorActorSubsystem)`.

### 7.1 Ground
- `/Engine/BasicShapes/Plane` (100 cm), scale 1200, centred at (0, 200 m, 0). It must cover the far field: CAM_Gameplay's top edge ray hits the ground about 275 m out.
- Material `M_LT_Ground`, built in Python with `MaterialFactoryNew`, then `MaterialEditingLibrary.delete_all_material_expressions` (clears any default Substrate slab), then legacy pins:
  - grass and dirt textures at TexCoord ×240 (5 m tiles)
  - lerp mask from `/BaseMaterial/Textures/Noises/T_Variation_1k_RGB_nonVT` at ×8
  - scalar `DirtOverride`
  - roughness 0.9
  - `recompile_material`
- CHECK under Substrate: the base-colour input is wired to our node, and an MCP preview shows textured ground. Fallbacks F4.
- Base pads: a 40 m plane per base, z +2 cm, MI with `DirtOverride=1`.

### 7.2 Units and buildings
`layout(seed=20260930)` in metres; UE uses cm. Alpha is on -X; beta mirrors x → -x with the yaw flipped.
- **Buildings, alpha**:
  - CC `covenant_sanctum` (-58, 14)
  - barracks `crucible_hall` (-46, 26)
  - archery `sigil_foundry` at s 2.5 (-46, 2)
  - siege `transmutation_forge` (-62, 32)
  - aviary `sigil_foundry` at s 2.8 (-66, -4)
- **Workers**: 4 per side near a grove at (∓70, 40).
- **Battle around (0, 6)**:

| Role | Count | x | y | Notes |
|---|---|---|---|---|
| Infantry | 8 | -1.6 / -3.0 (2 ranks) | 0..12 | beta's front rank at +1.4, so the lines touch |
| Heavy | 4 | -4.5 | -2..14 | flank |
| Archers | 6 | -11 | 1..11 | |
| Mages | 2 | -14 | 4, 8 | |
| Siege | 1 | -20 | 6 | |
| Scout (alpha) / bulwark (beta) | 2 | -8 | -6, 18 | |
| Flyers | 2 | (-2, 3), (-5, 10) | | z 7 and 8 m |

- Jitter ±0.35 m and yaw ±15°.
- Spawn: `eas.spawn_actor_from_object(sm, loc, rot)`, then `set_actor_scale3d(mesh_scale)`, `static_mesh_component.set_material(0, team_mi)`, `set_actor_label`, `set_folder_path('Alpha/Units')`.
- About 58 units and 10 buildings in total.

### 7.3 Scatter
Seeded, with caps:
- At most 450 trees: `/PCGBiomeSample/Meshes/PCG_Tree_01`, `PCG_Tree_02`, `PCG_Spruce_01`, `PCG_Pine_01`.
- At most 400 undergrowth: `PCG_Sapling_01/02`, `PCG_Seedling_01/02`.
- At most 40 `PCG_Boulder_01`.
- Placement: forest band y 40–300 m; side clumps |x| 90–350 m; groves at (∓78, 46); excluded from battle and base areas.
- Scales are set from probe-read bounds so trees are 8–16 m tall.

### 7.4 Cameras and player start
- `CAM_Gameplay` and `CAM_Close` (§0.6) via `spawn_actor_from_class(unreal.CameraActor, …)`.
  - `camera_component.field_of_view`, `constrain_aspect_ratio=False`.
  - `auto_activate_for_player = unreal.AutoReceiveInput.PLAYER0` on CAM_Gameplay only.
  - Confirmed mechanism: `CameraActor.cpp:102-117`, `PlayerController.cpp:603-628`.
- `PlayerStart` at (-300 m, -300 m, 1 m). The DefaultPawn sphere is only `bOwnerNoSee` (`DefaultPawn.cpp:67`), so keep it far out of frame.

### 7.5 Sky and post (§8 values)
- Actors: DirectionalLight, SkyAtmosphere, SkyLight, ExponentialHeightFog, PostProcessVolume (`unbound=True`), and VolumetricCloud (A looks only).
- All lights are Movable.
- Set properties on **components**, per Epic's `…/editor_toolset/skills/default_outdoor_lighting.py`; read it when implementing.
- Post settings: `s = ppv.get_editor_property('settings')` → set the `override_*` flags and values → `ppv.set_editor_property('settings', s)`.
- GI and reflections are set **in the PPV** (`bOverride_DynamicGlobalIlluminationMethod`, `Scene.h:1363`; `bOverride_ReflectionMethod`, `:1287`). That way the editor and `-game` render the same without per-run console variables.

### 7.6 Save and check
- `les.save_current_level()`.
- Write `run/manifest_<look>.json` with a sha256 over the sorted (label, mesh, rounded transform, material) of units and buildings, plus both camera transforms.
- **CHECK** that all three manifests have the same hash.
- Run MCP `LogsToolset.GetLogEntries`: no new Error-level entries.

## 8. Look recipes, preview and tuning (MCP)

| | A (Manor-Lords-style) | A_noLumen | B (Northgard-style) |
|---|---|---|---|
| Sun pitch / yaw | -32 / 60 | same as A | -55 / 60 |
| Sun intensity | 10 lux | 10 lux | 10 lux |
| Sun `light_source_angle` | 2.0 | 2.0 | 4.5 |
| Sun temperature | 5600 K (`use_temperature`) | 5600 K | 6000 K |
| Sun `atmosphere_sun_light` | on | on | on |
| Sun `cast_cloud_shadows` | on | on | n/a |
| SkyAtmosphere | default | default | default |
| SkyLight | `real_time_capture`, intensity 1.0 | same as A | `real_time_capture`, intensity 1.6 |
| VolumetricCloud | default (`m_SimpleVolumetricCloud_Inst`, assigned, not edited) | same | none |
| Fog density / falloff | 0.02 / 0.2, volumetric off | same | 0.004 / 0.2, inscattering tinted (0.45, 0.6, 0.85) (CHECK property name) |
| GI / reflections (PPV) | Lumen / Lumen | ScreenSpace / ScreenSpace | None / ScreenSpace |
| Saturation, contrast | 0.95, 1.05 | same | 1.30, 1.10 |
| Bloom, vignette, AO | 0.3, 0.4, 0.5 | same | 0.15, 0.2, 0.3 |
| Motion blur | 0 | 0 | 0 |
| Exposure | engine histogram auto-exposure (bias 0) | same | same |

Common to every look: VSM shadows, Nanite, TSR, `r.ScreenPercentage 100`. Sun yaw 60 lights from camera-left-behind, so shadows fall up-right in frame.

**Preview loop:**
- For each look, call MCP `SetCameraTransform` to CAM_Gameplay, then `CaptureViewport`; repeat for CAM_Close.
- Claude judges against fixed criteria: units readable against the ground, teams distinguishable, nothing clipped to black or white, the look intent is visible.
- Budget: at most 3 iterations per look.
- Allowed knobs: sun pitch ±10°, `auto_exposure_bias` ±1 EV, fog density ×2 or ÷2, saturation ±0.1.
- Every change goes into `results.json`.
- Send the first A/B preview pair to Alec as a "preview, not final" proactive PNG (SendUserFile, `display:"render"`), so he can redirect early.
- Also use MCP `SearchCVars ScreenPercentage` to record the editor viewport screen-percentage state.

## 9. Editor captures (`lt_capture.run(look)`): close camera and fallback set
For each map and each camera:
1. `les.load_level(map)` → `wait_frames(30)`.
2. `les.pilot_level_actor(cam)` (CHECK the Python overload in the probe; fallback `set_level_viewport_camera_info` + `set_level_viewport_fov`), `editor_set_game_view(True)`, `editor_set_viewport_realtime(True)`.
3. `unreal.SystemLibrary.execute_console_command(world, 'r.HighResScreenshotDelay 64')`. The default run-up is only 4 frames (`ConsoleManager.cpp:4184`, `UnrealClient.cpp:1685-1700`), and a resolution change resets TSR and Lumen history.
4. Then `r.ScreenPercentage 100`.
5. `wait_seconds(20)` so exposure and Lumen settle in the live view.
6. `task = unreal.AutomationLibrary.take_high_res_screenshot(1920, 1080, f'{LT}/out/editor_{look}_{cam}.png', camera=cam, force_game_view=True, delay=1.0)`, then `while not task.is_task_done(): yield`, then `wait_frames(60)`.
7. Assert the PNG exists, `png_size == (1920, 1080)` and it is larger than 300 KB. Then `eject_pilot_level_actor()`.

That gives 6 PNGs. Close the editor afterwards: `python ue_job.py lt_admin quit`. Confirm no `UnrealEditor` process remains.

## 10. Frame rate (standalone `-game`, editor closed)

### 10.1 `run_fps.ps1 -Look A|B|A_noLumen -Rep n [-Warmup]`
```powershell
$UE='D:\Epic Games\UE_5.8\Engine\Binaries\Win64\UnrealEditor.exe'; $LT='D:/Projects/Chimera-Unreal/ProjectChimera/LookTest'
$tag="game_$($Look)_r$Rep"
$a = @('"D:\Projects\Chimera-Unreal\ProjectChimera\ProjectChimera.uproject"', "/Game/LookTest/Maps/LT_$Look",
  '-game','-fullscreen','-ResX=1920','-ResY=1080','-novsync','-nosound','-unattended','-nosplash',
  "-ABSLOG=$LT/logs/$tag.log", '-LogCmds="LogViewport Verbose"',
  '-ExecCmds="t.MaxFPS 0,r.VSync 0,r.ScreenPercentage 100,r.HighResScreenshotDelay 64"',
  '-csvCaptureFrames=4000', "-csvExecCmds=`"3700:HighResShot 1920x1080 filename=$LT/out/$tag.png`"", '-ExitAfterCsvProfiling')
# warm-up: -csvCaptureFrames=1500, no csvExecCmds; results discarded
$smi = Start-Process nvidia-smi -ArgumentList '--query-gpu=timestamp,utilization.gpu,memory.used,clocks.gr,temperature.gpu,power.draw','--format=csv','-lms','1000' -RedirectStandardOutput "$LT/logs/$tag.smi.csv" -PassThru -NoNewWindow
$p = Start-Process $UE -ArgumentList $a -PassThru; $p.WaitForExit(900000) | Out-Null; Stop-Process $smi
# move the newest D:\Projects\Chimera-Unreal\ProjectChimera\Saved\Profiling\CSV\*.csv -> $LT/out/csv/$tag.csv
```
Mechanism, all confirmed in source:
- `-csvCaptureFrames=` starts a boot capture (`CsvProfiler.cpp:5177`).
- `-csvExecCmds` entries are `frame:cmd`, comma-separated, and run only while capturing (`:5204-5230`, `:1005-1016`). Frame indices count from boot.
- `-ExitAfterCsvProfiling` (`:4309`).
- `HighResShot … filename=` syntax: `UnrealClient.cpp:2436`.
- `-ExecCmds` is comma-split (`ParseExecCommands.cpp`).

### 10.2 Order
- Warm-ups: A, B, A_noLumen once each. This fills the game-side shader cache; results are discarded.
- Measured: A, B, A_noLumen, A, B, A_noLumen (2 reps each, interleaved against thermal drift).
- Each run is about 2–3 min. Run each with `run_in_background` and wait for the notification.

### 10.3 `parse_csv.py` (stdlib)
- Read rows until the `EVENTS` header or the first non-numeric row.
- Required columns: `FrameTime`, `GPUTime`, `GameThreadTime`, `RenderThreadTime`; `RHIThreadTime` if present (`LaunchEngineLoop.cpp:1517-1523`; `CsvProfiler.cpp:4388`).
- Window = frames [2000, 3650): after warm-up, before the screenshot.
- Report per run:
  - median fps = 1000/median FrameTime
  - mean fps = N / sum
  - 1% low = 1000/p99 FrameTime
  - median GPU / GT / RT ms, and the bound = largest of these
  - max frame in the window
  - peak VRAM from the smi log
- Per look: mean of the reps. **Flag** the result if:
  - reps differ by more than 5%,
  - the window has fewer than 1,500 frames, or
  - the max frame exceeds 100 ms.
- Resolution CHECK: the game log must contain `Scene viewport resized to 1920x1080` (`SceneViewport.cpp:1977`, Verbose).

### 10.4 Validity CHECKs, from the game PNG of each run
- The view is CAM_Gameplay framing; this proves the view target was set.
- No grey default materials.
- A differs from A_noLumen: mean absolute difference > 2/255, and GPU time differs. This proves the PPV GI override took effect in `-game`.

## 11. Composite, checks, delivery

### 11.1 `composite.py` (Pillow)
- Inputs: `game_A_r2.png` and `game_B_r2.png` (gameplay pair) and `editor_A_CAM_Close.png` and `editor_B_CAM_Close.png` (close pair).
- Assert each input is 1920×1080.
- Output: 3840×1144 per pair. A 64 px label bar on top, using `C:\Windows\Fonts\arial.ttf`.
  - "A · Manor-Lords-style · Lumen GI+refl, VSM, TSR · median N fps (1% low M)"
  - "B · Northgard-style · no GI, SSR, VSM, TSR · …"
  - The second line: "Same models, ground, trees, camera · engine output, unretouched · RTX 3060 1080p, UE 5.8.3 -game (uncooked)"
  - For the close pair, the label says "editor render".
- Save `out/composite_gameplay.png` and `out/composite_close.png`.
- Write `results.json` with the input SHA-256s, recipe values, manifest hashes, the fps table, the A_noLumen numbers, editor memory and the AllToolsets decision.

### 11.2 Fallback rules for the deliverable images
- If any game PNG fails §10.4, use the editor PNG for that look and say so in the label and the message.
- If a look's editor and game PNGs differ markedly in brightness (>10% mean luminance), report it. Do not normalise it.

### 11.3 Optional, only if time allows
- **Clash probe**: import 3 Tripo GLBs (`D:\tripo-out\bulwark_adept.glb`, `pierce_marksman.glb`, `circle_savant.glb`). They are normalised to a 1.0-unit box, so scale each to the Hunyuan counterpart's height. Place them beside their Hunyuan twins in CAM_Close for one extra editor shot, as stand-ins for "player-imported assets".
- **Texel density**: a host script over the GLB positions and UVs computes px/m per model, as a redo-cost number.

### 11.4 Delivery
- Load SendUserFile via ToolSearch. Send `composite_gameplay.png` and `composite_close.png` (`display:"render"`, `status:"proactive"`), plus the A_noLumen PNG as an extra.
- The text is 2–3 lines:
  - the fps for A, A_noLumen and B
  - the one-line caveat (§12)
  - the decision for Alec: A, B or neither. If A, regenerating the 24 models for photoreal is on the table.

### 11.5 Record (checkpoint)
- Copy `LookTest\tools\*` to `D:\Projects\Project_Chimera\tools\unreal-looktest\`.
- Copy the PNGs, composites, `results.json` and the parsed CSV summaries to `docs\unreal-move\look-test-2026-09-30\`.
- Rewrite `HANDOFF.md`, commit with the required trailer lines, and push `master`.

## 12. What the test can and cannot show

**It shows:**
- How UE 5.8 lighting and grading treat **our current 24 Hunyuan models** at Chimera's real default zoom and at a close zoom.
- Whether the two teams stay distinguishable with Godot's exact 0.35 tint.
- Whether baked shading in the 1024² albedos fights a physically lit sun (an asset-redo signal).
- Measured frame rate on this PC for this scene: about 58 units, 10 buildings and about 900 Nanite scatter meshes. Both Lumen and no-Lumen numbers.

**It cannot show:**
- Real Manor Lords quality. There are no Megascans or Fab assets on this machine (no StarterContent pack; only `T_ground_Moss_D`). The ground is Chimera's own 1024² grass and dirt, the trees are PCGBiomeSample stylised meshes, the terrain is flat (Chimera has no heightmap), and there is no grass, animation, VFX or UI.
- The Northgard art style. Northgard's look is mostly hand-painted textures; post-processing cannot repaint photo-ish bakes.
- A 1,000-unit stress test.
- Packaged-game fps. `-game` runs uncooked through the editor binary; packaged is similar or slightly faster.
- Godot's rim-light term, which is omitted.
- Manor Lords' and Northgard's real cameras. Chimera's camera is used for both looks.

**Caveat line for Alec:** "Read A as 'UE5 lighting on our current models and ground', not 'Manor Lords': no photo-scanned ground, grass or buildings were available offline, so Manor-Lords-grade ground is a Fab/asset cost, not an engine limit. Read B as 'stylised lighting on the same models'; true Northgard needs painted textures."

## 13. Fallbacks for risky steps

| Risk | Detect | Fallback |
|---|---|---|
| F1. Bridge does not tick or the queue stalls | No `bridge_ready.json`, or a job never gets a result | Run each job module directly: `-ExecutePythonScript=…/lt_<job>.py`. Its `__main__` drives its own generator from a tick callback, then calls `set_keep_python_script_alive(False)`. One editor launch per job. |
| F2. GLB import (Interchange) | Zero paths, wrong class, or bounds check fails | (a) `unreal.InterchangeManager.get_interchange_manager_scripted().import_asset(dest, unreal.InterchangeManager.create_source_data(file), unreal.ImportAssetParameters(is_automated=True))`. (b) Blender `D:\tools\blender\blender-4.5.10-windows-x64\blender.exe --background --factory-startup --python glb2fbx_blender.py -- in.glb out.fbx` (`bpy.ops.import_scene.gltf`, then `export_scene.fbx(path_mode='COPY', embed_textures=True)`), then import with `FbxFactory`, or with MCP `StaticMeshTools.import_file`. |
| F3. Team-tint parameter missing | `BaseColorFactor` not in the parameter names | A Python-built `M_LT_TeamTint`: albedo × lerp(1, TeamColor, 0.35) × Value, normal, roughness 0.75, two-sided, with MIs pointing at the imported textures. |
| F4. Ground material under Substrate | Base colour not wired, or the preview is black or grey | (a) An MI of the glTF parent with `BaseColorTexture`=grass, using a tiling parameter if `get_*_parameter_names` lists one. (b) `ground_glb.py` writes a 2-triangle GLB (UV 0..240, grass embedded), imported through the proven roster path. |
| F5. Capture with Lumen and post-process | Game PNG has the wrong view, grey materials, or is black | (a) The editor `take_high_res_screenshot` set (§9). (b) SceneCapture2D with `capture_source=SCS_FinalColorLDR`, an RGBA8 render target, `capture_scene()`, `RenderingLibrary.export_render_target`. Labelled "scene capture"; convergence not guaranteed. A black editor capture suggests a locked Windows session: check for a running `LogonUI.exe` and ask Alec to keep Parsec connected or unlocked. |
| F6. Frame rate | CSV missing columns, no file, or flags tripped | Rerun the flagged look. If CSV still fails: run the editor-viewport measurement in the bridge (game view, realtime, 1,500 slate-tick dt samples) labelled "editor lower bound", and record PresentMon if it is installed. |
| F7. AllToolsets weight | Kill rule (§0.2) | EditorToolset only. |
| F8. Port 8000 busy | Preflight | Use `-ModelContextProtocolPort=8765` and re-register. |
| F9. `-unattended` side effects | Plugin disabled in the log, or a viewport that does not render | Relaunch without `-unattended`. |

### Dossier claims dropped or replaced
| Dossier claim | Status in this plan |
|---|---|
| ini section `[/Script/ModelContextProtocolEngine.ModelContextProtocolSettings]` | Not used; the launch flag is used instead |
| UDP remote execution | Not used |
| Commandlet import | Not used; everything runs in the GUI editor |
| Transient `InterchangePipelineStackOverride` | Not used |
| `BasicShapeMaterial` `Color` parameter | Not used |
| `AssetTools.save_assets` for maps | Replaced by `save_current_level` |
| Exposure EV sweep | Replaced by auto-exposure with a ±1 EV tuning knob |
| Manor Lords / Northgard real pitch and FOV | Replaced by Chimera's camera |
| Stencil outline and posterise effects | Not built; optional stretch |

These are kept, each behind a CHECK:
- facing yaw (§6.5)
- ×100 scale and feet pivot (§6.3)
- CaptureViewport while the editor is not the foreground window (§5.3)
- CameraActor view target in `-game` (§10.4)
- the Python spellings (probe)
- Godot FOV 75 vertical (Godot docs)

## 14. Time budget
| Phase | Time |
|---|---|
| Preflight and config | about 15 min |
| First launch with shader compile | 30–120 min |
| Probe and MCP | 15 min |
| Import | 15–30 min |
| Maps | 15 min |
| Preview and tuning | 30 min |
| Captures | 15 min |
| Frame-rate runs | 30 min |
| Composite and delivery | 15 min |

Report once at the end, apart from the early preview PNG.