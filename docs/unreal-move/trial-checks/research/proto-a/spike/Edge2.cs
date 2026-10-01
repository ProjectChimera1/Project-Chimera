#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using ProjectChimera.Effects;

namespace SpikeNs
{
    public sealed class GraphShapePublic
    {
        [JsonPropertyName("nodes")]      public List<NodeBase> Nodes     { get; set; } = new();
        [JsonPropertyName("exec_edges")] public List<ExecEdge> ExecEdges { get; set; } = new();
        [JsonPropertyName("data_edges")] public List<DataEdge> DataEdges { get; set; } = new();
    }

    public static class Edge2
    {
        public static void Run()
        {
            var dsl = Sg.Dsl(); var settingsRefl = SettingsJson.Options;
            var settingsSg = new JsonSerializerOptions { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, TypeInfoResolver = SpikeContext.Default };
            // DSL graph from the flat migration (mirrors TriggerGraphConverterTests)
            var flat = new[]
            {
                new TriggerDefinition
                {
                    Name = "A", Priority = 2,
                    Events = new[] { new TriggerEvent { Type = "match_start" } },
                    Conditions = new[] { new TriggerCondition { Type = "always" } },
                    Actions = new[]
                    {
                        new TriggerAction { Type = "spawn_unit", UnitId = "grunt", X = Fixed.FromFloat(1.5f), Z = Fixed.FromFloat(2.5f), Count = 3 },
                        new TriggerAction { Type = "display_message", Text = "hi", Duration = Fixed.FromFloat(2.25f) },
                    },
                },
                new TriggerDefinition { Name = "B", Events = new[] { new TriggerEvent { Type = "unit_dies", Faction = 1 } }, Actions = new[] { new TriggerAction { Type = "victory", Faction = 0 } } },
            };
            TriggerGraph graph = TriggerGraph.FromFlat(flat);
            graph.Nodes.Add(new EffectActionNode { Id = 100, Effect = new SequenceEffect(new DamageEffect(Fixed.FromInt(10), DamageType.Normal), new DamageEffect(Fixed.FromFloat(2.5f), DamageType.Magic, UnitTag.Organic)) });
            string canonical = graph.ToCanonicalJson();
            Console.WriteLine("graph canonical json length=" + canonical.Length);
            Diff.Check("dsl:fromJson", () => Dump.Of(JsonSerializer.Deserialize<GraphShapePublic>(canonical, DslJson.Options)),
                                       () => Dump.Of(Helpers.DeSg<GraphShapePublic>(canonical, dsl)));
            Diff.Check("dsl:reserialize", () => JsonSerializer.Serialize(JsonSerializer.Deserialize<GraphShapePublic>(canonical, DslJson.Options), DslJson.Options),
                                          () => Helpers.SerSg(Helpers.DeSg<GraphShapePublic>(canonical, dsl), dsl));
            Diff.Check("dsl:serializeEqualsCanonical", () => canonical, () => Helpers.SerSg(Helpers.DeSg<GraphShapePublic>(canonical, dsl), dsl));
            foreach (string bad in new[]
            {
                "{ \"nodes\": [ { \"id\": 0, \"kind\": \"run_script\" } ], \"exec_edges\": [], \"data_edges\": [] }",
                "{ \"nodes\": [ { \"id\": 0, \"kind\": \"trigger\", \"script\": \"x\" } ], \"exec_edges\": [], \"data_edges\": [] }",
                "{ \"nodes\": [], \"exec_edges\": [ { \"src\": 0 } ], \"data_edges\": [] }",
                "{ \"nodes\": [], \"exec_edges\": [], \"data_edges\": [ { \"src\":0, \"src_port\":0, \"dst\":1, \"dst_port\":0 } ] }",
                "{ \"nodes\": [], \"exec_edges\": [], \"data_edges\": [], \"extra\": 1 }",
            })
                Diff.Check("dsl:bad:" + bad.Substring(0, Math.Min(50, bad.Length)), () => Dump.Of(JsonSerializer.Deserialize<GraphShapePublic>(bad, DslJson.Options)),
                                                      () => Dump.Of(Helpers.DeSg<GraphShapePublic>(bad, dsl)));

            // PlayerProfile (LocalProfileSource options: indented + skip)
            var profiles = new List<PlayerProfile>
            {
                new PlayerProfile { ProfileId = "h#1", HeroDefId = "h", FactionId = "alpha", DisplayName = "Hero", SignatureAbility = null,
                    Values = new List<ProfileAttributeValue> { new("hero.level", 3), new("hero.xp", 65536) },
                    Inventory = new List<ProfileInventoryItem> { new("potion", 2, 0), new("ring", 0) } },
            };
            var profOptsRefl = new JsonSerializerOptions { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            var profOptsSg = new JsonSerializerOptions { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, TypeInfoResolver = SpikeContext.Default };
            Diff.Check("profile:ser", () => JsonSerializer.Serialize(profiles, profOptsRefl), () => Helpers.SerSg(profiles, profOptsSg));
            string pj = JsonSerializer.Serialize(profiles, profOptsRefl);
            Diff.Check("profile:de", () => Dump.Of(JsonSerializer.Deserialize<List<PlayerProfile>>(pj, profOptsRefl)), () => Dump.Of(Helpers.DeSg<List<PlayerProfile>>(pj, profOptsSg)));
            string pjOld = "[ { \"profile_id\":\"x\", \"inventory\": [ { \"item_id\":\"a\", \"charges\": 1 } ], \"values\": [ { \"key\":\"k\", \"raw\": 5 } ] } ]";
            Diff.Check("profile:de:slotAbsent", () => Dump.Of(JsonSerializer.Deserialize<List<PlayerProfile>>(pjOld, profOptsRefl)), () => Dump.Of(Helpers.DeSg<List<PlayerProfile>>(pjOld, profOptsSg)));
            string pjNull = "[ { \"profile_id\":\"x\", \"inventory\": null, \"values\": null } ]";
            Diff.Check("profile:de:nullLists", () => Dump.Of(JsonSerializer.Deserialize<List<PlayerProfile>>(pjNull, profOptsRefl)), () => Dump.Of(Helpers.DeSg<List<PlayerProfile>>(pjNull, profOptsSg)));
            Diff.Check("profile:default-ser", () => JsonSerializer.Serialize(profiles[0]), () => Helpers.SerSg(profiles[0], new JsonSerializerOptions { TypeInfoResolver = SpikeContext.Default }));

            // SettingsData
            var sd = new SettingsData { LlmBaseUrl = "http://x", CameraSpeed = 1.25f, UiScale = 0.1f, ResolutionWidth = 100 };
            Diff.Check("settings:ser", () => JsonSerializer.Serialize(sd, settingsRefl), () => Helpers.SerSg(sd, settingsSg));
            foreach (string sj in new[] { "{}", "{ \"camera_speed\": 2, /*c*/ \"unknown\": 1, \"ui_scale\": null, }", "{ \"llm_base_url\": null, \"schema_version\": 99 }", "{ \"camera_speed\": \"x\" }" })
                Diff.Check("settings:de:" + sj, () => Dump.Of(SettingsData.FromJson(sj, settingsRefl)), () => Dump.Of(SettingsData.FromJson(sj, settingsSg)));

            // ContentPackageManifest
            var man = new ContentPackageManifest { Id = "m", DisplayName = "M", CreatedAt = "2026-01-01T00:00:00.0000000Z", ScenarioHash = 5, ProofOfPlay = null, Tags = new List<string> { "a" } };
            var manRefl = new JsonSerializerOptions { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            var manSg = new JsonSerializerOptions { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, TypeInfoResolver = SpikeContext.Default };
            Diff.Check("manifest:ser", () => JsonSerializer.Serialize(man, manRefl), () => Helpers.SerSg(man, manSg));
            string mj = JsonSerializer.Serialize(man, manRefl);
            Diff.Check("manifest:de", () => Dump.Of(JsonSerializer.Deserialize<ContentPackageManifest>(mj, manRefl)), () => Dump.Of(Helpers.DeSg<ContentPackageManifest>(mj, manSg)));

            // FactionWriter hero / veterancy / attribute_model (WhenWritingNull) + default-options feedback
            var heroR = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
            var heroS = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, TypeInfoResolver = SpikeContext.Default };
            var hero = new HeroDefinition { MaxLevel = 5, SignatureAbility = "x", Attributes = new HeroAttributesDefinition { Base = new Dictionary<string, float> { ["b"] = 2.5f, ["a"] = 1f } } };
            Diff.Check("hero:ser", () => JsonSerializer.Serialize(hero, heroR), () => Helpers.SerSg(hero, heroS));
            var vet = new VeterancyDefinition { Ranks = new List<VeterancyRank> { new VeterancyRank { Kills = 3, StatDeltas = new Dictionary<string, float> { ["hp"] = 5f } } } };
            Diff.Check("vet:ser", () => JsonSerializer.Serialize(vet, heroR), () => Helpers.SerSg(vet, heroS));
            var fb = new CombatFeedbackProfile { HitFlash = new FlashSpec { ColorRgb = new[] { 1f, 0.5f, 0f } }, ImpactSoundId = "x.ogg", HitFreezeFrames = 2 };
            var defR = new JsonSerializerOptions(); var defS = new JsonSerializerOptions { TypeInfoResolver = SpikeContext.Default };
            Diff.Check("feedback:ser-default", () => JsonSerializer.Serialize(fb), () => Helpers.SerSg(fb, defS));
            Diff.Check("feedback:de-default", () => Dump.Of(JsonSerializer.Deserialize<CombatFeedbackProfile>(JsonSerializer.Serialize(fb))), () => Dump.Of(Helpers.DeSg<CombatFeedbackProfile>(JsonSerializer.Serialize(fb), defS)));
            string[] models = System.IO.Directory.GetFiles(@"D:\Projects\Project_Chimera\godot\resources\data\attribute-models", "*.json");
            foreach (string f in models)
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(f));
                string raw = doc.RootElement.GetProperty("attribute_model").GetRawText();
                var am = JsonSerializer.Deserialize<AttributeModelDefinition>(raw, FactionDefinition.JsonOptions);
                Diff.Check("attrmodel:ser:" + System.IO.Path.GetFileName(f), () => JsonSerializer.Serialize(am, heroR), () => Helpers.SerSg(am, heroS));
            }
            // LLM provider request bodies: anonymous -> named DTO must produce identical bytes
            var body = new { model = "m\u00e9\"<x>", max_tokens = 123, system = "sys & <b>", messages = new[] { new { role = "user", content = "hi \u2603 'q'" } } };
            string expected = JsonSerializer.Serialize(body);
            Console.WriteLine("anonymous body json: " + expected);
        }
    }
}
