---
baseline_commit: abe4936
---

# Story 1.3b: Generalize SimChecksum coverage + coverage-guard + ScenarioDirector locale fix

Status: ready-for-dev

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a solo developer closing the #1 latent desync hole,
I want `SimChecksum` widened from Ore-only to **every per-faction store** (Ore + Crystal + SupplyUsed + SupplyCap + FactionBase) for all active factions in ascending slot order — a one-time `checksum_algo_version` bump with both goldens re-baselined, a guard test that FAILS (naming the array) if a new per-faction array is added without coverage, and the `ScenarioDirector` float/locale threshold leak converted to Fixed-vs-Fixed + InvariantCulture,
so that the checksum reflects full sim truth, desync detection cannot silently miss divergence, and no float or locale-dependent formatting can enter the trigger sim path.

> **This is migration Step 2 of the strangler — the determinism-coverage keystone.** Story 1.3a re-pointed `SimChecksum`'s faction loop at the `FactionRegistry` (Ore-only, byte-identical, no algo bump) and added the 4-faction golden. **This story widens that same `foreach` to all per-faction arrays, bumps `checksum_algo_version` exactly once, and re-baselines BOTH goldens (1.2's and 1.3a's) to v2.** It also adds the `SimChecksumCoverageGuardTest` (so future stores can't silently escape the hash) and fixes the `ScenarioDirector` `ToFloat()/ToString("F2")` threshold leak (AR-16's locale round-trip). The widening is the meaningful change; the `ScenarioDirector` fix is checksum-neutral (it is proven by dedicated tests, not the golden — see AC3).

## Acceptance Criteria

1. **(SimChecksum covers all per-faction stores, ascending, with a single algo-version bump + both goldens re-baselined)** **Given** the post-1.3a Ore-only active-faction loop in `SimChecksum.Compute` (`SimChecksum.cs:59-60`) **When** it is widened to hash `Ore`, `Crystal`, `SupplyUsed`, `SupplyCap`, and `FactionBase` for every `FactionRegistry.ActiveFactions` entry in ascending slot order, a `checksum_algo_version` constant is introduced and set to **2** (v1 = the implicit pre-widening Ore-only hash), and **both** committed goldens (`golden-scenario.golden.txt` and `golden-multifaction.golden.txt`) are re-recorded to the new algorithm **Then** both goldens move to new values, each self-stamps `checksum_algo_version: 2` in its header, all 1.2/1.3a golden AC tests pass against the re-baselined files, and the re-baseline is a single intentional event (the algo version is bumped exactly once). A known-fixed-state guard test pins the v2 algorithm to a committed expected hash.

2. **(Coverage guard fails — naming the array — when a per-faction store escapes the hash)** **Given** a new `SimChecksumCoverageGuardTest` that reflects over `ResourceStore`'s public per-faction array fields and differential-mutates each active-faction slot **When** any such array is NOT folded into `SimChecksum` (mutating it leaves the checksum unchanged) **Then** the guard FAILS with a message naming the uncovered field (`ResourceStore.<Name>`). With all five arrays covered the guard passes; temporarily removing any one array's `Mix` line turns the guard red naming exactly that array (negative control).

3. **(ScenarioDirector threshold path is Fixed-vs-Fixed + InvariantCulture, no behavior change)** **Given** the `ScenarioDirector` `resource_threshold` emit/match round-trip (`:168` `ore.ToFloat()`, `:170` `ore.ToString("F2")`, `:252` `float.TryParse` + `Compare(float…)`) and the `resource_comparison` condition (`:289` `…ToFloat()` + `Compare(float…)`) **When** they are converted to carry the ore as its raw `Fixed` integer (InvariantCulture) and compare **Fixed-vs-Fixed** **Then** (a) both re-baselined goldens are **byte-identical** before vs after the `ScenarioDirector` change (no regression — the goldens run empty triggers so `Tick` early-returns), (b) a focused functional test proves a `resource_threshold` trigger fires/does-not-fire correctly across boundary cases on the new Fixed path, and (c) the same trigger scenario fires identically under a comma-decimal culture (`de-DE`), guarding against asymmetric culture handling. No `float` arithmetic or culture-dependent number formatting remains in the threshold/condition sim path (the `slot < 2` loop bound and the `add_resources` `Fixed.FromFloat` are deliberately left for Stories 9.2 / 1.4 — see scope fence).

_Covers: FR-39, FR-44, AR-15 (generalized SimChecksum + coverage guard), AR-16 (locale-leak — the first of its three nondeterminisms; the other two land in 1.4). Depends on: 1.3a (the FactionRegistry active-faction loop — DONE)._

> Split from former 1.3. Brownfield fix to `SimChecksum.cs` (widens the `:59-60` loop) and `ScenarioDirector.cs` (`:165-172` emit + `:252`/`:289` match/condition). Bumps `checksum_algo_version` once (its own re-baseline step). 9.1 is the multiplayer twin (server-side collector) and depends on this coverage; 9.2 later widens the `slot < 2` loop to all factions.

---

## Developer Context

**You (the dev agent) have ONLY this file. Read this whole section before editing anything.** This story is a **determinism-coverage** change with three independent pieces that share one re-baseline: (1) widen the checksum, (2) add a coverage guard, (3) kill the `ScenarioDirector` float/locale leak. The hardest things are **discipline** (do NOT widen the `Faction` enum, resize arrays, or widen `slot < 2` — those are 9.2; do NOT touch `Fixed.FromFloat` in `add_resources` — that's 1.4) and **honest testing** (the goldens do NOT exercise `ScenarioDirector`, so the golden alone does NOT prove the locale fix — you must write the functional + culture tests in AC3, and they must not be tautological).

### The shape of the work (3 surgical production edits + 1 const + 2 golden re-bakes + ~3 new tests)

1. **Widen `SimChecksum.Compute`** (`SimChecksum.cs:56-60`): replace the Ore-only `foreach` with a per-active-faction block hashing `Ore`, `Crystal`, `SupplyUsed`, `SupplyCap`, and `FactionBase` (ascending). Add `public const int AlgoVersion = 2;` + doc. Update the class XML "Hashed state" list.
2. **Stamp the algo version into golden headers** (`GoldenChecksumReplay.cs` `FormatGolden`): add one `# checksum_algo_version: {SimChecksum.AlgoVersion}` line. One-line change; `ParseGolden` already skips `#` lines, so it is informational/self-identifying only.
3. **Re-baseline BOTH goldens to v2** (`CHIMERA_GOLDEN_RECORD=1`): `golden-scenario.golden.txt` (1.2 / `GoldenScenario`) AND `golden-multifaction.golden.txt` (1.3a / `MultiFactionScenario`). Reuse each scenario's existing record-mode test with the twice-run + `ParseGolden(FormatGolden(seq))` round-trip safety gate. `dotnet build` to refresh embedded copies, then commit both.
4. **Add `SimChecksumCoverageGuardTest.cs`** (AC2): reflection + differential mutation over `ResourceStore`'s per-faction arrays.
5. **Add the v2 known-state hash guard** (AC1): a unit test pinning `Compute` over a fixed hand-built world to a committed expected hash (this is the "9.1-style" guard that a future algo change must intentionally update).
6. **Fix `ScenarioDirector`** (AC3): Fixed-vs-Fixed + InvariantCulture for the `resource_threshold` emit/match and the `resource_comparison` condition; add a `Compare(Fixed,Fixed,string)` overload. Add `using System.Globalization;`. Keep `slot < 2`. Then verify both goldens are byte-identical (no regression), and write the functional + culture tests.
7. **Verify:** `dotnet test` all green (the 35 existing 1.1/1.2/1.3a tests re-baselined + the new ones), `dotnet build godot/godot.csproj` green, both goldens show `checksum_algo_version: 2`.

### Key design decisions (do not re-derive these — they are settled here)

**D1 — Hash all FIVE per-faction `ResourceStore` arrays, including `FactionBase`.** AC1 names "Ore, Crystal, SupplyUsed, SupplyCap **and every per-faction store**." `FactionBase` (`FixedVec3[]`) is a per-faction store **read in-tick** by `GatheringSystem` (workers path to it to deposit), so a peer divergence there would desync — it belongs in the hash. Including it also makes the AC2 coverage guard exception-free (every public per-faction array is covered; no allow-list needed). It is constant within a match, so it adds constant bytes — harmless, absorbed by the re-baseline.

**D2 — `MatchStats` is deliberately EXCLUDED** (do not hash it, do not put it in the guard). Its per-faction arrays (`_kills`/`_losses`/`_unitsBuilt`/`_oreMined`, `MatchStats.cs:17-26`) are **private**, write-only stats **derived from** already-hashed entity deaths, read only by getters, and never branch the tick — observational scoreboard data, analogous to the hash-excluded `CombatFeedbackProfile` (AR-29). The reflection guard scans **public** fields, so it will not see `MatchStats` regardless; document the exclusion so a future dev does not "helpfully" fold it in.

**D3 — `checksum_algo_version` lives on `SimChecksum` as `const int AlgoVersion = 2`.** No such constant exists today (verified: only architecture docs mention `checksum_algo_version`). v1 is the implicit Ore-only hash used by 1.1/1.2/1.3a. This story introduces the constant **at 2** in one step ("bump exactly once"). It is stamped into each golden header so a baseline self-identifies its algorithm; "old replays under the prior algo version do not spuriously desync" is satisfied because (a) the version is now recorded, and (b) **no checksum-bearing replay exists yet** — `ReplayRecorder` does not store checksums until Story 1.5 (and replay-v2 header gating is 9.11). Do NOT build replay-version machinery here; the const + golden stamp + known-state guard is the whole deliverable.

**D4 — Coverage guard = reflection + differential mutation** (AC2). Reflect `ResourceStore`'s public array fields whose length equals the faction-array size; for each, mutate the **active** faction's slot (type-aware: `Fixed`/`int`/`FixedVec3`) and assert `Compute` changes. A field that does not move the checksum → FAIL naming it. This directly implements "added without being hashed → FAILS, naming the array" and proves *actual* coverage (not a hand-list that drifts). An unhandled element type throws a clear "extend the guard" error (forces a conscious decision when a new array type appears).

**D5 — `ScenarioDirector` fix is Fixed-vs-Fixed + InvariantCulture (Option A — surgical).** Carry the ore as its raw `Fixed` integer string (InvariantCulture — locale-invariant, lossless) instead of `ToFloat().ToString("F2")`; parse it back as an integer (InvariantCulture) → `Fixed.FromRaw`; compare via a new `Compare(Fixed,Fixed,string)` overload. The authored threshold (`def.Amount`/`c.Amount` are `float` from JSON) becomes `Fixed` via `Fixed.FromFloat(constant)` **at the compare site**.
  - This removes the genuine hazards: **float arithmetic in a sim-branching comparison** (cross-architecture nondeterminism) and **culture-dependent number formatting**.
  - The residual `Fixed.FromFloat(def.Amount)` converts an **authored constant** (identical JSON float bits on every peer → identical `Fixed`), so it is **not** a cross-machine desync source. It matches the pre-existing `add_resources` pattern (`:336`) and is removed wholesale by **Story 1.4** when `FixedJsonConverter` makes `TriggerEvent/TriggerCondition.Amount` a `Fixed`. Do NOT introduce a load-time pre-quantization refactor here (that is the stricter "Option B" — see the note in Task 4; it is out of scope unless Alec asks).
  - Mirror the prior float `==`/`!=` tolerance (`MathF.Abs(a-b) < 0.01f`) with a Fixed epsilon (`Fixed.FromRaw(655)` ≈ 0.00999) so existing trigger behavior is preserved exactly.

**D6 — Keep `slot < 2` in the emit loop.** Widening that 2-player bound to all active factions is **Story 9.2** ("the `slot<2`/`1-a.Faction` loop"). 1.3b only de-floats the loop body; it does not change which slots it visits. (Confirmed by the 1.3a scope-fence note: "ScenarioDirector victory/threshold loops → 1.3b/9.2; float→Fixed threshold = 1.3b; slot<2 = 9.2".)

### Pre-flight facts you MUST NOT re-derive (verified against the codebase at commit `abe4936`)

- **`SimChecksum.Compute(EntityWorld, BuildingStore, ResourceStore, FactionRegistry)`** (`SimChecksum.cs:26-27`) is the only entry point (FNV-1a 32-bit, Godot-free, null-guards `factions` at `:31`). The faction section is now the post-1.3a `foreach (Faction f in factions.ActiveFactions) hash = Mix(hash, resources.Ore[(int)f].Raw);` at **`:59-60`** — **this is the only block you widen.** The entity loop (`:36-45`) and building loop (`:48-54`, which already hashes `ConstructionTimer`) are faction-agnostic — **leave them alone.**
- **`Mix(uint, int)`** (`:68`) takes an `int`. So: `Fixed[]` arrays hash via `.Raw`; `int[]` arrays (`SupplyUsed`/`SupplyCap`) hash **directly** (no `.Raw`); `FixedVec3` hashes as three `Mix` calls (`.X.Raw`, `.Y.Raw`, `.Z.Raw`).
- **`ResourceStore` per-faction arrays (all sized `FACTION_COUNT = 5`, `ResourceStore.cs:9`), indexed by `(int)Faction`:** `public readonly Fixed[] Ore` (`:12`), `Fixed[] Crystal` (`:13`), `int[] SupplyUsed` (`:17`), `int[] SupplyCap` (`:19`), `FixedVec3[] FactionBase` (`:27`). All are `public readonly` → the reflection guard sees exactly these five. `FACTION_COUNT` is **private** — get the length via a constructed instance's `Ore.Length`, do not hardcode 5.
- **`SupplySystem.Tick` recomputes `SupplyUsed[f]` every tick** (`SupplySystem.cs:25-32`) and is system #6 in both goldens — so widening to `SupplyUsed` makes the v2 goldens genuinely *move* (the Barracks completes and units spawn during the run). `Crystal` stays 0 in both goldens (no crystal gathering) → hashed as a constant 0, which is fine. This is why the re-baseline is meaningful, not cosmetic.
- **Both goldens load an EMPTY scenario** (`director.LoadScenario(new ScenarioData())` — `GoldenScenario.cs:127`, `MultiFactionScenario.cs:94`), so `_triggers.Length == 0` and **`ScenarioDirector.Tick` early-returns at `ScenarioDirector.cs:96` — `CollectEvents`/the emit loop never runs in the goldens.** Therefore the AC3 `ScenarioDirector` change is checksum-neutral by construction; the goldens stay byte-identical **trivially**, and they CANNOT prove the locale fix. The functional + culture tests (AC3 b/c) are the real proof — do not skip them, and do not pretend the byte-identical golden validates the fix.
- **The `ScenarioDirector` threshold path** to convert: emit loop `:164-172` (`:168` `ore.ToFloat()`, `:170` `ore.ToString("F2")`, `:171` `units.ToString()`); match `EventMatches` `:250-255` (`resource_threshold` `float.TryParse` + `Compare(float…)` and `unit_count_threshold` `int.TryParse` + `Compare(int…)`); condition `EvalCondition` `:288-289` (`resource_comparison` `…ToFloat()` + `Compare(float…)`). The two `Compare` helpers are `:358-367` (`float`, with the `0.01f` epsilon for `==`/`!=`) and `:369-378` (`int`). `FiredEvent` is a private struct (`:382-394`) with a `string? Data` payload — you may keep the string payload (InvariantCulture raw-int) **or** add a numeric field; the string-with-InvariantCulture route is the smaller diff and is recommended.
- **`Fixed` API you will use** (`FixedPoint.cs`): `FromInt` (`:24`), `FromRaw` (`:30`), `ToInt` (`:52`), the `Raw` field, full comparison operators `< > <= >= == !=` (`:82-92`), `Fixed.Abs` (`:102`). `FixedVec3` exposes `.X/.Y/.Z` as `Fixed`.
- **Golden engine** (`GoldenChecksumReplay.cs`): re-baseline via `MaybeRecord` (env-gated, `:195`), `FormatGolden` (`:175`) writes the `#` header + `<tick> <hashHex8>` lines, `ParseGolden` (`:128`) skips `#`/blank lines. `RunAndRecord(ticks, perturb, build)` and `LoadGolden(fileName)` are already parameterized (1.3a) — `MultiFactionScenario` passes `build:` / `fileName:`. The 1.3a multi-faction record-mode test already wires its own `GoldenHeader`; the 1.2 path uses `DefaultHeader`.
- **Scenarios:** `GoldenScenario.Build()` (P1 worker+melee+ranged, P2 fodder, `FactionRegistry(2)`, `DefaultTicks=300`) and `MultiFactionScenario.Build()` (P1–P4, `FactionRegistry(4)`, `DefaultTicks=300`). Both set `ChecksumInterval = 1`. Neither needs ANY change for this story (you re-record their goldens, you do not edit the builders) — except do not be surprised that hashing `FactionBase`/`Supply` shifts every sample.

### Scope fence — do NOT, in this story

- **Do NOT** extend the `Faction` enum past `Player4`, resize `ResourceStore`/`MatchStats` arrays, change `FACTION_COUNT = 5`, or widen `ScenarioDirector`'s `slot < 2` loop — **all Story 9.2.** [Source: epics.md#Story-9.2]
- **Do NOT** touch `add_resources`'s `Fixed.FromFloat(a.Amount)` (`ScenarioDirector.cs:336`), `create_timer`'s `a.TimerSeconds`, or convert `TriggerEvent/TriggerCondition.Amount` to `Fixed` in the model — **Story 1.4 (FixedJsonConverter sweep)** removes all authored-float→Fixed at the parse boundary at once. [Source: epics.md#Story-1.4]
- **Do NOT** fix the other two AR-16 nondeterminisms (the unstable `Array.Sort` at `ScenarioDirector.cs:192`, the `Dictionary`-backed `_timers`/`_variables` at `:33-34`) — **Story 1.4.** [Source: epics.md:196 AR-16]
- **Do NOT** add a server/MP checksum collector, hash `EntityWorld`/`BuildingStore` differently, or fold any net-new store (Modifier/Energy/DslVar/Hero) — those land with their own stores (9.1 / 2.2b / 3.2 / 7.x), each extending the guard you build here.
- **Do NOT** hand-edit a golden file. Re-baseline only via `CHIMERA_GOLDEN_RECORD=1` + rebuild.

---

## Tasks / Subtasks

- [ ] **Task 1 — Widen `SimChecksum` + introduce `AlgoVersion` (AC: 1)**
  - [ ] `SimChecksum.cs`: add `public const int AlgoVersion = 2;` near the FNV constants, XML-documented (v1 = implicit Ore-only hash for Stories 1.1–1.3a; v2 = full per-faction coverage, this story). This is the single `checksum_algo_version` bump.
  - [ ] Replace the `:59-60` Ore-only `foreach` body with the full per-faction block (ascending, via the registry):
    ```csharp
    // ── Faction resources (all per-faction stores, active factions, ascending slot order) ──
    // Story 1.3b widened this from Ore-only to full coverage; checksum_algo_version bumped to 2.
    foreach (Faction f in factions.ActiveFactions)
    {
        int idx = (int)f;
        hash = Mix(hash, resources.Ore[idx].Raw);
        hash = Mix(hash, resources.Crystal[idx].Raw);
        hash = Mix(hash, resources.SupplyUsed[idx]);          // int[] — pass directly
        hash = Mix(hash, resources.SupplyCap[idx]);           // int[]
        hash = Mix(hash, resources.FactionBase[idx].X.Raw);   // read in-tick by GatheringSystem
        hash = Mix(hash, resources.FactionBase[idx].Y.Raw);
        hash = Mix(hash, resources.FactionBase[idx].Z.Raw);
    }
    ```
  - [ ] Update the class XML "Hashed state" list (`:9-14`) to: "ResourceStore: Ore, Crystal, SupplyUsed, SupplyCap, FactionBase for each active faction (via FactionRegistry, ascending)". Note the entity and building loops are unchanged.
  - [ ] `dotnet build godot/godot.csproj` → green. (No call-site changes: the signature is unchanged from 1.3a.)

- [ ] **Task 2 — Stamp the algo version into golden headers + re-baseline BOTH goldens (AC: 1)**
  - [ ] `GoldenChecksumReplay.cs` `FormatGolden` (`:175-187`): add one line after the format line — `sb.Append($"# checksum_algo_version: {ProjectChimera.Core.SimChecksum.AlgoVersion}\n");`. (Informational only; `ParseGolden` skips `#` lines. Both goldens now self-identify as v2 on their next record.)
  - [ ] Re-baseline `golden-scenario.golden.txt` (1.2): `CHIMERA_GOLDEN_RECORD=1`, run the 1.2 record-mode test (filter `~GoldenChecksumReplay`); it must use the existing twice-run + `ParseGolden(FormatGolden(seq))` round-trip safety gate. Confirm 300 samples, early≠late hashes (still dynamic).
  - [ ] Re-baseline `golden-multifaction.golden.txt` (1.3a): `CHIMERA_GOLDEN_RECORD=1`, run the multi-faction record-mode test (filter `~MultiFaction`). Confirm 300 samples, early≠late, distinct from the 1.2 golden.
  - [ ] `dotnet build` (refreshes both embedded copies), then commit BOTH goldens. Verify each file's header now reads `# checksum_algo_version: 2`.
  - [ ] **Sanity:** the v2 goldens MUST differ from the committed v1 values (because Supply/Crystal/FactionBase are now hashed). If a golden is byte-identical to its old value, the widen did not take effect — fix Task 1 before recording. (This is the inverse of 1.3a's "must not move" gate.)

- [ ] **Task 3 — Coverage guard + v2 known-state hash guard (AC: 1, 2)**
  - [ ] Create `godot/ProjectChimera.Sim.Tests/Golden/SimChecksumCoverageGuardTest.cs`. Reflect `ResourceStore`'s public per-faction array fields (length == a constructed instance's `Ore.Length`) and differential-mutate each active slot; FAIL naming any field whose mutation does not change `Compute`. (Skeleton in Dev Notes.) Assert the set is non-empty and that all five known arrays (Ore/Crystal/SupplyUsed/SupplyCap/FactionBase) are covered.
  - [ ] In the same file, add the **v2 known-state hash guard** (AC1): build a tiny fixed world by hand (a couple of entities at fixed `Fixed` positions/health, one building, `ResourceStore` with set Ore/Crystal/Supply/FactionBase for P1/P2), call `SimChecksum.Compute(..., new FactionRegistry(2))`, and assert it equals a committed expected `uint` (record the value once from a green run and paste it in, with a comment that an intentional algo change must update it AND bump `AlgoVersion`). Assert `SimChecksum.AlgoVersion == 2`.
  - [ ] `dotnet test --filter FullyQualifiedName~Coverage` → green.

- [ ] **Task 4 — Fix `ScenarioDirector` threshold/condition float-locale leak (AC: 3)**
  - [ ] `ScenarioDirector.cs`: add `using System.Globalization;`. Add the Fixed `Compare` overload + epsilon (mirrors the `0.01f` tolerance):
    ```csharp
    // ≈ the prior 0.01f float tolerance (0.01 × 65536 ≈ 655 raw) so ==/!= behavior is preserved.
    private static readonly Fixed CompareEpsilon = Fixed.FromRaw(655);
    private static bool Compare(Fixed a, Fixed b, string op) => op switch
    {
        ">"  => a > b,   "<"  => a < b,
        ">=" => a >= b,  "<=" => a <= b,
        "==" => Fixed.Abs(a - b) <  CompareEpsilon,
        "!=" => Fixed.Abs(a - b) >= CompareEpsilon,
        _    => false
    };
    ```
  - [ ] Emit loop (`:165-172`) — keep `slot < 2`; carry the ore as raw Fixed (InvariantCulture), no `ToFloat()/ToString("F2")`:
    ```csharp
    for (int slot = 0; slot < 2; slot++)  // slot<2 stays — widening to all factions is Story 9.2
    {
        var faction = (Faction)(slot + 1);
        int oreRaw  = _resources.Ore[(int)faction].Raw;
        int units   = CountAlive(world, faction);
        events.Add(new FiredEvent("resource_threshold",   slot, oreRaw.ToString(CultureInfo.InvariantCulture)));
        events.Add(new FiredEvent("unit_count_threshold", slot, units.ToString(CultureInfo.InvariantCulture)));
    }
    ```
  - [ ] Match `EventMatches` (`:250-255`) — parse the raw int (InvariantCulture) and compare Fixed-vs-Fixed / int-vs-int:
    ```csharp
    case "resource_threshold":
        if (f.Slot != def.Faction) return false;
        return int.TryParse(f.Data, NumberStyles.Integer, CultureInfo.InvariantCulture, out int oreRaw)
            && Compare(Fixed.FromRaw(oreRaw), Fixed.FromFloat(def.Amount), def.Operator);
    case "unit_count_threshold":
        if (f.Slot != def.Faction) return false;
        return int.TryParse(f.Data, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cnt)
            && Compare(cnt, def.Count, def.Operator);
    ```
  - [ ] Condition `EvalCondition` `resource_comparison` (`:289`) — compare Fixed-vs-Fixed, no `ToFloat()`:
    ```csharp
    case "resource_comparison":
        return Compare(_resources.Ore[(int)faction], Fixed.FromFloat(c.Amount), c.Operator);
    ```
  - [ ] **Scope fence reminder:** do NOT touch `add_resources` `Fixed.FromFloat(a.Amount)` (`:336`), the `Array.Sort` (`:192`), or the `Dictionary` timers/variables — all Story 1.4. Leave `slot < 2` as-is (9.2).
  - [ ] **(Option B — stricter, only if Alec requests it; otherwise skip):** instead of `Fixed.FromFloat(def.Amount)` at the compare site, pre-quantize each trigger's resource thresholds to `Fixed` once in `LoadScenario` (the allowed load boundary) and compare raw ints — fully float-free in the tick. This needs index-threading through `AnyEventMatches`/`AllConditionsMet`; it is a larger, riskier refactor for a marginal gain (the residual `FromFloat` is a constant conversion, cross-platform-deterministic). Default to Option A.
  - [ ] `dotnet build godot/godot.csproj` → green.

- [ ] **Task 5 — AC3 proof tests + golden no-regression (AC: 3)**
  - [ ] **Golden no-regression:** after Task 4, run the full Golden suite — both re-baselined goldens MUST be byte-identical vs their just-committed v2 values (`git status` clean for both `.golden.txt`). They are unaffected because `Tick` early-returns on empty triggers; this proves the `ScenarioDirector` edit changed no hashed sim state.
  - [ ] **Functional correctness** (`Golden/ScenarioDirectorThresholdTests.cs`): build a `ScenarioData` with ONE `resource_threshold` trigger (faction 0, `amount=100`, op `">="`) whose action is observable (recommended: `display_message`, captured via `director.OnDisplayMessage`). Drive a `ScenarioDirector` over a `ResourceStore` with `Ore[Player1]` set to test values; `Tick` once; assert the trigger fired for `Ore=150` and `Ore=100`, did NOT fire for `Ore=50`. Add an `op "<"` case. (This pins the new Fixed compare's boundary behavior.)
  - [ ] **Culture robustness** (same file): wrap the `Ore=150` fire case in `CultureInfo.CurrentCulture = new CultureInfo("de-DE")` (restore in a `finally`); assert it STILL fires. This guards against asymmetric culture handling — e.g. a future revert of the emit to `ToString("F2")` (which yields "150,00" under de-DE) while the match parses with `InvariantCulture` would FAIL this test. Add a comment stating exactly what it does and does not prove (it does NOT, on a single machine, prove cross-architecture float determinism — that is structural, removed by going Fixed; it DOES guard culture-symmetry + correct firing).
  - [ ] `dotnet test --filter FullyQualifiedName~ScenarioDirectorThreshold` → green.

- [ ] **Task 6 — Verify end-to-end + negative controls (AC: 1, 2, 3)**
  - [ ] `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` → ALL green: the 35 prior tests (now asserting against the re-baselined goldens) + the new coverage/known-state/threshold tests, headless, in seconds.
  - [ ] **AC2 negative control:** temporarily comment out one `Mix(...)` line in `SimChecksum`'s faction block (e.g. the `Crystal` line), run `~Coverage` → the guard goes **red naming `ResourceStore.Crystal`**; restore → green. (Proves the guard guards.)
  - [ ] **AC1 negative control:** corrupt one data line of each `.golden.txt`, rebuild, run → each goes red with a located-tick message; restore (re-embed via rebuild) → green.
  - [ ] `dotnet build godot/godot.csproj` → green. Confirm production edits are limited to `SimChecksum.cs` (+const, widened loop), `ScenarioDirector.cs` (Fixed compare + InvariantCulture), and `GoldenChecksumReplay.cs` (one header line). Confirm both goldens' headers read `checksum_algo_version: 2`.

---

## Dev Notes

### Reference snippets (copy/adapt — verified against current code at `abe4936`)

**Coverage guard skeleton (Task 3):**
```csharp
#nullable enable
using System;
using System.Linq;
using System.Reflection;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    public class SimChecksumCoverageGuardTest
    {
        [Fact]
        public void EveryPerFactionResourceArray_IsFoldedIntoTheChecksum()
        {
            var registry  = new FactionRegistry(2);   // P1, P2 active
            var world     = new EntityWorld();         // empty — isolates ResourceStore's contribution
            var buildings = new BuildingStore();
            const int slot = (int)Faction.Player1;     // an active slot the loop reads

            var reference = new ResourceStore(Fixed.Zero);
            int factionLen = reference.Ore.Length;     // == private FACTION_COUNT
            FieldInfo[] perFaction = typeof(ResourceStore)
                .GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => f.FieldType.IsArray)
                .Where(f => ((Array)f.GetValue(reference)!).Length == factionLen)
                .ToArray();

            Assert.NotEmpty(perFaction);

            foreach (FieldInfo field in perFaction)
            {
                var resources = new ResourceStore(Fixed.Zero);
                uint before = SimChecksum.Compute(world, buildings, resources, registry);
                MutateActiveSlot(field, resources, slot);
                uint after  = SimChecksum.Compute(world, buildings, resources, registry);

                Assert.True(before != after,
                    $"Per-faction array ResourceStore.{field.Name} is NOT folded into SimChecksum: " +
                    $"mutating [{(Faction)slot}] left the checksum unchanged. Add it to the active-faction " +
                    $"block in SimChecksum.Compute and bump SimChecksum.AlgoVersion (or document a deliberate exclusion).");
            }
        }

        private static void MutateActiveSlot(FieldInfo field, ResourceStore r, int slot)
        {
            var arr  = (Array)field.GetValue(r)!;
            Type elem = field.FieldType.GetElementType()!;
            if      (elem == typeof(Fixed))     arr.SetValue(Fixed.FromInt(999), slot);
            else if (elem == typeof(int))       arr.SetValue(123456, slot);
            else if (elem == typeof(FixedVec3)) arr.SetValue(new FixedVec3(Fixed.FromInt(7), Fixed.FromInt(8), Fixed.FromInt(9)), slot);
            else throw new NotSupportedException(
                $"Coverage guard cannot mutate ResourceStore.{field.Name} (element {elem.Name}). " +
                $"Extend MutateActiveSlot for this type so its coverage can be proven.");
        }
    }
}
```

**Why the re-baseline is correct and one-time:** the only state-set change is the `SimChecksum` faction block (Task 1). Both goldens move together to v2 in Task 2; thereafter every 1.2/1.3a AC test asserts against the new committed files. `AlgoVersion` is bumped exactly once (introduced at 2). The `ScenarioDirector` change (Task 4) touches no hashed state, so it does not trigger a second re-baseline — verified by the Task 5 byte-identical gate.

**Re-baseline commands (PowerShell):**
```powershell
# 1.2 golden:
$env:CHIMERA_GOLDEN_RECORD=1; dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~GoldenChecksumReplay; Remove-Item Env:\CHIMERA_GOLDEN_RECORD
# 1.3a multi-faction golden:
$env:CHIMERA_GOLDEN_RECORD=1; dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~MultiFaction; Remove-Item Env:\CHIMERA_GOLDEN_RECORD
# then: dotnet build (refresh embedded copies); git add both golden-*.txt; commit
```

### Constraints & gotchas

- **`dotnet build`/`dotnet test` are authoritative** for C# correctness — the Godot MCP `run` does not rebuild the test assembly. Build/test before declaring done. [Source: LEARNINGS.md:122; 1.1/1.2/1.3a Dev Notes]
- **The golden does NOT validate the `ScenarioDirector` fix** — `Tick` early-returns on empty triggers (`:96`), so the emit loop never runs in either golden. The functional + de-DE tests (Task 5) are the only proof; a byte-identical golden here means "no regression," not "fix works." Do not conflate them. [Source: ScenarioDirector.cs:96; GoldenScenario.cs:127]
- **`SupplyUsed`/`SupplyCap` are `int[]`, not `Fixed[]`** — hash them directly via `Mix(hash, arr[idx])`, never `arr[idx].Raw`. [Source: ResourceStore.cs:17,19]
- **Authored thresholds are `float`** (`TriggerEvent.Amount:75`, `TriggerCondition.Amount:109`). The `Fixed.FromFloat` at the compare site is a constant conversion (deterministic across peers) and is the 1.4 cleanup target — do not over-engineer it away in 1.3b. [Source: TriggerDefinition.cs:75,109; epics.md#Story-1.4]
- **Determinism still binds the new tests:** author worlds with `Fixed` (`FromInt`/`FromRaw`, never `FromFloat` in scenario setup), no `System.Random`/`DateTime`, no `Dictionary`/`HashSet` enumeration. [Source: project-context.md "Determinism"]
- **Sim/Presentation boundary:** `SimChecksum`/`ScenarioDirector`/`ResourceStore` are `src/Core` (sim) and Godot-free; the 1.1 `GodotFreeBoundaryTest` fails the build if a `using Godot` sneaks in. [Source: project-context.md]
- **No new dependencies, nothing added to `godot.sln`.** Reuse the 1.1 xUnit stack. The coverage guard uses `System.Reflection` (already referenced via `GoldenChecksumReplay`). [Source: 1.1/1.3a Dev Notes]
- **Pre-existing CS8632** nullable warnings in `SimulationLoop.cs`/`GatheringSystem.cs`/`FlowFieldSystem.cs` are not this story's bug; don't "fix" them. [Source: 1.3a Dev Notes]

### Project Structure Notes

- Production edits (sim layer, auto-globbed into both `godot.csproj` and the Tier-1 test project): `godot/src/Core/SimChecksum.cs`, `godot/src/Core/ScenarioDirector.cs`. Test-engine edit: `godot/ProjectChimera.Sim.Tests/Golden/GoldenChecksumReplay.cs` (one header line).
- New test files in the existing `Golden/` folder: `SimChecksumCoverageGuardTest.cs`, `ScenarioDirectorThresholdTests.cs`. Re-recorded (not new) goldens: `golden-scenario.golden.txt`, `golden-multifaction.golden.txt`. Do NOT create `Validation/`, `Checksum/`, `Bootstrap/` folders (those are 1.7/1.8). [Source: 1.2/1.3a Project Structure Notes]
- No `.csproj` change needed: both goldens are already embedded (1.2/1.3a added them); re-recording overwrites content, the embed entries persist.

### Project Context Rules

_Extracted from `_bmad-output/project-context.md` — these govern every edit here:_

- **`SimChecksum` must cover all active factions + all sim state** — the as-built Ore-only hash is "a bug being fixed"; this story IS that fix. Hash width stays 32-bit (FNV-1a); 64-bit canonical is a later story. [Source: project-context.md "Peer agreement…over the whole model"]
- **`Fixed` end-to-end; never `Fixed.FromFloat` outside the converter/AI quantize** — honored: the only `FromFloat` added converts an authored constant at the compare site (1.4 removes it via `FixedJsonConverter`); the sim *comparison* is Fixed-vs-Fixed/int. Never `float`/`Mathf` for gameplay outcome. [Source: project-context.md "Determinism", "Fixed end-to-end"]
- **Process per-faction state in ascending order** — the widened loop iterates `FactionRegistry.ActiveFactions` (ascending) exactly as 1.3a established; iteration order is part of the deterministic contract. [Source: project-context.md "Determinism"]
- **SoA + reuse existing systems** — you hash the existing `ResourceStore` arrays in place; introduce no parallel types. [Source: project-context.md "Data layout"]
- **`FactionRegistry` localizes faction counts; never a bare `FACTION_COUNT` in new loops** — the widened loop uses `ActiveFactions`, not a `0..FACTION_COUNT` index. [Source: project-context.md "Forward Architecture Rules"; 1.3a]
- **Engine/runtime:** Godot 4.6.3 target, .NET 8 (`net8.0`); assembly/namespace `ProjectChimera.*`; project files `godot.csproj`/`godot.sln`. [Source: project-context.md "Technology Stack"]

### References

- [Source: epics.md#Story-1.3b (lines 544-560)] — story statement; the three ACs (widen to Ore+Crystal+SupplyUsed+SupplyCap + every per-faction store, ascending; `checksum_algo_version` bump exactly once + 1.2 golden re-baseline; `SimChecksumCoverageGuardTest` naming the uncovered array; `ScenarioDirector` `ToFloat()/ToString("F2")` → Fixed.Raw compares, byte-identical golden); the brownfield line/file pointers; the AR-16 "partial (locale leak); other two in 1.4" split.
- [Source: epics.md:196 (AR-15)] — "Generalized SimChecksum: widen … for all active factions in ascending order. Bump checksum_algo_version once (its own re-baseline step). Add a SimChecksumCoverageGuardTest that fails if a new per-faction/per-entity store lacks coverage."
- [Source: epics.md:197 (AR-16)] — "in-tick Fixed→float→string→parse round-trip in ScenarioDirector → Fixed-vs-Fixed + InvariantCulture" (1.3b does this one); the unstable `Array.Sort` and `Dictionary` timers/variables → 1.4.
- [Source: epics.md#Story-1.4 (562-580)] — the `FixedJsonConverter` single-quantization-boundary + the other two AR-16 negative tests this story stops short of; confirms `add_resources`/model-`Amount` `FromFloat` is 1.4's sweep.
- [Source: epics.md#Story-9.1 (2182-2196)] — the MP twin: server-side collector + "a guard unit test asserts a known world state produces a fixed expected hash" (the known-state guard pattern reused in Task 3) + "ConstructionTimer is already hashed (line 48)".
- [Source: epics.md#Story-9.2 (2200-2216)] — the enum→Player8 + array-resize + `(int)Faction` audit + `slot<2`-loop widening this story deliberately leaves; confirms 1.3b owns only the float→Fixed of the same loop.
- [Source: godot/src/Core/SimChecksum.cs:9-14,26-31,56-60,68] — XML "Hashed state" list to update; `Compute` signature + `factions` null-guard; the `:59-60` Ore-only `foreach` to widen; `Mix(uint,int)`.
- [Source: godot/src/Core/ResourceStore.cs:9,12-13,17,19,27] — `FACTION_COUNT=5` (private); the five public per-faction arrays (`Ore`/`Crystal` `Fixed[]`, `SupplyUsed`/`SupplyCap` `int[]`, `FactionBase` `FixedVec3[]`).
- [Source: godot/src/Economy/SupplySystem.cs:25-32] — `SupplyUsed` recomputed each tick (so widening to Supply makes the v2 goldens move).
- [Source: godot/src/Core/MatchStats.cs:14,17-26] — the private per-faction stat arrays deliberately EXCLUDED from the hash and the guard (observational, presentation-domain).
- [Source: godot/src/Core/ScenarioDirector.cs:96,164-172,250-255,288-289,336,358-378,382-394] — `Tick` early-return; the emit loop; the match/condition float compares; `add_resources` `FromFloat` (fence to 1.4); the two `Compare` helpers + the `0.01f` epsilon to mirror; the private `FiredEvent`.
- [Source: godot/src/Core/Definitions/TriggerDefinition.cs:75,83,109,123] — `Amount` is `float`, `Operator` is `string` (why the threshold needs a `Fixed` conversion to compare Fixed-vs-Fixed).
- [Source: godot/src/Core/FixedPoint.cs:24,30,52,82-92,102] — `Fixed` API (`FromInt`/`FromRaw`/`ToInt`/comparison operators/`Abs`) for the widened hash and the Fixed `Compare`.
- [Source: godot/ProjectChimera.Sim.Tests/Golden/GoldenChecksumReplay.cs:128,175-202] — `ParseGolden` skips `#`; `FormatGolden` (add the algo-version header line); `MaybeRecord` re-baseline writer + safety gate.
- [Source: godot/ProjectChimera.Sim.Tests/Golden/GoldenScenario.cs:121,127 + MultiFactionScenario.cs:90,94] — the two scenarios to re-record (no builder edits); both `EnableChecksums(..., FactionRegistry(N))` + empty `LoadScenario` (why `Tick` early-returns in goldens).

### Latest tech information

- **No new dependencies.** Reuse the 1.1 xUnit stack (`xunit` 2.9.2, `Microsoft.NET.Test.Sdk` 17.11.1, `xunit.runner.visualstudio` 2.8.2). The coverage guard needs only `System.Reflection`; the culture test needs `System.Globalization.CultureInfo`.
- **Golden-file portability (1.10c):** the re-recorded goldens ride the same `<EmbeddedResource>` + manifest-stream + `\n`-split/`\r`-trim path; the v2 checksum *values* must be byte-identical across Windows↔Linux (the determinism proof), only the transport is newline-tolerant. The new `# checksum_algo_version: 2` header line is `#`-skipped by `ParseGolden`, so it does not affect portability or parsing.
- **`InvariantCulture` raw-int formatting** (`int.ToString(CultureInfo.InvariantCulture)` / `int.TryParse(…, NumberStyles.Integer, CultureInfo.InvariantCulture, …)`) is the correct .NET idiom for locale-free, lossless integer round-trips — exactly what AR-16 prescribes ("Fixed-vs-Fixed + InvariantCulture").

### Previous Story Intelligence

From **Story 1.3a** (done, code-review ACCEPTED 2026-06-23):

- The widened loop is the **exact** `foreach (Faction f in factions.ActiveFactions)` 1.3a built — you only add `Mix` calls inside it. The registry, signature, and call sites are already correct; **do not** re-thread anything or touch `SimulationLoop`/`MainScene`/`GoldenScenario`/`MultiFactionScenario` builders.
- 1.3a deliberately left `checksum_algo_version` unborn and both goldens at v1 ("the widening … is the very next story (1.3b)"). This story is that step — the goldens are EXPECTED to move now (the inverse of 1.3a's "must not move" gate). The 1.3a Dev Notes' "Why the 1.2 golden cannot move" reasoning no longer applies — you are intentionally re-baselining.
- Reuse the 1.3a record-mode safety gate **verbatim**: twice-run + `ParseGolden(FormatGolden(seq))` round-trip before writing; AC tests early-`return` in `IsRecordMode`. Never commit a golden a second run cannot reproduce.
- 1.3a's review flagged "no tautological asserts." Apply it to AC3: do NOT write a `ScenarioDirector` test that merely re-computes the same float and asserts equality — assert an OBSERVABLE trigger outcome (a captured delegate / a mutated `ResourceStore`) and a real boundary (fires at 150≥100, not at 50≥100), under both invariant and de-DE cultures.
- 1.3a's review **deferred a "deep-liveness self-check needing a harness API change" to 1.3b** — it is NOT required here; the coverage guard (reflection + differential mutation) is the stronger, self-contained proof that supersedes it.
- The 1.3a ctor-range-vs-store-size finding stays deferred to 9.2 (registry accepts 1–8, stores are `[5]`). Irrelevant here: every live caller passes ≤4, and the widened loop reads only `ActiveFactions` (≤4 slots, all in-bounds).
- Git history is `[AutoSave]`-only (hourly autocommit to `master`); your edits sit alongside. `.cs.uid` sidecars may auto-appear for touched scripts (Godot artifact, swept by autosave) — not your concern.

---

## Dev Agent Record

### Agent Model Used

{{agent_model_name_version}}

### Debug Log References

### Completion Notes List

### File List
