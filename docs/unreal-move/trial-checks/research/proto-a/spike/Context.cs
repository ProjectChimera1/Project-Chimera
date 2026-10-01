#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using ProjectChimera.Effects;
using ProjectChimera.AI;

namespace SpikeNs
{
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(UnitDefinition))]
    [JsonSerializable(typeof(BuildingDefinition))]
    [JsonSerializable(typeof(FactionDefinition))]
    [JsonSerializable(typeof(ResearchDefinition))]
    [JsonSerializable(typeof(HeroDefinition))]
    [JsonSerializable(typeof(VeterancyDefinition))]
    [JsonSerializable(typeof(AttributeModelDefinition))]
    [JsonSerializable(typeof(CombatFeedbackProfile))]
    [JsonSerializable(typeof(AbilityDefinition))]
    [JsonSerializable(typeof(ItemDefinition))]
    [JsonSerializable(typeof(TriggerDefinition))]
    [JsonSerializable(typeof(ScenarioData))]
    [JsonSerializable(typeof(BalanceReport))]
    [JsonSerializable(typeof(BehaviorDefinition))]
    [JsonSerializable(typeof(ContentPackageManifest))]
    [JsonSerializable(typeof(PlayerProfile))]
    [JsonSerializable(typeof(List<PlayerProfile>))]
    [JsonSerializable(typeof(SettingsData))]
    [JsonSerializable(typeof(ProofOfPlayToken))]
    [JsonSerializable(typeof(CustomUiTree))]
    [JsonSerializable(typeof(EffectNode))]
    [JsonSerializable(typeof(List<EffectNode>))]
    [JsonSerializable(typeof(NodeBase))]
    [JsonSerializable(typeof(DataEdge))]
    [JsonSerializable(typeof(ExecEdge))]
    [JsonSerializable(typeof(Fixed))]
    [JsonSerializable(typeof(ProjectChimera.Core.UnitTag))]
    [JsonSerializable(typeof(ProjectChimera.Effects.PeriodicStackMode))]
    [JsonSerializable(typeof(ProjectChimera.Effects.StatusFlags))]
    [JsonSerializable(typeof(ProjectChimera.Effects.StackRule))]
    [JsonSerializable(typeof(ProjectChimera.Effects.TargetFilter))]
    [JsonSerializable(typeof(ProjectChimera.Combat.DamageType))]
    [JsonSerializable(typeof(Dictionary<ProjectChimera.Combat.DamageType, Dictionary<ProjectChimera.Combat.ArmorType, Fixed>>))]
    [JsonSerializable(typeof(DtoPublic))]
    [JsonSerializable(typeof(GraphShapePublic))]
    public partial class SpikeContext : JsonSerializerContext
    {
    }
}
