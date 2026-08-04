#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core;                 // EntityWorld, Faction, UnitOrder, UnitCommand, Fixed
using ProjectChimera.Core.Definitions;     // FactionDefinition, ScenarioData
using ProjectChimera.Core.Sim;             // SimulationHost, NullLogSink
using ProjectChimera.Combat;               // AttackDelivery, DamageType
using ProjectChimera.Multiplayer;          // TickCommandPacket
using ProjectChimera.Multiplayer.Server;   // MergedTickBuilder, MergedTickApplier, FrozenSlotInjector
using ProjectChimera.Sim.Tests.Effects;    // PassiveTestAbilities (the in-code passive defs)

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 9.6 — the mid-match freeze-and-continue scenario. Mirrors <see cref="MergedTickN2Scenario"/>'s MERGED
    /// driver (the SAME fixed 2-faction mover set + order-sensitive <c>bump</c> fold, driven through the REAL
    /// <see cref="MergedTickBuilder"/> + <see cref="MergedTickApplier"/>) BUT after <c>dropAtTick</c> it stops
    /// submitting Player2's real orders and instead calls the REAL <see cref="FrozenSlotInjector.Drain"/> — the same
    /// production injector the <c>DedicatedServer</c> node uses — to inject an EMPTY command for the dropped slot each
    /// tick. So the test exercises production code, not a duplicate: the merged fan-in keeps completing, the surviving
    /// faction plays on, and the dropped faction's units go idle while staying alive + folded into <c>SimChecksum</c>.
    ///
    /// <para>DW-413 — the world additionally constructs AC3's NAMED passive-sim straddle cases, both owned by the
    /// DROPPED faction so the freeze is what they straddle: a slow Player2 PROJECTILE volley in flight across the
    /// drop tick (speed 2 over distance 10 = a 150-tick flight; shots fired pre-drop land well post-drop), and a
    /// Player2 unit MID-HEALTH-REGEN at the drop (pre-damaged to 50/100, +2 HP per 5 ticks → still healing at tick
    /// 100, full ~tick 125). Passive combat/regen are SIM systems — a frozen command stream must not touch them.</para>
    ///
    /// <para>The gate over this scenario is relative but POSITIVE (DW-416): two independent drop runs must be
    /// byte-identical; the drop run must DIVERGE from a no-drop control AND from a no-injection reference; and — the
    /// idle-equivalence pin — it must be BYTE-IDENTICAL to a control where Player2 stays connected and explicitly
    /// submits empty (idle) command packets every post-drop tick. This scenario is deliberately baseline-free (no
    /// committed golden), so nothing here can move an existing golden.</para>
    /// </summary>
    public static class MidMatchDropScenario
    {
        /// <summary>400 ticks so the sim runs 300+ ticks past the default drop (dropAtTick=100).</summary>
        public const int DefaultTicks   = 400;
        /// <summary>The tick at which Player2 (slot 1) drops — its real orders stop and empties are injected.</summary>
        public const int DefaultDropTick = 100;

        // ── DW-413 fixture ids (asserted by BuildDropHost — see the invariant throw) ──────────────────────────
        /// <summary>Player2's slow-projectile attacker (Delivery=Projectile, speed 2, auto-attacking id 5).</summary>
        public const int ProjectileAttackerId = 4;
        /// <summary>The attacker's high-HP Neutral target — its dropping Health proves shots keep landing post-drop.</summary>
        public const int ProjectileTargetId   = 5;
        /// <summary>Player2's self-regen unit, pre-damaged to 50/100 — mid-regen when the drop hits.</summary>
        public const int RegenUnitId          = 6;
        /// <summary>The regen unit's pre-damaged starting health.</summary>
        public static readonly Fixed RegenStartHealth = Fixed.FromInt(50);
        /// <summary>The regen unit's MaxHealth (regen completes here, post-drop).</summary>
        public static readonly Fixed RegenMaxHealth   = Fixed.FromInt(100);

        private static readonly Faction[] SlotFaction = { Faction.Player1, Faction.Player2 };
        private static readonly int[] P1Units = { 0, 1 };
        private static readonly int[] P2Units = { 2, 3 };
        /// <summary>The frozen slot set passed to the injector after the drop (Player2 = slot 1). int[] is IReadOnlyList&lt;int&gt;.</summary>
        private static readonly int[] FrozenSlot1 = { 1 };
        private const int BumpEventIndex = 0;

        /// <summary>
        /// Construct the drop world: the four movable units of <see cref="MergedTickN2Scenario"/> (ids 0,1 = P1 and
        /// 2,3 = P2, driven by the per-tick Move+bump order stream) PLUS the DW-413 passive straddle elements, all
        /// far from the movers' oscillation box (x∈[−15,15), z∈[−12,12)) so they never interact with it:
        ///   • id 4 (P2) — a projectile attacker at (0,0,40): Delivery=Projectile, ProjectileSpeed 2, AttackRange 12,
        ///     1 attack/s at the Neutral 10 units away ⇒ each shot flies ~150 ticks, so several are ALWAYS in flight
        ///     across the default drop at tick 100 (fired pre-drop, landing post-drop);
        ///   • id 5 (Neutral) — the 999-HP target (survives the run; its falling Health is the landing signal);
        ///   • id 6 (P2) — a self-regen unit at (−10,0,40) (<c>furnace_trickle</c>: +2 HP per 5 ticks, installed at
        ///     the production spawn seam), pre-damaged to 50/100 ⇒ mid-regen at tick 100, full ~tick 125 (post-drop).
        /// The scenario keeps the order-sensitive <c>bump</c> fold. Fresh stores per call — no static/shared state.
        /// Uses its OWN host builder (NOT <see cref="MergedTickN2Scenario.BuildHost"/>) so the committed
        /// golden-merged-n2 golden is untouched by the added units.
        /// </summary>
        public static GoldenHarness BuildDropHost()
        {
            var registry = new AbilityRegistry(new[]
            {
                PassiveTestAbilities.FurnaceTrickle(), // while_alive: Persistent(Heal 2 every 5 ticks)
            });

            var host = SimulationHost.Create(
                NullLogSink.Instance, new FactionRegistry(2), new FactionDefinition(), new FactionDefinition(),
                registry: registry);
            host.ChecksumInterval = 1;

            EntityWorld w = host.World;

            // ids 0..3 — the MergedTickN2Scenario mover set, byte-identical placement.
            int a = w.Create(V(-10, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            int b = w.Create(V(-10, 0, 4), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            int c = w.Create(V( 10, 0, 0), Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));
            int d = w.Create(V( 10, 0, 4), Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));

            // id 4 — DW-413: Player2's slow-projectile attacker (auto-acquires the Neutral at distance 10 ≤ 12).
            int shooter = w.Create(V(0, 0, 40), Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));
            w.EffectiveAttackDamage[shooter] = Fixed.FromInt(10);
            w.AttackRange[shooter]     = Fixed.FromInt(12);
            w.AttackSpeed[shooter]     = Fixed.FromInt(1);
            w.DamageTypeOf[shooter]    = DamageType.Normal;
            w.Delivery[shooter]        = AttackDelivery.Projectile;
            w.ProjectileSpeed[shooter] = Fixed.FromInt(2);          // 10 units at 2/s ⇒ ~150 ticks in flight
            w.CommandState[shooter]    = UnitCommand.Idle;

            // id 5 — the shooter's high-HP Neutral target (never fights back, survives the run).
            int target = w.Create(V(10, 0, 40), Faction.Neutral, Fixed.FromInt(999), Fixed.FromInt(3));

            // id 6 — DW-413: Player2's self-regen unit, pre-damaged; the Persistent HoT is installed by firing the
            // SAME production spawn seam PassiveScenario uses (OnUnitDefinitionApplied → while_alive install).
            int regen = w.Create(V(-10, 0, 40), Faction.Player2, RegenMaxHealth, Fixed.Zero);
            w.SelfPassiveAbilityIndex[regen] = registry.IndexOf("furnace_trickle");
            w.Health[regen] = RegenStartHealth;
            w.OnUnitDefinitionApplied?.Invoke(regen);

            if (a != 0 || b != 1 || c != 2 || d != 3 ||
                shooter != ProjectileAttackerId || target != ProjectileTargetId || regen != RegenUnitId)
                throw new InvalidOperationException(
                    $"MidMatchDropScenario invariant broken: unit ids were {a},{b},{c},{d},{shooter},{target},{regen} — " +
                    "expected 0..6.");

            host.ScenarioDirector.LoadScenario(MergedTickN2Scenario.BuildOrderSensitiveScenario());
            return new GoldenHarness(host, 0);
        }

        /// <summary>
        /// The deterministic per-(tick,faction) order fill — byte-identical to <see cref="MergedTickN2Scenario"/>'s
        /// private fill (a Move per unit on oscillating integer targets + one faction-distinct <c>bump</c> raise so
        /// apply order is observable). Kept local so this scenario is self-contained. The DW-413 passive units
        /// (ids 4..6) are deliberately NOT in the order stream — they exercise the sim continuing WITHOUT commands.
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

        /// <summary>What Player2's slot does from <c>dropAtTick</c> onward.</summary>
        public enum PostDropMode
        {
            /// <summary>Production drop behavior: Player2 vanishes and the REAL <see cref="FrozenSlotInjector.Drain"/>
            /// injects its empties so the merge keeps completing (the run under test).</summary>
            FrozenInjected,
            /// <summary>Player2 vanishes and NOTHING is injected — the merge stalls, Player1's post-drop orders never
            /// apply. The non-vacuity reference: a no-op injector would reproduce exactly this.</summary>
            VanishNoInject,
            /// <summary>DW-416's idle-equivalence control: Player2 STAYS CONNECTED and explicitly submits an EMPTY
            /// (zero-order) command packet every tick — the genuine "player idles at the keyboard" stream. The real
            /// drop run must be byte-identical to this, proving the freeze equals IDLE, not merely "different".</summary>
            ExplicitIdle,
        }

        /// <summary>
        /// The mid-match-drop driver: fresh <see cref="MergedTickBuilder"/> for the run. Before <c>dropAtTick</c> both
        /// factions submit and the merged tick builds inline (identical to <see cref="MergedTickN2Scenario"/>). From
        /// <c>dropAtTick</c> on, ONLY Player1 submits real orders and the slot-1 stream follows
        /// <see cref="PostDropMode"/>: production injection, nothing (the stall reference), or explicit idle submits.
        /// </summary>
        public sealed class DropDriver
        {
            private readonly MergedTickBuilder _builder = new(2, SlotFaction);
            private readonly Func<int, int, int, int, bool> _dslSink;
            private readonly EntityWorld _world;
            private readonly int _dropAtTick;
            private readonly PostDropMode _mode;
            private readonly UnitOrder[] _p1 = new UnitOrder[TickCommandPacket.MAX_ORDERS];
            private readonly UnitOrder[] _p2 = new UnitOrder[TickCommandPacket.MAX_ORDERS];
            private readonly byte[] _packBuf   = new byte[TickCommandPacket.HEADER_BYTES + TickCommandPacket.MAX_ORDERS * UnitOrder.SIZE];
            private readonly byte[] _injectBuf = new byte[TickCommandPacket.HEADER_BYTES];
            private uint _frontier;

            public DropDriver(EntityWorld world, Func<int, int, int, int, bool> dslSink, int dropAtTick,
                              PostDropMode mode = PostDropMode.FrozenInjected)
            {
                _world = world;
                _dslSink = dslSink;
                _dropAtTick = dropAtTick;
                _mode = mode;
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

                if (i < _dropAtTick)
                {
                    // Player2 submits its real orders only before the drop.
                    int n2 = FillFactionOrders(i, Faction.Player2, P2Units, _p2);
                    int l2 = TickCommandPacket.Write(_packBuf, tick, Faction.Player2, _p2, n2);
                    _builder.Submit(1, _packBuf, l2, out _);
                }
                else if (_mode == PostDropMode.ExplicitIdle)
                {
                    // DW-416: the idle control — Player2 stays connected and submits a ZERO-order packet, the
                    // genuine idle command stream the frozen-slot injection claims to be equivalent to.
                    int l2 = TickCommandPacket.Write(_packBuf, tick, Faction.Player2, _p2, 0);
                    _builder.Submit(1, _packBuf, l2, out _);
                }

                if (tick > _frontier) _frontier = tick;

                // Inline build (a no-op while the tick's fan-in is incomplete).
                if (_builder.TryBuild(tick, out byte[] merged, out int len))
                    ApplyMerged(merged, len);

                // After the drop, inject empties for the frozen slot across the whole gap → the REAL production
                // drain. In VanishNoInject the merge deliberately stalls (the non-vacuity reference); in
                // ExplicitIdle nothing is frozen (Player2 submitted above).
                if (_mode == PostDropMode.FrozenInjected && i >= _dropAtTick)
                    FrozenSlotInjector.Drain(_builder, FrozenSlot1, SlotFaction, _frontier, _injectBuf, ApplyMerged);
            }
        }

        /// <summary>Run the drop path (Player2 frozen at <paramref name="dropAtTick"/>) with a FRESH builder.</summary>
        public static IReadOnlyList<GoldenChecksumReplay.Sample> RunDrop(int ticks = DefaultTicks, int dropAtTick = DefaultDropTick)
            => Run(ticks, dropAtTick, PostDropMode.FrozenInjected);

        /// <summary>
        /// Run the drop WITHOUT injection — the non-vacuity reference. Player2's real orders stop AND no empties are
        /// injected, so the merge stalls and Player1's post-drop orders never apply. If the real injector were a no-op,
        /// <see cref="RunDrop"/> would reproduce THIS sequence; the golden gate asserts they diverge, proving the
        /// injector actually delivers Player1's ongoing commands post-drop (not merely "Player2 went away").
        /// </summary>
        public static IReadOnlyList<GoldenChecksumReplay.Sample> RunDropNoInject(int ticks = DefaultTicks, int dropAtTick = DefaultDropTick)
            => Run(ticks, dropAtTick, PostDropMode.VanishNoInject);

        /// <summary>
        /// DW-416 — run the EXPLICIT-IDLE control: Player2 stays connected and submits an empty (zero-order) packet
        /// every tick from <paramref name="dropAtTick"/> on. The positive baseline the drop run must equal
        /// byte-for-byte: "frozen via injection" must be indistinguishable from "the player genuinely idles".
        /// </summary>
        public static IReadOnlyList<GoldenChecksumReplay.Sample> RunDropIdleControl(int ticks = DefaultTicks, int dropAtTick = DefaultDropTick)
            => Run(ticks, dropAtTick, PostDropMode.ExplicitIdle);

        /// <summary>Run the NO-DROP control (both factions submit for all ticks) — the divergence baseline.</summary>
        public static IReadOnlyList<GoldenChecksumReplay.Sample> RunNoDrop(int ticks = DefaultTicks)
            // dropAtTick == ticks ⇒ the drop never triggers within the run → both factions submit throughout.
            => Run(ticks, ticks, PostDropMode.FrozenInjected);

        private static IReadOnlyList<GoldenChecksumReplay.Sample> Run(int ticks, int dropAtTick, PostDropMode mode)
        {
            GoldenHarness h = BuildDropHost();
            var driver = new DropDriver(h.World, h.Host.DslEventSink, dropAtTick, mode);
            var seq = new List<GoldenChecksumReplay.Sample>(ticks);
            h.Host.SetChecksumSink((tick, hash) => seq.Add(new GoldenChecksumReplay.Sample(tick, hash)));
            for (int i = 0; i < ticks; i++)
            {
                driver.ApplyTick(i, h.World);
                h.Host.StepOnce();
            }
            return seq;
        }

        private static FixedVec3 V(int x, int y, int z) =>
            new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));
    }
}
