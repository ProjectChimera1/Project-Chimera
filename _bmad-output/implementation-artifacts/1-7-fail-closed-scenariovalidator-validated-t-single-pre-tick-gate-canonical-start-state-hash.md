---
baseline_commit: b42f5a7
---

# Story 1.7: Fail-closed ScenarioValidator + Validated<T> single pre-tick gate (canonical start-state hash)

Status: ready-for-dev

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a solo developer ensuring no unvalidated state can ever reach the tick loop,
I want a `ScenarioValidator` exposing `Validate(model) → Validated<T>` as the single pre-tick gate on all five scenario entry paths (with the `Validated<T>` constructor mintable **only** by the validator), plus a canonical-model start-state hash (FNV-64 over `Fixed.Raw`, fields sorted, annotations excluded) that re-points the match start-state hash away from `ComputeFileHash(path)`,
so that every match starts from validated, hash-agreed truth and AI-generated stale-file desyncs are eliminated.

> **This is migration Step 4 of the determinism strangler (`game-architecture.md` §C2 / D3.1, AR-39 + AR-23 + AR-13).** It is **net-new** (no validator, no `Validated<T>`, no canonical hash exist today) and lands **before** the `SimulationHost`/`ScenarioApplier` extraction (Stories 1.8a/1.8b), so the gate is inserted at the **current** apply boundary in **shadow/log-only mode** — it never halts master and **must not change one tick of sim behavior**. Two facts from the codebase reshape the idealized architecture text and are settled below: (1) **`ScenarioData` is `float`-based today** (no `Fixed` fields, no annotation field), so the canonical hash **quantizes floats to `Fixed.Raw` at hash time** rather than reading stored `Fixed` — converting the model to `Fixed` is deferred D3 work that would risk the golden. (2) There is **exactly one** hash write site (`MainScene.cs:316`); the architecture's "second MP overwrite ~:1761" **does not exist**.

## Acceptance Criteria

1. **(Single mint-restricted gate on every entry path)** **Given** the five scenario entry paths (file-loaded, AI/map-gen-generated, fallback, editor-in-memory, replay-loaded) **When** any path produces a `ScenarioData` that will be applied to the sim **Then** it passes through `ScenarioValidator.Validate(model)`, which on success returns a `Validated<ScenarioData>`, **and** `Validated<T>` cannot be constructed anywhere except inside `ScenarioValidator` (compiler-enforced via the `Proof`-token pattern in D1, backed by a `ValidatedSoleMinterTest`).

2. **(Invalid models are rejected with a located error)** **Given** an invalid model — an **out-of-range / non-finite** value (NaN/±Inf, outside the 16.16 `Fixed` range, a position outside `MapBounds`, a negative ore/supply/rate), a **dangling reference** (a building/unit owned by a `slot` no `PlayerSlot` declares, a duplicate slot, an unknown `Building.Type` name), or a **random effect declared while `SimRng` is unavailable** (the forbidden-until-`SimRng` rule, AR-13 — a forward seam, see D4) — **When** `Validate` runs **Then** it returns `ValidationResult` with `Ok == false` and a **located** error string (naming the offending field path, e.g. `scenario.units[3].slot`), and (in fail-closed mode) the model never reaches `ApplyScenario`.

3. **(Canonical start-state hash replaces the file-byte hash)** **Given** `CanonicalModelHash` (FNV-64 over `Fixed.Raw`, collections sorted, enums folded by **name**, cosmetic annotations excluded, with a `0 → 1` FNV-sentinel) **When** two semantically identical models from different files (different whitespace, key order, `1.0`-vs-`1`, file path) are hashed **Then** their 64-bit hashes are **equal**, a single changed gameplay value yields a **different** hash, and the match start-state hash at `MainScene.cs:316` is computed from the in-memory model via `CanonicalModelHash` (folded to the existing 32-bit wire), **not** `ScenarioSerializer.ComputeFileHash(ScenarioPath)`.

4. **(Shadow/log-only on master; fail-closed is a release-branch toggle)** **Given** the validator wired on master **When** it encounters an invalid model **Then** it runs in **shadow/log-only** mode — the presentation call site logs the located rejection (`GD.PrintErr`) and **still applies** the model, so master never breaks — **and** the fail-closed flip (refuse to apply on any failure) is implemented as a documented toggle (`CHIMERA_VALIDATE_FAILCLOSED` env var, default off) intended to be flipped only on a release branch after a corpus run proves every shipped scenario passes.

5. **(Goldens byte-identical — no sim behavior change)** **Given** the full Tier-1 suite **When** it runs **Then** `golden-scenario.golden.txt` and `golden-multifaction.golden.txt` verify green **unchanged** (`git status` shows no diff). The validator is shadow-only and the canonical hash feeds **only** the lobby handshake value — neither touches sim state. If a golden moves, something leaked into the tick — **fix it; do not re-record.**

_Covers: FR-39 (LAN determinism / desync-free MP — closes the AI-gen stale-file hash hole), FR-44 (deterministic-sim test coverage — negative-validation + canonical-hash tests), AR-39 (single pre-tick `Validate(model)` gate, shadow-on-master / fail-closed-on-release), AR-23 (canonical-model start-state hash, FNV-64 over `Fixed.Raw`, re-points off `ComputeFileHash`), AR-13 (forbidden-until-`SimRng` rule, relocated here from 1.5). Depends on: 1.5 (DONE — `SimRng` exists + folded into `SimChecksum` v3; the validator owns the rule that forbids random effects until it is wired) and 1.6 (DONE — the validator validates content loaded through the data-driven path; `DamageTable` already proven). Independent of 1.8a/1.8b (this story lands first and they consume `Validated<T>` later)._

> Net-new. Inserts the C2 fail-closed `Validate(model)` boundary + the D3 canonical-model hash as **shadow-mode seams** at the existing `MainScene` apply boundary, **before** the sim-spine extraction. Negative-validation + canonical-hash tests added to Tier-1 in a new `Validation/` folder.

---

## Developer Context

**You (the dev agent) have ONLY this file. Read this whole section before editing anything.** The work is **four small net-new types** (`Validated<T>`, `ValidationResult`, `ScenarioValidator`, `CanonicalModelHash`), a **presentation-side gate helper**, a **two-site insertion** in `MainScene` (`ApplyScenario` + `ApplyFallbackScenario`), a **one-line re-point** of the hash at `MainScene.cs:316`, and **new Tier-1 tests**. The validator types live in `src/Core/Definitions` (Godot-free, auto-globbed into Tier-1). The traps are:

1. **Moving a golden.** This story is **shadow-only and hash-only** — it must change **zero** sim ticks (it is the *same* class as Story 1.6: goldens stay byte-identical). The validator only *logs* on master; the canonical hash feeds *only* `_lobbyUi.ScenarioHash` (the lobby handshake), never a sim store. If `golden-scenario.golden.txt` or `golden-multifaction.golden.txt` moves, you leaked something into the tick — **find it and fix it; never set `CHIMERA_GOLDEN_RECORD`.** (AC5)
2. **Minting `Validated<T>` from the wrong place.** The whole point is that a `Validated<T>` is *proof of validation*. If any code outside `ScenarioValidator` can `new Validated<…>(…)`, the type guarantee is dead. Use the **`Proof`-token pattern** (D1) — it is **compiler-enforced** (a nested `Proof` with a private constructor that only the enclosing `ScenarioValidator` can call). Do **not** settle for an `internal` constructor (assembly-wide = anyone can mint).
3. **Hashing floats as text or post-apply state.** The canonical hash must fold **`Fixed.FromFloat(field).Raw`** (the integer the sim will actually use), **never** the raw `float` bits, the JSON text, or post-`ApplyScenario` world state. Folding the quantized `Fixed.Raw` is what makes `"1.0"` and `"1"` hash equal while a real value change diverges. (D5)
4. **Type-enforcing the applier in this story.** `ApplyScenario` still takes a raw `ScenarioData` in 1.7 — `Validate` runs *beside* it (shadow). Making `ApplyScenario`/the applier *require* a `Validated<ScenarioData>` parameter is **Story 1.8b's** job (when `ScenarioApplier` is extracted). 1.7 builds the gate + the type; 1.8b enforces consumption. (D8)
5. **Validating against the wrong slot ceiling.** `FactionRegistry.PLAYER_COUNT == 8` is the *forward* target, but the live `Faction` enum tops out at `Player4 = 4` and the as-built per-faction arrays are sized 5 (`new FactionDefinition[5]`, the verified MainScene.cs:242 overflow). The validator must reject slots that exceed the **live** ceiling, not just the forward one. (D4 slot rule)
6. **Logging from inside the validator.** The validator is in `src/Core/Definitions` — **Godot-free, AOT-eligible** (`GodotFreeBoundaryTest` fails the build on `using Godot`). It **returns** located errors; it never calls `GD.Print`. The `GD.PrintErr` shadow-log happens at the **presentation** call site (`MainScene`). `ILogSink` does not exist yet (Story 1.8a). (D7)
7. **Over-reaching into Epic 9 / Epic 7 / D3 scope.** Do **not** add server-side attestation, quorum, the `hash==0` hard-reject, or widen the wire (Epic 9); do **not** fold triggers/effects into the hash (Epic 7); do **not** build `ContentLoader`/unify `JsonSerializerOptions` or convert `ScenarioData` to `Fixed` (D3, later). See the Scope Fence.

### The shape of the work (4 net-new types + 1 gate helper + 2 MainScene insertions + 1 hash re-point + a new `Validation/` test folder; goldens UNCHANGED)

1. **Net-new `Validated<T>`** (`src/Core/Definitions/Validated.cs`) — a generic `readonly struct` wrapping a `T Value`, with the **`Proof`-token** constructor so only `ScenarioValidator` can mint it (D1).
2. **Net-new `ValidationResult`** (same file or `ValidationResult.cs`) — `readonly struct { bool Ok; string? Error; Validated<ScenarioData> Value; }` with `Pass`/`Fail` factories (D3).
3. **Net-new `ScenarioValidator`** (`src/Core/Definitions/ScenarioValidator.cs`) — `public ValidationResult Validate(ScenarioData model)` running the bounds / reference / slot / building-type / forbidden-until-`SimRng` checks (D4); owns the nested `Proof` (D1). Pure C#, no `using Godot`.
4. **Net-new `CanonicalModelHash`** (`src/Core/Definitions/CanonicalModelHash.cs`) — `static ulong Compute(ScenarioData)` (FNV-64, sorted, quantized, sentinel) + `static uint ToWire(ulong)` (fold to the 32-bit wire) + `const int AlgoVersion = 2` (D5).
5. **Net-new presentation gate helper** — `ScenarioGate.ValidateBeforeApply(ScenarioData, string pathLabel)` (in `src/UI/` or as a private `MainScene` method) that calls `Validate`, applies the shadow/fail-closed policy, and `GD.PrintErr`s located failures (D7).
6. **Insert the gate** at the top of `ApplyScenario` (covers file-loaded, AI-generated, editor-in-memory — they all funnel here) and in `ApplyFallbackScenario` (build a `ScenarioData` mirror to validate+hash; keep the existing hardcoded apply to preserve behavior). Replay is covered transitively (its scenario loads via the file path). (D6/D7)
7. **Re-point `MainScene.cs:316`** from `ScenarioSerializer.ComputeFileHash(GlobalizePath(ScenarioPath))` to `CanonicalModelHash.ToWire(CanonicalModelHash.Compute(model))` over the in-memory applied model (D6).
8. **Tests** — new `ProjectChimera.Sim.Tests/Validation/`: `NegativeValidationTests.cs` (AC2), `CanonicalModelHashTests.cs` (AC3), `ValidatedMintingTests.cs` (AC1 + `ValidatedSoleMinterTest`), `ShadowModeTests.cs` (AC4). The existing golden suite proves AC5 (must stay green, goldens unchanged).

### Key design decisions (settled here — do NOT re-derive)

**D1 — `Validated<T>` is minted only by the validator via the compiler-enforced `Proof`-token pattern.** A nested `Proof` class inside `ScenarioValidator` has a **private** constructor; because C# lets an enclosing type call its nested type's private constructor (and no one else can), only `ScenarioValidator` can produce a `Proof`, and `Validated<T>`'s public constructor *requires* one. This is genuine compile-time enforcement in a single assembly — strictly stronger than an `internal` constructor.
```csharp
// Validated.cs
public readonly struct Validated<T>
{
    public T Value { get; }
    // Mintable ONLY by a validator: requires a Proof, whose ctor is private to ScenarioValidator.
    public Validated(T value, ScenarioValidator.Proof proof) { Value = value; }
}
```
```csharp
// inside ScenarioValidator
public sealed class Proof { private Proof() { } }      // only the enclosing ScenarioValidator can `new Proof()`
private static readonly Proof _proof = new Proof();    // the enclosing type CAN call the private nested ctor
// mint on success:  new Validated<ScenarioData>(model, _proof)
```
A `ValidatedSoleMinterTest` (source/reflection scan asserting no `new Validated<` outside `ScenarioValidator`) is belt-and-suspenders, not the primary guarantee. _(Rejected: `internal` ctor — assembly-wide, not validator-only. Rejected: nesting `Validated<T>` inside the validator — makes `Validated<ScenarioData>` awkward to name from `ScenarioApplier` in 1.8b.)_

**D2 — `Validate` operates on the existing `ScenarioData`; `ScenarioModel` is NOT introduced.** The model is `Validated<ScenarioData>`. The architecture's `Validated<ScenarioModel>` and the distinct canonical `ScenarioModel` type are **D3/ContentLoader work** (a separate refactor that risks the golden). The generic `Validated<T>` carries over unchanged when `ScenarioModel` later arrives, and Epic 2's `Validate(AbilityDefinition) → Validated<AbilityDefinition>` (Story 2.3) reuses the same machinery. _(`ScenarioModel` confirmed absent in source — planning docs only.)_

**D3 — `Validate` returns a `ValidationResult` (pure; no logging, no throw).** Shape:
```csharp
public readonly struct ValidationResult
{
    public bool Ok { get; }
    public string? Error { get; }                 // located, e.g. "scenario.resource_nodes[2].supply must be >= 0 (was -50)"
    public Validated<ScenarioData> Value { get; } // meaningful only when Ok
}
```
On the first failed check, return `Fail(locatedMessage)` (fail-fast — one located error is enough). On full success, return `Pass(new Validated<ScenarioData>(model, _proof))`. The validator **never** logs and **never** throws — the call site decides what to do (D7). This keeps `NegativeValidationTests` Godot-free and assertion-clean.

**D4 — The validator's checks (concrete, against the as-built `float` `ScenarioData`).** Iterate **by index** (deterministic) and fail-fast with a located path. Checks:
- **Finite + in `Fixed` range** for every numeric `float` field: `MapBounds`; per slot `StartOre/BaseX/BaseZ`; per node `X/Z/Supply/Rate`; per building `X/Z`; per unit `X/Z`. Reject `float.IsNaN`/`float.IsInfinity` and `|v| >= 32768f` (the `FixedJsonConverter` range, `FixedRangeLimit`). *(The model is plain `float`, so it does NOT pass through `FixedJsonConverter` today — the validator does these checks itself. When D3 makes the model `Fixed`, the converter handles NaN/Inf and these become redundant.)*
- **Non-negative:** `StartOre >= 0`, `Supply >= 0`, `Rate >= 0`, `MaxGatherers >= 0`.
- **Position in bounds:** `MapBounds` finite and `> 0`; every `X`/`Z` (nodes, buildings, units, slot bases) satisfies `|coord| <= MapBounds`.
- **Slot validity (the AR-39 length-5 overflow guard):** every `PlayerSlot.Slot` is in `[0, FactionRegistry.PLAYER_COUNT)` **and** maps to a defined `Faction` (`slot + 1 <= (int)Faction.Player4` on the as-built enum — relaxes automatically when Story 9.2 extends `Faction` to `Player8`); `PlayerSlot.Slot` values are **unique**; every `Building.Slot` and `Unit.Slot` **references a declared `PlayerSlot.Slot`** (else "dangling reference"). Use `FactionRegistry`/`Faction` constants — never a bare literal.
- **Building type:** `Building.Type` resolves to a known `BuildingType` enum name (the same set `ParseBuildingType` switches on) — an unknown name is rejected, **not** silently defaulted to `CommandCenter`.
- **Forbidden-until-`SimRng` (AR-13) — a forward seam.** No effect/ability/random-effect data model exists yet (Epic 2), and `SimRng` is **unconditionally present** on every `EntityWorld` (`world.Rng`, non-null, no flag). So today this rule has **no live scenario field to inspect**. Implement it as an explicit, unit-tested decision function the effect validator will extend:
  ```csharp
  // Returns a located error if the model declares randomness without a wired SimRng; null otherwise.
  // TODAY: ScenarioData has no random-effect field and SimRng is always present, so this is a no-op
  // on real scenarios. Epic 2 (Story 2.3) feeds it real EffectNode/AbilityDefinition data.
  internal static string? CheckRandomnessRequiresSimRng(bool modelDeclaresRandomness, bool simRngAvailable)
      => (modelDeclaresRandomness && !simRngAvailable)
         ? "model declares a random effect but SimRng is not wired (AR-13)"
         : null;
  ```
  Drive it directly from `NegativeValidationTests` (the four truth-table cases) so the rule is *owned and proven* by 1.7, then have `Validate` call it with `modelDeclaresRandomness: false` (no field exists) and `simRngAvailable: true`. _(See Clarifying Questions — Alec may prefer to defer the rule entirely to Epic 2; the recommended default is to land the seam now per the epic's "owned by 1.7".)_

**D5 — `CanonicalModelHash` = FNV-64 over quantized `Fixed.Raw`, sorted, enums-by-name, cosmetic fields excluded, `0 → 1` sentinel.** Mirror `SimChecksum`'s byte-wise mix but 64-bit (no FNV-64 helper exists — author the constants). Fold, in this **fixed order**:
  1. `AlgoVersion` (= 2) as the first mix (namespaces the hash; algo-1 is the retired byte-FNV).
  2. `MapBounds` (quantized), `WinCondition` (enum **name** string), `TerrainRef` (string UTF-8).
  3. `PlayerSlots` **sorted by `Slot`**: fold `Slot` (int), `FactionJson` (string), `StartOre`/`BaseX`/`BaseZ` (quantized).
  4. `ResourceNodes` **sorted by `(X, Z, Supply, Rate, MaxGatherers)`**: fold `X`/`Z`/`Supply`/`Rate` (quantized), `MaxGatherers` (int).
  5. `Buildings` **sorted by `(Slot, Type, X, Z)`**: fold `Type` (string), `Slot` (int), `X`/`Z` (quantized), `PreBuilt` (bool→byte).
  6. `Units` **sorted by `(Slot, UnitId, X, Z)`**: fold `UnitId` (string), `Slot` (int), `X`/`Z` (quantized).
  - **Quantize** every numeric `float` field via `Fixed.FromFloat(v).Raw` — the exact integer the applier feeds the sim (D5 trap #3). **Sort** so file ordering can't change the hash. Fold enums/strings by **name/UTF-8 bytes** (ordinal drifts on enum insert). **Exclude** `Id` and `DisplayName` (cosmetic identity/metadata — the "annotations excluded" embodiment for today's schema; no `_editor`/`_ext` field exists yet) and **`Triggers`** (trigger/effect canonicalization is Epic 7 / D3.4 — see Scope Fence and Clarifying Questions).
  - **Sentinel:** if the 64-bit result is `0`, return `1` (a valid model must never hash to the "no hash" value that the handshake treats as fail-open). `ToWire(ulong h)` folds to `uint` via `(uint)(h ^ (h >> 32))` and re-applies the `0 → 1` sentinel for the existing 32-bit wire.
  - `Compute(ScenarioData)` is **allocation-frugal** but correctness-first; it is called once per match load (not in-tick), so the sort allocations are fine.

**D6 — Re-point the single hash write; leave the wire/handshake protocol untouched.** `MainScene.cs:316` becomes:
```csharp
// Canonical model hash over the in-memory scenario — stable across whitespace/key-order/path,
// fixing the AI-gen stale-file desync. Folded to the existing 32-bit Ready-packet wire.
_lobbyUi.ScenarioHash = CanonicalModelHash.ToWire(CanonicalModelHash.Compute(model));
```
where `model` is the `ScenarioData` that was just loaded/generated/applied (the `_scenario` field, or the fallback mirror). `_lobbyUi.ScenarioHash` stays `uint`; the Ready-packet format (`NetworkCommand.cs:208-220`) and the `LobbyUi.cs:315` compare are **unchanged**. Server-side attestation, the `hash==0` hard-reject, and 64-bit-wire widening are **Epic 9** (Stories 9-1/9-4). The 64-bit `Compute` is exposed for Epic 9 to consume later.

**D7 — Shadow/fail-closed policy lives at the presentation call site; toggle via env var.** A small helper:
```csharp
// presentation side (MainScene or src/UI/ScenarioGate.cs) — NOT in the Godot-free validator
private static readonly bool _failClosed =
    System.Environment.GetEnvironmentVariable("CHIMERA_VALIDATE_FAILCLOSED") == "1";

/// Returns true if apply should proceed. Shadow mode (default): logs + proceeds. Fail-closed: logs + halts.
private bool ValidateBeforeApply(ScenarioData model, string pathLabel)
{
    var result = _validator.Validate(model);
    if (result.Ok) return true;
    GD.PrintErr($"[ScenarioValidator] {pathLabel} REJECTED: {result.Error}");
    return !_failClosed; // shadow → proceed (true); fail-closed → halt (false)
}
```
Mirrors the codebase's only branch-toggle idiom (`CHIMERA_GOLDEN_RECORD`, `GoldenChecksumReplay.cs:31`). The fail-closed *flip* is **not** this story's deliverable — only the toggle plumbing + the shadow default. (AC4)

**D8 — `Validate` runs *beside* apply in 1.7; the applier is NOT yet type-gated.** `ApplyScenario` keeps its `ScenarioData` parameter. The "single pre-tick gate" is *logically* present (every path calls `Validate` before apply) but **not yet type-enforced**. Story 1.8b extracts `ScenarioApplier` and changes its signature to consume **only** `Validated<ScenarioData>`, making the gate compiler-mandatory. 1.7's job is to ship the gate + the type + the canonical hash; do not pre-empt 1.8b. (Trap #4)

**D9 — `ApplyFallbackScenario` builds a `ScenarioData` mirror for validate+hash, but keeps its hardcoded apply.** The fallback (`MainScene.cs:619-665`) writes directly into stores and never builds a `ScenarioData` (and never calls `ScenarioDirector.LoadScenario`). To route it through the gate (AC1) and give it a real canonical hash (AC3) **without changing behavior**, construct a `ScenarioData` mirroring the hardcoded values, run it through `ValidateBeforeApply` (shadow) and `CanonicalModelHash`, then apply via the **existing hardcoded code**. Do **not** reroute the fallback through `ApplyScenario` — that would newly fire `match_start` triggers and move behavior.

### Pre-flight facts you MUST NOT re-derive (verified against the codebase at `b42f5a7`)

- **`ScenarioData` is `float`-based, no `Fixed`, no annotation field** (`godot/src/Core/Definitions/ScenarioData.cs`). Class fields `:126-167`: `Id`/`DisplayName`/`TerrainRef` (string), `MapBounds` (float, `:143`, default 120), `WinCondition` (enum `:146`), `PlayerSlots`/`ResourceNodes`/`Buildings`/`Units` (arrays `:149-159`), `Triggers` (`TriggerDefinition[]`, `:165`). Nested numeric fields (all `float`): `ScenarioPlayerSlot.{StartOre(:33,def 200),BaseX(:37),BaseZ(:41)}` + `Slot`(int `:25`) + `FactionJson`(string `:29`); `ScenarioResourceNode.{X(:50),Z(:53),Supply(:57,def 400),Rate(:61,def 5)}` + `MaxGatherers`(int `:64,def 4`); `ScenarioBuilding.{X(:83),Z(:86)}` + `Type`(string `:76,def "CommandCenter"`) + `Slot`(int `:80`) + `PreBuilt`(bool `:93,def true`); `ScenarioUnit.{X(:111),Z(:114)}` + `UnitId`(string `:104,def "worker"`) + `Slot`(int `:108`). **No `_editor`/`_ext`/`[JsonExtensionData]` field exists** — "annotations excluded" is satisfied by excluding `Id`/`DisplayName` today. `WinCondition` enum `:9-16` = `{DestroyAllBuildings, EliminateAllUnits}`. [Source: ScenarioData.cs]
- **The single hash write site is `MainScene.cs:316`** (in `_Ready`, after `LoadAndApplyScenario`): `_lobbyUi.ScenarioHash = Definitions.ScenarioSerializer.ComputeFileHash(ProjectSettings.GlobalizePath(ScenarioPath));`. **There is no second/MP-overwrite** — the architecture's "~:1761" is `ApplySettingsToSystems` (camera), unrelated. The hash hashes **file bytes** (`ScenarioSerializer.ComputeFileHash`, `:59-80`), so AI-gen/fallback/editor paths carry a stale or `0` hash. [Source: MainScene.cs:314-318; ScenarioSerializer.cs:59-80]
- **The apply choke point is `ApplyScenario(ScenarioData scenario)` (`MainScene.cs:512-572`)** — called from `MainScene.cs:484` (AI-generated, via `_pendingGeneratedScenario`) and `:501` (file-loaded). `Fixed.FromFloat` happens **here**: `StartOre` `:531`, `BaseX/BaseZ` `:532-533`, node `X/Z/Supply/Rate` `:539-541`, building `X/Z` `:548`, unit `X/Z` inside `SpawnScenarioUnit(:575)` at `:567`. Final line `:571` `_scenarioDirector.LoadScenario(scenario)`. **Insert `ValidateBeforeApply` at the top of this method** (covers file-loaded + AI-generated + editor-in-memory). [Source: MainScene.cs:476-572]
- **`ApplyFallbackScenario()` (`MainScene.cs:619-665`)** builds hardcoded P1/P2 setup directly into stores; `_scenario` stays null; `ScenarioDirector.LoadScenario` is never called. Invoked at `:496` when `LoadFromFile` returns null. [Source: MainScene.cs:496,619-665]
- **The five entry paths → two insertion points.** (1) file-loaded `LoadFromFile`(`:491`)→`ApplyScenario`(`:501`); (2) AI/map-gen `MapGeneratorPanel.OnLoadRequested`(`:237/:257`)→`LoadGeneratedScenario`(`:1981`)→scene reload→`ApplyScenario(generated)`(`:484`); (3) fallback `ApplyFallbackScenario`(`:496`); (4) editor-in-memory: the live `_scenario` mutated in Edit mode (e.g. `MoveStartPosition` `:1015`) and re-applied through `ApplyScenario`; (5) replay-loaded `new ReplayPlayer(filePath, _world)`(`:2156`) replays the **command stream over the already-loaded scenario** — it does NOT apply a `ScenarioData` (only WARNs on a path mismatch `:2163-2166`), so it is covered **transitively** by its underlying file load. ⇒ Gating `ApplyScenario` (1/2/4) + `ApplyFallbackScenario` (3) covers all five; replay-header hash-binding is **Epic 7/9**. [Source: MainScene.cs:476-501,1981,2156-2166; MapGeneratorPanel.cs:237,257]
- **`SimChecksum` is FNV-1a 32-bit; `Mix` is private; no FNV-64 helper exists** (`godot/src/Core/SimChecksum.cs`). Constants `:24-26` (`FNV_OFFSET=2166136261u`, `FNV_PRIME=16777619u`); byte-wise `Mix(uint,int)` `:105-113`; folds `Fixed` via `.Raw` (`:58-61`); folds 64-bit `Rng.State` as low/high int mixes `:95-97` (**the precedent for folding 64-bit values** — but you need FNV-64 constants `14695981039346656037` offset / `1099511628211` prime, authored fresh). `AlgoVersion=3` `:37`. [Source: SimChecksum.cs]
- **`Fixed`** (`godot/src/Core/FixedPoint.cs`): `public readonly int Raw` is a **public field** `:16` (not a property); `FromInt`(`:24`), `FromFloat`(`:27`, `(int)(value*65536)`), `FromRaw`(`:30`); `Zero/One/Half` `:34-36`; all six comparison ops `:82-92`; `Clamp/Min/Max` `:105-111`. `FixedRangeLimit = 32768d` lives on `FixedJsonConverter.cs:30`. [Source: FixedPoint.cs; FixedJsonConverter.cs]
- **`FactionRegistry`** (`godot/src/Core/FactionRegistry.cs`): `PLAYER_COUNT = 8` `:18` (forward target), `FACTION_ARRAY_SIZE = 9` `:23`, `static Faction ToFaction(int slot) => (Faction)(slot+1)` `:26`, `ActiveFactions`/`ActiveCount` `:47/:50`. **No `FACTION_COUNT`** here (as-built `FACTION_COUNT=5` lives on `ResourceStore`/`MatchStats`). **`Faction` enum** (`EntityWorld.cs:47-54`, backing `byte`) tops at `Player4 = 4` — so a slot whose `ToFaction(slot)` exceeds `Player4` is undefined and overflows the as-built `[5]` arrays. [Source: FactionRegistry.cs; EntityWorld.cs:47-54]
- **`SimRng` is always present** (`godot/src/Core/SimRng.cs` `sealed class`; `EntityWorld.Rng { get; }` `:79`, constructed unconditionally `:194` with `DEFAULT_RNG_SEED` `:70`). `world.Rng.State` (`SimRng.cs:35`) is the only inspectable value; there is **no null/flag** meaning "Rng absent." Folded into `SimChecksum` v3 (`:95-97`); replay seed persisted v2 (`ReplayRecorder.cs:25,122`; `ReplayPlayer.cs:104,110`). [Source: SimRng.cs; EntityWorld.cs:70,79,194]
- **No effect/random data model exists** — grep `EffectNode|EffectDef|EffectGraph|RandomEffect` and `System.Random|new Random(` under `godot/src` → **no matches** in sim code (only presentation `AudioManager`/camera, and dev-only `StressTest.cs` which is `<Compile Remove>`'d from Tier-1). The forbidden-until-`SimRng` rule has **no live data** today. [Source: grep @ b42f5a7]
- **`FixedJsonConverter`** (`godot/src/Core/Definitions/FixedJsonConverter.cs`) rejects non-number `:34-35`, NaN/±Inf `:38-39`, and `|f| >= 32768` `:45-48` with located `JsonException`. *(ScenarioData's floats do NOT route through it today — the validator replicates the finiteness/range checks itself; see D4.)* [Source: FixedJsonConverter.cs]
- **MP handshake is fail-open and server-bypassed.** `LobbyUi.cs:315` compares only `if (ScenarioHash != 0 && peerHash != 0 && peerHash != ScenarioHash)` → either-side-`0` skips the check; `DedicatedServer.HandleReady` (`:171-191`) ignores the hash payload entirely; `LockstepManager.SeedInitialTicks` (`:579-590`) carries no start-state hash (divergence is caught only by post-tick runtime checksums `:365-376`). **1.7 only changes the hash *value source*** — hardening the handshake (server attest, `0`-reject) is Epic 9. [Source: LobbyUi.cs:313-325; DedicatedServer.cs:171-191; LockstepManager.cs:579-590]
- **Tier-1 project** (`godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj`): `net8.0` `:4`, xUnit `2.9.2` `:53`, **not** Godot-SDK. Globs `..\src\Core\**\*.cs` `:14` ⇒ new validator types under `src/Core/Definitions` **auto-compile, no `.csproj` edit**. Existing test folders: `Combat/`, `Determinism/`, `Golden/` (+ root `GodotFreeBoundaryTest.cs`). **`Validation/` is NEW**; `NegativeValidationTests`/`ScenarioApplierTests` do not exist. `MainScene.cs`/`StressTest.cs` are `<Compile Remove>`'d (`:34/:35`). [Source: ProjectChimera.Sim.Tests.csproj]
- **All Story 1.7 target types are net-new (confirmed absent):** `ScenarioValidator`, `Validated<T>`, `ValidationResult`, `CanonicalModelHash`, `ScenarioModel`, `ContentLoader`, `ScenarioApplier`, `SimulationHost` — none in source. `BuildingType` enum (the name set for `Building.Type` validation) is `byte`-backed in `BuildingStore.cs`. [Source: grep @ b42f5a7; BuildingStore.cs]

### Scope fence — do NOT, in this story

- **Do NOT** move/re-record either golden (AC5). Shadow-only + hash-only ⇒ goldens byte-identical. A moved golden = a real leak into the tick; fix it.
- **Do NOT** give `Validated<T>` an `internal` (or public) constructor — use the compile-enforced `Proof` token (D1).
- **Do NOT** convert `ScenarioData`'s `float` fields to `Fixed`, build `ContentLoader`, or unify the `JsonSerializerOptions` (D3 / later). The model stays `float`; the hash quantizes at hash time.
- **Do NOT** introduce `ScenarioModel`, `SimulationHost`, or `ScenarioApplier`, and do NOT change `ApplyScenario`'s signature to require `Validated<…>` — that type-enforcement is **Story 1.8b** (D8).
- **Do NOT** fold `Triggers` (or any effect/DSL data) into the canonical hash — Epic 7 (D3.4). Do NOT bind a canonical hash into the replay header — Epic 7/9.
- **Do NOT** add server-side attestation, a checksum collector/quorum, the `hash==0` hard-reject, or widen the Ready-packet wire past 32-bit — **Epic 9** (9-1/9-4). 1.7 changes only the local hash *value*.
- **Do NOT** perform the fail-closed *flip*. Ship shadow/log-only as the default; provide the `CHIMERA_VALIDATE_FAILCLOSED` toggle only (AC4).
- **Do NOT** call `GD.Print`/`using Godot` in `ScenarioValidator`/`CanonicalModelHash`/`Validated`/`ValidationResult` — they are Godot-free `src/Core/Definitions` types (`GodotFreeBoundaryTest`). Logging is presentation-side (D7).
- **Do NOT** reroute `ApplyFallbackScenario` through `ApplyScenario` (would fire `match_start` triggers) — build a mirror for validate+hash, keep the hardcoded apply (D9).

---

## Tasks / Subtasks

- [ ] **Task 1 — Net-new `Validated<T>` + `ValidationResult` with the `Proof`-token mint guard (AC: 1)**
  - [ ] Create `godot/src/Core/Definitions/Validated.cs` (`#nullable enable`, namespace `ProjectChimera.Core.Definitions`, **no `using Godot`**): `public readonly struct Validated<T>` with `public T Value { get; }` and the `Validated(T value, ScenarioValidator.Proof proof)` constructor (D1).
  - [ ] In the same file (or `ValidationResult.cs`): `public readonly struct ValidationResult` (`Ok`, `Error`, `Value`) with `static ValidationResult Pass(Validated<ScenarioData>)` / `static ValidationResult Fail(string located)` (D3).
  - [ ] `dotnet build godot/godot.csproj` → expect an error until `ScenarioValidator.Proof` exists (Task 2); that's fine.

- [ ] **Task 2 — Net-new `ScenarioValidator.Validate` + all checks (AC: 1, 2)**
  - [ ] Create `godot/src/Core/Definitions/ScenarioValidator.cs` (`#nullable enable`, **no `using Godot`**): `public sealed class ScenarioValidator` with the nested `public sealed class Proof { private Proof() {} }`, the `private static readonly Proof _proof = new Proof();`, and `public ValidationResult Validate(ScenarioData model)`.
  - [ ] Implement the D4 checks, fail-fast with a **located** error (field path + offending value): finite+range numerics; non-negatives; positions within `MapBounds`; slot bounds via `FactionRegistry`/`Faction` (`[0,PLAYER_COUNT)` + maps to a defined `Faction` ≤ `Player4` + unique); building/unit slot references a declared `PlayerSlot` (dangling-ref); `Building.Type` is a known `BuildingType` name.
  - [ ] Add `internal static string? CheckRandomnessRequiresSimRng(bool modelDeclaresRandomness, bool simRngAvailable)` (AR-13 seam, D4); call it from `Validate` with `(false, true)` (no live random data today).
  - [ ] On full success: `return ValidationResult.Pass(new Validated<ScenarioData>(model, _proof));`.
  - [ ] `dotnet build godot/godot.csproj` → green (only pre-existing CS8632 warnings).

- [ ] **Task 3 — Net-new `CanonicalModelHash` (FNV-64, sorted, quantized, sentinel) (AC: 3)**
  - [ ] Create `godot/src/Core/Definitions/CanonicalModelHash.cs` (`#nullable enable`, **no `using Godot`**): `public static class CanonicalModelHash` with `const int AlgoVersion = 2`, FNV-64 constants, a 64-bit byte-wise `Mix`, `ulong Compute(ScenarioData)`, and `uint ToWire(ulong)`.
  - [ ] Fold the D5 field set in fixed order; **sort** each collection by its D5 key; **quantize** floats via `Fixed.FromFloat(v).Raw`; fold enums/strings by name/UTF-8; **exclude** `Id`/`DisplayName`/`Triggers`; apply the `0 → 1` sentinel in both `Compute` and `ToWire`.
  - [ ] `dotnet build godot/godot.csproj` → green.

- [ ] **Task 4 — Insert the shadow-mode gate on all entry paths + re-point the hash (AC: 1, 3, 4)**
  - [ ] Add the presentation gate (D7): a `ScenarioValidator _validator` field on `MainScene` (or a small `src/UI/ScenarioGate.cs`), the `_failClosed` env-var read, and `bool ValidateBeforeApply(ScenarioData, string pathLabel)`.
  - [ ] Call `ValidateBeforeApply(scenario, "<path>")` at the **top of `ApplyScenario`** (`MainScene.cs:512`); in fail-closed mode `return;` before applying (shadow mode proceeds).
  - [ ] In `ApplyFallbackScenario` (`MainScene.cs:619`): build a `ScenarioData` mirror of the hardcoded values, run `ValidateBeforeApply(mirror, "fallback")`, and keep the existing hardcoded apply (D9). Stash the mirror so the hash re-point can use it.
  - [ ] Re-point `MainScene.cs:316`: `_lobbyUi.ScenarioHash = CanonicalModelHash.ToWire(CanonicalModelHash.Compute(model));` where `model` is the applied `ScenarioData` (`_scenario` for the normal paths, the fallback mirror otherwise). Keep the `GD.Print` hash log line (now `0x{...:X8}` of the folded wire).
  - [ ] `dotnet build godot/godot.csproj` → green.

- [ ] **Task 5 — Tier-1 `Validation/` tests (AC: 1, 2, 3, 4)**
  - [ ] New `godot/ProjectChimera.Sim.Tests/Validation/NegativeValidationTests.cs` (AC2): for each invalid case assert `Validate(...).Ok == false` and `Error` **locates** the field — NaN/Inf ore, over-range position, position outside `MapBounds`, negative supply/rate, `slot` ≥ ceiling, duplicate slot, building/unit slot with no declared `PlayerSlot` (dangling), unknown `Building.Type` ("Frost"). Plus the four-case truth table for `CheckRandomnessRequiresSimRng`. Assert a **valid** model returns `Ok == true` with a `Validated<ScenarioData>` whose `Value` is the same model.
  - [ ] New `Validation/CanonicalModelHashTests.cs` (AC3): build one `ScenarioData`; assert two semantically-identical instances (reordered collections, equal-after-quantize values) hash **equal**; a single changed gameplay value hashes **different**; `Id`/`DisplayName`/`Triggers` changes do **not** change the hash; the hash is **never 0**; `ToWire` is non-zero and stable. Pin one hash against an **independently-computed** FNV-64 of a tiny known model (avoid a self-tautology).
  - [ ] New `Validation/ValidatedMintingTests.cs` (AC1): assert `Validate` mints a `Validated<ScenarioData>` on success; add `ValidatedSoleMinterTest` scanning the sim source for `new Validated<` and asserting the only hit is inside `ScenarioValidator.cs`.
  - [ ] New `Validation/ShadowModeTests.cs` (AC4): assert the validator itself never throws/logs (pure) and `ValidationResult.Fail` carries the located message (the shadow-vs-fail-closed branch is presentation-side; assert the policy helper's decision given `_failClosed` true/false if it is extracted to `src/UI` and Godot-free-testable — otherwise document it as the in-engine smoke check).
  - [ ] `dotnet test --filter FullyQualifiedName~Validation` → green.

- [ ] **Task 6 — Prove AC5: goldens byte-identical, full suite green, Godot-free boundary (AC: 5)**
  - [ ] `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` → ALL green, with `golden-scenario.golden.txt` and `golden-multifaction.golden.txt` **UNCHANGED** (`git status` clean on both). A moved golden = a real leak; fix it, do NOT re-record.
  - [ ] Grep the four net-new `Definitions` files: zero `using Godot`/`GD.`/`float` gameplay math/`System.Random` (the only `Fixed.FromFloat` is the load-time quantize in `CanonicalModelHash`). Confirm `GodotFreeBoundaryTest` passes.
  - [ ] `git diff` shows no signature change to `ISimSystem`/`SimChecksum.Compute`/any `Tick`/`ApplyScenario`'s parameter type.

- [ ] **Task 7 — In-engine smoke (AC: 3, 4) — optional but recommended**
  - [ ] Run the game (`/godot-verify` or Godot MCP `run`): a normal skirmish still loads + plays; the console prints the new canonical `Scenario hash: 0x...`. Edit one position in `alpha_map_01.json` by a sub-quantum amount that rounds to the same `Fixed` (e.g. a trailing-zero change `45.0`→`45.00`) and confirm the hash is **unchanged**; change it by a real amount and confirm the hash **changes**. Optionally set `CHIMERA_VALIDATE_FAILCLOSED=1` with a deliberately broken scenario and confirm it refuses to apply (then unset). _(MainScene is excluded from Tier-1, so this is the only check of the production wiring + env toggle.)_

---

## Dev Notes

### `Validated<T>` + `ValidationResult` (Task 1) — full
```csharp
#nullable enable
namespace ProjectChimera.Core.Definitions
{
    /// <summary>Proof that a value passed ScenarioValidator. Mintable ONLY by the validator
    /// (its ctor requires a ScenarioValidator.Proof, whose ctor is private to that class).</summary>
    public readonly struct Validated<T>
    {
        public T Value { get; }
        public Validated(T value, ScenarioValidator.Proof proof) { Value = value; }
    }

    /// <summary>Pure result of validation — no logging, no throw. The caller decides shadow vs fail-closed.</summary>
    public readonly struct ValidationResult
    {
        public bool Ok { get; }
        public string? Error { get; }                  // located, e.g. "scenario.units[3].slot=5 ..."
        public Validated<ScenarioData> Value { get; }  // valid only when Ok

        private ValidationResult(bool ok, string? error, Validated<ScenarioData> value)
        { Ok = ok; Error = error; Value = value; }

        public static ValidationResult Pass(Validated<ScenarioData> value) => new(true, null, value);
        public static ValidationResult Fail(string located) => new(false, located, default);
    }
}
```

### `ScenarioValidator` (Task 2) — skeleton (checks abbreviated; implement all of D4)
```csharp
#nullable enable
using ProjectChimera.Core;            // Fixed, Faction, FactionRegistry
using ProjectChimera.Economy;         // BuildingType (confirm namespace at the BuildingType enum)

namespace ProjectChimera.Core.Definitions
{
    public sealed class ScenarioValidator
    {
        public sealed class Proof { private Proof() { } }      // only this class can `new Proof()`
        private static readonly Proof _proof = new Proof();

        private const float Range = 32768f;                    // FixedJsonConverter.FixedRangeLimit

        public ValidationResult Validate(ScenarioData m)
        {
            if (!Finite(m.MapBounds) || m.MapBounds <= 0f)
                return ValidationResult.Fail($"scenario.map_bounds must be finite and > 0 (was {m.MapBounds}).");

            // --- player slots: finite/range, non-neg ore, in-bounds base, slot ceiling, uniqueness ---
            var seenSlots = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < m.PlayerSlots.Length; i++)
            {
                var s = m.PlayerSlots[i];
                if (s.Slot < 0 || s.Slot >= FactionRegistry.PLAYER_COUNT)
                    return ValidationResult.Fail($"scenario.player_slots[{i}].slot={s.Slot} out of [0,{FactionRegistry.PLAYER_COUNT}).");
                if (s.Slot + 1 > (int)Faction.Player4)        // as-built enum ceiling (relaxes at Story 9.2)
                    return ValidationResult.Fail($"scenario.player_slots[{i}].slot={s.Slot} maps to an undefined Faction (engine supports <= {(int)Faction.Player4 - 1}).");
                if (!seenSlots.Add(s.Slot))
                    return ValidationResult.Fail($"scenario.player_slots[{i}].slot={s.Slot} is a duplicate.");
                var bad = NumOk("start_ore", s.StartOre, i, nonNeg: true)
                       ?? Coord("base_x", s.BaseX, i, m.MapBounds) ?? Coord("base_z", s.BaseZ, i, m.MapBounds);
                if (bad != null) return ValidationResult.Fail(bad);
            }

            // --- resource nodes / buildings / units: finite/range, non-neg, in-bounds, dangling slot, building type ---
            // ... implement per D4 (omitted here for brevity — follow the same located-error pattern) ...

            // --- AR-13 forward seam: no random-effect field exists yet; SimRng is always present ---
            var rng = CheckRandomnessRequiresSimRng(modelDeclaresRandomness: false, simRngAvailable: true);
            if (rng != null) return ValidationResult.Fail("scenario: " + rng);

            return ValidationResult.Pass(new Validated<ScenarioData>(m, _proof));
        }

        internal static string? CheckRandomnessRequiresSimRng(bool modelDeclaresRandomness, bool simRngAvailable)
            => (modelDeclaresRandomness && !simRngAvailable)
               ? "model declares a random effect but SimRng is not wired (AR-13)"
               : null;

        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        private static bool InRange(float v) => Finite(v) && v < Range && v > -Range;
        private static string? NumOk(string field, float v, int i, bool nonNeg)
            => !InRange(v) ? $"scenario...[{i}].{field}={v} is non-finite or out of 16.16 range."
             : (nonNeg && v < 0f) ? $"scenario...[{i}].{field}={v} must be >= 0." : null;
        private static string? Coord(string field, float v, int i, float bounds)
            => !InRange(v) ? $"scenario...[{i}].{field}={v} non-finite/out of range."
             : (v < -bounds || v > bounds) ? $"scenario...[{i}].{field}={v} outside map_bounds {bounds}." : null;
    }
}
```
> Confirm the `BuildingType` enum's namespace before the `using` (it is `byte`-backed in `BuildingStore.cs`). Validate `Building.Type` with `System.Enum.TryParse<BuildingType>(b.Type, out _)` — reject on false (mirror the set `ParseBuildingType` at `MainScene.cs:607` switches on, so the validator and the applier agree).

### `CanonicalModelHash` (Task 3) — skeleton
```csharp
#nullable enable
using System.Linq;
using System.Text;
using ProjectChimera.Core;            // Fixed

namespace ProjectChimera.Core.Definitions
{
    /// <summary>FNV-64 over the canonical model: Fixed.Raw-quantized numerics, sorted collections,
    /// enums/strings by name, Id/DisplayName/Triggers excluded. Replaces the byte-FNV file hash (AR-23, algo-2).</summary>
    public static class CanonicalModelHash
    {
        public const int AlgoVersion = 2;                          // 1 = retired byte-FNV
        private const ulong Offset = 14695981039346656037UL;
        private const ulong Prime  = 1099511628211UL;

        public static ulong Compute(ScenarioData m)
        {
            ulong h = Offset;
            h = MixInt(h, AlgoVersion);
            h = MixInt(h, Fixed.FromFloat(m.MapBounds).Raw);
            h = MixStr(h, m.WinCondition.ToString());              // enum by NAME, not ordinal
            h = MixStr(h, m.TerrainRef);

            foreach (var s in m.PlayerSlots.OrderBy(x => x.Slot))
            { h = MixInt(h, s.Slot); h = MixStr(h, s.FactionJson);
              h = MixInt(h, Fixed.FromFloat(s.StartOre).Raw);
              h = MixInt(h, Fixed.FromFloat(s.BaseX).Raw); h = MixInt(h, Fixed.FromFloat(s.BaseZ).Raw); }

            foreach (var n in m.ResourceNodes.OrderBy(x => x.X).ThenBy(x => x.Z).ThenBy(x => x.Supply).ThenBy(x => x.Rate))
            { h = MixInt(h, Fixed.FromFloat(n.X).Raw); h = MixInt(h, Fixed.FromFloat(n.Z).Raw);
              h = MixInt(h, Fixed.FromFloat(n.Supply).Raw); h = MixInt(h, Fixed.FromFloat(n.Rate).Raw);
              h = MixInt(h, n.MaxGatherers); }

            foreach (var b in m.Buildings.OrderBy(x => x.Slot).ThenBy(x => x.Type).ThenBy(x => x.X).ThenBy(x => x.Z))
            { h = MixStr(h, b.Type); h = MixInt(h, b.Slot);
              h = MixInt(h, Fixed.FromFloat(b.X).Raw); h = MixInt(h, Fixed.FromFloat(b.Z).Raw);
              h = MixInt(h, b.PreBuilt ? 1 : 0); }

            foreach (var u in m.Units.OrderBy(x => x.Slot).ThenBy(x => x.UnitId).ThenBy(x => x.X).ThenBy(x => x.Z))
            { h = MixStr(h, u.UnitId); h = MixInt(h, u.Slot);
              h = MixInt(h, Fixed.FromFloat(u.X).Raw); h = MixInt(h, Fixed.FromFloat(u.Z).Raw); }

            return h == 0UL ? 1UL : h;                              // FNV-sentinel: never the fail-open value
        }

        /// Fold the 64-bit canonical hash into the existing 32-bit Ready-packet wire.
        public static uint ToWire(ulong h) { uint w = (uint)(h ^ (h >> 32)); return w == 0u ? 1u : w; }

        private static ulong MixInt(ulong h, int value)
        {
            uint v = (uint)value;                                  // 4 bytes, little-endian (mirror SimChecksum.Mix)
            h ^= v & 0xFF;        h *= Prime; h ^= (v >> 8) & 0xFF;  h *= Prime;
            h ^= (v >> 16) & 0xFF; h *= Prime; h ^= (v >> 24) & 0xFF; h *= Prime;
            return h;
        }
        private static ulong MixStr(ulong h, string? s)
        {
            h = MixInt(h, s?.Length ?? -1);                        // length-prefix so "ab"+"c" != "a"+"bc"
            if (s == null) return h;
            foreach (byte by in Encoding.UTF8.GetBytes(s)) { h ^= by; h *= Prime; }
            return h;
        }
    }
}
```
> The independently-computed AC3 pin: hand-fold a tiny model (e.g. empty collections, `MapBounds=120`, `WinCondition=DestroyAllBuildings`, `TerrainRef=""`) with the same constants in the test and assert equality — do NOT assert `Compute(x) == Compute(x)` (a tautology).

### MainScene insertion (Task 4) — the three edits
```csharp
// (a) field + helper (near the other system fields / a small src/UI/ScenarioGate.cs is also fine)
private readonly Definitions.ScenarioValidator _validator = new();
private static readonly bool _failClosed =
    System.Environment.GetEnvironmentVariable("CHIMERA_VALIDATE_FAILCLOSED") == "1";

private bool ValidateBeforeApply(ScenarioData model, string pathLabel)
{
    var r = _validator.Validate(model);
    if (r.Ok) return true;
    GD.PrintErr($"[ScenarioValidator] {pathLabel} REJECTED: {r.Error}");
    return !_failClosed;                                   // shadow: proceed; fail-closed: halt
}

// (b) top of ApplyScenario(ScenarioData scenario)  (MainScene.cs:512)
if (!ValidateBeforeApply(scenario, "ApplyScenario")) return;

// (c) re-point the hash  (MainScene.cs:316) — `model` = the applied ScenarioData (_scenario or fallback mirror)
_lobbyUi.ScenarioHash = Definitions.CanonicalModelHash.ToWire(Definitions.CanonicalModelHash.Compute(model));
GD.Print($"[MainScene] Scenario hash: 0x{_lobbyUi.ScenarioHash:X8}");
```
> `ApplyFallbackScenario` builds a mirror `ScenarioData` (the hardcoded P1/P2 slots/ore/nodes/buildings/units), calls `ValidateBeforeApply(mirror, "fallback")`, applies the existing hardcoded code, and assigns `_scenario = mirror` (or a dedicated field) so step (c) hashes a real model rather than `0`.

### Constraints & gotchas
- **`dotnet build` / `dotnet test` are authoritative** for C# correctness; the Godot MCP `run` does not rebuild the test assembly. Build + test before declaring done. [Source: LEARNINGS / 1.1–1.6 Dev Notes]
- **This story must NOT move the goldens** (AC5) — it is shadow-only + hash-only. The canonical hash writes `_lobbyUi.ScenarioHash`, never a sim store; the validator only logs on master. A green golden suite with **unchanged** `.txt` files is the proof. [Source: D8/D9; GoldenChecksumReplay.cs]
- **Quantize, don't stringify.** Fold `Fixed.FromFloat(v).Raw` (the value the sim uses), never the `float` bits or JSON text. This is what makes `1.0`/`1` hash equal and a real change diverge. [Source: D5; FixedPoint.cs:27]
- **Located errors only.** Every `Fail(...)` must name the field path + offending value (`scenario.buildings[2].slot=7 references no player_slot`). A bare "invalid scenario" fails the AC2 "located" requirement. [Source: D3]
- **Godot-free boundary.** `Validated`/`ValidationResult`/`ScenarioValidator`/`CanonicalModelHash` are `src/Core/Definitions` — no `using Godot`, no `GD.*`, no gameplay `float` math. `Fixed.FromFloat` is allowed (load-time quantize, the sanctioned boundary). `GodotFreeBoundaryTest` enforces this. [Source: project-context.md; GodotFreeBoundaryTest.cs]
- **`Faction` enum tops at `Player4`.** Validating against `PLAYER_COUNT=8` alone would pass slots that overflow the as-built `[5]` arrays. Keep BOTH the `PLAYER_COUNT` bound and the `slot+1 <= (int)Faction.Player4` live-ceiling check. [Source: EntityWorld.cs:47-54; FactionRegistry.cs:18; the MainScene.cs:242 overflow the architecture calls out]
- **No new dependencies, no `.csproj` edit.** All four types live under `src/Core/Definitions`, auto-globbed into `godot.csproj` and `ProjectChimera.Sim.Tests` via `..\src\Core\**`. Tests use in-memory `ScenarioData` instances. [Source: ProjectChimera.Sim.Tests.csproj:14]
- **Pre-existing CS8632** nullable warnings are not this story's bug — leave them. [Source: deferred-work.md]
- **Independence rule (from the 1.1/1.4/1.5/1.6 reviews):** derive the AC3 hash pin from the algorithm (an independently hand-folded FNV-64 of a known tiny model), not by asserting `Compute(x) == Compute(x)`. [Source: prior review findings]

### Project Structure Notes
- **NEW:** `godot/src/Core/Definitions/Validated.cs` (+`ValidationResult`), `ScenarioValidator.cs`, `CanonicalModelHash.cs`; optional `godot/src/UI/ScenarioGate.cs` (else the gate helper is a private `MainScene` method). **NEW tests:** `ProjectChimera.Sim.Tests/Validation/` — `NegativeValidationTests.cs`, `CanonicalModelHashTests.cs`, `ValidatedMintingTests.cs`, `ShadowModeTests.cs`.
- **EDIT:** `godot/src/Core/MainScene.cs` — validator field + gate helper; `ValidateBeforeApply` at the top of `ApplyScenario` (`:512`); fallback mirror in `ApplyFallbackScenario` (`:619`); hash re-point at `:316`.
- **UNCHANGED (must stay so):** `golden-scenario.golden.txt`, `golden-multifaction.golden.txt`, `SimChecksum.cs`, `ScenarioData.cs` (no model→`Fixed` change), `ScenarioSerializer.cs` (`ComputeFileHash` stays — frozen as algo-1, no longer used at `:316`), `ISimSystem`, `SimulationLoop.cs`, `ApplyScenario`'s parameter type.
- **No** `ScenarioModel`/`ContentLoader`/`SimulationHost`/`ScenarioApplier`/`Sim/` folder — later stories.

### Project Context Rules
_Extracted from `_bmad-output/project-context.md` + `game-architecture.md` — these govern every edit here:_
- **Fail-closed, never-throw-mid-tick sim; authoring/presentation catches and degrades.** The validator is the `Validated<T>` fail-closed boundary pattern: untrusted input → `Validate` → sim. It returns located errors (never throws); presentation logs + (shadow) proceeds. [Source: project-context.md / game-architecture.md "Sim = deterministic, fail-closed"]
- **All sim math uses `Fixed` (16.16); `Fixed.FromFloat` is load-time only.** The canonical hash quantizes at hash time (load-time, allowed); `Get`/compare paths are integer-only. No `float` gameplay math, no `System.Random`, no `using Godot` in the Definitions types. [Source: project-context.md "Determinism"]
- **No gameplay rule hardcoded where a creator can't reach it; reuse existing systems.** The validator reuses `FactionRegistry`/`Faction`/`BuildingType`/`Fixed`; the hash mirrors `SimChecksum`'s mix style. No parallel converter, no new dependency. [Source: project-context.md "data-driven" / "Data layout"]
- **Process deterministically; never enumerate a `Dictionary`/`HashSet` in hashed order.** The hash **sorts** every collection (`OrderBy`) before folding; the validator iterates **by index**. The `HashSet<int> seenSlots` is used only for membership (never enumerated into the hash). [Source: project-context.md "Determinism — ascending order"]
- **Engine/runtime:** Godot 4.6.3 target, .NET 8 (`net8.0`); assembly/namespace `ProjectChimera.*`; project files `godot.csproj`/`godot.sln`; Tier-1 `ProjectChimera.Sim.Tests` (xUnit 2.9.2, Godot-free). [Source: project-context.md "Technology Stack"]

### References
- [Source: epics.md#Story-1.7 (lines 620-638)] — story statement; the 4 ACs (five paths → `Validate` → `Validated<T>` mint-restricted; invalid model → located failure incl. the forbidden-until-`SimRng` example; canonical FNV-64-over-`Fixed.Raw` hash re-points off `ComputeFileHash`; shadow/log-only on master with release-branch fail-closed flip); Covers FR-39/FR-44/AR-39/AR-23/AR-13; Depends on 1.5, 1.6.
- [Source: epics.md (lines 490, 582-598, 600-618)] — Epic 1 sequencing (1.7 is net-new, before 1.8); 1.5 relocates the forbidden-until-`SimRng` rule's ownership to 1.7; 1.6 proves the data-driven content path the validator guards.
- [Source: game-architecture.md §D3 (lines 788-916)] — Unified fail-closed loader; canonical-model hash (FNV-64 over `Fixed.Raw`, fixed field order, enums-by-name, sorted collections, annotations excluded, `0→1` sentinel, algo-2 vs frozen byte-algo-1); D3.1 re-points `MainScene.cs:303/316` to the in-memory model hash.
- [Source: game-architecture.md §C2 (lines 1455-1672, 1706-1709)] — `ScenarioValidator` is the net-new fail-closed `Validate(model)` gate; `Validated<ScenarioModel>` is the type-enforced "untrusted input → fail-closed → sim" pattern; "every untrusted source funnels through this one gate"; Step 4 inserts it in **shadow/log-only** (failures log-only, still apply) with the fail-closed flip on a release branch.
- [Source: game-architecture.md §D4 (lines 995-1033)] — server-attested canonical `startStateHash` over `Fixed.Raw` (the Epic-9 hardening 1.7 does NOT do); "never over post-`ApplyScenario` state that passed through `Fixed.FromFloat`"; the `hash==0` fail-open becomes a hard reject (Epic 9).
- [Source: MainScene.cs:316,476-572,619-665,1981,2156-2166] · [ScenarioData.cs:9-167] · [SimChecksum.cs:24-113] · [FixedPoint.cs:16-92] · [FactionRegistry.cs:18-50] · [EntityWorld.cs:47-54,70-79,194] · [SimRng.cs:23-72] · [FixedJsonConverter.cs:30-48] · [LobbyUi.cs:313-325] · [DedicatedServer.cs:171-191] · [ProjectChimera.Sim.Tests.csproj:14,53] — the verified current-state citations behind every Pre-flight fact above.

---

## Dev Agent Record

### Agent Model Used

### Debug Log References

### Completion Notes List

### File List
