---
title: D2 — Trigger-DSL Design — Decision Briefing
status: DRAFT — analysis complete, DECISION PENDING (user makes the call)
workflow: gds-game-architecture Step 4, decision D2
generated: 2026-06-20 (ultracode D2 deep-dive workflow, 10 agents, adversarially verified)
note: Working sidecar. The final D2 DECISION record goes into game-architecture.md only after the user decides. Delete this once D2 is recorded there.
---

# D2 — Trigger-DSL Design: Decision Briefing (FINAL)

*Architecture deep-dive · consumes D1 (Bounded Effect-Graph) · produces the briefing, not the call. The user makes the final decision.*

> **Grounding status.** Every load-bearing code claim in this briefing was verified against the live source: `ScenarioDirector.cs`, `SimChecksum.cs`, `LockstepManager.cs`, `ReplayRecorder.cs`, `ReplayPlayer.cs`, `LLMService.cs`, `NetworkCommand.cs`. Confirmed-absent (D1 deliverables D2 depends on): `godot/src/Effects/**`, `godot/src/**/SimRng*.cs`. This FINAL revision folds in three adversarial reviews (determinism, genre-coverage, brownfield). Where a critic drew blood, the fix is in the design; where a critic over-reached, I say so and why. **Five corrections changed the briefing's substance** and are called out inline with ⚑.

---

## 1. D2 IN ONE PARAGRAPH

D1 settled **what a trigger's actions can do** — a sealed, closed, capped, Fixed-only `EffectNode` graph. **D2 is the logic layer wrapped around that vocabulary**: typed scoped variables and arrays, Fixed-point arithmetic/boolean expressions, bounded loops, timers, define-and-raise custom events, and creator-authored runtime UI bound to DSL variables — the machinery that decides *which* effect graphs run, *when*, *how many times*, with *what data*, and *what the player sees and clicks*. The whole decision is one tension, the project's named top risk: the **DSL Goldilocks point**. Too narrow and the platform fails its North Star ("build any game" — no TD wave loop, no MOBA last-hit, no RPG quest flag). Too broad and the DSL becomes **unvalidatable or non-deterministic**, violating the two non-negotiables (NFR-6 *statically server-validatable before any tick*; NFR-4 *deterministic 30 Hz lockstep*). Every reference engine resolved this with a Turing-complete escape hatch (JASS, Galaxy, Lua); **Chimera categorically forbids that** (FR-27, decision #12). The escape from the trap is not to *prove arbitrary programs safe* (undecidable) but to **forbid the undecidable region by construction** — a Starlark/CEL/eBPF-shaped bounded language where non-termination *cannot be expressed* — and recover ~the 90% of real creator content that only ever used bounded patterns. The recommendation is a **single typed event/dataflow graph IR (Option C) that *contains* D1's effect subgraphs**, so D2 extends D1's validator and executor rather than duplicating them. ⚑ But the honest headline guarantee is **per-tick bounded cost**, *not* whole-program termination — timers and next-tick events deliberately reintroduce unbounded cross-tick iteration, exactly as any event system must (Critique-1 §4, correct; fixed throughout).

---

## 2. REQUIREMENTS CHECKLIST

Per decision #13 (maximal 1.0) + PRD §6.1, almost everything is MVP. "Stretch" is reserved for items the sources *explicitly* defer.

| # | Capability | Tier | Source |
|---|---|---|---|
| **Variables & data** | | | |
| V1 | Typed variables (closed type set) | **MVP** | FR-24, dec #12 |
| V2 | Scopes: global / per-player / trigger-local | **MVP** | FR-24 "typed, scoped" |
| V3 | Arrays / collections (static-capacity) | **MVP** | FR-24 |
| V4 | Records/structs (named typed fields) | **Stretch-leaning ⚑** | INPUT 4 §2.2 — *moved from MVP-recommended; see §10 Q2* |
| V5 | Per-instance / per-unit variable scope | **Stretch** | overlaps D1 Modifier SoA |
| **Expressions** | | | |
| E1 | Arithmetic (Fixed-point) | **MVP** | FR-24 |
| E2 | Boolean (OR / NOT / grouping) | **MVP** | FR-24 |
| E3 | Fixed-only; no float in the tick | MVP-constraint | FR-27, NFR-4 |
| E4 | Bounded grammar (widen D1's `! && || comparisons count()`) | MVP-constraint | D1 validator rule #1 |
| **Control flow** | | | |
| C1 | Conditionals (ECA + in-action branch) | **MVP** | FR-23/24 |
| C2 | Bounded loops (`ForEach`-over-finite) | **MVP** | FR-24 |
| C2b | Sanctioned cross-tick batched iteration (`ForEachBatched`) | **MVP ⚑** | *new — Critique-2 §5/6; the answer to ">cap iteration"* |
| C3 | Provable **per-tick** bounded cost | MVP-constraint | NFR-4, FR-27 |
| **Timers / events** | | | |
| T1 | Timers (create, expiry → logic) | **MVP** | FR-24 |
| Ev1–2 | Define + raise custom events (decoupled) | **MVP** | FR-25 |
| Ev3 | Built-in event set → event-driven bus | **MVP (verify)** | FR-23; INPUT 4 §1.7 |
| Ev4 | Rich typed event payloads incl. **killer/last-hit attribution** | **MVP-prereq ⚑** | *new — Critique-2 §2; combat-layer dependency* |
| **Custom UI** | | | |
| U1 | Closed widget set incl. **data-bound repeater/`ItemList`** | **MVP ⚑** | FR-26 + *Critique-2 §7* |
| U2 | UI bound to DSL variables (read path) | **MVP** | FR-26 |
| U3 | Buttons → raise custom events (write path) | **MVP** | FR-26, INPUT 5 |
| U4 | UI statically validatable | MVP-constraint | NFR-6 |
| **Tiers** | | | |
| A1–A4 | T1 presets · T2 ECA (build real editor) · T3 GraphEdit · T4 NL/AI | **MVP** | FR-28, dec #13 |
| A5 | All four tiers **share ONE IR**; full bidirectional edit only in the IR-native tier ⚑ | **MVP (downgraded claim)** | *Critique-2 §8 — see §6 sub-dec 8* |
| **Governing** | | | |
| H1–H6 | Deterministic · Fixed · statically validatable · no escape hatch · expressiveness-bounded-by-validatability · actions emit D1 `EffectNode`s | MVP-constraints | FR-27, NFR-4/6, dec #12 |

**Explicitly out of scope:** runtime-*created* widgets (pre-declared-and-toggled only at MVP); D1's own stretch effects (`Switch`, `NamedEffectReference`, `Morph`, `IssueOrder` — D2 must **not** depend on `Switch` for its branching); mid-game save/full-world serializer (`[v2]`); **runtime strings / string variables / string parsing — permanently out** (non-deterministic). ⚑ Critique-2 §3 is right that this *forfeits a real WC3 capability class* (player-named heroes, typed passwords, runtime renaming). We accept the forfeit — it is inseparable from determinism — but now **state it plainly** rather than burying it: *display text is presentation-side templates only; there is no runtime player-authored text.*

---

## 3. THE DESIGN ENVELOPE

Constraints **every** option in §4 must satisfy. Each stated as a firm recommendation.

### 3.1 Termination / cost model — **layered hybrid: structural gate + static cost rejection + checksummed fuel seatbelt**

The heart of D2. The doctrine, borrowed verbatim from eBPF: **"unprovable means invalid."** But ⚑ the FINAL framing corrects the draft's central over-claim (Critique-1 §1, §3, §4 — all correct):

> **The guarantee is PER-TICK BOUNDED COST, not whole-program termination.** Layer 0 makes each tick's work *halt*; Layers 2–3 make it *affordable and identical on every client*; cross-tick oscillation (timers, next-tick events) is deliberately unbounded over the match and is a *liveness/resource* concern, not a determinism one.

Four layers, strongest/earliest first:

**Layer 0 — Grammar cannot express non-termination (Starlark).** *Primary.*
No `while`, no recursion, no `goto` — **absent from the grammar, not guarded**. The only loop form is `ForEach` over an **already-materialized finite collection** (a DSL array, a `SearchArea` result ≤64, a player/unit group, or `range(k)` with `k` a load-bounded constant or runtime value **clamped** to a named cap). The collection is **snapshotted at loop entry**. Each tick's work is therefore structurally finite.

**Layer 1 — Acyclic custom-event dispatch, proven at load (eBPF call-graph DAG).** *Primary for events.*
Build the static event-dispatch graph at load and **prove it a DAG (reject cycles)** — this kills the within-tick "trigger runs trigger runs trigger" stack-overflow class that sank WC3's `Trigger - Run`. The one escape valve for legitimate state-machine feedback (A→B→A) must **cross a tick boundary** (`RaiseEventNextTick` / 1-tick timer), which keeps the *within-tick* dispatch graph acyclic.

**Layer 2 — Static cost bound (CEL cost estimation).** ⚑ *Substantially strengthened — Critique-1 §1 and §3 both correct.*
Two corrections fold in here:
- **(a) Cost is the product of declared CAPS, not runtime lengths.** Because array lengths are runtime-dynamic, the worst-case node-visit count for a nested `ForEach` must be computed from `MaxArrayCapacity`, not the actual length. Critique-1 §1 is right that this makes the DSL *narrower than "nest freely"*: `MaxArrayCapacity=256` with `MaxLoopNestingDepth` quickly exceeds any sane `MaxDslOpsPerTrigger`. **Consequence, stated honestly:** deep nesting over large arrays is *rejected at load*, and the **caps are the load-bearing risk, not a tuning dial** (see §9). The mitigation that keeps genres buildable is **per-loop declared bounds** as part of the static signature (a loop may declare "≤16 here") plus the `ForEachBatched` cross-tick primitive (C2b) for genuinely large sets.
- **(b) Cost must close over the event-dispatch DAG's transitive closure.** ⚑ Critique-1 §3 is correct that a DAG bounds *depth* but not *fan-out*: depth-8 × fan-out-8 = 8⁸ handler invocations, all "acyclic," which only Layer 3 would catch — breaking "fuel never fires on valid content." **Fix:** the Layer-2 estimator sums cost over the **transitive closure** of the event DAG (computable precisely *because* it is a DAG — longest-weighted-path / total-reachable-cost), and rejects at load if worst-case cascade cost exceeds `MaxCascadeOps`. Add `MaxEventFanOut` as a named cap. *Without this, "raise event" is the unbounded escape hatch wearing a DAG costume.*

**Layer 3 — Deterministic per-tick fuel budget (lockstep pattern).** *Last-resort seatbelt, fully checksummed.*
A per-tick global instruction budget (`MaxDslOpsPerTick`), decremented per DSL node across all triggers, **halting deterministically at a whole-trigger boundary** (never mid-effect-leaf, so it can never apply half a `Sequence`). **This counter is sim state and folds into `SimChecksum`.** Because Layers 0–2 now reject *all* legitimately-expensive content (including cascades), Layer 3 only ever fires on **malformed/hand-edited** definitions, where defensive halting is acceptable. **Wall-clock / WASM epoch metering is forbidden** — only count-based, sim-state-derived fuel is admissible.

**Static rejection is the gate; fuel is the seatbelt.** Pure fuel is wrong as the gate (torn state on a sim that can't cheaply roll back, terrible "sometimes half-runs" UX). Pure static rejection is correct *and complete here* — completeness is undecidable in general, but Chimera *sidesteps undecidability by not admitting the hard programs* (Layer 0).

⚑ **"Clamp at runtime" is deleted from the vocabulary (Critique-3 §10, correct).** The draft said caps are "enforced at load (reject) AND at runtime (clamp)" — but a runtime *clamp* is exactly the silent partial execution the fuel doctrine condemns, and the as-built `Math.Min(a.Count, 50)` (`ScenarioDirector.cs:312`) is precisely that footgun. **Resolution:** runtime caps are **assertions that never fire on validated content** (identical logic to Layer-3 fuel) — on a validated scenario they are unreachable; on a malformed one they halt-with-diagnostic at a trigger boundary. It is *"reject at load, assert at runtime,"* never *"clamp."* ⚑ Critique-2 §6 raises the mirror case — a `ForEach` over a group whose runtime size *exceeds* cap would *silently truncate*. **Resolution:** truncation is never silent — a group whose provable max size exceeds the loop cap is either a **load-time validator error** ("this group can exceed 64; choose `ForEachBatched` or declare a cap") or, for `ForEachUpTo(cap)`, a **loud authored choice** the creator explicitly opted into.

**Named termination constants** (named, reviewable, corpus-validated like D1's caps):
`MaxLoopIterations` (≈256), `MaxLoopNestingDepth` (≈3), `MaxEventCascadeDepth` (≈8), **`MaxEventFanOut`** ⚑, **`MaxCascadeOps`** ⚑, `MaxDslOpsPerTrigger` (Layer-2), `MaxDslOpsPerTick` (Layer-3), `MaxArrayCapacity` (≈256), `MaxNextTickEventQueue` ⚑ (bounds the inter-tick oscillator — Critique-1 §4), `MaxVariablesPerScenario`. **All corpus-validated before D2 locks — see §9, this is a representation gate, not a dial.**

**How it stays lockstep-deterministic** (the sharpest constraint):
- **Fixed-only** (16.16) in every expression reaching the tick; authored float→Fixed converted **exactly once at load**, NaN/Inf rejected. The current `float`-epsilon `==` (`ScenarioDirector.cs:364`) and `float.TryParse` on stringified ore (`:252`) are **deleted**.
- ⚑ **The action-boundary float leaks are retyped (Critique-1 §5, correct and under-scoped in the draft).** Verified additional float surfaces beyond the two the draft named: `OnSpawnUnit` delegate signature is `Action<string,int,float,float,int>` (`:47`); `add_resources` does `Fixed.FromFloat(a.Amount)` *at runtime* (`:336`); `create_timer` does `(int)(a.TimerSeconds * TICKS_PER_SECOND)` float→int truncation (`:331`). **Fix folded into D1s/D2s:** retype `OnSpawnUnit` X/Z → `Fixed`, all `TriggerDefinition` numeric fields → `Fixed`/`int` at the deserialization boundary, timer seconds→ticks via integer math.
- ⚑ **Deterministic iteration order — TWO live nondeterminisms in the as-built code, neither flagged by the draft (Critique-1 §2 and §10, both correct and verified).**
  1. `EvaluateTriggers` builds order via `Array.Sort(order, (a,b) => Priority diff)` (`:192`) — **`Array.Sort` is unstable**. Today latent; the instant D2 adds shared mutable variables / fuel / cascades, *equal-priority trigger order determines the result* and an unstable sort differs across runtime versions. **Fix (prerequisite, not enhancement):** total order `(Priority desc, then declaration-index asc)` via explicit comparator.
  2. Timer expiry iterates `new List<string>(_timers.Keys)` (`:149`) — **`Dictionary` key order is not contractually deterministic.** **Fix:** `DslVarTable` and the timer store use **dense index-keyed arrays**; simultaneous timer expiries fire in **timer-declaration-index order**.
  Both get explicit negative tests (two equal-priority triggers / two same-tick timers, both writing a shared var → identical checksums across runtimes). ⚑ These also gate `SimRng` draw-order determinism (Critique-1 §8): RNG amplifies the sort bug, so "deterministic SimRng draw order" is an invariant *dependent on* the total-ordering fix.
- **Single seeded `SimRng`** (D1 prerequisite) is the only randomness; the validator rejects any random DSL construct until it ships + is checksummed.
- **No wall-clock** anywhere; all time is the tick counter; all delay is `StartTimer` → event (no `Wait`/inline sleep).
- ⚑ **Zero per-tick heap allocation in the event/eval path (Critique-3 §8, correct).** The as-built `CollectEvents` allocates a `List` every tick (`:108`) and `EvaluateTriggers` allocates an `int[]` + sort every tick (`:190`) — both violate the project's no-per-tick-GC rule. The D3s event-bus rewrite must retire these (allocate-at-load), as an explicit acceptance criterion.
- **Variables, arrays, timers, event queue, next-tick queue, and the fuel counter all fold into `SimChecksum`** — closing the confirmed hole where `_variables`/`_timers` are invisible today (`SimChecksum.cs:26-57` hashes only World/Buildings/Resources).

### 3.2 Variable / type model — **typed, scoped, SoA, checksummed `DslVarTable`**

Replace `Dictionary<string,int> _variables` (`ScenarioDirector.cs:34`) with a typed, scoped, dense-index-keyed table.

**Closed value-type set:** `Int`, `Fixed` (16.16; the *only* fractional numeric — no float type), `Bool` (0/1), `EntityRef` (id + generation, detects stale refs), `FactionRef`, `Point` (Fixed X,Z), `TimerRef`, `Array<scalar>` (flat backing + length, declared capacity ≤ `MaxArrayCapacity`). ⚑ **`Record` is moved to stretch-leaning** (was MVP-recommended) — see §10 Q2; genres remain buildable with parallel arrays, less ergonomically.

**Scopes:** Global (one per scenario; persisted; scoreboard/economy), Per-player (faction slot 0..7; per-hero gold/lives/votes), Trigger-local (allocated on fire, freed at end; **loop counters are *always* lexically-scoped locals — never engine-global**, killing WC3's `Integer A` reentrancy bug). Per-instance/per-unit is first-stretch.

**Storage & checksum:** SoA, **dense-index-keyed arrays (never string-hashed dictionaries)** ⚑, allocated at scenario-load. Every live global/per-player value mixes into `SimChecksum` in **declaration-index order**. ⚑ **The table must be hoisted out of `ScenarioDirector` into a top-level sim store** (sibling of `BuildingStore`/`ResourceStore`), because `SimulationLoop` computes the checksum and has no reference to the director (Critique-3 §1, correct — this is an ownership refactor, not "extend Compute"). Declaration is **explicit** (name + type + scope + initial value in `ScenarioData`), giving the validator a closed registry to type-check against.

### 3.3 Expression model — **CEL-shaped pure, typed, side-effect-free sublanguage (Fixed-point)**

Adopt CEL's architecture for the arithmetic+boolean layer: pure, loop-free, side-effect-free expression trees over typed scoped variables with a **closed function set** (`+ - * / mod` on Fixed/Int with div-by-zero rejected at validation; `> < >= <= == !=`; `&& || !`; bounded built-ins `count`, `distance`, `min/max`, `abs`; bounded comprehensions `all`/`any`/`exists` over a capped collection). **Two-phase, exactly like CEL:** parse + type-check + cost-estimate at load (server-side) → evaluate the pre-checked AST in the tick (cheap, Fixed-only). This is a **strict extension of D1's already-chosen bounded grammar** — D1 mandates its `Switch` validator be exactly `! && || comparisons count()`; D2's expression grammar *is that same grammar, generalized with arithmetic and typed variable reads*. Keep the **Function (pure, read/condition) vs. Action (effectful, write = D1 graph)** split; a condition is just a boolean expression. This retires the as-built pure-AND `AllConditionsMet` (`:263`) in favor of real OR/NOT/grouping.

### 3.4 Event model — **engine-emitted bus + acyclic custom events**

**Built-in events → event-driven bus.** Today `CollectEvents` *polls* thresholds for both factions every tick and *diffs* entity flags O(entities)/tick (`:106-175`), squeezing payloads through a nullable `string` that gets stringified-and-reparsed (`:170`, `:252`). Replace with sim systems **emitting typed events** (closed structs, not stringified blobs) into a per-tick bus; thresholds become **edge events** on crossing. ⚑ **This is a semantic change, not "observably identical" (Critique-2 §10, correct):** the current `resource_threshold` is *level-triggered* (re-fires every tick while sustained, per the `:164` comment). A map relying on "fires every tick while ore ≥ 500" **breaks** under edge semantics. **Resolution:** support both a `WhileTrue`/level form and an `OnCross`/edge form explicitly; do not claim golden-checksum identity across D3s — it is a **declared, migrated behavior change** (see §7 D3s gate).

⚑ **Rich payloads incl. killer attribution are a hard prerequisite (Critique-2 §2, correct and load-bearing).** The as-built `unit_dies` event carries only the *victim's* slot (`:126`); there is no killer reference anywhere. MOBA last-hit gold and kill-credit quests are **unbuildable** without `killer:EntityRef`/`FactionRef` on the death event — and no DSL feature fixes it. Whether the combat layer records last-hit attribution is a **D1/combat-layer prerequisite**, now surfaced in §8.

**Custom events — define + raise + subscribe** (FR-25): a closed registry of author-defined names (optional typed params), a `RaiseEvent` action enqueuing a handler invocation, **same-tick** processing under the Layer-3 fuel budget + `MaxEventCascadeDepth` + the **Layer-2 transitive cascade-cost bound** ⚑. Subscription is a static graph; the dispatch graph is **proven a DAG at load**; legitimate feedback crosses a tick. ⚑ **Same-tick dispatch is a rewrite of the evaluation loop, not an addition (Critique-3 §4, correct).** The as-built `EvaluateTriggers` is a *single priority sweep* with no notion of a trigger firing another within the tick; same-tick raise requires a **work-list drain** that re-enters. D2 must explicitly specify the new loop *and* the semantics the draft left undefined: **does a same-tick re-raise respect `_triggerCooldown`? can a `RunOnce` trigger fire twice in one tick via events?** Recommended answers: cooldown/run-once are checked per-dispatch (a run-once trigger fires at most once per match even if re-raised; cooldown suppresses re-entry) — pinned in §6 sub-dec 7.

### 3.5 Custom-UI binding model — **two rails on existing infrastructure; READ is free, WRITE goes through lockstep**

INPUT 5's recommendation is adopted because it adds **no new networking, no new threading** — it grafts leaf types onto shipped rails. But ⚑ **two of the draft's claims here were materially wrong and are corrected** (Critique-1 §6, Critique-3 §5/§6).

**READ rail (sim → UI) — `CustomUiBridge` + versioned `DslVarReadback`, modeled on `FogOfWarBridge`.**
At tick boundary (after triggers run last on settled state), the sim publishes a read-only, versioned snapshot of the `DslVarTable` to a presentation-side buffer with per-variable dirty/version stamps. UI widgets pull in `_Process` and re-format only when the bound variable's version changed. **Formatting is presentation-side** (int→string, Fixed→`mm:ss`, enum→label) — **strings never enter the tick.** Pure, one-way, presentation-only, **cannot desync, not in the checksum** (it is a copy of already-checksummed state). This ships scoreboards/wave-counters/timers with **zero command-rail change.**

**WRITE rail (UI → sim) — a new `DslEventCommand` on the lockstep command bus.**
A button's `Pressed` handler **mutates nothing**; it calls a new `LockstepManager.EnqueueDslEvent(eventId, args)` — the analog of `EnqueueOrder` (`:215`) — capturing `LocalFaction` as raiser. It rides the existing pipeline (buffered, serialized into `TickCommandPacket`, applied at `currentTick + _currentDelay`). SP/MP fall out for free (offline → apply-now; online → defers — same code paths, verified at `:217` and `:315-316`).

⚑ **Correction 1 — "preserves the per-faction check" is overstated (Critique-1 §6, correct).** The anti-cheat at `:601` is `if (_world.FactionOf[id] != expectedFaction) continue` — it checks that the *commanded unit* belongs to the faction. A `DslEventCommand` has **no unitId**; its only authority binding is the packet-header raiser faction. So event authorization ("may faction F raise event E?") is **net-new sim state** (a per-event allowed-raiser set checked in `ApplyDslEvents`), not a reuse of the unit-ownership check. The "you already voted" guard is a **sim-side condition on the handler**, never client-side button-disable.

⚑ **Correction 2 — "replays are free" is FALSE as written (Critique-3 §5 AND §6, both correct, both verified — this was the draft's headline write-path argument).** Two independent facts:
- `ReplayRecorder.RecordTick` is **hardwired to `UnitOrder`**: it writes exactly `unitId(2)+cmd(1)+tx(4)+tz(4)` per order (`ReplayRecorder.cs:74-81`), `VERSION = 1` (`:25`). A `DslEventCommand` has **no representation in the replay file**. DSL events ride the *network* `TickCommandPacket` for free (true) but **NOT the replay file** (false).
- `ReplayPlayer` is explicitly a **"drop-in replacement for the LockstepManager's online Flush() path"** (`ReplayPlayer.cs:13`) — it **bypasses `LockstepManager` entirely**, parses only the `UnitOrder` layout (`:98-102`), and has its *own duplicated* `ApplyOrders` (`:142`). So a `ApplyDslEvents` "beside `ApplyOrders` in LockstepManager" is **never reached during replay**.
**Resolution (folded into D9s):** the write path requires a `ReplayRecorder.VERSION → 2` bump with a new DSL-event record kind, a `ReplayPlayer` parse+apply branch, and DSL-event application threaded through **all command-application sites** — there are **four**: live-player `ApplyOrders` (`:315`), spectator branch (`:261`), `ReplayPlayer.ApplyOrders` (`:142`), and the recorder (`:318`). The draft's "one new `ApplyDslEvents`" is a **~4× undercount**. **Demote the claim:** "free *network* propagation; replay requires a format-v2 record type." A pre-D9s cleanup that unifies the three `ApplyOrders` copies into one injection point is strongly recommended.

⚑ **Correction 3 — pin the tick-phase ordering (Critique-1 §6, correct, unspecified in draft).** Whether the raised event is visible to the director the same tick depends on whether `ApplyDslEvents` runs *before* the sim systems tick. The director "runs last on settled state" (`:11-13` comment, verified). **Pinned canonical order:** `ApplyDslEvents` (enqueue onto sim bus) → sim systems tick → `ScenarioDirector` drains the bus — documented as checksum-relevant semantics so all clients agree which tick the handler fires.

**Wire encoding — Option B (parallel capped event list):** extend `TickCommandPacket` to `… + orderCount + orders[] + eventCount + events[]`, each event a small fixed struct, separately capped (`MaxDslEventsPerTick`). Clean separation, own cap/validation, raiser from the header. (Option A — overloading `UnitOrder` with a sentinel — muddies the unit-order struct and lacks the per-event authorization; rejected.)

**Local-only buttons** (toggle a panel, open a sub-menu) use a **closed presentation-action whitelist** (`ToggleWidgetVisible`, `OpenSubPanel`, `CloseSelf`, per-client `SetLocalUiVar`) and are **statically barred from touching any DSL variable or event** (validator enforces disjoint sim/local-UI namespaces) — so a cosmetic toggle can never branch sim logic.

**Authoring + validation:** custom UI is a declarative widget tree in `ScenarioData` (covered by the existing `scenarioHash` so divergent UIs refuse to start), from a **closed widget vocabulary** — `Panel, Label, Counter, ProgressBar, Button, Timer, Leaderboard, FloatingText`, **+ `ItemList`/data-bound repeater** ⚑ (Critique-2 §7 — without it, "a shop UI whose item count you control" is impossible; this is the difference between "scoreboards work" and "custom UIs work"). Every `BindVar` resolves + type-matches the closed registry; every `BindEvent` resolves to a registered event or valid local widget; hard named-constant caps (widgets ≤256, depth ≤8, leaderboard/list rows ≤64) **rejected at load, asserted at runtime** (not clamped).

---

## 4. THREE OPTIONS

All three satisfy the §3 envelope at MVP (it is non-negotiable). They differ in IR shape, execution, four-tier fit, brownfield cost, and risk.

### Option A — Extended ECA-as-Data
Evolve the flat ECA structure into a richer **typed declarative tree**, interpreted by an evolved `ScenarioDirector`.
- **IR:** `TriggerDefinition` keeps its role but is typed and deepened — `Events[]` typed + custom subscriptions; `Conditions` → one boolean expression tree; `Actions` → an ordered **statement list** where each statement is a D1 `EffectDef`, a variable-assignment (expression RHS), a bounded `ForEach` block, a `RaiseEvent`, or an `If/Else` block (D2's own branching, **not** dependent on D1 `Switch`). Statements nest; it stays *data interpreted*, not compiled.
- **Execution:** evolved director walks the statement tree with an explicit work-stack (mirroring D1's executor); effects compile-once to `EffectNode`s; fuel decremented per statement; event-driven bus replaces polling.
- **Termination:** Layer 0 by construction (no statement type for `while`/recursion). Layers 1–3 as §3.1.
- **Four tiers:** T2 is **native** (the real sentence editor the as-built panel never built). T1 = filled subtrees. T4 = same statement-tree JSON → 5-pass validator. **T3 is the weakest fit** — a nested statement tree must be rendered into a node graph and parsed back; T3 is an **adapter, not native**.
- **Verdict:** ✅✅ lowest migration / risk; ✅ full capability; ⚠️ ergonomic ceiling on complex nested logic; ⚠️ **T3 (MVP-mandated) is a second-class adapter**.

### Option B — Bounded Imperative IR + tiny deterministic VM
A compact typed **instruction list** (eBPF/Starlark-inspired) all tiers compile to, run by a minimal deterministic VM.
- **IR:** closed opcode set — `LOAD/STORE` (typed, scoped), Fixed/Int arithmetic, comparisons, `FOREACH_BEGIN/END` (snapshotted, cap-encoded), `IF/JUMP_IF_FALSE` (**forward-only**, no back-edges except the verified `FOREACH`), `CALL_EFFECT` (invoke a D1 `EffectDef` by id), `RAISE_EVENT`, `START_TIMER`. No backward-jump opcode exists → no `while`/recursion by construction.
- **Execution:** a tiny deterministic VM (typed operand stack, scoped slots, Fixed ALU, fuel per instruction), allocated once at load.
- **Termination:** B's strength — the opcode set has no back-edge, so a **load-time verifier** proves the CFG is a DAG-with-bounded-loops (eBPF's pre-5.3 model, *for free* because Chimera controls the compiler). Layers 1–3 as §3.
- **Four tiers:** the instruction IR is unambiguously canonical; all tiers are compilers/decompilers. **Round-tripping (T2→T3→T2) requires decompilation** (bytecode → structured view) — real, novel, correctness-risky work.
- **Verdict:** ✅✅ highest ceiling + cleanest *conceptual* single-IR; ❌ highest build (compiler + verifier + VM + 4 decompilers) and highest risk; ceiling **exceeds the WC3-parity bar** (decision #11, explicitly *not* Roblox scale). ⚑ I **partially disagree** with the draft's "two paradigms" framing: `CALL_EFFECT`-by-id is clean, and one could argue the VM *subsumes* rather than sits-beside the graph. But the substantive point stands — it is maximum infrastructure for headroom the project deliberately doesn't want.

### Option C — Typed Event/Dataflow Graph (Blueprints-style), graph IR is canonical
A typed visual graph with **two edge types** (exec-flow + data-flow) as the canonical IR; bounded-loop nodes; acyclic event edges. **The D1 effect graph is the action-region sublanguage** — the trigger graph is a *superset that contains* D1 effect subgraphs.
- **IR:** exec edges thread `Event → Node → Node` (an "action" region *is* a D1 `Sequence`/`SearchArea`/`Persistent`/leaf graph); D2 adds logic exec-nodes (`ForEach`, `Branch`, `RaiseEvent`, `SetVariable`, `StartTimer`). Data edges carry typed values — a data subgraph is literally a visual expression tree (§3.3 rendered as wires; wire color = type). Variables in a side table; **persistent node ids** enable lossless round-trip + on-node error rendering.
- **Execution:** compiled once at load into a topologically-ordered exec plan; data subgraphs flattened to pre-checked ASTs pulled lazily; runtime walks with a work-stack (**D1's executor, generalized**); the action region invokes the contained D1 graph directly (no translation — it *is* a D1 graph); fuel per node.
- **Termination:** the validator is a **graph linter — the exact kind D1 already mandates, extended** with three rules (data-edge purity, bounded-loop-node is the only back-edge, acyclic event edges). One validator covers both effect subgraphs (D1's rules) and logic nodes (D2's additions).
- **Four tiers:** **T3 GraphEdit is native, first-class** — the only option where the MVP-mandated T3 is the IR rendered directly. T1 = macro subgraphs. T4 emits the graph. **T2 needs a defined fallback** for graph-only constructs (read-only "edit in graph view" placeholder) — the mirror of A's T3 weakness.
- **Verdict:** ✅✅ highest D1-unification (one graph, one executor, one validator); ✅✅ native T3; ⚠️ medium migration; ⚠️ **dominant risk = Godot `GraphEdit` is "Experimental"** + T2 fallback.

### 4.x Comparison matrix

| Criterion | **A — Extended ECA-as-Data** | **B — Bounded Imperative IR + VM** | **C — Typed Event/Dataflow Graph** |
|---|---|---|---|
| Static-validatability / termination | ✅✅ trivial (schema can't express it) | ✅✅ principled (literal verifier) | ✅✅ reuses D1 graph linter |
| Determinism | ✅✅ smallest new surface | ✅ new VM = more surface | ✅ same executor family as D1 |
| Expressiveness / genre coverage | ✅ full (ergonomic ceiling) | ✅✅ highest (beyond the bar) | ✅ full (clean nested logic) |
| Authoring — T1 presets | ✅ native | ✅ compile target | ✅ native (macros) |
| Authoring — T2 ECA | ✅✅ **native** | ⚠️ needs decompiler | ⚠️ needs graph-only fallback |
| Authoring — T3 GraphEdit (**MVP**) | ⚠️ **adapter (2nd-class)** | ⚠️ needs decompiler | ✅✅ **native (1st-class)** |
| Authoring — T4 NL/AI | ✅✅ easiest (flat-ish JSON) | ✅ emits bytecode (harder) | ✅ emits graph (slightly harder) |
| One-IR cleanliness | ✅ good (tree canonical) | ✅✅ cleanest concept | ✅✅ cleanest practice (Blueprints) |
| Brownfield migration cost | ✅✅ **lowest (evolve director)** | ❌ **highest (replace w/ VM)** | ⚠️ medium (extends D1 graph work) |
| D1 unification | ✅ clean (wraps `EffectDef[]`) | ✅ clean, arguably 2 paradigms | ✅✅ **highest (one graph; superset)** |
| Net-new infrastructure | ✅ least | ❌ most | ⚠️ medium |
| Dominant risk | T3 fidelity; high-end ergonomics | VM/decompiler correctness; over-built | `GraphEdit` "Experimental"; T2 fallback |

---

## 5. RECOMMENDATION

> **Adopt Option C (typed event/dataflow graph) as the single canonical IR — with the graph serialization (node ids + two typed edge types) canonical from the FIRST migration step, not retrofitted at the end — where C's graph *contains* D1's effect subgraphs, and `GraphEdit` is a replaceable view behind an abstraction layer.**

⚑ **This is the one place I substantively revise the draft's recommendation shape (Critique-2 §9 and Critique-3 §7, both correct — the draft's "hybrid in sequencing, one IR from day one" was internally contradictory).** The draft proposed shipping an A-shaped *statement-tree* slice through T2/T4 for steps D1s–D6s, then "re-expressing" it as a graph at D7s. But a statement-tree authored and round-tripped through T2 *is a tree*; calling it a graph doesn't make T2→graph lossless, and "re-express at D7s" is precisely the decompiler/migration risk used to reject Option B — paid once on live user content. **You cannot bank A's low migration cost AND claim C's graph-is-canonical purity across that boundary.**

**The resolution (decisive):** commit to the **graph-shaped serialized IR from D1s** — node ids and typed edges present in `ScenarioData` from the first step, even while *only T2 and T4 front-ends exist*. T2 renders a **linear projection of a graph** from the start (a sentence list is a perfectly good projection of an exec-edge chain). `GraphEdit` (T3) becomes a **later, additive editor view** over an IR that was always a graph — **no D7s content migration, no second serialization.** This keeps the genuine benefit the draft wanted (de-risk `GraphEdit` by not making it a prerequisite — three of four tiers ship and are fully usable before the editor exists) **without** the overclaim. The honest one-line characterization: *this is C's graph IR, authored through non-graph front-ends first; it is not "A with a graph view bolted on," because the canonical serialization is the graph from step one.* If the user prefers to *actually* make the statement-tree canonical and treat the graph as a pure view, that is the legitimate **Option A** — and they should choose it knowingly (see §10 Q1); it is not what "C" means.

**Why C is the Goldilocks point, tied to the two non-negotiables and to D1:**

1. **Static validatability (NFR-6) — C reuses D1's exact validator machinery.** D1 already commits to a closed sealed-node hierarchy, a proven-DAG linter, hard caps at load and runtime, and intrinsic per-node Sim/Presentation domains, all in `src/Effects/`. C's validator is *that same graph linter + three rules*. A and B each build a *separate* validator (a tree-walker; a bytecode verifier). C is the only option where D2's static-validation story is a **delta on work D1 is doing anyway** — smallest net validator surface, most likely correct because it shares battle-tested code.

2. **Lockstep determinism (NFR-4) — C shares D1's executor paradigm.** D1 executes via an explicit work-stack, allocated-once, Fixed-only, ascending-id, seeded-`SimRng`, checksummed. C's runtime is the **same work-stack generalized** to D2's logic nodes — one execution model to make deterministic, not two.

3. **The D1 decision points at C.** D1's hand-off: "the DSL emits these same `EffectNode`s as its action layer." The cleanest realization is an IR where the action region **literally is a D1 effect subgraph embedded in the larger logic graph**. A and B *reference* effect graphs from a foreign structure; C *contains* them.

4. **C is the only option where the MVP-mandated T3 is native.** In A and B, T3 is an adapter/decompiler with round-trip-fidelity risk; in C, T3 is the IR rendered directly. Given T3 must ship, C removes the single largest piece of bespoke authoring tooling.

**The honest cost, and the guardrails the critics demanded:**
- **`GraphEdit` "Experimental" is the dominant risk.** Mitigation is **mandatory and load-bearing**: the IR is pure editor-agnostic data with persistent node ids — *no `GraphEdit`/Godot types in the IR* — and `GraphEdit` is a replaceable view. Because the IR is editor-agnostic *and graph-canonical from D1s*, the capability slice ships through T2/T4 before the editor exists, and if `GraphEdit` proves inadequate the IR and three tiers are unaffected.
- **C's T2 needs a defined fallback** (read-only "edit in graph view" placeholder) for graph-only constructs. Acceptable — T2 is "already built, basic, verify-to-ship" (FR-23). But ⚑ **the four-tier *interoperate* promise must be downgraded to the truth (Critique-2 §8):** full bidirectional editing is guaranteed **only in the IR-native tier**; the others provide best-effort projection + a non-destructive fallback. This is inherent to *any* single-IR choice (A has the mirror problem) — it is honesty, not a defect.
- ⚑ **The caps are the real risk, not a dial (Critique-2 §5, correct).** "~90% bounded" is the load-bearing empirical claim and it is currently *asserted*. If the corpus shows common maps need "for every unit on the map, do X," then ForEach-over-finite-only is a *representation* error, not a tuning miss. **Corpus validation is therefore a gate on D2 itself (§9), and `ForEachBatched` is the sanctioned >cap answer shipped at MVP** (C2b) — not "shard it yourself."
- **Why not A outright** (lowest risk): A's tree-not-graph IR makes the mandatory T3 a *permanent* adapter and creates a structural seam to D1's effect graphs — paying forever to save once. C captures the de-risking *path* (non-graph tiers ship first) while landing the superior *representation*.
- **Why not B** (highest ceiling): exceeds the WC3-parity bar (decision #11), maximum infrastructure for a solo dev on the critical path, to buy headroom the PRD deliberately doesn't want. Right answer to a more ambitious question than the one asked.

**One-line Goldilocks statement:** C lands the DSL in the **per-tick-bounded class** (Layer 0 grammar: no while/recursion, only ForEach-over-finite; Layer 1 acyclic events; Layer 2 cap-product + transitive-cascade cost rejection; Layer 3 checksummed fuel) — broad enough to build TD/MOBA/RPG/survival/autochess (all six genres need only bounded patterns, *given* `ForEachBatched` for large sets and rich event payloads for kill-credit) and narrow enough to prove affordable before any client ticks — realized as one graph paradigm shared with D1 so the validator and executor are *extensions, not duplications*.

---

## 6. SETTLED SUB-DECISIONS (recommended)

1. **Loop-bounding = layered hybrid (§3.1).** No `while`/recursion/`goto` — absent from grammar. Only `ForEach` over a snapshotted finite collection. Plus acyclic event DAG (Layer 1), **cap-product + transitive-cascade static cost rejection** (Layer 2) ⚑, and a **checksummed per-tick fuel** halting at a whole-trigger boundary (Layer 3). **"Reject at load, assert at runtime" — never clamp** ⚑. The guarantee is **per-tick bounded cost, not whole-program termination** ⚑. Constants named/reviewable/corpus-validated, incl. `MaxEventFanOut`, `MaxCascadeOps`, `MaxNextTickEventQueue`.

2. **Variables = closed types × scopes, dense-index-keyed, checksummed (§3.2).** `Int, Fixed, Bool, EntityRef, FactionRef, Point, TimerRef, Array<scalar>`. **No float type.** Scopes Global / Per-player / Trigger-local at MVP; per-instance + **`Record`** stretch-leaning ⚑. Hoisted to a **top-level sim store** ⚑; all live values fold into `SimChecksum` in declaration-index order. Assignment compiles to D1's `SetVariable` leaf with an expression RHS.

3. **Custom UI = MVP, phased: read-path first, write-path second (§3.5).** Read path (`CustomUiBridge` + versioned `DslVarReadback`) is MVP-early, zero command-rail change, cannot desync — ships scoreboards/timers. Write path (`DslEventCommand`) is MVP but a **second milestone touching the wire format AND the replay format** ⚑. Runtime-created widgets stretch. Widget set closed, **+ `ItemList` data-bound repeater** ⚑.

4. **T3 GraphEdit = native editor over an editor-agnostic, graph-canonical-from-D1s IR.** Pure data, persistent node ids, no Godot types in the IR; `GraphEdit` a replaceable view. Two edge types (exec, data); validator enforces **data-edge purity**. ⚑ **Graph serialization is canonical from the first step**, not retrofitted (revises the draft).

5. **T4 NL/LLM + validator (§3.3).** ⚑ **The "same validator" claim is qualified (Critique-3 §9, correct — the draft never read `LLMService.cs`; I have now).** The as-built `Validate` is a 5-pass (triggers) / 7-pass (scenario) *value-range* checker over the **flat** `TriggerDefinition`/`ScenarioData` shape, invoked **only inside the generation Task** (`LLMService.cs:134, 468`), **never at `LoadScenario`**. The new IR needs a **type-checker + graph-linter + cost-bounder** — a *different* validator that happens to share an entry point, **not an extension of the 5 passes.** The strategic equalizer holds (every tier emits the same IR through the same *new* validator, so AI is no more dangerous than a human and AI authoring is safe-by-construction — a claim an escape-hatch system could never make), but **the validator must be built, and the client-side advisory validator must be promoted to an authoritative server-side load-time gate** — net-new, because `LoadScenario` (`ScenarioDirector.cs:70`) does *zero* validation today and there is no server-side load path. FR-30 review-before-apply stays load-bearing. ⚑ Also fold in the **50-vs-64 cap discrepancy** (verified in *three* places: `ScenarioDirector.cs:312`, `LLMService.cs:73/309`, and the LLM prompt at `:362`): D1's `Spawn≤64` is authoritative; the stale 50 in the director's `Math.Min` and the LLM validator must be reconciled to one named constant during the D0 audit.

6. **DSL IR ↔ D1 = superset-containment.** The trigger graph contains D1 effect subgraphs; the action region *is* a D1 graph executed by D1's executor unchanged. One graph paradigm, one executor family, one validator. DTOs beside `EffectDef` in `src/Core/Definitions/`; runtime nodes beside D1's in `src/Effects/` (`ProjectChimera.Effects.Dsl` or sibling `ProjectChimera.Dsl`).

7. **Built-in events = bus, not polling; custom-event dispatch is a loop rewrite with pinned semantics (§3.4).** ⚑ Threshold events support **both level (`WhileTrue`) and edge (`OnCross`)** forms — *not* "observably identical." Same-tick dispatch is a **work-list drain** replacing the single priority sweep; **run-once fires at most once per match even if re-raised; cooldown suppresses same-tick re-entry**; per-tick allocation in the eval/event path is retired (allocate-at-load). Rich typed payloads incl. **killer/last-hit attribution** required (combat-layer prerequisite, §8).

8. **Four-tier promise downgraded to the truth.** ⚑ One shared IR; **full bidirectional editing guaranteed only in the IR-native tier**; other tiers best-effort projection + non-destructive fallback. A settled sub-decision, not a footnote.

9. **Trigger evaluation has a total deterministic order.** ⚑ `(Priority desc, then declaration-index asc)` via explicit comparator (replaces unstable `Array.Sort`); dense-index var/timer stores; simultaneous timer expiries in timer-declaration-index order. Prerequisite, gated by negative tests.

---

## 7. MIGRATION SEQUENCE (strangler — golden-checksum-gated, always-shippable)

Preserves the `On*` seam and the "evaluates last on settled state" contract; reuses D1's golden-checksum harness. **D2 begins only after D1 steps 1–2 and 8 land** (harness + `SimRng`; `TriggerAction[]`→`EffectDef[]`).

⚑ **Invariant correction (Critique-3 §2, correct).** The draft's blanket "every step gated on byte-identical `SimChecksum`" is **self-contradictory** with D1s (whose *purpose* is to change what `Compute` returns). **Split the invariant:** steps assert **identical observable outcomes**; checksum-baseline **re-pin** is a *named, expected event* at D1s (and again at D5s when fuel enters the hash). A **checksum-algorithm-version field is a D1s deliverable**, not Open-Question-#4 — without it a v0 replay spuriously "desyncs" under a v1 algorithm (`WriteChecksum` is bare `type+tick+checksum`, `ReplayRecorder.VERSION=1` gates only the command file).

> **Hard dependency on D1:** steps 1 (harness), 2 (`SimRng` + checksum/replay inclusion), 8 (`TriggerAction[]`→`EffectDef[]`, delete the switch, preserve `On*`) are prerequisites.

**D0 — Land on D1's seam + audit `ExecuteActions`.** Confirm D1 step 8: `Actions` is `EffectDef[]`; `On*` intact; golden checksums green. ⚑ **Explicit audit (Critique-3 §3, correct):** enumerate every `ExecuteActions` case, classify Sim vs Presentation, reconcile the as-built runtime clamps (`Math.Min(…,50)` vs D1 `≤64`) and runtime float→Fixed (`add_resources`, `create_timer`) against D1's load-time discipline. *Baseline tag; no behavior change.*

**D1s — Typed `DslVarTable` (hoisted to a top-level store) + checksum inclusion.** Replace the two `Dictionary`s with the dense-index-keyed, scoped, SoA `DslVarTable` **owned outside the director**; thread it into `EnableChecksums` and **change the `SimChecksum.Compute` signature** (+ every call site: `SimulationLoop.cs:98/135`, `MainScene.cs:268`) ⚑. Migrate existing `set_variable`/`variable_comparison`/`create_timer`/`timer_expires` with identical observable behavior. **Establish the graph-canonical serialization** (node ids + typed edges) in `ScenarioData` even though only T2/T4 author it ⚑. *Gate: identical outcomes; checksum re-pinned (named event); algorithm-version field added.*

**D2s — Expression evaluator (CEL-shaped, Fixed-only) + total trigger order.** Add the pure typed expression layer; rewrite `Conditions` → boolean tree (OR/NOT/grouping); `set_variable` RHS → expression; **delete float-epsilon `Compare` + `float.TryParse`**; retype `OnSpawnUnit`/`TriggerDefinition` floats → Fixed ⚑; **install the total `(Priority, decl-index)` order** ⚑. Begin Layer-2 cost estimation. *Gate: literal-only scenarios unchanged; equal-priority + same-tick-timer negative tests give identical cross-runtime checksums.*

**D3s — Event-driven bus + typed payloads (retire polling + per-tick GC).** Sim systems emit typed events into a closed per-tick bus; thresholds get **both level and edge** forms ⚑; secure **killer/last-hit payload** ⚑; **zero per-tick allocation** ⚑. *Gate: built-in firing matches under the chosen form (level maps preserved); payloads typed; no heap alloc in the path.*

**D4s — Custom events + acyclic dispatch + loop rewrite.** Closed registry, `RaiseEvent`, static subscription graph, **load-time DAG proof + transitive cascade-cost bound** (`MaxCascadeOps`, `MaxEventFanOut`) ⚑, `MaxEventCascadeDepth`, same-tick **work-list drain** with pinned run-once/cooldown semantics ⚑, `RaiseEventNextTick` with `MaxNextTickEventQueue` folded into the checksum ⚑. *Gate: event cycle rejected at load; fan-out cascade rejected at load (not at runtime fuel); next-tick oscillator bounded + deterministic.*

**D5s — Bounded `ForEach`/`ForEachBatched` + `Branch` + Layer-3 fuel.** The only loop forms (ForEach over snapshotted finite collection, ascending order; **`ForEachBatched` for >cap sets** ⚑) + in-action `Branch`. Wire per-tick fuel into `SimChecksum` (re-pin); finalize Layer-2. **Makes TD/autochess authorable.** *Gate: cap-product over-cost rejected at load; group-exceeds-cap is a load error or loud `ForEachUpTo`, never silent truncation ⚑; fuel exhaustion on an adversarial fixture halts at a whole-trigger boundary identically on two headless clients (no torn state); checksum re-pinned (named event).*

**D6s — Promote validator to authoritative server-side load gate.** Make the type-checker + graph-linter + cap/cost validator the **mandatory pre-tick gate at scenario-load** — closing the hole where hand-edited JSON enters the sim unchecked (`LoadScenario` does zero validation today). ⚑ This is a **new validator and a new execution context**, not an extension of the 5-pass value checker. Extend the T4 path to emit + round-trip the new IR through it. Reconcile the 50/64 constant ⚑. *Gate: a malformed hand-crafted scenario is rejected pre-tick; `scenarioHash` covers the larger serialized form.*

**D7s — T3 GraphEdit view (additive only).** ⚑ Build the `GraphEdit` editor as a replaceable view over the **already-graph-canonical** IR. *No content migration* (the IR was a graph since D1s). T2 sentence-editor renders the same IR with the graph-only fallback. *Gate: round-trip T2→T3→T2 preserves the IR by persistent-id equality; the IR has no `GraphEdit` types.*

**D8s — Custom-UI read path (`CustomUiBridge` + `DslVarReadback`).** Versioned snapshot + closed widget set (incl. `ItemList`) + load-and-assert caps + UI in `ScenarioData` (covered by `scenarioHash`). Pure presentation; no rail change. **Ships scoreboards/wave-counters/timers.** *Gate: UI reads checksummed state only; divergent UI fails `scenarioHash`.*

**D9s — Custom-UI write path (`DslEventCommand`) — network AND replay AND four apply-sites.** ⚑ Extend `TickCommandPacket` (parallel capped event list); add `EnqueueDslEvent`; **bump `ReplayRecorder.VERSION → 2` with a DSL-event record kind; add a `ReplayPlayer` parse+apply branch**; thread DSL-event application through **all four** command-application sites (live `:315`, spectator `:261`, `ReplayPlayer.ApplyOrders`, recorder `:318`) — recommend a pre-step unifying the three `ApplyOrders` copies. Net-new per-event authorization in `ApplyDslEvents`; pinned tick-phase order (apply → systems → director drain); local-only buttons on the closed whitelist with disjoint-namespace validation; per-tick cap + authored cooldown (reuse `_triggerCooldown`). *Gate: a button press defers by `_currentDelay`, applies at the same exec tick on two headless clients, **appears identically in a v2 replay**, and a local-only toggle provably cannot affect `SimChecksum`.*

*(Stretch, post-MVP: per-unit variable scope; `Record`; runtime-created widgets; richer built-ins — each behind the same validator + caps.)*

---

## 8. PREREQUISITES SURFACED + HAND-OFFS TO D3

### 8.1 Prerequisites D2 needs that don't exist yet
- **D1's `src/Effects/` + sealed `EffectNode` + graph executor + graph validator** — D2 contains/extends these. Hard dependency on D1 steps 5–8. *(Confirmed absent.)*
- **`SimRng`** — required before any random DSL construct; validator rejects randomness until it ships + is checksummed. ⚑ Its draw-order determinism *depends on* the total-trigger-order fix. *(Confirmed absent.)*
- **`SimChecksum` signature change + call-site threading** — must hash `DslVarTable`, event queue, next-tick queue, fuel; touches `SimulationLoop` + `MainScene` ⚑. *(Verified three-arg at `:26`; omits vars/timers — the confirmed desync hole.)*
- **Total trigger-evaluation order + dense-index var/timer stores** — fixes the unstable `Array.Sort` (`:192`) and `Dictionary` enumeration (`:149`) ⚑. *(Both verified.)*
- ⚑ **Combat-layer killer/last-hit attribution on `unit_dies`** — without it, MOBA gold / kill-credit quests are unbuildable. A D1/combat prerequisite, not a DSL feature. *(Verified absent: `:126` carries victim slot only.)*
- **Expression evaluator** (CEL-shaped, Fixed-only, two-phase) — net-new.
- **Custom-event registry + same-tick work-list + acyclic-dispatch + transitive-cost verifier** — net-new.
- **A *new* static validator** (type-check + graph-lint + cap/cost) **promoted to a server-side load-time gate** — net-new; the as-built 5/7-pass `Validate` is a value-range checker over the flat shape, invoked only in the generation Task, never at load ⚑.
- **`TickCommandPacket` extension + `EnqueueDslEvent`/`ApplyDslEvents`** AND **`ReplayRecorder`/`ReplayPlayer` format-v2 + four apply-sites** ⚑. *(Verified: recorder hardwired to `UnitOrder` `:74-81`; `ReplayPlayer` bypasses `LockstepManager` `:13`.)*
- **`CustomUiBridge` + `DslVarReadback` + closed widget set (incl. `ItemList`)** — net-new, modeled on `FogOfWarBridge`.
- **Caps corpus-validated as a D2 gate** (loop/nesting/cascade/fan-out/fuel/array) — *representation* gate, not a pre-1.0 checkbox ⚑.
- **Headless DSL fixtures** incl. negatives: event cycle, fan-out cascade over-cost, fuel-exhaustion-no-torn-state, equal-priority/same-tick-timer ordering, group-exceeds-cap, level-vs-edge threshold.

### 8.2 Hand-offs to D3 (data-driven schema & loader)
D3 owns `System.Text.Json` (de)serialization; D2 constrains it. D3 must serialize deterministically into `ScenarioData` (so `scenarioHash` stays meaningful):
- **The graph IR** — trigger/logic nodes (`ForEach`/`ForEachBatched`/`Branch`/`RaiseEvent`/`SetVariable`/`StartTimer`/Get-Set/expression), **two edge types**, **persistent node ids**, embedded **D1 `EffectDef` action subgraphs**. Graph-canonical from D1s ⚑.
- **Variable schema** — name/type/scope/initial-value (closed types incl. `Array<T>` capacity).
- **Custom-event registry** — names + typed param shapes + **per-event allowed-raiser set** ⚑.
- **UI-definition schema** — closed widget tree (incl. `ItemList`) with `BindVar`/`BindEvent`/`Format`/layout + named caps.
- **Authoring-affordance annotations** (never destroyed): T3 node positions, T1 preset-origin, T4 prompt provenance.
- **Replay format v2** record schema for `DslEventCommand` ⚑ (so D3 and the replay layer agree on the on-disk shape).
- **Constraint on shape:** closed typed nodes only, named references, **Fixed-at-load** (convert once, reject NaN/Inf). No source-gen `JsonSerializerContext` today (runtime reflection, case-insensitive) — D3 decides whether to add one; D2 only requires deterministic serialization.

---

## 9. RESIDUAL RISKS / WATCH-ITEMS

Genuine risks that survive the design (critics' findings that remain live, not papered over):

1. **The caps ARE the architecture (highest residual).** ⚑ "~90% bounded" is asserted, not proven (Critique-2 §5). If the WC3/TD/MOBA corpus shows common content needs "do X to every unit on the map" as a *single-tick* operation, then ForEach-over-finite-only is a representation error and `ForEachBatched`+caps won't paper it. **Mitigation:** corpus validation is a **gate on D2 before lock** (§7 prereq), and `ForEachBatched` ships at MVP. **Watch:** if even one target genre needs unbounded *within-tick* iteration, reopen the envelope, not the constants.

2. **Cap-product cost narrows the DSL more than "nest freely" suggests.** ⚑ (Critique-1 §1.) `MaxArrayCapacity=256` × nesting rejects deep loops at load. Per-loop declared bounds soften this, but authors *will* hit "rejected at load for cost." **Watch:** corpus-tune `MaxDslOpsPerTrigger` vs. real nesting; document the ceiling as an acceptance criterion (e.g., "boss wave of 200 must be tick-dripped, not single-action").

3. **`GraphEdit` "Experimental."** Mitigated by editor-agnostic, graph-canonical-from-D1s IR + replaceable view + non-graph tiers shipping first. **Watch:** if `GraphEdit` is unusable, T3 falls back to a custom view; IR and T1/T2/T4 are unaffected — but T3 is MVP, so budget a view-swap.

4. **Write-path is a bigger build than it looks.** ⚑ Network + replay-v2 + four apply-sites + new authorization (Critique-3 §5/§6). De-risked by read-path-first (most UI value, zero rail change), but the write-path milestone is real engineering, not "free."

5. **Cross-tick oscillator liveness.** ⚑ (Critique-1 §4.) `RaiseEventNextTick`/timers are deterministic but can run an unintended every-tick loop for a match. Not a desync. **Mitigation:** `MaxNextTickEventQueue` bounds the queue; the *guarantee* is honestly per-tick, not whole-program. **Watch:** a creator-facing "this trigger has fired every tick for N seconds" diagnostic would help, post-MVP.

6. **Level→edge threshold migration breaks sustained-state maps.** ⚑ (Critique-2 §10.) Resolved by supporting both forms, but existing scenarios relying on level semantics need migration; do not claim D3s is checksum-identical.

7. **Forfeited capability class: runtime strings.** ⚑ (Critique-2 §3.) No player-named heroes / typed passwords / runtime renaming — permanently. Accepted for determinism; **stated, not hidden.**

---

## 10. OPEN QUESTIONS FOR THE USER

The genuine forks. Headline first; each has a recommended default.

**Q1 — Canonical IR shape (the headline).**
**Recommended default: Option C (typed event/dataflow graph), graph-canonical from the first migration step, `GraphEdit` a replaceable view; non-graph tiers (T2/T4) ship the capability first.**
The fork: **C** (native T3, highest D1-unification, medium migration, `GraphEdit` risk, graph-canonical from D1s — *no* late content migration) vs. **A** (statement-tree canonical, lowest risk/migration, but T3 a permanent adapter and a structural seam to D1; legitimate if you'd rather make the tree canonical and treat the graph as a pure view) vs. **B** (bytecode VM, highest ceiling but beyond the WC3-parity bar and highest build/risk). ⚑ *Note the revision: "C via an A-shaped slice with a D7s re-expression" is **off the table** as incoherent — choose either C-graph-from-D1s or honest-A-with-graph-view. Are you comfortable taking the `GraphEdit` risk for native T3 + one paradigm with D1, given the mitigation (editor-agnostic, graph-canonical IR + view abstraction + non-graph tiers first)?*

**Q2 — `Record`/structs at MVP, or stretch?**
**Recommended default: stretch (ship arrays-only at MVP).** ⚑ *Revised from the draft's MVP-recommend.* TD wave tables / RPG quest defs / autochess pools are *naturally* records, but they are **buildable with parallel arrays** (less ergonomic), and records add type-system + validator + serialization surface to an already-large MVP. The draft's "parallel-arrays-is-the-WC3-papercut" argument is real but secondary to shipping. *Ship records in 1.0 for ergonomics, or defer to first stretch and accept parallel-array authoring at MVP?*

**Q3 — Write-path scope: confirm network + replay-v2 + wire encoding.**
**Recommended default: write path IN 1.0; read-path-early; Option B wire encoding (parallel capped event list); event payload `eventId(2) + arg0(1) + arg1(1)`.** ⚑ *With the corrected cost:* the write path is **not** "free replay" — it requires a `ReplayRecorder.VERSION→2` bump and application through four sites. *Confirm: (a) write path is in 1.0 (not deferred); (b) you accept the replay-format-v2 work as part of it; (c) is `eventId + two small enum args` sufficient, or must a custom button carry a `Fixed`/`Point` argument (widening the event struct)?*

**Q4 — Termination/cost philosophy: confirm the corrected guarantee and the desync-config gate.**
**Recommended default: accept (a) the guarantee is per-tick-bounded-cost, not whole-program termination; (b) folding the DSL fuel + event/next-tick queues into `SimChecksum`; (c) a `rulesetHash` (or caps folded into `scenarioHash`) compared at the lobby handshake alongside the existing `scenarioHash` gate; (d) a checksum-algorithm-version field shipped in D1s; (e) caps corpus-validated as a gate on D2.** ⚑ *The corrected framing matters: caps are now desync-critical config (two clients on different `MaxDslOpsPerTick` desync), so they need a lobby-time hash check like `scenarioHash`, and they are a representation gate, not a tuning dial. Confirm you accept fuel/queues as new contributors to the desync signal and the caps-as-gate posture?*

---

**Bottom line for the facilitator.** D2 is the logic layer wrapping D1's effect vocabulary, achievable because the project *forbids the undecidable region* rather than proving it safe: Starlark-style bounded grammar (no while/recursion, only ForEach-over-finite + a sanctioned `ForEachBatched` for large sets), CEL-style typed pure Fixed expressions, eBPF-style acyclic-dispatch with **transitive cascade-cost rejection**, and a checksummed fuel seatbelt that valid content never trips. The recommendation is **a single typed event/dataflow graph IR (Option C) that *contains* D1's effect subgraphs, graph-canonical from the first migration step** — extending D1's validator and executor rather than duplicating them, making T3 native, de-risked by keeping `GraphEdit` a replaceable view and shipping the capability through non-graph tiers first. The adversarial reviews materially improved it: the honest guarantee is **per-tick bounded cost, not whole-program termination**; two **live ordering nondeterminisms** in the as-built code (unstable `Array.Sort`, `Dictionary` enumeration) are now prerequisite fixes; the **"free replay" claim was false** and the write-path is correctly scoped to network + replay-v2 + four apply-sites + net-new authorization; the **"four tiers interoperate" promise is downgraded** to one-IR-with-IR-native-bidirectional-editing; **killer attribution and a data-bound UI repeater** are surfaced as the concrete gaps that otherwise make MOBA and data-driven UIs unbuildable; and the **caps are reframed as a representation gate**, not a tuning dial. The user's calls: the IR shape (C-graph-from-D1s vs honest-A vs B), records-at-MVP, the write-path scope incl. replay-v2, and ratifying the corrected per-tick-cost + desync-config philosophy.

Key files grounding this briefing (all absolute, all verified this session):
- `D:\Projects\Project_Chimera\godot\src\Core\ScenarioDirector.cs` — flat polled ECA; `Dictionary<string,int>` vars (`:33-34`); unstable `Array.Sort` tie-break (`:192`); `Dictionary` timer enumeration (`:149`); float-epsilon `Compare` (`:364`); `float.TryParse` (`:252`); `Math.Min(a.Count,50)` (`:312`); runtime `Fixed.FromFloat`/`(int)(float*ticks)` (`:331,:336`); `OnSpawnUnit` float signature (`:47`); `LoadScenario` does zero validation (`:70`).
- `D:\Projects\Project_Chimera\godot\src\Core\SimChecksum.cs` — three-arg `Compute` omitting vars/timers (`:26-57`) — the confirmed desync hole.
- `D:\Projects\Project_Chimera\godot\src\Multiplayer\LockstepManager.cs` — `EnqueueOrder`/`Flush`/`ApplyOrders` write-path rail (`:215,:240,:594`); per-faction unit-ownership check (`:601`); four-ish apply contexts (live `:315`, spectator `:261`); `Recorder.RecordTick` calls (`:318-324`).
- `D:\Projects\Project_Chimera\godot\src\Multiplayer\ReplayRecorder.cs` — hardwired to `UnitOrder` (`:74-81`), `VERSION=1` (`:25`).
- `D:\Projects\Project_Chimera\godot\src\Multiplayer\ReplayPlayer.cs` — "drop-in replacement for LockstepManager.Flush" bypassing lockstep (`:13`); duplicated `ApplyOrders` (`:142`); `UnitOrder`-only parse (`:98-102`).
- `D:\Projects\Project_Chimera\godot\src\AI\LLMService.cs` — 5-pass trigger / 7-pass scenario *value-range* validator over the flat shape, invoked only in the generation Task (`:134,:468`), never at load; auto-clamps spawn to `MAX_SPAWN_COUNT=50` (`:73,:309-310`).
- `D:\Projects\Project_Chimera\godot\src\Core\MainScene.cs` — `EnableChecksums(_buildings,_resources)` (`:268`); `OnSpawnUnit` wiring (`:1879`); replay Flush-vs-lockstep branch (`~:400`).
- `D:\Projects\Project_Chimera\_bmad-output\game-architecture.md` — D1 record (`:221-423`), hand-off to D2 (`:416-418`).
- Confirmed-absent (D1 deliverables D2 depends on): `godot/src/Effects/**`, `godot/src/**/SimRng*.cs`.