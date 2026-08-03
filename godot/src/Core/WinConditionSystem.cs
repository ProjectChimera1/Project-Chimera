#nullable enable
using ProjectChimera.Core.Definitions; // ScenarioData, WinCondition, WinConditionSpec, WinPresetKind

namespace ProjectChimera.Core
{
    /// <summary>
    /// Story 7.11 + 7.12 — the deterministic, sim-layer win-condition evaluator. Story 7.11 moved win evaluation out
    /// of the per-frame, P1/P2-hardcoded presentation switch (<c>MainScene.CheckWinCondition</c>) into this system;
    /// Story 7.12 generalized it from the 2-faction "the single other faction" assumption to <b>N-faction,
    /// team-aware, last-team-standing</b> resolution driven by the sim-owned <see cref="AllianceStore"/> mask
    /// (default FFA / teams-of-1). Ticks in the fixed loop AFTER <c>AiOpponentSystem</c> (so it sees post-death
    /// alive counts) and immediately BEFORE <c>ScenarioDirector</c> (so the director's <c>OnVictory</c> escape hatch
    /// still runs last). Emits per-faction <see cref="WinStateStore.Verdict"/> latches presentation merely consumes.
    ///
    /// <para>Pure sim: engine-free, no fractional-primitive math, no wall-clock, no load-time quantize — every value
    /// is an integer tick. Entities iterate <c>0..HighWaterMark</c> skipping <c>!IsAlive</c>; factions iterate
    /// <see cref="FactionRegistry.ActiveFactions"/> (never <c>Player1</c>/<c>Player2</c> literals). All folded state
    /// is integer ticks in the <see cref="WinStateStore"/>; the immutable team mask lives in the folded
    /// <see cref="AllianceStore"/>.</para>
    ///
    /// <para><b>Per-tick resolution (7.12, the heart):</b> after advancing 7.11 counters, in order —
    /// (1) <b>loss pass</b>: for each active <see cref="WinStateStore.VERDICT_NONE"/> faction, if its preset loss
    /// predicate holds (built-in: no alive assets of the relevant kind; survival: designated faction eliminated;
    /// assassination/landmark: target dead or unresolved — each latching the target faction's whole TEAM; plus, for
    /// the non-designated factions of asymmetric presets, total wipeout) latch <see cref="WinStateStore.VERDICT_LOST"/>
    /// per the 7.11 grace rules (loss-by-absence grace-gated; a resolved-target death ungated), while the match
    /// CONTINUES for every unresolved faction; (2) <b>positive-objective win</b>: a KotH team reaching
    /// <c>hold_ticks</c> or a Survival team reaching its deadline alive latches <see cref="WinStateStore.VERDICT_WON"/>
    /// for that team and LOST for all others; (3) <b>last-team-standing</b>: if exactly one team is still live and at
    /// least one faction has latched LOST, that team's live factions win; on a tick that eliminates the last ≥2 live
    /// teams simultaneously, the highest-slot faction eliminated this tick (and its team) wins (the 7.11 P1+P2
    /// double-elim → Player2 tie-break). In FFA (teams-of-1, the default) the model degenerates to
    /// last-faction-standing and matches 7.11 exactly in the 2-faction case.</para>
    ///
    /// <para>Eliminated factions' remaining entities/buildings REMAIN in place under their own faction (neither
    /// force-removed nor reassigned to Neutral) — elimination is a verdict only; a latched-LOST faction can never
    /// subsequently win.</para>
    /// </summary>
    public sealed class WinConditionSystem : ISimSystem
    {
        /// <summary>Win-evaluation grace period in ticks — the deterministic replacement for the old framerate-
        /// dependent 180-frame presentation grace. 90 ticks = 3 s at 30 ticks/sec. Gates every loss-by-ABSENCE
        /// branch (built-in "no assets"; survival faction not alive; non-designated total wipeout; a leader/landmark
        /// that never resolved): this system ticks at index 14, BEFORE <c>ScenarioDirector</c> (15), so a designated
        /// faction/target spawned by a <c>match_start</c> trigger does not exist yet on tick 1 and must not read as an
        /// instant loss. Preset hold/survival COUNTERS advance from match start, and every WIN path (plus
        /// loss-by-destruction of a RESOLVED target — a real kill is never a spawn transient) evaluates every tick,
        /// so a preset with hold_ticks/survive_ticks below the grace can still resolve.</summary>
        public const int GRACE_TICKS = 90;

        private const int FACTION_COUNT = FactionRegistry.FACTION_ARRAY_SIZE; // 9: WinStateStore / AllianceStore array size (Neutral + Player1..Player8)

        private readonly WinStateStore _store;
        private readonly EntityWorld _world;      // DW-184: Configure packs the leader ref against the live world's generations
        private readonly BuildingStore _buildings;
        private readonly FactionRegistry _factions;
        private readonly AllianceStore _alliances;

        // Reusable per-tick scratch for "distinct teams present/live" scans (team ids are in [0, FACTION_COUNT)).
        // A member field (not a per-tick alloc) keeps the tick allocation-free; determinism-neutral (cleared per use).
        private readonly bool[] _teamScratch = new bool[FACTION_COUNT];

        // Review (7.12): per-faction "latched LOST THIS tick" scratch. The simultaneous double-elimination tie-break
        // may promote the winning team's members back to WON, but ONLY those eliminated on the tie-break tick — a
        // teammate who died on an EARLIER tick is genuinely dead and must stay LOST (the monotone-latch invariant),
        // else WinnerFaction() could report a faction that already showed its defeat banner. Cleared per ResolveTick.
        private readonly bool[] _eliminatedThisTick = new bool[FACTION_COUNT];

        // ── Apply-time resolved config (NOT folded — deterministic apply-time constants, rebuilt every apply). ──
        private RegionStore _regions = RegionStore.Empty;
        private WinPresetKind _preset = WinPresetKind.None;
        private WinCondition _builtin = WinCondition.DestroyAllBuildings;
        private int _regionIndex = -1;   // KotH: resolved region index (-1 = unresolved)
        private int _holdTicks;          // KotH: contiguous sole-hold ticks to win
        private Faction _survivalFaction = Faction.Neutral; // TimedSurvival: designated faction
        private int _leaderRef = -1;                        // Assassination: generation-stamped EntityWorld.PackRef (DW-184); -1 = unresolved
        private Faction _leaderFaction = Faction.Neutral;
        private int _landmarkRef = -1;                      // Landmark: generation-stamped BuildingStore.PackRef (P6); -1 = unresolved
        private Faction _landmarkFaction = Faction.Neutral;

        public WinConditionSystem(WinStateStore store, EntityWorld world, BuildingStore buildings,
                                  FactionRegistry factions, AllianceStore alliances)
        {
            _store     = store;
            _world     = world;
            _buildings = buildings;
            _factions  = factions;
            _alliances = alliances;
        }

        /// <summary>
        /// Resolve the applied <paramref name="scenario"/>'s win condition into runtime config (mirrors
        /// <c>ScenarioDirector.SetRegionStore</c> apply-time injection). Called by <c>ScenarioApplier</c> AFTER
        /// buildings/units are placed and the <see cref="RegionStore"/> is built. <paramref name="unitEntityIds"/>
        /// maps each authored <c>ScenarioData.Units</c> index to its spawned entity id (or -1); likewise
        /// <paramref name="buildingSlots"/> for <c>ScenarioData.Buildings</c> → BuildingStore slot. The validator
        /// has already guaranteed every preset param is in range, so out-of-range resolutions here are defensive.
        /// The alliance mask is NOT set here — it is a first-class store (default FFA; Story 9.15 seeds teams).
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
                    // Review P5 — post-gate defense-in-depth: an unresolved region (BuildRegionStore quantize-skip, or
                    // a direct/headless host) or a non-positive hold would make UpdateKothCounters a permanent no-op —
                    // a silently un-winnable match with no natural loser. Fall back deterministically to the built-in
                    // elimination rules so the match stays winnable.
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
                    // DW-184 (mirrors the Landmark P6 fix below): every CROSS-TICK entity reference must be
                    // generation-stamped via EntityWorld.PackRef/TryResolveRef — a raw id is ABA-unsafe (a same-tick
                    // same-faction slot recycle between the death systems and this evaluator would mask the leader's
                    // death, making the target effectively immortal). Golden-neutral: at generation 0,
                    // PackRef(id) == id. A -1 map entry (never spawned) keeps the -1 "unresolved" sentinel.
                    if (unitEntityIds != null && idx >= 0 && idx < unitEntityIds.Length && unitEntityIds[idx] >= 0)
                        _leaderRef = _world.PackRef(unitEntityIds[idx]);
                    ScenarioUnit[] units = scenario.Units ?? System.Array.Empty<ScenarioUnit>();
                    if (idx >= 0 && idx < units.Length)
                        _leaderFaction = FactionRegistry.ToFaction(units[idx].Slot);
                    break;
                }

                case WinPresetKind.LandmarkDestruction:
                {
                    int idx = spec.StructureIndex;
                    // Review P6 (Story 2.13 D-3): every CROSS-TICK building reference must be generation-stamped via
                    // PackRef/TryResolveRef — a raw slot is ABA-unsafe (a completing construction can recycle the slot
                    // to the SAME faction on the SAME tick the landmark dies, masking the loss). Golden-neutral: at
                    // generation 0, PackRef(slot) == slot. A -1 map entry (never placed) keeps the -1 "unresolved"
                    // sentinel, which can never resolve.
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
            _leaderRef       = -1;
            _leaderFaction   = Faction.Neutral;
            _landmarkRef     = -1;
            _landmarkFaction = Faction.Neutral;
        }

        public void Tick(EntityWorld world, Fixed dt)
        {
            if (IsFullyResolved()) return; // every active faction has a latched verdict — the match is decided

            _store.MatchTicks++;

            // Advance the per-faction/per-team counters EVERY tick (so hold-time / survival count from match start);
            // the grace gate below only defers the loss-by-ABSENCE latch, never the counter bookkeeping.
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

            ResolveTick(world);
        }

        // ── Unified per-tick resolution (7.12): loss pass → positive-objective win → last-team-standing. ──
        private void ResolveTick(EntityWorld world)
        {
            // Live teams BEFORE this tick's losses — gates the simultaneous double-elimination tie-break (a match that
            // starts with a single team must never "win" by wiping itself; only the last ≥2 teams dying at once ties).
            int liveTeamsBefore = CountLiveTeams();

            int highestEliminatedThisTick = -1; // highest faction slot latched LOST this tick (the tie-break winner)
            System.Array.Clear(_eliminatedThisTick); // reset the "eliminated on THIS tick" set for the tie-break

            // ── (1a) Designated-target losses — latch the target faction's WHOLE TEAM (asymmetric presets). ──
            switch (_preset)
            {
                case WinPresetKind.TimedSurvival:
                    if (_survivalFaction != Faction.Neutral && DesignatedSurvivalEliminated(world))
                        LoseTeam(_survivalFaction, ref highestEliminatedThisTick);
                    break;
                case WinPresetKind.Assassination:
                    // Guard _leaderFaction != Neutral (mirrors the TimedSurvival case): a validated scenario never
                    // designates a Neutral-slot leader, but a mis-seeded/unvalidated one would make LoseTeam resolve
                    // team 0 and wrongly latch LOST any active faction allied to slot 0.
                    if (_leaderFaction != Faction.Neutral && LeaderDead(world))
                        LoseTeam(_leaderFaction, ref highestEliminatedThisTick);
                    break;
                case WinPresetKind.LandmarkDestruction:
                    if (_landmarkFaction != Faction.Neutral && LandmarkDead())
                        LoseTeam(_landmarkFaction, ref highestEliminatedThisTick);
                    break;
            }

            // ── (1b) Symmetric per-faction losses (built-in asset predicate; asymmetric non-designated wipeout). ──
            foreach (Faction f in _factions.ActiveFactions)
            {
                int idx = (int)f;
                if (_store.Verdict[idx] != WinStateStore.VERDICT_NONE) continue;
                if (SymmetricLoss(world, f))
                {
                    _store.Verdict[idx] = WinStateStore.VERDICT_LOST;
                    _eliminatedThisTick[idx] = true;
                    if (idx > highestEliminatedThisTick) highestEliminatedThisTick = idx;
                }
            }

            // ── (2) Positive-objective win — wins the whole satisfying team, LOST for all others; done. ──
            if (_preset == WinPresetKind.KingOfTheHill)
            {
                int team = KothWinningTeam();
                if (team >= 0) { WinTeam(team); return; }
                // KotH normally concludes ONLY by the hold-win (7.11 parity — no elimination fallback). Story 11.2:
                // a CONCEDE (the only way a KotH faction latches VERDICT_LOST short of the hold-win) must still
                // resolve, so fall through to last-team-standing instead of the old bare return that dead-ended the
                // match. ApplyLastTeamStanding no-ops unless AnyLost() AND exactly one live team remains (or the
                // double-elim tie) — and under KotH nobody latches LOST except via concede or the hold-win's WinTeam
                // above — so a normal (no-concede) KotH match still concludes ONLY by the hold-win.
                ApplyLastTeamStanding(liveTeamsBefore, highestEliminatedThisTick);
                return;
            }
            if (_preset == WinPresetKind.TimedSurvival)
            {
                int sf = (int)_survivalFaction;
                if (sf > 0 && sf < FACTION_COUNT
                    && _store.Verdict[sf] == WinStateStore.VERDICT_NONE
                    && _store.SurvivalRemaining[sf] <= 0
                    && FactionAlive(world, _survivalFaction))
                {
                    // Review P12(b): elimination (1a) is checked BEFORE this, so a designate eliminated on the exact
                    // deadline tick has already latched LOST above and this Verdict==NONE guard skips the win.
                    // Review fix (7.12): the win ALSO requires the designate be ALIVE at the deadline (restoring the
                    // 7.11 EvaluateSurvival ordering that decoupling the loss/win passes dropped). Loss-by-absence is
                    // grace-gated, so with survive_ticks < GRACE the counter can reach 0 while the designate is dead
                    // or never spawned; without this guard that faction would WIN by timer. The unresolved case then
                    // stays NONE until grace ends, when (1a) latches its deserved LOST.
                    WinTeam(_alliances.TeamOf(_survivalFaction));
                    return;
                }
            }

            // ── (3) Last-team-standing (built-in + the asymmetric presets after their designated/wipeout losses). ──
            ApplyLastTeamStanding(liveTeamsBefore, highestEliminatedThisTick);
        }

        /// <summary>Symmetric per-faction loss predicate (grace-gated loss-by-absence). Built-in: no alive asset of
        /// the relevant kind (buildings for DestroyAllBuildings, units for EliminateAllUnits). Asymmetric presets:
        /// total wipeout for the NON-designated factions — but ONLY in a ≥3-faction match. In a 2-faction asymmetric
        /// match the single opponent IS the last team standing (the 7.11 <c>OtherFaction</c> parity: it wins when the
        /// designated target dies, and is never itself wiped-eliminated); total-wipeout only DISCRIMINATES among ≥3
        /// factions. KotH has no symmetric loss (it concludes only by the hold-win). Neutral is never active.</summary>
        private bool SymmetricLoss(EntityWorld world, Faction f)
        {
            switch (_preset)
            {
                case WinPresetKind.None: // built-in
                    if (_store.MatchTicks < GRACE_TICKS) return false; // spawn-transient guard
                    return _builtin == WinCondition.DestroyAllBuildings
                        ? !HasAliveBuildings(f)
                        : !HasAliveUnits(world, f);

                case WinPresetKind.KingOfTheHill:
                    return false; // KotH resolves only by the positive hold-win

                default: // TimedSurvival / Assassination / LandmarkDestruction — non-designated total wipeout
                    if (_factions.ActiveCount < 3) return false; // 2-faction: 7.11 OtherFaction parity (no wipeout)
                    if (_store.MatchTicks < GRACE_TICKS) return false;
                    return !FactionAlive(world, f);
            }
        }

        // ── King of the Hill (team-aware) ──
        /// <summary>Advance/reset the per-TEAM contiguous sole-hold counters. Review P12(a) — NEUTRAL units neither
        /// hold nor contest the zone (presence scans iterate <see cref="FactionRegistry.ActiveFactions"/>, which
        /// excludes Neutral). Story 7.12 — presence is aggregated by TEAM: allied co-occupants do NOT contest each
        /// other, and the accruing count is stored on the team's REPRESENTATIVE (lowest-slot member) so contiguous
        /// team holding survives an individual ally leaving. The counter advances only when exactly ONE team is
        /// present in the region; any contested (≥2 teams) or empty tick resets every counter.</summary>
        private void UpdateKothCounters(EntityWorld world)
        {
            if (_regionIndex < 0) return; // unresolved region — never advances

            System.Array.Clear(_teamScratch);
            int presentTeams = 0;
            int soleTeam = -1;
            foreach (Faction f in _factions.ActiveFactions)
            {
                if (!HasUnitInRegion(world, f)) continue;
                int team = _alliances.TeamOf(f);
                if (team >= 0 && team < FACTION_COUNT && !_teamScratch[team])
                {
                    _teamScratch[team] = true;
                    presentTeams++;
                    soleTeam = team;
                }
            }

            if (presentTeams == 1)
            {
                int rep = TeamRep(soleTeam); // lowest-slot active member — the one contiguous accumulator
                foreach (Faction f in _factions.ActiveFactions)
                    _store.KothHoldTicks[(int)f] = ((int)f == rep) ? _store.KothHoldTicks[rep] + 1 : 0;
            }
            else
            {
                // Contested (≥2 teams) OR empty (0 teams): no team SOLELY holds → reset every counter.
                foreach (Faction f in _factions.ActiveFactions)
                    _store.KothHoldTicks[(int)f] = 0;
            }
        }

        /// <summary>The team id whose representative counter has reached <c>hold_ticks</c>, else -1. Only the team
        /// representative accrues (see <see cref="UpdateKothCounters"/>), so the matching faction is that rep.</summary>
        private int KothWinningTeam()
        {
            if (_holdTicks <= 0) return -1;
            foreach (Faction f in _factions.ActiveFactions)
                if (_store.Verdict[(int)f] == WinStateStore.VERDICT_NONE
                    && _store.KothHoldTicks[(int)f] >= _holdTicks)
                    return _alliances.TeamOf(f);
            return -1;
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

        // ── Timed Survival / Assassination / Landmark designated-target death checks ──
        private bool DesignatedSurvivalEliminated(EntityWorld world)
        {
            // Grace-gated loss-by-ABSENCE: the designated faction may be spawned by a match_start trigger that runs
            // AFTER this system on tick 1 (ScenarioDirector ticks at index 15); absence inside the grace window is
            // "not spawned yet", never an instant loss.
            if (_store.MatchTicks < GRACE_TICKS) return false;
            return !FactionAlive(world, _survivalFaction);
        }

        // (Expressibility note, Story 7.13: the public DSL cannot yet DESIGNATE a specific unit/structure instance —
        // the typed spec's placement index is the native designation channel until that vocabulary lands.)
        private bool LeaderDead(EntityWorld world)
        {
            // P3: an unresolved leader (passed param-validation but failed to spawn, so unitEntityIds[idx] == -1) means
            // the protected asset never existed → its owner's team loses deterministically. Grace-gated (the leader may
            // be a match_start spawn that lands AFTER this system on tick 1).
            if (_leaderRef < 0) return _store.MatchTicks >= GRACE_TICKS;
            // DW-184 (mirrors LandmarkDead's P6): deref the generation-stamped ref each tick — a failed resolve (died,
            // or the slot recycled to a NEW unit, even same-faction on the same tick) or a faction flip means the
            // DESIGNATED leader is gone → a real assassination, NEVER grace-gated. The old raw-id IsAlive+faction check
            // could not see a same-tick same-faction recycle (no entity generation counter existed).
            return !world.TryResolveRef(_leaderRef, out int id) || world.FactionOf[id] != _leaderFaction;
        }

        private bool LandmarkDead()
        {
            // P3: an unresolved structure (buildingSlots[idx] == -1) means the protected asset never existed → its
            // owner's team loses deterministically. Grace-gated (a match_start placement lands after this system).
            if (_landmarkRef < 0) return _store.MatchTicks >= GRACE_TICKS;
            // Review P6: deref the generation-stamped ref each tick — a failed resolve (destroyed, or the slot recycled
            // to a NEW building, even same-faction on the same tick) or a faction flip means the DESIGNATED landmark is
            // gone → its owner's team loses. NEVER grace-gated: a RESOLVED landmark dying is a real destruction.
            return !_buildings.TryResolveRef(_landmarkRef, out int slot)
                || _buildings.FactionOf[slot] != _landmarkFaction;
        }

        // ── Team-aware verdict resolution ──
        /// <summary>Latch <see cref="WinStateStore.VERDICT_WON"/> for <paramref name="team"/>'s still-live factions and
        /// <see cref="WinStateStore.VERDICT_LOST"/> for every OTHER active still-live faction (a team wins/loses as a
        /// unit). Already-latched verdicts are never overwritten.</summary>
        private void WinTeam(int team)
        {
            foreach (Faction f in _factions.ActiveFactions)
            {
                int idx = (int)f;
                if (_store.Verdict[idx] != WinStateStore.VERDICT_NONE) continue;
                _store.Verdict[idx] = (_alliances.TeamOf(f) == team)
                    ? WinStateStore.VERDICT_WON
                    : WinStateStore.VERDICT_LOST;
            }
        }

        /// <summary>Latch <see cref="WinStateStore.VERDICT_LOST"/> for every still-live faction on
        /// <paramref name="anyMember"/>'s team (the designated target's whole team), tracking the highest slot
        /// eliminated this tick for the simultaneous-elimination tie-break.</summary>
        private void LoseTeam(Faction anyMember, ref int highestEliminatedThisTick)
        {
            int team = _alliances.TeamOf(anyMember);
            foreach (Faction f in _factions.ActiveFactions)
            {
                int idx = (int)f;
                if (_store.Verdict[idx] != WinStateStore.VERDICT_NONE) continue;
                if (_alliances.TeamOf(f) == team)
                {
                    _store.Verdict[idx] = WinStateStore.VERDICT_LOST;
                    _eliminatedThisTick[idx] = true;
                    if (idx > highestEliminatedThisTick) highestEliminatedThisTick = idx;
                }
            }
        }

        private void ApplyLastTeamStanding(int liveTeamsBefore, int highestEliminatedThisTick)
        {
            int liveTeamsAfter = CountLiveTeams();

            if (liveTeamsAfter == 1)
            {
                // A single team at match start is NOT a win — resolution must have begun (≥1 faction latched LOST).
                if (!AnyLost()) return;
                WinTeam(SoleLiveTeam());
                return;
            }

            if (liveTeamsAfter == 0 && liveTeamsBefore >= 2 && highestEliminatedThisTick > 0)
            {
                // The last ≥2 live teams were eliminated SIMULTANEOUSLY this tick → deterministic tie-break: the
                // highest-SLOT faction eliminated this tick (and its team) wins, overriding its just-this-tick LOST.
                // Reproduces the 7.11 P1+P2 double-elim → Player2 wins (in FFA the highest slot IS the highest team id;
                // under non-FFA teams the tie-break keys on the highest faction SLOT, not the team id).
                //
                // Review fix: promote ONLY winning-team members eliminated on THIS tick (or still NONE) — a teammate
                // latched LOST on an EARLIER tick is genuinely dead and stays LOST, preserving the monotone-latch
                // invariant so WinnerFaction() never reports a faction that already resolved (and spectated) as LOST.
                int winTeam = _alliances.TeamOf((Faction)highestEliminatedThisTick);
                foreach (Faction f in _factions.ActiveFactions)
                {
                    int idx = (int)f;
                    if (_alliances.TeamOf(f) != winTeam) continue;
                    if (_eliminatedThisTick[idx] || _store.Verdict[idx] == WinStateStore.VERDICT_NONE)
                        _store.Verdict[idx] = WinStateStore.VERDICT_WON;
                }
                return;
            }

            // liveTeamsAfter == 0 && liveTeamsBefore < 2 → LOST-only outcome (a lone team wiped itself out): leave the
            // verdicts as LOST (the no-victor match-over form). liveTeamsAfter >= 2 → the match CONTINUES.
        }

        // ── Faction / team scans ──
        private int CountLiveTeams()
        {
            System.Array.Clear(_teamScratch);
            int count = 0;
            foreach (Faction f in _factions.ActiveFactions)
            {
                if (_store.Verdict[(int)f] != WinStateStore.VERDICT_NONE) continue;
                int team = _alliances.TeamOf(f);
                if (team >= 0 && team < FACTION_COUNT && !_teamScratch[team]) { _teamScratch[team] = true; count++; }
            }
            return count;
        }

        private int SoleLiveTeam()
        {
            foreach (Faction f in _factions.ActiveFactions)
                if (_store.Verdict[(int)f] == WinStateStore.VERDICT_NONE)
                    return _alliances.TeamOf(f);
            return -1;
        }

        private bool AnyLost()
        {
            foreach (Faction f in _factions.ActiveFactions)
                if (_store.Verdict[(int)f] == WinStateStore.VERDICT_LOST) return true;
            return false;
        }

        /// <summary>Lowest-slot active member of <paramref name="team"/> (<see cref="FactionRegistry.ActiveFactions"/>
        /// is ascending), i.e. the team's KotH accumulator representative.</summary>
        private int TeamRep(int team)
        {
            foreach (Faction f in _factions.ActiveFactions)
                if (_alliances.TeamOf(f) == team) return (int)f;
            return -1;
        }

        /// <summary>True once EVERY active faction has a latched (non-<see cref="WinStateStore.VERDICT_NONE"/>) verdict
        /// — the match is fully decided and further ticking is a no-op. Distinct from
        /// <see cref="WinStateStore.IsResolved"/> (any non-none, which latches on the FIRST loss): the match must keep
        /// evaluating while some factions are LOST and others are still NONE (the N-faction "match continues" rule).
        /// Presentation reads this to know when to fire the terminal game-over overlay.</summary>
        public bool IsFullyResolved()
        {
            foreach (Faction f in _factions.ActiveFactions)
                if (_store.Verdict[(int)f] == WinStateStore.VERDICT_NONE) return false;
            return true;
        }

        // ── Asset scans ──
        private bool HasAliveBuildings(Faction faction)
        {
            for (int i = 0; i < _buildings.Count; i++)
                if (_buildings.Alive[i] && _buildings.FactionOf[i] == faction) return true;
            return false;
        }

        private static bool HasAliveUnits(EntityWorld world, Faction faction)
        {
            int hwm = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
                if (world.IsAlive(i) && world.FactionOf[i] == faction) return true;
            return false;
        }

        private bool FactionAlive(EntityWorld world, Faction faction)
            => HasAliveUnits(world, faction) || HasAliveBuildings(faction);
    }
}
