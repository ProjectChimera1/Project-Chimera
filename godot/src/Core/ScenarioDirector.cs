#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using ProjectChimera.Economy;

namespace ProjectChimera.Core
{
    /// <summary>
    /// Evaluates scenario triggers each simulation tick.
    /// Pure C# — no Godot dependency. Runs last in the simulation loop so it
    /// sees fully-updated world state (post-combat, post-construction).
    ///
    /// Delegates fire for effects that require the presentation layer
    /// (spawn, message, sound, victory). All pure sim mutations (timers,
    /// variables, add_resources) happen directly inside Tick().
    /// </summary>
    public class ScenarioDirector : ISimSystem
    {
        // ── Dependencies ──────────────────────────────────────────────────────

        private readonly BuildingStore   _buildings;
        private readonly ResourceStore   _resources;

        // ── Trigger runtime state ─────────────────────────────────────────────

        private TriggerDefinition[]  _triggers    = Array.Empty<TriggerDefinition>();
        private bool[]               _triggerFired    = Array.Empty<bool>();    // run_once guard
        private int[]                _triggerCooldown = Array.Empty<int>();    // remaining ticks

        // The precomputed trigger evaluation order (Priority desc, then ascending declaration index). Computed
        // ONCE per LoadScenario via a stable LINQ OrderByDescending/ThenBy (analyzer-clean — no Array.Sort, so no
        // CHM0003), because the order is stable for the whole match: only Enabled/fired/cooldown change per tick.
        private int[]                _triggerOrder = Array.Empty<int>();

        // ── Named timers and integer variables — dense creation-index stores (Story 7.1) ──────
        // Parallel-array (SoA) stores keyed by CREATION index, replacing the former Dictionary<string,int> whose
        // enumeration order depended on insertion history (AR-16 forbids Dictionary enumeration in the tick). Name
        // lookup is a deterministic linear scan (small N); the tick iterates the value list by ASCENDING creation
        // index, so same-tick timer expiries emit in declaration order regardless of insertion history. NO
        // Dictionary/HashSet is used at all. Story 7.3 hoists these into the top-level DslVarTable folded into
        // SimChecksum — kept minimal and self-contained here so that hoist is clean.
        private readonly List<string> _timerNames     = new();
        private readonly List<int>    _timerRemaining = new(); // remaining ticks; 0 = inactive/expired slot

        private readonly List<string> _variableNames  = new();
        private readonly List<int>    _variableValues = new();

        // ── Named regions (Story 6.4) ─────────────────────────────────────────
        // The resolved (float→Fixed done once at ScenarioApplier) region rects the unit_in_region condition scans.
        // Supplied by the applier via SetRegionStore before LoadScenario, same way scenario context is supplied
        // today. Static authored data (never mutates mid-match), so it is NOT in SimChecksum. Defaults to Empty so
        // a director built without regions (every pre-6.4 path / test) evaluates unit_in_region as false cleanly.
        private RegionStore _regions = RegionStore.Empty;

        // ── Change-detection snapshots ────────────────────────────────────────

        private readonly EntityFlags[] _prevFlags          = new EntityFlags[EntityWorld.MAX_ENTITIES];
        private readonly bool[]        _prevBuildingAlive  = new bool[BuildingStore.MAX_BUILDINGS];
        private readonly bool[]        _prevBuildingDone   = new bool[BuildingStore.MAX_BUILDINGS];

        private bool _firstTick = true;

        // ── Presentation-layer delegates ──────────────────────────────────────

        /// <summary>Requests the presentation layer to spawn units. (unitId, factionSlot, x, z, count) — x/z are
        /// <see cref="Fixed"/> so the in-tick path stays Fixed-only; the binder routes them through the Fixed-native
        /// spawn primitive with no <c>Fixed.FromFloat</c>.</summary>
        public Action<string, int, Fixed, Fixed, int>? OnSpawnUnit;

        /// <summary>Requests a toast notification. (text, durationSeconds) — duration is <see cref="Fixed"/>; the
        /// binder converts it to float at the presentation boundary only.</summary>
        public Action<string, Fixed>? OnDisplayMessage;

        /// <summary>Requests a sound effect. (soundId)</summary>
        public Action<string>? OnPlaySound;

        /// <summary>Signals a match outcome. (winnerFactionSlot: 0=P1, 1=P2)</summary>
        public Action<int>? OnVictory;

        // ── Constructor ───────────────────────────────────────────────────────

        public ScenarioDirector(BuildingStore buildings, ResourceStore resources)
        {
            _buildings = buildings;
            _resources = resources;
        }

        /// <summary>
        /// Story 6.4: supply the resolved <see cref="RegionStore"/> the <c>unit_in_region</c> condition scans. The
        /// applier builds it (float→Fixed once, at the single conversion boundary) and hands it here BEFORE
        /// <see cref="LoadScenario"/>. Never mutated mid-match, so it is not part of the checksummed state.
        /// A null argument degrades to <see cref="RegionStore.Empty"/> (no regions ⇒ unit_in_region is false).
        /// </summary>
        public void SetRegionStore(RegionStore? store) => _regions = store ?? RegionStore.Empty;

        /// <summary>
        /// Load triggers from a freshly-applied scenario. Resets all runtime state.
        /// Call after ApplyScenario() so the initial alive-state snapshots are clean.
        /// </summary>
        public void LoadScenario(ScenarioData scenario)
        {
            // Story 7.2: route the sole trigger consumption through the graph-canonical IR as an IDENTITY lowering —
            // FromFlat migrates the flat TriggerDefinition[] into the graph, ToFlat lowers it straight back. This
            // proves the flat↔graph round-trip is lossless on live content while keeping the tick byte-identical
            // (no hash fold, no on-disk format change). Later stories walk the graph directly (7.3); for 7.2 it is a
            // waypoint. Every golden builds with empty triggers, so FromFlat([]).ToFlat() == [] — a no-op there.
            _triggers        = TriggerGraph.FromFlat(scenario.Triggers).ToFlat();
            _triggerFired    = new bool[_triggers.Length];
            _triggerCooldown = new int[_triggers.Length];

            // Precompute the total evaluation order ONCE: Priority desc, then ascending declaration index. LINQ
            // OrderByDescending/ThenBy is a STABLE sort with an explicit total tiebreak — deterministic across
            // runtimes AND analyzer-clean (it calls no Array.Sort/List.Sort, so it does not trip CHM0003). The
            // declaration index is the flat-array ordering surrogate (Story 7.2 supersedes it with a persistent id).
            _triggerOrder = Enumerable.Range(0, _triggers.Length)
                .OrderByDescending(i => _triggers[i].Priority)
                .ThenBy(i => i)
                .ToArray();

            _timerNames.Clear();
            _timerRemaining.Clear();
            _variableNames.Clear();
            _variableValues.Clear();
            _firstTick = true;

            // Snapshot initial state so the first diff doesn't generate spurious events.
            Array.Clear(_prevFlags, 0, _prevFlags.Length);
            Array.Clear(_prevBuildingAlive, 0, _prevBuildingAlive.Length);
            Array.Clear(_prevBuildingDone, 0, _prevBuildingDone.Length);

            for (int i = 0; i < BuildingStore.MAX_BUILDINGS; i++)
            {
                _prevBuildingAlive[i] = _buildings.Alive[i];
                _prevBuildingDone[i]  = _buildings.Alive[i]
                    && _buildings.ConstructionTimer[i] <= Fixed.Zero;
            }
        }

        // ── ISimSystem ────────────────────────────────────────────────────────

        public void Tick(EntityWorld world, Fixed dt)
        {
            if (_triggers.Length == 0) return;

            var events = CollectEvents(world);
            TickCooldowns();
            EvaluateTriggers(events, world);
            UpdateSnapshots(world);
        }

        // ── Event collection ──────────────────────────────────────────────────

        private List<FiredEvent> CollectEvents(EntityWorld world)
        {
            var events = new List<FiredEvent>(16);

            // match_start fires on the very first tick after LoadScenario().
            if (_firstTick)
            {
                events.Add(new FiredEvent("match_start", -1, 0, null));
                _firstTick = false;
            }

            // Entity deaths — compare current Alive flag against previous snapshot.
            int hwm = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
            {
                bool wasAlive = (_prevFlags[i] & EntityFlags.Alive) != 0;
                bool isAlive  = world.IsAlive(i);
                if (wasAlive && !isAlive)
                {
                    int slot = (int)world.FactionOf[i] - 1; // Player1=1 → slot 0
                    events.Add(new FiredEvent("unit_dies", slot, 0, null));
                }
            }

            // Building completions (was under construction → now done).
            for (int i = 0; i < _buildings.Count; i++)
            {
                bool wasAlive = _prevBuildingAlive[i];
                bool isAlive  = _buildings.Alive[i];
                bool wasDone  = _prevBuildingDone[i];
                bool isDone   = isAlive && _buildings.ConstructionTimer[i] <= Fixed.Zero;

                if (isAlive && !wasDone && isDone)
                {
                    int slot = (int)_buildings.FactionOf[i] - 1;
                    events.Add(new FiredEvent("building_completed", slot, 0,
                        _buildings.Type[i].ToString()));
                }
                _ = wasAlive; // snapshot updated in UpdateSnapshots
            }

            // Timers — decrement each ACTIVE timer and collect expiries in CREATION-INDEX (declaration) order.
            // Iterating the parallel value list by ascending index is the deterministic contract (AR-16): same-tick
            // expiries emit independent of insertion history, with NO Dictionary enumeration. An expired timer is
            // marked inactive (remaining = 0) in place — a later create_timer of the same name reactivates its slot,
            // preserving its creation index. Mutating values in place is safe (no add/remove during the loop).
            for (int i = 0; i < _timerRemaining.Count; i++)
            {
                if (_timerRemaining[i] <= 0) continue; // inactive/expired slot
                int remaining = _timerRemaining[i] - 1;
                if (remaining <= 0)
                {
                    _timerRemaining[i] = 0;
                    events.Add(new FiredEvent("timer_expires", -1, 0, _timerNames[i]));
                }
                else
                {
                    _timerRemaining[i] = remaining;
                }
            }

            // Threshold events — polled every tick so triggers can react to sustained states.
            // Carry the ore as its raw Fixed integer (a typed int payload) — so the match path compares Fixed-vs-Fixed
            // with NO string formatting/parsing and no float arithmetic in the tick (AR-16). slot < 2 stays as-is:
            // widening to all active factions is Story 9.2, not this story.
            for (int slot = 0; slot < 2; slot++)
            {
                var faction = (Faction)(slot + 1);
                int oreRaw  = _resources.Ore[(int)faction].Raw;
                int units   = CountAlive(world, faction);
                events.Add(new FiredEvent("resource_threshold",   slot, oreRaw, null));
                events.Add(new FiredEvent("unit_count_threshold", slot, units,  null));
            }

            return events;
        }

        // ── Cooldown bookkeeping ──────────────────────────────────────────────

        private void TickCooldowns()
        {
            for (int i = 0; i < _triggerCooldown.Length; i++)
                if (_triggerCooldown[i] > 0) _triggerCooldown[i]--;
        }

        // ── Trigger evaluation ────────────────────────────────────────────────

        private void EvaluateTriggers(List<FiredEvent> events, EntityWorld world)
        {
            // Iterate the PRECOMPUTED total order (Priority desc, then ascending declaration index) built once in
            // LoadScenario. The order is stable for the whole match (only Enabled/fired/cooldown change per tick),
            // so recomputing it every tick was wasted work — and the per-tick Array.Sort was an unstable introsort
            // that tripped CHM0003. ExecuteActions runs in this order, so equal-priority triggers writing shared
            // state resolve last-writer by ascending declaration index, deterministically across peers (AR-16).
            foreach (int idx in _triggerOrder)
            {
                var t = _triggers[idx];
                if (!t.Enabled || _triggerFired[idx] || _triggerCooldown[idx] > 0) continue;
                if (!AnyEventMatches(t.Events, events))                             continue;
                if (!AllConditionsMet(t.Conditions, world))                         continue;

                ExecuteActions(t.Actions);

                if (t.RunOnce) _triggerFired[idx] = true;

                // Fixed seconds → whole ticks via SecondsToTicks (64-bit intermediate, overflow-safe): AC2/AR-14.
                int coolTicks = SecondsToTicks(t.CooldownSeconds);
                if (coolTicks > 0) _triggerCooldown[idx] = coolTicks;
            }
        }

        /// <summary>
        /// Convert a <see cref="Fixed"/> duration in seconds to whole sim ticks WITHOUT overflowing the
        /// Fixed multiply. <c>seconds * Fixed.FromInt(TICKS_PER_SECOND)</c> overflows the <c>(int)</c> cast
        /// inside <see cref="Fixed"/>'s <c>operator*</c> once the product leaves 16.16 range (~1092 s at
        /// 30 ticks/s) and silently wraps negative — yet <c>FixedJsonConverter</c> still admits durations up
        /// to ~32767 s. Computing the product in a 64-bit intermediate and shifting down maps every
        /// converter-admitted duration to a correct non-negative tick count, and is byte-identical to the
        /// prior Fixed math for in-range values.
        /// </summary>
        private static int SecondsToTicks(Fixed seconds) =>
            (int)(((long)seconds.Raw * SimulationLoop.TICKS_PER_SECOND) >> Fixed.FRACTIONAL_BITS);

        // ── Snapshot update ───────────────────────────────────────────────────

        private void UpdateSnapshots(EntityWorld world)
        {
            int hwm = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
                _prevFlags[i] = world.Flags[i];

            for (int i = 0; i < _buildings.Count; i++)
            {
                _prevBuildingAlive[i] = _buildings.Alive[i];
                _prevBuildingDone[i]  = _buildings.Alive[i]
                    && _buildings.ConstructionTimer[i] <= Fixed.Zero;
            }
        }

        // ── Event matching ────────────────────────────────────────────────────

        private static bool AnyEventMatches(TriggerEvent[] evDefs, List<FiredEvent> fired)
        {
            foreach (var def in evDefs)
                foreach (var f in fired)
                    if (EventMatches(def, f)) return true;
            return false;
        }

        private static bool EventMatches(TriggerEvent def, in FiredEvent f)
        {
            if (def.Type != f.Type) return false;
            switch (def.Type)
            {
                case "match_start":
                    return true;
                case "unit_dies":
                    return f.Slot == def.Faction;
                case "building_completed":
                    if (f.Slot != def.Faction) return false;
                    return string.IsNullOrEmpty(def.BuildingType) || f.Data == def.BuildingType;
                case "timer_expires":
                    return string.IsNullOrEmpty(def.TimerName) || f.Data == def.TimerName;
                case "resource_threshold":
                    if (f.Slot != def.Faction) return false;
                    // f.Numeric is the ore's raw Fixed integer (a typed int payload). Compare Fixed-vs-Fixed; def.Amount
                    // is a Fixed quantized at the JSON boundary by FixedJsonConverter, so there is NO string round-trip
                    // and no in-tick float in the trigger tick path (AR-14/AR-16).
                    return Compare(Fixed.FromRaw(f.Numeric), def.Amount, def.Operator);
                case "unit_count_threshold":
                    if (f.Slot != def.Faction) return false;
                    return Compare(f.Numeric, def.Count, def.Operator);
                default:
                    return false;
            }
        }

        // ── Condition evaluation ──────────────────────────────────────────────

        private bool AllConditionsMet(TriggerCondition[] conds, EntityWorld world)
        {
            foreach (var c in conds)
                if (!EvalCondition(c, world)) return false;
            return true;
        }

        private bool EvalCondition(TriggerCondition c, EntityWorld world)
        {
            var faction = (Faction)(c.Faction + 1);
            switch (c.Type)
            {
                case "always":
                    return true;
                case "building_exists":
                {
                    if (string.IsNullOrEmpty(c.BuildingType)) return true;
                    if (!Enum.TryParse<BuildingType>(c.BuildingType, out var bt)) return false;
                    for (int i = 0; i < _buildings.Count; i++)
                        if (_buildings.Alive[i] && _buildings.FactionOf[i] == faction
                            && _buildings.Type[i] == bt
                            && _buildings.ConstructionTimer[i] <= Fixed.Zero)
                            return true;
                    return false;
                }
                case "resource_comparison":
                    // Fixed-vs-Fixed (no float). c.Amount is a Fixed quantized at the JSON boundary (Story 1.4) — no in-tick FromFloat.
                    return Compare(_resources.Ore[(int)faction], c.Amount, c.Operator);
                case "unit_count":
                    return Compare(CountAlive(world, faction), c.Count, c.Operator);
                case "variable_comparison":
                    if (string.IsNullOrEmpty(c.Variable)) return false;
                    return Compare(GetVariable(c.Variable), c.Value, c.Operator);
                case "unit_in_region":
                    // Story 6.4: true when ANY live unit of `faction` is inside region `region_id`. Pure Fixed
                    // inclusive point-in-rect over EntityWorld.Position[] in ASCENDING entity-id order (the
                    // deterministic contract) — no float/Mathf/Random. An unresolved id at eval time is false (the
                    // validator already blocks dangling refs pre-tick, so this only guards shadow-mode content).
                    if (!_regions.TryGetIndex(c.RegionId, out int rIdx)) return false;
                    int rhwm = world.HighWaterMark;
                    for (int i = 0; i < rhwm; i++)
                        if (world.IsAlive(i) && world.FactionOf[i] == faction
                            && _regions.Contains(rIdx, world.Position[i]))
                            return true;
                    return false;
                default:
                    return true;
            }
        }

        // ── Action execution ──────────────────────────────────────────────────

        private void ExecuteActions(TriggerAction[] actions)
        {
            foreach (var a in actions)
            {
                switch (a.Type)
                {
                    case "spawn_unit":
                        if (!string.IsNullOrEmpty(a.UnitId))
                            OnSpawnUnit?.Invoke(a.UnitId, a.Faction, a.X, a.Z,
                                Math.Min(a.Count, 50));
                        break;
                    case "display_message":
                        if (!string.IsNullOrEmpty(a.Text))
                            OnDisplayMessage?.Invoke(a.Text, a.Duration);
                        break;
                    case "play_sound":
                        if (!string.IsNullOrEmpty(a.SoundId))
                            OnPlaySound?.Invoke(a.SoundId);
                        break;
                    case "victory":
                        OnVictory?.Invoke(a.Faction);
                        break;
                    case "defeat":
                        OnVictory?.Invoke(1 - a.Faction); // other faction wins
                        break;
                    case "create_timer":
                        if (!string.IsNullOrEmpty(a.TimerName) && a.TimerSeconds > Fixed.Zero)
                            // Fixed seconds → whole ticks via SecondsToTicks (64-bit intermediate): the plain
                            // Fixed multiply overflows for durations past ~1092 s and wraps negative (AR-14).
                            // Clamp to at least 1 tick: a sub-frame duration (0 < s < 1/30) rounds to 0 ticks, and
                            // storing remaining=0 would be indistinguishable from an expired/inactive slot and never
                            // fire. The old Dictionary path stored 0 and fired one tick later (decrement to -1);
                            // Math.Max(1, …) reproduces that exact "fires next tick" latency without the overload.
                            SetTimer(a.TimerName, Math.Max(1, SecondsToTicks(a.TimerSeconds)));
                        break;
                    case "add_resources":
                    {
                        var faction = (Faction)(a.Faction + 1);
                        _resources.AddOre(faction, a.Amount); // a.Amount is already Fixed (quantized at the JSON boundary, Story 1.4)
                        break;
                    }
                    case "set_variable":
                        if (!string.IsNullOrEmpty(a.Variable))
                            SetVariable(a.Variable, a.Value);
                        break;
                }
            }
        }

        // ── Dense timer / variable stores (creation-index SoA; deterministic linear-scan lookup) ──────────────

        /// <summary>Create or reset a named timer to <paramref name="ticks"/> remaining, preserving its creation
        /// index (a reset reuses the existing slot; a new name appends). No Dictionary — linear scan (small N).</summary>
        private void SetTimer(string name, int ticks)
        {
            for (int i = 0; i < _timerNames.Count; i++)
                if (string.Equals(_timerNames[i], name, StringComparison.Ordinal))
                {
                    _timerRemaining[i] = ticks;
                    return;
                }
            _timerNames.Add(name);
            _timerRemaining.Add(ticks);
        }

        /// <summary>Read a named integer variable, defaulting to 0 when it was never set (does NOT create a slot —
        /// matches the prior Dictionary.TryGetValue-default-0 semantics). Linear scan, no Dictionary.</summary>
        private int GetVariable(string name)
        {
            for (int i = 0; i < _variableNames.Count; i++)
                if (string.Equals(_variableNames[i], name, StringComparison.Ordinal))
                    return _variableValues[i];
            return 0;
        }

        /// <summary>Set a named integer variable, preserving its creation index (update in place, or append a new
        /// name). Last-writer within a tick follows trigger declaration-index order (AR-16). No Dictionary.</summary>
        private void SetVariable(string name, int value)
        {
            for (int i = 0; i < _variableNames.Count; i++)
                if (string.Equals(_variableNames[i], name, StringComparison.Ordinal))
                {
                    _variableValues[i] = value;
                    return;
                }
            _variableNames.Add(name);
            _variableValues.Add(value);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static int CountAlive(EntityWorld world, Faction faction)
        {
            int n = 0;
            int hwm = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
                if (world.IsAlive(i) && world.FactionOf[i] == faction) n++;
            return n;
        }

        // ≈ the prior 0.01f float tolerance (0.01 × 65536 = 655.36, rounded to 655 raw ≈ 0.0099945) so ==/!= behavior is closely preserved.
        private static readonly Fixed CompareEpsilon = Fixed.FromRaw(655);

        /// <summary>
        /// Fixed-vs-Fixed comparison for the threshold/condition sim path. Replaces the prior float compare,
        /// removing the last float arithmetic (and MathF) from ScenarioDirector (AR-16). The ==/!= cases keep a
        /// small epsilon ≈ the old 0.01f tolerance (FromRaw(655) = 0.0099945 — within ~1e-4, not exact), so
        /// existing trigger behavior is closely preserved (integer thresholds never land in that sub-0.01 gap).
        /// </summary>
        private static bool Compare(Fixed a, Fixed b, string op) => op switch
        {
            ">"  => a > b,
            "<"  => a < b,
            ">=" => a >= b,
            "<=" => a <= b,
            "==" => Fixed.Abs(a - b) <  CompareEpsilon,
            "!=" => Fixed.Abs(a - b) >= CompareEpsilon,
            _    => false
        };

        private static bool Compare(int a, int b, string op) => op switch
        {
            ">"  => a > b,
            "<"  => a < b,
            ">=" => a >= b,
            "<=" => a <= b,
            "==" => a == b,
            "!=" => a != b,
            _    => false
        };

        // ── Internal event record ─────────────────────────────────────────────

        private readonly struct FiredEvent
        {
            public readonly string  Type;
            public readonly int     Slot;    // -1 = no faction
            public readonly int     Numeric; // typed numeric payload: ore raw-Fixed integer, or unit count
            public readonly string? Data;    // string payload: building type, timer name (null when unused)

            public FiredEvent(string type, int slot, int numeric, string? data)
            {
                Type    = type;
                Slot    = slot;
                Numeric = numeric;
                Data    = data;
            }
        }
    }
}
