#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Effects;

namespace SpikeNs
{
    /// <summary>Runs with System.Text.Json reflection DISABLED (what NativeAOT does by default) to see what still works.</summary>
    public static class NoRefl
    {
        static void T(string name, Func<string> f)
        {
            try { Console.WriteLine($"[{name}] OK  -> {f().Replace("\n", "\\n")}"); }
            catch (Exception ex) { Console.WriteLine($"[{name}] THROW {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
        }

        public static void Run()
        {
            Console.WriteLine("IsReflectionEnabledByDefault=" + JsonSerializer.IsReflectionEnabledByDefault);
            var indented = new JsonSerializerOptions { WriteIndented = true };
            T("1 JsonNode.Parse+ToJsonString(indented opts, no resolver)", () => JsonNode.Parse("{\"a\":[1,2.50,\"x\"],\"b\":{\"c\":true}}")!.ToJsonString(indented));
            T("1b JsonNode.ToJsonString() default", () => JsonNode.Parse("{\"a\":1}")!.ToJsonString());
            T("2 JsonArray.Add<string>(\"s\") then ToJsonString(indented)", () => { var a = new JsonArray(); a.Add("s"); return a.ToJsonString(indented); });
            T("2b JsonArray.Add((JsonNode)\"s\") then ToJsonString(indented)", () => { var a = new JsonArray(); a.Add((JsonNode)"s"); return a.ToJsonString(indented); });
            T("2c JsonArray.Add(JsonObject) generic overload", () => { var a = new JsonArray(); var o = new JsonObject(); a.Add(o); return a.ToJsonString(indented); });
            T("2d JsonObject[k]=int", () => { var o = new JsonObject(); o["k"] = 5; o["f"] = 2.5; o["s"] = "x"; o["b"] = true; return o.ToJsonString(indented); });
            T("2e JsonObject[k]= (JsonNode)float", () => { var o = new JsonObject(); o["k"] = 1.5f; return o.ToJsonString(); });
            T("2f JsonValue.Create<float[]>", () => JsonValue.Create(new float[] { 1, 2 })!.ToJsonString());
            T("3 anonymous Serialize", () => JsonSerializer.Serialize(new { model = "m", n = 1 }));
            T("3b Dictionary<string,string> Serialize default opts", () => JsonSerializer.Serialize(new Dictionary<string, string> { ["a"] = "b" }));
            T("3c List<string> Serialize default opts", () => JsonSerializer.Serialize(new List<string> { "a" }));
            T("3d int Serialize default opts", () => JsonSerializer.Serialize(5));
            T("4 JsonDocument.Parse + TryGetProperty", () => { using var d = JsonDocument.Parse("{\"x\":{\"y\":[1,2]}}"); return d.RootElement.GetProperty("x").GetProperty("y")[1].GetRawText(); });
            T("5 reflection Deserialize<UnitDefinition>(lenient options w/o resolver)", () => JsonSerializer.Deserialize<UnitDefinition>("{\"id\":\"u\"}", FactionDefinition.JsonOptions)!.Id);
            T("6 reflection Deserialize<AbilityDefinition>(ContentJson.Options) (JsonStringEnumConverter inside)", () => JsonSerializer.Deserialize<AbilityDefinition>("{\"id\":\"u\"}", ContentJson.Options)!.Id);
            T("7 SG lenient unit", () => Helpers.DeSg<UnitDefinition>("{\"id\":\"u\",\"hp\":3.5}", Sg.Lenient())!.Hp.ToString());
            T("8 SG strict ability w/ effect & feedback (converter re-entry via options)",
              () => Dump.Of(Helpers.DeSg<AbilityDefinition>("{\"id\":\"u\",\"cooldown\":2.5,\"effect\":{\"kind\":\"damage\",\"amount\":5,\"damage_type\":\"Magic\"},\"combat_feedback\":{\"shake\":{\"duration_sec\":1,\"strength\":2}}}", Sg.Strict())).Substring(0, 120));
            T("9 SG strict ability serialize (EffectNodeJsonConverter.Write re-entry)",
              () => Helpers.SerSg(Helpers.DeSg<AbilityDefinition>("{\"id\":\"u\",\"cooldown\":2.5,\"effect\":{\"kind\":\"damage\",\"amount\":5,\"damage_type\":\"Magic\",\"require_tag\":\"Organic\"}}", Sg.Strict()), Sg.Strict()));
            T("10 SG scenario roundtrip incl. widgets", () => Helpers.SerSg(Helpers.DeSg<ScenarioData>("{\"id\":\"a\",\"win_condition\":\"DestroyAllBuildings\",\"custom_ui\":{\"widgets\":[{\"kind\":\"Panel\",\"id\":1,\"anchor\":\"TopLeft\"}]}}", Sg.Scenario()), Sg.Scenario()).Length.ToString());
            T("11 JsonSerializerOptions copy ctor keeps resolver", () => { var c = new JsonSerializerOptions(Sg.Strict()); return (c.TypeInfoResolver is SpikeContext).ToString() + "/" + c.Converters.Count; });
            T("12 un-patched Dictionary<string,int> Deserialize via reflection API w/ SG options (type has no root)", () => JsonSerializer.Deserialize<Dictionary<string, int>>("{\"a\":1}", Sg.Lenient())!.Count.ToString());
            T("13 un-patched SettingsJson.Options + SettingsData (reflection)", () => JsonSerializer.Deserialize<SettingsData>("{}", SettingsJson.Options)!.SchemaVersion.ToString());
        }
    }
}
