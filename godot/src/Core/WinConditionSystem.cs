#nullable enable
using ProjectChimera.Core.Definitions; // ScenarioData, WinCondition, WinConditionSpec, WinPresetKind

namespace ProjectChimera.Core
{
    /// <summary>
    /// Story 7.11 — the deterministic, sim-layer win-condition evaluator. Replaces the per-frame, P1/P2-hardcoded
    /// switch that used to live in presentation (<c>MainScene.CheckWinCondition</c>): win evaluation is now
    /// server-checkable and byte-identical across peers. Ticks in the fixed loop AFTER
    /// <c>AiOpponentSystem</c> (so it sees post-death alive counts) and immediately BEFORE
    /// <c>ScenarioDirector</c> (so the director's <c>OnVictory</c> escape hatch still runs last). Emits a
    /// <see cref="Faction"/>-typed verdict via the folded <see cref="WinStateStore"/> that presentation merely
    /// consumes to drive <c>ShowGameOver</c>.
    ///
    /// <para>Pure sim: engine-free, no fractional-primitive math, no wall-clock, and no load-time quantize — every
    /// value is an integer tick. Entities iterate <c>0..HighWaterMark</c> skipping <c>!IsAlive</c>; factions iterate
    /// <see cref="FactionRegistry.ActiveFactions"/>. All state is integer ticks in the <see cref="WinStateStore"/>.
    /// It evaluates the two built-ins (verified to pick the same winner/loser as the old switch) plus four T1
    /// presets (King of the Hill, Timed Survival, Assassination, Landmark Destruction) from a typed
    /// <see cref="WinConditionSpec"/> resolved at scenario-apply.</para>
    /// </summary>
    public sealed class WinConditionSystem : ISimSystem
    {
        /// <summary>Win-evaluation grace period in ticks — the deterministic replacement for the old framerate-
        /// dependent 180-frame (~3 s at 60 fps) presentation grace. 90 ticks = 3 s at 30 ticks/sec. Gates the
        /// BUILT-IN win conditions (avoiding a spawn-transient false game-over) AND — review P2 — every preset
        /// loss-by-ABSENCE branch (survival faction not alive / leader never resolved / landmark never resolved):
        /// this system ticks at index 14, BEFORE <c>ScenarioDirector</c> (15), so a designated faction/target
        /// spawned by a <c>match_start</c> trigger does not exist yet on tick 1 and must not read as an instant
        /// loss. Preset hold/survival COUNTERS still advance from match start, and every WIN path (plus
        /// loss-by-destruction of a RESOLVED target — a real kill is never a spawn transient) evaluates every
        /// tick, so a preset with hold_ticks/survive_ticks below the grace can still resolve.</summary>
        public const int GRACE_TICKS = 90;

        private const int FACTION_COUNT = 5; // WinStateStore array size (Faction enum 0-4)

        private readonly WinStateStore _store;
        private readonly BuildingStore _buildings;
        private readonly FactionRegistry _factions;

        // ── Apply-time resolved config (NOT folded — deterministic apply-time constants, rebuilt every apply). ──
        private RegionStore _regions = RegionStore.Empty;
        private WinPresetKind _preset = WinPresetKind.None;
        private WinCondition _builtin = WinCondition.DestroyAllBuildings;
        private int _regionIndex = -1;   // KotH: resolved region index (-1 = unresolved)
        private int _holdTicks;          // KotH: contiguous sole-hold ticks to win
        private Faction _survivalFaction = Faction.Neutral; // TimedSurvival: designated faction
        private int _leaderEntityId = -1;                   // Assassination: resolved runtime entity id
        private Faction _leaderFaction = Faction.Neutral;
        private int _landmarkRef = -1;                      // Landmark: generation-stamped BuildingStore.PackRef (P6); -1 = unresolved
        private Faction _landmarkFaction = Faction.Neutral;

        public WinConditionSystem(WinStateStore store, BuildingStore buildings, FactionRegistry factions)
        {
            _store     = store;
            _buildings = buildings;
            _factions  = factions;
        }

        /// <summary>
        /// Resolve the applied <paramref name="scenario"/>'s win condition into runtime config (mirrors
        /// <c>ScenarioDirector.SetRegionStore</c> apply-time injection). Called by <c>ScenarioApplier</c> AFTER
        /// buildings/units are placed and the <see cref="RegionStore"/> is built. <paramref name="unitEntityIds"/>
        /// maps each authored <c>ScenarioData.Units</c> index to its spawned entity id (or -1); likewise
        /// <paramref name="buildingSlots"/> for <c>ScenarioData.Buildings</c> → BuildingStore slot. The validator
        /// has already guaranteed every preset param is in range, so out-of-range resolutions here are defensive.
        /// </summary>
        public void Configure(ScenarioData scenario, RegionStore regions,
                              int[]? unitEntityIds, int[]? buildingSlots)
        {
            // P4: zero the folded store here (Configure is always an apply-time fresh-match call) so the system is
            // self-contained w.r.t. the folded store — a second Configure without an intervening external Clear()
            // cannot carry stale SurvivalRemaining/KothHoldTicks/Verdict into the checksum.
            _store.Clear();

            // Reset to the built-in path first (a re-apply must not carry stale preset config) — the SAME
            // restore-to-post-ctor-defaults path SimulationHost.ClearForReset routes through (review P10).
            ResetConfig();

            _regions = regions ?? RegionStore.Empty;
            _builtin = scenario.WinCondition;

            WinConditionSpec? spec = scenario.WinConditionSpec;
            if (spec is null || spec.Preset == WinPresetKind.None) return;

            _preset = spec.Preset;
            switch (spec.Preset)
            {
                case WinPresetKind.KingOfTheHill:
                    _regions.TryGetIndex(spec.RegionId, out _regionIndex);
                    _holdTicks = spec.HoldTicks;
                    // Review P5 — post-gate defense-in-depth (mirrors the P3-pass-1 "never a silent stalemate"
                    // rule): the validator guarantees a declared region and a positive hold, but Apply is also
                    // reachable by direct/headless hosts, and BuildRegionStore's defensive quantize-skips can drop
                    // a rect the author declared (degenerate after quantization). An unresolved region (or hold ≤ 0) would make
                    // UpdateKothCounters no-op forever — a silently un-winnable match. There is no natural LOSER
                    // for a missing region, so fall back deterministically to the built-in elimination rules:
                    // the match stays winnable.
                    if (_regionIndex < 0 || _holdTicks <= 0)
                    {
                        _preset      = WinPresetKind.None;
                        _regionIndex = -1;
                        _holdTicks   = 0;
                    }
                    break;

                case WinPresetKind.TimedSurvival:
                    _survivalFaction = FactionRegistry.ToFaction(spec.FactionSlot);
                    int sf = (int)_survivalFaction;
                    if (sf >= 0 && sf < FACTION_COUNT)
                        _store.SurvivalRemaining[sf] = spec.SurviveTicks;
                    break;

                case WinPresetKind.Assassination:
                {
                    int idx = spec.LeaderUnitIndex;
                    if (unitEntityIds != null && idx >= 0 && idx < unitEntityIds.Length)
                        _leaderEntityId = unitEntityIds[idx];
                    ScenarioUnit[] units = scenario.Units ?? System.Array.Empty<ScenarioUnit>();
                    if (idx >= 0 && idx < units.Length)
                        _leaderFaction = FactionRegistry.ToFaction(units[idx].Slot);
                    break;
                }

                case WinPresetKind.LandmarkDestruction:
                {
                    int idx = spec.StructureIndex;
                    // Review P6 (Story 2.13 D-3): every CROSS-TICK building reference must be generation-stamped
                    // via PackRef/TryResolveRef — a raw slot is ABA-unsafe (construction completing mid-match can
                    // recycle the slot to the SAME faction on the SAME tick the landmark dies, masking the loss).
                    // Golden-neutral: at generation 0, PackRef(slot) == slot. A -1 map entry (never placed) keeps
                    // the -1 "unresolved" sentinel, which can never resolve.
                    if (buildingSlots != null && idx >= 0 && idx < buildingSlots.Length && buildingSlots[idx] >= 0)
                        _landmarkRef = _buildings.PackRef(buildingSlots[idx]);
                    ScenarioBuilding[] bs = scenario.Buildings ?? System.Array.Empty<ScenarioBuilding>();
                    if (idx >= 0 && idx < bs.Length)
                        _landmarkFaction = FactionRegistry.ToFaction(bs[idx].Slot);
                    break;
                }
            }
        }

        /// <summary>
        /// Review P10 — restore every apply-time config field to its post-construction default. Called by
        /// <c>SimulationHost.ClearForReset</c> (the config is NOT in any store, so a Clear WITHOUT a re-Configure
        /// must not leave e.g. <c>_preset == TimedSurvival</c> pointed at a zeroed <c>SurvivalRemaining</c> — an
        /// instant false win on the next tick) and by <see cref="Configure"/> as its reset-first step, so both
        /// paths share the one restore.
        /// </summary>
        public void ResetConfig()
        {
            _regions         = RegionStore.Empty;
            _preset          = WinPresetKind.None;
            _builtin         = WinCondition.DestroyAllBuildings;
            _regionIndex     = -1;
            _holdTicks       = 0;
            _survivalFaction = Faction.Neutral;
            _leaderEntityId  = -1;
            _leaderFaction   = Faction.Neutral;
            _landmarkRef     = -1;
            _landmarkFaction = Faction.Neutral;
        }

        public void Tick(EntityWorld world, Fixed dt)
        {
            if (_store.IsResolved()) return; // match already decided — the latch is final

            _store.MatchTicks++;

            // Advance the per-faction counters EVERY tick (so hold-time / survival count from match start); the
            // grace gate below only defers the WIN/LOSS LATCH, never the counter bookkeeping.
            if (_preset == WinPresetKind.TimedSurvival)
            {
                int sf = (int)_survivalFaction;
                if (sf >= 0 && sf < FACTION_COUNT && _store.SurvivalRemaining[sf] > 0)
                    _store.SurvivalRemaining[sf]--;
            }
            else if (_preset == WinPresetKind.KingOfTheHill)
            {
                UpdateKothCounters(world);
            }

            // The grace gate defers ONLY the built-in latch (spawn-transient guard); presets evaluate every tick.
            switch (_preset)
            {
                case WinPresetKind.None:
                    if (_store.MatchTicks >= GRACE_TICKS) EvaluateBuiltin(world);
                    break;
                case WinPresetKind.KingOfTheHill:       EvaluateKoth();               break;
                case WinPresetKind.TimedSurvival:       EvaluateSurvival(world);      break;
                case WinPresetKind.Assassination:       EvaluateAssassination(world); break;
                case WinPresetKind.LandmarkDestruction: EvaluateLandmark();           break;
            }
        }

        // ── Built-in win conditions — must pick the SAME winner/loser as the old MainScene switch. ──
        private void EvaluateBuiltin(EntityWorld world)
        {
            bool p1, p2;
            if (_builtin == WinCondition.DestroyAllBuildings) CountBuildingsAlive(out p1, out p2);
            else                                              CountUnitsAlive(world, out p1, out p2);

            // Parity with the old switch: `if (!p1Alive) ShowGameOver(2); else if (!p2Alive) ShowGameOver(1);`
            // — a simultaneous double-elimination resolves to Player2 winning (the !p1 branch takes priority).
            if (!p1)      Resolve(Faction.Player2, Faction.Player1);
            else if (!p2) Resolve(Faction.Player1, Faction.Player2);
        }

        private void CountBuildingsAlive(out bool p1, out bool p2)
        {
            p1 = false; p2 = false;
            for (int i = 0; i < _buildings.Count; i++)
            {
                if (!_buildings.Alive[i]) continue;
                Faction f = _buildings.FactionOf[i];
                if (f == Faction.Player1) p1 = true;
                else if (f == Faction.Player2) p2 = true;
            }
        }

        private static void CountUnitsAlive(EntityWorld world, out bool p1, out bool p2)
        {
            p1 = false; p2 = false;
            int hwm = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
            {
                if (!world.IsAlive(i)) continue;
                Faction f = world.FactionOf[i];
                if (f == Faction.Player1) p1 = true;
                else if (f == Faction.Player2) p2 = true;
            }
        }

        // ── King of the Hill ──
        /// <summary>Advance/reset the per-faction contiguous sole-hold counters. Review P12(a) — NEUTRAL units
        /// neither hold nor contest the zone: presence scans iterate <see cref="FactionRegistry.ActiveFactions"/>,
        /// which excludes Neutral, so a Neutral bystander standing in the region never blocks a sole hold. This is
        /// deliberate — Neutral entities are scenery/critters, not combatants.</summary>
        private void UpdateKothCounters(EntityWorld world)
        {
            if (_regionIndex < 0) return; // unresolved region — never advances

            Faction sole = Faction.Neutral;
            int present = 0;
            foreach (Faction f in _factions.ActiveFactions)
            {
                if (HasUnitInRegion(world, f)) { present++; sole = f; }
            }

            if (present == 1)
            {
                foreach (Faction f in _factions.ActiveFactions)
                    _store.KothHoldTicks[(int)f] = (f == sole) ? _store.KothHoldTicks[(int)f] + 1 : 0;
            }
            else
            {
                // Contested (≥2 present) OR empty (0 present): no faction SOLELY holds → reset every counter.
                foreach (Faction f in _factions.ActiveFactions)
                    _store.KothHoldTicks[(int)f] = 0;
            }
        }

        private void EvaluateKoth()
        {
            if (_holdTicks <= 0) return;
            foreach (Faction f in _factions.ActiveFactions)
            {
                if (_store.KothHoldTicks[(int)f] >= _holdTicks)
                {
                    Resolve(f, OtherFaction(f));
                    return;
                }
            }
        }

        private bool HasUnitInRegion(EntityWorld world, Faction faction)
        {
            int hwm = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
                if (world.IsAlive(i) && world.FactionOf[i] == faction
                    && _regions.Contains(_regionIndex, world.Position[i]))
                    return true;
            return false;
        }

        // ── Timed Survival ──
        private void EvaluateSurvival(EntityWorld world)
        {
            if (_survivalFaction == Faction.Neutral) return;
            int sf = (int)_survivalFaction;

            if (!FactionAlive(world, _survivalFaction))
            {
                // Review P2 — grace-gated loss-by-ABSENCE: the designated faction may be spawned by a match_start
                // trigger that runs AFTER this system on tick 1 (ScenarioDirector ticks at index 15); absence
                // inside the grace window means "not spawned yet", never an instant loss. Review P12(b) — on the
                // tick BOTH branches become true (eliminated on the exact deadline tick), elimination is checked
                // FIRST → the designated faction LOSES (pinned by test).
                if (_store.MatchTicks >= GRACE_TICKS)
                    Resolve(OtherFaction(_survivalFaction), _survivalFaction); // eliminated before the deadline → loses
                return;
            }
            if (sf >= 0 && sf < FACTION_COUNT && _store.SurvivalRemaining[sf] <= 0)
                Resolve(_survivalFaction, OtherFaction(_survivalFaction)); // survived to the deadline → wins
        }

        // ── Assassination ──
        // (Expressibility note, Story 7.13: the public DSL cannot yet DESIGNATE a specific unit instance — the
        // typed spec's placement index is the native designation channel until that vocabulary lands.)
        private void EvaluateAssassination(EntityWorld world)
        {
            // P3: an unresolved leader (passed param-validation but failed to spawn, so unitEntityIds[idx] == -1)
            // means the protected asset never existed → its owner loses deterministically, never a silent
            // stalemate. Review P2 — grace-gated: the leader may be spawned by a match_start trigger that runs
            // AFTER this system on tick 1, so absence inside the grace window is "not spawned yet", not a loss.
            if (_leaderEntityId < 0)
            {
                if (_store.MatchTicks >= GRACE_TICKS)
                    Resolve(OtherFaction(_leaderFaction), _leaderFaction);
                return;
            }
            // Dead if the slot is no longer alive, or was recycled to a different faction (defensive against the
            // no-per-instance-id ABA edge — we latch the tick the leader dies, before any later-tick recycle).
            // NEVER grace-gated: a RESOLVED leader dying is a real assassination, not a spawn transient.
            if (!world.IsAlive(_leaderEntityId) || world.FactionOf[_leaderEntityId] != _leaderFaction)
                Resolve(OtherFaction(_leaderFaction), _leaderFaction);
        }

        // ── Landmark Destruction ──
        // (Expressibility note, Story 7.13: same instance-designation gap as Assassination — see above.)
        private void EvaluateLandmark()
        {
            // P3: an unresolved structure (passed param-validation but failed to place, so buildingSlots[idx] == -1)
            // means the protected asset never existed → its owner loses deterministically, never a silent
            // stalemate. Review P2 — grace-gated: absence inside the grace window is "not placed yet", not a loss
            // (the same director-runs-after-us ordering as the leader case above).
            if (_landmarkRef < 0)
            {
                if (_store.MatchTicks >= GRACE_TICKS)
                    Resolve(OtherFaction(_landmarkFaction), _landmarkFaction);
                return;
            }
            // Review P6: deref the generation-stamped ref each tick — a failed resolve (destroyed, or the slot
            // recycled to a NEW building, even same-faction on the same tick) or a faction flip means the
            // DESIGNATED landmark is gone → its owner loses. NEVER grace-gated: a RESOLVED landmark dying is a
            // real destruction, not a spawn transient.
            if (!_buildings.TryResolveRef(_landmarkRef, out int slot)
                || _buildings.FactionOf[slot] != _landmarkFaction)
                Resolve(OtherFaction(_landmarkFaction), _landmarkFaction);
        }

        // ── Shared verdict + faction helpers ──
        private void Resolve(Faction winner, Faction loser)
        {
            int w = (int)winner, l = (int)loser;
            // P1: only a REAL faction (index > 0) can WIN. A Neutral "winner" (index 0 — no distinct other active
            // faction) must never latch WON, or IsResolved()/WinnerFaction() would freeze the match at winner 0.
            if (w > 0 && w < FACTION_COUNT && _store.Verdict[w] == WinStateStore.VERDICT_NONE)
                _store.Verdict[w] = WinStateStore.VERDICT_WON;
            if (l >= 0 && l < FACTION_COUNT && _store.Verdict[l] == WinStateStore.VERDICT_NONE)
                _store.Verdict[l] = WinStateStore.VERDICT_LOST;
        }

        /// <summary>The single OTHER active faction (the two-faction assumption — N-faction resolution is Story
        /// 7.12). Returns <see cref="Faction.Neutral"/> if no distinct other active faction exists.</summary>
        private Faction OtherFaction(Faction f)
        {
            foreach (Faction a in _factions.ActiveFactions)
                if (a != f) return a;
            return Faction.Neutral;
        }

        private bool FactionAlive(EntityWorld world, Faction faction)
        {
            int hwm = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
                if (world.IsAlive(i) && world.FactionOf[i] == faction) return true;
            for (int i = 0; i < _buildings.Count; i++)
                if (_buildings.Alive[i] && _buildings.FactionOf[i] == faction) return true;
            return false;
        }
    }
}
