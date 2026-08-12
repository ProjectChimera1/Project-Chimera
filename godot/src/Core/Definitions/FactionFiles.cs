#nullable enable

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// DW-696 — the ONE declaration of the on-disk faction-file naming convention, shared by the WRITE side (the
    /// faction wizard's Finish step) and BOTH discovery scans.
    ///
    /// <para>The convention used to be spelled in three unlinked places: DW-528 named it for the writer
    /// (<c>FactionDefinerWizardCore.FactionFileSuffix</c>) while
    /// <see cref="FactionDefinition.LoadSelectableFromDirectory"/> and
    /// <c>ProjectChimera.Core.Skirmish.SkirmishCatalog.ScanFactions</c> each carried a hand-copied
    /// <c>"*_faction.json"</c> literal. Nothing tied them together, so changing the suffix in one place would let the
    /// wizard save a faction file every picker then fails to discover — a save-and-vanish with no error anywhere,
    /// the worst class of silent failure for authored content.</para>
    ///
    /// <para>Both discovery sites now derive their glob from <see cref="DiscoveryGlob"/> and the wizard's suffix is an
    /// alias of <see cref="Suffix"/>, so a suffix change moves all three together by construction. Pinned by
    /// <c>FactionFileConventionTests</c> (a source scan forbids re-introducing the literal at either scan site).</para>
    ///
    /// <para>Compile-time constants, not <c>static readonly</c>, so the wizard's existing
    /// <c>public const string FactionFileSuffix</c> can alias <see cref="Suffix"/> without changing its public shape.
    /// Godot-free, allocation-free, no I/O — this type only names the convention; the scanning lives at the call sites.</para>
    /// </summary>
    public static class FactionFiles
    {
        /// <summary>
        /// The suffix+extension an authored faction file carries: <c>&lt;id&gt;_faction.json</c>. The decoration
        /// between a free-text faction id and the name that hits the filesystem.
        /// </summary>
        public const string Suffix = "_faction.json";

        /// <summary>
        /// The <see cref="System.IO.Directory.GetFiles(string,string)"/> pattern that finds every file written under
        /// <see cref="Suffix"/> — deliberately NOT a bare <c>*.json</c>, because the faction directory also holds
        /// unrelated sample content (<c>_buildingcard_sample.json</c> / <c>_unitcard_sample.json</c>) that must never
        /// be mistaken for a faction.
        /// </summary>
        public const string DiscoveryGlob = "*" + Suffix;
    }
}
