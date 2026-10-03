export const meta = {
  name: 'unreal-trial-phase3-ac',
  description: 'Unreal trial Phase 3, checks a and c: A11 renderer with reviewer visual check, beside C9 units and video -> C11 packaged builds -> S7 scatter packaged runs',
  phases: [
    { title: 'Check A', detail: 'A11 renderer, shots, verify; separate reviewer fills visual_check.json' },
    { title: 'Check C', detail: 'C9 unit layer and sculpt video; C11 Development and Shipping packages; S7 SX16' },
    { title: 'Markers', detail: 'one checkpoint file per finished task' },
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


async function markRound(name, rep, j) {
  const body = JSON.stringify({ task: name, scores: j ? j.scores : null, good_enough: j ? j.good_enough : null, judge_summary: j ? (j.summary || '').slice(0, 1200) : null,
    top_gaps: j ? (j.gaps || []).slice(0, 6).map(g => g.gain + ': ' + (g.gap || '').slice(0, 220)) : [], impl_status: rep.status, notes: (rep.notes || '').slice(0, 1200), evidence: rep.evidence || [], files: rep.files_changed || [] }, null, 1)
  await agent(`Write one file and do nothing else. Use the Write tool to create the file ${MARK}/${name}.json whose content is exactly the text between the line BEGIN-JSON and the line END-JSON below (copy it verbatim: no reformatting, no added text, no code fences). Then reply with the single word: written
BEGIN-JSON
${body}
END-JSON`, { label: `${name}:mark`, phase: 'Markers', model: 'haiku' })
}
const SPLAN = DOCS + '/plan-c-scatter.md'
const R9 = DOCS + '/research/r9-free-asset-routes.md'
const STATE3 = `PHASE 3 (CONVERGE) STATE, 2026-10-03 (supersedes the Phase 2 state above wherever they differ): Phases 0-2 are complete, verified and committed (EXECUTION §7, §8). Check (a): A3-A8b, A10 and A14 passed; A10's four Unreal runs give 1440/1440 against the A4 goldens; the renderer is A11's (next). Check (b): T2 calibration frozen (as T2b after the §7 ruling: positives P1, P3, P4, P5, P6 = 5/5, negatives 8/8), T3 capture exact, T4a chose its routes (hinting None, so no recalibration ran); D10 answered by Alec: Slate's lighter text weight is accepted, no coverage compensation, gates unchanged. Check (c): C4, C5, C7, C8, C10 passed; the ground look pass is committed (art director 5.5/10, terrain_gpu_ms 2.907 ms); runtime scatter S0-S6 is committed (plan ${SPLAN}; every SX gate passes; shipped defaults L0, grass ends 70/45 m, config_fnv 0x2e5eca117dd40fb2). READ EXECUTION §8 IN FULL before you start, especially 'S4 rulings' (R1: image bars that would compare against s1_a use s1_s4a via parse_terrain.py --img-ref; hashes and saved files stay on s1_a; this applies to C11's pkg_s1l and to S7), 'S5 record' (frozen image pairs via temporal_freeze, compared only within one freeze window; xf_<cp> shot names) and 'S6 ruling'/'S6 closed'. Checkpoint notes of finished tasks: ${MARK}/<task>.json. Phase 3's row is EXECUTION §1.2 '3 Converge' (lock queue: B T4b → T5 → T6 → T7 → T8 interleaved with A11 runs and C9; C11 packaging steps slotted while B fixes code). No Phase 3 task gates a timing number (EXECUTION §3 C9); timings are reported. Anything visual a task note marks 'for Alec' is sent by the main session: name the file in your report.`

// Phase 3, lanes A and C: A11 (renderer, shots, verify, reviewer's visual_check) beside C9 -> C11 -> S7. Runs beside phase3-b.js (HUD T4b-T8).
const TASKS3 = {
  A11: { id: 'A11', phase: 'Check A', model: 'opus', effort: 'medium', title: 'Renderer, cameras, shots, verify', where: `${PLAN.A} §3.7 (rendering, facing, buildings, shots, hidden frames, verify) and §4 A11; EXECUTION §2.2; A10's checkpoint ${MARK}/A10.json`,
    extra: `${STATE3}\nLock tag a/A11, one build or run per hold, background jobs with the waiting rule; one discarded warm-up run first. Accept per plan A §4 A11: verify.json at ticks 0/60/300/900/1440 with visible == alive and max_err_cm <= 0.5; check_shots.py passes per army at every shot (incl. mirroring); 7 PNGs at 1920x1080 > 300 KB; no new log warnings after the tick-900 mass deaths (zero-scale check; the under-ground fallback of §3.7 if artefacts show); a rendered ai run (no shots) with verify at the 4 ticks gives visible == alive including AI-created ids; traces still 1440/1440 on every run. PROOF/visual_check.json is NOT yours: a separate reviewer agent fills it after your report, so leave it absent. Make the contact sheet (KIT/evidence.py sheet of the 7 shots; register it as EV/a/a-A11-contact-sheet.jpg with a phone copy): it is for Alec with the verify numbers. A12 (Phase 4 measurement) is not part of this task. Check (a)'s paths only: P/, U/TrialOut/a, EV/a, tools/sim-trial and the tools/unreal-sim mirror (secret_scan).` },
  C9: { id: 'C9', phase: 'Check C', model: 'sonnet', effort: 'high', title: 'Unit layer and sculpt video', where: `${PLAN.C} §4 C9 as changed by ${SPLAN} §3.10 (C9 bullet); EXECUTION §3 C20`,
    extra: `${STATE3}\nPlan-c-scatter §3.10 overrides plan C here: units subscribe to FOnTerrainChanged (S4a) and ride HF.SampleSurface (the drawn triangulated surface), not a new bilinear sampler; C9 needs only S4a, never the scatter renderer. Copy crucible_mortar from ProjectChimera/Content/LookTest/Roster/crucible_mortar inside a short LOCK hold (EXECUTION §3 C20). Lock tag c/C9, one build or run per hold. Accept: -Script VIDEO -Tag video -FixedFps 30 -Extra "-ChimeraTerrainUnits=300" then bash T/Tools/make_video.sh T/Out/video; ffprobe duration 10-15 s; GIF <= 8 MB; one -Script C1U -Tag c1u_smoke exits 0 (C1U stays scatter-off) and, now that C9 lands, one unmeasured -Script C1US -Tag c1us_smoke smoke exits 0 (reported; C1US belongs to S8's campaign). The dormant gates must hold with units off (S1 --equal-hashes s1_a, --s1). Register EV/c/c-C9-sculpt.gif (and the mp4 if it fits check (c)'s 12 MB cap; otherwise list it): the GIF is for Alec. Look at the video yourself: units must sit on the surface through the strokes.` },
  C11: { id: 'C11', phase: 'Check C', model: 'sonnet', effort: 'high', title: 'Gate G2: packaged builds (Development and Shipping)', where: `${PLAN.C} §4 C11 as changed by ${SPLAN} §3.10 (C11 bullet); EXECUTION §8 'S4 rulings' R1`,
    extra: `${STATE3}\nScatter S5 and S6 have passed, so the cook carries scatter (Content/Terrain/Scatter) and S7 runs next inside C11's window on these packages (plan-c-scatter §3.10). C11's own runs are scripted, so scatter is off in them by default. If a scatter-carrying cook or packaged run fails for a scatter cause, follow §3.10: delete Content/Terrain/Scatter (reproducible by S3), re-cook, run your gated bars, and report the scatter cause (S7 then records SX16 FAIL). Lock tag c/C11-<step>: each package step (build, cook, stage) is its own background hold with the waiting rule (10-40 min each). Accept per plan C §4 C11: Development three steps exit 0 and the exe exists; -Script S1 -Tag pkg_s1 -Packaged exits 0 with hashes equal s1_a (or max_abs_dh <= 1e-4 with the compiler cause), after vs before changed_frac >= 0.10, C7's paint bar, the parser log scan clean (incl. Failed to load); -Script S1L -Tag pkg_s1l -Packaged passes with its image bars against s1_s4a (R1: --img-ref T/Out/s1_s4a; hashes against s1_a); -Script MOUSE -Tag pkg_mouse -Packaged -Windowed -ResX 1600 -ResY 900 -Inject meets P10 (preflight first; if the desktop is locked or injection is blocked, report it with D9 in decisions_for_alec, never a waiver). Shipping: package, then -Script S1 -Tag ship_s1 -Packaged -Shipping: results.json hashes equal s1_a and after.png exists. Packaged trees stay git-ignored (T/Packaged). Note in your report the commit each package was built from (git -C U rev-parse HEAD plus any uncommitted check (c) paths): S7's reference run must come from the same tree.` },
  S7: { id: 'S7', phase: 'Check C', model: 'sonnet', effort: 'high', title: 'Scatter packaged runs (SX16)', where: `${SPLAN} §4 S7, §5 SX16 and §3.10; EXECUTION §8 'S4 rulings', 'S5 record', 'S6 closed'`,
    extra: `${STATE3}\nC11 has just packaged Development and Shipping with scatter in the cook; use those packages (do not re-package unless C11's report says scatter was removed from the cook, in which case report SX16 FAIL with the cause and stop). First -Script S1X -Tag s1x_final -Extra "-ChimeraTerrainScatter=1" in -game from the same tree the packages were built from (the shipped defaults; the reference). Then the five packaged runs of plan-c-scatter §4 S7 (pkg_s1x, pkg_s1xl, ship_s1x, ship_s1xl, pkg_mousex with MOUSE's flags). Image bars follow EXECUTION §8: frozen pairs within one window, xf_<cp> names, s1_s4a where a reference image would be s1_a. Accept: parse_terrain.py --s1x T/Out/pkg_s1x --reload T/Out/pkg_s1xl --ref T/Out/s1_a --xref T/Out/s1x_final --packaged and the Shipping pair with --shipping give SX16 PASS; --scatter T/Out/pkg_mousex gives SX1, SX5, SX8 PASS (desktop rule as C11). Lock tag c/S7, one run per hold, background jobs with the waiting rule.` },
}

const VISUAL = {
  type: 'object',
  properties: {
    checks: { type: 'array', items: { type: 'object', properties: { item: { type: 'string' }, yes: { type: 'boolean' }, evidence: { type: 'string', description: 'which shot(s), where in the frame, what you saw' } }, required: ['item', 'yes', 'evidence'] } },
    all_yes: { type: 'boolean' },
    written: { type: 'string', description: 'the full path of the visual_check.json you wrote' },
    summary: { type: 'string' },
  },
  required: ['checks', 'all_yes', 'written', 'summary'],
}

async function reviewA11(rep, n) {
  return agent(`${COMMON}\n\n${STATE3}\nYOUR ROLE: the separate reviewer agent of plan A §4 A11 who fills PROOF/visual_check.json (EV/a per EXECUTION §2.3; plan A §4 A11 and §5 item 6). You edit nothing else and run no Unreal jobs. Open every one of A11's 7 shots at full size (and the contact sheet) and judge each item yes or no with evidence: (1) two colour-distinct armies at tick 0; (2) the lines meet near x≈0 by tick 300; (3) thinner armies at 900 and 1440; (4) feet on the ground (no floating or sunk units); (5) close-ups face their motion; (6) both CommandCentres visible in the overview; (7) no stretched or rotated meshes. Be strict: a 'yes' needs visible proof in a named shot. Write visual_check.json in the format the plan and check_shots.py expect (read plan A and the existing PROOF files first), register it with KIT/evidence.py add if it is not already registered, and return your verdicts. This is review pass ${n}.\n\nA11 REPORT:\n${JSON.stringify(rep, null, 1)}\n${SO}`, { label: `A11:visual${n}`, phase: 'Check A', schema: VISUAL, model: 'opus', effort: 'xhigh' })
}

async function runA11(t) {
  log('A11: start')
  let rep = await agent(implPrompt(t), { label: 'A11:impl', phase: t.phase, schema: REPORT, model: t.model, effort: t.effort })
  if (!rep) { results.A11 = { status: 'agent-error' }; await mark('A11'); return }
  let vis = null
  for (let n = 1; n <= 3 && rep.status !== 'blocked'; n++) {
    vis = await reviewA11(rep, n)
    if (!vis || vis.all_yes || n === 3) break
    const fixed = await agent(`${COMMON}\n\nYOUR TASK: finish task A11 — ${t.title} (spec: ${t.where}). The separate reviewer answered 'no' to visual checks below. Fix each cause in the renderer (never in the reviewer's file), re-shoot, re-run every acceptance command, and return a complete fresh report (files_changed lists every file A11 changed). Do not edit visual_check.json; the reviewer re-judges after you.\nTASK NOTE: ${t.extra}\n\nPREVIOUS REPORT:\n${JSON.stringify(rep, null, 1)}\n\nREVIEWER VERDICTS:\n${JSON.stringify(vis, null, 1)}\n${SO}`, { label: `A11:visfix${n}`, phase: t.phase, schema: REPORT, model: 'opus', effort: 'medium' })
    if (!fixed) break
    rep = fixed
  }
  let v = null
  if (rep.status !== 'blocked') {
    const tv = Object.assign({}, t, { extra: `${t.extra}\nREVIEWER (separate agent) VISUAL CHECK RESULT: ${vis ? JSON.stringify({ all_yes: vis.all_yes, written: vis.written, checks: vis.checks }) : 'none'}. Every visual check must be 'yes' for A11 to pass (plan A §4 A11).` })
    for (let round = 0; round < 3; round++) {
      v = await verifyBoth(tv, rep, round)
      if (v.verdict === 'pass' || round === 2) break
      const fixed = await agent(fixPrompt(tv, rep, v), { label: `A11:fix${round + 1}`, phase: t.phase, schema: REPORT, model: 'opus', effort: 'medium' })
      if (!fixed) break
      rep = fixed
    }
  }
  const visualOk = !!(vis && vis.all_yes)
  const status = rep.status === 'blocked' ? 'blocked' : (v && v.verdict === 'pass' && visualOk ? 'pass' : 'fail')
  results.A11 = { status, report: rep, verdict: v }
  log(`A11: ${status}; visual ${visualOk ? 'all yes' : 'not all yes'}`)
  await mark('A11')
}

const laneA = runA11(TASKS3.A11)
const laneC = (async () => {
  await runTask(TASKS3.C9)
  // C11's deps are C5, C7 and C8 (all passed); it never waits on C9's result, only on the order of the lock queue.
  await runTask(TASKS3.C11)
  if (!ok('C11')) { skip('S7', 'C11 not passed'); return }
  await runTask(TASKS3.S7)
})()
await Promise.all([laneA, laneC])

const out = {}
for (const id of Object.keys(results)) out[id] = brief(id)
return out
