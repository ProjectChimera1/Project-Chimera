#nullable enable
namespace ProjectChimera.Core
{
    /// <summary>
    /// Story 9.5 — the single rule that resolves "which faction is the LOCAL player" for the presentation layer.
    ///
    /// <para>Offline OR spectator ⇒ <see cref="Faction.Player1"/> (the reference / local viewer); otherwise the
    /// server-assigned online faction. This mirrors the convention hardcoded in
    /// <c>LockstepManager.EnqueueDslEvent</c>: <c>LockstepManager.LocalFaction</c> is mutated by
    /// <c>GoOnline</c>/<c>GoSpectate</c> and is NOT reset by <c>GoOffline</c>, so reading it raw offline would leak a
    /// stale <c>Player2</c>/<c>Neutral</c> from a prior online/spectate match in the same process — making a subsequent
    /// offline F5 playtest select nothing, mis-color the minimap, and fog the wrong faction. Gating on
    /// <c>isOnline &amp;&amp; !isSpectator</c> clamps offline/spectate back to <see cref="Faction.Player1"/> WITHOUT
    /// depending on <c>LocalFaction</c> being reset — but it DOES depend on the online FLAGS being accurate, so the
    /// return-to-Edit seam (<c>MainScene.ResetMatchOnReturnToEdit</c>) calls <c>GoOffline()</c> to clear
    /// <c>IsOnline</c>/<c>IsSpectator</c> after a match; otherwise those flags (like <c>LocalFaction</c>) would persist
    /// across the same-process Edit↔Play boundary and this clamp would never engage for the offline-after-online case.</para>
    ///
    /// Godot-free, pure, Tier-1 (covered by the <c>src/Core/**</c> glob).
    /// </summary>
    public static class LocalFactionPolicy
    {
        /// <summary>
        /// The effective local faction for presentation. <paramref name="localFaction"/> is honoured ONLY for an
        /// online, non-spectating client; offline or spectator resolves to <see cref="Faction.Player1"/>.
        /// </summary>
        public static Faction Effective(bool isOnline, bool isSpectator, Faction localFaction)
            => (isOnline && !isSpectator) ? localFaction : Faction.Player1;
    }
}
