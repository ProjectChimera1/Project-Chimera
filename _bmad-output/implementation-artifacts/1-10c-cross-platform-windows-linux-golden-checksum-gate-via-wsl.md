---
baseline_commit: 7e35e9e938f25c23bb0d786574513efec95f2e16
---

# Story 1.10c: Cross-platform Windows↔Linux golden-checksum gate via WSL

Status: review

<!-- Context engine analysis completed — comprehensive developer guide. Validation optional: run validate-create-story before dev-story. -->
<!-- 1.10c is the LAST M1 determinism-floor sibling (after 1.10a CI golden gate, 1.10b analyzer gate). It is an
     INFRASTRUCTURE/TOOLING story — it writes ZERO sim code and changes ZERO goldens. It proves the simulation
     produces byte-identical Fixed checksums on Linux as on Windows, using Alec's existing WSL/Ubuntu-24.04 — the
     real proof Fixed-point determinism holds cross-platform (the #1-ship-risk-adjacent gate, AR-37). The hard
     enabler is already true: the golden harness was deliberately built portable FOR THIS STORY (embedded-resource
     goldens, InvariantCulture parsing, '\n' separators, integer Fixed math). Closing this story GREEN closes M1. -->

## Story

As a solo developer who must keep determinism from silently breaking,
I want the golden-checksum harness also running on Linux via the existing WSL/Ubuntu, with the Windows and Linux checksum sequences diffed and any mismatch failing the gate,
so that any future change that breaks cross-platform reproducibility fails CI before it can ship.

## Acceptance Criteria

**Verbatim from `epics.md` (Story 1.10c, lines 758-762; covers AR-37, depends on 1.10b):**

> **Given** the existing WSL/Ubuntu with .NET installed **When** the golden-checksum harness runs on both Windows and Linux **Then** the two checksum sequences are diffed and are byte-identical, and a mismatch fails the gate

### Decomposed, testable acceptance criteria

- **AC1 — The harness runs on Linux via the existing WSL/Ubuntu.** The Tier-1 Godot-free golden-checksum suite (`ProjectChimera.Sim.Tests`) runs to completion inside WSL `Ubuntu-24.04` with the .NET 8 SDK installed, targeting `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` **by path**, with `--locked-mode` restore, `-c Release`. It installs **no Godot/GodotSharp**, never targets `godot.sln`/`godot.csproj`, and **never sets `CHIMERA_GOLDEN_RECORD`** (verify mode only — the Linux run must never re-record a golden).
- **AC2 — Windows and Linux checksum sequences are diffed and are byte-identical.** For all **four** committed goldens, the per-tick `SimChecksum` sequence produced on Linux equals the sequence produced on Windows. This is realized **transitively**: both platforms verify against the *same* committed LF/embedded golden (which *is* the Windows-recorded sequence), so a green WSL verify-run proves `Linux sequence == committed golden == Windows sequence`. The harness's own `GoldenChecksumReplay.CompareSequences` (exact `uint` equality, per tick) **is** the diff.
- **AC3 — A mismatch fails the gate, and the divergence is located.** If any tick's Linux-computed checksum differs from the committed golden, the WSL run **fails (non-zero exit)** and reports the **first diverging tick** (`expected` vs `actual`), and the orchestrating check propagates the non-zero exit. Proven once by an *induced* divergence (a deliberate one-tick/one-value perturbation in a scratch copy → WSL run RED → reverted), exactly as 1.10b proved its release gate with a deliberate `new System.Random()`.
- **AC4 — The cross-OS portability invariant is made permanent.** A Tier-1 guard test asserts the committed goldens are stored **LF-only** (zero `\r` bytes), so the three-layer line-ending neutralization (`godot/.gitattributes eol=lf` + embedded-resource load + `\r`-tolerant `ParseGolden`) cannot silently regress and reintroduce a spurious Win↔Linux diff.
- **AC5 — Documented, reproducible, and actually run (the AR-37 "the diff actually runs" requirement).** A committed **runbook** + **check script** document the one-time `.NET-in-WSL` prerequisite and the push-button procedure; the gate is **run once**, proven byte-identical, and the result is recorded in this story's Change Log (date, WSL distro + .NET version, verdict). **No sim code, `SimChecksum`, tick order, wire format, or any `*.golden.txt` changes** — every change is additive tooling/test/docs.
- **AC6 — (DECISION-GATED, see [Decisions for Alec](#decisions-for-alec-answer-before-or-during-dev) #1) optional always-on backstop.** *If Alec approves the add-on:* an `ubuntu-latest` sibling leg is added to `.github/workflows/determinism-gate.yml` that runs the same suite on every push as a continuous cross-platform signal, keeping the `tier1-golden-gate` job name stable. *If not:* the WSL check is the gate, triggered at release per the runbook (and AR-37's "not always-on cloud CI" is honored).

_Covers: **AR-37** (cross-platform determinism gate, Windows↔Linux golden diff via WSL). Depends on: **1.10b** (DONE — the analyzer gate; this is the next sibling in the same CI lane). **Hard milestone M1 completes when 1.1–1.10 (incl. 1.10a/1.10b/1.10c) are green** — closing this story GREEN closes M1._

---

## SCOPE — read this before coding

### ✅ IN scope (this story)
1. **Install the .NET 8 SDK inside the existing WSL `Ubuntu-24.04`** (the AR-37 prerequisite — currently *not* installed; see [Live environment facts](#live-environment-facts-probed-2026-06-25)). Document it (runbook) and/or script it (`wsl-dotnet-setup.sh`).
2. **A cross-platform check script** that runs the Tier-1 golden suite on **both** Windows and WSL/Ubuntu (verify mode), and **fails (non-zero exit) on any divergence**, printing a clear verdict.
3. **A runbook** (mirror `godot/tools/lan-determinism-runbook.md`) documenting the prereq, the procedure, how to read PASS/FAIL, and the rule: a RED run is a **real cross-platform determinism bug — fix the code, never re-record a golden**.
4. **A Tier-1 guard test** locking the LF-only golden invariant (AC4).
5. **Run the gate once, prove byte-identical, record it in the Change Log** (AC5). Unlike 1.9b's parked two-machine LAN gate, this one **CAN be fully closed now** (one machine + local WSL).
6. **(Decision-gated, #1)** an optional `ubuntu-latest` sibling leg in `determinism-gate.yml` as the always-on backstop.

### ❌ OUT of scope (do NOT do these here)
- **Do NOT change any `*.golden.txt`, `SimChecksum`, `GoldenChecksumReplay`, the tick order, the 60-tick interval, or any sim source.** This story is additive tooling/test/docs. If the WSL run is RED, that is a *finding* (a determinism bug to file/fix), **not** a license to edit a golden.
- **Do NOT fix the `AiOpponentSystem` float→Fixed debt** (the latent cross-platform hazard — see [Cross-platform risk analysis](#cross-platform-risk-analysis-the-one-thing-that-can-actually-diverge)). The current goldens deliberately keep the AI quiescent, so it never reaches the hash today. The float→Fixed migration is its own later work (D2 / the AI-determinism story). **Document the coverage caveat; do not widen scope to fix it.**
- **Do NOT install Godot/GodotSharp in WSL, and do NOT target `godot.sln`/`godot.csproj`.** Target the Godot-free Tier-1 test csproj by path, .NET 8 SDK only (AR-2/AR-35) — exactly like 1.10a's Windows job.
- **Do NOT set `CHIMERA_GOLDEN_RECORD` anywhere** in the script/CI. Record mode rewrites the *source* goldens (via `[CallerFilePath]`); on the Linux leg that would silently overwrite the committed baseline and the gate would prove nothing.
- **Do NOT add a `PackageReference` to `godot.csproj`** or any new NuGet dependency (would break the `DependencyHygieneTests` one-package guard and force a lock-file regen). The Linux leg uses the *existing* `packages.lock.json` + `--locked-mode`.
- **Do NOT build the D3 version-stamp consistency check** (`CurrentGameVersion`/`schema_version`/`checksum_algo_version`/`PROTOCOL_VERSION`/`min_game_version` moving together). The architecture lists it as another job of the "check-runner," but it is a **separate concern** from the Windows↔Linux golden diff — see Decision #3. Default: out of 1.10c.
- **Do NOT build the Linux *export* (dedicated-server/client build).** That is **Story 10.7** (FR-50, also "Covers AR-37"). 1.10c is the *test-harness* checksum diff, not the engine export — do not conflate them.

### Scope reconciliation (read this first)
- **The harness already exists and was built FOR this story — reuse it, do not re-author it.** `GoldenChecksumReplay.cs` loads goldens as **embedded resources** ("portable across Windows/Linux: no file paths, no line-ending fragility — required for the 1.10c cross-platform gate"), parses with `CultureInfo.InvariantCulture`, writes `'\n'` explicitly, and the parser `.Trim()`s each line (strips `\r`). The math hashed into `SimChecksum` is pure integer `Fixed.Raw` with manual little-endian byte-mixing (no `BitConverter`, no float). **Format, encoding, line-endings, endianness, and culture are already neutralized.** The only thing that *can* differ is the actual computed hash — which is the whole point of the gate.
- **"Diff the two sequences" = both platforms verify against the same committed golden.** You do **not** need to author a bespoke cross-machine sequence-diff (Decision #2). The committed golden *is* the Windows-recorded sequence; the WSL verify-run computes the Linux sequence and `CompareSequences` asserts it equals the golden tick-by-tick. Green-on-both ⇒ byte-identical. RED ⇒ the gate fails with the diverging tick. A literal "record-both-then-diff" is optional belt-and-suspenders only (and would need a *non-destructive* dump seam — record mode writes the source goldens, so it must NOT be reused for this).
- **The AC says "the existing WSL/Ubuntu," so the WSL leg is mandatory.** An `ubuntu-latest` GitHub-hosted job satisfies "runs on Linux" but is a *different, ephemeral* Linux, not Alec's WSL Ubuntu-24.04 (which is *also the Linux dedicated-server build host* per AR-37). The ubuntu-latest job is therefore an optional **add-on** (Decision #1/AC6), never a substitute for the WSL leg.

---

## Tasks / Subtasks

- [x] **Task 1 — Lock the cross-OS portability invariant as a Tier-1 guard test (AC: 4).**
  - [x] Add `godot/ProjectChimera.Sim.Tests/Meta/CrossPlatformGoldenGuardTests.cs` (new Tier-1 test, auto-globbed by the test SDK — no csproj change). Locate the four goldens via `[CallerFilePath]` (mirror `GoldenChecksumReplay.GoldenSourcePath` / the 1.10a `DependencyHygieneTests` pattern).
  - [x] Assert each `Golden/*.golden.txt` contains **zero `\r` bytes** (pure LF) — read raw bytes, assert `!bytes.Contains((byte)'\r')`, with an actionable message naming the file. This keeps the embedded-resource bytes identical on Windows and Linux checkouts. *(Done: glob `Golden/*.golden.txt` + per-file CR-byte assert, with a `>=4` floor guarding against a vacuous pass if `[CallerFilePath]` resolution drifts.)*
  - [x] (Recommended) Also assert `godot/.gitattributes` exists and declares `eol=lf` for the golden path, so the git-side normalization can't be deleted unnoticed. (Belt to the guard test's suspenders.) *(Done as a second `[Fact]`.)*
  - [x] Run `dotnet test … --filter FullyQualifiedName~CrossPlatformGolden` → green; verify teeth (temporarily inject a `\r` into a scratch string → fails → revert). *(Teeth verified: a scratch CRLF golden tripped the guard — "contains a carriage-return (\r, 0x0D) byte at offset 55" — then deleted; goldens untouched.)*

- [x] **Task 2 — Install + verify the .NET 8 SDK in WSL Ubuntu-24.04 (AC: 1) [the AR-37 prereq].**
  - [x] Install the **.NET 8 SDK ≥ 8.0.419** in WSL (the repo `global.json` pins `8.0.419` with `rollForward: latestFeature`, and it applies to `/mnt/d/...` too — a **lower feature band/patch will fail SDK resolution**, e.g. `8.0.118 < 8.0.419`). *(Installed **8.0.422** via `dotnet-install.sh --channel 8.0`.)*
  - [x] **⚠ Feed gotcha (likely the #1 time-sink):** Ubuntu 24.04's *built-in* feed (`apt install dotnet-sdk-8.0`) frequently ships a feature band **below `8.0.419`**, which then **fails `global.json` resolution**. Use a feed that carries the current `8.0.4xx`: either the **Microsoft package feed** (`packages.microsoft.com` → `apt install dotnet-sdk-8.0`) **or** `./dotnet-install.sh --channel 8.0` (always pulls the latest `8.0.x`). After install, confirm `dotnet --version` ≥ `8.0.419`. *(Confirmed live: apt candidate was **8.0.128** (band 1 < 4) AND sudo needs a password → both ruled out apt. Used `dotnet-install.sh --channel 8.0` → 8.0.422, no sudo.)*
  - [x] Prefer the **package/apt** route so `dotnet` is on the system `PATH` for a non-interactive `bash -lc` (the `dotnet-install.sh` route installs to `~/.dotnet` and needs a `PATH` export in the script). *(Superseded by reality — apt unavailable (sudo). dotnet-install route used; PATH persisted to **both `~/.profile` AND `~/.bashrc`** because `bash -lc` is a NON-interactive login shell and Ubuntu's `~/.bashrc` returns early for non-interactive shells, so a `~/.bashrc`-only export is never reached. The check worker also exports PATH explicitly, so the gate never depends on profile state.)*
  - [x] Capture the procedure in a committed `godot/tools/wsl-dotnet-setup.sh` (idempotent) and/or the runbook (Task 4). *(Done — idempotent, no-sudo, version-floor-aware; re-run proven to skip install + add only the missing profile entry.)*
  - [x] Verify: `wsl -d Ubuntu-24.04 -- bash -lc 'dotnet --version'` → `8.0.4xx`; and from the repo path, `wsl … 'cd /mnt/d/Projects/Project_Chimera && dotnet --version'` resolves the `global.json` SDK without error. *(Both verified: `8.0.422` + `GLOBALJSON_RESOLVED_OK`.)*

- [x] **Task 3 — Author the cross-platform determinism check script (AC: 1, 2, 3).**
  - [x] `godot/tools/cross-platform-determinism-check.ps1` (PowerShell — matches the repo's tooling convention; there are **zero** `.sh` scripts and the LAN tooling is all `.ps1`). It must:
    - Run the Tier-1 golden suite on **Windows**: `dotnet restore … --locked-mode` then `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release --no-restore` (or `--filter FullyQualifiedName~Golden` for the fast subset). Verify mode (no `CHIMERA_GOLDEN_RECORD`). *(Done; the `.ps1` also hard-refuses if `CHIMERA_GOLDEN_RECORD` is set.)*
    - Run the **same** suite in **WSL**: `wsl -d Ubuntu-24.04 -- bash -lc 'cd /mnt/d/Projects/Project_Chimera && dotnet restore … --locked-mode && dotnet test … -c Release --no-restore'` — with **isolated obj/bin** so the Linux build never reuses Windows intermediates (see the [obj/bin isolation gotcha](#wsl-build-gotchas-readbefore-writing-the-script)). Verify mode. *(Done via a committed bash WORKER `cross-platform-determinism-check.wsl.sh` invoked as `wsl -- bash <file> <repo-path>`. **Pivoted from inline `bash -lc` to a worker file** because PowerShell mangled inline-bash variable assignments across four quoting layers. **Isolation pivoted from `obj/wsl` to a separate WSL-native clone** (the story's blessed fallback) — `obj/wsl` hit CS0579 duplicate-AssemblyInfo because the SDK glob still swept the Windows `obj/`; the clone fully isolates, avoids the 9p flakiness, and never disturbs the Windows build.)*
    - **Translate the path** Windows→WSL (`D:\Projects\Project_Chimera` → `/mnt/d/Projects/Project_Chimera`); derive it, don't hardcode (`wsl wslpath` or string-map the drive). *(Done — `ConvertTo-WslPath` uses `wsl wslpath` with a drive-letter regex fallback.)*
    - **Exit non-zero** if *either* leg fails; print a clear final verdict — `✅ Windows↔Linux byte-identical (N goldens, M ticks each)` or `❌ CROSS-PLATFORM DESYNC` with the WSL `CompareSequences` first-divergence line surfaced. *(Done — verdict surfaces the first-divergence line via `Tee-Object` capture.)*
  - [x] Prove AC3 once: induce a divergence (e.g. temporarily edit one tick in a *scratch copy* of a golden, or a one-line sim perturbation behind a temp flag) → WSL leg RED, script exits non-zero, divergence located → **revert**. Record in the Change Log; commit nothing of the perturbation. *(Done: flipped one hex digit (`31E69403`→`31E69400`) of the first golden line in the WSL clone → Linux test RED, located: "Checksum drift at tick 1: expected 0x31E69400, actual 0x31E69403", exit 1 → reverted. The "actual" = the real Linux value = the committed Windows baseline, reconfirming parity. Perturbation never committed.)*

- [x] **Task 4 — Author the cross-platform determinism runbook (AC: 5).**
  - [x] `godot/tools/cross-platform-determinism-runbook.md`, mirroring the structure of `lan-determinism-runbook.md`:
    - **§0 What PASS means** (both legs green ⇒ byte-identical Fixed checksums Win↔Linux for the four goldens).
    - **§1 Prerequisite** — install .NET 8 (≥8.0.419) in WSL (Task 2); one-time.
    - **§2 How the gate works** — the transitive-diff explanation (committed golden = Windows sequence; WSL verify-run = Linux sequence; `CompareSequences` is the per-tick diff).
    - **§3 Run it** — `powershell -File godot/tools/cross-platform-determinism-check.ps1`.
    - **§4 Read the verdict.**
    - **§5 If it's RED** — it is a **real cross-platform determinism bug**. Do NOT re-record a golden. Suspect order: (1) `Fixed.FromFloat` double→float narrowing at the JSON boundary (the applier golden exercises it); (2) an unexpected float/culture reaching the hash; (3) if an AI-active golden was added, `AiOpponentSystem`'s float scoring (the known latent hazard).
    - **§6 Coverage caveat** — the four current goldens deliberately keep the AI quiescent, so this gate proves parity only for non-AI-float paths; its value scales with golden coverage (link the caveat below).
    - **§7 Record the result** in this story's Change Log. *(Done — all sections present, plus §8 (the always-on ubuntu leg) and §9 (troubleshooting incl. the CS0579/clone-isolation note).)*

- [x] **Task 5 — (DECISION #1) optional always-on `ubuntu-latest` backstop (AC: 6).** *(Alec chose **both** — WSL gate + ubuntu leg.)*
  - [x] *Only if Alec approves the add-on.* Add an `ubuntu-latest` leg to `.github/workflows/determinism-gate.yml` (a sibling job `tier1-golden-gate-linux`, **or** convert `tier1-golden-gate` to a `strategy.matrix.os: [windows-latest, ubuntu-latest]` — keep the job *name* `tier1-golden-gate` resolvable/stable per the workflow header). Reuse `setup-dotnet@v4` `8.0.419` + `dotnet restore … --locked-mode` + `dotnet test … -c Release`. Do **not** install Godot. Upload the `.trx` like the Windows leg. *(Done as a **sibling job** `tier1-golden-gate-linux` — chose sibling over matrix specifically to keep the `tier1-golden-gate` job name unchanged. Distinct artifact name `tier1-test-results-linux` to avoid an upload collision. Runs on every push (continuous signal); no Godot.)*
  - [x] Confirm the YAML parses; note ubuntu billing is 1× (vs Windows 2×) so the continuous Linux signal is cheap. *(YAML validated: jobs = `tier1-golden-gate` (windows), `tier1-golden-gate-linux` (ubuntu), `tier1-analyzer-gate` (windows). Goes live on the next push, like 1.10a did.)*

- [x] **Task 6 — Run the gate, prove byte-identical, log it (AC: 5).**
  - [x] Run `cross-platform-determinism-check.ps1` end-to-end → both legs green; the four goldens verify on WSL; `git status --short -- '*.golden.txt'` empty (no golden moved). *(Done — `legs: Windows=PASS, WSL=PASS`, exit 0; `GOLDENS_UNCHANGED`.)*
  - [x] Record in the Change Log: date, `Ubuntu-24.04` + the installed .NET version, the verdict line, and "M1 cross-platform gate GREEN." **This closes the AC live and closes M1.** *(Recorded below.)*

- [ ] **Task 7 — Code review + sprint status.** *(Sprint status set to `review`; the code review itself is the next phase, run by the user with a different LLM.)*
  - [ ] Run `gds-code-review` (3-layer adversarial, different LLM/fresh context recommended). Address findings.
  - [ ] On PASS, set this story `done` in `sprint-status.yaml`. Note that 1.10c being done means **Epic 1 / M1 is GREEN** (verify 1.1–1.10b are all `done`) and flag the Epic-1 retrospective as available.

- [x] **Task 8 — (DECISION #3 scope expansion) Version-stamp consistency check.** *(Alec chose **include it now**, overriding the story's "defer" recommendation.)*
  - [x] Add `godot/ProjectChimera.Sim.Tests/Meta/VersionStampConsistencyTests.cs` — the single place that pins the project's cross-version/cross-peer compatibility stamps so none drifts silently and a bump forces the "do siblings + goldens move too?" review.
  - [x] **Honest reality surfaced (probed live):** of the architecture's five named stamps, **two do not exist yet** (`CurrentGameVersion`, `schema_version` — D3.1 work) and one (`SimChecksum.AlgoVersion`) is already canonically pinned. So the guard pins the **five EXISTING** stamps — `SimChecksum.AlgoVersion=3`, `CanonicalModelHash.AlgoVersion=2`, `TickCommandPacket.PROTOCOL_VERSION=1`, `ReplayRecorder.VERSION=2`, `ContentPackageManifest.MinGameVersion="0.1"` — documents the two unbuilt D3.1 stamps, and **tripwires** `ScenarioData.schema_version` so it cannot land outside this registry. It does **not** build the unbuilt stamps (that is D3.1, out of scope). +5 Tier-1 tests, green on Windows and Linux.

---

## Dev Notes

### Developer context — why this story exists and the one insight that makes it easy
Determinism is the project's load-bearing invariant (NFR-4). Same-OS green (1.10a's Windows gate) is **necessary but not sufficient** — the determinism risk that actually ships the game broken is **Windows client vs Linux dedicated server producing different `Fixed` results** (a float or culture leak that rounds differently per-platform/JIT and silently desyncs MP). The only real proof Fixed-point holds is to run the harness on **both** OSes and confirm the checksum sequences are byte-identical. That is this story.

**The insight that makes it small:** the golden harness was **deliberately engineered for this exact gate**. Goldens are embedded resources (no file-path/line-ending fragility), parsing is `InvariantCulture`, separators are explicit `'\n'`, the parser strips `\r`, and `SimChecksum` hashes only integer `Fixed.Raw` via manual little-endian byte-mixing. So the transport is already cross-platform-safe. **Running the existing verify-mode suite in WSL *is* the Windows↔Linux diff** — because the committed goldens were recorded on Windows, a green WSL run literally asserts "Linux-computed checksums == Windows-recorded checksums," tick by tick. You are not building a diff engine; you are running the existing one on a second OS and wiring the prereq + the run + the proof.

### Cross-platform risk analysis (the ONE thing that can actually diverge)
The structural facts (from `SimChecksum.cs`, `FixedPoint.cs`): the hash ingests only `Fixed.Raw` ints, `int[]`, bools, and the `SimRng` `ulong` state — **zero float**. `Mix` masks bytes manually (endian-independent, no `BitConverter`). `Fixed` arithmetic is pure int/long shifts; `Fixed.Sqrt` is integer Newton's method. So format/encoding/endianness/culture are **not** the risk.

**The risk, prioritized:**
1. **[HIGHEST — latent, not active today] `AiOpponentSystem` uses raw `float` scoring in the live tick path.** It is system `[7]` in `SimulationHost._systems` — it ticks in **every** golden scenario. `ScoreLaunchAttack`/`ExecuteBestAction` compute and compare `float`s, whose results *can* differ across platform/JIT. **Why no golden fails today:** `GoldenScenario` deliberately starves the AI (no production building + zero ore → below the attack threshold, can't build/train) so it runs but **no-ops deterministically every tick**; the multifaction/applier/tie-break goldens likewise never drive the AI to act. **So float AI scoring never changes a hashed value in any current golden.** This is the known "AI float→Fixed debt" (advisory `CHM0005`/`CHM0001` in 1.10b's analyzer; tracked in `deferred-work.md` and `[[chimera-mp-disconnect-ai-takeover-reconnect]]`). **Implication for this story:** the gate proves parity only for scenarios that don't exercise AI float branches — document this caveat (Task 4 §6). Do **not** fix the AI here (out of scope).
2. **[MEDIUM] `Fixed.FromFloat` at the JSON deserialize boundary** (`FixedJsonConverter.Read`: `float f = (float)d; … Fixed.FromFloat(f)`; the `(int)(value * 65536)` cast). The `golden-applier-scenario` golden *does* exercise this (it applies a scenario from data). IEEE-754 single-rounding is spec-defined and deterministic on .NET x64, so low actual risk — but it is the one float-touch in a hashed golden's *setup*, so it's the prime suspect if a diff ever appears.
3. **[LOW — already mitigated] CultureInfo number formatting (the "1.3b locale fix").** `ScenarioDirector.cs` carries thresholds as raw `Fixed` ints with `InvariantCulture`/`NumberStyles.Integer` (proven by a de-DE comma-decimal test). Golden parsing also uses `InvariantCulture`. Not a risk in the goldens (they load empty triggers → `ScenarioDirector` early-returns), but it's the canonical culture hazard the codebase already fixed. (`Fixed.ToString()` uses `ToFloat().ToString("F4")` — culture-sensitive — but it's display/debug only, never hashed.)
4. **[LOW — already mitigated] Unstable sort.** `ScenarioDirector.EvaluateTriggers` uses `Array.Sort` but with an **explicit total-order comparator** (priority desc, then ascending declaration index) — safe. (The advisory `CHM0003` finding here is mitigated by the tie-break.) All combat/entity iteration is ascending-ID.
5. **[NONE] Hash endianness / DateTime.** `Mix` is endian-safe; no wall-clock in the hashed path.

**Bottom line:** a Win↔Linux diff should pass on day one for the four committed goldens. If it does *not*, work the suspect order above — and **never** "fix" it by editing a golden.

### Architecture compliance — AR-37 and the cadence it mandates
**AR-37 (verbatim, `epics.md:228`):** *"Cross-platform determinism gate: the golden-checksum harness runs on **both Windows and Linux** and the two `Fixed`-checksum sequences are diffed (the only real proof Fixed-point holds). Run by an AI-orchestrated check-workflow runner (triggered/scheduled, not always-on). **Prereq: install .NET inside the existing WSL/Ubuntu** (also the Linux dedicated-server build host). FR-39 is the #1 ship risk and a hard gate."*

**Operational sidecar (`game-architecture.Step5-cross-cutting-briefing.md:76-85`, paraphrased + key quotes):**
- *"the runner is an AI-orchestrated workflow (**not always-on cloud CI**). It runs the suites + replays + the Windows↔Linux comparison and reports/diagnoses. Day-to-day Tier-1 runs locally on the Windows PC; the cross-platform comparison runs via the workflow against a Linux env **when it matters**."*
- *"**Advisory, not blocking on `master`** — the repo auto-commits hourly (`[AutoSave]`); a hard pre-commit gate would fight that loop. **Hard enforcement only on a release branch.**"*
- *"Alec already has WSL/Ubuntu installed … M1 work is small: install .NET in that Ubuntu + run the check there + diff against Windows. Same WSL also hosts the Linux dedicated-server build."*
- **Adversarial residual risk (lines 109-116):** *"the AI-runner must have a **scheduled trigger or it silently never runs before releases**."* → whatever you build, the *trigger* must be reliable (a release-checklist step in the runbook at minimum; the optional ubuntu-latest leg makes it automatic — Decision #1).

**Boundaries this story must respect (must NOT break or duplicate):**
- **AR-2 / AR-35:** target the Godot-free Tier-1 csproj by path; `.NET 8` SDK only; never `godot.sln`/`godot.csproj`; never install Godot/GodotSharp; no new `PackageReference` (the `DependencyHygieneTests` one-package guard from 1.10a + the analyzer guards from 1.10b stay green); `--locked-mode` against the existing `packages.lock.json`.
- **AR-36 (1.10b):** the workflow `determinism-gate.yml` header *reserves sibling jobs and pins the `tier1-golden-gate` job name stable* — **add a job (or a matrix), do not re-author the workflow or duplicate the analyzer gate.**
- **AR-41:** add no telemetry/beacon; the cross-platform diff is a local/workflow check, not analytics.
- **AR-13 / AR-40:** `SimRng` and the two pinned M1 checksum forks are already folded into the checksum you diff — do not touch them.

### Live environment facts (probed 2026-06-25)
- **WSL present:** `Ubuntu-24.04`, WSL **version 2**, currently `Stopped` (starts on first `wsl` invocation). Matches `[[linux-env-for-crossplatform-check]]`.
- **.NET is NOT yet installed in WSL** (`wsl -- bash -lc 'command -v dotnet …'` → `NO_DOTNET_IN_WSL`). **Task 2 is real, not a no-op.**
- **`git config core.autocrlf = true`** at the repo level — **BUT** `godot/.gitattributes` (`* text=auto eol=lf`) overrides it for everything under `godot/`. `git ls-files --eol 'godot/.../Golden/*.golden.txt'` → all four are **`i/lf w/lf attr/text=auto eol=lf`** (LF in index *and* working tree, on Windows). So the goldens check out LF on both OSes. The AC4 guard test makes this permanent. *(Note: `.gitattributes` lives under `godot/`, not repo root — it still covers the goldens since they're under `godot/`.)*
- **No `ubuntu`/`linux` reference exists in `.github/`** — CI is windows-only today (`tier1-golden-gate` + `tier1-analyzer-gate`, both `windows-latest`). The Linux leg (CI or WSL) is genuinely net-new.
- **No `RuntimeIdentifier`/RID anywhere; single TFM `net8.0`.** No build-determinism MSBuild flags (`Deterministic`/`ContinuousIntegrationBuild`/`PathMap`) are mandated by the architecture — AR-37's proof rests on **Fixed-point + InvariantCulture discipline**, not byte-reproducible build artifacts. Don't add RID/flags unless a real divergence forces it.

### WSL build gotchas (read BEFORE writing the script)
- **`global.json` applies in WSL too.** Running `dotnet` from `/mnt/d/Projects/Project_Chimera` reads the repo-root `global.json` (`8.0.419`, `rollForward: latestFeature`). WSL must have an **8.0.x ≥ 8.0.419** SDK or SDK resolution fails. (rollForward `latestFeature` rolls *up* within `8.0`, never down to a lower patch.)
- **Windows and WSL must NOT share `obj/`/`bin/`.** Building the same project from `/mnt/d` after a Windows build reuses Windows-generated intermediates (`obj/project.assets.json`, host-specific paths) → stale/false results or build errors. Isolate the Linux outputs, e.g. `dotnet … -p:BaseIntermediateOutputPath=obj/wsl/ -p:BaseOutputPath=bin/wsl/` (trailing slashes required), **or** run the WSL leg in a separate WSL-native working copy (`git clone` into `~/`). The `.gitignore` `**/obj/` + `**/bin/` already covers any `obj/wsl`/`bin/wsl`. Isolated-output-path is the lower-friction default; the separate-clone fallback is for if `/mnt/d` builds prove flaky (the 9p mount is slow and case-sensitivity/permission quirks exist).
- **`--locked-mode` + isolated obj:** restore writes the assets to the custom `obj` path; the lock file is read from the project root regardless — fine. Mirror CI: explicit `dotnet restore … --locked-mode` then `dotnet test … --no-restore`.
- **PATH in `bash -lc`:** the apt/package .NET install puts `dotnet` on the system PATH so `wsl … bash -lc 'dotnet …'` just works; the `dotnet-install.sh` route needs `export PATH="$HOME/.dotnet:$PATH"` in the command.

### File structure requirements
**Create:**
- `godot/ProjectChimera.Sim.Tests/Meta/CrossPlatformGoldenGuardTests.cs` — LF-only golden invariant (AC4). (`Meta/` already exists from 1.10a/1.10b.)
- `godot/tools/cross-platform-determinism-check.ps1` — the two-OS check + verdict (AC1-3).
- `godot/tools/cross-platform-determinism-runbook.md` — the procedure + PASS/FAIL semantics (AC5).
- `godot/tools/wsl-dotnet-setup.sh` — idempotent .NET-8-in-WSL installer (AC1; optional if the runbook documents the apt steps inline).

**Edit (only if Decision #1 = yes):**
- `.github/workflows/determinism-gate.yml` — add the `ubuntu-latest` sibling leg (keep `tier1-golden-gate` name stable). **No other workflow change.**

**Do NOT touch:** any `*.golden.txt`; `GoldenChecksumReplay.cs`/`GoldenChecksumReplayTests.cs`; `SimChecksum.cs`; `FixedPoint.cs`; `godot.csproj`; `SimSources.props`; the existing `tier1-golden-gate`/`tier1-analyzer-gate` jobs; `godot/.gitattributes` (read it, don't rewrite it).

**Solution note:** the WSL leg builds the Tier-1 csproj **by path** (`godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj`), never `godot.sln`/`godot.csproj` — same as the Windows CI lane.

### Testing requirements
- **AC4 guard test = Tier-1 xUnit, Godot-free.** Assert the invariant as a test that runs everywhere `dotnet test` runs (the project's guard-test culture: `DependencyHygieneTests`, `SimChecksumCoverageGuardTest`, `GodotFreeBoundaryTest`, `AnalyzerGateGuardTests`). Use `[CallerFilePath]` + `Path.Combine` for portable paths — and verify it passes **on the WSL run too** (it will: it just reads bytes).
- **The cross-platform check itself is a script-run, not an xUnit test** (it shells into WSL). Its proof is the recorded Change-Log run (AC5) + the one-time induced-divergence demonstration (AC3) — mirror 1.10b's "deliberate-violation, recorded, reverted" pattern.
- **After every change**, re-run the exact Windows CI command `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release` → suite green (was **~203** at 1.10b close; **do not hardcode the count** — rely on xUnit's exit code) and `git status --short -- '*.golden.txt'` empty.

### Previous-story intelligence (1.10a + 1.10b — both DONE, code-reviewed PASS)
- **1.10a built the CI lane + the golden harness wiring + reproducibility pins** this story reuses: `determinism-gate.yml` (job `tier1-golden-gate`, `windows-latest`, `setup-dotnet@v4` **`8.0.419`**, `dotnet restore … --locked-mode`, targets the Godot-free test csproj **by path**, never installs Godot). `global.json` pins SDK `8.0.419` (`rollForward: latestFeature`). `godot/ProjectChimera.Sim.Tests/packages.lock.json` exists; CI restores `--locked-mode`. The header comment **explicitly reserves 1.10c as a sibling** and says "keep `tier1-golden-gate` stable." 1.10a also wrote the runbook precedent indirectly and **explicitly deferred the golden line-ending parity question to 1.10c** (the only cross-platform item it punted).
- **1.10b added the analyzer sibling job** (`tier1-analyzer-gate`) without disturbing `tier1-golden-gate`, and proved its release gate by a **deliberate violation, recorded then reverted** — the exact pattern AC3 should follow. It also surfaced (via the new `CHM0006`) the **A17 culture nondeterminism** of `float.Parse`/`float.ToString` — the same class of bug a Win↔Linux diff is the runtime backstop for.
- **Conventions to respect (both stories):** never "fix" a red gate by re-recording a golden; assert invariants as Tier-1 guard tests, not CI-only shell steps; no hardcoded test counts; never set `CHIMERA_GOLDEN_RECORD`; the `[CallerFilePath]` path-resolution works on the CI checkout *and* on the WSL `/mnt/d` path; SDK pinning is reproducibility hygiene (determinism is integer math, SDK-independent).

### Git intelligence
- The repo auto-commits hourly as `[AutoSave] <timestamp>` (~24/day); story work lands inside that stream. A red CI (or a red WSL check) does **not** block the local commit/push — it's a signal. That's why the cross-platform gate is "advisory on master, hard on release" (AR-37): the [AutoSave] loop must never be blocked.
- Build/CI artifacts live at `.github/workflows/determinism-gate.yml`, `global.json` (root), `godot/ProjectChimera.Sim.Tests/packages.lock.json`, `godot/.gitattributes`, `godot/.editorconfig`. Tooling/runbooks live in `godot/tools/` (`lan-determinism-runbook.md`, `lan-desync-smoke.ps1`, `loopback-desync-smoke.{ps1,cmd}`) — the new runbook + script join them.
- `baseline_commit` for this story: `7e35e9e`.

### Project Context Rules (from `_bmad-output/project-context.md`)
- **Sim/Presentation boundary is sacred + Godot-free Tier-1 is the whole point** (AR-35): the Linux leg compiles the pure-sim source via the test csproj, no engine. Preserve it.
- **`Fixed` (16.16) is the only sim numeric type;** integer math is what makes the checksum platform-independent — this gate *verifies* that property end-to-end across OSes.
- **Determinism rules** (ascending-ID iteration, no `Dictionary`/`HashSet` enumeration in sim order, no wall-clock, seeded `SimRng` only, `InvariantCulture`) are exactly what a Win↔Linux divergence would expose if violated.
- **Dependency discipline:** sole shipped dep `NakamaClient 3.13.0`; test/tool deps isolated; prefer in-repo over new deps. The Linux leg adds **no** dependency.
- **Engine/runtime:** Godot 4.6.3, `net8.0`; project files are `godot.csproj`/`godot.sln` (this story does **not** touch them). Brownfield style: reuse the harness, small additive slice, respect determinism constraints.

### References
- `_bmad-output/planning-artifacts/epics.md:752-764` — Story 1.10c (statement, AC, "Covers AR-37 / Depends on 1.10b", M1 close note). `:228` — **AR-37** verbatim. `:226-227` — AR-35/AR-36. `:179` — AR-2. `:234` — AR-41. `:174-175` — M1 must be GREEN before the D1 strangler. `:2580-2596` — **Story 10.7** (Linux *export*, FR-50, also "Covers AR-37") — the sibling NOT to conflate with this.
- `_bmad-output/game-architecture.Step5-cross-cutting-briefing.md:47-49,76-87,109-116,127-130` — the AR-37 rationale/operational sidecar: "not always-on cloud CI," "advisory master / hard release," ".NET-in-WSL prereq," the "scheduled trigger or it silently never runs" residual risk, and the M1 build-sequence step "AI check-runner + Win↔Linux comparison."
- `_bmad-output/game-architecture.md:1310-1314` — mirror prose: "byte-identical Fixed checksums," "no float/culture leaked," "InvariantCulture pinned process-wide."
- `_bmad-output/planning-artifacts/prds/prd-Project_Chimera-2026-06-05/prd.md` — **FR-39** (§4.9:282, the #1-risk LAN determinism gate), **FR-47** (§4.10:299, CI regression guard), **FR-44** (§4.10:296), **FR-50** (§4.10:307, Linux export); §6.2:374 M1 definition; :388 zero-desync hard gate.
- `_bmad-output/implementation-artifacts/1-10a-…md` — the CI lane, `global.json`/`packages.lock.json`/`--locked-mode`, the `[CallerFilePath]` guard-test pattern, the explicit deferral of line-ending parity to 1.10c (`:313`), the "keep job name stable / sibling jobs" reservation.
- `_bmad-output/implementation-artifacts/1-10b-…md` — the sibling-job precedent (`tier1-analyzer-gate` added without touching `tier1-golden-gate`), the deliberate-violation→record→revert proof pattern, and `CHM0006` (the A17 float/culture finding the cross-platform diff backstops).
- Current code: `godot/ProjectChimera.Sim.Tests/Golden/GoldenChecksumReplay.cs` (embedded-resource `LoadGolden`, `CompareSequences`, `ParseGolden` `\r`-tolerant, `RecordEnvVar=CHIMERA_GOLDEN_RECORD`, 32-bit `uint` `Sample`); `Golden/*.golden.txt` (4 files, LF, UTF-8 no BOM, `# header` + `"<tick> <HEX8>"` lines, trailing `\n`); `Golden/GoldenChecksumReplayTests.cs` + `MultiFactionGoldenTests`/`GoldenApplierScenarioTests`/`SameTickTieBreakGoldenTests`; `src/Core/SimChecksum.cs` (integer-only, endian-safe `Mix`); `src/Core/FixedPoint.cs`; `src/Core/Definitions/FixedJsonConverter.cs` (the one float boundary); `src/AI/AiOpponentSystem.cs` (the latent float hazard); `.github/workflows/determinism-gate.yml` (the lane to extend); `godot/.gitattributes` (`eol=lf`); `global.json` (SDK `8.0.419`); `godot/tools/lan-determinism-runbook.md` (runbook precedent to mirror).
- Memory: `[[linux-env-for-crossplatform-check]]` (WSL/Ubuntu present, only .NET-in-WSL + run-the-check is new — confirmed live), `[[project-chimera-gds-planning-chain]]`, `[[banned-api-aot-analyzer-tooling]]`, `[[chimera-mp-disconnect-ai-takeover-reconnect]]` (the AI float→Fixed debt).

---

## Decisions for Alec (answer before or during dev)

> These are saved per the workflow's "save questions for the end." None block starting Tasks 1-4; Decision #1 gates Task 5.

1. **Add an always-on `ubuntu-latest` CI leg, or WSL-only?** *(gates AC6/Task 5)*
   - **Recommended: do BOTH.** Deliver the WSL leg (AC-mandated, tests your *real* Linux dedicated-server host, AR-37-faithful) **and** add the cheap `ubuntu-latest` leg to `determinism-gate.yml` as the always-on backstop. The ubuntu leg directly resolves AR-37's own residual risk ("the runner must have a scheduled trigger or it silently never runs before releases") at near-zero cost (ubuntu bills 1× vs Windows 2×), giving continuous cross-platform proof on every push between your manual WSL runs.
   - **Alternative (AR-37-literal): WSL-only.** AR-37 explicitly says "not always-on cloud CI." If you want to honor that literally, skip the ubuntu leg; the WSL check is the gate, triggered by a release-checklist step in the runbook. *Downside:* relies on the runbook being run before releases (the residual risk AR-37 itself flagged).
   - *My lean: both — it's the strongest and the ubuntu cost is trivial.*

2. **"Diff the two sequences": transitive (recommended) or literal record-and-compare?**
   - **Recommended: transitive** — both platforms verify against the same committed golden; `CompareSequences` is the per-tick diff; green-on-both ⇒ byte-identical. Zero harness changes; it's exactly what the harness was built for.
   - **Alternative: literal** — also dump each platform's computed sequence to a scratch file and `diff` them. Belt-and-suspenders, but needs a *non-destructive* dump seam (record mode writes the source goldens, so it can't be reused). More code for marginal assurance.
   - *My lean: transitive; add the literal dump later only if a real divergence ever needs forensic detail.*

3. **Fold in the D3 version-stamp consistency check?** The architecture lists "checks the version stamps move together" (`CurrentGameVersion`/`schema_version`/`checksum_algo_version`/`PROTOCOL_VERSION`/`min_game_version`) as another job of the check-runner.
   - **Recommended: out of 1.10c** — it's a separate concern from the Windows↔Linux golden diff and would bloat this story. File it as its own small follow-up.
   - *My lean: defer.*

4. **How should "hard enforcement on a release branch" be realized for the WSL leg?** If Decision #1 = ubuntu-too, the ubuntu leg can be release-gated in CI mirroring 1.10b (`release/**` + `workflow_dispatch`). For the WSL leg specifically (CI can't run WSL), "hard on release" = a documented **release-checklist gate** in the runbook (the WSL check must be green before cutting a release). *My lean: runbook release-checklist gate for WSL + (if #1=yes) a release-gated ubuntu leg in CI.*

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (Claude Opus 4.8)

### Debug Log References

Live commands run during dev (key ones):
- WSL probe — `apt-cache policy dotnet-sdk-8.0` → candidate `8.0.128` (band 1 < 4); `sudo -n` → needs password; `dotnet` → none. Both findings ruled out the apt route.
- `.NET` install — `wsl-dotnet-setup.sh` → `dotnet-install.sh --channel 8.0` → **8.0.422** to `~/.dotnet`.
- PATH fix — `bash -lc` (non-interactive login) didn't see `~/.bashrc` export; persisted to `~/.profile` too → `dotnet --version` = 8.0.422 + `GLOBALJSON_RESOLVED_OK`.
- Isolation pivots — `obj/wsl` redirect → **CS0579 duplicate-AssemblyInfo** (SDK glob still swept Windows `obj/`); `-p:DefaultItemExcludes` fought 4 quoting layers; **pivoted to a separate WSL-native clone** → clean 203-green Linux run.
- Inline-bash pivot — PowerShell mangled inline `bash -lc` variable assignments (`D=…` arrived empty) → moved Linux logic into committed `*.wsl.sh` worker files invoked as `bash <file> <arg>`.
- AC3 — perturbed first golden tick in the clone → `Checksum drift at tick 1: expected 0x31E69400, actual 0x31E69403`, exit 1 → reverted.
- Final gate — `cross-platform-determinism-check.ps1` → `legs: Windows=PASS, WSL=PASS`, exit 0; `git status --short -- '*.golden.txt'` empty.

### Completion Notes List

**Outcome: all 6 ACs satisfied; M1 cross-platform gate is GREEN (Win↔Linux byte-identical).** This story wrote ZERO sim code and changed ZERO goldens — every change is additive tooling/test/docs (+ CI + `.gitignore`).

- **AC1 (harness runs on Linux):** .NET 8.0.422 installed in WSL Ubuntu-24.04; Tier-1 suite runs to completion there, targeting the Godot-free csproj by path, `--locked-mode`, `-c Release`, no Godot, never `CHIMERA_GOLDEN_RECORD`.
- **AC2 (byte-identical, transitively):** all four committed goldens verify GREEN on Linux against the committed (Windows-recorded) sequences → `Linux == committed golden == Windows`, tick by tick.
- **AC3 (mismatch fails + locates):** proven by an induced one-value perturbation in the WSL clone → RED, first divergence located, non-zero exit, reverted (mirrors 1.10b's deliberate-violation proof). The `.ps1` propagates the non-zero by construction.
- **AC4 (LF invariant permanent):** `CrossPlatformGoldenGuardTests` asserts every golden is LF-only (zero `\r`) + `.gitattributes` declares `eol=lf`; teeth verified.
- **AC5 (documented + run + recorded):** runbook + check script committed; gate run once, green, recorded (Change Log below).
- **AC6 (always-on backstop — Decision #1=both):** `tier1-golden-gate-linux` (ubuntu-latest) sibling job added to `determinism-gate.yml`, runs the same suite every push.

**Decisions resolved (asked up front; the two that change the build):**
- **#1 ubuntu CI leg → BOTH.** Shipped the WSL gate AND the always-on `ubuntu-latest` leg.
- **#3 version-stamp check → INCLUDE NOW** (scope expansion, overriding the story's "defer"). Built `VersionStampConsistencyTests` (Task 8). **Surfaced the honest reality:** 2 of the 5 named stamps don't exist yet (D3.1) and 1 is already pinned — so the guard pins the 5 existing compatibility stamps + tripwires the unbuilt `schema_version`; it does NOT build the unbuilt D3.1 stamps.
- **#2 "diff" → TRANSITIVE** (both OSes verify the same committed golden; `CompareSequences` is the diff). **#4 hard-on-release → runbook release-checklist** for the WSL leg (CI can't run WSL) + the every-push ubuntu leg as the cloud signal.

**Key engineering pivots (both away from the story's first-guess approach, for documented reasons):**
1. **WSL isolation: separate clone, not `obj/wsl`.** The `obj/wsl` redirect hit CS0579 because the SDK compile-glob still swept the Windows `obj/`'s stale `AssemblyInfo.cs`; the `DefaultItemExcludes` escape fought PowerShell→wsl→bash→MSBuild quoting. The story's blessed fallback (a WSL-native clone of committed HEAD) fully isolates, dodges 9p flakiness, and never disturbs the Windows build.
2. **WSL invocation: committed worker files, not inline `bash -lc`.** PowerShell mangled inline-bash variable assignments. The `.ps1` orchestrates and calls `cross-platform-determinism-check.wsl.sh` / `wsl-dotnet-setup.sh` as `wsl -- bash <file> <args>` (clean, no embedded quoting).

**Coverage caveat (documented, NOT fixed — out of scope):** the four goldens keep the AI quiescent, so `AiOpponentSystem`'s `float` scoring never reaches the hash today. This gate proves parity only for non-AI-float paths; the AI `float`→`Fixed` debt is its own later work (D2). Runbook §6 records this.

**Regression status:** Tier-1 **210 green on Windows** (was ~203 at 1.10b close; +2 LF guard, +5 version-stamp), **203 green on Linux** (committed HEAD clone; the +7 new guards land on Linux via the ubuntu CI leg once pushed). Goldens byte-identical/unmoved.

### File List

**Created:**
- `godot/ProjectChimera.Sim.Tests/Meta/CrossPlatformGoldenGuardTests.cs` — AC4 LF-only golden invariant (Tier-1 guard, 2 tests).
- `godot/ProjectChimera.Sim.Tests/Meta/VersionStampConsistencyTests.cs` — Decision #3 version-stamp consistency registry (Tier-1 guard, 5 tests).
- `godot/tools/wsl-dotnet-setup.sh` — idempotent no-sudo .NET-8-in-WSL installer (AC1 prereq).
- `godot/tools/cross-platform-determinism-check.ps1` — the two-OS check orchestrator + verdict (AC1–3).
- `godot/tools/cross-platform-determinism-check.wsl.sh` — WSL/Linux worker: clone + restore + verify-mode test (isolation).
- `godot/tools/cross-platform-determinism-runbook.md` — procedure + PASS/FAIL semantics + coverage caveat (AC5).

**Modified:**
- `.github/workflows/determinism-gate.yml` — added the `tier1-golden-gate-linux` (ubuntu-latest) sibling job; `tier1-golden-gate` + `tier1-analyzer-gate` left untouched (AC6).
- `godot/.gitignore` — added `TestResults/` + `*.trx` (test-output hygiene for the new check script).

**Unchanged (verified):** all `*.golden.txt`, `SimChecksum.cs`, `GoldenChecksumReplay.cs`, `FixedPoint.cs`, `godot.csproj`, `SimSources.props`, `godot/.gitattributes`, the existing CI jobs.

### Change Log

- **2026-06-25 — Story 1.10c implemented; M1 cross-platform gate GREEN.** Built the Windows↔Linux golden-checksum gate (additive tooling/test/docs only): AC4 LF-only guard test (teeth verified), .NET 8.0.422 installed in WSL Ubuntu-24.04 via no-sudo `dotnet-install.sh`, the `.ps1`+`.wsl.sh` two-OS check (WSL-native-clone isolation), the runbook, and (Decision #1) the always-on `ubuntu-latest` CI sibling leg. AC3 proven via an induced+reverted divergence. **Decision #3 scope expansion:** added `VersionStampConsistencyTests` (pins the 5 existing version stamps; documents/tripwires the 2 unbuilt D3.1 stamps). Tier-1 210 green (Windows) / 203 green (Linux clone).
- **2026-06-25 — AC5 cross-platform gate RUN (recorded):** `powershell -File godot/tools/cross-platform-determinism-check.ps1` → **`legs: Windows=PASS, WSL=PASS` → ✅ Windows↔Linux byte-identical**, exit 0. Environment: **WSL `Ubuntu-24.04`, .NET SDK `8.0.422`** (≥ the `global.json` `8.0.419` floor). All four committed goldens verified byte-identical on Linux vs the Windows-recorded baseline; `git status --short -- '*.golden.txt'` empty (no golden moved). **M1 cross-platform determinism gate (AR-37) is GREEN — closing this story closes M1 (pending code review).**

---

## Review Findings

_Code review 2026-06-25 (`gds-code-review`, 3-layer adversarial — Blind Hunter / Edge-Case Hunter / Acceptance Auditor, all Claude Opus 4.8, fresh/no-context; diff baseline `7e35e9e`). **Acceptance Auditor verdict: all 6 ACs satisfied, every "do NOT" scope rule clean**; the 5 pinned version-stamp values, the four LF-only goldens, `ScenarioData.schema_version` absence, and "no golden/sim file moved" were independently re-verified against source. One confirmed (reproduced) defect + two cheap hardening patches; 10 findings dismissed with reasons below._

### Patches (open)

- [ ] [Review][Patch] Verdict aggregation crashes under `Set-StrictMode -Version Latest` on single-leg runs — `$ran = @($windowsPassed, $wslPassed) | Where-Object {…}` collapses to a scalar `[bool]` when one leg is skipped, so `$ran.Count` throws *"The property 'Count' cannot be found on this object."* **Reproduced in Windows PowerShell 5.1 AND PowerShell 7.** Breaks both advertised diagnostic switches (`-SkipWindows`/`-SkipWsl`) and the runbook §5 `-SkipWindows` RED-iteration path; a Windows-only run that actually PASSES exits non-zero with a cryptic error. The both-legs path is unaffected, so the recorded AC5 GREEN run stays valid. Fix: wrap the whole pipeline in an outer `@()` → `$ran = @(@($windowsPassed, $wslPassed) | Where-Object { $_ -ne $null })`. [godot/tools/cross-platform-determinism-check.ps1:115]
- [ ] [Review][Patch] No guard that the destructive WSL clone dir differs from the source repo before `git clean -fdx` / `rm -rf "$CLONE"`. Near-zero probability (both paths effectively hardcoded) but catastrophic if a future `$HOME` ever makes `$CLONE == $SRC` (`clean -fdx` wipes ignored files incl. uncommitted scratch; `rm -rf` deletes the source). Fix: assert `[ "$CLONE" != "$SRC" ]` (and that `$CLONE` is under `$HOME`) before the destructive block. [godot/tools/cross-platform-determinism-check.wsl.sh:50]
- [ ] [Review][Patch] Runbook §5 ("Fix the code, then re-run §3") omits that the WSL leg builds from **committed HEAD** (documented in §2 but not restated at point of use), so an uncommitted fix silently isn't tested and the leg re-reports the same RED. Fix: add a one-line "commit your fix first — the WSL leg clones committed HEAD" note in §5. [godot/tools/cross-platform-determinism-runbook.md §5]

### Dismissed (considered, not actioned)

- `git fetch … HEAD` from a `file://` working-tree remote is fragile for a detached-HEAD source — repo lives on `master`; the script's exit code stays correct; no clean fix worth the added complexity.
- `sdk_satisfies_globaljson` regex rejects a hypothetical 4-digit feature band `8.0.1000` — .NET 8.0 ships only 3-digit bands and is near EOL; unreachable. Correctly accepts 8.0.419/8.0.422 and rejects 8.0.128/8.0.3xx (the cases that matter).
- Vacuous PASS if `dotnet test` discovered zero tests — xUnit exits non-zero on no-match; the story deliberately avoided a hardcoded min-count.
- New `tier1-golden-gate-linux` job doesn't set `DOTNET_CLI_TELEMETRY_OPTOUT` — matches the pre-existing windows jobs; patching only the new leg would create a fresh in-file asymmetry. (.NET CLI telemetry ≠ the project beacon AR-41 forbids.)
- AC3 first-divergence echo is regex-coupled to the drift message — regex verified to match the real `GoldenChecksumReplay.DescribeDivergence` text today; the gate's non-zero-exit propagation is independent of it.
- ubuntu CI leg committed but not yet observed green — empirically de-risked: the WSL leg already ran `dotnet restore --locked-mode` + the suite on Linux GREEN, so cross-platform locked-restore works; will surface on the next push (as 1.10a did).
- `SimChecksum.AlgoVersion` pinned in two guards (`VersionStampConsistencyTests` + `SimChecksumCoverageGuardTest`) — intentional single-view registry, documented in the test ("a deliberate bump must update BOTH").
- Windows-leg `dotnet restore` failure `throw`s past the verdict block (asymmetric vs the WSL leg) — exit stays non-zero; surfacing a hard environment error raw is acceptable.
- Unquoted `$wslRepo`/`$wslWorker` passed to `wsl … bash` + UNC edge in `ConvertTo-WslPath` — current repo path has no spaces; UNC fails loudly; latent only.
- CI can't prove the goldens were Windows-recorded — inherent to the golden approach; mitigated by verify-only + never-record + the new AC4 LF guard; out of scope for this story.
