#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectChimera.Core.Definitions;
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

        // ── Named timers and integer variables ────────────────────────────────

        private readonly Dictionary<string, int> _timers    = new();
        private readonly Dictionary<string, int> _variables = new();

        // ── Change-detection snapshots ────────────────────────────────────────

        private readonly EntityFlags[] _prevFlags          = new EntityFlags[EntityWorld.MAX_ENTITIES];
        private readonly bool[]        _prevBuildingAlive  = new bool[BuildingStore.MAX_BUILDINGS];
        private readonly bool[]        _prevBuildingDone   = new bool[BuildingStore.MAX_BUILDINGS];

        private bool _firstTick = true;

        // ── Presentation-layer delegates ──────────────────────────────────────

        /// <summary>Requests the presentation layer to spawn units. (unitId, factionSlot, x, z, count)</summary>
        public Action<string, int, float, float, int>? OnSpawnUnit;

        /// <summary>Requests a toast notification. (text, durationSeconds)</summary>
        public Action<string, float>? OnDisplayMessage;

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
        /// Load triggers from a freshly-applied scenario. Resets all runtime state.
        /// Call after ApplyScenario() so the initial alive-state snapshots are clean.
        /// </summary>
        public void LoadScenario(ScenarioData scenario)
        {
            _triggers        = scenario.Triggers;
            _triggerFired    = new bool[_triggers.Length];
            _triggerCooldown = new int[_triggers.Length];
            _timers.Clear();
            _variables.Clear();
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
                events.Add(new FiredEvent("match_start", -1, null));
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
                    events.Add(new FiredEvent("unit_dies", slot, null));
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
                    events.Add(new FiredEvent("building_completed", slot,
                        _buildings.Type[i].ToString()));
                }
                _ = wasAlive; // snapshot updated in UpdateSnapshots
            }

            // Timers — decrement and collect expiries.
            // Iterate over a copy of keys to allow modification during enumeration.
            var keys = new List<string>(_timers.Keys);
            foreach (var name in keys)
            {
                int remaining = _timers[name] - 1;
                if (remaining <= 0)
                {
                    _timers.Remove(name);
                    events.Add(new FiredEvent("timer_expires", -1, name));
                }
                else
                {
                    _timers[name] = remaining;
                }
            }

            // Threshold events — polled every tick so triggers can react to sustained states.
            // Carry the ore as its raw Fixed integer (InvariantCulture) — locale-invariant and lossless — so the
            // match path compares Fixed-vs-Fixed with no float arithmetic or culture-dependent number formatting
            // (AR-16). slot < 2 stays as-is: widening to all active factions is Story 9.2, not this story.
            for (int slot = 0; slot < 2; slot++)
            {
                var faction = (Faction)(slot + 1);
                int oreRaw  = _resources.Ore[(int)faction].Raw;
                int units   = CountAlive(world, faction);
                events.Add(new FiredEvent("resource_threshold",   slot, oreRaw.ToString(CultureInfo.InvariantCulture)));
                events.Add(new FiredEvent("unit_count_threshold", slot, units.ToString(CultureInfo.InvariantCulture)));
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
            // Sort indices by priority descending. Trigger arrays are small (<100).
            var order = new int[_triggers.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (a, b) => _triggers[b].Priority - _triggers[a].Priority);

            foreach (int idx in order)
            {
                var t = _triggers[idx];
                if (!t.Enabled || _triggerFired[idx] || _triggerCooldown[idx] > 0) continue;
                if (!AnyEventMatches(t.Events, events))                             continue;
                if (!AllConditionsMet(t.Conditions, world))                         continue;

                ExecuteActions(t.Actions);

                if (t.RunOnce) _triggerFired[idx] = true;

                int coolTicks = (int)(t.CooldownSeconds * SimulationLoop.TICKS_PER_SECOND);
                if (coolTicks > 0) _triggerCooldown[idx] = coolTicks;
            }
        }

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
                    // f.Data is the ore's raw Fixed integer (InvariantCulture). Compare Fixed-vs-Fixed; the
                    // authored threshold (def.Amount — a JSON float) becomes Fixed at the compare site. That
                    // residual FromFloat converts an authored CONSTANT (identical bits on every peer, so not a
                    // cross-machine desync source); Story 1.4 removes it when FixedJsonConverter makes Amount a Fixed.
                    return int.TryParse(f.Data, NumberStyles.Integer, CultureInfo.InvariantCulture, out int oreRaw)
                        && Compare(Fixed.FromRaw(oreRaw), Fixed.FromFloat(def.Amount), def.Operator);
                case "unit_count_threshold":
                    if (f.Slot != def.Faction) return false;
                    return int.TryParse(f.Data, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cnt)
                        && Compare(cnt, def.Count, def.Operator);
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
                    // Fixed-vs-Fixed (no ToFloat). Authored threshold → Fixed at the compare site (1.4 removes the FromFloat).
                    return Compare(_resources.Ore[(int)faction], Fixed.FromFloat(c.Amount), c.Operator);
                case "unit_count":
                    return Compare(CountAlive(world, faction), c.Count, c.Operator);
                case "variable_comparison":
                    if (string.IsNullOrEmpty(c.Variable)) return false;
                    _variables.TryGetValue(c.Variable, out int v);
                    return Compare(v, c.Value, c.Operator);
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
                        if (!string.IsNullOrEmpty(a.TimerName) && a.TimerSeconds > 0)
                            _timers[a.TimerName] =
                                (int)(a.TimerSeconds * SimulationLoop.TICKS_PER_SECOND);
                        break;
                    case "add_resources":
                    {
                        var faction = (Faction)(a.Faction + 1);
                        _resources.AddOre(faction, Fixed.FromFloat(a.Amount));
                        break;
                    }
                    case "set_variable":
                        if (!string.IsNullOrEmpty(a.Variable))
                            _variables[a.Variable] = a.Value;
                        break;
                }
            }
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

        // ≈ the prior 0.01f float tolerance (0.01 × 65536 ≈ 655 raw) so ==/!= behavior is preserved.
        private static readonly Fixed CompareEpsilon = Fixed.FromRaw(655);

        /// <summary>
        /// Fixed-vs-Fixed comparison for the threshold/condition sim path. Replaces the prior float compare,
        /// removing the last float arithmetic (and MathF) from ScenarioDirector (AR-16). The ==/!= cases keep a
        /// small epsilon mirroring the old 0.01f tolerance so existing trigger behavior is preserved exactly.
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
            public readonly int     Slot; // -1 = no faction
            public readonly string? Data; // payload: building type, ore amount, timer name, etc.

            public FiredEvent(string type, int slot, string? data)
            {
                Type = type;
                Slot = slot;
                Data = data;
            }
        }
    }
}
