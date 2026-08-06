#nullable enable
using ProjectChimera.AI;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Persistence;
using ProjectChimera.Core.Sim;
using ProjectChimera.Sim.Tests.Golden;
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// DW-643 — <c>EffectiveAttackDamage</c> is non-negative on EVERY writer, and the two systems that partition the
    /// roster on it ask ONE shared predicate.
    ///
    /// <para>The defect. <see cref="CombatSystem"/> asked <c>EffectiveAttackDamage == Fixed.Zero</c> for
    /// "non-combatant"; <see cref="AiOpponentSystem"/> asked <c>&gt; Fixed.Zero</c> for "conscriptable". Those are
    /// exact complements only over NON-NEGATIVE damage, an invariant held solely by
    /// <see cref="ProjectChimera.Effects.ModifierSystem.RecomputeEntity"/>'s <c>Fixed.Max(Fixed.Zero, …)</c> floor.
    /// <see cref="SaveGameState.RestoreInto"/> rebuilds the SoA from raw ints and never runs that floor, so a
    /// negative value carried in a save landed in the world unclamped — and the two systems then classified the SAME
    /// unit two different ways: a combatant to combat (negative != zero) and a non-combatant to the AI.</para>
    ///
    /// <para>The closure is both halves the ledger offered: the restore path re-applies the zero-floor, AND both
    /// call sites route through <see cref="EntityWorld.CanDealDamage"/> / <see cref="EntityWorld.IsNonCombatant"/>,
    /// so the partition is structural instead of resting on a third system's clamp.</para>
    ///
    /// <para>Godot-free, <see cref="Fixed"/>-only, ascending-id. Nothing here re-records a golden: every value a
    /// well-formed save or a validated definition can produce is already non-negative, so the clamp is a no-op on it
    /// and <c>&lt;=</c> agrees with <c>==</c> everywhere the old spelling was reachable.</para>
    /// </summary>
    public class EffectiveAttackDamageClampTests
    {
        /// <summary>One tick at the 30 tps sim rate — only cooldowns read it; no assertion depends on the value.</summary>
        private static readonly Fixed Dt = Fixed.One / Fixed.FromInt(30);

        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        // ── Half 1: the shared predicate is a TOTAL, exact partition ─────────────────────────────────────

        [Theory]
        [InlineData(-1000)]
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(1000)]
        public void CanDealDamage_AndIsNonCombatant_AreExactComplements(int raw)
        {
            var w = new EntityWorld();
            int id = w.Create(V(0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.EffectiveAttackDamage[id] = Fixed.FromRaw(raw);

            Assert.NotEqual(w.CanDealDamage(id), w.IsNonCombatant(id));
            Assert.Equal(raw > 0, w.CanDealDamage(id));
        }

        // ── Half 2: combat and the AI agree about a NEGATIVE-damage unit ─────────────────────────────────

        /// <summary>A unit carrying a negative EffectiveAttackDamage — the state only an unclamped writer can produce.</summary>
        private static int NegativeDamageUnit(EntityWorld w, FixedVec3 pos, Faction f)
        {
            int id = w.Create(pos, f, Fixed.FromInt(100), Fixed.FromInt(3));
            w.EffectiveAttackDamage[id] = Fixed.FromInt(-5);
            w.AttackRange[id]  = Fixed.FromInt(2);   // an in-range enemy exists — only the damage term may stop it
            w.AttackSpeed[id]  = Fixed.Zero;         // cooldown never blocks: any engagement fires this very tick
            w.Delivery[id]     = AttackDelivery.Hitscan;
            w.DamageTypeOf[id] = DamageType.Normal;
            w.ArmorTypeOf[id]  = ArmorType.Unarmored;
            return id;
        }

        /// <summary>A plain melee combatant (range ≤ 2.5 ⇒ hitscan, so any damage lands inside one tick).</summary>
        private static int Combatant(EntityWorld w, FixedVec3 pos, Faction f, int dmg = 10)
        {
            int id = w.Create(pos, f, Fixed.FromInt(100), Fixed.FromInt(3));
            w.EffectiveAttackDamage[id] = Fixed.FromInt(dmg);
            w.AttackRange[id]  = Fixed.FromInt(2);
            w.AttackSpeed[id]  = Fixed.Zero;
            w.Delivery[id]     = AttackDelivery.Hitscan;
            w.DamageTypeOf[id] = DamageType.Normal;
            w.ArmorTypeOf[id]  = ArmorType.Unarmored;
            return id;
        }

        /// <summary>An idle, damage-bearing Player2 wave unit — conscriptable by construction
        /// (the NonCombatantCommandGateTests fixture, so the wave-launch decision is measured on known-good input).</summary>
        private static int AiWaveUnit(EntityWorld w, FixedVec3 pos)
        {
            int id = w.Create(pos, Faction.Player2, Fixed.FromInt(80), Fixed.FromInt(3));
            w.EffectiveAttackDamage[id] = Fixed.FromInt(6);
            w.AttackRange[id]  = Fixed.FromInt(2);
            w.AttackSpeed[id]  = Fixed.FromInt(1);
            w.DamageTypeOf[id] = DamageType.Siege;
            w.ArmorTypeOf[id]  = ArmorType.Medium;
            return id;
        }

        /// <summary>
        /// The CombatSystem half of the contradiction. Under the old <c>== Fixed.Zero</c> spelling a negative-damage
        /// idle unit fell THROUGH the non-combatant gate into <c>TickIdleCombat</c> and auto-acquired the adjacent
        /// enemy — a unit the AI simultaneously refused to count as a combat unit at all.
        /// </summary>
        [Fact]
        public void NegativeDamageUnit_IsANonCombatantToCombat_AndNeverAcquires()
        {
            var w      = new EntityWorld();
            var combat = new CombatSystem(new ProjectileStore());
            int u      = NegativeDamageUnit(w, V(0, 0), Faction.Player1);
            int enemy  = Combatant(w, V(1, 0), Faction.Player2);   // well inside u's range 2

            Fixed before = w.Health[enemy];
            combat.Tick(w, Dt);

            Assert.True(w.IsNonCombatant(u), "a negative-damage unit must be a NON-combatant to CombatSystem (DW-643)");
            Assert.Equal(-1, w.AttackTarget[u]);
            Assert.True((w.Flags[u] & EntityFlags.Attacking) == 0);
            Assert.Equal(before.Raw, w.Health[enemy].Raw);
        }

        /// <summary>The AI half, asserted against the SAME unit shape: it is not conscriptable, and it never was.</summary>
        [Fact]
        public void NegativeDamageUnit_IsNotConscriptableByTheAi()
        {
            SimulationHost host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2),
                                                        new FactionDefinition(), new FactionDefinition(),
                                                        damageTable: null, aiLevel: AiDifficulty.Normal);
            EntityWorld w = host.World;

            // DW-783 — something hostile for the wave to march AT. Before this batch the destination was the
            // hardcoded P1_BASE in every FFA match, so this fixture could launch a wave into a world holding no
            // enemy at all; the destination is now computed from the nearest hostile structure (else unit), so
            // without a target the wave correctly declines and the exclusion assertion below is never exercised.
            // Placed far from the wave so it is a destination only, never an engagement.
            w.Create(V(-40, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));

            // A full real wave (so the wave DOES launch and the assertion is about exclusion, not inertia) plus one
            // negative-damage unit sitting with it.
            int threshold = AiOpponentSystem.DifficultyProfile(AiDifficulty.Normal).AttackThreshold;
            var wave = new int[threshold];
            for (int i = 0; i < wave.Length; i++) wave[i] = AiWaveUnit(w, V(40, i * 2 - 4));
            int stray = NegativeDamageUnit(w, V(40, 20), Faction.Player2);

            host.Ai.Tick(w, Dt);

            foreach (int id in wave) Assert.Equal(UnitCommand.AttackMove, w.CommandState[id]); // the wave launched
            Assert.False(w.CanDealDamage(stray));
            Assert.Equal(UnitCommand.Idle, w.CommandState[stray]);   // …and left the stray behind
        }

        // ── Half 3: the save-restore overlay re-applies the zero-floor ───────────────────────────────────

        private sealed class Applied
        {
            public SimulationHost Host = null!;
            public ScenarioData Model = null!;
            public FactionDefinition?[] SlotDefs = null!;
        }

        /// <summary>A scenario-applied host — the shape both the capture side and the load side need (SaveLoadTests' idiom).</summary>
        private static Applied BuildApplied()
        {
            FactionDefinition faction = GoldenApplierScenario.BuildFaction();
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;
            slotDefs[(int)Faction.Player2] = faction;

            SimulationHost host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);
            ScenarioData model = GoldenApplierScenario.BuildModel();
            ValidationResult r = new ScenarioValidator().Validate(model);
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);
            return new Applied { Host = host, Model = model, SlotDefs = slotDefs };
        }

        /// <summary>The lowest live entity id — the deterministic ascending-id pick, no scenario-layout assumption.</summary>
        private static int FirstAlive(EntityWorld w)
        {
            for (int i = 0; i < w.HighWaterMark; i++) if (w.IsAlive(i)) return i;
            Assert.Fail("the applied scenario produced no live entity");
            return -1;
        }

        /// <summary>
        /// The actual DW-643 vector: a blob whose <c>EffAtkDmg</c> lane is negative (a corrupt/tampered save, or one
        /// written by a build that let an unclamped value through) must not land that value in the world. Restore runs
        /// no tick and a modifier-free entity is never recomputed, so nothing downstream would ever fix it.
        ///
        /// The negative is planted on the SOURCE world before capture, which is exactly what such a blob encodes —
        /// <c>CaptureFrom</c> copies <c>EffectiveAttackDamage[i].Raw</c> straight through.
        /// </summary>
        [Fact]
        public void Restore_ClampsANegativeEffectiveAttackDamageToZero()
        {
            Applied saved = BuildApplied();
            int id = FirstAlive(saved.Host.World);
            saved.Host.World.EffectiveAttackDamage[id] = Fixed.FromInt(-5);

            var table = CanonicalEffectDescriptorTable.Build(saved.Host.AbilityRegistry, saved.Host.ItemRegistry);
            SaveGameState state = SaveGameState.CaptureFrom(saved.Host, table);

            Applied load = BuildApplied();
            var loadTable = CanonicalEffectDescriptorTable.Build(load.Host.AbilityRegistry, load.Host.ItemRegistry);
            state.RestoreInto(load.Host, loadTable, load.SlotDefs);

            Assert.Equal(Fixed.Zero.Raw, load.Host.World.EffectiveAttackDamage[id].Raw);
            // The whole point of the clamp: both systems now read the restored unit the same way.
            Assert.True(load.Host.World.IsNonCombatant(id));
            Assert.False(load.Host.World.CanDealDamage(id));
        }

        /// <summary>
        /// The fence on the clamp: it must not disturb a WELL-FORMED save. Every non-negative
        /// <c>EffectiveAttackDamage</c> — including a deliberate zero — round-trips bit-exact, which is why no golden
        /// moves and no checksum shifts.
        /// </summary>
        [Fact]
        public void Restore_LeavesNonNegativeEffectiveAttackDamageBitExact()
        {
            Applied saved = BuildApplied();
            EntityWorld sw = saved.Host.World;
            int first = FirstAlive(sw);
            sw.EffectiveAttackDamage[first] = Fixed.Zero;                 // the boundary value

            var expected = new int[sw.HighWaterMark];
            for (int i = 0; i < sw.HighWaterMark; i++) expected[i] = sw.EffectiveAttackDamage[i].Raw;

            var table = CanonicalEffectDescriptorTable.Build(saved.Host.AbilityRegistry, saved.Host.ItemRegistry);
            SaveGameState state = SaveGameState.CaptureFrom(saved.Host, table);

            Applied load = BuildApplied();
            var loadTable = CanonicalEffectDescriptorTable.Build(load.Host.AbilityRegistry, load.Host.ItemRegistry);
            state.RestoreInto(load.Host, loadTable, load.SlotDefs);

            for (int i = 0; i < expected.Length; i++)
                Assert.Equal(expected[i], load.Host.World.EffectiveAttackDamage[i].Raw);
        }

        /// <summary>
        /// <c>BaseAttackDamage</c> is deliberately NOT floored by the restore (it is the authored source, bounded to
        /// [0, …) by the definition validator, and ModifierSystem re-derives Effective from it). Pinned so a later
        /// change that silently starts flooring Base — and would therefore mask a corrupt blob at a different field —
        /// has to come past this test.
        /// </summary>
        [Fact]
        public void Restore_DoesNotFloorBaseAttackDamage()
        {
            Applied saved = BuildApplied();
            int id = FirstAlive(saved.Host.World);
            saved.Host.World.BaseAttackDamage[id] = Fixed.FromInt(-5);

            var table = CanonicalEffectDescriptorTable.Build(saved.Host.AbilityRegistry, saved.Host.ItemRegistry);
            SaveGameState state = SaveGameState.CaptureFrom(saved.Host, table);

            Applied load = BuildApplied();
            var loadTable = CanonicalEffectDescriptorTable.Build(load.Host.AbilityRegistry, load.Host.ItemRegistry);
            state.RestoreInto(load.Host, loadTable, load.SlotDefs);

            Assert.Equal(Fixed.FromInt(-5).Raw, load.Host.World.BaseAttackDamage[id].Raw);
        }
    }
}
