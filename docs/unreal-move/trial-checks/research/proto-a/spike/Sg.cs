#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using ProjectChimera.Effects;
using ProjectChimera.Combat;

namespace SpikeNs
{
    /// <summary>name-only generic enum converter (the AOT-safe replacement for JsonStringEnumConverter(null,false)).</summary>
    public sealed class StrictEnum<T> : JsonStringEnumConverter<T> where T : struct, Enum
    {
        public StrictEnum() : base(namingPolicy: null, allowIntegerValues: false) { }
    }

    public static class Sg
    {
        public static void AddStrictEnums(JsonSerializerOptions o)
        {
            o.Converters.Add(new StrictEnum<WinCondition>());
            o.Converters.Add(new StrictEnum<WinPresetKind>());
            o.Converters.Add(new StrictEnum<ObjectiveState>());
            o.Converters.Add(new StrictEnum<DslValueType>());
            o.Converters.Add(new StrictEnum<VarScope>());
            o.Converters.Add(new StrictEnum<UnitTag>());
            o.Converters.Add(new StrictEnum<PeriodicStackMode>());
            o.Converters.Add(new StrictEnum<StatusFlags>());
            o.Converters.Add(new StrictEnum<StackRule>());
            o.Converters.Add(new StrictEnum<TargetFilter>());
            o.Converters.Add(new StrictEnum<DamageType>());
            o.Converters.Add(new StrictEnum<ArmorType>());
            o.Converters.Add(new StrictEnum<AttackDomain>());
            o.Converters.Add(new StrictEnum<SeparationPriority>());
            o.Converters.Add(new StrictEnum<UnitCategory>());
            o.Converters.Add(new StrictEnum<DataWireType>());
            o.Converters.Add(new StrictEnum<WidgetKind>());
            o.Converters.Add(new StrictEnum<AnchorPoint>());
        }

        private static JsonSerializerOptions Base()
        {
            var o = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            o.TypeInfoResolver = SpikeContext.Default;
            return o;
        }

        public static JsonSerializerOptions Strict()
        {
            var o = Base();
            o.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            AddStrictEnums(o);
            o.Converters.Add(new FixedJsonConverter());
            o.Converters.Add(new EffectNodeJsonConverter());
            return o;
        }
        public static JsonSerializerOptions Scenario()
        {
            var o = Base();
            o.WriteIndented = true;
            AddStrictEnums(o);
            o.Converters.Add(new FixedJsonConverter());
            o.Converters.Add(new WidgetBaseJsonConverter());
            return o;
        }
        public static JsonSerializerOptions Lenient() => Base();
        public static JsonSerializerOptions ModelOutput()
        {
            var o = Strict();
            o.PropertyNameCaseInsensitive = true;
            o.UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip;
            return o;
        }
        public static JsonSerializerOptions ItemWriter()
        {
            var o = Strict();
            o.WriteIndented = true;
            o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault;
            return o;
        }
        public static JsonSerializerOptions Dsl()
        {
            var o = Strict();
            o.WriteIndented = true;
            o.Converters.Add(new NodeBaseJsonConverter());
            o.Converters.Add(new DataEdgeJsonConverter());
            o.Converters.Add(new ExecEdgeJsonConverter());
            return o;
        }
    }
}
