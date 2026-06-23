---
baseline_commit: ddbb110
---

# Story 1.3a: FactionRegistry — localize all faction-count knowledge

Status: review

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a solo developer hardening the sim for N players,
I want a `FactionRegistry` (sim-layer, Godot-free) that centralizes `PLAYER_COUNT=8`, `FACTION_ARRAY_SIZE=9` (incl. Neutral), and the one `(Faction)(slot+1)` cast — and route `SimChecksum`'s faction-resource loop through it (Ore-only, byte-identical) — replacing the scattered 2-faction hardcodes,
so that every checksum, slot loop, and new subsystem iterates factions one correct way (never a bare `FACTION_COUNT`), with a 3–4-faction golden scenario proving the span path.

> **This is migration Step 5 of the strangler — the C7 keystone, and the source of truth that 1.3b and Epic 5's 5.1 build on.** Story 1.2 pinned today's `SimChecksum` sequence behind a golden harness. This story introduces the registry and re-points the checksum's faction loop at it **without changing a single checksum byte at N≤2** — the 1.2 golden must stay green and un-re-baselined. The *widening* of `SimChecksum` (Crystal/Supply, algo-version bump, re-baseline) is the very next story (1.3b) and is explicitly NOT this one.

## Acceptance Criteria

1. **(Registry exists, sim-layer, with the canonical constants + cast)** **Given** a net-new `FactionRegistry` in `src/Core` **When** it is inspected and unit-tested **Then** it contains no `using Godot` / no Godot Node type, and exposes `PLAYER_COUNT = 8` (playable, excl. Neutral), `FACTION_ARRAY_SIZE = 9` (incl. Neutral slot 0), `ToFaction(int slot)` returning `(Faction)(slot + 1)` as the **single** cast site, and an ascending `ActiveFactions` list — and a bare `FACTION_COUNT` literal is never introduced in any new slot loop.

2. **(Checksum routes through the registry — byte-identical at N=2, NO re-baseline)** **Given** `SimChecksum.Compute` today hashing `Ore[Player1]`/`Ore[Player2]` via faction literals **When** its faction-resource section is rewritten to iterate `registry.ActiveFactions` ascending (still Ore-only — coverage unchanged) and the registry is threaded through `SimulationLoop.EnableChecksums` and its two `Compute` call sites, MainScene, and the 1.2 harness **Then** the committed 1.2 golden (`golden-scenario.golden.txt`) passes **unchanged** (no re-record, no `checksum_algo_version` change) and all three 1.2 AC tests stay green — proving no behavior change at N≤2.

3. **(3–4-faction golden scenario exercises the span path)** **Given** a new in-code scenario with 3–4 active factions (distinct per-faction ore) constructed with `new FactionRegistry(activePlayerCount)` **When** the multi-faction golden is recorded twice in one process and re-run in a fresh process **Then** both runs are byte-identical and match a committed multi-faction golden file, **and** a focused test proves the per-faction ore loop genuinely spans the active factions — mutating `Ore[Player3]` changes the checksum under `new FactionRegistry(3)` but leaves it unchanged under `new FactionRegistry(2)` (the 2-active loop never reaches Player3) — **and** a one-tick perturbation of a faction-3 entity's `Fixed` health is detected and located (drift guard works on the multi-faction scenario too).

_Covers: AR-3 (FactionRegistry localizes all faction-count knowledge). Depends on: 1.2 (the golden harness — DONE)._

> Split from former 1.3 (FactionRegistry is a separable concern from the checksum-widening/algo-version work). **Disambiguation (do not confuse):** as-built `FACTION_COUNT=5` (ResourceStore/MatchStats) is *current enum cardinality incl. Neutral* and stays as-is — 9.2 raises it. `PLAYER_COUNT=8`/`FACTION_ARRAY_SIZE=9` are the *forward* constants the registry introduces; new slot loops use them. This story does NOT extend the `Faction` enum, resize any store array, hold `FactionDefinition`s, widen the checksum, or touch `ScenarioDirector` — those are 9.2 / 9.2 / 5.1 / 1.3b / 1.3b respectively.

---

## Developer Context

**You (the dev agent) have ONLY this file. Read this whole section before editing anything.** This story adds **one net-new sim file** (`FactionRegistry.cs`) and makes a **surgical, behavior-preserving** change to the checksum path. The single hardest thing is *discipline*: it is trivially tempting to "finish the job" by extending the enum to Player8, resizing `ResourceStore`/`MatchStats` arrays, widening the hash to Crystal/Supply, or fixing `ScenarioDirector`'s float leak — **every one of those is a different, later story** (9.2 / 9.2 / 1.3b / 1.3b). If you do any of them here you break the clean split and the 1.2 golden re-baseline contract. Stay in the lane defined below.

### The shape of the work (1 new sim file + ~4 surgical edits + 1 new test scenario/golden + tests)

1. **Create `godot/src/Core/FactionRegistry.cs`** — pure C#, Godot-free: `PLAYER_COUNT=8`, `FACTION_ARRAY_SIZE=9`, static `ToFaction(slot)`, instance `ActiveFactions` (ascending, set from an `activePlayerCount` ctor).
2. **Route `SimChecksum.Compute` through the registry** — add a `FactionRegistry` parameter; replace the two `Ore[Player1]`/`Ore[Player2]` literal lines with a `foreach (Faction f in factions.ActiveFactions)` Ore-only loop. **Coverage unchanged → byte-identical at N=2.**
3. **Thread the registry** through `SimulationLoop` (`EnableChecksums` gains a `FactionRegistry` param; both `Compute` call sites pass it), `MainScene.cs:268` (`new FactionRegistry(2)` + TODO(5.1)), and the 1.2 harness `GoldenScenario.cs:119`.
4. **Generalize the golden engine** (`GoldenChecksumReplay`) with optional `build`/`fileName` params so a second scenario can reuse it **without breaking the 1.2 call sites**.
5. **Build the multi-faction (recommend 4-faction) scenario** + record/commit its golden (`golden-multifaction.golden.txt`).
6. **Tests:** registry unit tests (constants/cast/ascending), the registry-count `Compute` span proof, the multi-faction golden (twice-in-process + committed + located-drift). **Verify the 1.2 golden is UNCHANGED.**
7. **Verify:** `dotnet test` all green (the 20 existing 1.2/1.1 tests + new ones), `dotnet build godot/godot.csproj` green.

### THE key design decision: an *active-faction list*, not a `0..PLAYER_COUNT` loop

**Decision:** the registry iterates the **active** factions of *this* match (an ascending `Faction[]` set at construction), NOT a bare `for (slot = 0; slot < PLAYER_COUNT; slot++)`.

- **Why not `0..PLAYER_COUNT`:** the architecture's illustrative checksum loop (game-architecture.md ~line 1930) iterates `0..FactionRegistry.PLAYER_COUNT` — but that is the **post-9.2 end state**, after the enum reaches `Player8` and the store arrays are sized to `FACTION_ARRAY_SIZE=9`. **Today the enum stops at `Player4` and `ResourceStore.Ore` is `Fixed[5]`.** A `0..8` loop would index `Ore[8]` → **IndexOutOfRange**. The epic deliberately specifies "iterate **active** factions" for exactly this reason.
- **Why active-list is also *better*:** it never hashes empty/unused slots, so the checksum is tight, and it carries forward unchanged through 1.3b (which adds Crystal/Supply *inside* the same `foreach`) and 9.2 (which just lets `activePlayerCount` go up to 8). One API, three stories.
- **Byte-identical proof:** for `new FactionRegistry(2)` → `ActiveFactions = [Player1, Player2]`, the loop does `Mix(Ore[1])` then `Mix(Ore[2])` — the *exact* two operations, same order, the code does today. The 1.2 golden does not move.
- **1.0 assumes contiguous player slots** `Player1..PlayerN` (faction == player slot; matches 9.2's "Faction==player for 1.0"). A count-based ctor is correct and sufficient. (5.1 will later populate active factions from the loaded per-slot `FactionDefinition`s; leave a one-line `// TODO(5.1)` where MainScene passes the count.)

### Pre-flight facts you MUST NOT re-derive (verified against the codebase at this commit)

- **The `Faction` enum is `Neutral=0, Player1=1, Player2=2, Player3=3, Player4=4`** (`EntityWorld.cs:47-54`) — 5 values. **Do NOT add Player5–8 (that is 9.2).** A 3–4-faction scenario uses the existing `Player1..Player4`, which fit the size-5 arrays with zero resizing.
- **`SimChecksum.Compute(EntityWorld, BuildingStore, ResourceStore)`** is the only checksum entry point (`SimChecksum.cs:26`, FNV-1a 32-bit, Godot-free). Its faction section is exactly two literal lines (`SimChecksum.cs:53-54`): `Ore[(int)Faction.Player1]`, `Ore[(int)Faction.Player2]`. The entity loop (`:32-40`, all alive entities ascending) and building loop (`:44-49`) are **faction-agnostic — leave them alone.** Only the two faction-resource lines change.
- **`SimChecksum.Compute` is called in exactly two places**, both inside `SimulationLoop`: `StepOnce()` (`:98`) and `Update(float)` (`:135`). No server/multiplayer checksum collector exists yet (that's 1.9a/9.1). So the blast radius of the signature change is fully contained in `SimulationLoop` + its `EnableChecksums` callers.
- **`SimulationLoop.EnableChecksums(BuildingStore, ResourceStore)`** (`:74`) stashes the two stores in private fields (`:59-60`) used by both `Compute` calls. **Add a `FactionRegistry` field + param here** and pass it to both `Compute` calls. `EnableChecksums` has exactly **two callers**: `MainScene.cs:268` and `GoldenScenario.cs:119`.
- **`MainScene.cs:268`** is `_simLoop.EnableChecksums(_buildings, _resources);`. Change to `_simLoop.EnableChecksums(_buildings, _resources, new FactionRegistry(2));` with a `// TODO(5.1): derive active player count from loaded scenario slots`. The game is 2-player today, so `2` is behavior-preserving. **MainScene is Godot-coupled and otherwise out of scope — change ONLY this line.**
- **`MainScene.cs:242` is `new FactionDefinition[5]` (`_slotFactionDefs`).** This is a real faction-count hardcode, **but migrating it is Story 5.1's job** (epics.md:1488 names it explicitly: "extracts MainScene's hardcoded P1/P2_FACTION_JSON + size-5 `_slotFactionDefs` array into the registry"). **Do NOT touch `_slotFactionDefs`, `_factionDef`, faction-JSON loading, or `StartPositionBridge` in this story.**
- **`ResourceStore.FACTION_COUNT = 5`** (`ResourceStore.cs:9`) and **`MatchStats.FACTION_COUNT = 5`** (`MatchStats.cs:14`) are **array-sizing** consts == current enum cardinality. The disambiguation note blesses them: they stay at 5; **9.2 raises them**. Do NOT make them reference `FACTION_ARRAY_SIZE` (=9) — that would resize arrays 5→9, which is 9.2's audited change, not yours.
- **`ResourceStore.Ore`/`Crystal`/`SupplyUsed`/`SupplyCap`/`FactionBase` are `[5]`-sized, indexed by `(int)Faction`** (slot 0 = Neutral, 1 = Player1 …). A `FactionRegistry(4)` scenario uses indices 1–4 — in bounds.
- **The 1.2 golden engine is reusable but currently single-scenario.** `GoldenChecksumReplay.RunAndRecord` hard-calls `GoldenScenario.Build()` (`GoldenChecksumReplay.cs:53`); `LoadGolden`/`MaybeRecord`/`GoldenSourcePath` hard-use `GoldenFileName` (`:28,104,173,186`). Generalize via **optional params with defaults** so every existing 1.2 call site keeps compiling untouched (see Task 3).
- **`GoldenScenario.PopulateScenario` already creates P1 (worker id 0 + melee + ranged + node + CC + barracks + 200 ore) and P2 (3 fodder, 0 ore).** Your multi-faction scenario is a **separate** builder (`MultiFactionScenario.Build`) — do NOT modify `GoldenScenario` beyond the one `EnableChecksums` line; the 1.2 golden depends on it byte-for-byte.
- **`AiOpponentSystem` plays `Player2` only** (`AI_FACTION` const). In the multi-faction scenario, P3/P4 have no AI — they sit inert (their ore is constant but **is hashed**, which is the point). Keep P2 quiet exactly as 1.2 does (no production building, 0 ore, <5 units).

---

## Tasks / Subtasks

- [x] **Task 1 — Create `FactionRegistry` (the source of truth) (AC: 1)**
  - [x] Create `godot/src/Core/FactionRegistry.cs`, namespace `ProjectChimera.Core`, `#nullable enable`. Pure C#: **no `using Godot`**, no Node types. (It compiles into both `godot.csproj` and the Tier-1 test project via the existing `src/Core/**` glob — no csproj edit needed.)
  - [x] Constants: `public const int PLAYER_COUNT = 8;` (playable, excl. Neutral) and `public const int FACTION_ARRAY_SIZE = 9;` (incl. Neutral slot 0). Document each with the disambiguation vs as-built `FACTION_COUNT=5`.
  - [x] The **single** cast site: `public static Faction ToFaction(int slot) => (Faction)(slot + 1);` — `slot` is 0-based player index (slot 0 → `Player1`). XML-doc that this is the ONE place the `+1` offset lives.
  - [x] Instance state: a ctor `public FactionRegistry(int activePlayerCount)` that builds `_activeFactions` = `[Player1 .. Player_{activePlayerCount}]` ascending as a `Faction[]` (use `ToFaction(i)` for `i in 0..activePlayerCount`). Validate `activePlayerCount` is in `[1, PLAYER_COUNT]` (throw `ArgumentOutOfRangeException` otherwise). Expose `public IReadOnlyList<Faction> ActiveFactions => _activeFactions;` and `public int ActiveCount => _activeFactions.Length;`.
  - [x] **Determinism:** `_activeFactions` is a plain `Faction[]` built in ascending order. Do **not** use `HashSet`/`Dictionary` (unordered enumeration is banned in sim). `foreach`/index over the array is the deterministic contract.
  - [x] Leave a `// TODO(5.1): hold per-slot FactionDefinition[] and derive ActiveFactions from assigned slots` note where 5.1 will extend this.

- [x] **Task 2 — Route `SimChecksum` through the registry, Ore-only (byte-identical at N=2) (AC: 1, 2)**
  - [x] `SimChecksum.cs`: change the signature to `Compute(EntityWorld world, BuildingStore buildings, ResourceStore resources, FactionRegistry factions)`. Leave the entity loop and building loop **untouched**.
  - [x] Replace the two literal lines (`:53-54`) with:
    ```csharp
    // ── Faction resources (active factions, ascending slot order, via the registry) ──
    // Ore-only today — Story 1.3b widens this same loop to Crystal/SupplyUsed/SupplyCap and bumps the algo version.
    foreach (Faction f in factions.ActiveFactions)
        hash = Mix(hash, resources.Ore[(int)f].Raw);
    ```
    Update the class XML-doc's "Hashed state" list to say "ResourceStore: Ore for each **active** faction (via FactionRegistry)". Do **not** add Crystal/Supply (that is 1.3b).
  - [x] `SimulationLoop.cs`: add `private FactionRegistry? _checksumFactions;`. Change `EnableChecksums(BuildingStore, ResourceStore)` → `EnableChecksums(BuildingStore buildings, ResourceStore resources, FactionRegistry factions)`; store `factions`. In **both** `Compute` call sites (`StepOnce:98`, `Update:135`) pass `_checksumFactions` (add `&& _checksumFactions != null` to the existing null guards).
  - [x] `MainScene.cs:268`: `_simLoop.EnableChecksums(_buildings, _resources, new FactionRegistry(2));` + the `// TODO(5.1)` comment. **Nothing else in MainScene.**
  - [x] `GoldenScenario.cs:119`: `loop.EnableChecksums(buildings, resources, new FactionRegistry(2));` (the 1.2 scenario is P1+P2 → 2 active). This is the ONLY change to `GoldenScenario.cs`.
  - [x] Build both: `dotnet build godot/godot.csproj` and `dotnet build godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` → green.

- [x] **Task 3 — Generalize the golden engine WITHOUT breaking the 1.2 call sites (AC: 3)**
  - [x] `GoldenChecksumReplay.cs`: give `RunAndRecord` an optional builder: `RunAndRecord(int ticks, Action<int, EntityWorld>? perturb = null, Func<GoldenHarness>? build = null)` → `build ??= GoldenScenario.Build;` then `GoldenHarness harness = build();`. All existing 1.2 calls (which omit `build`) keep working unchanged.
  - [x] Add an optional `fileName` to `LoadGolden(string fileName = GoldenFileName)`, `MaybeRecord(IReadOnlyList<Sample> seq, string fileName = GoldenFileName)`, and `GoldenSourcePath(string fileName = GoldenFileName, [CallerFilePath] string thisFilePath = "")`. Use the param in the manifest-resource match (`n.EndsWith(fileName, …)`) and the source-path combine. Existing callers pass nothing → identical behavior.
  - [x] Confirm the 1.2 tests still compile and pass **before** moving on (run `dotnet test --filter FullyQualifiedName~Golden`).

- [x] **Task 4 — Build the multi-faction scenario + record/commit its golden (AC: 3)**
  - [x] Create `godot/ProjectChimera.Sim.Tests/Golden/MultiFactionScenario.cs`: a `Build()` returning a `GoldenHarness`, replicating the same 9-system loop as `GoldenScenario` but with **4 active factions** (recommend 4 to span the full current enum P1–P4):
    - **P1:** worker (id 0, the perturb-able gatherer pattern) + a melee unit + resource node + `FactionBase` + `AddOre(Player1, 200)` — drives evolution.
    - **P2:** 2–3 fodder, **0 ore, no production building** (AI stays quiet — same recipe as 1.2).
    - **P3:** 1–2 **inert** units at x ≥ 40 (well clear of the P1↔P2 combat zone, ≈ x∈[-14,11]) + a **distinct** `AddOre(Player3, 150)` (no node/base near them → ore stays constant but is hashed every tick). Make the **P3 perturb target a gatherer** (`GatherState.Idle`, like 1.2's worker) so `CombatSystem` never touches its health and the AC3 `+1` persists.
    - **P4:** 1 inert unit at x ≥ 50 + a **distinct** `AddOre(Player4, 75)`.
    - **Inert-unit caveat (prevents a flaky located-drift test):** a freshly-`Create`d combat unit defaults to `UnitCommand.Idle`, which *chases the nearest enemy globally* — it will NOT stay put, and a P3 unit drifting into combat would overwrite the perturb target's health. Keep P3/P4 truly inert by either (a) making them gatherers with no reachable node (the proven 1.2 mechanism — `CombatSystem` skips gatherers), or (b) setting their unit-command SoA to `Stop`/`HoldPosition` AND positioning them far enough that no enemy ever enters range. **Verify by running:** the multi-faction golden must be *dynamic* (P1 evolves) yet the P3 perturb target's health *stable* before perturbation. If the golden won't stabilize, an inert unit is drifting into combat — fix positioning/command, never the golden.
    - Expose a `PerturbTargetId` for the **P3 gatherer**; create it at a fixed, first-in-its-scenario id and assert the id invariant in `Build()` (mirror `GoldenScenario`'s id-stability guard).
    - Wire `loop.EnableChecksums(buildings, resources, new FactionRegistry(4));` and `ChecksumInterval = 1`; `director.LoadScenario(new ScenarioData())`.
    - Author **all values with `Fixed.FromInt`/`Fixed.FromRaw` — no `Fixed.FromFloat`**, no `System.Random`, no `Dictionary`/`HashSet` enumeration.
  - [x] Add the new golden as an embedded resource: in `ProjectChimera.Sim.Tests.csproj`, alongside the existing pair, add `<None Remove="Golden\golden-multifaction.golden.txt" />` and `<EmbeddedResource Include="Golden\golden-multifaction.golden.txt" />`.
  - [x] Record it: run the multi-faction recorder in record mode (`CHIMERA_GOLDEN_RECORD=1`, passing `build: MultiFactionScenario.Build` and `fileName: "golden-multifaction.golden.txt"`), confirm ≥300 samples, **inspect that early≠late hashes** (P1 evolution makes it dynamic), then `dotnet build` (refresh the embedded copy) and commit the golden. Reuse the 1.2 re-baseline safety gate (record twice + round-trip `ParseGolden(FormatGolden(seq))` before writing).

- [x] **Task 5 — The tests (AC: 1, 2, 3)**
  - [x] `Golden/FactionRegistryTests.cs` (registry unit tests, AC1): `PLAYER_COUNT == 8`; `FACTION_ARRAY_SIZE == 9`; `ToFaction(0) == Faction.Player1`, `ToFaction(3) == Faction.Player4`; `new FactionRegistry(4).ActiveFactions` equals `[Player1,Player2,Player3,Player4]` in that order; `ActiveCount == 4`; out-of-range `activePlayerCount` (0, 9) throws. Assert no-`using Godot` is structural (the Godot-free boundary test from 1.1 already proves the whole sim source — no extra work, just don't add Godot here).
  - [x] **Span proof (AC3 core)** in `FactionRegistryTests.cs` — prove the ore loop reads *exactly* the active factions, with no tautology: build a tiny world + `ResourceStore`; set `Ore[Player3]` to value A and compute `h3a = Compute(w,b,r,new FactionRegistry(3))` and `h2a = Compute(w,b,r,new FactionRegistry(2))`; change `Ore[Player3]` to value B and recompute `h3b`, `h2b`. Assert `h3a != h3b` (Player3's ore **value** IS hashed when 3 are active) **and** `h2a == h2b` (the 2-active loop never reads Player3). Together these pin that the registry's active span controls exactly which factions' ore enters the hash. **Do NOT** instead assert `Compute(…,(2)) != Compute(…,(3))` — that differs merely from one extra `Mix` call even when `Ore[P3]==0`, so it would NOT prove Player3's value is read (the 1.2 "no tautological assert" lesson).
  - [x] `Golden/MultiFactionGoldenTests.cs` (AC3): mirror the 1.2 AC pattern against the multi-faction golden — (a) `RecordMultiFactionBaseline` (record-mode writer with the same twice-run + round-trip safety gate, skipped-in-normal-mode assertions evolve/sample-count); (b) twice-in-process identical AND match committed `golden-multifaction.golden.txt`; (c) cross-process = fresh run vs committed golden; (d) located-drift: perturb the **P3** entity's `Fixed` health at loop index K=100 → divergence located at tick K+1, expected≠actual. Reuse `RunAndRecord(…, build: MultiFactionScenario.Build)` and `LoadGolden("golden-multifaction.golden.txt")`. Add the same `DefaultTicks > k+1` precondition assert.
  - [x] All new Golden tests must `return` early in `IsRecordMode` (same guard as 1.2) so a re-baseline run doesn't fail against a stale embedded copy.

- [x] **Task 6 — Verify end-to-end + the 1.2-golden-unchanged gate (AC: 2)**
  - [x] **The byte-identicality gate (AC2 — do not skip):** run the full suite. The **existing `golden-scenario.golden.txt` must pass with ZERO changes** — `RunsTwiceInProcess_BothMatchGoldenAndEachOther`, `MatchesGolden_RecordedInSeparateProcess`, `OneTickPerturbation_IsDetectedAndLocated` all green, and `git status` shows `golden-scenario.golden.txt` **unmodified**. If the 1.2 golden diverges, your registry iteration is NOT byte-identical → fix the registry/loop; **never re-record the 1.2 golden in this story** (that is 1.3b, and it bumps the algo version).
  - [x] `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` → all green: the 20 prior tests (6×1.1 + 4 Golden + 10 boundary) + the new registry/span/multi-faction tests, headless, in seconds.
  - [x] Negative control: corrupt one line of `golden-multifaction.golden.txt`, rebuild, run → it goes **red with a located-tick message**; restore (byte-identical) → green. (Proves the new guard guards.)
  - [x] `dotnet build godot/godot.csproj` → green (the only production edits are `FactionRegistry.cs`, the `SimChecksum`/`SimulationLoop` signature thread-through, and MainScene's one line).

---

## Dev Notes

### Reference snippets (copy/adapt — verified against current code)

**`FactionRegistry.cs` skeleton (Task 1):**
```csharp
#nullable enable
using System.Collections.Generic;

namespace ProjectChimera.Core
{
    /// <summary>
    /// Single source of truth for faction-count / slot knowledge in the sim layer.
    /// Pure C# — no Godot. Owns the forward N-player constants and the one (Faction)(slot+1) cast.
    /// </summary>
    public sealed class FactionRegistry
    {
        /// <summary>Playable factions, excluding Neutral. Forward target (ship ceiling 4-now / 8-fast-follow).</summary>
        public const int PLAYER_COUNT = 8;

        /// <summary>Per-faction array size incl. Neutral (slot 0). Forward target.
        /// NOTE: distinct from the as-built ResourceStore/MatchStats FACTION_COUNT=5 (current enum cardinality,
        /// raised by Story 9.2). New slot loops use PLAYER_COUNT / ActiveFactions — never a bare FACTION_COUNT.</summary>
        public const int FACTION_ARRAY_SIZE = 9;

        /// <summary>The ONE place the (Faction)(slot+1) offset lives. slot is 0-based; slot 0 → Player1.</summary>
        public static Faction ToFaction(int slot) => (Faction)(slot + 1);

        private readonly Faction[] _activeFactions; // ascending, deterministic iteration

        /// <summary>Active factions = Player1..Player{activePlayerCount}, ascending (1.0: contiguous slots).</summary>
        public FactionRegistry(int activePlayerCount)
        {
            if (activePlayerCount < 1 || activePlayerCount > PLAYER_COUNT)
                throw new System.ArgumentOutOfRangeException(nameof(activePlayerCount));
            _activeFactions = new Faction[activePlayerCount];
            for (int i = 0; i < activePlayerCount; i++)
                _activeFactions[i] = ToFaction(i);
            // TODO(5.1): hold per-slot FactionDefinition[] and derive ActiveFactions from assigned slots.
        }

        public IReadOnlyList<Faction> ActiveFactions => _activeFactions;
        public int ActiveCount => _activeFactions.Length;
    }
}
```

**Why the 1.2 golden cannot move (Task 6 gate):** `new FactionRegistry(2).ActiveFactions` is `[Player1, Player2]`. The new `foreach` does `Mix(Ore[1])` then `Mix(Ore[2])` — identical operations/order/values to the deleted `Ore[Player1]`/`Ore[Player2]` lines. Same FNV input stream ⇒ same hash bytes ⇒ the committed golden matches with no re-record.

**Re-baseline command (for the NEW multi-faction golden only — never the 1.2 one here):**
```
# PowerShell:
$env:CHIMERA_GOLDEN_RECORD=1; dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~MultiFaction; Remove-Item Env:\CHIMERA_GOLDEN_RECORD
# then: dotnet build (refreshes the embedded copy) ; git add golden-multifaction.golden.txt ; commit
```

### Constraints & gotchas

- **`dotnet build`/`dotnet test` are authoritative** for C# correctness — the editor's MCP `run` does not rebuild the test assembly. Build/test before declaring done. [Source: LEARNINGS.md:122; 1.1/1.2 Dev Notes]
- **Scope fence — do NOT, in this story:** extend the `Faction` enum (9.2); resize `ResourceStore`/`MatchStats` arrays or change their `FACTION_COUNT=5` (9.2); widen `SimChecksum` to Crystal/Supply or bump `checksum_algo_version` or re-baseline the 1.2 golden (1.3b); touch `ScenarioDirector` (float→Fixed threshold = 1.3b; `slot<2`/`1-a.Faction` loop = 9.2); migrate `MainScene._slotFactionDefs[5]`, faction-JSON loading, or `StartPositionBridge` (5.1); add `FactionDefinition` holding to the registry (5.1). [Source: epics.md Stories 1.3b/5.1/9.2; game-architecture.md Step 5]
- **Determinism still binds your test setup:** author scenarios with `Fixed` (`FromInt`/`FromRaw`, never `FromFloat`), no `System.Random`/`DateTime`, no `Dictionary`/`HashSet` enumeration. The registry's `_activeFactions` is an ascending array on purpose. [Source: project-context.md "Determinism"]
- **Sim/Presentation boundary:** `FactionRegistry` lives in `src/Core` (sim) and is Godot-free. The 1.1 `GodotFreeBoundaryTest` will fail the build if a `using Godot` sneaks in — that is your free compile-time guard. [Source: project-context.md]
- **No new dependencies, nothing added to `godot.sln`, no `Multiplayer/` pulled in.** Reuse the 1.1 xUnit stack. [Source: 1.1/1.2 Dev Notes]
- **Existing slot+1 / faction-count sites you will SEE but must NOT migrate here** (they belong to later stories — listed so you don't think you missed them): `MainScene.cs:242` (`new FactionDefinition[5]`), `:504/:520/:610/:1022` (`(Faction)(slot+1)` / `FactionBase` in `ApplyScenario`) → 5.1/1.8b; `EntityPlacer.cs:581` (`(int)_startSlot + 1`, presentation) → 9.2; `GatheringSystem.cs:137,173` (`FactionBase[(int)faction]`, direct index, fine as-is); `ScenarioDirector` victory/threshold loops → 1.3b/9.2.

### Project Structure Notes

- New production file: **`godot/src/Core/FactionRegistry.cs`** (sim layer; auto-globbed into both projects — no csproj edit). [Source: game-architecture.md:1612 homes `FactionSlots.cs`/`FactionRegistry.cs`; the epic folds them into a single `FactionRegistry` — one file is correct here.]
- All 1.3a **test** code stays in the existing **`Golden/`** folder (1.2's, which this story extends): `MultiFactionScenario.cs`, `MultiFactionGoldenTests.cs`, `FactionRegistryTests.cs`, `golden-multifaction.golden.txt`. Do **not** create `Validation/`, `Builder/`, `Checksum/`, `Bootstrap/` (those are 1.7/1.8b/1.3b/1.8c). [Source: 1.2 Project Structure Notes]
- **Naming note:** the architecture sketches a `FactionSlots.ToFaction` + separate `FactionRegistry`; the epics (1.3a, 5.1) consolidate both into **one `FactionRegistry`** with a static `ToFaction`. Follow the epic — single file, `FactionRegistry.ToFaction`. [Source: epics.md:533, 1473]

### Project Context Rules

_Extracted from `_bmad-output/project-context.md` — these govern every edit here:_

- **`FactionRegistry` localizes faction counts** — `PLAYER_COUNT=8` playable, `FACTION_ARRAY_SIZE=9` incl. Neutral; **never a bare `FACTION_COUNT` in new loops.** This story is the canonical implementation of that rule. [Source: project-context.md "Forward Architecture Rules"]
- **`SimChecksum` must (eventually) cover all active factions** — this story re-points its faction loop at the registry (mechanism); 1.3b does the actual widening. Keep it Ore-only here. [Source: project-context.md "Peer agreement…over the whole model"]
- **Simulation/Presentation boundary is sacred; determinism breaks MP silently if violated** — `Fixed` only, ascending iteration, no wall-clock/unseeded-RNG/unordered-enumeration. [Source: project-context.md]
- **SoA + reuse existing systems** — you construct the existing `EntityWorld`/`BuildingStore`/`ResourceStore`/9 systems for the new scenario; introduce no parallel types. [Source: project-context.md "Data layout"]
- **Engine/runtime:** Godot 4.6.3 target, .NET 8 (`net8.0`); test-only xUnit deps stay in `ProjectChimera.Sim.Tests.csproj`; assembly/namespace `ProjectChimera.*`; project files `godot.csproj`/`godot.sln`. [Source: project-context.md "Technology Stack"]

### References

- [Source: epics.md#Story-1.3a (lines 530-542)] — story statement, the compound AC, "iterate active factions ascending via the registry / never a bare FACTION_COUNT / 3–4-faction golden exercises the span", the FACTION_COUNT-vs-PLAYER_COUNT disambiguation, and the split rationale from former 1.3.
- [Source: epics.md#Story-1.3b (544-560)] — the SimChecksum widening + `checksum_algo_version` bump + 1.2 re-baseline + ScenarioDirector float fix that this story deliberately does NOT do (`foreach` loop carries forward into 1.3b).
- [Source: epics.md#Story-5.1 (1470-1488)] — the later FactionRegistry extension that holds per-slot `FactionDefinition`s and migrates MainScene's `_slotFactionDefs[5]`/JSON loading. Your registry must be cleanly extensible for it (TODO(5.1) markers).
- [Source: epics.md#Story-9.2 (2200-2216)] — the enum→Player8 + array-resize + exhaustive `(int)Faction` audit + ScenarioDirector loop work this story stops short of.
- [Source: game-architecture.md C7/Step-5 (lines 1545-1556, 1670-1671, 1710-1711)] — AR-3: "`FactionRegistry` localizes all faction-count knowledge"; "introduce `FactionRegistry.FactionForSlot` + `FACTION_COUNT`; size arrays off it; **no behavioral change at N≤2; golden checksum unchanged; Ship**"; "Faction-count knowledge lives only in FactionSlots/FactionRegistry … no `slot+1`, no `[5]`/`[2]` sizing elsewhere"; the illustrative `0..PLAYER_COUNT` checksum loop (≈1930) is the **post-9.2** end state (today: active-list, arrays are `[5]`).
- [Source: godot/src/Core/SimChecksum.cs:26,53-54] — `Compute` signature + the two faction-resource literal lines you replace; entity/building loops to leave alone.
- [Source: godot/src/Core/SimulationLoop.cs:59-60,74-78,95-100,132-137] — checksum store fields, `EnableChecksums`, the two `Compute` call sites + null guards to thread the registry through.
- [Source: godot/src/Core/MainScene.cs:242,268] — `:242` `_slotFactionDefs[5]` (5.1, do not touch); `:268` the one `EnableChecksums` line you change.
- [Source: godot/src/Core/EntityWorld.cs:47-54] — `Faction { Neutral=0, Player1=1 … Player4=4 }` (stops at Player4; do not extend).
- [Source: godot/src/Core/ResourceStore.cs:9,12-13,27,31-35] / MatchStats.cs:14] — `FACTION_COUNT=5` array sizing (stays; 9.2 raises); `Ore`/`Crystal`/… `[5]` indexed by `(int)Faction`.
- [Source: godot/ProjectChimera.Sim.Tests/Golden/GoldenChecksumReplay.cs:51-53,104,173,186] — `RunAndRecord`/`LoadGolden`/`MaybeRecord`/`GoldenSourcePath` to generalize with optional `build`/`fileName` params (defaults preserve 1.2 call sites).
- [Source: godot/ProjectChimera.Sim.Tests/Golden/GoldenScenario.cs:119 + GoldenChecksumReplayTests.cs] — the 1.2 harness (`EnableChecksums` line to update) and the AC-test pattern to mirror for the multi-faction golden.
- [Source: godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj:30-36] — the `<None Remove>` + `<EmbeddedResource Include>` golden pattern to duplicate for `golden-multifaction.golden.txt`.

### Latest tech information

- **No new dependencies.** Reuse the 1.1 xUnit stack already pinned in the csproj (`xunit` 2.9.2, `Microsoft.NET.Test.Sdk` 17.11.1, `xunit.runner.visualstudio` 2.8.2). The registry + multi-faction harness need only `System.*` + the sim source.
- **Golden-file portability (1.10c):** the new `golden-multifaction.golden.txt` rides the same `<EmbeddedResource>` + manifest-stream + `\n`-split/`\r`-trim path as the 1.2 golden — portable across the Windows↔Linux (WSL) gate. The checksum *values* must be byte-identical across platforms (the determinism proof); only the file transport is newline-tolerant.

### Previous Story Intelligence

From **Story 1.2** (done, code-review ACCEPTED 2026-06-22):

- The golden engine (`GoldenChecksumReplay`) and scenario (`GoldenScenario`) are clean and reusable — this story is their **first reuse**, exactly as 1.2 intended ("Stories 1.3b/1.4/1.5 reuse this directly"). Generalize via optional params; do not rewrite.
- `GoldenScenario.PerturbTargetId == 0` (the worker), and `Build()` asserts the worker is created first so the id is stable. **Mirror that id-stability guard** in `MultiFactionScenario` for its P3 perturb target.
- The 1.2 re-baseline path early-returns all AC tests in `IsRecordMode` and the recorder runs a **twice-run + `ParseGolden(FormatGolden(seq))` round-trip safety gate before writing** (a 1.2 review patch). Reuse this exact gate in `RecordMultiFactionBaseline` — never commit a golden a second run can't reproduce.
- The 1.2 review's **"no tautological assert"** lesson applies to your span proof: assert `Compute(…,FactionRegistry(2)) != Compute(…,FactionRegistry(3))` over a state with `Ore[P3]!=0` — a real behavioral signal, not an expression that reduces to itself.
- The 1.2 review **deferred a "deep-liveness self-check needing a harness API change" to 1.3b** — you do NOT need it here; the registry-count `Compute` comparison is the lighter, sufficient span proof for 1.3a.
- Pre-existing **CS8632** nullable warnings exist in `GatheringSystem.cs`/`SimulationLoop.cs`/`FlowFieldSystem.cs` (identical in game + test builds) — not your bug; don't "fix" them.
- Git history is `[AutoSave]`-only (hourly autocommit to `master`). The working tree at story start has the 1.2 review patches applied to `FixedBoundaryTests.cs` and `GoldenChecksumReplayTests.cs` — that is expected 1.2 finalization, not your concern; your edits sit alongside.

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (Claude Opus 4.8) — gds-dev-story workflow.

### Debug Log References

- **AC2 byte-identicality gate:** after threading `FactionRegistry` through `SimChecksum`'s faction loop, the full pre-existing suite stayed **20/20 green** and `git diff --stat -- godot/ProjectChimera.Sim.Tests/Golden/golden-scenario.golden.txt` was **empty** → the 1.2 golden reproduces byte-for-byte with the registry-based `foreach (Faction f in factions.ActiveFactions)` Ore loop. No re-baseline, no `checksum_algo_version` change. `new FactionRegistry(2).ActiveFactions = [Player1, Player2]` ⇒ the loop does `Mix(Ore[1])` then `Mix(Ore[2])` — the exact two operations the deleted literals did.
- **Multi-faction golden record (record mode, `CHIMERA_GOLDEN_RECORD=1`, filter `~MultiFaction`):** 300 samples, dynamic (tick 1 `E353C32B` ≠ tick 300 `D2FCEE92`); twice-run + `ParseGolden(FormatGolden(seq))` round-trip safety gate passed before writing.
- **Negative control:** corrupting tick 150 of `golden-multifaction.golden.txt` →
  `Checksum drift at tick 150: expected 0xDEADBEEF, actual 0x1AF2752C` (2 tests red, located tick exact);
  restoring the golden → **35/35 green** (note: a `Copy-Item` restore preserves the old timestamp, so an MSBuild touch/rebuild is needed to re-embed — content was already correct).
- **Builds:** `dotnet build godot/ProjectChimera.Sim.Tests` and `dotnet build godot/godot.csproj` both **0 errors**.
- **Final suite:** **35/35 green** (20 prior: 6×1.1 + 4 Golden + 10 boundary; + 11 registry/span cases; + 4 multi-faction golden), headless, ~120 ms.

### Completion Notes List

- **AC1** — `godot/src/Core/FactionRegistry.cs` (sim-layer, Godot-free): `PLAYER_COUNT=8`, `FACTION_ARRAY_SIZE=9`, the single `ToFaction(slot) => (Faction)(slot+1)` cast, and an ascending `Faction[] ActiveFactions` built from an `activePlayerCount` ctor (validated `[1, PLAYER_COUNT]`, throws `ArgumentOutOfRangeException`). Plain array — no `HashSet`/`Dictionary`. `TODO(5.1)` marker left for the per-slot `FactionDefinition[]` extension. No bare `FACTION_COUNT` introduced in any new loop.
- **AC2** — `SimChecksum.Compute` gained a `FactionRegistry factions` param; the two `Ore[Player1]`/`Ore[Player2]` literals became one ascending `foreach` over `factions.ActiveFactions` (still Ore-only — coverage unchanged). Threaded through `SimulationLoop` (`_checksumFactions` field + `EnableChecksums` param + both `Compute` call sites with `&& _checksumFactions != null` guards), `MainScene.cs` (`new FactionRegistry(2)` + `TODO(5.1)`), and `GoldenScenario.cs` (`new FactionRegistry(2)`). 1.2 golden untouched.
- **AC3** — `MultiFactionScenario` runs the same 9-system loop with `new FactionRegistry(4)`; distinct per-faction ore (P1=200 evolving, P3=150, P4=75, P2=0). `MultiFactionGoldenTests` proves twice-in-process + committed-golden + cross-process reproduction + located drift on a **faction-3** entity at tick K+1. `FactionRegistryTests` adds the **non-tautological span proof**: changing `Ore[Player3]` moves the hash under `FactionRegistry(3)` but **not** under `FactionRegistry(2)`.
- **Design decision — inert P3/P4 (story's option b, chosen over the gatherer suggestion):** the P3 perturb target (id 0, created first + asserted) and the P4 unit are **non-combatants** (`AttackDamage == 0` → `CombatSystem` skips them at the non-combatant guard), `GatherState.Inactive` (GatheringSystem skips), with **no `Moving` flag** and isolated far placement (x≥40). With no neighbour inside MovementSystem's 2.0 separation radius, their steering force is zero → they never move; and unit speed 3 u/s over a 300-tick (10 s) run caps travel at ~30 u (gap ≈49 u), so even a post-fight global chase can't reach them within the run. Their health is therefore provably stable → the AC3 `+1` persists and locates at exactly K+1. Chosen over a `GatherState.Idle` gatherer because `GatheringSystem.FindBestNode` searches **globally** (no faction/radius), so a gatherer would path to P1's shared node and wander into combat (the story's flagged flaky-test trap).
- **Scope fence held:** did NOT extend the `Faction` enum (still P1–P4), resize `ResourceStore`/`MatchStats` arrays or touch `FACTION_COUNT=5`, widen the checksum to Crystal/Supply, bump `checksum_algo_version`, re-baseline the 1.2 golden, touch `ScenarioDirector`, or migrate `MainScene._slotFactionDefs[5]`/faction-JSON/`StartPositionBridge` (those are 1.3b / 9.2 / 5.1). MainScene changed by exactly one logical line.
- **Cosmetic note (deliberate, in scope):** the shared `FormatGolden` writes a "Story 1.2 / GoldenScenario.Build()" header comment into `golden-multifaction.golden.txt`. `FormatGolden` is intentionally NOT in Task 3's generalize-list, and `ParseGolden` skips `#` lines, so this is inert; the filename disambiguates. Left as-is for scope discipline.
- **Pre-existing CS8632** nullable warnings in `SimulationLoop.cs`/`GatheringSystem.cs`/`FlowFieldSystem.cs` are not this story's bug; the one new field (`FactionRegistry? _checksumFactions`) matches its two sibling fields exactly (per "read like the surrounding code") — left consistent, not "fixed".
- **Godot side-effect:** the editor auto-generated `.cs.uid` sidecars for the new/touched scripts (e.g. `FactionRegistry.cs.uid`); these are Godot resource-UID artifacts, not hand-authored — swept up by the repo's autosave.

### File List

**Production (sim / game — compiled into both `godot.csproj` and the Tier-1 test project):**
- `godot/src/Core/FactionRegistry.cs` — NEW (the source of truth).
- `godot/src/Core/SimChecksum.cs` — MODIFIED (`Compute` signature + ascending active-faction Ore loop + XML doc).
- `godot/src/Core/SimulationLoop.cs` — MODIFIED (`_checksumFactions` field + `EnableChecksums` param + both `Compute` call sites/guards).
- `godot/src/Core/MainScene.cs` — MODIFIED (one `EnableChecksums(..., new FactionRegistry(2))` line + `TODO(5.1)`).

**Test (`ProjectChimera.Sim.Tests`):**
- `godot/ProjectChimera.Sim.Tests/Golden/GoldenChecksumReplay.cs` — MODIFIED (optional `build`/`fileName` params; 1.2 call sites unchanged).
- `godot/ProjectChimera.Sim.Tests/Golden/GoldenScenario.cs` — MODIFIED (one `EnableChecksums` line → `new FactionRegistry(2)`).
- `godot/ProjectChimera.Sim.Tests/Golden/MultiFactionScenario.cs` — NEW (4-active-faction scenario).
- `godot/ProjectChimera.Sim.Tests/Golden/FactionRegistryTests.cs` — NEW (constants/cast/ascending/bounds + non-tautological span proof).
- `godot/ProjectChimera.Sim.Tests/Golden/MultiFactionGoldenTests.cs` — NEW (record + twice-in-process + cross-process + located drift on faction-3).
- `godot/ProjectChimera.Sim.Tests/Golden/golden-multifaction.golden.txt` — NEW (recorded baseline, 300 samples).
- `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` — MODIFIED (embedded-resource entry for the new golden).

**Tracking:**
- `_bmad-output/implementation-artifacts/sprint-status.yaml` — story status `ready-for-dev → in-progress → review`.

## Change Log

| Date       | Change                                                                                                  |
|------------|---------------------------------------------------------------------------------------------------------|
| 2026-06-22 | Implemented Story 1.3a: added `FactionRegistry` (sim source of truth: `PLAYER_COUNT=8`, `FACTION_ARRAY_SIZE=9`, single `ToFaction` cast, ascending `ActiveFactions`); re-pointed `SimChecksum`'s faction loop at the registry (Ore-only, byte-identical at N=2 — 1.2 golden unchanged); generalized the golden engine with optional `build`/`fileName`; added the 4-faction `MultiFactionScenario` + recorded golden + registry/span/golden tests. 35/35 Tier-1 tests green; `godot.csproj` builds clean. Status → review. |
