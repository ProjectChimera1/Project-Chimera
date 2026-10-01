#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using ProjectChimera.Effects;
using ProjectChimera.AI;

namespace SpikeNs
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0] : "walk";
            if (mode == "norefl") { AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false); NoRefl.Run(); return 0; }
            if (mode == "walk")
            {
                var roots = new (string, Type)[]
                {
                    ("UnitDefinition", typeof(UnitDefinition)), ("BuildingDefinition", typeof(BuildingDefinition)),
                    ("FactionDefinition", typeof(FactionDefinition)), ("ResearchDefinition", typeof(ResearchDefinition)),
                    ("AbilityDefinition", typeof(AbilityDefinition)), ("ItemDefinition", typeof(ItemDefinition)),
                    ("TriggerDefinition", typeof(TriggerDefinition)), ("ScenarioData", typeof(ScenarioData)),
                    ("BalanceReport", typeof(BalanceReport)), ("BehaviorDefinition", typeof(BehaviorDefinition)),
                    ("ContentPackageManifest", typeof(ContentPackageManifest)), ("PlayerProfile", typeof(PlayerProfile)),
                    ("SettingsData", typeof(SettingsData)), ("CombatFeedbackProfile", typeof(CombatFeedbackProfile)),
                    ("HeroDefinition", typeof(HeroDefinition)), ("VeterancyDefinition", typeof(VeterancyDefinition)),
                    ("AttributeModelDefinition", typeof(AttributeModelDefinition)), ("CustomUiTree", typeof(CustomUiTree)),
                    ("ProofOfPlayToken", typeof(ProofOfPlayToken)),
                };
                foreach (Type wt in typeof(WidgetBase).Assembly.GetTypes().Where(t => t.BaseType == typeof(WidgetBase)))
                {
                    var rw = Walker.Walk(wt);
                    Console.WriteLine($"WidgetDerived {wt.Name}: poco={rw.Poco.Count} enums=[{string.Join(",", rw.Enums.Select(e => e.Substring(e.LastIndexOf('.') + 1)))}]");
                }
                var rb = Walker.Walk(typeof(WidgetBase)); Console.WriteLine("WidgetBase enums=[" + string.Join(",", rb.Enums.Select(e => e.Substring(e.LastIndexOf('.') + 1))) + "] poco=" + rb.Poco.Count);
                var allP = new SortedSet<string>(); var allE = new SortedSet<string>();
                foreach (var (n, t) in roots)
                {
                    var r = Walker.Walk(t);
                    Console.WriteLine($"ROOT {n}: poco={r.Poco.Count} enums={r.Enums.Count} convLeaves=[{string.Join(",", r.ConverterLeafHits)}] nonPublic=[{string.Join(",", r.NonPublic)}]");
                    Console.WriteLine($"   enums: {string.Join(", ", r.Enums.Select(e => e.Substring(e.LastIndexOf('.') + 1)))}");
                    Console.WriteLine($"   setters: {string.Join("; ", r.NotesSetters)}");
                    foreach (var p in r.Poco) allP.Add(p); foreach (var e in r.Enums) allE.Add(e);
                }
                Console.WriteLine($"UNION poco={allP.Count} enums={allE.Count}");
                Console.WriteLine("UNION POCO LIST:\n  " + string.Join("\n  ", allP));
                Console.WriteLine("UNION ENUM LIST:\n  " + string.Join("\n  ", allE));
                return 0;
            }
            if (mode == "edge2") { Edge2.Run(); Diff.Report(); return 0; }
            if (mode == "controls") { Controls.Run(); return 0; }
            if (mode == "diff") { DiffRun.Run(); return Diff.Fail == 0 ? 0 : 1; }
            return 0;
        }
    }
}
