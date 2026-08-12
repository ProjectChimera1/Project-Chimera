export const meta = {
  name: 'chimera-dw-burndown',
  description: 'Burn down Godot-free deferred-work bundles: parallel worktree dev + Tier-1 gate, serial merge, ledger close, then one multi-lens review sweep',
  whenToUse: 'Epic 15 deferred-work burn-down, Godot-FREE bundles only. Godot-coupled bundles stay on bmad-loop (single-client bridge + routed in-engine gate).',
  phases: [
    { title: 'Implement', detail: 'one agent per bundle, isolated git worktree, must leave Tier-1 green' },
    { title: 'Merge', detail: 'serial merge into the target branch, Tier-1 re-run after each' },
    { title: 'Ledger', detail: 'close the merged DW entries in deferred-work.md' },
    { title: 'Re-record', detail: 'rebaseline mode only: one serial AlgoVersion bump + golden record, on Windows' },
    { title: 'Review', detail: 'multi-lens adversarial sweep over the whole merged diff' },
  ],
}

// ------------------------------------------- config -------------------------------------------
// args: { bundleNames: string[], chunkSize?: number, worklistPath?: string, skipReview?: boolean }
//       - or just the bundle-name array, or a JSON string of either (see the normalizer below).
//
// CHUNK SIZE IS A HARDWARE LIMIT, NOT A TUNING KNOB. This machine is a Ryzen 5 5600 (6 physical
// cores / 12 logical) with 16 GB RAM. Every implement agent runs `dotnet build` plus the full
// Tier-1 suite, each wanting ~1.5-3 GB and multiple cores. Above ~4 concurrent the box thrashes
// swap and every suite run slows more than the extra parallelism buys. Workflow's own cap is
// min(16, cores-2) and there is no knob to lower it, so concurrency is bounded HERE by chunking.
// Invoked as a slash command, `args` arrives as the RAW STRING the user typed, so a bare
// `args.bundleNames` is always undefined and the run bails before spawning anything. Accept all
// three shapes: JSON text, a bare array of bundle names, or the full options object.
const parsed = typeof args === 'string' ? (() => { try { return JSON.parse(args) } catch { return {} } })() : (args ?? {})
const OPTS = Array.isArray(parsed) ? { bundleNames: parsed } : parsed

const CHUNK = OPTS.chunkSize ?? 4
const WORKLIST = OPTS.worklistPath ?? 'D:/Projects/Project_Chimera/.claude/workflows/dw-worklist.json'
const NAMES = OPTS.bundleNames ?? []
const MODEL = OPTS.model ?? 'opus'          // fleet-wide model for every agent in this run
// DW-502: NO hardcoded fallback. This used to default to a literal 3834 - a snapshot of one commit's
// Tier-1 pass count that went stale immediately. Seven independent worktrees, across three runs, each
// measured 3714 against it, and the 120-test gap read as a regression they had to disprove: several
// burned gate time detaching to their parent commit, and one reached for `git stash` to measure it and
// cross-wired every parallel worktree's stash stack (DW-521). A stale baseline is worse than no
// baseline, so the figure must come from whoever just measured it. Absent => refuse to launch.
const BASELINE = OPTS.baselineTests
const BASE = OPTS.baseSha ?? ''             // pre-run master SHA; anchors the final review diff range
// Where merge/ledger/review run. Defaults to the main checkout (the original behaviour). Pass a
// dedicated integration worktree to keep the burn-down off the working tree the human is using -
// that isolation is why this option exists, and merging into a non-master integration branch is
// also what the auto-mode safety classifier will accept (2026-08-05: it refused merge-to-master).
const INTEG = OPTS.integrationPath ?? 'D:/Projects/Project_Chimera'
const INTEG_BRANCH = OPTS.integrationBranch ?? 'master'
// RE-BASELINE MODE (Story 15-22 / Phase C onward). OFF by default - with it off this script behaves
// exactly as it always has. The stock implement contract HARD-STOPS on any golden movement (step 7),
// which is correct for ordinary burn-down and fatal for a re-baseline window, where every bundle moves
// goldens BY DESIGN and would report success=false. In rebaseline mode: bundles still never touch a
// golden or AlgoVersion, but they leave the golden tests RED and enumerate what moved and why; one
// serial Re-record phase then does the bump + record ONCE, after every bundle has merged.
const REBASELINE = OPTS.rebaseline ?? false
// Free-text, passed verbatim into the Re-record agent: which version bump, which control to check,
// which pins to update. Story-specific, so it lives in the caller's args, not in this script.
const REBASELINE_BRIEF = OPTS.rebaselineBrief ?? ''

if (!NAMES.length) {
  log('No bundleNames passed. Read .claude/workflows/dw-worklist.json and pass a subset via args.')
  return { error: 'no bundleNames in args' }
}

// DW-502: fail the LAUNCH, not a bundle. Every implement and merge agent is handed this number as the
// suite baseline it must not fall below; if it is wrong they either chase a phantom regression or, worse,
// accept a real one. Measure it on the branch you are about to merge into and pass it in:
//   dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj   (read "Passed: <n>")
if (!Number.isInteger(BASELINE) || BASELINE <= 0) {
  log('No baselineTests passed (or not a positive integer). Measure the CURRENT Tier-1 pass count on the ' +
      'integration branch and pass it as args.baselineTests - a stale hardcoded figure made seven worktrees ' +
      'disprove a 120-test phantom regression (DW-502) and triggered a cross-worktree stash incident (DW-521).')
  return { error: 'missing baselineTests in args' }
}

const IMPL = {
  type: 'object',
  required: ['bundle', 'success', 'branch', 'dwIds', 'summary', 'testsPassed', 'filesTouched'],
  properties: {
    bundle: { type: 'string' },
    success: { type: 'boolean', description: 'true ONLY if the work is committed AND the full Tier-1 suite passed' },
    branch: { type: 'string', description: 'branch holding the commit, empty string if nothing was committed' },
    dwIds: { type: 'array', items: { type: 'string' } },
    dwResolved: { type: 'array', items: { type: 'string' }, description: 'the subset genuinely fixed; may be shorter than dwIds' },
    summary: { type: 'string' },
    testsPassed: { type: 'integer' },
    testsFailed: { type: 'integer' },
    filesTouched: { type: 'array', items: { type: 'string' } },
    newFindings: {
      type: 'array',
      description: 'pre-existing problems found but deliberately NOT fixed (out of bundle scope)',
      items: {
        type: 'object',
        required: ['title', 'location', 'reason'],
        properties: { title: { type: 'string' }, location: { type: 'string' }, reason: { type: 'string' } },
      },
    },
    blockedReason: { type: 'string', description: 'why success is false, empty when success is true' },
    goldensMoved: {
      type: 'array',
      description: 'rebaseline mode: golden tests left RED by this bundle, each with the reason it moved. ' +
                   'A golden you cannot explain is a defect in the fix, not an expected movement.',
      items: {
        type: 'object',
        required: ['test', 'reason'],
        properties: { test: { type: 'string' }, file: { type: 'string' }, reason: { type: 'string' } },
      },
    },
  },
}

const MERGE = {
  type: 'object',
  required: ['bundle', 'merged', 'suitePassed'],
  properties: {
    bundle: { type: 'string' },
    merged: { type: 'boolean' },
    conflicts: { type: 'array', items: { type: 'string' } },
    suitePassed: { type: 'boolean' },
    testsFailed: { type: 'integer' },
    note: { type: 'string' },
  },
}

// Steps 7 and 8 are the only part of the implement contract that differs between ordinary burn-down and
// a re-baseline window. Everything else - worktree reset, ledger reading, decision precedence, the
// no-bookkeeping rule, the stash ban, the commit shape - is identical, so it stays in one place.
const DETERMINISM_RULE = REBASELINE ? `
7. DETERMINISM - READ THIS TWICE. THIS RUN IS A DELIBERATE RE-BASELINE WINDOW, so your fix is EXPECTED
   to move SimChecksum goldens. That does NOT license you to touch one. Do NOT re-record any golden, do
   NOT edit any *.golden.txt payload, do NOT bump SimChecksum.AlgoVersion, and do NOT update any
   Assert.Equal(<n>, SimChecksum.AlgoVersion) pin. ONE serial phase does all of that ONCE, after every
   bundle has merged; a golden re-recorded here bakes your half-finished intermediate state into the
   permanent baseline and no later gate can see it.
   What you do INSTEAD: leave the golden tests RED, and report every one of them in \`goldensMoved\` with
   the reason your change moved it. A golden you CANNOT explain is a defect in your fix, not an expected
   movement - investigate it before you report success.
   THE FOLD SET MUST NOT CHANGE. Every fix in this batch changes folded VALUES only. Do not add, remove,
   or reorder anything SimChecksum hashes, and do not add a new folded field. SimChecksumCoverageGuardTest
   pins a known-state hash that proves this: if THAT test fails, you changed the fold - stop, set
   success=false, and say so. Adding folded state is a different kind of re-baseline than this window is
   scoped for.
   ReBaselineDifferentialGuardTests must also stay GREEN: it runs a scenario carrying none of the batch's
   state against a frozen control. If it fires, your change perturbed something it should not have. Stop.
   NEVER re-freeze or overwrite the control file to make it pass - that is how this gate was silently
   disabled for two releases.
   CanonicalModelHash and StartStateHash must NOT move unless your bundle's intent explicitly says so.` : `
7. DETERMINISM - READ THIS TWICE. If your change alters any value folded into SimChecksum, goldens
   move. Do NOT re-record a golden to make a test pass. If a golden breaks, either your change is
   wrong, or it needs a deliberate isolated re-baseline that is NOT this bundle's job. Stop, set
   success=false, and explain in blockedReason. The same applies to CanonicalModelHash and
   StartStateHash.`

const GATE_RULE = `
8. Gate - BOTH must pass, run them yourself, do not assume. Use RELATIVE paths from your worktree
   root - you are gating YOUR OWN worktree, never the shared integration checkout at ${INTEG}:
     dotnet build godot/godot.csproj
     dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj
   The suite baseline at launch is ${BASELINE} passing / 0 failing / 1 skipped; your additions only
   grow it. ${REBASELINE
     ? 'The build must be CLEAN and every test must pass EXCEPT the goldens your change legitimately\n' +
       '   moved (enumerated in goldensMoved). A NON-golden failure means success=false. Use --logger trx:\n' +
       '   the console logger truncates its failure list, and in this mode you must enumerate the failures\n' +
       '   exactly rather than eyeball them. Count the moved goldens in testsFailed and say so in summary.'
     : 'ANY failure means success=false -'} with ONE documented exception: a LONE failure of
   CanonicalModelHashPerfTests...StaysUnderTheRegressionCeiling is a known CPU-contention timing
   flake when several suites run concurrently. If it is the ONLY failure, re-run just that test
   with --filter; if it passes in isolation, the suite counts as green (say so in summary). Any
   other failure, or a repeat failure in isolation, is real. Report the real numbers in
   testsPassed/testsFailed - never a guess.`

const implPrompt = (name) => `
You are implementing ONE deferred-work bundle for Project Chimera. You are in your OWN git worktree
- an isolated checkout. Nothing you do here touches anyone else's files.

BUNDLE: ${name}

0. FIRST, before anything else: the harness may have cut your worktree from a STALE cached
   snapshot (days old - a subject line like "chore(snapshot): ..." is the tell). Run:
     git log --oneline -1
     git reset --hard ${BASE || 'master'}
   Unconditionally. Your worktree shares the main repo's refs, so the run-base commit is always
   available. Skipping this silently rebuilds days-old code, reports a wrong test baseline, and
   poisons your merge with divergence every later agent pays for.

1. Read ${WORKLIST}. Find the object in \`waves[].bundles[]\` whose \`name\` is exactly "${name}".
   That gives you its \`dw_ids\`, its \`intent\` (the cohesive goal), and the \`files\` its entries name.
2. Read ${INTEG}/_bmad-output/implementation-artifacts/deferred-work.md and find
   each \`### <DW-id>:\` block for your ids. The \`reason:\` line is the actual defect. Some entries
   carry \`decision:\` lines - a recorded human decision OVERRIDES your own judgement about scope.
3. Read D:/Projects/Project_Chimera/.bmad-loop/decisions.json. If any of your DW ids appears there,
   its \`intent\` is a DIRECT INSTRUCTION FROM THE PROJECT OWNER. Follow it exactly.
4. VERIFY EACH ENTRY AGAINST THE CURRENT CODE BEFORE FIXING IT. The ledger is append-only and some
   entries were written months ago; a few are already resolved. If an entry is already fixed, do NOT
   invent work - report it in \`dwResolved\` only if it is genuinely satisfied, and say so in \`summary\`.
5. Implement the fix. Follow godot/CLAUDE.md: simulation layer is pure C# (no \`using Godot;\`), SoA
   arrays, Fixed-point math (never float) in anything the checksum covers, process entities by
   ascending id, new per-unit SoA fields go through EntityWorld.ApplyUnitDefinition.
6. Add regression coverage that would FAIL without your fix. Tests live in
   godot/ProjectChimera.Sim.Tests/ and must be Godot-free.
${DETERMINISM_RULE}
${GATE_RULE}
9. DO NOT EDIT deferred-work.md, sprint-status.yaml, or epics.md. Bookkeeping is a later serial
   phase; 80 agents editing one ledger is a guaranteed merge conflict. Record anything you would
   have filed in \`newFindings\` instead.
10. Commit on a branch named \`dw/${name}\`:
      git checkout -b dw/${name}
      git add -A && git commit -m "dw(${name}): <what changed> - <DW ids>"
    Report that branch name. If you could not finish, commit nothing, set success=false, and put the
    real reason in blockedReason - a partial commit is worse than none.

NEVER run \`git stash\` (or pop/drop/clear) in any form. The stash stack is SHARED across every
worktree of this repo; parallel agents stash-popping concurrently cross wires, and long-lived
stashes in the stack hold real unmerged work that must not be touched. To compare against the
pre-fix baseline use \`git show <sha>:<file>\` or a temp commit on your own branch - never the stash.

Be honest. A truthful success=false costs one bundle; a false success=true corrupts the merge and
the ledger. Your final message is the structured result, not prose for a human.
`

const mergePrompt = (r) => `
Merge ONE completed deferred-work bundle into the shared INTEGRATION checkout at
${INTEG}, which is on branch \`${INTEG_BRANCH}\`. This is NOT a per-bundle worktree - it is the one
place every bundle in this run lands, so leave it on that branch and never switch it. Run every git
and dotnet command below from ${INTEG}.

BUNDLE : ${r.bundle}
BRANCH : ${r.branch}
DW ids : ${(r.dwResolved || r.dwIds || []).join(', ')}
WHAT   : ${r.summary}

1. \`git merge --no-ff ${r.branch}\`.
2. On conflict: resolve it. You have both sides; keep BOTH intents - these are independent fixes to
   the same file, not competing versions. If the conflict is genuinely irreconcilable,
   \`git merge --abort\`, set merged=false, and list the conflicting paths.
   CONFLICT DISCIPLINE - the branch was cut from a snapshot taken at RUN LAUNCH, so master's side
   of every conflict may be many merges newer than the branch base. Taking the branch side of a
   hunk wholesale has already destroyed two load-bearing lines in this burn-down (the fallback
   \`present[]\` flags, restored in 4f6837b). For each conflicted file run
   \`git diff $(git merge-base HEAD <branch>) HEAD -- <file>\` to see everything master gained
   since the branch base, and keep ALL of it unless this bundle deliberately supersedes it. Files
   Tier-1 cannot compile (Compile-Removed in SimSources.props, e.g. src/Core/Bootstrap/**) get NO
   safety net from step 3 - re-read the merged result against BOTH parents before committing.
   NEVER use \`git stash\` - the stack is shared across worktrees and holds real unmerged work.
3. After a clean merge, re-run the FULL suite:
     dotnet test ${INTEG}/godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj${REBASELINE ? ' --logger trx' : ''}
   Baseline ${BASELINE} passing / 0 failing / 1 skipped at launch, and it only grows as merges
   land. A LONE failure of CanonicalModelHashPerfTests...StaysUnderTheRegressionCeiling that passes
   on an isolated --filter re-run is the documented CPU-contention flake - treat as green and note
   it.${REBASELINE ? `
   RE-BASELINE MODE - golden failures are EXPECTED here and ACCUMULATE as merges land; every bundle in
   this run moves goldens by design and the single re-record happens after all of them. So:
   \`suitePassed\` means NO NON-GOLDEN FAILURE. Golden failures are fine; list them in \`note\` with the
   running count. Use --logger trx and read the trx - the console logger truncates its failure list, and
   in this mode you must separate golden from non-golden failures exactly, not by eyeball.
   TWO failures are still hard stops even though they look golden-adjacent: SimChecksumCoverageGuardTest
   (its pinned known-state hash proves the fold SET did not change) and ReBaselineDifferentialGuardTests
   (the frozen control). If either goes red after a merge, the merged pair interacts in a way that
   changed the fold or perturbed the control - undo the merge and report it. Never re-record or
   re-freeze anything here.` : ''}
   If the merge turns the
   suite red, the two changes interact: fix it here if the fix is small and obvious, otherwise
   \`git reset --hard HEAD~1\` to undo the merge, set suitePassed=false, and explain in \`note\`.
4. Report real numbers. Never claim a suite pass you did not observe.
`

const ledgerPrompt = (rs) => `
Close the deferred-work entries for bundles that merged cleanly. Work in the INTEGRATION checkout at
${INTEG} (branch \`${INTEG_BRANCH}\`) - run every command from there, and commit there. This is
bookkeeping - do NOT change any production code.

MERGED THIS CHUNK:
${rs.map((r) => `  - ${r.bundle}: ${(r.dwResolved || r.dwIds).join(', ')} - ${r.summary}`).join('\n')}

NEW FINDINGS to file (surfaced but deliberately not fixed):
${rs.flatMap((r) => (r.newFindings || []).map((f) => `  - ${f.title} | ${f.location} | ${f.reason}`)).join('\n') || '  (none)'}

1. In _bmad-output/implementation-artifacts/deferred-work.md, for each resolved DW id, change its
   \`status: open\` line to \`status: done <TODAY>\` and add a \`resolution:\` line naming the bundle.
   <TODAY> is the REAL current date - run \`date +%F\` and use its output; this run can cross
   midnight, so never copy a date from an earlier entry.
   THE LEDGER IS APPEND-ONLY - never rewrite, reorder, or delete an entry. Only flip status and add
   the resolution line.
2. File each new finding as a canonical entry at the end, numbered from the current highest DW id:
     ### DW-<n>: <one-line title>
     origin: workflow burn-down run, <TODAY>
     location: <file:line>
     reason: <what is wrong and why it matters>
     status: open
   The format is load-bearing: an entry missing the \`### DW-<n>:\` heading or the \`status:\` line is
   invisible to bmad-loop triage forever. See .claude/skills/bmad-loop-sweep/deferred-work-format.md.
3. Commit: \`chore(sweep): close <ids> via workflow burn-down\`.
4. Report the counts you actually changed: entries closed, entries filed.
`

// The ONE place in a re-baseline run that is allowed to touch a golden payload or an AlgoVersion.
// Serial, last, and on Windows - the float-AI ai-active golden is Windows-only, so recording anywhere
// else leaves it stale (see the determinism-gate-ai-active-golden note).
const rerecordPrompt = (rs) => `
You are the SINGLE serial RE-RECORD phase of a deliberate golden re-baseline. Every bundle below has
already merged into the integration checkout at ${INTEG} (branch \`${INTEG_BRANCH}\`) and deliberately left
its golden tests RED. You are the only agent in this run permitted to re-record a golden or move an
AlgoVersion. Run every command from ${INTEG}. You are on Windows - required, do not delegate this to WSL.

MERGED BUNDLES AND WHAT THEY MOVED:
${rs.map((r) => `  - ${r.bundle} [${(r.dwResolved || r.dwIds || []).join(', ')}]: ${r.summary}` +
    ((r.goldensMoved || []).length ? `\n      moved: ${(r.goldensMoved).map((g) => `${g.test} - ${g.reason}`).join('; ')}` : '')
  ).join('\n')}

${REBASELINE_BRIEF ? `STORY BRIEF (authoritative - it overrides your own judgement about scope):\n${REBASELINE_BRIEF}\n` : ''}
PROCEDURE - in this order, and do not skip a gate because the suite "looks fine".

1. HALT GATE A - the fold set must not have changed. Run
   \`dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj --filter FullyQualifiedName~SimChecksumCoverageGuard\`
   BEFORE you touch anything. Its pinned known-state hash must still match. If it moved, a merged bundle
   changed what SimChecksum folds - that is a different kind of re-baseline than this window is scoped
   for. STOP, record halted=true and the reason, change nothing.
2. HALT GATE B - the frozen differential control. Run
   \`dotnet test ... --filter FullyQualifiedName~ReBaselineDifferentialGuard\`. Both assertions must be
   green: a scenario carrying none of the batch's state must still hash byte-identically to its frozen
   control, and the control file's own pinned bytes must be untouched. If either fails, a merged fix
   perturbed something it should not have. STOP and report which. Do NOT re-record, overwrite, or
   re-pin the control to make it pass - that is exactly how this gate was silently disabled for two
   releases, and the tautology it left behind survived two folds undetected.
3. Bump the AlgoVersion named in the brief, and write its doc entry on the constant's XML doc. Say
   plainly what KIND of bump it is: a fold change (what was added/removed/reordered) or a re-record
   generation marker with no fold change. A reader will assume the former unless you say otherwise.
4. Update every pinned assertion for that constant across the test assembly - grep for it, do not work
   from a remembered list. Note that AlgoVersionPinCommentHygieneTests fails any TRAILING COMMENT on a
   pin line that claims a version below the current constant, so a stale \`// v23 = ...\` comment turns the
   suite red: bring each current, or delete it (deleting is preferred - the rationale belongs once, on
   the constant's own doc).
5. RECORD. Set CHIMERA_GOLDEN_RECORD=1 and run the golden tests, then \`dotnet build\` (that refreshes the
   embedded copies - a record run without the rebuild leaves the assembly holding the OLD bytes), then
   the FULL suite with \`--logger trx\` and read the trx, not the console (the console logger truncates
   its failure list; it showed 6 of 26 last time).
6. ACCOUNT FOR EVERY MOVEMENT - this is the actual deliverable, not a formality. After a record run
   EVERY golden shows as modified, because the recorder rewrites the \`checksum_algo_version\` header on
   all of them. Diff each with \`git diff -- <file> | grep -v '^#'\` to separate real movement from header
   churn, and produce a table: file -> moved or header-only -> the DW id that explains it. A golden that
   moved with NO attributable cause is a defect in one of the merged fixes. Report it as unexplained
   rather than absorbing it.
7. Re-run HALT GATE B one final time, post-record.
8. Stage BY PATH and commit. Never \`git add -A\` - it sweeps in Godot-generated .uid sidecars and
   automation's Snapshot.md date bump. Commit as
   \`rebaseline(<story>): AlgoVersion <old> -> <new>, re-record <n> goldens\`.

Report honestly: halted, the gate that halted you, the version moved, the pins updated, the movement
table, any unexplained movement, and the final suite numbers you actually observed.
`

// ------------------------------------------ execution ------------------------------------------
// agent() THROWS when a subagent completes without calling StructuredOutput. parallel() swallows
// that into null, but the direct awaits (merge loop, ledger, triage) do not - one report-less
// agent killed the 2026-08-03 overnight run 4 chunks in. Catch -> null; null is already handled.
const safeAgent = (prompt, opts) =>
  agent(prompt, opts).catch((e) => { log(`agent ${opts?.label ?? ''} died report-less: ${e.message}`); return null })

log(`${NAMES.length} Godot-free bundles, chunks of ${CHUNK} (16GB / 6-core ceiling)`)

const allMerged = []
const allFailed = []
const allFindings = []

for (let i = 0; i < NAMES.length; i += CHUNK) {
  const chunk = NAMES.slice(i, i + CHUNK)
  const n = Math.floor(i / CHUNK) + 1
  const total = Math.ceil(NAMES.length / CHUNK)
  log(`chunk ${n}/${total}: ${chunk.join(', ')}`)

  phase('Implement')
  const built = (await parallel(chunk.map((name) => () =>
    agent(implPrompt(name), {
      label: `impl:${name}`, phase: 'Implement', schema: IMPL,
      isolation: 'worktree', model: MODEL, effort: 'max',
    })
  ))).filter(Boolean)

  const ok = built.filter((r) => r.success && r.branch)
  const bad = built.filter((r) => !r.success || !r.branch)
  bad.forEach((r) => allFailed.push(r))
  built.forEach((r) => (r.newFindings || []).forEach((f) => allFindings.push({ ...f, bundle: r.bundle })))
  if (bad.length) log(`  ${bad.length} did not reach a green commit: ${bad.map((b) => b.bundle).join(', ')}`)

  // Serial on purpose: every merge touches the same branch and re-runs the same suite.
  phase('Merge')
  const merged = []
  for (const r of ok) {
    const m = await safeAgent(mergePrompt(r), {
      label: `merge:${r.bundle}`, phase: 'Merge', schema: MERGE, model: MODEL, effort: 'high',
    })
    if (m && m.merged && m.suitePassed) { merged.push(r); allMerged.push(r) }
    else { allFailed.push({ ...r, blockedReason: `merge failed: ${m?.note || 'agent returned null'}` }) }
  }

  // Per-chunk so a crash loses at most one chunk of bookkeeping, never the whole run.
  if (merged.length) {
    phase('Ledger')
    await safeAgent(ledgerPrompt(merged), { label: `ledger:chunk-${n}`, phase: 'Ledger', model: MODEL, effort: 'high' })
  }

  log(`chunk ${n}/${total} done - merged ${merged.length}/${chunk.length}; running total ${allMerged.length}`)
  if (budget.total && budget.remaining() < 40_000) { log('token target reached, stopping early'); break }
}

// ------------------------- serial re-record (rebaseline mode only, once) -------------------------
// Deliberately AFTER every chunk: the whole point of a batch window is one bump and one record, so a
// per-chunk record would defeat it. Runs before the review sweep so the reviewers see the final state.
let rerecord = null
if (REBASELINE && allMerged.length) {
  phase('Re-record')
  const withGoldens = allMerged.reduce((n, r) => n + (r.goldensMoved || []).length, 0)
  log(`re-record: ${allMerged.length} merged bundles, ${withGoldens} golden movements reported`)
  rerecord = await safeAgent(rerecordPrompt(allMerged), {
    label: 'rebaseline:re-record', phase: 'Re-record', model: MODEL, effort: 'max',
    schema: {
      type: 'object',
      required: ['halted', 'summary'],
      properties: {
        halted: { type: 'boolean', description: 'true if a halt gate fired and nothing was recorded' },
        haltedGate: { type: 'string' },
        algoVersionFrom: { type: 'integer' },
        algoVersionTo: { type: 'integer' },
        pinsUpdated: { type: 'array', items: { type: 'string' } },
        goldensRecorded: { type: 'array', items: { type: 'string' }, description: 'files with REAL movement, header churn excluded' },
        goldensHeaderOnly: { type: 'integer' },
        unexplainedMovement: {
          type: 'array',
          description: 'goldens that moved with no attributable DW id - a defect, not an expected movement',
          items: { type: 'object', required: ['file', 'note'], properties: { file: { type: 'string' }, note: { type: 'string' } } },
        },
        testsPassed: { type: 'integer' },
        testsFailed: { type: 'integer' },
        commit: { type: 'string' },
        summary: { type: 'string' },
      },
    },
  })
  if (!rerecord) log('  re-record agent died report-less - the merges are landed but NOT re-recorded')
  else if (rerecord.halted) log(`  HALTED at ${rerecord.haltedGate} - nothing recorded, escalate`)
  else log(`  recorded ${(rerecord.goldensRecorded || []).length} goldens; ${(rerecord.unexplainedMovement || []).length} unexplained`)
}

// ----------------------------------- final multi-lens review -----------------------------------
let review = null
if (!OPTS.skipReview && allMerged.length) {
  phase('Review')
  // HEAD~N undercounts - each chunk also lands a ledger commit on the first-parent line, plus any
  // review fixes. Anchor on the recorded pre-run SHA whenever the caller supplied one.
  const range = BASE ? `${BASE}..HEAD` : `HEAD~${allMerged.length}..HEAD`
  const LENSES = [
    ['correctness', 'Logic errors, off-by-one, wrong operator, inverted condition, unhandled null, wrong bounds. Prove each with concrete inputs producing a wrong output.'],
    ['determinism', 'Anything breaking deterministic lockstep: float in checksum-folded paths, non-deterministic iteration (Dictionary/HashSet order, LINQ), wall-clock reads, unseeded RNG, entities processed out of id order.'],
    ['regression', 'Behavior silently changed beyond the bundle intents - a fix that altered an unrelated path, a test weakened or deleted rather than fixed, a golden quietly re-recorded.'],
    ['integration', 'Interactions BETWEEN the merged bundles that no single agent could see: two fixes to the same subsystem that individually pass but jointly contradict, or a shared assumption one changed and another still relies on.'],
  ]
  if (REBASELINE) LENSES.push(['rebaseline',
    'The re-record itself. A green suite over freshly-recorded goldens proves nothing - you are the check ' +
    'on the record run. Look for: a golden whose movement no merged bundle explains; a golden re-recorded ' +
    'by a bundle instead of the serial phase; the frozen differential control re-frozen, re-pinned, or ' +
    'edited; the AlgoVersion doc entry claiming a fold change that did not happen (or omitting one that ' +
    'did); a pin or pin-comment left stale; CanonicalModelHash/StartStateHash moving without a bundle that ' +
    'says it should; and any test weakened or skipped to survive the record rather than fixed.'])

  const found = (await parallel(LENSES.map(([key, brief]) => () =>
    agent(
      `Adversarially review the merged deferred-work burn-down in ${INTEG} (branch ${INTEG_BRANCH}) - ` +
      `run every git command from there.\n\n` +
      `DIFF: \`git diff ${range}\` (${allMerged.length} merged bundles).\n` +
      `Start from \`git log --first-parent --oneline ${range}\` and review EACH merge's diff ` +
      `individually - at this bundle count one flat diff buries defects. Spend extra care on files ` +
      `touched by more than one bundle.\n` +
      `BUNDLES: ${allMerged.map((r) => r.bundle).join(', ')}\n\n` +
      `LENS - ${key}: ${brief}\n\n` +
      `Each agent worked in isolation and saw only its own bundle; you see the whole. Report ONLY defects you can ` +
      `substantiate by reading the code - no speculation, no style notes. For each, give file:line, what breaks, and ` +
      `the concrete input/state that triggers it. Reporting nothing is a valid and useful result.`,
      { label: `review:${key}`, phase: 'Review', model: MODEL, effort: 'max',
        schema: { type: 'object', required: ['lens', 'findings'], properties: {
          lens: { type: 'string' },
          findings: { type: 'array', items: { type: 'object',
            required: ['title', 'file', 'severity', 'failureScenario'],
            properties: { title: { type: 'string' }, file: { type: 'string' },
              severity: { type: 'string', enum: ['high', 'medium', 'low'] },
              failureScenario: { type: 'string' } } } } } } }
    )
  ))).filter(Boolean)

  const flat = found.flatMap((f) => (f.findings || []).map((x) => ({ ...x, lens: f.lens })))
  log(`review sweep: ${flat.length} findings across ${found.length} lenses`)

  if (flat.length) {
    review = await safeAgent(
      `Triage these review findings from the Chimera deferred-work burn-down, then FILE them.\n\n` +
      `${JSON.stringify(flat, null, 1)}\n\n` +
      `1. Drop duplicates (several lenses may report one defect) and anything you cannot confirm by reading the code.\n` +
      `   Work in the INTEGRATION checkout at ${INTEG} (branch ${INTEG_BRANCH}); commit there.\n` +
      `2. FIX the high-severity confirmed defects directly. After each fix run the full Tier-1 suite\n` +
      `   (dotnet test ${INTEG}/godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj);\n` +
      `   it must stay green. Commit as \`fix(sweep): <what> - post-merge review\`.\n` +
      `3. File every confirmed medium/low as a canonical deferred-work entry (### DW-<n>: heading + status: open +\n` +
      `   origin/location/reason lines), numbered from the current highest id. The format is load-bearing.\n` +
      `4. Report: confirmed, dropped, fixed, filed.`,
      { label: 'review:triage', phase: 'Review', model: MODEL, effort: 'max' }
    )
  }
}

return {
  requested: NAMES.length,
  merged: allMerged.length,
  mergedBundles: allMerged.map((r) => r.bundle),
  failed: allFailed.map((r) => ({ bundle: r.bundle, reason: r.blockedReason || 'unknown' })),
  newFindingsFiled: allFindings.length,
  rebaseline: REBASELINE ? (rerecord ?? 'RE-RECORD DID NOT REPORT - merges landed, goldens NOT re-recorded') : 'not a rebaseline run',
  reviewSweep: review ?? 'skipped or no findings',
  note: 'Godot-COUPLED bundles were excluded by construction - they stay on bmad-loop for the routed in-engine gate.',
}
