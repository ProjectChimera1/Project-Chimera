#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Sim.Tests.Effects; // PassiveTestAbilities (the in-code passive defs)

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 2.6 (AC4) — the PASSIVE-ACTIVE golden scenario: the first golden whose recorded
    /// <see cref="SimChecksum"/> sequence exercises all three passive runtime drivers AND the new v8
    /// <c>EffectiveArmor</c> fold. Four stationary units run a fixed combat:
    ///   • id 0 (P1) — an AURA owner (<c>aura_guard</c>): every tick grants +5 armor to allies within radius 5.
    ///   • id 1 (P1) — an ARMORED ALLY in that radius (1000 HP, no attack): receives the aura's +5 armor and is the
    ///     attacker's target, so its <c>EffectiveArmor</c> is non-zero mid-match (the v8 fold has real signal).
    ///   • id 2 (P1) — a SELF-REGEN unit (<c>furnace_trickle</c>), pre-damaged to 50/100, far from combat: its
    ///     while-alive Persistent HoT (installed at the spawn seam) heals it up toward MaxHealth.
    ///   • id 3 (Neutral) — an ON-HIT attacker (<c>onhit_searing</c>): melee-attacks the ally each cadence; its base
    ///     hit AND its on-hit Magic rider are both reduced by the ally's aura-granted armor (DamageResolver's term).
    /// The attacker is NEUTRAL (never Player2) so the float-scoring <see cref="ProjectChimera.AI.AiOpponentSystem"/>
    /// no-ops; every hashed field is integer/<see cref="Fixed"/>, so this golden is CROSS-PLATFORM SAFE and compared
    /// on BOTH CI legs (NOT Windows-gated). Mirrors the <see cref="ModifierScenario"/> wiring; abilities are wired
    /// directly into the per-entity SoA (the <see cref="ScenarioApplier"/>/<see cref="EntityWorld.ApplyUnitDefinition"/>
    /// attach path is proven separately by <c>ApplyUnitDefinitionGuardTest</c>), and the self-passive install is
    /// triggered by firing the same <see cref="EntityWorld.OnUnitDefinitionApplied"/> seam the production spawn uses
    /// — keeping the scenario free of any float-derived value.
    /// </summary>
    public static class PassiveScenario
    {
        /// <summary>300 ticks = 10s at 30 tps; with ChecksumInterval = 1 that yields 300 samples (ticks 1..300).</summary>
        public const int DefaultTicks = 300;

        /// <summary>Construct a fresh, fully-wired sim running the three passive drivers + the armor term.</summary>
        public static GoldenHarness Build()
        {
            var registry = new AbilityRegistry(new[]
            {
                PassiveTestAbilities.AuraGuard(),     // aura: SearchArea(5, Ally) → ApplyModifier(+5 armor, Refresh)
                PassiveTestAbilities.OnHitSearing(),  // on_hit: Damage 15 Magic
                PassiveTestAbilities.FurnaceTrickle(),// while_alive: Persistent(Heal 2 every 5 ticks)
            });

            var host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(2),          // P1 + P2 active (P2 has no units → AI no-op)
                new FactionDefinition(),
                new FactionDefinition(),
                registry: registry);
            host.ChecksumInterval = 1;

            EntityWorld w = host.World;

            // id 0 — aura owner (P1), stationary, no attack (combat skips a zero-damage unit).
            int owner = w.Create(V(-10, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.Zero);
            w.AuraAbilityIndex[owner] = registry.IndexOf("aura_guard");

            // id 1 — armored ally (P1) in the aura radius; high HP so it survives the whole run, no attack.
            int ally = w.Create(V(-8, 0, 0), Faction.Player1, Fixed.FromInt(1000), Fixed.Zero);

            // id 2 — self-regen unit (P1), pre-damaged, far from combat. Fire the spawn-install seam directly (the
            // production path is ApplyUnitDefinition → OnUnitDefinitionApplied; here we wire the SoA + fire the seam
            // to install the Persistent HoT without any float-derived def value).
            int regen = w.Create(V(10, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.Zero);
            w.SelfPassiveAbilityIndex[regen] = registry.IndexOf("furnace_trickle");
            w.Health[regen] = Fixed.FromInt(50);
            w.OnUnitDefinitionApplied?.Invoke(regen);

            // id 3 — on-hit attacker (Neutral, an enemy of P1; NOT Player2 → AI stays a no-op). Melee (range 2 ≤ the
            // 2.5 threshold), 1s cadence, attacks the ally each cycle; its base + on-hit damage are armor-reduced.
            int attacker = w.Create(V(-6, 0, 0), Faction.Neutral, Fixed.FromInt(100), Fixed.Zero);
            w.BaseAttackDamage[attacker]      = Fixed.FromInt(10);
            w.EffectiveAttackDamage[attacker] = Fixed.FromInt(10);
            w.AttackRange[attacker]           = Fixed.FromInt(2);
            w.AttackSpeed[attacker]           = Fixed.FromInt(1);
            w.OnHitAbilityIndex[attacker]     = registry.IndexOf("onhit_searing");

            host.ScenarioDirector.LoadScenario(new ScenarioData()); // mirror MainScene lifecycle (empty → no-op)
            return new GoldenHarness(host, owner);
        }

        private static FixedVec3 V(int x, int y, int z) =>
            new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));
    }
}
