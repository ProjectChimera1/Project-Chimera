#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProjectChimera.AI;                 // AiOpponentSystem (P1_BASE / DifficultyProfile — DW-125), AiDifficulty
using ProjectChimera.Core;               // EntityWorld, Fixed, FixedVec3, Faction, FactionRegistry, UnitCategory, GatherState, UnitCommand, BuildingType
using ProjectChimera.Core.Definitions;   // AbilityRegistry, AbilityDefinition, FactionDefinition, UnitDefinition
using ProjectChimera.Core.Sim;           // SimulationHost, NullLogSink, ScenarioData
using ProjectChimera.Multiplayer;        // OrderApplier, UnitOrder
using Xunit;
using Xunit.Abstractions;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 5.8 (FR-20, FR-18) — real-content, AI-piloted integration coverage proving BOTH showcase factions
    /// (alpha/"Crucible Covenant", beta/"Sanguine Court") are individually AI-playable through the existing
    /// SINGLE <see cref="ProjectChimera.AI.AiOpponentSystem"/> instance (hardcoded to pilot Player2), and that
    /// each faction's signature mechanic fires live inside such a match — not just in Story 5.4's isolated
    /// scenario. No new production systems: this exercises Stories 5.1-5.7 together.
    ///
    /// FR-18 is proven by running the AI TWICE — once with alpha occupying <c>factionDef2</c>, once with beta —
    /// swapping which real roster the one hardcoded Player2 AI slot pilots (Design Notes: adding a second,
    /// simultaneous AI instance would be new production capability, out of this integration-only story's scope).
    /// FR-20's unique-mechanic bar is proven live inside these same AI-piloted matches: Sanguine Furnace via the
    /// beta match's own real gathering worker (a combat-exempt <c>forgehand</c>, so nothing can damage it — the
    /// ONLY thing that can move its Health is its own <c>furnace_trickle</c> passive), and Equal Exchange via a
    /// scripted self-cast (the AI never casts an ACTIVE ability on its own — <c>AiOpponentSystem</c> never writes
    /// <c>PendingCastTarget</c> — so this uses the same scripted-order technique Story 5.4's
    /// <see cref="ProjectChimera.Sim.Tests.Effects.SignatureMechanicRealContentTests"/> established).
    ///
    /// Every test loads the REAL shipped <c>alpha_faction.json</c>/<c>beta_faction.json</c> + the REAL
    /// <see cref="AbilityRegistry"/> (mirrors <c>SignatureMechanicRealContentTests.LoadRealContent</c>/<c>NewHost</c>)
    /// — never a hand-built stand-in.
    /// </summary>
    public class AsymmetryPlaytestValidationTests
    {
        private readonly ITestOutputHelper _output;

        public AsymmetryPlaytestValidationTests(ITestOutputHelper output) => _output = output;

        // ── Scenario constants ───────────────────────────────────────────────────────────────────────────────

        /// <summary>DW-125 — the difficulty this harness pins on the AI slot, passed EXPLICITLY to
        /// <see cref="SimulationHost.Create"/> instead of relying on its default, so the threshold read below and
        /// the AI actually piloting the match can never describe two different difficulties.</summary>
        private const AiDifficulty HarnessAiDifficulty = AiDifficulty.Normal;

        /// <summary>The AI's own attack threshold for <see cref="HarnessAiDifficulty"/>, so the AI's pre-placed
        /// initial wave is immediately conscriptable into a <c>LaunchAttack</c> (Idle/Stop both count, per
        /// <c>AiSnapshot.AvailableCombatUnits</c>).
        ///
        /// DW-125 — read SYMBOLICALLY from <see cref="AiOpponentSystem.DifficultyProfile"/> rather than
        /// hand-copying the literal <c>5</c>: a future difficulty-curve retune now resizes this wave with the real
        /// bar instead of leaving a wave that silently no longer meets it (a test that keeps passing in a degraded
        /// way). <c>static readonly</c>, not <c>const</c>, because it is now computed from production code.</summary>
        private static readonly int InitialWaveSize =
            AiOpponentSystem.DifficultyProfile(HarnessAiDifficulty).AttackThreshold;

        /// <summary>Real-content defenders guarding the opposing (non-AI-piloted) base, so the AI's eventual
        /// attack wave engages REAL combat instead of marching to empty ground (unlike the deliberately-starved
        /// <c>AiActiveScenario</c>, which gives its inert Player1 zero units on purpose).</summary>
        private const int OpposingDefenderCount = 4;

        /// <summary>50s at 30 ticks/sec — enough headroom for the AI's own Barracks to complete (~10s fallback
        /// construction), several training cycles from its real roster, and its attack wave to close the ~90-unit
        /// gap to the opposing base and fight.</summary>
        private const int MatchTicks = 1500;

        // Deterministic entity-id layout authored by <see cref="BuildAiPilotedHarness"/> (asserted there so a
        // future reordering fails loudly instead of silently pointing these constants at the wrong unit):
        //   0..(InitialWaveSize-1)                              -> AI (Player2) initial wave (real Melee unit)
        //   InitialWaveSize                                      -> AI (Player2) real Worker-category unit
        //   InitialWaveSize+1 .. InitialWaveSize+OpposingDefenderCount -> opposing (Player1) real Melee defenders
        //   InitialWaveSize+1+OpposingDefenderCount (alpha only) -> isolated Player2 Equal-Exchange caster
        // (static readonly, not const, because InitialWaveSize is now read from AiOpponentSystem — DW-125.
        //  Declared AFTER it so C#'s textual static-initializer order gives them the resolved wave size.)
        private static readonly int AiWorkerEntityId            = InitialWaveSize;
        private static readonly int EqualExchangeCasterEntityId  = InitialWaveSize + 1 + OpposingDefenderCount;

        /// <summary>The AI (Player2) base this harness authors. Deliberately the harness's OWN geometry —
        /// <see cref="AiOpponentSystem"/> has no P2-base constant to import (its five structure-placement positions
        /// cluster around x≈36..54; this sits in the middle of them) — but it is only MEANINGFUL relative to the
        /// AI's real attack destination, so <see cref="AssertHarnessGeometryMatchesTheAi"/> pins that coupling
        /// loudly instead of leaving it to a comment (DW-125).</summary>
        private static readonly FixedVec3 AiBasePos = new(Fixed.FromInt(45), Fixed.Zero, Fixed.Zero);

        /// <summary>Minimum AI-base-to-enemy-base separation this harness's premise needs: the AI's initial wave
        /// must genuinely have to MARCH (and so must start well outside every real roster's attack range) rather
        /// than being in contact at spawn. Comfortably above any shipped unit's range.</summary>
        private static readonly Fixed MinBaseSeparation = Fixed.FromInt(30);

        // ── Real-content resolution (mirrors SignatureMechanicRealContentTests.ResolveDataDir/LoadRealContent) ──

        private static string ResolveDataDir(string sub)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "resources", "data", sub);
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException(
                $"Could not locate resources/data/{sub} above {AppContext.BaseDirectory}");
        }

        /// <summary>
        /// Shipped-roster floor for the REAL ability registry: 10 files lived under <c>resources/data/abilities/</c>
        /// when this guard was written (DW-536, the same floor DW-107 pinned in
        /// <see cref="ProjectChimera.Sim.Tests.Effects.SignatureMechanicRealContentTests"/>).
        /// <see cref="AbilityRegistry.LoadFromDirectory"/> SILENTLY excludes any file that fails
        /// <c>AbilityValidator</c>, and no callback can ever fire for a file that was deleted outright — so without
        /// a count floor a bad roster edit shrinks the registry and every "real content" test in this class keeps
        /// passing against less-than-shipped content. Revise this floor only on a DELIBERATE shipped-roster change.
        /// </summary>
        private const int MIN_SHIPPED_ABILITY_COUNT = 10;

        /// <summary>
        /// DW-536 — the guarded ability-registry load <see cref="LoadRealContent"/> uses. Fails LOUD on BOTH
        /// silent-shrink paths: a shipped file that fails <c>AbilityValidator</c> (via <c>onSkipped</c>, previously
        /// left at its default <c>null</c> here) and a shipped file that disappeared entirely (via
        /// <paramref name="minAbilityCount"/>, which a skip callback can never see).
        ///
        /// Factored out of <see cref="LoadRealContent"/> rather than inlined so the guard itself is directly
        /// exercisable against a synthetic directory (see the two <c>AbilityRegistryGuard_*</c> facts below) —
        /// against the real shipped directory it can only ever be observed NOT firing, which is exactly how the
        /// unguarded load stayed green while claiming to prove real-content coverage.
        /// </summary>
        private static AbilityRegistry LoadGuardedAbilityRegistry(string abilitiesDir, int minAbilityCount)
        {
            var skippedAbilityFiles = new List<string>();
            AbilityRegistry registry = AbilityRegistry.LoadFromDirectory(abilitiesDir, skippedAbilityFiles.Add);
            Assert.True(skippedAbilityFiles.Count == 0,
                $"shipped ability file(s) failed validation and were silently excluded from the registry: {string.Join(", ", skippedAbilityFiles)}");
            Assert.True(registry.Count >= minAbilityCount,
                $"real ability registry holds {registry.Count} abilities, below the shipped floor of {minAbilityCount} — shipped content shrank (deleted/moved file?); revise MIN_SHIPPED_ABILITY_COUNT only on a deliberate roster change.");
            return registry;
        }

        /// <summary>
        /// Loads the REAL shipped alpha/beta rosters + the REAL <see cref="AbilityRegistry"/>, then resolves every
        /// roster unit's abilities against it. DW-536: the registry load now routes through
        /// <see cref="LoadGuardedAbilityRegistry"/>, so a future ability-JSON edit that breaks validation (or a
        /// deleted ability file) fails this class LOUDLY instead of quietly shrinking the content every FR-18/FR-20
        /// assertion below is measured against.
        /// </summary>
        private static (FactionDefinition alpha, FactionDefinition beta, AbilityRegistry registry) LoadRealContent()
        {
            AbilityRegistry registry = LoadGuardedAbilityRegistry(ResolveDataDir("abilities"), MIN_SHIPPED_ABILITY_COUNT);
            FactionDefinition alpha = FactionDefinition.LoadFromFile(Path.Combine(ResolveDataDir("factions"), "alpha_faction.json"));
            FactionDefinition beta  = FactionDefinition.LoadFromFile(Path.Combine(ResolveDataDir("factions"), "beta_faction.json"));
            foreach (UnitDefinition u in alpha.Units) u.ResolveAbilities(registry);
            foreach (UnitDefinition u in beta.Units)  u.ResolveAbilities(registry);
            return (alpha, beta, registry);
        }

        // ── Shared AI-piloted match builders ─────────────────────────────────────────────────────────────────

        /// <summary>Alpha occupies the AI's hardcoded Player2 slot; beta is the passive, real-content opposing
        /// (Player1) side. Reused by the alpha FR-18 test, the Equal Exchange scripted-cast test, and the
        /// composition/determinism test — a fresh, independently-loaded build every call (no shared/static
        /// state), matching every other golden scenario's <c>Build()</c> contract.</summary>
        private static GoldenHarness BuildAlphaPilotedHarness()
        {
            var (alpha, beta, registry) = LoadRealContent();
            return BuildAiPilotedHarness(aiRoster: alpha, opposingRoster: beta, registry);
        }

        /// <summary>Beta occupies the AI's hardcoded Player2 slot; alpha is the passive, real-content opposing
        /// (Player1) side. Reused by the beta FR-18 test, the Sanguine Furnace live-fire test, and the
        /// composition/determinism test.</summary>
        private static GoldenHarness BuildBetaPilotedHarness()
        {
            var (alpha, beta, registry) = LoadRealContent();
            return BuildAiPilotedHarness(aiRoster: beta, opposingRoster: alpha, registry);
        }

        /// <summary>
        /// Builds a fresh, fully-wired AI-piloted match: <paramref name="aiRoster"/> fills
        /// <see cref="SimulationHost.Create"/>'s <c>factionDef2</c> (the AI's hardcoded Player2 slot — Design
        /// Notes), <paramref name="opposingRoster"/> fills <c>factionDef1</c> (Player1, passive real-content
        /// defenders — never AI-piloted, never a second <c>AiOpponentSystem</c>). Gives the AI a live
        /// CommandCenter + ample ore (so it can afford to build its OWN Barracks and train from its real roster
        /// — nothing here pre-builds a Barracks, so <c>ScoreBuildBarracks</c> must actually fire), a real
        /// Worker-category unit gathering a nearby node (proves "gathers" and, for beta, doubles as the
        /// furnace-regen observation unit — a gatherer is combat-exempt, so nothing else can move its Health),
        /// and an initial wave of real Melee units at the Normal attack threshold (conscriptable, not yet
        /// launched — the AI's own <c>ScoreLaunchAttack</c> decides when). The opposing side gets a live
        /// CommandCenter + real Melee defenders positioned at <c>AiOpponentSystem.P1_BASE</c> (the AI's
        /// hardcoded attack destination), so the eventual attack wave engages REAL combat.
        /// </summary>
        private static GoldenHarness BuildAiPilotedHarness(
            FactionDefinition aiRoster, FactionDefinition opposingRoster, AbilityRegistry registry)
        {
            AssertHarnessGeometryMatchesTheAi();

            SimulationHost host = SimulationHost.Create(
                NullLogSink.Instance, new FactionRegistry(2), opposingRoster, aiRoster,
                aiLevel: HarnessAiDifficulty, registry: registry);
            host.ChecksumInterval = 1;
            EntityWorld world = host.World;

            // ── AI (Player2) base: a live, real CommandCenter + ample ore so it can afford to build its own
            //    Barracks (AiOpponentSystem's hardcoded COST_BARRACKS), train from its real roster, and expand. ──
            FixedVec3 aiCcPos = AiBasePos;
            int aiCc = host.BuildSys.PlaceBuildingDirect(BuildingType.CommandCenter, Faction.Player2, aiCcPos, preBuilt: true);
            if (aiCc < 0) throw new InvalidOperationException("BuildAiPilotedHarness: AI CommandCenter failed to place.");
            host.Resources.FactionBase[(int)Faction.Player2] = aiCcPos;
            host.Resources.AddOre(Faction.Player2, Fixed.FromInt(1000));

            // ── AI (Player2) initial wave: real roster Melee units, held at Stop (counted as "available" by
            //    AiSnapshot but not wandering off before the AI's own attack decision — trained units spawn at
            //    Stop too, so this matches how the AI's OWN production holds new units). ──
            UnitDefinition? aiMeleeDef = aiRoster.GetUnitByCategory("Melee");
            if (aiMeleeDef == null)
                throw new InvalidOperationException($"BuildAiPilotedHarness: '{aiRoster.Id}' has no Melee unit for the initial wave.");
            for (int i = 0; i < InitialWaveSize; i++)
            {
                int u = world.Create(new FixedVec3(Fixed.FromInt(40), Fixed.Zero, Fixed.FromInt(i * 2 - 4)),
                                      Faction.Player2, Fixed.FromFloat(aiMeleeDef.Hp), Fixed.FromFloat(aiMeleeDef.Speed));
                world.ApplyUnitDefinition(u, aiMeleeDef);
                world.CommandState[u] = UnitCommand.Stop;
            }

            // ── AI (Player2) worker: real Worker-category unit (alpha "worker"/Acolyte, beta "forgehand"/
            //    Cinderhand Thrall) gathering a nearby node — proves FR-18's "gathers" bar. A gatherer is
            //    combat-exempt (CombatSystem.Tick's GatherState guard), so for beta this SAME unit doubles as the
            //    Sanguine Furnace live-fire observation unit: nothing else can touch its Health. ──
            UnitDefinition? aiWorkerDef = aiRoster.GetUnitByCategory("Worker");
            if (aiWorkerDef == null)
                throw new InvalidOperationException($"BuildAiPilotedHarness: '{aiRoster.Id}' has no Worker unit.");
            int aiWorker = world.Create(new FixedVec3(Fixed.FromInt(50), Fixed.Zero, Fixed.FromInt(10)),
                                        Faction.Player2, Fixed.FromFloat(aiWorkerDef.Hp), Fixed.FromFloat(aiWorkerDef.Speed));
            world.ApplyUnitDefinition(aiWorker, aiWorkerDef);
            world.GatherState[aiWorker]   = GatherState.Idle;
            world.CarryCapacity[aiWorker] = Fixed.FromInt(20);
            if (aiWorker != AiWorkerEntityId)
                throw new InvalidOperationException(
                    $"BuildAiPilotedHarness invariant broken: AI worker id was {aiWorker}, expected {AiWorkerEntityId}.");
            host.Nodes.Create(new FixedVec3(Fixed.FromInt(55), Fixed.Zero, Fixed.FromInt(10)),
                              Fixed.FromInt(500), Fixed.FromInt(7), 3);

            // ── Opposing (Player1) base + defenders: real content, positioned AT AiOpponentSystem's attack
            //    destination — read SYMBOLICALLY from AiOpponentSystem.P1_BASE (DW-125), never hand-copied as
            //    (-45,0,0) — so the AI's eventual attack wave engages REAL combat instead of marching to empty
            //    ground. Stop (never chase) — a defended base, not a second AI. ──
            FixedVec3 p1BasePos = AiOpponentSystem.P1_BASE;
            int p1Cc = host.BuildSys.PlaceBuildingDirect(BuildingType.CommandCenter, Faction.Player1, p1BasePos, preBuilt: true);
            if (p1Cc < 0) throw new InvalidOperationException("BuildAiPilotedHarness: opposing CommandCenter failed to place.");
            UnitDefinition? defenderDef = opposingRoster.GetUnitByCategory("Melee");
            if (defenderDef == null)
                throw new InvalidOperationException($"BuildAiPilotedHarness: '{opposingRoster.Id}' has no Melee unit for defenders.");
            for (int i = 0; i < OpposingDefenderCount; i++)
            {
                // DW-125: parked 3u in front of the AI's real destination (P1_BASE + offset), so retuning P1_BASE
                // relocates the defenders WITH the wave instead of stranding them on ground it no longer visits.
                int u = world.Create(p1BasePos + new FixedVec3(Fixed.FromInt(3), Fixed.Zero, Fixed.FromInt(i * 2 - 3)),
                                      Faction.Player1, Fixed.FromFloat(defenderDef.Hp), Fixed.FromFloat(defenderDef.Speed));
                world.ApplyUnitDefinition(u, defenderDef);
                world.CommandState[u] = UnitCommand.Stop;
            }

            // ── Equal Exchange scripted-cast fixture: alpha's roster is the sole spike_transmutation carrier
            //    (SignatureMechanicEffectId), so ONLY the alpha-piloted match gets this isolated Player2 caster
            //    (alpha's first Melee unit IS "infantry", the real carrier) — far from all combat (never in
            //    attack range of anything, Stop so it never chases) for
            //    EqualExchange_FiresLive_ViaScriptedCast's scripted self-cast. The AI itself never casts an
            //    ACTIVE ability on its own (Boundaries/Design Notes), so this is driven by a scripted order. ──
            if (aiRoster.SignatureMechanicEffectId == "spike_transmutation")
            {
                int caster = world.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.FromInt(200)),
                                           Faction.Player2, Fixed.FromFloat(aiMeleeDef.Hp), Fixed.FromFloat(aiMeleeDef.Speed));
                world.ApplyUnitDefinition(caster, aiMeleeDef);
                world.CommandState[caster] = UnitCommand.Stop;
                if (caster != EqualExchangeCasterEntityId)
                    throw new InvalidOperationException(
                        $"BuildAiPilotedHarness invariant broken: Equal Exchange caster id was {caster}, expected {EqualExchangeCasterEntityId}.");
            }

            host.ScenarioDirector.LoadScenario(new ScenarioData());
            return new GoldenHarness(host, aiWorker);
        }

        /// <summary>
        /// DW-125 — fail LOUDLY (rather than silently degrade) if a future retune of <see cref="AiOpponentSystem"/>
        /// invalidates this harness's own authored geometry. Two of the three mirrored values are now imported
        /// symbolically (<see cref="InitialWaveSize"/>, <see cref="AiOpponentSystem.P1_BASE"/>); the third —
        /// <see cref="AiBasePos"/> — has no production constant to import, so its couplings are asserted instead:
        ///   1. the AI base and the AI's attack destination sit on OPPOSITE sides of the map origin (otherwise
        ///      "the wave marches out and fights" collapses into contact at spawn, and the AI's own structure
        ///      placements — clustered on the positive X side — would land on top of the enemy base);
        ///   2. that separation stays beyond <see cref="MinBaseSeparation"/>, so the pre-placed defenders and the
        ///      AI's initial wave genuinely start out of weapons range;
        ///   3. the attack threshold is a positive unit count, so the initial wave is never empty (an empty wave
        ///      would make every FR-18 'fights' assertion vacuous rather than failing).
        /// </summary>
        private static void AssertHarnessGeometryMatchesTheAi()
        {
            FixedVec3 enemyBase = AiOpponentSystem.P1_BASE;

            if (!(AiBasePos.X > Fixed.Zero && enemyBase.X < Fixed.Zero))
                throw new InvalidOperationException(
                    $"BuildAiPilotedHarness geometry drifted: the AI base ({AiBasePos.X.Raw} raw X) and " +
                    $"AiOpponentSystem.P1_BASE ({enemyBase.X.Raw} raw X) must sit on OPPOSITE sides of the origin " +
                    "for this harness's march-out-and-fight premise to hold. Re-author AiBasePos for the new P1_BASE.");

            Fixed separation = AiBasePos.X - enemyBase.X;
            if (separation < MinBaseSeparation)
                throw new InvalidOperationException(
                    $"BuildAiPilotedHarness geometry drifted: AI base to AiOpponentSystem.P1_BASE separation is " +
                    $"{separation.Raw} raw (< {MinBaseSeparation.Raw} raw) — the initial wave and the opposing " +
                    "defenders would start inside weapons range, so the test would no longer prove the AI DECIDED " +
                    "to attack. Re-author AiBasePos for the new P1_BASE.");

            if (InitialWaveSize <= 0)
                throw new InvalidOperationException(
                    $"BuildAiPilotedHarness geometry drifted: AiOpponentSystem's {HarnessAiDifficulty} attack " +
                    $"threshold is {InitialWaveSize}, so this harness would author an EMPTY initial wave and the " +
                    "FR-18 'fights' assertions would become vacuous.");
        }

        /// <summary>Issue a slot-0 self-cast intent through the shared, real <see cref="OrderApplier"/> (mirrors
        /// <c>SignatureMechanicRealContentTests.IssueSelfCast</c>) — TargetZ -1 = Self/None per NetworkCommand's
        /// convention. <paramref name="ownerFaction"/> must match the caster's actual faction (OrderApplier's
        /// anti-cheat "own units only" guard).</summary>
        private static void IssueSelfCast(EntityWorld world, int casterId, Faction ownerFaction) =>
            OrderApplier.Apply(world, new UnitOrder(casterId, UnitCommand.CastAbility, Fixed.FromRaw(0), Fixed.FromRaw(-1)), ownerFaction);

        // ── FR-18 — each real roster is AI-playable via the existing single AiOpponentSystem ────────────────

        [Fact]
        public void AiPilots_AlphaRoster_GathersBuildsTrainsAndFights()
        {
            GoldenHarness h = BuildAlphaPilotedHarness();
            AssertAiPilotedFactionProgresses(h, "alpha");
        }

        [Fact]
        public void AiPilots_BetaRoster_GathersBuildsTrainsAndFights()
        {
            GoldenHarness h = BuildBetaPilotedHarness();
            AssertAiPilotedFactionProgresses(h, "beta");
        }

        /// <summary>
        /// FR-18 — over a fixed tick count, the AI-piloted faction (Player2) must: grow its building count
        /// (builds its own Barracks — nothing here pre-places one), train at least one unit from its real roster
        /// (cumulative <see cref="MatchStats.UnitsBuilt"/>, immune to the noise of combat losses), deposit ore
        /// via its real gathering worker (cumulative <see cref="MatchStats.OreMined"/>), and engage in real
        /// combat (cumulative kills, either direction, over zero) against the opposing real-content defenders.
        /// </summary>
        private static void AssertAiPilotedFactionProgresses(GoldenHarness h, string rosterLabel)
        {
            const Faction ai = Faction.Player2;

            int buildingsBefore   = CountFactionBuildings(h, ai);
            int oreMinedBefore    = h.Host.MatchStats.OreMined(ai);
            int engagementBefore  = h.Host.MatchStats.Kills(Faction.Player1) + h.Host.MatchStats.Kills(Faction.Player2);

            for (int i = 0; i < MatchTicks; i++) h.Host.StepOnce();

            int buildingsAfter  = CountFactionBuildings(h, ai);
            int oreMinedAfter   = h.Host.MatchStats.OreMined(ai);
            int engagementAfter = h.Host.MatchStats.Kills(Faction.Player1) + h.Host.MatchStats.Kills(Faction.Player2);
            int unitsBuilt      = h.Host.MatchStats.UnitsBuilt(ai);
            int aliveUnits      = CountFactionUnits(h.World, ai);

            Assert.True(buildingsAfter > buildingsBefore,
                $"{rosterLabel}: AI-piloted building count did not grow ({buildingsBefore} -> {buildingsAfter}) over {MatchTicks} ticks (FR-18 'builds').");
            Assert.True(unitsBuilt > 0,
                $"{rosterLabel}: AI-piloted faction trained zero units from its real roster over {MatchTicks} ticks (FR-18 'trains').");
            Assert.True(oreMinedAfter > oreMinedBefore,
                $"{rosterLabel}: AI-piloted faction's real worker deposited no ore ({oreMinedBefore} -> {oreMinedAfter}) over {MatchTicks} ticks (FR-18 'gathers').");
            Assert.True(engagementAfter > engagementBefore,
                $"{rosterLabel}: no combat kills were recorded ({engagementBefore} -> {engagementAfter}) over {MatchTicks} ticks — the AI-piloted wave never engaged real combat (FR-18 'fights').");
            Assert.True(aliveUnits > 0,
                $"{rosterLabel}: AI-piloted faction has zero alive units at match end.");
        }

        private static int CountFactionBuildings(GoldenHarness h, Faction f)
        {
            int n = 0;
            for (int i = 0; i < h.Buildings.Count; i++)
                if (h.Buildings.Alive[i] && h.Buildings.FactionOf[i] == f) n++;
            return n;
        }

        private static int CountFactionUnits(EntityWorld world, Faction f)
        {
            int n = 0;
            int hwm = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
                if (world.IsAlive(i) && world.FactionOf[i] == f) n++;
            return n;
        }

        // ── FR-20 — Sanguine Furnace fires live inside a real AI-piloted match ───────────────────────────────

        /// <summary>
        /// Beta's real "forgehand" worker (the AI-piloted match's own gathering unit — see
        /// <see cref="BuildAiPilotedHarness"/>) regenerates off its shipped <c>furnace_trickle</c> passive while
        /// the beta AI is actively building/training/fighting elsewhere in this SAME match. A gatherer is
        /// combat-exempt (<c>CombatSystem.Tick</c>'s <c>GatherState</c> guard), so nothing else can move its
        /// Health — proving the mechanic fires live in a real AI-driven match, not just Story 5.4's isolated
        /// scenario.
        /// </summary>
        [Fact]
        public void SanguineFurnace_FiresLive_InAiMatch()
        {
            GoldenHarness h = BuildBetaPilotedHarness();
            EntityWorld world = h.World;

            int forgehand = AiWorkerEntityId;
            Assert.Equal(Faction.Player2, world.FactionOf[forgehand]);
            Assert.NotNull(world.SourceDefinition[forgehand]);
            Assert.Equal("forgehand", world.SourceDefinition[forgehand]!.Id);
            Assert.True(world.SourceDefinition[forgehand]!.SelfPassiveAbilityIndex >= 0,
                "forgehand's furnace_trickle did not resolve to a self-passive slot.");

            // Pre-damage below max so regen has room to show; clamp at Zero so a future data-driven HP retune
            // below 30 can't push this into negative Health territory ahead of the regen assertion below.
            world.Health[forgehand] = Fixed.Max(Fixed.Zero, world.Health[forgehand] - Fixed.FromInt(30));
            Fixed before = world.Health[forgehand];

            for (int i = 0; i < 60; i++) h.Host.StepOnce();

            Assert.True(world.Health[forgehand] > before,
                $"beta's real forgehand did not regen off furnace_trickle while AI-piloted: {world.Health[forgehand].Raw} vs before {before.Raw}");
            Assert.True(world.Health[forgehand] <= world.EffectiveMaxHealth[forgehand],
                $"Furnace regen overshot EffectiveMaxHealth: {world.Health[forgehand].Raw} > {world.EffectiveMaxHealth[forgehand].Raw}");
            Assert.NotEqual(GatherState.Inactive, world.GatherState[forgehand]); // still a gatherer — never diverted into combat
        }

        // ── FR-20 — Equal Exchange fires live inside a real AI-piloted match, via a scripted cast ───────────

        /// <summary>
        /// <c>spike_transmutation</c> (Equal Exchange) is an ACTIVE ability the AI never casts on its own
        /// (<c>AiOpponentSystem</c> never writes <c>PendingCastTarget</c>). This scripts a self-cast on the
        /// isolated real alpha "infantry" caster (see <see cref="BuildAiPilotedHarness"/>) partway through an
        /// alpha-AI-piloted match (the AI is already active elsewhere in the SAME match) and asserts the exact
        /// -25 HP flat, armor-independent self-cost.
        /// </summary>
        [Fact]
        public void EqualExchange_FiresLive_ViaScriptedCast()
        {
            var (_, _, registry) = LoadRealContent();
            int spikeIdx = registry.IndexOf("spike_transmutation");
            Assert.True(spikeIdx >= 0, "spike_transmutation is not in the real loaded AbilityRegistry.");
            AbilityDefinition spike = registry.Get(spikeIdx);

            GoldenHarness h = BuildAlphaPilotedHarness();
            EntityWorld world = h.World;

            int caster = EqualExchangeCasterEntityId;
            Assert.Equal(Faction.Player2, world.FactionOf[caster]);
            Assert.NotNull(world.SourceDefinition[caster]);
            Assert.Equal("infantry", world.SourceDefinition[caster]!.Id);
            Assert.Equal(spikeIdx, world.AbilityId[caster * EntityWorld.MAX_ABILITIES_PER_UNIT + 0]);

            for (int i = 0; i < 30; i++) h.Host.StepOnce(); // the alpha AI is already active elsewhere in this SAME match

            Fixed before = world.Health[caster];
            IssueSelfCast(world, caster, Faction.Player2);
            h.Host.StepOnce();

            Fixed delta = before - world.Health[caster];
            Assert.Equal(25, spike.CostHealth); // pin the AC's stated -25 to the real shipped ability data
            Assert.Equal(Fixed.FromInt(spike.CostHealth).Raw, delta.Raw);
        }

        // ── AC3/AC4 — composition/win-rate recording + same-machine determinism ─────────────────────────────

        /// <summary>
        /// AC4 — two fresh in-process builds of the SAME AI-piloted match (per roster) are byte-identical
        /// (no static/shared mutable state, no unseeded RNG, no Dictionary/HashSet enumeration in the AI path —
        /// same-machine-only per Design Notes; <c>AiOpponentSystem</c> scores with <c>float</c>, so this is not a
        /// cross-platform claim, matching <c>AiActiveGoldenTests</c>' own precedent).
        ///
        /// AC3 — composition sampling: counts the AI-piloted faction's alive units by
        /// <see cref="UnitDefinition.ParsedCategory"/> at match end for both rosters (varying ONLY which real
        /// roster occupies the AI slot, per Design Notes — no RNG-seed plumbing exists to vary anything else),
        /// computes a Manhattan composition distance, and records both alongside each run's kills/losses/ore/
        /// units-built via test output (the FR-20 playtest validation note).
        /// </summary>
        [Fact]
        public void RecordCompositionAndDeterminism()
        {
            var (alphaHarnessA, alphaSeqA) = RunTracked(BuildAlphaPilotedHarness, MatchTicks);
            var (_, alphaSeqB) = RunTracked(BuildAlphaPilotedHarness, MatchTicks);
            Assert.True(alphaSeqA.Count >= MatchTicks,
                $"expected >= {MatchTicks} checksum samples for the alpha-piloted run, got {alphaSeqA.Count}");
            Assert.True(alphaSeqA.SequenceEqual(alphaSeqB),
                "AC4 — two in-process runs of the SAME alpha-piloted AI match diverged (a static/shared mutable-state or nondeterminism leak).");

            var (betaHarnessA, betaSeqA) = RunTracked(BuildBetaPilotedHarness, MatchTicks);
            var (_, betaSeqB) = RunTracked(BuildBetaPilotedHarness, MatchTicks);
            Assert.True(betaSeqA.Count >= MatchTicks,
                $"expected >= {MatchTicks} checksum samples for the beta-piloted run, got {betaSeqA.Count}");
            Assert.True(betaSeqA.SequenceEqual(betaSeqB),
                "AC4 — two in-process runs of the SAME beta-piloted AI match diverged (a static/shared mutable-state or nondeterminism leak).");

            Dictionary<UnitCategory, int> alphaComposition = CountAliveByCategory(alphaHarnessA.World, Faction.Player2);
            Dictionary<UnitCategory, int> betaComposition  = CountAliveByCategory(betaHarnessA.World, Faction.Player2);
            int compositionDistance = CompositionDistance(alphaComposition, betaComposition);
            int alphaSurvivors = alphaComposition.Values.Sum();
            int betaSurvivors  = betaComposition.Values.Sum();

            MatchStats aStats = alphaHarnessA.Host.MatchStats;
            MatchStats bStats = betaHarnessA.Host.MatchStats;
            _output.WriteLine(
                $"[FR-20 playtest note] alpha-piloted @ tick {MatchTicks}: composition {{{DescribeComposition(alphaComposition)}}} " +
                $"survivors={alphaSurvivors} kills={aStats.Kills(Faction.Player2)} losses={aStats.Losses(Faction.Player2)} " +
                $"oreMined={aStats.OreMined(Faction.Player2)} unitsBuilt={aStats.UnitsBuilt(Faction.Player2)}");
            _output.WriteLine(
                $"[FR-20 playtest note] beta-piloted  @ tick {MatchTicks}: composition {{{DescribeComposition(betaComposition)}}} " +
                $"survivors={betaSurvivors} kills={bStats.Kills(Faction.Player2)} losses={bStats.Losses(Faction.Player2)} " +
                $"oreMined={bStats.OreMined(Faction.Player2)} unitsBuilt={bStats.UnitsBuilt(Faction.Player2)}");
            _output.WriteLine($"[FR-20 playtest note] composition distance (Manhattan over UnitCategory counts) = {compositionDistance}");
            _output.WriteLine(
                $"[FR-20 playtest note] win-rate figure: alpha-piloted kills-minus-losses={aStats.Kills(Faction.Player2) - aStats.Losses(Faction.Player2)}, " +
                $"beta-piloted kills-minus-losses={bStats.Kills(Faction.Player2) - bStats.Losses(Faction.Player2)}");

            // Non-vacuity: both AI-piloted armies have surviving real-roster units at match end — the point of
            // AC3 is comparing two REAL rosters' compositions, not two empty graves.
            Assert.True(alphaSurvivors > 0, "AC3: alpha-piloted Player2 has zero surviving units at match end — nothing to compare.");
            Assert.True(betaSurvivors  > 0, "AC3: beta-piloted Player2 has zero surviving units at match end — nothing to compare.");
        }

        /// <summary>Build a FRESH harness, wire the checksum sink, and step it <paramref name="ticks"/> times —
        /// like <see cref="GoldenChecksumReplay.RunAndRecord"/>, but also returns the harness itself so callers
        /// can inspect end-of-match world/store state (composition sampling), which the discard-the-harness
        /// <c>RunAndRecord</c> helper cannot provide.</summary>
        private static (GoldenHarness Harness, List<GoldenChecksumReplay.Sample> Seq) RunTracked(Func<GoldenHarness> build, int ticks)
        {
            GoldenHarness harness = build();
            var seq = new List<GoldenChecksumReplay.Sample>(ticks);
            harness.Host.SetChecksumSink((tick, hash) => seq.Add(new GoldenChecksumReplay.Sample(tick, hash)));
            for (int i = 0; i < ticks; i++) harness.Host.StepOnce();
            return (harness, seq);
        }

        private static Dictionary<UnitCategory, int> CountAliveByCategory(EntityWorld world, Faction faction)
        {
            var counts = new Dictionary<UnitCategory, int>();
            int hwm = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
            {
                if (!world.IsAlive(i)) continue;
                if (world.FactionOf[i] != faction) continue;
                // A def-less fallback-trained unit (never happens on the real-content path here, but guarded for
                // robustness) has no SourceDefinition; treat it like the production fallback spawn's implicit
                // category (Melee — BuildingSystem's own def-less branch has no category concept either).
                UnitCategory cat = world.SourceDefinition[i]?.ParsedCategory ?? UnitCategory.Melee;
                counts[cat] = counts.GetValueOrDefault(cat) + 1;
            }
            return counts;
        }

        private static int CompositionDistance(Dictionary<UnitCategory, int> a, Dictionary<UnitCategory, int> b)
        {
            int distance = 0;
            foreach (UnitCategory cat in Enum.GetValues<UnitCategory>())
                distance += Math.Abs(a.GetValueOrDefault(cat) - b.GetValueOrDefault(cat));
            return distance;
        }

        private static string DescribeComposition(Dictionary<UnitCategory, int> counts) =>
            string.Join(", ", counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));

        // ── DW-536 hardening teeth — the silent-shrink seam LoadRealContent now guards ───────────────────────

        /// <summary>Create an isolated synthetic ability directory seeded with <paramref name="validFileNames"/>
        /// copied byte-for-byte from the REAL shipped set (so "valid" means what the shipped validator says, never
        /// a hand-written stand-in). Caller deletes it.</summary>
        private static string NewSyntheticAbilityDir(params string[] validFileNames)
        {
            string dir = Path.Combine(Path.GetTempPath(), "chimera_dw536_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            foreach (string name in validFileNames)
                File.Copy(Path.Combine(ResolveDataDir("abilities"), name), Path.Combine(dir, name));
            return dir;
        }

        /// <summary>
        /// DW-536 red-test #1 — the onSkipped half. A validation-broken ability file is SILENTLY excluded by
        /// <see cref="AbilityRegistry.LoadFromDirectory"/> when <c>onSkipped</c> is left at its default (the exact
        /// shape <see cref="LoadRealContent"/> shipped with), so an unguarded real-content load keeps passing
        /// against quietly-reduced content. Asserts the unguarded call is indeed silent, then that
        /// <see cref="LoadGuardedAbilityRegistry"/> throws and names the offending file. Drop the skip assert from
        /// the guard and this fails.
        /// </summary>
        [Fact]
        public void AbilityRegistryGuard_FailsLoud_WhenAShippedAbilityFileFailsValidation()
        {
            string dir = NewSyntheticAbilityDir("spike_transmutation.json");
            try
            {
                // "on_death" is outside the closed PassiveActivation set — AbilityValidator rejects it.
                File.WriteAllText(Path.Combine(dir, "broken_probe.json"),
                    "{ \"id\": \"broken_probe\", \"activation\": \"on_death\" }");

                AbilityRegistry silent = AbilityRegistry.LoadFromDirectory(dir); // onSkipped defaulted — the DW-536 trap
                Assert.Equal(1, silent.Count);                                   // the broken file vanished with no signal
                Assert.Equal(-1, silent.IndexOf("broken_probe"));

                var thrown = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
                    () => LoadGuardedAbilityRegistry(dir, minAbilityCount: 1));
                Assert.Contains("broken_probe.json", thrown.Message);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        /// <summary>
        /// DW-536 red-test #2 — the count-floor half, which covers the case <c>onSkipped</c> can NEVER see: a
        /// shipped ability file deleted or moved outright. Every remaining file still validates, so the skip
        /// channel stays silent and only the floor can catch the shrink. Drop the floor assert from the guard and
        /// this fails.
        /// </summary>
        [Fact]
        public void AbilityRegistryGuard_FailsLoud_WhenTheShippedAbilityRosterShrinks()
        {
            string dir = NewSyntheticAbilityDir("spike_transmutation.json", "furnace_trickle.json");
            try
            {
                var skipped = new List<string>();
                Assert.Equal(2, AbilityRegistry.LoadFromDirectory(dir, skipped.Add).Count);
                Assert.Empty(skipped); // nothing failed validation — the skip channel cannot see a deleted file

                var thrown = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
                    () => LoadGuardedAbilityRegistry(dir, MIN_SHIPPED_ABILITY_COUNT));
                Assert.Contains("below the shipped floor", thrown.Message);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        /// <summary>
        /// DW-536 named guard: every test in this class routes through <see cref="LoadRealContent"/>'s skip/floor
        /// asserts, but this fact exists so a shipped-content shrink fails under a name that says exactly that
        /// instead of surfacing first as a confusing AI-behavior or mechanic failure — and it pins the two
        /// signature ability ids this class actually drives (<c>furnace_trickle</c> via beta's forgehand
        /// self-passive, <c>spike_transmutation</c> via the alpha scripted cast) as present in the REAL registry.
        /// </summary>
        [Fact]
        public void LoadRealContent_ShippedAbilityRoster_LoadsCompletely()
        {
            var (_, _, registry) = LoadRealContent(); // fails loud inside on any skip or a sub-floor count

            Assert.True(registry.Count >= MIN_SHIPPED_ABILITY_COUNT,
                $"registry.Count {registry.Count} fell below the shipped floor {MIN_SHIPPED_ABILITY_COUNT}.");
            Assert.True(registry.IndexOf("furnace_trickle") >= 0, "shipped furnace_trickle is missing from the real registry.");
            Assert.True(registry.IndexOf("spike_transmutation") >= 0, "shipped spike_transmutation is missing from the real registry.");
        }
    }
}
