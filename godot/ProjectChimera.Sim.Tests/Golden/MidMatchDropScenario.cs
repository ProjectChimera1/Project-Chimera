#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core;                 // EntityWorld, Faction, UnitOrder, UnitCommand, Fixed
using ProjectChimera.Multiplayer;          // TickCommandPacket
using ProjectChimera.Multiplayer.Server;   // MergedTickBuilder, MergedTickApplier, FrozenSlotInjector

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 9.6 — the mid-match freeze-and-continue scenario. Mirrors <see cref="MergedTickN2Scenario"/>'s MERGED
    /// driver (the SAME fixed 2-faction world + order-sensitive <c>bump</c> fold, driven through the REAL
    /// <see cref="MergedTickBuilder"/> + <see cref="MergedTickApplier"/>) BUT after <c>dropAtTick</c> it stops
    /// submitting Player2's real orders and instead calls the REAL <see cref="FrozenSlotInjector.Drain"/> — the same
    /// production injector the <c>DedicatedServer</c> node uses — to inject an EMPTY command for the dropped slot each
    /// tick. So the test exercises production code, not a duplicate: the merged fan-in keeps completing, the surviving
    /// faction plays on, and the dropped faction's units go idle while staying alive + folded into <c>SimChecksum</c>.
    ///
    /// <para>Two independent runs of the drop path must be byte-identical (remaining peer stays in sync), and the
    /// drop run must DIVERGE from a no-drop control (proving the freeze actually changed the sim — non-vacuous).</para>
    /// </summary>
    public static class MidMatchDropScenario
    {
        /// <summary>400 ticks so the sim runs 300+ ticks past the default drop (dropAtTick=100).</summary>
        public const int DefaultTicks   = 400;
        /// <summary>The tick at which Player2 (slot 1) drops — its real orders stop and empties are injected.</summary>
        public const int DefaultDropTick = 100;

        private static readonly Faction[] SlotFaction = { Faction.Player1, Faction.Player2 };
        private static readonly int[] P1Units = { 0, 1 };
        private static readonly int[] P2Units = { 2, 3 };
        /// <summary>The frozen slot set passed to the injector after the drop (Player2 = slot 1). int[] is IReadOnlyList&lt;int&gt;.</summary>
        private static readonly int[] FrozenSlot1 = { 1 };
        private const int BumpEventIndex = 0;

        /// <summary>
        /// The deterministic per-(tick,faction) order fill — byte-identical to <see cref="MergedTickN2Scenario"/>'s
        /// private fill (a Move per unit on oscillating integer targets + one faction-distinct <c>bump</c> raise so
        /// apply order is observable). Kept local so this scenario is self-contained.
        /// </summary>
        private static int FillFactionOrders(int i, Faction faction, int[] units, UnitOrder[] buf)
        {
            int n = 0;
            for (int k = 0; k < units.Length; k++)
            {
                int id = units[k];
                int tx = ((i * 3 + id * 7)  % 30) - 15;
                int tz = ((i * 5 + id * 11) % 24) - 12;
                buf[n++] = new UnitOrder(id, UnitCommand.Move, Fixed.FromInt(tx), Fixed.FromInt(tz));
            }
            int v = faction == Faction.Player1 ? (i % 40) + 1 : (i % 40) + 101;
            buf[n++] = new UnitOrder(BumpEventIndex, UnitCommand.DslEvent, Fixed.FromRaw(v), Fixed.FromRaw(0));
            return n;
        }

        /// <summary>
        /// The mid-match-drop driver: fresh <see cref="MergedTickBuilder"/> for the run. Before <c>dropAtTick</c> both
        /// factions submit and the merged tick builds inline (identical to <see cref="MergedTickN2Scenario"/>). From
        /// <c>dropAtTick</c> on, ONLY Player1 submits and <see cref="FrozenSlotInjector.Drain"/> injects the empty
        /// Player2 command across the unemitted gap up to the survivor's frontier, building + applying each merged tick.
        /// </summary>
        public sealed class DropDriver
        {
            private readonly MergedTickBuilder _builder = new(2, SlotFaction);
            private readonly Func<int, int, int, int, bool> _dslSink;
            private readonly EntityWorld _world;
            private readonly int _dropAtTick;
            private readonly bool _inject;
            private readonly UnitOrder[] _p1 = new UnitOrder[TickCommandPacket.MAX_ORDERS];
            private readonly UnitOrder[] _p2 = new UnitOrder[TickCommandPacket.MAX_ORDERS];
            private readonly byte[] _packBuf   = new byte[TickCommandPacket.HEADER_BYTES + TickCommandPacket.MAX_ORDERS * UnitOrder.SIZE];
            private readonly byte[] _injectBuf = new byte[TickCommandPacket.HEADER_BYTES];
            private uint _frontier;

            /// <param name="inject">When true (production behavior) the frozen slot's empties are injected so the merge
            /// keeps completing and Player1's ongoing orders apply. When FALSE the injector is stubbed out — the merge
            /// STALLS on the never-arriving Player2 slot, so no merged packet is applied after the drop and Player1's
            /// post-drop orders never reach the sim. The false variant is the non-vacuity reference for the golden gate.</param>
            public DropDriver(EntityWorld world, Func<int, int, int, int, bool> dslSink, int dropAtTick, bool inject = true)
            {
                _world = world;
                _dslSink = dslSink;
                _dropAtTick = dropAtTick;
                _inject = inject;
            }

            private void ApplyMerged(byte[] merged, int len) =>
                MergedTickApplier.Apply(merged, len, _world, null, null, null, null, null, null, null, _dslSink);

            public void ApplyTick(int i, EntityWorld world)
            {
                uint tick = (uint)i;

                // Player1 (the survivor) always submits.
                int n1 = FillFactionOrders(i, Faction.Player1, P1Units, _p1);
                int l1 = TickCommandPacket.Write(_packBuf, tick, Faction.Player1, _p1, n1);
                _builder.Submit(0, _packBuf, l1, out _);

                // Player2 submits ONLY before the drop.
                if (i < _dropAtTick)
                {
                    int n2 = FillFactionOrders(i, Faction.Player2, P2Units, _p2);
                    int l2 = TickCommandPacket.Write(_packBuf, tick, Faction.Player2, _p2, n2);
                    _builder.Submit(1, _packBuf, l2, out _);
                }

                if (tick > _frontier) _frontier = tick;

                // Inline build (pre-drop both arrived; post-drop this is a no-op and the injector completes the tick).
                if (_builder.TryBuild(tick, out byte[] merged, out int len))
                    ApplyMerged(merged, len);

                // After the drop, inject empties for the frozen slot across the whole gap → the REAL production drain.
                // With _inject == false the merge deliberately stalls (the non-vacuity reference).
                if (_inject && i >= _dropAtTick)
                    FrozenSlotInjector.Drain(_builder, FrozenSlot1, SlotFaction, _frontier, _injectBuf, ApplyMerged);
            }
        }

        /// <summary>Run the drop path (Player2 frozen at <paramref name="dropAtTick"/>) with a FRESH builder.</summary>
        public static IReadOnlyList<GoldenChecksumReplay.Sample> RunDrop(int ticks = DefaultTicks, int dropAtTick = DefaultDropTick)
        {
            GoldenHarness h = MergedTickN2Scenario.BuildHost();
            var driver = new DropDriver(h.World, h.Host.DslEventSink, dropAtTick, inject: true);
            return Run(h, ticks, (i, w) => driver.ApplyTick(i, w));
        }

        /// <summary>
        /// Run the drop WITHOUT injection — the non-vacuity reference. Player2's real orders stop AND no empties are
        /// injected, so the merge stalls and Player1's post-drop orders never apply. If the real injector were a no-op,
        /// <see cref="RunDrop"/> would reproduce THIS sequence; the golden gate asserts they diverge, proving the
        /// injector actually delivers Player1's ongoing commands post-drop (not merely "Player2 went away").
        /// </summary>
        public static IReadOnlyList<GoldenChecksumReplay.Sample> RunDropNoInject(int ticks = DefaultTicks, int dropAtTick = DefaultDropTick)
        {
            GoldenHarness h = MergedTickN2Scenario.BuildHost();
            var driver = new DropDriver(h.World, h.Host.DslEventSink, dropAtTick, inject: false);
            return Run(h, ticks, (i, w) => driver.ApplyTick(i, w));
        }

        /// <summary>Run the NO-DROP control (both factions submit for all ticks) — the divergence baseline.</summary>
        public static IReadOnlyList<GoldenChecksumReplay.Sample> RunNoDrop(int ticks = DefaultTicks)
        {
            GoldenHarness h = MergedTickN2Scenario.BuildHost();
            // dropAtTick == ticks ⇒ the drop never triggers within the run → both factions submit throughout.
            var driver = new DropDriver(h.World, h.Host.DslEventSink, ticks, inject: true);
            return Run(h, ticks, (i, w) => driver.ApplyTick(i, w));
        }

        private static IReadOnlyList<GoldenChecksumReplay.Sample> Run(GoldenHarness h, int ticks, Action<int, EntityWorld> perturb)
        {
            var seq = new List<GoldenChecksumReplay.Sample>(ticks);
            h.Host.SetChecksumSink((tick, hash) => seq.Add(new GoldenChecksumReplay.Sample(tick, hash)));
            for (int i = 0; i < ticks; i++)
            {
                perturb(i, h.World);
                h.Host.StepOnce();
            }
            return seq;
        }
    }
}
