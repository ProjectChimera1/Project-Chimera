#nullable enable
using System.Text.Json.Serialization;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Data-driven AI/role BEHAVIOR definition loaded from JSON (Story 3.6, D-1). One entry per behavior; each is an
    /// orthogonal, composable identity axis on a <see cref="UnitDefinition"/> (a "healer" = a Ranged archetype + a heal
    /// ability + a <c>support</c> behavior — never a subclass, per the platform composition rule). Mirrors
    /// <see cref="AbilityDefinition"/> (PascalCase auto-props + snake_case <c>[JsonPropertyName]</c>), and is indexed by
    /// the <see cref="BehaviorRegistry"/> exactly as abilities are by <see cref="AbilityRegistry"/>.
    ///
    /// <para><b>Authoring data only — no runtime, no fold (D-2).</b> Unlike <see cref="AbilityDefinition"/> (which the
    /// effect engine resolves + executes), nothing in the sim reads a behavior this story: no SoA array, no
    /// <c>EntityWorld</c> field, no <c>Resolve*</c>, no checksum fold. 3.6 reserves only the authored field + the
    /// validation rules; a future utility-AI story adds the runtime (resolve + fold) then.</para>
    ///
    /// <para><b>Compatibility is data, not a hardcoded matrix (D-1).</b> Each behavior owns its own
    /// <see cref="CompatibleArchetypes"/> list, so the archetype-compat rule the validator enforces is a data lookup a
    /// creator can reach — never a C# matrix. An omitted / empty list is PERMISSIVE (compatible with every archetype),
    /// so shipping a new behavior can never retro-break an existing unit.</para>
    /// </summary>
    public class BehaviorDefinition
    {
        /// <summary>Stable id used for references (the <c>behaviors[]</c> entries on a unit) and located error messages.</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        /// <summary>Human-readable name shown in the UI (presentation only — never a gameplay key).</summary>
        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = "";

        /// <summary>Optional prose describing what the behavior does (shown as the picker tooltip body).</summary>
        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        /// <summary>
        /// The <see cref="UnitCategory"/> archetype names (e.g. <c>"Ranged"</c>) this behavior may be attached to.
        /// Nullable + empty ⇒ compatible with ALL archetypes (the permissive default — see <see cref="IsCompatibleWith"/>).
        /// A non-empty list restricts the behavior to exactly those archetypes (creator opt-in; the archetype-incompatible
        /// case the validator badges). Case-sensitive to match the <see cref="UnitCategory"/> string tokens.
        /// </summary>
        [JsonPropertyName("compatible_archetypes")]
        public string[]? CompatibleArchetypes { get; set; }

        /// <summary>
        /// True when this behavior may be attached to a unit whose archetype is <paramref name="category"/>. Lenient:
        /// a null / empty <see cref="CompatibleArchetypes"/> ⇒ compatible with every archetype; otherwise a case-sensitive
        /// membership test against the listed tokens (matching <see cref="UnitCategory"/>'s exact string names).
        /// </summary>
        public bool IsCompatibleWith(string category)
        {
            if (CompatibleArchetypes == null || CompatibleArchetypes.Length == 0) return true;
            for (int i = 0; i < CompatibleArchetypes.Length; i++)
                if (CompatibleArchetypes[i] == category) return true;
            return false;
        }
    }
}
