#nullable enable
using System;
using Godot;
using ProjectChimera.Core.Definitions;
using ProjectChimera.UGC;

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

            // victory → game-over overlay (presentation-output only) + Story 9.8 proof-of-play mint. The mint is a
            // presentation-side POST-MATCH side effect: it reads only the model already held by the context
            // (ctx.Scenario) and the local-faction identity (never sim tick state, per the C3 rule), so it touches
            // neither the sim loop nor SimChecksum nor CanonicalModelHash.AlgoVersion.
            ctx.ScenarioDirector.OnVictory = winnerSlot =>
            {
                ctx.Scene.ShowGameOver(winnerSlot + 1);
                TryMintProofOfPlay(ctx, winnerSlot);
            };
        }

        /// <summary>
        /// Story 9.8 — mint + persist a signed proof-of-play token, but ONLY when the winning slot is the LOCAL
        /// faction (a loss, or another faction's win, mints nothing). The token binds to the CANONICAL model identity
        /// via <see cref="CanonicalModelHash.Compute"/> (the full 64-bit value, not the wire fold) so any later content
        /// edit re-derives to a mismatch and the publish gate treats it as stale. Fail-soft: any provisioning / IO
        /// error is logged and swallowed — a mint failure must never crash the game-over path.
        /// </summary>
        private static void TryMintProofOfPlay(SceneContext ctx, int winnerSlot)
        {
            if (ctx.Scenario == null) return;

            try
            {
                // Mint only on a LOCAL-faction win. Review P1: Lockstep may be null at victory (e.g. offline paths that
                // never reset it) — the repo-wide null-safe pattern (CameraPhase/MinimapPhase/MainScene) defaults to
                // Player1 so this can never throw out from under the game-over path.
                Faction localFaction = ctx.Lockstep?.EffectiveLocalFaction ?? Faction.Player1;
                if (!ProofOfPlayMint.ShouldMint(winnerSlot, localFaction)) return;

                // Review P3: resolve/provision the signing key WITHOUT ever rotating a corrupt existing one — that would
                // invalidate every previously-minted token. A corrupt key ⇒ skip this mint, leaving the stored value
                // intact.
                SigningKeyStatus keyStatus = ProofOfPlayMint.GetOrProvisionSigningKey(ctx.SecretStore, out byte[] key);
                if (keyStatus == SigningKeyStatus.CorruptExisting)
                {
                    GD.PrintErr("[ProofOfPlay] Existing signing key is unreadable — skipping mint (key left intact, " +
                                "not rotated, so prior tokens still verify).");
                    return;
                }

                ulong  hash = CanonicalModelHash.Compute(ctx.Scenario);
                string scenarioId = ProofOfPlayMint.ResolveScenarioId(ctx.Scenario);

                // Wall-clock read is presentation-side / off the sim tick path — the sanctioned RS0030 exemption
                // pattern (see ContentPackageManifest.CreatedAt).
#pragma warning disable RS0030
                string mintedAt = DateTime.UtcNow.ToString("o");
#pragma warning restore RS0030

                ProofOfPlayToken token = ProofOfPlaySigner.Create(hash, PublishGate.WinOutcome, mintedAt, scenarioId, key);

                var store = new ProofOfPlayStore(ProjectSettings.GlobalizePath(ProofOfPlayMint.TokenDirGodotPath));
                store.Save(scenarioId, token);
                GD.Print($"[ProofOfPlay] Minted win token for '{scenarioId}' (hash {token.ScenarioHash}).");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ProofOfPlay] Mint failed: {ex.Message}");
            }
        }
    }
}
