#nullable enable
// Scratch prototype for the R2 report (NOT part of the repo). Runs a scripted 1,000-unit match through the REAL sim
// source set, composed exactly like MainScene.BuildHeadlessServerSimHost / ServerBootstrap.Build plus the Godot-edge
// pieces the headless path skips (flow-field obstacle init, footprint source, path-request delegates).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using ProjectChimera.AI;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Navigation;
using ProjectChimera.UI;

namespace Proto
{
    sealed class ConsoleLog : ILogSink
    {
        public bool Verbose;
        public void Info(string m)  { if (Verbose) Console.Error.WriteLine("[I] " + m); }
        public void Warn(string m)  { Console.Error.WriteLine("[W] " + m); }
        public void Error(string m) { Console.Error.WriteLine("[E] " + m); }
    }

    sealed class Rig
    {
        public SimulationHost Host = null!;
        public FactionDefinition?[] SlotDefs = null!;
        public ILogSink Log = null!;
        public Action<int, float, float> OnPath = null!, OnAttackMove = null!;
        public Action<int> OnCancel = null!;
    }

    static class Program
    {
        // Scenario knobs (the report's proposal).
        const ulong MatchSeed = 0xC0FFEE1234567890UL;

        static string Res(string godotDir, string resPath) =>
            Path.Combine(godotDir, resPath.Substring("res://".Length).Replace('/', Path.DirectorySeparatorChar));


        // ── "Lite" content loading: JsonDocument only (AOT-safe, no reflection-based STJ), scalar fields only, no abilities.
        // Used purely to prove JIT-vs-NativeAOT determinism of the TICK while the real loaders still need source-gen.
        static bool Lite => Environment.GetEnvironmentVariable("CHIMERA_LITE") == "1";
        static float Fl(JsonElement e, string n, float d) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : d;
        static string St(JsonElement e, string n, string d) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : d;
        static FactionDefinition LiteFaction(string path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var f = new FactionDefinition { Id = St(root, "id", ""), DisplayName = St(root, "display_name", "") };
            foreach (var u in root.GetProperty("units").EnumerateArray())
            {
                var d = new UnitDefinition
                {
                    Id = St(u, "id", ""), DisplayName = St(u, "display_name", ""), Category = St(u, "category", "Melee"),
                    Hp = Fl(u, "hp", 100f), Speed = Fl(u, "speed", 4f), AttackDamage = Fl(u, "attack_damage", 10f),
                    AttackRange = Fl(u, "attack_range", 5f), AttackSpeed = Fl(u, "attack_speed", 1f),
                    DamageType = St(u, "damage_type", "Normal"), ArmorType = St(u, "armor_type", "Unarmored"),
                    Armor = Fl(u, "armor", 0f), VisionRange = Fl(u, "vision_range", 8f), SplashRadius = Fl(u, "splash_radius", 0f),
                    ProjectileSpeed = Fl(u, "projectile_speed", 18f), CollisionRadius = Fl(u, "collision_radius", 1.0f),
                    SeparationPriority = St(u, "separation_priority", "Normal"),
                };
                if (u.TryGetProperty("delivery", out var dv) && dv.ValueKind == JsonValueKind.String) d.Delivery = dv.GetString();
                f.Units.Add(d);
            }
            return f;
        }
        static ScenarioData LiteScenario(string path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            var slots = new List<ScenarioPlayerSlot>();
            foreach (var s in r.GetProperty("player_slots").EnumerateArray())
                slots.Add(new ScenarioPlayerSlot { Slot = (int)Fl(s, "slot", 0), FactionJson = "", StartOre = Fl(s, "start_ore", 0), StartCrystal = Fl(s, "start_crystal", 0), BaseX = Fl(s, "base_x", 0), BaseZ = Fl(s, "base_z", 0) });
            var nodes = new List<ScenarioResourceNode>();
            foreach (var n in r.GetProperty("resource_nodes").EnumerateArray())
                nodes.Add(new ScenarioResourceNode { X = Fl(n, "x", 0), Z = Fl(n, "z", 0), Supply = Fl(n, "supply", 600), Rate = Fl(n, "rate", 5), MaxGatherers = (int)Fl(n, "max_gatherers", 4) });
            var bs = new List<ScenarioBuilding>();
            foreach (var b in r.GetProperty("buildings").EnumerateArray())
                bs.Add(new ScenarioBuilding { Type = St(b, "type", "CommandCenter"), Slot = (int)Fl(b, "slot", 0), X = Fl(b, "x", 0), Z = Fl(b, "z", 0), PreBuilt = true });
            var us = new List<ScenarioUnit>();
            foreach (var u in r.GetProperty("units").EnumerateArray())
                us.Add(new ScenarioUnit { UnitId = St(u, "unit_id", ""), Slot = (int)Fl(u, "slot", 0), X = Fl(u, "x", 0), Z = Fl(u, "z", 0) });
            return new ScenarioData
            {
                Id = St(r, "id", "x"), DisplayName = St(r, "display_name", "x"), TerrainRef = "", MapBounds = Fl(r, "map_bounds", 120),
                WinCondition = WinCondition.DestroyAllBuildings, PlayerSlots = slots.ToArray(), ResourceNodes = nodes.ToArray(),
                Buildings = bs.ToArray(), Units = us.ToArray(),
            };
        }

        static Rig Build(string godotDir, string scenarioFile, bool aiOn, bool flowFields)
        {
            CultureInfoPin();
            var log = new ConsoleLog();
            string data = Path.Combine(godotDir, "resources", "data");

            // Faction defaults (mirror MainScene.BuildHeadlessServerSimHost).
            var p1Def = Lite ? LiteFaction(Path.Combine(data, "factions", "alpha_faction.json")) : FactionDefinition.LoadFromFile(Path.Combine(data, "factions", "alpha_faction.json"));
            var p2Def = Lite ? LiteFaction(Path.Combine(data, "factions", "beta_faction.json")) : FactionDefinition.LoadFromFile(Path.Combine(data, "factions", "beta_faction.json"));
            var factions = new FactionRegistry(2);
            var slotDefs = factions.SlotDefinitions;
            slotDefs[(int)Faction.Player1] = p1Def;
            slotDefs[(int)Faction.Player2] = p2Def;

            var damageTable = Lite ? DamageTable.Default : DamageTable.Load(Path.Combine(data, "damage_table.json"));
            var registry = Lite ? AbilityRegistry.Empty : AbilityRegistry.LoadFromDirectory(Path.Combine(data, "abilities"), n => Console.Error.WriteLine("[skipped ability] " + n));

            ScenarioData? model; string? err = null;
            if (Lite) model = LiteScenario(scenarioFile); else model = ScenarioSerializer.LoadFromFile(scenarioFile, out err);
            if (model == null) throw new Exception("scenario parse failed: " + err);

            foreach (var slot in model.PlayerSlots ?? Array.Empty<ScenarioPlayerSlot>())
            {
                if (string.IsNullOrEmpty(slot.FactionJson)) continue;
                var f = FactionRegistry.ToFaction(slot.Slot);
                string abs = Res(godotDir, slot.FactionJson);
                if (File.Exists(abs)) slotDefs[(int)f] = FactionDefinition.LoadFromFile(abs);
            }

            int n = ProjectChimera.Multiplayer.PlayerCountPolicy.SimActivePlayers(model.PlayerSlots?.Length ?? 0);
            SimulationHost? host = ServerBootstrap.Build(model, slotDefs, damageTable, log, n, registry);
            if (host == null) throw new Exception("ServerBootstrap.Build returned null (scenario rejected)");

            Console.WriteLine($"HASHES startState=0x{StartStateHash.Compute(model, host.Heroes):X16} canonicalModel=0x{CanonicalModelHash.Compute(model):X16} content=0x{ContentHash.Compute(new[] { p1Def, p2Def }, registry, ItemRegistry.Empty, damageTable):X16} ruleset=0x{RulesetHash.Compute():X16} agreement=0x{MatchAgreementHash.Compute(4, model, host.Heroes, new[] { p1Def, p2Def }, registry, ItemRegistry.Empty, damageTable, aiOn ? AiControlPlan.OfflineDefault : AiControlPlan.None):X16}");
            // The Godot-edge pieces ServerBootstrap does NOT do (see report section 7):
            host.World.Rng.Seed(MatchSeed);
            if (!aiOn) host.SetAiControlPlan(AiControlPlan.None);
            var rig = new Rig { Host = host, SlotDefs = slotDefs, Log = log };

            if (flowFields)
            {
                // NavigationPhase: DW-570 footprint source; FlowFieldInitPhase: RebuildObstacles + baseline.
                host.FlowFields.SetBuildingFootprintSource(BuildingNavFootprint.ObstacleExtentSource(host.Buildings, slot =>
                {
                    string id = host.Buildings.DefinitionId[slot];
                    if (string.IsNullOrEmpty(id)) return null;
                    int owner = (int)host.Buildings.FactionOf[slot];
                    if (owner >= 0 && owner < slotDefs.Length) { var d = slotDefs[owner]?.GetBuilding(id); if (d != null) return d; }
                    for (int i = 0; i < slotDefs.Length; i++) { var d = slotDefs[i]?.GetBuilding(id); if (d != null) return d; }
                    return null;
                }));
                host.FlowFields.RebuildObstacles(host.Buildings);
                host.Steering.SyncBuildingBaseline();
                var w = host.World; var st = host.Steering;
                rig.OnPath       = (id, x, z) => st.RequestPath(w, id, new FixedVec3(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z)));
                rig.OnAttackMove = (id, x, z) => st.RequestAttackMove(w, id, new FixedVec3(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z)));
                rig.OnCancel     = id => st.CancelPath(id);
            }
            return rig;
        }

        static void CultureInfoPin()
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        }

        static int OrdersIssued; static readonly SortedDictionary<int,int> PerTick = new(); static int CurTick;
        static void Issue(Rig r, int id, UnitCommand cmd, Fixed x, Fixed z, bool queued = false)
        {
            var w = r.Host.World;
            if (!w.IsAlive(id)) return;
            OrdersIssued++; PerTick[CurTick] = PerTick.GetValueOrDefault(CurTick) + 1;
            byte cb = (byte)cmd; if (queued) cb |= UnitOrderFlags.Queued;
            var o = new ProjectChimera.Multiplayer.UnitOrder(w.PackRef(id), (UnitCommand)cb, x, z);
            ProjectChimera.Multiplayer.OrderApplier.Apply(w, in o, w.FactionOf[id], r.OnPath, r.OnAttackMove, r.OnCancel,
                r.Host.BuildSys, r.Host.CombatEvents, r.Host.ItemSys, r.Host.ResearchSys, r.Host.DslEventSink, r.Host.WinState,
                r.Log, r.Host.HeroXp);
        }

        static string TypeOf(Rig r, int id)
        {
            var w = r.Host.World; var def = r.SlotDefs[(int)w.FactionOf[id]];
            int mt = w.MeshType[id];
            return def != null && mt >= 0 && mt < def.Units.Count ? def.Units[mt].Id : "?";
        }

        // The scripted orders (tick = value of CurrentTick when the order is applied, i.e. effective on tick+1).
        static void Script(Rig r, int tick, List<int> p1, List<int> p2)
        {
            var w = r.Host.World;
            Fixed F(int v) => Fixed.FromInt(v);
            switch (tick)
            {
                case 0:   // everyone marches: AttackMove at the enemy line, z fanned over 13 goal cells
                    for (int k = 0; k < p1.Count; k++) Issue(r, p1[k], UnitCommand.AttackMove, F(60), F(((k % 13) - 6) * 2));
                    for (int k = 0; k < p2.Count; k++) Issue(r, p2[k], UnitCommand.AttackMove, F(-60), F(((k % 13) - 6) * 2));
                    break;
                case 150: // flank + air
                    foreach (int id in p1) { string t = TypeOf(r, id); if (t == "scout") Issue(r, id, UnitCommand.Move, F(0), F(-90)); if (t == "griffin") Issue(r, id, UnitCommand.AttackMove, F(60), F(-30)); }
                    foreach (int id in p2) { if (TypeOf(r, id) == "wyvern") Issue(r, id, UnitCommand.AttackMove, F(-60), F(30)); }
                    break;
                case 300: // queued follow-up on the scouts (Shift-queue: Move now, AttackMove after)
                    foreach (int id in p1) if (TypeOf(r, id) == "scout") { Issue(r, id, UnitCommand.Move, F(40), F(-80)); Issue(r, id, UnitCommand.AttackMove, F(95), F(-5), queued: true); }
                    break;
                case 450: // siege forward
                    foreach (int id in p1) if (TypeOf(r, id) == "siege_engine") Issue(r, id, UnitCommand.AttackMove, F(50), F(0));
                    foreach (int id in p2) if (TypeOf(r, id) == "war_machine")  Issue(r, id, UnitCommand.AttackMove, F(-50), F(0));
                    break;
                case 600: // re-target survivors at the enemy base side
                    foreach (int id in p1) { string t = TypeOf(r, id); if (t != "worker") Issue(r, id, UnitCommand.AttackMove, F(85), F(0)); }
                    foreach (int id in p2) { string t = TypeOf(r, id); if (t != "forgehand") Issue(r, id, UnitCommand.AttackMove, F(-85), F(0)); }
                    break;
                case 900: // hold / stop subsets
                    foreach (int id in p1) if (TypeOf(r, id) == "heavy_infantry") Issue(r, id, UnitCommand.HoldPosition, F(0), F(0));
                    foreach (int id in p2) if (TypeOf(r, id) == "bulwark") Issue(r, id, UnitCommand.Stop, F(0), F(0));
                    break;
                case 1200: // force-attack: each P1 archer targets the lowest-id live P2 unit of the same rank
                {
                    var targets = new List<int>();
                    foreach (int id in p2) if (w.IsAlive(id)) targets.Add(id);
                    int k = 0;
                    foreach (int id in p1) if (TypeOf(r, id) == "archer" && targets.Count > 0)
                        { int tgt = targets[k++ % targets.Count]; Issue(r, id, UnitCommand.AttackTarget, Fixed.FromRaw(w.PackRef(tgt)), Fixed.Zero); }
                    break;
                }
                case 1800: // final push
                    foreach (int id in p1) { string t = TypeOf(r, id); if (t != "worker") Issue(r, id, UnitCommand.AttackMove, F(0), F(0)); }
                    foreach (int id in p2) { string t = TypeOf(r, id); if (t != "forgehand") Issue(r, id, UnitCommand.AttackMove, F(0), F(0)); }
                    break;
            }
        }

        sealed class Result
        {
            public List<(uint tick, uint hash)> Samples = new();
            public double MsPerTickMedian, MsTotal;
            public string Summary = "";
            public List<string> Timeline = new();
        }

        static Result Run(string godotDir, string scenarioFile, int ticks, bool aiOn, bool flowFields, int sampleEvery, bool timeline)
        {
            Rig r = Build(godotDir, scenarioFile, aiOn, flowFields);
            var host = r.Host; var w = host.World;
            var res = new Result();
            host.ChecksumInterval = 0; // we fold explicitly so sampling cadence is ours
            // Combat-capable unit lists per faction, ascending id.
            var p1 = new List<int>(); var p2 = new List<int>();
            for (int id = 0; id < w.HighWaterMark; id++)
            {
                if (!w.IsAlive(id)) continue;
                string t = TypeOf(r, id);
                if (t == "worker" || t == "forgehand") continue;
                (w.FactionOf[id] == Faction.Player1 ? p1 : p2).Add(id);
            }
            Console.Error.WriteLine($"[rig] entities={w.AliveCount} p1combat={p1.Count} p2combat={p2.Count} buildings={host.Buildings.Count}");

            var sw = new Stopwatch(); var per = new List<double>(ticks); int firstVerdict = -1;
            for (int t = 0; t < ticks; t++)
            {
                CurTick = t; Script(r, t, p1, p2);
                sw.Restart();
                host.StepOnce();
                sw.Stop(); per.Add(sw.Elapsed.TotalMilliseconds);
                uint tick = host.CurrentTick;
                if (firstVerdict < 0 && (host.WinState.Verdict[1] != 0 || host.WinState.Verdict[2] != 0)) { firstVerdict = (int)tick; res.Timeline.Add($"FIRST-VERDICT at tick {tick}: P1={host.WinState.Verdict[1]} P2={host.WinState.Verdict[2]}"); }
                bool sample = tick <= 10 || tick % (uint)sampleEvery == 0 || t == ticks - 1;
                if (sample)
                {
                    uint h = SimChecksum.Compute(w, host.Buildings, host.Resources, new FactionRegistry(2), host.Modifiers, host.Heroes,
                        host.Items, host.Nodes, host.Research, host.Vars, host.LoopState, host.DslEvents, host.WinState, host.Alliances, host.TriggerEnabled);
                    res.Samples.Add((tick, h));
                }
                if (timeline && (tick % 300 == 0))
                {
                    int a1 = 0, a2 = 0, mv = 0, at = 0;
                    for (int id = 0; id < w.HighWaterMark; id++) { if (!w.IsAlive(id)) continue; if (w.FactionOf[id] == Faction.Player1) a1++; else a2++; if ((w.Flags[id] & EntityFlags.Moving) != 0) mv++; if ((w.Flags[id] & EntityFlags.Attacking) != 0) at++; }
                    res.Timeline.Add($"tick {tick,5}: alive P1={a1,4} P2={a2,4} moving={mv,4} attacking={at,4} verdictP1={host.WinState.Verdict[1]} verdictP2={host.WinState.Verdict[2]} rngState=0x{w.Rng.State:X16} ore1={host.Resources.Ore[1].Raw} ore2={host.Resources.Ore[2].Raw}");
                }
            }
            per.Sort();
            res.MsPerTickMedian = per[per.Count / 2];
            res.MsTotal = 0; foreach (var d in per) res.MsTotal += d;
            res.Summary = $"firstVerdict={firstVerdict} cachedFields={host.FlowFields.CachedFieldCount} alive={w.AliveCount} bldgs={host.Buildings.Count} p50={per[per.Count / 2]:F3}ms p95={per[(int)(per.Count * 0.95)]:F3}ms max={per[per.Count - 1]:F3}ms total={res.MsTotal:F0}ms";
            return res;
        }

        static int Main(string[] a)
        {
            string godotDir = a[0]; string scenario = a[1]; int ticks = int.Parse(a[2]);
            bool aiOn = a.Length > 3 && a[3] == "ai";
            bool flow = !(a.Length > 4 && a[4] == "noflow");
            int every = a.Length > 5 ? int.Parse(a[5]) : 30;
            string? dump = a.Length > 6 ? a[6] : null;

            var r1 = Run(godotDir, scenario, ticks, aiOn, flow, every, timeline: true);
            Console.WriteLine($"RUN1 {r1.Summary}"); Console.WriteLine($"ORDERS-RUN1 total={OrdersIssued} perTick={string.Join(',', PerTick)}");
            foreach (var l in r1.Timeline) Console.WriteLine(l);
            var r2 = Run(godotDir, scenario, ticks, aiOn, flow, every, timeline: false);
            Console.WriteLine($"RUN2 {r2.Summary}");
            bool same = r1.Samples.Count == r2.Samples.Count; int diverge = -1;
            for (int i = 0; same && i < r1.Samples.Count; i++) if (r1.Samples[i] != r2.Samples[i]) { same = false; diverge = i; }
            Console.WriteLine($"IN-PROCESS-REPEAT identical={same} samples={r1.Samples.Count}" + (same ? "" : $" firstDivergeIdx={diverge}"));
            if (dump != null)
            {
                var sb = new StringBuilder();
                foreach (var (t, h) in r1.Samples) sb.Append(t.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(h.ToString("X8", CultureInfo.InvariantCulture)).Append('\n');
                File.WriteAllText(dump, sb.ToString());
            }
            uint fold = 2166136261u; foreach (var (t, h) in r1.Samples) { fold = (fold ^ h) * 16777619u; fold = (fold ^ t) * 16777619u; }
            Console.WriteLine($"SAMPLE-FOLD 0x{fold:X8}");
            return same ? 0 : 2;
        }
    }
}
