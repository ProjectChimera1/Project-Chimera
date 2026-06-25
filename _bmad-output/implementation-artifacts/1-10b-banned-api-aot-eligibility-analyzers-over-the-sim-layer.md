---
baseline_commit: ddd9dc5f1a16b4d5c5da2d116e9269e9b204be70
---

# Story 1.10b: Banned-API + AOT-eligibility analyzers over the sim layer

Status: done

<!-- Context engine analysis completed — comprehensive developer guide. Validation optional: run validate-create-story before dev-story. -->

## Story

As a solo developer who must keep determinism from silently breaking,
I want a banned-API Roslyn analyzer plus an AOT-eligibility analyzer over the sim layer, advisory on master and hard-enforced on a release branch,
so that any future change that smuggles a nondeterministic or AOT-incompatible API into gameplay code fails CI before it can ship.

## Acceptance Criteria

**Verbatim from `epics.md` (Story 1.10b, covers AR-36, depends on 1.10a):**

> **Given** the banned-API analyzer over `src/Core,Combat,Economy,Navigation,Effects,Dsl` **When** sim code uses `using Godot`, float gameplay math, `System.Random`, or `Fixed.FromFloat` outside the converter allow-list **Then** it reports advisory on master and hard-fails on the release branch; the AOT-eligibility analyzer flags AOT-incompatible patterns the same way

### Decomposed, testable acceptance criteria

- **AC1 — Banned-API gate exists over the Godot-free sim source.** A Roslyn banned-API analyzer runs over the deterministic sim source set (`src/Core, Combat, Economy, Navigation, AI, Multiplayer`-sim; forward-compatible with `Effects`/`Dsl` when they exist — see [Scope reconciliation](#scope-reconciliation-read-this-first)). It reports the AR-36 determinism bans: `using Godot`/Godot types, the float↔Fixed and float↔string conversion boundaries (`Fixed.FromFloat`/`Fixed.ToFloat`/`float.Parse`/`float.ToString`), `System.Random`/other nondeterministic RNG, and wall-clock types (`DateTime`/`DateTimeOffset`/`Stopwatch`/`Environment.TickCount`).
- **AC2 — `Fixed.FromFloat`/`Fixed.ToFloat` allow-list.** The single AR-14 quantization boundary `FixedJsonConverter` (its `Read` at `FixedJsonConverter.cs:50` and `Write` at `:54`) is explicitly allow-listed and does **not** report. Any *new* `FromFloat`/`ToFloat` call elsewhere in tick-reachable sim code does report.
- **AC3 — AOT-eligibility analyzer runs over the same Godot-free source.** The built-in .NET trim/AOT analyzers (`IL2xxx`/`IL3xxx`) run over the sim compilation via `<IsAotCompatible>true</IsAotCompatible>`, with **no** Godot/GodotSharp in the compilation closure (so the verdict is clean and meaningful).
- **AC4 — Advisory on master, hard-fail on release.** On `master` the analyzers produce **warnings only** — the build/CI stays green so the hourly `[AutoSave]` auto-commit loop is never blocked (AR-36). A release path (`-warnaserror` for the selected rule set) makes the same findings **fail the build**, and this enforcement mechanism is wired and proven to fail on a real violation.
- **AC5 — Runs in the 1.10a headless CI lane.** The gate is a **sibling job** added to the existing `.github/workflows/determinism-gate.yml` (job `tier1-golden-gate` stays intact), reusing the Godot-free `windows-latest` / SDK `8.0.419` / `--locked-mode` setup. No Godot is installed in the lane.
- **AC6 — Reproducible deps, shipping surface untouched.** The analyzer package(s) are dev-only (`PrivateAssets=all`) and confined to the analysis compilation; **`godot.csproj` keeps exactly one `PackageReference` (NakamaClient 3.13.0)** so the 1.10a `DependencyHygieneTests`/`GodotCsproj_CarriesExactly_TheSingleShippedPackage` guards stay green. Any new NuGet dep is captured in a committed `packages.lock.json`.

---

## SCOPE — read this before coding

### ✅ IN scope (this story)
1. **Stand up the two analyzers** over the Godot-free sim compilation: off-the-shelf banned-API analyzer (`Microsoft.CodeAnalysis.BannedApiAnalyzers` + `BannedSymbols.txt`) **+** the built-in AOT/trim analyzer (`<IsAotCompatible>`).
2. **Advisory-on-master / fail-on-release** severity cadence, with the release-enforcement mechanism wired and verified once against a deliberate violation.
3. **A sibling CI job** in the existing `determinism-gate.yml`.
4. **Allow-list** the `FixedJsonConverter` quantization boundary.
5. **A documented baseline** (current violation counts per rule) and the **ratchet decision** of which rules are release-gated now (zero-baseline rules) vs advisory-only (rules with existing legitimate/debt violations).
6. **Guard test(s)** that keep the analyzer config honest (mirroring the 1.10a guard-test convention), and an extension of `DependencyHygieneTests` so the new analyzer dep can't leak into the shipping build.

### ❌ OUT of scope (do NOT do these here)
- **Do NOT fix the violations the analyzers surface.** 1.10b makes the debt *visible and advisory*. Existing `Fixed.FromFloat` static-constant sites, the reflective `System.Text.Json` AOT warnings (IL2026/IL3050), and the AI-layer float debt are **expected advisory findings** — they are cleaned up by their own later stories (the D2 "Fixed end-to-end" migration, the D3 JSON source-gen migration, the AI float→Fixed determinism work). Touching them here is scope creep.
- **Do NOT write the full custom `BannedSimApiAnalyzer`** (a true `float`/`double` primitive-declaration ban, Dictionary-enumeration detection, unstable-sort detection, magic-cap-literal detection). Those need a hand-written `DiagnosticAnalyzer` and have runtime backstops (the golden-checksum replay catches order/sort desync; `SecretExclusionTest` already covers `[Export]`-key). They are a **documented follow-up** (see [Decisions for Alec](#decisions-for-alec-answer-before-or-during-dev)). The off-the-shelf analyzer + AOT analyzer meet this AC.
- **Do NOT add the actual `PublishAot` build or the dedicated-server `.csproj` project-split.** AR-38/D5 explicitly defer NativeAOT *building* post-1.0 — 1.0 ships only the **analyzer gate** + Godot-free discipline.
- **Do NOT relocate `StressTest.cs`** — already done (it lives in `godot/tools/StressTest.cs`, out of `src/Core`).
- **Do NOT set `CHIMERA_GOLDEN_RECORD`, target `godot.sln`/`godot.csproj` in the lane, or install Godot/GodotSharp** (the existing workflow header forbids all three).

### Scope reconciliation (read this first)
The AC's folder list — `src/Core,Combat,Economy,Navigation,Effects,Dsl` — is **illustrative of "the sim layer," not the literal include set**:
- `Effects/` and `Dsl/` **do not exist yet** (they are created in Epics 2 and 7). Make the analysis source-include **glob-based** so they are covered automatically the moment they appear.
- `AI/` and `Multiplayer`-sim **are** in the Godot-free determinism boundary (they are in the 1.10a test compilation; S-MP rules explicitly want analyzer coverage of the applier/Multiplayer). **Include them.** The canonical "sim layer" = the exact source set the Godot-free `ProjectChimera.Sim.Tests.csproj` already compiles. Mirror it.
- `AI/` carries known float debt (it is not yet deterministic — float→Fixed is a prerequisite before AI runs in lockstep MP). Its findings are **advisory** and quantify that debt; they are **not** release-gated until the AI determinism migration. This is by design.

---

## Tasks / Subtasks

- [x] **Task 1 — Single source of truth for the sim include set (AC1, AC3).**
  - [x] Extracted the `<Compile Include>` sim-source set into `godot/SimSources.props`, anchored to `$(MSBuildThisFileDirectory)` so it resolves identically from either sibling project; imported from the test csproj. **Behavior-neutral confirmed:** Tier-1 stayed at 196 tests, goldens byte-identical.
  - [x] *(Chose the shared-props route, as recommended.)*

- [x] **Task 2 — Create the Godot-free analysis project (AC1, AC3, AC6).**
  - [x] Created `godot/ProjectChimera.Sim.Analysis/ProjectChimera.Sim.Analysis.csproj` (`Microsoft.NET.Sdk`, `net8.0`, `EnableDefaultCompileItems=false`, `IsPackable=false`, `Nullable=enable`, `RestorePackagesWithLockFile=true`), imports `..\SimSources.props`, references nothing Godot. Confirmed: it compiles the sim source cleanly with zero Godot in the closure (clean AOT verdict).
  - [x] `<IsAotCompatible>true</IsAotCompatible>` + `<TrimmerSingleWarn>false</TrimmerSingleWarn>` — IL2026/IL3050 fire (13/11 sites).
  - [x] Added `Microsoft.CodeAnalysis.BannedApiAnalyzers` 3.3.4 (`PrivateAssets=all`) + `<AdditionalFiles Include="BannedSymbols.txt" />`.

- [x] **Task 3 — Author `BannedSymbols.txt` (AC1, AC2).**
  - [x] Created `godot/ProjectChimera.Sim.Analysis/BannedSymbols.txt` — the **zero-baseline-only** set (DateTime/DateTimeOffset/Stopwatch/Environment.TickCount(64), Random/Guid.NewGuid/RandomNumberGenerator, `N:Godot`, float/double `.Parse`/`.ToString`, JsonPolymorphicAttribute). FromFloat/ToFloat deliberately NOT here — see Task 11/CHM0005.

- [x] **Task 4 — Allow-list the quantization boundary (AC2).**
  - [x] **Mechanism refined:** the `FixedJsonConverter` `FromFloat`/`ToFloat` allow-list is realized in the **custom analyzer (CHM0005)**, which recognizes `FixedJsonConverter` and does not report there (proven by paired unit tests: a `FromFloat` inside the converter does not fire, one elsewhere does). The converter source is therefore left clean (no pragma needed). **Additionally** the Task-6 baseline scan surfaced 2 *legitimate author/packaging-time* `DateTime` sites (`ContentPackager.cs:92`, `ContentPackageManifest.cs:110`) — allow-listed via tight `#pragma warning disable RS0030` so RS0030 stays a clean zero baseline.

- [x] **Task 5 — Severity + advisory/release cadence (AC4).**
  - [x] **Location corrected:** advisory severities pinned in `godot/.editorconfig` (RS0030/RS0031 + CHM0001–CHM0005 = `warning`), NOT a nested `Sim.Analysis/.editorconfig` — the sim sources are *linked* from `godot/src/**`, and Roslyn resolves editorconfig by each file's real on-disk path, so a project-dir editorconfig would never govern them.
  - [x] Release ratchet wired: `Condition="'$(ChimeraRelease)' == 'true'"` PropertyGroup sets `<WarningsAsErrors>$(WarningsAsErrors);RS0030</WarningsAsErrors>` (RS0030 is the verified zero-baseline gated set).

- [x] **Task 6 — Establish the baseline + pick the release-gated rule set (AC4).**
  - [x] Built the analysis project; captured unique per-rule counts (see [Baseline table](#baseline--ratchet-fill-during-dev)).
  - [x] **Ratchet applied:** only **RS0030** is release-gated (zero-baseline after the 2 author-time DateTime sites were allow-listed). Everything else (CHM0001=128, CHM0005=133, CHM0004=6, CHM0002=1, CHM0003=1, IL2026=11, IL3050=13) stays advisory, each mapped to its clearing story.

- [x] **Task 7 — CI sibling job (AC5).**
  - [x] Added job `tier1-analyzer-gate` to `.github/workflows/determinism-gate.yml` (`tier1-golden-gate` untouched). Reuses `windows-latest` / `setup-dotnet@v4` `8.0.419` / `--locked-mode`; runs the analyzer unit tests + the advisory gate build (green on master).
  - [x] Release enforcement reachable via a `workflow_dispatch` `run_release_gate` input **and** `refs/heads/release/**`; the release step uses `--no-incremental` (REQUIRED — see Change Log: the advisory step pre-compiles, and toggling only `ChimeraRelease` doesn't invalidate the up-to-date check, so without it CoreCompile is skipped and the gate silently passes).
  - [x] *(Left `actions/*@v4` to match the sibling job; the @v5 bump is non-urgent.)*

- [x] **Task 8 — Guard tests + dependency hygiene (AC6).**
  - [x] Extended `DependencyHygieneTests.cs`: asserts `BannedApiAnalyzers` is pinned 3.3.4 / `PrivateAssets=all` in the analysis project, and that no Roslyn analyzer package leaks into `godot.csproj`. (`GodotCsproj_CarriesExactly_TheSingleShippedPackage` still green — godot.csproj stays at one PackageReference.)
  - [x] Added `Meta/AnalyzerGateGuardTests.cs` (5 Tier-1 guards): `SimSources.props` exists; both projects import it (source set can't drift); `BannedSymbols.txt` exists + is an `AdditionalFile`; `<IsAotCompatible>` set; the custom analyzer is referenced `OutputItemType="Analyzer"`. `[CallerFilePath]` convention.

- [x] **Task 9 — Lock file, local proof, and the deliberate-violation gate test (AC4, AC6).**
  - [x] Generated + committed `packages.lock.json` for all three new projects; `dotnet restore … --locked-mode` succeeds for each (CI parity).
  - [x] Local proof: advisory build green (295 warnings, 0 errors); Tier-1 203 green, goldens byte-identical.
  - [x] **Release gate proven to fail:** a temporary `new System.Random()` in a sim file made the release build (`-p:ChimeraRelease=true --no-incremental`) emit `error RS0030` → **Build FAILED, exit 1**; reverted. The same code is advisory-only (warning, build succeeds) without the release flag.

- [x] **Task 11 — Custom `BannedSimApiAnalyzer` (APPROVED SCOPE EXPANSION, Alec 2026-06-24).**
  *The original story deferred the custom analyzer; Alec elected to build it now. This implements the four rules off-the-shelf cannot express, plus it resolves the RS0030-monolith tension (see note below).*
  - [x] Created `godot/analyzers/ProjectChimera.Analyzers/` (`netstandard2.0`) + `godot/analyzers/ProjectChimera.Analyzers.Tests/` (`net8.0` xUnit). Added `<Compile Remove="analyzers\**\*.cs" />` + `ProjectChimera.Sim.Analysis\**` to `godot.csproj` — single `PackageReference` intact, hygiene guard green.
  - [x] **CHM0001 — true `float`/`double` primitive ban** (`PredefinedType` keyword; skips `float.X` member-access to avoid double-reporting RS0030). Advisory — 128 sites.
  - [x] **CHM0002 — `Dictionary`/`HashSet` enumeration** (`foreach` whose collection implements `IDictionary`/`IReadOnlyDictionary`/`ISet`). Advisory — 1 site (LLMService).
  - [x] **CHM0003 — unstable sort** (`Array.Sort` / `List<T>.Sort`). Advisory — 1 real finding (`ScenarioDirector.cs:206`).
  - [x] **CHM0004 — magic cap literal** (int literal ≥ 8 as a relational bound or array size, not a `const`/enum). Advisory — 6 sites.
  - [x] **CHM0005 — `Fixed.FromFloat`/`ToFloat` outside the `FixedJsonConverter` allow-list.** Advisory — 133 sites. Owns the conversion ban so RS0030 stays clean/gateable.
  - [x] TDD'd each rule (RED→GREEN) via a `CSharpCompilation.WithAnalyzers` harness (no Roslyn test-SDK dep). 17 tests, paired positive/negative per rule. (RED proven: 10 positives failed against the stub.)
  - [x] Referenced from `ProjectChimera.Sim.Analysis` via `<ProjectReference OutputItemType="Analyzer" ReferenceOutputAssembly="false" />`; CHM rules fire over the sim set alongside RS0030 + IL rules.
  - [x] `ProjectChimera.Analyzers.Tests` runs in `tier1-analyzer-gate`; lock files committed for all three new projects.

- [ ] **Task 10 — Code review + sprint status.**
  - [ ] Run `gds-code-review` (3-layer adversarial). Address findings.
  - [ ] On PASS, set this story `done` in `sprint-status.yaml`.

---

## Dev Notes

### Developer context — why this story exists and how to not break it
Determinism is the project's load-bearing invariant (NFR-4): a single `float`, `System.Random`, wall-clock read, or `using Godot` in tick code desyncs multiplayer **silently** — no crash, no error, just two machines drifting apart. Until now that rule is enforced only by discipline and code review (Story 1.4 did a manual banned-API/float audit of the tick path). 1.10b makes the rule **mechanical**: a Roslyn gate that catches the next violation at build time.

The hard constraint that shapes everything: the gate must be **advisory on `master`** so the hourly `[AutoSave]` auto-commit loop (~24 commits/day) is never blocked, but **hard-fail on a release branch** so nothing nondeterministic actually ships (AR-36). That is a *severity cadence*, not two different analyzers.

The second constraint: this is a **brownfield** codebase with existing, *legitimate* float usage (load-time JSON quantization, float-typed content DTOs pending the D2 migration, AI float debt). A naïve "ban float, fail the build" would flag ~150+ sites and be useless. The correct pattern is the **ratchet**: turn the analyzers on advisory everywhere, then release-gate only the rules that are already at a zero baseline; let the rest stay advisory (visible debt) until their dedicated cleanup story clears them. **Your job is to install the gate and document the baseline — not to clean the codebase.**

### Architecture compliance — the exact rules to enforce (AR-36 + Consistency Rules)
AR-36 (verbatim): *"Banned-API Roslyn analyzer over the sim layer (bans `using Godot`, float gameplay math/`FromFloat`/`ToFloat`, `System.Random`/Godot RNG, `DateTime`/`Stopwatch`, Dictionary enumeration driving sim order, unstable `Array.Sort`, `GD.Print`, `[JsonPolymorphic]`, `[Export]` holding a key, bare cap literals). Composes with an AOT-eligibility analyzer. Advisory on master; hard enforcement release-branch only."* The architecture names the intended analyzer `BannedSimApiAnalyzer` + `AotAnalyzer` (folder `godot/analyzers/`).

Each architecture Consistency Rule carries an `ENFORCEMENT: analyzer flags …` clause. Map of rule → mechanism in THIS story:

| Rule | What the architecture wants flagged | Mechanism in 1.10b | In scope? |
|---|---|---|---|
| **S-CORE-3** | `ToFloat()`/`ToString()`/`float.Parse` in sim (A17 float/culture/rounding bug) | `BannedSymbols.txt` method bans (member-access → off-the-shelf catches) | ✅ |
| **S-CORE-4 / S-MP-5** | `DateTime`/`Stopwatch`/`Time.GetTicksMsec` wall-clock in sim | `BannedSymbols.txt` type bans (`Time.*` via `N:Godot`) | ✅ |
| **S-CORE-5** | `GD.Print`/`Console`/`using Godot` in sim | `N:Godot` ban + Godot-free compile (the compile itself forbids `using Godot`) | ✅ |
| **S-FX-4** | `Fixed.FromFloat` in tick-reachable code (allow-list the converter) | `BannedSymbols.txt` method ban + converter suppression; advisory on the load-time const sites (D2 debt) | ✅ (advisory) |
| **AR-2 / AR-38 / D3** | AOT-incompatibility (reflective JSON, dynamic codegen) | `<IsAotCompatible>` → IL2xxx/IL3xxx on the Godot-free compile | ✅ (advisory) |
| **S-CORE-1** | Dictionary/`HashSet` enumeration driving sim order | Best-effort only off-the-shelf; **defer** to custom analyzer. Runtime backstop: golden-checksum replay | ❌ deferred |
| **S-CORE-2** | unstable `Array.Sort`/`OrderBy` lacking a tie-break | Needs custom analyzer. **Defer.** Runtime backstop: golden-checksum replay | ❌ deferred |
| **"float gameplay math"** (true primitive ban) | every `float`/`double` declaration/arithmetic in sim | Needs custom analyzer (off-the-shelf can't — see Tech below). **Defer**; the conversion-boundary bans cover the AC's intent | ❌ deferred |
| **S-CON-2 / S-FX-5** | bare cap literals not in `SimConstants` | Needs custom analyzer. **Defer.** Backstop: `RulesetCorpusTest` | ❌ deferred |
| **S-CFG-1** | `[Export] string` holding a key | Already covered by `SecretExclusionTest` (Tier-1). Not re-implemented here | ❌ (existing test) |
| **AR-36** | `[JsonPolymorphic]` attribute | `BannedSymbols.txt` type ban | ✅ |

The deferred rows are why this story is "config + off-the-shelf," not "write a custom analyzer." Flag the custom-analyzer follow-up — see [Decisions for Alec](#decisions-for-alec-answer-before-or-during-dev).

### Library / framework requirements (verified June 2026)
- **`Microsoft.CodeAnalysis.BannedApiAnalyzers` — pin `3.3.4`.** Latest battle-tested stable; analyzer-only (no runtime closure), works against net8/net9. **Avoid `4.14.0`** (unlisted from NuGet, dotnet/roslyn #80232) and the `3.11+`/`4.x` line (a `System.Collections.Immutable 9.0.0` load failure in some SDK combos, #78695). Add with `<PrivateAssets>all</PrivateAssets>`.
  - Diagnostic **RS0030** = "Do not use banned APIs" (default Warning) — this is the gate rule. RS0031 = duplicate-entry detector (keep at warning). RS0035 = unrelated (`RestrictedInternalsVisibleTo`); ignore.
  - **`BannedSymbols.txt` doc-ID syntax:** `T:` type, `M:` method/ctor, `P:` property, `F:` field, `E:` event, `N:` namespace; backtick arity for generics (`` `2 ``), `{}` for generic instantiation, optional `;Message`, `//` comments. Wired via `<AdditionalFiles>`.
- **⚠ Off-the-shelf CANNOT ban the `float`/`double` keyword.** `T:System.Single`/`T:System.Double` only fire on *member access* (`(1.5f).ToString()`, `float.Parse`), **not** on declarations/fields/params/arithmetic (roslyn-analyzers #7371). So a true primitive ban needs a custom `DiagnosticAnalyzer`. **This story does not need one** because AR-36's float rules (S-CORE-3/S-FX-4) are the *conversion boundaries* (`FromFloat`/`ToFloat`/`Parse`/`ToString`), which are member-access and ARE catchable. Keep `T:System.Single`/`T:System.Double` lines as a cheap backstop, but the conversion-method bans are the real coverage.
- **AOT/trim: `<IsAotCompatible>true</IsAotCompatible>` (net8.0+)** is the umbrella — it sets `IsTrimmable` + `EnableTrimAnalyzer` + `EnableSingleFileAnalyzer` + `EnableAotAnalyzer`. These are Roslyn analyzers: they run on a plain `dotnet build`, **no `dotnet publish` / native toolchain needed**. `IL2xxx` = trim (IL2026 = `[RequiresUnreferencedCode]`), `IL3xxx` = AOT/single-file (IL3050 = `[RequiresDynamicCode]`). Add `<TrimmerSingleWarn>false</TrimmerSingleWarn>` for per-site detail.
  - **Why a Godot-free compilation is mandatory for AOT analysis:** GodotSharp is a large, *unannotated* native-interop assembly. Enabling AOT analysis on a project that references it gives noise (or, with `VerifyReferenceAotCompatibility`, IL3058 spam) and a *false* verdict about your own code. The sim layer is already pure C# with no Godot — analyzing it standalone gives a clean, true signal AND doubles as a compile-time guard that the sim has zero Godot dependencies.
- **Warn-local / fail-release severity:** the compiler switch `-warnaserror` (and `-warnaserror:RS0030,IL3050,…`) **overrides** editorconfig severity; the MSBuild property `-p:TreatWarningsAsErrors=true` does **not** (roslyn #43051). So gate via the explicit-ID `WarningsAsErrors` property or the `-warnaserror:<ids>` switch, never the blanket property. **Verify the gate actually fails** by introducing a deliberate `new Random()` once (Task 9).

### Starter `BannedSymbols.txt`
```text
// BannedSymbols.txt — determinism floor for the Godot-free sim compilation (AR-36 / S-CORE / S-FX).
// Format: {DocCommentId}[;Message]. RS0030 fires on USE. '//' starts a comment.
// Refine method overloads (ToString/Parse have several) against the Task-6 baseline scan.

// --- Wall-clock / nondeterministic time (S-CORE-4, S-MP-5) ---
T:System.DateTime;Wall-clock is nondeterministic in lockstep. Derive time from the sim tick (CurrentTick / Fixed dt).
T:System.DateTimeOffset;Wall-clock is nondeterministic in lockstep. Derive time from the sim tick.
T:System.Diagnostics.Stopwatch;Measures real time (nondeterministic). Count ticks in sim, never elapsed wall-time.
P:System.Environment.TickCount;Wall-clock is nondeterministic in sim.
P:System.Environment.TickCount64;Wall-clock is nondeterministic in sim.

// --- Nondeterministic randomness (AR-36) ---
T:System.Random;Use SimRng (seeded, folded into SimChecksum). System.Random differs across runs/peers.
M:System.Guid.NewGuid;Nondeterministic. Use a seeded id scheme.
T:System.Security.Cryptography.RandomNumberGenerator;Nondeterministic. Use SimRng in sim.

// --- Godot in the sim layer (S-CORE-5) --- (the Godot-free compile also forbids `using Godot`)
N:Godot;The sim layer is Godot-free pure C#. Godot types / GD.Print / Time.GetTicksMsec belong in presentation; sim logs via ILogSink.

// --- float<->Fixed and float<->string boundaries in the tick (S-CORE-3, S-FX-4) ---
// FixedJsonConverter (the AR-14 boundary) allow-lists FromFloat/ToFloat via [UnconditionalSuppressMessage].
M:ProjectChimera.Core.Fixed.FromFloat(System.Single);float->Fixed only at load (the converter). Never in-tick.
M:ProjectChimera.Core.Fixed.ToFloat;Fixed->float only on save/presentation. Never in a sim decision.
M:System.Single.Parse(System.String);Float parse is culture/rounding-nondeterministic (A17). Author thresholds as Fixed.
M:System.Double.Parse(System.String);Double parse is culture/rounding-nondeterministic (A17). Author thresholds as Fixed.
M:System.Single.ToString;Float formatting is culture-nondeterministic (A17). Never stringify floats in sim.
M:System.Double.ToString;Double formatting is culture-nondeterministic (A17). Never stringify doubles in sim.

// --- AOT / serialization traps (AR-36, D3) ---
T:System.Text.Json.Serialization.JsonPolymorphicAttribute;Reflective polymorphism is an AOT trap; use a closed typed registry.

// --- Optional backstop (member-access only; declarations NOT caught — see #7371) ---
T:System.Single;Prefer Fixed for all gameplay magnitudes (backstop: only member access is flagged).
T:System.Double;Prefer Fixed for all gameplay magnitudes (backstop: only member access is flagged).
```

### File structure requirements
**Create:**
- `godot/SimSources.props` *(Task 1 — or skip and duplicate globs)* — the single sim-source include set.
- `godot/ProjectChimera.Sim.Analysis/ProjectChimera.Sim.Analysis.csproj` — Godot-free analysis target.
- `godot/ProjectChimera.Sim.Analysis/BannedSymbols.txt`
- `godot/ProjectChimera.Sim.Analysis/.editorconfig` — advisory severities.
- `godot/ProjectChimera.Sim.Analysis/packages.lock.json` — generated, committed.
- `godot/ProjectChimera.Sim.Tests/Meta/<AnalyzerGateGuardTests>.cs` — guard test(s).

**Edit:**
- `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` — import `SimSources.props` (Task 1).
- `godot/src/Core/Definitions/FixedJsonConverter.cs` — allow-list suppression (Task 4).
- `godot/ProjectChimera.Sim.Tests/Meta/DependencyHygieneTests.cs` — analyzer-isolation assertions (Task 8).
- `.github/workflows/determinism-gate.yml` — add `tier1-analyzer-gate` job (Task 7).

**Do NOT touch:** `godot.csproj` (must stay at one `PackageReference`), any `*.golden.txt`, `SimChecksum.cs`, any sim source other than the converter suppression, `godot/tools/StressTest.cs`.

**Solution note:** `godot.sln` contains only `godot.csproj`; `ProjectChimera.Sim.Tests` is built by direct path in CI, not via the sln. The new analysis project follows the same pattern — built by path, **not** added to the sln.

### Testing requirements
- **Tier-1 (xUnit, Godot-free) only** for the guard tests — assert invariants as tests that run everywhere `dotnet test` runs (the 1.10a convention), never as CI-only shell steps. Use `[CallerFilePath]` + `Path.Combine` for portable paths (works on the CI checkout `D:\a\…`).
- The analyzer gate itself is verified by **building** the analysis project (the gate is the build, not a test). The "does the release gate actually fail" proof (Task 9) is a manual one-time check recorded in the Change Log — do not commit the deliberate violation.
- After every change, re-run `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release` and confirm the suite still passes (was 196 at 1.10a close — do not hardcode the count) and goldens are byte-identical (`git status --short -- '*.golden.txt'` is empty).

### Previous-story intelligence (1.10a — DONE, code-reviewed PASS)
1.10a built the CI lane this story plugs into. Reuse, don't duplicate:
- **`.github/workflows/determinism-gate.yml`** already exists with job `tier1-golden-gate` and a header comment that *explicitly reserves 1.10b/1.10c as sibling jobs* — **add a job, don't make a new workflow; keep `tier1-golden-gate` intact.** Lane: `windows-latest`, `actions/setup-dotnet@v4` pinned `8.0.419`, `dotnet restore … --locked-mode`, targets the Godot-free test csproj **by path** (never `godot.sln`/`godot.csproj`, never installs Godot).
- **`global.json`** pins SDK `8.0.419` (rollForward `latestFeature`). **`godot/ProjectChimera.Sim.Tests/packages.lock.json`** exists; CI uses `--locked-mode`. If you add a NuGet dep anywhere, you MUST regenerate that project's lock file or `--locked-mode` restore fails.
- **`Meta/DependencyHygieneTests.cs`** (the `Meta/` folder exists) enforces AR-2/AR-35: `godot.csproj` carries **exactly one** `PackageReference` (NakamaClient 3.13.0) via `GodotCsproj_CarriesExactly_TheSingleShippedPackage`, and test-only deps stay isolated. **Adding any `PackageReference` to `godot.csproj` will fail this test** — keep the analyzer dev-only (`PrivateAssets=all`) and out of the shipping project. Extend this test to cover the new analysis project (Task 8).
- **Conventions to respect:** never "fix" a red gate by re-recording a golden; assert invariants as Tier-1 guard tests; no hardcoded test counts (rely on xUnit's exit code); never set `CHIMERA_GOLDEN_RECORD`; SDK pinning is reproducibility hygiene, not a correctness gate.
- **No `.editorconfig` rules / `Directory.Build.props` / `.targets` exist yet** (only a charset-only `godot/.editorconfig`). 1.10b creates the first analyzer-config files — greenfield, nothing to extend there.

### Git intelligence
- The repo auto-commits hourly as `[AutoSave] <timestamp>` (~24/day); story work lands *inside* that stream, not as one tidy `feat:` commit. Deliberate commits use Conventional-Commits + the `Co-Authored-By: Claude …` / `Claude-Session:` trailers.
- Build/CI files live at: `.github/workflows/determinism-gate.yml`, `global.json` (repo root), `godot/ProjectChimera.Sim.Tests/*.csproj` + its `packages.lock.json`. No `.props`/`.targets`/analyzer-config files in history → confirmed greenfield for analyzer config.

### Project Context Rules (from `_bmad-output/project-context.md`)
- **Sim/Presentation boundary is sacred.** Sim = `src/Core, Combat, Economy, Navigation` (+ sim-side AI/Multiplayer): pure C#, no `using Godot;`, no `float`/`Vector3` for gameplay state. This story *enforces* that boundary mechanically — it must not blur it.
- **`Fixed` (16.16) is the only sim numeric type;** `Fixed.FromFloat` is authoring/load-time only (`src/Core/FixedPoint.cs`). The converter is the one quantization boundary (AR-14).
- **Determinism rules:** ascending-ID iteration; no `Dictionary`/`HashSet` enumeration in sim order; no wall-clock; seeded `SimRng` only. These are exactly the rules the analyzer encodes.
- **Dependency discipline:** the sole shipped NuGet dep is `NakamaClient 3.13.0`; prefer in-repo over new deps; test/tool deps stay off the shipping sim (keeps it AOT-eligible). The analyzer package is a dev-only tool dep — honor this.
- **Engine/runtime:** Godot **4.6.3** (csproj already bumped), C# `net8.0` (`.NET 9 AOT` is a *future* aspiration — 1.10b is the analyzer gate toward it, not the AOT build).
- **Conventions:** `PascalCase.cs`, `#nullable enable` per file, comment public methods. Brownfield style: investigate before changing, favor small shippable slices, respect determinism constraints.

### Baseline / ratchet (FILLED during dev — unique sites, 2026-06-24)
_Counts are unique `(file,line)` sites from a Release build of `ProjectChimera.Sim.Analysis` (raw build output double-emits; deduped here)._

| Rule ID | Unique sites | Release-gated? | Disposition / cleared by |
|---|---|---|---|
| RS0030 — `N:Godot` | 0 | ✅ yes | — (the Godot-free compile also forbids `using Godot`) |
| RS0030 — `System.Random` / `Guid.NewGuid` / `RandomNumberGenerator` | 0 | ✅ yes | — |
| RS0030 — `DateTime`/`DateTimeOffset`/`Stopwatch`/`Environment.TickCount` | 2 → **0 effective** | ✅ yes | the 2 sites (`ContentPackager.cs:92`, `ContentPackageManifest.cs:110`) are author/packaging-time, not tick-reachable, excluded from the sim/start-state hash → explicitly allow-listed (`#pragma RS0030`) |
| ~~RS0030 — `float`/`double` `.Parse`/`.ToString`~~ → **moved to CHM0006 (code-review 2026-06-24)** | n/a | ❌ advisory | The bare-name doc-IDs resolved **unreliably** (caught nothing in the real build — proven by injection probe); replaced by the semantic CHM0006. RS0030's gated set is unaffected (still zero-baseline). |
| RS0030 — `JsonPolymorphicAttribute` | 0 | ✅ yes | — |
| **→ RS0030 (the gated set)** | **0 effective** | **✅ GATED** | release gate (`-warnaserror:RS0030`) clean today; proven to FAIL on a deliberate `new System.Random()` |
| CHM0001 — `float`/`double` primitive keyword | 128 | ❌ advisory | D2 "Fixed end-to-end" migration |
| CHM0005 — `Fixed.FromFloat`/`ToFloat` (non-converter) | 133 | ❌ advisory | D2 "Fixed end-to-end" migration |
| CHM0004 — magic cap literal | 6 | ❌ advisory | structural-cap → `SimConstants` work (Epics 2/7) |
| CHM0002 — Dictionary/HashSet enumeration | 1 (`LLMService.cs:584`) | ❌ advisory | AI float→Fixed / determinism work |
| CHM0003 — unstable `Array.Sort` | 1 (`ScenarioDirector.cs:206`) | ❌ advisory | **real finding** — tie-break fix (tracked, not fixed here per scope) |
| CHM0006 — `float`/`double` `.Parse`/`.ToString` (semantic) | 1 (`FixedPoint.cs:147`) | ❌ advisory | **added code-review 2026-06-24** — the A17 float↔string boundary, reliably detected (replaces the unreliable RS0030 bare-DocID bans). The 1 site is the `Fixed.ToString` debug formatter (D2 debt; already CHM0005-flagged). |
| AOT IL2026 / IL3050 (reflective STJ) | 11 / 13 | ❌ advisory | D3 JSON source-gen migration |
| _(not a gate rule)_ CS8765 nullability mismatch | 2 | n/a | pre-existing; surfaced only because the analysis project sets `Nullable=enable`. Not determinism, not gated. |

**Note vs. the pre-dev estimate:** FromFloat/ToFloat was estimated "≈95"; the actual is **133** (CHM0005), and the raw `float`/`double` keyword count is **128** (CHM0001) — both larger than guessed, which is exactly why CHM0005 had to be split off RS0030 for the gate to stay clean.

### References
- `_bmad-output/planning-artifacts/epics.md` — Epic 1 §Story 1.10b (user story, AC, "Covers AR-36 / Depends on 1.10a"); AR-2, AR-36, AR-38; Story 1.4 banned-API audit AC.
- `_bmad-output/game-architecture.md` — AR-36/AR-38 detail; §"Determinism enforcement" (lines ~1287–1416, the two-tier testing + banned-API analyzer + AOT gate); Consistency Rules S-CORE-1..6, S-FX-4/5, S-CFG-1, S-CON-2, S-MP-5, S-TEST-1 (lines ~2342–2464); target directory tree with `analyzers/BannedSimApiAnalyzer` + `AotAnalyzer` and the StressTest relocation note (lines ~1569–1593).
- `_bmad-output/project-context.md` — determinism rules, sim/presentation boundary, dependency discipline.
- `_bmad-output/implementation-artifacts/1-10a-…md` — the CI lane, `DependencyHygieneTests`, lock-file pattern, guard-test conventions.
- Current code: `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` (the source-include template to mirror); `godot/src/Core/Definitions/FixedJsonConverter.cs:50,54` (allow-list targets); `godot/src/Core/FixedPoint.cs` (`Fixed.FromFloat`); `.github/workflows/determinism-gate.yml` (the lane to extend); `godot/.editorconfig` (charset-only today).
- Memory: `[[banned-api-aot-analyzer-tooling]]` (tooling facts), `[[project-chimera-gds-planning-chain]]`.
- External (verified June 2026): BannedApiAnalyzers `BannedSymbols.txt` format spec (dotnet/roslyn `BannedApiAnalyzers.Help.md`); RS0030/RS0031 docs (dotnet/roslyn-analyzers); the float/double member-access limitation (roslyn-analyzers #7371, #2747); namespace-ban-vs-`using` (#6647); 4.14.0 removal (#80232); Native AOT overview (`IsAotCompatible` enables all four analyzers, net8+); trim-libraries prep; editorconfig-vs-`-warnaserror` precedence (roslyn #43051; MS Learn configuration-files).

---

## Decisions for Alec (answer before or during dev)

> **RESOLVED 2026-06-24 (dev):** (1) **Dedicated `ProjectChimera.Sim.Analysis` project** — chosen. (3) **"float gameplay math" = conversion boundaries** — adopted (CHM0005/RS0030), AND (2) **the custom `BannedSimApiAnalyzer` was built now** at Alec's explicit direction (scope expansion → Task 11). So 1.10b ships the off-the-shelf gate **and** the custom analyzer.

1. **Analyzer home — dedicated project vs. fold into the test project.** *Recommended:* a dedicated Godot-free `ProjectChimera.Sim.Analysis` project (clean AOT verdict — no xunit/test-SDK in the closure; analyzes only sim source; doubles as a "sim has zero Godot deps" compile guard). *Alternative:* add `<IsAotCompatible>` + BannedApiAnalyzers to the existing `ProjectChimera.Sim.Tests.csproj` (no new project, but you must editorconfig-mute the IL/RS rules on the test `.cs` files and accept xunit AOT noise). The story assumes the dedicated project.
2. **Custom `BannedSimApiAnalyzer` — now or later?** The architecture's folder tree imagines a hand-written analyzer covering the *true* `float`/`double` primitive ban, Dictionary-enumeration, unstable-sort, and magic-cap-literal detection — things off-the-shelf can't express. This story deliberately **defers** it (those rules have runtime backstops: golden-checksum replay for sort/enum order, `RulesetCorpusTest` for caps, `SecretExclusionTest` for `[Export]`-key). Confirm you're OK shipping 1.10b as "off-the-shelf bans + AOT analyzer (advisory/release cadence)" and tracking the custom analyzer as a follow-up — or say the word and I'll scope the custom analyzer into 1.10b (larger story).
3. **"float gameplay math" interpretation.** Read here as the **conversion boundaries** (`FromFloat`/`ToFloat`/`float.Parse`/`float.ToString`) per the architecture's S-CORE-3 enforcement wording, not "every float declaration." That makes the AC config-satisfiable. If you want a true primitive ban, it pulls in the custom analyzer from #2.

## Dev Agent Record

### Agent Model Used
Claude Opus 4.8 (`claude-opus-4-8`), via the `gds-dev-story` workflow.

### Debug Log References
Three issues hit and resolved during dev (no HALTs):
1. **`MSB4025` — analyzer csproj failed to load:** an XML comment contained `--locked-mode`; `--` is illegal inside XML comments. Reworded to `locked-mode`.
2. **CHM0005 unit tests RED for the wrong reason:** the test snippets placed `using ProjectChimera.Core;` *after* the in-snippet `namespace` declaration → `CS1529`, so the unqualified `Fixed` never bound and the semantic lookup found no symbol. Moved the `using` to the top of each snippet. (The inside-converter negative test had been passing *vacuously* for the same reason — now passes for the right reason.)
3. **Release-gate proof reported a FALSE PASS:** building the advisory variant immediately before the release variant left the assembly up-to-date, and toggling only the `ChimeraRelease` MSBuild property does **not** invalidate MSBuild's incremental check → `CoreCompile` was skipped, so the analyzers never re-ran and `-warnaserror:RS0030` had nothing to escalate. Fixed with `--no-incremental`. **This also fixed a latent CI bug:** the release step runs after the advisory step on the same runner, so the gate would have silently passed on real violations — `--no-incremental` is now on the CI release step too.

### Completion Notes List
**What shipped.** A determinism analyzer gate over the Godot-free sim compilation (`ProjectChimera.Sim.Analysis`), composed of three analyzer families that run on a plain `dotnet build`:
- **Off-the-shelf `BannedApiAnalyzers` 3.3.4 (RS0030)** driven by `BannedSymbols.txt` — the **zero-baseline** hard determinism bans (Random/DateTime/Stopwatch/TickCount/Guid.NewGuid/RandomNumberGenerator/`N:Godot`/float·double `.Parse`·`.ToString`/`JsonPolymorphicAttribute`). **Release-gated.**
- **Custom `BannedSimApiAnalyzer` (CHM0001–CHM0005)** — the rules off-the-shelf cannot express: true `float`/`double` keyword ban, Dictionary/HashSet enumeration, unstable `Array.Sort`/`List.Sort`, magic cap literal, and `Fixed.FromFloat`/`ToFloat`-outside-the-converter. **Advisory** (legitimate existing debt). 17 TDD unit tests.
- **Built-in trim/AOT analyzers (IL2xxx/IL3xxx)** via `<IsAotCompatible>` on the Godot-free compile — a clean, meaningful AOT verdict. **Advisory** (reflective STJ = D3 debt).

**Cadence (AR-36):** advisory on master (warnings only → the hourly `[AutoSave]` loop is never blocked); release builds (`-p:ChimeraRelease=true`, or a `release/**` branch / the `workflow_dispatch` input) escalate the zero-baseline set (RS0030) to errors and hard-fail. Proven to fail on a deliberate `new System.Random()` and to stay green on clean code.

**Key design decision — RS0030 stays a clean zero-baseline set.** RS0030 is one monolithic diagnostic ID, so it can only be release-gated if *every* banned symbol it covers is zero-baseline. The non-zero `Fixed.FromFloat`/`ToFloat` ban (133 legitimate D2-debt sites) was therefore moved OFF `BannedSymbols.txt` into the custom analyzer as advisory **CHM0005** (which owns the `FixedJsonConverter` allow-list in-analyzer). This resolves a latent contradiction in the story's literal plan (it had FromFloat/ToFloat in `BannedSymbols.txt` *and* wanted RS0030 release-gated — mutually exclusive given the ~95+ debt sites).

**Refinements to the story's literal plan (all documented in the tasks):**
- **Converter allow-list (AC2)** realized in the analyzer (CHM0005 recognizes `FixedJsonConverter`), so no pragma/edit was needed in `FixedJsonConverter.cs`. Paired unit tests prove the allow-list.
- **Severity editorconfig** placed in `godot/.editorconfig`, not a nested `Sim.Analysis/.editorconfig` — the sim sources are *linked* from `godot/src/**`, and Roslyn resolves editorconfig by each file's real path, so a project-dir editorconfig would never govern them.
- **`godot.csproj` `<Compile Remove>`** for `analyzers/**` (+ defensive `ProjectChimera.Sim.Analysis/**`) was required (the game globs `godot/**/*.cs`; the analyzer sources reference `Microsoft.CodeAnalysis` and would break the shipping build). It adds no `PackageReference`, so the AR-2 hygiene guard stays green.
- **Two author-time `DateTime` sites allow-listed** (`ContentPackager`, `ContentPackageManifest`) — packaging/metadata timestamps, never tick-reachable, excluded from the sim hash — so RS0030's effective baseline is zero.

**One real finding (left advisory per scope):** CHM0003 flagged an unstable `Array.Sort` at `ScenarioDirector.cs:206`. Not fixed here (1.10b makes debt visible, it doesn't clean it); the analyzer now guards it.

**AC status:** AC1 ✅ (banned-API gate over the Godot-free sim set, all AR-36 determinism bans across RS0030 + CHM). AC2 ✅ (converter allow-listed via CHM0005; a new FromFloat/ToFloat elsewhere reports). AC3 ✅ (`<IsAotCompatible>` IL2xxx/IL3xxx over a Godot-free closure). AC4 ✅ (advisory master / `-warnaserror:RS0030` release, wired and proven to fail). AC5 ✅ (`tier1-analyzer-gate` sibling job, `tier1-golden-gate` untouched). AC6 ✅ (analyzer dev-only `PrivateAssets=all`; `godot.csproj` still one `PackageReference`; three `packages.lock.json` committed).

**Verification:** Tier-1 **203** green (196 + 7 new guards), goldens byte-identical; analyzer unit tests **17/17**; advisory gate build 0 errors; release gate fails on the deliberate violation (exit 1) and passes clean; all three new projects restore `--locked-mode`; `bin/obj` confirmed git-ignored.

### File List
**Created**
- `godot/SimSources.props`
- `godot/ProjectChimera.Sim.Analysis/ProjectChimera.Sim.Analysis.csproj`
- `godot/ProjectChimera.Sim.Analysis/BannedSymbols.txt`
- `godot/ProjectChimera.Sim.Analysis/packages.lock.json`
- `godot/analyzers/ProjectChimera.Analyzers/ProjectChimera.Analyzers.csproj`
- `godot/analyzers/ProjectChimera.Analyzers/BannedSimApiAnalyzer.cs`
- `godot/analyzers/ProjectChimera.Analyzers/AnalyzerReleases.Shipped.md`
- `godot/analyzers/ProjectChimera.Analyzers/AnalyzerReleases.Unshipped.md`
- `godot/analyzers/ProjectChimera.Analyzers/packages.lock.json`
- `godot/analyzers/ProjectChimera.Analyzers.Tests/ProjectChimera.Analyzers.Tests.csproj`
- `godot/analyzers/ProjectChimera.Analyzers.Tests/AnalyzerTestHarness.cs`
- `godot/analyzers/ProjectChimera.Analyzers.Tests/BannedSimApiAnalyzerTests.cs`
- `godot/analyzers/ProjectChimera.Analyzers.Tests/packages.lock.json`
- `godot/ProjectChimera.Sim.Tests/Meta/AnalyzerGateGuardTests.cs`

**Modified**
- `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` — import `..\SimSources.props` (replaces the inline sim-include set).
- `godot/godot.csproj` — `<Compile Remove>` for `analyzers\**` + `ProjectChimera.Sim.Analysis\**` (no new `PackageReference`).
- `godot/.editorconfig` — advisory severities for RS0030/RS0031 + CHM0001–CHM0005 over `src/**`.
- `godot/ProjectChimera.Sim.Tests/Meta/DependencyHygieneTests.cs` — analyzer-isolation + analyzer-pin guards.
- `godot/src/Core/Definitions/ContentPackager.cs` — `#pragma RS0030` allow-list (author-time `DateTime`).
- `godot/src/Core/Definitions/ContentPackageManifest.cs` — `#pragma RS0030` allow-list (author-time `DateTime`).
- `.github/workflows/determinism-gate.yml` — `tier1-analyzer-gate` sibling job + `workflow_dispatch` `run_release_gate` input.
- `_bmad-output/implementation-artifacts/sprint-status.yaml` — status transitions.
- `_bmad-output/implementation-artifacts/1-10b-…md` — this story (frontmatter `baseline_commit`, tasks, Dev Agent Record, baseline table, File List, Change Log, Status).

### Change Log
| Date | Change |
|---|---|
| 2026-06-24 | Story set in-progress; `baseline_commit: ddd9dc5`. Approved scope expansion (custom analyzer → Task 11). |
| 2026-06-24 | Task 1: extracted `godot/SimSources.props` (behavior-neutral; Tier-1 196 green, goldens identical). |
| 2026-06-24 | Task 11: built `BannedSimApiAnalyzer` (CHM0001–CHM0005) + 17 TDD unit tests (RED→GREEN proven). |
| 2026-06-24 | Tasks 2/3/5: stood up `ProjectChimera.Sim.Analysis` (off-the-shelf RS0030 + custom analyzer + `<IsAotCompatible>`); zero-baseline `BannedSymbols.txt`; advisory severities in `godot/.editorconfig`; release ratchet (`ChimeraRelease`→`WarningsAsErrors=RS0030`). |
| 2026-06-24 | Task 6: baseline scan (unique sites: RS0030 2→0 after allow-list, CHM0001 128, CHM0005 133, CHM0004 6, CHM0002 1, CHM0003 1, IL2026 11, IL3050 13). Allow-listed 2 author-time `DateTime` sites. |
| 2026-06-24 | Task 7: `tier1-analyzer-gate` CI sibling job (advisory build + analyzer unit tests; release variant on `release/**`/dispatch, `--no-incremental`). |
| 2026-06-24 | Task 8: `DependencyHygieneTests` extended + new `AnalyzerGateGuardTests` (Tier-1 203 green). |
| 2026-06-24 | Task 9: committed 3 lock files; **deliberate-violation proof** — `new System.Random()` → release build `error RS0030`, FAILED (exit 1); reverted. |
| 2026-06-24 | Status → review. |
| 2026-06-24 | `gds-code-review` (3-layer adversarial, Opus): all 6 ACs confirmed met; gate **empirically verified** (injected `DateTime.UtcNow` fired RS0030; real build RS0030=0, 295 warns/0 err). 2 actionable findings (1 decision, 1 patch); the Edge Case Hunter's "gate would hard-fail" High was disproven by build. |
| 2026-06-24 | Review patch (decision-1): **CHM0006** added — advisory, *semantic* `float`/`double` `.Parse`/`.ToString` detection — closes the AC1 float↔string gap the unreliable RS0030 bare-DocID bans left open (the bans matched in a vanilla probe but caught nothing in the real build). Removed the 4 dead `BannedSymbols.txt` lines. RS0030 stays zero-baseline; injection probe confirms `float.Parse`/`float.ToString`/`double.Parse` now caught. |
| 2026-06-24 | Review patch (patch-1): CHM0005 converter allow-list **namespace-anchored** to `ProjectChimera.Core.Definitions` (was bare type-name, exploitable by any same-named type). Analyzer unit tests **17→21** green; analysis build CHM0006=1 site (`FixedPoint.cs:147`), RS0030=0; **Tier-1 203 green, goldens byte-identical**. |
| 2026-06-24 | Code review **PASS** → Status `done`. |

---

### Review Findings (gds-code-review, 2026-06-24)

_3-layer adversarial review (Blind Hunter / Edge Case Hunter / Acceptance Auditor), all on Opus 4.8, against `baseline_commit ddd9dc5`. The Acceptance Auditor confirmed **all 6 ACs met, zero scope breaches, all Dev-Agent-Record claims true**. Findings below are post-**empirical verification** — the reviewer built `ProjectChimera.Sim.Analysis` and ran a live RS0030 injection probe rather than reasoning from assumed analyzer semantics. **2 actionable (1 decision, 1 patch), 6 deferred, 6 dismissed.**_

**✅ Verified clean (empirical, not asserted):** RS0030 is wired & functional — an injected `System.DateTime.UtcNow` in `src/Core/` fired RS0030 (2 sites); the real advisory build is green at **295 warnings / 0 errors with RS0030 = 0**, so the release-gated set is genuinely clean on current code (**AC4 holds**). Per-rule counts reproduced the baseline table (CHM0001/0005/0004/0002/0003 + IL2026/IL3050 all at documented values).

#### Decision needed
- [x] [Review][Decision] ✅ **RESOLVED 2026-06-24 — Alec chose option 1 (fix now):** added advisory **CHM0006** (reliable *semantic* detection of `float`/`double` `.Parse`/`.ToString`), deleted the 4 dead bare-DocID lines from `BannedSymbols.txt`. Injection-probe-proven: `float.Parse`/`float.ToString`/`double.Parse` are now caught (CHM0006) where RS0030 previously caught nothing; RS0030 stays exactly zero-baseline so the release gate is untouched. AC1's float↔string claim now holds. — _Original finding:_ **The four `M:System.Single/Double.Parse/.ToString` bans in `BannedSymbols.txt` did not reliably fire — the float↔string release-gate coverage was effectively non-functional** [`BannedSymbols.txt:28-31`] — Real-pipeline probe: an injected `float.ToString("F4")` **and** `float.Parse("1.0")` in `src/Core/` produced **0 RS0030** (only the `DateTime` type-ban fired). Bare-name method doc-IDs resolve unreliably in the real compilation (the *same* `float.ToString("F4")` DID fire in a vanilla micro-test, so this is config-dependent and not trustworthy as a gate). Net: AC1's literal claim that the gate reports "`float.Parse`/`float.ToString`" is **not met by the release-gated RS0030 set**. Mitigations already in place: CHM0001 (float keyword, advisory) flags float locals/fields; `FixedJsonConverter` is the sanctioned parse path; golden-checksum replay backstops a real desync. **Decide:** (1) fix now — explicit overload signatures, or move the float-parse/stringify ban into the custom analyzer as a CHM rule; (2) accept & document — downgrade the baseline claim, rely on CHM0001/CHM0005 + golden backstop; (3) fold into the D2 "Fixed end-to-end" migration that removes the float debt anyway.

#### Patch
- [x] [Review][Patch] ✅ **APPLIED 2026-06-24:** `IsInsideAllowlistedConverter` now resolves the enclosing type's symbol and requires namespace `ProjectChimera.Core.Definitions`; a paired negative test (a same-named `FixedJsonConverter` in another namespace **does** report CHM0005) is green. — _Original finding:_ **CHM0005 converter allow-list matched `FixedJsonConverter` by bare type name with no namespace anchor** [`BannedSimApiAnalyzer.cs:223-229`] — `IsInsideAllowlistedConverter` matches `t.Identifier.ValueText == "FixedJsonConverter"` only; any future / UGC / test-double type named `FixedJsonConverter` in *any* namespace silently exempts CHM0005 forever. The sibling receiver check (`:191-192`) correctly pins `ownerNs == "ProjectChimera.Core"` — the allow-list side should be anchored the same way. **Flagged independently by all three review layers** (highest-confidence finding). Fix: anchor to namespace `ProjectChimera.Core.Definitions` (or resolve the containing type's symbol) + add a paired negative test. Advisory rule, single converter today → latent, but it's the rule's trust boundary and the fix is cheap.

#### Deferred (advisory-rule polish — all backstopped, none blocking)
- [x] [Review][Defer] **CHM0002 only inspects `foreach`** — misses `.Keys`/`.Values`, LINQ, and explicit `.GetEnumerator()` enumeration of a Dictionary/HashSet [`BannedSimApiAnalyzer.cs:135`] — advisory; golden-checksum replay backstops order desync; story scoped CHM0002 as best-effort.
- [x] [Review][Defer] **CHM0001 misses fully-qualified `System.Single`/`System.Double` and `var`-inferred float** [`BannedSimApiAnalyzer.cs:119`] — only the `float`/`double` keyword (`PredefinedType`) fires; the XML-doc's "the real coverage" slightly overclaims. Advisory, rare spelling.
- [x] [Review][Defer] **CHM0003 misses `Span<T>.Sort` / delegate-reached sorts and over-flags tie-broken (deterministic) sorts** [`BannedSimApiAnalyzer.cs:182-184`] — advisory; story scoped CHM0003 to `Array.Sort`/`List<T>.Sort`.
- [x] [Review][Defer] **CHM0004 heuristic — false-positives on ordinary loop bounds/comparisons; blind to `static readonly` caps and negated (`< -N`) bounds** [`BannedSimApiAnalyzer.cs:200-276`] — advisory by design ("Heuristic and advisory"); cleanup story (Epics 2/7) will triage.
- [x] [Review][Defer] **Analyzer test hardening** [`BannedSimApiAnalyzerTests.cs`] — `OrderBy_does_not_report_CHM0003` is structurally vacuous (CHM0003 can't match `OrderBy` regardless); positive coverage omits `float?`, `List<float>`, tuple-element and lambda-param forms (the bulk of the 128 CHM0001 sites). The rules empirically fire on these; the tests just don't pin them.
- [x] [Review][Defer] **CI release-gate `== 'true'` string-boolean comparison is load-bearing-by-quirk** [`determinism-gate.yml`] — correct today (dispatch inputs serialize as strings); add a guard comment so a future `== true` "cleanup" can't silently disable the on-demand release proof.

#### Dismissed as noise (6)
1. **[Edge Case Hunter, High] "RS0030 release gate would hard-fail on `FixedPoint.cs:147`"** — empirically false; RS0030 = 0, gate clean & functional. That line is flagged by CHM0005 (the `ToFloat()` call), not RS0030; the `.ToString("F4")` does not match the bare `M:System.Single.ToString` ban in the real compilation (see Decision finding).
2. **[Edge] CHM0005 false-positive if the converter delegates to a helper outside the type** — speculative future refactor; the current converter inlines the call (correct).
3. **[Blind] `ImplicitUsings disable` may break the Godot-free compile** — empirically the analysis project builds clean (295 warns / 0 err).
4. **[Auditor] `Microsoft.NET.ILLink.Tasks` is a `Direct` dep in the lock file** — benign/expected from `<IsAotCompatible>`; `godot.csproj` still carries exactly one `PackageReference` (verified).
5. **[Blind] advisory/release `--no-incremental` asymmetry** — no `bin`/`obj` caching in the lane + fresh checkout per run; not a current defect.
6. **[Auditor] `.editorconfig` `[*.cs]` global vs File-List "over `src/**`"** — harmless wording nit; only `ProjectChimera.Sim.Analysis` actually runs these rules.
