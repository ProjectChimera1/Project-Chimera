#nullable enable
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The per-scenario persistence contract (Story 3.8, AR-12 / FR-7a / FR-7b): which hero progression carries forward
    /// between a creator's custom games. A nullable net-new block on <see cref="ScenarioData.PersistenceManifest"/> (JSON
    /// <c>persistence_manifest</c>): <c>null</c> ⇒ persistence NOT configured (the default for every existing scenario);
    /// present ⇒ an <see cref="Enabled"/> master toggle + the selected eligible attribute keys.
    ///
    /// <para><b>Authoring-only (D-2, mirrors <see cref="HeroDefinition"/>).</b> PURE AUTHORING DATA. NO sim system reads
    /// or consumes it this story — the load/apply rail (fill the <see cref="PlayerProfileShape"/> from a saved profile and
    /// apply it as deterministic init state) is Story 3.9. So there is deliberately NO checksum/hash fold and NO per-entity
    /// SoA array: an unread nullable POCO, omitted-when-null on <see cref="ScenarioData"/>, moves no golden
    /// (<c>CanonicalModelHash</c> hashes by path + id string) and no <c>StartStateHash</c>/<c>SimChecksum</c>. Story 3.9
    /// adds the fill+fold at init-apply time.</para>
    ///
    /// <para><b>Determinism.</b> Godot-free (<c>src/Core/Definitions</c>), plain <c>bool</c>/<c>string</c> authoring data.
    /// The <see cref="PersistenceManifestValidator"/> range/eligibility rules gate it at authoring time (editor Save) AND
    /// at the pre-tick D3 gate (<see cref="ScenarioValidator"/>) so a hand-edited/cheat manifest is fail-closed rejected.</para>
    /// </summary>
    public sealed class PersistenceManifest
    {
        /// <summary>The master toggle. <c>true</c> (the default when a manifest is instantiated) ⇒ persistence is active
        /// for this scenario and Story 3.9's hero picker is available; <c>false</c> ⇒ persistence explicitly disabled
        /// while retaining the selection. (A NULL manifest — not this flag — means "never configured".)</summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        /// <summary>The selected attribute keys that carry forward (a subset of <see cref="PersistableAttributes.Eligible"/>
        /// in a valid manifest). Enabled-with-zero is valid (an empty profile shape); the validator rejects only
        /// unknown / mid-game-ineligible / duplicate keys.
        /// <para>The setter COERCES null → empty: a hand-edited <c>"attributes": null</c> deserializes the property to
        /// null (JSON null overrides the field initializer), and unguarded readers (<see cref="DeriveProfileShape"/>,
        /// the editor checklist) would NRE. Normalising here makes the field never-null for every consumer.</para></summary>
        [JsonPropertyName("attributes")]
        public List<string> Attributes
        {
            get => _attributes;
            set => _attributes = value ?? new List<string>();
        }
        private List<string> _attributes = new();

        /// <summary>A deep copy (the list is re-materialised; strings are immutable) so a clone and its source can be
        /// edited independently — the editor undo/history path, mirroring <see cref="HeroDefinition.Clone"/>.</summary>
        public PersistenceManifest Clone() => new PersistenceManifest
        {
            Enabled    = Enabled,
            Attributes = new List<string>(Attributes),
        };

        /// <summary>
        /// Derive the <see cref="PlayerProfileShape"/> this manifest implies: each selected key that
        /// <see cref="PersistableAttributes.IsEligible"/>, resolved IN CATALOG ORDER into a <see cref="ProfileSlot"/>.
        /// Invalid keys (unknown / mid-game / duplicate) are SKIPPED here — the validator is what REJECTS them; this
        /// method only ever produces well-formed slots, so a fully-valid manifest and a partially-invalid one both yield
        /// a coherent shape (the invalid ones simply do not appear). Duplicate eligible keys collapse to one slot.
        /// </summary>
        public PlayerProfileShape DeriveProfileShape()
        {
            var slots = new List<ProfileSlot>();
            // Walk the catalog (not the selection) so the result is catalog-ordered and deduplicated regardless of the
            // order the creator checked boxes — producer-independent, like HeroStore.FoldOrder.
            for (int i = 0; i < PersistableAttributes.Eligible.Length; i++)
            {
                PersistableAttribute attr = PersistableAttributes.Eligible[i];
                if (Attributes.Contains(attr.Key))
                    slots.Add(new ProfileSlot(attr.Key, attr.Scope));
            }
            return new PlayerProfileShape(slots);
        }
    }
}
