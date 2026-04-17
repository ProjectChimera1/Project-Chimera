#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using ProjectChimera.Core;

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// Plays back a recorded match by feeding the stored command stream through the
    /// deterministic simulation in place of live network input.
    ///
    /// Drop-in replacement for the LockstepManager's online Flush() path:
    ///   <c>if (_replayPlayer?.Flush(tick) == true) _simLoop.StepOnce();</c>
    ///
    /// Applies stored orders directly to EntityWorld using the same logic as
    /// LockstepManager.ApplyOrders — no network involvement.
    /// </summary>
    public sealed class ReplayPlayer
    {
        // ── Public info ───────────────────────────────────────────────────────────

        /// <summary>The scenario path embedded in the replay file header.</summary>
        public string ScenarioPath { get; }

        /// <summary>True once all recorded ticks have been applied.</summary>
        public bool IsFinished { get; private set; }

        /// <summary>
        /// Highest tick number recorded in this file.
        /// Used for logging; the replay ends when <see cref="Flush"/> reaches this tick.
        /// </summary>
        public uint LastTick => _lastTick;

        /// <summary>
        /// Total number of tick-faction records in the file (informational; shown in logs).
        /// </summary>
        public int TotalTicks { get; private set; }

        // ── Path-request delegates (mirror LockstepManager) ───────────────────────

        /// <summary>Called when a Move order should request a flow-field path. Args: (unitId, destX, destZ).</summary>
        public Action<int, float, float>? OnRequestPath;
        /// <summary>Called when an AttackMove order should request a path.</summary>
        public Action<int, float, float>? OnRequestAttackMove;
        /// <summary>Called when a Stop or Hold order should cancel any pending path.</summary>
        public Action<int>? OnCancelPath;

        // ── Replay data ───────────────────────────────────────────────────────────

        // Key = simulation tick; value = list of (faction, orders[]) for that tick.
        private readonly Dictionary<uint, List<(Faction Faction, UnitOrder[] Orders, int Count)>> _ticks;
        private uint _lastTick;

        private readonly EntityWorld _world;

        // ── Construction ──────────────────────────────────────────────────────────

        /// <summary>
        /// Load a replay file. Throws <see cref="InvalidDataException"/> if the file is corrupt.
        /// </summary>
        /// <param name="filePath">Absolute path to a .chmr replay file.</param>
        /// <param name="world">The EntityWorld that will be driven during playback.</param>
        public ReplayPlayer(string filePath, EntityWorld world)
        {
            _world = world;
            _ticks = new Dictionary<uint, List<(Faction, UnitOrder[], int)>>(capacity: 512);
            _lastTick = 0;

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: false);

            // ── Parse header ─────────────────────────────────────────────────────
            uint magic = reader.ReadUInt32();
            if (magic != ReplayRecorder.MAGIC)
                throw new InvalidDataException($"Replay: bad magic 0x{magic:X8} in '{filePath}'");

            ushort version = reader.ReadUInt16();
            if (version != ReplayRecorder.VERSION)
                throw new InvalidDataException($"Replay: unsupported version {version}");

            ushort pathLen = reader.ReadUInt16();
            var pathBytes  = reader.ReadBytes(pathLen);
            ScenarioPath   = System.Text.Encoding.UTF8.GetString(pathBytes);

            // ── Parse tick records ────────────────────────────────────────────────
            while (stream.Position < stream.Length - 3) // at least 4 bytes remain
            {
                uint tick = reader.ReadUInt32();
                if (tick == ReplayRecorder.EOF_SENTINEL) break;

                var faction = (Faction)reader.ReadByte();
                int count   = reader.ReadByte();

                var orders = new UnitOrder[count];
                for (int i = 0; i < count; i++)
                {
                    ushort unitId  = reader.ReadUInt16();
                    var    command = (UnitCommand)reader.ReadByte();
                    int    tx      = reader.ReadInt32();
                    int    tz      = reader.ReadInt32();
                    orders[i] = new UnitOrder(unitId, command, Fixed.FromRaw(tx), Fixed.FromRaw(tz));
                }

                if (!_ticks.TryGetValue(tick, out var list))
                {
                    list = new List<(Faction, UnitOrder[], int)>(capacity: 2);
                    _ticks[tick] = list;
                }

                list.Add((faction, orders, count));
                TotalTicks++;

                if (tick > _lastTick)
                    _lastTick = tick;
            }
        }

        // ── Playback ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Apply all stored orders for <paramref name="tick"/> to EntityWorld.
        /// Always returns <c>true</c> — replay never stalls waiting for a peer.
        /// Call from MainScene._Process in place of LockstepManager.Flush().
        /// </summary>
        public bool Flush(uint tick)
        {
            if (_ticks.TryGetValue(tick, out var entries))
            {
                foreach (var (faction, orders, count) in entries)
                    ApplyOrders(orders, count, faction);
            }

            if (tick >= _lastTick)
                IsFinished = true;

            return true;
        }

        // ── Order application (mirrors LockstepManager.ApplyOrders) ──────────────

        private void ApplyOrders(UnitOrder[] orders, int count, Faction expectedFaction)
        {
            for (int i = 0; i < count; i++)
            {
                var o  = orders[i];
                int id = o.UnitId;
                if (!_world.IsAlive(id)) continue;
                if (_world.FactionOf[id] != expectedFaction) continue;

                _world.CommandState[id] = o.Command;

                switch (o.Command)
                {
                    case UnitCommand.Move:
                    {
                        var target = new FixedVec3(Fixed.FromRaw(o.TargetX), Fixed.Zero,
                                                   Fixed.FromRaw(o.TargetZ));
                        _world.CommandGoal[id]  = target;
                        _world.MoveTarget[id]   = target;
                        _world.Flags[id]        = (_world.Flags[id] | EntityFlags.Moving)
                                                  & ~EntityFlags.Attacking;
                        _world.AttackTarget[id] = -1;
                        OnRequestPath?.Invoke(id, Fixed.FromRaw(o.TargetX).ToFloat(),
                                                  Fixed.FromRaw(o.TargetZ).ToFloat());
                        break;
                    }
                    case UnitCommand.AttackMove:
                    {
                        var target = new FixedVec3(Fixed.FromRaw(o.TargetX), Fixed.Zero,
                                                   Fixed.FromRaw(o.TargetZ));
                        _world.CommandGoal[id]  = target;
                        _world.MoveTarget[id]   = target;
                        _world.Flags[id]        = (_world.Flags[id] | EntityFlags.Moving)
                                                  & ~EntityFlags.Attacking;
                        _world.AttackTarget[id] = -1;
                        OnRequestAttackMove?.Invoke(id, Fixed.FromRaw(o.TargetX).ToFloat(),
                                                        Fixed.FromRaw(o.TargetZ).ToFloat());
                        break;
                    }
                    case UnitCommand.Stop:
                    case UnitCommand.HoldPosition:
                    {
                        _world.Flags[id]        = _world.Flags[id]
                                                  & ~(EntityFlags.Moving | EntityFlags.Attacking);
                        _world.AttackTarget[id] = -1;
                        OnCancelPath?.Invoke(id);
                        break;
                    }
                }
            }
        }
    }
}
