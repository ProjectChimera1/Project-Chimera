export const meta = {
  name: 'unreal-trial-phase2',
  description: 'Trial Phase 2 (core) for checks a, b, c: implement, verify with two Opus lenses, fix; one checkpoint marker per finished task',
  phases: [
    { title: 'Check A', detail: 'A3 SimSession, A4 scenario/CLI/goldens, A5 NativeAOT ABI; then A6 AOT goldens, A7 C++ harness, A10 Unreal module, and A8 Godot leg -> A8b MainScene probe -> A14' },
    { title: 'Check B', detail: 'T2 calibration (D6 = grayscale), T4a fonts/text/icon routes, T2r recalibration only if T4a picks a hinting other than None' },
    { title: 'Check C', detail: 'C4 director + S1/S1L/C1, then C5 collision + soak, C7 ground material, C8 mouse; C10 sim-grid parity beside them' },
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

const TASKS = {
  A3: { id: 'A3', phase: 'Check A', model: 'opus', effort: 'medium', title: 'SimSession, pre-tick hashes, world digests, order script, footprint into the sim set', where: `${PLAN.A} §3.2 and §4 A3; facts F10-F17 and F38 in §2; research ${DOCS}/research/r2-sim-entry-checksum-scenario.md`,
    extra: `D1 is confirmed, so build SimSession as §3.2 specifies, every F13 step in that order, each mirrored with a file:line comment to its Godot site. Every change must be behaviour-neutral for existing code: the new optional ServerBootstrap.Build item parameter defaults to null and every existing golden, hash and test outcome stays unchanged; never touch AlgoVersion. New G/src files start with #nullable enable; keep LF and no BOM. T1 runs under the lock (tag a/A3) with the waiting rule. Analysis build with --no-incremental: RS0030 and IL* unchanged (IL = 0); write OUT/a3_chm_delta.txt with before/after CHM counts, each new CHM0001/CHM0005 attributed to BuildingNavFootprint.cs or SimSession's delegate bridge (this becomes the later baseline).` },
  A4: { id: 'A4', phase: 'Check A', model: 'sonnet', effort: 'high', title: 'Scenario trial_1000, CLI gen/run, trial goldens and meta files, comparator', where: `${PLAN.A} §3.3, §3.5 and §4 A4; prototype in ${DOCS}/research/proto-a/proto (gen_scenario.py, Program.cs, full_every1.txt)`,
    extra: `You record exactly two NEW goldens, Golden/trial-1000.golden.txt and Golden/trial-1000-ai.golden.txt (CHIMERA_GOLDEN_RECORD=1; the ai one Windows-gated like AiActiveGoldenTests.cs:57-66), plus their meta sidecars. Every existing golden stays byte-identical: git diff --diff-filter=MD -- 'godot/ProjectChimera.Sim.Tests/Golden/*.golden.txt' must be empty. STOP rule: gen must print units=1000 orders=2077 verdict=0 and ticks 1/60/300/1440 must equal F19 (0E7082D2, 71BA9D54, 3A69E1EB, 2CFCF09E). On any difference, diff SimSession and the CLI against PA/proto/Program.cs and fix the cause; never record a golden past a mismatch. compare_traces.py implements all of §3.5 including --final mode and parity.png (A7, A8, A10 and A13 rely on it). NAT/.gdignore and the godot.csproj Compile Remove line land here. T1 under the lock (tag a/A4) with the waiting rule.` },
  A5: { id: 'A5', phase: 'Check A', model: 'sonnet', effort: 'high', title: 'NativeAOT library ChimeraSim.dll and its C ABI', where: `${PLAN.A} §3.4 and §4 A5; facts F7-F9, F16-F17, F22`,
    extra: `NAT/publish.ps1 is an ILC publish: run it under the lock (tag a/A5) with the waiting rule and the VS Installer PATH rule (rule 4). -StageOnly is implemented but first used in A10. NAT/include/chimera_sim.h is the ABI of record (§3.4 table; static_assert sizes 56/28). Every export body is try/catch(Exception); no export writes folded sim state except submit_order, step and host config; check_exports.py asserts exports == header == allow-list. T1 under the lock (tag a/A5).` },
  A6: { id: 'A6', phase: 'Check A', model: 'sonnet', effort: 'high', title: 'AOT golden replay gate', where: `${PLAN.A} §4 A6; fact F34`,
    extra: `Write NAT/AotGoldens/GOLDENS.md first (one row per committed golden; every excluded golden named with its reason). Never modify a *Scenario.cs, a golden or a test: exclude a builder that needs xUnit and say why. The PublishAot publish is an ILC publish: lock (tag a/A6) with the waiting rule and the PATH rule. Report N1, N2 and the table. You run beside A7, A8 and A10: build only your own project.` },
  A7: { id: 'A7', phase: 'Check A', model: 'sonnet', effort: 'medium', title: 'C++ smoke harness against the NativeAOT DLL', where: `${PLAN.A} §4 A7; §3.4-3.5; ${DOCS}/research/proto-a/cppload`,
    extra: `Light work (cl.exe through vcvars, no lock). The DLL under test is A5's NAT/bin/publish/ChimeraSim.dll; A10 may republish it while you work (same sources). Copy it once into NAT/harness/bin/ and run against your copy, recording its sha256. Compare with compare_traces.py --set main cpp=... and --set ai cpp_ai=... against the A4 goldens; digests must equal the CLI's. Record the FTZ/DAZ (--mxcsr 0x9FC0 --no-shim) experiment result.` },
  A8: { id: 'A8', phase: 'Check A', model: 'sonnet', effort: 'medium', title: 'Godot headless leg', where: `${PLAN.A} §3.6 and §4 A8; fact F7, F21, F38`,
    extra: `Every Godot run takes the lock (tag a/A8): run ST/run_godot_leg.ps1 through it from Git Bash, e.g. UE_LOCK_TAG=a/A8 bash ${U}/ue_lock.sh pwsh -NoProfile -ExecutionPolicy Bypass -File <script> -Variant main, with the waiting rule. New Node classes are partial with #nullable enable; generate .uid sidecars with GODOT --headless --path G --import (under the lock). After your own compares, run compare_traces.py --set main with cli=, godot= and, if A7's C++ trace already exists, cpp=, and register its parity.png as EV/a/a-A8-parity.png with KIT/evidence.py add (chart PNG at most 300 KB). This image is for Alec: name the legs it contains in your report.` },
  A8b: { id: 'A8b', phase: 'Check A', model: 'opus', effort: 'medium', title: 'MainScene composition probe (gating)', where: `${PLAN.A} §1, §4 A8b; facts F13, F38; MainScene.cs:2985-3075`,
    extra: `The probe is DEBUG-only and changes nothing in the normal game path: a run without --sim-trial-probe must behave exactly as before. Windowed Godot runs take the lock (tag a/A8b) with the waiting rule; the window opens on Alec's desktop and must quit by itself. STOP rule: if probe_match is below 6/6, do not change SimSession, OrderScript, the trial goldens or the meta files to make it match. Report failed with which hashes differ, both values, and your diagnosis of the composition delta (which F13 step or loaded input differs, with file:line). The main session decides what to do.` },
  A10: { id: 'A10', phase: 'Check A', model: 'opus', effort: 'medium', title: 'Unreal module ChimeraSimHost: load, step, trace, error paths (no renderer yet)', where: `${PLAN.A} §3.7 and §4 A10; EXECUTION §1.5 (X1 skeleton) and §2.2; facts F22-F31; ${DOCS}/research/r3-ue-host-and-instancing.md`,
    extra: `Extend X1's ChimeraSimHost and keep -ChimeraAotSmoke as a diagnostic. Build §3.7 without the renderer (A11 adds it): game mode, arena, camera, director loop, options, warm-up, trace, CSV stats, exits per EXECUTION §2.2 (failures exit forced after closing their files; success needs the LogChimeraSim: RESULT sentinel). Lock tag a/A10, one command per hold, each with the waiting rule: the publish (ILC), -StageOnly, the build (the .uproject/Target.cs edits are done before it, with no editor in between) and each -game run. run_sim.ps1 calls KIT/preflight.ps1 before taking the lock. A7 may have the DLL loaded: if the publish fails because a file is in use, wait and retry. The four runs (main, main -MaxFps 24, hitch, ai) must give 1440/1440 against the A4 goldens; a divergence is a finding to diagnose (pacing must never reach the sim), never a reason to touch a golden.` },
  A14: { id: 'A14', phase: 'Check A', model: 'opus', effort: 'medium', title: 'Full MainScene replay (measured, reported, not gating)', where: `${PLAN.A} §1 and §4 A14; fact F38; DW-681 in R/_bmad-output/implementation-artifacts/deferred-work.md`,
    extra: `Measured and reported, not gated. Write the pre-registered expectation (§4 A14) into OUT/a14/expectation.txt before the run. Windowed Godot runs take the lock (tag a/A14) with the waiting rule. If the replay diverges, write the DW text (title, first divergent 60-tick window, evidence paths, suspected cause) to OUT/a14/dw_draft.md; the main session files it in the ledger. Do not fix DW-681 and do not change MainScene's normal behaviour. Status done means the measurement ran and is reported, whatever its result.` },

  T2: { id: 'T2', phase: 'Check B', model: 'sonnet', effort: 'medium', title: 'Calibration (positives 4/4, negatives 8/8 per pair)', where: `${PLAN.B} §4.2-4.4 and §6 T2; EXECUTION §7 (P2 decision)`,
    extra: `D6 is answered: grayscale re-render. EXECUTION §7's decision applies: positive control P2 (font-kerning:none) is dropped and P4 becomes P1+P3. First: update T/ref_controls.json (P2 marked dropped with the reason, citing EXECUTION §7; P4 = P1+P3 with static fonts), re-render P4 for both pairs with T0's renderer, and prove with sha256 before and after that both references, text_off_{A,B}.png, the placeholders and every other control file are byte-identical; update T/ref_check.py's control count to match and re-run it to REF CHECK OK. Then regenerate regions.json from ref_hudonly.png (§6 T2), and write make_p5.py (hinting None, §4.4 P5) and calibrate.py. Positives are P1, P3, P4, P5, so the acceptance line per pair reads: CALIBRATION OK positives 4/4 pass, negatives 8/8 fail only in intended regions; report-only: [..]; thresholds sha256 <hex>. The escalation rules of §4.4 stand: a negative that passes every gate stops calibration (status failed, with its per-metric values); if every text soft metric of some region is report-only, finish calibration but make the first line of your notes start with ESCALATE: and name the region and metrics (T4a then waits for the main session). No lock (headless Chromium and Python). Create T/results/calibration_log.csv with its header; the main session commits T/ as 'HUD: calibration frozen' and appends that commit hash.` },
  T4a: { id: 'T4a', phase: 'Check B', model: 'opus', effort: 'medium', title: 'UI vocabulary, fonts, SChimeraText, text and icon routes', where: `${PLAN.B} §2.3-2.5, §5 and §6 T4a; ${DOCS}/research/r5-slate-pixel-faithful.md`,
    extra: `T2's calibration is frozen and T3's pipeline is proven. Write the files of §6 T4a, and also T/hud_iterate.sh per §5 (T4b onwards use it; the route runs may use it, with --skip-build where the route is a runtime switch). Lock tag b/T4a with the waiting rule: one hold per build plus shots (inner calls are re-entrant, about 4-5 min), comparisons outside the lock. §5.6 holds: never edit thresholds.json, regions.json, calibration.json or the references; no per-element text nudges; no images outside the allow-list. Look at the worst crops of the leading routes yourself before you choose. The first line of your notes must be exactly: CHOSEN hinting=<None|Default|AutoLight> kern=<on|off> text=<frac|block> icons=<svg|png>. If the chosen hinting is not None, the one planned recalibration of §4.4 follows as a separate task; do not recalibrate yourself.` },

  C4: { id: 'C4', phase: 'Check C', model: 'sonnet', effort: 'high', title: 'Director, metrics, S1/S1L/C1 scripts, determinism, GPU-vs-CPU depth', where: `${PLAN.C} §3.8 and §4 C4; P7, P8, P11 in §5`,
    extra: `Lock tag c/C4 with the waiting rule, one build or run per hold. Collision ops log skipped:C5 until C5. Exit codes per EXECUTION §2.2 (C3 established the forced-exit contract and results.json completed=true); the log scan follows KIT/logscan.py's rules (EXECUTION §3 C4). save writes sim_grid_fnv and the 16 C10 probe values (define the 16 sim-world probe points here, including negative non-exact heights and cell-boundary points; C10 consumes them).` },
  C5: { id: 'C5', phase: 'Check C', model: 'opus', effort: 'medium', title: 'Collision path and the RMC #290 soak', where: `${PLAN.C} §3.6 and §4 C5; P5 and P6 in §5; risk 2 in §6`,
    extra: `Lock tag c/C5; the two SOAK runs are background jobs with the waiting rule. P5's correctness bars gate here (0 disagreements either way, max |dz| <= 1 cm at all four checkpoints, a trimesh on every touched body); its timings (cook p95, GT apply p95) and the fast-cook on/off comparison are measured and reported only (EXECUTION §3 C9). P6 (soak0 memory) gates; soak250 is reported. If the patch misbehaves, try risk 2's hidden collision set; if that fails too, report failed (CF trigger) with the evidence.` },
  C7: { id: 'C7', phase: 'Check C', model: 'opus', effort: 'medium', title: 'Ground material, look, GPU cost', where: `${PLAN.C} §3.5 and §4 C7; V1-V6 in §5; risks 5 and 7 in §6`,
    extra: `Lock tag c/C7: the commandlet run is a background job (30-50 min is possible) with the waiting rule; one warm-up S1 after each material change, then s1_mat. Register the composite as EV/c/c-C7-composite.jpg plus its phone copy with KIT/evidence.py add: it is for Alec, and the main session sends it. Look at the composite and the S1 shots yourself and write V1-V6 notes with reasons (V3 and V6 gate later; V1, V2, V4, V5 are reported). terrain_gpu_ms is measured and reported only (Phase 4 gates it).` },
  C8: { id: 'C8', phase: 'Check C', model: 'sonnet', effort: 'medium', title: 'Mouse sculpting, HUD, OS-mouse proof', where: `${PLAN.C} §3.4 and §4 C8; P10 in §5; risk 9 in §6`,
    extra: `Lock tag c/C8 with the waiting rule. Before the run, run KIT/preflight.ps1 and check that LogonUI.exe is absent. If the screen is locked or the OS injection is blocked, also run the -ChimeraTerrainSynthMouse fallback (logged source=slate) and report status failed with both results: source=slate fails P10 unless Alec waives it, so add D9 (EXECUTION §5) to decisions_for_alec. Never change desktop, focus-policy or Parsec settings.` },
  C10: { id: 'C10', phase: 'Check C', model: 'sonnet', effort: 'medium', title: 'Sim-grid parity (C# ElevationGrid vs the C++ heightfield)', where: `${PLAN.C} §3.2, §3.9 and §4 C10; P9 in §5`,
    extra: `No lock (dotnet run of a small console). RT = R/tools/unreal-terrain; elevhash compiles only the three Godot-free sim files listed and never touches ProjectChimera.Sim.Tests or any godot/ file. The C++ values come from C4's s1_a save (sim_grid_fnv and the 16 probes at C4's points); if they are missing, report blocked with what is missing. The C10 hand-off to check (a) is deferred to the merge (EXECUTION §3 C19).` },
}

function makeT2r(h) {
  return { id: 'T2r', phase: 'Check B', model: 'sonnet', effort: 'medium', title: `Planned recalibration at T4a's hinting ${h}`, where: `${PLAN.B} §4.4 (one planned recalibration) and §6 T2`,
    extra: `T4a chose hinting=${h}. This is plan B §4.4's one planned recalibration, run for the main session: regenerate P5 for both pairs with make_p5.py at hinting ${h}, then re-run calibrate.py exactly as T2 did. Change nothing else: controls P1, P3, P4 and N1-N8, the references, regions.json, the threshold rule and the code stay as T2 froze them (show sha256 before and after for each). Acceptance per pair: CALIBRATION OK positives 4/4 pass, negatives 8/8 fail only in intended regions; the new thresholds sha256; a calibration_log.csv row noting 'recalibrated: T4a hinting=${h}'. No lock. Do not touch H or T4a's files.` }
}

const laneA = (async () => {
  await runTask(TASKS.A3)
  if (!ok('A3')) { skipAll(['A4', 'A5', 'A6', 'A7', 'A8', 'A8b', 'A10', 'A14'], 'A3 not passed'); return }
  await runTask(TASKS.A4)
  if (!ok('A4')) { skipAll(['A5', 'A6', 'A7', 'A8', 'A8b', 'A10', 'A14'], 'A4 not passed (STOP rule territory)'); return }
  await runTask(TASKS.A5)
  const ps = []
  if (ok('A5')) ps.push(runTask(TASKS.A6), runTask(TASKS.A7), runTask(TASKS.A10))
  else skipAll(['A6', 'A7', 'A10'], 'A5 not passed')
  ps.push((async () => {
    await runTask(TASKS.A8)
    await runTask(TASKS.A8b)
    if (!ok('A8b')) { skip('A14', 'A8b not passed (the probe gates; STOP per plan A)'); return }
    await runTask(TASKS.A14)
  })())
  await Promise.all(ps)
})()

const laneB = (async () => {
  await runTask(TASKS.T2)
  if (!ok('T2')) { skip('T4a', 'T2 not passed'); return }
  if (/^\s*ESCALATE/.test(results.T2.report.notes || '')) { skip('T4a', 'T2 escalated per plan B §4.4: the main session decides'); return }
  await runTask(TASKS.T4a)
  if (!ok('T4a')) return
  const m = /CHOSEN\s+hinting=(\w+)/.exec(results.T4a.report.notes || '')
  if (!m) { log('T4a: no CHOSEN line in notes; the recalibration decision is left to the main session'); return }
  if (m[1] === 'None') { log('T4a chose hinting None: no recalibration needed'); return }
  await runTask(makeT2r(m[1]))
})()

const laneC = (async () => {
  await runTask(TASKS.C4)
  if (!ok('C4')) { skipAll(['C5', 'C7', 'C8', 'C10'], 'C4 not passed'); return }
  const pC10 = runTask(TASKS.C10)
  await runTask(TASKS.C5)
  await runTask(TASKS.C7)
  await runTask(TASKS.C8)
  await pC10
})()

await Promise.all([laneA, laneB, laneC])

const out = {}
for (const id of Object.keys(results)) out[id] = brief(id)
return out
