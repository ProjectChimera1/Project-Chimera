---
baseline_commit: baf9ae5
---

# Story 1.4: Fixed end-to-end — FixedJsonConverter quantization + nondeterminism negative tests

Status: review

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a solo developer guaranteeing cross-platform bit-identical sim,
I want a single `FixedJsonConverter` that quantizes at deserialize and rejects NaN/Inf/over-range, `Fixed.FromFloat` allow-listed only inside the converter, zero in-tick float in the trigger path, and negative tests for the three latent nondeterminisms (in-tick `Fixed→float→string` round-trip, unstable `Array.Sort`, `Dictionary`-iteration order),
so that there is exactly one quantization boundary and no float or unordered-collection can leak into gameplay math.

> **This is migration Step "D3.0 + the A17/ordering slice of D3.4" of the strangler — the float-quantization-boundary keystone.** Story 1.3b removed the *first* of AR-16's three nondeterminisms (the `ScenarioDirector` `Fixed→float→"F2"→TryParse` round-trip → Fixed-vs-Fixed + `InvariantCulture`) but deliberately fenced four items to **this** story: the residual `Fixed.FromFloat` at the two compare sites, the `add_resources` `Fixed.FromFloat`, the `create_timer` float math, the unstable `Array.Sort`, and the `Dictionary`-backed `_timers` enumeration. This story builds the **net-new `FixedJsonConverter`** (it does not exist today), converts the four in-tick-read trigger fields to `Fixed` so those `FromFloat` sites vanish, replaces the unstable trigger sort with a stable total order, makes the `_timers` enumeration deterministic, and gates all three AR-16 nondeterminisms with negative tests. The golden checksum stays **byte-identical** (the converter quantizes identically to the current `FromFloat`; the sort tiebreak cannot change the empty-trigger goldens).

## Acceptance Criteria

1. **(FixedJsonConverter: one quantization boundary, NaN/Inf/over-range rejected with a located error)** **Given** JSON content with a fractional numeric value bound to a `Fixed` model field **When** it is deserialized via `FixedJsonConverter` (a `System.Text.Json` `JsonConverter<Fixed>`) **Then** it is quantized to `Fixed` at the parse boundary (identical raw to `Fixed.FromFloat`), and any `NaN`, `±Infinity`, or value whose magnitude would overflow 16.16 (`|value| ≥ 32768`) is **rejected** with a located `JsonException` (carrying the JSON property path) rather than silently producing `0` / `int.Min`/`int.Max`. The converter is registered on every options instance that deserializes content carrying `Fixed` fields (the scenario path and the two AI-ingest paths).

2. **(No in-tick float / no `Fixed.FromFloat` outside the converter allow-list in the trigger tick path)** **Given** the 30 Hz tick **When** a float/`Fixed.FromFloat` audit runs over `ScenarioDirector` (system #9 in the tick) **Then** no float gameplay math and no `Fixed.FromFloat` call exist in the trigger evaluation/condition/action path — the four in-tick-read trigger fields (`TriggerEvent.Amount`, `TriggerCondition.Amount`, `TriggerAction.Amount`, `TriggerAction.TimerSeconds`) are `Fixed`, the two compare sites are Fixed-vs-Fixed, `add_resources` adds a `Fixed`, and `create_timer` computes ticks via `Fixed` math. The one remaining in-tick float in the sim (`FogOfWarSystem`) is documented as out of scope (owned by Story 6.5).

3. **(Trigger sort is a stable total order; negative test fails if the unstable sort returns)** **Given** the as-built unstable `Array.Sort(order, (a,b) => Priority[b] - Priority[a])` (`ScenarioDirector.cs:196`) **When** it is replaced with a total order **(Priority desc, then ascending declaration index)** **Then** equal-priority triggers fire in ascending declaration-index order, a negative test asserts that exact ordering, and the test **FAILS** if the tiebreak is removed (the comparator reverts to priority-only / an unstable sort).

4. **(Negative tests for the timer/variable iteration-order and float round-trip nondeterminisms; checksum byte-identical)** **Given** a scenario exercising `Dictionary`-backed timers/variables and the (removed) in-tick `Fixed→float→string` round-trip **When** the `_timers` enumeration is made deterministic (stable order, independent of insertion order) and the scenario runs twice — and across two runs whose timers are **created in different declaration orders** — **Then** the per-tick `SimChecksum` sequences are byte-identical, and the test **FAILS** if the `Dictionary`-iteration order dependence or the float round-trip is reintroduced into the tick. (`_variables` is key-access only — no enumeration change needed; folding timers/variables into `SimChecksum` is **deferred to Story 7.2** and is NOT done here.)

_Covers: FR-44 (deterministic-sim + headless test coverage), FR-47 (regression-guarded determinism), AR-14 (single quantization boundary), AR-16 (the two remaining latent nondeterminisms + negative-test gating of all three). Depends on: 1.3b (DONE — the first AR-16 fix + the four explicit fences to this story)._

> Brownfield hardening of `FixedPoint.cs` (net-new converter) and the `ScenarioDirector` trigger path. **Scope contradiction to be aware of:** Story 7.1a's AC names the *same* `ScenarioDirector` lines, the *same* total order, and *also* Covers AR-16 — but 7.1a/7.1b/7.2 do the **full** rebuild (graph IR with persistent node-ids, typed/scoped `DslVarTable` SoA folded into `SimChecksum`). This story does the **minimal flat-array determinism fix** (declaration-index tiebreak, deterministic timer enumeration) that 7.x later supersedes; it is the deterministic baseline 7.1a's note requires ("Must land before the IR rebuild"). See **Scope fence** below.

---

## Developer Context

**You (the dev agent) have ONLY this file. Read this whole section before editing anything.** This story has **two independent halves that share one no-regression gate**: (A) build the net-new `FixedJsonConverter` and route the four in-tick trigger fields through it (AR-14); (B) kill AR-16's two remaining nondeterminisms (unstable sort, `Dictionary` timer enumeration) and gate all three with negative tests. The hardest things are **discipline** (do NOT start the broad model-wide float→Fixed sweep, do NOT build `ContentLoader`/`Validated<T>`/`ScenarioApplier`/the graph-IR/the `DslVarTable`, do NOT fold timers/variables into `SimChecksum` — each is a later story) and **honest testing** (the goldens have empty triggers, so they prove "no regression," NOT that any of this works — the negative tests are the only proof, and a naive "run twice in one process" does NOT catch the bugs because both the introsort and `Dictionary` are deterministic *within a single runtime/insertion order*; the guards must vary insertion/declaration order or assert an exact order).

### The shape of the work (1 net-new converter + 4 field type changes + 3 surgical `ScenarioDirector` edits + 1 LLMService-validation fix + ~4 new test files; golden byte-identical)

1. **Net-new `FixedJsonConverter : JsonConverter<Fixed>`** in `godot/src/Core/Definitions/FixedJsonConverter.cs` — read a JSON number, reject NaN/Inf/over-range with a located error, quantize via `Fixed.FromFloat` (the **only** allow-listed call site); write back as a number.
2. **Register the converter** on the three options that deserialize trigger-bearing content: `ScenarioSerializer._options` (`ScenarioSerializer.cs:23-29`) and the two inline AI-ingest options (`LLMService.cs:265`, `:508`).
3. **Change four trigger model fields from `float` → `Fixed`** (`TriggerDefinition.cs`): `TriggerEvent.Amount` (`:75`), `TriggerCondition.Amount` (`:109`), `TriggerAction.Amount` (`:175`), `TriggerAction.TimerSeconds` (`:172`). *(Leave `Duration` `:166` — presentation toast — and `X`/`Z` `:153/:156` as `float`; per the AC2 audit they are NOT read in the tick. See Scope fence.)*
4. **De-float the three in-tick `ScenarioDirector` sites** (`:261`, `:300`, `:341-342`, `:347`) now that the fields are `Fixed` — Fixed-vs-Fixed compares, `Fixed` `AddOre`, `Fixed`-math timer ticks. **The ONLY other caller of a converted field is `LLMService.Validate` Pass 5 (`:315-316`)** — there is **no MainScene/UI/trigger-editor caller** (verified — see the caller-surface fact below). The spawn/message presentation path reads `X`/`Z`/`Count`/`Text`/`Duration`, never `Amount`/`TimerSeconds`.
5. **Replace the unstable trigger sort** (`:196`) with a stable total order: Priority desc, then ascending declaration index.
6. **Make `_timers` enumeration deterministic** (`:150-163`): iterate a sorted snapshot of keys (ordinal `InvariantCulture`) — or an insertion-ordered structure — so same-tick expiries emit in a stable order. **Do NOT** change `_variables` (key-access only).
7. **Add the negative tests** (AC1/AC3/AC4) and re-confirm both goldens are byte-identical.
8. **Verify:** `dotnet test` all green (the existing 41 tests unchanged + the new ones), `dotnet build godot/godot.csproj` green, both goldens unchanged on disk.

### Key design decisions (do not re-derive these — they are settled here)

**D1 — `FixedJsonConverter` is a `System.Text.Json` `JsonConverter<Fixed>`; the read path is the single quantization boundary.** The whole codebase deserializes with `System.Text.Json` (no Newtonsoft, no Godot `Json`) — verified. The converter:
  - **Read:** accept `JsonTokenType.Number`; read as `double` (handles both integer and fractional JSON numbers); reject `double.IsNaN(d) || double.IsInfinity(d)`; reject over-range `|d| >= 32768d` (anything `≥ 32768` overflows the `(int)(value * 65536)` cast in `Fixed.FromFloat` and wraps — this is exactly the ≥32768 overflow the 1.3b review deferred here); then quantize `Fixed.FromFloat((float)d)`. Throw `JsonException("<reason>")` on rejection — `System.Text.Json` automatically decorates it with the JSON `Path`, giving the "located error" the AC requires. Optionally also reject non-Number tokens with a located message.
  - **Write:** `writer.WriteNumberValue(value.ToFloat())` — emits a human-readable decimal (authors edit decimals, not raw ints). `ToFloat` on the write path is **not** an in-tick call and is acceptable; the canonical hash uses `.Raw`, not this text.
  - **Home:** `godot/src/Core/Definitions/FixedJsonConverter.cs` (Godot-free, AOT-eligible, beside the model). This is the architecture's intended location.

**D2 — `Fixed.FromFloat` becomes allow-listed to the converter for the TICK path only; the broad load-time sweep is NOT this story.** AR-14 says `FromFloat` is allow-listed "only in the converter (+ the AI quantize step)." Fully honoring that everywhere means rehoming ~95 load-time `FromFloat` sites (MainScene `ApplyScenario`, `BuildingSystem.SpawnUnitFromBuilding`, `EntityWorld.Create`, `DamageMatrix`, static constants) through the model — a large, multi-story sweep tied to `ScenarioApplier` (1.8b) and `Validated<T>` (1.7). **This story removes only the THREE in-tick `FromFloat` sites** (all in `ScenarioDirector`), which is what AC2 ("the 30 Hz tick") strictly requires, and leaves the load-time sites for their consuming stories. The converter is built fully production-ready so Story 1.6 can load `damage_table.json` through it. *(This is a scope decision — see the question at the end.)*

**D3 — Convert exactly the four in-tick-read trigger fields to `Fixed`; leave the rest float.** The AC2 audit (verified) found **exactly three** in-tick `FromFloat` sites, all reading `TriggerEvent.Amount` / `TriggerCondition.Amount` / `TriggerAction.Amount`, plus the `create_timer` float math on `TriggerAction.TimerSeconds`. Converting those four fields to `Fixed` (deserialized through the converter) removes every in-tick float in the trigger path. `TriggerAction.X`/`Z` (spawn position) and `Duration` (toast) are NOT read in the tick per the audit (the spawn delegate / toast run in presentation) — leaving them `float` keeps the diff minimal and byte-identical; they migrate with the broad sweep. **Byte-identical guarantee:** the converter quantizes via the same `Fixed.FromFloat`, so a field that was `Fixed.FromFloat(def.Amount)` at the compare site and is now `Fixed` from the converter has the **same `.Raw`** — no hashed value moves.

**D4 — Trigger total order = Priority desc, then ascending declaration index (the flat-array surrogate for the future persistent node-id).** `Array.Sort(int[] order, Comparison)` is an **unstable introsort**; equal-priority triggers can reorder, and `ExecuteActions` runs in that order, so equal-priority triggers writing shared state are nondeterministic. The flat `TriggerDefinition[]` has **no id/node-id field** (verified) — the only stable identity is the **array index `idx`** (= declaration order). Fix the comparator to tiebreak on `idx` ascending. Story 7.1a/7.1b later swap this for the graph IR's persistent node-id; the declaration-index order is the correct, byte-stable behavior until then.

**D5 — `_timers` enumeration made deterministic; `_variables` untouched; neither folded into `SimChecksum` (that is 7.2).** `_timers` IS enumerated in-tick (`:150` `new List<string>(_timers.Keys)`), and same-tick expiries append `timer_expires` events in `Dictionary`-key order — nondeterministic across runtimes/insertion histories. Fix: snapshot keys in a **deterministic order** (e.g. `_timers.Keys.OrderBy(k => k, StringComparer.Ordinal)`) before the decrement/expire loop. `_variables` is **never enumerated** (only `TryGetValue` at `:305` and indexer-write at `:352`) — leave it exactly as-is. **Do NOT** add `_timers`/`_variables` to `SimChecksum`: that is Story 7.2's typed/scoped `DslVarTable` SoA work and would force an algo-version re-baseline this story must not do.

**D6 — The negative tests must drive order-nondeterminism into HASHED state (timers/variables are not hashed yet), and must not be tautological.** Because `_timers`/`_variables` are absent from `SimChecksum` until 7.2, a checksum-based test only catches order bugs that reach hashed state (entities, buildings, `ResourceStore`). Two robust patterns:
  - **Sort guard (AC3):** build ≥3 **equal-priority** triggers whose actions are individually observable in fire order (e.g. each `set_variable` then a later trigger reads it, or capture an ordered fire-log via `OnDisplayMessage` / a test delegate). Assert they fire in **ascending declaration index**. Removing the tiebreak makes the introsort produce a different order for a crafted arrangement → test fails. (Also add a focused unit test on the ordering function itself.)
  - **Timer guard (AC4):** create two named timers that expire the **same tick**, each firing a trigger whose action produces an **order-dependent hashed effect** — the cleanest is `spawn_unit` (spawn order → entity-id assignment → ascending-id `Position`/`Health` hash). Build the scenario **twice with the two `create_timer` actions in opposite declaration order** and assert the per-tick `SimChecksum` sequences are identical. With the `Dictionary.Keys` bug, the differing insertion order yields a different expiry/emission order → different spawn order → different checksum → test fails; with the sorted-keys fix, identical. ("Run twice in one process with identical insertion order" is necessary but NOT sufficient — both runs match even with the bug.)
  - **Float-round-trip guard (AC4):** the de-DE culture test from 1.3b (`ScenarioDirectorThresholdTests`) already proves the threshold path is culture-symmetric; extend/keep it as the guard that fails if `ToString("F2")`/`float.TryParse` returns to the emit/match path. (A source-scanning test over `ScenarioDirector.cs` for `ToString("F`/`.ToFloat()` in the threshold region is an optional belt-and-suspenders; the Roslyn banned-API analyzer is Story 1.10b, not here.)

**D7 — Over-range rejection in the converter resolves the 1.3b deferred finding.** 1.3b's review deferred "authored threshold `≥ 32768` overflows the `(int)(value*65536)` cast and wraps negative, inverting the comparison." The converter's `|d| >= 32768` reject closes this at the parse boundary for every `Fixed` field — note this in the converter's XML doc.

**D8 — `FogOfWarSystem` in-tick float is fenced to Story 6.5.** The audit found `FogOfWarSystem.Tick` (system #7) uses `float` + `System.MathF.Sqrt` (`FogOfWarSystem.cs:64-66,74-94`). Story 6.5 ("sim-side deterministic … vision and fog-of-war verify") owns this. Fog currently drives a presentation visibility grid and is **not** in `SimChecksum`; AC2's audit **documents** this as a known, out-of-scope in-tick float (verify no sim system branches on fog visibility before signing AC2). Do NOT fix it here. *(Flagged as a question at the end.)*

### Pre-flight facts you MUST NOT re-derive (verified against the codebase at `baf9ae5`)

- **Serialization is `System.Text.Json` exclusively.** No `JsonConverter<T>` subclass exists anywhere; `FixedJsonConverter` is **net-new**. The only converter in use is the built-in `JsonStringEnumConverter` (`ScenarioSerializer.cs:28`; `ScenarioData.cs:9` on `WinCondition`). [Source: investigation]
- **`Fixed.FromFloat` (`FixedPoint.cs:27`) = `new Fixed((int)(value * ONE))`** — unchecked; `NaN→0`, `±Inf→int.Min/Max` silently, over-range wraps. `ONE = 65536` (`:13`). `Fixed.MaxValue/MinValue` exist (`:38-39`, raw `int.Max/Min` → `ToFloat() ≈ ±32768`). `FromInt` (`:24`), `FromRaw` (`:30`), `ToFloat` (`:55`), `Raw` field (`:16`). No NaN/Inf guard exists anywhere today. [Source: FixedPoint.cs]
- **`ScenarioData.Triggers`** (`ScenarioData.cs:166`, JSON key `"triggers"`, type `TriggerDefinition[]`) is deserialized via `ScenarioSerializer.LoadFromFile` → `JsonSerializer.Deserialize<ScenarioData>(json, _options)` (`ScenarioSerializer.cs:39`). `ScenarioDirector.LoadScenario` reads `scenario.Triggers` (`:73`). **So registering the converter on `_options` makes the trigger `Fixed` fields quantize through it.** [Source: ScenarioData.cs, ScenarioSerializer.cs, ScenarioDirector.cs]
- **There are 5 distinct `JsonSerializerOptions` across 7 deserialize sites** (no choke point). The three that deserialize trigger-bearing content and therefore need the converter: `ScenarioSerializer._options` (`:23`), and the two **inline** AI options `LLMService.cs:265` (`TriggerDefinition`) and `:508` (`ScenarioData`) — both `new JsonSerializerOptions { PropertyNameCaseInsensitive = true }`. The faction/packager/settings options do NOT carry `Fixed` fields after this story and need no change. [Source: investigation]
- **The 30 Hz tick order (verified, as-built):** 1 BuildingSystem, 2 GatheringSystem, 3 MovementSystem, 4 CombatSystem, 5 ProjectileSystem, 6 SupplySystem, 7 **FogOfWarSystem (in-tick float — fenced to 6.5)**, 8 AiOpponentSystem, 9 **ScenarioDirector**. Driven by `SimulationLoop` (`StepOnce`/`Update`). The three in-tick `FromFloat` sites are all in #9. Combat/Economy/Navigation tick code is float-clean. [Source: SimulationLoop.cs, GoldenScenario.cs:104-119, investigation]
- **`ScenarioDirector` exact anchors (live file):** dict fields `:34-35` (`Dictionary<string,int> _timers/_variables`); `Tick` early-returns `:97` (`if (_triggers.Length == 0) return;`); `_timers` enumeration `:150-163`; threshold emit loop `:165-` (1.3b, raw-Fixed-int + `InvariantCulture`, `slot < 2`); `EventMatches` `resource_threshold` compare with `Fixed.FromFloat(def.Amount)` `:261`; `EvalCondition` `resource_comparison` `Fixed.FromFloat(c.Amount)` `:300`; `variable_comparison` `TryGetValue` `:305`; unstable `Array.Sort` `:196` (in `EvaluateTriggers`); `ExecuteActions` `create_timer` float math `:341-342`, `set_variable` `:352`, `add_resources` `Fixed.FromFloat(a.Amount)` `:347`. (Epics cite `:192`/`:149`; the live numbers are `:196`/`:150` after 1.3b's edits.) [Source: ScenarioDirector.cs]
- **`TriggerDefinition` has NO id/node-id field** (`TriggerDefinition.cs:15-43`: Name/Enabled/RunOnce/CooldownSeconds/Priority/Events/Conditions/Actions). The sort tiebreak must be the **array index**. The fields to convert to `Fixed`: `TriggerEvent.Amount:75`, `TriggerCondition.Amount:109`, `TriggerAction.Amount:175`, `TriggerAction.TimerSeconds:172`. [Source: TriggerDefinition.cs]
- **COMPLETE caller surface of the four fields (closed set — verified by grep, this is the whole list; the dev must not miss one):** Exactly **4 files** reference `TriggerEvent`/`TriggerCondition`/`TriggerAction` anywhere: `ScenarioDirector.cs`, `LLMService.cs`, `TriggerDefinition.cs` (the model), `ScenarioDirectorThresholdTests.cs`. The `.Amount`/`.TimerSeconds` **read sites** are exactly:
  1. `ScenarioDirector.cs:261` `Fixed.FromFloat(def.Amount)`, `:300` `Fixed.FromFloat(c.Amount)`, `:347` `Fixed.FromFloat(a.Amount)`, `:340` `a.TimerSeconds > 0`, `:342` `(int)(a.TimerSeconds * TICKS_PER_SECOND)` — **Task 3** (sim).
  2. **`LLMService.Validate` Pass 5 `:315-316`**: `if (a.Type == "create_timer" && a.TimerSeconds <= 0) return (null, $"… invalid duration {a.TimerSeconds}s.");` — **a NON-SIM caller Task 3 must ALSO fix** (the one the original draft missed). This is the AI-ingest validator, not presentation.
  3. `ScenarioDirectorThresholdTests.cs` — `Amount =` literals — **Task 7** (update to `Fixed`).
  **There is NO `MainScene`/`src/UI`/`src/CreationSuite` (trigger-editor) caller** — `ScenarioDirector.ExecuteActions` (`:320-327`) dispatches spawn/message via `OnSpawnUnit(UnitId, Faction, X, Z, Count)` / `OnDisplayMessage(Text, Duration)`, which pass **none** of the four converted fields, and no trigger-editor UI exists yet. The "presentation caller in MainScene" from earlier drafts is a phantom — do not hunt for it. [Source: grep `TriggerAction|TriggerCondition|TriggerEvent`, `\.Amount\b`, `TimerSeconds` over `**/*.cs`; ScenarioDirector.cs:314-356]
- **`Fixed` has an implicit `int→Fixed` operator (`FixedPoint.cs:96-97`) but NO implicit `float→Fixed` and NO `(int)(Fixed)` cast (`FixedPoint.cs:81-97`, full operator set).** This dictates which sites the compiler catches for you vs. which you must visit by hand:
  - **(a) `(int)(a.TimerSeconds * TICKS_PER_SECOND)` (`:342`) will NOT compile** — there is no `Fixed→int` cast → rewrite to `(a.TimerSeconds * Fixed.FromInt(SimulationLoop.TICKS_PER_SECOND)).ToInt()`. **Compiler-forced.**
  - **(b) The `> 0` / `<= 0` guards (`ScenarioDirector.cs:340`, `LLMService.cs:315`) WILL compile unchanged** — the `int` literal `0` becomes `Fixed.Zero` via the implicit op. So **`LLMService.cs:315` throws no compiler error and is silently missed unless deliberately visited.** Rewrite both to `Fixed.Zero` for clarity; note `{a.TimerSeconds}s` now renders as `30.0000` (Fixed's `F4` `ToString`) instead of `30` — harmless (error-message text only), or use `{a.TimerSeconds.ToFloat()}s` to keep the old look.
  - **(c) Setup/test literals:** `Amount = 100` compiles (→ `Fixed.FromInt(100)`, raw `6553600` — **identical `.Raw` to the old `Fixed.FromFloat(100f)`**, so byte-identical holds), but `Amount = 100.5f` does **not** compile (no implicit `float→Fixed`). Author test literals as explicit `Fixed.FromInt`/`FromRaw` (per Task 7), never relying on the implicit int op. [Source: FixedPoint.cs:81-97]
- **No shipped scenario JSON carries triggers** — `grep '"triggers"|"amount"|"timer_seconds"' **/*.json` → **0 hits**. Every existing map has empty triggers (consistent with the empty-trigger goldens), so the converter's NaN/Inf/over-range **reject has zero blast radius on existing on-disk data** — it guards only AI-generated content (`LLMService` `:265`/`:508`) and the new tests. Do not expect (and do not go looking for) a shipped map that the range check would newly reject. [Source: grep `**/*.json`]
- **`ResourceStore.AddOre(Faction, Fixed)`** (`ResourceStore.cs:46-47`) takes a `Fixed`; `Ore` is `Fixed[]` and IS hashed in `SimChecksum` (`:80`). So after converting `TriggerAction.Amount` to `Fixed`, `add_resources` is `_resources.AddOre(faction, a.Amount)` — no conversion. [Source: ResourceStore.cs, SimChecksum.cs]
- **`SimChecksum`** (`SimChecksum.cs`, `AlgoVersion = 2`) hashes EntityWorld (Position.X/Y/Z + Health per alive entity, ascending id), BuildingStore (Alive/Health/ConstructionTimer), ResourceStore (Ore/Crystal/SupplyUsed/SupplyCap/FactionBase per active faction). **It does NOT reference `_timers`/`_variables`** — do not change this. [Source: SimChecksum.cs]
- **Both goldens load an EMPTY scenario** (`director.LoadScenario(new ScenarioData())` — `GoldenScenario.cs:127`, `MultiFactionScenario.cs`), so `_triggers.Length == 0` → `ScenarioDirector.Tick` early-returns (`:97`). **The sort, the timer loop, and the compare sites never run in the goldens** → both goldens are byte-identical by construction, and they CANNOT prove any of this story's behavior. The negative tests are the only proof. [Source: GoldenScenario.cs, ScenarioDirector.cs:97]

### Scope fence — do NOT, in this story

- **Do NOT** start the broad model-wide `float → Fixed` sweep — converting `UnitDefinition.Hp/Speed/AttackDamage/...`, `ScenarioData.StartOre/BaseX/X/Z/...`, or rehoming MainScene/`BuildingSystem`/`EntityWorld` load-time `FromFloat` sites. Only the **four trigger fields** convert here. The rest rehome via `ScenarioApplier` (1.8b) / `Validated<T>` (1.7) / their consuming stories. [Source: epics.md#Story-1.7, #Story-1.8b]
- **Do NOT** build `ContentLoader`, `ChimeraJsonContext`/source-gen context, `NodeBaseJsonConverter`, the canonical-model FNV-64 hash, or unify the 5 `JsonSerializerOptions` into one — that is the rest of D3 (1.7 / later). [Source: game-architecture.md D3.1–D3.9]
- **Do NOT** build `Validated<T>` / `ScenarioValidator` (Story 1.7) or `ScenarioApplier` (Story 1.8b). The converter is wired into the **current** `ScenarioSerializer`/AI paths, not a new gate. [Source: epics.md#Story-1.7, #Story-1.8b]
- **Do NOT** fold `_timers`/`_variables` into `SimChecksum`, build the dense-index SoA `DslVarTable`, add typed/scoped variables, or build the graph IR / persistent node-ids — **all Epic 7 (7.1a/7.1b/7.2).** This story does the minimal flat-array fix only. [Source: epics.md#Story-7.1a, #Story-7.2]
- **Do NOT** fix `FogOfWarSystem`'s in-tick float — **Story 6.5.** Document it in the AC2 audit. [Source: epics.md#Story-6.5]
- **Do NOT** build the Roslyn banned-API / AOT analyzer — **Story 1.10b.** AC2's "audit" here is the documented audit + the negative tests, not a compiler analyzer. [Source: epics.md#Story-1.10b]
- **Do NOT** convert `TriggerAction.X`/`Z`/`Duration` to `Fixed`, widen `slot < 2`, touch the `Faction` enum, or change `ResourceStore` sizes (9.2 / broad sweep). [Source: epics.md#Story-9.2]
- **Do NOT** hand-edit a golden file. If (unexpectedly) a golden moves, STOP — the empty-trigger goldens must stay byte-identical; a move means an unintended hashed-state change.

---

## Tasks / Subtasks

- [x] **Task 1 — Net-new `FixedJsonConverter` (AC: 1)**
  - [x] Create `godot/src/Core/Definitions/FixedJsonConverter.cs`: `public sealed class FixedJsonConverter : JsonConverter<Fixed>` (`#nullable enable`, namespace `ProjectChimera.Core.Definitions`, Godot-free).
  - [x] `Read`: if `reader.TokenType != JsonTokenType.Number` throw a located `JsonException`; `double d = reader.GetDouble();` reject `double.IsNaN(d) || double.IsInfinity(d)`; reject `Math.Abs(d) >= 32768d` (16.16 over-range — would overflow `Fixed.FromFloat`); `return Fixed.FromFloat((float)d);` (**the sole allow-listed `FromFloat` call**). Throw `JsonException` with a clear reason on each rejection (System.Text.Json appends the `Path`).
  - [x] `Write`: `writer.WriteNumberValue(value.ToFloat());`.
  - [x] XML-doc the class: single quantization boundary (AR-14); rejects NaN/Inf/over-range (resolves the 1.3b ≥32768 deferral, D7); the only place `Fixed.FromFloat` may be called on external data.
  - [x] `dotnet build godot/godot.csproj` → green.

- [x] **Task 2 — Register the converter + convert the four trigger fields to `Fixed` (AC: 1, 2)**
  - [x] Register `new FixedJsonConverter()` in `ScenarioSerializer._options.Converters` (`ScenarioSerializer.cs:28`), and in the two inline AI options (`LLMService.cs:265`, `:508`). (AI content is the highest NaN/Inf risk — the converter must guard it.)
  - [x] `TriggerDefinition.cs`: change `TriggerEvent.Amount` (`:75`), `TriggerCondition.Amount` (`:109`), `TriggerAction.Amount` (`:175`), `TriggerAction.TimerSeconds` (`:172`) from `float` → `Fixed`. Keep the `[JsonPropertyName]` keys. Pick sensible `Fixed` defaults (`Amount = Fixed.Zero`; `TimerSeconds = Fixed.FromInt(30)`).
  - [x] Confirm `using ProjectChimera.Core;` is present in `TriggerDefinition.cs` for the `Fixed` type.
  - [x] `dotnet build` → green (the field-type change will surface every caller — fix in Task 3 + the presentation caller).

- [x] **Task 3 — De-float the three in-tick `ScenarioDirector` sites (AC: 2)**
  - [x] `EventMatches` `resource_threshold` (`:261`): `Compare(Fixed.FromRaw(oreRaw), def.Amount, def.Operator)` (drop `Fixed.FromFloat` — `def.Amount` is now `Fixed`).
  - [x] `EvalCondition` `resource_comparison` (`:300`): `Compare(_resources.Ore[(int)faction], c.Amount, c.Operator)` (drop `Fixed.FromFloat`).
  - [x] `ExecuteActions` `add_resources` (`:347`): `_resources.AddOre(faction, a.Amount);` (drop `Fixed.FromFloat`).
  - [x] `ExecuteActions` `create_timer` (`:341-342`): compute ticks with `Fixed` math, no float — e.g. `_timers[a.TimerName] = (a.TimerSeconds * Fixed.FromInt(SimulationLoop.TICKS_PER_SECOND)).ToInt();` and keep the `a.TimerSeconds > Fixed.Zero` guard.
  - [x] **Fix the AI-validation caller `LLMService.Validate` Pass 5 (`:315-316`)** — the **only** non-sim read of a converted field, and one the compiler will **not** flag (it compiles unchanged via implicit `int→Fixed`). Change `a.TimerSeconds <= 0` → `a.TimerSeconds <= Fixed.Zero` (explicit), and keep `{a.TimerSeconds}s` (now prints `30.0000s`) or switch to `{a.TimerSeconds.ToFloat()}s` for the old `30s` look — error-message text only, zero determinism impact. **Note:** the converter (registered on the `:265` options in Task 2) already rejects NaN/Inf/over-range at deserialize — caught by the `:268` try/catch as `"Invalid JSON: …"` — so this `<= 0` check still earns its keep for in-range non-positive durations (`0` / negative).
  - [x] **Do NOT hunt for a MainScene/UI/CreationSuite caller — there is none (verified).** `ExecuteActions` dispatches spawn/message via `OnSpawnUnit(UnitId, Faction, X, Z, Count)` / `OnDisplayMessage(Text, Duration)` (`:320-327`); neither passes `Amount`/`TimerSeconds`, and no trigger-editor UI exists yet. (If a grep for `.Amount`/`.TimerSeconds` on a trigger type ever returns a hit outside `ScenarioDirector.cs` / `LLMService.cs` / the tests, STOP — the closed set was wrong.)
  - [x] `dotnet build godot/godot.csproj` → green. Confirm no `Fixed.FromFloat` remains in `ScenarioDirector.cs` (grep the file), and that `LLMService.Validate` was actually visited (`a.TimerSeconds <= Fixed.Zero`) — the build will **not** remind you, since that line compiles either way.

- [x] **Task 4 — Stable trigger total order (AC: 3)**
  - [x] `EvaluateTriggers` (`:196`): replace the priority-only comparator with **Priority desc, then ascending declaration index**:
    ```csharp
    Array.Sort(order, (a, b) =>
    {
        int byPriority = _triggers[b].Priority - _triggers[a].Priority; // desc
        return byPriority != 0 ? byPriority : a - b;                    // tiebreak: ascending declaration index
    });
    ```
    (Comment that `a - b` is the flat-array surrogate for the future persistent node-id total order; 7.1b supersedes.)
  - [x] `dotnet build` → green.

- [x] **Task 5 — Deterministic `_timers` enumeration (AC: 4)**
  - [x] `CollectEvents` timer loop (`:150`): replace `var keys = new List<string>(_timers.Keys);` with a deterministically-ordered snapshot, e.g. `var keys = _timers.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();` (add `using System.Linq;`). Keep the decrement/expire/remove body unchanged. Comment WHY (same-tick expiry order must be insertion-order-independent; AR-16; 7.2 replaces with the dense SoA store).
  - [x] Leave `_variables` untouched (key-access only — no enumeration).
  - [x] `dotnet build` → green.

- [x] **Task 6 — `FixedJsonConverter` tests (AC: 1)**
  - [x] New `godot/ProjectChimera.Sim.Tests/Determinism/FixedJsonConverterTests.cs`:
    - Round-trip: deserialize `{"amount": 12.5}` into a tiny test record (or a `TriggerAction`) → assert `.Amount.Raw == Fixed.FromFloat(12.5f).Raw` (quantized at the boundary, identical raw).
    - Integer JSON number (`100`) deserializes correctly.
    - **NaN/Inf rejected:** deserializing `NaN`/`Infinity` (as raw JSON tokens, or a string the converter rejects) throws `JsonException`; assert the message/path locates the field.
    - **Over-range rejected:** `32768` and `1e9` throw `JsonException`; `32767.5` succeeds. (Pins D7 — the 1.3b ≥32768 deferral.)
  - [x] `dotnet test --filter FullyQualifiedName~FixedJsonConverter` → green.

- [x] **Task 7 — AR-16 negative tests (AC: 3, 4)**
  - [x] New `godot/ProjectChimera.Sim.Tests/Golden/TriggerOrderingTests.cs` (sort guard, AC3): build a `ScenarioData` with ≥3 **equal-priority** triggers whose fire order is observable (ordered fire-log via `OnDisplayMessage` or a test delegate; or `set_variable`→read). Drive a `ScenarioDirector` one tick; assert they fired in **ascending declaration index**. Add a negative-control note: removing the `a - b` tiebreak makes a crafted arrangement fail. Add a focused unit assertion on the ordering for equal priorities.
  - [x] New `godot/ProjectChimera.Sim.Tests/Golden/TimerDeterminismTests.cs` (timer guard, AC4): a helper builds a scenario with two `create_timer` actions expiring the same tick, each `timer_expires` firing a `spawn_unit` (order → entity-id → hashed Position/Health). Build it **twice with the two `create_timer` actions in opposite order**; run the harness (reuse `GoldenChecksumReplay.RunAndRecord` with a custom `build:`); assert the two `SimChecksum` sequences are **byte-identical** (`SequenceEqual`). Document that this fails if `_timers` reverts to `Dictionary.Keys` order. (If `spawn_unit` needs a faction-unit definition to resolve, wire a minimal `FactionDefinition` like the existing scenarios; if that is heavy, use a variable-gated `add_resources` whose ordering is made non-commutative via a `set_variable`+`variable_comparison` chain — pick whichever reaches hashed state cleanly and note the choice.)
  - [x] Float-round-trip guard (AC4): confirm/extend the de-DE culture test in `ScenarioDirectorThresholdTests.cs` still passes against the now-`Fixed` `Amount` fields (the threshold test builds `TriggerEvent { Amount = ... }` — update those literals from `float` to `Fixed`). It remains the guard that the emit/match path stays culture-symmetric / float-free.
  - [x] `dotnet test --filter "FullyQualifiedName~TriggerOrdering|FullyQualifiedName~TimerDeterminism|FullyQualifiedName~ScenarioDirectorThreshold"` → green.

- [x] **Task 8 — Verify end-to-end + negative controls + golden no-regression (AC: 1, 2, 3, 4)**
  - [x] **Golden no-regression:** run the full Golden suite — both `golden-scenario.golden.txt` and `golden-multifaction.golden.txt` must be byte-identical to their committed values (`git status` clean for both). They are unaffected (empty triggers → `Tick` early-returns); this proves the trigger-path edits changed no hashed state. **If a golden moves, STOP and find the unintended hashed-state change** (likely a non-byte-identical `Amount` quantization).
  - [x] **AC3 negative control:** remove the `a - b` tiebreak (revert to priority-only) → `TriggerOrderingTests` goes red; restore → green.
  - [x] **AC4 negative control:** revert the timer loop to `new List<string>(_timers.Keys)` → `TimerDeterminismTests` goes red (sequences diverge); restore → green.
  - [x] **AC1 negative control:** feed the converter `NaN`/`32768` in a test → `JsonException` with located path; valid values pass.
  - [x] `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` → ALL green (the existing 41 tests + the new ones), headless, in seconds. `dotnet build godot/godot.csproj` → green (only the pre-existing CS8632 warnings, untouched). Grep the sim tick path: zero `Fixed.FromFloat`/`ToFloat`/`float ` in `ScenarioDirector.cs` (FogOfWar documented as the lone fenced exception).

---

## Dev Notes

### Reference snippets (copy/adapt — verified against current code at `baf9ae5`)

**`FixedJsonConverter` (Task 1):**
```csharp
#nullable enable
using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectChimera.Core;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The single quantization boundary (AR-14). Converts a JSON number to the 16.16
    /// <see cref="Fixed"/> type at deserialize, fronting the otherwise-unguarded
    /// <see cref="Fixed.FromFloat"/> and rejecting NaN / ±Infinity / over-range
    /// (|value| >= 32768, which would overflow Fixed.FromFloat's (int)(value*65536) cast)
    /// with a located JsonException. This is the ONLY place Fixed.FromFloat may run on
    /// external data; the 30 Hz tick does Fixed-only math.
    /// </summary>
    public sealed class FixedJsonConverter : JsonConverter<Fixed>
    {
        public override Fixed Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.Number)
                throw new JsonException($"Expected a number for Fixed, got {reader.TokenType}.");

            double d = reader.GetDouble();
            if (double.IsNaN(d) || double.IsInfinity(d))
                throw new JsonException($"Fixed value must be finite; got {d}.");
            if (Math.Abs(d) >= 32768d) // 16.16 range is roughly [-32768, 32767.99998]
                throw new JsonException($"Fixed value {d} is out of 16.16 range (|value| must be < 32768).");

            return Fixed.FromFloat((float)d); // the sole allow-listed FromFloat on external data
        }

        public override void Write(Utf8JsonWriter writer, Fixed value, JsonSerializerOptions options)
            => writer.WriteNumberValue(value.ToFloat());
    }
}
```
*Note: STJ surfaces NaN/Infinity tokens depending on `JsonNumberHandling`; the codebase does not set `AllowNamedFloatingPointLiterals`, so literal `NaN` in JSON is itself a parse error before the converter — keep the explicit `IsNaN/IsInfinity` guard anyway (covers numbers that round to non-finite and documents intent), and write the NaN/Inf test to assert rejection regardless of which layer throws (`JsonException`).*

**Register the converter (Task 2):**
```csharp
// ScenarioSerializer.cs:23-29
private static readonly JsonSerializerOptions _options = new()
{
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented       = true,
    Converters          = { new JsonStringEnumConverter(), new FixedJsonConverter() }, // ← add
};
// LLMService.cs:265 / :508 — add `, Converters = { new FixedJsonConverter() }` to each inline options.
```

**Trigger fields → `Fixed` (Task 2):**
```csharp
// TriggerEvent.Amount :75  /  TriggerCondition.Amount :109  /  TriggerAction.Amount :175
[JsonPropertyName("amount")] public Fixed Amount { get; set; } = Fixed.Zero;
// TriggerAction.TimerSeconds :172
[JsonPropertyName("timer_seconds")] public Fixed TimerSeconds { get; set; } = Fixed.FromInt(30);
```

**Timer ordering snapshot (Task 5):**
```csharp
// :150 — deterministic, insertion-order-independent (AR-16). 7.2 replaces with the dense SoA store.
var keys = _timers.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
foreach (var name in keys) { /* …decrement / expire / remove body unchanged… */ }
```

**Harness reuse for the AC4 twice-build test (Task 7):** `GoldenChecksumReplay.RunAndRecord(ticks, perturb: null, build: () => BuildTwoTimerScenario(order))` returns the per-tick `Sample` list; compare `seqA.Select(s => s.Hash).SequenceEqual(seqB.Select(s => s.Hash))`. Build each harness exactly like `GoldenScenario.Build()` (fresh stores, 9-system loop, `EnableChecksums(buildings, resources, new FactionRegistry(2))`, `ChecksumInterval = 1`) but `LoadScenario(scenarioWithTwoTimers)` instead of the empty one.

### Constraints & gotchas

- **`dotnet build`/`dotnet test` are authoritative** for C# correctness — the Godot MCP `run` does not rebuild the test assembly. Build/test before declaring done. [Source: LEARNINGS.md:122; 1.1/1.2/1.3a/1.3b Dev Notes]
- **The goldens do NOT validate any behavior in this story** — empty triggers → `Tick` early-returns (`:97`). They only prove "no regression." The negative tests are the only proof. Do not conflate them. [Source: ScenarioDirector.cs:97; GoldenScenario.cs:127]
- **"Run twice in one process" does NOT catch these bugs** — `Array.Sort` introsort and `Dictionary` enumeration are deterministic *within a single runtime + identical insertion order*. The bugs manifest across **insertion order / runtime version**. The AC3 guard asserts an **exact ascending order**; the AC4 guard varies **declaration order** across two builds. Design the tests so removing the fix actually turns them red (verify the negative controls in Task 8). [Source: epics.md#Story-7.1a "byte-identical … across runtime versions"]
- **`_variables` is key-access only — do NOT "also fix" it** with sorting/enumeration; there is nothing to fix, and adding enumeration would be churn. [Source: ScenarioDirector.cs:305,352]
- **Byte-identical or bust:** because the converter quantizes via the same `Fixed.FromFloat`, every converted field keeps its exact `.Raw`. If a golden moves, an `Amount`/`TimerSeconds` is quantizing differently (or a non-trigger field was touched) — fix the cause; never re-baseline in this story. [Source: D3; epics.md#Story-1.4 note]
- **Determinism binds the new tests too:** author worlds with `Fixed.FromInt`/`FromRaw` (never `FromFloat` in scenario setup), no `System.Random`/`DateTime`, no `Dictionary` enumeration in test logic. The `TriggerOrdering`/`TimerDeterminism` scenarios set `Fixed` `Amount`/`TimerSeconds` via `Fixed.FromInt`. [Source: GoldenScenario.cs:100; FixedBoundaryTests.cs]
- **Sim/Presentation boundary:** `FixedJsonConverter`, `TriggerDefinition`, `ScenarioDirector` are `src/Core` (sim) and Godot-free; `GodotFreeBoundaryTest` fails the build if a `using Godot` sneaks in. The converter writing `ToFloat()` is fine (not in-tick). [Source: project-context.md; GodotFreeBoundaryTest.cs]
- **No new dependencies.** `JsonConverter<T>` is in `System.Text.Json.Serialization` (already used). Reuse the 1.1 xUnit stack. Nothing added to `godot.sln`/`.csproj` except the auto-globbed new `.cs` files (sim source is glob-included into the test project via `../src/**`, so no manual `<Compile>` entry). [Source: ProjectChimera.Sim.Tests.csproj:13-28]
- **Pre-existing CS8632** nullable warnings in `SimulationLoop.cs`/`GatheringSystem.cs`/`FlowFieldSystem.cs` are not this story's bug; don't "fix" them. [Source: 1.3a/1.3b Dev Notes]

### Project Structure Notes

- Production edits (sim layer, auto-globbed into both `godot.csproj` and the Tier-1 test project via `../src/Core/**`): **NEW** `godot/src/Core/Definitions/FixedJsonConverter.cs`; **edit** `godot/src/Core/Definitions/TriggerDefinition.cs` (4 field types), `godot/src/Core/Definitions/ScenarioSerializer.cs` (register converter), `godot/src/Core/ScenarioDirector.cs` (de-float 3 sites + sort tiebreak + timer ordering), `godot/src/AI/LLMService.cs` (register converter ×2 at `:265`/`:508` **+ fix the `Validate` Pass-5 `TimerSeconds` guard/error-string at `:315-316`**). **No presentation edit** — the verified caller surface has no `MainScene`/`src/UI`/`src/CreationSuite` reader of the four fields (see the caller-surface pre-flight fact).
- New test files in the existing folders: `Determinism/FixedJsonConverterTests.cs`, `Golden/TriggerOrderingTests.cs`, `Golden/TimerDeterminismTests.cs`; edit `Golden/ScenarioDirectorThresholdTests.cs` (Fixed `Amount` literals). Do NOT create `Validation/`, `Checksum/`, `Bootstrap/`, `Dsl/`, `Effects/` folders (later stories). The two `.cs.uid` files in `git status` (`SimChecksumCoverageGuardTest.cs.uid`, `ScenarioDirectorThresholdTests.cs.uid`) are Godot artifacts from 1.3b's test files — not your concern.
- No `.csproj` change needed: sim source is glob-included; both goldens stay embedded and unchanged.

### Project Context Rules

_Extracted from `_bmad-output/project-context.md` + `game-architecture.md` — these govern every edit here:_

- **`Fixed` end-to-end, convert at parse; `Fixed.FromFloat` allow-listed ONLY in the converter (+ AI quantize step).** This story makes the converter that single boundary for the trigger fields, and removes the three in-tick `FromFloat` sites. The ~95 load-time `FromFloat` sites are fenced to their consuming stories (D2). [Source: project-context.md "Fixed end-to-end"; game-architecture.md:1837,1884]
- **Zero float in the 30 Hz tick.** After this story the trigger path (system #9) is float-free; `FogOfWarSystem` (#7) is the documented exception, owned by 6.5 (D8). [Source: game-architecture.md:1884; epics.md#Story-6.5]
- **Process per-faction / per-entity state in ascending order; iteration order is part of the deterministic contract.** The trigger total order (declaration index) and the sorted timer-key snapshot honor this. [Source: project-context.md "Determinism"]
- **Reuse existing systems; SoA; composition.** No new stores or subsystems — the timer/variable SoA store is explicitly 7.2, not here. [Source: project-context.md "Data layout"; epics.md#Story-7.2]
- **Everything data-driven via `System.Text.Json` from `resources/data/`.** The converter integrates into the existing `ScenarioSerializer`/AI deserialize paths; no bespoke parser. [Source: project-context.md "Everything is data-driven"]
- **Engine/runtime:** Godot 4.6.3 target, .NET 8 (`net8.0`); assembly/namespace `ProjectChimera.*`; project files `godot.csproj`/`godot.sln`; Tier-1 test project `ProjectChimera.Sim.Tests` (xUnit, Godot-free). [Source: project-context.md "Technology Stack"; ProjectChimera.Sim.Tests.csproj]

### References

- [Source: epics.md#Story-1.4 (lines 562-580)] — story statement; the four ACs (FixedJsonConverter quantize + NaN/Inf reject + located error; banned-API/float audit over the tick; unstable `Array.Sort` negative test with ascending-id tiebreak; `Dictionary` timers/variables + float-round-trip negative test, byte-identical, fails-if-reintroduced); the "byte-identical golden (or re-baseline only if a sort tiebreak changes output)" note.
- [Source: epics.md:195 (AR-14)] — "FixedJsonConverter quantizes + rejects NaN/Inf/over-range at deserialize (the single quantization boundary); validator checks Fixed.Raw ranges; hash/tick use .Raw with no second conversion. Fixed.FromFloat allow-listed only in the converter (+ AI quantize step). Zero float in the 30 Hz tick."
- [Source: epics.md:197 (AR-16)] — the three nondeterminisms; #1 (ScenarioDirector round-trip) done in 1.3b; #2 unstable `Array.Sort` → total order (Priority desc, ascending node-id tiebreak); #3 Dictionary `_timers`/`_variables` → dense-index SoA folded into SimChecksum (the full SoA-fold is 7.2; this story does the minimal fix).
- [Source: epics.md#Story-7.1a (1770-1784), #Story-7.1b (1786-1812), #Story-7.2 (1814-1830)] — the Epic-7 overlap: 7.1a names the same `:192/:149` lines + same total order + Covers AR-16; 7.1b builds persistent node-ids; **7.2 owns the typed/scoped `DslVarTable` SoA store folded into SimChecksum** (the deferral that keeps this story byte-identical / no algo bump).
- [Source: epics.md#Story-6.5] — "sim-side deterministic … vision and fog-of-war verify" owns the `FogOfWarSystem` in-tick float (D8).
- [Source: epics.md#Story-1.7, #Story-1.8b, #Story-1.10b] — `Validated<T>`/`ScenarioValidator` (1.7), `ScenarioApplier` (1.8b), the Roslyn banned-API analyzer (1.10b) — all fenced out of this story.
- [Source: game-architecture.md:788-935 (D3 decision + migration sequence D3.0/D3.4) and :2251] — `ContentLoader` + one canonical options + `FixedConverter` (NaN/Inf reject) = D3.0; the sort/timer/A17 work = D3.4 (with the var/timer SoA fold tied to the DSL variable schema); the `FixedJsonConverter : JsonConverter<Fixed>` sketch.
- [Source: godot/src/Core/FixedPoint.cs:13,16,24,27,30,38-39,55] — `ONE=65536`, `Raw`, `FromInt`, `FromFloat` (unguarded `(int)(value*ONE)`), `FromRaw`, `MaxValue/MinValue`, `ToFloat` — the converter's building blocks + the over-range boundary.
- [Source: godot/src/Core/Definitions/ScenarioSerializer.cs:23-29,35-40] — `_options` (where to register the converter) + `LoadFromFile` → `Deserialize<ScenarioData>`.
- [Source: godot/src/Core/Definitions/ScenarioData.cs:165-166] — `Triggers` (`TriggerDefinition[]`, JSON `"triggers"`) is part of `ScenarioData` → loaded via `_options`.
- [Source: godot/src/Core/Definitions/TriggerDefinition.cs:75,109,172,175] — the four fields to convert to `Fixed`; `:33` `Priority`; no id field (sort tiebreak = array index).
- [Source: godot/src/Core/ScenarioDirector.cs:34-35,97,150-163,196,261,300,341-342,347,352] — dict fields; `Tick` early-return; timer enumeration; unstable sort; the three in-tick `FromFloat` sites; `set_variable` (key-access).
- [Source: godot/src/Core/ResourceStore.cs:46-47,12] — `AddOre(Faction, Fixed)`; `Ore` is hashed `Fixed[]`.
- [Source: godot/src/Core/SimChecksum.cs] — `AlgoVersion = 2`; hashes entity/building/resource state; **no timers/variables** (do not add — 7.2).
- [Source: godot/src/AI/LLMService.cs:264-265,315-316,507-508] — the two inline AI-ingest options to register the converter on (`:265`/`:508`), **and the `Validate` Pass-5 `TimerSeconds` guard/error-string (`:315-316`) — the only non-sim read of a converted field, which compiles silently after the type change (implicit `int→Fixed`).**
- [Source: godot/ProjectChimera.Sim.Tests/Golden/GoldenChecksumReplay.cs:43-66,102-149,169-205] — `RunAndRecord`/`LoadGolden`/`ParseGolden`/`FormatGolden`/`MaybeRecord` for the AC4 twice-build comparison.
- [Source: godot/ProjectChimera.Sim.Tests/Golden/GoldenScenario.cs:84-130 + ScenarioDirectorThresholdTests.cs:36-103,146-151 + SimChecksumCoverageGuardTest.cs + FactionRegistryTests.cs:93-115 + GodotFreeBoundaryTest.cs:13-22] — scenario-build template; the `OnDisplayMessage`/de-DE/`RunOneTick` observability patterns; the differential-mutation + anti-tautology + structural negative-test idioms.
- [Source: godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj:13-28,44-48] — sim source glob-include (new `.cs` auto-picked up); xUnit 2.9.2 / Test.Sdk 17.11.1 / runner 2.8.2.

### Latest tech information

- **No new dependencies.** `System.Text.Json` `JsonConverter<Fixed>` is the .NET-idiomatic mechanism; `reader.GetDouble()` reads both integer and fractional JSON number tokens. Throwing `JsonException` from `Read` yields a path-located error automatically (the AC's "located error"). [Source: System.Text.Json docs; investigation]
- **`float.IsNaN(x)` / `float.IsInfinity(x)`** are the correct C# guards (`float.IsInf` does not exist) — the converter reads `double` and uses `double.IsNaN`/`double.IsInfinity`. [Source: LEARNINGS.md:25]
- **Golden-file portability (1.10c):** unchanged here — no golden moves, and the converter affects values, not the embedded-resource transport. [Source: 1.3b Dev Notes]
- **AI-ingest path:** the architecture wants a separate lenient case-insensitive options for the LLM path (it already is — `PropertyNameCaseInsensitive = true`), but it must **still** carry the `FixedJsonConverter` so AI-generated NaN/Inf/over-range is rejected (the highest-risk source). [Source: game-architecture.md:797-798]

### Previous Story Intelligence

From **Story 1.3b** (done, code-review ACCEPTED 2026-06-23):

- 1.3b explicitly fenced FOUR things to this story, with exact pointers: the residual `Fixed.FromFloat(def.Amount/c.Amount)` at the two compare sites; the `add_resources` `Fixed.FromFloat`; the unstable `Array.Sort`; the `Dictionary` timers/variables. It also left the in-code comments at `ScenarioDirector.cs:256-259,299` saying "Story 1.4 removes it when FixedJsonConverter makes Amount a Fixed." This story is that step — those comments come out as the code is de-floated.
- 1.3b's **deferred review finding** is resolved here: "authored threshold `≥ 32768` overflows the `(int)(value*65536)` cast and wraps negative" → the converter's over-range reject (D7) closes it at the parse boundary.
- 1.3b established the Fixed-vs-Fixed + `InvariantCulture` threshold path and the `Compare(Fixed,Fixed,string)` overload with the `Fixed.FromRaw(655)` epsilon. **Reuse that `Compare` overload** — after this story `def.Amount`/`c.Amount` are already `Fixed`, so the `Fixed.FromFloat` wrappers at the call sites simply drop away; the epsilon/operators are unchanged.
- 1.3b's review insisted on **no tautological asserts** and **observable outcomes** (a captured delegate / mutated `ResourceStore`, a real boundary, under invariant + de-DE cultures). Apply the same rigor to AC3/AC4: assert an exact fire order and a real cross-insertion-order byte-identity, with verified negative controls — not "the same value twice."
- The `ScenarioDirectorThresholdTests` from 1.3b build `TriggerEvent { Amount = <float> }` — those literals must change to `Fixed` (e.g. `Amount = Fixed.FromInt(100)`) when the field type changes; keep the de-DE case as the float-round-trip guard.
- Git history is `[AutoSave]`-only (hourly autocommit to `master`); your edits sit alongside. `.cs.uid` sidecars may auto-appear for touched scripts (Godot artifact) — not your concern. `baseline_commit: baf9ae5` is recorded for the code-review diff range.

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (Claude Opus 4.8)

### Debug Log References

- `dotnet build godot/godot.csproj` → **Build succeeded, 0 errors.** 14 `CS8632` nullable warnings, ALL pre-existing (`SimulationLoop.cs`, `GatheringSystem.cs`, `FlowFieldSystem.cs`) — none introduced by this story.
- `dotnet test ProjectChimera.Sim.Tests` → **Passed! 56/56** (41 pre-existing + 15 new), 0 failed, ~120 ms, headless/Godot-free.
- **AC3 negative control** (reverted sort tiebreak → priority-only): `TriggerOrderingTests.EqualPriorityTriggers_FireInAscendingDeclarationIndex` went **RED** — the introsort shuffled the 24 equal-priority triggers to `[0,21,20,19,…]` instead of ascending (confirms the 24-count crosses .NET's 16-element insertion-sort threshold so the instability actually manifests). Restored → green.
- **AC4 negative control** (reverted `_timers` snapshot → `new List<string>(_timers.Keys)`): `TimerDeterminismTests.SameTickExpiries_EmitInOrdinalSortedOrder_IndependentOfInsertionOrder` went **RED** — reverse-insertion emitted `["gamma","beta","alpha"]` (Dictionary insertion order) ≠ sorted. Restored → green.
- **AC1 negative control**: covered by passing `FixedJsonConverterTests` — NaN/±Infinity and `≥32768`/`1e9` throw `JsonException` (over-range carries a located `Path` naming `amount`); `32767.5` and integers pass.
- **Golden no-regression**: `git status --porcelain` empty for BOTH `golden-scenario.golden.txt` and `golden-multifaction.golden.txt` (empty triggers → `Tick` early-returns; no hashed state moved). `AlgoVersion` unchanged at 2 — no re-baseline.

### Completion Notes List

**Summary.** Built the net-new `FixedJsonConverter` (AR-14 single quantization boundary), routed the in-tick trigger fields through it, removed all in-tick float from the trigger path (AR-14), and killed AR-16's two remaining nondeterminisms (unstable sort, `Dictionary` timer enumeration) with negative-controlled tests. Goldens stay byte-identical.

**AC1 — FixedJsonConverter (single quantization boundary).** `godot/src/Core/Definitions/FixedJsonConverter.cs` (`JsonConverter<Fixed>`): reads a JSON number, rejects NaN/±Infinity and `|value| ≥ 32768` (the 16.16 overflow boundary) with a located `JsonException`, else quantizes via the **sole allow-listed** `Fixed.FromFloat`. Registered on `ScenarioSerializer._options` and BOTH inline AI-ingest options in `LLMService` (`:265` TriggerDefinition, `:508` ScenarioData). The over-range reject resolves Story 1.3b's deferred `≥32768` overflow finding (D7).

**AC2 — zero in-tick float in the trigger path.** Converted the four story-named fields to `Fixed` (`TriggerEvent.Amount`, `TriggerCondition.Amount`, `TriggerAction.Amount`, `TriggerAction.TimerSeconds`), removed the three in-tick `Fixed.FromFloat` sites (resource_threshold, resource_comparison, add_resources → now Fixed-vs-Fixed / Fixed add), and rewrote `create_timer` to Fixed math + `.ToInt()` (no `Fixed→int` cast exists). Fixed the **silent non-sim caller** `LLMService.Validate` Pass 5 (`a.TimerSeconds <= 0` → `<= Fixed.Zero`; error string via `.ToFloat()`) — it compiles unchanged via the implicit `int→Fixed` op, so it would have been missed by the build.
- **⚠ DEVIATION — a FIFTH field (`CooldownSeconds`) was converted.** The story's four-field audit **missed an in-tick float**: `EvaluateTriggers` computed `(int)(t.CooldownSeconds * SimulationLoop.TICKS_PER_SECOND)` — float gameplay math in the trigger evaluation path. AC2 demands *"no float gameplay math in the trigger evaluation/condition/action path"* and asserts FogOfWar is *"the one remaining in-tick float in the sim,"* so leaving cooldown float would make AC2 literally false. `CooldownSeconds` has exactly two sites (the field + this one computation — no LLMService/UI caller, grep-verified), so de-floating it is minimal and uses the identical seconds→ticks Fixed pattern as `TimerSeconds`. This **completes AC2**, not scope creep; `X`/`Z`/`Duration` correctly stay `float` (presentation-only, not in-tick).
- **AC2 audit sign-off:** zero `Fixed.FromFloat`/`.ToFloat()` *calls* remain in `ScenarioDirector` (only a comment + the presentation-boundary delegate signatures `Action<…float…>` for spawn X/Z and message duration). `FogOfWarSystem` (#7) is the documented lone remaining in-tick float — fenced to Story 6.5; verified no sim system branches on fog visibility (consumed only by `UI/MinimapBridge` + `UI/FogOfWarBridge`; `Navigation/FlowField` mirrors only grid *dimensions*), so it cannot leak into `SimChecksum`.

**AC3 — stable total-order sort.** Comparator is now `(Priority desc, then ascending declaration index)`; the index tiebreak is the flat-array surrogate for the future persistent node-id (7.1b supersedes). Behavioral test asserts the exact ascending fire order for **24** equal-priority triggers (above the introsort threshold, so reverting genuinely shuffles — verified RED) plus a distinct-priority test pinning priority-desc as the primary key.

**AC4 — deterministic timer enumeration + float-round-trip guard.** `_timers` now iterates `Keys.OrderBy(k => k, StringComparer.Ordinal)`. `_variables` left untouched (key-access only). Neither folded into `SimChecksum` (deferred to Story 7.2).
- **⚠ DEVIATION — AC4's checksum-sequence sketch was replaced with a direct emission-order assertion (to AVOID a tautology).** In the current `ScenarioDirector` the timer enumeration order's only effect is the order of `timer_expires` events, which is consumed solely by the **boolean** `AnyEventMatches`; triggers then fire in the independent SORT order, so timer order **never reaches `SimChecksum`**. A "build twice, compare checksums" test (the story's spawn-order sketch) would therefore pass *even with the bug* — a tautology the story itself forbids. `TimerDeterminismTests` instead asserts the emission order directly (reflection white-box, the same idiom as `SimChecksumCoverageGuardTest`): two opposite insertion orders must both emit ordinal-sorted — and the reverse-insertion case **fails if the fix is reverted** (verified RED). The float-round-trip guard is the 1.3b de-DE `ScenarioDirectorThresholdTests`, updated to `Fixed` `Amount` literals, still green.

**Scope fences honored:** no broad model-wide float→Fixed sweep, no `ContentLoader`/canonical-options unify, no `Validated<T>`/`ScenarioValidator`/`ScenarioApplier`, no graph-IR/`DslVarTable`/timer-variable SoA fold, no FogOfWar fix, no Roslyn analyzer, no `X`/`Z`/`Duration` conversion, no golden hand-edit. No new dependencies. Pre-existing `CS8632` warnings left untouched.

### File List

**Production (sim + AI):**
- `godot/src/Core/Definitions/FixedJsonConverter.cs` — **NEW**: the `JsonConverter<Fixed>` quantization boundary.
- `godot/src/Core/Definitions/TriggerDefinition.cs` — MOD: 5 fields `float→Fixed` (`TriggerEvent.Amount`, `TriggerCondition.Amount`, `TriggerAction.Amount`, `TriggerAction.TimerSeconds`, `TriggerDefinition.CooldownSeconds`); `+using ProjectChimera.Core`.
- `godot/src/Core/Definitions/ScenarioSerializer.cs` — MOD: register `FixedJsonConverter` on `_options`.
- `godot/src/Core/ScenarioDirector.cs` — MOD: de-float 3 in-tick sites + `create_timer` + cooldown (Fixed math); stable total-order sort; ordinal-sorted `_timers` snapshot; `+using System.Linq`.
- `godot/src/AI/LLMService.cs` — MOD: register `FixedJsonConverter` on both AI-ingest options; `Validate` Pass-5 `TimerSeconds` guard → `Fixed.Zero`; `+using ProjectChimera.Core`.

**Tests (Tier-1 xUnit, Godot-free):**
- `godot/ProjectChimera.Sim.Tests/Determinism/FixedJsonConverterTests.cs` — **NEW**: AC1 (quantize/NaN/Inf/over-range/located-error + TriggerAction integration), 12 cases.
- `godot/ProjectChimera.Sim.Tests/Golden/TriggerOrderingTests.cs` — **NEW**: AC3 (24-trigger equal-priority ascending order + distinct-priority primary key).
- `godot/ProjectChimera.Sim.Tests/Golden/TimerDeterminismTests.cs` — **NEW**: AC4 (emission-order determinism, reflection white-box; bites on revert).
- `godot/ProjectChimera.Sim.Tests/Golden/ScenarioDirectorThresholdTests.cs` — MOD: `Fixed` `Amount` literals (de-DE float-round-trip guard kept green).

**Tracking:**
- `_bmad-output/implementation-artifacts/sprint-status.yaml` — `1-4` → `review`.
- `_bmad-output/implementation-artifacts/1-4-…-negative-tests.md` — this story (frontmatter `baseline_commit: baf9ae5` preserved; tasks, Dev Agent Record, Status).

_Note: Godot may auto-generate `.cs.uid` sidecars for the new test `.cs` files (engine artifact, not hand-authored)._

### Change Log

| Date       | Change |
|------------|--------|
| 2026-06-23 | Implemented Story 1.4. Net-new `FixedJsonConverter` (AR-14 single quantization boundary; NaN/Inf/over-range reject, resolves 1.3b's ≥32768 deferral). Five trigger fields `float→Fixed` (the four named + `CooldownSeconds`, a missed in-tick-float gap completing AC2). Removed all in-tick `Fixed.FromFloat`/float math from the trigger path (3 compare/add sites + `create_timer` + cooldown). Stable total-order trigger sort (Priority desc, declaration index asc) and ordinal-sorted `_timers` enumeration (AR-16). Added 15 tests (converter + AC3 + AC4) with verified RED negative controls; updated 1.3b threshold test to `Fixed`. Goldens byte-identical; `AlgoVersion` unchanged. Status → review. |
