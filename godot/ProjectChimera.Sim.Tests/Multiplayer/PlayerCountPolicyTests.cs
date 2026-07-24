#nullable enable
using System.IO;
using System.Runtime.CompilerServices;
using ProjectChimera.Core;                 // FactionRegistry
using ProjectChimera.Core.Definitions;     // ScenarioSerializer, ScenarioData
using ProjectChimera.Multiplayer;          // PlayerCountPolicy
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>Story 9.7 (P3/P5) — the shared player-count clamps: MP target ≤ transport seat ceiling, sim count ≤
    /// faction ceiling, both floored at 2; plus the byte-identity guard that the shipped default scenario → 2.</summary>
    public class PlayerCountPolicyTests
    {
        [Theory]
        [InlineData(0, 2)]   // missing/empty → floor 2
        [InlineData(1, 2)]
        [InlineData(2, 2)]
        [InlineData(3, 3)]
        [InlineData(4, 4)]
        [InlineData(5, 5)]   // sim allows > seat ceiling (offline skirmish)
        [InlineData(8, 8)]
        [InlineData(9, 8)]   // capped at PLAYER_COUNT
        public void SimActivePlayers_ClampsToFactionCeiling(int raw, int expected)
        {
            Assert.Equal(expected, PlayerCountPolicy.SimActivePlayers(raw));
            Assert.True(expected <= FactionRegistry.PLAYER_COUNT);
        }

        [Theory]
        [InlineData(0, 2)]
        [InlineData(2, 2)]
        [InlineData(3, 3)]
        [InlineData(4, 4)]
        [InlineData(5, 4)]   // MP target NEVER exceeds the transport seat ceiling
        [InlineData(8, 4)]
        public void MpTargetPlayers_ClampsToSeatCeiling(int raw, int expected)
        {
            Assert.Equal(expected, PlayerCountPolicy.MpTargetPlayers(raw));
            Assert.True(expected <= PlayerCountPolicy.MpSeatCeiling);
        }

        [Fact]
        public void SeatCeiling_IsBelowFactionCeiling_AndFloorIsTwo()
        {
            Assert.Equal(2, PlayerCountPolicy.MpFloor);
            Assert.True(PlayerCountPolicy.MpSeatCeiling <= FactionRegistry.PLAYER_COUNT);
        }

        [Fact]
        public void DefaultShippedScenario_DerivesTwoPlayers()
        {
            // P5: guards the N=2 byte-identical invariant AT the derivation site — the shipped default scenario
            // (the MainScene ScenarioPath export default) must author exactly 2 player slots, so both the client's
            // FactionRegistry(N) and the server's activeFactionCount derive 2.
            string path = RepoPath("resources/data/scenarios/alpha_map_01.json");
            Assert.True(File.Exists(path), $"default scenario not found at {path}");

            ScenarioData? model = ScenarioSerializer.LoadFromFile(path);
            Assert.NotNull(model);
            int raw = model!.PlayerSlots?.Length ?? 0;
            Assert.Equal(2, raw);
            Assert.Equal(2, PlayerCountPolicy.SimActivePlayers(raw));
            Assert.Equal(2, PlayerCountPolicy.MpTargetPlayers(raw));
        }

        /// <summary>Resolve a repo-relative path from this test's source location: .../godot/ProjectChimera.Sim.Tests/
        /// Multiplayer/ → up 2 to godot/.</summary>
        private static string RepoPath(string rel, [CallerFilePath] string here = "")
        {
            string dir   = Path.GetDirectoryName(here)!;                       // .../Multiplayer
            string godot = Path.GetFullPath(Path.Combine(dir, "..", ".."));    // .../godot
            return Path.Combine(godot, rel.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
