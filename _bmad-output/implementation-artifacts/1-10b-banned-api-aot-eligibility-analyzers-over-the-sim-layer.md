# Story 1.10b: Banned-API + AOT-eligibility analyzers over the sim layer

Status: ready-for-dev

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

- [ ] **Task 1 — Single source of truth for the sim include set (AC1, AC3).**
  - [ ] Extract the `<Compile Include>` sim-source set currently inline in `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` into a shared `godot/SimSources.props` (the Core/Combat/Economy/Navigation/AI globs + the 3 Multiplayer files + `Multiplayer/Server/**`, and the `Remove`s for `MainScene.cs` + `Bootstrap/Phases/**`). Import it from the test csproj. This must be **behavior-neutral** — the test project compiles the identical file set; run the Tier-1 suite and confirm the same test count + byte-identical goldens.
  - [ ] *(Alternative if you prefer no refactor: duplicate the identical globs in the new analysis project. Shared props is recommended — it prevents the analyzer's coverage from silently drifting from the test project's as new sim files are added.)*

- [ ] **Task 2 — Create the Godot-free analysis project (AC1, AC3, AC6).**
  - [ ] Create `godot/ProjectChimera.Sim.Analysis/ProjectChimera.Sim.Analysis.csproj` (`Microsoft.NET.Sdk`, `net8.0`, **not** `Godot.NET.Sdk`). Set `<EnableDefaultCompileItems>false</EnableDefaultCompileItems>`, `<IsPackable>false</IsPackable>`, `<Nullable>enable</Nullable>`, `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>`. Import `..\SimSources.props` (or duplicate the globs). It references **nothing Godot** — if a sim file leaks a `using Godot`, this project fails to compile, which is itself a useful guard.
  - [ ] Enable the AOT/trim analyzer: `<IsAotCompatible>true</IsAotCompatible>` + `<TrimmerSingleWarn>false</TrimmerSingleWarn>` (expands the IL2104 rollup into per-site IL2xxx).
  - [ ] Add the banned-API analyzer: `<PackageReference Include="Microsoft.CodeAnalysis.BannedApiAnalyzers" Version="3.3.4"><PrivateAssets>all</PrivateAssets></PackageReference>` and `<AdditionalFiles Include="BannedSymbols.txt" />`.

- [ ] **Task 3 — Author `BannedSymbols.txt` (AC1, AC2).**
  - [ ] Create `godot/ProjectChimera.Sim.Analysis/BannedSymbols.txt` from the [starter list below](#starter-bannedsymbolstxt). Use the exact doc-comment-ID syntax. Refine method overloads against the baseline scan (Task 6).

- [ ] **Task 4 — Allow-list the quantization boundary (AC2).**
  - [ ] Suppress `RS0030` for the two legitimate `Fixed.FromFloat`/`Fixed.ToFloat` calls in `src/Core/Definitions/FixedJsonConverter.cs` (`Read` line 50, `Write` line 54). Prefer method-level `[System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("ApiDesign", "RS0030", Justification = "AR-14: the single float↔Fixed quantization boundary")]` (also documents intent and is visible to the AOT tools), or a tight `#pragma warning disable RS0030` / `restore` around each call. Confirm a *new* `FromFloat` elsewhere still reports.

- [ ] **Task 5 — Severity + advisory/release cadence (AC4).**
  - [ ] Add a nested `.editorconfig` in `godot/ProjectChimera.Sim.Analysis/` (`root = true`) setting advisory defaults: `dotnet_diagnostic.RS0030.severity = warning`, `dotnet_diagnostic.RS0031.severity = warning`. (Keep AOT IL-rules at their default warning severity.)
  - [ ] Wire the release ratchet: in the analysis csproj, a `Condition="'$(ChimeraRelease)' == 'true'"` `PropertyGroup` sets `<WarningsAsErrors>$(WarningsAsErrors);RS0030;RS0031;<the zero-baseline IL ids></WarningsAsErrors>` for **only** the rule IDs chosen in Task 6. (Use `WarningsAsErrors` for specific IDs, **not** `-p:TreatWarningsAsErrors`, because editorconfig severity overrides the property but not the explicit ID escalation / the `-warnaserror` switch — roslyn #43051.)

- [ ] **Task 6 — Establish the baseline + pick the release-gated rule set (AC4).**
  - [ ] Build the analysis project locally and capture the full warning list per rule ID. Record counts in the [Baseline table](#baseline--ratchet-fill-during-dev) in this story.
  - [ ] **Ratchet rule:** a rule is release-gated (added to `WarningsAsErrors`) **only if its current count is zero or every site is explicitly allow-listed.** Expect zero-baseline (gate now): `T:System.Random`, `DateTime`/`DateTimeOffset`/`Stopwatch`, `Guid.NewGuid`, `N:Godot`, `JsonPolymorphicAttribute`. Expect non-zero (advisory only): `Fixed.FromFloat`/`ToFloat` (≈95 load-time static-const sites are D2 debt), the AI float sites, and the IL2026/IL3050 reflective-JSON AOT warnings (D3 source-gen debt). Document each non-gated rule with the story that will clear it.

- [ ] **Task 7 — CI sibling job (AC5).**
  - [ ] Add job `tier1-analyzer-gate` to `.github/workflows/determinism-gate.yml` (do **not** create a new workflow; keep `tier1-golden-gate` unchanged). Reuse: `windows-latest`, `actions/setup-dotnet@v4` pinned `8.0.419`, `dotnet restore … --locked-mode`, then `dotnet build godot/ProjectChimera.Sim.Analysis/ProjectChimera.Sim.Analysis.csproj -c Release --no-restore` (advisory — no `-warnaserror`, so the job is green on master with warnings printed as annotations).
  - [ ] Make the release enforcement reachable: gate the `-warnaserror`/`/p:ChimeraRelease=true` variant behind a release branch condition or a `workflow_dispatch` input, so the mechanism exists and can be exercised even before a real release branch is cut.
  - [ ] *(Optional, while in the file: bump `actions/*@v4` → `@v5` — GitHub is deprecating the Node 20 runtime. Non-urgent.)*

- [ ] **Task 8 — Guard tests + dependency hygiene (AC6).**
  - [ ] Extend `godot/ProjectChimera.Sim.Tests/Meta/DependencyHygieneTests.cs`: assert `BannedApiAnalyzers` is `PrivateAssets=all`, lives **only** in the analysis project, and is **absent** from `godot.csproj` (which must stay at exactly one `PackageReference`). Add the analysis csproj to whatever set the test parses.
  - [ ] Add a small Tier-1 guard test (in `Meta/`) asserting `BannedSymbols.txt` exists and is referenced as an `AdditionalFile`, and (if you did Task 1) that both projects import `SimSources.props` so the analyzer's source set can't drift from the tested source set. Use the `[CallerFilePath]` path-resolution convention from 1.10a.

- [ ] **Task 9 — Lock file, local proof, and the deliberate-violation gate test (AC4, AC6).**
  - [ ] Generate `godot/ProjectChimera.Sim.Analysis/packages.lock.json` (`dotnet restore` with `RestorePackagesWithLockFile=true`) and commit it. Confirm `dotnet restore … --locked-mode` succeeds (CI uses it).
  - [ ] Local proof: `dotnet build …Sim.Analysis.csproj -c Release` is green with the advisory warnings printed; the Tier-1 suite (`dotnet test …Sim.Tests.csproj -c Release`) still passes with byte-identical goldens.
  - [ ] **Prove the release gate fails:** temporarily add `var _ = new System.Random();` to a sim file, build with the release variant (`/p:ChimeraRelease=true` or `-warnaserror:RS0030`), confirm the build **fails**, then revert. Record this in the Change Log.

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

### Baseline / ratchet (fill during dev)
| Rule ID | Current count in sim set | Release-gated now? | If not, cleared by |
|---|---|---|---|
| RS0030 — `N:Godot` | _(expect 0)_ | _(expect yes)_ | — |
| RS0030 — `System.Random` | _(expect 0)_ | _(expect yes)_ | — |
| RS0030 — `DateTime`/`DateTimeOffset`/`Stopwatch` | _(verify — Agent found 3 DateTime, confirm none in the sim set)_ | _(yes if 0)_ | — |
| RS0030 — `Guid.NewGuid` / `JsonPolymorphicAttribute` | _(expect 0)_ | _(expect yes)_ | — |
| RS0030 — `Fixed.FromFloat`/`ToFloat` (non-converter) | _(≈95 load-time static-const sites)_ | **no (advisory)** | D2 "Fixed end-to-end" migration |
| RS0030 — `float.Parse`/`float.ToString` | _(verify — A17 sites should already be fixed by 1.4)_ | _(yes if 0)_ | — |
| AOT IL2026/IL3050 (reflective STJ) | _(expect >0)_ | **no (advisory)** | D3 JSON source-gen migration |
| AI-layer float conversion sites | _(>0)_ | **no (advisory)** | AI float→Fixed determinism work |

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

1. **Analyzer home — dedicated project vs. fold into the test project.** *Recommended:* a dedicated Godot-free `ProjectChimera.Sim.Analysis` project (clean AOT verdict — no xunit/test-SDK in the closure; analyzes only sim source; doubles as a "sim has zero Godot deps" compile guard). *Alternative:* add `<IsAotCompatible>` + BannedApiAnalyzers to the existing `ProjectChimera.Sim.Tests.csproj` (no new project, but you must editorconfig-mute the IL/RS rules on the test `.cs` files and accept xunit AOT noise). The story assumes the dedicated project.
2. **Custom `BannedSimApiAnalyzer` — now or later?** The architecture's folder tree imagines a hand-written analyzer covering the *true* `float`/`double` primitive ban, Dictionary-enumeration, unstable-sort, and magic-cap-literal detection — things off-the-shelf can't express. This story deliberately **defers** it (those rules have runtime backstops: golden-checksum replay for sort/enum order, `RulesetCorpusTest` for caps, `SecretExclusionTest` for `[Export]`-key). Confirm you're OK shipping 1.10b as "off-the-shelf bans + AOT analyzer (advisory/release cadence)" and tracking the custom analyzer as a follow-up — or say the word and I'll scope the custom analyzer into 1.10b (larger story).
3. **"float gameplay math" interpretation.** Read here as the **conversion boundaries** (`FromFloat`/`ToFloat`/`float.Parse`/`float.ToString`) per the architecture's S-CORE-3 enforcement wording, not "every float declaration." That makes the AC config-satisfiable. If you want a true primitive ban, it pulls in the custom analyzer from #2.

## Dev Agent Record

### Agent Model Used

### Debug Log References

### Completion Notes List

### File List

### Change Log
