#nullable enable
using System;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// DW-447 — one INDEPENDENT per-peer simulation for the desync self-test surface: its own
    /// <see cref="SimulationHost"/> (fresh stores, nothing shared between instances) driven by a deterministic
    /// scripted order stream, computing its own REAL per-tick <see cref="ProjectChimera.Core.SimChecksum"/>.
    ///
    /// Two callers share this one class so the surfaces cannot drift:
    /// <list type="bullet">
    ///   <item><c>LoopbackDesyncSelfTest</c> (DEBUG, in-Godot): each of the 4 loopback ENet peers steps ITS OWN
    ///     instance and sends the computed hash — no longer the hardcoded <c>GOOD</c> constant, so the server's
    ///     quorum now compares 4 independently-computed checksums over the real transport (and a genuine
    ///     divergence FAILS the self-test instead of being structurally impossible).</item>
    ///   <item><c>IndependentPeerSimQuorumTests</c> (Tier-1, Godot-free): N instances stepped in lockstep feed
    ///     their own hashes into a real <c>ServerHost</c> quorum — the headless "N independent sims compute +
    ///     compare their own checksums" artifact DW-447 records as missing.</item>
    /// </list>
    ///
    /// The scripted content is the proven-deterministic MergedTickN2Scenario shape: a fixed 2-faction world
    /// (2 movable Player1 units, 2 movable Player2 units) with oscillating integer Move targets — pure
    /// Fixed/integer math, a byte-identical function of the step index on every instance, so N instances stepped
    /// the same number of times MUST agree; any disagreement is a real determinism regression. Godot-free
    /// (System.* + Core + Definitions only) and single-file-included in SimSources.props.
    /// </summary>
    internal sealed class LoopbackPeerSim
    {
        /// <summary>Units in the scripted world: ids 0,1 = Player1; ids 2,3 = Player2.</summary>
        internal const int UnitCount = 4;

        private readonly SimulationHost _host;
        private int  _step;
        private uint _lastTick;
        private uint _lastHash;
        private bool _sinkFired;

        /// <summary>The wrapped host's current tick (0 until the first <see cref="Step"/>).</summary>
        internal uint CurrentTick => _host.CurrentTick;

        /// <summary>Build the fixed deterministic world. Fresh stores per instance — no static/shared state.</summary>
        internal LoopbackPeerSim()
        {
            _host = SimulationHost.Create(
                NullLogSink.Instance, new FactionRegistry(2), new FactionDefinition(), new FactionDefinition());
            _host.ChecksumInterval = 1; // a real computed checksum EVERY tick — the self-test sends each one
            _host.SetChecksumSink((tick, hash) => { _lastTick = tick; _lastHash = hash; _sinkFired = true; });

            int a = _host.World.Create(new FixedVec3(Fixed.FromInt(-10), Fixed.Zero, Fixed.FromInt(0)), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            int b = _host.World.Create(new FixedVec3(Fixed.FromInt(-10), Fixed.Zero, Fixed.FromInt(4)), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            int c = _host.World.Create(new FixedVec3(Fixed.FromInt( 10), Fixed.Zero, Fixed.FromInt(0)), Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));
            int d = _host.World.Create(new FixedVec3(Fixed.FromInt( 10), Fixed.Zero, Fixed.FromInt(4)), Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));
            if (a != 0 || b != 1 || c != 2 || d != 3)
                throw new InvalidOperationException(
                    $"LoopbackPeerSim invariant broken: unit ids were {a},{b},{c},{d}, expected 0,1,2,3.");
        }

        /// <summary>
        /// Advance exactly one tick: apply the deterministic scripted Move orders for this step index (each unit
        /// ordered under its OWN faction through the shared <see cref="OrderApplier"/> — the one command→world
        /// path), step the 16-system loop once, and return the REAL computed (tick, checksum) pair the sink
        /// captured. Fails loud if the checksum sink did not fire (it must, with ChecksumInterval = 1).
        /// </summary>
        internal (uint tick, uint hash) Step()
        {
            int i = _step++;
            for (int id = 0; id < UnitCount; id++)
            {
                // The MergedTickN2Scenario oscillating-target idiom: pure integer, a function of (i, id) only.
                int tx = ((i * 3 + id * 7)  % 30) - 15;
                int tz = ((i * 5 + id * 11) % 24) - 12;
                var order = new UnitOrder(id, UnitCommand.Move, Fixed.FromInt(tx), Fixed.FromInt(tz));
                OrderApplier.Apply(_host.World, in order, id < 2 ? Faction.Player1 : Faction.Player2);
            }

            _sinkFired = false;
            _host.StepOnce();
            if (!_sinkFired)
                throw new InvalidOperationException(
                    "LoopbackPeerSim checksum sink did not fire — ChecksumInterval must be 1 and checksums enabled.");
            return (_lastTick, _lastHash);
        }
    }
}
