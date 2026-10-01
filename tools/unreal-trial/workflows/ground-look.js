export const meta = {
  name: 'unreal-trial-ground-look',
  description: 'Check c: bring the runtime terrain ground closer to Manor Lords (references, look pass judged by an Opus art director over up to 4 rounds, technical verify)',
  phases: [
    { title: 'Look', detail: 'G0 Manor Lords references and CC0 candidates; G1 look pass with judge rounds; technical verify' },
    { title: 'Markers', detail: 'one checkpoint file per judged round and per finished task for the main session' },
  ],
}

const R = 'D:/Projects/Project_Chimera'
const U = 'D:/Projects/Chimera-Unreal'
const DOCS = R + '/docs/unreal-move/trial-checks'
const PLAN = { A: DOCS + '/plan-a-native-sim.md', B: DOCS + '/plan-b-match-hud.md', C: DOCS + '/plan-c-runtime-terrain.md', X: DOCS + '/EXECUTION.md' }
const KIT = R + '/tools/unreal-trial'
const EV = DOCS + '/evidence'
const MARK = U + '/TrialOut/checkpoints'
const SO = 'Call StructuredOutput with each field as a top-level argument, not as a JSON string under an `input` key.'

const COMMON = `You are working on Project Chimera's Unreal trial, Phase 2 (Core). Chimera is an RTS creation platform (Godot 4.6.3 + C#) whose presentation is moving to Unreal 5.8.3 while the engine-free deterministic C# simulation stays. The trial has three checks: (a) the sim compiled with NativeAOT drives 1,000 units in Unreal with checksums equal to Godot's; (b) the Round 2 Match HUD board 3.1a rebuilt in C++ Slate and scored against its mockup; (c) runtime terrain editing (RealtimeMeshComponent). Plans of record: ${PLAN.A} (A), ${PLAN.B} (B), ${PLAN.C} (C) and ${PLAN.X} (EXECUTION). EXECUTION.md §1.1 (lock classes), §1.2 (the Phase 2 row), §2 (shared infrastructure; §2.2 Unreal runtime conventions; §2.3 evidence names and git policy), §3 (23 conflict resolutions) and §7 (Phase 0-1 results and main-session decisions) OVERRIDE the plans where they differ: read them before you start. Paths: R=${R}, U=${U}, P=${U}/ProjectChimera, H=${U}/ChimeraHud, T(errain)=${U}/ChimeraTerrain, KIT=${KIT}, EV=${EV}. Inside each plan its own shorthands apply (plan A: G, NAT, ST, PA and OUT=${U}/TrialOut/a; plan B: T = R/tools/unreal-hud; plan C: T = ChimeraTerrain, RT = R/tools/unreal-terrain). Plan A's PROOF and plan C's PROOF are EV/a and EV/c (EXECUTION §2.3).

STATE AT THE START OF PHASE 2 (verified and committed: R at 15bb7cb9, U at e441cee). Every Phase 0-1 task passed independent verification (S3, S4, A0, A1, A2, X1, A9, T0, T1, T3, C0, C1, C2, C3, C6; details in EXECUTION §7). Sim test baseline: total=7136 passed=7135 failed=0 skipped=1. Sim content JSON is source-generated; IL2026/IL3050 = 0 and release-gated; no golden or AlgoVersion has moved. P has X1's ChimeraSimHost module (the -ChimeraAotSmoke diagnostic: a NativeAOT DLL runs inside UnrealEditor -game) and A9's P/SimTrial/unit_meshes.json (18 static meshes). H captures Slate 1:1 (T3); T0's grayscale references and T1's regions, comparator and self-tests are in R/tools/unreal-hud and H/HudRef. ChimeraTerrain builds with RMC b8669a0 plus the collision patch; 23 data tests pass; Dynamic chunks render in -game (G1 PASS). RMC #303 reproduces on this RTX 3060 (Static draw invisible), so only the Dynamic path exists. C6's CC0 textures are in ChimeraTerrain/Textures.
ALEC'S DECISIONS: D1-D5 confirmed 2026-10-01 as recommended in EXECUTION §5 (D1: the Godot leg is SimSession in Godot's own build and runtime, plus the gating MainScene start-state probe A8b; the full-MainScene replay A14 is measured, not gated). D6 confirmed 2026-10-01: check (b) scores the HUD against the grayscale re-render of the mockup (T0's references), exactly as recommended. No Phase 2 task gates a timing number (EXECUTION §3 C9: gated timings come from Phase 4); timings measured now are reported.

RULES
1. Read your task in its plan and every design section, fact and research file it cites before writing code. Cite the headers/files you rely on; never guess an API name: open the header under D:/Epic Games/UE_5.8/Engine (or the RMC source, or the C# source).
2. Heavy jobs take the lock, from Git Bash only: UE_LOCK_TAG=<check>/<task> bash ${U}/ue_lock.sh <command>. Heavy = Build.bat, any UnrealEditor*/UnrealEditor-Cmd process (editor, -game, commandlet, automation), RunUAT, packaged exe runs, AND every full sim test run (dotnet test of godot/ProjectChimera.Sim.Tests), every NativeAOT/ILC publish, every Godot run. Light work (dotnet build, dotnet run of a console, python, the C++ harness and cl.exe, headless Chromium, git status/diff) takes no lock. The lock is re-entrant (UE_LOCK_HOLDER), waits for 2.5 GB of free RAM (up to 20 min) and logs every hold to U/ue_lock.log. One command per hold; never hold it while you edit code. From PowerShell, plain 'bash' is WSL: use "C:/Program Files/Git/bin/bash.exe".
3. WAITING: the Bash tool kills a foreground call at 600 s, and lock waits can be long (about ten agents of three checks share one lock; a wait of over an hour is possible). For anything that may take over ~8 minutes including the lock wait, start it with the Bash tool's run_in_background as  bash -c '<command> > <log> 2>&1; echo "EXIT=$?" > <log>.exit'  and then call  bash ${U}/wait_for.sh --file <log>.exit  in the FOREGROUND, repeating while it exits 124. NEVER end your turn while a background job of yours is running: your final message is your report and is taken as final.
4. NativeAOT publish needs "C:/Program Files (x86)/Microsoft Visual Studio/Installer" prepended to PATH (VS 2026 vcvarsall calls vswhere by bare name). The Analysis build's banned-API and AOT analyzers report only with --no-incremental.
5. Git: do NOT commit, push, stash, reset, checkout, restore, clean or apply in either repo, and do not edit HANDOFF.md. The main session commits each task's verified files by explicit path WHILE this workflow runs, so commits can appear under you: that is expected; never undo them. Touch only your task's paths (check a: godot/, tools/sim-trial, P/, U/TrialOut/a, EV/a; check b: tools/unreal-hud, H/, EV/b; check c: tools/unreal-terrain, ChimeraTerrain/, EV/c). Never kill a process you did not start. Never change machine, desktop or Parsec settings.
6. Parallel work: the other checks' tasks run beside yours, and in check (a) A6, A7, A8 and A10 run at the same time. Never build a .NET project or Unreal target that another running task is building: after A5, only the Godot chain (A8, then A8b, then A14) builds godot.csproj, and only A10 republishes NAT. A file in use (for example NAT/bin/publish/ChimeraSim.dll loaded by a harness) means wait and retry; never delete or overwrite another task's outputs.
7. Done means shown: run every acceptance command of your task and report its real output. If an item cannot pass, say so with the output. Never weaken a gate or threshold, edit or re-record an existing golden, or rewrite a test to make it pass. A STOP rule in the plan or in your task note means stop and report status failed with the evidence. Sim test runs (T1 in plan A §4: dotnet test, then python ST/trx_summary.py) gate on failed=0 with plan A's flake rule (the LLM generation-timeout tests flake under load: rerun them alone).
8. Evidence: name and register curated evidence per EXECUTION §2.3 with python KIT/evidence.py add (working output stays where the plan puts it; check (b) images are never committed, only listed). The main session sends images to Alec; name in your report any image your task note says is for him.
9. Return a structured report: status (done | failed | blocked), every acceptance item with its command, real output (trimmed to the decisive lines) and pass true/false, every file created or changed (full paths: the main session commits exactly these), evidence paths, deviations from the plan with reasons, and decisions only Alec can make (usually none).`

const REPORT = {
  type: 'object',
  properties: {
    task: { type: 'string' },
    status: { type: 'string', enum: ['done', 'failed', 'blocked'] },
    acceptance: { type: 'array', items: { type: 'object', properties: { item: { type: 'string' }, command: { type: 'string' }, output: { type: 'string' }, pass: { type: 'boolean' } }, required: ['item', 'pass'] } },
    files_changed: { type: 'array', items: { type: 'string' } },
    evidence: { type: 'array', items: { type: 'string' } },
    deviations: { type: 'array', items: { type: 'string' } },
    decisions_for_alec: { type: 'array', items: { type: 'string' } },
    notes: { type: 'string', description: 'what was done and anything the next task must know, under 15 lines' },
  },
  required: ['task', 'status', 'acceptance', 'files_changed', 'notes'],
}
const VERDICT = {
  type: 'object',
  properties: {
    verdict: { type: 'string', enum: ['pass', 'fail'] },
    problems: { type: 'array', items: { type: 'object', properties: { severity: { type: 'string', enum: ['blocker', 'major', 'minor'] }, detail: { type: 'string' }, evidence: { type: 'string' }, fix: { type: 'string' } }, required: ['severity', 'detail', 'fix'] } },
    rechecked: { type: 'array', items: { type: 'string' }, description: 'commands you re-ran or artefacts and files you inspected, each with its result' },
    summary: { type: 'string' },
  },
  required: ['verdict', 'problems', 'rechecked', 'summary'],
}

const LENSES = [
  { key: 'verify', text: `YOUR ROLE: independent verifier of the RESULTS (you do not edit files). 1. Read the task's acceptance criteria in the spec, the EXECUTION.md overrides that apply and the task note below. 2. Re-run yourself every acceptance command that needs no lock, except a build of a project another running task builds now (rule 6): then inspect its build log instead. For locked items, inspect the artefacts and logs they produced (timestamps from this task, exit codes, sentinel lines, counts you compute yourself with Python, image sizes; open every image an acceptance item depends on); re-run one under the lock, with the waiting rule, only if the evidence is missing, stale or contradicts the claim. 3. Confirm each acceptance item is the one the plan states, not a substitute: a changed command, threshold or count is a problem unless EXECUTION or the task note allows it. Verdict pass only if every acceptance item truly passes and no blocker or major problem remains. List every problem with a severity and a concrete fix; do not filter.` },
  { key: 'review', text: `YOUR ROLE: independent code and rules reviewer (you do not edit files, and you run no builds, tests or Unreal jobs: read files, git diff/status/log, grep and small read-only Python only). Read every change this task made (git -C <repo> diff and every new file in its files list; other tasks' changes share the trees: judge only this task's) and judge: (1) correctness bugs, each with a concrete failure scenario; (2) whether the code does what the plan's design sections for this task say (contracts, order of operations, file:line mirrors, the exit-code convention of EXECUTION §2.2, FParse without separators, LF and no BOM where required, #nullable enable and partial Node classes in G/src); (3) the Spec non-negotiables: the sim stays deterministic and knows nothing about Unreal or its host; one way in for orders; looks never write sim state; text first, no Blueprints; (4) gate integrity: no weakened threshold, no edited or re-recorded existing golden, no test rewritten to pass, no acceptance item silently replaced, no hard-coded expected value that makes a check vacuous; (5) scope and hygiene: edits outside this task's paths, leftover debug code, secrets (run python KIT/secret_scan.py on new config or mirrored folders). Verdict pass only if no blocker or major problem remains. List every problem with a severity and a concrete fix; do not filter.` },
]

function implPrompt(t) {
  return `${COMMON}\n\nYOUR TASK: ${t.id} — ${t.title}. Spec: ${t.where}.\nTASK NOTE: ${t.extra || 'none'}\nReturn the report. ${SO}`
}
function verifyPrompt(t, rep, lens) {
  return `${COMMON}\n\n${lens.text}\nTask: ${t.id} — ${t.title} (spec: ${t.where}).\nTASK NOTE GIVEN TO THE IMPLEMENTER: ${t.extra || 'none'}\nThe implementer's report is below: treat it as claims, not facts.\n\nIMPLEMENTER REPORT:\n${JSON.stringify(rep, null, 1)}\n${SO}`
}
function fixPrompt(t, rep, v) {
  return `${COMMON}\n\nYOUR TASK: finish task ${t.id} — ${t.title} (spec: ${t.where}). A previous implementer reported the result below and independent verifiers FAILED it (one re-checked the results, one reviewed the code and the rules). Fix every blocker and major problem (and the minor ones that are cheap), re-run all acceptance commands, and return a complete fresh report (files_changed must list every file the task changed, including the previous implementer's).\nTASK NOTE: ${t.extra || 'none'}\n\nPREVIOUS REPORT:\n${JSON.stringify(rep, null, 1)}\n\nVERIFIER VERDICTS:\n${JSON.stringify(v.lenses, null, 1)}\n${SO}`
}

const results = {}
const ok = (...ids) => ids.every(i => results[i] && results[i].status === 'pass')
function skip(id, why) { results[id] = { status: 'skipped', why }; log(`${id}: skipped (${why})`) }
function skipAll(ids, why) { ids.forEach(i => skip(i, why)) }

function brief(id) {
  const r = results[id]
  if (!r) return { task: id, status: 'missing' }
  if (!r.report) return Object.assign({ task: id }, r)
  const rep = r.report, v = r.verdict
  return {
    task: id, status: r.status, impl_status: rep.status,
    notes: (rep.notes || '').slice(0, 1500),
    failing: (rep.acceptance || []).filter(a => !a.pass).map(a => a.item + ' :: ' + (a.output || '').slice(0, 300)),
    files: rep.files_changed || [], evidence: rep.evidence || [], deviations: rep.deviations || [], decisions: rep.decisions_for_alec || [],
    verifier: v ? { verdict: v.verdict, summary: (v.summary || '').slice(0, 1200), problems: (v.problems || []).filter(p => p.severity !== 'minor').map(p => p.severity + ': ' + (p.detail || '').slice(0, 300)) } : null,
  }
}

async function mark(id) {
  const body = JSON.stringify(brief(id), null, 1)
  await agent(`Write one file and do nothing else. Use the Write tool to create the file ${MARK}/${id}.json whose content is exactly the text between the line BEGIN-JSON and the line END-JSON below (copy it verbatim: no reformatting, no added text, no code fences). Then reply with the single word: written\nBEGIN-JSON\n${body}\nEND-JSON`, { label: `${id}:mark`, phase: 'Markers', model: 'haiku' })
}

async function verifyBoth(t, rep, round) {
  const sfx = round ? String(round + 1) : ''
  const run = l => agent(verifyPrompt(t, rep, l), { label: `${t.id}:${l.key}${sfx}`, phase: t.phase, schema: VERDICT, model: 'opus', effort: 'xhigh' })
  let vs = await parallel(LENSES.map(l => () => run(l)))
  vs = await parallel(LENSES.map((l, i) => () => vs[i] ? Promise.resolve(vs[i]) : run(l)))
  const lenses = LENSES.map((l, i) => vs[i] ? Object.assign({ lens: l.key }, vs[i]) : { lens: l.key, verdict: 'fail', problems: [{ severity: 'blocker', detail: 'verifier agent returned nothing', fix: 're-verify' }], rechecked: [], summary: 'no verdict' })
  const pass = lenses.every(v => v.verdict === 'pass')
  return {
    verdict: pass ? 'pass' : 'fail', lenses,
    problems: lenses.flatMap(v => (v.problems || []).map(p => Object.assign({}, p, { detail: `[${v.lens}] ${p.detail}` }))),
    summary: lenses.map(v => `${v.lens}: ${v.verdict} — ${v.summary}`).join(' | '),
  }
}

async function runTask(t) {
  log(`${t.id}: start`)
  let rep = await agent(implPrompt(t), { label: `${t.id}:impl`, phase: t.phase, schema: REPORT, model: t.model, effort: t.effort })
  if (!rep) { results[t.id] = { status: 'agent-error' }; log(`${t.id}: implementer error`); await mark(t.id); return results[t.id] }
  let v = null
  for (let round = 0; round < 3; round++) {
    if (rep.status === 'blocked') break
    v = await verifyBoth(t, rep, round)
    if (v.verdict === 'pass' || round === 2) break
    const fixed = await agent(fixPrompt(t, rep, v), { label: `${t.id}:fix${round + 1}`, phase: t.phase, schema: REPORT, model: 'opus', effort: 'medium' })
    if (!fixed) break
    rep = fixed
  }
  const status = rep.status === 'blocked' ? 'blocked' : (v && v.verdict === 'pass' ? 'pass' : 'fail')
  results[t.id] = { status, report: rep, verdict: v }
  log(`${t.id}: ${status}${v ? ' — ' + v.summary.slice(0, 200) : ''}`)
  await mark(t.id)
  return results[t.id]
}

const T = U + '/ChimeraTerrain'
const REFS = T + '/Out/refs/manor_lords'
const LOOK = T + '/Out/look'

const GOAL = `ALEC'S REQUEST (2026-10-01, after seeing c-C7-composite): "Definitely get the ground closer to Manor Lords level." Phase 2 is complete and committed (R 6031381d, U 01306d0); C7 built M_ChimeraGround (Scripts/ChimeraGround.hlsl, Scripts/make_ground_material.py, Tools/run_commandlet.ps1, Tools/composite.py; CC0 Poly Haven grass_ground, brown_mud_02, rocks_ground_05, snow_02 via Tools/fetch_textures.py). Its result reads dry, flat and straw-coloured, not like Manor Lords' meadows. Manor Lords is the world look Alec chose (near-photoreal, UE5, Lumen-free look recipe in P/LookTest: the A_noLumen volume, plan C §3.5 lighting). Constraints: plan C §3.5 and §7 still hold (no grass or foliage scatter in this pass, no RVT unless risk 5 triggers, no Lumen, text-first: the material stays generated by make_ground_material.py from ChimeraGround.hlsl, and textures stay CC0 with their manifest). Every C4-C8 bar must still pass: S1 bars (hashes equal C4's s1_a: the material must not touch sim or terrain data), the paint bar (paint vs sculpt changed_frac >= 0.30 in the paint footprint), P7 and P11. terrain_gpu_ms (C1 visible phases, measured as C7 did) must stay <= 3.0 ms, the Phase 4 bar of D2 (Alec's); if the look truly needs more, say so with both numbers, never exceed it silently. Work in ChimeraTerrain and its mirror R/tools/unreal-terrain (run Tools/sync_to_repo.sh at the end; secret_scan passes).`

const JUDGE = {
  type: 'object',
  properties: {
    scores: { type: 'object', properties: {
      hue_saturation: { type: 'number' }, macro_variation: { type: 'number' }, tiling: { type: 'number' }, micro_detail: { type: 'number' },
      layer_integration: { type: 'number' }, lighting_and_atmosphere: { type: 'number' }, overall: { type: 'number' } },
      required: ['hue_saturation', 'macro_variation', 'tiling', 'micro_detail', 'layer_integration', 'lighting_and_atmosphere', 'overall'] },
    gaps: { type: 'array', items: { type: 'object', properties: { gap: { type: 'string' }, evidence: { type: 'string' }, fix: { type: 'string' }, gain: { type: 'string', enum: ['large', 'medium', 'small'] } }, required: ['gap', 'fix', 'gain'] } },
    scatter_gap: { type: 'string', description: 'how much of the remaining distance to Manor Lords is grass/foliage scatter, which is out of scope for this pass' },
    good_enough: { type: 'boolean' },
    summary: { type: 'string' },
  },
  required: ['scores', 'gaps', 'scatter_gap', 'good_enough', 'summary'],
}

const G0 = { id: 'G0', phase: 'Look', model: 'sonnet', effort: 'medium', title: 'Manor Lords ground references and CC0 texture candidates', where: `${PLAN.C} §3.5 (ground material) and §4 C6-C7; Tools/fetch_textures.py`,
  extra: `${GOAL}\nYOUR PART (no lock, no Unreal): (1) Download 8-12 Manor Lords screenshots that show open ground at RTS or near-RTS camera distance (Steam store page of app 1363080 and its official press or media images; record each URL) into ${REFS}/ (git-ignored: ChimeraTerrain/Out/ is ignored in U; never copy these into R or commit them; they are third-party pixels). (2) For each, mark ground-only crops (grass meadow, dry grass, dirt road or path, field or mud, rock if any) by looking at the image yourself, and measure each crop's mean and spread of hue, saturation and value (HSV), luminance histogram, and the scale of colour patches (dominant blob size in screen % via a blurred-luma autocorrelation or similar). Write ${REFS}/refs.json (url, file, crops with boxes and stats) and a contact sheet ${REFS}/refs_sheet.jpg (crops labelled). Measure our C7 rts80 and closeup shots (T/Out/s1_mat or the C7 composite sources in EV/c/c-C7-composite-sources.json) the same way and put the comparison table in ${REFS}/compare_c7.md. (3) Propose CC0 ground texture sets that would close the gap (Poly Haven via its API, ambientCG; licence must be CC0): at least 3 candidates per layer (meadow grass, dry or patchy grass, dirt path or mud, rock, and a 'forest floor or meadow variation' layer), each with id, URL, licence, resolution, maps available and why it fits; check each id resolves with the API. Write ${REFS}/candidates.json. Do not change fetch_textures.py or any T/ source. Report the gap in plain words (which way and how far hue, saturation, value and patch scale differ).` }

const G1 = { id: 'G1', phase: 'Look', model: 'opus', effort: 'medium', title: 'Ground look pass toward Manor Lords', where: `${PLAN.C} §3.5, §4 C7, §5 V1-V6; risks 5 and 7`,
  extra: `${GOAL}\nINPUTS: G0's references and measurements in ${REFS} (refs.json, refs_sheet.jpg, compare_c7.md, candidates.json). Read them and look at the reference images yourself.\nYOUR PART: close the gap at the rts80 view first (that is what a player sees), then closeup and oblique. Levers, in order of expected gain: the texture set (swap layers to G0's candidates via fetch_textures.py, which keeps the manifest, md5s and CC0 record; update the layer ids in one place); albedo hue, saturation and value per layer and macro colour variation at Manor Lords' patch scale (two or three noise octaves, world-space, no visible repeat at rts80); distance-blended tiling and a detail normal so near and far both hold; height-blend contrast of path and rock edges; rock and snow tint so they sit in the scene; then lighting and atmosphere of the ChimeraTerrain game mode only (sun angle and colour, sky light, fog, exposure, post) within the A_noLumen recipe's style (Lumen off). Keep every texture fetch inside the existing weight branches (risk 5) and watch the GPU cost after each change.\nLOOP: build with run_commandlet (lock tag c/G1, background job and the waiting rule), one warm-up S1 after each material change, then an S1 run (tag look_r<n>) and a C1 run for terrain_gpu_ms. Write a reference composite with Tools/composite.py extended: row 1 two Manor Lords references (from ${REFS}) beside our rts80 'look full' shot, row 2 our closeup and oblique, numbers burned in (GPU ms, paint bar, round). Put it at ${LOOK}/look_r<n>.jpg and a phone copy at ${LOOK}/look_r<n>-phone.jpg (<= 1600 px wide, <= 1 MB). These composites hold third-party pixels: never register them in EV or copy them into R. Also write an ours-only composite EV/c/c-G1-ground-r<n>.jpg via KIT/evidence.py add (committed). Report the round number, the composite paths and the measured numbers (terrain_gpu_ms, paint bar, S1 bars, hashes vs s1_a, HSV of our ground crops vs G0's table).
G0 NOTE (main session): G0's reviewer found one flaw that was not fixed: compare_c7.md's rts80 painted-path 'core' mask is wrong (measure3.footprint_mask runs distance_transform_edt on the raw footprints.json paint pixel list without closing its sampling gaps), so our path colour and the Dirt-slot calibration numbers in compare_c7.md are unreliable. Re-measure our path yourself with a closed mask before tuning the dirt layer. Every other G0 measurement (grass hue 34 vs 62 deg, the warm C7 light, value, spread, grain vs macro scale, rock) was re-verified independently.` }

function judgePrompt(rep, round) {
  return `${COMMON}\n\n${GOAL}\nYOUR ROLE: art director and independent judge (you do not edit files and run no Unreal jobs). Round ${round}. Look at the newest reference composite named in the report below (open it and the full-size shots it was made from), at G0's references in ${REFS} (refs_sheet.jpg and at least four full images) and at G0's measurements (refs.json, compare_c7.md), and judge how close our ground is to Manor Lords' ground at the same kind of view. Score each criterion 0-10 (10 = indistinguishable in a side-by-side at phone size): hue_saturation, macro_variation, tiling (visible repeats), micro_detail, layer_integration (paths, rock, snow sitting in the scene), lighting_and_atmosphere, overall. Count only what a ground material, CC0 textures and the game mode's lighting can achieve: grass and foliage scatter are out of scope, so say separately in scatter_gap how much of the remaining distance they account for. List every remaining gap with evidence, a concrete fix and its expected gain, largest first; do not filter. good_enough = overall >= 7 and no criterion below 6, given no scatter. Be strict: a ground that reads as a different biome or as a flat colour is a 3 at most.\n\nIMPLEMENTER REPORT:\n${JSON.stringify(rep, null, 1)}\n${SO}`
}
function refinePrompt(rep, j, round) {
  return `${COMMON}\n\nYOUR TASK: continue the ground look pass (task G1, round ${round + 1}). The art director's verdict on round ${round} is below: take its gaps largest-gain first, change the levers, rebuild, re-run S1 and C1, and write look_r${round + 1}.jpg and its phone copy, plus EV/c/c-G1-ground-r${round + 1}.jpg, exactly as round ${round} did. Keep every constraint of the task note. Return a complete fresh report.\nTASK NOTE: ${G1.extra}\n\nPREVIOUS REPORT:\n${JSON.stringify(rep, null, 1)}\n\nART DIRECTOR VERDICT:\n${JSON.stringify(j, null, 1)}\n${SO}`
}

async function markRound(round, rep, j) {
  const body = JSON.stringify({ task: 'G1', round, scores: j ? j.scores : null, good_enough: j ? j.good_enough : null, judge_summary: j ? (j.summary || '').slice(0, 1200) : null,
    top_gaps: j ? (j.gaps || []).slice(0, 5).map(g => g.gain + ': ' + (g.gap || '').slice(0, 200)) : [], scatter_gap: j ? (j.scatter_gap || '').slice(0, 400) : null,
    impl_status: rep.status, notes: (rep.notes || '').slice(0, 1200), evidence: rep.evidence || [], files: rep.files_changed || [] }, null, 1)
  await agent(`Write one file and do nothing else. Use the Write tool to create the file ${MARK}/G1-r${round}.json whose content is exactly the text between the line BEGIN-JSON and the line END-JSON below (copy it verbatim: no reformatting, no added text, no code fences). Then reply with the single word: written\nBEGIN-JSON\n${body}\nEND-JSON`, { label: `G1-r${round}:mark`, phase: 'Markers', model: 'haiku' })
}

await runTask(G0)
if (!(results.G0 && results.G0.report && results.G0.report.status === 'done')) { log('G0 produced no references; stopping') }
else {
  log('G1: start')
  let rep = await agent(implPrompt(G1), { label: 'G1:impl', phase: 'Look', schema: REPORT, model: G1.model, effort: G1.effort })
  let judged = null
  for (let round = 0; rep && round < 4; round++) {
    judged = await agent(judgePrompt(rep, round), { label: `G1:judge${round}`, phase: 'Look', schema: JUDGE, model: 'opus', effort: 'xhigh' })
    await markRound(round, rep, judged)
    if (!judged || judged.good_enough || round === 3) break
    const next = await agent(refinePrompt(rep, judged, round), { label: `G1:refine${round + 1}`, phase: 'Look', schema: REPORT, model: 'opus', effort: 'medium' })
    if (!next) break
    rep = next
  }
  if (!rep) { results.G1 = { status: 'agent-error' } }
  else {
    // Technical verification of the final round, with up to two fix rounds (as in runTask).
    let v = null
    for (let round = 0; round < 3; round++) {
      if (rep.status === 'blocked') break
      v = await verifyBoth(G1, rep, round)
      if (v.verdict === 'pass' || round === 2) break
      const fixed = await agent(fixPrompt(G1, rep, v), { label: `G1:fix${round + 1}`, phase: 'Look', schema: REPORT, model: 'opus', effort: 'medium' })
      if (!fixed) break
      rep = fixed
    }
    const status = rep.status === 'blocked' ? 'blocked' : (v && v.verdict === 'pass' ? 'pass' : 'fail')
    results.G1 = { status, report: rep, verdict: v, judge: judged }
    log(`G1: ${status}; judge overall ${judged && judged.scores ? judged.scores.overall : '?'}`)
  }
  await mark('G1')
}

const out = {}
for (const id of Object.keys(results)) out[id] = Object.assign(brief(id), results[id] && results[id].judge ? { judge: results[id].judge } : {})
return out
