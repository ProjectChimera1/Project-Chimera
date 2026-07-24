#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using ProjectChimera.Core;                 // Faction, EntityWorld, FactionRegistry, BuildingStore, WinStateStore, WinConditionSystem, Fixed
using ProjectChimera.Core.Definitions;     // ScenarioData, ScenarioSerializer, FactionDefinition
using ProjectChimera.Core.Sim;             // SimulationHost, ServerBootstrap, NullLogSink
using ProjectChimera.Multiplayer;          // TickCommandPacket, UnitOrder, UnitCommand
using ProjectChimera.Multiplayer.Server;   // MergedTickBuilder, MergedTickApplier, ServerHost
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// Story 9.15 — the headless N=4 end-to-end proof. For BOTH configs — 2v2 (teams {1,1,2,2}) and 4-FFA (teams 0) —
    /// the committed 4-start-position scenario <c>quad_map_01</c> is loaded through the REAL production paths
    /// (<see cref="ServerBootstrap.Build"/> → <see cref="ScenarioValidator"/> → <see cref="ScenarioApplier.Apply"/> →
    /// <c>AllianceSeeder.Seed</c> → <see cref="WinConditionSystem.Configure"/>), then driven to one elimination and full
    /// victory with every per-tick command fanned in through the REAL <see cref="MergedTickBuilder"/>(4) +
    /// <see cref="MergedTickApplier"/> — not a single hand-stepped host.
    ///
    /// <para><b>What each check actually proves (no overclaiming):</b></para>
    /// <list type="bullet">
    ///   <item><b>Correct victory:</b> every active faction latches the right WON/LOST verdict via the unchanged
    ///     <see cref="WinConditionSystem"/>, and the match fully resolves.</item>
    ///   <item><b>Determinism:</b> two independent, same-process runs of each config produce a BYTE-IDENTICAL full-fold
    ///     <c>SimChecksum</c> sequence (a run-vs-run determinism proof — NOT a cross-platform golden; no committed
    ///     baseline is compared, and the float AI below is bit-identical only because both runs share one process).</item>
    ///   <item><b>Quorum liveness + unanimous-accept:</b> the positive run feeds ONE in-process host's single checksum
    ///     to all four slots, so quorum-green is UNANIMOUS BY CONSTRUCTION — <see cref="AssertQuorumGreen"/> proves the
    ///     <see cref="ServerHost"/>(4) stays live (windows keep completing past a threshold) and ACCEPTS unanimous input
    ///     without a false halt/desync. It does NOT (and cannot, from unanimous input) prove divergence detection.</item>
    ///   <item><b>Divergence detection</b> is proven by the separate <c>MinorityDesync</c> negative test (a minority-wrong
    ///     checksum → <c>DesyncCount&gt;0</c>, <c>!Passing</c>, an alert to the minority slot), and the merged-tick path is
    ///     proven LOAD-BEARING (an applied order changes the world) by <c>MergedTickApplier_AppliesAnOrder…</c>.</item>
    /// </list>
    ///
    /// <para>The AI opponent (Story 3.10, <c>Player2</c>-only) runs inside <c>StepOnce</c>, so BOTH configs are
    /// deliberately shaped so <c>Player2</c> is always on the winning side and never needs eliminating — the scripted
    /// building destruction only ever targets non-AI factions, which cannot rebuild.</para>
    /// </summary>
    public class FourPlayerEndToEndTests
    {
        private const int Ticks     = 150; // > GRACE_TICKS (90) + the elimination schedule + inert tail
        private const int MinWindows = 120; // ServerHost windows the quorum must have compared (one per executed tick)

        private const int WON  = WinStateStore.VERDICT_WON;
        private const int LOST = WinStateStore.VERDICT_LOST;

        // ── 2v2: teams {1,1,2,2}. Eliminate team 2 (P3,P4 — neither AI-controlled) → team {P1,P2} wins as a whole. ──
        [Fact]
        public void TwoVsTwo_LoadsThroughRealPaths_RunsToTeamVictory_QuorumGreen_AndTwoRunByteIdentical()
        {
            var teams = new[] { 1, 1, 2, 2 };
            var kills = new (int factionSlot, int atTick)[] { (2, 100), (3, 102) }; // eliminate P3 then P4

            RunResult a = RunConfig(teams, kills);
            RunResult b = RunConfig(teams, kills);

            // Per-faction verdicts: team {P1,P2} WON, team {P3,P4} LOST.
            Assert.Equal(WON,  a.Verdict(Faction.Player1));
            Assert.Equal(WON,  a.Verdict(Faction.Player2));
            Assert.Equal(LOST, a.Verdict(Faction.Player3));
            Assert.Equal(LOST, a.Verdict(Faction.Player4));
            Assert.True(a.FullyResolved);
            Assert.Equal((int)Faction.Player1, a.WinnerFaction); // lowest WON slot = team representative

            AssertQuorumGreen(a);
            Assert.Equal(a.Hashes, b.Hashes); // byte-identical two-run SimChecksum sequence (zero desync)
        }

        // ── 4-FFA: teams 0. Eliminate P1, P3, P4 (none AI-controlled) → P2 (AI) is last team standing → P2 wins. ──
        [Fact]
        public void FourFfa_LoadsThroughRealPaths_RunsToLastFactionStanding_QuorumGreen_AndTwoRunByteIdentical()
        {
            var teams = new[] { 0, 0, 0, 0 };
            var kills = new (int factionSlot, int atTick)[] { (0, 100), (2, 102), (3, 104) }; // eliminate P1, P3, P4

            RunResult a = RunConfig(teams, kills);
            RunResult b = RunConfig(teams, kills);

            Assert.Equal(WON,  a.Verdict(Faction.Player2));
            Assert.Equal(LOST, a.Verdict(Faction.Player1));
            Assert.Equal(LOST, a.Verdict(Faction.Player3));
            Assert.Equal(LOST, a.Verdict(Faction.Player4));
            Assert.True(a.FullyResolved);
            Assert.Equal((int)Faction.Player2, a.WinnerFaction);

            AssertQuorumGreen(a);
            Assert.Equal(a.Hashes, b.Hashes);
        }

        // ── Negative: a minority-wrong checksum in one window must be DETECTED (quorum is not trivially green). ──
        [Fact]
        public void MinorityDesync_IsDetected_AlertsTheMinoritySlot_AndFailsPassing()
        {
            var alerts = new List<int>();
            var server = new ServerHost(4, NullLogSink.Instance,
                sendReliableTo: (slot, _) => alerts.Add(slot),
                broadcastReliable: _ => { });

            // Tick 1: three slots agree on 0xAAAA, slot 3 diverges to 0xBBBB → majority = 0xAAAA, minority = {3}.
            server.OnChecksum(0, 1, 0xAAAAu);
            server.OnChecksum(1, 1, 0xAAAAu);
            server.OnChecksum(2, 1, 0xAAAAu);
            server.OnChecksum(3, 1, 0xBBBBu); // completes the window

            Assert.True(server.DesyncCount > 0, "a minority divergence must count as a desync window");
            Assert.False(server.Passing, "Passing must be false once any desync window is observed");
            Assert.False(server.Halted, "a strict majority exists, so the match must NOT halt");
            Assert.Contains(3, alerts); // the DesyncAlert went to the diverged slot
        }

        // ── harness ───────────────────────────────────────────────────────────────────────────────────────────────

        private sealed class RunResult
        {
            public required SimulationHost Host;
            public required List<uint> Hashes;
            public required ServerHost Server;
            public int Verdict(Faction f) => Host.WinState.Verdict[(int)f];
            public int WinnerFaction => Host.WinState.WinnerFaction();
            public bool FullyResolved => Host.WinCon.IsFullyResolved();
        }

        // Liveness + unanimous-accept: the four slots are fed ONE host's identical checksum, so agreement is unanimous by
        // construction. This asserts the quorum stays LIVE (windows keep completing past the threshold) and ACCEPTS that
        // unanimous input with no false halt/desync — divergence DETECTION is proven by the MinorityDesync negative test.
        private static void AssertQuorumGreen(RunResult r)
        {
            Assert.True(r.Server.Passing, "ServerHost.Passing must stay true on unanimous input (no false desync window)");
            Assert.False(r.Server.Halted, "ServerHost must not have halted on unanimous input");
            Assert.Equal(0, r.Server.DesyncCount);
            Assert.True(r.Server.WindowsCompared >= MinWindows,
                $"expected >= {MinWindows} compared windows (liveness), got {r.Server.WindowsCompared}");
        }

        /// <summary>
        /// Load quad_map_01 through the REAL server bootstrap (validate + apply + alliance-seed) and return the wired
        /// host + each slot's authored base corner (used as the "stay home" Move target). 2v2 assigns per-slot teams
        /// BEFORE apply; the applier's AllianceSeeder.Seed consumes them (FFA = teams 0). The scenario's res:// faction
        /// paths are Godot-coupled, so the two shipped faction defs are resolved from disk exactly as the presentation
        /// pre-pass / CanonicalScenarioTests do (slots 0,2 = alpha; slots 1,3 = beta).
        /// </summary>
        private static SimulationHost LoadQuadHost(int[] teamPerSlot, out (Fixed x, Fixed z)[] baseTargets)
        {
            ScenarioData? model = ScenarioSerializer.LoadFromFile(DataFile("scenarios", "quad_map_01.json"));
            Assert.NotNull(model);
            Assert.Equal(4, model!.PlayerSlots.Length);

            for (int i = 0; i < model.PlayerSlots.Length; i++)
                model.PlayerSlots[i].Team = teamPerSlot[i];

            FactionDefinition? alpha = FactionDefinition.LoadFromFile(DataFile("factions", "alpha_faction.json"));
            FactionDefinition? beta  = FactionDefinition.LoadFromFile(DataFile("factions", "beta_faction.json"));
            Assert.NotNull(alpha);
            Assert.NotNull(beta);
            var slotDefs = new FactionDefinition?[FactionRegistry.FACTION_ARRAY_SIZE];
            slotDefs[(int)Faction.Player1] = alpha; slotDefs[(int)Faction.Player3] = alpha;
            slotDefs[(int)Faction.Player2] = beta;  slotDefs[(int)Faction.Player4] = beta;

            SimulationHost? host = ServerBootstrap.Build(model, slotDefs, damageTable: null,
                NullLogSink.Instance, activeFactionCount: 4);
            Assert.NotNull(host); // quad_map_01 is valid → the fail-closed validator must NOT trip

            baseTargets = new (Fixed x, Fixed z)[4];
            for (int i = 0; i < 4; i++)
                baseTargets[i] = (Fixed.FromFloat(model.PlayerSlots[i].BaseX), Fixed.FromFloat(model.PlayerSlots[i].BaseZ));
            return host!;
        }

        // ── The merged-tick path is LOAD-BEARING: an applied order visibly changes the world (RED if the applier no-ops). ──
        // Victory in the runs above is driven by RazeFaction, so a MergedTickApplier that silently applied NOTHING (its
        // documented TryRead-false no-op) would still resolve + stay byte-identical + keep the (unanimous) quorum green.
        // This test closes that gap: it fans ONE real Move order through MergedTickBuilder(4)+MergedTickApplier and asserts
        // the ordered unit actually moved toward the target — so the "real merged-tick path" claim is verified to have EFFECT.
        [Fact]
        public void MergedTickApplier_AppliesAnOrder_TheUnitMovesTowardTarget()
        {
            SimulationHost host = LoadQuadHost(new[] { 0, 0, 0, 0 }, out _);
            EntityWorld world = host.World;

            int id = FirstAliveUnit(world, Faction.Player1);
            Assert.True(id >= 0, "expected a Player1 unit to exist after apply");
            FixedVec3 start = world.Position[id];

            // Target the map center (0,0) — far from P1's base corner (-50,-50), so a real Move increases BOTH X and Z.
            var target = (x: Fixed.FromInt(0), z: Fixed.FromInt(0));

            var slotFactions = new[] { Faction.Player1, Faction.Player2, Faction.Player3, Faction.Player4 };
            var builder = new MergedTickBuilder(4, slotFactions);
            var orders  = new UnitOrder[TickCommandPacket.MAX_ORDERS];
            var packBuf = new byte[TickCommandPacket.HEADER_BYTES + TickCommandPacket.MAX_ORDERS * UnitOrder.SIZE];

            for (int i = 0; i < 30; i++)
            {
                uint tick = (uint)i;
                for (int slot = 0; slot < 4; slot++)
                {
                    // Only slot 0 carries the Move for `id`; the other three submit empty bundles (real 4-way fan-in).
                    int count = 0;
                    if (slot == 0) { orders[0] = new UnitOrder(id, UnitCommand.Move, target.x, target.z); count = 1; }
                    int len = TickCommandPacket.Write(packBuf, tick, slotFactions[slot], orders, count);
                    builder.Submit(slot, packBuf, len, out _);
                }
                if (builder.TryBuild(tick, out byte[] merged, out int mergedLen))
                    MergedTickApplier.Apply(merged, mergedLen, world);
                host.StepOnce();
            }

            FixedVec3 end = world.Position[id];
            Assert.True(world.IsAlive(id), "the ordered unit must still be alive");
            Assert.True(end.X.Raw != start.X.Raw || end.Z.Raw != start.Z.Raw,
                "the merged-tick-applied Move produced NO movement — MergedTickApplier is a no-op (the path is not load-bearing).");
            Assert.True(end.X.Raw > start.X.Raw && end.Z.Raw > start.Z.Raw,
                "the unit did not move toward the (0,0) target — the applied Move order did not take effect as expected.");
        }

        /// <summary>Lowest-id alive unit of <paramref name="faction"/>, or -1 if none.</summary>
        private static int FirstAliveUnit(EntityWorld world, Faction faction)
        {
            int hwm = world.HighWaterMark;
            for (int id = 0; id < hwm; id++)
                if (world.IsAlive(id) && world.FactionOf[id] == faction) return id;
            return -1;
        }

        /// <summary>
        /// Load quad_map_01 through the real server bootstrap, then drive N=4 merged-tick play to victory, feeding every
        /// executed tick's full-fold checksum to a live ServerHost(4).
        /// </summary>
        private static RunResult RunConfig(int[] teamPerSlot, (int factionSlot, int atTick)[] kills)
        {
            SimulationHost host = LoadQuadHost(teamPerSlot, out (Fixed x, Fixed z)[] baseTarget);
            host.ChecksumInterval = 1; // checksum every tick so a divergence's located tick is exact

            var hashes = new List<uint>(Ticks);
            var server = new ServerHost(4, NullLogSink.Instance,
                sendReliableTo: (_, _) => { }, broadcastReliable: _ => { });
            host.SetChecksumSink((tick, hash) =>
            {
                hashes.Add(hash);
                for (int slot = 0; slot < 4; slot++) server.OnChecksum(slot, tick, hash); // 4 peers agree
            });

            var slotFactions = new[] { Faction.Player1, Faction.Player2, Faction.Player3, Faction.Player4 };
            var builder = new MergedTickBuilder(4, slotFactions);
            var orders  = new UnitOrder[TickCommandPacket.MAX_ORDERS];
            var packBuf = new byte[TickCommandPacket.HEADER_BYTES + TickCommandPacket.MAX_ORDERS * UnitOrder.SIZE];

            for (int i = 0; i < Ticks; i++)
            {
                uint tick = (uint)i;

                // Real 4-way merged-tick fan-in: each slot submits a single-faction bundle (a Move for its first unit,
                // else empty) → the builder merges all four ascending → the applier applies the merged packet to the world.
                for (int slot = 0; slot < 4; slot++)
                {
                    int count = FillMoveOrder(host.World, slotFactions[slot], baseTarget[slot], orders);
                    int len = TickCommandPacket.Write(packBuf, tick, slotFactions[slot], orders, count);
                    builder.Submit(slot, packBuf, len, out _);
                }
                if (builder.TryBuild(tick, out byte[] merged, out int mergedLen))
                    MergedTickApplier.Apply(merged, mergedLen, host.World);

                // Scripted eliminations (deterministic, identical across runs): raze every building of the target faction.
                foreach ((int factionSlot, int atTick) in kills)
                    if (atTick == i) RazeFaction(host.Buildings, FactionRegistry.ToFaction(factionSlot));

                host.StepOnce(); // runs the full 16-system loop incl. WinConditionSystem; fires the checksum sink
            }

            return new RunResult { Host = host, Hashes = hashes, Server = server };
        }

        /// <summary>Fill a single Move order for <paramref name="faction"/>'s lowest-id alive unit; returns 0 if none.</summary>
        private static int FillMoveOrder(EntityWorld world, Faction faction, (Fixed x, Fixed z) target, UnitOrder[] buf)
        {
            int hwm = world.HighWaterMark;
            for (int id = 0; id < hwm; id++)
            {
                if (!world.IsAlive(id) || world.FactionOf[id] != faction) continue;
                buf[0] = new UnitOrder(id, UnitCommand.Move, target.x, target.z);
                return 1;
            }
            return 0;
        }

        /// <summary>Destroy every alive building owned by <paramref name="faction"/> (idempotent; a no-op if already gone).</summary>
        private static void RazeFaction(BuildingStore buildings, Faction faction)
        {
            for (int i = 0; i < buildings.Count; i++)
                if (buildings.Alive[i] && buildings.FactionOf[i] == faction) buildings.Destroy(i);
        }

        /// <summary>Resolve a file under <c>godot/resources/data/&lt;sub&gt;/</c> by walking up from the test-assembly dir.</summary>
        private static string DataFile(string sub, string fileName)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "resources", "data", sub);
                if (Directory.Exists(candidate)) return Path.Combine(candidate, fileName);
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException($"Could not locate resources/data/{sub} above {AppContext.BaseDirectory}");
        }
    }
}
