#nullable enable
using System.Collections.Generic;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Effects;

namespace ProjectChimera.Economy
{
    /// <summary>
    /// Story 4.9 — the faction-scoped research order path: start/cancel via the shared <c>OrderApplier</c>
    /// exec-tick command surface (mirrors <see cref="BuildingSystem.TrainUnitCommand"/>/<c>ReviveHeroCommand</c>/
    /// <c>BuyItemCommand</c>), per-tick countdown + completion (mirrors <see cref="BuildingSystem.TickProduction"/>),
    /// permanent cumulative modifier application to every currently alive faction unit on completion, and
    /// future-spawn catch-up via <see cref="ApplyCompletedResearch"/> (wired to <c>EntityWorld.OnUnitDefinitionApplied</c>
    /// by <c>SimulationHost</c>, the same hook the Story 2.6 self-passive installer uses).
    ///
    /// <para>Research is faction-wide with exactly ONE order in progress per faction at a time (no per-building
    /// queue — see the spec's Design Notes). A repeatable research keeps ONE cumulative modifier slot per
    /// <see cref="ResearchDefinition"/> (never one slot per level), reused via <see cref="StackRule.Refresh"/> on
    /// every completion so <see cref="Effects.EffectCaps.MaxModifiersPerEntity"/> is never at risk from research alone.</para>
    /// </summary>
    public class ResearchSystem : ISimSystem
    {
        /// <summary>
        /// Distinctive base for a research's permanent cumulative <see cref="Modifier.Id"/> (Story 4.9), mirroring
        /// <see cref="HeroXpSystem.HeroGrowthModifierId"/>'s scheme — the ASCII-digit-byte convention for
        /// story-numbered constants (story "4.9" → the digits '4','9' as the two high bytes, matching
        /// <c>HeroGrowthModifierId</c>'s <c>0x3133_0000</c> "31 33" ~ 3.13 precedent). <see cref="ItemSystem.ItemModifierIdBase"/>
        /// does NOT share this digit-byte scheme — it uses a distinctive ASCII-letter base ("IT" for "item",
        /// <c>0x4954_0000</c>) plus a per-item ref offset instead. Offset by a faction's research-list index
        /// (<see cref="ResearchModifierId"/>) so each (faction-scoped) research entry keeps one stable, non-colliding
        /// id across its whole cumulative lifetime.
        /// </summary>
        public const int ResearchModifierIdBase = 0x3439_0000; // "49" ~ story 4.9

        /// <summary>The deterministic per-research modifier id for research-list index <paramref name="researchIndex"/>
        /// within a faction's <c>FactionDefinition.Research</c> — one stable slot re-applied via
        /// <see cref="StackRule.Refresh"/> every time that research's cumulative level completes.</summary>
        public static int ResearchModifierId(int researchIndex) => ResearchModifierIdBase + researchIndex;

        private static readonly Dictionary<string, int> EmptyCost = new();

        private readonly BuildingStore  _buildings;
        private readonly ResourceStore  _resources;
        private readonly ResearchStore  _research;
        private readonly ModifierStore  _modifiers;
        private readonly CombatEventQueue? _events;
        // Per-faction definitions indexed by (int)Faction. Slot 0 = Neutral (unused) — mirrors BuildingSystem._factions.
        private readonly FactionDefinition?[] _factions;

        public ResearchSystem(BuildingStore buildings, ResourceStore resources, ResearchStore research,
                              ModifierStore modifiers, CombatEventQueue? events = null,
                              FactionDefinition? p1Faction = null, FactionDefinition? p2Faction = null)
        {
            _buildings = buildings;
            _resources = resources;
            _research  = research;
            _modifiers = modifiers;
            _events    = events;
            _factions  = new FactionDefinition?[FactionRegistry.FACTION_ARRAY_SIZE]; // 9: Neutral + Player1..Player8
            _factions[(int)Faction.Player1] = p1Faction;
            _factions[(int)Faction.Player2] = p2Faction;

            if (p1Faction != null) _research.EnsureCapacity(Faction.Player1, ResearchCount(p1Faction));
            if (p2Faction != null) _research.EnsureCapacity(Faction.Player2, ResearchCount(p2Faction));
        }

        private FactionDefinition? GetFactionDef(Faction faction)
        {
            int idx = (int)faction;
            if (idx < 0 || idx >= _factions.Length) return null;
            return _factions[idx];
        }

        /// <summary>
        /// Story 4.11: presentation READ accessor — <see cref="ProjectChimera.UI.CommandCardSystem"/>'s research
        /// button grid needs the same <see cref="FactionDefinition"/> this system's own gates read internally, to
        /// resolve a selected building's <c>BuildingDefinition.AvailableResearch</c> against
        /// <see cref="FactionDefinition.Research"/>/<see cref="FactionDefinition.IndexOfResearch"/> for its dim
        /// predicate (mirrors <see cref="StartResearchCommand"/>'s own resolution, read-only). No sim-array
        /// mutation; a thin public wrapper over the private <see cref="GetFactionDef"/> this system already uses.
        /// </summary>
        public FactionDefinition? GetFactionDefinition(Faction faction) => GetFactionDef(faction);

        /// <summary>Null-safe count of <paramref name="fdef"/>'s <see cref="FactionDefinition.Research"/> list
        /// (review fix). <see cref="FactionDefinition.GetResearch"/>/<see cref="FactionDefinition.IndexOfResearch"/>
        /// and <see cref="Definitions.ResearchValidator"/> all explicitly tolerate an authored <c>"research": null</c>
        /// faction file (the load succeeds, treated as empty) — every direct <c>fdef.Research.Count</c>/
        /// <c>fdef.Research[i]</c> access in this system must match that same tolerance, never NRE. Treats a null
        /// list as count-0 (no research available).</summary>
        private static int ResearchCount(FactionDefinition fdef) => fdef.Research?.Count ?? 0;

        public void Tick(EntityWorld world, Fixed dt)
        {
            // Mirrors BuildingSystem.RecalculateSupplyCaps' faction loop (BuildingSystem.cs:129) — every playable
            // slot 1..8 (Story 9.2 widened both from the old 1-4 bound so Player5-8 research also counts down).
            for (int f = 1; f < FactionRegistry.FACTION_ARRAY_SIZE; f++)
            {
                Faction faction = (Faction)f;
                int researchIndex = _research.InProgressIndex[f];
                if (researchIndex < 0) continue; // idle

                _research.RemainingTicks[f]--;
                if (_research.RemainingTicks[f] > 0) continue; // still counting down

                CompleteResearch(world, faction, f, researchIndex);
            }
        }

        // ── Start ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Apply a lockstep <see cref="UnitCommand.StartResearch"/> command at exec-tick (Story 4.9). Mirrors
        /// <see cref="BuildingSystem.TrainUnitCommand"/>: validates BUILDING ownership (anti-cheat, SILENT reject —
        /// no event, no position leak), then runs every Start gate in order, spending nothing on any failure. Every
        /// gate past ownership pushes <see cref="CombatEventType.OrderDenied"/> at the building's position on
        /// rejection (a legitimate-but-rejected order, not a crafted anti-cheat one). On a passing gate chain: spends
        /// the resolved next level's cost, then starts the faction's in-progress countdown. Returns true iff a new
        /// order was started.
        /// </summary>
        public bool StartResearchCommand(int buildingId, Faction expectedFaction, int researchIndex)
        {
            if (buildingId < 0 || buildingId >= _buildings.Count) return false;
            if (!_buildings.Alive[buildingId]) return false;
            if (_buildings.FactionOf[buildingId] != expectedFaction) return false; // anti-cheat: own building only (silent)
            // Guard-parity with BuildingSystem.TrainUnit: a still-constructing building cannot start research — a
            // SILENT reject (no OrderDenied event), matching TrainUnit's convention, not the OrderDenied-emitting
            // affordability-style gates below.
            if (_buildings.IsUnderConstruction(buildingId)) return false;

            FixedVec3 buildingPos = _buildings.Position[buildingId];
            int f = (int)expectedFaction;

            var fdef = GetFactionDef(expectedFaction);
            if (fdef == null || researchIndex < 0 || researchIndex >= ResearchCount(fdef))
            {
                Deny(buildingPos, expectedFaction, DenialReason.InvalidTarget);
                return false;
            }
            ResearchDefinition rdef = fdef.Research[researchIndex];

            // Gate (2): the named building must offer this research.
            BuildingDefinition? bdef = fdef.GetBuilding(_buildings.DefinitionId[buildingId]);
            if (bdef == null || System.Array.IndexOf(bdef.AvailableResearch ?? System.Array.Empty<string>(), rdef.Id) < 0)
            {
                Deny(buildingPos, expectedFaction, DenialReason.InvalidTarget);
                return false;
            }

            // Gate (3): no concurrent order for this faction.
            if (_research.InProgressIndex[f] != -1)
            {
                Deny(buildingPos, expectedFaction, DenialReason.QueueFull); // a research is already in progress for this faction
                return false;
            }

            // Gate (4): not already maxed.
            _research.EnsureCapacity(expectedFaction, ResearchCount(fdef));
            int nextLevel = _research.CompletedLevels[f][researchIndex];
            if (nextLevel >= rdef.Levels.Count)
            {
                Deny(buildingPos, expectedFaction, DenialReason.InvalidTarget); // already fully researched
                return false;
            }

            // Gate (5): prerequisites — research-id resolution checked first (the more specific match), else a
            // building prerequisite via TechTreeChecker (mirrors 4.8's UNION-of-building/research-ids resolution).
            if (!PrerequisitesMet(fdef, expectedFaction, f, rdef.Prerequisites))
            {
                Deny(buildingPos, expectedFaction, DenialReason.PrereqMissing);
                return false;
            }

            // Gate (6): affordability of the resolved next level's cost.
            ResearchLevel level = rdef.Levels[nextLevel];
            // A null ladder element (malformed JSON "levels": [ ..., null ]) loads cleanly — ResearchValidator skips
            // null level entries without emitting an error — so guard the sole reachable dereference here and DENY
            // rather than NRE mid-sim (mirrors the null-Research-list tolerance the ctor/gates already carry via
            // ResearchCount). Cancel/Complete only ever touch a level THIS gate already validated non-null.
            if (level == null)
            {
                Deny(buildingPos, expectedFaction, DenialReason.InvalidTarget);
                return false;
            }
            IReadOnlyDictionary<string, int> cost = level.Cost ?? EmptyCost;
            if (!_resources.CanAfford(expectedFaction, cost))
            {
                Deny(buildingPos, expectedFaction, DenialReasons.ForUnaffordableCost(_resources, expectedFaction, cost));
                return false;
            }

            // All gates passed — spend exactly once (exec-tick), then start the countdown.
            _resources.Spend(expectedFaction, cost);
            _research.InProgressIndex[f]   = researchIndex;
            _research.RemainingTicks[f]    = level.TimeTicks;
            _research.StartedAtPosition[f] = buildingPos;
            return true;
        }

        /// <summary>Gate (5): every <see cref="ResearchDefinition.Prerequisites"/> entry must be satisfied. An id that
        /// resolves via <see cref="FactionDefinition.IndexOfResearch"/> is treated as a research prerequisite (that
        /// faction's completed-level count for it must be &gt; 0); otherwise it is treated as a building prerequisite
        /// via <see cref="TechTreeChecker.AreMet"/> (mirrors 4.8's UNION-of-building/research-ids resolution;
        /// research-id resolution checked first since <c>IndexOfResearch</c> is the more specific match).</summary>
        private bool PrerequisitesMet(FactionDefinition fdef, Faction faction, int f, string[]? prereqs)
        {
            if (prereqs == null || prereqs.Length == 0) return true;
            foreach (string id in prereqs)
            {
                int prereqResearchIdx = fdef.IndexOfResearch(id);
                if (prereqResearchIdx >= 0)
                {
                    _research.EnsureCapacity(faction, ResearchCount(fdef));
                    if (_research.CompletedLevels[f][prereqResearchIdx] <= 0) return false;
                }
                else
                {
                    if (!TechTreeChecker.AreMet(_buildings, faction, new[] { id })) return false;
                }
            }
            return true;
        }

        // ── Cancel ────────────────────────────────────────────────────────────

        /// <summary>
        /// Apply a lockstep <see cref="UnitCommand.CancelResearch"/> command at exec-tick (Story 4.9). Mirrors
        /// <see cref="StartResearchCommand"/>'s ownership guard (any owned building may cancel — research state is
        /// faction-wide, not building-scoped). A no-op (false, no event) when the faction has no order in progress.
        /// Refund = <c>CancelRefundFraction × currentLevelCost</c> per resource key, credited via
        /// <see cref="ResourceStore.Add"/>; clears the in-progress state. Returns true iff an order was cancelled.
        /// </summary>
        public bool CancelResearchCommand(int buildingId, Faction expectedFaction)
        {
            if (buildingId < 0 || buildingId >= _buildings.Count) return false;
            if (!_buildings.Alive[buildingId]) return false;
            if (_buildings.FactionOf[buildingId] != expectedFaction) return false; // anti-cheat: own building only (silent)

            int f = (int)expectedFaction;
            int researchIndex = _research.InProgressIndex[f];
            if (researchIndex < 0) return false; // idle — silent no-op (no refund, no event, no state change)

            var fdef = GetFactionDef(expectedFaction);
            if (fdef != null && researchIndex < ResearchCount(fdef))
            {
                ResearchDefinition rdef = fdef.Research[researchIndex];
                int levelIdx = _research.CompletedLevels[f][researchIndex];
                if (levelIdx < rdef.Levels.Count)
                {
                    ResearchLevel level = rdef.Levels[levelIdx];
                    IReadOnlyDictionary<string, int>? cost = level.Cost;
                    if (cost != null && cost.Count > 0)
                    {
                        Fixed fraction = Fixed.FromFloat(rdef.CancelRefundFraction);
                        var refund = new Dictionary<string, int>();
                        foreach (var (key, amount) in cost)
                            refund[key] = (fraction * Fixed.FromInt(amount)).ToInt();
                        _resources.Add(expectedFaction, refund);
                    }
                }
            }

            _research.InProgressIndex[f] = -1;
            _research.RemainingTicks[f]  = 0;
            return true;
        }

        // ── Completion ────────────────────────────────────────────────────────

        private void CompleteResearch(EntityWorld world, Faction faction, int f, int researchIndex)
        {
            var fdef = GetFactionDef(faction);
            if (fdef == null || researchIndex >= ResearchCount(fdef))
            {
                // Defensive: the faction def changed/vanished mid-order (not a supported runtime path). Clear the
                // order deterministically rather than throw or apply a stale modifier.
                _research.InProgressIndex[f] = -1;
                _research.RemainingTicks[f]  = 0;
                return;
            }

            ResearchDefinition rdef = fdef.Research[researchIndex];
            _research.EnsureCapacity(faction, ResearchCount(fdef));
            int levelIdx = _research.CompletedLevels[f][researchIndex];
            if (levelIdx >= rdef.Levels.Count)
            {
                _research.InProgressIndex[f] = -1;
                _research.RemainingTicks[f]  = 0;
                return;
            }
            ResearchLevel level = rdef.Levels[levelIdx];

            // Increment the completed-level count and accumulate this level's delta into the running cumulative
            // Fixed total — the single load-boundary quantization this story owns (each INDIVIDUAL level's value
            // already range-validated in [-32768, 32768) by 4.8's ResearchValidator). The RUNNING SUM across many
            // completed levels of a repeatable ladder is NOT individually range-validated, so it is saturated via
            // SaturatingAdd below rather than wrapping Fixed's underlying int32 (review fix).
            _research.CompletedLevels[f][researchIndex] = levelIdx + 1;
            ResearchModifierDelta? md = level.ModifierDelta;
            if (md != null)
            {
                _research.CumulativeMaxHealthDelta[f][researchIndex]    = SaturatingAdd(_research.CumulativeMaxHealthDelta[f][researchIndex],    Fixed.FromFloat(md.MaxHealthDelta));
                _research.CumulativeAttackDamageDelta[f][researchIndex] = SaturatingAdd(_research.CumulativeAttackDamageDelta[f][researchIndex], Fixed.FromFloat(md.AttackDamageDelta));
                _research.CumulativeMoveSpeedDelta[f][researchIndex]    = SaturatingAdd(_research.CumulativeMoveSpeedDelta[f][researchIndex],    Fixed.FromFloat(md.MoveSpeedDelta));
                _research.CumulativeArmorDelta[f][researchIndex]        = SaturatingAdd(_research.CumulativeArmorDelta[f][researchIndex],        Fixed.FromFloat(md.ArmorDelta));
            }

            // Apply to every currently alive unit of this faction — mirrors SupplySystem.Tick's ascending-id loop.
            int hwm = world.HighWaterMark;
            for (int id = 0; id < hwm; id++)
            {
                if ((world.Flags[id] & EntityFlags.Alive) == 0) continue;
                if (world.FactionOf[id] != faction) continue;
                // DW-85: living-army completion must NOT burst-heal — preserve current Health across the
                // remove-then-reapply while the MaxHealth ceiling grows.
                ApplyCumulativeModifier(world, id, faction, f, researchIndex, preserveCurrentHealth: true);
            }

            _events?.Push(CombatEventType.ResearchComplete, _research.StartedAtPosition[f], faction); // Story 11.4: stamp the actor faction for the local-only completion cue

            // Idle — a subsequent Start begins the NEXT level with that level's own cost/time.
            _research.InProgressIndex[f] = -1;
            _research.RemainingTicks[f]  = 0;
        }

        /// <summary>
        /// Install/refresh entity <paramref name="id"/>'s ONE cumulative modifier slot for faction-index
        /// <paramref name="f"/>'s research at <paramref name="researchIndex"/> to the store's CURRENT cumulative
        /// deltas. <see cref="ModifierStore.Apply"/>'s <see cref="StackRule.Refresh"/> path only resets a same-id
        /// instance's duration — it does NOT update an already-installed instance's stat deltas (by design, for a
        /// timed buff whose magnitude never changes on refresh). A research's cumulative delta DOES grow on every
        /// completion, so this removes any existing same-id instance first (a no-op the first time — nothing to
        /// remove) and re-applies fresh: the combination is what makes "one cumulative slot per research,
        /// StackRule.Refresh" carry the GROWN magnitude (never <see cref="StackRule.Stack"/>-ed per level, per the
        /// spec's Design Notes).
        ///
        /// <para><b>DW-85 heal suppression.</b> <see cref="ModifierStore.ApplyStatDeltas"/>' Decision-#3 heals current
        /// Health by any positive MaxHealth delta on APPLY. For a repeatable +MaxHealth research this would burst-heal
        /// every living faction unit by the FULL cumulative bonus on every completion (and the remove step first clamps
        /// Health DOWN by the OLD cumulative), turning the research into a repeatable army-heal. When
        /// <paramref name="preserveCurrentHealth"/> is true (the living-army completion path), this snapshots
        /// <c>world.Health[id]</c> before the remove+reapply and restores it afterward — re-clamped into the freshly
        /// raised <see cref="EntityWorld.EffectiveMaxHealth"/> — so the ceiling grows but current Health stays invariant.
        /// The future-spawn catch-up path (<see cref="ApplyCompletedResearch"/>) passes false so a newly trained unit
        /// still spawns at full upgraded HP. <see cref="ModifierStore"/>'s shared heal-on-apply semantics are untouched.</para>
        /// </summary>
        private void ApplyCumulativeModifier(EntityWorld world, int id, Faction faction, int f, int researchIndex, bool preserveCurrentHealth)
        {
            int modId = ResearchModifierId(researchIndex);
            Fixed healthBefore = world.Health[id];    // DW-85: snapshot to suppress the remove-then-reapply burst-heal
            _modifiers.RemoveByModifierId(id, modId); // revert the stale (smaller) delta, if any
            _modifiers.Apply(id, BuildCumulativeModifier(f, researchIndex), casterId: id, casterFaction: faction);
            // living-army completion only; future-spawn catch-up keeps its heal. IsAlive re-checked because this
            // writes a per-entity SoA slot after Apply/Remove — mirrors ModifierStore's post-effect IsAlive guards
            // (defensive against a future lethal research period/expire effect that could recycle the host mid-apply).
            if (preserveCurrentHealth && world.IsAlive(id))
                world.Health[id] = Fixed.Clamp(healthBefore, Fixed.Zero, world.EffectiveMaxHealth[id]);
        }

        /// <summary>
        /// Build the permanent cumulative <see cref="Modifier"/> for faction-index <paramref name="f"/>'s research at
        /// <paramref name="researchIndex"/> from the store's current cumulative deltas — <c>DurationTicks: -1</c>
        /// (permanent), <see cref="StackRule.Refresh"/>, <c>MaxStacks: 1</c> (ONE cumulative slot per research, never
        /// stacked per level). Shared by <see cref="CompleteResearch"/> (apply to every currently alive unit) and
        /// <see cref="ApplyCompletedResearch"/> (future-spawn catch-up), both via <see cref="ApplyCumulativeModifier"/>.
        /// </summary>
        private Modifier BuildCumulativeModifier(int f, int researchIndex) => new Modifier(
            ResearchModifierId(researchIndex),
            durationTicks: -1,
            StackRule.Refresh,
            maxStacks: 1,
            maxHealthDelta:    _research.CumulativeMaxHealthDelta[f][researchIndex],
            attackDamageDelta: _research.CumulativeAttackDamageDelta[f][researchIndex],
            moveSpeedDelta:    _research.CumulativeMoveSpeedDelta[f][researchIndex],
            status: StatusFlags.None,
            periodEffect: null,
            periodTicks: 0,
            armorDelta: _research.CumulativeArmorDelta[f][researchIndex]);

        // ── Future-spawn catch-up ────────────────────────────────────────────────

        /// <summary>
        /// Story 4.9 future-spawn catch-up: called from <c>EntityWorld.OnUnitDefinitionApplied</c> (wired once at
        /// <c>SimulationHost</c> construction, mirroring the Story 2.6 self-passive installer). For the spawned unit's
        /// faction, re-applies every research whose completed-level count &gt; 0's stored cumulative
        /// <see cref="Modifier"/> to ONLY the newly spawned unit — the only hook that fires for every future spawn
        /// regardless of spawn path (training, scenario placement, hero respawn, editor restore/placement). Never
        /// re-triggers completion or spends resources.
        /// </summary>
        public void ApplyCompletedResearch(EntityWorld world, int id)
        {
            if (!world.IsAlive(id)) return;
            Faction faction = world.FactionOf[id];
            var fdef = GetFactionDef(faction);
            if (fdef == null) return;

            int f = (int)faction;
            _research.EnsureCapacity(faction, ResearchCount(fdef));
            for (int ri = 0; ri < ResearchCount(fdef); ri++)
            {
                if (_research.CompletedLevels[f][ri] <= 0) continue;
                // Future-spawn catch-up KEEPS the heal (preserveCurrentHealth: false) so a freshly trained unit
                // spawns at full upgraded HP — DW-85 suppresses the heal for the living-army path only.
                ApplyCumulativeModifier(world, id, faction, f, ri, preserveCurrentHealth: false);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Add <paramref name="delta"/> to <paramref name="current"/> using a 64-bit intermediate (so the add itself
        /// can never silently wrap Fixed's underlying int32), then SATURATE to [<see cref="Fixed.MinValue"/>,
        /// <see cref="Fixed.MaxValue"/>] — the full representable 16.16 range, i.e. the same ±<see cref="ResourceCostValidator.Range"/>
        /// (32768) ceiling <see cref="Definitions.ResearchValidator"/> range-checks each INDIVIDUAL level's
        /// <see cref="ResearchModifierDelta"/> against (review fix). A multi-level repeatable ladder whose
        /// per-level deltas each individually pass validation can still sum past that ceiling, so the RUNNING
        /// cumulative total needs its own bound. A saturating clamp, never a throw — this runs mid-match sim code
        /// and must stay deterministic.
        /// </summary>
        private static Fixed SaturatingAdd(Fixed current, Fixed delta)
        {
            long sum = (long)current.Raw + delta.Raw;
            if (sum > int.MaxValue) return Fixed.MaxValue;
            if (sum < int.MinValue) return Fixed.MinValue;
            return Fixed.FromRaw((int)sum);
        }

        // Story 11.4 (FR-74): guard-sourced denial — the rejecting gate stamps the SPECIFIC reason it computed plus the
        // acting faction; the reactive UI renders it, never re-derives it. `_events` null (golden/replay) → silent.
        private void Deny(FixedVec3 pos, Faction faction, DenialReason reason) => _events?.PushDenied(pos, faction, reason);
    }
}
