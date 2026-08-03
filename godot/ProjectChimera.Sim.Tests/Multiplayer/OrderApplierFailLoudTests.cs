#nullable enable
using System.Collections.Generic;
using System.IO;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;   // CanonicalModelHash.AlgoVersion (replay header)
using ProjectChimera.Core.Sim;           // ILogSink — the AR-4 injected diagnostic seam
using ProjectChimera.Economy;            // BuildingSystem / BuildingStore / ResourceStore / BuildingType
using ProjectChimera.Multiplayer;        // OrderApplier / UnitOrder / UnitCommand / TickCommandPacket / ReplayRecorder / ReplayPlayer
using ProjectChimera.Multiplayer.Server; // MergedTickBuilder / MergedTickApplier
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// DW-304 — OrderApplier building-family commands FAIL LOUD (Warn through the injected <see cref="ILogSink"/>)
    /// instead of silently no-oping when the system handle that executes them (`buildings`/`research`) is null at
    /// apply time. The drop itself must stay the exact deterministic no-op it always was (goldens/headless/replay
    /// paths legitimately pass null and pass NO sink — locked by the existing
    /// OrderApplier_Train_NullBuildings_IsDeterministicNoOp / CancelTrain_NullBuildings_IsDeterministicNoOp tests);
    /// what changes is OBSERVABILITY: an accidentally unwired live path (a lost player order) now screams. These
    /// tests pin: (1) every command in the family warns exactly once, naming the command and the missing system;
    /// (2) a wired system never warns — including on an ordinary guard REJECT, which is not a systemless drop;
    /// (3) the sink-less legacy contract (no throw, no write) is unchanged; and (4) the two Godot-free production
    /// forwarding paths — <see cref="MergedTickApplier"/> (online client/spectator) and <see cref="ReplayPlayer"/>
    /// (playback) — actually thread the sink into the shared applier (a hand-threaded arg a refactor could drop).
    /// </summary>
    public class OrderApplierFailLoudTests
    {
        /// <summary>Capturing <see cref="ILogSink"/> (the NullLogSink pattern, but recording).</summary>
        private sealed class RecordingSink : ILogSink
        {
            public readonly List<string> Infos = new List<string>();
            public readonly List<string> Warns = new List<string>();
            public void Info(string message) => Infos.Add(message);
            public void Warn(string message) => Warns.Add(message);
        }

        /// <summary>The full building-family command surface DW-304 names, with the system each executes on.</summary>
        public static IEnumerable<object[]> SystemlessBuildingCommands()
        {
            yield return new object[] { UnitCommand.Train,          "BuildingSystem" };
            yield return new object[] { UnitCommand.CancelTrain,    "BuildingSystem" };
            yield return new object[] { UnitCommand.SetRally,       "BuildingSystem" };
            yield return new object[] { UnitCommand.ReviveHero,     "BuildingSystem" };
            yield return new object[] { UnitCommand.BuyItem,        "BuildingSystem" };
            yield return new object[] { UnitCommand.StartResearch,  "ResearchSystem" };
            yield return new object[] { UnitCommand.CancelResearch, "ResearchSystem" };
        }

        [Theory]
        [MemberData(nameof(SystemlessBuildingCommands))]
        public void SystemlessDrop_WarnsOnce_NamingCommandAndMissingSystem_AndStaysANoOp(UnitCommand cmd, string missingSystem)
        {
            var world = new EntityWorld();
            var sink  = new RecordingSink();

            // No buildings/research wired — the DW-304 drop. With a sink injected it must WARN, exactly once.
            OrderApplier.Apply(world, new UnitOrder(3, cmd, Fixed.FromRaw(1), Fixed.Zero),
                               Faction.Player1, log: sink);

            string warn = Assert.Single(sink.Warns);
            Assert.Contains(cmd.ToString(), warn);   // names the dropped command
            Assert.Contains(missingSystem, warn);    // names the unwired system
            Assert.Contains("DROPPED", warn);
            Assert.Empty(sink.Infos);                // Warn severity — this is a lost order on a live path

            // The deterministic no-op is UNCHANGED: the building branch returned before any entity-space write.
            Assert.Equal(UnitCommand.Idle, world.CommandState[3]);
        }

        [Theory]
        [MemberData(nameof(SystemlessBuildingCommands))]
        public void SystemlessDrop_WithNoSink_KeepsTheLegacySilentNoThrowContract(UnitCommand cmd, string _)
        {
            // Golden/headless/replay-without-systems paths pass NO sink — behavior must be byte-identical to
            // pre-DW-304: no throw, no write, no side effect.
            var world = new EntityWorld();
            var ex = Record.Exception(() =>
                OrderApplier.Apply(world, new UnitOrder(3, cmd, Fixed.FromRaw(1), Fixed.Zero), Faction.Player1));
            Assert.Null(ex);
            Assert.Equal(UnitCommand.Idle, world.CommandState[3]);
        }

        [Fact]
        public void WiredBuildingSystem_NeverWarns_OnSuccessOrOnAGuardReject()
        {
            var world     = new EntityWorld();
            var sink      = new RecordingSink();
            var buildings = new BuildingStore();
            var sys       = new BuildingSystem(buildings, new ResourceStore(Fixed.Zero));
            int b = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);

            // An anti-cheat REJECT (Player2 rallying Player1's building) is not a systemless drop — warn-free.
            OrderApplier.Apply(world, new UnitOrder(b, UnitCommand.SetRally, Fixed.FromInt(3), Fixed.FromInt(3)),
                               Faction.Player2, buildings: sys, log: sink);
            Assert.False(buildings.HasRallyPoint[b]);

            // A SUCCESSFUL building command is warn-free too.
            OrderApplier.Apply(world, new UnitOrder(b, UnitCommand.SetRally, Fixed.FromInt(16), Fixed.FromInt(-4)),
                               Faction.Player1, buildings: sys, log: sink);
            Assert.True(buildings.HasRallyPoint[b]); // the wired path actually executed

            Assert.Empty(sink.Warns);
        }

        [Fact]
        public void MergedTickApplier_ForwardsTheLogSink_SoASystemlessTrainWarns()
        {
            // The online client/spectator path: LockstepManager.ApplyMerged → MergedTickApplier → OrderApplier.
            // The sink rides the same hand-threaded optional tail as buildings/research — pin the forwarding.
            var world       = new EntityWorld();
            var slotFaction = new[] { Faction.Player1, Faction.Player2 };
            var builder     = new MergedTickBuilder(2, slotFaction);

            var buf0  = new byte[TickCommandPacket.HEADER_BYTES + TickCommandPacket.MAX_ORDERS * UnitOrder.SIZE];
            var train = new UnitOrder(0, UnitCommand.Train, Fixed.FromRaw(1), Fixed.Zero);
            int l0 = TickCommandPacket.Write(buf0, 1u, Faction.Player1, new[] { train }, 1);
            builder.Submit(0, buf0, l0, out _);

            var buf1 = new byte[TickCommandPacket.HEADER_BYTES];
            int l1 = TickCommandPacket.Write(buf1, 1u, Faction.Player2, System.Array.Empty<UnitOrder>(), 0);
            builder.Submit(1, buf1, l1, out _);

            Assert.True(builder.TryBuild(1u, out byte[] merged, out int len));

            var sink = new RecordingSink();
            MergedTickApplier.Apply(merged, len, world, log: sink); // no buildings wired → the drop must warn
            string warn = Assert.Single(sink.Warns);
            Assert.Contains(nameof(UnitCommand.Train), warn);
            Assert.Contains("BuildingSystem", warn);
        }

        [Fact]
        public void ReplayPlayer_ThreadsTheLogSink_SoASystemlessSetRallyWarnsOnPlayback()
        {
            // The playback path: ReplayPlayer.ApplyOrders hand-threads the optional tail into OrderApplier.Apply —
            // on a production replay an unwired Buildings means playback silently diverges from the recording, so
            // the sink must ride along. (Parity tests that replay without systems on purpose simply set no Log.)
            string path = Path.GetTempFileName();
            try
            {
                var setRally = new UnitOrder(0, UnitCommand.SetRally, Fixed.FromInt(16), Fixed.FromInt(-4));
                using (var rec = new ReplayRecorder(path, "test://dw-304-fail-loud", EntityWorld.DEFAULT_RNG_SEED,
                                                    0x11UL, 0x22UL, CanonicalModelHash.AlgoVersion,
                                                    new[] { Faction.Player1, Faction.Player2 }))
                {
                    var orders = new[] { setRally };
                    rec.RecordTick(1, Faction.Player1, orders, 0, orders.Length);
                }

                var world  = new EntityWorld();
                var sink   = new RecordingSink();
                var player = new ReplayPlayer(path, world) { Log = sink }; // Buildings deliberately NOT wired
                player.Flush(1);

                string warn = Assert.Single(sink.Warns);
                Assert.Contains(nameof(UnitCommand.SetRally), warn);
                Assert.Contains("BuildingSystem", warn);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
