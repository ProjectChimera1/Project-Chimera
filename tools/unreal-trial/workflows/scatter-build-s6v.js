export const meta = {
  name: 'unreal-trial-scatter-build-s6v',
  description: 'Check c: close S6 - bake the chosen scatter defaults per the main-session ruling, re-verify, art director scores the shipped defaults',
  phases: [
    { title: 'Scatter', detail: 'S4 runtime; S5 harness and determinism; S6 look rounds with an art director' },
    { title: 'Markers', detail: 'one checkpoint file per finished task and per judged look round' },
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
const STATE = `PHASE 3 STATE (supersedes the Phase 2 state above where they differ): Phase 2 is complete and committed. The ground look pass is committed (R 23efc90f, U 1c2f459): round 4 of M_ChimeraGround (CC0 Grass004/Rock030 etc.), terrain_gpu_ms 2.907 ms against the 3.0 ms bar, art director 5.5/10; every C4-C8 bar holds and hashes equal s1_a. Alec approved scatter ("Go with what you think is best. Just make sure it's implemented well!"); the plan of record is ${SPLAN} (bespoke C++ scatter, not PCG; read it in full, especially §0-§3, your task in §4, the bars in §5 and the risks in §6; its rules for every implementer at the top of §4 apply, and its F17 rule is now satisfied: the ground-look final commit is in). Main-session task S0 is done (plan C §7 and §3 pointer, EXECUTION §8 slot, U/.gitignore ChimeraTerrain/ScatterSrc/, blender in KIT/preflight.ps1's busy pattern). Asset routes: ${R9} (free routes per class; CC0 and project-original content only, because Fab Standard content, including every Quixel item and the $0 ones, may be barred from level-editing tools; CC-BY Fab packs are not in Alec's library yet, so do not plan on them; Epic engine samples such as PVE are allowed only in the S6 bake-off as candidates and are marked 'epic' in the manifest, pending Alec's licence decision). DONE AND COMMITTED BEFORE THIS RUN: scatter S0, S1 (meshes and CC0 assets; contact sheet sent to Alec), S4a (terrain change event), S2 (generator, scheduler, 43 tests) and S3 (import and materials); read their checkpoint notes in D:/Projects/Chimera-Unreal/TrialOut/checkpoints/{S1,S2,S3b,S4a}.json for hand-offs (S3b: 35 meshes, 65 materials from 7 masters, asset_settings_sha256 35f87ad8f1941e59fd4c2b78e4859d10017551b55adca5aed94f3d6e4a86b9be, which S6's from-clean check compares; the S3 commandlet runs on a deleted Content/Terrain/Scatter inside one lock hold; rebuilding ScatterSrc needs make_scatter_meshes.py then fetch_scatter_assets.py --refresh-l0 before S3) (S2's notes say how S4 must drive the scheduler; S1's notes say L1 meshes carry no COLOR_0). Paths for check (c): T = ChimeraTerrain, S = T/Source/ChimeraTerrain, RT = R/tools/unreal-terrain (mirror; every task ends with bash T/Tools/sync_to_repo.sh and secret_scan).`

const TASKS = {
  S1: { id: 'S1', phase: 'Scatter', model: 'opus', effort: 'medium', title: 'Scatter meshes and assets outside Unreal', where: `${SPLAN} §3.6 and §4 S1; ${R9} per-class routes`,
    extra: `${STATE}\nNo lock (Python, Blender headless, fetches). Use r9's CC0 picks per class (grass clumps, flowers, ferns, shrubs, trees, rocks) and its candidate ids; keep the triangle budgets of §4 S1. Make a contact sheet (preview_scatter.py) at T/Out/scatter_preview/contact_sheet.jpg plus a phone copy (<= 1600 px wide, <= 1 MB) and register an ours-only copy as EV/c/c-S1-scatter-contact.jpg with KIT/evidence.py add: it is for Alec. Touch no file under T/Source (S2 and S4a run beside you and build the module).` },
  S4a: { id: 'S4a', phase: 'Scatter', model: 'sonnet', effort: 'medium', title: 'Terrain change event', where: `${SPLAN} §3.5 and §4 S4a; r8d integration points`,
    extra: `${STATE}\nLock tag c/S4a with the waiting rule. You run before S2 (both edit T/Source; S2 starts when you finish). S1 runs beside you without touching T/Source.` },
  S2: { id: 'S2', phase: 'Scatter', model: 'sonnet', effort: 'high', title: 'Scatter generator and pure tests', where: `${SPLAN} §3.1-3.5 and §4 S2`,
    extra: `${STATE}\nLock tag c/S2 with the waiting rule. S4a (the terrain change event) is already in. Re-read make_ground_material.py's SCALARS after the ground-look commit before writing any threshold (RockBand moved to 0.10: band edges 23.2 and 44.0 deg; check the current values).` },
  S3: { id: 'S3', phase: 'Scatter', model: 'sonnet', effort: 'high', title: 'Scatter import and materials', where: `${SPLAN} §3.6-3.7 and §4 S3`,
    extra: `${STATE}\nLock tag c/S3; the commandlet is a background job with the waiting rule (warm-up up to 60 min). Inputs are S1's meshes and manifest and S2's Editor build.` },
  S4: { id: 'S4', phase: 'Scatter', model: 'opus', effort: 'medium', title: 'Runtime scatter (actor, renderer, scheduler, apply, ops)', where: `${SPLAN} §3.5, §3.7-3.8 and §4 S4; facts F18-F27`,
    extra: `${STATE}\nLock tag c/S4, one build or run per hold, background jobs with the waiting rule. MOUSE needs an unlocked desktop: if preflight finds it locked, record "deferred to C11 pkg_mouse" as the plan says, never a waiver.` },
  S5: { id: 'S5', phase: 'Scatter', model: 'sonnet', effort: 'high', title: 'Scatter harness and determinism runs', where: `${SPLAN} §3.8 and §4 S5; bars in §5`,
    extra: `${STATE}\nLock tag c/S5 with the waiting rule. Timing rows are reported, not gated, until S8 in Phase 4 (EXECUTION §3 C9).` },
  S6: { id: 'S6', phase: 'Scatter', model: 'opus', effort: 'medium', title: 'Scatter look rounds against Manor Lords', where: `${SPLAN} §4 S6 and §5 L; ${R9} pilot gate; G0 references in T/Out/refs/manor_lords`,
    extra: `${STATE}\nLock tag c/S6. Use r9's pilot gate per slot inside the bake-off: switch a slot to its next free route when it scores below 5.5 at rts80 or 5 at closeup; keep at >= 7 / >= 6; one tune round in between. PVE (Epic engine content) may be judged as a candidate but is not chosen as a shipped default until Alec decides the licence rule: if it wins a slot, report it in decisions_for_alec and keep the best CC0 route as the default. Each round's composite (ours vs Manor Lords, local only, never committed) goes to T/Out/look/lookx_r<n>.jpg and a phone copy lookx_r<n>-phone.jpg, plus an ours-only EV/c/c-S6-scatter-r<n>.jpg: these are for Alec. At most 3 rounds; the art director is a separate agent dispatched by the workflow after your report, so put the round's composite paths and M1-M10 in your report and stop after the round you were asked for.` },
}

// S6 runs as up to 3 rounds: implement -> art director -> next round; then the two-lens technical verify.
async function runLookRounds(t) {
  log(`${t.id}: start`)
  let rep = await agent(implPrompt(t), { label: `${t.id}:impl`, phase: t.phase, schema: REPORT, model: t.model, effort: t.effort })
  let j = null
  for (let round = 1; rep && round <= 3; round++) {
    j = await agent(`${COMMON}\n\n${STATE}\nYOUR ROLE: art director and independent judge of scatter round ${round} (you do not edit files and run no Unreal jobs). Open the round's composite and full-size shots named in the report below, the G0 references in ${T_OUT}/refs/manor_lords (refs_sheet.jpg and at least four full images) and look_measure's M1-M10, and score at rts80, oblique and closeup, scatter on and off paired: hue_saturation, macro_variation, tiling, micro_detail, layer_integration, lighting_and_atmosphere, overall (0-10; 10 = indistinguishable at phone size). Name every remaining gap with evidence, fix and expected gain, largest first, and say per slot (grass, flowers, ferns, shrubs, trees, rocks) whether to keep, tune or switch it under r9's pilot gate. good_enough = rts80 >= 7.5 and oblique >= 7.5 (plan S6's stop rule).\n\nIMPLEMENTER REPORT:\n${JSON.stringify(rep, null, 1)}\n${SO}`, { label: `${t.id}:judge${round}`, phase: t.phase, schema: JUDGE, model: 'opus', effort: 'xhigh' })
    await markRound(`${t.id}-r${round}`, rep, j)
    if (!j || j.good_enough || round === 3) break
    const next = await agent(`${COMMON}\n\nYOUR TASK: scatter look round ${round + 1} (task ${t.id}). Take the art director's verdict below largest-gain first, change palette, slot routes and settings as the plan allows, rerun LOOKX and the S1X/THINX reruns of the round, and write the round's composites. Return a complete fresh report.\nTASK NOTE: ${t.extra}\n\nPREVIOUS REPORT:\n${JSON.stringify(rep, null, 1)}\n\nART DIRECTOR VERDICT:\n${JSON.stringify(j, null, 1)}\n${SO}`, { label: `${t.id}:round${round + 1}`, phase: t.phase, schema: REPORT, model: 'opus', effort: 'medium' })
    if (!next) break
    rep = next
  }
  if (!rep) { results[t.id] = { status: 'agent-error' }; await mark(t.id); return }
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
  results[t.id] = { status, report: rep, verdict: v, judge: j }
  log(`${t.id}: ${status}; art director rts80 overall ${j && j.scores ? j.scores.overall : '?'}`)
  await mark(t.id)
}

const T_OUT = U + '/ChimeraTerrain/Out'

// Second run (2026-10-02): S4's first workflow (wf_2dfdfe39-05c) ended with round 2 'blocked' on two main-session rulings, now made.
const RULINGS = `MAIN-SESSION RULINGS (2026-10-02; recorded in EXECUTION.md §8 'S4 rulings' and in plan-c-scatter.md §3.5, SX8 and §4 S4 item 3; read them): (R1) Image reference after the ground look: s1_a's screenshots predate the C7 and G1 ground-look commits, so every image bar that would compare against s1_a takes its image reference from s1_s4a via parse_terrain.py --img-ref (hashes and saved files stay against s1_a; never re-record s1_a). S4's S1L row is: parse_terrain.py --s1l T/Out/s1l_sx4 --ref T/Out/s1_a --img-ref T/Out/s1_s4a. The same applies to every S5 and S7 image bar that would name s1_a. (R2) SX8's per-component proxy categories are accepted as plan §3.5 now states: apply-dirtied recreates always count in proxy_recreates; refills, PSO-precache and editor-compile recreates are reported; proxy_recreates, proxy_compile_recreates_during_edits and proxy_engine_recreates_during_edits are gated 0. The main session moved S4's two round-1 crash folders to U/TrialOut/c_s4/crashes_round1, so T/Saved/Crashes is empty.`
const S4R2 = U + '/TrialOut/c_s4/r2/s4_r2_report.json'
TASKS.S4.extra += `\n${RULINGS}\nS4 HISTORY: round 1 was implemented and FAILED by both verifiers on two majors (the S1L image reference and the SX8 categories, both now ruled) plus minors; round 2 (a fix agent) fixed the code and the minors and stopped as 'blocked' only on those rulings. Its complete report is the JSON file ${S4R2}: READ IT IN FULL before you start; it lists every acceptance item with its command and output, every file changed and every deviation. Under the rulings its status is no longer blocked; judge it as a claim of done.`
TASKS.S5.extra += `\n${RULINGS}\nFROM S4's VERIFIERS: before each scatter shot and each fresh compare, wait for compile idle (EXECUTION §2.2) and report shader_propagations per phase (editor -game still compiles mid-run); S4 already exercised scatter_fresh, ScatterThreads=0, ScatterApply=clear and Governor=6 once (SXPATHS runs), so treat a failure of those paths here as an S4 defect to fix, not a plan change; parse_terrain.py --scatter now has an apply_fit row and an instance floor. S4's report: ${S4R2}.`
TASKS.S6.extra += `\n${RULINGS}`

// Third run (2026-10-03): S5 round 3 passed every gate (verifier pass) but the reviewer failed it on one reporting major (SX17).
const S5R3 = U + '/TrialOut/c_s5/r3/s5_r3_report.json'
const S5V3 = U + '/TrialOut/c_s5/r3/s5_r3_verdicts.json'
const S5REC = `S5 STATE: S5 round 3 (workflow wf_c7e98308-d2d) passes every gate: the results verifier returned PASS with 4 minors; the code-and-rules reviewer returned FAIL on ONE major (SX17 reporting) plus minors. The full round-3 report is ${S5R3} and both verdicts are ${S5V3}: READ BOTH IN FULL. The main session has recorded S5's deviations in EXECUTION.md §8 'S5 record (main session, 2026-10-03)' and amended plan SX10, SX17, SX18 and the sapling row: frozen image pairs via temporal_freeze, compare frozen frames only within one freeze window, the rock tilt frame, THINX's rock disc d48 s10 x14, saplings to 600 m, LATX spacing, the S1X shot names xf_<cp> / x_<pose>_full. Read that section; do not re-litigate it.`
TASKS.S5.extra += `\n${S5REC}`
TASKS.S6.extra += `\n${S5REC}\nPERFORMANCE STEER FROM S5: at S1X rts80 SX15 reads drawn 34,529 against the 24k cap and LOD0 triangles up to 3.52 M against 1.5 M, and an unmeasured C1S smoke showed GT p99 +2.2 ms / RT p99 +4.3 ms against SX11's 1.5 ms. These are reported, not gated, until S8, but S8's kill rule reads them. In every round report SX15's drawn, primitives and triangle figures at rts80 beside the look scores, and when two choices look about equal, take the one that draws fewer instances and triangles (for example fewer, larger grass clumps).`

// Fourth run (2026-10-03): S5 passed and is committed. S6's three look rounds ended at rts80 6.0; its fix pass stopped 'blocked' on the
// bake ruling, now made (EXECUTION §8 'S6 ruling').
const S6REP = U + '/TrialOut/c_s6/s6_final_report.json'
const S6VER = U + '/TrialOut/c_s6/s6_verdicts.json'
const S6RULE = `MAIN-SESSION S6 RULING (2026-10-03; EXECUTION.md §8 'S6 ruling'; read it in full): bake only the round-3 (R3PF) parameters that keep S2's FieldCoverage in range at the default seed (S6's option 2: TreeEdgeW 0.62, FlowerDriftLo 0.70, BorderBias 0.35, BorderLoM 118 stay at in-range values), plus the 70/45 m grass ends (GrassT0 70 m, GrassT1 45 m), and make ScatterLevel's default L0. The FieldCoverage test and its ranges are NOT edited. The material look overrides of round 3 (scatter_material_spec LOOK_OVERRIDES) are part of the chosen look and stay. Check (c)'s evidence cap is now 12 MB (evidence.py CHECK_CAP; EXECUTION §2.3), and the main session has already committed-registered the three art-director verdicts as EV/c/c-S6-art-director-r1..r3.json. M6 is 'on >= off' with no tolerance (round 3 misses it; reported, never gated).`

const S6BAKE = {
  id: 'S6', phase: 'Scatter', title: 'Scatter look: bake the chosen defaults and close S6',
  where: `${SPLAN} §4 S6 (closing step: 'The chosen defaults become the palette table and ScatterLevel's default'), §3.4, §3.7, §5 L; EXECUTION §8 'S6 ruling'`,
  extra: `${STATE}\n${RULINGS}\n${S5REC}\n${S6RULE}\nS6 HISTORY: S5 is committed. S6 ran three look rounds (art director rts80 6.0 / 6.0 / 6.0; markers ${MARK}/S6-r1.json..S6-r3.json) and a fix pass that fixed every verifier problem (the CRLF include regression, the M6 tolerance, M4/M8 reporting, the bake-off sheet, the S3 comparison) except the bake, which it left 'blocked' on the ruling above. Its complete report is ${S6REP} and the verifiers' verdicts are ${S6VER}: READ BOTH IN FULL before you start.\nYOUR JOB: (1) bake the defaults per the ruling into the palette table (TerrainScatterPalette), the grass cull ends and ScatterLevel's default, so a run with -ChimeraTerrainScatter=1 and NO other scatter option renders the chosen look. If a test pins a value derived from the default palette (a count or hash), update it only as a direct consequence of the bake and list each one in deviations with old and new values; never touch FieldCoverage's ranges. (2) Lock tag c/S6, background jobs with the waiting rule, never edit code while a hold of yours is open: rebuild Editor and Game; Chimera.Terrain tests fail=0 (FieldCoverage passes unedited); the standalone harness; the Python suites. (3) With the shipped defaults only: S1X (tag s1x_def) and --s1x against s1_a (SX1-SX5, SX7-SX9, SX18 PASS); THINX (SX6) and its oracle; SXSMOKE --scatter PASS; dormant S1 --equal-hashes s1_a PASS; one C1S smoke at the defaults with --summary (scatter_gpu_ms, SX15 drawn/primitives/triangles at rts80). (4) LOOKX tag lookx_final with no round options, look_measure.py --scatter, the composite lookx_final.jpg and its phone copy lookx_final-phone.jpg (Manor Lords row, then ours on, then off; third-party pixels: local only, never committed) and an ours-only EV/c/c-S6-scatter-final.jpg (committed). An art director scores lookx_final after your report, so put the composite paths and M1-M10 in it. (5) Register evidence, sync the mirror, secret scan. Return a COMPLETE report: files_changed lists every file S6 changed in all rounds (the 55 in ${S6REP} plus yours).`,
}

async function bakeS6(t) {
  log('S6: bake the chosen defaults, then verify')
  let rep = await agent(implPrompt(t), { label: 'S6:bake', phase: t.phase, schema: REPORT, model: 'opus', effort: 'medium' })
  if (!rep) { results.S6 = { status: 'agent-error' }; await mark('S6'); return }
  let v = null
  for (let round = 0; round < 2; round++) {
    if (rep.status === 'blocked') break
    v = await verifyBoth(t, rep, round + 2)
    if (v.verdict === 'pass' || round === 1) break
    const fixed = await agent(fixPrompt(t, rep, v), { label: `S6:fix${round + 2}`, phase: t.phase, schema: REPORT, model: 'opus', effort: 'medium' })
    if (!fixed) break
    rep = fixed
  }
  const status = rep.status === 'blocked' ? 'blocked' : (v && v.verdict === 'pass' ? 'pass' : 'fail')
  let j = null
  if (status === 'pass') {
    j = await agent(`${COMMON}\n\n${STATE}\n${S6RULE}\nYOUR ROLE: art director and independent judge of S6's SHIPPED DEFAULTS (run lookx_final; you do not edit files and run no Unreal jobs). Open the composite and full-size shots named in the report below, the G0 references in ${T_OUT}/refs/manor_lords (refs_sheet.jpg and at least four full images), the round-3 verdict in ${MARK}/S6-r3.json and EV/c/c-S6-art-director-r3.json, and look_measure's M1-M10, and score at rts80, oblique and closeup, scatter on and off paired: hue_saturation, macro_variation, tiling, micro_detail, layer_integration, lighting_and_atmosphere, overall (0-10; 10 = indistinguishable at phone size). Say how the shipped defaults compare with round 3 and why. Name every remaining gap with evidence, fix and expected gain, largest first. good_enough = rts80 >= 7.5 and oblique >= 7.5. This is reported, not gated (D4).\n\nREPORT:\n${JSON.stringify(rep, null, 1)}\n${SO}`, { label: 'S6:judge-final', phase: t.phase, schema: JUDGE, model: 'opus', effort: 'xhigh' })
    await markRound('S6-final', rep, j)
  }
  results.S6 = { status, report: rep, verdict: v, judge: j }
  log(`S6: ${status}${v ? ' — ' + v.summary.slice(0, 200) : ''}`)
  await mark('S6')
}

await bakeS6(S6BAKE)

const out = {}
for (const id of Object.keys(results)) out[id] = Object.assign(brief(id), results[id] && results[id].judge ? { judge: results[id].judge } : {})
return out
