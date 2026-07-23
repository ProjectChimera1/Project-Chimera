#nullable enable
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Core
{
    /// <summary>
    /// Story 9.5 — the per-client fog VIEWER perspective, verified Godot-free: a <see cref="FogOfWarSystem"/> reveals
    /// exactly its constructed/retargeted faction's vision, and nothing else. Proves (a) a Player2-viewer fog reveals
    /// the Player2 unit's cell and NOT the Player1-only cell (and the symmetric Player1 case), (b) the default (Player1)
    /// ctor stays byte-identical to pre-change, and (c) <see cref="FogOfWarSystem.SetViewer"/> retargets a
    /// default-constructed fog so its revealed Grid equals a fog constructed directly with that faction. The fog Grid is
    /// read only by presentation and is NOT folded into SimChecksum, so a per-client differing fog is correct behaviour.
    /// </summary>
    public class FogPerspectiveTests
    {
        // Two distinct cells far enough apart that a small vision circle around one never touches the other.
        private static readonly FixedVec3 P1_POS = new FixedVec3(Fixed.FromInt(-40), Fixed.Zero, Fixed.FromInt(-40));
        private static readonly FixedVec3 P2_POS = new FixedVec3(Fixed.FromInt( 40), Fixed.Zero, Fixed.FromInt( 40));

        /// <summary>Build a world holding one Player1 unit and one Player2 unit at distinct cells with a small vision range.</summary>
        private static EntityWorld MakeTwoFactionWorld()
        {
            var w = new EntityWorld();
            int p1 = w.Create(P1_POS, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            int p2 = w.Create(P2_POS, Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));
            w.VisionRange[p1] = Fixed.FromInt(4); // 4 world units = 2 cells — well short of the 80-unit separation
            w.VisionRange[p2] = Fixed.FromInt(4);
            return w;
        }

        [Fact]
        public void Player2Viewer_RevealsOwnCell_NotEnemyCell()
        {
            var world = MakeTwoFactionWorld();
            var fog = new FogOfWarSystem(Faction.Player2);
            fog.Tick(world, Fixed.Zero);

            Assert.True(fog.IsVisible(P2_POS.X.ToFloat(), P2_POS.Z.ToFloat()),
                "Player2 viewer must reveal the Player2 unit's cell.");
            Assert.False(fog.IsVisible(P1_POS.X.ToFloat(), P1_POS.Z.ToFloat()),
                "Player2 viewer must NOT reveal the Player1-only cell.");
        }

        [Fact]
        public void Player1Viewer_RevealsOwnCell_NotEnemyCell()
        {
            var world = MakeTwoFactionWorld();
            var fog = new FogOfWarSystem(Faction.Player1);
            fog.Tick(world, Fixed.Zero);

            Assert.True(fog.IsVisible(P1_POS.X.ToFloat(), P1_POS.Z.ToFloat()),
                "Player1 viewer must reveal the Player1 unit's cell.");
            Assert.False(fog.IsVisible(P2_POS.X.ToFloat(), P2_POS.Z.ToFloat()),
                "Player1 viewer must NOT reveal the Player2-only cell.");
        }

        [Fact]
        public void DefaultCtor_IsPlayer1_ByteIdenticalToExplicitPlayer1()
        {
            // The default ctor viewer is Player1 — its revealed Grid must equal an explicitly-Player1-constructed fog.
            var world = MakeTwoFactionWorld();

            var fogDefault  = new FogOfWarSystem();                 fogDefault.Tick(world,  Fixed.Zero);
            var fogExplicit = new FogOfWarSystem(Faction.Player1);  fogExplicit.Tick(world, Fixed.Zero);

            Assert.Equal(fogExplicit.Grid, fogDefault.Grid); // byte-for-byte identical
        }

        [Fact]
        public void SetViewer_RetargetsDefaultFog_ToMatchDirectlyConstructedViewer()
        {
            // A default (Player1) fog, retargeted to Player2 BEFORE the tick, must reveal exactly what a fog
            // constructed directly with Player2 reveals.
            var world = MakeTwoFactionWorld();

            var fogRetargeted = new FogOfWarSystem();
            fogRetargeted.SetViewer(Faction.Player2);
            fogRetargeted.Tick(world, Fixed.Zero);

            var fogDirect = new FogOfWarSystem(Faction.Player2);
            fogDirect.Tick(world, Fixed.Zero);

            Assert.Equal(fogDirect.Grid, fogRetargeted.Grid); // byte-for-byte identical
        }
    }
}
