#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Persistence;
using ProjectChimera.Core.Sim;
using ProjectChimera.Effects;
using ProjectChimera.Navigation;
using ProjectChimera.Sim.Tests.Golden;
using Xunit;

namespace ProjectChimera.Sim.Tests.Persistence
{
    /// <summary>
    /// DW-692 — the THREE sibling effective stats <c>EffectiveMoveSpeed</c> / <c>EffectiveMaxHealth</c> /
    /// <c>EffectiveArmor</c> are re-applied through <c>ModifierSystem</c>'s zero-floor by
    /// <see cref="SaveGameState.RestoreInto"/>, exactly as DW-643 already did for <c>EffectiveAttackDamage</c>.
    ///
    /// <para>The defect. <c>ModifierSystem.RecomputeEntity</c> floors all FOUR effective stats at zero, but the
    /// save-restore overlay rebuilds the SoA from raw ints and is the ONE writer of these fields that does not run
    /// that floor. Restore runs no tick and a modifier-free entity is never recomputed, so a negative value carried
    /// in a corrupt / tampered / older-build blob lands in the live world and NOTHING downstream repairs it.
    /// DW-643 closed only the attack-damage lane, deliberately, because each sibling has its own downstream consumer
    /// to reason about first — which is what these tests pin, one field at a time:</para>
    ///
    /// <list type="bullet">
    ///   <item><b>MoveSpeed</b> — <see cref="MovementSystem"/> multiplies the unit vector toward the goal by it, so a
    ///     negative speed steers the unit directly AWAY from its own move target, forever.</item>
    ///   <item><b>MaxHealth</b> — it is the CEILING every Health write clamps into
    ///     (<c>Fixed.Clamp(h, 0, ceiling)</c> = <c>Max(0, Min(ceiling, h))</c>), so a negative ceiling parks the unit
    ///     at 0 HP while leaving <c>Health &gt; EffectiveMaxHealth</c> — an entity ABOVE its own ceiling, the
    ///     DW-491/DW-325 lethal-ceiling surface, and one that never trips the ceiling-collapse death rail because
    ///     that rail fires only on an above-zero → zero TRANSITION.</item>
    ///   <item><b>Armor</b> — <c>DamageTable.FinalDamage</c> is <c>max(0, amount*matrix − flatArmor)</c>, so a
    ///     negative armor is subtracted-negative: it ADDS damage to every hit rather than merely disabling the unit.</item>
    /// </list>
    ///
    /// <para>Godot-free, <see cref="Fixed"/>-only. Nothing here re-records a golden: every value a well-formed save
    /// or a validated definition can produce is already non-negative, so the floor is an exact no-op on one — which
    /// the bit-exact round-trip fence below pins directly.</para>
    /// </summary>
    public class EffectiveStatRestoreClampTests
    {
        /// <summary>One tick at the 30 tps sim rate.</summary>
        private static readonly Fixed Dt = Fixed.One / Fixed.FromInt(30);

        private sealed class Applied
        {
            public SimulationHost Host = null!;
            public FactionDefinition?[] SlotDefs = null!;
        }

        /// <summary>A scenario-applied host — the shape both the capture side and the load side need
        /// (the SaveLoadTests / EffectiveAttackDamageClampTests idiom).</summary>
        private static Applied BuildApplied()
        {
            FactionDefinition faction = GoldenApplierScenario.BuildFaction();
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;
            slotDefs[(int)Faction.Player2] = faction;

            SimulationHost host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);
            ValidationResult r = new ScenarioValidator().Validate(GoldenApplierScenario.BuildModel());
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);
            return new Applied { Host = host, SlotDefs = slotDefs };
        }

        /// <summary>The lowest live entity id — the deterministic ascending-id pick, no scenario-layout assumption.</summary>
        private static int FirstAlive(EntityWorld w)
        {
            for (int i = 0; i < w.HighWaterMark; i++) if (w.IsAlive(i)) return i;
            Assert.Fail("the applied scenario produced no live entity");
            return -1;
        }

        /// <summary>Capture <paramref name="saved"/> and overlay it onto a freshly applied host — the real disk-path
        /// shape (<c>CaptureFrom</c> copies each lane's <c>.Raw</c> straight through, which is exactly what a corrupt
        /// or tampered blob encodes).</summary>
        private static Applied RoundTrip(Applied saved)
        {
            var table = CanonicalEffectDescriptorTable.Build(saved.Host.AbilityRegistry, saved.Host.ItemRegistry);
            SaveGameState state = SaveGameState.CaptureFrom(saved.Host, table);

            Applied load = BuildApplied();
            var loadTable = CanonicalEffectDescriptorTable.Build(load.Host.AbilityRegistry, load.Host.ItemRegistry);
            state.RestoreInto(load.Host, loadTable, load.SlotDefs);
            return load;
        }

        // ── Field 1: EffectiveMoveSpeed ──────────────────────────────────────────────────

        [Fact]
        public void Restore_ClampsANegativeEffectiveMoveSpeedToZero()
        {
            Applied saved = BuildApplied();
            int id = FirstAlive(saved.Host.World);
            saved.Host.World.EffectiveMoveSpeed[id] = Fixed.FromInt(-4);

            Applied load = RoundTrip(saved);

            Assert.Equal(Fixed.Zero.Raw, load.Host.World.EffectiveMoveSpeed[id].Raw);
        }

        /// <summary>
        /// The consumer that makes the negative harmful, asserted through the REAL <see cref="MovementSystem"/>:
        /// seek is <c>toTarget.Normalized() * EffectiveMoveSpeed</c>, so an unclamped negative speed produces a
        /// velocity pointing AWAY from the move target (a negative dot with the direction it was ordered to travel) —
        /// the unit accelerates backwards and can never arrive. The unit is parked far from the applied scenario's
        /// other entities so no separation term can contribute to the velocity under test.
        /// </summary>
        [Fact]
        public void Restore_ANegativeMoveSpeedUnit_NeverSteersAwayFromItsMoveTarget()
        {
            Applied saved = BuildApplied();
            EntityWorld sw = saved.Host.World;
            int id = FirstAlive(sw);

            sw.Position[id]   = new FixedVec3(Fixed.FromInt(500), Fixed.Zero, Fixed.FromInt(500)); // alone out here
            sw.MoveTarget[id] = new FixedVec3(Fixed.FromInt(560), Fixed.Zero, Fixed.FromInt(500)); // +X, well past arrival
            sw.Flags[id]     |= EntityFlags.Moving;
            sw.CommandState[id] = UnitCommand.Move;
            sw.StatusFlagsOf[id] = StatusFlags.None;
            sw.EffectiveMoveSpeed[id] = Fixed.FromInt(-4);

            Applied load = RoundTrip(saved);
            EntityWorld lw = load.Host.World;

            new MovementSystem().Tick(lw, Dt);

            FixedVec3 toTarget = lw.MoveTarget[id] - lw.Position[id];
            Fixed dot = lw.Velocity[id].X * toTarget.X + lw.Velocity[id].Z * toTarget.Z;
            Assert.True(dot.Raw >= 0,
                "a restored unit must never be steered AWAY from its own move target (negative EffectiveMoveSpeed)");
        }

        // ── Field 2: EffectiveMaxHealth ──────────────────────────────────────────────────

        [Fact]
        public void Restore_ClampsANegativeEffectiveMaxHealthToZero()
        {
            Applied saved = BuildApplied();
            int id = FirstAlive(saved.Host.World);
            saved.Host.World.EffectiveMaxHealth[id] = Fixed.FromInt(-10);

            Applied load = RoundTrip(saved);

            Assert.Equal(Fixed.Zero.Raw, load.Host.World.EffectiveMaxHealth[id].Raw);
        }

        /// <summary>
        /// The consumer, asserted through the REAL <see cref="HealEffect"/> (whose body is the shared
        /// <c>Fixed.Clamp(h + amount, Fixed.Zero, EffectiveMaxHealth)</c> every HP writer uses): with a negative
        /// ceiling the clamp lands Health at 0 while the ceiling stays below it, leaving the entity permanently
        /// ABOVE its own maximum — an invariant no live path can produce and no death rail catches.
        /// </summary>
        [Fact]
        public void Restore_ANegativeCeilingUnit_NeverEndsUpAboveItsOwnMaxHealth()
        {
            Applied saved = BuildApplied();
            int id = FirstAlive(saved.Host.World);
            saved.Host.World.EffectiveMaxHealth[id] = Fixed.FromInt(-10);

            Applied load = RoundTrip(saved);
            EntityWorld lw = load.Host.World;

            var hash = new SpatialHash();
            hash.Rebuild(lw);
            var ctx = new EffectContext(lw, id, id, lw.FactionOf[id], DamageTable.Default, hash);
            new HealEffect(Fixed.FromInt(50)).Apply(in ctx);

            Assert.True(lw.Health[id].Raw <= lw.EffectiveMaxHealth[id].Raw,
                "a restored entity must never hold more Health than its own EffectiveMaxHealth ceiling");
        }

        // ── Field 3: EffectiveArmor ──────────────────────────────────────────────────────

        [Fact]
        public void Restore_ClampsANegativeEffectiveArmorToZero()
        {
            Applied saved = BuildApplied();
            int id = FirstAlive(saved.Host.World);
            saved.Host.World.EffectiveArmor[id] = Fixed.FromInt(-7);

            Applied load = RoundTrip(saved);

            Assert.Equal(Fixed.Zero.Raw, load.Host.World.EffectiveArmor[id].Raw);
        }

        /// <summary>
        /// The consumer, asserted through the REAL <see cref="DamageTable.FinalDamage"/> expression
        /// <c>DamageResolver</c> calls: armor is SUBTRACTED, so an unclamped negative armor AMPLIFIES every hit the
        /// restored unit takes. The restored unit must take no more than the unarmored baseline.
        /// </summary>
        [Fact]
        public void Restore_ANegativeArmorUnit_NeverTakesAmplifiedDamage()
        {
            Applied saved = BuildApplied();
            EntityWorld sw = saved.Host.World;
            int id = FirstAlive(sw);
            sw.ArmorTypeOf[id]    = ArmorType.Unarmored;
            sw.EffectiveArmor[id] = Fixed.FromInt(-7);

            Applied load = RoundTrip(saved);
            EntityWorld lw = load.Host.World;

            Fixed swing    = Fixed.FromInt(20);
            Fixed baseline = DamageTable.Default.FinalDamage(swing, DamageType.Normal, ArmorType.Unarmored, Fixed.Zero);
            Fixed actual   = DamageTable.Default.FinalDamage(swing, DamageType.Normal, lw.ArmorTypeOf[id],
                                                            lw.EffectiveArmor[id]);

            Assert.True(actual.Raw <= baseline.Raw,
                "a restored unit must never take MORE than unarmored damage (negative EffectiveArmor amplifies)");
        }

        // ── The fence: a WELL-FORMED save is bit-exact through all three floors ──────────

        /// <summary>
        /// The clamps must not disturb a well-formed save. Every non-negative value — including the deliberate zero
        /// boundary on each lane — round-trips bit-exact across the whole entity range, which is why no golden moves
        /// and no checksum shifts (all three fields are folded into <c>SimChecksum</c>).
        /// </summary>
        [Fact]
        public void Restore_LeavesNonNegativeEffectiveStatsBitExact()
        {
            Applied saved = BuildApplied();
            EntityWorld sw = saved.Host.World;
            int first = FirstAlive(sw);
            sw.EffectiveMoveSpeed[first] = Fixed.Zero;  // the boundary value on each lane
            sw.EffectiveMaxHealth[first] = Fixed.Zero;
            sw.EffectiveArmor[first]     = Fixed.Zero;

            int n = sw.HighWaterMark;
            var speed = new int[n]; var maxHp = new int[n]; var armor = new int[n];
            for (int i = 0; i < n; i++)
            {
                speed[i] = sw.EffectiveMoveSpeed[i].Raw;
                maxHp[i] = sw.EffectiveMaxHealth[i].Raw;
                armor[i] = sw.EffectiveArmor[i].Raw;
            }

            Applied load = RoundTrip(saved);
            EntityWorld lw = load.Host.World;

            for (int i = 0; i < n; i++)
            {
                Assert.Equal(speed[i], lw.EffectiveMoveSpeed[i].Raw);
                Assert.Equal(maxHp[i], lw.EffectiveMaxHealth[i].Raw);
                Assert.Equal(armor[i], lw.EffectiveArmor[i].Raw);
            }
        }

        /// <summary>
        /// The Base_ lanes are deliberately NOT floored — they are the authored source ModifierSystem re-derives
        /// Effective from, and the definition validator already bounds them at load, so flooring them here would only
        /// mask a corrupt blob at a different field (the DW-643 stance, held for all three siblings). Pinned so a
        /// later change that quietly starts flooring Base has to come past this test.
        /// </summary>
        [Fact]
        public void Restore_DoesNotFloorTheBaseLanes()
        {
            Applied saved = BuildApplied();
            EntityWorld sw = saved.Host.World;
            int id = FirstAlive(sw);
            sw.BaseMoveSpeed[id] = Fixed.FromInt(-4);
            sw.BaseMaxHealth[id] = Fixed.FromInt(-10);
            sw.BaseArmor[id]     = Fixed.FromInt(-7);

            Applied load = RoundTrip(saved);
            EntityWorld lw = load.Host.World;

            Assert.Equal(Fixed.FromInt(-4).Raw,  lw.BaseMoveSpeed[id].Raw);
            Assert.Equal(Fixed.FromInt(-10).Raw, lw.BaseMaxHealth[id].Raw);
            Assert.Equal(Fixed.FromInt(-7).Raw,  lw.BaseArmor[id].Raw);
        }
    }
}
