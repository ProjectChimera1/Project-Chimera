#nullable enable
using System.IO;
using ProjectChimera.Core;              // EntityWorld, Faction, Fixed, UnitOrder, UnitCommand, WinStateStore
using ProjectChimera.Core.Definitions;  // CanonicalModelHash
using ProjectChimera.Multiplayer;       // ReplayRecorder, ReplayPlayer
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// Story 11.2 (FR-66) — the REPLAY apply path threads the WinStateStore into the shared <c>OrderApplier</c>
    /// (the one-switch parity rule): a recorded <see cref="UnitCommand.Concede"/> order latches the conceding
    /// faction's verdict on playback byte-identically to the live run. Without the threading (WinState never
    /// forwarded to OrderApplier.Apply in <c>ReplayPlayer.ApplyOrders</c>), a replayed concede would silently
    /// no-op and diverge from the live outcome.
    /// </summary>
    public class ReplayConcedeTests
    {
        private static readonly Faction[] Roster2 = { Faction.Player1, Faction.Player2 };

        private static ReplayRecorder NewRec(string path)
            => new(path, "test://concede", EntityWorld.DEFAULT_RNG_SEED,
                   scenarioHash: 0x11UL, rulesetHash: 0x22UL, modelAlgoVersion: CanonicalModelHash.AlgoVersion, roster: Roster2);

        [Fact]
        public void RecordedConcede_ReplaysLatchLost_ThroughThreadedWinState()
        {
            string path = Path.GetTempFileName();
            try
            {
                var concede = new UnitOrder(0, UnitCommand.Concede, Fixed.Zero, Fixed.Zero);
                using (var rec = NewRec(path))
                    rec.RecordTick(1, Faction.Player1, new[] { concede }, 0, 1);

                var world = new EntityWorld();
                var win = new WinStateStore();
                var player = new ReplayPlayer(path, world) { WinState = win };

                player.Flush(1); // the recorded Concede applies through the shared OrderApplier + threaded WinState

                Assert.Equal(WinStateStore.VERDICT_LOST, win.Verdict[(int)Faction.Player1]);
                Assert.Equal(WinStateStore.VERDICT_NONE, win.Verdict[(int)Faction.Player2]);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void RecordedConcede_NullWinState_IsDeterministicNoOp()
        {
            string path = Path.GetTempFileName();
            try
            {
                var concede = new UnitOrder(0, UnitCommand.Concede, Fixed.Zero, Fixed.Zero);
                using (var rec = NewRec(path))
                    rec.RecordTick(1, Faction.Player1, new[] { concede }, 0, 1);

                var world = new EntityWorld();
                var player = new ReplayPlayer(path, world); // no WinState wired (golden/headless replay)

                player.Flush(1); // must not throw — a deterministic no-op
            }
            finally { File.Delete(path); }
        }
    }
}
