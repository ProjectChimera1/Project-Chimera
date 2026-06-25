namespace ProjectChimera.Core
{
    /// <summary>
    /// Per-unit separation precedence in crowd steering (Story 1.13, DG-2 / FR-54). Parsed from
    /// <c>UnitDefinition.separation_priority</c> at spawn into <c>EntityWorld.SeparationPriorityOf</c> and read
    /// in-sim by <see cref="ProjectChimera.Navigation.MovementSystem"/> every tick: a <see cref="Push"/> unit is
    /// never displaced by a <see cref="Yield"/> neighbour it contacts (it skips that neighbour's contribution to
    /// its own separation; the yield unit is still pushed by the push unit). Every other combination
    /// (push/push, yield/yield, <see cref="Normal"/>/anything) separates symmetrically as before.
    ///
    /// FOLDED into <c>SimChecksum</c> (v5) — it is sim truth read on every peer, so a content divergence here
    /// must desync detectably. Therefore the integer member values are part of the hashed determinism contract:
    /// they are frozen and MUST NOT be reordered or renumbered later (the same back-compat freeze as the
    /// <c>UnitCommand</c> 0–5 values). <see cref="Normal"/> is the parsed default so existing/unauthored units
    /// keep their pre-1.13 symmetric separation.
    /// </summary>
    public enum SeparationPriority : byte
    {
        Yield  = 0, // Always gives way; pushed by a Push neighbour but never resists.
        Normal = 1, // Default — symmetric mutual separation (pre-1.13 behaviour).
        Push   = 2, // Holds its ground against a Yield neighbour (ignores its push).
    }
}
