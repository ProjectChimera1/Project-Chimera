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
        /// <summary>Wire all four ScenarioDirector On* delegates from the context (called by TriggerEditorPhase).</summary>
        public static void Bind(SceneContext ctx)
        {
            // spawn_unit → the Godot-free applier (sim→sim; the one On* that legitimately writes sim truth).
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
                    ctx.Applier.SpawnUnit(def, faction, x + i * 2.5f, z);
            };

            // display_message → HUD toast (presentation-output only).
            ctx.ScenarioDirector.OnDisplayMessage = ctx.Scene.ShowTriggerMessage;

            // play_sound → audio (presentation-output only).
            ctx.ScenarioDirector.OnPlaySound = _ => ctx.AudioMgr?.PlayBuildingPlaced();

            // victory → game-over overlay (presentation-output only).
            ctx.ScenarioDirector.OnVictory = winnerSlot => ctx.Scene.ShowGameOver(winnerSlot + 1);
        }
    }
}
