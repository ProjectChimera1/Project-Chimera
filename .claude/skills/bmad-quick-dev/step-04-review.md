# Step 4: Review

## RULES

- **Language** — Speak in `{{.communication_language}}`. Write any file output in `{{.document_output_language}}`.
- All review subagents must run at the same model capability as the current session.
- Run subagents synchronously: launch them together, then wait for all results before continuing.

## INSTRUCTIONS

Change `{spec_file}` status to `in-review` in the frontmatter before continuing.

### Construct Diff

Read `{baseline_commit}` from `{spec_file}` frontmatter. If `{baseline_commit}` is missing or `NO_VCS`, use best effort to determine what changed. Otherwise, construct `{diff_output}` covering all changes — tracked and untracked — since `{baseline_commit}`.

Do NOT `git add` anything — this is read-only inspection.

### Review

Execute these review layers in parallel wherever their execution methods allow: substitute the runtime placeholders (e.g. `{diff_output}`) into each layer's instruction, then follow it verbatim. Parallel means several blocking calls awaited together in this turn — never backgrounded or detached, never ending the turn to await results. When running layers as subagents, spawn every reviewer before reading or reacting to any of their output; begin collection and triage only once all are launched.

{workflow.review_layers}

If a layer's instruction requires subagents and none are available, generate one review prompt file per such layer in `{{.implementation_artifacts}}` and HALT. Ask the human to run each in a separate session (ideally a different LLM) and paste back the findings.

### Classify

1. Deduplicate only findings with the same claim and same required action. Then evaluate each remaining finding independently. Do not reject a finding because a related finding was rejected.
2. Assign severity to each finding by consequence for the artifact's main consumer (software user, document reader, etc).
   Disregard any severity assigned by a reviewing subagent. Review subagents operate under by-design information asymmetry and do not have enough context to set final severity for this workflow.
   - `low`: none or cosmetic
   - `medium`: tolerable
   - `high`: intolerable
3. Route each finding into exactly one triage category. The first three categories are **this story's problem** — caused or exposed by the current change. The last two are **not this story's problem**.
   - **intent_gap** — caused by the change; cannot be resolved from the spec because the captured intent is incomplete. Do not infer intent unless there is exactly one possible reading.
   - **bad_spec** — caused by the change, including direct deviations from spec. The spec should have been clear enough to prevent it. When in doubt between bad_spec and patch, prefer bad_spec — a spec-level fix is more likely to produce coherent code.
   - **patch** — caused by the change; trivially fixable without human input. Just part of the diff.
   - **defer** — pre-existing issue not caused by this story, surfaced incidentally by the review. Collect for later focused attention.
   - **reject** — noise. Drop silently. When unsure between defer and reject, prefer reject — only defer findings you are confident are real.
4. Process findings in cascading order. If intent_gap or bad_spec findings exist, they trigger a loopback — lower findings are moot since code will be re-derived. If neither exists, process patch and defer normally. Before each loopback, read `{spec_file}` frontmatter `review_loop_iteration` (missing means `0`), increment it by 1, and write it back. If it exceeds 5, HALT and escalate to the human.
   - **intent_gap** — Root cause is inside `<frozen-after-approval>`. Revert code changes. Loop back to the human to resolve. Once resolved, read fully and follow `./step-02-plan.md` to re-run steps 2–4.
   - **bad_spec** — Root cause is outside `<frozen-after-approval>`. Before reverting code: extract KEEP instructions for positive preservation (what worked well and must survive re-derivation). Revert code changes. Read the `## Spec Change Log` in `{spec_file}` and strictly respect all logged constraints when amending the non-frozen sections that contain the root cause. Append a new change-log entry recording: the triggering finding, what was amended, the known-bad state avoided, and the KEEP instructions. Read fully and follow `./step-03-implement.md` to re-derive the code, then this step will run again.
   - **patch** — Auto-fix. These are the only findings that survive loopbacks. If the step-03 implementation subagent can be re-engaged with its context intact, send it all patch findings in one synchronous message — for each: the file, what is wrong, and what the fix must do. If it cannot be re-engaged, apply the patches yourself. Then re-run the checks in `{spec_file}`'s `## Verification` section, if present; if verification fails and the failure cannot be fixed, HALT and escalate to the human.
   - **defer** — Append one new **canonical** entry to `{{.deferred_work_file}}`. First determine `<n>`: read the file, find the highest existing `### DW-<number>` heading, and add 1. Do not modify existing entries or look for duplicates.
     ```markdown
     ### DW-<n>: <short one-line title>
     origin: deferred by review of `{spec_file}`, {date}
     source_spec: `{spec_file}`
     location: <file:line where the finding lives>
     severity: <high|medium|low>
     reason: <one sentence> — Evidence: <why this is real>
     status: open
     ```
     The `### DW-<n>:` heading and the `status:` line are **both mandatory and must not be simplified away**. `bmad-loop sweep` triage parses *only* entries carrying both, so an entry missing either is invisible to the deferred-work burn-down and will never be swept (upstream bmad-code-org/bmad-loop#304).
   - **reject** — Drop silently.

## NEXT

Read fully and follow `./step-05-present.md`
