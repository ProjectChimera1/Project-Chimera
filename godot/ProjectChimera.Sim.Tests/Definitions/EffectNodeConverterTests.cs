#nullable enable
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ProjectChimera.Combat;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 2.3 AC2 / AC6 — the closed-registry converter. Every registered <c>kind</c> round-trips to its sealed
    /// runtime type; an unknown kind / missing required field / stray property (at BOTH the POCO and the
    /// effect-object layer) / over-deep nesting is a LOCATED reject, never a silent skip or a stack overflow; and no
    /// source file uses the forbidden <c>[JsonPolymorphic]</c>/<c>[JsonDerivedType]</c> (AR-22).
    /// </summary>
    public class EffectNodeConverterTests
    {
        // Deserialize an effect node directly (isolating the CONVERTER from the validator).
        private static EffectNode? Compile(string effectJson)
        {
            string json = $$"""{ "id": "t", "targeting": "Self", "effect": {{effectJson}} }""";
            AbilityDefinition def = JsonSerializer.Deserialize<AbilityDefinition>(json, ContentJson.Options)!;
            return def.EffectGraph;
        }

        // ── Every registered kind compiles to its sealed runtime type ─────────────────────────────────

        [Fact]
        public void DirectHpDelta_Compiles() =>
            Assert.IsType<DirectHpDeltaEffect>(Compile("""{ "kind": "direct_hp_delta", "delta": -10 }"""));

        [Fact]
        public void Heal_Compiles() =>
            Assert.IsType<HealEffect>(Compile("""{ "kind": "heal", "amount": 25 }"""));

        [Fact]
        public void Damage_Compiles_WithEnumByName()
        {
            var d = Assert.IsType<DamageEffect>(Compile("""{ "kind": "damage", "amount": 30, "damage_type": "Siege" }"""));
            Assert.Equal(DamageType.Siege, d.Type);
        }

        [Fact]
        public void Sequence_Compiles_WithOrderedChildren()
        {
            var s = Assert.IsType<SequenceEffect>(Compile(
                """{ "kind": "sequence", "children": [ { "kind": "heal", "amount": 1 }, { "kind": "damage", "amount": 2, "damage_type": "Normal" } ] }"""));
            Assert.Equal(2, s.Children.Length);
            Assert.IsType<HealEffect>(s.Children[0]);
            Assert.IsType<DamageEffect>(s.Children[1]);
        }

        [Fact]
        public void SearchArea_Compiles_WithFlagsFilterByName()
        {
            var s = Assert.IsType<SearchAreaEffect>(Compile(
                """{ "kind": "search_area", "radius": 4, "filter": "Enemy", "child": { "kind": "heal", "amount": 1 } }"""));
            Assert.Equal(TargetFilter.Enemy, s.Filter);
            Assert.IsType<HealEffect>(s.Child);
        }

        [Fact]
        public void ApplyModifier_Compiles_WithModifierFields()
        {
            var a = Assert.IsType<ApplyModifierEffect>(Compile(
                """{ "kind": "apply_modifier", "modifier": { "id": 7, "duration_ticks": 90, "stacking": "Stack", "max_stacks": 3, "attack_damage_delta": 5 } }"""));
            Assert.Equal(7, a.Modifier.Id);
            Assert.Equal(90, a.Modifier.DurationTicks);
            Assert.Equal(StackRule.Stack, a.Modifier.Stacking);
            Assert.Equal(3, a.Modifier.MaxStacks);
            Assert.Equal(5 * 65536, a.Modifier.AttackDamageDelta.Raw);
        }

        [Fact]
        public void Persistent_Compiles_WithPhases()
        {
            var p = Assert.IsType<PersistentEffect>(Compile(
                """{ "kind": "persistent", "initial_effect": { "kind": "heal", "amount": 1 }, "period_effect": { "kind": "damage", "amount": 2, "damage_type": "Normal" }, "period_ticks": 30, "period_count": 5 }"""));
            Assert.IsType<HealEffect>(p.InitialEffect);
            Assert.IsType<DamageEffect>(p.PeriodEffect);
            Assert.Null(p.ExpireEffect);
            Assert.Equal(30, p.PeriodTicks);
            Assert.Equal(5, p.PeriodCount);
        }

        // ── Fail-closed teeth ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void UnknownKind_IsLocatedReject_NamingTheKind()
        {
            AbilityValidationResult r = AbilityLoader.Load(
                """{ "id": "fireball", "targeting": "Self", "effect": { "kind": "lua", "script": "os.exit()" } }""", "fireball");
            Assert.False(r.Ok);
            Assert.Contains("fireball", r.Error!);
            Assert.Contains("unknown kind 'lua'", r.Error!);
        }

        [Fact]
        public void MissingRequiredField_IsLocatedReject_NamingTheField()
        {
            // damage with no 'amount'
            AbilityValidationResult r = AbilityLoader.Load(
                """{ "id": "d", "targeting": "Self", "effect": { "kind": "damage", "damage_type": "Magic" } }""", "d");
            Assert.False(r.Ok);
            Assert.Contains("amount", r.Error!);
        }

        [Fact]
        public void UnknownProperty_OnPocoLayer_IsLocatedReject()
        {
            // Disallow governs the AbilityDefinition POCO: a stray top-level field is rejected.
            AbilityValidationResult r = AbilityLoader.Load(
                """{ "id": "p", "targeting": "Self", "cooldwn": 5, "effect": { "kind": "heal", "amount": 1 } }""", "p");
            Assert.False(r.Ok);
            Assert.Contains("cooldwn", r.Error!);
        }

        [Fact]
        public void UnknownProperty_InsideEffectObject_IsLocatedReject()
        {
            // Disallow does NOT reach inside a custom converter — the converter must reject the stray itself.
            // RED if RejectUnknownProperties is removed from EffectNodeJsonConverter (the property would be skipped).
            AbilityValidationResult r = AbilityLoader.Load(
                """{ "id": "p", "targeting": "Self", "effect": { "kind": "heal", "amount": 1, "script": "evil" } }""", "p");
            Assert.False(r.Ok);
            Assert.Contains("script", r.Error!);
            Assert.Contains("unknown property", r.Error!);
        }

        [Fact]
        public void DeeplyNestedJson_ThrowsLocatedParseError_NotStackOverflow()
        {
            // 9 nested sequences → the 9th composition is at depth 8 → the converter's parse-depth guard rejects it.
            // Reaching this assertion at all proves it did not stack-overflow.
            var sb = new StringBuilder();
            sb.Append("""{ "id": "deep", "targeting": "Self", "effect": """);
            for (int i = 0; i < 9; i++) sb.Append("""{ "kind": "sequence", "children": [ """);
            sb.Append("""{ "kind": "heal", "amount": 1 }""");
            for (int i = 0; i < 9; i++) sb.Append(" ] }");
            sb.Append(" }");

            AbilityValidationResult r = AbilityLoader.Load(sb.ToString(), "deep");
            Assert.False(r.Ok);
            Assert.Contains("MaxEffectDepth", r.Error!);
        }

        [Fact]
        public void NumericEnumValue_IsRejected_NameOnly()
        {
            // allowIntegerValues:false — a numeric enum value must be rejected (enums by NAME only).
            AbilityValidationResult r = AbilityLoader.Load(
                """{ "id": "d", "targeting": "Self", "effect": { "kind": "damage", "amount": 1, "damage_type": 3 } }""", "d");
            Assert.False(r.Ok);
            Assert.Contains("damage_type", r.Error!);
        }

        [Fact]
        public void NoSourceFile_UsesJsonPolymorphic_OrJsonDerivedType()
        {
            // AR-22: closed-registry converter only; the attributes are forbidden project-wide. Match the ATTRIBUTE
            // in USE (a line that, trimmed, starts with it) — not a doc-comment mention (our own files reference the
            // forbidden attributes in XML docs to explain why they're banned).
            string srcRoot = SrcRoot();
            string[] offenders = Directory
                .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
                .Where(f => File.ReadLines(f).Any(line =>
                {
                    string s = line.TrimStart();
                    return s.StartsWith("[JsonPolymorphic", System.StringComparison.Ordinal)
                        || s.StartsWith("[JsonDerivedType", System.StringComparison.Ordinal);
                }))
                .Select(Path.GetFileName)
                .ToArray()!;
            Assert.True(offenders.Length == 0,
                $"[JsonPolymorphic]/[JsonDerivedType] is forbidden (AR-22) but appears in: {string.Join(", ", offenders)}.");
        }

        // <repo>/godot/ProjectChimera.Sim.Tests/Definitions/THIS.cs → <repo>/godot/src
        private static string SrcRoot([CallerFilePath] string thisFile = "")
        {
            string defsDir  = Path.GetDirectoryName(thisFile)!;
            string testProj = Path.GetDirectoryName(defsDir)!;
            string godot    = Path.GetDirectoryName(testProj)!;
            return Path.Combine(godot, "src");
        }
    }
}
