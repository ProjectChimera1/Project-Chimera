export const meta = {
  name: 'unreal-trial-phase3-b',
  description: 'Unreal trial Phase 3, check b: HUD convergence T4b -> T5 -> T6 -> T7 -> T8, each verified by two Opus xhigh lenses',
  phases: [
    { title: 'Check B', detail: 'T4b top strip; T5 toast/world/minimap/tab; T6 selection and card; T7 visual tail; T8 full board' },
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

// Phase 3, lane B (check b): HUD convergence T4b -> T5 -> T6 -> T7 -> T8, serial. Runs beside phase3-ac.js (A11; C9 -> C11 -> S7).
const BRULES = `CHECK (b) RULES FOR EVERY TASK: plan B §5 and §5.6 hold: never edit thresholds.json, regions.json, calibration.json or the references; no per-element text nudges (one per-style Y nudge of at most 2 px with its measured reason is allowed, §2.3); no images outside the allow-list; bakes are generated from code or our own SVGs, never cropped from a reference. Use T/hud_iterate.sh (written in T4a) for every iteration: one lock hold per build plus shots (tag b/<task>; inner calls are re-entrant, about 4-5 min), comparisons and code edits outside the lock. Read the worst crops of failing regions yourself before each fix; record every iteration in convergence.csv. A gate that cannot pass for a reason that is not in your code (a reference, mask or threshold defect) is a STOP: report status failed with the per-metric values and the crops, and say what the main session must decide; never loosen anything. The other lanes run Unreal jobs under the same lock; be economical with holds. Paths: T = R/tools/unreal-hud, H = U/ChimeraHud. Every task ends with the H -> R mirror (tools/unreal-hud/ChimeraHud-src) through secret_scan.py.`

const TASKS3B = {
  T4b: { id: 'T4b', phase: 'Check B', model: 'opus', effort: 'medium', title: 'Top-strip convergence', where: `${PLAN.B} §2, §5 and §6 T4b; T4a's checkpoint ${MARK}/T4a.json`,
    extra: `${STATE3}\n${BRULES}\nAccept: bash T/hud_iterate.sh t4 --only '^top\\.' gives PAIR A: RESULT PASS 0/8 (and report pair B's line). This is an R and U checkpoint (HUD: prefix; the main session commits).` },
  T5: { id: 'T5', phase: 'Check B', model: 'sonnet', effort: 'medium', title: 'Toast, world overlays, minimap panel, group tab', where: `${PLAN.B} §2.6 and §6 T5`,
    extra: `${STATE3}\n${BRULES}\nFiles add Ui/ChimeraBakes.* (shadow outer/inset, radial, ring set, ringed node) and the minimap layer order of §2.6. Accept: bash T/hud_iterate.sh t5 --only '^(toast|world\\.|mm\\.|tab\\.)' --gates G1,G2,G3,G5,G6 gives RESULT PASS on A and B (G4 reported; bakes are tuned in T7). Earlier regions (top.*) must still pass: run the t4 filter once at the end and report it.` },
  T6: { id: 'T6', phase: 'Check B', model: 'sonnet', effort: 'medium', title: 'Selection panel and command card', where: `${PLAN.B} §2.4 and §6 T6`,
    extra: `${STATE3}\n${BRULES}\nFiles add the vat-glow radial and dashed-border bakes and the locked-button pre-compositing of §2.4. Accept: bash T/hud_iterate.sh t6 --only '^(sel\\.|card\\.)' --gates G1,G2,G3,G5 gives RESULT PASS on A and B, including all 12 slot states. Earlier regions (top.*, and T5's filter with its gates) must still pass: run them once at the end and report them. This is an R and U checkpoint.` },
  T7: { id: 'T7', phase: 'Check B', model: 'opus', effort: 'medium', title: 'The hard visual tail: ornaments, shadows, radial, ring, dashes', where: `${PLAN.B} §2 and §6 T7`,
    extra: `${STATE3}\n${BRULES}\nFiles: Panels/SChimeraOrnamentFrame, bake parameters, laser keyframe maths. Accept: bash T/hud_iterate.sh t7 --only '^(orn\\.|mm\\.plate|mm\\.map|sel\\.portrait|card\\.V|world\\.ring)' gives RESULT PASS (all gates) on A and B. Earlier tasks' filters must still pass: run them once at the end and report them.` },
  T8: { id: 'T8', phase: 'Check B', model: 'opus', effort: 'medium', title: 'Full-board convergence, UI-scale shot, HUD cost', where: `${PLAN.B} §5 and §6 T8; EXECUTION §1.2 (Phase 3 and 4 rows)`,
    extra: `${STATE3}\n${BRULES}\nAccept: bash T/hud_iterate.sh final gives PAIR A: RESULT PASS 0/38, PAIR B: RESULT PASS 0/38 and DETERMINISM OK, with convergence.csv showing the path. In the last hold: bash T/hud_shot.sh --backdrop '#14161A' --extra -HudUiScale=0.8 --tag uiscale80 prints SHOT OK ... scale=0.800 (ungated; look at it yourself: panels stay edge-anchored with no overlap or clipping). HUD COST: EXECUTION §1.1 makes hud_cost a Measure-class job that runs in Phase 4 under LOCK --measure with nothing else dispatched; other lanes are running now, so write and dry-run T/hud_cost.ps1 (a single short -HudOff/-on smoke under the normal lock is fine, reported as unmeasured) and leave the measured 3+3 reps to Phase 4; say so in deviations. Make the scorecard previews of plan B (crops and the full board, check (b) images are never committed, only listed) and name them in your report: they are for Alec. This is an R and U checkpoint.` },
}

const laneB = (async () => {
  for (const id of ['T4b', 'T5', 'T6', 'T7', 'T8']) {
    await runTask(TASKS3B[id])
    if (!ok(id)) {
      const rest = ['T4b', 'T5', 'T6', 'T7', 'T8']
      skipAll(rest.slice(rest.indexOf(id) + 1), `${id} not passed`)
      return
    }
  }
})()
await laneB

const out = {}
for (const id of Object.keys(results)) out[id] = brief(id)
return out
