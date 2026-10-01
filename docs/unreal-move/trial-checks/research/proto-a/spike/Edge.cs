#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;

namespace SpikeNs
{
    public sealed class DtoPublic
    {
        [JsonPropertyName("multipliers")]
        public Dictionary<DamageType, Dictionary<ArmorType, Fixed>>? Multipliers { get; set; }
    }

    public static class Edge
    {
        public static void Run(JsonSerializerOptions strictSg, JsonSerializerOptions scenSg, JsonSerializerOptions lenSg, JsonSerializerOptions modelSg, JsonSerializerOptions defSg)
        {
            string ab(string extra = "", string dmg = "\"damage_type\": \"Magic\"") =>
                "{ \"id\":\"x\", \"display_name\":\"X\", \"targeting\":\"TargetUnit\", \"cooldown\": 3, " + extra +
                " \"effect\": { \"kind\":\"damage\", \"amount\": 5, " + dmg + " } }";
            var abilityCases = new Dictionary<string, string>
            {
                ["ok"] = ab(),
                ["unknownTop"] = ab("\"cooldwn\": 1,"),
                ["enumNumeric"] = ab("", "\"damage_type\": 3"),
                ["enumLower"] = ab("", "\"damage_type\": \"magic\""),
                ["enumBad"] = ab("", "\"damage_type\": \"Nope\""),
                ["fixedHuge"] = ab().Replace("\"cooldown\": 3", "\"cooldown\": 40000"),
                ["fixedString"] = ab().Replace("\"cooldown\": 3", "\"cooldown\": \"3\""),
                ["comments"] = "// c\n" + ab("/* x */ "),
                ["trailingComma"] = ab().Replace("}", "},"),
                ["feedbackUnknown"] = ab("\"combat_feedback\": { \"shake\": { \"duration_sec\": 1, \"strengthh\": 2 } },"),
                ["nullEffect"] = "{ \"id\":\"x\", \"effect\": null }",
                ["emptyObj"] = "{}",
                ["wrongType"] = "{ \"id\": 5 }",
                ["dupKey"] = ab("\"cooldown\": 9,"),
                ["upperId"] = ab().Replace("\"id\"", "\"ID\""),
                ["badEffectKind"] = ab().Replace("\"damage\"", "\"nuke\""),
                ["trunc"] = "{ \"id\": \"x\"",
                ["parsedTargetingProp"] = ab("\"ParsedTargeting\": 1,"),
            };
            foreach (var kv in abilityCases)
            {
                string k = kv.Key, j = kv.Value;
                Diff.Check("edge:ability:" + k, () => Dump.Of(JsonSerializer.Deserialize<AbilityDefinition>(j, ContentJson.Options)),
                                               () => Dump.Of(Helpers.DeSg<AbilityDefinition>(j, strictSg)));
                Diff.Check("edge:abilityModel:" + k, () => Dump.Of(JsonSerializer.Deserialize<AbilityDefinition>(j, ContentJson.ModelOutputOptions)),
                                               () => Dump.Of(Helpers.DeSg<AbilityDefinition>(j, modelSg)));
            }
            var scen = new Dictionary<string, string>
            {
                ["winNumeric"] = "{ \"id\":\"a\", \"win_condition\": 1 }",
                ["winNameLower"] = "{ \"id\":\"a\", \"win_condition\": \"destroyallbuildings\" }",
                ["winNameBad"] = "{ \"id\":\"a\", \"win_condition\": \"Nope\" }",
                ["winNumericStr"] = "{ \"id\":\"a\", \"win_condition\": \"1\" }",
                ["unknownKey"] = "{ \"id\":\"a\", \"zzz\": 1, \"units\": [ { \"unit_id\":\"w\", \"slot\":1, \"x\": 1.5, \"z\": 2, \"extra\": 1 } ] }",
                ["varEnumNumeric"] = "{ \"id\":\"a\", \"variables\": [ { \"name\":\"v\", \"type\": 1, \"scope\":\"Global\", \"initial\": 3 } ] }",
                ["varOk"] = "{ \"id\":\"a\", \"variables\": [ { \"name\":\"v\", \"type\": \"Int\", \"scope\":\"Global\", \"initial\": 3.5 } ] }",
                ["varFixedBad"] = "{ \"id\":\"a\", \"variables\": [ { \"name\":\"v\", \"type\": \"Int\", \"scope\":\"Global\", \"initial\": 99999 } ] }",
                ["customUiBadKind"] = "{ \"id\":\"a\", \"custom_ui\": { \"widgets\": [ { \"kind\": \"Zed\" } ] } }",
                ["customUiOk"] = "{ \"id\":\"a\", \"custom_ui\": { \"widgets\": [ { \"kind\": \"Panel\", \"id\": 1, \"anchor\": \"TopLeft\" } ] } }",
                ["customUiUnknownTop"] = "{ \"id\":\"a\", \"custom_ui\": { \"widgets\": [], \"foo\": 1 } }",
                ["nullArrays"] = "{ \"id\":\"a\", \"units\": null, \"player_slots\": null }",
                ["floatAsInt"] = "{ \"id\":\"a\", \"map_bounds\": 120 , \"suggested_players\": 2.0 }",
                ["trigger"] = "{ \"id\":\"a\", \"triggers\": [ { \"name\":\"t\", \"cooldown_seconds\": 2.25, \"events\":[{\"type\":\"timer\",\"amount\":1.5}] } ] }",
            };
            foreach (var kv in scen)
            {
                string k = kv.Key, j = kv.Value;
                Diff.Check("edge:scenario:" + k, () => Dump.Of(JsonSerializer.Deserialize<ScenarioData>(j, ContentJson.ScenarioOptions)),
                                               () => Dump.Of(Helpers.DeSg<ScenarioData>(j, scenSg)));
                Diff.Check("edge:scenarioModel:" + k, () => Dump.Of(JsonSerializer.Deserialize<ScenarioData>(j, ContentJson.ModelOutputOptions)),
                                               () => Dump.Of(Helpers.DeSg<ScenarioData>(j, modelSg)));
            }
            var unit = new Dictionary<string, string>
            {
                ["ok"] = "{ \"id\":\"u\", \"hp\": 120.5, \"category\":\"Ranged\", \"tags\":[\"Organic\"], \"prerequisites\":[\"b\"] }",
                ["extra"] = "{ \"id\":\"u\", \"newField\": {\"a\":1}, \"hp\": 1 }",
                ["catNumeric"] = "{ \"id\":\"u\", \"category\": 3 }",
                ["parsedProp"] = "{ \"id\":\"u\", \"ParsedCategory\": 3, \"ParsedDamageType\": 2 }",
                ["abilityIndices"] = "{ \"id\":\"u\", \"AbilityIndices\": [1,2], \"AuraAbilityIndex\": 4 }",
                ["costNull"] = "{ \"id\":\"u\", \"cost\": null }",
                ["costMap"] = "{ \"id\":\"u\", \"cost\": {\"ore\": 5, \"crystal\": 7} }",
                ["costDup"] = "{ \"id\":\"u\", \"cost\": {\"ore\": 5, \"ore\": 7} }",
                ["hpStr"] = "{ \"id\":\"u\", \"hp\": \"100\" }",
                ["hpNaN"] = "{ \"id\":\"u\", \"hp\": NaN }",
                ["heroBlock"] = "{ \"id\":\"u\", \"is_hero\": true, \"hero\": { \"max_level\": 5, \"attributes\": { \"base\": {\"str\": 1.5}, \"per_level\": {\"str\": 0.25} } } }",
                ["vet"] = "{ \"id\":\"u\", \"veterancy\": { \"ranks\": [ {\"kills\": 3, \"stat_deltas\": {\"hp\": 2.5}} ] } }",
                ["feedback"] = "{ \"id\":\"u\", \"combat_feedback\": { \"hit_flash\": { \"color_rgb\": [1,0,0] } } }",
            };
            foreach (var kv in unit)
            {
                string k = kv.Key, j = kv.Value;
                Diff.Check("edge:unit:" + k, () => Dump.Of(JsonSerializer.Deserialize<UnitDefinition>(j, FactionDefinition.JsonOptions)),
                                             () => Dump.Of(Helpers.DeSg<UnitDefinition>(j, lenSg)));
                Diff.Check("edge:unitModel:" + k, () => Dump.Of(JsonSerializer.Deserialize<UnitDefinition>(j, ContentJson.ModelOutputOptions)),
                                             () => Dump.Of(Helpers.DeSg<UnitDefinition>(j, modelSg)));
                Diff.Check("edge:unitSer:" + k, () => JsonSerializer.Serialize(JsonSerializer.Deserialize<UnitDefinition>(j, FactionDefinition.JsonOptions), FactionDefinition.JsonOptions),
                                             () => Helpers.SerSg(Helpers.DeSg<UnitDefinition>(j, lenSg), lenSg));
                Diff.Check("edge:unitSerDefault:" + k, () => JsonSerializer.Serialize(JsonSerializer.Deserialize<UnitDefinition>(j, FactionDefinition.JsonOptions)),
                                             () => Helpers.SerSg(Helpers.DeSg<UnitDefinition>(j, lenSg), defSg));
            }
            // BuildingDefinition inheritance property order
            Diff.Check("edge:buildingSer", () => JsonSerializer.Serialize(new BuildingDefinition { Id = "b" }, FactionDefinition.JsonOptions),
                                          () => Helpers.SerSg(new BuildingDefinition { Id = "b" }, lenSg));
            // DamageTable-style enum-keyed dict
            var dtReflOpts = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, Converters = { new JsonStringEnumConverter(), new FixedJsonConverter() } };
            var dtSgOpts = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, TypeInfoResolver = SpikeContext.Default };
            dtSgOpts.Converters.Add(new JsonStringEnumConverter<DamageType>());
            dtSgOpts.Converters.Add(new JsonStringEnumConverter<ArmorType>());
            dtSgOpts.Converters.Add(new FixedJsonConverter());
            var dt = new Dictionary<string, string>
            {
                ["ok"] = "{ \"multipliers\": { \"Normal\": { \"Unarmored\": 1.0, \"Light\": 0.5 } } }",
                ["lowerKeys"] = "{ \"multipliers\": { \"normal\": { \"unarmored\": 1.0, \"LIGHT\": 0.5 } } }",
                ["intKeys"] = "{ \"multipliers\": { \"0\": { \"1\": 1.0 } } }",
                ["badKey"] = "{ \"multipliers\": { \"Zed\": { \"Light\": 1.0 } } }",
                ["dupKey"] = "{ \"multipliers\": { \"Normal\": { \"Light\": 1.0, \"Light\": 2.0 } } }",
                ["flagsKey"] = "{ \"multipliers\": { \"Normal, Pierce\": { \"Light\": 1.0 } } }",
            };
            foreach (var kv in dt)
            {
                string k = kv.Key, j = kv.Value;
                Diff.Check("edge:damageTableDict:" + k, () => Dump.Of(JsonSerializer.Deserialize<DtoPublic>(j, dtReflOpts)),
                                                         () => Dump.Of(Helpers.DeSg<DtoPublic>(j, dtSgOpts)));
            }
        }
    }
}
