#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Dsl;
using ProjectChimera.Effects;
using ProjectChimera.Sim.Tests.Golden;

namespace SpikeAot
{
    public static partial class ScenarioRunner
    {
        static ulong Fnv(IReadOnlyList<GoldenChecksumReplay.Sample> seq)
        {
            ulong h = 14695981039346656037UL;
            foreach (var s in seq) { h = (h ^ s.Tick) * 1099511628211UL; h = (h ^ s.Hash) * 1099511628211UL; }
            return h;
        }
        public static void Run(TextWriter w, string name, int ticks, Func<GoldenHarness> build, string goldenFile)
        {
            try
            {
                var seq = GoldenChecksumReplay.RunAndRecord(ticks, build: build);
                w.WriteLine($"{name} samples={seq.Count} last={seq[^1].Hash} fnv={Fnv(seq):X16}");
            }
            catch (Exception ex) { w.WriteLine($"{name} EXCEPTION {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
        }
    }

    public static class Program
    {
        const string Repo = @"D:\Projects\Project_Chimera\godot";
        const string DataDir = Repo + @"\resources\data";

        static ulong Fnv64(string s)
        {
            ulong h = 14695981039346656037UL;
            foreach (byte b in Encoding.UTF8.GetBytes(s)) { h ^= b; h *= 1099511628211UL; }
            return h;
        }

        public static int Main(string[] args)
        {
            var w = Console.Out;
            w.WriteLine("runtime: " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
                + " reflectionEnabledSTJ=" + System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault
                + " dynamicCode=" + System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported);
            ScenarioRunner.RunAll(w);
            try { Content(w); } catch (Exception ex) { w.WriteLine("CONTENT EXCEPTION " + ex); }
            try { RealMatches(w); } catch (Exception ex) { w.WriteLine("MATCH EXCEPTION " + ex); }
            try { Probes(w); } catch (Exception ex) { w.WriteLine("PROBE EXCEPTION " + ex); }
            try { Roundtrips(w); } catch (Exception ex) { w.WriteLine("ROUNDTRIP EXCEPTION " + ex); }
            return 0;
        }

        static void Content(TextWriter w)
        {
            // every loader, through the migrated call sites (no reflection allowed)
            var skipped = new List<string>();
            var abilities = AbilityRegistry.LoadFromDirectory(Path.Combine(DataDir, "abilities"), s => skipped.Add(s));
            var items = ItemRegistry.LoadFromDirectory(Path.Combine(DataDir, "items"), s => skipped.Add(s));
            var behaviors = BehaviorRegistry.LoadFromDirectory(Path.Combine(DataDir, "behaviors"), s => skipped.Add(s));
            var table = DamageTable.Load(Path.Combine(DataDir, "damage_table.json"));
            var factions = new List<FactionDefinition>();
            foreach (string f in Directory.GetFiles(Path.Combine(DataDir, "factions"), "*_faction.json").OrderBy(x => x, StringComparer.Ordinal))
                factions.Add(FactionDefinition.LoadFromFile(f));
            w.WriteLine($"registries: abilities={abilities.Count} items={items.Count} behaviors={behaviors.Count} factions={factions.Count} skipped={skipped.Count}");
            foreach (string s in skipped) w.WriteLine("  skipped: " + s);
            w.WriteLine($"ContentHash(all)=0x{ContentHash.Compute(factions, abilities, items, table):X16}");
            foreach (string f in Directory.GetFiles(Path.Combine(DataDir, "abilities"), "*.json").OrderBy(x => x, StringComparer.Ordinal))
            {
                var r = AbilityLoader.LoadFromFile(f);
                w.WriteLine($"ability {Path.GetFileName(f)} ok={r.Ok}");
            }
            // invalid input paths must still fail closed with the same message shape
            w.WriteLine("ability bad enum: " + AbilityLoader.Load("{\"id\":\"x\",\"effect\":{\"kind\":\"damage\",\"amount\":1,\"damage_type\":3}}", "t").Error);
            w.WriteLine("ability unknown key: " + AbilityLoader.Load("{\"id\":\"x\",\"cooldwn\":1}", "t").Error);
            w.WriteLine("item load: " + ItemLoader.Load("{\"id\":\"i\",\"display_name\":\"I\",\"max_health_delta\":5}", "t").Ok);
            w.WriteLine("item writer: " + Fnv64(ItemWriter.Serialize(new ItemDefinition { Id = "ring", DisplayName = "Ring", MaxHealthDelta = Fixed.FromInt(50) })).ToString("X16"));
            try { DamageTable.FromJson("{\"multipliers\":{\"Zed\":{}}}"); } catch (Exception ex) { w.WriteLine("damage table bad key: " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0]); }
        }

        static void RealMatches(TextWriter w)
        {
            string facDir = Path.Combine(DataDir, "factions");
            FactionDefinition alpha = FactionDefinition.LoadFromFile(Path.Combine(facDir, "alpha_faction.json"));
            FactionDefinition beta = FactionDefinition.LoadFromFile(Path.Combine(facDir, "beta_faction.json"));
            foreach (string f in Directory.GetFiles(Path.Combine(DataDir, "scenarios"), "*.json").OrderBy(x => x, StringComparer.Ordinal))
            {
                string name = Path.GetFileName(f);
                try
                {
                    ScenarioData? m = ScenarioSerializer.LoadFromFile(f, out string? err);
                    if (m is null) { w.WriteLine($"match {name} LOAD-FAIL {err}"); continue; }
                    string reser = ScenarioSerializer.Serialize(m);
                    var slotDefs = new FactionDefinition?[8];
                    foreach (var s in m.PlayerSlots)
                        if (s.Slot >= 0 && s.Slot < slotDefs.Length)
                            slotDefs[s.Slot] = s.FactionJson.Contains("beta") ? beta : alpha;
                    var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(Math.Max(2, m.PlayerSlots.Length)),
                        slotDefs[0] ?? alpha, slotDefs.Length > 1 && slotDefs[1] != null ? slotDefs[1]! : beta);
                    host.ChecksumInterval = 1;
                    var seq = new List<GoldenChecksumReplay.Sample>();
                    host.SetChecksumSink((t, h) => seq.Add(new GoldenChecksumReplay.Sample(t, h)));
                    var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);
                    var vr = new ScenarioValidator().Validate(m);
                    if (!vr.Ok) { w.WriteLine($"match {name} canonical=0x{CanonicalModelHash.Compute(m):X16} reserializeFnv={ScenarioSerializer.ComputeHash(Encoding.UTF8.GetBytes(reser))} VALIDATION: {vr.Error}"); continue; }
                    applier.Apply(vr.Value);
                    for (int i = 0; i < 200; i++) host.StepOnce();
                    ulong h = 14695981039346656037UL;
                    foreach (var s in seq) { h = (h ^ s.Tick) * 1099511628211UL; h = (h ^ s.Hash) * 1099511628211UL; }
                    w.WriteLine($"match {name} canonical=0x{CanonicalModelHash.Compute(m):X16} reserializeFnv={ScenarioSerializer.ComputeHash(Encoding.UTF8.GetBytes(reser))} samples={seq.Count} fnv={h:X16}");
                }
                catch (Exception ex) { w.WriteLine($"match {name} EXCEPTION {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
            }
        }


        static void Probes(TextWriter w)
        {
            var rx = new System.Text.RegularExpressions.Regex(@"\A[a-z0-9_-]+\z", System.Text.RegularExpressions.RegexOptions.Compiled);
            w.WriteLine("probe regex compiled-option: " + rx.IsMatch("abc_1") + "/" + rx.IsMatch("A b"));
            w.WriteLine("probe regex static replace: " + System.Text.RegularExpressions.Regex.Replace("a---b--c", "-+", "-"));
            string zp = Path.Combine(Path.GetTempPath(), "spikeaot_probe_" + Environment.ProcessId + ".zip");
            using (var z = System.IO.Compression.ZipFile.Open(zp, System.IO.Compression.ZipArchiveMode.Create)) { var e = z.CreateEntry("a.txt"); using var sw = new StreamWriter(e.Open()); sw.Write("hello"); }
            using (var z = System.IO.Compression.ZipFile.OpenRead(zp)) { w.WriteLine("probe zip: " + z.Entries.Count + " " + z.GetEntry("a.txt")!.Length); }
            File.Delete(zp);
            using var http = new System.Net.Http.HttpClient();
            w.WriteLine("probe HttpClient ctor ok timeout=" + http.Timeout.TotalSeconds);
            w.WriteLine("probe DateTime-free: ok; env var: " + (Environment.GetEnvironmentVariable("NOPE_X") ?? "null"));
            w.WriteLine("probe stackalloc+Span: " + SpanProbe());
            w.WriteLine("probe MixUnknownEffect-equivalent reflection: fields=" + typeof(DamageEffect).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Length);
            w.WriteLine("probe typeof(UnitTag).GetEnumNames=" + string.Join("|", Enum.GetNames(typeof(UnitTag))));
        }
        static int SpanProbe() { Span<int> s = stackalloc int[4]; s[0] = 3; return s[0] + s.Length; }

        static void Roundtrips(TextWriter w)
        {
            // THE golden that pins serializer bytes
            string json = ScenarioSerializer.Serialize(ProjectChimera.Core.MapGen.ProceduralMapGenerator.Generate(0xC0FFEEUL));
            uint hash = ScenarioSerializer.ComputeHash(Encoding.UTF8.GetBytes(json));
            w.WriteLine($"ProceduralMapGenerator golden hash={hash} expected=3387691366 match={hash == 3387691366u}");
            // trigger graph canonical json
            var flat = new[]
            {
                new TriggerDefinition { Name = "A", Priority = 2, Events = new[] { new TriggerEvent { Type = "match_start" } }, Conditions = new[] { new TriggerCondition { Type = "always" } },
                    Actions = new[] { new TriggerAction { Type = "spawn_unit", UnitId = "grunt", X = Fixed.FromFloat(1.5f), Z = Fixed.FromFloat(2.5f), Count = 3 }, new TriggerAction { Type = "display_message", Text = "hi", Duration = Fixed.FromFloat(2.25f) } } },
                new TriggerDefinition { Name = "B", Events = new[] { new TriggerEvent { Type = "unit_dies", Faction = 1 } }, Actions = new[] { new TriggerAction { Type = "victory", Faction = 0 } } },
            };
            TriggerGraph g = TriggerGraph.FromFlat(flat);
            g.Nodes.Add(new EffectActionNode { Id = 100, Effect = new SequenceEffect(new DamageEffect(Fixed.FromInt(10), DamageType.Normal), new DamageEffect(Fixed.FromFloat(2.5f), DamageType.Magic, UnitTag.Organic)) });
            string gj = g.ToCanonicalJson();
            TriggerGraph back = TriggerGraph.FromJson(gj);
            w.WriteLine($"graph canonical len={gj.Length} fnv64={Fnv64(gj):X16} roundtripEqual={back.ToCanonicalJson() == gj}");
            // faction writer (JsonNode DOM patching + POCO serialization with WhenWritingNull options)
            string facText = File.ReadAllText(Path.Combine(DataDir, "factions", "alpha_faction.json"));
            FactionDefinition fd = FactionDefinition.LoadFromFile(Path.Combine(DataDir, "factions", "alpha_faction.json"));
            UnitDefinition u0 = fd.Units[0];
            w.WriteLine("FactionWriter.SerializeUnitClean fnv64=" + Fnv64(FactionWriter.SerializeUnitClean(u0)).ToString("X16"));
            var clone = FactionDefinerWizardCoreProbe.Clone(u0);
            w.WriteLine("clone via serialize/deserialize id=" + clone?.Id);
            // LLM request bodies are internal; exercise through AotJson default options
            w.WriteLine("AotJson default serialize CombatFeedbackProfile: " + JsonShim.Serialize(new CombatFeedbackProfile { ImpactSoundId = "x.ogg", HitFreezeFrames = 2 }));
            // profile + settings + manifest
            var prof = new PlayerProfile { ProfileId = "h#1", HeroDefId = "h", FactionId = "alpha", DisplayName = "Hero", Inventory = new List<ProfileInventoryItem> { new("potion", 2, 0) }, Values = new List<ProfileAttributeValue> { new("hero.level", 3) } };
            string tmp = Path.Combine(Path.GetTempPath(), "spikeaot_profiles_" + Environment.ProcessId);
            var src = new LocalProfileSource(tmp);
            src.Save(prof);
            w.WriteLine("profile roundtrip: " + src.LoadAll().Count + " bytes-fnv=" + Fnv64(File.ReadAllText(Path.Combine(tmp, "profiles.json"))).ToString("X16"));
            try { Directory.Delete(tmp, true); } catch { }
            w.WriteLine("settings: " + Fnv64(JsonShim.Serialize(new SettingsData(), SettingsJson.Options)).ToString("X16"));
        }
    }

    public static class FactionDefinerWizardCoreProbe
    {
        public static T? Clone<T>(T source) where T : class
        {
            string j = JsonShim.Serialize(source, FactionDefinition.JsonOptions);
            return JsonShim.Deserialize<T>(j, FactionDefinition.JsonOptions);
        }
    }
}

namespace SpikeAot
{
    public static class JsonShim
    {
#if ORIG
        public static string Serialize<T>(T v, System.Text.Json.JsonSerializerOptions o) => System.Text.Json.JsonSerializer.Serialize(v, o);
        public static string Serialize<T>(T v) => System.Text.Json.JsonSerializer.Serialize(v);
        public static T? Deserialize<T>(string j, System.Text.Json.JsonSerializerOptions o) => System.Text.Json.JsonSerializer.Deserialize<T>(j, o);
#else
        public static string Serialize<T>(T v, System.Text.Json.JsonSerializerOptions o) => ProjectChimera.Core.Definitions.AotJson.Serialize(v, o);
        public static string Serialize<T>(T v) => ProjectChimera.Core.Definitions.AotJson.Serialize(v);
        public static T? Deserialize<T>(string j, System.Text.Json.JsonSerializerOptions o) => ProjectChimera.Core.Definitions.AotJson.Deserialize<T>(j, o);
#endif
    }
}
