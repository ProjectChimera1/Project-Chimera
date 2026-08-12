# Trigger DSL Reference (creator-facing)

_Status: canonical for the trigger/event runtime as built. Where this file and the code disagree, the code
(`godot/src/Core/ScenarioDirector.cs`, `godot/src/Dsl/**`) is the truth — please file it as a defect._

This reference documents the **rules a scenario author can rely on**. It is not an exhaustive node catalogue
(the trigger editor's palette is that); it covers the behaviours that are easy to guess wrong, starting with the
one that has bitten authors hardest: **which events survive a busy tick, and which do not.**

---

## 1. Events: edge vs polled

Every event your triggers subscribe to is one of two kinds, and the difference decides everything below.

**Edge events** happen at an instant. They occur once, and if nobody consumes the occurrence on the tick it
happened, there is nothing left to observe next tick:

`match_start` · `unit_dies` · `building_completed` · `timer_expires` · `unit_damaged` · `unit_trained` ·
`ability_cast` · `hero_level` · `player_chat`

**Polled events** are a *condition being true*, re-tested every tick. If one is missed on a tick, it simply
re-emits on the next one — nothing has to be remembered:

`resource_threshold` · `unit_count_threshold`

**Custom events** you raise yourself (`raise_event`) are neither: they ride the next-tick work list and are
delivered to their single subscriber directly.

---

## 2. Event persistence scope (the re-queue rail)

The engine runs a fixed op budget per tick. When a tick is heavy — a long `for_each`, many triggers firing —
the sweep **halts at a whole-trigger boundary** and the triggers it had not reached yet simply run next tick.
For polled events that is harmless. For edge events it used to mean the occurrence was gone forever.

So edge occurrences a trigger *would* have consumed are **persisted** onto a re-queue rail and redelivered on a
later tick, addressed to that specific trigger.

### What the rail persists

| Loss | Persisted? | What you observe |
|---|---|---|
| **Fuel break** — the per-tick op budget ran out before the sweep reached your trigger | **Yes** | Your trigger receives the occurrence on a later tick, once the budget recovers |
| **Batched suppression** — your trigger is mid-drip on a batched continuation row, so it cannot re-fire yet | **Yes** | The occurrence waits out the drip and dispatches when the row completes |

Redelivery is **addressed to one trigger**. A trigger that already consumed the occurrence this tick can never
be re-fired by the redelivery of the same occurrence.

### What the rail deliberately does NOT persist (the non-goals)

These are **authored semantics** — the trigger was asked not to run — so the occurrence is dropped exactly as it
was before the rail existed, giving you parity with how polled events behave:

- **The trigger is disabled.** An occurrence arriving while a trigger is off is gone. Turning the trigger back
  on later does not replay it.
- **The trigger is run-once and already spent.** No occurrence is ever banked against a spent one-shot.
- **The trigger is cooling down.** This includes a cooldown armed by an *earlier occurrence on the same tick*:
  if two units die on one tick and the first death fires a trigger with a cooldown, the second death is dropped,
  not queued. **A cooldown suppresses; it does not defer.**
- **A pending redelivery whose target became ineligible.** If a persisted occurrence is waiting for your trigger
  and that trigger is disabled, spent or cooling by the time the redelivery arrives, it is dropped on arrival
  under the same rule.
- **Polled events**, always — there is nothing to persist, they re-emit by construction.
- **Custom events**, always — they ride the work list instead.

If you want "every death is counted, no matter what", do not put a cooldown or a run-once on the counting
trigger; count in a variable and gate the *reaction* instead.

### Conditions are re-checked, never frozen

A persisted occurrence stores the *event*, not a verdict. Your trigger's conditions and condition-expressions
are evaluated at **redelivery** against the world as it is then. A condition that was true on the tick the unit
died and false two ticks later will **not** fire. This is the "re-evaluate next tick" contract, applied to edge
events.

### Capacity

The rail holds up to **64** pending occurrences. If it is full, the newest is dropped — deterministically, so
every player in a multiplayer match drops the same one and the match stays in lockstep. Reaching that ceiling
means a scenario is generating edge events far faster than its triggers consume them; treat it as a design
signal, not a limit to engineer around.

---

## 3. Ordering guarantees

- Triggers evaluate in **priority-descending, then declaration-ascending** order — the same order on every
  machine and in every replay.
- Occurrences of the same event dispatch in **emission order** (for deaths, ascending entity id).
- Persisted occurrences are enqueued **occurrence-major, then ascending trigger**, so redelivery order is
  identical on every peer.

Nothing in the trigger runtime depends on wall-clock time, frame rate, or floating-point arithmetic.

---

## 4. Per-tick budget

A single tick's trigger work is bounded (`DslBounds.MaxDslOpsPerTick`). When the budget is exhausted:

1. The trigger **in flight finishes** — it is never torn in half.
2. Every remaining trigger skips the tick and re-evaluates on the next one.
3. Their unconsumed **edge** occurrences persist onto the rail as described above.

Authors do not need to budget explicitly; the seatbelt exists so a runaway scenario degrades into slower trigger
throughput rather than a stalled or divergent match.
