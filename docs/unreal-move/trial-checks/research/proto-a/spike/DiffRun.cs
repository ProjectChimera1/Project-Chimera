#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using ProjectChimera.Effects;
using ProjectChimera.AI;

namespace SpikeNs
{
    public static class DiffRun
    {
        const string DataDir = @"D:\Projects\Project_Chimera\godot\resources\data";

        public static void Run()
        {
            var strictSg = Sg.Strict(); var scenSg = Sg.Scenario(); var lenSg = Sg.Lenient(); var modelSg = Sg.ModelOutput(); var itemWSg = Sg.ItemWriter();

            foreach (string f in Directory.GetFiles(Path.Combine(DataDir, "factions"), "*.json"))
            {
                string json = File.ReadAllText(f); string n = "faction:" + Path.GetFileName(f);
                Diff.Check(n, () => Dump.Of(JsonSerializer.Deserialize<FactionDefinition>(json, FactionDefinition.JsonOptions)),
                              () => Dump.Of(Helpers.DeSg<FactionDefinition>(json, lenSg)));
                // re-serialize with lenient options (the Wizard clone path) and compare bytes
                Diff.Check(n + ":reser", () => JsonSerializer.Serialize(JsonSerializer.Deserialize<FactionDefinition>(json, FactionDefinition.JsonOptions), FactionDefinition.JsonOptions),
                                          () => Helpers.SerSg(Helpers.DeSg<FactionDefinition>(json, lenSg), lenSg));
            }
            foreach (string f in Directory.GetFiles(Path.Combine(DataDir, "abilities"), "*.json"))
            {
                string json = File.ReadAllText(f); string n = "ability:" + Path.GetFileName(f);
                Diff.Check(n, () => Dump.Of(JsonSerializer.Deserialize<AbilityDefinition>(json, ContentJson.Options)),
                              () => Dump.Of(Helpers.DeSg<AbilityDefinition>(json, strictSg)));
                Diff.Check(n + ":model", () => Dump.Of(JsonSerializer.Deserialize<AbilityDefinition>(json, ContentJson.ModelOutputOptions)),
                              () => Dump.Of(Helpers.DeSg<AbilityDefinition>(json, modelSg)));
                Diff.Check(n + ":reser", () => JsonSerializer.Serialize(JsonSerializer.Deserialize<AbilityDefinition>(json, ContentJson.Options), ContentJson.Options),
                              () => Helpers.SerSg(Helpers.DeSg<AbilityDefinition>(json, strictSg), strictSg));
            }
            foreach (string f in Directory.GetFiles(Path.Combine(DataDir, "items"), "*.json"))
            {
                string json = File.ReadAllText(f); string n = "item:" + Path.GetFileName(f);
                Diff.Check(n, () => Dump.Of(JsonSerializer.Deserialize<ItemDefinition>(json, ContentJson.Options)),
                              () => Dump.Of(Helpers.DeSg<ItemDefinition>(json, strictSg)));
                Diff.Check(n + ":writer", () => ItemWriter.Serialize(JsonSerializer.Deserialize<ItemDefinition>(json, ContentJson.Options)!),
                              () => Helpers.SerSg(Helpers.DeSg<ItemDefinition>(json, strictSg), itemWSg));
            }
            var scenarioTexts = new List<(string, string)>();
            foreach (string f in Directory.GetFiles(Path.Combine(DataDir, "scenarios"), "*.json"))
                scenarioTexts.Add((Path.GetFileName(f), File.ReadAllText(f)));
            foreach (ulong seed in new ulong[] { 0xC0FFEEUL, 0xBEEFUL, 1, 2, 3, 12345, 0xDEADBEEFUL })
                scenarioTexts.Add(("gen-" + seed, ScenarioSerializer.Serialize(ProjectChimera.Core.MapGen.ProceduralMapGenerator.Generate(seed))));
            foreach (var (name, json) in scenarioTexts)
            {
                string n = "scenario:" + name;
                Diff.Check(n, () => Dump.Of(JsonSerializer.Deserialize<ScenarioData>(json, ContentJson.ScenarioOptions)),
                              () => Dump.Of(Helpers.DeSg<ScenarioData>(json, scenSg)));
                Diff.Check(n + ":reser", () => JsonSerializer.Serialize(JsonSerializer.Deserialize<ScenarioData>(json, ContentJson.ScenarioOptions), ContentJson.ScenarioOptions),
                              () => Helpers.SerSg(Helpers.DeSg<ScenarioData>(json, scenSg), scenSg));
                Diff.Check(n + ":model", () => Dump.Of(JsonSerializer.Deserialize<ScenarioData>(json, ContentJson.ModelOutputOptions)),
                              () => Dump.Of(Helpers.DeSg<ScenarioData>(json, modelSg)));
            }
            {
                var map = ProjectChimera.Core.MapGen.ProceduralMapGenerator.Generate(0xC0FFEEUL);
                string bytes = ScenarioSerializer.Serialize(map);
                uint h = ScenarioSerializer.ComputeHash(Encoding.UTF8.GetBytes(bytes));
                Console.WriteLine($"golden hash reflection={h} expected=3387691366");
                string sgBytes = Helpers.SerSg(JsonSerializer.Deserialize<ScenarioData>(bytes, ContentJson.ScenarioOptions)!, scenSg).Replace("\r\n", "\n");
                uint h2 = ScenarioSerializer.ComputeHash(Encoding.UTF8.GetBytes(sgBytes));
                Console.WriteLine($"golden hash sg-reserialized={h2} identical={sgBytes == bytes}");
            }
            foreach (string f in Directory.GetFiles(Path.Combine(DataDir, "attribute-models"), "*.json"))
            {
                string json = File.ReadAllText(f);
                using var doc = JsonDocument.Parse(json);
                string raw = doc.RootElement.GetProperty("attribute_model").GetRawText();
                string n = "attrmodel:" + Path.GetFileName(f);
                Diff.Check(n, () => Dump.Of(JsonSerializer.Deserialize<AttributeModelDefinition>(raw, FactionDefinition.JsonOptions)),
                              () => Dump.Of(Helpers.DeSg<AttributeModelDefinition>(raw, lenSg)));
            }
            var defaultSg = new JsonSerializerOptions { TypeInfoResolver = SpikeContext.Default };
            foreach (string f in Directory.GetFiles(Path.Combine(DataDir, "behaviors"), "*.json"))
            {
                string json = File.ReadAllText(f);
                Diff.Check("behavior:" + Path.GetFileName(f), () => Dump.Of(JsonSerializer.Deserialize<BehaviorDefinition>(json)),
                              () => Dump.Of(Helpers.DeSg<BehaviorDefinition>(json, defaultSg)));
            }
            Edge.Run(strictSg, scenSg, lenSg, modelSg, defaultSg);
            Diff.Report();
        }
    }
}
