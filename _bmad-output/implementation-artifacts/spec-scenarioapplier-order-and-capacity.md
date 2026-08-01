---
title: 'ScenarioApplier deterministic placement order + fail-closed store-capacity caps (DW-37, DW-230)'
type: 'bugfix'
created: '2026-08-01'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
baseline_revision: '612d53e2ae9c184e76e3e90b4c17c85b89093bb2'
final_revision: 'ac8eaf31129d589c6133f9eac357dbf663fdb792'
context: []
warnings: []
---

<intent-contract>

## Intent

**Problem:** (DW-37) `ScenarioApplier` creates placed **items / units / buildings** in `ScenarioData` array order, so runtime refs (packed refs 0,1,2… follow Create order) track array order — while `StartStateHash`/`CanonicalModelHash` canonicalize each collection (sort by a stable key) before folding. Two scenarios with the same set in different array order therefore hash identically yet assign different runtime refs, so a `PickupItem`/inventory ref or a win-condition entity ref could resolve to a different physical entity per peer. (DW-230) A scenario with > 64 resource nodes / > 64 buildings silently overflows: `Nodes.Create`'s `-1` is discarded and `PlaceBuildingDirect(ById)`'s `-1` is assigned unchecked into `buildingSlots`, and the validator has no count cap — so overflow entries vanish with no diagnostic and no gate rejection.

**Approach:** In `ScenarioApplier.Apply`, iterate the Items, Units, and Buildings loops in the **same canonical key order** the hashes sort by, before `Create` — so runtime refs become a deterministic function of the (order-independent) set. Keep `unitEntityIds`/`buildingSlots` **aligned to the authored array index** (WinCon presets reference authored index). Make the applier check the `-1` full-store sentinel from `Nodes.Create` and `PlaceBuildingDirect(ById)` (warn + skip, mirroring the existing item-store guard), and add fail-closed validator count caps for `resource_nodes` and `buildings`.

## Boundaries & Constraints

**Always:**
- Sort keys match the hashes EXACTLY: Items → (`ItemId` ordinal, `Fixed.FromFloat(X).Raw`, `Fixed.FromFloat(Z).Raw`); Units → (`Slot`, `UnitId` ordinal, `X.Raw`, `Z.Raw`); Buildings → (`Slot`, `Type` ordinal, `X.Raw`, `Z.Raw`, `PreBuilt`).
- The order must be a STRICT TOTAL order deterministic across runtimes: break any tie on the authored index (so no reliance on sort stability / platform sort internals).
- `unitEntityIds[ui]` / `buildingSlots[bi]` stay indexed by the AUTHORED array position `ui`/`bi` (Story 7.11 leader_unit_index / structure_index resolve against authored index).
- A scenario already authored in canonical order Creates in byte-identical order (sort is a no-op) — no golden moves for such fixtures.
- Validator caps use the store constants (`ResourceNodeStore.MAX_NODES`, `BuildingStore.MAX_BUILDINGS`); first-fail located error naming the field, count, and cap.

**Block If:**
- A golden moves for a fixture that is NOT explained purely by placement-order canonicalization (i.e. any behavioral regression, not just entity-id reordering of an out-of-canonical-order fixture).

**Never:**
- Do NOT sort the resource-nodes placement loop (out of DW-37 scope — node placement is not ref-addressed by items/win-conditions; nodes stay in authored order).
- Do NOT change any hash algorithm / `AlgoVersion`, wire format, or the `ScenarioData` DTO.
- Do NOT edit the deferred-work ledger.
- Do NOT relax the item-store overflow behavior (already warn+skip).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Canonical-order set | Units/buildings/items authored in canonical order | Create order == authored order (byte-identical) | none |
| Reordered same set | Same set, array shuffled | Identical Create order & runtime refs to the canonical-order build | none |
| Win-cond ref after sort | Assassination `leader_unit_index=k` / Landmark `structure_index=k` with a shuffled Units/Buildings array | Resolves to the AUTHORED index-k entity (not the k-th placed) | none |
| > 64 resource nodes | `resource_nodes.Length == 65` | Validator returns Fail located at `scenario.resource_nodes` naming count 65 & cap 64 | fail-closed at gate |
| > 64 buildings | `buildings.Length == 65` | Validator returns Fail located at `scenario.buildings` naming count 65 & cap 64 | fail-closed at gate |
| Node/building store full (shadow/direct) | Create returns `-1` | Applier warns + skips (node) / warns + records `-1` slot (building, WinCon treats as unresolved) | logged, no crash |

</intent-contract>

## Code Map

- `godot/src/Core/Sim/ScenarioApplier.cs` -- `Apply`: the Items (l.307-323), Units (l.262-302), Buildings (l.233-257) loops + `Nodes.Create` (l.221) call. Add canonical-order index helpers; check the `-1` sentinels.
- `godot/src/Core/Definitions/CanonicalModelHash.cs` -- authority for the Units/Buildings sort keys (l.291-311). Read-only reference.
- `godot/src/Core/Definitions/StartStateHash.cs` -- authority for the Items sort key (l.92-99). Read-only reference.
- `godot/src/Core/Definitions/ScenarioValidator.cs` -- add resource_nodes/buildings count caps (near the null-collection checks, l.141-147).
- `godot/src/Core/ResourceNodeStore.cs` / `godot/src/Economy/BuildingSystem.cs` -- `MAX_NODES`=64 / `PlaceBuildingDirect(ById)` return `-1` when full. Read-only reference.
- `godot/src/Core/WinConditionSystem.cs` -- l.144-164 already guards `buildingSlots[idx] >= 0` / authored-index reads. Read-only; confirms alignment contract.
- `godot/ProjectChimera.Sim.Tests/Builder/ScenarioApplierTests.cs` -- add DW-37 ordering + index-alignment tests.
- `godot/ProjectChimera.Sim.Tests/Validation/ScenarioValidatorCapacityTests.cs` -- NEW: DW-230 cap tests.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Sim/ScenarioApplier.cs` -- Add three private static `CanonicalXOrder(...)` helpers returning an authored-index permutation sorted by the exact hash keys with an authored-index final tiebreaker; iterate Items/Units/Buildings loops over those permutations (bodies unchanged, still writing `unitEntityIds[ui]`/`buildingSlots[bi]` by authored index). Capture `Nodes.Create`'s return and warn+skip on `-1`; capture `PlaceBuildingDirect(ById)`'s return into a local, warn on `-1`, then assign to `buildingSlots[bi]`.
- `godot/src/Core/Definitions/ScenarioValidator.cs` -- After the null-collection checks, add `resource_nodes.Length > ResourceNodeStore.MAX_NODES` and `buildings.Length > BuildingStore.MAX_BUILDINGS` fail-closed caps (located, naming count & cap).
- `godot/ProjectChimera.Sim.Tests/Builder/ScenarioApplierTests.cs` -- Add: (a) shuffled-vs-canonical same-set produces identical placement (entity positions by id / building slots / item refs); (b) canonical-order fixture is a no-op; (c) a shuffled Units array with an Assassination `leader_unit_index` resolves to the authored-index entity.
- `godot/ProjectChimera.Sim.Tests/Validation/ScenarioValidatorCapacityTests.cs` -- NEW: 65 nodes rejected (located), 64 nodes pass; 65 buildings rejected (located), 64 buildings pass.

**Acceptance Criteria:**
- Given two `Validated<ScenarioData>` over the identical item/unit/building SET but different array order, when applied, then the resulting entity positions-by-id, building slots, and item packed refs are identical between the two applies.
- Given a scenario already authored in canonical order (e.g. `GoldenApplierScenario`), when applied, then the Create order is unchanged (the applier-driven golden and all pre-existing goldens stay green with no re-baseline).
- Given a shuffled Units array and an Assassination preset naming `leader_unit_index=k`, when configured, then the leader entity is the one spawned from authored `Units[k]`.
- Given a model with `resource_nodes.Length == 65` (or `buildings.Length == 65`), when validated, then `ValidationResult.Ok == false` with the error naming the collection, the count, and the cap; a length-64 model passes.
- Given the full Tier-1 suite, when run twice, then results are byte-identical (determinism) and green.

## Design Notes

Canonical-order helper shape (no LINQ in Core.Sim; strict total order for cross-runtime determinism):

```csharp
private static int[] CanonicalUnitOrder(ScenarioUnit[] u)
{
    var idx = new int[u.Length];
    for (int i = 0; i < idx.Length; i++) idx[i] = i;
    Array.Sort(idx, (a, b) =>
    {
        int c = u[a].Slot.CompareTo(u[b].Slot);                                    if (c != 0) return c;
        c = string.CompareOrdinal(u[a].UnitId, u[b].UnitId);                        if (c != 0) return c;
        c = Fixed.FromFloat(u[a].X).Raw.CompareTo(Fixed.FromFloat(u[b].X).Raw);     if (c != 0) return c;
        c = Fixed.FromFloat(u[a].Z).Raw.CompareTo(Fixed.FromFloat(u[b].Z).Raw);     if (c != 0) return c;
        return a.CompareTo(b); // authored-index tiebreaker → strict total order (identity when already canonical)
    });
    return idx;
}
```

Items/buildings mirror this with their own keys. Iterate `foreach (int ui in CanonicalUnitOrder(unitsArr))` etc. Because the tiebreaker is the authored index, an already-canonical array yields the identity permutation → byte-identical Create order → no golden move (verified against `GoldenApplierScenario`, which is authored slot/coord-ascending). The validator caps make the applier `-1` guards unreachable on gate-passed paths — they are belt-and-suspenders for shadow/direct callers, exactly like the existing item-store guard framing.

## Verification

**Commands:**
- `dotnet build godot/godot.sln` -- expected: succeeds (analyzer/AOT gate clean; Core.Sim stays Godot-free).
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all green, including every golden (no re-baseline) and the new DW-37/DW-230 tests.
- Re-run the test command once more -- expected: byte-identical results (determinism).

## Spec Change Log

(none — no bad_spec loopback occurred.)

## Review Triage Log

### 2026-08-01 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 1: (high 0, medium 0, low 1)
- defer: 2: (high 0, medium 1, low 1)
- reject: 5: (high 0, medium 0, low 5)
- addressed_findings:
  - `[low]` `[patch]` Verification-gap: the "already-canonical → identity permutation → no golden move" property was pinned only for the units comparator. Added `Apply_CanonicalOrderBuildings_IsANoOp_CreatesInAuthoredOrder_DW37` and `Apply_CanonicalOrderItems_IsANoOp_CreatesInAuthoredOrder_DW37` to lock the buildings + items comparators to their `CanonicalModelHash`/`StartStateHash` keys, so a future key-drift on those two collections fails a unit test rather than only (indirectly) a golden.

Deferred (surfaced here for orchestrator ledger intake — this run must not edit the ledger):
- `[medium]` Resource-node placement + gather assignment are NOT canonicalized. Nodes are placed in authored array order and `GatheringSystem.FindBestNode` (`godot/src/Economy/GatheringSystem.cs:353`) resolves an equidistant tie by lowest node store slot = authored order, while `CanonicalModelHash` sorts nodes order-independently. Two peers loading the same node SET in a different array order (tampered/divergent files that still pass the order-independent handshake) assign nodes to different slots and pick different tie-winners on equidistant workers → SimChecksum desync. The exact DW-37 class for the one placement collection the intent left out of scope. Pre-existing; not touched by this change.
- `[low]` Units placement is the one collection with neither a validator count cap nor an applier `-1` spawn-overflow warn (nodes/buildings/items all have both after this change). A scenario authoring > `EntityWorld.MAX_ENTITIES` (4096) units passes validation, then silently drops the overflow with no diagnostic; if a dropped authored index is an Assassination `leader_unit_index`, its owner loses after grace with no logged cause. Pre-existing fail-closed asymmetry; out of DW-230's node/building scope.

Rejected (recorded for auditability): items validator cap (item overflow already warn+skip-guarded and desync-safe); stateless-cap comment precision (production Apply always runs on a fresh/cleared store); landmark-overflow→owner-loses (pre-existing intentional `-1`-unresolved semantics, deterministic); comparator `Fixed.FromFloat` recompute (load-time, ≤64 elems — negligible); warning `>MAX` wording (kept verbatim-consistent with the pre-existing item-store guard).

## Auto Run Result

Status: done
Blocking condition: none

### Summary
Implemented DW-37 (deterministic placement order) + DW-230 (fail-closed store-capacity caps) in `ScenarioApplier`. `Apply` now Creates items/units/buildings in the same canonical key order the determinism hashes fold by (via authored-index-permutation comparators with an authored-index tiebreaker → strict total order, identity when already canonical), keeping `unitEntityIds`/`buildingSlots` indexed by authored position for Story 7.11 preset resolution. Added applier `-1` full-store-sentinel guards (warn + skip / record-unresolved) and fail-closed validator count caps for `resource_nodes` and `buildings`.

### Files changed
- `godot/src/Core/Sim/ScenarioApplier.cs` — 3 `Canonical{Unit,Building,Item}Order` comparators; Units/Buildings/Items loops iterate the canonical permutation; `Nodes.Create` / `PlaceBuildingDirect(ById)` `-1` returns captured, warned, and (buildings) recorded verbatim.
- `godot/src/Core/Definitions/ScenarioValidator.cs` — fail-closed `resource_nodes`/`buildings` count caps vs `MAX_NODES`/`MAX_BUILDINGS`, located, naming count + cap.
- `godot/ProjectChimera.Sim.Tests/Builder/ScenarioApplierTests.cs` — DW-37 reordered-same-set / canonical no-op (units, buildings, items) / shuffled-units Assassination index-alignment tests; DW-230 store-full warn+skip tests; updated one incidental inline hero-entity-id assertion (warchief now sorts first → id 0).
- `godot/ProjectChimera.Sim.Tests/Validation/ScenarioValidatorCapacityTests.cs` (new) — over-cap rejected / at-cap passes, for nodes and buildings.

### Review findings breakdown
- Patches applied: 1 (low) — buildings/items canonical-order drift-guard tests.
- Deferred: 2 (1 medium: resource-node gather-tie determinism; 1 low: units-overflow fail-closed asymmetry) — surfaced above for orchestrator ledger intake (this run must not edit the ledger).
- Rejected: 5 (all low/noise) — see triage log.

### Follow-up review recommendation
false — this pass patched 1 finding: 0 high, 0 medium, 1 low; score = 3×0 + 1×1 = 1 (< 5), no high.

### Verification performed
- `dotnet build godot/godot.sln` → Build succeeded, 0 warnings, 0 errors (Core.Sim stays Godot-free/LINQ-free).
- `dotnet test …ProjectChimera.Sim.Tests` → 3779 passed, 0 failed, 1 skipped (pre-existing reserved test). Every golden green with NO re-baseline (canonical sort is the identity permutation on all shipped fixtures) — confirming "Golden re-baseline where order changes" was not triggered for any serialized golden.
- Re-run full suite → byte-identical (determinism confirmed).
- Matrix Test Audit: all 7 I/O matrix rows covered by tests that ran and passed.

### Residual risks
- The two deferred items (resource-node gather-tie determinism; units-overflow fail-closed asymmetry) are pre-existing and out of this bundle's intent scope; both are logged above for the orchestrator.
- The applier `-1` guards are unreachable on any gate-passed path (the validator caps reject over-capacity scenarios first); they are belt-and-suspenders for shadow/direct callers, exercised via the store-prefill tests.
