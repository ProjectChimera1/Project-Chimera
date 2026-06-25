#nullable enable
using System;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Navigation
{
    /// <summary>
    /// Story 1.13 (DG-2 / FR-54) — MovementSystem separation behaviour: the moving-vs-idle bias (AC1), the
    /// per-pair summed-radii contact threshold (AC2b), the push-beats-yield rule (AC2c), and the missing/&lt;=0
    /// collision_radius default (AC3). Each test builds a small <see cref="EntityWorld"/> + <see cref="MovementSystem"/>
    /// directly — no Godot, no SimulationHost — and authors all state in <see cref="Fixed"/> (FromFloat only to
    /// CONSTRUCT inputs), ascending-id, so they run on every OS including the WSL cross-platform leg.
    ///
    /// Engine note: MovementSystem updates <c>Position[i]</c> in-loop, so two MUTUALLY-interacting units
    /// contaminate each other within one tick (deterministic, ascending-id). The exact AC1 asymmetry tests
    /// therefore use a FIXED HoldPosition "wall" as the neighbour (it never moves → contamination-free, exact
    /// magnitudes); the two-mobile-unit symmetry tests assert opposite directions + a comparable-magnitude band.
    /// </summary>
    public class SeparationBiasTests
    {
        private static readonly Fixed Dt = Fixed.One / Fixed.FromInt(30); // one 30 tps tick

        // ── AC1a / AC6e — a moving unit yields LESS than an idle unit against the same fixed neighbour ──────────

        [Fact]
        public void MovingUnit_IsDisplacedLess_ThanIdleUnit_AgainstSameWall()
        {
            // World A: an IDLE unit beside a fixed wall — full separation push (bias ×1.0).
            var wIdle = new EntityWorld();
            int idle  = Unit(wIdle, V(0, 0, 0));
            Wall(wIdle, V(1, 0, 0)); // HoldPosition → never moves → contamination-free neighbour
            new MovementSystem().Tick(wIdle, Dt);
            Fixed idleDx = wIdle.Position[idle].X; // started at X=0, so this IS the delta

            // World B: a MOVING unit (seek purely +Z so its X-delta is PURELY separation) beside the same wall.
            var wMove  = new EntityWorld();
            int moving = Unit(wMove, V(0, 0, 0));
            wMove.MoveTarget[moving] = V(0, 0, 2); // pure +Z seek — no X component from seeking
            wMove.Flags[moving] |= EntityFlags.Moving;
            Wall(wMove, V(1, 0, 0));
            new MovementSystem().Tick(wMove, Dt);
            Fixed moveDx = wMove.Position[moving].X;

            // Both are pushed away from the wall (−X); the IDLE unit is displaced MORE (the moving unit's
            // path-following is damped by MOVING_SEPARATION_BIAS). A symmetric/unbiased model would make these
            // equal — so this is also the AC6e "removing the bias changes hashed truth" behavioural proof.
            Assert.True(idleDx.Raw < 0, "idle unit should be pushed in −X");
            Assert.True(moveDx.Raw < 0, "moving unit should be pushed in −X");
            Assert.True(Fixed.Abs(idleDx) > Fixed.Abs(moveDx),
                $"idle |Δx| ({idleDx}) must exceed moving |Δx| ({moveDx}) — the moving-vs-idle bias.");
        }

        // ── AC1b — same-state pairs split symmetrically (opposite directions, comparable magnitude) ─────────────

        [Fact]
        public void TwoIdleUnits_SplitSymmetrically_OppositeAndComparable()
            => AssertSymmetricSplit(moving: false);

        [Fact]
        public void TwoMovingUnits_SplitSymmetrically_OppositeAndComparable()
            => AssertSymmetricSplit(moving: true);

        private static void AssertSymmetricSplit(bool moving)
        {
            var w = new EntityWorld();
            int a = Unit(w, V(0, 0, 0));
            int b = Unit(w, V(1, 0, 0));
            if (moving)
            {
                // Identical pure-+Z seek for BOTH, so only the X axis carries the (symmetric) separation.
                w.MoveTarget[a] = V(0, 0, 2); w.Flags[a] |= EntityFlags.Moving;
                w.MoveTarget[b] = V(1, 0, 2); w.Flags[b] |= EntityFlags.Moving;
            }
            new MovementSystem().Tick(w, Dt);

            Fixed da = w.Position[a].X - Fixed.Zero;          // a started at X=0
            Fixed db = w.Position[b].X - Fixed.FromInt(1);    // b started at X=1

            Assert.True(da.Raw < 0 && db.Raw > 0, "the pair must split in opposite directions along X");
            // Same-state pair is NOT differentially biased, so |Δa| ≈ |Δb| (within 25% — the small inequality is
            // the engine's deterministic in-tick sequential update, NOT the bias, which would make it ~2:1).
            long ra = Math.Abs(da.Raw), rb = Math.Abs(db.Raw);
            Assert.True(ra * 5 >= rb * 4 && rb * 5 >= ra * 4,
                $"same-state split should be near-symmetric (|Δa|={da}, |Δb|={db}); a 2:1 ratio would mean the bias leaked into a same-state pair.");
        }

        // ── AC2b — per-pair contact is the SUMMED radii, not the flat query radius ──────────────────────────────

        [Fact]
        public void SummedRadii_SmallUnitsDoNotContactAtRange_DefaultUnitsDo()
        {
            FixedVec3 aPos = V(0, 0, 0);
            FixedVec3 bPos = new FixedVec3(Fixed.One + Fixed.Half, Fixed.Zero, Fixed.Zero); // X = 1.5

            // Small radii: 0.5 + 0.5 = 1.0 contact < 1.5 distance → NO separation (though within the 2.0 query).
            var wSmall = new EntityWorld();
            int s0 = Unit(wSmall, aPos); int s1 = Unit(wSmall, bPos);
            wSmall.CollisionRadius[s0] = Fixed.Half; wSmall.CollisionRadius[s1] = Fixed.Half;
            new MovementSystem().Tick(wSmall, Dt);
            AssertUnmoved(wSmall, s0, aPos);
            AssertUnmoved(wSmall, s1, bPos);

            // Default radii: 1.0 + 1.0 = 2.0 contact > 1.5 distance → they DO separate (same positions).
            var wDef = new EntityWorld();
            int d0 = Unit(wDef, aPos); int d1 = Unit(wDef, bPos); // CollisionRadius defaults to 1.0 in Create
            new MovementSystem().Tick(wDef, Dt);
            Assert.True(wDef.Position[d0] != aPos || wDef.Position[d1] != bPos,
                "default-radius units at distance 1.5 should separate (summed contact 2.0 > 1.5).");
        }

        // ── AC2c — a Push unit is not displaced by a Yield neighbour; the Yield unit still moves ────────────────

        [Fact]
        public void PushUnit_NotDisplacedByYieldNeighbour_YieldUnitMoves()
        {
            var w = new EntityWorld();
            FixedVec3 pushPos = V(0, 0, 0), yieldPos = V(1, 0, 0);
            int push  = Unit(w, pushPos);  w.SeparationPriorityOf[push]  = SeparationPriority.Push;
            int yield = Unit(w, yieldPos); w.SeparationPriorityOf[yield] = SeparationPriority.Yield;

            new MovementSystem().Tick(w, Dt);

            AssertUnmoved(w, push, pushPos);                       // Push holds its ground
            Assert.True(w.Position[yield].X.Raw > yieldPos.X.Raw,  // Yield is shoved away (+X)
                "the Yield unit must still be pushed by the Push unit.");
        }

        [Fact]
        public void PushVsPush_AndYieldVsYield_BothSeparateNormally()
        {
            foreach (SeparationPriority p in new[] { SeparationPriority.Push, SeparationPriority.Yield })
            {
                var w = new EntityWorld();
                FixedVec3 p0 = V(0, 0, 0), p1 = V(1, 0, 0);
                int a = Unit(w, p0); w.SeparationPriorityOf[a] = p;
                int b = Unit(w, p1); w.SeparationPriorityOf[b] = p;
                new MovementSystem().Tick(w, Dt);
                Assert.True(w.Position[a].X.Raw < p0.X.Raw && w.Position[b].X.Raw > p1.X.Raw,
                    $"a {p}/{p} pair must separate normally (the skip rule applies ONLY to Push-vs-Yield).");
            }
        }

        // ── AC3 — Create defaults the radius; SpawnUnit clamps omitted/<=0/over-max to the documented bounds ────

        [Fact]
        public void Create_DefaultsSeparationFields()
        {
            var w = new EntityWorld();
            int id = Unit(w, V(0, 0, 0));
            Assert.Equal(EntityWorld.DEFAULT_COLLISION_RADIUS.Raw, w.CollisionRadius[id].Raw);
            Assert.Equal(SeparationPriority.Normal, w.SeparationPriorityOf[id]);
            Assert.Equal(UnitCategory.Melee, w.CategoryOf[id]);
        }

        [Fact]
        public void SpawnUnit_ClampsCollisionRadius_OmittedNegativeZero_ToDefault_OverMax_ToMax()
        {
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2),
                                             new FactionDefinition(), new FactionDefinition());
            var applier = new ScenarioApplier(host, NullLogSink.Instance, new FactionDefinition?[5]);

            // Omitted (C# default 1.0), 0, and −3 all fall back to DEFAULT — no exception, no NaN (Fixed never
            // float-divides by a zero radius; the normalizer is the summed radii >= 2*DEFAULT > 0).
            int omitted = applier.SpawnUnit(new UnitDefinition { Id = "a" }, Faction.Player1, 0, 0);
            int zero    = applier.SpawnUnit(new UnitDefinition { Id = "b", CollisionRadius = 0f }, Faction.Player1, 0, 0);
            int neg     = applier.SpawnUnit(new UnitDefinition { Id = "c", CollisionRadius = -3f }, Faction.Player1, 0, 0);
            int one     = applier.SpawnUnit(new UnitDefinition { Id = "d", CollisionRadius = 1.0f }, Faction.Player1, 0, 0);
            int over    = applier.SpawnUnit(new UnitDefinition { Id = "e", CollisionRadius = 5.0f }, Faction.Player1, 0, 0);

            int expected = EntityWorld.DEFAULT_COLLISION_RADIUS.Raw;
            Assert.Equal(expected, host.World.CollisionRadius[omitted].Raw);
            Assert.Equal(expected, host.World.CollisionRadius[zero].Raw);
            Assert.Equal(expected, host.World.CollisionRadius[neg].Raw);
            Assert.Equal(expected, host.World.CollisionRadius[one].Raw);
            Assert.Equal(EntityWorld.MAX_COLLISION_RADIUS.Raw, host.World.CollisionRadius[over].Raw);
        }

        [Fact]
        public void DefaultRadiusUnit_AndExplicitDefault_SeparateIdentically()
        {
            FixedVec3 p0 = V(0, 0, 0), p1 = V(1, 0, 0);

            var wAuto = new EntityWorld();                 // radius from Create() default
            int a0 = Unit(wAuto, p0); int a1 = Unit(wAuto, p1);
            new MovementSystem().Tick(wAuto, Dt);

            var wExpl = new EntityWorld();                 // radius set explicitly to the same default value
            int e0 = Unit(wExpl, p0); wExpl.CollisionRadius[e0] = EntityWorld.DEFAULT_COLLISION_RADIUS;
            int e1 = Unit(wExpl, p1); wExpl.CollisionRadius[e1] = EntityWorld.DEFAULT_COLLISION_RADIUS;
            new MovementSystem().Tick(wExpl, Dt);

            Assert.Equal(wAuto.Position[a0].X.Raw, wExpl.Position[e0].X.Raw);
            Assert.Equal(wAuto.Position[a1].X.Raw, wExpl.Position[e1].X.Raw);
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────

        private static FixedVec3 V(int x, int y, int z)
            => new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));

        /// <summary>A bare idle unit (default radius 1.0, Normal priority) — speed 3, no attack stats.</summary>
        private static int Unit(EntityWorld w, FixedVec3 pos)
            => w.Create(pos, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));

        /// <summary>A fixed obstacle: HoldPosition anchors it so it never moves and never separates (Story 1.12),
        /// giving a contamination-free neighbour for exact-magnitude assertions.</summary>
        private static int Wall(EntityWorld w, FixedVec3 pos)
        {
            int id = Unit(w, pos);
            w.CommandState[id] = UnitCommand.HoldPosition;
            return id;
        }

        private static void AssertUnmoved(EntityWorld w, int id, FixedVec3 start)
        {
            Assert.Equal(start.X.Raw, w.Position[id].X.Raw);
            Assert.Equal(start.Z.Raw, w.Position[id].Z.Raw);
        }
    }
}
