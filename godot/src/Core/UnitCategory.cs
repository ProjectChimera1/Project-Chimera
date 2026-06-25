namespace ProjectChimera.Core
{
    /// <summary>
    /// The six unit archetypes (the only "types" in the data-driven model — everything else composes).
    /// Parsed from <c>UnitDefinition.Category</c> at spawn into <c>EntityWorld.CategoryOf</c> and read ONLY
    /// by the presentation-side <see cref="ProjectChimera.Navigation.FormationPlanner"/> to decide which units
    /// lead (front line) and which trail (back line) in a multi-unit move (Story 1.13, DG-2 / FR-54).
    ///
    /// NOT folded into <c>SimChecksum</c> — it is presentation-read (like <c>EntityWorld.MeshType</c>): the
    /// formation it shapes is computed ONCE on the issuing machine and transmitted as a <c>Fixed</c>
    /// <c>MoveTarget</c> over the wire, so a divergent local category cannot desync. Because it is unhashed, its
    /// integer member values are free to reorder later (unlike the folded <see cref="SeparationPriority"/>).
    /// </summary>
    public enum UnitCategory : byte
    {
        Worker    = 0,
        Melee     = 1,
        Ranged    = 2,
        Siege     = 3,
        Air       = 4,
        Structure = 5,
    }
}
