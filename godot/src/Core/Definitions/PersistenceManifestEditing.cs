#nullable enable
namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The pure state transitions the Persistence Manifest editor applies when a creator flips the master switch or
    /// toggles an attribute row (Story 3.8). Extracted from <c>PersistenceManifestPanel</c> (a Godot <c>Node</c>, which the
    /// Tier-1 assembly cannot instantiate) so the observable authoring behavior — WHAT a click persists — is Godot-free and
    /// unit-testable, mirroring how the Unit Card editor's resolve logic was extracted from its panel. The panel is left as
    /// thin wiring that calls these and syncs its controls; these methods own the semantics.
    /// </summary>
    public static class PersistenceManifestEditing
    {
        /// <summary>Master-switch transition. Enabling instantiates the manifest if absent and sets
        /// <see cref="PersistenceManifest.Enabled"/> = true; disabling sets it false while RETAINING the manifest and its
        /// selection (the picker-availability semantics are Story 3.9's — a disabled manifest is inert, not cleared).
        /// Returns the resulting manifest (may be newly created; may be null only if <paramref name="on"/> is false and
        /// there was nothing to disable).</summary>
        public static PersistenceManifest? ApplyMasterToggle(PersistenceManifest? current, bool on)
        {
            if (on)
            {
                current ??= new PersistenceManifest();
                current.Enabled = true;
                return current;
            }

            if (current != null) current.Enabled = false;   // retain the selection
            return current;
        }

        /// <summary>Attribute-row transition. Selecting an attribute implies persistence is on (create/enable the manifest
        /// if needed) and adds the key (no duplicates); deselecting removes it. Returns the resulting manifest (never null —
        /// selecting always materialises one).</summary>
        public static PersistenceManifest ApplyAttributeToggle(PersistenceManifest? current, string key, bool selected)
        {
            current ??= new PersistenceManifest();
            current.Enabled = true;   // touching the checklist means the creator wants persistence active

            if (selected)
            {
                if (!current.Attributes.Contains(key)) current.Attributes.Add(key);
            }
            else
            {
                current.Attributes.Remove(key);
            }

            return current;
        }
    }
}
