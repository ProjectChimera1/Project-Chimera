#nullable enable
using System.Collections.Generic;
using System.Text.Json.Serialization;
using ProjectChimera.AI;
using ProjectChimera.AI.Providers;
using ProjectChimera.Combat;
using ProjectChimera.Dsl;
using ProjectChimera.Effects;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// TRIAL (R1 research spike) — the ONE source-generated metadata context for the sim's JSON surface. Metadata mode
    /// only (no fast-path serializers) so the behaviour is driven by the posture's <c>JsonSerializerOptions</c>
    /// (<see cref="ContentJson"/>), exactly as the reflection serializer was. Used as the <c>TypeInfoResolver</c> of every posture.
    /// </summary>
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    // ── content roots (every DTO a loader / writer / editor reads or writes) ──
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
    [JsonSerializable(typeof(BehaviorDefinition))]
    [JsonSerializable(typeof(ContentPackageManifest))]
    [JsonSerializable(typeof(PlayerProfile))]
    [JsonSerializable(typeof(List<PlayerProfile>))]
    [JsonSerializable(typeof(SettingsData))]
    [JsonSerializable(typeof(ProofOfPlayToken))]
    [JsonSerializable(typeof(CustomUiTree))]
    [JsonSerializable(typeof(DamageTable.Dto))]
    [JsonSerializable(typeof(TriggerGraph.GraphJsonShape))]
    // ── converter-handled roots (hand-written closed-registry converters; metadata = the converter) ──
    [JsonSerializable(typeof(EffectNode))]
    [JsonSerializable(typeof(List<EffectNode>))]
    [JsonSerializable(typeof(NodeBase))]
    [JsonSerializable(typeof(DataEdge))]
    [JsonSerializable(typeof(ExecEdge))]
    [JsonSerializable(typeof(WidgetBase))]
    [JsonSerializable(typeof(Fixed))]
    // ── enums that the hand-written converters re-enter the serializer for (WriteEnum/ReadEnum) ──
    [JsonSerializable(typeof(UnitTag))]
    [JsonSerializable(typeof(PeriodicStackMode))]
    [JsonSerializable(typeof(StatusFlags))]
    [JsonSerializable(typeof(StackRule))]
    [JsonSerializable(typeof(TargetFilter))]
    [JsonSerializable(typeof(DamageType))]
    // ── LLM (presentation-adjacent) ──
    [JsonSerializable(typeof(BalanceReport))]
    [JsonSerializable(typeof(AnthropicRequestBody))]
    [JsonSerializable(typeof(ChatRequestBody))]
    [JsonSerializable(typeof(OllamaRequestBody))]
    internal partial class ChimeraJsonContext : JsonSerializerContext
    {
    }
}
