#nullable enable

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// The reset action an Edit↔Play mode transition should take. Extracted from
    /// <see cref="Phases.WinConditionPhase"/>'s <c>ModeChanged</c> handler (Story 3.10) so the highest-blast-radius
    /// routing decision — clearing and re-seeding the world — is Godot-free and Tier-1 testable (DW-22).
    /// </summary>
    public enum ModeResetAction
    {
        /// <summary>
        /// Online match or active replay entering Play: the sim is already live, so re-applying the authored start
        /// would re-apply mid-online-match (lockstep desync) or clobber the replay's restored RNG seed. Do nothing.
        /// </summary>
        None,

        /// <summary>
        /// Offline editor playtest loop, in BOTH directions: signals the caller to clear the world and re-apply the
        /// authored board (the downstream <c>ResetToAuthoredStart</c> is what re-seeds <c>DEFAULT_RNG_SEED</c> — this
        /// predicate only routes the decision). Returned only when <c>!isOnline &amp;&amp; !hasReplay</c>.
        /// </summary>
        AuthoredStart,

        /// <summary>
        /// Online match or replay returning to Edit: the pre-3.10 lifecycle-only reset (via
        /// <c>ResetMatchOnReturnToEdit</c>) — no destructive authored-start re-apply.
        /// </summary>
        Lifecycle,
    }

    /// <summary>
    /// Pure, Godot-free decision for how a mode transition should reset the world. Single source of truth for the
    /// <see cref="Phases.WinConditionPhase"/> <c>ModeChanged</c> routing (DW-22). This routes only the DECISION: the
    /// value it returns is identical to the inline guard it replaced (<c>Decide(...) == AuthoredStart</c> ⟺ the old
    /// <c>offlineEditorLoop = !isOnline &amp;&amp; !hasReplay</c>), so the destructive <c>ResetToAuthoredStart</c> the
    /// caller gates on <c>AuthoredStart</c> fires ONLY for the offline editor loop — an online match can never desync
    /// and a replay's restored seed is never clobbered. (The caller's dispatch on that value stays in the Godot-coupled
    /// handler and is covered by the in-engine gate, not this Tier-1 predicate.)
    /// </summary>
    public static class ModeTransitionResetPolicy
    {
        /// <summary>
        /// Decide the reset action for a mode transition.
        /// </summary>
        /// <param name="isOnline">Whether a lockstep online match is active (<c>Lockstep.IsOnline</c>).</param>
        /// <param name="hasReplay">Whether a replay is loaded (<c>ReplayPlayer != null</c>).</param>
        /// <param name="targetIsPlay">Whether the transition target mode is Play (<c>mode == (int)GameMode.Play</c>).</param>
        /// <returns>
        /// <see cref="ModeResetAction.AuthoredStart"/> for the offline editor loop (both directions);
        /// <see cref="ModeResetAction.None"/> for online/replay entering Play;
        /// <see cref="ModeResetAction.Lifecycle"/> for online/replay returning to Edit.
        /// </returns>
        public static ModeResetAction Decide(bool isOnline, bool hasReplay, bool targetIsPlay)
        {
            bool offlineEditorLoop = !isOnline && !hasReplay;
            if (offlineEditorLoop) return ModeResetAction.AuthoredStart; // clear + re-apply authored board, both directions
            return targetIsPlay ? ModeResetAction.None       // online/replay → Play: never re-apply
                                : ModeResetAction.Lifecycle; // online/replay → Edit: lifecycle-only reset
        }
    }
}
