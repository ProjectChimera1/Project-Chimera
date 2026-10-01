#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Dsl;
using ProjectChimera.Effects;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// TRIAL (R1 research spike) — the single AOT-safe call surface for every System.Text.Json (de)serialize in the sim
    /// source set. Each method resolves the <see cref="JsonTypeInfo{T}"/> from the options' <c>TypeInfoResolver</c>
    /// (a source-generated <c>ChimeraJsonContext</c>) instead of the reflection overloads, so no IL2026/IL3050 site remains.
    /// An options object WITHOUT a resolver (a hand-built test/Godot-panel options) takes the reflection fallback, which is
    /// reachable only while <see cref="JsonSerializer.IsReflectionEnabledByDefault"/> (false under NativeAOT).
    /// </summary>
    public static class AotJson
    {
        /// <summary>Default-posture options (the old <c>JsonSerializer.Deserialize&lt;T&gt;(json)</c> with no options), bound to the context.</summary>
        public static readonly JsonSerializerOptions DefaultOptions = new() { TypeInfoResolver = ChimeraJsonContext.Default };

        private static JsonTypeInfo<T> Info<T>(JsonSerializerOptions o) => (JsonTypeInfo<T>)o.GetTypeInfo(typeof(T));
        private static bool Bound(JsonSerializerOptions o) => o.TypeInfoResolver is not null;

        public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize(json, Info<T>(DefaultOptions));
        public static T? Deserialize<T>(string json, JsonSerializerOptions options) =>
            Bound(options) ? JsonSerializer.Deserialize(json, Info<T>(options)) : Reflection.Deserialize<T>(json, options);
        public static T? Deserialize<T>(JsonElement element, JsonSerializerOptions options) =>
            Bound(options) ? element.Deserialize(Info<T>(options)) : Reflection.Deserialize<T>(element, options);
        public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Info<T>(DefaultOptions));
        public static string Serialize<T>(T value, JsonSerializerOptions options) =>
            Bound(options) ? JsonSerializer.Serialize(value, Info<T>(options)) : Reflection.Serialize(value, options);
        public static void Serialize<T>(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            if (Bound(options)) JsonSerializer.Serialize(writer, value, Info<T>(options));
            else Reflection.Serialize(writer, value, options);
        }

        /// <summary>The reflection overloads, quarantined behind the runtime feature switch (dead + trimmed under NativeAOT).</summary>
        private static class Reflection
        {
            private static void Guard()
            {
                if (!JsonSerializer.IsReflectionEnabledByDefault)
                    throw new InvalidOperationException("JsonSerializerOptions without a TypeInfoResolver cannot be used when reflection-based serialization is disabled; bind it to ChimeraJsonContext.");
            }
            [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Reachable only when IsReflectionEnabledByDefault (JIT: Godot / Tier-1); false and trimmed under NativeAOT.")]
            [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "As above.")]
            public static T? Deserialize<T>(string json, JsonSerializerOptions o) { Guard(); return JsonSerializer.Deserialize<T>(json, o); }
            [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "As above.")]
            [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "As above.")]
            public static T? Deserialize<T>(JsonElement e, JsonSerializerOptions o) { Guard(); return e.Deserialize<T>(o); }
            [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "As above.")]
            [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "As above.")]
            public static string Serialize<T>(T v, JsonSerializerOptions o) { Guard(); return JsonSerializer.Serialize(v, o); }
            [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "As above.")]
            [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "As above.")]
            public static void Serialize<T>(Utf8JsonWriter w, T v, JsonSerializerOptions o) { Guard(); JsonSerializer.Serialize(w, v, o); }
        }

        /// <summary>Name-only (allowIntegerValues:false) converter for one enum — same underlying EnumConverter as before.</summary>
        public sealed class StrictEnum<TEnum> : JsonStringEnumConverter<TEnum> where TEnum : struct, Enum
        {
            public StrictEnum() : base(namingPolicy: null, allowIntegerValues: false) { }
        }

        /// <summary>
        /// The AOT-safe replacement for <c>new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false)</c>: ONE
        /// factory registered per posture, dispatching to a closed registry of per-enum converters (no <c>MakeGenericType</c>).
        /// An enum outside the registry throws loudly — it never silently falls back to the lenient numeric converter.
        /// </summary>
        public sealed class StrictEnumConverterFactory : JsonConverterFactory
        {
            private static readonly Dictionary<Type, JsonConverterFactory> Registry = new()
            {
                [typeof(WinCondition)]       = new StrictEnum<WinCondition>(),
                [typeof(WinPresetKind)]      = new StrictEnum<WinPresetKind>(),
                [typeof(ObjectiveState)]     = new StrictEnum<ObjectiveState>(),
                [typeof(DslValueType)]       = new StrictEnum<DslValueType>(),
                [typeof(VarScope)]           = new StrictEnum<VarScope>(),
                [typeof(UnitTag)]            = new StrictEnum<UnitTag>(),
                [typeof(PeriodicStackMode)]  = new StrictEnum<PeriodicStackMode>(),
                [typeof(StatusFlags)]        = new StrictEnum<StatusFlags>(),
                [typeof(StackRule)]          = new StrictEnum<StackRule>(),
                [typeof(TargetFilter)]       = new StrictEnum<TargetFilter>(),
                [typeof(DamageType)]         = new StrictEnum<DamageType>(),
                [typeof(ArmorType)]          = new StrictEnum<ArmorType>(),
                [typeof(AttackDomain)]       = new StrictEnum<AttackDomain>(),
                [typeof(SeparationPriority)] = new StrictEnum<SeparationPriority>(),
                [typeof(UnitCategory)]       = new StrictEnum<UnitCategory>(),
                [typeof(AnchorPoint)]        = new StrictEnum<AnchorPoint>(),
                [typeof(WidgetKind)]         = new StrictEnum<WidgetKind>(),
                [typeof(LocalUiAction)]      = new StrictEnum<LocalUiAction>(),
                [typeof(DataWireType)]       = new StrictEnum<DataWireType>(),
            };

            /// <summary>Every enum type the registry covers (for the completeness guard test).</summary>
            public static IReadOnlyCollection<Type> RegisteredEnums => Registry.Keys;

            public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

            public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
                Registry.TryGetValue(typeToConvert, out JsonConverterFactory? f)
                    ? f.CreateConverter(typeToConvert, options)
                    : throw new NotSupportedException($"Enum '{typeToConvert.FullName}' is not in AotJson.StrictEnumConverterFactory's closed registry — add it (and a [JsonSerializable] root).");
        }

        /// <summary>Registers the strict name-only enum policy on a posture (replaces the old per-posture JsonStringEnumConverter).</summary>
        public static void AddStrictEnums(JsonSerializerOptions o) => o.Converters.Add(new StrictEnumConverterFactory());
    }
}
