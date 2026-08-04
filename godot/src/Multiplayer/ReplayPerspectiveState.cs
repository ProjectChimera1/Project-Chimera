#nullable enable

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// Story 9.11 / DW-431 — the replay playback VIEW-perspective state machine, extracted from
    /// <c>MainScene</c> (the <c>ModeTransitionResetPolicy</c> "pure decision + Godot shell" precedent) so the
    /// session transitions — begin at reveal-all, cycle through the roster, and the DW-431 end-of-session reset —
    /// are Godot-free and Tier-1 testable. Presentation-only: the perspective drives
    /// <c>Fog.SetViewer</c> / <c>FogBridge.RevealAll</c>, which are never folded into SimChecksum; MainScene owns
    /// those Godot side effects, this type owns the DECISION.
    /// </summary>
    public sealed class ReplayPerspectiveState
    {
        /// <summary>The "reveal-all" perspective sentinel — no single player's fog applied (the full-map view).</summary>
        public const int RevealAllPerspective = -1;

        /// <summary>-1 = reveal-all; 0..N-1 = the fog viewer is roster[Perspective].</summary>
        public int Perspective { get; private set; } = RevealAllPerspective;

        /// <summary>True when no single-player fog perspective is applied (the full-map view).</summary>
        public bool IsRevealAll => Perspective < 0;

        /// <summary>Reset to the session default (reveal-all) at the start of a replay playback session
        /// (called by <c>MainScene.BeginReplayPlaybackSession</c>).</summary>
        public void BeginSession() => Perspective = RevealAllPerspective;

        /// <summary>
        /// Cycle the perspective: reveal-all → roster[0] → … → roster[N-1] → reveal-all.
        /// <paramref name="rosterLength"/> ≤ 0 (no active replay) always lands on reveal-all — the pre-extraction
        /// <c>ReplayCyclePerspective</c> behavior, byte-identical. Returns the new perspective.
        /// </summary>
        public int Cycle(int rosterLength)
        {
            int next = Perspective + 1;
            if (next >= rosterLength) next = RevealAllPerspective; // wrap back to reveal-all
            Perspective = next;
            return next;
        }

        /// <summary>
        /// DW-431 — a replay session ended (natural finish at the final tick, or the return-to-Edit teardown):
        /// reset any cycled single-player perspective back to reveal-all, so the frozen final frame — and anything
        /// after — is never left under a stale player's fog-of-war. The caller restores the matching fog state
        /// (RevealAll / viewer) alongside; this records the decision.
        /// </summary>
        public void EndSession() => Perspective = RevealAllPerspective;
    }
}
