#nullable enable
using Godot;
using ProjectChimera.Core.Definitions;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// DW-229 — the ONE shared per-slot faction-def resolution path, called by BOTH the boot
    /// <see cref="Phases.ScenarioLoadPhase"/> and the in-place Edit↔Play re-apply
    /// (<c>MainScene.ResetToAuthoredStart</c>). It first <b>resets every slot to its <c>_Ready</c>-seeded default</b>
    /// (via the Godot-free <see cref="SlotFactionReset"/>), then re-resolves each player slot's res:// <c>faction_json</c>
    /// onto the shared array. The per-apply reset is what makes a cleared or repointed <c>faction_json</c> take effect
    /// without a full scene reload, and closes the "keeps a stale def on clear" gap in the former inline resolver.
    ///
    /// <para>Godot-coupled by design — it owns the sole <c>ProjectSettings.GlobalizePath</c> on the scenario-apply
    /// path plus the <c>GD.PrintErr</c> diagnostics — so it lives under <c>Bootstrap/Phases/</c> (excluded from the
    /// Godot-free Tier-1 assembly, like the concrete setup phases and <c>SceneContext</c>). Only its pure reset step
    /// is factored out into <see cref="SlotFactionReset"/> for Tier-1 coverage.</para>
    /// </summary>
    public static class SlotFactionResolver
    {
        /// <summary>
        /// Reset <paramref name="slotDefs"/> to <paramref name="seededDefaults"/> in place, then resolve each of
        /// <paramref name="scenario"/>'s player slots' <c>faction_json</c> onto it. The array is mutated IN PLACE
        /// (it is aliased by the applier / <c>SceneContext</c> / MainScene — never reassign it). A slot with an empty
        /// <c>faction_json</c>, an out-of-range faction index, or a missing file keeps its seeded default (the reset
        /// prologue already restored it) — only an existing file overwrites the slot.
        /// </summary>
        public static void Resolve(
            ScenarioData scenario,
            FactionDefinition?[] slotDefs,
            FactionDefinition?[] seededDefaults,
            AbilityRegistry abilityRegistry)
        {
            // Per-apply reset (DW-229): restore every slot to its _Ready-seeded default BEFORE re-resolving, so a
            // faction_json cleared/repointed since boot can never leave a stale def. First boot is a no-op reset
            // (the array already equals the seeded defaults), so this path stays byte-identical for an unchanged scenario.
            SlotFactionReset.ToSeeded(slotDefs, seededDefaults);

            foreach (var slot in scenario.PlayerSlots ?? System.Array.Empty<ScenarioPlayerSlot>())
            {
                if (string.IsNullOrEmpty(slot.FactionJson)) continue; // empty ⇒ keep the just-restored seeded default
                var faction = FactionRegistry.ToFaction(slot.Slot); // resolved via the one canonical cast site
                if ((int)faction < 0 || (int)faction >= slotDefs.Length) continue; // bounds guard (mirrors MainScene:2673)
                string abs = ProjectSettings.GlobalizePath(slot.FactionJson);
                if (System.IO.File.Exists(abs))
                {
                    var def = FactionDefinition.LoadFromFile(abs);
                    // Story 2.4b: back-fill this slot's freshly-loaded faction defs' ability ids → registry indices
                    // BEFORE the applier spawns its units (ApplyUnitDefinition reads UnitDefinition.AbilityIndices,
                    // empty until ResolveAbilities runs). Idempotent + drops unknown ids.
                    foreach (var u in def.Units) u.ResolveAbilities(abilityRegistry);
                    // Story 2.11 (AC2): closed-set tag validation — drop any unit carrying an unknown tag (fail-closed,
                    // located error). Runs on BOTH legs (here + ServerBootstrap) so client/server stay in parity before
                    // any SpawnUnit; a dropped unit → GetUnit null → the applier's def==null skip → no EntityWorld slot.
                    foreach (string err in UnitTagValidator.ValidateAndDropUnits(def))
                        GD.PrintErr($"[UnitTagValidator] {err} (unit dropped)");
                    // Story 5.7 (FR-19/UX-DR80, DW-97 match-load closure): non-blocking roster-completeness diagnostic,
                    // AFTER tag-drop so it reflects the roster that will actually spawn. Never blocks the load; just
                    // surfaces a located error if the roster fails ValidateComplete (e.g. missing Worker role or a
                    // blank mesh_path) so it isn't a silent unplayable match start.
                    // DW-327: threaded with the SAME abilityRegistry the discovery scan
                    // (FactionDefinition.LoadSelectableFromDirectory) and the Edit→Play launch gate (FactionLaunchGate)
                    // now use, so the signature_mechanic_effect_id resolution check is no longer dormant on this leg —
                    // a dangling signature id is reported at match-load instead of surfacing for the first time as a
                    // hard Play veto. Still non-blocking here by design (diagnostic, not a gate).
                    FactionValidationResult completeResult = FactionValidator.ValidateComplete(def, abilityRegistry);
                    if (!completeResult.Ok)
                        foreach ((string _, string message) in completeResult.Errors)
                            GD.PrintErr($"[FactionValidator] slot {slot.Slot} ({abs}): {message}");
                    slotDefs[(int)faction] = def; // only assign when the file exists (the reset handles empty/missing)
                }
            }
        }
    }
}
