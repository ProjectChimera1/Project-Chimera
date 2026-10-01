#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Multiplayer;

namespace ProjectChimera.SimTrial
{
    /// <summary>
    /// The scripted orders of scenario <c>trial_1000</c> (plan A section 3.3), ported one-to-one from the prototype
    /// <c>research/proto-a/proto/Program.cs</c> <c>Script</c>/<c>Issue</c>: batches at ticks 0/150/300/450/600/900/1200
    /// (and 1800 for runs longer than 1,440 ticks). It is used by <c>gen</c> ONLY: it looks at the live world (which
    /// units are alive, which enemies are alive at tick 1200), issues each order through
    /// <see cref="SimSession.ApplyOrder"/> and records it as an <see cref="OrderRow"/>. Every other leg replays the
    /// frozen CSV, so this script is never re-implemented per language.
    /// </summary>
    internal sealed class TrialScript
    {
        private readonly SimSession _s;
        private readonly List<int> _p1 = new();
        private readonly List<int> _p2 = new();
        private readonly List<OrderRow> _rows = new();
        private int _dropped;

        public TrialScript(SimSession session)
        {
            _s = session;
            EntityWorld w = session.Host.World;
            // Combat-capable unit lists per faction, ascending id (workers excluded).
            for (int id = 0; id < w.HighWaterMark; id++)
            {
                if (!w.IsAlive(id)) continue;
                string t = TypeOf(id);
                if (t == "worker" || t == "forgehand") continue;
                (w.FactionOf[id] == Faction.Player1 ? _p1 : _p2).Add(id);
            }
        }

        /// <summary>Rows issued so far, in submit order.</summary>
        public IReadOnlyList<OrderRow> Rows => _rows;

        /// <summary>Orders <see cref="SimSession.ApplyOrder"/> reported as dropped (0 for a healthy run).</summary>
        public int Dropped => _dropped;

        /// <summary>Orders that reached a live unit of the issuing faction.</summary>
        public int Applied => _rows.Count - _dropped;

        private string TypeOf(int id)
        {
            EntityWorld w = _s.Host.World;
            FactionDefinition? def = _s.SlotFactionDefs[(int)w.FactionOf[id]];
            int mt = w.MeshType[id];
            return def != null && mt >= 0 && mt < def.Units.Count ? def.Units[mt].Id : "?";
        }

        private void Issue(int tick, int id, UnitCommand cmd, Fixed x, Fixed z, bool queued = false)
        {
            EntityWorld w = _s.Host.World;
            if (!w.IsAlive(id)) return; // a dead unit is simply not ordered (the prototype's rule)
            byte cb = (byte)cmd;
            if (queued) cb |= UnitOrderFlags.Queued;
            var row = new OrderRow(tick, (byte)w.FactionOf[id], w.PackRef(id), cb, x.Raw, z.Raw, 0);
            _rows.Add(row);
            UnitOrder o = row.ToUnitOrder();
            if (!_s.ApplyOrder(row.IssuingFaction, in o)) _dropped++;
        }

        /// <summary>Issue the batch scheduled for <paramref name="tick"/> (the world's current tick), if any.</summary>
        public void IssueFor(int tick)
        {
            EntityWorld w = _s.Host.World;
            static Fixed F(int v) => Fixed.FromInt(v);
            switch (tick)
            {
                case 0:   // everyone marches: AttackMove at the enemy line, z fanned over 13 goal cells
                    for (int k = 0; k < _p1.Count; k++) Issue(tick, _p1[k], UnitCommand.AttackMove, F(60), F(((k % 13) - 6) * 2));
                    for (int k = 0; k < _p2.Count; k++) Issue(tick, _p2[k], UnitCommand.AttackMove, F(-60), F(((k % 13) - 6) * 2));
                    break;
                case 150: // flank + air
                    foreach (int id in _p1) { string t = TypeOf(id); if (t == "scout") Issue(tick, id, UnitCommand.Move, F(0), F(-90)); if (t == "griffin") Issue(tick, id, UnitCommand.AttackMove, F(60), F(-30)); }
                    foreach (int id in _p2) { if (TypeOf(id) == "wyvern") Issue(tick, id, UnitCommand.AttackMove, F(-60), F(30)); }
                    break;
                case 300: // queued follow-up on the scouts (Shift-queue: Move now, AttackMove after)
                    foreach (int id in _p1) if (TypeOf(id) == "scout") { Issue(tick, id, UnitCommand.Move, F(40), F(-80)); Issue(tick, id, UnitCommand.AttackMove, F(95), F(-5), queued: true); }
                    break;
                case 450: // siege forward
                    foreach (int id in _p1) if (TypeOf(id) == "siege_engine") Issue(tick, id, UnitCommand.AttackMove, F(50), F(0));
                    foreach (int id in _p2) if (TypeOf(id) == "war_machine") Issue(tick, id, UnitCommand.AttackMove, F(-50), F(0));
                    break;
                case 600: // re-target survivors at the enemy base side
                    foreach (int id in _p1) { string t = TypeOf(id); if (t != "worker") Issue(tick, id, UnitCommand.AttackMove, F(85), F(0)); }
                    foreach (int id in _p2) { string t = TypeOf(id); if (t != "forgehand") Issue(tick, id, UnitCommand.AttackMove, F(-85), F(0)); }
                    break;
                case 900: // hold / stop subsets
                    foreach (int id in _p1) if (TypeOf(id) == "heavy_infantry") Issue(tick, id, UnitCommand.HoldPosition, F(0), F(0));
                    foreach (int id in _p2) if (TypeOf(id) == "bulwark") Issue(tick, id, UnitCommand.Stop, F(0), F(0));
                    break;
                case 1200: // force-attack: each P1 archer targets the live P2 combat units in turn (ascending id)
                {
                    var targets = new List<int>();
                    foreach (int id in _p2) if (w.IsAlive(id)) targets.Add(id);
                    int k = 0;
                    foreach (int id in _p1) if (TypeOf(id) == "archer" && targets.Count > 0)
                    {
                        int tgt = targets[k++ % targets.Count];
                        Issue(tick, id, UnitCommand.AttackTarget, Fixed.FromRaw(w.PackRef(tgt)), Fixed.Zero);
                    }
                    break;
                }
                case 1800: // final push
                    foreach (int id in _p1) { string t = TypeOf(id); if (t != "worker") Issue(tick, id, UnitCommand.AttackMove, F(0), F(0)); }
                    foreach (int id in _p2) { string t = TypeOf(id); if (t != "forgehand") Issue(tick, id, UnitCommand.AttackMove, F(0), F(0)); }
                    break;
            }
        }
    }
}
