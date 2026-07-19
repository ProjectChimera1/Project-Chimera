---
title: 'Hero-harvest resolver extraction + Tier-1 coverage (DW-27, DW-32)'
type: 'refactor'
created: '2026-07-19'
status: 'done'
review_loop_iteration: 0
baseline_revision: 'ea26505a73481bdf2105f6012c85572e8e34acff'
final_revision: 'ca56d4d5336c04fa249da4da99c9f2e2dc8fda71'
followup_review_recommended: false
context: ['{project-root}/_bmad-output/project-context.md']
warnings: []
---

<intent-contract>

## Intent

**Problem:** The end-of-match harvest of a live hero's `HeroStore.Level`/`Xp`/inventory into the deployed `PlayerProfile` — and the picker Save/Overwrite has-vs-fallback resolution — lives entirely in the Godot-coupled `MainScene.ResetToAuthoredStart` + `HeroPickerOverlay`, outside the Godot-free `ProjectChimera.Sim.Tests`. A wrong-way change (dropping the harvested value and re-persisting the level-1/0 placeholder) would silently regress AC3 with the whole suite green (DW-27), and a fallen hero's manifest-persisted attributes finalizing per FR-7a has no Tier-1 coverage at all (DW-32).

**Approach:** Lift the plain-data harvest logic into a new Godot-free `HeroHarvestResolver` (`src/Core/Definitions/`): `Capture` (live `HeroStore` row → immutable `HeroHarvest`), `ResolveProgress`, and `ResolveInventory` (the has-vs-fallback resolution). Rewire `MainScene`, `SceneContext`, `HeroPickerPhase`, and `HeroPickerOverlay` to delegate to it with NO behavior change, then add `HeroHarvestResolverTests` covering "Has → uses harvested, not the level-1/0 fallback" (DW-27) and a fallen (disabled-revival / awaiting) hero finalizing manifest attributes end-to-end (DW-32).

## Boundaries & Constraints

**Always:** Keep the resolver Godot-free (no `using Godot`, no float/`System.Random`/`DateTime`/`Dictionary`-enumeration — it is sim code under the release analyzer gate). Preserve existing behavior byte-for-byte: the rewired production paths must produce the same `PlayerProfile`/start-state as today. `Capture` keys on the persisted `HeroStore.Alive` row (NOT `Alive3_14`), so a fallen hero stays harvestable. Route all captured level/xp through the manifest-shape `HeroProfileLoader.BuildProfile` seam exactly as the picker does today.

**Block If:** The rewire cannot preserve existing behavior without changing the start-state hash or the persisted profile shape (would need a determinism re-baseline — out of scope here).

**Never:** Do not bump any hash `AlgoVersion` or re-baseline goldens. Do not change what gets persisted, the manifest shape, or the fallback values (Save uses level 1 / 0 xp; Overwrite uses the target's own level/xp). Do not add new SoA fields or touch the death/revival mechanics. Do not edit the deferred-work ledger.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Live hero with progress | `HeroStore` has an `Alive` row whose `Id == MintId(pendingProfile)`, Level 5 / grown Xp | `Capture` → `HeroHarvest{ Has=true, HeroDefId=profile.HeroDefId, Level=5, Xp=grown, Inventory=captured }` | No error |
| No deployed profile | `pendingProfile == null` | `Capture` → `HeroHarvest.None` (`Has=false`) | No error |
| No matching live row | Profile set, but no `Alive` row matches `MintId` | `Capture` → `HeroHarvest.None` | No error |
| Resolve Has, matching def | `harvest.Has`, `HeroDefId=="grommash"`, resolve `"grommash"` w/ fallback (1, 0) | `ResolveProgress` → harvested (5, grown Xp), NOT the fallback | No error |
| Resolve Has, mismatched def | `harvest.Has` for `"grommash"`, resolve `"valla"` | `ResolveProgress` → fallback; `ResolveInventory` → null | No error |
| Resolve None | `harvest.Has == false` | `ResolveProgress` → fallback; `ResolveInventory` → null | No error |
| Fallen hero finalize (FR-7a) | Hero fell (disabled-revival → `Alive3_14=false`, `Alive=true`; or awaiting), Level 5 / grown Xp, id `== MintId(profile)` | `Capture` finds it → `BuildProfile(shape hero.level+hero.xp)` yields a profile carrying the grown Level/Xp, not the authored placeholder | No error |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/HeroHarvestResolver.cs` -- NEW Godot-free resolver: `HeroHarvest` struct + `Capture`/`ResolveProgress`/`ResolveInventory`.
- `godot/src/Core/MainScene.cs` (~1685–1716, 1840, 1853) -- inline harvest capture in `ResetToAuthoredStart`; replace with `HeroHarvestResolver.Capture`, reuse its `Has/Level/Xp/Inventory`.
- `godot/src/Core/Bootstrap/Phases/SceneContext.cs` (~140–146) -- 5 flat harvest fields; replace with one `HeroHarvestResolver.HeroHarvest Harvest`.
- `godot/src/Core/Bootstrap/Phases/HeroPickerPhase.cs` (~36–43) -- provider wiring; expose the live harvest to the picker.
- `godot/src/UI/HeroPickerOverlay.cs` (~385–443) -- `ResolveHeroProgress` + Save/Overwrite inventory sites; delegate has/fallback logic to the resolver.
- `godot/src/Core/Definitions/HeroProfileLoader.cs` -- existing `MintId`/`CaptureInventory`/`BuildProfile` (reused, unchanged).
- `godot/ProjectChimera.Sim.Tests/Definitions/HeroHarvestResolverTests.cs` -- NEW Tier-1 tests.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Definitions/HeroHarvestResolver.cs` -- Create the static resolver. `readonly struct HeroHarvest { bool Has; string HeroDefId; int Level; Fixed Xp; IReadOnlyList<ProfileInventoryItem>? Inventory; static readonly None; }`. `Capture(HeroStore, ItemStore, ItemRegistry, PlayerProfile?)` mirrors `ResetToAuthoredStart` step 1: null/no-match → `None`; else the first `Alive` row whose `Id == HeroProfileLoader.MintId(profile)` → harvest with `CaptureInventory`. `ResolveProgress(in HeroHarvest, string heroDefId, int fallbackLevel, Fixed fallbackXp)` and `ResolveInventory(in HeroHarvest, string heroDefId)` implement the `Has && HeroDefId == heroDefId ? harvested : fallback/null` rule from `HeroPickerOverlay`.
- `godot/src/Core/MainScene.cs` -- In `ResetToAuthoredStart`, replace the inline slot-scan capture with `var harvest = HeroHarvestResolver.Capture(_host.Heroes, _host.Items, _host.ItemRegistry, _ctx.PendingHeroProfile); _ctx.Harvest = harvest;`. Derive `haveSnapshot = harvest.Has && preserveHeroProgress`, `snapLevel = harvest.Level`, `snapXp = harvest.Xp`. Replace the two `_ctx.HarvestedHeroInventory ?? pending.Inventory` sites with `harvest.Inventory ?? pending.Inventory`. Behavior must be identical.
- `godot/src/Core/Bootstrap/Phases/SceneContext.cs` -- Replace the 5 harvest fields with `public Definitions.HeroHarvestResolver.HeroHarvest Harvest = Definitions.HeroHarvestResolver.HeroHarvest.None;` (keep the explaining comment).
- `godot/src/Core/Bootstrap/Phases/HeroPickerPhase.cs` -- Wire the picker to the live harvest: `_ctx.HeroPicker.HarvestProvider = () => _ctx.Harvest;` (replaces the two `HeroProgressProvider`/`HeroInventoryProvider` lambdas).
- `godot/src/UI/HeroPickerOverlay.cs` -- Replace the `HeroProgressProvider`/`HeroInventoryProvider` fields with `public Func<HeroHarvestResolver.HeroHarvest>? HarvestProvider;`. `ResolveHeroProgress` reads `HarvestProvider?.Invoke() ?? HeroHarvest.None` then returns `HeroHarvestResolver.ResolveProgress(...)`. Save uses `HeroHarvestResolver.ResolveInventory(h, unitId)`; Overwrite uses `HeroHarvestResolver.ResolveInventory(h, target.HeroDefId) ?? target.Inventory`. Same semantics as today.
- `godot/ProjectChimera.Sim.Tests/Definitions/HeroHarvestResolverTests.cs` -- Add xUnit tests for every I/O Matrix row (Godot-free). For the fallen-hero row, drive the REAL death path (a minimal `EntityWorld`+`HeroStore`+`HeroXpSystem` fixture as in `HeroRevivalTests`), minting the hero with `Id == MintId(profile)`, killing + ticking so the row lands `Alive=true`/`Alive3_14=false` (disabled) and also cover the enabled-awaiting branch; then assert `Capture`→`ResolveProgress`→`BuildProfile` finalizes the grown Level/Xp.

**Acceptance Criteria:**
- Given the resolver is added, when the C# solution builds, then it compiles with no new warnings and `HeroHarvestResolver.cs` contains no `using Godot`.
- Given a deployed hero grew to level 5 in a playtest, when the picker Save path resolves progress via the resolver with the authored (1, 0) fallback, then the finalized profile carries level 5 and the grown Xp — not the placeholder — proving DW-27's wrong-way regression is now caught.
- Given a hero has fallen (revival disabled, so its `HeroStore` row stays `Alive` while `Alive3_14` is false) or is awaiting revival, when its progress is captured and routed through the manifest-shape `BuildProfile`, then the persisted profile finalizes the manifest-selected attributes at the grown values per FR-7a (DW-32).
- Given the rewire, when the existing hero-persistence/revival tests run, then they still pass and no hash `AlgoVersion` changes (behavior preserved).

## Spec Change Log

_No bad_spec loopbacks — empty._

## Review Triage Log

### 2026-07-19 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 4: (high 0, medium 0, low 4)
- defer: 1
- reject: 3
- addressed_findings:
  - `[low]` `[patch]` Inventory `as List` downcast in `MainScene` was asymmetric with its sibling shape-branch and would silently drop the loadout if `CaptureInventory`'s return type ever widened — typed `HeroHarvest.Inventory` as the concrete `List<ProfileInventoryItem>?` (matching `CaptureInventory`/`PlayerProfile.Inventory`), removed the cast, so both re-mint branches now use identical `harvest.Inventory ?? pending.Inventory`.
  - `[low]` `[patch]` `HarvestProvider` was invoked twice per Save/Overwrite (once for inventory, once inside `ResolveHeroProgress`), risking a read-skew if the closure ever became non-idempotent — `ResolveHeroProgress` now takes the caller's already-captured harvest, one invocation per action.
  - `[low]` `[patch]` Doc/comment accuracy — softened the "byte-for-byte"/"immutable" over-claims, documented the one intentional deviation (the `items`/`reg` null-guard, production-path-equivalent), and fixed the stale "keyed by unit id" comment in `SceneContext` (no per-id map exists anymore).
  - `[low]` `[patch]` `Capture_NoDeployedProfile_ReturnsNone` asserted only `false == false` (tautological) — now asserts the full `None` shape (null HeroDefId, Level 0, Xp 0, null Inventory).
- deferred (recorded here, NOT written to the ledger — the invocation directs the orchestrator to record resolution and forbids ledger edits):
  - The rewired Godot-coupled wiring (`MainScene.ResetToAuthoredStart` capture-stash + preserve-gate + inventory fallback; `HeroPickerPhase`'s `HarvestProvider` closure; `HeroPickerOverlay`'s id/fallback forwarding) is verified only by structural inspection — those `Node` types are excluded from the Tier-1 `ProjectChimera.Sim.Tests` assembly, so a wrong-way wiring change (dropping the `_ctx.Harvest` stash, handing over the wrong closure, forwarding the wrong `heroDefId`) still passes the suite green. This is the same pre-existing Node-seam DW-27/DW-32 named; the extraction narrows it (the plain-data decision is now Tier-1 covered) but does not eliminate it. A Tier-2 GdUnit4 test driving the picker Save/Overwrite would close the residual.
- rejected: Capture key "asymmetry" (matches row by `MintId(ProfileId)`, gates resolve on nullable `HeroDefId`) — behavior-identical to the pre-extraction `HarvestedHeroDefId == heroDefId` gate, and `HeroDefId` is never null for a deployed profile. Unconditional `_ctx.Harvest = harvest` "wiping a prior harvest" — unreachable (each reset re-mints the hero, so the row is live on the next capture) and `None` is more correct than a stale harvest. DW-32 test asserting `Alive3_14`/`AwaitingRevival` — defensible precondition guards.

## Design Notes

The extraction is a pure move-and-delegate: the resolver reproduces exactly two existing decisions — (1) `ResetToAuthoredStart`'s "first `Alive` row whose `Id == MintId(PendingHeroProfile)` → snapshot Level/Xp + `CaptureInventory`", and (2) `HeroPickerOverlay.ResolveHeroProgress`'s "provider Has for this `heroDefId` ? harvested : fallback". `SceneContext.Harvest` replaces the flat `HasHarvestedHeroProgress`/`HarvestedHero*` fields as a single value-typed carrier; the phase hands the picker a `() => _ctx.Harvest` live-read closure (the harvest is captured at return-to-Edit, long after picker init, so the closure — not a snapshot property — is required). `default(HeroHarvest)` has `Has=false`, so an un-harvested picker session falls back exactly as before.

Sketch:
```csharp
public static HeroHarvest Capture(HeroStore heroes, ItemStore items, ItemRegistry reg, PlayerProfile? p) {
    if (p == null || heroes == null) return HeroHarvest.None;
    HeroId target = HeroProfileLoader.MintId(p);
    for (int slot = 0; slot < heroes.Count; slot++) {
        if (!heroes.Alive[slot] || heroes.Id[slot] != target) continue;
        var inv = (items != null && reg != null) ? HeroProfileLoader.CaptureInventory(heroes, items, reg, slot) : null;
        return new HeroHarvest(true, p.HeroDefId, heroes.Level[slot], heroes.Xp[slot], inv);
    }
    return HeroHarvest.None;
}
```

## Verification

**Commands:**
- `dotnet build godot/godot.sln -c Debug` -- expected: build succeeds, no new warnings.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj --filter "FullyQualifiedName~HeroHarvestResolver"` -- expected: all new resolver tests pass.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj --filter "FullyQualifiedName~HeroProfilePersistence|FullyQualifiedName~HeroRevival|FullyQualifiedName~HeroXp"` -- expected: existing hero tests still green (behavior preserved).

## Auto Run Result

Status: done

**Summary:** Lifted the Godot-coupled end-of-match hero harvest (live `HeroStore` Level/Xp/inventory → `PlayerProfile`) and the picker has-vs-fallback resolution into a new Godot-free `HeroHarvestResolver`, then rewired `MainScene`, `SceneContext`, `HeroPickerPhase`, and `HeroPickerOverlay` to delegate to it (behavior-preserving; no hash `AlgoVersion` bump, no golden re-baseline). Added Tier-1 coverage that a Has-progress hero uses the harvested value over the level-1/0 placeholder (DW-27) and that a fallen (disabled-revival / awaiting) hero's manifest-persisted attributes finalize per FR-7a (DW-32).

**Files changed:**
- `godot/src/Core/Definitions/HeroHarvestResolver.cs` (NEW) — Godot-free `HeroHarvest` struct + `Capture`/`ResolveProgress`/`ResolveInventory`; `Capture` keys on the persisted `HeroStore.Alive` row (not `Alive3_14`); `Inventory` typed as the concrete `List<ProfileInventoryItem>?`.
- `godot/src/Core/MainScene.cs` — `ResetToAuthoredStart` step 1 delegates to `HeroHarvestResolver.Capture` and stashes `_ctx.Harvest`; both re-mint branches now use cast-free `harvest.Inventory ?? pending.Inventory`.
- `godot/src/Core/Bootstrap/Phases/SceneContext.cs` — the 5 flat harvest fields replaced by one `HeroHarvestResolver.HeroHarvest Harvest` carrier (default `None`).
- `godot/src/Core/Bootstrap/Phases/HeroPickerPhase.cs` — the two provider lambdas collapsed to one live-read closure `HarvestProvider = () => _ctx.Harvest`.
- `godot/src/UI/HeroPickerOverlay.cs` — `ResolveHeroProgress` + Save/Overwrite delegate to the resolver; the harvest is captured once per action and threaded through both progress and inventory resolution.
- `godot/ProjectChimera.Sim.Tests/Definitions/HeroHarvestResolverTests.cs` (NEW) — 11 Tier-1 tests: full I/O matrix, DW-27 Save-flow (harvested wins) + its non-tautological negative twin, DW-32 fallen-hero `[Theory]` (both revival branches) driving the real death path.

**Review findings:** 4 low-severity patches applied (inventory field typed `List` + cast removed; single `HarvestProvider` invoke; doc/comment accuracy; strengthened a tautological assertion). 1 item deferred (production Godot-coupled wiring verified only structurally — recorded in the triage log, not written to the ledger per the invocation). 3 rejected as noise.

**Verification:**
- `dotnet build godot/godot.sln -c Debug` — 0 errors, 11 pre-existing warnings (none from touched files); resolver contains no `using Godot`.
- Full sim suite `dotnet test ProjectChimera.Sim.Tests` — 2748 passed, 1 pre-existing skip, 0 failed (hero-tests hash-version assertions still green → behavior preserved).

**Residual risks:** The rewired `MainScene`/`HeroPickerPhase`/`HeroPickerOverlay` seams are `Node` types outside the Tier-1 assembly, so a wrong-way *wiring* change (not caught by the now-covered resolver logic) could still ship green — the pre-existing Godot-seam DW-27/DW-32 named, narrowed but not eliminated. `godot-verify` (in-engine) was not run; this is a Godot-free sim + pure-delegation UI refactor fully covered by Tier-1 tests.
