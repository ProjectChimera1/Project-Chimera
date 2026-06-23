---
baseline_commit: a90c786
---

# Story 1.5: Seeded deterministic SimRng folded into checksum + replay

Status: ready-for-dev

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

- [ ] **Task 1 — Net-new `SimRng` class (AC: 1)**
  - [ ] Create `godot/src/Core/SimRng.cs`: `public sealed class SimRng` (`#nullable enable`, namespace `ProjectChimera.Core`, **no `using Godot`**, no `float`/`double`). Use the SplitMix64 reference in Dev Notes.
  - [ ] API: `SimRng(ulong seed = …)`, `ulong State { get; }`, `void Seed(ulong seed)`, `ulong NextRaw()`, `int NextInt(int countExclusive)` (throw on `<= 0`), `Fixed NextFixed()` (`Fixed.FromRaw((int)(NextRaw() >> 48))`).
  - [ ] XML-doc the class: only sim randomness source (AR-13/AR-15); integer-only; shared single instance; folded into checksum + replay; the ascending-id-candidates-then-draw rule.
  - [ ] `dotnet build godot/godot.csproj` → green.

- [ ] **Task 2 — Hold one shared `SimRng` on `EntityWorld` (AC: 1, 2)**
  - [ ] `EntityWorld.cs`: add `public SimRng Rng { get; }`; initialize with a named default seed const in the ctor (so `new EntityWorld()` keeps working everywhere). Add `using` for `ProjectChimera.Core` types as needed (same namespace — none needed).
  - [ ] Confirm reseed path: `world.Rng.Seed(seed)` resets the stream; the `Rng` reference stays fixed (matches the readonly-array pattern).
  - [ ] `dotnet build` → green.

- [ ] **Task 3 — Fold RNG state into `SimChecksum` + bump `AlgoVersion` (AC: 2)**
  - [ ] `SimChecksum.cs`: after the faction loop (`:87`), before `return hash;` (`:89`), add:
    ```csharp
    // ── RNG state (v3) ── single shared SimRng; its state is sim truth — a divergent draw stream desyncs silently.
    ulong rng = world.Rng.State;
    hash = Mix(hash, (int)(rng & 0xFFFFFFFFUL)); // low 32 bits
    hash = Mix(hash, (int)(rng >> 32));          // high 32 bits
    ```
  - [ ] Bump `AlgoVersion` 2 → 3 (`:35`) and append the v3 line to the version-history doc (`:27-34`); add the RNG line to the hashed-state list in the class doc (`:9-19`). **No signature change** to `Compute`.
  - [ ] `dotnet build` → green.

- [ ] **Task 4 — Replay seed: record + restore (AC: 2)**
  - [ ] `ReplayRecorder.cs`: bump `VERSION` 1 → 2 (`:25`); make `WriteHeader` (`:106-114`) write the 8-byte `ulong` seed after the scenario-path bytes; accept the seed via the ctor/begin path. **Persist the match-START seed (the value passed to `world.Rng.Seed(...)`), NOT `world.Rng.State`** — `State` has advanced after ticks; a replay must restore the stream's origin. Comment WHY (seed-only — lockstep regenerates the stream; D6).
  - [ ] `ReplayPlayer.cs`: after the header path parse (`:73-84`), read the seed **only when `version >= 2`** (`reader.ReadUInt64()`); expose it (e.g. a `Seed` property) and ensure the playback bootstrap calls `world.Rng.Seed(seed)` **before the first `StepOnce`**. v1 files → default seed (document).
  - [ ] `dotnet build godot/godot.csproj` → green.

- [ ] **Task 5 — `SimRng` unit tests (AC: 1)**
  - [ ] New `godot/ProjectChimera.Sim.Tests/Determinism/SimRngTests.cs`:
    - Same seed → two instances produce bit-identical `NextRaw` streams (≥1000 draws).
    - `Seed(s)` restore reproduces the stream from that point; different seeds diverge.
    - `NextInt(n)` always in `[0, n)`; `NextInt(0)`/negative throws.
    - `NextFixed()` always in `[0, 1)` (`Raw` in `[0, 65535]`), integer-only.
    - **Independent expected value:** hand-compute one SplitMix64 step for a known seed and assert `NextRaw()` equals it (not derived by re-running the method — AC1).
  - [ ] `dotnet test --filter FullyQualifiedName~SimRng` → green.

- [ ] **Task 6 — Re-baseline goldens + re-pin coverage guard (AC: 2)**
  - [ ] `SimChecksumCoverageGuardTest`: change the `AlgoVersion` assertion 2→3; in `ComputeKnownStateHash`, seed the hand-built `world.Rng` to a fixed known value; re-pin `ExpectedV2Hash` (→ a v3 name/value) from a green run; update comments.
  - [ ] Re-baseline **both** goldens: `CHIMERA_GOLDEN_RECORD=1` → run the record-mode golden test(s) for `golden-scenario` **and** `golden-multifaction` → `dotnet build` (refreshes embedded copies) → verify both files changed and self-stamp `checksum_algo_version: 3`. Commit them in the SAME commit as Task 3. (PowerShell: `$env:CHIMERA_GOLDEN_RECORD=1; dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~Golden; $env:CHIMERA_GOLDEN_RECORD=$null; dotnet build godot/godot.csproj`.)
  - [ ] `dotnet test` (normal mode) → both goldens verify green at v3.

- [ ] **Task 7 — RNG-driven checksum + replay reproduction tests (AC: 2, 3)**
  - [ ] New `godot/ProjectChimera.Sim.Tests/Golden/SimRngChecksumReplayTests.cs`. **The RNG-driven behavior MUST live in a test-only `ISimSystem` added to the loop — NOT in a `RunAndRecord` `perturb` callback.** A `.chmr` records *orders*, not the perturb, so the perturb does not re-run on playback and the replayed checksum would diverge for a non-bug reason. Put the draw in a system so the replay (which re-runs the sim) reproduces it:
    ```csharp
    sealed class RngDrawTestSystem : ISimSystem { // test-only; mirrors how Epic 2 effects will draw
        public void Tick(EntityWorld world, Fixed dt) {
            int id = /* a known alive entity id from the scenario */;
            world.Health[id] = world.Health[id] + Fixed.FromInt(world.Rng.NextInt(3)); // draw advances shared stream + writes HASHED state
        }
    }
    ```
    - **AC3 "twice":** `RunAndRecord(ticks, build: () => BuildLoopWithRngDrawSystem(seed))` (the `build:` factory adds `RngDrawTestSystem` to the loop and `world.Rng.Seed(seed)`s before running) — run twice; assert the two `Sample.Hash` sequences are `SequenceEqual`. Same seed + same systems → identical stream.
    - **AC2/AC3 "across a replay":** record that SAME loop (test system included) to a `.chmr` (match-start seed in header); replay by reconstructing the SAME loop (test system included) + `world.Rng.Seed(seed)` from the header, driving it through `ReplayPlayer`; capture the checksum sequence and assert byte-identical to the live run. The draw reproduces because it lives in the replayed sim. **See the replay-test note below** for the csproj decision.
    - **Negative control:** a different seed (or temporarily removing the RNG mix from `SimChecksum`) makes the sequences diverge — proves the test bites. Verify RED, then restore.
  - [ ] `dotnet test --filter FullyQualifiedName~SimRngChecksumReplay` → green.

  > **Replay-test note (resolve before writing the `.chmr` test):** `ReplayRecorder`/`ReplayPlayer` live in `src/Multiplayer`, which the Tier-1 csproj does **not** glob (a wildcard would drag in ENet/Godot-coupled `LockstepManager`). **Recommended:** add the two files as **explicit single-file `<Compile Include>`** entries to `ProjectChimera.Sim.Tests.csproj` after confirming they (and their pure deps like `UnitOrder`) are Godot-free — then the full record→replay round-trip is Tier-1-testable. **Fallback** (if they can't be isolated cleanly): unit-test the seed header write/read directly, prove RNG reproduction via the golden harness "twice" path, and defer the full sim-replay verification to Tier-2/GdUnit4. Pick one; note the choice in the Dev Agent Record.

- [ ] **Task 8 — Verify end-to-end (AC: 1, 2, 3)**
  - [ ] `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` → ALL green (existing suite + new tests), headless, in seconds.
  - [ ] `dotnet build godot/godot.csproj` → green (only the pre-existing CS8632 warnings — do not "fix" them).
  - [ ] Grep `SimRng.cs` and `SimRngTests.cs`: zero `float`/`double`/`System.Random`/`Mathf`/`Math.`/`using Godot`.
  - [ ] Confirm both goldens self-stamp `checksum_algo_version: 3` and the coverage guard pins v3.
  - [ ] Confirm `git diff` touches no system `Tick` signature, no `ISimSystem`, no `SimChecksum.Compute` signature (D2).

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

### Debug Log References

### Completion Notes List

### File List

---

## Open Decisions for Alec (surfaced during analysis — none block dev)

1. **Replay round-trip test scope (Task 7).** Recommended: add `ReplayRecorder.cs`/`ReplayPlayer.cs` as explicit Godot-free includes to the Tier-1 csproj so AC2/AC3's `.chmr` seed round-trip is unit-tested headlessly. Fallback: narrower seed-header test + golden-harness "twice" proof, deferring full sim-replay verification to Tier-2. (Dev will pick and record the choice; flagging in case you have a preference.)
2. **RNG algorithm.** SplitMix64 recommended (tiny, integer-only, seed-0 safe). `pcg32` is an acceptable substitute — leaf choice per `game-architecture.md`:2522.
3. **Match-seed source.** For 1.5 the seed is a fixed default const (the MP match-seed handshake is Epic 9). No real seed negotiation is wired now — confirm that's the intended scope boundary.
