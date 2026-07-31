#nullable enable
using ProjectChimera.Core.Definitions;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// DW-229 — the pure per-apply reset step of <see cref="SlotFactionResolver"/>. Restores every slot's
    /// faction def to its <c>_Ready</c>-seeded default IN PLACE, so the subsequent per-slot re-resolution starts
    /// from a clean baseline: a slot whose <c>faction_json</c> was cleared or repointed since boot reverts to its
    /// default instead of keeping a stale def, and re-resolution is idempotent across many Edit↔Play re-applies.
    ///
    /// <para>Godot-free by design (touches only the <see cref="FactionDefinition"/> array), so it is globbed into
    /// the Tier-1 assembly and the reset/revert-to-seeded semantics stay unit-testable — <see cref="SlotFactionResolver"/>
    /// itself is Godot-coupled (<c>ProjectSettings.GlobalizePath</c> / <c>GD.PrintErr</c>) and lives under
    /// <c>Bootstrap/Phases/</c>, outside this assembly.</para>
    /// </summary>
    public static class SlotFactionReset
    {
        /// <summary>
        /// Copy <paramref name="seededDefaults"/> into <paramref name="slotDefs"/> element-by-element. The array is
        /// mutated IN PLACE — it is aliased by the applier, the SceneContext, and MainScene and must NEVER be
        /// reassigned (mirrors <c>ScenarioLoadPhase.RestoreSlotFactionDefs</c>). Both arrays are the same length by
        /// construction (the defaults are a <c>_Ready</c>-time clone of the live array).
        /// </summary>
        public static void ToSeeded(FactionDefinition?[] slotDefs, FactionDefinition?[] seededDefaults)
        {
            for (int i = 0; i < slotDefs.Length; i++)
                slotDefs[i] = seededDefaults[i];
        }
    }
}
