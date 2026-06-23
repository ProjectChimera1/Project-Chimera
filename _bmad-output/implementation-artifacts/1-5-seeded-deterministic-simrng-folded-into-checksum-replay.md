---
baseline_commit: a90c786
---

# Story 1.5: Seeded deterministic SimRng folded into checksum + replay

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a solo developer enabling random effects without breaking determinism,
I want a net-new Godot-free `SimRng` (deterministic, integer/`Fixed`-only, single seeded stream), held as one shared instance on the sim-state object, with its state folded into `SimChecksum` and its seed recorded/restored by `ReplayRecorder`/`ReplayPlayer`,
so that randomness is reproducible across machines and replays, and no non-deterministic `Random` can ever enter the sim.

> **This is Migration Step 2 of the determinism strangler (`game-architecture.md` §D1 / N1).** Step 1 (the golden-checksum harness, Story 1.2) and the `Fixed`-end-to-end quantization boundary (Story 1.4) are DONE. This story builds the **net-new `SimRng`** (none exists today — the sim layer is verified RNG-free), folds its state into the existing `SimChecksum` (bumping `AlgoVersion` 2→3, which **intentionally re-baselines both goldens** — the opposite of 1.4's byte-identical rule), and threads its seed through the `.chmr` replay system so a replay regenerates the identical RNG stream. It is a **hard prerequisite** for every later random effect/Modifier pattern (Epic 2) and for the validator's "forbidden-until-SimRng" rule — **which is owned by Story 1.7, NOT this story.** This story builds the generator and wires it into the two determinism tripwires (checksum + replay); it adds **no random gameplay behavior** and changes **no system's logic**.

## Acceptance Criteria

1. **(Seeded, bit-identical, integer-only)** **Given** a `SimRng` seeded with a fixed seed **When** the same sequence of `NextRaw`/`NextInt`/`NextFixed` draws is requested in two separate instances (or after `Seed(...)` restore) **Then** the outputs are bit-identical and use **only** integer/`Fixed` math — no `System.Random`, no Godot RNG, no `float`/`double` anywhere in the type. A unit test derives at least one expected value **independently** from the algorithm definition (not by re-running the method — the "a tautological assert proves nothing" rule from the 1.1 review).

2. **(Folded into SimChecksum + recorded/restored by replay)** **Given** the single shared `SimRng` instance **When** a checksum is computed **Then** its state is folded into `SimChecksum.Compute` (so `AlgoVersion` bumps 2→3 and both goldens are re-baselined in the same commit), **and** the match seed is written to the `ReplayRecorder` header and read back by `ReplayPlayer` so a replay restores the exact stream start. A divergent RNG stream between two runs/peers changes the checksum (desync is detectable).

3. **(RNG-driven behavior reproduces across runs and a replay)** **Given** a scenario that draws from the sim `SimRng` during ticks and writes the result into hashed state **When** the golden harness runs it twice with the same seed **and** the run is recorded to a `.chmr` and played back **Then** all three per-tick `SimChecksum` sequences are byte-identical. A test reseeds to a known seed so the two runs share the stream, and a negative control (different seed, or RNG state removed from the hash) demonstrably diverges.

_Covers: FR-39 (deterministic lockstep), FR-44 (deterministic-sim + headless test coverage), FR-47 (regression-guarded determinism), AR-13 (SimRng is the only sim randomness, folded into checksum/replay), AR-15 (canonical draw order). Depends on: 1.4 (DONE — `Fixed` end-to-end + the converter boundary)._

> Net-new (`SimRng` does not exist; the sim layer Core/Combat/Economy/Navigation is RNG-free — only `StressTest` and two UI files use Godot RNG, all outside the sim/test boundary). Re-baselines both goldens once RNG state enters the hash. The validator's forbidden-until-SimRng gate is **Story 1.7**.

---

## Developer Context

**You (the dev agent) have ONLY this file. Read this whole section before editing anything.** The work is one net-new class plus three small integration edits and a re-baseline. The traps are: (1) **making `SimRng` a `struct`** — it MUST be a reference type or the shared-stream semantics silently break; (2) **letting a `float`/`double` touch the generator** — it must be pure integer math; (3) **forgetting the re-baseline** — unlike Story 1.4, the goldens WILL move here and that is correct and required; (4) **over-engineering replay** — record the *seed only*, not per-tick RNG state; (5) **adding random gameplay** — no system draws in this story, the plumbing is for Epic 2.

### The shape of the work (1 net-new class + 1 EntityWorld field + 1 SimChecksum fold + replay seed in/out + re-baseline both goldens + 2 new test files)

1. **Net-new `SimRng`** in `godot/src/Core/SimRng.cs` — a sealed class, integer-only (SplitMix64 recommended), API `NextRaw()`/`NextInt(count)`/`NextFixed()` + `State`/`Seed(...)`. Godot-free.
2. **Hold one shared instance on `EntityWorld`** — `public SimRng Rng { get; } = new SimRng(...)`. Reached by `SimChecksum.Compute(world, …)` and by every system (via the `world` they already receive) with **zero signature changes**.
3. **Fold `world.Rng.State` into `SimChecksum.Compute`** (after the faction loop, before `return`), bump `AlgoVersion` 2→3, update the doc comment.
4. **Replay seed** — `ReplayRecorder` writes the 8-byte seed into the header (format `VERSION` 1→2); `ReplayPlayer` reads it (version-gated) and calls `world.Rng.Seed(seed)` before the first tick.
5. **Re-baseline both goldens** (`golden-scenario`, `golden-multifaction`) + re-pin the `SimChecksumCoverageGuardTest` known-state hash to v3 — in the **same commit** as the `AlgoVersion` bump.
6. **Tests** — `Determinism/SimRngTests.cs` (AC1) and `Golden/SimRngChecksumReplayTests.cs` (AC2/AC3).

### Key design decisions (settled here — do NOT re-derive)

**D1 — `SimRng` is a sealed reference type (class), NOT a struct.** The architecture mandates "a single shared mutable instance referenced (never copied) by callers, so a draw in one place advances the stream everyone sees" (`game-architecture.md`:1898) and stores it as a reference field `public readonly SimRng Rng` inside a `readonly struct` (`:2001`) — which only works if `SimRng` is a reference type. The epic's phrase **"threaded through systems by ref" means by *reference* (shared instance), not the C# `ref` keyword on a struct.** A `struct`/`ref struct` copied into `Tick`/`Compute`/an `EffectContext` field would lose its draw advances and desync silently. **Make it a `class`.**

**D2 — It lives as one field on `EntityWorld` (`world.Rng`), reached through the shared `world` — so nothing's signature changes.** The architecture explicitly defers "where the seed/state lives" to implementation and recommends "a small `SimState` field exposed to `SimChecksum`" (`:2522`). `SimChecksum.Compute(EntityWorld world, …)` and `ISimSystem.Tick(EntityWorld world, …)` **both already receive `world`** — so a single `Rng` reference on `EntityWorld` satisfies both "exposed to SimChecksum" and "threaded through systems" with **no change to `Compute`, `ISimSystem`, the 9 systems, or the loop call sites.** This is the lowest-churn, lowest-risk home and keeps the tick byte-identical except for the intended checksum fold. _(Rejected alternative: a field on `SimulationLoop` threaded as a new `Tick`/`Compute` parameter — that churns the interface + all 9 systems + `Compute` + every call/test site for zero determinism benefit, since no system draws in this story.)_ `Rng` is a per-world **single reference** (like the readonly SoA arrays: fixed reference, mutable contents) — **NOT a per-entity array.**

**D3 — Algorithm: SplitMix64 (recommended), integer-only, single `ulong` state.** Per `:2522` the exact algorithm is an implementation leaf choice provided it is (a) integer-only — zero `float`/`double`; (b) deterministic across platforms; (c) fully state-exposed for checksum/replay. **SplitMix64** is the recommended pick: ~5 integer ops, one `ulong` of state, full 2⁶⁴ period, **no zero-state trap** (tolerates seed `0`, unlike raw xorshift). A complete reference implementation is in Dev Notes — copy it; do not hand-roll constants. (`pcg32` is an acceptable substitute if you prefer; same requirements apply.)

**D4 — API surface (`:2522`): `NextRaw()` / `NextInt(int countExclusive)` / `NextFixed()` + `State` / `Seed(...)`.**
- `ulong NextRaw()` — advances state, returns 64 raw bits (the one primitive draw).
- `int NextInt(int countExclusive)` — non-negative int in `[0, countExclusive)`. Modulo bias is **acceptable** (bias is identical on every peer → does not break determinism); throw on `countExclusive <= 0`.
- `Fixed NextFixed()` — `Fixed` in `[0, 1)` built from the **top 16 bits** of a draw as the 16 fractional bits: `Fixed.FromRaw((int)(NextRaw() >> 48))`. Integer-only — `FixedPoint.cs` has **no** `[0,1)` helper (confirmed), so `SimRng` owns this.
- `ulong State { get; }` (for the checksum fold) and `void Seed(ulong seed)` (match start / replay restore — resets state in place; the `Rng` reference stays fixed).

**D5 — Fold into `SimChecksum`; bump `AlgoVersion` 2→3; RE-BASELINE both goldens (intended).** Mix `world.Rng.State` as two `int`s (low/high 32 bits) after the per-faction loop. **`SimChecksum.Mix(uint, int)` is the existing primitive — reuse it; do not add a new hash path.** Because the goldens draw nothing, `State` stays at the seed every tick → folding it shifts **every** golden checksum by a constant → both goldens (and the coverage-guard pinned hash) move. **This is required, not a regression** — the opposite of Story 1.4's byte-identical rule. Re-baseline in the SAME commit as the version bump (the version stamp self-identifies the baseline).

**D6 — Replay records the SEED ONLY (8 bytes in the header), restored at playback start.** A lockstep replay re-runs identical recorded inputs through the deterministic sim, so the stream regenerates from the seed alone — per-tick RNG state is **redundant** and must NOT be recorded (it would bloat every `.chmr` and invite drift). Concretely: `ReplayRecorder` header `VERSION` 1→2, write the `ulong` seed after the scenario path; `ReplayPlayer` reads it (only when `version >= 2`) and calls `world.Rng.Seed(seed)` **before the first `StepOnce`**. The per-tick checksum (now including RNG state) is the proof the stream stayed in sync. v1 `.chmr` files (no seed) → default seed; document the back-compat.

**D7 — No system draws; no random gameplay; tick logic unchanged.** Random *effects*, the Effect graph, `ModifierSystem`, and any system actually calling `world.Rng` are **Epic 2** (`game-architecture.md` D1 steps 5–9). This story builds the generator and wires the two tripwires only. The AC3 "RNG-driven behavior" is produced by the **test** (a `perturb` callback that draws from `world.Rng` and mutates a hashed array), not by sim code.

**D8 — The "forbidden-until-SimRng" validator rule is Story 1.7, not here.** `epics.md`:630 explicitly relocated it: "the forbidden-until-SimRng rule (AR-13); relocated here from 1.5 so the rule is owned by the story that builds the validator." Do NOT build `ScenarioValidator`, `Validated<T>`, or any "reject random effect" gate in this story.

### Pre-flight facts you MUST NOT re-derive (verified against the codebase at `a90c786`)

- **`SimChecksum`** (`godot/src/Core/SimChecksum.cs`): `public const int AlgoVersion = 2;` (`:35`, with a version-history doc block `:27-34` — append a `v3` line). `Compute(EntityWorld world, BuildingStore buildings, ResourceStore resources, FactionRegistry factions)` (`:41-42`). The fold insertion point is **after the `foreach (Faction f in factions.ActiveFactions)` loop closes (`:87`), before `return hash;` (`:89`)**. `Mix(uint hash, int value)` is at `:95-103` (FNV-1a, feeds 4 LE bytes). `int[]` values are passed directly; `Fixed` as `.Raw`; a `ulong` must be split into two `int` mixes. The class doc (`:9-19`) lists the hashed set — add the RNG line. [Source: SimChecksum.cs]
- **`SimulationLoop`** (`godot/src/Core/SimulationLoop.cs`): `ISimSystem.Tick(EntityWorld world, Fixed dt)` (`:8-12`). Ctor `SimulationLoop(EntityWorld world, params ISimSystem[] systems)` (`:63`) sets `World` (`:44`). `EnableChecksums(BuildingStore, ResourceStore, FactionRegistry)` (`:75`). `SimChecksum.Compute(...)` is called at **`:100` (`StepOnce`) and `:137` (`Update`)** — **both unchanged** under D2 (they pass `World`, which now carries `Rng`). The 9-system tick loop is `:91-92` / `:124-127` — **unchanged** (no system draws). [Source: SimulationLoop.cs]
- **`EntityWorld`** (`godot/src/Core/EntityWorld.cs`): `public class EntityWorld` (`:60`), `MAX_ENTITIES = 4096` (`:62`), readonly SoA arrays `:65-95+` allocated in the ctor. Add `public SimRng Rng { get; }` here and `= new SimRng(seed)` in the ctor (or a field initializer). **`new EntityWorld()` is called widely (scenarios, tests)** — give `Rng` a default seed so the parameterless ctor keeps working; reseed via `world.Rng.Seed(matchSeed)` from the bootstrap/replay. (Aside: `EntityWorld.cs:206 VisionRange = Fixed.FromFloat(8f)` is a pre-existing load-time `FromFloat` — **not your concern**, fenced to its own story.) [Source: EntityWorld.cs; game-architecture.md:1875-1876]
- **`Fixed`** (`godot/src/Core/FixedPoint.cs`): `Raw` is `int`, 16.16 (`FRACTIONAL_BITS = 16`, `ONE = 65536`). `FromInt`/`FromRaw`/`FromFloat`, `ToInt()` (`Raw >> 16`), constants `Zero`/`One`/`Half`, full operator set incl. `%`. **`FromFloat` is load-time only — must never appear in `SimRng`.** No `[0,1)` mapping helper exists. [Source: FixedPoint.cs]
- **`ReplayRecorder`** (`godot/src/Multiplayer/ReplayRecorder.cs`): `namespace ProjectChimera.Multiplayer`, `public sealed class ReplayRecorder : IDisposable`. `MAGIC = 0x524D4843u` ('CHMR'), `VERSION = 1` (`:24-25`), `EOF_SENTINEL = 0xFFFFFFFFu` (`:28`). Header = `magic(4) + version(2) + scenarioPathLen(2) + scenarioPath(UTF8)` written by `WriteHeader(...)` (`:106-114`); `RecordTick(uint tick, Faction faction, UnitOrder[] buf, int baseIdx, int count)` (`:66`); `Close()` (`:92`). **Godot-free.** [Source: ReplayRecorder.cs]
- **`ReplayPlayer`** (`godot/src/Multiplayer/ReplayPlayer.cs`): parses the header (magic/version/pathLen/path) at **`:73-84`** — add a version-gated `ReadUInt64()` seed read after the path; `Flush(...)` applies per-tick orders at `:126-138`. Expose the parsed seed so the playback bootstrap reseeds `world.Rng` before stepping. [Source: ReplayPlayer.cs — confirm exact anchors before editing]
- **Tier-1 test project** (`godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj`): net8.0, **shared-source** compile of `..\src\{Core,Combat,Economy,Navigation,AI}\**\*.cs` (`:14-21`), `MainScene.cs` + `StressTest.cs` removed (`:26-27`). **`src/Multiplayer` is NOT globbed** — so `ReplayRecorder`/`ReplayPlayer` are not in the test assembly today (see the replay-test note in Tasks). xUnit 2.9.2 / `Microsoft.NET.Test.Sdk` 17.11.1 (`:44-49`). Goldens embedded: `Golden/golden-scenario.golden.txt` + `Golden/golden-multifaction.golden.txt` (`:34-38`). New `.cs` files under `src/Core` auto-glob in — no `.csproj` edit for `SimRng.cs` or the test files. [Source: ProjectChimera.Sim.Tests.csproj]
- **`GoldenChecksumReplay` harness** (`godot/ProjectChimera.Sim.Tests/Golden/GoldenChecksumReplay.cs`): `public static class`. `RunAndRecord(int ticks, Action<int, EntityWorld>? perturb = null, …)` builds a fresh sim, steps N ticks, returns `IReadOnlyList<Sample>` (`:51`); `Sample(uint Tick, uint Hash)` (`:34`); `LoadGolden(fileName)` (`:106`); `ParseGolden(byte[])` (`:128`); `MaybeRecord(seq, fileName, header)` writes the source golden in record mode (`:198`); `GoldenHeader(Title, ScenarioDescription, RebaselineHint)` (`:160`); `IsRecordMode` via `CHIMERA_GOLDEN_RECORD` (`:40`). Re-baseline hint (`:167`): **"set `CHIMERA_GOLDEN_RECORD=1`, run `dotnet test --filter FullyQualifiedName~GoldenChecksumReplay`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit."** Confirm the third `RunAndRecord` param (a `build:`/scenario factory added in 1.4) before use. [Source: GoldenChecksumReplay.cs]
- **`SimChecksumCoverageGuardTest`** (`godot/ProjectChimera.Sim.Tests/Golden/`): asserts `AlgoVersion` and pins a hand-built known-state hash (`ExpectedV2Hash`) via `ComputeKnownStateHash`. When RNG enters the hash: change the version assertion 2→3, **seed the hand-built `world.Rng` to a known value** in the builder, and re-pin the expected hash (capture from a green run). [Source: SimChecksumCoverageGuardTest.cs]
- **Existing randomness audit (whole repo, verified):** **No `System.Random` and no `DateTime` anywhere.** Godot RNG only in `StressTest.cs:35,79-83` (`RandomNumberGenerator` — excluded from Tier-1, relocates in 1.8a), `AudioManager.cs:146` (`GD.RandRange`), `RtsCameraController.cs:193,195` (`GD.RandRange`) — **all presentation/test layer**; the sim (`Core/Combat/Economy/Navigation`) is RNG-free. Do **not** touch these — they are legitimately non-deterministic presentation. [Source: grep over `**/*.cs`]
- **No Roslyn analyzer exists yet** (it's Story 1.10b). `SimRng` is a new sim-internal pure-C# type — it compiles and is usable immediately with **no allowlist work**. The analyzer that bans `System.Random`/Godot RNG comes later. [Source: glob `*Analyzer.cs` → none; game-architecture.md:2475]

### Scope fence — do NOT, in this story

- **Do NOT** build random effects, the `EffectNode` graph, `ModifierSystem`, the `AbilitySystem`, or make ANY of the 9 systems draw from `world.Rng`. The param/field is plumbing for Epic 2. [Source: game-architecture.md D1 steps 5–9]
- **Do NOT** build `ScenarioValidator`, `Validated<T>`, or the "forbidden-until-SimRng" rule — that is **Story 1.7** (`epics.md`:630). [Source: epics.md#Story-1.7]
- **Do NOT** make `SimRng` a `struct`/`ref struct` (D1). Do NOT add `ref` keywords to `Tick`/`Compute`. Do NOT add per-entity RNG arrays to `EntityWorld` (it is one shared reference, D2).
- **Do NOT** record per-tick RNG state in the replay — seed only (D6).
- **Do NOT** change any system's `Tick` signature, `ISimSystem`, or `SimChecksum.Compute`'s signature (D2 keeps them all stable).
- **Do NOT** touch `StressTest`/`AudioManager`/`RtsCameraController` RNG, or wire a real multiplayer match-seed handshake (that is Epic 9 — use a fixed default/const seed for now).
- **Do NOT** hand-edit a golden file or the pinned coverage hash — regenerate via the record-mode path. But **DO** re-baseline this time (unlike 1.4): the goldens *must* move because RNG state entered the hash.
- **Do NOT** introduce any `float`/`double`/`Mathf`/`System.Math.*` into `SimRng` or its tests.

---

## Tasks / Subtasks

- [x] **Task 1 — Net-new `SimRng` class (AC: 1)**
  - [x] Create `godot/src/Core/SimRng.cs`: `public sealed class SimRng` (`#nullable enable`, namespace `ProjectChimera.Core`, **no `using Godot`**, no `float`/`double`). Use the SplitMix64 reference in Dev Notes.
  - [x] API: `SimRng(ulong seed = …)`, `ulong State { get; }`, `void Seed(ulong seed)`, `ulong NextRaw()`, `int NextInt(int countExclusive)` (throw on `<= 0`), `Fixed NextFixed()` (`Fixed.FromRaw((int)(NextRaw() >> 48))`).
  - [x] XML-doc the class: only sim randomness source (AR-13/AR-15); integer-only; shared single instance; folded into checksum + replay; the ascending-id-candidates-then-draw rule.
  - [x] `dotnet build godot/godot.csproj` → green.

- [x] **Task 2 — Hold one shared `SimRng` on `EntityWorld` (AC: 1, 2)**
  - [x] `EntityWorld.cs`: add `public SimRng Rng { get; }`; initialize with a named default seed const (`DEFAULT_RNG_SEED`) in the ctor (so `new EntityWorld()` keeps working everywhere). Same namespace — no new `using`.
  - [x] Confirm reseed path: `world.Rng.Seed(seed)` resets the stream; the `Rng` reference stays fixed (matches the readonly-array pattern).
  - [x] `dotnet build` → green.

- [x] **Task 3 — Fold RNG state into `SimChecksum` + bump `AlgoVersion` (AC: 2)**
  - [x] `SimChecksum.cs`: after the faction loop, before `return hash;`, added the two-int (low/high 32-bit) `Mix` fold of `world.Rng.State`.
  - [x] Bumped `AlgoVersion` 2 → 3; appended the v3 line to the version-history doc; added the RNG line to the hashed-state list in the class doc. **No signature change** to `Compute`.
  - [x] `dotnet build` → green.

- [x] **Task 4 — Replay seed: record + restore (AC: 2)**
  - [x] `ReplayRecorder.cs`: bumped `VERSION` 1 → 2; added a required `ulong seed` ctor param + public `Seed` property; `WriteHeader` writes the 8-byte seed after the scenario-path bytes. Persists the match-START seed (NOT `State`); commented WHY (seed-only, D6). MainScene call site updated to pass `EntityWorld.DEFAULT_RNG_SEED`.
  - [x] `ReplayPlayer.cs`: relaxed the exact-match version check to accept v1..current; reads the seed only when `version >= 2` (v1 → `DEFAULT_RNG_SEED`); exposes a `Seed` property; reseeds `_world.Rng.Seed(seed)` in the ctor **before any tick** (foolproof — MainScene needs no change).
  - [x] `dotnet build godot/godot.csproj` → green.

- [x] **Task 5 — `SimRng` unit tests (AC: 1)**
  - [x] New `godot/ProjectChimera.Sim.Tests/Determinism/SimRngTests.cs`: same-seed bit-identical streams (1000 draws); `Seed` restore reproduces / different seeds diverge; `State` tracks seed + advances; `NextInt(n)` in `[0,n)` + throws on `<=0`; `NextFixed()` `Raw` in `[0, ONE)`.
  - [x] **Independent expected value:** pinned the canonical SplitMix64 outputs (computed in standalone Python, externally citable — seed 0 → `0xE220A8397B1DCDAF`, etc.) and asserted `NextRaw()` equals them. NOT derived by re-running the method (AC1).
  - [x] `dotnet test --filter FullyQualifiedName~SimRng` → green (16 cases).

- [x] **Task 6 — Re-baseline goldens + re-pin coverage guard (AC: 2)**
  - [x] `SimChecksumCoverageGuardTest`: `AlgoVersion` assertion 2→3; `ComputeKnownStateHash` seeds the hand-built `world.Rng` to a fixed known value (`0x0123456789ABCDEF`); renamed `ExpectedV2Hash`→`ExpectedV3Hash` and re-pinned to `0x8C96EC08` (captured from a green v3 run); updated docs + method name.
  - [x] Re-baselined **both** goldens via the record path; both now self-stamp `checksum_algo_version: 3` and every checksum moved (e.g. golden-scenario tick 1: `2863D41A`→`31E69403`). Regenerated, not hand-edited.
  - [x] `dotnet test` (normal mode) → both goldens verify green at v3.

- [x] **Task 7 — RNG-driven checksum + replay reproduction tests (AC: 2, 3)**
  - [x] New `godot/ProjectChimera.Sim.Tests/Golden/SimRngChecksumReplayTests.cs`. The RNG draw lives in a test-only `RngDrawTestSystem : ISimSystem` (advances `world.Rng` + writes hashed Health), so it re-runs on playback.
    - **AC3 "twice":** two live runs with the same seed → identical per-tick checksum sequences (`SequenceEqual`).
    - **AC2/AC3 "across a replay":** record the match seed to a `.chmr` (v2 header), reconstruct the SAME loop seeded to a WRONG value, let `ReplayPlayer` restore the seed from the header, drive via `Flush`+`StepOnce`; replayed checksum sequence byte-identical to the live run. Verified `recorder.Seed`→`player.Seed`→`world.Rng.State` round-trip.
    - **Negative control:** a different seed diverges the checksum sequence (stream/checksum-level — no reflection/BCL-internal probing, per the 1.4 review lesson). Plus a v1-back-compat test (no seed → `DEFAULT_RNG_SEED`).
  - [x] `dotnet test --filter FullyQualifiedName~SimRngChecksumReplay` → green (4 cases).

  > **Replay-test note — RESOLVED: took the RECOMMENDED path.** Added `ReplayRecorder.cs`, `ReplayPlayer.cs`, and `NetworkCommand.cs` (for `UnitOrder`) as explicit single-file `<Compile Include>` entries to `ProjectChimera.Sim.Tests.csproj` after verifying all three are Godot-free (System.* + Core only). The full record→restore-seed→replay round-trip is now Tier-1 (headless) testable — no Tier-2/GdUnit4 deferral. `src/Multiplayer` is still NOT globbed (LockstepManager/ENet stay out).

- [x] **Task 8 — Verify end-to-end (AC: 1, 2, 3)**
  - [x] `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` → ALL green (81 passed / 0 failed; baseline 61 + 20 new), headless, ~115 ms.
  - [x] `dotnet build godot/godot.csproj` → green (only the pre-existing CS8632 warnings — left untouched).
  - [x] Grep `SimRng.cs` and `SimRngTests.cs`: zero `float`/`double`/`System.Random`/`Mathf`/`Math.`/`using Godot` (reworded the two doc comments that named the banned tokens).
  - [x] Both goldens self-stamp `checksum_algo_version: 3`; the coverage guard pins v3 (`0x8C96EC08`).
  - [x] `git diff` confirmed: no system `Tick` signature, no `ISimSystem`, no `SimChecksum.Compute` signature changed (D2).

---

## Dev Notes

### Reference snippets (copy/adapt — grounded in the verified code at `a90c786`)

**`SimRng` (Task 1) — SplitMix64, integer-only:**
```csharp
#nullable enable
namespace ProjectChimera.Core
{
    /// <summary>
    /// The ONLY source of randomness in the simulation (AR-13/AR-15). Seeded, deterministic, and
    /// INTEGER-ONLY (SplitMix64) — the same seed yields a bit-identical stream on every machine, across
    /// replays, and across platforms. One shared instance lives on <see cref="EntityWorld.Rng"/>; it is
    /// advanced in-tick, its <see cref="State"/> is folded into <see cref="SimChecksum"/>, and its seed is
    /// persisted by ReplayRecorder/Player. NEVER use System.Random / Godot RNG / float in sim code. Random
    /// selection MUST collect candidates in ascending-id order BEFORE calling NextInt.
    /// </summary>
    public sealed class SimRng
    {
        // SplitMix64 (Vigna). Single ulong state; full 2^64 period; tolerates any seed incl. 0.
        private const ulong GAMMA = 0x9E3779B97F4A7C15UL;
        private ulong _state;

        public SimRng(ulong seed = 0UL) => _state = seed;

        /// <summary>Current internal state — the value folded into SimChecksum (the desync tripwire).</summary>
        public ulong State => _state;

        /// <summary>Reset the stream to a seed (match start / replay restore). Deterministic.</summary>
        public void Seed(ulong seed) => _state = seed;

        /// <summary>Advance the stream and return 64 raw bits — the single primitive draw.</summary>
        public ulong NextRaw()
        {
            unchecked
            {
                ulong z = (_state += GAMMA);
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        /// <summary>Non-negative int in [0, countExclusive). Modulo bias is deterministic (identical on every
        /// peer) so it does not break lockstep. countExclusive must be &gt; 0.</summary>
        public int NextInt(int countExclusive)
        {
            if (countExclusive <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(countExclusive), "must be > 0");
            return (int)(NextRaw() % (ulong)countExclusive);
        }

        /// <summary>Fixed in [0, 1): top 16 raw bits become the 16 fractional bits. Integer-only — no float.</summary>
        public Fixed NextFixed() => Fixed.FromRaw((int)(NextRaw() >> 48));
    }
}
```

**`EntityWorld` field (Task 2):**
```csharp
// near the other readonly fields:
public const ulong DEFAULT_RNG_SEED = 0x9E3779B97F4A7C15UL; // recognizable nonzero default
public SimRng Rng { get; }
// in the ctor, beside the `new T[MAX_ENTITIES]` allocations:
Rng = new SimRng(DEFAULT_RNG_SEED);
// bootstrap / replay set the match seed: world.Rng.Seed(matchSeed);
```

**`SimChecksum.Compute` fold (Task 3)** — inserted after the `foreach` faction loop (`:87`):
```csharp
ulong rng = world.Rng.State;
hash = Mix(hash, (int)(rng & 0xFFFFFFFFUL));
hash = Mix(hash, (int)(rng >> 32));
return hash;
```

**Replay header seed (Task 4)** — `ReplayRecorder.WriteHeader` after the path write:
```csharp
_writer.Write(MAGIC);
_writer.Write(VERSION);          // now 2
var pathBytes = System.Text.Encoding.UTF8.GetBytes(scenarioPath);
_writer.Write((ushort)pathBytes.Length);
_writer.Write(pathBytes);
_writer.Write(seed);             // NEW: 8-byte ulong match seed (D6)
```
```csharp
// ReplayPlayer header parse (:73-84) after reading pathBytes:
ulong seed = 0UL;
if (version >= 2) seed = reader.ReadUInt64();
// expose `seed`; the playback driver calls world.Rng.Seed(seed) before the first StepOnce.
```

**Independent expected-value test (Task 5, AC1)** — one hand-computed SplitMix64 step:
```csharp
// seed s=0: z = s + GAMMA = 0x9E3779B97F4A7C15; then the two mix lines; assert NextRaw() == that value.
// Compute the expected constant by hand (or a one-off scratch program), paste it literally — do NOT
// call a second SimRng to "verify" (tautological). This pins the algorithm, not just self-consistency.
```

### Constraints & gotchas

- **`dotnet build`/`dotnet test` are authoritative** for C# correctness; the Godot MCP `run` does not rebuild the test assembly. Build + test before declaring done. [Source: LEARNINGS/1.1–1.4 Dev Notes]
- **This story RE-BASELINES the goldens** — the single biggest departure from Story 1.4 (which forbade it). Folding RNG state + bumping `AlgoVersion` MUST move every golden checksum. If a golden does NOT move after Task 3, the fold isn't running (or `world.Rng` isn't reached). Re-baseline via the record path, never by hand. [Source: SimChecksum.cs:27-34; GoldenChecksumReplay.cs:167]
- **`SimRng` must be a `class`, integer-only.** A struct breaks shared-stream semantics (D1); a `float`/`double` breaks cross-platform determinism. SplitMix64 needs `unchecked` for its wrapping `*`/`+` on `ulong`. [Source: game-architecture.md:1898,2001]
- **Reach `Rng` through `world`, change no signatures** (D2). If you find yourself editing `ISimSystem` or `SimChecksum.Compute`'s parameter list, stop — the field-on-`world` design avoids it. [Source: SimChecksum.cs:41-42; SimulationLoop.cs:8-12]
- **Replay = seed only** (D6). Do not serialize per-tick RNG state.
- **Sim/Presentation boundary:** `SimRng` is `src/Core` (sim) and Godot-free — the `GodotFreeBoundaryTest` fails the build if a `using Godot` sneaks in. Tests author worlds with `Fixed.FromInt`/`FromRaw` and `SimRng.Seed`, never `FromFloat`/`System.Random`/`DateTime`. [Source: project-context.md; ProjectChimera.Sim.Tests.csproj]
- **No new dependencies.** Reuse `SimChecksum.Mix`, the `Fixed` API, and the 1.1 xUnit stack. `SimRng.cs` auto-globs into both `godot.csproj` and the test project via `..\src\Core\**`. The only possible `.csproj` edit is the explicit `ReplayRecorder.cs`/`ReplayPlayer.cs` includes for the replay test (Task 7 note). [Source: ProjectChimera.Sim.Tests.csproj:14-27]
- **Pre-existing CS8632** nullable warnings (`SimulationLoop.cs`/`GatheringSystem.cs`/`FlowFieldSystem.cs`) are not this story's bug — leave them. [Source: deferred-work.md]
- **Test-durability lesson from 1.4:** the 1.4 review flagged negative controls that lean on reflection/BCL internals. Make this story's negative control robust — assert on the RNG *stream/checksum* directly (seed A vs seed B diverges), not on implementation internals. [Source: deferred-work.md#story-1.4]

### Project Structure Notes

- **NEW:** `godot/src/Core/SimRng.cs` (sim, Godot-free, auto-globbed). **NEW tests:** `Determinism/SimRngTests.cs`, `Golden/SimRngChecksumReplayTests.cs`.
- **EDIT:** `godot/src/Core/EntityWorld.cs` (one `Rng` field + ctor init), `godot/src/Core/SimChecksum.cs` (fold + `AlgoVersion` + doc), `godot/src/Multiplayer/ReplayRecorder.cs` (seed in header + VERSION), `godot/src/Multiplayer/ReplayPlayer.cs` (seed read + reseed), `Golden/SimChecksumCoverageGuardTest.cs` (v3 + re-pin), and the embedded goldens `Golden/golden-scenario.golden.txt` + `Golden/golden-multifaction.golden.txt` (re-baselined, not hand-edited).
- **Possible EDIT:** `ProjectChimera.Sim.Tests.csproj` (explicit `ReplayRecorder.cs`/`ReplayPlayer.cs` includes) — only if you take the recommended replay-test path (Task 7 note).
- **No** `Effects/`, `Dsl/`, `Validation/` folders, no `ScenarioValidator`/`Validated<T>` — later stories.

### Project Context Rules

_Extracted from `_bmad-output/project-context.md` + `game-architecture.md` — these govern every edit here:_

- **`SimRng` (new, seeded, deterministic) is the ONLY randomness in sim. Never `System.Random`/Godot RNG in sim code; random selection sorts ascending-ID then draws; `SimRng` folds into `SimChecksum`.** This story builds exactly that generator and the fold. [Source: project-context.md "Forward Architecture Rules"]
- **All simulation math uses `Fixed` (16.16); never `float`/`double`/`Mathf`/`Math.*` for gameplay-affecting values; `Fixed.FromFloat` is load-time only.** `SimRng` is integer-only and `NextFixed` builds a `Fixed` from raw bits — no float ever. [Source: project-context.md "Determinism"]
- **Process entities in ascending ID order; iteration order is part of the deterministic contract.** Random selection (Epic 2) must collect candidates ascending-id BEFORE drawing — documented on `SimRng` for the future. [Source: game-architecture.md:1897-1898]
- **Reuse existing systems; SoA; composition over inheritance.** `SimRng` is one shared instance on the existing `EntityWorld`/checksum/replay machinery — no parallel subsystem. [Source: project-context.md "Data layout"]
- **Engine/runtime:** Godot 4.6.3 target, .NET 8 (`net8.0`); assembly/namespace `ProjectChimera.*`; project files `godot.csproj`/`godot.sln`; Tier-1 test project `ProjectChimera.Sim.Tests` (xUnit, Godot-free). [Source: project-context.md "Technology Stack"]

### References

- [Source: epics.md#Story-1.5 (lines 582-598)] — story statement; the 3 ACs (seeded bit-identical integer-only draws; state folded into `SimChecksum` + recorded/restored by `ReplayRecorder`/`Player`; RNG-driven scenario byte-identical across two runs + a replay); "net-new, grep shows only `System.Random` in StressTest/AudioManager"; "re-baselines golden once RNG state enters the hash"; Covers FR-39/FR-44/FR-47/AR-13/AR-15; Depends on 1.4.
- [Source: epics.md#Story-1.7 (line 630)] — the forbidden-until-SimRng validator rule is owned by 1.7, **relocated out of 1.5**. Do not build it here.
- [Source: game-architecture.md:1897-1898 (§N1 — deterministic kernel)] — the RULE: only `SimRng`; canonical draw order (sys-reg → faction slot → entity id); candidates sorted ascending-id before `NextInt`; single shared mutable instance referenced, never copied.
- [Source: game-architecture.md:2522 (Deferred to implementation — M1)] — `SimRng` concrete API (`NextInt(count)`/`NextFixed()`/`NextRaw()`) + state lives in a small `SimState` field exposed to `SimChecksum` (this story decides it = `EntityWorld.Rng`).
- [Source: game-architecture.md:496-503 (migration sequence, step 2)] — build `SimRng` (PCG/xorshift over `Fixed`/int), thread through systems by ref, include in `SimChecksum` + `ReplayRecorder`/`Player`; until then the validator forbids random effects.
- [Source: game-architecture.md:2001] — `EffectContext.Rng` is a shared `SimRng` reference (Epic 2 forward-compat — confirms reference-type design).
- [Source: SimChecksum.cs:35,41-42,87-103] · [SimulationLoop.cs:8-12,100,137] · [EntityWorld.cs:60-95] · [FixedPoint.cs] · [ReplayRecorder.cs:24-25,106-114] · [GoldenChecksumReplay.cs:34,51,167,198] · [ProjectChimera.Sim.Tests.csproj:14-38] — verified anchors above.

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (Claude Code, gds-dev-story workflow).

### Debug Log References

- Baseline before any change: full Tier-1 suite green (61/61).
- After Task 3 fold + AlgoVersion bump, the coverage guard reported the new v3 known-state hash via its own failure message: `actual 0x8C96EC08` → pinned.
- Re-baseline (record mode, `~Golden` filter): 28 cases green; both source goldens rewritten and self-stamp `checksum_algo_version: 3`.
- Final: game build succeeds (only pre-existing CS8632 warnings); full Tier-1 suite green (81/81, ~115 ms).

### Completion Notes List

- **Design decisions honored as specified.** `SimRng` is a sealed reference type (D1, SplitMix64/D3); single shared instance on `EntityWorld.Rng` reached through the `world` callers already receive (D2 — zero `ISimSystem`/`Compute`/system-Tick signature churn, verified via `git diff`); state folded into `SimChecksum` with `AlgoVersion` 2→3 and an intended re-baseline of both goldens (D5); replay records the SEED ONLY in a v2 header (D6); no system draws / no random gameplay added (D7); no validator/`ScenarioValidator` built (D8 — that is Story 1.7).
- **AC1 independence.** The expected `NextRaw()` values were computed in a standalone Python implementation of SplitMix64 (and cross-checked against the textbook canonical value `0xE220A8397B1DCDAF` for seed 0), then pasted as literals — not produced by calling `SimRng` (the "tautological assert" rule).
- **Replay-test scope decision (Task 7 open question #1):** took the RECOMMENDED path — `ReplayRecorder.cs`/`ReplayPlayer.cs`/`NetworkCommand.cs` are explicitly compiled into the Tier-1 (Godot-free) test assembly, so the full record→restore-seed→replay round-trip is verified headlessly. All three were confirmed Godot-free (System.* + `ProjectChimera.Core` only). `src/Multiplayer` remains un-globbed so ENet/`LockstepManager` stay out.
- **Match-seed source (open question #3):** for 1.5 the seed is the fixed `EntityWorld.DEFAULT_RNG_SEED` constant; the real MP seed handshake remains Epic 9 (scope boundary confirmed).
- **Back-compat:** `ReplayPlayer`'s exact-match version check was relaxed to accept v1..current; v1 `.chmr` files (no seed field) fall back to `DEFAULT_RNG_SEED` (covered by a dedicated test).
- **Pre-existing items left alone:** the CS8632 nullable warnings and `EntityWorld.cs` load-time `Fixed.FromFloat(8f)` for `VisionRange` are fenced to their own stories and were not touched.

### File List

**New:**
- `godot/src/Core/SimRng.cs`
- `godot/ProjectChimera.Sim.Tests/Determinism/SimRngTests.cs`
- `godot/ProjectChimera.Sim.Tests/Golden/SimRngChecksumReplayTests.cs`

**Modified:**
- `godot/src/Core/EntityWorld.cs` (DEFAULT_RNG_SEED const + shared `Rng` property + ctor init)
- `godot/src/Core/SimChecksum.cs` (RNG-state fold + `AlgoVersion` 2→3 + docs)
- `godot/src/Multiplayer/ReplayRecorder.cs` (VERSION 1→2 + seed ctor param/property + header write)
- `godot/src/Multiplayer/ReplayPlayer.cs` (version-range check + seed read + `Seed` property + reseed `world.Rng`)
- `godot/src/Core/MainScene.cs` (recorder call site passes the match seed)
- `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` (3 explicit Godot-free Multiplayer includes)
- `godot/ProjectChimera.Sim.Tests/Golden/SimChecksumCoverageGuardTest.cs` (v3 assertion + seeded known-state + re-pinned hash)
- `godot/ProjectChimera.Sim.Tests/Golden/golden-scenario.golden.txt` (re-baselined to v3)
- `godot/ProjectChimera.Sim.Tests/Golden/golden-multifaction.golden.txt` (re-baselined to v3)

### Change Log

| Date | Change |
|------|--------|
| 2026-06-23 | Implemented Story 1.5: net-new `SimRng` (SplitMix64) on `EntityWorld`, folded its state into `SimChecksum` (AlgoVersion 2→3, both goldens re-baselined), threaded the match seed through `.chmr` replay (header v1→2), added 20 Tier-1 tests. Full suite 81/81 green. Status → review. |
| 2026-06-23 | Code review (3-layer adversarial: Blind / Edge / Auditor, all Opus 4.8) — 0 critical/high/medium; all 3 ACs + D1–D8 verified MET, SplitMix64 outputs independently re-derived. Applied the 1 LOW patch: `ReplayPlayer` now rejects a truncated v2 seed header with `InvalidDataException` (+1 regression test). Game build green; Tier-1 suite 82/82. 3 LOW items deferred (→ Epic 2 replay-of-orders proof / → Epic 9 recorder match-seed / coverage-hardening). Status → done. |

---

## Open Decisions for Alec (surfaced during analysis — none block dev)

1. **Replay round-trip test scope (Task 7).** Recommended: add `ReplayRecorder.cs`/`ReplayPlayer.cs` as explicit Godot-free includes to the Tier-1 csproj so AC2/AC3's `.chmr` seed round-trip is unit-tested headlessly. Fallback: narrower seed-header test + golden-harness "twice" proof, deferring full sim-replay verification to Tier-2. (Dev will pick and record the choice; flagging in case you have a preference.)
2. **RNG algorithm.** SplitMix64 recommended (tiny, integer-only, seed-0 safe). `pcg32` is an acceptable substitute — leaf choice per `game-architecture.md`:2522.
3. **Match-seed source.** For 1.5 the seed is a fixed default const (the MP match-seed handshake is Epic 9). No real seed negotiation is wired now — confirm that's the intended scope boundary.

---

### Review Findings

_Adversarial code review (Blind Hunter + Edge Case Hunter + Acceptance Auditor, all Opus 4.8), 2026-06-23. Diff `a90c786..HEAD` scoped to `godot/`. Outcome: **0 decision-needed · 1 patch · 3 deferred · 1 dismissed**. All 3 ACs and all design decisions D1–D8 verified **MET**; the SplitMix64 outputs were independently re-derived and confirmed against the pinned test constants (AC1 independence is real, not circular); both goldens confirmed re-baselined + self-stamped `checksum_algo_version: 3`; no `ISimSystem` / `SimChecksum.Compute` / system-`Tick` signature changed (D2); all scope fences respected._

**Patch (open):**

- [x] [Review][Patch] Truncated/forged v2 `.chmr` header throws `EndOfStreamException` instead of the documented `InvalidDataException` [godot/src/Multiplayer/ReplayPlayer.cs:~88] — the net-new 8-byte seed read (`version >= 2 ? reader.ReadUInt64() : DEFAULT_RNG_SEED`) was unguarded; a v2-stamped file truncated within the seed field escaped the loader's `InvalidDataException` corruption contract (the live `MainScene.TryLoadReplay` catch swallows it → degraded diagnostics only, no crash, no desync). _Severity LOW. Found independently by Blind + Edge._ **✅ FIXED 2026-06-23** — added an "8 bytes remaining" length check before the v2 seed read, throwing `InvalidDataException` on a short read (matches the bad-magic / unsupported-version rejections); scoped to the net-new seed field only (the pre-existing path read stays out of scope). Added regression test `V2Replay_TruncatedSeed_ThrowsInvalidData`. Game build green; Tier-1 suite 82/82.

**Deferred:**

- [x] [Review][Defer] AC3 replay round-trip records zero orders — replay proof reduces to seed round-trip + same-seed determinism [godot/ProjectChimera.Sim.Tests/Golden/SimRngChecksumReplayTests.cs:~348] — deferred, **by design** (D6 seed-only / D7 no production draws this story); Auditor-confirmed NOT an AC violation. The end-to-end "recorded behavior replays under RNG" proof lands when a real system draws (Epic 2). Found by Blind + Auditor.
- [x] [Review][Defer] Recorder hardcodes `EntityWorld.DEFAULT_RNG_SEED` instead of the match's actual start seed [godot/src/Core/MainScene.cs:~2113] — deferred, correct today (1.5 always runs the default seed) but a latent silent-desync trap once the Epic 9 MP seed handshake introduces a real per-match seed. Wire the recorded seed to the real match-start seed when that handshake lands (naive live `world.Rng.State` is itself wrong after draws). Found by Blind.
- [x] [Review][Defer] `ulong.MaxValue` seed exercised only as a fixture, never pinned as a stream [godot/ProjectChimera.Sim.Tests/Determinism/SimRngTests.cs] — deferred, coverage-hardening only (max-seed `unchecked` wrap verified correct; seeds 0 & 12345 already independently pinned). Optional: add one externally-computed max-seed assertion. Found by Edge.

**Dismissed (1):** `NextInt`/`NextFixed` lack an independently-pinned value — Auditor's own verdict "Gap: None functionally"; both are pure functions of the independently-pinned `NextRaw`, so independence holds transitively.
