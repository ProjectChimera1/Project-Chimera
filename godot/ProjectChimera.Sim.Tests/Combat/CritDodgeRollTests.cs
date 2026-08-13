#nullable enable
using System.Collections.Generic;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Core.Stats;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// Story 15-24b — the deterministic combat dice. Pins the five load-bearing claims:
    ///   • ZERO-DICE NEUTRALITY: a full fight with no crit/dodge stats consumes NO SimRng draw — the stream
    ///     (and therefore every recorded golden) is byte-identical to pre-15-24b;
    ///   • the DRAW BUDGET is exact and countable (SplitMix64 advances state by exactly one gamma per draw);
    ///   • DRAW ORDER is crit-at-commit THEN dodge-at-arrival, pinned by an outcome that flips if the order
    ///     ever swaps;
    ///   • crit MATH: damage × (1.5 base + crit_multiplier Σ), sealed into a projectile's spawn snapshot;
    ///   • dodge SEMANTICS: a weapon-only, victim-side full negation (no damage, target kept, on-hit skipped,
    ///     AttackDodged pushed) that splash/ability damage can never roll.
    /// Seeds are FOUND by deterministic search over the probe-able SplitMix64 stream (a scratch SimRng seeded
    /// with the live state reproduces the exact upcoming draws), never tuned magic numbers.
    /// </summary>
    public class CritDodgeRollTests
    {
        /// <summary>SplitMix64's per-draw state increment — duplicated from <see cref="SimRng"/> (private
        /// there) and SELF-VERIFIED by <see cref="DrawBudget_GammaPinHolds"/> so the copies cannot drift.</summary>
        private const ulong Gamma = 0x9E3779B97F4A7C15UL;

        /// <summary>Exact draw count between two stream states: state advances by exactly one gamma per draw
        /// (mod 2^64), so <c>draws = (after − before) × gamma⁻¹ mod 2^64</c>. Plain division would be WRONG
        /// past one draw — 2×gamma already wraps 64 bits. Gamma is odd, so its modular inverse exists and the
        /// count is exact for any number of draws; computed by Newton's iteration (each step doubles the
        /// correct low bits: 5 steps ≥ 64).</summary>
        private static ulong DrawsBetween(ulong before, ulong after)
        {
            ulong inv = Gamma; // seed: correct to 3 bits for any odd number
            for (int i = 0; i < 5; i++) inv = unchecked(inv * (2UL - unchecked(Gamma * inv)));
            return unchecked((after - before) * inv);
        }

        [Fact]
        public void DrawBudget_GammaPinHolds()
        {
            var rng = new SimRng(12345UL);
            ulong s0 = rng.State;
            rng.NextRaw();
            Assert.Equal(1UL, DrawsBetween(s0, rng.State)); // one draw == exactly one gamma — the counting basis
            rng.NextRaw(); rng.NextRaw();
            Assert.Equal(3UL, DrawsBetween(s0, rng.State)); // and the wrap-safe inverse counts past one (2×gamma overflows 64 bits)
        }

        // ── Harness ──────────────────────────────────────────────────────────────────────────────────────

        private static SimulationHost Host()
        {
            var host = SimulationHost.Create(
                NullLogSink.Instance, new FactionRegistry(2), new FactionDefinition(), new FactionDefinition());
            host.ScenarioDirector.LoadScenario(new ScenarioData());
            return host;
        }

        /// <summary>A melee attacker parked in range of (and force-attacking) <paramref name="target"/>.</summary>
        private static int Fighter(EntityWorld w, Fixed atk, int target)
        {
            int id = w.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.BaseAttackDamage[id] = atk; w.EffectiveAttackDamage[id] = atk;
            w.AttackRange[id] = Fixed.FromInt(3);
            w.AttackSpeed[id] = Fixed.FromInt(1000); // one swing inside any test window
            w.CommandState[id] = UnitCommand.AttackTarget;
            w.AttackTarget[id] = w.PackRef(target);
            w.CommandTarget[id] = w.PackRef(target);
            return id;
        }

        private static int Victim(EntityWorld w, int hp)
        {
            int id = w.Create(new FixedVec3(Fixed.FromInt(2), Fixed.Zero, Fixed.Zero), Faction.Player2, Fixed.FromInt(hp), Fixed.FromInt(3));
            w.BaseAttackDamage[id] = Fixed.Zero; w.EffectiveAttackDamage[id] = Fixed.Zero;
            return id;
        }

        private static void Grant(SimulationHost host, int id, StatId stat, Fixed value)
        {
            var mod = new Modifier(900 + (int)stat, -1, StackRule.Refresh, 1,
                StatVocabulary.Canonicalize(new List<StatDelta> { new StatDelta(stat, value) }),
                StatusFlags.None, null, 0);
            host.Modifiers.Apply(id, mod, id, host.World.FactionOf[id]);
        }

        /// <summary>Deterministically find a seed whose upcoming draw sequence satisfies
        /// <paramref name="wanted"/> (probing a SCRATCH SimRng — the live stream is untouched).</summary>
        private static ulong FindSeed(System.Func<SimRng, bool> wanted)
        {
            for (ulong seed = 1; seed < 4096; seed++)
                if (wanted(new SimRng(seed))) return seed;
            throw new Xunit.Sdk.XunitException("no seed under 4096 satisfies the wanted draw shape — loosen the predicate");
        }

        // ── Zero-dice neutrality: the golden-movement gate ───────────────────────────────────────────────

        [Fact]
        public void FightWithoutDiceStats_ConsumesNoDraw_TheRngStreamIsUntouched()
        {
            var host = Host();
            var w = host.World;
            int victim = Victim(w, 1000);
            Fighter(w, Fixed.FromInt(10), victim);

            ulong before = w.Rng.State;
            for (int i = 0; i < 30; i++) host.StepOnce();

            Assert.True(w.Health[victim] < Fixed.FromInt(1000), "non-vacuity: the fighter actually landed a hit");
            Assert.Equal(0UL, DrawsBetween(before, w.Rng.State)); // NO draw — pre-15-24b goldens byte-identical
        }

        // ── Crit ─────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void GuaranteedCrit_MultipliesBy1_5_AndConsumesExactlyOneDraw()
        {
            var host = Host();
            var w = host.World;
            int victim = Victim(w, 1000);
            int fighter = Fighter(w, Fixed.FromInt(10), victim);
            Grant(host, fighter, StatId.CritChance, Fixed.One); // chance 1.0 — NextFixed < 1.0 always

            ulong before = w.Rng.State;
            host.StepOnce(); // the single swing

            Assert.Equal(1UL, DrawsBetween(before, w.Rng.State)); // crit draw only (victim has no dodge → no second draw)
            Assert.Equal(Fixed.FromInt(1000 - 15).Raw, w.Health[victim].Raw); // 10 × 1.5 (Normal vs Unarmored ×1, armor 0)
        }

        [Fact]
        public void CritMultiplierStat_AddsToTheBase()
        {
            var host = Host();
            var w = host.World;
            int victim = Victim(w, 1000);
            int fighter = Fighter(w, Fixed.FromInt(10), victim);
            Grant(host, fighter, StatId.CritChance, Fixed.One);
            Grant(host, fighter, StatId.CritMultiplier, Fixed.Half); // total ×2.0

            host.StepOnce();
            Assert.Equal(Fixed.FromInt(1000 - 20).Raw, w.Health[victim].Raw);
        }

        [Fact]
        public void ProjectileCrit_IsSealedAtLaunch_NotReRolledAtImpact()
        {
            var host = Host();
            var w = host.World;
            int victim = Victim(w, 1000);
            int fighter = Fighter(w, Fixed.FromInt(10), victim);
            w.Delivery[fighter] = AttackDelivery.Projectile; // ranged — damage snapshots at spawn
            Grant(host, fighter, StatId.CritChance, Fixed.One);

            ulong before = w.Rng.State;
            host.StepOnce(); // launch tick: the ONE crit draw happens here
            Assert.Equal(1UL, DrawsBetween(before, w.Rng.State));

            ulong afterLaunch = w.Rng.State;
            for (int i = 0; i < 30 && w.Health[victim].Raw == Fixed.FromInt(1000).Raw; i++) host.StepOnce();

            Assert.Equal(Fixed.FromInt(1000 - 15).Raw, w.Health[victim].Raw); // the critted snapshot arrived
            // Flight + impact drew nothing further beyond later swings' own crit draws; assert the FIRST impact
            // applied the sealed value rather than re-rolling: every draw after launch belongs to a LAUNCH
            // (one per swing), never an impact — with AttackSpeed=1000s there was no second swing, so:
            Assert.Equal(0UL, DrawsBetween(afterLaunch, w.Rng.State));
        }

        // ── Dodge ────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void SuccessfulDodge_NegatesTheHit_KeepsTheAttackerSwinging_AndPushesTheCue()
        {
            var host = Host();
            var w = host.World;
            // First upcoming draw must land under the 0.75 dodge cap → the dodge SUCCEEDS deterministically.
            w.Rng.Seed(FindSeed(r => r.NextFixed().Raw < StatVocabulary.DodgeChanceSumMaxRaw));
            int victim = Victim(w, 1000);
            int fighter = Fighter(w, Fixed.FromInt(10), victim);
            Grant(host, victim, StatId.DodgeChance, Fixed.One); // clamps to the 0.75 cap

            Assert.Equal(StatVocabulary.DodgeChanceSumMaxRaw, w.EffectiveDodgeChance[victim].Raw); // the cap holds

            ulong before = w.Rng.State;
            int eventsBefore = host.CombatEvents.Count;
            host.StepOnce();

            Assert.Equal(1UL, DrawsBetween(before, w.Rng.State));            // exactly the dodge draw
            Assert.Equal(Fixed.FromInt(1000).Raw, w.Health[victim].Raw);     // full negation — no damage at all
            Assert.NotEqual(-1, w.AttackTarget[fighter]);                    // attacker keeps its target (keeps swinging)
            bool sawDodge = false;
            for (int e = eventsBefore; e < host.CombatEvents.Count; e++)
                if (host.CombatEvents.Get(e).Type == CombatEventType.AttackDodged) sawDodge = true;
            Assert.True(sawDodge, "the presentation cue must ride the (unfolded) event queue");
        }

        [Fact]
        public void FailedDodge_ConsumesTheDraw_AndTheHitLands()
        {
            var host = Host();
            var w = host.World;
            // First upcoming draw must land ON/ABOVE the cap → the dodge FAILS deterministically.
            w.Rng.Seed(FindSeed(r => r.NextFixed().Raw >= StatVocabulary.DodgeChanceSumMaxRaw));
            int victim = Victim(w, 1000);
            Fighter(w, Fixed.FromInt(10), victim);
            Grant(host, victim, StatId.DodgeChance, Fixed.One);

            ulong before = w.Rng.State;
            host.StepOnce();

            Assert.Equal(1UL, DrawsBetween(before, w.Rng.State)); // the draw happened either way — stream parity
            Assert.Equal(Fixed.FromInt(1000 - 10).Raw, w.Health[victim].Raw);
        }

        [Fact]
        public void NonWeaponDamage_NeverRolls_WhateverTheVictimsDodge()
        {
            // Direct Apply calls — the provenance gate itself. Splash and every effect-graph damage leaf
            // construct their DamageContext with the default isWeaponHit:false, which is exactly this shape.
            var w = new EntityWorld();
            int victim = w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));
            w.EffectiveDodgeChance[victim] = Fixed.FromRaw(StatVocabulary.DodgeChanceSumMaxRaw); // dodge present

            ulong before = w.Rng.State;
            var nonWeapon = new DamageContext(w, victim, ArmorType.Unarmored, Faction.Player1, DamageTable.Default, null, null);
            DamageResolver.Apply(in nonWeapon, Fixed.FromInt(10), DamageType.Normal);

            Assert.Equal(0UL, DrawsBetween(before, w.Rng.State));            // NO draw for non-weapon damage
            Assert.Equal(Fixed.FromInt(90).Raw, w.Health[victim].Raw);       // and the damage lands in full
        }

        // ── Draw order: crit-at-commit BEFORE dodge-at-arrival ───────────────────────────────────────────

        [Fact]
        public void DrawOrder_IsCritThenDodge_PinnedByAnOrderSensitiveOutcome()
        {
            // Find a seed where draw1 succeeds as a CRIT (d1 < crit 1.0 — always) AND as-a-dodge would FAIL
            // (d1 >= dodge cap), while draw2 succeeds as a DODGE (d2 < cap). Correct order (crit d1, dodge d2):
            // the hit is dodged → victim untouched. Swapped order (dodge d1, crit d2): the dodge FAILS and the
            // hit lands. The victim's Health therefore pins the order.
            ulong seed = FindSeed(r =>
            {
                Fixed d1 = r.NextFixed(); Fixed d2 = r.NextFixed();
                return d1.Raw >= StatVocabulary.DodgeChanceSumMaxRaw && d2.Raw < StatVocabulary.DodgeChanceSumMaxRaw;
            });

            var host = Host();
            var w = host.World;
            w.Rng.Seed(seed);
            int victim = Victim(w, 1000);
            int fighter = Fighter(w, Fixed.FromInt(10), victim);
            Grant(host, fighter, StatId.CritChance, Fixed.One);
            Grant(host, victim, StatId.DodgeChance, Fixed.One); // clamped 0.75

            ulong before = w.Rng.State;
            host.StepOnce();

            Assert.Equal(2UL, DrawsBetween(before, w.Rng.State));        // both dice drew — the stream advances identically whatever the outcomes
            Assert.Equal(Fixed.FromInt(1000).Raw, w.Health[victim].Raw); // dodged ⇒ crit-first order held
        }

        // ── Clamps ───────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void ChanceClamps_HoldAtTheRegistryBounds()
        {
            var host = Host();
            var w = host.World;
            int u = Victim(w, 100);
            Grant(host, u, StatId.CritChance, Fixed.FromInt(5));      // → 1.0
            Grant(host, u, StatId.DodgeChance, Fixed.FromInt(5));     // → 0.75
            Grant(host, u, StatId.CritMultiplier, Fixed.FromInt(7));  // → clamped Σ ≤ 8 (single delta ok) — total ≤ 9.5

            Assert.Equal(StatVocabulary.CritChanceSumMaxRaw, w.EffectiveCritChance[u].Raw);
            Assert.Equal(StatVocabulary.DodgeChanceSumMaxRaw, w.EffectiveDodgeChance[u].Raw);
            Assert.Equal(EntityWorld.CritBaseMultiplierRaw + Fixed.FromInt(7).Raw, w.CritMultiplierOf(u).Raw);
        }
    }
}
