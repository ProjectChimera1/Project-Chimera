using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 3.13 (AC) — the HERO-XP golden scenario. A deployed hero kills a line of hostile Neutral units carrying an
    /// XP bounty, in range, crossing several authored curve thresholds → its Level advances and per-level stat growth is
    /// applied through the folded <see cref="ProjectChimera.Effects.ModifierStore"/>. The per-tick
    /// <see cref="SimChecksum"/> (v11, folding XpBounty + the mutable HeroStore Level/Xp/GrowthStacks) captures the XP
    /// accumulating, the level advancing, and the enemies dying — pinning the XP runtime end-to-end.
    ///
    /// CROSS-PLATFORM SAFE: every value is integer/<see cref="Fixed"/>; the curve (baseXp 50, growth 1.0) and bounties
    /// are exact. Targets are Neutral 0-damage passives (never fight back) and Player2 is EMPTY, so the float-scoring AI
    /// no-ops — this golden is NOT Windows-gated and is compared on both CI legs.
    /// </summary>
    public static class HeroXpScenario
    {
        /// <summary>300 ticks = 10s at 30 tps; ChecksumInterval = 1 → 300 samples.</summary>
        public const int DefaultTicks = 300;

        /// <summary>The deployed hero's stable identity (any fixed value — it folds as HeroId.Value).</summary>
        private static readonly HeroId HeroIdentity = new HeroId(3_130_000_013UL);

        public static GoldenHarness Build()
        {
            var host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(2),       // P1 + P2 active (P2 EMPTY → AI no-ops → cross-platform safe)
                new FactionDefinition(),
                new FactionDefinition());
            host.ChecksumInterval = 1;

            int hero = PopulateScenario(host);
            host.ScenarioDirector.LoadScenario(new ScenarioData());
            return new GoldenHarness(host, hero);
        }

        private static int PopulateScenario(SimulationHost host)
        {
            EntityWorld w = host.World;
            HeroStore heroes = host.Heroes;

            // ── id 0 — the deployed HERO (Player1). High damage / long range / Hitscan so it one-shots the nearest
            //    hostile each attack. Idle → auto-acquires the nearest enemy every cooldown. ──
            int hero = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(500), Fixed.FromInt(3));
            // Set BASE attack damage (not just Effective): once the growth modifier lands at level 2, ModifierSystem
            // recomputes Effective = Base + Σgrowth, so a Base of 0 would silently DISCARD the authored 100 (Create
            // leaves Base health=500 / armor=0, so only damage needs an explicit base to model production growth).
            w.BaseAttackDamage[hero]      = Fixed.FromInt(100);
            w.EffectiveAttackDamage[hero] = Fixed.FromInt(100);
            w.AttackRange[hero] = Fixed.FromInt(20);
            w.AttackSpeed[hero] = Fixed.FromInt(1);   // 1s == 30 ticks between attacks
            w.DamageTypeOf[hero] = DamageType.Normal;
            w.Delivery[hero]     = AttackDelivery.Hitscan;
            w.CommandState[hero] = UnitCommand.Idle;

            // Mint the hero row with an authored curve that levels quickly (baseXp 50, growth 1.0 → 50 XP per level up to
            // MaxLevel 5) and non-zero growth (so the ModifierStore growth is observable), then establish the D-8 link.
            int slot = heroes.Mint(HeroIdentity, hero, level: 1, xp: Fixed.Zero,
                maxLevel: 5,
                baseXp: Fixed.FromInt(50), xpGrowth: Fixed.One, xpShareRadius: Fixed.FromInt(30),
                healthPerLevel: Fixed.FromInt(10), damagePerLevel: Fixed.FromInt(2), armorPerLevel: Fixed.FromInt(1));
            w.HeroIndex[hero] = heroes.PackRef(slot);

            // ── ids 1..8 — a line of hostile Neutral targets (0 damage → never fight back), each low-HP (one-shot) and
            //    carrying an XP bounty of 30. 8 × 30 = 240 XP → crosses the 50-per-level thresholds up to MaxLevel 5. ──
            for (int i = 1; i <= 8; i++)
            {
                int e = w.Create(V(i, 0, 0), Faction.Neutral, Fixed.FromInt(1), Fixed.FromInt(3));
                w.XpBounty[e] = Fixed.FromInt(30);
                w.CommandState[e] = UnitCommand.Stop; // stand still; 0 damage → passive
            }

            return hero;
        }

        private static FixedVec3 V(int x, int y, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));
    }
}
