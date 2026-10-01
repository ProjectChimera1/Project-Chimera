#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ProjectChimera.AI;                 // AiControlPlan
using ProjectChimera.Combat;             // ProjectileStore, DamageTable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Multiplayer;        // UnitOrder, PlayerCountPolicy
using Xunit;

namespace ProjectChimera.Sim.Tests.Sim
{
    /// <summary>
    /// Unreal trial A3 (plan A §3.2, §4 A3): the shared Godot-free match composition <see cref="SimSession"/>, the
    /// pre-tick hash helper <see cref="PreTickHashes"/>, the wide diagnostics <see cref="WorldDigest"/> and the frozen
    /// order format <see cref="OrderScript"/>, on the shipped <c>alpha_map_01</c> map with real content.
    ///
    /// <para>What each test pins: (1) two builds step 300 ticks identically; (2) the RNG is still at
    /// <see cref="EntityWorld.DEFAULT_RNG_SEED"/> after <see cref="ServerBootstrap.Build"/> and before the match seed,
    /// so seeding after the apply equals Godot's seed-then-apply order (<c>MainScene.cs:2996-3007</c>); (3) AI plan
    /// None leaves P2's buildings alone; (4) the flow-field wiring changes the match; (5) the new optional
    /// <c>ServerBootstrap.Build</c> item parameter is neutral (null, omitted and <see cref="ItemRegistry.Empty"/> give the
    /// same H0); (6) <see cref="WorldDigest.WideDigest"/> folds every public array; (7) the CSV round trip including
    /// the 0x80 queued flag and negative raws. Budget: well under 10 s in Debug.</para>
    /// </summary>
    public class SimSessionTests
    {
        private const ulong TrialSeed = 0xC0FFEE1234567890UL;
        private const string Map = "res://resources/data/scenarios/alpha_map_01.json";
        private const int Ticks = 300;

        /// <summary>The <c>res://</c> root: the godot/ directory that holds resources/data.</summary>
        private static string ContentRoot()
            => Directory.GetParent(RealContentFixture.DataDir("factions"))!.Parent!.Parent!.FullName;

        private static SimSession Build(bool aiOn = false, bool flowFields = true)
            => SimSession.Build(ContentRoot(), Map, TrialSeed, aiOn, null, new SimSessionOptions { FlowFields = flowFields });

        private static List<int> AliveUnits(SimSession s, Faction f)
        {
            var ids = new List<int>();
            EntityWorld w = s.Host.World;
            for (int id = 0; id < w.HighWaterMark; id++)
                if (w.IsAlive(id) && w.FactionOf[id] == f) ids.Add(id);
            return ids;
        }

        /// <summary>Tick-0 orders that make the run non-trivial: P1's workers Move past P2's CommandCenter (x = 45)
        /// to x = 60, so a path must go around a building.</summary>
        private static void IssueMarch(SimSession s)
        {
            EntityWorld w = s.Host.World;
            int k = 0;
            foreach (int id in AliveUnits(s, Faction.Player1))
            {
                var o = new UnitOrder(w.PackRef(id), UnitCommand.Move, Fixed.FromInt(60), Fixed.FromInt(k++ * 2 - 1));
                Assert.True(s.ApplyOrder(Faction.Player1, in o));
            }
        }

        /// <summary>Per-tick checksums for <see cref="Ticks"/> ticks (interval 1, as the goldens).</summary>
        private static uint[] Run(SimSession s, bool march = true)
        {
            s.Host.ChecksumInterval = 1;
            if (march) IssueMarch(s);
            var seq = new uint[Ticks];
            for (int t = 0; t < Ticks; t++)
            {
                s.Step();
                Assert.Equal((uint)(t + 1), s.Host.CurrentTick);
                seq[t] = s.Host.LastChecksum;
            }
            return seq;
        }

        [Fact]
        public void TwoBuilds_Step300Ticks_Identically()
        {
            SimSession a = Build(), b = Build();
            Assert.True(a.FlowFieldsWired);
            Assert.Equal(a.PreTick(), b.PreTick());
            uint[] sa = Run(a), sb = Run(b);
            Assert.Equal(sa, sb);
            Assert.Equal(WorldDigest.WideDigest(a.Host.World, a.Host.Projectiles), WorldDigest.WideDigest(b.Host.World, b.Host.Projectiles));
            Assert.Equal(WorldDigest.UnitsDigest(a.Host.World), WorldDigest.UnitsDigest(b.Host.World));
            Assert.Equal(sa[Ticks - 1], a.Host.ComputeChecksumNow());
        }

        [Fact]
        public void Rng_IsDefaultAfterBootstrap_AndSeededOnlyOnce()
        {
            SimSession s = Build();
            Assert.Equal(EntityWorld.DEFAULT_RNG_SEED, s.RngStateBeforeSeed);
            Assert.Equal(TrialSeed, s.Seed);
            // Nothing in the flow-field wiring draws: the state is exactly the seed.
            Assert.Equal(TrialSeed, s.Host.World.Rng.State);

            // The same holds on the bare ServerBootstrap path SimSession is built on (Apply draws nothing).
            SimulationHost bare = BareBootstrap(null, out _, out _, out _);
            Assert.Equal(EntityWorld.DEFAULT_RNG_SEED, bare.World.Rng.State);
        }

        [Fact]
        public void AiPlanNone_LeavesP2BuildingsUnchanged()
        {
            SimSession s = Build(aiOn: false);
            Assert.Equal(AiControlPlan.None.Mask, s.AiPlan.Mask);
            string before = P2Buildings(s);
            int countBefore = s.Host.Buildings.Count;
            Run(s, march: false);
            Assert.Equal(countBefore, s.Host.Buildings.Count);
            Assert.Equal(before, P2Buildings(s));

            SimSession on = Build(aiOn: true);
            Assert.Equal(AiControlPlan.OfflineDefault.Mask, on.AiPlan.Mask);
            Assert.NotEqual(s.PreTick().Agreement, on.PreTick().Agreement); // the plan is folded into the agreement hash
            Assert.Equal(s.PreTick().StartState, on.PreTick().StartState);  // ...and nothing else at tick 0
        }

        private static string P2Buildings(SimSession s)
        {
            BuildingStore b = s.Host.Buildings;
            var parts = new List<string>();
            for (int i = 0; i < b.Count; i++)
                if (b.FactionOf[i] == Faction.Player2)
                    parts.Add($"{i}:{b.DefinitionId[i]}:{b.Alive[i]}:{b.Position[i].X.Raw},{b.Position[i].Z.Raw}");
            Assert.NotEmpty(parts);
            return string.Join("|", parts);
        }

        [Fact]
        public void FlowFieldsOff_DiffersFromOn_ByTick300()
        {
            SimSession on = Build(flowFields: true), off = Build(flowFields: false);
            Assert.False(off.FlowFieldsWired);
            Assert.Equal(on.PreTick(), off.PreTick()); // the wiring changes no start state
            uint[] a = Run(on), b = Run(off);
            Assert.NotEqual(a[Ticks - 1], b[Ticks - 1]);
        }

        [Fact]
        public void ServerBootstrapItemParameter_IsNeutral()
        {
            SimulationHost omitted = BareBootstrap(null, out ScenarioData m0, out FactionDefinition?[] d0, out DamageTable t0, omitItems: true);
            SimulationHost viaNull = BareBootstrap(null, out ScenarioData m1, out FactionDefinition?[] d1, out DamageTable t1);
            SimulationHost viaEmpty = BareBootstrap(ItemRegistry.Empty, out ScenarioData m2, out FactionDefinition?[] d2, out DamageTable t2);
            Assert.Same(ItemRegistry.Empty, omitted.ItemRegistry);
            Assert.Same(ItemRegistry.Empty, viaNull.ItemRegistry);
            PreTickHashes h0 = PreTickHashes.Compute(omitted, m0, d0, t0, AiControlPlan.OfflineDefault);
            PreTickHashes h1 = PreTickHashes.Compute(viaNull, m1, d1, t1, AiControlPlan.OfflineDefault);
            PreTickHashes h2 = PreTickHashes.Compute(viaEmpty, m2, d2, t2, AiControlPlan.OfflineDefault);
            Assert.Equal(h0, h1);
            Assert.Equal(h0, h2);
            for (int t = 0; t < 30; t++) { omitted.StepOnce(); viaEmpty.StepOnce(); }
            Assert.Equal(omitted.ComputeChecksumNow(), viaEmpty.ComputeChecksumNow());
        }

        /// <summary>The pre-A3 headless composition (MainScene.BuildHeadlessServerSimHost's recipe), optionally with
        /// the new item parameter.</summary>
        private static SimulationHost BareBootstrap(ItemRegistry? items, out ScenarioData model,
            out FactionDefinition?[] slotDefs, out DamageTable damage, bool omitItems = false)
        {
            string root = ContentRoot();
            string data = Path.Combine(root, "resources", "data");
            var factions = new FactionRegistry(2);
            slotDefs = factions.SlotDefinitions;
            slotDefs[(int)Faction.Player1] = FactionDefinition.LoadFromFile(Path.Combine(data, "factions", "alpha_faction.json"));
            slotDefs[(int)Faction.Player2] = FactionDefinition.LoadFromFile(Path.Combine(data, "factions", "beta_faction.json"));
            damage = DamageTable.Load(Path.Combine(data, "damage_table.json"));
            AbilityRegistry abilities = AbilityRegistry.LoadFromDirectory(Path.Combine(data, "abilities"));
            model = ScenarioSerializer.LoadFromFile(Path.Combine(data, "scenarios", "alpha_map_01.json"))!;
            Assert.NotNull(model);
            foreach (ScenarioPlayerSlot slot in model.PlayerSlots)
                slotDefs[(int)FactionRegistry.ToFaction(slot.Slot)] =
                    FactionDefinition.LoadFromFile(Path.Combine(root, slot.FactionJson.Substring("res://".Length)));
            int n = PlayerCountPolicy.SimActivePlayers(model.PlayerSlots.Length);
            SimulationHost? host = omitItems
                ? ServerBootstrap.Build(model, slotDefs, damage, new NullLogSink(), n, abilities)
                : ServerBootstrap.Build(model, slotDefs, damage, new NullLogSink(), n, abilities, itemRegistry: items);
            Assert.NotNull(host);
            return host!;
        }

        [Fact]
        public void PreTick_Tick0_IsAPureReadOfTheCurrentChecksum()
        {
            SimSession s = Build();
            uint last = s.Host.LastChecksum;
            PreTickHashes h = s.PreTick();
            Assert.Equal(s.Host.ComputeChecksumNow(), h.Tick0);
            Assert.Equal(0u, s.Host.CurrentTick);
            Assert.Equal(last, s.Host.LastChecksum);
            Assert.NotEqual(0u, h.Tick0);
            Span<ulong> six = stackalloc ulong[PreTickHashes.VALUE_COUNT];
            h.CopyTo(six);
            Assert.Equal(new[] { h.StartState, h.CanonicalModel, h.Content, h.Ruleset, h.Agreement, (ulong)h.Tick0 }, six.ToArray());

            s.Host.ChecksumInterval = 1;
            s.Step();
            Assert.Equal(s.Host.LastChecksum, s.Host.ComputeChecksumNow());
            Assert.Equal(1u, s.Host.CurrentTick);
        }

        [Fact]
        public void ApplyOrder_ReportsDrops_AndADroppedOrderChangesNothing()
        {
            SimSession s = Build();
            EntityWorld w = s.Host.World;
            int p1 = AliveUnits(s, Faction.Player1)[0];
            int p2 = AliveUnits(s, Faction.Player2)[0];

            ulong wide0 = WorldDigest.WideDigest(w, s.Host.Projectiles);
            var enemy = new UnitOrder(w.PackRef(p2), UnitCommand.Move, Fixed.FromInt(10), Fixed.FromInt(-10));
            Assert.False(s.ApplyOrder(Faction.Player1, in enemy));               // not P1's unit
            int staleRef = ((w.Generation[p1] + 1) << EntityWorld.REF_SLOT_BITS) | p1;
            var stale = new UnitOrder(staleRef, UnitCommand.Move, Fixed.FromInt(10), Fixed.FromInt(-10));
            Assert.False(s.ApplyOrder(Faction.Player1, in stale));               // generation mismatch
            Assert.Equal(wide0, WorldDigest.WideDigest(w, s.Host.Projectiles)); // OrderApplier dropped both

            var own = new UnitOrder(w.PackRef(p1), UnitCommand.Move, Fixed.FromInt(10), Fixed.FromInt(-10));
            Assert.True(s.ApplyOrder(Faction.Player1, in own));
            Assert.NotEqual(wide0, WorldDigest.WideDigest(w, s.Host.Projectiles));
            Assert.Equal(UnitCommand.Move, w.CommandState[p1]);
        }

        [Fact]
        public void WorldDigest_FoldsEveryPublicArray_AndSeesUnfoldedState()
        {
            SimSession s = Build();
            EntityWorld w = s.Host.World;
            var visited = new List<string>();
            ulong wide = WorldDigest.WideDigest(w, s.Host.Projectiles, visited);
            Assert.Equal(visited.Count, visited.Distinct().Count());

            var expected = new List<string>();
            foreach (FieldInfo f in typeof(EntityWorld).GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (f.FieldType.IsArray) expected.Add("EntityWorld." + f.Name);
            foreach (FieldInfo f in typeof(ProjectileStore).GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (f.FieldType.IsArray) expected.Add("ProjectileStore." + f.Name);
            Assert.True(expected.Count > 80, $"reflection found only {expected.Count} arrays");
            Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), visited.OrderBy(x => x, StringComparer.Ordinal));
            Assert.Equal(wide, WorldDigest.WideDigest(w, s.Host.Projectiles)); // pure and repeatable

            // Velocity is NOT folded by SimChecksum (plan A F40) but is by the wide digest and the units digest.
            int id = AliveUnits(s, Faction.Player1)[0];
            uint checksum = s.Host.ComputeChecksumNow();
            ulong units = WorldDigest.UnitsDigest(w);
            w.Velocity[id] = new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.Zero);
            Assert.Equal(checksum, s.Host.ComputeChecksumNow());
            Assert.NotEqual(wide, WorldDigest.WideDigest(w, s.Host.Projectiles));
            Assert.NotEqual(units, WorldDigest.UnitsDigest(w));
        }

        [Fact]
        public void OrderScript_CsvRoundTrip_KeepsQueuedFlagAndNegativeRaws()
        {
            byte queuedAttackMove = (byte)((byte)UnitCommand.AttackMove | UnitOrderFlags.Queued);
            var rows = new List<OrderRow>
            {
                new OrderRow(0, (byte)Faction.Player1, 7, (byte)UnitCommand.Move, -3932160, 393216, 0),
                new OrderRow(0, (byte)Faction.Player2, 4103, queuedAttackMove, int.MinValue, int.MaxValue, 0),
                new OrderRow(150, (byte)Faction.Player1, 12, (byte)UnitCommand.AttackTarget, 4105, 0, 0),
                new OrderRow(150, (byte)Faction.Player1, 13, (byte)UnitCommand.CastAbility, -1, -65536, 3),
            };
            var script = new OrderScript(rows);
            string csv = script.Format();
            Assert.StartsWith(OrderScript.HEADER + "\n", csv);
            Assert.Contains("0,2,4103,130,-2147483648,2147483647,0\n", csv);

            OrderScript back = OrderScript.Parse(csv);
            Assert.Equal(rows, back.Rows);
            Assert.Equal(script.Digest(), back.Digest());
            Assert.Equal(csv, back.Format());
            Assert.Equal(rows, OrderScript.Parse(csv.Replace("\n", "\r\n")).Rows); // CRLF tolerated

            UnitOrder o = back.Rows[1].ToUnitOrder();
            Assert.Equal(4103, o.UnitId);
            Assert.Equal(queuedAttackMove, (byte)o.Command);
            Assert.Equal(int.MinValue, o.TargetX);
            Assert.Equal(int.MaxValue, o.TargetZ);

            // The digest is order-sensitive (it folds rows in submit order).
            var swapped = new OrderScript(new List<OrderRow> { rows[1], rows[0], rows[2], rows[3] });
            Assert.NotEqual(script.Digest(), swapped.Digest());

            Assert.Throws<FormatException>(() => OrderScript.Parse("tick,faction\n0,1\n"));
            Assert.Throws<FormatException>(() => OrderScript.Parse(OrderScript.HEADER + "\n5,1,0,1,0,0,0\n4,1,0,1,0,0,0\n"));
            Assert.Throws<FormatException>(() => OrderScript.Parse(OrderScript.HEADER + "\n0,1,0,256,0,0,0\n"));
            Assert.Throws<FormatException>(() => OrderScript.Parse(OrderScript.HEADER + "\n0,1,0,1,0.5,0,0\n"));
        }

        [Fact]
        public void OrderScript_ApplyDue_AppliesRowsAtTheirTick_InFileOrder()
        {
            SimSession s = Build();
            EntityWorld w = s.Host.World;
            List<int> p1 = AliveUnits(s, Faction.Player1);
            int p2 = AliveUnits(s, Faction.Player2)[0];
            var rows = new List<OrderRow>
            {
                new OrderRow(0, (byte)Faction.Player1, w.PackRef(p1[0]), (byte)UnitCommand.Move, Fixed.FromInt(10).Raw, 0, 0),
                new OrderRow(0, (byte)Faction.Player1, w.PackRef(p2), (byte)UnitCommand.Move, 0, 0, 0), // dropped: enemy unit
                new OrderRow(2, (byte)Faction.Player1, w.PackRef(p1[1]), (byte)UnitCommand.Stop, 0, 0, 0),
            };
            var script = new OrderScript(rows);
            int cursor = 0, applied = 0, dropped = 0;
            Assert.Equal(2, script.ApplyDue(s, ref cursor, ref applied, ref dropped));
            Assert.Equal(UnitCommand.Move, w.CommandState[p1[0]]);
            s.Step();
            Assert.Equal(0, script.ApplyDue(s, ref cursor, ref applied, ref dropped)); // tick 1: nothing due
            s.Step();
            Assert.Equal(1, script.ApplyDue(s, ref cursor, ref applied, ref dropped));
            Assert.Equal(3, cursor);
            Assert.Equal(2, applied);
            Assert.Equal(1, dropped);
            Assert.Equal(UnitCommand.Stop, w.CommandState[p1[1]]);

            // A cursor left behind (a skipped tick) fails loudly.
            int behind = 2;
            s.Step();
            Assert.Throws<InvalidOperationException>(() => script.ApplyDue(s, ref behind, ref applied, ref dropped));
        }

        [Fact]
        public void Build_FailsClosed_WithTheFailureClass()
        {
            var missing = Assert.Throws<SimSessionBuildException>(() =>
                SimSession.Build(ContentRoot(), "res://resources/data/scenarios/does_not_exist.json", TrialSeed, false));
            Assert.Equal(SimSessionFailure.ContentLoad, missing.Failure);
        }
    }
}
