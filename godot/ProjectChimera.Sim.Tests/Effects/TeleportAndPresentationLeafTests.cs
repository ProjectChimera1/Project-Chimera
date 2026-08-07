#nullable enable
using System.Text.Json;
using ProjectChimera.Combat;            // DamageTable, CombatEventQueue, CombatEventType
using ProjectChimera.Core;              // EntityWorld, Fixed, FixedVec3, Faction, UnitCommand, EntityFlags, ElevationGrid, SimulationLoop
using ProjectChimera.Core.Definitions;  // ContentJson, CombatFeedbackProfile, FlashSpec, ShakeSpec, AbilityDefinition
using ProjectChimera.Effects;           // TeleportEffect, PlayVfxEffect, PlaySoundEffect, ShakeScreenEffect, EffectExecutor, EffectContext
using ProjectChimera.Multiplayer;       // OrderApplier, UnitOrder (the production cast entry, P5)
using ProjectChimera.Navigation;        // PathabilityGrid (wall-bypass, P2)
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 15.13 (DW-248) — behaviour + determinism for the four effect-vocabulary leaves that complete AR-8:
    /// the sim-mutating <see cref="TeleportEffect"/> and the checksum-neutral presentation leaves
    /// (<see cref="PlayVfxEffect"/> / <see cref="PlaySoundEffect"/> / <see cref="ShakeScreenEffect"/>). Net-new
    /// coverage, no golden file: Teleport relocates the CASTER deterministically and re-establishes entity
    /// consistency; the presentation leaves push exactly one non-folded <see cref="CombatEvent"/> and mutate zero sim
    /// state; a null event sink is a safe no-op; and every kind round-trips through <see cref="EffectNodeJsonConverter"/>.
    /// </summary>
    public class TeleportAndPresentationLeafTests
    {
        private static readonly FixedVec3 Origin = new FixedVec3(Fixed.FromInt(5), Fixed.Zero, Fixed.FromInt(7));

        private static int CreateCaster(EntityWorld w) =>
            w.Create(Origin, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));

        private static EffectContext GroundCtx(EntityWorld w, int caster, FixedVec3 point) =>
            new EffectContext(w, caster, primaryTargetId: -1, w.FactionOf[caster], DamageTable.Default,
                              targetPoint: point, hasTargetPoint: true);

        // ── Teleport: ground blink relocates the caster + resets movement ──────────────────────────────

        [Fact]
        public void Teleport_GroundCast_MovesCasterToPoint_AndResetsMovement()
        {
            var w = new EntityWorld();
            int id = CreateCaster(w);
            // Dirty the movement state so the reset is observable, not just coincidentally already-zero.
            w.Velocity[id]     = new FixedVec3(Fixed.FromInt(2), Fixed.Zero, Fixed.FromInt(2));
            w.Flags[id]       |= EntityFlags.Moving;
            w.CommandState[id] = UnitCommand.Move;
            w.Elevation[id]    = Fixed.FromInt(99); // bogus — must be re-sampled at the destination

            var point = new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.FromInt(20));
            new EffectExecutor().Run(new TeleportEffect(), GroundCtx(w, id, point));

            // Position becomes the point (Y flattened to 0).
            Assert.Equal(point.X.Raw, w.Position[id].X.Raw);
            Assert.Equal(Fixed.Zero.Raw, w.Position[id].Y.Raw);
            Assert.Equal(point.Z.Raw, w.Position[id].Z.Raw);
            // PrevPosition follows (no interpolation smear).
            Assert.Equal(point.X.Raw, w.PrevPosition[id].X.Raw);
            Assert.Equal(point.Z.Raw, w.PrevPosition[id].Z.Raw);
            // Movement reset: velocity zero, moving flag cleared, MoveTarget == dest, CommandState Idle.
            Assert.Equal(FixedVec3.Zero.X.Raw, w.Velocity[id].X.Raw);
            Assert.Equal(FixedVec3.Zero.Z.Raw, w.Velocity[id].Z.Raw);
            Assert.Equal(EntityFlags.None, w.Flags[id] & EntityFlags.Moving);
            Assert.Equal(point.X.Raw, w.MoveTarget[id].X.Raw);
            Assert.Equal(point.Z.Raw, w.MoveTarget[id].Z.Raw);
            Assert.Equal(UnitCommand.Idle, w.CommandState[id]);
            // Elevation re-sampled at the destination (the bogus 99 is overwritten with SampleElevation(dest)).
            Assert.Equal(w.SampleElevation(point.X, point.Z).Raw, w.Elevation[id].Raw);
        }

        [Fact]
        public void Teleport_IsDeterministic_AcrossTwoIdenticalRuns()
        {
            var point = new FixedVec3(Fixed.FromInt(13), Fixed.Zero, Fixed.FromInt(-4));

            static FixedVec3 Run(FixedVec3 p)
            {
                var w = new EntityWorld();
                int id = CreateCaster(w);
                new EffectExecutor().Run(new TeleportEffect(), GroundCtx(w, id, p));
                return w.Position[id];
            }

            FixedVec3 a = Run(point);
            FixedVec3 b = Run(point);
            Assert.Equal(a.X.Raw, b.X.Raw);
            Assert.Equal(a.Y.Raw, b.Y.Raw);
            Assert.Equal(a.Z.Raw, b.Z.Raw);
            Assert.Equal(point.X.Raw, a.X.Raw); // and it is genuinely the cast point
            Assert.Equal(point.Z.Raw, a.Z.Raw);
        }

        [Fact]
        public void Teleport_Charge_MovesCasterToLiveTargetPosition()
        {
            var w = new EntityWorld();
            int caster = CreateCaster(w);
            var targetPos = new FixedVec3(Fixed.FromInt(30), Fixed.Zero, Fixed.FromInt(12));
            int target = w.Create(targetPos, Faction.Neutral, Fixed.FromInt(50), Fixed.FromInt(3));

            // A TargetUnit cast: no ground point, PrimaryTargetId is the live non-caster target.
            var ctx = new EffectContext(w, caster, target, w.FactionOf[caster], DamageTable.Default);
            new EffectExecutor().Run(new TeleportEffect(), ctx);

            Assert.Equal(targetPos.X.Raw, w.Position[caster].X.Raw);
            Assert.Equal(targetPos.Z.Raw, w.Position[caster].Z.Raw);
            // The target itself never moves.
            Assert.Equal(targetPos.X.Raw, w.Position[target].X.Raw);
        }

        [Fact]
        public void Teleport_SelfCastWithNoDestination_IsNoOp()
        {
            var w = new EntityWorld();
            int id = CreateCaster(w);
            // Self cast: PrimaryTargetId == caster, no ground point ⇒ no destination.
            var ctx = new EffectContext(w, id, id, w.FactionOf[id], DamageTable.Default);
            new EffectExecutor().Run(new TeleportEffect(), ctx);

            Assert.Equal(Origin.X.Raw, w.Position[id].X.Raw);
            Assert.Equal(Origin.Z.Raw, w.Position[id].Z.Raw);
        }

        [Fact]
        public void Teleport_DeadCaster_IsNoOpAndDoesNotThrow()
        {
            var w = new EntityWorld();
            int id = CreateCaster(w);
            w.Destroy(id);
            var point = new FixedVec3(Fixed.FromInt(9), Fixed.Zero, Fixed.FromInt(9));

            var ex = Record.Exception(() => new EffectExecutor().Run(new TeleportEffect(), GroundCtx(w, id, point)));
            Assert.Null(ex);
            Assert.False(w.IsAlive(id));
        }

        // ── Teleport is PLACEMENT-class: it bypasses walls (P2) and re-samples elevation on a non-flat grid (P3) ──

        [Fact]
        public void Teleport_BypassesAWallBetweenOriginAndDestination_PlacementNotSweptStep()
        {
            var w = new EntityWorld();
            // A full N-S wall at cell column 64 (world X ∈ [0, 2)), exactly as MovementSystemBlockingTests builds it.
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            const int GS = PathabilityGrid.GRID_SIZE;
            for (int row = 0; row < GS; row++) mask[row * GS + 64] = true;
            var wall = new PathabilityGrid(mask);
            w.SetPathabilityGrid(wall);
            Assert.True(wall.IsBlocked(Fixed.Zero, Fixed.Zero), "the wall must actually block the segment (cell column 64).");

            // Caster WEST of the wall; destination due EAST, past it. A swept CheckedStep.Resolve move would hard-stop
            // at the origin side (X < 0); teleport must NOT.
            int id = w.Create(new FixedVec3(Fixed.FromInt(-10), Fixed.Zero, Fixed.Zero),
                              Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            var dest = new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.Zero);

            new EffectExecutor().Run(new TeleportEffect(), GroundCtx(w, id, dest));

            // Landed AT the destination, PAST the wall — not stopped short of it.
            Assert.Equal(dest.X.Raw, w.Position[id].X.Raw);
            Assert.Equal(dest.Z.Raw, w.Position[id].Z.Raw);
            Assert.True(w.Position[id].X > Fixed.Zero, "the caster must have crossed the wall (X > 0), not stopped short.");
        }

        [Fact]
        public void Teleport_ReSamplesElevation_AtTheDestination_OnANonFlatGrid()
        {
            var w = new EntityWorld();
            // 2 columns × 1 row, 10 u/cell from world X=0: col0 (X ∈ [0,10)) = height 3, col1 (X ∈ [10,20)) = height 17.
            var grid = new ElevationGrid(
                new[] { Fixed.FromInt(3), Fixed.FromInt(17) }, width: 2, height: 1,
                worldMinX: Fixed.Zero, worldMinZ: Fixed.Zero, cellSize: Fixed.FromInt(10));
            w.SetElevationGrid(grid);

            int id = w.Create(new FixedVec3(Fixed.FromInt(5), Fixed.Zero, Fixed.Zero),
                              Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            Assert.Equal(Fixed.FromInt(3).Raw, w.Elevation[id].Raw); // spawned on the LOW cell

            var dest = new FixedVec3(Fixed.FromInt(15), Fixed.Zero, Fixed.Zero); // the HIGH cell
            new EffectExecutor().Run(new TeleportEffect(), GroundCtx(w, id, dest));

            // Elevation re-sampled AT the destination — not left at (nor re-sampled at) the origin's height.
            Assert.Equal(w.SampleElevation(dest.X, dest.Z).Raw, w.Elevation[id].Raw);
            Assert.Equal(Fixed.FromInt(17).Raw, w.Elevation[id].Raw);
            Assert.NotEqual(Fixed.FromInt(3).Raw, w.Elevation[id].Raw); // NOT the origin's elevation
        }

        [Fact]
        public void Teleport_Charge_FlattensDestinationYToZero()
        {
            // P6: the charge branch must flatten Y like the ground branch — a wholesale copy of the target's position
            // would smear a stray Y into the caster's Position (Position.Y is invariant-Zero across the sim).
            var w = new EntityWorld();
            int caster = CreateCaster(w);
            int target = w.Create(new FixedVec3(Fixed.FromInt(30), Fixed.Zero, Fixed.FromInt(12)),
                                  Faction.Neutral, Fixed.FromInt(50), Fixed.FromInt(3));
            // Force a non-zero Y onto the target's SoA slot so a non-flattening charge would be observably wrong.
            w.Position[target] = new FixedVec3(Fixed.FromInt(30), Fixed.FromInt(9), Fixed.FromInt(12));

            var ctx = new EffectContext(w, caster, target, w.FactionOf[caster], DamageTable.Default);
            new EffectExecutor().Run(new TeleportEffect(), ctx);

            Assert.Equal(Fixed.Zero.Raw, w.Position[caster].Y.Raw); // destination Y flattened
            Assert.Equal(Fixed.FromInt(30).Raw, w.Position[caster].X.Raw);
            Assert.Equal(Fixed.FromInt(12).Raw, w.Position[caster].Z.Raw);
        }

        // ── Real production cast path (P5): AbilityCastSystem drives a GroundPoint Teleport end-to-end ──────────

        [Fact]
        public void Teleport_ViaProductionCastPath_MovesCasterToTheGroundCastPoint()
        {
            // The REAL cast spine (a stand-in for the DW-882-blocked in-engine gate): OrderApplier stashes the ground
            // point into PendingCastPointX/Z, and AbilityCastSystem's GroundPoint branch builds the EffectContext with
            // PrimaryTargetId = -1 + TargetPoint = the click. This exercises that plumbing, not a hand-built context.
            var blink = new AbilityDefinition
            {
                Id = "blink", DisplayName = "Blink", Targeting = "GroundPoint",
                CostEnergy = Fixed.FromInt(10), Cooldown = Fixed.FromInt(5),
                EffectGraph = new TeleportEffect(),
            };
            var h = new CastHarness(blink);
            int caster = h.Caster("blink", energy: 50, pos: new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero));

            // GroundPoint cast: slot 0 in the wire byte, the ground point (10,0,20) in TargetX/TargetZ.
            OrderApplier.Apply(h.World,
                new UnitOrder(caster, UnitCommand.CastAbility, Fixed.FromInt(10), Fixed.FromInt(20), slot: 0),
                Faction.Player1);
            h.Cast.Tick(h.World, SimulationLoop.FixedDt);

            Assert.Equal(Fixed.FromInt(10).Raw, h.World.Position[caster].X.Raw);
            Assert.Equal(Fixed.Zero.Raw,        h.World.Position[caster].Y.Raw);
            Assert.Equal(Fixed.FromInt(20).Raw, h.World.Position[caster].Z.Raw);
            Assert.True(h.Cooldown(caster) > 0, "the ground cast resolved (cooldown started).");
        }

        // ── Presentation leaves: push exactly one non-folded event, mutate no sim state ─────────────────

        private static CombatFeedbackProfile SampleProfile() => new CombatFeedbackProfile
        {
            HitFlash     = new FlashSpec { ColorRgb = new[] { 0.2f, 0.4f, 0.9f }, Scale = 1.3f, DurationSec = 0.25f },
            ImpactSoundId = "zap",
            Shake        = new ShakeSpec { DurationSec = 0.15f, Strength = 0.3f },
        };

        [Theory]
        [InlineData("play_vfx")]
        [InlineData("play_sound")]
        [InlineData("shake_screen")]
        public void PresentationLeaf_PushesExactlyOneEvent_CarryingProfile_AndMutatesNoSimState(string kind)
        {
            var w = new EntityWorld();
            int id = CreateCaster(w);
            Fixed healthBefore = w.Health[id];
            var events = new CombatEventQueue();
            CombatFeedbackProfile profile = SampleProfile();

            LeafEffect leaf = kind switch
            {
                "play_vfx"     => new PlayVfxEffect(profile),
                "play_sound"   => new PlaySoundEffect(profile),
                _              => new ShakeScreenEffect(profile),
            };
            CombatEventType expected = kind switch
            {
                "play_vfx"     => CombatEventType.PlayVfx,
                "play_sound"   => CombatEventType.PlaySound,
                _              => CombatEventType.ShakeScreen,
            };

            var ctx = new EffectContext(w, id, id, w.FactionOf[id], DamageTable.Default, events: events);
            new EffectExecutor().Run(leaf, ctx);

            // Exactly one event of the right type, carrying the SAME profile reference, at the caster's position.
            Assert.Equal(1, events.Count);
            CombatEvent evt = events.Get(0);
            Assert.Equal(expected, evt.Type);
            Assert.Same(profile, evt.Feedback);
            Assert.Equal(Origin.X.Raw, evt.Position.X.Raw);
            Assert.Equal(Origin.Z.Raw, evt.Position.Z.Raw);
            // Zero folded sim mutation.
            Assert.Equal(healthBefore.Raw, w.Health[id].Raw);
            Assert.Equal(Origin.X.Raw, w.Position[id].X.Raw);
            Assert.Equal(Origin.Z.Raw, w.Position[id].Z.Raw);
            // These cues are ambient (must not draw on the notification reserve).
            Assert.True(CombatEventQueue.IsAmbient(expected));
        }

        [Fact]
        public void PresentationLeaf_NullEventsSink_IsSafeNoOp()
        {
            var w = new EntityWorld();
            int id = CreateCaster(w);
            var ctx = new EffectContext(w, id, id, w.FactionOf[id], DamageTable.Default); // events == null
            var ex = new EffectExecutor();

            Assert.Null(Record.Exception(() => ex.Run(new PlayVfxEffect(SampleProfile()), ctx)));
            Assert.Null(Record.Exception(() => ex.Run(new PlaySoundEffect(SampleProfile()), ctx)));
            Assert.Null(Record.Exception(() => ex.Run(new ShakeScreenEffect(null), ctx)));
            Assert.True(w.IsAlive(id));
            Assert.Equal(Origin.X.Raw, w.Position[id].X.Raw); // no sim state touched
        }

        // ── JSON round-trip: read → write → read is byte-stable and type-preserving for every new kind ──

        [Fact] public void Teleport_RoundTrips()                => AssertRoundTrips(new TeleportEffect());
        [Fact] public void TeleportWithRequireTag_RoundTrips()  => AssertRoundTrips(new TeleportEffect(UnitTag.Organic));
        [Fact] public void PlayVfx_RoundTrips()                 => AssertRoundTrips(new PlayVfxEffect(SampleProfile()));
        [Fact] public void PlayVfxNoPayload_RoundTrips()        => AssertRoundTrips(new PlayVfxEffect(null));
        [Fact] public void PlaySound_RoundTrips()               => AssertRoundTrips(new PlaySoundEffect(SampleProfile()));
        [Fact] public void ShakeScreen_RoundTrips()             => AssertRoundTrips(new ShakeScreenEffect(SampleProfile()));

        /// <summary>Serialize → deserialize → serialize: the re-serialization must be byte-identical (the graph
        /// survives), and the deserialized node must be the same runtime kind. Payload-bearing kinds additionally
        /// keep their presentation payload across the trip.</summary>
        private static void AssertRoundTrips(EffectNode node)
        {
            string json1 = JsonSerializer.Serialize(node, ContentJson.Options);
            EffectNode? back = JsonSerializer.Deserialize<EffectNode>(json1, ContentJson.Options);
            Assert.NotNull(back);
            Assert.Equal(node.GetType(), back!.GetType());
            string json2 = JsonSerializer.Serialize(back, ContentJson.Options);
            Assert.Equal(json1, json2);

            // Payload survives the trip (proves feedback was parsed, not silently dropped).
            switch (node)
            {
                case PlaySoundEffect ps when ps.Feedback is not null:
                    Assert.Equal(ps.Feedback.ImpactSoundId, ((PlaySoundEffect)back).Feedback!.ImpactSoundId);
                    break;
                case PlayVfxEffect pv when pv.Feedback is not null:
                    Assert.NotNull(((PlayVfxEffect)back).Feedback!.HitFlash);
                    break;
                case ShakeScreenEffect ss when ss.Feedback is not null:
                    Assert.NotNull(((ShakeScreenEffect)back).Feedback!.Shake);
                    break;
            }
        }
    }
}
