#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions; // ScenarioData, ScenarioPlayerSlot, FactionDefinition
using ProjectChimera.Core.Sim;          // SimulationHost, NullLogSink
using Xunit;

namespace ProjectChimera.Sim.Tests.Core
{
    /// <summary>
    /// Story 9.14 — the fog's shared-team vision: with the toggle ON, the local viewer's fog UNIONS every ALLIED
    /// faction's units' sight; with it OFF, only the viewer's own faction lights the fog. Fog is presentation-only
    /// (never folded into <c>SimChecksum</c>), so a per-client shared-vision setting can never desync the sim.
    /// </summary>
    public class FogSharedVisionTests
    {
        private static readonly Fixed Dt = Fixed.One / Fixed.FromInt(30);
        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        private static AllianceStore P1P2Allied()
        {
            var a = new AllianceStore();
            a.TeamId[(int)Faction.Player2] = (int)Faction.Player1;
            return a;
        }

        private static int SightUnit(EntityWorld w, FixedVec3 pos, Faction f)
        {
            int id = w.Create(pos, f, Fixed.FromInt(100), Fixed.FromInt(3));
            w.VisionRange[id] = Fixed.FromInt(12);
            return id;
        }

        [Fact]
        public void SharedVisionOn_UnionsAlliedSight()
        {
            var w = new EntityWorld();
            SightUnit(w, V(50, 50), Faction.Player2); // an allied scout, far from any P1 unit
            var fog = new FogOfWarSystem(Faction.Player1, P1P2Allied()) { SharedTeamVision = true };

            fog.Tick(w, Dt);
            Assert.True(fog.IsVisible(50f, 50f)); // the teammate's scouted area is revealed on P1's fog
        }

        [Fact]
        public void SharedVisionOff_OnlyOwnFactionSight()
        {
            var w = new EntityWorld();
            SightUnit(w, V(50, 50), Faction.Player2); // allied scout — must NOT reveal when the toggle is off
            var fog = new FogOfWarSystem(Faction.Player1, P1P2Allied()) { SharedTeamVision = false };

            fog.Tick(w, Dt);
            Assert.False(fog.IsVisible(50f, 50f));
        }

        [Fact]
        public void EnemyVision_NeverShared_EvenWhenToggleOn()
        {
            var w = new EntityWorld();
            SightUnit(w, V(50, 50), Faction.Player3); // an ENEMY (not allied to P1) — never unioned
            var fog = new FogOfWarSystem(Faction.Player1, P1P2Allied()) { SharedTeamVision = true };

            fog.Tick(w, Dt);
            Assert.False(fog.IsVisible(50f, 50f));
        }

        [Fact]
        public void OwnFaction_AlwaysVisible_RegardlessOfToggle()
        {
            var w = new EntityWorld();
            SightUnit(w, V(-40, -40), Faction.Player1);
            var fog = new FogOfWarSystem(Faction.Player1, P1P2Allied()) { SharedTeamVision = false };

            fog.Tick(w, Dt);
            Assert.True(fog.IsVisible(-40f, -40f)); // the viewer's own vision is unaffected by the shared-vision toggle
        }

        // ── SimulationHost wiring: the LIVE host.Fog must carry host.Alliances (not just a hand-built fog) ──

        [Fact]
        public void SimulationHost_WiresAllianceIntoLiveFog_UnionsAlliedSight()
        {
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2),
                                             new FactionDefinition(), new FactionDefinition());
            // Seed a 2-faction team {P1,P2} through the seeder; host.Fog's viewer is Player1 by default.
            AllianceSeeder.Seed(host.Alliances, new ScenarioData
            {
                PlayerSlots = new[]
                {
                    new ScenarioPlayerSlot { Slot = 0, Team = 1 },
                    new ScenarioPlayerSlot { Slot = 1, Team = 1 },
                },
            });
            Assert.True(host.Alliances.AreAllied(Faction.Player1, Faction.Player2));

            // A P2 teammate scouts far from any P1 unit (there are none). If SimulationHost failed to pass Alliances
            // into host.Fog, the default single-viewer fog would leave this tile dark.
            int ally = host.World.Create(V(50, 50), Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));
            host.World.VisionRange[ally] = Fixed.FromInt(12);

            host.Fog.Tick(host.World, Dt); // the LIVE host-owned fog (SharedTeamVision default true)
            Assert.True(host.Fog.IsVisible(50f, 50f)); // teammate's scouted tile is revealed on P1's fog
        }
    }
}
