#nullable enable

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 1.8c Task 4 (C3) — the SINGLE assignment site for the four <c>ScenarioDirector.On*</c> presentation
    /// delegates. C3 rule: a sim node may *fire* an <c>On*</c> delegate, but the body may never read/write sim
    /// state — these are presentation-output channels. <c>OnSpawnUnit</c> is the one legitimate exception that
    /// calls back into the Godot-free sim writer (<c>ScenarioApplier.SpawnUnit</c>, sim→sim) — that re-point is
    /// correct and unchanged. Replaces the inline assignments formerly in <c>MainScene.SetupTriggerEditor</c>.
    /// </summary>
    public static class ScenarioDelegateBinder
    {
        /// <summary>The per-unit lateral spawn offset, 2.5 world units = 163840 raw in 16.16 (Story 7.1). A named
        /// <see cref="Fixed"/> constant so the trigger spawn fan-out stays Fixed-only — no in-tick float offset.</summary>
        private static readonly Fixed SpawnLateralOffset = Fixed.FromRaw(163840); // 2.5

        /// <summary>Wire all four ScenarioDirector On* delegates from the context (called by TriggerEditorPhase).</summary>
        public static void Bind(SceneContext ctx)
        {
            // spawn_unit → the Godot-free applier (sim→sim; the one On* that legitimately writes sim truth).
            // x/z arrive as Fixed; route through the Fixed-native SpawnUnitAt with a Fixed lateral offset so no
            // Fixed.FromFloat and no float arithmetic runs on this path (Story 7.1).
            ctx.ScenarioDirector.OnSpawnUnit = (unitId, slot, x, z, count) =>
            {
                var faction    = (Faction)(slot + 1);
                int fIdx       = (int)faction;
                var factionDef = (fIdx >= 0 && fIdx < ctx.SlotFactionDefs.Length)
                    ? ctx.SlotFactionDefs[fIdx] : ctx.FactionDef;
                var def = factionDef?.GetUnit(unitId);
                if (def == null)
                {
                    ctx.Log.Warn($"[ScenarioDirector] spawn_unit: unknown unit_id '{unitId}' for slot {slot}.");
                    return;
                }
                for (int i = 0; i < count; i++)
                    ctx.Applier.SpawnUnitAt(def, faction, x + Fixed.FromInt(i) * SpawnLateralOffset, z);
            };

            // display_message → HUD toast (presentation-output only). Convert Fixed→float at THIS presentation
            // boundary (never in the tick): duration seconds feed the HUD toast timer.
            ctx.ScenarioDirector.OnDisplayMessage = (text, dur) => ctx.Scene.ShowTriggerMessage(text, dur.ToFloat());

            // play_sound → audio (presentation-output only).
            ctx.ScenarioDirector.OnPlaySound = _ => ctx.AudioMgr?.PlayBuildingPlaced();

            // victory → game-over overlay (presentation-output only).
            ctx.ScenarioDirector.OnVictory = winnerSlot => ctx.Scene.ShowGameOver(winnerSlot + 1);
        }
    }
}
