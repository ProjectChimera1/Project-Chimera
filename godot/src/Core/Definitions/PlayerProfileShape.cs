#nullable enable
using System.Collections.Generic;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>One resolved slot in a <see cref="PlayerProfileShape"/> — the stable key + scope of an attribute the
    /// scenario's <see cref="PersistenceManifest"/> selected to carry forward. Story 3.9 fills each slot with a VALUE
    /// (the loaded/saved profile state); this story only defines the SHAPE.</summary>
    public readonly record struct ProfileSlot(string Key, AttributeScope Scope);

    /// <summary>
    /// The authoring-side contract a <see cref="PersistenceManifest"/> implies (Story 3.8): the ORDERED set of attribute
    /// slots that persist for a scenario, derived from the manifest's selected eligible keys (in catalog order). It is a
    /// pure shape — no values, no runtime consumer this story. Story 3.9's offline Save/Load rail fills these slots from
    /// a loaded <c>PlayerProfile</c> and applies them as deterministic init state; the gate that rejects a mid-game
    /// snapshot is <see cref="PersistenceManifestValidator"/>. Godot-free, deterministic.
    /// </summary>
    public sealed class PlayerProfileShape
    {
        /// <summary>The ordered slots (catalog order) this profile carries. Empty is valid — an enabled manifest that
        /// selected nothing yields an empty shape.</summary>
        public IReadOnlyList<ProfileSlot> Slots { get; }

        public PlayerProfileShape(IReadOnlyList<ProfileSlot> slots) => Slots = slots;
    }
}
