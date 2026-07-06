#nullable enable
using System.Globalization;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Godot-free presentation helpers for the Story 3.3 read-only Unit Card panel: numeric-stat formatting
    /// (UX-DR34) and ability-id → display-name resolution (D-3). Lives under <c>src/Core/Definitions</c> — a
    /// <c>SimSources.props</c>-globbed path — so the Tier-1 test project (which compiles that source set directly
    /// and does NOT reference <c>src/CreationSuite</c>/<c>src/UI</c>) can exercise this logic without the engine.
    ///
    /// <para><b>Determinism posture.</b> Purely additive, unfolded, formatting-only. It reads a content POCO
    /// (<see cref="UnitDefinition"/>/<see cref="AbilityRegistry"/>), touches no sim array, and moves no checksum
    /// or golden — the 3.1c pure-presentation posture. No <c>using Godot;</c>, no <see cref="ProjectChimera.Core.Fixed"/>
    /// (the card reads authoring floats, never sim state).</para>
    /// </summary>
    public static class UnitCardText
    {
        /// <summary>
        /// Format an authoring-float stat for the UX-DR34 mono readout: up to 2 decimals, trailing zeros trimmed,
        /// invariant culture (UI chrome, not localized content). <c>55f → "55"</c>, <c>0.95f → "0.95"</c>,
        /// <c>1.5f → "1.5"</c>, <c>8f → "8"</c>. Invariant culture keeps the decimal separator a '.' regardless of
        /// the host locale, so a French/German OS can't render "0,95" and desync the visual from the data model.
        /// </summary>
        public static string FormatStat(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>
        /// Resolve each ability id to a human display label for the card's attached-ability list (D-3): a known id
        /// resolves to its <see cref="AbilityDefinition.DisplayName"/>; an unknown id (or the <see cref="AbilityRegistry.Empty"/>
        /// registry, whose <see cref="AbilityRegistry.IndexOf"/> returns −1 for everything) falls back to the raw id, so
        /// the row is never blank. An ability that resolved but carries an empty display name also falls back to the id.
        /// ALL ids are included in list order (passives too) — this reads the raw <see cref="UnitDefinition.Abilities"/>
        /// array directly, NOT the castable-only <c>[JsonIgnore] AbilityIndices</c> (which drops passives and is empty
        /// until scenario link). Godot-free + link-time-cheap (a per-id linear scan over the few abilities).
        /// </summary>
        public static string[] ResolveAbilityLabels(string[]? ids, AbilityRegistry? registry)
        {
            if (ids == null || ids.Length == 0)
                return System.Array.Empty<string>();

            var labels = new string[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                string id = ids[i];
                int idx = registry?.IndexOf(id) ?? -1;
                if (idx >= 0)
                {
                    string name = registry!.Get(idx).DisplayName;
                    labels[i] = string.IsNullOrEmpty(name) ? id : name;  // resolved-but-unnamed → raw id (never blank)
                }
                else
                {
                    labels[i] = id;  // unknown id / Empty registry → raw-id fallback
                }
            }
            return labels;
        }
    }
}
