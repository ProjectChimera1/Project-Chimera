export const meta = {
  name: 'chimera-dw-burndown',
  description: 'Burn down Godot-free deferred-work bundles: parallel worktree dev + Tier-1 gate, serial merge, ledger close, then one multi-lens review sweep',
  whenToUse: 'Epic 15 deferred-work burn-down, Godot-FREE bundles only. Godot-coupled bundles stay on bmad-loop (single-client bridge + routed in-engine gate).',
  phases: [
    { title: 'Implement', detail: 'one agent per bundle, isolated git worktree, must leave Tier-1 green' },
    { title: 'Merge', detail: 'serial merge into the target branch, Tier-1 re-run after each' },
    { title: 'Ledger', detail: 'close the merged DW entries in deferred-work.md' },
    { title: 'Review', detail: 'multi-lens adversarial sweep over the whole merged diff' },
  ],
}

// ─────────────────────────────────────────── config ───────────────────────────────────────────
// args: { bundleNames: string[], chunkSize?: number, worklistPath?: string, skipReview?: boolean }
//
// CHUNK SIZE IS A HARDWARE LIMIT, NOT A TUNING KNOB. This machine is a Ryzen 5 5600 (6 physical
// cores / 12 logical) with 16 GB RAM. Every implement agent runs `dotnet build` plus the 3834-test
// Tier-1 suite, each wanting ~1.5-3 GB and multiple cores. Above ~4 concurrent the box thrashes
// swap and every suite run slows more than the extra parallelism buys. Workflow's own cap is
// min(16, cores-2) and there is no knob to lower it, so concurrency is bounded HERE by chunking.
const CHUNK = args?.chunkSize ?? 4
const WORKLIST = args?.worklistPath ?? 'D:/Projects/Project_Chimera/.bmad-loop/workflow-worklist.json'
const NAMES = args?.bundleNames ?? []

if (!NAMES.length) {
  log('No bundleNames passed. Read .bmad-loop/workflow-worklist.json and pass a subset via args.')
  return { error: 'no bundleNames in args' }
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

const implPrompt = (name) => `
You are implementing ONE deferred-work bundle for Project Chimera. You are in your OWN git worktree
— an isolated checkout. Nothing you do here touches anyone else's files.

BUNDLE: ${name}

1. Read ${WORKLIST}. Find the object in \`waves[].bundles[]\` whose \`name\` is exactly "${name}".
   That gives you its \`dw_ids\`, its \`intent\` (the cohesive goal), and the \`files\` its entries name.
2. Read D:/Projects/Project_Chimera/_bmad-output/implementation-artifacts/deferred-work.md and find
   each \`### <DW-id>:\` block for your ids. The \`reason:\` line is the actual defect. Some entries
   carry \`decision:\` lines — a recorded human decision OVERRIDES your own judgement about scope.
3. Read D:/Projects/Project_Chimera/.bmad-loop/decisions.json. If any of your DW ids appears there,
   its \`intent\` is a DIRECT INSTRUCTION FROM THE PROJECT OWNER. Follow it exactly.
4. VERIFY EACH ENTRY AGAINST THE CURRENT CODE BEFORE FIXING IT. The ledger is append-only and some
   entries were written months ago; a few are already resolved. If an entry is already fixed, do NOT
   invent work — report it in \`dwResolved\` only if it is genuinely satisfied, and say so in \`summary\`.
5. Implement the fix. Follow godot/CLAUDE.md: simulation layer is pure C# (no \`using Godot;\`), SoA
   arrays, Fixed-point math (never float) in anything the checksum covers, process entities by
   ascending id, new per-unit SoA fields go through EntityWorld.ApplyUnitDefinition.
6. Add regression coverage that would FAIL without your fix. Tests live in
   godot/ProjectChimera.Sim.Tests/ and must be Godot-free.
7. DETERMINISM — READ THIS TWICE. If your change alters any value folded into SimChecksum, goldens
   move. Do NOT re-record a golden to make a test pass. If a golden breaks, either your change is
   wrong, or it needs a deliberate isolated re-baseline that is NOT this bundle's job. Stop, set
   success=false, and explain in blockedReason. The same applies to CanonicalModelHash and
   StartStateHash.
8. Gate — BOTH must pass, run them yourself, do not assume:
     dotnet build D:/Projects/Project_Chimera/godot/godot.csproj
     dotnet test D:/Projects/Project_Chimera/godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj
   The suite baseline is 3834 passing / 0 failing / 1 skipped. ANY failure means success=false.
   Report the real numbers in testsPassed/testsFailed — never a guess.
9. DO NOT EDIT deferred-work.md, sprint-status.yaml, or epics.md. Bookkeeping is a later serial
   phase; 80 agents editing one ledger is a guaranteed merge conflict. Record anything you would
   have filed in \`newFindings\` instead.
10. Commit on a branch named \`dw/${name}\`:
      git checkout -b dw/${name}
      git add -A && git commit -m "dw(${name}): <what changed> — <DW ids>"
    Report that branch name. If you could not finish, commit nothing, set success=false, and put the
    real reason in blockedReason — a partial commit is worse than none.

Be honest. A truthful success=false costs one bundle; a false success=true corrupts the merge and
the ledger. Your final message is the structured result, not prose for a human.
`

const mergePrompt = (r) => `
Merge ONE completed deferred-work bundle into the working branch of the MAIN checkout at
D:/Projects/Project_Chimera. You are NOT in a worktree — you are on the real branch.

BUNDLE : ${r.bundle}
BRANCH : ${r.branch}
DW ids : ${(r.dwResolved || r.dwIds || []).join(', ')}
WHAT   : ${r.summary}

1. \`git merge --no-ff ${r.branch}\`.
2. On conflict: resolve it. You have both sides; keep BOTH intents — these are independent fixes to
   the same file, not competing versions. If the conflict is genuinely irreconcilable,
   \`git merge --abort\`, set merged=false, and list the conflicting paths.
3. After a clean merge, re-run the FULL suite:
     dotnet test D:/Projects/Project_Chimera/godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj
   Baseline 3834 passing / 0 failing / 1 skipped, and it should only grow. If the merge turns the
   suite red, the two changes interact: fix it here if the fix is small and obvious, otherwise
   \`git reset --hard HEAD~1\` to undo the merge, set suitePassed=false, and explain in \`note\`.
4. Report real numbers. Never claim a suite pass you did not observe.
`

const ledgerPrompt = (rs) => `
Close the deferred-work entries for bundles that merged cleanly. You are in the MAIN checkout at
D:/Projects/Project_Chimera. This is bookkeeping — do NOT change any production code.

MERGED THIS CHUNK:
${rs.map((r) => `  - ${r.bundle}: ${(r.dwResolved || r.dwIds).join(', ')} — ${r.summary}`).join('\n')}

NEW FINDINGS to file (surfaced but deliberately not fixed):
${rs.flatMap((r) => (r.newFindings || []).map((f) => `  - ${f.title} | ${f.location} | ${f.reason}`)).join('\n') || '  (none)'}

1. In _bmad-output/implementation-artifacts/deferred-work.md, for each resolved DW id, change its
   \`status: open\` line to \`status: done 2026-08-03\` and add a \`resolution:\` line naming the bundle.
   THE LEDGER IS APPEND-ONLY — never rewrite, reorder, or delete an entry. Only flip status and add
   the resolution line.
2. File each new finding as a canonical entry at the end, numbered from the current highest DW id:
     ### DW-<n>: <one-line title>
     origin: workflow burn-down run, 2026-08-03
     location: <file:line>
     reason: <what is wrong and why it matters>
     status: open
   The format is load-bearing: an entry missing the \`### DW-<n>:\` heading or the \`status:\` line is
   invisible to bmad-loop triage forever. See .claude/skills/bmad-loop-sweep/deferred-work-format.md.
3. Commit: \`chore(sweep): close <ids> via workflow burn-down\`.
4. Report the counts you actually changed: entries closed, entries filed.
`

// ────────────────────────────────────────── execution ──────────────────────────────────────────
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
      isolation: 'worktree', model: 'opus', effort: 'max',
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
    const m = await agent(mergePrompt(r), {
      label: `merge:${r.bundle}`, phase: 'Merge', schema: MERGE, model: 'opus', effort: 'high',
    })
    if (m && m.merged && m.suitePassed) { merged.push(r); allMerged.push(r) }
    else { allFailed.push({ ...r, blockedReason: `merge failed: ${m?.note || 'agent returned null'}` }) }
  }

  // Per-chunk so a crash loses at most one chunk of bookkeeping, never the whole run.
  if (merged.length) {
    phase('Ledger')
    await agent(ledgerPrompt(merged), { label: `ledger:chunk-${n}`, phase: 'Ledger', model: 'opus', effort: 'high' })
  }

  log(`chunk ${n}/${total} done — merged ${merged.length}/${chunk.length}; running total ${allMerged.length}`)
  if (budget.total && budget.remaining() < 40_000) { log('token target reached, stopping early'); break }
}

// ─────────────────────────────────── final multi-lens review ───────────────────────────────────
let review = null
if (!args?.skipReview && allMerged.length) {
  phase('Review')
  const range = `HEAD~${allMerged.length}..HEAD`
  const LENSES = [
    ['correctness', 'Logic errors, off-by-one, wrong operator, inverted condition, unhandled null, wrong bounds. Prove each with concrete inputs producing a wrong output.'],
    ['determinism', 'Anything breaking deterministic lockstep: float in checksum-folded paths, non-deterministic iteration (Dictionary/HashSet order, LINQ), wall-clock reads, unseeded RNG, entities processed out of id order.'],
    ['regression', 'Behavior silently changed beyond the bundle intents — a fix that altered an unrelated path, a test weakened or deleted rather than fixed, a golden quietly re-recorded.'],
    ['integration', 'Interactions BETWEEN the merged bundles that no single agent could see: two fixes to the same subsystem that individually pass but jointly contradict, or a shared assumption one changed and another still relies on.'],
  ]

  const found = (await parallel(LENSES.map(([key, brief]) => () =>
    agent(
      `Adversarially review the merged deferred-work burn-down in D:/Projects/Project_Chimera.\n\n` +
      `DIFF: \`git diff ${range}\` (${allMerged.length} merged bundles).\n` +
      `BUNDLES: ${allMerged.map((r) => r.bundle).join(', ')}\n\n` +
      `LENS — ${key}: ${brief}\n\n` +
      `Each agent worked in isolation and saw only its own bundle; you see the whole. Report ONLY defects you can ` +
      `substantiate by reading the code — no speculation, no style notes. For each, give file:line, what breaks, and ` +
      `the concrete input/state that triggers it. Reporting nothing is a valid and useful result.`,
      { label: `review:${key}`, phase: 'Review', model: 'opus', effort: 'max',
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
    review = await agent(
      `Triage these review findings from the Chimera deferred-work burn-down, then FILE them.\n\n` +
      `${JSON.stringify(flat, null, 1)}\n\n` +
      `1. Drop duplicates (several lenses may report one defect) and anything you cannot confirm by reading the code.\n` +
      `2. FIX the high-severity confirmed defects directly. After each fix run the full Tier-1 suite\n` +
      `   (dotnet test D:/Projects/Project_Chimera/godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj);\n` +
      `   it must stay green. Commit as \`fix(sweep): <what> — post-merge review\`.\n` +
      `3. File every confirmed medium/low as a canonical deferred-work entry (### DW-<n>: heading + status: open +\n` +
      `   origin/location/reason lines), numbered from the current highest id. The format is load-bearing.\n` +
      `4. Report: confirmed, dropped, fixed, filed.`,
      { label: 'review:triage', phase: 'Review', model: 'opus', effort: 'max' }
    )
  }
}

return {
  requested: NAMES.length,
  merged: allMerged.length,
  mergedBundles: allMerged.map((r) => r.bundle),
  failed: allFailed.map((r) => ({ bundle: r.bundle, reason: r.blockedReason || 'unknown' })),
  newFindingsFiled: allFindings.length,
  reviewSweep: review ?? 'skipped or no findings',
  note: 'Godot-COUPLED bundles were excluded by construction — they stay on bmad-loop for the routed in-engine gate.',
}
