#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Multiplayer;

namespace ProjectChimera.SimTrial.Chmr
{
    /// <summary>
    /// Unreal trial A14 (plan A section 4 A14): the frozen trial order CSV as a replay v8 <c>.chmr</c>, and its
    /// headless control replay.
    ///
    /// <para><b>write</b>: the header is the exact v8 layout <see cref="ReplayRecorder"/> writes (ReplayRecorder.cs
    /// WriteHeader: magic, version, scenario path, seed, canonical scenario hash, ruleset hash, model algo, roster, AI
    /// mask); every body frame is the frozen <see cref="MergedTickPacket.Write"/> codec. One deviation from the recorder,
    /// forced by the scenario: <see cref="ReplayRecorder.RecordTick"/> buffers one merged frame per tick, at most
    /// <see cref="MergedTickPacket.MERGED_MAX_SUBBUNDLES"/> (8) sub-bundles of <see cref="TickCommandPacket.MAX_ORDERS"/>
    /// (32) orders = 256 orders per tick, and its FlushTick re-sorts sub-bundles by faction with an unstable selection
    /// sort; trial_1000 issues 960 orders at tick 0 and 848 at tick 600. So a tick's orders are written as several
    /// consecutive merged frames carrying the SAME tick (chunks of up to 32 consecutive same-faction rows, up to 8 chunks
    /// per frame), in CSV order. <see cref="ReplayPlayer"/> accepts that unchanged (its body loop appends every decoded
    /// sub-bundle to the tick's list in file order, ReplayPlayer.cs:257-281), so playback applies the rows in exactly the
    /// CSV order every other leg uses (OrderScript.ApplyDue). An EMPTY merged frame (0 sub-bundles) at the end tick sets
    /// the player's LastTick, so playback steps through tick 1440 instead of finishing after the last order (tick 1200).
    /// The write ends with the recorder's result trailer (incomplete, no winner) and the frame-length EOF.</para>
    ///
    /// <para><b>verify</b>: plays the .chmr through the REAL <see cref="ReplayPlayer"/> (wired as
    /// MatchLifecycleController.TryLoadReplay wires it) on a <see cref="SimSession"/> and compares every tick with the
    /// golden. That is the managed control: when it is 1440/1440, the file carries the match exactly, so a MainScene
    /// divergence is MainScene's.</para>
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0) return Usage();
                var o = ParseOptions(args, 1);
                switch (args[0])
                {
                    case "write":  return Write(o);
                    case "verify": return Verify(o);
                    default:       return Usage();
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("ERROR: " + e);
                return 2;
            }
        }

        private static int Usage()
        {
            Console.Error.WriteLine("usage: ChmrTool write  --content <godot dir> --scenario <json> --orders <csv> --seed <0x..> [--ai] --end-tick 1440 --out <file.chmr>");
            Console.Error.WriteLine("       ChmrTool verify --content <godot dir> --scenario <json> --chmr <file> --seed <0x..> [--ai] --ticks 1440 --golden <golden.txt> --out <trace.txt>");
            return 2;
        }

        private sealed class Options
        {
            public string Content = "", Scenario = "", Orders = "", Out = "", Chmr = "", Golden = "";
            public ulong Seed;
            public bool Ai;
            public int EndTick = 1440, Ticks = 1440;
        }

        private static Options ParseOptions(string[] a, int start)
        {
            var o = new Options();
            for (int i = start; i < a.Length; i++)
            {
                string k = a[i];
                string Next() => i + 1 < a.Length ? a[++i] : throw new ArgumentException($"{k} needs a value");
                switch (k)
                {
                    case "--content":  o.Content = Next(); break;
                    case "--scenario": o.Scenario = Next(); break;
                    case "--orders":   o.Orders = Next(); break;
                    case "--out":      o.Out = Next(); break;
                    case "--chmr":     o.Chmr = Next(); break;
                    case "--golden":   o.Golden = Next(); break;
                    case "--seed":     o.Seed = ParseSeed(Next()); break;
                    case "--ai":       o.Ai = true; break;
                    case "--end-tick": o.EndTick = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--ticks":    o.Ticks = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    default: throw new ArgumentException($"unknown option {k}");
                }
            }
            return o;
        }

        private static ulong ParseSeed(string s) =>
            s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? ulong.Parse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : ulong.Parse(s, CultureInfo.InvariantCulture);

        // ── write ────────────────────────────────────────────────────────────────

        private static int Write(Options o)
        {
            // The same loaders the game uses, so the header's scenario hash is the one TryLoadReplay recomputes from the
            // scenario MainScene loads (CanonicalModelHash.Compute over the loaded model, MatchLifecycleController.cs).
            SimSession s = SimSession.Build(o.Content, o.Scenario, o.Seed, o.Ai);
            PreTickHashes pre = s.PreTick();
            ulong scenarioHash = CanonicalModelHash.Compute(s.Model);
            ulong rulesetHash  = RulesetHash.Compute();
            int   algo         = CanonicalModelHash.AlgoVersion;
            int   mask         = s.AiPlan.Mask;
            var   roster       = new[] { Faction.Player1, Faction.Player2 };
            OrderScript script = OrderScript.Load(o.Orders);

            string scenarioAbs = Path.GetFullPath(o.Scenario).Replace('\\', '/');
            int frames = 0, maxFrameOrders = 0, emptyFrames = 0;
            using (var fs = new FileStream(o.Out, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false))
            {
                // Header — ReplayRecorder.WriteHeader, field for field.
                w.Write(ReplayRecorder.MAGIC);
                w.Write(ReplayRecorder.VERSION);
                byte[] pathBytes = Encoding.UTF8.GetBytes(scenarioAbs);
                w.Write((ushort)pathBytes.Length);
                w.Write(pathBytes);
                w.Write(o.Seed);
                w.Write(scenarioHash);
                w.Write(rulesetHash);
                w.Write(algo);
                w.Write((ushort)roster.Length);
                foreach (Faction f in roster) w.Write((byte)f);
                w.Write(mask);

                // Body — consecutive merged frames per tick, CSV order preserved.
                var buf       = new byte[MergedTickPacket.MERGED_MAX_BYTES];
                var factions  = new Faction[MergedTickPacket.MERGED_MAX_SUBBUNDLES];
                var counts    = new int[MergedTickPacket.MERGED_MAX_SUBBUNDLES];
                var flat      = new UnitOrder[MergedTickPacket.MERGED_MAX_SUBBUNDLES * TickCommandPacket.MAX_ORDERS];
                IReadOnlyList<OrderRow> rows = script.Rows;
                int lastOrderTick = -1;
                int r = 0;
                while (r < rows.Count)
                {
                    int tick = rows[r].Tick;
                    lastOrderTick = tick;
                    int n = 0, frameOrders = 0;
                    while (r < rows.Count && rows[r].Tick == tick)
                    {
                        if (n == MergedTickPacket.MERGED_MAX_SUBBUNDLES)
                        {
                            EmitFrame(w, buf, (uint)tick, factions, counts, flat, n);
                            frames++; maxFrameOrders = Math.Max(maxFrameOrders, frameOrders);
                            n = 0; frameOrders = 0;
                        }
                        byte fac = rows[r].Faction;
                        int c = 0, baseIdx = n * TickCommandPacket.MAX_ORDERS;
                        while (r < rows.Count && rows[r].Tick == tick && rows[r].Faction == fac && c < TickCommandPacket.MAX_ORDERS)
                        {
                            flat[baseIdx + c] = rows[r].ToUnitOrder();
                            c++; r++;
                        }
                        factions[n] = (Faction)fac;
                        counts[n]   = c;
                        n++; frameOrders += c;
                    }
                    if (n > 0)
                    {
                        EmitFrame(w, buf, (uint)tick, factions, counts, flat, n);
                        frames++; maxFrameOrders = Math.Max(maxFrameOrders, frameOrders);
                    }
                }
                if (o.EndTick > lastOrderTick)
                {
                    EmitFrame(w, buf, (uint)o.EndTick, factions, counts, flat, 0); // sets ReplayPlayer.LastTick
                    frames++; emptyFrames++;
                }

                // Result trailer (ReplayRecorder.Close): incomplete recording, no winner, final tick = end tick.
                w.Write((ushort)ReplayRecorder.TRAILER_BYTES);
                w.Write(ReplayRecorder.FRAME_TRAILER);
                w.Write((byte)0);
                w.Write((uint)o.EndTick);
                w.Write((byte)0);
                w.Write((ushort)0); // frame-length EOF
            }

            // Independent decode: re-read every frame with the frozen TryRead and compare with the CSV row for row.
            int decoded = DecodeAndCompare(o.Out, script.Rows, out string decodeMsg);
            Console.WriteLine($"chmr={Path.GetFullPath(o.Out).Replace('\\', '/')} version={ReplayRecorder.VERSION} bytes={new FileInfo(o.Out).Length}");
            Console.WriteLine($"rows={script.Count} frames={frames} empty_end_frames={emptyFrames} max_orders_per_frame={maxFrameOrders} end_tick={o.EndTick}");
            Console.WriteLine($"seed=0x{o.Seed:X16} ai_mask=0x{mask:X2} scenario_hash=0x{scenarioHash:X16} ruleset_hash=0x{rulesetHash:X16} algo={algo}");
            Console.WriteLine($"pre.canonical_model=0x{pre.CanonicalModel:X16} pre.ruleset=0x{pre.Ruleset:X16} (header hashes {(pre.CanonicalModel == scenarioHash && pre.Ruleset == rulesetHash ? "equal" : "DIFFER")})");
            Console.WriteLine($"decoded={decoded}/{script.Count} {decodeMsg}");
            return decoded == script.Count && decodeMsg == "decoded_equal=yes" ? 0 : 1;
        }

        private static void EmitFrame(BinaryWriter w, byte[] buf, uint tick, Faction[] factions, int[] counts, UnitOrder[] flat, int n)
        {
            int len = MergedTickPacket.Write(buf, tick, factions, counts, flat, n);
            w.Write((ushort)len);
            w.Write(buf, 0, len);
        }

        private static int DecodeAndCompare(string path, IReadOnlyList<OrderRow> rows, out string msg)
        {
            byte[] all = File.ReadAllBytes(path);
            using var ms = new MemoryStream(all);
            using var rd = new BinaryReader(ms);
            rd.ReadUInt32(); rd.ReadUInt16();
            int pl = rd.ReadUInt16(); rd.ReadBytes(pl);
            rd.ReadBytes(8 * 3 + 4);
            int fc = rd.ReadUInt16(); rd.ReadBytes(fc); rd.ReadInt32();
            var factions = new Faction[MergedTickPacket.MERGED_MAX_SUBBUNDLES];
            var counts   = new int[MergedTickPacket.MERGED_MAX_SUBBUNDLES];
            var flat     = new UnitOrder[MergedTickPacket.MERGED_MAX_SUBBUNDLES * TickCommandPacket.MAX_ORDERS];
            int k = 0;
            while (ms.Length - ms.Position >= 2)
            {
                int len = rd.ReadUInt16();
                if (len == 0) break;
                byte[] frame = rd.ReadBytes(len);
                if (frame[0] == ReplayRecorder.FRAME_TRAILER) continue;
                if (!MergedTickPacket.TryRead(frame, len, out uint tick, factions, counts, flat, out int n))
                { msg = $"decoded_equal=no (TryRead rejected a frame after {k} rows)"; return k; }
                for (int b = 0; b < n; b++)
                    for (int i = 0; i < counts[b]; i++)
                    {
                        UnitOrder u = flat[b * TickCommandPacket.MAX_ORDERS + i];
                        if (k >= rows.Count) { msg = "decoded_equal=no (more orders than rows)"; return k; }
                        OrderRow row = rows[k];
                        if (row.Tick != (int)tick || row.Faction != (byte)factions[b] || row.UnitRef != u.UnitId
                            || row.Command != (byte)u.Command || row.XRaw != u.TargetX || row.ZRaw != u.TargetZ || row.Slot != u.Slot)
                        { msg = $"decoded_equal=no (row {k + 1}: {row} vs tick {tick} faction {(byte)factions[b]} {u.UnitId} {(byte)u.Command} {u.TargetX} {u.TargetZ} {u.Slot})"; return k; }
                        k++;
                    }
            }
            msg = k == rows.Count ? "decoded_equal=yes" : $"decoded_equal=no ({k} of {rows.Count})";
            return k;
        }

        // ── verify ───────────────────────────────────────────────────────────────

        private static int Verify(Options o)
        {
            SimSession s = SimSession.Build(o.Content, o.Scenario, o.Seed, o.Ai);
            SimulationHost host = s.Host;
            EntityWorld world = host.World;
            var rp = new ReplayPlayer(o.Chmr, world); // reseeds world.Rng from the header (ReplayPlayer.cs:237)
            if (rp.Seed != o.Seed) throw new InvalidDataException($"header seed 0x{rp.Seed:X16} != --seed 0x{o.Seed:X16}");
            if (rp.AiControlMask != s.AiPlan.Mask)
                throw new InvalidDataException($"header AI mask 0x{rp.AiControlMask:X2} != session plan 0x{s.AiPlan.Mask:X2}");
            string? gate = ReplayPlayer.ScenarioGateBlockReason(rp.ScenarioHash, CanonicalModelHash.Compute(s.Model))
                           ?? ReplayPlayer.RulesetGateBlockReason(rp.RulesetHash, RulesetHash.Compute());
            if (gate != null) throw new InvalidDataException("re-gate refused: " + gate);

            // Wired as MatchLifecycleController.TryLoadReplay wires it; the path delegates are SimSession's
            // FlowFieldBridge.ToSim equivalents (SimSession.cs:133-142).
            var steering = host.Steering;
            rp.OnRequestPath       = (id, x, z) => steering.RequestPath(world, id, new FixedVec3(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z)));
            rp.OnRequestAttackMove = (id, x, z) => steering.RequestAttackMove(world, id, new FixedVec3(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z)));
            rp.OnCancelPath        = id => steering.CancelPath(id);
            rp.Buildings    = host.BuildSys;
            rp.Items        = host.ItemSys;
            rp.Research     = host.ResearchSys;
            rp.DslEventSink = host.DslEventSink;
            rp.WinState     = host.WinState;
            rp.HeroXp       = host.HeroXp;
            host.SetAiControlPlan(AI.AiControlPlan.FromMask(rp.AiControlMask));

            var hashes = new uint[o.Ticks + 1];
            host.ChecksumInterval = 1;
            host.SetChecksumSink((tick, h) => { if (tick <= (uint)o.Ticks) hashes[tick] = h; });
            int steps = 0;
            var states = new StringBuilder();
            while (host.CurrentTick < (uint)o.Ticks)
            {
                rp.Flush(host.CurrentTick);
                host.StepOnce();
                steps++;
                uint t = host.CurrentTick;
                if (t == 300 || t == 900 || t == 1440)
                    states.Append("# state.").Append(t.ToString(CultureInfo.InvariantCulture)).Append(": ")
                          .Append(StateLine(world, host)).Append('\n');
            }

            var sb = new StringBuilder();
            sb.Append("# leg: chmr_managed_replay\n# runtime: ").Append(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription)
              .Append("\n# chmr: ").Append(Path.GetFullPath(o.Chmr).Replace('\\', '/')).Append('\n');
            for (int t = 1; t <= o.Ticks; t++) sb.Append(t.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(hashes[t].ToString("X8", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(states);
            File.WriteAllText(o.Out, sb.ToString(), new UTF8Encoding(false));
            Console.Write(states.ToString());

            var golden = new Dictionary<int, uint>();
            foreach (string line in File.ReadAllLines(o.Golden))
            {
                if (line.StartsWith("#") || line.Trim().Length == 0) continue;
                string[] p = line.Split(' ');
                golden[int.Parse(p[0], CultureInfo.InvariantCulture)] = uint.Parse(p[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            int equal = 0, first = -1;
            for (int t = 1; t <= o.Ticks; t++)
            {
                if (golden.TryGetValue(t, out uint g) && g == hashes[t]) equal++;
                else if (first < 0) first = t;
            }
            Console.WriteLine($"verify steps={steps} finished={rp.IsFinished} last_tick={rp.LastTick} ai_mask=0x{rp.AiControlMask:X2} alive_end={world.AliveCount}");
            Console.WriteLine($"managed_replay={equal}/{o.Ticks} first_divergent_tick={(first < 0 ? "none" : first.ToString(CultureInfo.InvariantCulture))} trace={Path.GetFullPath(o.Out).Replace('\\', '/')}");
            return equal == o.Ticks ? 0 : 1;
        }

        /// <summary>The A14 state line, the same fields the MainScene watcher prints: units and wide digests
        /// (<see cref="WorldDigest"/>, the wide one folds the unfolded CommandState that DW-681's frame writer moves)
        /// and the alive units per command state.</summary>
        internal static string StateLine(EntityWorld w, SimulationHost h)
        {
            int alive = 0, move = 0, stop = 0, idle = 0, amove = 0, other = 0;
            for (int i = 0; i < w.HighWaterMark; i++)
            {
                if (!w.IsAlive(i)) continue;
                alive++;
                switch (w.CommandState[i])
                {
                    case UnitCommand.Move: move++; break;
                    case UnitCommand.Stop: stop++; break;
                    case UnitCommand.Idle: idle++; break;
                    case UnitCommand.AttackMove: amove++; break;
                    default: other++; break;
                }
            }
            return string.Format(CultureInfo.InvariantCulture,
                "digest=0x{0:X16} wide=0x{1:X16} alive={2} move={3} stop={4} idle={5} attackmove={6} other={7}",
                WorldDigest.UnitsDigest(w), WorldDigest.WideDigest(w, h.Projectiles), alive, move, stop, idle, amove, other);
        }
    }
}
