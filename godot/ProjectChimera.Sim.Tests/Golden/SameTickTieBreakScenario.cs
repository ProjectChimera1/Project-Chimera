#nullable enable
using System;
using ProjectChimera.Combat;            // DamageType, ArmorType
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 1.9a (AC2, D11) — pins the AR-40 fork #1 cross-faction same-tick tie-break (ascending faction
    /// slot, today subsumed by the ascending-entity-ID combat iteration). A SYMMETRIC two-faction melee duel:
    /// a Player1 unit (id 0) and a Player2 unit (id 1) start in melee range with IDENTICAL stats and zero
    /// cooldown, so they auto-engage and trade blows every tick. On the lethal tick the lower-id (= lower
    /// faction slot) attacker resolves FIRST, so the survivor is decided deterministically by the tie-break.
    /// Pinning the per-tick SimChecksum sequence proves that resolution is deterministic and order-stable
    /// across runs AND across processes; a divergence here would be a genuine same-tick non-determinism bug.
    ///
    /// <para>Built in code with <see cref="Fixed"/>-only values (no <c>Fixed.FromFloat</c>) for byte-stability,
    /// exactly like <see cref="GoldenScenario"/>. Distinct from the other goldens (its own file); they are
    /// NEVER re-recorded here.</para>
    /// </summary>
    public static class SameTickTieBreakScenario
    {
        /// <summary>300 ticks = 10s at 30 tps; ChecksumInterval=1 → 300 samples.</summary>
        public const int DefaultTicks = 300;

        /// <summary>The NEW golden file — distinct from the three existing goldens (never re-recorded here).</summary>
        public const string GoldenFileName = "same-tick-tie-break.golden.txt";

        /// <summary>Self-identifying header so the file declares its own re-baseline recipe.</summary>
        public static GoldenChecksumReplay.GoldenHeader Header => new(
            "cross-faction same-tick tie-break golden (Story 1.9a, AR-40 fork #1)",
            "Pins the SimChecksum sequence for a symmetric Player1-vs-Player2 same-tick melee engagement " +
            "(ascending faction-slot / ascending-entity-ID resolution), stepped via StepOnce at ChecksumInterval=1.",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~SameTickTieBreak`, " +
            "then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit. NEVER record the other goldens.");

        /// <summary>
        /// Construct a fresh, fully-wired sim holding the symmetric duel. Allocates brand-new stores/systems on
        /// EVERY call (no shared state), so two in-process calls are independent and a fresh process reproduces
        /// the committed golden exactly.
        /// </summary>
        public static GoldenHarness Build()
        {
            var host = SimulationHost.Create(
                NullLogSink.Instance, new FactionRegistry(2),
                new FactionDefinition(), new FactionDefinition());
            host.ChecksumInterval = 1;

            // P1 (id 0) and P2 (id 1) created in faction-slot order so ascending id == ascending faction slot.
            int p1 = MakeDuelist(host.World, new FixedVec3(Fixed.FromInt(0), Fixed.Zero, Fixed.Zero), Faction.Player1);
            int p2 = MakeDuelist(host.World, new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.Zero), Faction.Player2);
            if (p1 != 0 || p2 != 1)
                throw new InvalidOperationException(
                    $"Tie-break golden invariant broken: ids were P1={p1}, P2={p2} (expected 0, 1). " +
                    "The duelists MUST be created in faction-slot order.");

            // Mirror MainScene's director lifecycle (empty trigger state — a faithful no-op).
            host.ScenarioDirector.LoadScenario(new ScenarioData());
            return new GoldenHarness(host, p1);
        }

        /// <summary>
        /// A symmetric melee duelist: 50 HP, 10 Normal damage vs Light armor, ZERO cooldown (fires every tick),
        /// melee range so it stands and trades blows in place. Identical for both factions, so ONLY the same-tick
        /// resolution order decides who survives the simultaneous lethal exchange.
        /// </summary>
        private static int MakeDuelist(EntityWorld world, FixedVec3 pos, Faction faction)
        {
            int id = world.Create(pos, faction, Fixed.FromInt(50), Fixed.FromInt(3));
            world.AttackDamage[id] = Fixed.FromInt(10);
            world.AttackRange[id]  = Fixed.FromInt(2);  // <= MELEE_THRESHOLD (2.5) ⇒ instant same-tick melee damage
            world.AttackSpeed[id]  = Fixed.Zero;        // zero cooldown ⇒ attacks every tick
            world.DamageTypeOf[id] = DamageType.Normal;
            world.ArmorTypeOf[id]  = ArmorType.Light;
            return id;
        }
    }
}
