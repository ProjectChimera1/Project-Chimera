# R3: Unreal trial project, hosting a NativeAOT sim library, and rendering 1,000 sim-driven units

Phase: UNDERSTAND (read-only). Date: 2026-10-01. Engine: UE 5.8.3 (CL 58210709, `D:/Epic Games/UE_5.8/Engine/Build/Build.version`).
Convention: `E` = `D:/Epic Games/UE_5.8/Engine`, `P` = `D:/Projects/Chimera-Unreal/ProjectChimera`,
`R` = `D:/Projects/Project_Chimera`. Every engine claim carries file:line in the installed source. UNVERIFIED marks anything
I could not prove without running the editor, a build, or a NativeAOT publish (none were run).

---

## 0. Short version

1. The trial project is a stock Blank C++ project with ONE module (`ProjectChimera`, empty) and the look-test content; there is no
   game mode, no actor, no subsystem of ours yet. Adding a second module and a native DLL load is a small, well-trodden change.
2. Loading the NativeAOT library: Epic ships an exact template for it (PluginBrowser `ThirdPartyLibrary`): `RuntimeDependencies`
   + `FPlatformProcess::GetDllHandle` in `StartupModule`. For a plain-C-export library we do not need an import `.lib` or
   `PublicDelayLoadDLLs`; `GetDllExport` per function is enough. **Never call `FreeDllHandle`** (Microsoft: unloading a
   NativeAOT library "is not supported").
3. 1,000 moving units: `UInstancedStaticMeshComponent` (ISM, not HISM). In 5.8 a transform update marks only the instance data
   dirty (incremental GPU upload); `bMarkRenderStateDirty` can stay `false`, which is cheaper than the old advice
   (`ISMInstanceDataManager.cpp:820-860`, `ActorComponent.cpp:2687-2690,2722-2728`). Imported static meshes are already Nanite.
4. Team colour: the existing `MI_LTteam_*` instances give one ISM component per (unit type, team) with no new material; per-instance
   custom data (`NumCustomDataFloats` + `UMaterialExpressionPerInstanceCustomData3Vector`) is the later, cheaper-in-draws route.
5. 5.8 DOES have an instanced skinned mesh route (`UInstancedSkinnedMeshComponent` + `UAnimBankData` / `UAnimSequenceTransformProviderData`,
   GPU-driven, with Nanite-skin and non-Nanite proxies). Maturity UNVERIFIED (no experimental marker in the headers; not run).
   The 12 rigged Tripo units are SkeletalMeshes today, so this is the only sane route to animated crowds; check (a) does not need it.
6. Fixed 30 Hz: the sim already owns an accumulator, a 0.25 s clamp, `PrevPosition` and `InterpolationAlpha`
   (`R/godot/src/Core/SimulationLoop.cs:232-268`, `EntityWorld.cs:296-297`). The UE side is one `UTickableWorldSubsystem` that
   calls an exported `Sim_Update(dt)` (play) or `Sim_StepOnce()` (checksum run), then lerps `PrevPosition -> Position` by alpha.
7. Reusing the look-test harness: `-game` + `-csvCaptureFrames` + `-csvExecCmds` + `-ExitAfterCsvProfiling` + `HighResShot` all work as
   in `run_fps.ps1`. `HighResShot` hard-codes "no UI" (`UnrealClient.cpp:1806`); the UI-inclusive shot is the `Screenshot showui`
   console command (`GameViewportClient.cpp:4284-4301`), which is what check (b) needs.
8. Build: `Build.bat ProjectChimeraEditor Win64 Development -Project=... -WaitMutex`; a trivial module change costs 80-115 s
   (about 35 s of that is UBT start-up/makefile). The 16-32 GB page file is in place (`c:\pagefile.sys` 16384-32768 MB, read today).

---

## 1. The trial project as it is on disk

### 1.1 Project descriptor and module code
- `P/ProjectChimera.uproject`: `EngineAssociation` is the GUID `{B44F274D-4E33-1231-4E78-41A8FFF3BD74}` (line 3; resolves through
  `HKCU\Software\Epic Games\Unreal Engine\Builds`, key present; the engine is the Launcher install, `C:\ProgramData\Epic\UnrealEngineLauncher\LauncherInstalled.dat`
  lists `UE_5.8` 5.8.3 at `D:\Epic Games\UE_5.8`). One module: `ProjectChimera`, `Runtime`, `LoadingPhase Default`.
- Plugins enabled: ModelingToolsEditorMode (Editor only), PythonScriptPlugin, EditorScriptingUtilities, ModelContextProtocol /
  ToolsetRegistry / AllToolsets / EditorToolset (all `TargetAllowList: [Editor]`), PCGBiomeSample (all targets). The original
  descriptor was only ModelingToolsEditorMode (`P/ProjectChimera.uproject.bak-20260930`).
- `P/Source/ProjectChimera/ProjectChimera.Build.cs:9-13`: `PCHUsage = UseExplicitOrSharedPCHs`; public deps
  `Core, CoreUObject, Engine, InputCore, EnhancedInput`; private deps empty. No Slate/UMG yet (comment line 15-16).
- `P/Source/ProjectChimera/ProjectChimera.cpp:6`: `IMPLEMENT_PRIMARY_GAME_MODULE(FDefaultGameModuleImpl, ProjectChimera, "ProjectChimera")`
  (a stock module class; there is no `StartupModule` to hang a DLL load on yet). `ProjectChimera.h` only includes `CoreMinimal.h`.
- Targets: `P/Source/ProjectChimera.Target.cs:6-14` (`Type = Game`, `DefaultBuildSettings = V7`,
  `IncludeOrderVersion = Unreal5_8`, `ExtraModuleNames.Add("ProjectChimera")`) and `ProjectChimeraEditor.Target.cs:6-14` (same, `Type = Editor`).
- Built binaries on disk: only the Editor target (`P/Binaries/Win64/UnrealEditor-ProjectChimera.dll`, `ProjectChimeraEditor.target`,
  `UnrealEditor.modules`). No Game-target binary has been built, no cook, no package.

### 1.2 Config (`P/Config/`)
`DefaultEngine.ini` (line numbers are the file's own):
- Maps: `GameDefaultMap=/Engine/Maps/Templates/OpenWorld` (line 4, the World Partition template; the editor launch uses
  `/Engine/Maps/Entry` instead to avoid loading it, `P/LookTest/tools/start_editor.sh:8`). No `GlobalDefaultGameMode`, no
  `EditorStartupMap`: the maps run with the engine default `AGameModeBase` + `ADefaultPawn` (PLAN.md 7.4, PlayerStart parked far out of frame).
- Renderer (lines 7-30): `r.AllowStaticLighting=False`, `r.Shadow.Virtual.Enable=1` (VSM), `r.GenerateMeshDistanceFields=True`,
  `r.DynamicGlobalIlluminationMethod=1` (Lumen), `r.ReflectionMethod=1` (Lumen), `r.SkinCache.CompileShaders=True`,
  **`r.RayTracing=False`** (line 18; was `True` in the `.bak`), `r.RayTracing.RayTracingProxies.ProjectEnabled=True`, `r.Substrate=True`,
  `r.Substrate.ProjectGBufferFormat=0`, auto-exposure/local-exposure defaults. The decision "Lumen off" (HANDOFF 2026-10-01) is NOT
  yet in the ini; Look A's Lumen is set per map in the PostProcessVolume.
- RHI (lines 32-39): `DefaultGraphicsRHI_DX12`, SM6 for D3D12 (`+D3D12TargetedShaderFormats=PCD3D_SM6`), SM5 for D3D11.
- Hardware targeting (lines 71-75): `Desktop`, `Maximum`.
- DPI (lines 80-83): `[/Script/Engine.UserInterfaceSettings] FontDPIPreset=Standard`, `FontDPI=72`;
  `bAuthorizeAutomaticWidgetVariableCreation=False`. No DPI-scale curve or rule override is set (engine default). Relevant to check (b).
- Redirects (lines 85-87): `TP_Blank -> /Script/ProjectChimera`. Lines 89-101 are an auto-added Android file-server section (harmless).
- `DefaultGame.ini`: CommonUI key-handling defaults and the ProjectID only. `DefaultInput.ini`: stock axis-config lines
  (Enhanced Input is a module dependency but unused). `DefaultEditor.ini`: empty template.
- UBT: `P/Saved/UnrealBuildTool/BuildConfiguration.xml` is an empty `<Configuration>`; no UBA overrides.

### 1.3 Content under `P/Content/LookTest` (231 MB; files on disk, editor not running)
- **Maps** (`Content/LookTest/Maps/`): `LT_A.umap`, `LT_A_noLumen.umap`, `LT_B.umap`, `LT_Facing.umap`. Each of A/A_noLumen/B holds 58 units,
  10 buildings, 450 trees, 400 undergrowth, 40 boulders (`P/LookTest/run/manifest_A.json` `counts`), layout seed
  20260930, layout hash `5e7ec198...84f3681b82be9784c0` identical in all three (`results.json`).
- **Ground** (`Ground/`): `T_Grass`, `T_Dirt`; **Materials**: `M_LT_Ground`, `MI_LT_GroundPad`.
- **Roster**: `Content/LookTest/Roster/<asset_key>/...`, 24 folders, one team MI per entry named `MI_LTteam_<asset_key>_<alpha|beta>_s0`
  next to the imported asset folder. Interchange puts the imported assets one level deeper:
  - Static: `Roster/<k>/<k>/StaticMeshes/<k>`, `.../Materials/<mat>`, `.../Textures/<tex>`.
  - Rigged: `Roster/<k>/<k>_rigged/SkeletalMeshes/{<k>, <k>_Skeleton, <k>_PhysicsAsset, <k>preset_biped_<clip>}`, `.../Materials/tripo_mat_<guid>`,
    `.../Textures/<k>_rigged_basecolor`. Clip assets are named `<asset><clip>` with no separator (HANDOFF "known issues"; example
    `pierce_marksmanpreset_biped_fire`).
- **Rigged units (12, SkeletalMesh + shared 41-bone Tripo skeleton, 6-11 clips each)**: acolyte_alchemist (worker), quicksilver_runner (scout),
  bulwark_adept (heavy), pierce_marksman (archer), circle_savant (mage), greycrest_bonded (griffin), cinderhand_thrall (forgehand),
  maul_fused_wretch (footsoldier), slag_bulwark (bulwark), pride_colossus (ironclad), bolt_penitent (crossbowman), cinder_cantor (rune_caster).
- **Static units (4)**: covenant_transmuter (Hunyuan, alpha infantry), crucible_mortar (siege), render_crawler (war machine), envy_wraithwing (wyvern).
- **Buildings (8 meshes, 10 placements)**: covenant_sanctum, crucible_hall, sigil_foundry (x2), transmutation_forge, sanguine_furnace,
  thrall_yards, bolt_sanctum (x2), render_works. (roster list: `P/LookTest/run/roster.json`.)
- Every imported static mesh reports `nanite: True`, `BLEND_OPAQUE`, feet pivot `min_z ~ 0`, height ratio 1.000 vs the source
  (`P/LookTest/run/roster_report.json`, entries `covenant_transmuter`, `crucible_mortar`). Parent material is the Interchange glTF Substrate
  `/InterchangeAssets/gltf/Substrate/M_GLTF` (`lt_common.py:41`); every material exposes a vector parameter `BaseColorFactor`, which is how the team MIs tint.
- Team tint maths (`lt_common.py:171-188`): `factor = (1 - 0.35 + 0.35*srgb_to_linear(team)) * value`; alpha (0.2,0.5,1.0) gives
  (0.662,0.725,1.000); beta (1.0,0.3,0.2) gives (1.000,0.676,0.662).
- Per-source facing correction (`P/LookTest/run/facing.json`): front yaw `tripo_rigged -90`, `godot_hunyuan -90`, `tripo_static 0`.
  The renderer must apply this on top of the sim heading.

### 1.4 Look A lighting and post (recipe `lt_common.py:427-446`, applied by `lt_common.py:635-680`)
- DirectionalLight: pitch -32, **yaw 120** (PLAN said 60; changed because camera yaw 90 maps world -X to screen right,
  `lt_common.py:422-425`), 10 lux, `light_source_angle` 2.0, 5600 K, atmosphere sun light on, cloud shadows on.
- SkyAtmosphere default; SkyLight real-time capture, intensity 1.0; VolumetricCloud with `/Engine/EngineSky/VolumetricClouds/m_SimpleVolumetricCloud_Inst`
  (assigned, not edited); ExponentialHeightFog density 0.02, falloff 0.2, volumetric off.
- PostProcessVolume (unbound): GI Lumen, reflections Lumen (A_noLumen: both ScreenSpace; B: GI None), saturation 0.95, contrast 1.05,
  bloom 0.3, vignette 0.4, AO 0.5, motion blur 0, auto-exposure bias 0.
- Actor labels: `LT_Sun, LT_SkyAtmosphere, LT_SkyLight, LT_HeightFog, LT_VolumetricCloud, LT_PostProcess` (`lt_common.py:605`).
- Cameras: `CAM_Gameplay` pivot (0,0,0), 80 m, pitch 50, vertical FOV 75 (UE horizontal 107.5), location (0,-5142,6128) cm, rotation (-50,90,0);
  `CAM_Close` pivot (-2,2,0), 30 m, pitch 40, vertical FOV 40. `CAM_Gameplay` auto-activates for Player0 (`lt_common.py:412-420`).
  HANDOFF says the default RTS camera comes in closer after the trial.
- Baseline cost to beat/compose with (`R/docs/unreal-move/look-test-2026-10-01/README.md`): A 62.9 fps median, GPU 15.4 ms, render thread 15.9 ms,
  game thread 4.4 ms; A_noLumen 76.2 fps; B 107.9 fps; VRAM 3.9 GB; all with ~58 units, 10 buildings and ~900 Nanite scatter meshes.
  So at 63 fps the GPU is already the binding side before 1,000 units arrive.

### 1.5 Tooling in `P/LookTest/tools` (mirrored in `R/tools/unreal-looktest`)
| File | Role |
|---|---|
| `start_editor.sh` | Launches `UnrealEditor.exe <uproject> /Engine/Maps/Entry -ModelContextProtocolStartServer -ExecutePythonScript=ue_bridge.py -unattended -nosplash -nosound -stdout -FullStdOutLogOutput`; needs `MSYS_NO_PATHCONV=1` (line 7). |
| `ue_bridge.py` | Editor-side job loop: `set_keep_python_script_alive(True)`, a Slate post-tick callback claims `queue/*.job.json`, reloads the module, runs `func(**args)`, generators advance one step per tick, result in `.result.json` (lines 32-155). Turns off `bThrottleCPUWhenNotForeground` (CDO, C++ name only). |
| `ue_job.py` | Host side: writes a job, waits for the result file, exit code 0/1. |
| `lt_common.py` | Roster loader, tint maths, seeded layout/scatter, cameras, recipes, sky/post, helpers (712 lines). |
| `lt_import.py` | Imports staged GLBs via `AssetImportTask` to `/Game/LookTest/Roster/<asset_key>`, validates height/pivot/blend mode/`BaseColorFactor`, writes `run/roster_report.json`. |
| `lt_build.py` | Builds a look map: ground, 58 units (static or SkeletalMeshActor with a single-node clip, seeded phase), 10 buildings, scatter, sky, post, cameras; writes `manifest_<look>.json`. |
| `lt_capture.py` | Editor captures via `AutomationLibrary.take_high_res_screenshot` (1920x1080, >300 KB check). |
| `prep_roster.py` | Host prep: strips `KHR_materials_volume` and orphan skins, computes scale from Godot height; writes `run/roster.json` (391 lines). |
| `run_fps.ps1` | One standalone `-game` run with CSV profiling, HighResShot, nvidia-smi log (section 3). |
| `parse_csv.py` | Window [2000, 3650) stats: median/mean fps, 1% low, GPU/GT/RT/RHI medians, bound, max frame, VRAM from the smi log; flags >5% rep spread, <1500 frames, >100 ms spike. |
| `mcp_call.py` | Direct JSON-RPC client for the editor's MCP server at `http://127.0.0.1:8000/mcp`. |
| `lt_facing.py`, `lt_probe.py`, `lt_dbg.py`, `lt_admin.py`, `composite.py`, `grid.py`, `preview.sh` | Facing calibration, API-spelling probe, debug, `quit`/`ping`, composite sheets. |

### 1.6 Gotchas to inherit (`P/LookTest/PLAN_DELTA.md` D7-D9, HANDOFF "In flight")
- D7 (lines 89-96): a PowerShell `Start-Process` GUI launch with `-ABSLOG` hung for 5 min with no window or log; launch from Bash with
  `-stdout -FullStdOutLogOutput` or via `run_fps.ps1`. Git Bash rewrites leading-slash args (`/Game/...`), so set `MSYS_NO_PATHCONV=1`.
- D8 (lines 98-113): MCP `initialize` works, serverInfo name is empty, `CaptureViewport` needs `captureTransform` and all six `annotations`
  fields, the image comes back as base64 inside a TEXT block, viewport-size capture (1014x550). The `unreal-mcp` server is currently
  ECONNREFUSED in this session because the editor is not running (expected).
- D9 (lines 115-121): building size rule (longest side 16 m for command centres, 12 m for other buildings); units keep Godot height.
- HANDOFF: MaterialEditingLibrary setters return `False` on success; skeletal meshes need `AlwaysTickPoseAndRefreshBones` +
  `set_update_animation_in_editor` or off-screen units T-pose; `-game` CSVs land in `%LOCALAPPDATA%\UnrealEngine\5.8\Saved\Profiling\CSV`;
  Godot does not reimport changed `.glb` (unrelated); editor idles at 4.2 GB.

---

## 2. How the look test ran (so the sim check can reuse it)

### 2.1 Standalone `-game` run (`P/LookTest/tools/run_fps.ps1`)
- Binary: `E/Binaries/Win64/UnrealEditor.exe` with `-game` (the Editor target in game mode; no cook, "uncooked -game", `results.json` `pc`/`engine` fields).
- Command line (lines 63-72): `"<uproject>" /Game/LookTest/Maps/LT_<Look> -game -fullscreen -ResX=1920 -ResY=1080 -novsync -nosound -unattended -nosplash
  -ABSLOG=<LT>/logs/<tag>.log -LogCmds="LogViewport Verbose" -ExecCmds="t.MaxFPS 0,r.VSync 0,r.ScreenPercentage 100,r.HighResScreenshotDelay 64"
  -csvCaptureFrames=4000 -csvCompression=0 -ExitAfterCsvProfiling -csvExecCmds="3700:HighResShot 1920x1080 filename=<LT>/out/<tag>.png"`.
  Warm-up run: 1500 frames, no screenshot (line 64-72).
- Preconditions (lines 53-57): the editor must not be running; map file exists; paths without spaces (forward slashes inside UE args).
- CSV: the profiler writes to the per-user dir `%LOCALAPPDATA%\UnrealEngine\5.8\Saved\Profiling\CSV` (installed-engine -game; line 25-27); the
  script finds the newest `.csv` created since launch and moves it to `out\csv\<tag>.csv`.
- Validity checks (lines 96-136): exit code must be 0 (`-ExitAfterCsvProfiling` is a clean exit), CSV >1 KB, log exists, last
  `Scene viewport resized to WxH` line must be 1920x1080, PNG exists. A `nvidia-smi` sampler runs at 1 Hz beside it.
- Engine source for the flags: `-csvCaptureFrames`/`-csvExecCmds`/`-ExitAfterCsvProfiling` in `E/Source/Runtime/Core/Private/ProfilingDebugging/CsvProfiler.cpp`
  (PLAN.md 10.1 cites `:5177`, `:5204-5230`, `:4309`); `-ExecCmds` is comma-split.
- Custom CSV columns for the sim: `CSV_DEFINE_CATEGORY`, `CSV_SCOPED_TIMING_STAT`, `CSV_CUSTOM_STAT`
  (`E/Source/Runtime/Core/Public/ProfilingDebugging/CsvProfiler.h:50,120,155`), compiled out in Shipping (`CsvProfilerConfig.h:10,31`).
  The ISM itself already emits `UInstancedStaticMeshComponent_BatchUpdateInstancesTransforms` (`InstancedStaticMesh.cpp:4471`).

### 2.2 Screenshots: `HighResShot` vs "with UI"
- `HighResShot <WxH | multiplier> filename=<path>` (parser `UnrealClient.cpp:2436-2502`). When `GIsHighResScreenshot` is set, the viewport
  requests the screenshot with **`bShowUI = false`** (`UnrealClient.cpp:1806-1811`), and screen messages are disabled for no-UI requests
  (`UnrealClient.cpp:261-264`). So `HighResShot` never contains Slate/UMG.
- `Screenshot [showui] [filename=...] [nosuffix]` (`GameViewportClient.cpp:4284-4301`) goes through `FScreenshotRequest::RequestScreenshot(FileName, bShowUI, ...)`;
  with `showui` the capture path takes the Slate window (`UnrealClient.cpp:2378-2394`; a view rect is not supported with UI). That is the command
  for HUD matching in check (b); it can be injected the same way as the look test: `-csvExecCmds="3700:Screenshot showui filename=<path> nosuffix"`
  (command syntax read from source; the full command line is UNVERIFIED until run).
- `r.HighResScreenshotDelay 64` was needed because the default run-up is 4 frames and a resolution change resets TSR/Lumen history (PLAN.md 9.3).
- Editor-side captures used `unreal.AutomationLibrary.take_high_res_screenshot(1920,1080,path,camera=cam,force_game_view=True,delay=1.0)`
  (`lt_capture.py:41-43`).

### 2.3 What to change for a sim run
- `run_fps.ps1`'s `-Look` is a `ValidateSet('A','A_noLumen','B')` (line 15) and the frame math assumes a 4000-frame capture; a sim variant
  needs a map parameter and a way to start the sim. Cleanest: the sim subsystem (section 6) auto-starts when it sees
  `-ChimeraSimUnits=1000` on the command line, so no map edit and no console-command timing problem. `-ExecCmds` is the fallback.
- Frame window [2000, 3650) in `parse_csv.py` assumes the measured scene is steady. A running sim (units fighting) is not steady; for the
  fps number prefer a scripted fixed-length sim (no deaths, constant unit count) or record per-tick unit count in a CSV stat.
- Keep the same machine rules: close the editor, ComfyUI off, Parsec noted, 1920x1080 fullscreen.

---

## 3. Hosting a NativeAOT DLL from a UE runtime module (5.8 APIs, confirmed)

### 3.1 Build-rule side
- `ModuleRules.RuntimeDependencies` (`E/Source/Programs/UnrealBuildTool/Configuration/Rules/ModuleRules.cs:1424`) stages files with the target.
  A dependency's path "should use `$(EngineDir)` and `$(ProjectDir)` variables as a root" (`ModuleRules.cs:312-314`). Overload with a
  source path (copied to `Path` at build time, ready to stage): `Add(string inPath, string inSourcePath, StagedFileType = NonUFS)` (`:408`).
- `PublicDelayLoadDLLs` (`ModuleRules.cs:1378`) is only needed when you link an import `.lib` and want to load the DLL yourself first. For a
  library consumed only through `GetDllExport` it is unnecessary.
- `PublicRuntimeLibraryPaths` / `PrivateRuntimeLibraryPaths` (`:1311`, `:1307`) are for `.so` search paths (non-Windows).
- Engine precedent for a copy-then-stage DLL: `E/Source/Editor/NNEEditor/NNEEditor.Build.cs:41,52,65`
  (`RuntimeDependencies.Add("$(TargetOutputDir)/" + name, Path.Combine(src, name))`); `E/Source/ThirdParty/Intel/OIDN/IntelOIDN.Build.cs:21-23`.
- Epic's own template, the thing to copy: `E/Plugins/Editor/PluginBrowser/Templates/ThirdPartyLibrary/`
  - `Source/ThirdParty/PLUGIN_NAMELibrary/PLUGIN_NAMELibrary.Build.cs:10` (`Type = ModuleType.External`), `:16` (`PublicAdditionalLibraries` import lib),
    `:19` (`PublicDelayLoadDLLs.Add("ExampleLibrary.dll")`), `:22` (`RuntimeDependencies.Add("$(PluginDir)/Binaries/ThirdParty/.../Win64/ExampleLibrary.dll")`).
  - `Source/PLUGIN_NAME/Private/PLUGIN_NAME.cpp:18-30` builds the absolute path (`IPluginManager::Get().FindPlugin(...)->GetBaseDir()` + relative path)
    and calls `FPlatformProcess::GetDllHandle`; `:49` frees it in `ShutdownModule`.
  - For a game module use `FPaths::ProjectDir()` (or `IPluginManager`) with `$(ProjectDir)/Binaries/ThirdParty/ChimeraSim/Win64/ChimeraSim.dll`.

### 3.2 Runtime side
- `FPlatformProcess::GetDllHandle(const TCHAR*)`, `GetDllExport(void*, const TCHAR*)`, `FreeDllHandle(void*)`
  (`E/Source/Runtime/Core/Public/GenericPlatform/GenericPlatformProcess.h:305-311`; Windows: `Core/Public/Windows/WindowsPlatformProcess.h:134-136`).
- Windows implementation (`Core/Private/Windows/WindowsPlatformProcess.cpp`): `GetDllHandle` (line 234) builds a search list of
  the modules directory, the pushed DLL directory and `AddDllDirectory` entries (`:242-252`), suppresses the OS error dialog (`:254-266`) and calls
  `LoadLibraryWithSearchPaths` (`:272`, defined `:2491`). `GetDllExport` is `GetProcAddress` with `TCHAR_TO_ANSI` (`:315-320`; export names are ANSI).
  `FreeDllHandle` is `FreeLibrary` (`:282-286`). `AddDllDirectory` (`:205`) / `PushDllDirectory` (`:322`) exist for dependent DLLs.
  A full absolute path to the library works and is the recommended form.
- **Do not unload.** Microsoft, "Building native libraries" (`learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/libraries`, fetched 2026-10-01):
  "Unloading Native AOT libraries (via `dlclose` or `FreeLibrary`, for example) is not supported." Only shared libraries are supported;
  exports are `[UnmanagedCallersOnly(EntryPoint = ...)]` methods. Consequences for the host:
  load once, keep the handle for the process lifetime, skip `FreeDllHandle`; reset sim state through exported `Sim_Destroy`/`Sim_Create`
  (never reload the DLL); rebuilding the DLL requires restarting the editor (as the spec already says).
- Staging for the three run modes:
  - `UnrealEditor.exe -game` and the editor: nothing is staged; the path under `<ProjectDir>/Binaries/ThirdParty/ChimeraSim/Win64/` is read in place.
  - Packaged: the `RuntimeDependencies` entry stages the file at the same project-relative path (NonUFS), so the same `FPaths::ProjectDir()`-based path
    works. UNVERIFIED: no package has been built from this project (Game-target engine binaries exist, e.g. `E/Binaries/Win64/UnrealGame-Win64-Shipping.exe`,
    165 MB, and `E/Intermediate/Build/Win64/x64/UnrealGame/{DebugGame,Development,Shipping}`).
- NativeAOT toolchain on this machine (for the next phase): `C:\Users\MD_Ki\.nuget\packages\microsoft.dotnet.ilcompiler\8.0.25` and
  `runtime.win-x64.microsoft.dotnet.ilcompiler\8.0.25` are already restored, so an SDK 8.0.419 `PublishAot` of a library should not need the network;
  MSVC is present (UBT log: `C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Tools\MSVC\14.50.35717`, Windows SDK 10.0.28000.0,
  `C:\Users\MD_Ki\AppData\Local\UnrealBuildTool\Log-backup-2026.09.29-14.45.53.txt`). Not run. Whether the publish also emits an import `.lib`: UNVERIFIED (not needed with `GetDllExport`).
- NativeAOT runtime start-up timing (at load vs first export call) and `DllMain` interplay with the UE loader lock: UNVERIFIED; the host should call a
  trivial `Sim_Version()` export right after `GetDllHandle` and log the result, so a bad start-up shows in the first log line.

### 3.3 Boundary shape that fits UE (recommendation, facts from the sim)
- The sim is Q16.16 fixed point (`R/godot/src/Core/FixedPoint.cs:12-14`, `FixedVec3` X/Y/Z of `Fixed`, lines 191-203). Export raw int32 triples; the host converts
  (`raw / 65536.0`) and applies the Godot-to-UE axis/handedness mapping and 100x (m to cm) itself.
- Positions live in `EntityWorld.Position` / `PrevPosition` (`EntityWorld.cs:296-297`, managed arrays). Prefer **copy-out into a host-owned buffer**
  (`Sim_ReadUnits(UnitRecord* out, int capacity)`; 1,000 x ~32 B = 32 KB per tick) over exporting raw array pointers: managed arrays move unless pinned,
  and NativeAOT's GC is separate from UE's allocator.
- All calls from the game thread only; no callbacks into UE (the sim must stay "engine-free"; spec non-negotiables: sim knows nothing about Unreal,
  one way in through the command stream, looks cannot touch rules).
- Checksum export: `uint32 Sim_LastChecksum()` + `uint32 Sim_CurrentTick()` (`SimulationHost.cs:200-201`; the loop folds a checksum every
  `ChecksumInterval = 60` ticks and fires `OnChecksum`, `SimulationLoop.cs:34,257-263`). A per-N-ticks log line `tick=<n> checksum=0x<8hex>` in the UE log is
  the comparison artifact against the Godot/test run.

---

## 4. Rendering 1,000 sim-driven units with instanced static meshes (5.8, confirmed)

### 4.1 Class choice: ISM, not HISM, for moving units
- `UInstancedStaticMeshComponent` (ISM) header: `E/Source/Runtime/Engine/Classes/Components/InstancedStaticMeshComponent.h`.
  `HierarchicalInstancedStaticMeshComponent` exists (`Classes/Components/HierarchicalInstancedStaticMeshComponent.h:134`) but is built around a cluster tree
  (`BuildTree`, `:222,357`; `bAutoRebuildTreeOnInstanceChanges`, `:198`); that is for static foliage, not per-frame movers. Nanite meshes
  get GPU culling anyway: the ISM creates a `Nanite::FSceneProxy` when `bCreateNanite` (`InstancedStaticMesh.cpp:2588-2605`).
- All 22 imported static meshes are Nanite (`roster_report.json`), so ISM + Nanite is the default path with no extra setting.

### 4.2 Transform updates
- Signatures (`InstancedStaticMeshComponent.h`):
  - `UpdateInstanceTransform(int32, const FTransform&, bool bWorldSpace=false, bool bMarkRenderStateDirty=false, bool bTeleport=false)` line 375.
  - `BatchUpdateInstancesTransforms(int32 Start, TArrayView<const FTransform>, bWorldSpace, bMarkRenderStateDirty, bTeleport)` line 391 (TArray overloads 388, 397).
  - `BatchUpdateInstancesTransform(Start, Num, const FTransform&, ...)` line 411 (same transform to a range; not useful for movers).
  - `BatchUpdateInstancesData(Start, Num, FInstancedStaticMeshInstanceData*, bMarkRenderStateDirty, bTeleport)` line 413: writes raw matrices
    (lowest-overhead path if the host builds `FInstancedStaticMeshInstanceData` itself; implementation `InstancedStaticMesh.cpp:4617`).
  - `AddInstance` 271, `AddInstances(TArray<FTransform>, bShouldReturnIndices, bWorldSpace, bUpdateNavigation=true)` 275, `PreAllocateInstancesMemory(int32)` 334,
    `ClearInstances` 431, `RemoveInstances` 421/427; ID-based variants `AddInstancesById` 294, `RemoveInstancesById` 304, `UpdateInstanceTransformById` 308.
- **5.8 flush behaviour (important):** each of these calls `PrimitiveInstanceDataManager.TransformChanged(i)` (`InstancedStaticMesh.cpp:4337` single, `:4514` batch, `:4653` raw-data batch; `ISMInstanceDataManager.cpp:274-277`), which tracks the changed index and calls `MarkComponentRenderInstancesDirty()`
  (`ISMInstanceDataManager.cpp:820-860`) -> `UActorComponent::MarkRenderInstancesDirty()` (`ActorComponent.cpp:2722-2728`). At end of frame,
  `SendRenderInstanceData_Concurrent()` runs (`ActorComponent.cpp:2687-2690`) and, when a proxy exists, does an incremental
  `PrimitiveInstanceDataManager.FlushChanges(...)`, then `UpdateBounds()` and `Scene->UpdatePrimitiveInstances(this)` (`InstancedStaticMesh.cpp:2544-2566`).
  So `bMarkRenderStateDirty` should stay `false`: `MarkRenderStateDirty()` sets `bRenderStateDirty` and forces a full end-of-frame
  render-state recreate (`ActorComponent.cpp:2695-2709`). The header comment "If true, the change should be visible immediately" (`:370`, `:383`) is stale for 5.8.
  (Inference from reading; the cost difference is UNVERIFIED until profiled with `UInstancedStaticMeshComponent_BatchUpdateInstancesTransforms`.)
- Per-call overhead to switch off (read at `InstancedStaticMesh.cpp:4469-4544`): `Modify()` (transaction record, editor-only effect), `InvalidateCachedBounds()`,
  navigation updates when `bNavigationRelevant && IsRegistered()` (partial nav update appends two transforms per instance, `:4482-4486,4528`), and physics body
  moves when `bPhysicsStateCreated` (`:4516`). For pure visuals: `SetCollisionEnabled(NoCollision)` / no body, `SetCanEverAffectNavigation(false)`.
  Pass `bWorldSpace=false` with the component at identity: the world-space path computes `GetRelativeTransform` per instance (Epic's own TODO at `:4508`; same TODO at `:4331`).
- Render interpolation: `FTransform` per instance from `lerp(PrevPosition, Position, alpha)`; for motion blur/TSR history there is an overload with previous
  transforms (`h:397`, needs `SetHasPerInstancePrevTransforms`, `h:349-360`). Skip for the trial unless ghosting appears (UNVERIFIED).

### 4.3 Team colour
- Per-instance custom data: `NumCustomDataFloats` (UPROPERTY, `h:181`), `SetNumCustomDataFloats(n)` (`h:327`; **clears all existing data to 0**, `InstancedStaticMesh.cpp:3987-3997`),
  `SetCustomData(i, TArrayView<const float>, bDirty)` (`h:330`, impl `:3922-3945`), range version `h:331` (impl `:3947-3985`), `SetCustomDataValue` (`h:323`, `:3902`).
  Each flags `CustomDataChanged(i)` so only changed instances upload.
- The material must read it: `UMaterialExpressionPerInstanceCustomData3Vector` (`E/Source/Runtime/Engine/Public/Materials/MaterialExpressionPerInstanceCustomData.h:40-60`, `DataIndex`,
  `ConstDefaultValue`). The imported Interchange materials do NOT have it; they expose `BaseColorFactor` only. A custom-data material is a new binary
  `.uasset` (art-class asset; can be authored by Python like `M_LT_Ground`, `PLAN.md` 7.1).
- Zero-material-work alternative for the trial: one ISM component per (unit type, team), assigned the existing `MI_LTteam_<k>_<faction>_s0`
  (`UStaticMeshComponent::SetMaterial(0, MI)`). With 2 factions and about 16 types that is at most ~32 components (draw groups); with 8 teams x 16 types, 128.
  The spec's "one draw per unit type" goal (Units row, spec "How it is built") then becomes "per type per team"; fine for the trial, revisit with custom data.
- Static-mesh unit assets for the check: crucible_mortar (`Roster/crucible_mortar/crucible_mortar/StaticMeshes/crucible_mortar`, 86 cm), covenant_transmuter
  (203 cm), render_crawler, envy_wraithwing. The 12 rigged units are SkeletalMeshes (next section).
- Per-source scale/facing from `roster.json` (`scale`) and `facing.json` (`front_yaw`) apply to instance transforms.

### 4.4 Animated crowds: 5.8 has an instanced skinned mesh route
- Present in 5.8 engine source:
  - `UInstancedSkinnedMeshComponent` (`E/Source/Runtime/Engine/Classes/Components/InstancedSkinnedMeshComponent.h:57-60`, derives `USkinnedMeshComponent`),
    instance data `FSkinnedMeshInstanceData { FTransform3f Transform; uint32 AnimationIndex; }` (`:28-52`), `NumCustomDataFloats` (`:80`), `TransformProvider` (`:68`).
  - API (by `FPrimitiveInstanceId`): `AddInstance(transform, animIndex)` / `AddInstances(...)` (`:208-214` region), `SetInstanceTransform`, `SetInstanceAnimationIndex`,
    `UpdateInstance(id, transform, animIndex)`, `RemoveInstance(s)`, `ClearInstances`, `SetNumCustomDataFloats`, `SetCustomData(id, TConstArrayView<float>)`,
    `SetHasPerInstancePrevTransforms`, bone attachment for instances (`SetInstanceBoneAttachment`), `OptimizeInstanceData`, GPU-only instance mode (`SetNumGPUInstances`).
  - Animation source: `UAnimBankData : UTransformProviderData` with `AnimBankItems` (`E/Source/Runtime/Engine/Classes/Animation/AnimBank.h:773-800`,
    `IsGpuOnly() == true`), assets `UAnimBank` (sequences baked for a `USkinnedAsset`, async DDC build, `AnimBank.h:176-330`) and a more general
    `UAnimSequenceTransformProviderData` with manual track position, loop modes, layers and blend spaces (`Classes/Animation/AnimSequenceTransformProviderData.h:33-46,130-148,871`).
    `AnimationIndex` selects the clip per instance (`AnimSequenceTransformProviderData.cpp:428-431`).
  - Proxies: Nanite-skinned `FNaniteInstancedSkinnedMeshSceneProxy` when `ShouldNaniteSkin()`, otherwise `FInstancedSkinnedMeshSceneProxy`
    (`Private/InstancedSkinnedMeshSceneProxyDesc.cpp:27-63`). CVars exist to force ref pose and animation bounds (`InstancedSkinnedMeshComponent.cpp:50-61`).
- Status: no "Experimental"/"Beta" marker in the headers I read; I did not find user-facing documentation in the install
  (`E/Documentation` has only third-party notices). Treat as **present but unproven here**: UNVERIFIED that the Tripo 41-bone rigs bake, that clip phase can be set
  per instance from the sim (a "manual" track exists, but whether the instanced skinned component drives it per instance is not confirmed), that Nanite skinning
  works with the glTF Substrate material, and what it costs on the 3060.
- Contrast: today's look test uses one `SkeletalMeshActor` per rigged unit (46 animated actors in `live_anim_in_editor`, `manifest_A.json`). 1,000 of those would be
  1,000 skeletal components ticking pose on the game thread; do not build the 1,000-unit check that way.
- Recommendation for check (a): static ISMs first (checksum parity is independent of animation). Treat instanced skinned meshes as a labelled stretch, driven by a
  sim "animation state" field (idle/walk/attack) mapped to `AnimationIndex`. Static-mesh stand-ins are fine as the animation-free baseline.

### 4.5 Rough budget (to size the check, not measured)
- 1,000 instances x 1 `FTransform` build + 48-byte matrix store per frame is trivial on the game thread; the look test's game thread is 4.4 ms at 58 units + 900 meshes
  (so it is mostly engine overhead, not our units). The GPU/render thread (15.4/15.9 ms) is the risk; Nanite instances of 5-8k-triangle meshes at the RTS camera are small on screen
  (5-10 px tall at the current camera, README "What it showed"). UNVERIFIED until measured: run the same `LT_A` scene plus 1,000 instances and read `GPUTime`/`RenderThreadTime`.

---

## 5. Cleanest fixed-30 Hz host inside `-game`, with render interpolation

### 5.1 Facts
- UE has no general fixed-step gameplay tick (fixed time step is a global debug mode: `-UseFixedTimeStep`, `-Deterministic`, `-FPS=`, `-benchmark`;
  `E/Source/Runtime/Launch/Private/LaunchEngineLoop.cpp:2457-2460,4727-4736`; `FApp::UseFixedTimeStep` `Core/Public/Misc/App.h:557`). Do not rely on it for the sim; it also changes
  the whole engine's time step.
- Host base class: `UTickableWorldSubsystem` (`E/Source/Runtime/Engine/Public/Subsystems/WorldSubsystem.h:80-95`; must forward `Initialize`/`Deinitialize`, defines `Tick`, `GetStatId`).
  Lives and dies with the `UWorld`, is created automatically in game worlds (override `ShouldCreateSubsystem` to limit to game worlds / when the DLL loaded), needs no map edit and no Blueprint.
  A `UGameInstanceSubsystem` is the alternative if the sim must survive level travel.
- The sim already implements the loop the host would otherwise write: `SimulationLoop.Update(float realDelta)` clamps to 0.25 s ("spiral of death"), accumulates, snapshots
  positions (`World.SnapshotPositions()`), runs the systems per tick, folds checksums, and sets `InterpolationAlpha = accumulator / (1/30)`
  (`R/godot/src/Core/SimulationLoop.cs:232-268`); `SimulationHost.Update(float)` / `StepOnce()` wrap it (`SimulationHost.cs:598-601`), `InterpolationAlpha` at `:202`.
  The float accumulator is wall-clock pacing only; it is never folded into the checksum.

### 5.2 Recommended loop
```
UChimeraSimSubsystem : UTickableWorldSubsystem
  Tick(DeltaTime):                       // game thread, once per frame
     if (Mode == Play)   ticks = Sim_Update(DeltaTime)        // library owns accumulator + clamp
     if (Mode == Scripted) { for i in 0..N: Sim_StepOnce(); }  // deterministic checksum run, frame-rate independent
     Sim_ReadUnits(buf, cap) -> prev/curr pairs, alpha = Sim_InterpolationAlpha()
     for each unit: pos = lerp(prev, curr, alpha); ISM.BatchUpdateInstancesTransforms(...)  // bMarkRenderStateDirty=false
```
- Tick group: default (`TG_PrePhysics`-equivalent for subsystems) is fine; the renderer reads instance data at end of frame.
- `Update` fed with `DeltaTime` from the subsystem is world-time (pause/dilation applied); pass real seconds (`FApp::GetDeltaTime()`) for a sim that must not slow with world dilation.
- Online/lockstep later: `LockstepPacer` (DW-912, listed in `R/godot/SimSources.props`) is the sim-side pacer; the host only calls it. Out of scope for the trial.
- For checksum check (a) use Scripted mode with a fixed seed, a scripted command stream, and N ticks: the log should print the same `tick/checksum` pairs as the Godot/test harness
  because UE time never enters the sim. Frame pacing may differ between machines/runs without changing the pairs.

---

## 6. Build facts

### 6.1 Command and tooling
- Editor-target build used for the trial: `"D:\Epic Games\UE_5.8\Engine\Build\BatchFiles\Build.bat" ProjectChimeraEditor Win64 Development -Project="D:\Projects\Chimera-Unreal\ProjectChimera\ProjectChimera.uproject" -WaitMutex`
  (UBT log command line, `C:\Users\MD_Ki\AppData\Local\UnrealBuildTool\Log.txt:31`; same text in `P/LookTest/PLAN.md:147`).
  Run it with the editor closed (the module DLL is locked otherwise; Live Coding not used).
- Toolchain (Log.txt): MSVC 14.50.35717 from `C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools`, Windows SDK 10.0.28000.0 (also 10.0.26100.0 present), ISPC 1.24.0.
  `P/.vsconfig` lists the VS components (Unreal debugger/IDE, VC tools, Windows 11 SDK 22621, NativeGame workload).
- **UBA** (Unreal Build Accelerator) runs in local executor mode: `UbaServer - Listening on 0.0.0.0:1345`, "Using Unreal Build Accelerator local executor", storage 40 GB at
  `C:\ProgramData\Epic\UnrealBuildAccelerator`. Action parallelism is memory-capped: "Requested 1.5 GB memory per action, 7.96 GB available: limiting max parallel actions to 5" (Log.txt:35-37 region).
- **Page-file fix**: the first build log (2026-09-29 14:45) says "2.71 GB available: limiting max parallel actions to 1"
  (`Log-backup-2026.09.29-14.45.53.txt:38`). After raising the page file, the log shows 8.76 GB available and 5 parallel actions. Current setting read today:
  `c:\pagefile.sys` initial 16384 MB, maximum 32768 MB (`Win32_PageFileSetting`). `NoUBA` was not needed in the logs I read (`[NoUba]` there is only the `WriteMetadata` action).
- Compile times (UBT logs): first full project compile 79.7 s of UBA time for 7 actions (SharedPCH.UnrealEd, PerModuleInline, ProjectChimera.cpp, two links; total `Log-backup-...14.45.53` run
  limited to 1 parallel action by the memory gate); plugin-list relink 116.0 s total, 79.6 s in the action phase (`Log-backup-2026.09.29-14.45.54.txt:112`); latest 95.45 s total
  (`Log.txt`, 2026-09-30 23:14). About 35 s is UBT start-up (platform registration, rules, makefile). "Makefile is older than .uproject file, ignoring it" appears whenever the
  `.uproject` changes (so adding a module costs one makefile rebuild). Compile cost of a real C++ module with more than a few files has not been measured here: UNVERIFIED.
- Engine plugins are prebuilt (`E/Build/InstalledBuild.txt`, content "UE_5.8"; `PLAN_CHECKS.md` item 6), so adding project modules never recompiles the engine.

### 6.2 Adding a NEW C++ module to the existing project (Launcher engine)
1. `Source/ChimeraSimHost/ChimeraSimHost.Build.cs` (a `ModuleRules` class; `PCHUsage = UseExplicitOrSharedPCHs`; deps `Core, CoreUObject, Engine` + `Projects` only if using `IPluginManager`).
2. `Source/ChimeraSimHost/ChimeraSimHost.cpp` with `IMPLEMENT_MODULE(FChimeraSimHostModule, ChimeraSimHost)` and a module class that loads the DLL (3.2).
3. Add the module to `ProjectChimera.uproject` `Modules` (`Type: Runtime`, `LoadingPhase: Default`; `PostConfigInit` only if the sim must exist before the engine loop - not needed).
4. Add `ExtraModuleNames.Add("ChimeraSimHost")` to BOTH target files (`ProjectChimera.Target.cs:13`, `ProjectChimeraEditor.Target.cs:13`). Modules not listed in `ExtraModuleNames` or a plugin are not built/linked.
5. `Build.bat` as above. No IDE project regeneration is required to build (the `.sln` files in `P/` are for the IDE; refresh them later with the engine's GenerateProjectFiles if wanted).
6. Hot reload: with the editor closed, a rebuild is a plain relink; UBT rebuilds the module DLL `UnrealEditor-ChimeraSimHost.dll`. Do NOT use Live Coding with the sim DLL loaded
   (it cannot be unloaded anyway).
Variant: put the host in a project **plugin** (`Plugins/ChimeraSim/ChimeraSim.uplugin`), mirroring the `ThirdPartyLibrary` template, if the sim should be reusable in a second UE project;
for the trial a game module is simpler (fewer moving parts, same staging rules).

### 6.3 A new sibling UE project
- Copy `ProjectChimera.uproject` (new name/`ProjectID` in `DefaultGame.ini`), keep the same `EngineAssociation` GUID, add `Source/<Name>/` + two target files, run `Build.bat <Name>Editor ...`.
  Cost: a fresh `Intermediate`, shared PCH and DDC for the new project (the first build log shows 7 actions, 80 s UBA time with memory throttled). Shaders: the first editor launch with
  Substrate + SM6 compiled for ~12 min (HANDOFF "How to run"; first-run estimate in PLAN.md 14 was 30-120 min), later launches ~2 min.
  A sibling project gains nothing for check (a) over adding a module here; it is worth it only to keep the trial isolated from the look-test project. Copying `Content/LookTest` is 231 MB.

### 6.4 Packaging (not exercised)
- `-game` through `UnrealEditor.exe` is what the trial uses (uncooked; "packaged is similar or slightly faster", `PLAN.md` 12). A packaged Windows build would use UAT
  `RunUAT.bat BuildCookRun` with the Game target (`E/Build/BatchFiles/RunUAT.bat` exists). Nothing has been tried; UNVERIFIED.

---

## 7. Recommended module layout for check (a)

```
D:/Projects/Chimera-Unreal/ProjectChimera/            (trial project; not a repo; copy Source/ChimeraSimHost into R/tools/unreal-trial/ at the checkpoint)
  ProjectChimera.uproject                              + Modules: ChimeraSimHost (Runtime, Default)
  Source/
    ProjectChimera.Target.cs / ...Editor.Target.cs     + ExtraModuleNames.Add("ChimeraSimHost")
    ChimeraSimHost/
      ChimeraSimHost.Build.cs                          deps: Core, CoreUObject, Engine; RuntimeDependencies.Add("$(ProjectDir)/Binaries/ThirdParty/ChimeraSim/Win64/ChimeraSim.dll")
      Public/ChimeraSimApi.h                           pure-C declarations: struct ChimeraUnitRecord { int32 id, type, team; int32 posX,posY,posZ; int32 prevX,prevY,prevZ; int32 facing; int32 anim; } ; extern "C" typedefs
      Private/ChimeraSimLibrary.{h,cpp}                FChimeraSimLibrary: GetDllHandle(ProjectDir/Binaries/ThirdParty/...), GetDllExport x N into a function-pointer table, one Version() sanity call, NO free
      Private/ChimeraSimHostModule.cpp                 StartupModule: load library once; logs path + version + tick-rate
      Public/ChimeraSimSubsystem.h/.cpp                UTickableWorldSubsystem: Start(units, seed, mode), Tick: step -> read -> interpolate -> ISM update; log "tick=<n> checksum=0x<hex>" each 60 ticks
      Private/ChimeraUnitRenderer.{h,cpp}              owns one ISM per (unit type, team); maps sim id -> (component, instance index); applies scale/facing per source (facing.json)
      Private/ChimeraSimCommands.cpp                   FAutoConsoleCommand "Chimera.Sim.Start/Stop/Report"; parses -ChimeraSimUnits=, -ChimeraSimSeed=, -ChimeraSimTicks=
  Binaries/ThirdParty/ChimeraSim/Win64/ChimeraSim.dll  produced by `dotnet publish -r win-x64 -c Release` (NativeLib=Shared) of a new godot-side library project that includes SimSources.props
  LookTest/ (unchanged)                                run_fps.ps1 gets a -Map / extra-args variant for a sim run
```
Side of the repo (not UE): a new `godot/ProjectChimera.Sim.Native/` (or similar) class library importing `..\SimSources.props`, exporting the C functions with
`[UnmanagedCallersOnly(EntryPoint=...)]` over `SimulationHost.Create/Update/StepOnce`, `EntityWorld` arrays, `SimChecksum`/`LastChecksum`. Same-source rule from the existing analysis project
(`ProjectChimera.Sim.Analysis.csproj`, `IsAotCompatible=true`) keeps trim/AOT diagnostics visible: the analyzer's reflective-STJ advisories are exactly the
"content loading must move to source generation" work the spec names.

Map/scene: reuse `LT_A` (or `LT_Facing`, tiny) with the roster ISMs spawned by the subsystem into the existing ground, so lighting, camera and the baseline fps remain comparable.
Spawn from C++ at `-ChimeraSimUnits=` start; no actor placement in the level.

Minimal proof order for check (a): (1) DLL loads in `-game`, `Sim_Version()` logged; (2) sim runs N ticks headless in the subsystem, checksum pairs printed, diffed against the
Godot-side test run of the same scenario; (3) 1,000 static ISM instances move from the sim, `Screenshot` + CSV; (4) optional skinned-instance spike.

---

## 8. Risks

| # | Risk | Evidence / basis | Mitigation |
|---|---|---|---|
| R1 | NativeAOT DLL cannot be unloaded; editor/PIE/Live Coding reloads of the host module re-enter with the DLL still mapped | Microsoft doc (3.2) | Load once, never free; sim create/destroy exports; rebuild the DLL only with the editor closed; test PIE start/stop twice |
| R2 | NativeAOT publish + content loading: the sim's JSON loading is reflection STJ (analyzer reports IL2xxx/IL3xxx advisories, `ProjectChimera.Sim.Analysis.csproj`) | Analysis csproj header | Source-gen first (spec order); a publish that "works" with trimmer warnings can still fail at runtime |
| R3 | Float/double use in the sim's AI (known float-to-Fixed debt, `SimSources.props` AI comment) could differ between the .NET JIT and AOT toolchains, moving checksums | `SimSources.props` AI comment, `DelayMath` note | Compare checksums JIT vs AOT in the unit test project first, before UE is involved |
| R4 | Axis/handedness/scale mapping errors (Godot Y-up metres -> UE Z-up cm; per-source front yaw) show as mirrored/rotated units, not as checksum errors | `facing.json`, `lt_common.py` layout | Fix mapping once in `ChimeraUnitRenderer`; check with the 4 reference units from `LT_Facing` |
| R5 | GPU, not game thread, is the bound: A is already 15.4 ms GPU at 58 units | `README.md` | Measure 1,000 units on `A_noLumen`/B-like settings too; Lumen is decided off anyway (HANDOFF) |
| R6 | ISM + Nanite per-(type,team) components multiply draws and PSO/material variants; Substrate glTF materials are heavy | Section 4.3 | Move to per-instance custom data with one custom material; measure first |
| R7 | Skinned crowd route unproven (instanced skinned mesh, anim bank bake of the Tripo rig, per-instance clip phase) | Section 4.4 | Time-box a spike; do not let it block the checksum check |
| R8 | 5.8 ISM semantic changes: `bMarkRenderStateDirty=false` assumed to be enough | `ISMInstanceDataManager.cpp:820-860` | Verify visually (units move) and in CSV; keep a switch to force `true` |
| R9 | `-game` runs through the editor binary; packaged DLL staging path untested | Section 3.2 | Check once with a minimal package after check (a) works |
| R10 | Memory: 16 GB machine; editor idles 4.2 GB, UBA throttles parallel compile by memory | HANDOFF; Log.txt:35-37 | Close the editor for builds; keep page file; RAM upgrade pending (HANDOFF) |
| R11 | Launching the editor from the PowerShell tool hung once with no log; MSYS rewrites `/Game/...` | `PLAN_DELTA.md` D7 | Use `start_editor.sh` / `run_fps.ps1`, `MSYS_NO_PATHCONV=1` |
| R12 | Unreal MCP (`unreal-mcp`) is down whenever the editor is closed; `CaptureViewport` returns 1014x550 and an image-in-text quirk | `PLAN_DELTA.md` D8; session error banner | Use `Screenshot`/`HighResShot` in `-game` for evidence |
| R13 | The `.uproject` `EngineAssociation` is a GUID (registered build), not "5.8"; a sibling project must copy it exactly | `ProjectChimera.uproject:3` | Copy the descriptor, do not hand-write it |
| R14 | Spec non-negotiable "Nothing in Unreal writes simulation state directly": host exports must be read-only plus a command-submit function | Spec "Non-negotiables" | API has `Sim_SubmitCommand(bytes)` only; no setters |

---

## 9. Open questions

1. Is `UInstancedSkinnedMeshComponent` + AnimBank production-ready in 5.8.3 for the Tripo rig (41 bones, 6-11 clips each)? Needs an editor spike (create an `UAnimBank`, add 1,000 instances).
2. Does the NativeAOT shared library on Windows initialise its runtime at `LoadLibrary` or at first export call, and does it interact badly with UE's loader-lock expectations? Needs the first `Sim_Version()` call to run inside UE.
3. Axis convention of the sim in world space: is ground X/Z (Godot Y up) and what is the sim's facing representation? (The look-test layout uses UE X/Y ground; the sim's `FixedVec3` use must be read before the renderer is written.) This is outside this report's files and belongs with the sim-side research.
4. Should the 1,000-unit run be fighting (units die, counts change) or a steady march? The CSV window logic prefers steady; the checksum check prefers a rich scenario. Probably two scenarios.
5. Where does the DLL and its build script live (repo `tools/`, plugin vs game module)? The trial project is not a git repo (`HANDOFF.md`); copying `Source/ChimeraSimHost` into the repo at the checkpoint is the proposal.
6. How are scripted commands fed into the sim from UE for the checksum run (file of recorded commands vs a built-in scenario export)? The `.chmr` replay player (`R/godot/src/Multiplayer/ReplayPlayer.cs`, included by `SimSources.props`) may already provide the stream.
7. Does the planned "Lumen off" project default (HANDOFF follow-up) get applied before the 1,000-unit measurement? Affects the baseline (63 vs 76 fps).

---

## 10. Things I did not verify (consolidated)
- Any behaviour requiring a running editor, a UE build, or a NativeAOT publish (none were run by design).
- NativeAOT `.lib` emission, runtime-init timing, loader-lock behaviour; packaged-build staging.
- ISM update cost with/without `bMarkRenderStateDirty`; Nanite ISM cost at 1,000 instances; whether the Substrate glTF material works with `PerInstanceCustomData` (needs a new material).
- Instanced skinned mesh route end to end (animation-index semantics per instance, Nanite skin with these meshes, performance).
- The `Screenshot showui filename=... nosuffix` line as a `-csvExecCmds` entry (syntax read from source only).
- UBT compile time for a multi-file module (only the empty module was timed: ~80-115 s).
- CsvProfiler.cpp line numbers for the boot-capture flags are quoted from `P/LookTest/PLAN.md` 10.1 (written 2026-09-30 from the same install), not re-read here.
