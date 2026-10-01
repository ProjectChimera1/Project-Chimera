# Trial execution: checks (a), (b) and (c) as one schedule

2026-10-01. Inputs, all read in full: `plan-a-native-sim.md` (**A**), `plan-b-match-hud.md` (**B**), `plan-c-runtime-terrain.md` (**C**), each revision 2;
`HANDOFF.md`; the Unreal Spec ("How it is built", "Non-negotiables"). The plans remain the task specs of record; this file sets the order, the
shared pieces, and the overrides listed in §3. Paths: **R** = `D:/Projects/Project_Chimera`, **U** = `D:/Projects/Chimera-Unreal`, **P** = `U/ProjectChimera`,
**H** = `U/ChimeraHud`, **T** = `U/ChimeraTerrain` (B and C each use `T` for something else, so this file spells paths out), **EV** =
`R/docs/unreal-move/trial-checks/evidence`, **KIT** = `R/tools/unreal-trial`, **LOCK** = `bash U/ue_lock.sh`, only from Git Bash. Task ids are the plans'
(A0-A14, T0-T10, C0-C14, CF). **S0-S4** and **X1** are added here. **UNVERIFIED** = an estimate or claim that only a run settles.

## 0. In short
- The UE lock is the critical path (about 9-17 h of serialised holds, §1.3); everything else runs beside it in three lanes. Phase 0 builds the shared
  pieces; Phase 1 answers each check's riskiest unknown first: NativeAOT inside Unreal (X1), RMC on 5.8.3 (C1, C3), Slate captured 1:1 (T3).
- 23 cross-plan conflicts are resolved in §3. Two would have failed runs outright: Unreal's non-forced exit always returns 0 (A and C rely on it),
  and C's log scan fails every clean run.
- Alec: five decisions up front with recommendations ("proceed" adopts all), one after B's T0 with an image, four contingent ones (§5).

## 1. Schedule

### 1.1 Job classes and the lock rule
| Class | What | Rule |
|---|---|---|
| Heavy | `Build.bat`; any `UnrealEditor*` process (editor, `-game`, `-Cmd` automation, Python commandlet); RunUAT steps; packaged-exe runs; **plus** full T1 (`dotnet test` Sim.Tests), every ILC publish (A0 aotsmoke, X1, A5, A6, A10, A13) and every Godot run (A0/A1 smoke, A8, A8b, A14) | takes LOCK, one at a time |
| Measure | A12, B T8's `hud_cost`, C12 | LOCK `--measure`, Phase 4 only, nothing else dispatched, only the measuring agent active |
| Light | `dotnet build`, CLI `gen`/`run`, the C++ harness (A7), Python compare and calibrate, headless Chromium (B T0, T2), git, fetches (C0, C6), short ffmpeg encodes | no lock |

T1, ILC and Godot go under the lock because A's rule (T1 never runs while LOCK is held, during an ILC publish or a Godot run; A §4 Rules) is enforceable no other way
once B and C use the lock, and because 2.9 GB of 15.9 GB was free today with no Unreal running (measured 2026-10-01). **Dispatch order:** (1) first-light gates;
(2) short holds that unblock a waiting agent; (3) long holds (warm-ups, the commandlet, packaging) while the other lanes do non-UE work or wait on a decision.
Holds are released between runs; a loop waits ≥ 10 s outside the lock before re-acquiring; no check kills a process another check started.

### 1.2 Phases
| Phase | Non-UE lanes (parallel) | LOCK queue, in order | Exit | Sent to Alec (SendUserFile, render) |
|---|---|---|---|---|
| **0 Shared** (~½ day) | S0, S2 (main session); S3, S4 (sonnet); A0's script part; B T0 and T1; C0 once S3 has written T | S1 selftest; S3's two empty-project builds | `LOCK SELFTEST OK`; H and T build `Result: Succeeded`; kit tests pass; S0 commit pushed | D1-D5, one message (§5) |
| **1 First light** (~1 day) | A: A0 → A1 → A2. B: T0 ∥ T1. C: C0 → C2/C3 code; C6 fetch | X1 → C1 (Editor + Game) → T3 steps 2-5 and 7-10 (step 6 once T0 has written `world_layer.png`) → A0's T1, Godot smoke, aotsmoke → A9 (after X1) → C2 → C3 (BG warm-up ≤ 60 min, G1, FAIL test) → A1 and A2's T1 and smoke | X1 smoke 5/5 (else D7); C1 and C3 `G1 PASS` (else CF); T3 10/10; A2 accepted; D1 answered | `b-T0-lcd-vs-gray-4x.png` + D6; `c-C3-g1.jpg`; the X1 line |
| **2 Core** (~1-2 days) | A: A3 → A4 → A5 → {A6, A7, A8}. B: T2 (after D6). C: C10 after C4 | C4 → A3/A4 T1 → A5 (ILC + T1) → T4a (8 route runs) → C5 (tests, S1, both soaks) → A8 → A8b → A6 (ILC + AOT goldens) → C7 (commandlet, BG) → A10 (stage, build, main/24fps/hitch/ai, error run) → A14 → C8 (desktop unlocked) | A10 1440/1440 on all four Unreal runs; A8b 6/6 (else STOP per A); T4a routes chosen; C5, C7, C8 accepted | `a-A8-parity.png` (cli, cpp, godot); `c-C7-composite.jpg` |
| **3 Converge** (~1-2 days) | A: reviewer fills `visual_check.json`. B: reads crops and fixes code between holds | B T4b → T5 → T6 → T7 → T8 (4-5 min holds) interleaved with A11 runs and C9; C11 packaging steps (BG, 10-40 min each) slotted while B is fixing code | B `PAIR A/B: RESULT PASS 0/38` + `DETERMINISM OK`; A11 accepted with every visual check "yes"; C9, C11 accepted | `a-A11-contact-sheet.jpg` + verify numbers; `c-C9-sculpt.gif`; B T8 scorecards (preview) |
| **4 Measure** (~3 h, consecutive) | none | A12 (warm-up, arena ×3, HideUnits, LT_A_noLumen ×3, 2,000 units, film) → B T8 `hud_cost` (warm-up, 3 off, 3 on) → C12 (warm-up, C1@64 ×3, C1@32 ×3, C1U ×3, C1@320, InPlaceUpdate=0, **SOAK unconditionally**) | needs D2; every run quiet (`quiet=1` in A, `-Measure` precheck passes in C, the lock's preflight for all) | `a-A12-perf.png` + `battle.mp4`; B HUD-cost line; `c-C12-summary.png` |
| **5 Final** (~½ day) | B T10 and C13 (Opus xhigh reviews), C14; main session: EV README, trial scorecard, mirrors, commits | A13 final evidence run (republish, restage, every leg, `--final` ×2); B T9 only if the lock is then idle, otherwise deferred to the merge | §4 holds for each check | three proof packs + `trial-scorecard.jpg` + verdict lines |

Stop rules in the plans stand (A's STOPs, B's escalations, C's switch to CF); the main session reschedules the lock queue around them.

### 1.3 Lock budget (estimates, UNVERIFIED; `ue_lock.log` gives the real numbers and becomes `EV/lock-usage.csv`)
| Lane | Holds | Estimate |
|---|---|---|
| Shared + X1 | S1 selftest; 2 scaffold builds; X1 build + 1 run | 20-30 min |
| (a) | A's own ~85-100 min (A §4, line 380) + T1 under lock (~7 runs; A0 measures the duration) + ~6 ILC publishes + ~8 Godot runs | 2.5-3.5 h |
| (b) | T3 45-60 min; T4a 35-45 min; T4b-T8 25-75 iterations × 4-5 min; hud_cost ~20 min; T9 (optional) ~40 min | 3-7 h |
| (c) | C1 ~15 min, C2 ~10, C3 30-75, C4 ~15, C5 ~30, C7 30-50, C8 ~10, C9 ~15, C11 1.5-4 h, C12 60-75 min | 5-8 h |

### 1.4 Checkpoints, commits, HANDOFF
Only the main session runs `git add`/`commit`/`push`, in R and in U. Three streams share each working tree, and two concurrent `git commit` calls collide on `index.lock`.
Commits are made per check, by explicit pathspec, with prefixes `SimTrial:` (a), `HUD:` (b; B's audit relies on it, B T10), `Terrain:` (c), `Trial:` (shared).
R checkpoints: S0, S4, A1, A2, A5, A8+A8b, T2 (`HUD: calibration frozen`), T4b, T6, T8, C5, C7, A13, T10, C14. Each one rewrites `HANDOFF.md` once, covering all three checks, and is pushed.
U checkpoints (local; U has no remote): S2, S3, X1, A10, A11, A12, T3, T4b, T6, T8, C1, C3, C5, C7, C11. **Every U checkpoint also runs that check's mirror into
R** (`tools/unreal-sim`, `tools/unreal-hud/ChimeraHud-src`, `tools/unreal-terrain`) through `secret_scan.py`. That way the off-machine copy is never more than one checkpoint old.

### 1.5 Added task X1: NativeAOT inside the Unreal process, on day 1 · sonnet (medium; opus medium if it crashes) · LOCK
Why: A first loads NativeAOT into Unreal at A10, behind A1-A5 and A9, yet "NativeAOT misbehaves inside UE (loader lock, GC suspension, SEH)" is A's one
architecture-level risk (A §6, R3 open question 2) and its fallback is Alec's decision (D7). Files: `R/tools/sim-trial/aot-ue-smoke/` (net8.0, `PublishAot`,
`NativeLib=Shared`, win-x64, ILCompiler 8.0.25 as A5) exporting `smoke_add`, `smoke_runtime(buf,cap,len)`, `smoke_selftest(kind)`: 1 throw/catch, 2 null dereference caught
as `NullReferenceException`, 3 allocate 256 MB then a blocking full GC, 4 `Task.Run` + `Wait`, 5 `Thread` start/join. In P: the `ChimeraSimHost` module skeleton from A10's
file list (`.uproject` entry, `ExtraModuleNames` in both Target.cs, Build.cs); with `-ChimeraAotSmoke=<abs dll>` a core ticker 60 frames after start loads the DLL
(`GetDllHandle`, never freed, A F22), calls each export, logs `LogChimeraSim: smoke kind=N rc=R`, and exits forced (§2.2): 0 if all pass, 6 otherwise. The `.uproject`
edit and the build share **one** hold (A10's rule), before A9's editor session. Accept: `Result: Succeeded`; a `-game` run on `/Engine/Maps/Entry` logs
`smoke 5/5 runtime=.NET 8.0.25 aot=1`, exits 0, no new `P/Saved/Crashes` folder. A10 extends the same module and keeps the switch as a diagnostic.

## 2. Shared infrastructure (Phase 0)

### 2.1 Tasks S0-S4
**S0 · R hygiene and plans commit** (main session). Add to `R/.gitignore`, **one line per extension**: `docs/unreal-move/trial-checks/research/hud-ref/*.png`, `….jpg`, and
`docs/unreal-move/trial-checks/evidence/b/**/*.png`, `….jpg`, `….gif`, `….mp4`. Gitignore has no brace expansion: a `*.{png,jpg}` line matched nothing in a scratch test
(`git check-ignore` rc 1, 2026-10-01). The five hud-ref images hold third-party (Manor Lords) pixels (B F24, B §2.7).
Then commit the plans, EXECUTION.md, `research/*.md`, `research/proto-a/` (493 KB, text only: cs, csproj, cpp, py, txt, json, diff, bat, xml) and `hud-ref`'s JSON, OFL fonts and
`LICENSES.txt` (`Trial: plans of record`). Accept: `git ls-files docs/unreal-move/trial-checks | grep -E '\.(png|jpg|gif)$'` prints nothing.

**S1 · `ue_lock.sh` v2** (main session's script per the fixed decisions; sonnet may write it). The contract:
1. CLI unchanged (`<cmd…>`, `--status`), plus `--measure <cmd…>`, `--selftest`. `UE_LOCK_TAG` (e.g. `a/A10`, `b/T4b`, `c/C11-cook`) goes into the owner file and the log.
2. **Re-entrant** (B §2.9): exports `UE_LOCK_HOLDER=$$` to the child. A nested call whose `UE_LOCK_HOLDER` equals the live owner pid runs the command directly, without
   acquiring or releasing. No private bypass variable.
3. **Orphan-safe staleness** (C risk 11a): today a lock counts as stale when the bash pid is gone (`ue_lock.sh:23-29`). In v2 a dead owner pid (or age ≥ `UE_LOCK_STALE_MIN`)
   is stale only when `tasklist` shows no `UnrealEditor*.exe`, `UnrealBuildTool.exe`, `ShaderCompileWorker.exe`, `UnrealPak.exe` or `ChimeraTerrain*.exe`. Otherwise it logs
   `ORPHAN <image> <pid>` every 60 s and waits. It never kills.
4. `--measure`: acquires as usual, records `kind=measure`, then runs `KIT/preflight.ps1 -Quiet` (below) before the command. If the machine is not quiet within 5 min it exits 4
   (`infra: busy`) without running the command. A's `-Perf` and C's `-Measure` checks remain as a second layer.
5. Appends `ts,event,tag,kind,wait_s,hold_s,rc` to `U/ue_lock.log`. The usage comment's PowerShell line (`ue_lock.sh:7`) becomes `& "C:/Program Files/Git/bin/bash.exe" …`:
   plain `bash` from PowerShell is WSL on this PC (A F36, C shell rule).
6. `--selftest`, run while nobody holds the lock (it replaces B T3's step 1, whose 10 s timeout fails whenever another check holds the lock): a nested call runs; a second caller
   waits and then acquires; the exit code propagates (7 → 7); a dead pid with no Unreal process is taken over; TERM releases. Prints `LOCK SELFTEST OK 5/5`.

**S2 · U hygiene** (main session). One `U/.gitignore` edit: `**/HudRef/`, `**/HudData/Placeholders/` (B §2.7), `ChimeraTerrain/{Out,Packaged,Textures}/` as three lines (Poly Haven sets are
reproducible by `fetch_textures.py`), `ChimeraTerrain/Plugins/RealtimeMeshComponent/` (nested clone of `b8669a0`; **VENDOR.md carries the full patch text**), `ue_lock.log`. Commit `Trial: ignore rules`.

**S3 · Sibling-project scaffold** · sonnet (medium) · LOCK for the two builds. `KIT/new_project.py --name ChimeraHud|ChimeraTerrain` writes the shared part once,
leaving existing `HudRef/`/`HudData/` folders alone. The `.uproject` copies EngineAssociation `{B44F274D-4E33-1231-4E78-41A8FFF3BD74}` from P (`P/ProjectChimera.uproject:3`), with one Runtime
module and no plugins. Both Target.cs files use V7 and `Unreal5_8` (`P/Source/ProjectChimera.Target.cs:11-12`). Build.cs lists Core, CoreUObject, Engine and InputCore plus
**`PublicIncludePaths.Add(ModuleDirectory)`**: V7 turns legacy include paths off (`UnrealBuildTool/Configuration/Rules/ModuleRules.cs:1473-1487`, verified), and C's flat `Data/ Render/ Game/`
folders need the line as much as B's `Ui/` (B F21). The module .h/.cpp use `IMPLEMENT_PRIMARY_GAME_MODULE`. `DefaultEngine.ini` takes P's renderer block **without** the AndroidFileServer
section (`SecurityToken`, `P/Config/DefaultEngine.ini:92`), with both default maps set to `/Engine/Maps/Entry`. `DefaultGame.ini` gets a new ProjectID. B T3 and C0 then add their own parts (B §2.1, C §3.1).
Accept: both empty projects build (`Result: Succeeded`), and `secret_scan.py` passes on both.

**S4 · Trial kit** (`KIT/`) · sonnet (medium) · no lock. Small, shared, tested (`python -m pytest KIT/tests -q`):
- `preflight.ps1` (Windows PowerShell 5.1 compatible, B's T3 spec promoted): primary screen 1920x1080 at AppliedDPI 96, `LogonUI.exe` absent, parsecd state recorded.
  `-Quiet` adds: no `dotnet|ilc|cl|link|MSBuild|testhost|Godot*|ffmpeg|ShaderCompileWorker` above 5% of a core over 10 s, total CPU below 10%, and an `MsMpEng` CPU snapshot.
  Every Unreal run wrapper (A `run_sim.ps1`, B `hud_shot.sh`, C `run_terrain.ps1`) calls it **before** taking the lock, so a Parsec resolution change fails fast without wasting a hold.
- `logscan.py`: one rule set for all three checks (C4 in §3).
- `secret_scan.py`: fails on `SecurityToken=` or `*Key=` with a value. Used by the three mirror scripts and by the main session before any R commit that touches `tools/unreal-*`.
- `evidence.py`: `add` (copy or register, name, sha256, size and pixel size into the check's `manifest.json`), `phone` (JPG ≤ 1600 px wide and ≤ 1 MB), `sheet` (captioned contact sheet),
  `scorecard` (the trial image, §4), `check` (enforces the git policy below; every manifest entry exists with a matching sha256).
- Installs `freetype-py` at a pinned version (needed by B's P5 control, B §4.4). Not installed today; numpy, Pillow, scipy, fontTools and matplotlib are (checked 2026-10-01).
- **No shared comparator.** The three checks compare different things: per-tick traces (A `compare_traces.py`), 38 calibrated HUD regions (B `hud_compare.py`) and
  terrain masks and depth (C `imgdiff.py`). One tool would lose what each needs. The shared pieces are capture semantics (§2.2), the log scan, preflight and evidence.

### 2.2 Unreal runtime conventions all three modules follow (each check keeps its own code; these override the plans where they differ)
- **Exit codes.** Write and close every output file first. A failure then calls `FPlatformMisc::RequestExitWithStatus(true, code)`; success may exit non-forced with 0.
  Non-forced exits return 0 whatever the code (§3 C3). Wrappers also require the success sentinel: A `LogChimeraSim: RESULT`, B `shot written`, C `results.json completed=true`.
- **Compile-idle wait.** `(GShaderCompilingManager == nullptr || GShaderCompilingManager->GetNumRemainingJobs() == 0) && FAssetCompilingManager::Get().GetNumRemainingAssets() == 0`
  (B F17). Then each plan's own frame count.
- **Screenshot done** means the file exists, is non-empty and is newer than the request. `OnScreenshotRequestProcessed` fires even on failure (B F2).
- **Launch from Bash** with `MSYS_NO_PATHCONV=1`. `-ABSLOG` must end in `.log`. String options use `FParse::Value(…, false)` (A F26).

### 2.3 Evidence: names, storage, git policy
- Layout: `EV/a/` (replaces A's `PROOF = …/a-native-sim`), `EV/b/`, `EV/c/` (replaces C's `PROOF = …/c-terrain`), `EV/README.md` (verdict table, reproduce commands),
  `EV/trial-scorecard.jpg`, `EV/lock-usage.csv`. Working output stays where each plan puts it (A `U/TrialOut/a`, B `H/HudRef/`, C `T/Out`); curated copies enter EV via `evidence.py add`.
- Names: `<check>-<task>-<subject>[-<variant>][-r<rep>].<ext>` (subject lowercase, hyphens), e.g. `a-A8-parity.png`, `c-C7-composite.jpg`; phone copies end `-phone.jpg`.
- Committed to R: `.md .json .csv .txt` (≤ 1 MB each), JPG ≤ 1.5 MB, chart PNG ≤ 300 KB, ≤ 8 MB per check (look-test precedent: two JPGs of 1.2 and 1.4 MB plus JSON).
- Never committed: full-resolution frames, `.mp4`, `.gif`, binaries, and **every (b) image** (placeholders and the pair-B backdrop are third-party pixels). These stay in
  their working folders and are listed in `manifest.json` with absolute path and sha256. A's 14 shot PNGs and `battle.mp4` follow this rule; its contact sheet goes in as a JPG.
- Each `manifest.json` row: file, sha256, bytes, w×h, task, source path, R commit, Unreal build id, `committed`, and `sent_to_alec` (time). `evidence.py check` runs before every EV commit.

## 3. Conflicts between the plans, and how they are resolved
| # | Conflict | Evidence | Resolution |
|---|---|---|---|
| C1 | Fixed decisions and HANDOFF say P is "not a git repo"; U became one today | `git -C U log`: 30b6744, 9f13ed6, no remote (A F37, B F23) | H, T and P are versioned in U; mirrors go to R at every checkpoint (§1.4); D5 |
| C2 | Baseline "6392 / 0 / 1" (fixed decisions, HANDOFF, B §7.4, C C10) vs A's measured 7,124 total / 7,122 pass / 1 fail / 1 skipped | A F6; the failing guard `Meta/ToolingGateGuardTests.cs:66-75` needs the BMAD dispatcher retired in c794681b | A0 deletes that guard. The gate is A0's measured numbers with failed = 0 (expected 7123/7122/0/1). HANDOFF corrected at the S0 or A1 checkpoint |
| C3 | A exits with `RequestExitWithStatus(false, 3)` and "non-zero" (A §3.7, A10); C exits `(false, code)` and expects FAIL → 2 (C §3.8, C3) | Non-forced posts WM_QUIT (`WindowsPlatformMisc.cpp:1520`) and `GuardedMain` returns the init ErrorLevel (`Launch.cpp:146-204`; `LaunchWindows.cpp:115-138`). Only `Force` calls `TerminateProcess(…, code)` after `GLog->Flush()` (`:1504-1515`); `RaiseExceptionOnFaultTermination` is off by default (`:140-144`) | §2.2: failures exit forced after closing their files; wrappers also require a success sentinel |
| C4 | C's parser fails on any `Failed to load` (C §3.8 l.215, C11) | every clean run logs 4 `LogWindows: Failed to load '<profiler>.dll'` lines (B F19; lines 2, 5, 7, 406 of `P/LookTest/logs/game_A_noLumen_r1.log`) | `logscan.py` fails on `Failed to load '/` (asset paths) and `Failed to load game mode` and allows exactly aqProf, VtuneApi, VtuneApi32e, WinPixGpuCapturer |
| C5 | B's ignore rule `docs/unreal-move/trial-checks/**/*.{png,jpg,gif}` and T0's repo-wide `-uall` image gates (B §2.7, T0) | the rule would cover A's and C's committed evidence images, and as written it matches nothing (no braces in gitignore; tested) | S0's narrower one-extension-per-line rules; B's gates scoped to `research/hud-ref` and `EV/b` |
| C6 | Three proof locations (A `a-native-sim/`, C `c-terrain/`, B `H/HudRef/proof` + `tools/unreal-hud/results`) | fixed decision: evidence under `…/evidence/` | §2.3 (renamed paths only). A's PNG shots and MP4 stay local |
| C7 | B needs a re-entrant lock (B §2.9), C needs an orphan-safe one (risk 11a), A flags the PowerShell trap (F36) | `ue_lock.sh:7, 23-29, 51-56` | lock v2 (S1); B T3 step 1 covered by `--selftest` |
| C8 | A: "T1 never while LOCK is held, ILC or Godot active" (A §4 Rules) | unenforceable with B and C using the lock; 2.9 GB free today | T1, ILC and Godot take the lock (§1.1) |
| C9 | Perf contamination handled unevenly: A `-Perf`, C `-Measure`, B's `hud_cost` has no check; C's P5 cook p95 draws on S1 and SOAK runs that are not measured runs (C §5) | C risk 11b asks for a measure lock | Phase 4 window with `--measure`. Every **gated** timing number comes from a Phase-4 run: C's P1-P5 timings from C12 (SOAK made unconditional), A's from A12, B's from T8-cost. Earlier timings are reported only |
| C10 | 1%-low bar: A ≥ 50 fps (battle arena) vs C ≥ 45 fps (sculpting) | A §8.2; C §5 P3 | kept apart (different loads); one merged decision D2 |
| C11 | Each plan commits and rewrites HANDOFF on its own schedule (A §4 Commits; B T2/T4b/T6/T8/T10; C C14) | shared working trees | main session is the only committer (§1.4) |
| C12 | Tree-wide status checks with three streams: C14 expects `git status --short` to show only its paths; B T0 checks images repo-wide | A edits `godot/`, B `tools/unreal-hud` in parallel | each check's status gates are limited to its own pathspecs |
| C13 | Mirrors: B redacts secrets, C mirrors `Config` with no scan, A mirrors only at A13 | `SecurityToken` in `P/Config/DefaultEngine.ini:92` (in U's history, local only) | the scaffold leaves the section out; `secret_scan.py` runs on every mirror; mirrors run at every checkpoint |
| C14 | U `.gitignore` edits owned by B T3; C's `Out/`, `Packaged/`, `Textures/` and nested RMC clone are not ignored | `U/.gitignore` today ignores `**/TrialOut/`, not `Out/` | one edit in S2 |
| C15 | B found that V7 needs the module root on the include path; C's module has the same flat-folder layout and no such line | `ModuleRules.cs:1473-1487`; C §3.1 module layout | in the scaffold (S3) |
| C16 | A waits on `GetNumRemainingJobs()` without B's null guard | `LaunchEngineLoop.cpp:3245-3260` (always created at start), `:6964` (nulled at exit) | §2.2 guard for all (low risk, no cost) |
| C17 | Only B checks the desktop before a run; A and C find a wrong size only afterwards | B F5/F18; Parsec can change the host resolution | shared `preflight.ps1` before the lock (S4) |
| C18 | `PS` is pwsh 7 in A and Windows PowerShell 5.1 in C; `T` means different things in B and C | A header; C shell rule | each check keeps its own convention inside its scripts; shared scripts are Python, Bash or 5.1-safe PowerShell |
| C19 | C10 hands "run `ServerBootstrap` on the edited grid" to check (a); A has no task for it | C C10, §3.9 | deferred to the merge (C does not gate it); recorded in HANDOFF |
| C20 | B T9 copies 231 MB of `P/Content/LookTest` and C9 copies `crucible_mortar` while A9 may be writing P in an editor session | A9 runs an editor on P | the copies run inside a short LOCK hold |
| C21 | Renderer config: H copies P's block verbatim with Lumen on (`P/Config/DefaultEngine.ini:12,14`), T switches to ScreenSpace, A's arena uses the A_noLumen volume | HANDOFF: look decided, Lumen off | no trial impact (H draws a flat backdrop; T9 uses the map's own volume). The merge sets Lumen off project-wide |
| C22 | Axis mapping between (a) and (c) | A §3.7 `UE = 100·(x, z, y)`; C §3.2 `SimToUe = (100x, 100z, 100h)`; both cameras at yaw 90 | consistent; C's `terrain.json` `axes` field stays the check |
| C23 | A first loads NativeAOT in Unreal only at A10, behind the whole .NET chain | A §6 risk 4; R3 open question 2 | X1 (§1.5) |

## 4. Done-gate for the trial (everything from one final build per check; numbers come from files, never from memory)
| Check | Passes when | Numbers Alec sees | Files on his phone |
|---|---|---|---|
| (a) | A §5 items 1-7, all from A13's single `--final` run | main set: 7 legs × 1,440/1,440 (meta, cli, cpp, godot, unreal, unreal_24fps, unreal_hitch); ai set: 5 legs; probe 6/6; A14's result beside it; T1 failed 0 and 0 goldens moved; IL2026/IL3050 = 0; AOT replay `jit_equal N1/N1 golden_equal N2/N2`; `visible==alive`, `max_err_cm`; fps median, 1%-low, sim p95 ms, GPU delta, LT_A and 2,000-unit figures | `a-A8-parity.png`, `a-A11-contact-sheet.jpg`, `a-A12-perf.png`, `battle.mp4`, `a-A13-final-parity.png` |
| (b) | B §7 items 1-5 | `PASS 0/38` on pair A and pair B; HUD MAD and SSIM per pair; calibration 5/5 positives, 8/8 negatives per pair; `DETERMINISM OK`; `scale=1.000 local=1920x1080`; HUD frame cost ms | `scorecard_A.png`, `scorecard_B.png`, `side_by_side_B_phone.png`, `worst5_crops.png`, `flicker_worst.gif`, `uiscale80.png` + the reviewer's verdict |
| (c) | C §5 Done: G1, G2, P1-P11 pass; V3 and V6 pass review | the §5 table values (tick ms p50/p99 at d20 and d100, render latency, fps with 1,000 units, terrain GPU ms, cook p95, soak MB, hashes, pick error, depth p99) | `c-C13-composite.jpg`, `g1.jpg`, `mouse.jpg`, `pkg.jpg`, `sculpt.gif` + README summary |

**Trial verdict:** PASS only when all three pass. A FAIL or PARTIAL goes to Alec with its numbers and the fallback taken, never rounded up (A §6, C §5), in each plan's
verdict wording (A §1). `EV/trial-scorecard.jpg` is one phone image (per check: verdict, three or four headline numbers, the file to open), sent with the proof packs.

## 5. Decisions that are Alec's (merged, deduplicated)
**Up front, in one message in Phase 0; "proceed" adopts every recommendation:**
- **D1 (a) What "the same checksum as Godot" means** (needed before A3). Recommended: the sim built and run by Godot 4.6.3, plus a gating check that the real
  MainScene starts from the same state (6/6 hashes). The full-MainScene replay is measured and reported, not gating: it will probably diverge because of DW-681 (today's
  Godot game stops arriving units from a per-frame writer outside the sim), a Godot-side bug. Alternative: gate on the full replay once DW-681 is fixed. (A §8.1)
- **D2 Performance bar** (needed before Phase 4; merges A §8.2 and C §8.2). Recommended, all at 1080p on this PC: (a) battle arena with 1,000 units, median ≥ 60 fps and 1%-low ≥ 50;
  (c) sculpting with 1,000 units, median ≥ 60 and 1%-low ≥ 45, terrain GPU ≤ 3 ms, brush tick p50 ≤ 1 ms (d20) and ≤ 2 ms (d100), soak growth ≤ 300 MB. Reported, not
  gated: HUD cost, the trees-and-scatter scene, a 2,000-unit run. Alternatives: also gate the trees-and-scatter scene; or report performance separately from pass/fail.
- **D3 (c) Scope**: sculpt and paint inside the running packaged game (the in-game map editor), with the sim reading the heights when play starts. Deforming terrain
  during a match is not part of the trial. (C §8.1)
- **D4 How looks count**: (b) passes on measured match to the mockup plus a reviewer's verdict. (a)'s visual check proves Unreal draws what the sim says, not that it is pretty.
  (c)'s ground look (realism, tiling, rock on slopes, paint edges) is reported to you and does not gate. Say so if (c)'s look must pass too. (C §8.3)
- **D5 Backup of the Unreal projects**: they have no remote. Recommended: keep local commits, and mirror their text into this repo at every checkpoint (pushed to GitHub);
  add a private remote at the merge. Alternative: create a private GitHub repo now. (A §8.4)

**After B's T0, with `b-T0-lcd-vs-gray-4x.png` attached:**
- **D6 (b) What pixel-faithful means for text** (blocks T2). Unreal draws grayscale text, while the approved mockup was captured with colour-fringed (LCD) text: 961 of
  1,341 ink pixels on the unit name are fringed. Recommended: compare against a grayscale re-render of the same mockup; hold layout, size, colour and shape exactly; text
  edges within ±1 px; weight and colour by measured tolerances. (B §10)

**Only if triggered:**
- **D7** NativeAOT fails inside the Unreal process (X1 or A10): accept (a) as FAILED/PARTIAL, or fund a helper-process host as a different architecture. (A §8.3)
- **D8** RMC and the in-engine fallback both fail: request Errant Landscape's free 30-day trial (a form in your name; price not published). (C §8.4)
- **D9** Windows blocks the injected mouse in C8 or C11: waive "real OS mouse" for P10 (Slate-level input instead), or keep it a FAIL. (C C8)
- **D10** A text residual remains in (b) after the full recipe, or a calibration control cannot be separated: accept it with its measured size, or keep (b) open. (B §8)

Decided here, not Alec's: lock classes, the measurement window, the evidence policy, T9 last or at the merge, C10's hand-off at the merge. Operational ask: keep the
desktop unlocked for C8 and C11's mouse runs and don't connect Parsec at a non-1080p resolution during Phases 3-4 (`preflight.ps1` reports either before a run).

## 6. Risks of running the three together
- **Lock bottleneck** (§1.3): if B's convergence runs long, A11 and C11 lose slots; the dispatch order keeps first lights and short holds moving, and the lock log shows where the hours went.
- **Memory**: 2.9 GB free today without Unreal (java 1.2 GB, Chrome, Discord, several Claude Code processes). One heavy process at a time; nothing of Alec's is closed; a
  measurement taken while paging is repeated, never accepted. **Defender** (`MsMpEng`, 8,599 CPU-s cumulative) is recorded in every `--measure` snapshot; an exclusion is a
  settings change, raised with Alec only if a rep spread above 5% points to it. **One tree, three agents**: only the main session touches git (§1.4).

## 7. Phase 0-1 results and main-session decisions (2026-10-01)
All Phase 0-1 tasks passed independent Opus verification (S3, S4 after a main-session fix, A0, A1, A2, X1, A9, T0, T1, T3, C0, C1, C2, C3, C6).
- **X1:** a NativeAOT DLL runs inside `UnrealEditor.exe -game` (throw/catch, NullReferenceException, 256 MB + blocking GC, Task.Run, Thread): `smoke 5/5 runtime=.NET 8.0.25 aot=1`, exit 0. D7 is not triggered.
- **A0 baseline:** T1 `total=7123 passed=7122 failed=0 skipped=1` after deleting the stale BMAD guard; ~2 min per run. The LLM generation-timeout tests flake under load (flake rule applies).
- **A1/A2:** source-generated STJ in place; Roslyn IL2026/IL3050 62/65 → 0/0 (now release-gated), CHM counts unchanged, no golden or AlgoVersion moved; `MixUnknownEffect` fails closed under AOT.
- **C3:** RMC Dynamic chunks render in `-game` (G1 PASS). **RMC #303 reproduces on the RTX 3060: Static draw is invisible**, so plan C's idle-Static and RVT fallbacks (risks 3 and 5) are ruled out.
- **T3:** the Slate capture is exact (MAD 0, max 0 against the backdrop; gamma-space blending max error 1).
- **Decision (B, T2): positive control P2 (`font-kerning:none`) is dropped, and P4 becomes P1+P3.** T0 measured that P2 moves the unit name's right ink edge by 2 px, beyond G5's 1 px. Slate is configured to kern (FullShaping, F7, proven by T4a's kern check), so "kerning off" is not a state the build can produce, and a positive control must model an allowed rendering difference. Gates are unchanged.
- **Decision (A, A0): the Godot server smoke runs `map_02_iron_crossing`** (the scenario `scenes/main.tscn` pins); A1 and later repeat the identical command.
- **Main-session fixes:** `secret_scan.py` now also flags well-known key shapes anywhere in a line (the S4 verifier's major finding); the `AndroidFileServer` plugin (which writes a `SecurityToken` on the first editor run, found by C2) is disabled in ChimeraHud and in `new_project.py`; `ue_lock.sh` waits for 2.5 GB of free RAM (at most 20 min, then logs `lowmem-proceed`) because other sessions share this PC, and its `--measure` preflight window is 5 min of wall clock.
- **Builds are slow under contention** (C1: 928 s; C3: 18 min for 10 actions while MsMpEng was busy): run every build with the background-and-wait pattern.
- **D6 answered by Alec (2026-10-01):** check (b) is scored against the grayscale re-render of the mockup, as recommended ("the text looks exactly the same, so it's a go"). With P2 dropped, T2's positives are P1, P3, P4 (= P1+P3) and P5, so its line reads `positives 4/4`.
- **Phase 2 runs as one workflow** (`tools/unreal-trial/workflows/phase2.js`): two Opus xhigh verifiers per task (results, and code plus rules), a checkpoint marker per finished task in `U/TrialOut/checkpoints/`, and main-session commits from those markers while the run continues. Inside check (a), the Godot chain A8 → A8b → A14 is serial because each builds `godot.csproj`; A6, A7 and A10 run beside it after A5. C5, C7 and C8 are serial because each edits and rebuilds the ChimeraTerrain module.
- **T2 ruling (main session, 2026-10-01).** T2 hit §4.4's STOP: under T1's gates the bold negative N3 (unit name at weight 700) passed every gate, because P5 (Slate's raw FreeType coverage) is 5-33% lighter in ink than Chromium's grayscale text, which makes the calibrated ink-mass threshold loose. Ruling: (R1) a hard one-sided heavy-ink gate, `mass_heavy` ≤ +0.05, is the "new hard metric for that defect class" §4.4 prescribes (positives reach at most +0.028, N3 +0.115); (R2) the unit SSIM is masked to its unit; (R3) only a region's intended negatives bound its thresholds; (R4) metrics that no positive moves are no longer simply report-only: a new positive **P6 "vector AA model"** (the board at deviceScaleFactor 4, box-filtered, vector-class pixels only) gives the ornament, ring, plate, map, portrait, card.V and menu regions real vector thresholds, so T4b and T7 cannot pass them vacuously; (R5) 87 panel-fill pixels in sel.panel's column x=417 were mis-classed as text and become flat (a mask fix); (R7) text weight is T4a's to measure on the real Slate render, and T4a may add an engine-supported coverage or gamma route; if the best route stays more than 5% lighter, that is D10 for Alec. The planned recalibration (T2r) also covers a coverage setting. Positives are now P1, P3, P4, P5, P6 (5/5). Continued as workflow `tools/unreal-trial/workflows/phase2-laneb.js`.
