export const meta = {
  name: 'unreal-trial-understand-design',
  description: 'Map code/engine for the 3 Unreal trial checks, write a plan per check, critique and revise, then integrate',
  phases: [
    { title: 'Understand', detail: '7 parallel readers: STJ/AOT, sim entry+checksum, UE host+ISM, HUD board 3.1, Slate, terrain options, Godot terrain' },
    { title: 'Design', detail: 'one Opus designer per check' },
    { title: 'Critique', detail: 'two Opus critics per plan (API-correctness lens, proof lens)' },
    { title: 'Revise', detail: 'apply valid critiques' },
    { title: 'Integrate', detail: 'one execution schedule across the three plans' },
  ],
}

const SCRATCH = 'C:/Users/MD_Ki/AppData/Local/Temp/claude/D--Projects-Project-Chimera/6de76155-94ab-440a-84fd-59aa5580f954/scratchpad'
const REPO = 'D:/Projects/Project_Chimera'
const OUT = REPO + '/docs/unreal-move/trial-checks'
const RES = OUT + '/research'
const SPEC = SCRATCH + '/unreal_spec.txt'
const SO = 'Call StructuredOutput with each field as a top-level argument, not as a JSON string under an `input` key.'

const CONTEXT = `CONTEXT. Project Chimera is an RTS creation platform: today Godot 4.6.3 + C#, with an engine-free deterministic simulation (fixed-point maths, seeded RNG, 30 ticks/s, ~75k lines, 6,392 tests; baseline 6392 pass / 0 fail / 1 skipped). The developer (Alec, solo) has decided to move presentation to Unreal Engine 5.8.3, gated on a trial with three checks:
 (a) the sim compiled with .NET NativeAOT into a native library (plain C exports) drives 1,000 units in Unreal with the SAME checksums as Godot. Sim content loading must first move from reflection-based System.Text.Json to source generation.
 (b) the Round 2 Match HUD, board 3.1 in docs/ui-redesign/Match.dc.html, built in native C++ (Slate or widgets built in code; no widget Blueprints) and matched to its mockup by screenshot ("pixel-faithful is the bar").
 (c) terrain editing while the game runs: a runtime-editable terrain, RealtimeMeshComponent (free, MIT) or Errant Landscape's runtime module (Unreal's own Landscape can't be sculpted in a shipped game).
The product spec (plain-text export of Alec's living doc) is at ${SPEC}; its "Non-negotiables" and "How it is built in Unreal" sections bind every design. HANDOFF: ${REPO}/HANDOFF.md.
Repo: ${REPO} (Godot project in godot/; sim tests godot/ProjectChimera.Sim.Tests; AOT/banned-API analyzer project godot/ProjectChimera.Sim.Analysis; the sim source set is defined in godot/SimSources.props). Unreal trial project: D:/Projects/Chimera-Unreal/ProjectChimera (UE 5.8.3 Blank C++, NOT a git repo, built with Build.bat in ~2 min; look-test tooling in its LookTest/tools and mirrored in ${REPO}/tools/unreal-looktest; live-found Unreal gotchas in tools/unreal-looktest/PLAN_DELTA.md D7-D9). Engine install: D:/Epic Games/UE_5.8 (engine source and plugin source are present under Engine/Source and Engine/Plugins; verify API claims there). Godot editors: C:/Godot/Godot_v4.6.3-stable_mono_win64 (project version) and 4.6.2. Machine: Windows 11, Ryzen 5 5600 (6 cores / 12 threads), 16 GB RAM, RTX 3060 12 GB, 1080p. .NET SDKs 8.0.419 (pinned by global.json), 9.0.312, 10.0.401. playwright-cli is installed globally (skill: playwright-cli).
EVIDENCE RULE: cite file:line (or URL + date) for every load-bearing claim; mark anything you could not verify as UNVERIFIED. Do not guess API names: open the header.`

const READONLY = `This is the UNDERSTAND phase. Do NOT edit any existing file in either project, do NOT launch the Unreal editor or run a UE build, do NOT commit. You may run read-only commands, and dotnet build/test of the sim test or analysis projects (they only write bin/obj). Create only the output files named in your task.`

const READER_SCHEMA = {
  type: 'object',
  properties: {
    report_path: { type: 'string' },
    summary: { type: 'string', description: '8-15 lines: the findings that matter most for the design' },
    key_facts: { type: 'array', items: { type: 'string' }, description: 'each with its file:line or URL' },
    risks: { type: 'array', items: { type: 'string' } },
    open_questions: { type: 'array', items: { type: 'string' } },
  },
  required: ['report_path', 'summary', 'key_facts', 'risks', 'open_questions'],
}

const READERS = {
  stj: {
    label: 'read:stj-aot',
    file: RES + '/r1-stj-aot-inventory.md',
    task: `TASK: inventory everything in the sim source set that blocks or changes behaviour under NativeAOT, above all reflection-based System.Text.Json, and write the migration recipe to source generation.
1. Read godot/SimSources.props IN FULL to get the complete list of compiled sim files (the globs AND every explicit single-file include further down), not just the first globs.
2. For every System.Text.Json use in that set: file:line; the API (JsonSerializer.Deserialize<T>/Serialize, JsonDocument, JsonNode, JsonElement, Utf8JsonReader/Writer); the root type T and the DTO graph it reaches (count the types); the JsonSerializerOptions used (naming policy, custom JsonConverter classes and what they do, number handling, [JsonPolymorphic]/[JsonDerivedType], case-insensitivity, comments/trailing commas, IncludeFields, enum converters, DefaultIgnoreCondition); whether any JsonSerializerContext already exists.
3. Also list every OTHER reflection / dynamic-code use in the set (Activator, Type.GetType, MakeGenericType, Enum.GetValues<T>-style reflection, Expression.Compile, dynamic, Reflection.Emit, assembly scanning, attributes read by reflection).
4. Run: dotnet build godot/ProjectChimera.Sim.Analysis/ProjectChimera.Sim.Analysis.csproj --no-incremental and capture every IL2xxx/IL3xxx warning with its site, grouped by rule; separate STJ-caused from other causes. Give the counts.
5. Work out what changes under source generation: private/init setters, constructors, fields, polymorphism, converters that rely on reflection, object/JsonElement/Dictionary<string,object> members, the dual-path content DTO constraint (see memory note chimera-dual-path-content-dto-constraint: no enums, float-not-Fixed, settable auto-props), default values and missing-member handling. Identify anything whose OUTPUT (deserialized values or serialized bytes, e.g. content hashes and the ProceduralMapGenerator golden that pins serializer output bytes) could differ.
6. List where non-sim Godot code (godot/src/UI, CreationSuite, UGC, Multiplayer) uses the same DTOs/serializer calls, so the migration keeps the Godot build compiling and behaving the same.
7. Give a concrete recipe: which JsonSerializerContext class(es), which [JsonSerializable] roots, how options move to [JsonSourceGenerationOptions], each call site's new form, and how to PROVE identity (e.g. load every shipped content file both ways and compare a canonical dump; the content-hash and golden tests that must stay byte-identical).
Write the report (300-600 lines markdown) to the report path.`,
  },
  sim: {
    label: 'read:sim-entry-checksum',
    file: RES + '/r2-sim-entry-checksum-scenario.md',
    task: `TASK: map how a match is built and run headlessly, so one 1,000-unit determinism scenario can run identically in (1) real Godot, (2) the .NET test harness, and (3) a NativeAOT native library called from C++/Unreal.
Document with file:line: the sim's top-level world/match object and its construction from content (which content files: units, factions, abilities, maps; where they live on disk); the tick loop and system order; the command/order stream API (how move / attack-move enter the sim); SimChecksum (API, AlgoVersion, what it folds, how often the game computes it; the ~2 s sync interval); the golden-checksum harness in ProjectChimera.Sim.Tests (how goldens are recorded and compared, scenario files); RNG seeding.
Find any existing scenario/test/benchmark with a large unit count (perf, stress, scale, 500-2,000 units) and any existing headless entry point in the Godot project (dedicated-server headless mode, command-line flags, test scenes, the MainSceneDebugSeam) that could run a scenario inside real Godot and print checksums. Determine everything MainScene (the Godot game) does at match start that the headless harness may do differently (map/terrain load, navmesh bake, spawn order, AI on/off, fog, frame-paced writes) — any such difference would break "same checksum as Godot".
List every JIT-vs-NativeAOT determinism hazard in the sim with sites: float use (AI scoring), string.GetHashCode / randomized hashing feeding order, Dictionary/HashSet enumeration after removals, Parallel/threads, culture-sensitive parsing/formatting, DateTime/Stopwatch, static initialisation order, Span/vectorised paths that differ by codegen.
Finish with a concrete proposal: the 1,000-unit scenario (units/factions/map, how spawned, scripted orders so units move AND fight through the run, tick count, checksum sampling ticks) and the minimal C API a native library needs (create from content dir + scenario + seed; step N ticks; read per-unit arrays for rendering: id, position, facing, type, owner, alive, animation state; checksum at tick; issue command; destroy), naming the existing C# methods each export calls.
Write the report (300-600 lines markdown) to the report path.`,
  },
  ue: {
    label: 'read:ue-host-ism',
    file: RES + '/r3-ue-host-and-instancing.md',
    task: `TASK: document the Unreal trial project and how to host a NativeAOT .NET native library and render 1,000 sim-driven units in it.
1. Read D:/Projects/Chimera-Unreal/ProjectChimera: .uproject, Source/ (Build.cs, targets, module code), Config/*.ini (DPI, rendering, ray tracing, default maps, game mode), the LookTest folder and tools (start_editor.sh, run_fps.ps1, ue_job.py, ue_bridge.py, lt_build.py, lt_import.py, lt_capture.py, parse_csv.py), and PLAN_DELTA.md gotchas. List the imported unit assets under Content/LookTest (static and skeletal meshes, team-tint materials, the look-test level, Look A lighting/post setup) by asset path (use the files on disk and the tools' scripts; the editor is not running).
2. From the installed engine source (D:/Epic Games/UE_5.8/Engine/Source and Plugins), confirm with file:line the 5.8 APIs to: load a third-party DLL from a runtime module (Build.cs RuntimeDependencies, PublicDelayLoadDLLs, FPlatformProcess::GetDllHandle / GetDllExport, staging next to the binary for -game and packaged builds); update thousands of instance transforms per frame in UInstancedStaticMeshComponent / HISM (BatchUpdateInstancesTransforms, UpdateInstanceTransform + MarkRenderStateDirty, AddInstances, PerInstanceCustomData for team colour, NumCustomDataFloats); whether 5.8 has an instanced skinned mesh / animation-to-texture route for animated crowds and its status; the cleanest way to run a C++ actor or subsystem that ticks a native sim at a fixed 30 Hz inside -game with render interpolation.
3. How the look test ran -game, took screenshots (HighResShot vs screenshot with UI) and CSV profiling (run_fps.ps1), so the sim check can reuse it.
4. Build facts: Build.bat command, UBA, the page-file fix, compile times; how a NEW C++ module or a new sibling UE project is added and built with the installed (Launcher) engine.
Recommend a module layout for check (a) and list risks. Write the report (300-500 lines markdown) to the report path.`,
  },
  hud: {
    label: 'read:hud-board-3.1',
    file: RES + '/r4-hud-board-3.1-spec.md',
    task: `TASK: fully specify Match HUD board 3.1 from the approved Round 2 UI set so it can be rebuilt pixel-faithfully in Unreal Slate, and produce the reference images.
Files: ${REPO}/docs/ui-redesign/Match.dc.html and its scripts support.js, icons.js, levers.js, ornaments.js, the README.md Match HUD section, and "Chimera Design System.dc.html" for tokens.
1. Identify exactly which board is "3.1" (list every 3.1* variant, e.g. 3.1a "HUD · early game", and say which is canonical and why).
2. Render it: serve docs/ui-redesign over http (python -m http.server on a free port; confirm it answers before navigating), and use playwright-cli (read the playwright-cli skill first; use snapshot/find and element refs, not guessed CSS selectors) to open Match.dc.html, find board 3.1, determine its design resolution, and capture the board element alone at device scale 1 at its native size, plus a 1920x1080 version if its native size differs. Save PNGs under ${RES}/hud-ref/. Stop the http server when done.
3. Extract exact numbers with playwright-cli eval (getBoundingClientRect relative to the board, getComputedStyle): for every visible element of the board (top strip, resources, clock, minimap, selection panel, portraits, command card, hero tabs, alerts, rings/markers drawn over the world, tooltips, ornaments) record box (x,y,w,h in board px), z-order, fill (solid/gradient stops+angle), borders (width, colour, per-corner radius), box-shadows, opacity, text runs (string, font family, weight, size px, letter-spacing, line-height, colour, alignment, text-transform, text-shadow), icons (name from icons.js, SVG path data, size, colour), and the world background image ('village' or similar: find where it comes from — file or data URI — and save it as a file under hud-ref/). Write the full dump to ${RES}/hud-ref/board-3.1-elements.json.
4. Fonts: list every font family used, where it is loaded from (Google Fonts URL or local), and download the TTF/OTF files into ${RES}/hud-ref/fonts if they are open-licensed (record the licence). Note any font fallbacks the browser actually used.
5. Write the human-readable spec (400-900 lines markdown): coordinate system, a table per region, the design tokens, and a list of the visual features Slate must reproduce (gradients, inner/outer shadows, blur/backdrop-filter, SVG icons, ornaments, text effects) ranked by how hard they are.
Report path is given below; return the paths of the PNGs and JSON in key_facts.`,
  },
  slate: {
    label: 'read:slate-5.8',
    file: RES + '/r5-slate-pixel-faithful.md',
    task: `TASK: determine from the installed UE 5.8 source (D:/Epic Games/UE_5.8/Engine/Source and Engine/Plugins; cite file:line) how to build a pixel-faithful HUD in pure C++ Slate with no widget Blueprints and as few binary assets as possible.
1. Fonts: loading a TTF/OTF from disk at runtime (FSlateFontInfo constructor taking a filename, FCompositeFont / FStandaloneCompositeFont), hinting and anti-aliasing (EFontHinting, related cvars), the exact conversion from CSS px to Slate font size (find the render DPI constant in the FreeType/Slate font code and work out the formula), letter spacing units (FSlateFontInfo::LetterSpacing), line height (LineHeightPercentage), text shadow, outline (FFontOutlineSettings), font weights from separate files.
2. Brushes and drawing: FSlateRoundedBoxBrush (per-corner radii, outline), FSlateColorBrush, gradients (FSlateDrawElement::MakeGradient, custom OnPaint), box shadows / blur (SBackgroundBlur cost, custom paint), dashed lines, image brushes from PNG at runtime (FSlateDynamicImageBrush, IImageWrapper), and whether 5.8 can rasterise SVG at runtime in Slate (search for Svg in SlateCore, Slate, and plugins) — if not, the best route for the mockup's SVG icons.
3. DPI: how to make 1 Slate unit = 1 screen px at 1920x1080 ([/Script/Engine.UserInterfaceSettings] DPI curve / ApplicationScale in DefaultEngine.ini), and how a Slate widget is added to the game viewport from C++ (GEngine->GameViewport->AddViewportWidgetContent, z-order).
4. Screenshots: capturing the final frame INCLUDING UI at exactly 1920x1080 from a -game windowed run, automated from the command line (FScreenshotRequest::RequestScreenshot with bShowUI, the "Shot showui" console command, r.SetRes, -ResX/-ResY/-windowed, -ExecCmds, then quit), and where the file lands.
5. Text-rendering differences to expect against Chromium (gamma, grayscale vs subpixel AA, hinting, kerning) and the settings that bring Slate closest.
6. A minimal compile-ready skeleton for a C++ HUD module (Build.cs dependencies, includes, a compound widget, adding it to the viewport from a GameMode/HUD) verified against the headers.
Write the report (300-500 lines markdown, code verified against headers) to the report path.`,
  },
  terrain: {
    label: 'read:terrain-options',
    file: RES + '/r6-runtime-terrain-options.md',
    task: `TASK: research runtime-editable terrain for UE 5.8 for an RTS map editor where creators sculpt (raise/lower/smooth/flatten/ramp) and paint terrain while the game runs, including packaged shipping builds, with collision for cursor picking and unit placement, at 60 fps on an RTX 3060 at 1080p, in a near-photoreal Manor-Lords-style look.
1. RealtimeMeshComponent (TriAxis-Games, MIT): repo URL, latest release/tag and date, which UE versions it supports (does anything support 5.8? which branch?), API for chunked sections and partial updates, async collision, LOD, licence, how to install as a project plugin. Fetch the actual GitHub pages (use the web-fetch agent type or WebFetch/WebSearch) and cite URLs.
2. Errant Landscape (Errant Photon, Fab): what it is, its runtime-editing module, price and licence, UE 5.8 support, whether it can be evaluated without purchase.
3. Built-in 5.8 options, verified in D:/Epic Games/UE_5.8 with file:line: UDynamicMeshComponent (GeometryFramework; usable in packaged games? collision updates?), UProceduralMeshComponent, any 5.8 "Mesh Terrain" feature, why Landscape can't be edited at runtime, Virtual Heightfield Mesh.
4. Runtime texture painting: splat maps via UTexture2D::UpdateTextureRegions or render targets feeding a layered material; how photoreal ground blends several layers; runtime virtual texture compatibility.
5. Known performance numbers or community reports for each option.
Conclude with a ranked recommendation for the trial, what to measure, and any purchase decision that would be Alec's. Write the report (300-500 lines markdown with a Sources list) to the report path.`,
  },
  gterrain: {
    label: 'read:godot-terrain',
    file: RES + '/r7-godot-terrain-as-built.md',
    task: `TASK: document how terrain works in the Godot build today, so the Unreal runtime terrain can match its data model and editing tools. Find with file:line: the terrain plugin (Terrain3D? under godot/addons) and Chimera's wrapper; how the map editor sculpts and paints (every tool: raise/lower/smooth/flatten/ramp/noise etc.; brush size/strength/falloff params and their units; paint layers and textures; water; impassable paint); undo for terrain edits; the terrain data saved in a scenario/map (heightmap resolution, world size in metres, height range, splat/paint storage, JSON fields and file format); map sizes of shipped maps; and how terrain feeds the simulation (height grid, high-ground levels, passability, navmesh bake: which sim data is derived from terrain, when, and whether it is in the checksum). Note any known perf numbers. Write the report (200-400 lines markdown) to the report path.`,
  },
}

function readerPrompt(key) {
  const r = READERS[key]
  return `${CONTEXT}\n\n${READONLY}\n\n${r.task}\n\nReport path: ${r.file} (create parent folders if needed).\nReturn: the report path, an 8-15 line summary, key facts with citations, risks, open questions. ${SO}`
}

phase('Understand')
const readerP = {}
for (const key of Object.keys(READERS)) {
  readerP[key] = agent(readerPrompt(key), { label: READERS[key].label, phase: 'Understand', schema: READER_SCHEMA, model: 'sonnet', effort: 'high' })
}

const PLAN_SCHEMA = {
  type: 'object',
  properties: {
    plan_path: { type: 'string' },
    summary: { type: 'string', description: '8-15 lines' },
    proof_of_done: { type: 'string' },
    task_count: { type: 'number' },
    decisions_for_alec: { type: 'array', items: { type: 'string' } },
  },
  required: ['plan_path', 'summary', 'proof_of_done', 'task_count', 'decisions_for_alec'],
}
const CRIT_SCHEMA = {
  type: 'object',
  properties: {
    issues: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          severity: { type: 'string', enum: ['blocker', 'major', 'minor'] },
          confidence: { type: 'number', description: '0-1' },
          title: { type: 'string' },
          detail: { type: 'string' },
          evidence: { type: 'string', description: 'file:line or URL you checked' },
          fix: { type: 'string' },
        },
        required: ['severity', 'confidence', 'title', 'detail', 'evidence', 'fix'],
      },
    },
  },
  required: ['issues'],
}
const REVISE_SCHEMA = {
  type: 'object',
  properties: {
    plan_path: { type: 'string' },
    accepted: { type: 'number' },
    rejected: { type: 'number' },
    summary: { type: 'string', description: 'what changed in the plan, 5-12 lines' },
    decisions_for_alec: { type: 'array', items: { type: 'string' } },
  },
  required: ['plan_path', 'accepted', 'rejected', 'summary', 'decisions_for_alec'],
}

const FRAME = `FIXED DECISIONS (made by the main session; design within them, flag disagreement in the plan's Risks section rather than silently deviating):
- Project layout: check (a) lives in the existing trial project D:/Projects/Chimera-Unreal/ProjectChimera (it holds the imported Tripo unit content). Check (b) gets a new sibling UE project D:/Projects/Chimera-Unreal/ChimeraHud and check (c) a new sibling project D:/Projects/Chimera-Unreal/ChimeraTerrain, created from the same Blank C++ shape, so the three can be built and debugged independently; they are merged into one project only after the trial passes. A sibling project may copy assets it needs from ProjectChimera/Content.
- One heavy-Unreal process at a time on this 16 GB machine: every UE build (Build.bat), editor launch and -game run goes through a global lock script the main session will provide at D:/Projects/Chimera-Unreal/ue_lock.sh (usage: bash ue_lock.sh <command...>; it waits for the lock, runs the command, releases). Plans must mark which tasks need it and keep locked steps short.
- The .NET side of check (a) lives in the Chimera repo: the source-generation migration in godot/src, and a new NativeAOT library project under godot/ (e.g. godot/ProjectChimera.Sim.Native) that compiles the same SimSources.props set. The sim test baseline (6392 / 0 / 1 skipped) must hold and NO golden may move: the migration must be behaviour-neutral.
- Text first: no gameplay or UI logic in Blueprints; C++ GameModes/actors selected by command-line URL options are preferred over binary maps; binary assets only for imported art (and materials where unavoidable, created by script).
- Everything must be verifiable unattended: every task needs a command whose output proves it; visual results are proven by screenshots that get compared (numbers) and looked at.
- Alec is on his phone: proof artefacts must be PNGs (or short GIF/MP4) plus numbers.
- Implementers: Sonnet 5.5 for ordinary code; Opus 5.5 for visual-judged work, geometry, and the hard tail. Mark each task with the tier.`

const CHECKS = [
  {
    key: 'a', name: 'sim', title: 'Check (a): NativeAOT sim drives 1,000 units in Unreal with checksums matching Godot',
    plan: OUT + '/plan-a-native-sim.md', readers: ['stj', 'sim', 'ue'],
    focus: `Cover, in order: the STJ reflection-to-source-generation migration with an identity proof; the NativeAOT library project and its C ABI (memory ownership, string/array marshalling, error reporting, threading, versioning); a C++ console smoke harness that loads the DLL outside Unreal and matches the managed checksums; the 1,000-unit scenario; how the SAME scenario runs inside real Godot (headless) and prints checksums at the same ticks; the Unreal module (DLL load and staging, fixed 30 Hz tick, ISM/HISM rendering per unit type with team colour, interpolation, camera), and the -game run that logs checksums and fps; the comparison (all four sources — Godot, managed harness, C++ harness, Unreal — equal at every sampled tick) and the evidence artefacts. State the exact definition of "same checksum as Godot".`,
  },
  {
    key: 'b', name: 'hud', title: 'Check (b): Round 2 Match HUD board 3.1 in native C++ Slate, matched to the mockup by screenshot',
    plan: OUT + '/plan-b-match-hud.md', readers: ['hud', 'slate'],
    focus: `Cover: project creation (ChimeraHud), DPI so 1 Slate unit = 1 px at the board's design resolution, fonts loaded from TTF files at runtime, icons (SVG to PNG rasterisation at build time or runtime), the widget tree region by region, the background (render the HUD over the same world image the mockup uses, so the comparison isolates the UI; plus one shot over the live look-test world as a bonus), the automated screenshot at exactly the reference size, and the COMPARISON: a script that aligns Unreal's PNG with the mockup PNG and reports per-region error (mean absolute difference and SSIM per named region and overall), a side-by-side and a diff heatmap, with explicit pass thresholds you justify (text anti-aliasing differences are expected; layout, size, colour and shape errors are not). Include an iterate-until-converged loop: compare, fix the worst region, rebuild, re-compare.`,
  },
  {
    key: 'c', name: 'terrain', title: 'Check (c): terrain editing while the game runs',
    plan: OUT + '/plan-c-runtime-terrain.md', readers: ['terrain', 'gterrain', 'ue'],
    focus: `Choose the terrain technology for the trial from the research (RealtimeMeshComponent vs Errant Landscape runtime vs a built-in fallback); if the strongest option costs money, that purchase is Alec's decision: say so, plan the free path, and put the buy decision in decisions_for_alec. Cover: project creation (ChimeraTerrain), plugin install, the heightfield data model matched to the Godot map format (resolution, world size, height range), chunked mesh with normals/tangents, sculpt tools (raise, lower, smooth, flatten at least) driven by mouse in -game AND by a scripted deterministic brush sequence for unattended measurement, splat-map painting with a Manor-Lords-style photoreal layered ground material, collision kept current for cursor traces, undo, and how an edited heightfield would flow into the sim's terrain data later (design note only). Proof: before/after screenshots and a short GIF/MP4 of sculpting while running; numbers: ms per brush stroke (mesh + collision), fps while sculpting continuously, triangle counts, memory.`,
  },
]

const results = await Promise.all(CHECKS.map(async (c) => {
  const rs = await Promise.all(c.readers.map(k => readerP[k]))
  const reports = c.readers.map((k, i) => `- ${READERS[k].file}${rs[i] ? `\n  summary: ${rs[i].summary}` : ' (reader FAILED: investigate this area yourself)'}`).join('\n')

  const design = await agent(`${CONTEXT}\n\n${FRAME}\n\nTASK: design the implementation and proof plan for ${c.title}.\nResearch reports (the evidence; read them in full, and re-verify anything load-bearing yourself against the code or engine source):\n${reports}\nAlso read ${SPEC} (Non-negotiables, How it is built) and ${REPO}/HANDOFF.md.\n${c.focus}\nThe plan will be executed by implementer agents inside a workflow, so break it into ordered tasks, each with: id, goal, tier (sonnet/opus), files to create or modify, the exact acceptance command(s) and expected output, dependencies, and whether it needs the UE lock. Define proof of done precisely (numbers, thresholds, artefacts). Add Risks with fallbacks, and Out of scope. Do not write code beyond short illustrative snippets. Do NOT edit project files; write only the plan.\nWrite the plan (200-450 lines markdown) to ${c.plan}. Return its path, summary, proof of done, task count and any decisions that are genuinely Alec's. ${SO}`,
    { label: `design:${c.name}`, phase: 'Design', schema: PLAN_SCHEMA, model: 'opus', effort: 'xhigh' })
  if (!design) return { check: c.key, failed: 'design' }

  const lenses = [
    { k: 'api', p: `LENS: technical correctness. Check every API, file, function, setting and build claim in the plan against the actual code and the installed engine source (open the headers and files). Find wrong or nonexistent APIs, wrong assumptions about the Chimera code, determinism hazards, ordering errors, missing steps, build/staging problems, and tasks too large or vague for an implementer.` },
    { k: 'proof', p: `LENS: proof and execution. Would the proof of done convince a skeptical engineer that the check passed, or could it pass while the real thing fails (e.g. checksums equal because nothing moves, a screenshot match that ignores the HUD, terrain edits that don't update collision)? Is every acceptance command runnable unattended on this machine (16 GB RAM, one heavy UE process at a time, Windows, Git Bash/PowerShell)? Are thresholds justified? What would a failure look like and does the plan detect it? Is anything out of scope that the check actually needs, or in scope that it doesn't?` },
  ]
  const crits = await Promise.all(lenses.map(l => agent(`${CONTEXT}\n\n${FRAME}\n\nTASK: adversarially review the plan at ${c.plan} for ${c.title}. The research it was built from: ${c.readers.map(k => READERS[k].file).join(', ')}.\n${l.p}\nReport EVERY issue you find with a severity (blocker/major/minor) and a confidence (0-1); do not filter for importance, the reviser filters. For each give the evidence you checked (file:line or URL) and a concrete fix. Do not edit any file. ${SO}`,
    { label: `critique:${c.name}:${l.k}`, phase: 'Critique', schema: CRIT_SCHEMA, model: 'opus', effort: 'xhigh' })))
  const issues = crits.filter(Boolean).flatMap((r, i) => (r.issues || []).map(x => ({ lens: lenses[i].k, ...x })))
  log(`${c.name}: ${issues.length} critique issues (${issues.filter(i => i.severity === 'blocker').length} blockers)`)

  const revised = await agent(`${CONTEXT}\n\n${FRAME}\n\nTASK: revise the plan at ${c.plan} (${c.title}) in light of these critique issues. For each issue, verify it yourself (open the cited file/header); accept it and change the plan, or reject it with a reason. Keep the plan concise (under ~500 lines). Append a "Review log" section: one line per issue, accepted/rejected and why.\nIssues (JSON):\n${JSON.stringify(issues, null, 1)}\nReturn the plan path, counts, what changed, and decisions that are genuinely Alec's. ${SO}`,
    { label: `revise:${c.name}`, phase: 'Revise', schema: REVISE_SCHEMA, model: 'opus', effort: 'xhigh' })
  return { check: c.key, design, issues_count: issues.length, blockers: issues.filter(i => i.severity === 'blocker').map(i => i.title), revised }
}))

phase('Integrate')
const integ = await agent(`${CONTEXT}\n\n${FRAME}\n\nTASK: the three trial-check plans are written and revised: ${CHECKS.map(c => c.plan).join(', ')}. Read all three in full. Write ${OUT}/EXECUTION.md (under ~200 lines): (1) one ordered execution schedule across the three checks showing what runs in parallel (non-UE work: .NET migration, native lib, C++ harness, mockup rendering, comparison scripts, plugin download) and what is serialised on the UE lock; (2) shared infrastructure to build first (the lock script contract, project creation for ChimeraHud/ChimeraTerrain, screenshot/compare tooling, how evidence files are named and stored under ${OUT}/evidence/); (3) conflicts or inconsistencies between the plans and how you resolved them; (4) the done-gate for the whole trial: per check, the artefacts and numbers that will be shown to Alec; (5) decisions that are genuinely Alec's, merged and deduplicated. Do not edit the plans or any code. Return a summary. ${SO}`,
  { label: 'integrate', phase: 'Integrate', schema: { type: 'object', properties: { path: { type: 'string' }, summary: { type: 'string' }, decisions_for_alec: { type: 'array', items: { type: 'string' } } }, required: ['path', 'summary', 'decisions_for_alec'] }, model: 'opus', effort: 'xhigh' })

const readers = {}
for (const k of Object.keys(readerP)) { const r = await readerP[k]; readers[k] = r ? { report: r.report_path, summary: r.summary, risks: r.risks, open_questions: r.open_questions } : null }
return { readers, results, integ }
