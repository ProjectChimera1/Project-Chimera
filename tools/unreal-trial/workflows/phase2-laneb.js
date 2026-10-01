export const meta = {
  name: 'unreal-trial-phase2-laneb',
  description: 'Trial Phase 2, check b continued after the main-session ruling on T2: calibration, T4a routes, planned recalibration',
  phases: [
    { title: 'Check B', detail: 'T2b calibration with P6 vector AA model, T4a fonts/text/icon/coverage routes, T2r if the route changes text coverage' },
    { title: 'Markers', detail: 'one checkpoint file per finished task for the main session' },
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

const ESC = U + '/TrialOut/b/t2_escalation.json'

const RULING = `MAIN-SESSION RULING on T2's escalation (2026-10-01; the main session records it in EXECUTION §7). First read the previous rounds' full reports and both verifiers' verdicts in ${ESC}; the working tree holds the last round's state (thresholds sha256 3f4afbdd...).
R1 RATIFIED: the hard G5 heavy-ink gate mass_heavy = +0.05 (hud_compare.HARD['mass_heavy']) is §4.4's prescribed remedy for N3 ("a new hard metric for that defect class"): positives reach at most +0.028, P5's heaviest run -0.0545, N3 +0.115. Document it with these numbers in hud_compare.py and in calibration.json.
R2 RATIFIED: unit_ssim masked to the unit (a mask fix: N5 must not bleed into mm.plate).
R3 RATIFIED: only eligible negatives (those intended for a region) bound that region's thresholds, per §4.4 (negatives fail only in their listed regions).
R4 REPLACED: "no_positive -> report-only" stays only as a last resort. The regions it leaves ungated (card.V, mm.map, mm.plate, orn.minimap, orn.selection, sel.portrait, top.menu, world.ring: the regions of T4b and T7) need a positive that models the allowed rasterizer difference of CSS-drawn vector geometry, as P5 models Slate's text. Add P6 "vector AA model" per pair: the same board rendered by the same Chromium at deviceScaleFactor 4 (same fonts, page and freeze time), box-filtered to 1920x1080; take its vector-class pixels (the regions.json class map) and the base's pixels everywhere else. Generate it the way P5 is generated (a script, deterministic, recorded with its sha256; ref_check treats it like P5). P6 must pass every hard gate: a hard-gate failure means a mask or line is wrong or the model is too coarse; diagnose and report it, never loosen a gate. Thresholds then follow the unchanged rule (1.5 x worst positive, below best eligible negative / 1.2). Positives become P1, P3, P4, P5, P6. Report which metrics remain report-only and why. If deviceScaleFactor 4 changes layout (not only anti-aliasing) so that P6 cannot be a fair model, stop and report that with the evidence instead of inventing another control.
R5 MASK FIX: sel.panel's 87 text-class pixels at x=417, y 939-1041 (panel fill, no run) are reclassified as flat in make_regions.py; regenerate regions.json; T1's self-tests (python -m pytest tools/unreal-hud/tests -q) still pass and make_regions --check prints its OK lines.
R6 G7: say why G7.mad/ssim are report-only; if P6 gives them a valid threshold, calibrate them.
R7 TEXT WEIGHT: P5 stays raw coverage at hinting None (the plan's model). Its 5-33% lighter ink is T4a's to measure on the real Slate render; no light-side hard gate now.
R8 B/N6's photo-noise flip after the P4 re-render is accepted as disclosed (it equals an earlier T0 render and passes ref_check with 0 noise).
Acceptance: per pair "CALIBRATION OK positives 5/5 pass, negatives 8/8 fail only in intended regions; report-only: [..]; thresholds sha256 <hex>"; REF CHECK OK; make_regions --check OK; pytest all pass; calibration.json records R1-R6 and the code hashes. Status done when these pass (the escalation is answered by this ruling); an ESCALATE: first line only if a text region still has every soft metric report-only. No lock. The main session commits T/ as 'HUD: calibration frozen' after you pass.`

const TASKS = {
  T2b: { id: 'T2b', phase: 'Check B', model: 'opus', effort: 'medium', title: 'Calibration after the main-session ruling (positives 5/5, negatives 8/8 per pair)', where: `${PLAN.B} §4.2-4.4 and §6 T2; EXECUTION §7 (P2 decision)`, extra: RULING },
  T4a: { id: 'T4a', phase: 'Check B', model: 'opus', effort: 'medium', title: 'UI vocabulary, fonts, SChimeraText, text and icon routes', where: `${PLAN.B} §2.3-2.5, §5 and §6 T4a; ${DOCS}/research/r5-slate-pixel-faithful.md`,
    extra: `T2's calibration is frozen under the main-session ruling R1-R8 (summary in ${ESC} and calibration.json; positives P1, P3, P4, P5, P6). T3's pipeline is proven. Write the files of §6 T4a, and also T/hud_iterate.sh per §5 (T4b onwards use it; the route runs may use it, with --skip-build where the route is a runtime switch). Lock tag b/T4a with the waiting rule: one hold per build plus shots (inner calls are re-entrant, about 4-5 min), comparisons outside the lock. §5.6 holds: never edit thresholds.json, regions.json, calibration.json or the references; no per-element text nudges; no images outside the allow-list. TEXT WEIGHT: the Slate text model P5 (raw FreeType coverage) is 5-33% lighter in ink than the grayscale reference. Measure the real Slate render's ink mass per top.* run for every route and put it in t4_routes.csv. If Unreal offers a supported, text-only way to change glyph coverage or gamma for Slate fonts (find it in the engine source and cite it; no material or binary assets), add it as route candidates and record them. Look at the worst crops of the leading routes yourself before you choose. The first line of your notes must be exactly: CHOSEN hinting=<None|Default|AutoLight> kern=<on|off> text=<frac|block> icons=<svg|png> coverage=<default|<setting=value>>. If the chosen hinting is not None or the coverage is not default, the one planned recalibration of §4.4 follows as a separate task; do not recalibrate yourself. If the real Slate text stays lighter than the reference by more than 5% ink on any top.* run with the best route, say so with the numbers in decisions_for_alec (D10).` },
}

function makeT2r(h, cov) {
  return { id: 'T2r', phase: 'Check B', model: 'sonnet', effort: 'medium', title: `Planned recalibration at T4a's route (hinting ${h}, coverage ${cov})`, where: `${PLAN.B} §4.4 (one planned recalibration) and §6 T2`,
    extra: `T4a chose hinting=${h} coverage=${cov}. This is plan B §4.4's one planned recalibration, run for the main session: regenerate P5 for both pairs with make_p5.py at that hinting and coverage (model the coverage setting exactly as the engine applies it; cite the engine source T4a cited), then re-run calibrate.py exactly as T2 did. Change nothing else: P1, P3, P4, P6, N1-N8, the references, regions.json, the threshold rule and the code stay as frozen (show sha256 before and after for each). Acceptance per pair: CALIBRATION OK positives 5/5 pass, negatives 8/8 fail only in intended regions; the new thresholds sha256; a calibration_log.csv row noting 'recalibrated: T4a hinting=${h} coverage=${cov}'. No lock. Do not touch H or T4a's files.` }
}

const laneB = (async () => {
  await runTask(TASKS.T2b)
  if (!ok('T2b')) { skip('T4a', 'T2b not passed'); return }
  if (/^\s*ESCALATE/.test(results.T2b.report.notes || '')) { skip('T4a', 'T2b escalated per plan B §4.4: the main session decides'); return }
  await runTask(TASKS.T4a)
  if (!ok('T4a')) return
  const m = /CHOSEN\s+hinting=(\w+).*?coverage=(\S+)/.exec(results.T4a.report.notes || '')
  if (!m) { log('T4a: no complete CHOSEN line in notes; the recalibration decision is left to the main session'); return }
  if (m[1] === 'None' && m[2] === 'default') { log('T4a chose hinting None and default coverage: no recalibration needed'); return }
  await runTask(makeT2r(m[1], m[2]))
})()

await laneB

const out = {}
for (const id of Object.keys(results)) out[id] = brief(id)
return out
