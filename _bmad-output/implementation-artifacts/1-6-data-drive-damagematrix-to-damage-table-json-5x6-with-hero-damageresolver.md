---
baseline_commit: 053885b
---

# Story 1.6: Data-drive DamageMatrix → damage_table.json (5×6 with Hero) + DamageResolver

Status: review

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a solo developer removing hardcoded balance constants,
I want the hardcoded 4×5 `DamageMatrix` lifted into `resources/data/damage_table.json` (extended to 5×6 with Hero damage/armor types), loaded and quantized via `FixedJsonConverter`, plus a `DamageResolver.Apply(in ctx, amount, type)` that unifies the formula + death/RecordKill/event sequence across the three verified call sites,
so that later authoring builds on data not constants, and there is one damage code path proven checksum-identical.

> **This is Migration Steps 3+4 of the determinism strangler (`game-architecture.md` §D1 / AR-26), combined.** It is the **inverse of Story 1.5**: 1.5 deliberately *moved* both goldens (RNG entered the hash); **this story must keep both goldens byte-identical** — the data-driven table must reproduce the retired hardcoded values bit-for-bit. The work is two net-new classes (`DamageTable`, `DamageResolver`), one net-new data file (`damage_table.json`), a 1-value extension to two enums (Hero, inserted before `COUNT`), and a behavior-preserving re-point of the **three** verified `DamageMatrix.Get` call sites. It adds **no new gameplay behavior** and changes **no balance number** for the existing 4×5 cells.

## Acceptance Criteria

1. **(Table lifted to data, original cells bit-identical, enums stable)** **Given** the hardcoded `DamageMatrix._table` (4 damage × 5 armor) **When** it is moved to `damage_table.json` extended to 5×6 (adding a Hero damage type and a Hero armor type) and loaded via `FixedJsonConverter` **Then** `DamageType`/`ArmorType` enum integer values remain stable keys (Normal=0…Magic=3, Unarmored=0…Fortified=4 unchanged; Hero inserted before `COUNT`) **and** the loaded table's original 4×5 cells match the prior `Fixed.FromFloat` values **bit-for-bit** (`Fixed.Raw` equal).

2. **(One damage path, golden checksum byte-identical)** **Given** `DamageResolver.Apply(in ctx, amount, type)` re-pointed from `CombatSystem.cs:271`, `ProjectileSystem.cs:76`, and `ProjectileSystem.cs:121` **When** the golden scenario **and** the multi-faction golden run **Then** every per-tick `SimChecksum` is **byte-identical to the committed baseline** (formula + Health subtraction + death + `RecordKill` + event order unchanged). **The golden `.txt` files are NOT re-baselined** — they must still verify green unchanged.

3. **(Combat-formula coverage)** **Given** the Tier-1 project **When** the new combat-formula test suite runs **Then** it asserts `DamageResolver`/`DamageTable` output for representative damage/armor pairs (including Hero) against **independently-computed** expected `Fixed` values, satisfying the combat-formula slice of FR-44, and asserts the death sequence (Destroy + `RecordKill` + `UnitKilled`) fires only when Health reaches zero.

4. **(Fail-closed load)** **Given** a malformed `damage_table.json` (NaN/±Inf, out-of-range, missing cell, or wrong dimensions) **When** it is loaded **Then** loading is **rejected with a located error** (the offending JSON path / cell named) rather than silently using defaults.

_Covers: FR-44 (deterministic-sim + combat-formula test coverage), AR-26 (data-drive the hardcoded tables — DamageMatrix 5×6 with Hero), AR-14 (single quantization boundary — load via `FixedJsonConverter`). Depends on: 1.4 (DONE — `Fixed` end-to-end + the `FixedJsonConverter` boundary). Independent of 1.5 (SimRng) — no RNG in this story._

> Brownfield: `DamageMatrix.cs` is a hardcoded `static class` with a `float[,]` 4×5 today. AR-26 clears the data-driven debt now (5×6 Hero). Adds the combat-formula slice of FR-44. **This story is a prerequisite for Story 1.7** (the `ScenarioValidator` validates the damage table) — see `epics.md`:636.

---

## Developer Context

**You (the dev agent) have ONLY this file. Read this whole section before editing anything.** The work is two small net-new classes, one tiny data file, a one-line-each enum extension, and a mechanical re-point of three call sites. The traps are:

1. **Moving a golden.** This is the **opposite** of Story 1.5. If a golden checksum changes, you have a **real regression** — fix the resolver, **do NOT re-record**. Both goldens run melee *and* ranged combat through the table (verified below), so any value drift or reordering *will* surface.
2. **A non-bit-identical table cell.** The loaded 4×5 cells must equal `Fixed.FromFloat(<old literal>)` to the raw int. They will — *if* you author the JSON decimals correctly and route the golden/test path through the in-code `DamageTable.Default` (built from the same float literals). Verify with a test (AC1); never assume.
3. **Adding the GDD's `− armorValue` term.** The as-built formula is `damage = base × multiplier` — there is **no flat armor subtraction in the shipped code**. Adding it (because the GDD/project-context mentions `final = base × matrix − armorValue`) would change every combat checksum and break AC2. **Out of scope.**
4. **Folding the pre-hit event into the resolver.** The `MeleeHit`/`RangedHit`/`SplashHit` event differs per call site (type *and* position) and the splash-secondary path emits none. Keep it at the call site, emitted *before* `Apply`, to preserve event order.
5. **Snapshot vs live armor.** The projectile primary hit uses the armor type **captured at spawn** (`_store.TargetArmor[projId]`), not live armor. The resolver must use the armor the *caller* supplies — do not have it re-read `world.ArmorTypeOf[target]`.

### The shape of the work (2 net-new classes + 1 data file + enum +1 + 3 call-site re-points + 3 new test cases; goldens UNCHANGED)

1. **Extend the enums** (`DamageType`, `ArmorType`) with a `Hero` member **before** `COUNT` (so existing integer values are stable). `DamageType.COUNT` 4→5, `ArmorType.COUNT` 5→6.
2. **Net-new `DamageTable`** (instance, replaces the static `DamageMatrix`): a dense `Fixed[,]` keyed `[(int)DamageType, (int)ArmorType]`, with `Get(DamageType, ArmorType)`, a static `Default` (canonical in-code table), and `FromJson(string)` / `Load(string path)` that deserialize+validate via `FixedJsonConverter`.
3. **Net-new `damage_table.json`** in `resources/data/` (5×6, named-object schema, original values + neutral Hero row/col).
4. **Net-new `DamageResolver`** (`static`) with `Apply(in DamageContext ctx, Fixed amount, DamageType type) → bool died` + the `DamageContext readonly struct`.
5. **Thread `DamageTable`** into `CombatSystem` and `ProjectileSystem` via an **optional trailing ctor param** (`DamageTable? table = null`, defaulting to `DamageTable.Default`). Golden/test builders stay **unchanged** (they get `Default`); `MainScene` passes the JSON-loaded table.
6. **Re-point the 3 call sites** to `DamageResolver.Apply`, preserving event/death order exactly.
7. **Delete the old static `DamageMatrix` class.**
8. **Tests** — `Combat/DamageTableTests.cs` (AC1 + AC4) and `Combat/DamageResolverTests.cs` (AC3). The existing golden suite proves AC2 (must stay green, goldens unchanged).

### Key design decisions (settled here — do NOT re-derive)

**D1 — Replace the `static class DamageMatrix` with an instance `DamageTable`; extend the enums with `Hero` before `COUNT`.** The static global with a hardcoded `float[,]` is exactly the anti-pattern AR-26 removes (balance numbers a creator can't reach). New `DamageType { Normal=0, Pierce=1, Siege=2, Magic=3, Hero=4, COUNT=5 }` and `ArmorType { Unarmored=0, Light=1, Medium=2, Heavy=3, Fortified=4, Hero=5, COUNT=6 }` — **Hero inserted immediately before `COUNT`** so every existing value is stable (AC1) and any code allocating `[(int)DamageType.COUNT, (int)ArmorType.COUNT]` arrays automatically grows to 5×6. Move the two enums into the new `DamageTable.cs` and **delete `DamageMatrix.cs`** (the static `_table`/`_fixed`/`Get` logic is fully superseded). All three `using`-free references resolve unchanged (same `ProjectChimera.Combat` namespace).

**D2 — Thread `DamageTable` via an OPTIONAL trailing ctor param on the two combat systems; default to `DamageTable.Default`.** Both systems already inject collaborators (`CombatEventQueue? events = null`, `MatchStats? stats = null`). Add `DamageTable? table = null` as the **last** ctor param and store `_table = table ?? DamageTable.Default`. Consequences:
- **Golden/test builders (`GoldenScenario.cs:112-113`, `MultiFactionScenario.cs:82-83`) need ZERO changes** — they omit the arg and get `Default`, which is built from the same float literals → byte-identical goldens by construction (AC2-safe with no edit risk).
- **`MainScene.cs:261-262`** passes the JSON-loaded table: `new CombatSystem(_projectiles, _combatEvents, _matchStats, _damageTable)` and the same for `ProjectileSystem`.
- The `DamageContext` carries the `_table` reference the system already holds — no reach-through.

_(Rejected: **table-on-`EntityWorld`** like 1.5's `Rng` — `Rng` is genuinely mutable per-tick sim *state* that advances every draw and belongs with the world; the damage table is immutable per-match *config*, so injecting it into the systems that consume it is the principled home, and keeps `EntityWorld` pure entity-SoA. Rejected: a **required** ctor param — it would force edits to the byte-identical golden builders for no benefit; the optional-with-safe-default form is fail-safe.)_

**D3 — `damage_table.json` is a named-object schema keyed by enum names; deserialized load-time into a `Dictionary<DamageType,Dictionary<ArmorType,Fixed>>`, validated, then baked into a dense `Fixed[,]`.** Schema (see Dev Notes for the full file):
```json
{ "multipliers": { "Normal": { "Unarmored": 1.0, …, "Hero": 1.0 }, … "Hero": { … } } }
```
Rationale: AC1 says "enums remain stable **keys**" → named keys; it is creator-facing data (the platform rule); and `System.Text.Json` on .NET 8 parses enum **dictionary keys by name** (an unknown key like `"Frost"` throws a located `JsonException` — free AC4 coverage). The `Dictionary` exists **only at load time** and is immediately baked into the dense `Fixed[(int)DamageType.COUNT, (int)ArmorType.COUNT]` array that `Get` indexes in-tick — no `Dictionary` enumeration ever happens during a tick, so the (future, Story 1.10b) determinism analyzer is satisfied. Validate by iterating **enum values** (`for d in 0..COUNT`, `for a in 0..COUNT`, assert `ContainsKey`) — not by enumerating the dictionary — so the validation order is itself deterministic.
_(Rejected: array-of-arrays `Fixed[][]` — positional, not "stable keys", less creator-friendly. Rejected: a 30-field named DTO — no way to detect "wrong dimensions" for AC4.)_
_(Fallback if enum dictionary-key parsing misbehaves on your toolchain: deserialize to `Dictionary<string,Dictionary<string,Fixed>>` and `Enum.TryParse` each key, throwing a located error on an unknown name. Same result.)_

**D4 — `DamageTable.Default` is the canonical in-code 5×6, built from the SAME float literals as the retired `DamageMatrix`.** It is the byte-identical oracle: built via `Fixed.FromFloat(<literal>)` at **static init (load-time, allowed)**, exactly mirroring the old `DamageMatrix` static ctor, so the original 4×5 cells are bit-identical **by construction** — independent of any JSON parsing. `Default` serves three roles: the ctor fallback (D2), the **missing-file** fallback in `MainScene` (graceful, matching the existing `FactionDefinition` "File.Exists ? Load : new()" pattern), and the AC1/AC3 test oracle. The Hero row and Hero column are **neutral `1.0`** placeholders (no invented counter — within the 0.7–1.3 soft-counter band) and are **tuned when heroes ship in Epic 3**; they do not affect any current golden (no Hero-typed units exist).

**D5 — `DamageResolver.Apply(in DamageContext ctx, Fixed amount, DamageType type) → bool died` unifies the formula + Health subtraction + death sequence; the pre-hit event and the melee attacker-cleanup stay at the call sites.** The unified body is exactly (byte-identical to all three sites):
```
multiplier = ctx.Table.Get(type, ctx.TargetArmor);
damage     = amount * multiplier;            // NO − armorValue (D8)
world.Health[t] = world.Health[t] - damage;
if (world.Health[t] <= Fixed.Zero) { Events?.Push(UnitKilled, Position[t]); Stats?.RecordKill(FactionOf[t], Killer); world.Destroy(t); return true; }
return false;
```
- **`DamageContext` (`readonly struct`, passed `in`)** carries `(EntityWorld World, int TargetId, ArmorType TargetArmor, Faction Killer, DamageTable Table, CombatEventQueue? Events, MatchStats? Stats)`. The **caller supplies `TargetArmor`** (melee + splash-secondary = live `world.ArmorTypeOf[i]`; projectile-primary = **snapshot** `_store.TargetArmor[projId]`) and **`Killer`** (melee = `world.FactionOf[attacker]`; projectile = `_store.Owner[projId]`).
- **The pre-hit event stays at each call site**, pushed **before** `Apply` (melee `MeleeHit @ Position[target]`; projectile `RangedHit`/`SplashHit @ _store.Position[projId]`; splash-secondary **none**). Folding it into the resolver would reorder or fabricate events → AC2 break.
- **The melee attacker-cleanup stays in `CombatSystem`**, gated on the returned `died`: `if (died) { world.AttackTarget[attacker] = -1; world.Flags[attacker] &= ~EntityFlags.Attacking; }`. The projectile sites ignore the return value (no attacker to clean up). This is why `Apply` returns `bool`.

**D6 — DO NOT re-baseline the goldens (inverse of Story 1.5).** AC2's proof is the **existing** golden suite (`GoldenChecksumReplayTests`, `MultiFactionGoldenTests`) passing with the committed `golden-scenario.golden.txt` / `golden-multifaction.golden.txt` **unchanged**. Both scenarios construct a P1 melee unit and a P1 ranged unit that fight P2 fodder (verified below), so `DamageTable.Get` feeds `Health` into the hash every combat tick. If a golden moves, the refactor changed behavior — **find the divergence and fix it; never set `CHIMERA_GOLDEN_RECORD`.**

**D7 — The loader fails closed with a located error; a missing file falls back to `Default`.** `FromJson` rejects: NaN/±Inf/over-range values (already located `JsonException` from `FixedJsonConverter`, AR-14), **missing cells** / **wrong dimensions** / **unknown enum keys** (explicit validation → `InvalidDataException` naming the offending `[DamageType][ArmorType]`). It must **not** silently substitute defaults for a present-but-invalid file (AC4). A **missing file** (`!File.Exists`) is the one graceful case — `MainScene` falls back to `DamageTable.Default` (matching the established `FactionDefinition` pattern); the loader itself never reads a non-existent path.

**D8 — Scope fence (forward-compat, not this story).** Do **NOT**: add the GDD's `− armorValue` flat reduction (as-built is `base × matrix` only — adding it breaks AC2); add Hero **units** or wire `UnitDefinition.ParsedDamageType`/`ParsedArmorType`'s `switch` for `"Hero"` (Epic 3; no Hero data exists; the new enum value is reachable from code/tests now, which is all this story needs); build `ScenarioApplier`/`ScenarioValidator`/`Validated<T>` (Stories 1.8b / 1.7); touch the N-resource registry or tech-tree (AR-26's other clauses are Epics 3–4).

### Pre-flight facts you MUST NOT re-derive (verified against the codebase at `053885b`)

- **`DamageMatrix`** (`godot/src/Combat/DamageMatrix.cs`): `public static class DamageMatrix`. Holds `private static readonly float[,] _table` (4×5, `:38-45`), a static ctor (`:49-57`) that does `Core.Fixed.FromFloat(_table[r,c])` into `_fixed`, and `public static Core.Fixed Get(DamageType, ArmorType)` (`:62-63`). The two enums `DamageType { Normal,Pierce,Siege,Magic,COUNT=4 }` (`:6-13`) and `ArmorType { Unarmored,Light,Medium,Heavy,Fortified,COUNT=5 }` (`:18-26`) live in **this file** — move them to `DamageTable.cs`. **No `#nullable enable` today** — add it to the new files. [Source: DamageMatrix.cs]
- **The exact original values** (rows = DamageType, cols = ArmorType): Normal `{1.00, 1.00, 0.75, 0.50, 0.35}`; Pierce `{1.50, 1.00, 0.75, 0.35, 0.25}`; Siege `{0.50, 0.50, 1.00, 1.00, 1.50}`; Magic `{1.00, 1.00, 1.00, 1.00, 0.50}`. As `Fixed.FromFloat` raws (`(int)(value*65536)`): `1.00→65536`, `0.75→49152`, `0.50→32768`, `0.35→22937`, `0.25→16384`, `1.50→98304`. These are the AC1/AC3 oracle constants — independently computed, paste them as literals. [Source: DamageMatrix.cs:41-44; FixedPoint.cs `FromFloat`]
- **The THREE `DamageMatrix.Get` call sites (grep-verified — there is no fourth):**
  - `CombatSystem.cs:271` (melee, in `TryDealDamage`): `DamageMatrix.Get(world.DamageTypeOf[attacker], world.ArmorTypeOf[target])`; raw damage `world.AttackDamage[attacker]`; killer `world.FactionOf[attacker]`; pre-hit `_events?.Push(CombatEventType.MeleeHit, world.Position[target])` (`:274`); death block `:277-284` includes the attacker-cleanup `world.AttackTarget[attacker]=-1; world.Flags[attacker]&=~EntityFlags.Attacking;`.
  - `ProjectileSystem.cs:76` (primary, in `ApplyHit`): `DamageMatrix.Get(_store.DmgType[projId], _store.TargetArmor[projId])` — **snapshot armor**; raw damage `_store.Damage[projId]`; killer `_store.Owner[projId]`; pre-hit `_events?.Push(isSplash ? SplashHit : RangedHit, _store.Position[projId])` (`:83-84`); death block `:87-92`; then `if (isSplash) ApplySplash(...)` (`:95-96`).
  - `ProjectileSystem.cs:121` (secondary, in `ApplySplash`): `DamageMatrix.Get(dmgType, world.ArmorTypeOf[i])` — **live armor**; raw damage `damage` (= `_store.Damage[projId]`); killer `owner`; **no pre-hit event**; death block `:124-129`.
  [Source: CombatSystem.cs:248-286; ProjectileSystem.cs:73-131]
- **`CombatSystem` ctor** (`:33`): `CombatSystem(ProjectileStore projectiles, CombatEventQueue? events = null, MatchStats? stats = null)`; fields `_projectiles/_events/_stats`. **`ProjectileSystem` ctor** (`:26`): `ProjectileSystem(ProjectileStore store, CombatEventQueue? events = null, MatchStats? stats = null)`; fields `_store/_events/_stats`. Add `DamageTable? table = null` last to each; store `_table = table ?? DamageTable.Default`. [Source: CombatSystem.cs:33-38; ProjectileSystem.cs:26-31]
- **`EntityWorld`** (`godot/src/Core/EntityWorld.cs`): SoA arrays `readonly Fixed[] Health` (`:87`), `readonly FixedVec3[] Position` (`:83`), `readonly Faction[] FactionOf` (`:89`), `readonly DamageType[] DamageTypeOf` (`:96`), `readonly ArmorType[] ArmorTypeOf` (`:97`), `readonly int[] AttackTarget` (`:91`), `readonly EntityFlags[] Flags` (`:82`), `readonly Fixed[] AttackDamage` (`:94`). `void Destroy(int id)` (`:256`), `bool IsAlive(int id)` (`:269`), `int HighWaterMark`. Core already references the Combat enums (`DamageTypeOf`/`ArmorTypeOf`), so the namespaces interoperate freely (one assembly). [Source: EntityWorld.cs:82-97,256-269]
- **`Faction`** (`EntityWorld.cs:47-54`): `enum Faction : byte { Neutral=0, Player1=1, Player2=2, Player3=3, Player4=4 }`. [Source: EntityWorld.cs]
- **`MatchStats.RecordKill(Faction killedFaction, Faction killerFaction)`** (`godot/src/Core/MatchStats.cs:35`) — argument order is **(victim, killer)**. All three call sites pass `(victimFaction, killerFaction)`; the resolver must too. [Source: MatchStats.cs:35-41]
- **`CombatEventQueue.Push(CombatEventType type, FixedVec3 position)`** (`godot/src/Combat/CombatEventQueue.cs:42`); `enum CombatEventType { MeleeHit, RangedHit, SplashHit, UnitKilled }` (`:6-12`). The queue is presentation feedback (drained by `CombatFeedbackBridge`) — **not folded into `SimChecksum`** — but its push order is still part of AC2's "event order unchanged" contract; preserve it. [Source: CombatEventQueue.cs]
- **`FixedJsonConverter`** (`godot/src/Core/Definitions/FixedJsonConverter.cs:24`): `JsonConverter<Fixed>`; `Read` rejects non-number tokens, NaN/±Inf, and `|f| >= 32768` with a located `JsonException` (System.Text.Json appends the JSON path). This is the AR-14 single quantization boundary — the loader **must** register it (gets AC4's NaN/range half for free). [Source: FixedJsonConverter.cs]
- **`ScenarioSerializer` options pattern** (`godot/src/Core/Definitions/ScenarioSerializer.cs:23-29`) — mirror it for the loader: `new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, Converters = { new JsonStringEnumConverter(), new FixedJsonConverter() } }`. (`ReadCommentHandling.Skip` lets you keep `//` comments in `damage_table.json`.) [Source: ScenarioSerializer.cs]
- **Both goldens run combat through the table** — `GoldenScenario.PopulateScenario` and `MultiFactionScenario.PopulateScenario` each create a P1 melee unit (`DamageType.Normal` vs P2 fodder `ArmorType.Medium` → cell `0.75`), a P1 ranged unit (`DamageType.Pierce`, spawns projectiles), and P2 fodder (`DamageType.Normal` vs P1 melee `ArmorType.Light` → cell `1.00`). So the cells `Normal/Light`, `Normal/Medium`, `Pierce/Medium`, `Pierce/Light` are exercised every run and **must be bit-identical** or the goldens move. [Source: GoldenScenario.cs:146-204; MultiFactionScenario.cs:129-184]
- **Golden/test builders construct the systems directly** at `GoldenScenario.cs:112-113` and `MultiFactionScenario.cs:82-83`: `new CombatSystem(projectiles, combatEvents, stats)` / `new ProjectileSystem(projectiles, combatEvents, stats)`. With the **optional** `table` param (D2) these compile unchanged and receive `Default`. [Source: GoldenScenario.cs; MultiFactionScenario.cs]
- **`MainScene` construction + load pattern** — systems built at `MainScene.cs:261-262`; faction JSON loaded at `:231-239` via `ProjectSettings.GlobalizePath(P1_FACTION_JSON)` + `File.Exists ? FactionDefinition.LoadFromFile(abs) : new FactionDefinition()`. Mirror this for the table: a `DAMAGE_TABLE_JSON = "res://resources/data/damage_table.json"` const, `_damageTable = File.Exists(abs) ? DamageTable.Load(abs) : DamageTable.Default`, loaded **before** `:261`, then passed to both system ctors. [Source: MainScene.cs:228-266]
- **Tier-1 test project** (`godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj`): shared-source globs `..\src\Combat\**\*.cs` (`:15`) and `..\src\Core\**\*.cs` (`:14`) — so `DamageTable.cs`, `DamageResolver.cs`, and `FixedJsonConverter.cs` auto-compile in. **No `.csproj` edit needed** (the new tests use inline JSON strings via `FromJson`, not a file or embedded resource). xUnit 2.9.2. [Source: ProjectChimera.Sim.Tests.csproj:13-36]
- **`resources/data/` layout**: contains `factions/` and `scenarios/` subdirs and no top-level files — `damage_table.json` is the first, placed at `godot/resources/data/damage_table.json`. [Source: glob `godot/resources/data/*`]
- **Data-driven precedent**: existing data classes live in `src/Core/Definitions/` and deserialize from JSON via `JsonPropertyName` + `JsonStringEnumConverter` (`ScenarioData.cs`, `UnitDefinition.cs`). The damage-table runtime + loader co-locate in `src/Combat/DamageTable.cs` because they need the Combat enums; the loader `using ProjectChimera.Core.Definitions;` for `FixedJsonConverter` (one assembly — no cycle problem). [Source: ScenarioData.cs; UnitDefinition.cs]
- **`UnitDefinition.ParsedDamageType`/`ParsedArmorType`** (`UnitDefinition.cs:97-113`) are `switch`es with a `_ => Normal` / `_ => Unarmored` default — a `"Hero"` string would currently fall through to `Normal`/`Unarmored`. **Leave them (D8)**; no faction JSON uses `"Hero"`, and heroes are Epic 3. [Source: UnitDefinition.cs]

### Scope fence — do NOT, in this story

- **Do NOT** re-baseline / re-record either golden (D6). They must stay byte-identical. If one moves, fix the bug.
- **Do NOT** add the `− armorValue` flat-reduction term to the formula (D8). As-built is `base × multiplier`.
- **Do NOT** fold the pre-hit event (`MeleeHit`/`RangedHit`/`SplashHit`) or the melee attacker-cleanup into `DamageResolver` (D5). They stay at the call sites.
- **Do NOT** make `DamageContext` a `class` or pass it by value — it is a `readonly struct` passed `in`. Do NOT make it a `ref struct` (it holds no `Span`/ref field and is fine as a plain `readonly struct`).
- **Do NOT** put the `DamageTable` on `EntityWorld` or change `ISimSystem`/`SimChecksum.Compute` signatures (D2).
- **Do NOT** read live `world.ArmorTypeOf[target]` inside the resolver — use the caller-supplied `ctx.TargetArmor` (preserves the projectile snapshot, D5).
- **Do NOT** add Hero units, wire the `UnitDefinition` `"Hero"` switch cases, or invent Hero balance beyond the neutral `1.0` placeholder (D4/D8).
- **Do NOT** build `ScenarioApplier`/`ScenarioValidator`/`Validated<T>`, or touch the resource registry / tech tree (later stories).
- **Do NOT** introduce any `float`/`double`/`Mathf`/`System.Math.*` into the in-tick path. `Fixed.FromFloat` is permitted **only** in `DamageTable.Default` (load-time, mirrors the retired static ctor) and inside `FixedJsonConverter` (the existing boundary) — never in `Get`, `Apply`, or any per-tick code.

---

## Tasks / Subtasks

- [x] **Task 1 — Extend the enums + net-new `DamageTable` (AC: 1, 4)**
  - [x] Create `godot/src/Combat/DamageTable.cs` (`#nullable enable`, namespace `ProjectChimera.Combat`, **no `using Godot`**). Move `DamageType` and `ArmorType` here from `DamageMatrix.cs`, adding `Hero` **before** `COUNT` (`DamageType.Hero=4, COUNT=5`; `ArmorType.Hero=5, COUNT=6`).
  - [x] `public sealed class DamageTable` holding a dense `private readonly Fixed[,] _cells` sized `[(int)DamageType.COUNT, (int)ArmorType.COUNT]`; `public Fixed Get(DamageType d, ArmorType a) => _cells[(int)d, (int)a];` (in-tick, integer-indexed, no float).
  - [x] `public static DamageTable Default { get; }` — built once from the original float literals (Hero row/col = `Fixed.One`), via `Fixed.FromFloat(<literal>)` at static init (load-time only). This is the byte-identical oracle (D4).
  - [x] `public static DamageTable FromJson(string json)` and `public static DamageTable Load(string absolutePath)` (Load = read file → FromJson). Use the `ScenarioSerializer` options shape (FixedJsonConverter + JsonStringEnumConverter + Skip comments). Deserialize to `Dictionary<DamageType,Dictionary<ArmorType,Fixed>>` (under a `multipliers` DTO property), validate (every enum cell present, exact dimensions, no unknown keys → located `InvalidDataException`; NaN/range → located `JsonException` from the converter), bake into `_cells`.
  - [x] Delete `godot/src/Combat/DamageMatrix.cs`.
  - [x] `dotnet build godot/godot.csproj` → expect compile errors only at the 3 call sites (fixed in Task 3).

- [x] **Task 2 — Net-new `damage_table.json` (AC: 1, 4)**
  - [x] Create `godot/resources/data/damage_table.json` with the named-object 5×6 schema (Dev Notes). Original 4×5 values exactly; Hero row + Hero column all `1.0`. Optional `//` header comment (allowed by `ReadCommentHandling.Skip`).

- [x] **Task 3 — Net-new `DamageResolver` + re-point the 3 call sites (AC: 2)**
  - [x] Create `godot/src/Combat/DamageResolver.cs` (`#nullable enable`, no `using Godot`): `readonly struct DamageContext` (`World, TargetId, TargetArmor, Killer, Table, Events, Stats`) + `static bool Apply(in DamageContext ctx, Fixed amount, DamageType type)` per the D5 body (no `− armorValue`).
  - [x] Add the optional `DamageTable? table = null` ctor param to `CombatSystem` and `ProjectileSystem`; store `_table = table ?? DamageTable.Default`.
  - [x] Re-point `CombatSystem.cs:268-285` (melee): push `MeleeHit` first, then `Apply`, then `if (died)` the attacker-cleanup. Re-point `ProjectileSystem.cs:74-96` (`ApplyHit`): compute `isSplash` + push the hit event first, then `Apply`, then `if (isSplash) ApplySplash`. Re-point `ProjectileSystem.cs:112-130` (`ApplySplash` loop): `Apply` with live armor, no pre-hit event. (Exact replacements in Dev Notes — preserve operation order.)
  - [x] `MainScene.cs`: add the `DAMAGE_TABLE_JSON` const, load `_damageTable` (File.Exists ? Load : Default) before `:261`, pass it to both system ctors.
  - [x] `dotnet build godot/godot.csproj` → green (only the pre-existing CS8632 warnings).

- [x] **Task 4 — `DamageTable` tests: bit-identical cells + fail-closed load (AC: 1, 4)**
  - [x] New `godot/ProjectChimera.Sim.Tests/Combat/DamageTableTests.cs`:
    - **AC1 bit-identical:** loop all 20 original cells; assert `DamageTable.FromJson(canonicalJson).Get(d,a).Raw == DamageTable.Default.Get(d,a).Raw`. Independently pin a representative set against the externally-computed raws (`Normal/Unarmored=65536`, `Normal/Medium=49152`, `Normal/Heavy=32768`, `Normal/Fortified=22937`, `Pierce/Unarmored=98304`, `Pierce/Fortified=16384`) — NOT by calling `Default` (avoids a tautology).
    - **Enum stability:** assert `(int)DamageType.Normal==0 … Magic==3`, `Hero==4`, `COUNT==5`; `(int)ArmorType.Unarmored==0 … Fortified==4`, `Hero==5`, `COUNT==6`.
    - **AC4 fail-closed:** assert located throws (not silent default) for: a NaN value (`JsonException`), an out-of-range value `40000` (`JsonException`), a missing cell, a missing row, an extra/unknown damage row (`"Frost"`), and a row with the wrong column count (`InvalidDataException` naming the offender). Each asserts the exception message locates the problem.
  - [x] `dotnet test --filter FullyQualifiedName~DamageTable` → green.

- [x] **Task 5 — `DamageResolver` combat-formula + death-sequence tests (AC: 3)**
  - [x] New `godot/ProjectChimera.Sim.Tests/Combat/DamageResolverTests.cs`. Build a tiny `EntityWorld` with `Fixed`-authored entities (`Fixed.FromInt`/`FromRaw` only — no `FromFloat`), a `CombatEventQueue`, a `MatchStats`, and `DamageTable.Default`.
    - **Formula (incl. Hero):** for representative pairs assert `Apply` reduces target `Health` by `amount * Get(type, armor)` against independently-pinned `Fixed` (e.g. `amount=10`, Normal vs Medium → Δ raw `491520`; a Hero-damage-vs-Hero-armor pair → full `1.0` → Δ raw equals `amount`).
    - **Death sequence:** lethal damage → `Apply` returns `true`, target not `IsAlive`, `RecordKill` incremented for (victim, killer), one `UnitKilled` event pushed; sub-lethal → returns `false`, target alive, no `RecordKill`, no `UnitKilled`.
    - **Snapshot armor:** `ctx.TargetArmor` (not live world armor) drives the multiplier (pass an armor differing from the entity's `ArmorTypeOf` and assert the supplied one is used).
  - [x] `dotnet test --filter FullyQualifiedName~DamageResolver` → green.

- [x] **Task 6 — Prove AC2: goldens byte-identical, full suite green (AC: 2)**
  - [x] `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` → ALL green, **with `golden-scenario.golden.txt` and `golden-multifaction.golden.txt` UNCHANGED** (`git status` shows no diff on either). If a golden test fails, the refactor diverged behavior — fix `DamageResolver`/`DamageTable.Default`, **do NOT re-record**.
  - [x] `dotnet build godot/godot.csproj` → green.
  - [x] Grep the new files: zero `float`/`double`/`System.Random`/`Mathf`/`Math.`/`using Godot` outside `DamageTable.Default`'s load-time `Fixed.FromFloat`. Confirm `git diff` shows no signature change to `ISimSystem` / `SimChecksum.Compute` / any system `Tick`.

- [x] **Task 7 — In-engine smoke (AC: 2) — optional but recommended**
  - [x] Run the game (Godot MCP `run` or `/godot-verify`): a skirmish still resolves combat and units die normally (the JSON table is wired through `MainScene`). Optionally edit one `damage_table.json` cell, confirm the change takes effect in-engine (proves the data-drive is live, not just `Default`), then revert. _(MainScene is excluded from Tier-1, so this is the only check that the production wiring uses the loaded table; a headless test lands with the Godot-free `ScenarioApplier` in Story 1.8b.)_

---

## Dev Notes

### `damage_table.json` (Task 2) — full file

```json
{
  // Damage effectiveness matrix (AR-26). final = base_damage * multipliers[DamageType][ArmorType].
  // 1.0 = full damage. Rows = attacker damage type; columns = defender armor type.
  // The original 4x5 values are the retired DamageMatrix defaults and MUST stay bit-identical (Story 1.6 AC1).
  // The Hero row and Hero column are neutral (1.0) placeholders, tuned when heroes ship in Epic 3.
  "multipliers": {
    "Normal": { "Unarmored": 1.0, "Light": 1.0, "Medium": 0.75, "Heavy": 0.5,  "Fortified": 0.35, "Hero": 1.0 },
    "Pierce": { "Unarmored": 1.5, "Light": 1.0, "Medium": 0.75, "Heavy": 0.35, "Fortified": 0.25, "Hero": 1.0 },
    "Siege":  { "Unarmored": 0.5, "Light": 0.5, "Medium": 1.0,  "Heavy": 1.0,  "Fortified": 1.5,  "Hero": 1.0 },
    "Magic":  { "Unarmored": 1.0, "Light": 1.0, "Medium": 1.0,  "Heavy": 1.0,  "Fortified": 0.5,  "Hero": 1.0 },
    "Hero":   { "Unarmored": 1.0, "Light": 1.0, "Medium": 1.0,  "Heavy": 1.0,  "Fortified": 1.0,  "Hero": 1.0 }
  }
}
```

### `DamageTable` (Task 1) — skeleton

```csharp
#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions; // FixedJsonConverter

namespace ProjectChimera.Combat
{
    public enum DamageType : byte { Normal = 0, Pierce = 1, Siege = 2, Magic = 3, Hero = 4, COUNT = 5 }
    public enum ArmorType  : byte { Unarmored = 0, Light = 1, Medium = 2, Heavy = 3, Fortified = 4, Hero = 5, COUNT = 6 }

    /// <summary>Data-driven damage multipliers (AR-26): final = base * Get(damage, armor). Loaded from
    /// damage_table.json via the FixedJsonConverter boundary; baked into a dense Fixed[,] for in-tick lookup.</summary>
    public sealed class DamageTable
    {
        private readonly Fixed[,] _cells;
        private DamageTable(Fixed[,] cells) => _cells = cells;

        public Fixed Get(DamageType d, ArmorType a) => _cells[(int)d, (int)a];

        // The canonical in-code table — same float literals as the retired DamageMatrix static ctor, so the
        // original 4x5 cells are bit-identical by construction. Hero row/col neutral (1.0). LOAD-TIME FromFloat.
        public static DamageTable Default { get; } = BuildDefault();

        private static DamageTable BuildDefault()
        {
            var c = new Fixed[(int)DamageType.COUNT, (int)ArmorType.COUNT];
            void Set(DamageType d, float un, float li, float me, float he, float fo, float hero)
            {
                c[(int)d,(int)ArmorType.Unarmored]=Fixed.FromFloat(un); c[(int)d,(int)ArmorType.Light]=Fixed.FromFloat(li);
                c[(int)d,(int)ArmorType.Medium]=Fixed.FromFloat(me);    c[(int)d,(int)ArmorType.Heavy]=Fixed.FromFloat(he);
                c[(int)d,(int)ArmorType.Fortified]=Fixed.FromFloat(fo); c[(int)d,(int)ArmorType.Hero]=Fixed.FromFloat(hero);
            }
            Set(DamageType.Normal, 1.00f,1.00f,0.75f,0.50f,0.35f, 1.0f);
            Set(DamageType.Pierce, 1.50f,1.00f,0.75f,0.35f,0.25f, 1.0f);
            Set(DamageType.Siege,  0.50f,0.50f,1.00f,1.00f,1.50f, 1.0f);
            Set(DamageType.Magic,  1.00f,1.00f,1.00f,1.00f,0.50f, 1.0f);
            Set(DamageType.Hero,   1.0f, 1.0f, 1.0f, 1.0f, 1.0f,  1.0f);
            return new DamageTable(c);
        }

        private sealed class Dto
        {
            [JsonPropertyName("multipliers")]
            public Dictionary<DamageType, Dictionary<ArmorType, Fixed>>? Multipliers { get; set; }
        }

        private static readonly JsonSerializerOptions _opts = new()
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter(), new FixedJsonConverter() },
        };

        public static DamageTable Load(string absolutePath) => FromJson(File.ReadAllText(absolutePath));

        public static DamageTable FromJson(string json)
        {
            // NaN/Inf/over-range rejected here (located JsonException from FixedJsonConverter) — AC4 half #1.
            Dto? dto = JsonSerializer.Deserialize<Dto>(json, _opts);
            if (dto?.Multipliers is null)
                throw new InvalidDataException("damage_table: missing required 'multipliers' object.");

            var cells = new Fixed[(int)DamageType.COUNT, (int)ArmorType.COUNT];
            // Iterate by ENUM VALUE (deterministic, no Dictionary enumeration) — AC4 half #2.
            for (int d = 0; d < (int)DamageType.COUNT; d++)
            {
                if (!dto.Multipliers.TryGetValue((DamageType)d, out var row) || row is null)
                    throw new InvalidDataException($"damage_table: missing row '{(DamageType)d}'.");
                if (row.Count != (int)ArmorType.COUNT)
                    throw new InvalidDataException(
                        $"damage_table: row '{(DamageType)d}' has {row.Count} cells, expected {(int)ArmorType.COUNT}.");
                for (int a = 0; a < (int)ArmorType.COUNT; a++)
                {
                    if (!row.TryGetValue((ArmorType)a, out var v))
                        throw new InvalidDataException($"damage_table: missing cell [{(DamageType)d}][{(ArmorType)a}].");
                    cells[d, a] = v;
                }
            }
            if (dto.Multipliers.Count != (int)DamageType.COUNT)
                throw new InvalidDataException(
                    $"damage_table: {dto.Multipliers.Count} rows, expected {(int)DamageType.COUNT} (unknown damage type?).");
            return new DamageTable(cells);
        }
    }
}
```

### `DamageResolver` (Task 3) — full

```csharp
#nullable enable
using ProjectChimera.Core;

namespace ProjectChimera.Combat
{
    /// <summary>Inputs for one damage application. The CALLER supplies TargetArmor (live or snapshot) and
    /// Killer, so the projectile path keeps its spawn-time armor snapshot.</summary>
    public readonly struct DamageContext
    {
        public readonly EntityWorld World;
        public readonly int TargetId;
        public readonly ArmorType TargetArmor;
        public readonly Faction Killer;
        public readonly DamageTable Table;
        public readonly CombatEventQueue? Events;
        public readonly MatchStats? Stats;

        public DamageContext(EntityWorld world, int targetId, ArmorType targetArmor, Faction killer,
                             DamageTable table, CombatEventQueue? events, MatchStats? stats)
        { World = world; TargetId = targetId; TargetArmor = targetArmor; Killer = killer; Table = table; Events = events; Stats = stats; }
    }

    /// <summary>The single damage code path (AR-26 / FR-44). final = amount * matrix[type][armor]
    /// (NO flat armor subtraction — as-built). Returns true if the target died.</summary>
    public static class DamageResolver
    {
        public static bool Apply(in DamageContext ctx, Fixed amount, DamageType type)
        {
            EntityWorld world = ctx.World;
            int t = ctx.TargetId;
            Fixed multiplier = ctx.Table.Get(type, ctx.TargetArmor);
            Fixed damage = amount * multiplier;
            world.Health[t] = world.Health[t] - damage;
            if (world.Health[t] <= Fixed.Zero)
            {
                ctx.Events?.Push(CombatEventType.UnitKilled, world.Position[t]);
                ctx.Stats?.RecordKill(world.FactionOf[t], ctx.Killer);
                world.Destroy(t);
                return true;
            }
            return false;
        }
    }
}
```

### Call-site re-points (Task 3) — preserve operation order exactly

**`CombatSystem.cs` melee branch (replaces `:269-284`):**
```csharp
// Melee — instant damage (event BEFORE Apply; attacker-cleanup AFTER, gated on death — order preserved)
_events?.Push(CombatEventType.MeleeHit, world.Position[target]);
var ctx = new DamageContext(world, target, world.ArmorTypeOf[target],
                            world.FactionOf[attacker], _table, _events, _stats);
if (DamageResolver.Apply(in ctx, world.AttackDamage[attacker], world.DamageTypeOf[attacker]))
{
    world.AttackTarget[attacker] = -1;
    world.Flags[attacker]       &= ~EntityFlags.Attacking;
}
```

**`ProjectileSystem.ApplyHit` (replaces `:76-92`):**
```csharp
Fixed splashRadius = _store.SplashRadius[projId];
bool  isSplash     = splashRadius > Fixed.Zero;
_events?.Push(isSplash ? CombatEventType.SplashHit : CombatEventType.RangedHit, _store.Position[projId]);

var ctx = new DamageContext(world, targetId, _store.TargetArmor[projId],   // snapshot armor
                            _store.Owner[projId], _table, _events, _stats);
DamageResolver.Apply(in ctx, _store.Damage[projId], _store.DmgType[projId]);

if (isSplash)
    ApplySplash(world, projId, targetId, splashRadius);
```

**`ProjectileSystem.ApplySplash` loop body (replaces `:121-129`):**
```csharp
var ctx = new DamageContext(world, i, world.ArmorTypeOf[i], owner, _table, _events, _stats); // live armor
DamageResolver.Apply(in ctx, damage, dmgType);
```

### Constraints & gotchas

- **`dotnet build` / `dotnet test` are authoritative** for C# correctness; the Godot MCP `run` does not rebuild the test assembly. Build + test before declaring done. [Source: LEARNINGS / 1.1–1.5 Dev Notes]
- **This story must NOT move the goldens** — the single biggest difference from Story 1.5 (which required moving them). A green golden suite with **unchanged** `.txt` files is AC2's proof. A moved golden = a real behavior change to fix. [Source: D6; GoldenChecksumReplay.cs]
- **Bit-identical narrowing is real but verify it.** `(float)<json-double> == <float-literal>` for all six distinct values here (1.0, 0.75, 0.5, 0.35, 0.25, 1.5) — short decimals narrow without double-rounding error — so the JSON path equals `Default`. The AC1 test pins this against externally-computed raws; if it ever fails, it is a JSON-authoring bug, caught at test time, **not** a reason to re-baseline. [Source: D4; FixedJsonConverter.cs]
- **No `− armorValue`.** The shipped formula is `base × multiplier`. The GDD/project-context line `final = base × matrix − armorValue` describes a *future* design, not the as-built code. Implementing it here breaks AC2. [Source: CombatSystem.cs:270-272; ProjectileSystem.cs:76-77]
- **Projectile primary uses snapshot armor.** `_store.TargetArmor[projId]` was captured at `CombatSystem.TryDealDamage` spawn time; the resolver must use the caller-supplied `ctx.TargetArmor`, not re-read live armor. [Source: ProjectileSystem.cs:76; CombatSystem.cs:257-265]
- **`Fixed` multiply is commutative** (integer `(a.Raw * b.Raw) >> 16`), so `amount * multiplier` == the old `rawDamage * multiplier`. Keep `amount * multiplier` for obvious equivalence. [Source: FixedPoint.cs]
- **Sim/Presentation boundary:** `DamageTable`/`DamageResolver` are `src/Combat` (sim) and Godot-free — `GodotFreeBoundaryTest` fails the build if a `using Godot` sneaks in. The only `Fixed.FromFloat` allowed is in `DamageTable.Default` (load-time) and `FixedJsonConverter`. [Source: project-context.md; GodotFreeBoundaryTest.cs]
- **No new dependencies, no `.csproj` edit.** `DamageTable.cs`/`DamageResolver.cs` auto-glob into both `godot.csproj` and the test project via `..\src\Combat\**`. Tests use inline JSON via `FromJson` — no file/embedded resource. [Source: ProjectChimera.Sim.Tests.csproj:13-36]
- **Pre-existing CS8632** nullable warnings are not this story's bug — leave them. [Source: deferred-work.md]
- **Independence rule (from the 1.1/1.4/1.5 reviews):** derive AC1/AC3 expected values from the algorithm (the raws `65536/49152/32768/22937/16384/98304`), not by calling `Default`/`Get` and asserting they equal themselves. [Source: 1.5 review findings]

### Project Structure Notes

- **NEW:** `godot/src/Combat/DamageTable.cs` (enums + table + Default + loader), `godot/src/Combat/DamageResolver.cs` (context + Apply), `godot/resources/data/damage_table.json`. **NEW tests:** `Combat/DamageTableTests.cs`, `Combat/DamageResolverTests.cs`.
- **EDIT:** `godot/src/Combat/CombatSystem.cs` (ctor `table` param + melee re-point), `godot/src/Combat/ProjectileSystem.cs` (ctor `table` param + 2 re-points), `godot/src/Core/MainScene.cs` (const + load + pass table to 2 ctors).
- **DELETE:** `godot/src/Combat/DamageMatrix.cs`.
- **UNCHANGED (must stay so):** `GoldenScenario.cs`, `MultiFactionScenario.cs`, `golden-scenario.golden.txt`, `golden-multifaction.golden.txt`, `SimChecksum.cs`, `ISimSystem`, `SimulationLoop.cs`.
- **No** `Effects/`, `Dsl/`, `Validation/`, `ScenarioApplier` — later stories.

### Project Context Rules

_Extracted from `_bmad-output/project-context.md` + `game-architecture.md` — these govern every edit here:_

- **No gameplay logic, balance number, counter, or rule may be hardcoded in a path a creator can't reach** — the platform rule. This story is the literal embodiment: the 4×5 `float[,]` becomes creator-editable `damage_table.json`. [Source: project-context.md "Everything is data-driven"]
- **Combat formula (don't re-derive it):** `final = base × matrix[damageType][armorType] − armorValue`; types Normal/Pierce/Siege/Magic/**Hero**, armors Unarmored/Light/Medium/Heavy/Fortified/**Hero**; soft counters 0.7–1.3 default. **But the as-built code omits `− armorValue`** — match the code, not the doc (D8). The 5×6 with Hero is exactly what this story adds. [Source: project-context.md "Combat formula"]
- **All sim math uses `Fixed` (16.16); `Fixed.FromFloat` is load-time only.** `Get`/`Apply` are integer-only; `Default` and `FixedJsonConverter` are the only load-time `FromFloat` sites. [Source: project-context.md "Determinism"]
- **Reuse existing systems; SoA; composition over inheritance; data classes in `Definitions` deserialize JSON.** `DamageTable` reuses `FixedJsonConverter` (AR-14) and the `ScenarioSerializer` options shape; no parallel converter, no new dependency. [Source: project-context.md "Data layout" / "Conventions"]
- **Engine/runtime:** Godot 4.6.3 target, .NET 8 (`net8.0`); assembly/namespace `ProjectChimera.*`; project files `godot.csproj`/`godot.sln`; Tier-1 test project `ProjectChimera.Sim.Tests` (xUnit, Godot-free). [Source: project-context.md "Technology Stack"]

### References

- [Source: epics.md#Story-1.6 (lines 600-618)] — story statement; the 4 ACs (lift 4×5→5×6 Hero via FixedJsonConverter with stable enum keys + bit-identical original cells; `DamageResolver.Apply(in ctx, amount, type)` re-pointed from the 3 sites, golden byte-identical; combat-formula test incl. Hero for FR-44; malformed table → located error); "Migration Steps 3+4 combined"; Covers FR-44/AR-26/AR-14; Depends on 1.4.
- [Source: epics.md (line 211) — AR-26] — "lift the 4×5 `DamageMatrix` → `damage_table.json`, extended to 5×6 by inserting Hero damage/armor types before COUNT."
- [Source: epics.md#Story-1.7 (line 636)] — 1.7 depends on 1.6 ("validates the damage table"); do not build the validator here.
- [Source: DamageMatrix.cs:6-63] — the enums, the 4×5 `_table` values, the static-ctor `Fixed.FromFloat` build, and `Get` being replaced.
- [Source: CombatSystem.cs:248-286] · [ProjectileSystem.cs:73-131] — the 3 call sites with their exact pre-hit events, death blocks, killer-faction sources, armor sources, and the melee attacker-cleanup.
- [Source: GoldenScenario.cs:84-204] · [MultiFactionScenario.cs:53-184] — both goldens build melee+ranged combat through the table and construct the systems directly (AC2's byte-identical surface; the unchanged-builder D2 target).
- [Source: FixedJsonConverter.cs:24-55] · [ScenarioSerializer.cs:23-40] — the AR-14 quantization boundary + the options pattern the loader mirrors.
- [Source: MatchStats.cs:35-41] · [CombatEventQueue.cs:6-46] · [EntityWorld.cs:47-97,256-269] — `RecordKill(victim,killer)`, `Push(type,pos)`/`CombatEventType`, the SoA arrays + `Faction` enum + `Destroy`/`IsAlive` the resolver/context use.
- [Source: ProjectChimera.Sim.Tests.csproj:13-36] — `..\src\Combat\**` shared-source glob (new files auto-compile; no csproj edit).

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (Claude Opus 4.8) — gds-dev-story workflow.

### Debug Log References

- `dotnet build godot/ProjectChimera.Sim.Tests/...` → **green** (only the 7 pre-existing CS8632 nullable warnings in GatheringSystem/SimulationLoop/FlowFieldSystem — none in new files).
- `dotnet build godot/godot.csproj` → **green** (same 7 warnings; validates the `MainScene` wiring).
- `dotnet test godot/ProjectChimera.Sim.Tests/...` → **97 passed / 0 failed** (includes both golden suites + 15 new tests).
- Two transient test compile errors fixed before the green run: (1) missing `using ProjectChimera.Core;` in `DamageTableTests` (CS0103 on `Fixed`); (2) `Apply(in Ctx(...))` passed an rvalue with the explicit `in` keyword (CS8156) → dropped `in` so the compiler materializes the readonly-ref temporary.
- In-engine smoke (Godot 4.6.3-stable, MCP): project ran with `is_playing=true` and **zero startup errors** → the real `damage_table.json` loads through `MainScene` → `DamageTable.Load` without throwing (the production path is excluded from Tier-1).
- `git diff --stat -- "*golden*.txt"` → **empty** (goldens NOT re-baselined; AC2 honored).

### Completion Notes List

- **All 4 ACs satisfied.**
  - **AC1** (lift to data, original cells bit-identical, enums stable): `DamageTableTests` asserts all 20 original JSON cells equal `DamageTable.Default` to the raw int, pins a representative set against externally-computed raws (65536/49152/32768/22937/98304/16384 — not via `Default`, no tautology), and pins enum integer values (Hero=4/COUNT=5; Hero=5/COUNT=6).
  - **AC2** (one damage path, golden byte-identical): `GoldenChecksumReplayTests` + `MultiFactionGoldenTests` pass with `golden-scenario.golden.txt` / `golden-multifaction.golden.txt` **unchanged on disk**. `DamageResolver` re-pointed from `CombatSystem` (melee) + `ProjectileSystem` (primary + splash); operation order preserved (pre-hit event before `Apply`; melee attacker-cleanup gated on the returned `died`).
  - **AC3** (combat-formula coverage incl. Hero): `DamageResolverTests` pins the formula (Normal/Medium Δ491520; Hero/Hero = full amount), the death sequence (Destroy + RecordKill(victim,killer) + one UnitKilled, only on lethal), and snapshot-armor (caller's `ctx.TargetArmor` wins over live world armor).
  - **AC4** (fail-closed load): located throws for NaN (`JsonException`), out-of-range 40000 (`JsonException` with `.Path` naming the cell), missing row, wrong column count, unknown enum key `"Frost"`, and missing `multipliers` object — never a silent default.
- **Key design decisions honored:** D2 (optional trailing `DamageTable? table = null` ctor param → golden builders unchanged, byte-identical by construction); D4 (`Default` from the same float literals, load-time `Fixed.FromFloat` only); D5 (resolver body = formula + Health − damage + death; events/cleanup at call sites); D6 (goldens NOT re-recorded); D7 (malformed = located error, missing file = `Default` fallback in `MainScene`).
- **Determinism:** `DamageResolver` is fully float-free; the only `Fixed.FromFloat` in new code is in `DamageTable.BuildDefault` (load-time, allowed). `Get`/`Apply` are integer-indexed. No `using Godot`, `System.Random`, `Mathf`, or `Math.*` in new sim code.
- **Scope fences honored (D8):** no `− armorValue` term added; no Hero units / `UnitDefinition` `"Hero"` switch; no `ScenarioValidator`/`ScenarioApplier`/resource-registry work; no `ISimSystem`/`SimChecksum.Compute`/system-`Tick` signature changes.
- **Open Decision #1 (Hero matrix values):** authored as neutral 1.0 placeholders per the spec; no Hero-typed units exist until Epic 3, so no current golden is affected. Awaiting Alec's call on any intended Hero interactions (pure data, zero code change).

### File List

**NEW**
- `godot/src/Combat/DamageTable.cs` — `DamageType`/`ArmorType` enums (Hero before COUNT), `DamageTable` (dense `Fixed[,]` + `Get` + `Default` + `FromJson`/`Load`).
- `godot/src/Combat/DamageResolver.cs` — `DamageContext` (`readonly struct`) + `static bool Apply(in ctx, amount, type)`.
- `godot/resources/data/damage_table.json` — 5×6 named-object multiplier table (original 4×5 values + neutral Hero row/col).
- `godot/ProjectChimera.Sim.Tests/Combat/DamageTableTests.cs` — AC1 + AC4.
- `godot/ProjectChimera.Sim.Tests/Combat/DamageResolverTests.cs` — AC3.

**MODIFIED**
- `godot/src/Combat/CombatSystem.cs` — optional `DamageTable? table` ctor param; melee branch re-pointed to `DamageResolver.Apply`.
- `godot/src/Combat/ProjectileSystem.cs` — optional `DamageTable? table` ctor param; `ApplyHit` (primary, snapshot armor) + `ApplySplash` (secondary, live armor) re-pointed.
- `godot/src/Core/MainScene.cs` — `DAMAGE_TABLE_JSON` const, `_damageTable` field, File.Exists?Load:Default load, table passed to both combat-system ctors.
- `_bmad-output/implementation-artifacts/sprint-status.yaml` — story status tracking (ready-for-dev → in-progress → review).

**DELETED**
- `godot/src/Combat/DamageMatrix.cs` — superseded by `DamageTable` (no remaining references).

### Change Log

| Date | Change |
|------|--------|
| 2026-06-23 | Story 1.6 context created (gds-create-story). Status → ready-for-dev. |
| 2026-06-23 | Story 1.6 implemented (gds-dev-story): hardcoded `DamageMatrix` (4×5) lifted to data-driven `DamageTable` / `damage_table.json` (5×6 with Hero) via the `FixedJsonConverter` boundary; net-new `DamageResolver` unifies the formula + death sequence across the 3 call sites; goldens byte-identical (unchanged). All 7 tasks complete; 97 Tier-1 tests green; in-engine smoke clean. Status → review. |

---

## Open Decisions for Alec (surfaced during analysis — none block dev)

1. **Hero matrix values.** The Hero row and Hero column are authored as neutral `1.0` placeholders (no invented counter; within the soft-counter band) because no Hero-typed units exist until Epic 3 and the GDD doesn't pin Hero multipliers. If you have intended Hero interactions (e.g. heroes take reduced damage from `Normal`, or Hero damage is reduced vs `Fortified`), say so and they'll be authored into `damage_table.json` — it's pure data, zero code change.
2. **Table threading: ctor injection vs EntityWorld home.** Recommended (and specced) is an optional `DamageTable? table` ctor param defaulting to `DamageTable.Default` — keeps the goldens byte-identical with zero builder edits and treats the table as the immutable config it is. The 1.5-style "put it on `EntityWorld`" alternative was rejected (the table isn't mutable per-tick state like `Rng`). Flag if you'd prefer the on-world form for consistency.
3. **Missing-file policy.** A *malformed* table is a hard located error (AC4). A *missing* `damage_table.json` falls back to `DamageTable.Default` (matching the existing `FactionDefinition` graceful pattern). If you'd rather a missing shipped data file be a hard failure too, that's a one-line change in `MainScene`.

---

### Review Findings

_Adversarial code review (`gds-code-review`), 2026-06-23 — 3 parallel layers (Blind Hunter, Edge Case Hunter, Acceptance Auditor) on diff `053885b..2f5f04a`. **All 4 ACs verified SATISFIED** — the Acceptance Auditor re-ran the 97-test Tier-1 suite green, independently recomputed the pinned `Fixed` raws (incl. `0.35f→22937`), and confirmed both goldens byte-identical to baseline via `git diff`. 4 findings dismissed as confirmed non-issues (`ex.Path` reliability, `NaN` rejection, float-vs-double quantization — all settled by the live run; empty-input still throws so AC4 holds). 2 forward-hardening findings remain (neither is triggered by the shipped data):_

- [ ] [Review][Decision] Loader does not fully fail-closed on duplicate JSON keys — `DamageTable.FromJson` deserializes `multipliers` into a `Dictionary<,>`, so a duplicated row or cell key in `damage_table.json` (a plausible creator copy-paste) silently resolves **last-wins**: `System.Text.Json` de-duplicates, `row.Count` and `Multipliers.Count` still equal `COUNT`, every validation check passes, and the earlier value is silently dropped — a silent, un-located balance change, contrary to AC4's "fail closed with a located error, never silent." Verified with a standalone .NET 8 repro (Blind + Edge layers). NOT triggered by the shipped table (no duplicate keys) — only matters for future creator-authored content. A proper fix needs duplicate-key detection (e.g. parse via `JsonDocument`/`Utf8JsonReader` and reject when the raw property count ≠ distinct-key count), which STJ does not do natively for dictionaries — a non-trivial add. Related: the `row.Count != COUNT` dimension check (`DamageTable.cs:128`) is a weak guard — the real completeness guarantee is the per-cell `TryGetValue` (`:133`); the WrongColumnCount test does not isolate the dimension case. DECISION NEEDED: harden now, or accept as a known limitation for 1.0 (the data-authoring UI will validate later)? [DamageTable.cs:115-142]
- [ ] [Review][Patch] `DamageResolver.Apply` has no aliveness guard — latent phantom-kill (duplicate `UnitKilled` event + inflated `RecordKill`) if a future caller invokes it on an already-dead / ≤0-HP target; not triggerable today (all 3 call sites guard upstream — verified), but the resolver is the documented single reusable damage path, and later epics (abilities, DoT via the Effect-Graph) will add callers. Safe one-line guard `if (!world.IsAlive(t)) return false;` at the top of `Apply` — no-op for current goldens, so AC2-preserving. [DamageResolver.cs:51-66]
