export const meta = {
  name: 'unreal-trial-phase0-1',
  description: 'Trial Phase 0 (shared kit, project scaffolds) and Phase 1 first light for checks a, b, c: implement, verify, fix',
  phases: [
    { title: 'Shared', detail: 'S4 trial kit, S3 sibling-project scaffolds' },
    { title: 'Check A', detail: 'A0 baseline, A1 STJ source-gen, A2 AOT hazards; X1 NativeAOT inside Unreal; A9 unit meshes' },
    { title: 'Check B', detail: 'T0 grayscale refs, T1 comparator, T3 ChimeraHud capture pipeline' },
    { title: 'Check C', detail: 'C0 scaffold+RMC, C6 textures, C1 RMC builds, C2 data core, C3 renderer + G1' },
  ],
}

const R = 'D:/Projects/Project_Chimera'
const U = 'D:/Projects/Chimera-Unreal'
const DOCS = R + '/docs/unreal-move/trial-checks'
const PLAN = { A: DOCS + '/plan-a-native-sim.md', B: DOCS + '/plan-b-match-hud.md', C: DOCS + '/plan-c-runtime-terrain.md', X: DOCS + '/EXECUTION.md' }
const SO = 'Call StructuredOutput with each field as a top-level argument, not as a JSON string under an `input` key.'

const COMMON = `You are working on Project Chimera's Unreal trial. Chimera is an RTS creation platform (Godot 4.6.3 + C#) whose presentation is moving to Unreal 5.8.3 while the engine-free deterministic C# simulation stays. The trial has three checks: (a) the sim compiled with NativeAOT drives 1,000 units in Unreal with checksums equal to Godot's; (b) the Round 2 Match HUD board 3.1a rebuilt in C++ Slate and scored against its mockup; (c) runtime terrain editing (RealtimeMeshComponent). Plans of record: ${PLAN.A} (A), ${PLAN.B} (B), ${PLAN.C} (C) and ${PLAN.X} (EXECUTION: schedule, shared infrastructure S0-S4, task X1, overrides). EXECUTION.md §1.1 (lock classes), §2 (shared infrastructure and the Unreal runtime conventions in §2.2) and §3 (23 conflict resolutions) OVERRIDE the plans where they differ: read those sections before you start. Paths: R=${R}, U=${U}, P=${U}/ProjectChimera, H=${U}/ChimeraHud, T(errain)=${U}/ChimeraTerrain, KIT=${R}/tools/unreal-trial.

ALREADY DONE by the main session (do not redo): S0 (R/.gitignore rules for hud-ref and evidence/b images; plans committed at 896525f6), S1 (U/ue_lock.sh v2: re-entrant via UE_LOCK_HOLDER, orphan-safe, --measure runs KIT/preflight.ps1 -Quiet, --status, --selftest prints "LOCK SELFTEST OK 5/5", hold log U/ue_lock.log), S2 (U/.gitignore: **/HudRef/, **/HudData/Placeholders/, ChimeraTerrain/{Out,Packaged,Textures}/, ChimeraTerrain/Plugins/RealtimeMeshComponent/, ue_lock.log). U is a local git repo (no remote).

RULES
1. Read your task in its plan and every design section, fact and research file it cites before writing code. Cite the headers/files you rely on; never guess an API name: open the header under D:/Epic Games/UE_5.8/Engine (or the RMC source).
2. Heavy jobs take the lock, from Git Bash only: UE_LOCK_TAG=<check>/<task> bash ${U}/ue_lock.sh <command>. Heavy = Build.bat, any UnrealEditor*/UnrealEditor-Cmd process (editor, -game, commandlet, automation), RunUAT, packaged exe runs, AND every full sim test run (dotnet test of godot/ProjectChimera.Sim.Tests), every NativeAOT/ILC publish, every Godot run. Light work (dotnet build, python, the C++ harness, headless Chromium, git status/diff) takes no lock. Lock v2 is re-entrant. One command per hold; never hold it while you edit code. From PowerShell, plain 'bash' is WSL: use "C:/Program Files/Git/bin/bash.exe".
3. WAITING: the Bash tool kills a foreground call at 600 s, and lock waits can be long (other checks' agents run Unreal jobs right now). For anything that may take over ~8 minutes including the lock wait, start it with the Bash tool's run_in_background as  bash -c '<command> > <log> 2>&1; echo "EXIT=$?" > <log>.exit'  and then call  bash ${U}/wait_for.sh --file <log>.exit  in the FOREGROUND, repeating while it exits 124. NEVER end your turn while a background job of yours is running: your final message is your report and is taken as final.
4. NativeAOT publish needs "C:/Program Files (x86)/Microsoft Visual Studio/Installer" prepended to PATH (VS 2026 vcvarsall calls vswhere by bare name; verified 2026-10-01).
5. Do NOT git commit, push, stash, reset or checkout in either repo, and do not edit HANDOFF.md: the main session commits by explicit paths. Other checks' agents are editing the same trees in parallel: touch only the paths your task owns (A: godot/, tools/sim-trial, P/; B: tools/unreal-hud, H/; C: tools/unreal-terrain, T(errain)/; shared: tools/unreal-trial). Never kill a process you did not start. Never change machine settings.
6. Done means shown: run every acceptance command of your task and report its real output. If an item cannot pass, say so with the output. Never weaken a gate or threshold, edit or re-record a golden, or rewrite a test to make it pass. A STOP rule in the plan means stop and report status failed with the evidence.
7. Return a structured report: status (done | failed | blocked), every acceptance item with its command, real output (trimmed to the decisive lines) and pass true/false, files created or changed, evidence paths, deviations from the plan with reasons, and decisions only Alec can make (usually none).`

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
    rechecked: { type: 'array', items: { type: 'string' }, description: 'commands you re-ran or artefacts you inspected, each with its result' },
    summary: { type: 'string' },
  },
  required: ['verdict', 'problems', 'rechecked', 'summary'],
}

function implPrompt(t) {
  return `${COMMON}\n\nYOUR TASK: ${t.id} — ${t.title}. Spec: ${t.where}.\n${t.extra || ''}\nReturn the report. ${SO}`
}
function verifyPrompt(t, rep) {
  return `${COMMON}\n\nYOUR ROLE: independent verifier (you do not edit files). Verify task ${t.id} — ${t.title} (spec: ${t.where}).\nThe implementer's report is below: treat it as claims, not facts.\n1. Read the task's acceptance criteria in the spec and the EXECUTION.md overrides that apply.\n2. Re-run yourself every acceptance command that needs no lock. For locked ones, inspect the artefacts and logs they produced (timestamps from this task, exit codes, sentinel lines, image sizes); re-run one under the lock (with the waiting rule) only if the evidence is missing, stale or contradicts the claim.\n3. Read the changes (git -C <repo> diff and the new files) for bugs and for violations of: the Spec non-negotiables (the sim stays deterministic and knows nothing about Unreal; one way in for orders; looks never write sim state; text first, no Blueprints), the plan's rules (e.g. LF/no BOM where required, no golden edits, forced exit codes per EXECUTION §2.2, lock usage), and scope (edits outside this task's paths).\nVerdict pass only if every acceptance item truly passes and no blocker or major problem remains. List every problem with a severity and a concrete fix; do not filter.\n\nIMPLEMENTER REPORT:\n${JSON.stringify(rep, null, 1)}\n${SO}`
}
function fixPrompt(t, rep, v) {
  return `${COMMON}\n\nYOUR TASK: finish task ${t.id} — ${t.title} (spec: ${t.where}). A previous implementer reported the result below and an independent verifier FAILED it. Fix every blocker and major problem (and the minor ones that are cheap), re-run all acceptance commands, and return a complete fresh report.\n${t.extra || ''}\n\nPREVIOUS REPORT:\n${JSON.stringify(rep, null, 1)}\n\nVERIFIER VERDICT:\n${JSON.stringify(v, null, 1)}\n${SO}`
}

const results = {}
async function runTask(t) {
  log(`${t.id}: start`)
  let rep = await agent(implPrompt(t), { label: `${t.id}:impl`, phase: t.phase, schema: REPORT, model: t.model, effort: t.effort })
  if (!rep) { results[t.id] = { status: 'agent-error' }; log(`${t.id}: implementer error`); return results[t.id] }
  let v = null
  for (let round = 0; round < 3; round++) {
    if (rep.status === 'blocked') break
    v = await agent(verifyPrompt(t, rep), { label: `${t.id}:verify${round ? round + 1 : ''}`, phase: t.phase, schema: VERDICT, model: 'opus', effort: 'xhigh' })
    if (!v) break
    if (v.verdict === 'pass') break
    if (round === 2) break
    const fixed = await agent(fixPrompt(t, rep, v), { label: `${t.id}:fix${round + 1}`, phase: t.phase, schema: REPORT, model: 'opus', effort: 'medium' })
    if (!fixed) break
    rep = fixed
  }
  const status = rep.status === 'blocked' ? 'blocked' : (v && v.verdict === 'pass' ? 'pass' : 'fail')
  results[t.id] = { status, report: rep, verdict: v }
  log(`${t.id}: ${status}${v ? ' — ' + v.summary.slice(0, 160) : ''}`)
  return results[t.id]
}
const ok = (...ids) => ids.every(i => results[i] && results[i].status === 'pass')
function skip(id, why) { results[id] = { status: 'skipped', why }; log(`${id}: skipped (${why})`) }

const TASKS = {
  S4: { id: 'S4', phase: 'Shared', model: 'sonnet', effort: 'medium', title: 'Trial kit (KIT = R/tools/unreal-trial)', where: `${PLAN.X} §2.1 S4 (and §2.2-2.3 for what the tools serve)`,
    extra: `Build preflight.ps1 (Windows PowerShell 5.1 compatible; B's T3 preflight spec in ${PLAN.B} §2.9/T3 promoted, plus -Quiet), logscan.py, secret_scan.py, evidence.py (add, phone, sheet, scorecard, check) and tests under KIT/tests run with python -m pytest KIT/tests -q. Install freetype-py at a pinned version with pip --user and record the version in KIT/README.md. preflight.ps1 -Quiet must exit 0 on a quiet machine and non-zero otherwise; ue_lock.sh --measure calls it as: powershell.exe -NoProfile -ExecutionPolicy Bypass -File ${R}/tools/unreal-trial/preflight.ps1 -Quiet. Acceptance: pytest all pass; preflight.ps1 prints the DESKTOP line and exits 0 now (the machine may not be quiet for -Quiet while other agents work: report the -Quiet result and show it detects load); secret_scan.py fails on a planted SecurityToken= line in a temp file and passes on clean input; bash ${U}/ue_lock.sh --selftest still prints LOCK SELFTEST OK 5/5.` },
  S3: { id: 'S3', phase: 'Shared', model: 'sonnet', effort: 'medium', title: 'Sibling-project scaffold: ChimeraHud and ChimeraTerrain', where: `${PLAN.X} §2.1 S3; project shapes in ${PLAN.B} §2.1 and ${PLAN.C} §3.1 (write only the shared part; B's T3 and C's C0 add their own)`,
    extra: `Write KIT/new_project.py --name ChimeraHud|ChimeraTerrain and run it for both. Leave existing HudRef/ or HudData/ folders alone (check B's T0 may create H/HudRef first). Then build each empty project under the lock: UE_LOCK_TAG=shared/S3 bash ${U}/ue_lock.sh "D:/Epic Games/UE_5.8/Engine/Build/BatchFiles/Build.bat" <Name>Editor Win64 Development -Project="<abs uproject>" -WaitMutex (use the waiting rule). Acceptance: both builds print Result: Succeeded and UnrealEditor-<Name>.dll exists; python KIT/secret_scan.py passes on both project folders (KIT exists from S4); EngineAssociation equals P's; no SecurityToken section.` },

  A0: { id: 'A0', phase: 'Check A', model: 'sonnet', effort: 'medium', title: 'Baseline and tools', where: `${PLAN.A} §4 A0`,
    extra: `Per EXECUTION §1.1, the full test run (T1), the aotsmoke publish and the Godot smoke runs take the lock (tags a/A0). Per EXECUTION §3 C2, delete the stale guard ToolingGateGuardTests.BurnDownDispatcher_HasNoHardcodedTier1Baseline and gate on failed = 0 with the measured totals. Record the measured baseline numbers and the T1 wall time in your notes (later tasks and the HANDOFF use them).` },
  A1: { id: 'A1', phase: 'Check A', model: 'sonnet', effort: 'high', title: 'Source-generated STJ migration (behaviour-neutral)', where: `${PLAN.A} §3.1 and §4 A1; research r1 §7 (${DOCS}/research/r1-stj-aot-inventory.md); prototype diffs in ${DOCS}/research/proto-a/`,
    extra: `This is the change most likely to move a golden silently: never re-record or bump AlgoVersion; if any golden or hash moves, STOP and find the type with the reflection oracle. Keep LF, no BOM. T1 and the Godot smoke take the lock (tag a/A1).` },
  A2: { id: 'A2', phase: 'Check A', model: 'sonnet', effort: 'medium', title: 'AOT hazards outside STJ (MixUnknownEffect fail-closed, NUL bytes, release gate)', where: `${PLAN.A} §3.1 and §4 A2`,
    extra: `Use a byte-safe Python edit for CanonicalFold.cs (it is -text to git and holds raw NUL bytes). T1 takes the lock (tag a/A2).` },
  X1: { id: 'X1', phase: 'Check A', model: 'sonnet', effort: 'medium', title: 'NativeAOT inside the Unreal process (day-1 smoke)', where: `${PLAN.X} §1.5 X1; module skeleton per ${PLAN.A} §3.7 file list; DLL hosting facts in ${DOCS}/research/r3-ue-host-and-instancing.md`,
    extra: `The .uproject edit, Target.cs edits and the build share ONE lock hold (do them with no editor running, then build). The publish of the smoke DLL is an ILC publish: it takes the lock too (tag a/X1). If Unreal crashes or hangs when the DLL loads, collect the crash folder, log and minidump path and report failed: that triggers Alec's decision D7, do not work around it with a helper process.` },
  A9: { id: 'A9', phase: 'Check A', model: 'sonnet', effort: 'medium', title: 'Static unit meshes for ISMs', where: `${PLAN.A} §4 A9; look-test import tooling in P/LookTest/tools and ${R}/tools/unreal-looktest (PLAN_DELTA.md D7-D9 gotchas)`,
    extra: `One editor session under the lock (tag a/A9) with the waiting rule; the editor's first launch may compile shaders for a long time. The ChimeraSimHost module X1 added must be built before the editor opens P (import_units.sh refuses otherwise). Close the editor cleanly at the end.` },

  T0: { id: 'T0', phase: 'Check B', model: 'sonnet', effort: 'medium', title: 'Grayscale references and controls', where: `${PLAN.B} §3 and §6 T0 (references and controls per §3-4.4)`,
    extra: `EXECUTION overrides: the R/.gitignore rule is already done by S0 (narrow one-extension lines; do not add the brace rule from the plan); scope the image gates to docs/unreal-move/trial-checks/research/hud-ref and docs/unreal-move/trial-checks/evidence/b. H may not exist yet (S3 runs in parallel): create H/HudRef/ and H/HudData/Placeholders/ yourself; S3 will leave them alone. lcd_vs_gray_text_4x.png is for Alec: put its path in evidence. No lock needed (headless Chromium).` },
  T1: { id: 'T1', phase: 'Check B', model: 'sonnet', effort: 'high', title: 'Regions, comparator, self-tests, text-drift model', where: `${PLAN.B} §4 and §6 T1`,
    extra: `Develop against H/HudRef/r4/ until T0's references exist (T0 runs in parallel and moves the r4 PNGs there; if they are not there yet, use ${DOCS}/research/hud-ref/). No lock.` },
  T3: { id: 'T3', phase: 'Check B', model: 'sonnet', effort: 'medium', title: 'ChimeraHud project + capture pipeline proof', where: `${PLAN.B} §2.1, §2.2, §2.7-2.9 and §6 T3`,
    extra: `EXECUTION overrides: S3 already scaffolded H (add B's parts on top). T3 acceptance step (1) is replaced by: bash ${U}/ue_lock.sh --selftest prints LOCK SELFTEST OK 5/5 (only when the lock is free; if another check holds it, run --status and note it). Use KIT/preflight.ps1 (from S4) instead of writing T/preflight.ps1. U/.gitignore already has B's rules (S2). Failures exit forced (§2.2). Lock tag b/T3 with the waiting rule; the first warm-up may compile shaders for a long time.` },

  C0: { id: 'C0', phase: 'Check C', model: 'sonnet', effort: 'medium', title: 'Scaffold and vendor RMC', where: `${PLAN.C} §3.1, §3.6 (the collision patch) and §4 C0`,
    extra: `S3 already wrote the shared scaffold of T(errain); add C's parts on top. U/.gitignore already ignores the nested RMC clone (S2); VENDOR.md must carry the full patch text.` },
  C6: { id: 'C6', phase: 'Check C', model: 'sonnet', effort: 'medium', title: 'Fetch and pack CC0 layer textures', where: `${PLAN.C} §3.5 and §4 C6`, extra: `No lock.` },
  C1: { id: 'C1', phase: 'Check C', model: 'sonnet', effort: 'medium', title: 'Gate G1a: RMC compiles on 5.8.3, Editor and Game targets', where: `${PLAN.C} §4 C1`,
    extra: `Lock tag c/C1 with the waiting rule (the first RMC build may be long). Local compile patches are time-boxed to 1 h and recorded in VENDOR.md; beyond that report failed (CF trigger).` },
  C2: { id: 'C2', phase: 'Check C', model: 'sonnet', effort: 'high', title: 'Data core and tests', where: `${PLAN.C} §3.2-3.4, §3.7 and §4 C2`, extra: `Lock tag c/C2 for the build and the automation run.` },
  C3: { id: 'C3', phase: 'Check C', model: 'opus', effort: 'medium', title: 'Chunk renderer, game shell, gate G1b', where: `${PLAN.C} §3.3, §3.5 (lighting), §3.8 and §4 C3; G1 bars in §5`,
    extra: `EXECUTION overrides: exit codes per §2.2 (failure = forced exit with the code, success sentinel results.json completed=true); log scan via KIT/logscan.py rules (§3 C4: the 4 profiler-DLL 'Failed to load' lines are normal). Lock tag c/C3 with the waiting rule; the warm-up may compile shaders for up to 60 min. Look at the G1 screenshots yourself before claiming the gate.` },
}

// Shared: kit first (S3 needs secret_scan.py), then scaffolds.
const pShared = (async () => {
  await runTask(TASKS.S4)
  await runTask(TASKS.S3)
})()

// Check A: .NET chain and the Unreal-side first light run side by side.
const pA0 = runTask(TASKS.A0)
const laneAnet = (async () => {
  await pA0
  if (!ok('A0')) { skip('A1', 'A0 not passed'); skip('A2', 'A0 not passed'); return }
  await runTask(TASKS.A1)
  if (!ok('A1')) { skip('A2', 'A1 not passed'); return }
  await runTask(TASKS.A2)
})()
const laneAue = (async () => {
  await runTask(TASKS.X1)
  await pA0
  if (!ok('X1')) { skip('A9', 'X1 not passed (D7 territory)'); return }
  await runTask(TASKS.A9)
})()

// Check B: references and comparator in parallel; the capture pipeline needs the scaffold and T0's world layer.
const laneB = (async () => {
  const pT0 = runTask(TASKS.T0)
  const pT1 = runTask(TASKS.T1)
  await Promise.all([pShared, pT0])
  if (!ok('S3')) { skip('T3', 'S3 scaffold not passed') } else { await runTask(TASKS.T3) }
  await pT1
})()

// Check C: scaffold + RMC, then the build gate, data core and renderer gate; textures on the side.
const laneC = (async () => {
  await pShared
  if (!ok('S3')) { ['C0', 'C6', 'C1', 'C2', 'C3'].forEach(i => skip(i, 'S3 scaffold not passed')); return }
  await runTask(TASKS.C0)
  if (!ok('C0')) { ['C6', 'C1', 'C2', 'C3'].forEach(i => skip(i, 'C0 not passed')); return }
  const pC6 = runTask(TASKS.C6)
  await runTask(TASKS.C1)
  if (!ok('C1')) { skip('C2', 'C1 not passed (CF trigger)'); skip('C3', 'C1 not passed (CF trigger)'); await pC6; return }
  await runTask(TASKS.C2)
  if (!ok('C2')) { skip('C3', 'C2 not passed') } else { await runTask(TASKS.C3) }
  await pC6
})()

await Promise.all([pShared, laneAnet, laneAue, laneB, laneC])

const out = {}
for (const [id, r] of Object.entries(results)) {
  out[id] = r.report ? {
    status: r.status,
    impl_status: r.report.status,
    notes: r.report.notes,
    failing: (r.report.acceptance || []).filter(a => !a.pass).map(a => a.item + ' :: ' + (a.output || '').slice(0, 300)),
    evidence: r.report.evidence || [],
    files: r.report.files_changed || [],
    deviations: r.report.deviations || [],
    decisions: r.report.decisions_for_alec || [],
    verifier: r.verdict ? { verdict: r.verdict.verdict, summary: r.verdict.summary, problems: r.verdict.problems.filter(p => p.severity !== 'minor').map(p => p.severity + ': ' + p.detail.slice(0, 300)) } : null,
  } : r
}
return out
