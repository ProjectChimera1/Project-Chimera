---
baseline_commit: 607d1665c2308e192acc1c8bc147139e937f5182
---

# Story 1.1: Engine bump 4.6.3 + Godot-free Tier-1 xUnit test project scaffold

Status: review

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a solo developer hardening Project Chimera for multiplayer,
I want the engine bumped to 4.6.3 and a net-new Godot-free xUnit test project (`ProjectChimera.Sim.Tests`) that compiles and runs the existing pure-sim source headless,
so that I have a fast, Godot-independent place to assert determinism before I touch any sim code.

> **This is migration Step 0 — the foundation.** Nothing else in Epic 1 can be checksum-gated until this project exists. Story 1.2 (golden-checksum harness) and every later determinism story compile their tests *into this same project*. Get the scaffold right and the rest of the epic rides on it.

## Acceptance Criteria

1. **(Engine bump)** **Given** the project on Godot 4.6.2 **When** the editor is bumped to 4.6.3 and opened **Then** the project builds and runs, and both the `godot_mcp` and `terrain_3d` addons still connect and report ready **And** no new build errors or addon load failures appear in the editor log.

2. **(Tier-1 project)** **Given** the pure-sim source under `src/Core, Combat, Economy, Navigation` (and `Effects, Dsl` when they exist) **When** `ProjectChimera.Sim.Tests` is created as a Godot-SDK-free .NET 8 xUnit project that references that source (no Godot SDK, no presentation) **Then** the test project compiles and `dotnet test` runs headless with zero reference to Godot types **And** a trivial smoke test (Fixed arithmetic round-trips) passes **And** test-only NuGet deps (xUnit) live only in this project, never in `godot.csproj`.

3. **(Godot-free proof)** **Given** any sim source file **When** the test project is built **Then** no `using Godot` is reachable from the test assembly's sim references — a compile-time proof that the sim layer is Godot-free at the test boundary.

_Covers: FR-44, AR-35 (Tier-1 project foundation), AR-1 (engine bump), AR-2 (Nakama pin / test-only deps). Depends on: — (none / earlier epics only)._

---

## Developer Context

**You (the dev agent) have ONLY this file. Read this whole section before editing anything.** The single hardest part of this story is not "make a test project" — it is making the test project compile the existing sim source **without the Godot SDK**, because three files in the "sim" folders secretly touch Godot. Those are pre-identified below. Miss them and the build fails with unresolvable `Godot.*` types.

### The shape of the work (5 edits + tests)

1. Bump `Godot.NET.Sdk` 4.6.2 → 4.6.3 in `godot.csproj`.
2. Guard `FixedPoint.cs`'s two `Godot.Vector3` bridge methods with `#if GODOT`.
3. Exclude the new test folder from `godot.csproj`'s default compile glob.
4. Create `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` (shared-source, Godot-free).
5. Write the smoke + Godot-free-boundary tests; `dotnet test` green.

### Pre-flight facts you MUST NOT re-derive (already verified against the codebase)

- **The "sim" folders for this project are exactly `Core`, `Combat`, `Economy`, `Navigation`.** `Multiplayer/` is **NOT** sim source for Tier-1 — it is Godot-coupled (LockstepManager, ENetTransport, etc. all `using Godot`) and Nakama-coupled. Do not include it.
- **`Combat`, `Economy`, `Navigation` are 100% Godot-free.** Verified: zero `using Godot`, zero `Godot.*`, zero `[Export]`/`GD.`/`Mathf`. The only two string hits for "Godot" in those folders are doc-comments saying *"Pure C# — no Godot dependency."* Include all of them with a glob.
- **`Core` has exactly three Godot-coupled files:**
  - `src/Core/MainScene.cs` — the 2,223-LOC god object (`using Godot`, `Node3D`). **EXCLUDE.** (It gets strangled in Story 1.8a/c; not your concern now.)
  - `src/Core/StressTest.cs` — `using Godot`, backs `scenes/stress_test.tscn`. **EXCLUDE.** (Relocates to `tools/` in Story 1.8a.)
  - `src/Core/FixedPoint.cs` — uses **fully-qualified `Godot.Vector3`** (not `using Godot`, which is why a naive grep misses it) in two methods at lines 248 & 251. **KEEP THIS FILE** — it is the smoke-test target and the most-depended-on type in the whole sim. Resolve its coupling with `#if GODOT` (see Task 2). Everything else in `Core` (incl. `Core/Definitions/`) is Godot-free.
- **No sim file *calls* `ToGodotVector3` / `FromGodotVector3`.** Only `FixedPoint.cs` defines them and only presentation (`MainScene.cs`) calls them. So wrapping them in `#if GODOT` cannot break any file you include. Verified.
- **Nakama is NOT needed in the test project.** Nakama is referenced only in `src/Multiplayer` (excluded). The epic AC says *"Nakama 3.13.0 pin **if needed**"* — it is not needed here. Do **not** add it unless a real compile error demands it (it won't, if folder scoping is correct).

### THE critical design decision: shared-source compile, NOT a project reference

`ProjectChimera.Sim.Tests` must **glob-`<Compile Include>` the sim `.cs` files directly** into its own assembly. It must **NOT** `<ProjectReference>` `godot.csproj`.

- **Why:** a `ProjectReference` to `godot.csproj` would transitively pull `GodotSharp.dll` into the test assembly — defeating the entire Godot-free guarantee (AC3) — and `godot.csproj` uses `Godot.NET.Sdk`, which a plain `Microsoft.NET.Sdk` test project cannot cleanly consume. The architecture is explicit: this is the *"shared-source home the deferred D5 AOT server compiles from"* — the sim source is compiled, separately, by the game, by this test project, and (post-1.0) by the AOT server. [Source: game-architecture.md §1 Testing / target tree, lines 1287–1294, 1580–1583]
- **Consequence:** sim types (`Fixed`, `EntityWorld`, `SimulationLoop`, …) end up compiled *into the test assembly itself*, not referenced from `ProjectChimera.dll`. That is intended and is what makes the AC3 reflection guard meaningful.
- Use **glob includes** (`..\src\Core\**\*.cs`), never hand-listed file paths — the architecture calls out hand-listed `<Compile Include>` drift as a residual risk; a CI folder-set match check lands later in Story 1.10. [Source: game-architecture.md lines 1681–1683, 1801–1802]

### Second gotcha: `godot.csproj` will try to eat the test files

`Godot.NET.Sdk` (like `Microsoft.NET.Sdk`) auto-globs `**/*.cs` under `godot/`. The new test folder lives at `godot/ProjectChimera.Sim.Tests/`, so its `.cs` files (which reference xUnit) would be **double-compiled into the game assembly** and break the game build. You must add an explicit `<Compile Remove="ProjectChimera.Sim.Tests\**\*.cs" />` to `godot.csproj` (Task 3). This is the easiest step to forget and produces confusing xUnit-in-game-build errors if missed.

---

## Tasks / Subtasks

- [x] **Task 1 — Bump engine to Godot 4.6.3 (AC: 1)**
  - [x] In `godot/godot.csproj` line 1, change `<Project Sdk="Godot.NET.Sdk/4.6.2">` → `Godot.NET.Sdk/4.6.3`.
  - [x] Run `dotnet restore godot/godot.csproj` and confirm `Godot.NET.Sdk 4.6.3` resolves from NuGet (it was released 2026-05-20; if restore fails, STOP and report — do not pin a non-existent version).
  - [x] Leave `project.godot` `config/features=PackedStringArray("4.6", ...)` **unchanged** — "4.6" is the minor line; a patch bump (.3) does not change it.
  - [x] **[Human-in-the-loop — Alec]** Install the Godot 4.6.3 **.NET** editor build and open the project. The dev agent cannot download/install the editor binary; flag this and pause AC1 editor verification until 4.6.3 is open.
  - [x] Once 4.6.3 is open, verify via `godot_mcp`: project builds & runs, `godot_mcp` + `terrain_3d` plugins are enabled and ready (`[editor_plugins] enabled=...` both load), and the editor log shows **no new** build errors or addon-load failures.

- [x] **Task 2 — Make `FixedPoint.cs` compile Godot-free (AC: 2, 3)**
  - [x] In `godot/src/Core/FixedPoint.cs`, wrap the two bridge methods (`ToGodotVector3` at ~line 248 and `FromGodotVector3` at ~line 251, under the `// --- Conversions to Godot types (presentation layer only) ---` comment) in `#if GODOT` / `#endif`.
  - [x] Rationale: `Godot.NET.Sdk` defines the `GODOT` preprocessor symbol, so the methods remain present in the game build; the Godot-free test project does not define `GODOT`, so they compile out — zero `Godot.Vector3` reference at the test boundary. This is a **zero-behavior-change, zero-call-site-change** edit (no sim file calls these; `MainScene.cs` still compiles under the game build where `GODOT` is defined).
  - [x] Do **not** move/delete these methods or convert them to extension methods — that would churn presentation call sites for no gain.

- [x] **Task 3 — Keep the test folder out of the game's compile (AC: 1)**
  - [x] In `godot/godot.csproj`, add an `<ItemGroup>` with `<Compile Remove="ProjectChimera.Sim.Tests\**\*.cs" />`.
  - [x] Verify `dotnet build godot/godot.csproj` still succeeds and the game assembly does not reference xUnit.

- [x] **Task 4 — Create the Godot-free Tier-1 project (AC: 2, 3)**
  - [x] Create folder `godot/ProjectChimera.Sim.Tests/` and file `ProjectChimera.Sim.Tests.csproj` (content below).
  - [x] SDK = `Microsoft.NET.Sdk` (NOT `Godot.NET.Sdk`); `TargetFramework=net8.0`; `IsPackable=false`.
  - [x] **Do not set `<Nullable>enable</Nullable>` project-wide.** `godot.csproj` leaves nullable at default(disabled) and sim files opt-in per-file via `#nullable enable`. Matching that keeps the shared sim source compiling under the *same* nullable context in both projects (no divergent warnings). Add `#nullable enable` to your own test files if you want it locally.
  - [x] Glob-include the four sim folders; `<Compile Remove>` `MainScene.cs` and `StressTest.cs`.
  - [x] Add test-only packages (xUnit stack). Do **not** add Nakama.
  - [x] Confirm there is **no** `<ProjectReference>` to `godot.csproj`.

- [x] **Task 5 — Smoke + Godot-free-boundary tests (AC: 2, 3)**
  - [x] `Determinism/FixedSmokeTests.cs` — Fixed arithmetic round-trips (int round-trip, multiply, divide→Half, raw round-trip, Sqrt(16)=4, FixedVec3 add).
  - [x] `GodotFreeBoundaryTest.cs` — reflect over `typeof(Fixed).Assembly` (the test assembly, via shared source) and assert its referenced assemblies contain neither `GodotSharp` nor `GodotSharpEditor`. This turns AC3 into an asserted test, not just "it compiled".

- [x] **Task 6 — Verify end-to-end**
  - [x] `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` → green, runs headless in seconds, no engine boot.
  - [x] `dotnet build godot/godot.csproj` → green (game build unaffected by the new project and the `#if GODOT` guard).
  - [x] AC1 editor/addon verification via `godot_mcp` (after Alec's 4.6.3 install).

---

## Dev Notes

### Reference artifacts to produce (copy/adapt — these are correct against the current code)

**`godot/godot.csproj`** (after Tasks 1 & 3):

```xml
<Project Sdk="Godot.NET.Sdk/4.6.3">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <TargetFramework Condition=" '$(GodotTargetPlatform)' == 'android' ">net9.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <AssemblyName>ProjectChimera</AssemblyName>
    <RootNamespace>ProjectChimera</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <!-- Nakama C# SDK — matchmaking, authentication, lobby -->
    <PackageReference Include="NakamaClient" Version="3.13.0" />
  </ItemGroup>
  <!-- Tier-1 test project lives under godot/ but is a separate Godot-free assembly;
       keep its files out of the game's default **/*.cs compile glob. -->
  <ItemGroup>
    <Compile Remove="ProjectChimera.Sim.Tests\**\*.cs" />
  </ItemGroup>
</Project>
```

**`godot/src/Core/FixedPoint.cs`** (Task 2 — the only change to this file):

```csharp
        // --- Conversions to Godot types (presentation layer only) ---
#if GODOT
        /// <summary>Convert to Godot Vector3 for rendering. Only use in presentation layer.</summary>
        public Godot.Vector3 ToGodotVector3() =>
            new Godot.Vector3(X.ToFloat(), Y.ToFloat(), Z.ToFloat());

        public static FixedVec3 FromGodotVector3(Godot.Vector3 v) =>
            new FixedVec3(Fixed.FromFloat(v.X), Fixed.FromFloat(v.Y), Fixed.FromFloat(v.Z));
#endif
```

**`godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj`** (Task 4):

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <!-- Godot-free: intentionally NOT Godot.NET.Sdk and the GODOT symbol is NOT defined here,
         so FixedPoint.cs's #if GODOT bridge methods compile out. -->
  </PropertyGroup>

  <!-- SHARED SOURCE: compile the pure-sim folders directly. NOT a <ProjectReference> to godot.csproj
       (that would drag GodotSharp into this assembly and break the Godot-free guarantee). -->
  <ItemGroup>
    <Compile Include="..\src\Core\**\*.cs"       LinkBase="Sim\Core" />
    <Compile Include="..\src\Combat\**\*.cs"     LinkBase="Sim\Combat" />
    <Compile Include="..\src\Economy\**\*.cs"    LinkBase="Sim\Economy" />
    <Compile Include="..\src\Navigation\**\*.cs" LinkBase="Sim\Navigation" />
    <!-- Effects/ and Dsl/ are net-new in Epics 2 & 7; those stories add their includes when the folders exist. -->

    <!-- The only Godot-coupled files in the sim folders. MainScene = god object (strangled in 1.8a/c);
         StressTest relocates to tools/ in 1.8a. FixedPoint.cs STAYS IN (Godot bridge guarded by #if GODOT). -->
    <Compile Remove="..\src\Core\MainScene.cs" />
    <Compile Remove="..\src\Core\StressTest.cs" />
  </ItemGroup>

  <ItemGroup>
    <!-- Test-only. Pin the exact versions `dotnet restore` resolves so CI (Story 1.10a) is reproducible.
         Known-good baseline; bump to current stable if newer. xUnit 2.x is the conservative choice. -->
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>

</Project>
```

**`godot/ProjectChimera.Sim.Tests/Determinism/FixedSmokeTests.cs`** (Task 5):

```csharp
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Determinism;

public class FixedSmokeTests
{
    [Fact]
    public void IntRoundTrip()
    {
        Assert.Equal(7, Fixed.FromInt(7).ToInt());
        Assert.Equal(-3, Fixed.FromInt(-3).ToInt());
    }

    [Fact]
    public void MultiplyAndDivide()
    {
        Assert.Equal(12, (Fixed.FromInt(3) * Fixed.FromInt(4)).ToInt());
        Assert.Equal(Fixed.Half, Fixed.One / Fixed.FromInt(2)); // 1 / 2 == Half
    }

    [Fact]
    public void RawRoundTrip()
    {
        Assert.Equal(Fixed.ONE, Fixed.FromRaw(Fixed.One.Raw).Raw);
    }

    [Fact]
    public void SqrtIsExactForPerfectSquares()
    {
        Assert.Equal(4, Fixed.Sqrt(Fixed.FromInt(16)).ToInt());
    }

    [Fact]
    public void Vec3Add()
    {
        var v = FixedVec3.One + FixedVec3.One;
        Assert.Equal(new FixedVec3(Fixed.FromInt(2), Fixed.FromInt(2), Fixed.FromInt(2)), v);
    }
}
```

**`godot/ProjectChimera.Sim.Tests/GodotFreeBoundaryTest.cs`** (Task 5 — makes AC3 an asserted test):

```csharp
using System.Linq;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests;

public class GodotFreeBoundaryTest
{
    // Fixed is compiled INTO this test assembly via shared source, so its assembly
    // is the test assembly. If any included sim file leaked `using Godot` or a
    // ProjectReference dragged GodotSharp in, this assembly would reference it.
    [Fact]
    public void SimAssembly_DoesNotReference_GodotSharp()
    {
        var refs = typeof(Fixed).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        Assert.DoesNotContain("GodotSharp", refs);
        Assert.DoesNotContain("GodotSharpEditor", refs);
    }
}
```

### Constraints & gotchas

- **`dotnet build` is authoritative** for catching C# errors — the editor's MCP `run` does not rebuild; it uses whatever DLL is on disk. Build before declaring done. [Source: LEARNINGS.md:122]
- **`FixedVec3.ToGodotVector3()` is a known, sanctioned exception** to the "no Godot in sim" rule — the `#if GODOT` guard is how it stays sanctioned *and* Godot-free-compilable. [Source: LEARNINGS.md:40; project-context.md "The One Architectural Rule"]
- **Do not register the test project in `godot.sln`.** Keep Godot's build path clean — run tests with `dotnet test <csproj path>`. (A root solution for CI can come with Story 1.10a; not now.)
- **Determinism rules still apply to any code you touch**: no `float`/`double`/`Mathf` for gameplay values, no `System.Random`, no `Dictionary`/`HashSet` enumeration in sim. You are not writing sim logic here, but your smoke tests must use `Fixed`, not float asserts. [Source: project-context.md "Determinism"]
- **Solo-dev scope discipline:** this story is *scaffold only*. Do not write the golden-checksum harness (that is Story 1.2), the `BannedSimApiAnalyzer` (Story 1.10b), `ILogSink` (Story 1.8a), or any negative/validation tests. Just: bump, scaffold, smoke, prove Godot-free.

### Project Structure Notes

- Canonical target tree places the project at **`godot/ProjectChimera.Sim.Tests/`** (sibling of `scenes/`, `src/`), glob-including the pure-sim folders. [Source: game-architecture.md lines 1580–1583]
- Tier-2 GdUnit4 tests live separately at `godot/tests/` (presentation/integration only) and are **out of scope** for this story. Don't create them here.
- Suggested internal layout for this project (folders filled by later stories): `Determinism/`, `Golden/` (1.2), `Validation/` (1.7), `Builder/` (1.8b), `Checksum/` (1.3b), `Bootstrap/` (1.8c). For 1.1 you create only `Determinism/` + the root boundary test.
- **Variance from `godot/CLAUDE.md` (intentional):** that L2 router says "Use GdUnit4 for unit tests in `tests/`". The forward architecture supersedes it with the **two-tier** model — Tier-1 xUnit (this project, determinism/sim) + Tier-2 GdUnit4 (`godot/tests/`, presentation). FR-44's literal "GdUnit4 in `godot/tests/`" predates the Godot-free/AOT insight. Follow the architecture. [Source: game-architecture.md §1, lines 1295–1300]

### Project Context Rules

_Extracted from `_bmad-output/project-context.md` — these govern every edit in this story:_

- **Simulation/Presentation boundary is sacred.** Sim = `src/Core, Combat, Economy, Navigation` — pure C#, no `using Godot;`, no Godot Node types, no `float` for gameplay state. This story's whole purpose is to *prove and lock* that boundary at a test seam.
- **Engine target for 1.0 is Godot 4.6.3** (as-built 4.6.2; 4.7 deferred post-1.0). Runtime is **.NET 8** (`net8.0`; `net9.0` only on the android condition).
- **Assembly/namespace:** `AssemblyName=ProjectChimera`, namespaces `ProjectChimera.<System>`. The on-disk project/solution files are `godot.csproj` / `godot.sln` (the `ProjectChimera.sln` name in `godot/CLAUDE.md` is stale).
- **NuGet discipline:** only `NakamaClient 3.13.0` ships in the game. Avoid adding dependencies. Test-only deps (xUnit) belong **only** in `ProjectChimera.Sim.Tests.csproj`, never in `godot.csproj`.
- **Conventions:** `PascalCase.cs` filename matches class name; `#nullable enable` per file; comment public methods and non-obvious logic; C# source under `godot/src/<System>/`.
- **Godot C# gotcha (only if you touch presentation):** classes inheriting Godot types must be `partial`; use `GD.Print()` not `Console.WriteLine()` (presentation/editor only — sim prints nothing).

### References

- [Source: epics.md#Story-1.1 (lines 494–510)] — story statement, ACs, FR/AR coverage, "migration Step 0" framing.
- [Source: epics.md#Epic-1 (lines 486–492)] — sequencing note (golden-checksum-gated strangler; 1.1/1.5/1.7/1.8 genuinely net-new), coverage note.
- [Source: game-architecture.md §1 Testing architecture (lines 1284–1300)] — two-tier model; Tier-1 = Godot-free `ProjectChimera.Sim.Tests`; *recommend updating FR-44 wording*.
- [Source: game-architecture.md target directory tree (lines 1575–1609)] — project location `godot/ProjectChimera.Sim.Tests/`, glob-include of pure-sim folders, MainScene/StressTest/FixedPoint notes.
- [Source: game-architecture.md Step-0 (lines 1690–1693)] — "create `ProjectChimera.Sim.Tests.csproj` (net8.0, no Godot)… no behavior change. Ship."
- [Source: game-architecture.md AOT split deferral (lines 100–108) & residual risks (1801–1802)] — glob-include the Godot-free shared-source set; shared-source `<Compile Include>` drift risk.
- [Source: game-architecture.md engine + test-runner decisions (lines 120, 129, 265–267)] — 4.6.3 patch-on-4.6 rationale; Tier-1 xUnit / Tier-2 GdUnit4.
- [Source: godot/godot.csproj:1] — current `Godot.NET.Sdk/4.6.2` to bump.
- [Source: godot/src/Core/FixedPoint.cs:245–252] — the two `Godot.Vector3` bridge methods to guard.
- [Source: godot/src/Core/MainScene.cs, godot/src/Core/StressTest.cs] — the two `using Godot` files in `Core` to exclude.
- [Source: godot/project.godot:15,28] — `config/features` "4.6" (leave as-is); enabled plugins `godot_mcp` + `terrain_3d` (verify ready for AC1).
- [Source: project-context.md] — determinism + sim/presentation rules, engine target, NuGet discipline (see Project Context Rules above).
- [Source: LEARNINGS.md:40,122] — FixedVec3 bridge exception; `dotnet build` authoritative. _(LEARNINGS.md is deprecated/auto-injected; content canonical at vault `20_Reference/GameDev/godot-csharp/LEARNINGS.md`.)_

### Latest tech information

- **`Godot.NET.Sdk/4.6.3`** — released 2026-05-20; available on NuGet. Patch on the 4.6 line (low regression/addon risk vs 4.7, which is deliberately deferred). Confirm restore before proceeding.
- **xUnit** — use the **2.x** line (e.g. `xunit` 2.9.2, `Microsoft.NET.Test.Sdk` 17.11.1, `xunit.runner.visualstudio` 2.8.2) for a determinism harness: maximal tooling/CI compatibility and stable `[Fact]`/`Assert` surface. If an xUnit v3 is in use project-wide later, this project can migrate then; for the foundation, prefer the conservative 2.x. **Pin the exact resolved versions** so Story 1.10a's CI is reproducible.
- **No new sim dependencies.** The test project needs only the xUnit stack; the sim source compiles against the BCL (`System.*`) alone.

### Previous Story Intelligence

None — this is the first story of Epic 1 (and the project's first automated test of any kind: "zero tests today"). No prior story file or established test pattern to inherit. Git history is uninformative ("[AutoSave]" commits only; working tree clean on `master`). You are establishing the pattern that Stories 1.2–1.13 will follow — make the csproj and folder layout clean.

---

## Dev Agent Record

### Agent Model Used

Claude Opus 4.8 (claude-opus-4-8)

### Debug Log References

- `dotnet restore godot/godot.csproj` → exit 0, "All projects are up-to-date" — `Godot.NET.Sdk/4.6.3` resolves from NuGet.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` → **Passed! Failed: 0, Passed: 6, Skipped: 0, Total: 6, Duration 133 ms** (headless, no engine boot).
- `dotnet build godot/godot.csproj` → **Build succeeded. 0 Error(s), 6 Warning(s)** — the 6 warnings are pre-existing CS8632 nullable annotations, identical in both the game and test builds (no divergence introduced).
- godot_mcp (editor on 4.6.3): `get_info` → `4.6.3-stable (official)`; `addon_status` → godot_mcp connected, `versions_match: true`; editor log → empty (0 errors / 0 warnings); `run` → `is_playing: true`, `/root/MainScene` live with Terrain3D + NavigationRegion3D + HUD; `stop`.

### Completion Notes List

- **AC1 ✅** Engine on **Godot 4.6.3-stable**. `godot.csproj` SDK already at 4.6.3 (prior edit/autosave); `dotnet restore` confirms it resolves. `project.godot` `config/features` left as `"4.6"` (a patch bump doesn't change the minor line). In-editor verification on Alec's already-installed 4.6.3 .NET build: project **builds & runs**; **godot_mcp** ready (it answered MCP calls); **terrain_3d** ready — confirmed by a live `Terrain3D` node instantiated at runtime under `/root/MainScene`; editor log clean (no build errors / no addon-load failures).
- **AC2 ✅** Created Godot-free Tier-1 `ProjectChimera.Sim.Tests` (`Microsoft.NET.Sdk`, `net8.0`) compiling the four pure-sim folders (Core, Combat, Economy, Navigation) via **shared-source globs** — NOT a `<ProjectReference>` to `godot.csproj`, no Godot SDK, no Nakama. `dotnet test` runs headless in ~0.1 s; the Fixed-arithmetic smoke test passes; the xUnit stack lives only in this csproj.
- **AC3 ✅** `GodotFreeBoundaryTest` reflects over `typeof(Fixed).Assembly` (= the test assembly, via shared source) and asserts neither `GodotSharp` nor `GodotSharpEditor` is referenced — passes. `FixedPoint.cs`'s two Godot `Vector3` bridge methods are guarded with `#if GODOT` (compiled out in this project; retained in the game build, where `Godot.NET.Sdk` defines `GODOT`). Zero call-site churn (no sim file calls them; only `MainScene.cs` does).
- **Pre-flight verified against the codebase:** Combat/Economy/Navigation are 100% Godot-free; Core's only Godot-coupled files are `MainScene.cs` + `StressTest.cs` (both excluded) and `FixedPoint.cs` (guarded).
- **Scope held to scaffold-only:** no golden-checksum harness (Story 1.2), no banned-API analyzer (1.10b), no validation tests (1.7). Established the csproj + folder pattern (only `Determinism/` + the root boundary test) for Stories 1.2+ to follow.

### File List

- `godot/godot.csproj` — modified (Task 3: added `<Compile Remove="ProjectChimera.Sim.Tests\**\*.cs" />`; SDK was already at 4.6.3).
- `godot/src/Core/FixedPoint.cs` — modified (Task 2: `#if GODOT` guard around `ToGodotVector3` / `FromGodotVector3`).
- `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` — new (Task 4).
- `godot/ProjectChimera.Sim.Tests/Determinism/FixedSmokeTests.cs` — new (Task 5).
- `godot/ProjectChimera.Sim.Tests/GodotFreeBoundaryTest.cs` — new (Task 5).

### Change Log

- **2026-06-22** — Story 1.1 implemented. Confirmed engine on Godot 4.6.3; guarded `FixedPoint.cs` Godot bridge with `#if GODOT`; excluded the Tier-1 test folder from the game compile; created the Godot-free `ProjectChimera.Sim.Tests` (net8.0, shared-source) with Fixed smoke tests + a Godot-free-boundary test. Verified: `dotnet test` 6/6 green, `dotnet build` game green, AC1 in-editor on 4.6.3 (builds/runs, both addons ready, clean log). Status: ready-for-dev → in-progress → review.
