#nullable enable
using System.Collections.Generic;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;   // ILogSink — the AR-4 injected diagnostic seam (DW-83)
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
        // DW-83: the injected AR-4 diagnostic seam (never a static ambient sink). Null (goldens/tests/headless) ⇒
        // exactly the pre-DW-83 behavior. Diagnostics only — never read by a sim branch, never folded.
        private readonly ILogSink? _log;
        // Per-faction definitions indexed by (int)Faction. Slot 0 = Neutral (unused) — mirrors BuildingSystem._factions.
        private readonly FactionDefinition?[] _factions;

        // ── DW-624: future-spawn catch-up refusal AGGREGATION ────────────────────────────────────────────────
        // Per-match tallies of catch-up installs the target's full ring REFUSED, outer = (int)Faction, inner =
        // research index (lazily grown, mirroring ResearchStore.EnsureCapacity's never-shrink shape). The
        // completion path can afford to warn inline because it fires ONCE per completed level; ApplyCompletedResearch
        // cannot — it fires once PER SPAWN (training, scenario placement, hero respawn, editor restore), so a
        // 200-unit scenario load with full rings would emit 200 lines. These accumulate silently instead and
        // <see cref="FlushSpawnCatchUpDiagnostics"/> emits ONE line per faction at the match boundary.
        //
        // UNFOLDED diagnostics, exactly ModifierStore.RefusedInstallCount's posture: never hashed into SimChecksum,
        // never read by any sim branch, so a run that reads them and a run that ignores them are byte-identical.
        private readonly int[][] _spawnRefusalsByResearch;
        // Count of DISTINCT spawns that lost at least one research bonus (a single spawn can refuse several
        // researches, so summing the per-research row would over-count units).
        private readonly int[] _spawnRefusedSpawnCount;

        public ResearchSystem(BuildingStore buildings, ResourceStore resources, ResearchStore research,
                              ModifierStore modifiers, CombatEventQueue? events = null,
                              FactionDefinition? p1Faction = null, FactionDefinition? p2Faction = null,
                              ILogSink? log = null)
        {
            _buildings = buildings;
            _resources = resources;
            _research  = research;
            _modifiers = modifiers;
            _events    = events;
            _log       = log;
            _factions  = new FactionDefinition?[FactionRegistry.FACTION_ARRAY_SIZE]; // 9: Neutral + Player1..Player8
            _factions[(int)Faction.Player1] = p1Faction;
            _factions[(int)Faction.Player2] = p2Faction;

            // DW-624: same outer shape as _factions / ResearchStore's per-faction arrays; inner rows start empty and
            // grow on first use (EnsureRefusalCapacity), so a def-less slot allocates nothing.
            _spawnRefusalsByResearch = new int[FactionRegistry.FACTION_ARRAY_SIZE][];
            _spawnRefusedSpawnCount  = new int[FactionRegistry.FACTION_ARRAY_SIZE];
            for (int i = 0; i < _spawnRefusalsByResearch.Length; i++)
                _spawnRefusalsByResearch[i] = System.Array.Empty<int>();

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

        /// <summary>
        /// DW-386 — override the faction definition for a specific faction slot at runtime, the exact twin of
        /// <c>BuildingSystem.SetFactionDef</c> and called from the SAME place (<c>ScenarioApplier</c>'s player-slot
        /// loop). The <see cref="_factions"/> array has been sized <c>FACTION_ARRAY_SIZE</c> (9) since Story 9.2, but
        /// the constructor only ever populates Player1/Player2 — so before this, a Player3..Player8 researcher
        /// resolved a NULL faction def and had NO research options at all, while the same slot's BUILDINGS resolved
        /// correctly through <c>BuildingSystem.SetFactionDef</c>. That asymmetry is the defect; one seam per system.
        ///
        /// <para>Deliberately ONLY the array assignment — no <see cref="ResearchStore.EnsureCapacity"/> call.
        /// Capacity is grown lazily by every consumer that needs it (<see cref="StartResearchCommand"/>,
        /// <see cref="ApplyCompletedResearch"/>, the completion path), and <c>ResearchStore</c>'s per-faction row
        /// LENGTH is folded into <c>SimChecksum</c> — so growing it eagerly here would move the fold for any slot
        /// whose def carries research, for no behavioural gain. Mirrors <c>BuildingSystem.SetFactionDef</c> exactly,
        /// which is likewise assignment-only. Out-of-range faction is a silent no-op.</para>
        /// </summary>
        public void SetFactionDef(Faction faction, FactionDefinition def)
        {
            int idx = (int)faction;
            if (idx >= 0 && idx < _factions.Length)
                _factions[idx] = def;
        }

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

            // DW-623: snapshot the four running cumulative totals BEFORE this level folds into them, so a
            // fully-refused completion can be rolled back EXACTLY. Subtracting the level's delta back off would NOT
            // be an inverse — SaturatingAdd clamps at the Fixed ceiling, and a clamped add is not invertible.
            Fixed prevMaxHealthDelta    = _research.CumulativeMaxHealthDelta[f][researchIndex];
            Fixed prevAttackDamageDelta = _research.CumulativeAttackDamageDelta[f][researchIndex];
            Fixed prevMoveSpeedDelta    = _research.CumulativeMoveSpeedDelta[f][researchIndex];
            Fixed prevArmorDelta        = _research.CumulativeArmorDelta[f][researchIndex];

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
            int eligibleUnits = 0; // DW-623: living faction units this completion actually tried to buff
            int refusedUnits = 0;  // DW-83: units whose full modifier ring dropped this completion's permanent buff
            for (int id = 0; id < hwm; id++)
            {
                if ((world.Flags[id] & EntityFlags.Alive) == 0) continue;
                if (world.FactionOf[id] != faction) continue;
                eligibleUnits++;
                // DW-85: living-army completion must NOT burst-heal — preserve current Health across the
                // remove-then-reapply while the MaxHealth ceiling grows.
                if (!ApplyCumulativeModifier(world, id, faction, f, researchIndex, preserveCurrentHealth: true))
                    refusedUnits++;
            }

            // DW-623 (owner ruling 2026-08-05, the design decision DW-83's diagnostic-only bundle deliberately left
            // open): "refund the spend when zero units received the modifier". The cost is spent at Start and the
            // level is banked BEFORE this loop, so a fully-starved army — every living unit already at the
            // EffectCaps.MaxModifiersPerEntity ceiling — used to leave the faction poorer with no effect and no
            // refund. When EVERY eligible unit refused, the completion is VOIDED instead: the level is un-banked,
            // the cumulative deltas roll back to their pre-completion snapshot, and the level's cost is credited
            // back. The faction returns to idle either way, so the player can free ring slots and re-start it.
            //
            // Two guards keep the ruling from voiding completions that DID deliver something:
            //   • eligibleUnits > 0 — an EMPTY army is not a refusal. Nothing was dropped, and the banked level is
            //     genuinely received by every FUTURE spawn through ApplyCompletedResearch's catch-up (which reads
            //     CompletedLevels > 0). Refunding here would make research impossible for an army-less faction.
            //   • the cumulative modifier must carry a payload — a research with no (or a net-zero) ResearchModifierDelta
            //     is a TECH GATE, not an upgrade: its whole value is the banked level that PrerequisitesMet reads.
            //     A ring refusal costs such a research nothing, so voiding it would break tech unlocks for a starved
            //     army — a worse defect than the one being fixed.
            bool nobodyReceivedIt = eligibleUnits > 0 && refusedUnits == eligibleUnits
                                    && CumulativeCarriesPayload(f, researchIndex);

            if (nobodyReceivedIt)
            {
                _research.CompletedLevels[f][researchIndex]             = levelIdx;
                _research.CumulativeMaxHealthDelta[f][researchIndex]    = prevMaxHealthDelta;
                _research.CumulativeAttackDamageDelta[f][researchIndex] = prevAttackDamageDelta;
                _research.CumulativeMoveSpeedDelta[f][researchIndex]    = prevMoveSpeedDelta;
                _research.CumulativeArmorDelta[f][researchIndex]        = prevArmorDelta;
                // Refund exactly what StartResearchCommand spent: the same level's Cost dictionary, re-resolved from
                // the same levelIdx (nothing can move CompletedLevels between Start and Complete — one order per
                // faction). Mirrors BuildingSystem.BuyItemCommand's "refund atomically → net zero" + Deny shape.
                _resources.Add(faction, level.Cost ?? EmptyCost);
                // No living unit could have LOST a prior instance of this research's modifier here: a refusal only
                // ever happens on a FRESH install into a full ring, and ApplyCumulativeModifier's RemoveByModifierId
                // would have freed a slot for any unit that already carried it (making its re-apply succeed). So
                // "every eligible unit refused" implies none of them held it — the rollback leaves no residue.
                _log?.Warn(RefusalWarn(rdef, levelIdx, faction, refusedUnits) +
                           "That was EVERY eligible living unit, so the completion is VOIDED: the level is NOT banked " +
                           "and its cost has been refunded (DW-623). Raise the cap or reduce the per-unit modifier " +
                           "load before re-starting it.");
                // Presentation: a voided completion is a rejected outcome, not a ResearchComplete — pushing the
                // success cue would play "research complete" for a level the faction never gained. InventoryFull is
                // the established modifier-cap denial reason (ItemSystem's modifier-capped pickup reject uses it).
                Deny(_research.StartedAtPosition[f], faction, DenialReason.InventoryFull);
            }
            else
            {
                // DW-83: ONE aggregated line per completion (never one per unit — a completion can sweep a 200-unit
                // army). Research is the producer the ledger flagged: each completed research owns its OWN permanent
                // modifier id, so a unit already carrying items + a self-passive + earlier researches can silently lose
                // an EARNED, PAID-FOR upgrade. Named by research id + level so a designer can act on it. The per-unit
                // spawn-catch-up path (ApplyCompletedResearch) still must NOT log inline — it fires per spawn — so
                // DW-624 gives it a per-MATCH tally flushed as one line by FlushSpawnCatchUpDiagnostics instead.
                if (refusedUnits > 0)
                    _log?.Warn(RefusalWarn(rdef, levelIdx, faction, refusedUnits) +
                               "Those units keep the research's cost but not its effect — raise the cap or reduce the " +
                               "per-unit modifier load (DW-83).");

                _events?.Push(CombatEventType.ResearchComplete, _research.StartedAtPosition[f], faction); // Story 11.4: stamp the actor faction for the local-only completion cue
            }

            // Idle — a subsequent Start begins the NEXT level with that level's own cost/time (or, on a voided
            // completion, re-attempts THIS level).
            _research.InProgressIndex[f] = -1;
            _research.RemainingTicks[f]  = 0;
        }

        /// <summary>Shared head of both ring-full completion warns (DW-83's partial-refusal line and DW-623's voided
        /// line) — names the research, the level, the faction, and how many living units dropped the bonus.</summary>
        private static string RefusalWarn(ResearchDefinition rdef, int levelIdx, Faction faction, int refusedUnits) =>
            $"[ResearchSystem] '{rdef.Id}' level {levelIdx + 1} ({faction}) completed, but its permanent " +
            $"bonus was DROPPED on {refusedUnits} living unit(s): their {EffectCaps.MaxModifiersPerEntity}-slot " +
            "modifier ring was already full (items + hero growth + self-passives + earlier researches). ";

        /// <summary>
        /// DW-623: true iff faction-index <paramref name="f"/>'s research at <paramref name="researchIndex"/> has a
        /// non-zero cumulative stat payload — i.e. the permanent modifier this completion installs actually delivers
        /// something, so a unit that refuses it genuinely loses value. The four <c>Cumulative*Delta</c> totals ARE the
        /// whole payload: <see cref="BuildCumulativeModifier"/> hard-codes <see cref="StatusFlags.None"/>, a null
        /// period effect and period 0. A research with no authored <see cref="ResearchModifierDelta"/> (or a ladder
        /// that nets back to zero) is a prerequisite gate whose value is the banked level itself — never voided.
        /// Compared on <see cref="Fixed.Raw"/> so this is an exact integer test, never a float epsilon.
        ///
        /// <para>DW-678 states the SAME partition one step later, on the built descriptor
        /// (<see cref="Modifier.HasNoEffect"/>) rather than on the stored totals, and uses it to skip the install
        /// outright. The two agree by construction — <see cref="BoundedDelta"/> only clamps a magnitude, so it can
        /// neither zero a non-zero total nor un-zero a zero one — and this one stays on the raw totals because it runs
        /// once per completion for a REFUND decision and must not allocate a descriptor to answer.</para>
        /// </summary>
        private bool CumulativeCarriesPayload(int f, int researchIndex) =>
            _research.CumulativeMaxHealthDelta[f][researchIndex].Raw    != 0 ||
            _research.CumulativeAttackDamageDelta[f][researchIndex].Raw != 0 ||
            _research.CumulativeMoveSpeedDelta[f][researchIndex].Raw    != 0 ||
            _research.CumulativeArmorDelta[f][researchIndex].Raw        != 0;

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
        ///
        /// <para><b>DW-83 refusal reporting.</b> Returns <c>false</c> iff the re-apply was REFUSED because the unit's
        /// per-entity modifier ring was already full — i.e. this research's earned permanent bonus was silently
        /// dropped for that unit. Detected by the delta in <see cref="ModifierStore.RefusedInstallCount"/> across the
        /// call rather than by <c>Apply</c>'s bare bool, which is ALSO false for a dead/stale target (a normal race,
        /// and reachable here: the remove step can collapse a MaxHealth ceiling and kill the host). Diagnostics only —
        /// no caller branches sim state on it.</para>
        ///
        /// <para><b>DW-678 empty-modifier skip.</b> A pure TECH GATE — a research whose levels author no
        /// <see cref="ResearchLevel.ModifierDelta"/> at all, or a ladder whose deltas net back to zero — builds a
        /// modifier with nothing in it (<see cref="Modifier.HasNoEffect"/>). Installing that used to burn one of the
        /// <see cref="EffectCaps.MaxModifiersPerEntity"/> ring slots on EVERY living faction unit and every future
        /// spawn for exactly zero effect, actively worsening the starvation DW-83/DW-623/DW-625 are about. It is
        /// skipped instead. The REMOVE still runs unconditionally and must stay above the guard: a ladder that nets
        /// back to zero (say +50 then −50 max health) has a live, non-zero instance from the earlier level that has to
        /// come off, and skipping the remove as well would strand it forever. A tech gate's value is the banked
        /// <see cref="ResearchStore.CompletedLevels"/> entry <see cref="PrerequisitesMet"/> reads — never the modifier
        /// — so nothing is lost, and a skipped install can no longer be counted as a ring REFUSAL (DW-83's warn,
        /// DW-623's void, DW-624's tally), which is correct: there was no bonus to drop.</para>
        /// </summary>
        private bool ApplyCumulativeModifier(EntityWorld world, int id, Faction faction, int f, int researchIndex, bool preserveCurrentHealth)
        {
            int modId = ResearchModifierId(researchIndex);
            Fixed healthBefore = world.Health[id];    // DW-85: snapshot to suppress the remove-then-reapply burst-heal
            int refusedBefore = _modifiers.RefusedInstallCount; // DW-83: exact ring-full attribution (see the doc above)
            Modifier cumulative = BuildCumulativeModifier(f, researchIndex);
            _modifiers.RemoveByModifierId(id, modId); // revert the stale (smaller) delta, if any
            // DW-678: install only what actually carries a payload — see the empty-modifier-skip note above.
            if (!cumulative.HasNoEffect())
                _modifiers.Apply(id, cumulative, casterId: id, casterFaction: faction);
            // living-army completion only; future-spawn catch-up keeps its heal. IsAlive re-checked because this
            // writes a per-entity SoA slot after Apply/Remove — mirrors ModifierStore's post-effect IsAlive guards
            // (defensive against a future lethal research period/expire effect that could recycle the host mid-apply).
            if (preserveCurrentHealth && world.IsAlive(id))
                world.Health[id] = Fixed.Clamp(healthBefore, Fixed.Zero, world.EffectiveMaxHealth[id]);
            return _modifiers.RefusedInstallCount == refusedBefore;
        }

        /// <summary>
        /// Build the permanent cumulative <see cref="Modifier"/> for faction-index <paramref name="f"/>'s research at
        /// <paramref name="researchIndex"/> from the store's current cumulative deltas — <c>DurationTicks: -1</c>
        /// (permanent), <see cref="StackRule.Refresh"/>, <c>MaxStacks: 1</c> (ONE cumulative slot per research, never
        /// stacked per level). Shared by <see cref="CompleteResearch"/> (apply to every currently alive unit) and
        /// <see cref="ApplyCompletedResearch"/> (future-spawn catch-up), both via <see cref="ApplyCumulativeModifier"/>.
        ///
        /// <para><b>DW-650</b> — every delta goes out through <see cref="BoundedDelta"/> so the minted descriptor always
        /// satisfies DW-488's <see cref="Modifier.CheckAuthoringBounds"/>. Research is the one Modifier minter whose
        /// magnitude is NOT authored: it is a running sum <see cref="SaturatingAdd"/> accumulates across a repeatable
        /// ladder, so no load-time content gate can bound it — the bound has to be applied where the modifier is built.
        /// </para>
        /// </summary>
        private Modifier BuildCumulativeModifier(int f, int researchIndex) => new Modifier(
            ResearchModifierId(researchIndex),
            durationTicks: -1,
            StackRule.Refresh,
            maxStacks: 1,
            maxHealthDelta:    BoundedDelta(_research.CumulativeMaxHealthDelta[f][researchIndex]),
            attackDamageDelta: BoundedDelta(_research.CumulativeAttackDamageDelta[f][researchIndex]),
            moveSpeedDelta:    BoundedDelta(_research.CumulativeMoveSpeedDelta[f][researchIndex]),
            status: StatusFlags.None,
            periodEffect: null,
            periodTicks: 0,
            armorDelta:        BoundedDelta(_research.CumulativeArmorDelta[f][researchIndex]));

        /// <summary>
        /// DW-650 — clamp one cumulative research total into DW-488's per-modifier authoring bound
        /// (±<see cref="Modifier.MaxStatDeltaTotalRaw"/>, ≈4096 stat units) as the modifier is built.
        ///
        /// <para><b>Why the clamp is here and not on the stored total.</b> <see cref="SaturatingAdd"/> deliberately
        /// saturates the BANKED cumulative at the full 16.16 <see cref="Fixed"/> range so the DW-623 void/rollback
        /// snapshot stays exact and the persisted/checksummed value keeps its established meaning. But a research's
        /// modifier feeds the same 8-slot <c>ModifierSystem.AccumulateBonus</c> int accumulator every other minter does,
        /// and <see cref="Effects.EffectCaps.MaxModifiersPerEntity"/> researches each contributing a Fixed-ceiling delta
        /// sum to ~8 × <c>int.MaxValue</c> — which WRAPS NEGATIVE, and DW-28's saturating <c>Base + Σbonus</c> read
        /// cannot recover an already-wrapped value (its own docstring says so). Clamping the CONTRIBUTION keeps the
        /// worst case at <c>MaxModifiersPerEntity × MaxStatDeltaTotalRaw</c> ≤ <c>int.MaxValue</c> — un-wrappable by
        /// construction, exactly as DW-488 intended.</para>
        ///
        /// <para>Deterministic and <see cref="Fixed"/>-only (pure raw-int compare, no float, no branch on wall-clock).
        /// Inert for anything a designer would actually ship: it engages only past ≈4096 accumulated stat units, and no
        /// shipped research authors a modifier delta at all.</para>
        /// </summary>
        private static Fixed BoundedDelta(Fixed cumulative)
        {
            if (cumulative.Raw >  Modifier.MaxStatDeltaTotalRaw) return Fixed.FromRaw( Modifier.MaxStatDeltaTotalRaw);
            if (cumulative.Raw < -Modifier.MaxStatDeltaTotalRaw) return Fixed.FromRaw(-Modifier.MaxStatDeltaTotalRaw);
            return cumulative;
        }

        // ── Future-spawn catch-up ────────────────────────────────────────────────

        /// <summary>
        /// Story 4.9 future-spawn catch-up: called from <c>EntityWorld.OnUnitDefinitionApplied</c> (wired once at
        /// <c>SimulationHost</c> construction, mirroring the Story 2.6 self-passive installer). For the spawned unit's
        /// faction, re-applies every research whose completed-level count &gt; 0's stored cumulative
        /// <see cref="Modifier"/> to ONLY the newly spawned unit — the only hook that fires for every future spawn
        /// regardless of spawn path (training, scenario placement, hero respawn, editor restore/placement). Never
        /// re-triggers completion or spends resources.
        ///
        /// <para><b>DW-624 refusal aggregation.</b> A spawn whose modifier ring is already full silently loses a
        /// banked, ALREADY-PAID-FOR research bonus here, and unlike the completion path there is nothing to refund
        /// (the level was banked and paid for ticks or minutes earlier — see DW-679). This path deliberately does
        /// NOT log inline: it fires once per spawn, so an aggregate line here would be per-spawn spam (a 200-unit
        /// scenario load with full rings = 200 lines). Each refusal is instead TALLIED per faction + per research
        /// and reported as ONE line by <see cref="FlushSpawnCatchUpDiagnostics"/> at the match boundary, which is
        /// what gives the designer research-NAME attribution that <see cref="ModifierStore"/>'s own throttled warn
        /// (a bare <c>0x3439_00xx</c> modifier id) cannot. Tally only — no sim state, no branch, nothing folded.</para>
        /// </summary>
        public void ApplyCompletedResearch(EntityWorld world, int id)
        {
            if (!world.IsAlive(id)) return;
            Faction faction = world.FactionOf[id];
            var fdef = GetFactionDef(faction);
            if (fdef == null) return;

            int f = (int)faction;
            int researchCount = ResearchCount(fdef);
            _research.EnsureCapacity(faction, researchCount);
            EnsureRefusalCapacity(f, researchCount);      // DW-624: same lazy growth as the store's inner arrays
            bool anyRefused = false;                      // DW-624: this SPAWN lost at least one research bonus
            for (int ri = 0; ri < researchCount; ri++)
            {
                if (_research.CompletedLevels[f][ri] <= 0) continue;
                // Future-spawn catch-up KEEPS the heal (preserveCurrentHealth: false) so a freshly trained unit
                // spawns at full upgraded HP — DW-85 suppresses the heal for the living-army path only.
                if (!ApplyCumulativeModifier(world, id, faction, f, ri, preserveCurrentHealth: false))
                {
                    _spawnRefusalsByResearch[f][ri]++;    // DW-624: accumulate, never log per spawn
                    anyRefused = true;
                }
            }
            if (anyRefused) _spawnRefusedSpawnCount[f]++;
        }

        // ── DW-624: per-match aggregation of the catch-up refusals ───────────────────────────────

        /// <summary>
        /// DW-624 — report and ZERO the per-match future-spawn catch-up refusal tally: at most ONE warn line per
        /// faction, naming every research whose banked, already-paid-for permanent bonus a full modifier ring
        /// dropped on a spawn, and how many spawns each one hit. Emits nothing at all for a match with no refusals
        /// (the overwhelmingly common case), so it is safe to call unconditionally at every match boundary.
        ///
        /// <para>Called by <c>SimulationHost.ClearForReset</c> (the host's per-match teardown, reached by every
        /// Play→Edit toggle / re-launch) and exposed on the host as <c>FlushMatchDiagnostics()</c> so a bootstrap
        /// can also flush explicitly at end-of-match or after a bulk scenario load. Idempotent: a second call with
        /// nothing accumulated logs nothing and returns 0.</para>
        ///
        /// <para>Diagnostics only — reads and clears unfolded counters, touches no sim array and pushes no event,
        /// so calling it (or not) is byte-identical for <c>SimChecksum</c>. Faction slots are walked in ascending
        /// order and each faction's researches in ascending research index, so the emitted text is deterministic.
        /// Returns the total number of refusals reported (0 when clean) — the seam tests assert on without a sink.</para>
        /// </summary>
        public int FlushSpawnCatchUpDiagnostics()
        {
            int flushedTotal = 0;
            for (int f = 1; f < _spawnRefusedSpawnCount.Length; f++) // slot 0 = Neutral (never carries a faction def)
            {
                int[] row = _spawnRefusalsByResearch[f];
                int spawns = _spawnRefusedSpawnCount[f];
                int total = 0;
                for (int ri = 0; ri < row.Length; ri++) total += row[ri];

                if (total > 0)
                {
                    // Build the line only when a sink is actually wired — golden/headless/Tier-1 hosts pass none,
                    // and the string-building must not cost them anything.
                    _log?.Warn(SpawnCatchUpWarn((Faction)f, row, spawns, total));
                    flushedTotal += total;
                }

                _spawnRefusedSpawnCount[f] = 0;
                System.Array.Clear(row); // per-MATCH aggregation: the next match starts from zero
            }
            return flushedTotal;
        }

        /// <summary>DW-624 — render one faction's catch-up refusal tally as a single designer-facing line: the
        /// affected spawn count, then each research by its authored <see cref="ResearchDefinition.Id"/> (ascending
        /// research index) with the number of spawns it was dropped on. Diagnostic string-building only, reached
        /// solely from <see cref="FlushSpawnCatchUpDiagnostics"/>'s sink-wired path.</summary>
        private string SpawnCatchUpWarn(Faction faction, int[] row, int spawns, int total)
        {
            var fdef = GetFactionDef(faction);
            var sb = new System.Text.StringBuilder();
            sb.Append("[ResearchSystem] future-spawn research catch-up DROPPED an already-completed, already-paid-for ")
              .Append("permanent bonus on ").Append(spawns).Append(" spawn(s) for ").Append(faction)
              .Append(" this match (").Append(total).Append(" refused install(s) in total): ");
            bool first = true;
            for (int ri = 0; ri < row.Length; ri++)
            {
                if (row[ri] <= 0) continue;
                if (!first) sb.Append(", ");
                first = false;
                sb.Append('\'').Append(ResearchIdAt(fdef, ri)).Append("' on ").Append(row[ri]).Append(" spawn(s)");
            }
            sb.Append(". Those units spawned WITHOUT an upgrade their faction already owns — their ")
              .Append(EffectCaps.MaxModifiersPerEntity)
              .Append("-slot modifier ring was already full (items + hero growth + self-passives + earlier ")
              .Append("researches), and unlike a completion there is nothing to refund. Aggregated per match — ONE ")
              .Append("line, never one per spawn (DW-624).");
            return sb.ToString();
        }

        /// <summary>DW-624 — the authored research id at <paramref name="ri"/>, or a positional fallback when the
        /// faction def is missing/short/blank (a research list can be shorter than an already-grown tally row if the
        /// def was swapped, and a malformed <c>"research": null</c> file loads with a null list by design).</summary>
        private static string ResearchIdAt(FactionDefinition? fdef, int ri)
        {
            var list = fdef?.Research;
            if (list == null || ri < 0 || ri >= list.Count) return $"research #{ri}";
            string? id = list[ri]?.Id;
            return string.IsNullOrEmpty(id) ? $"research #{ri}" : id!;
        }

        /// <summary>DW-624 — grow faction-index <paramref name="f"/>'s per-research refusal row to at least
        /// <paramref name="researchCount"/> entries, preserving existing counts and never shrinking (mirrors
        /// <see cref="ResearchStore.EnsureCapacity"/>). Out-of-range <paramref name="f"/> is a silent no-op.</summary>
        private void EnsureRefusalCapacity(int f, int researchCount)
        {
            if (f < 0 || f >= _spawnRefusalsByResearch.Length) return;
            if (researchCount <= _spawnRefusalsByResearch[f].Length) return;
            System.Array.Resize(ref _spawnRefusalsByResearch[f], researchCount);
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
