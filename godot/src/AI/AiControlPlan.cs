#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;

namespace ProjectChimera.AI
{
    /// <summary>
    /// DW-908 — WHICH factions an AI drives in THIS match, as a per-match value instead of the hardcoded
    /// <c>AiOpponentSystem.AI_FACTION</c> constant.
    ///
    /// <para><b>The defect this exists for.</b> The AI was bound to a CONSTANT (<c>Faction.Player2</c>), not to who
    /// OCCUPIES the slot. Offline that pairing happens to hold — <c>SkirmishSetupToScenario.ActiveSlotsInLaunchOrder</c>
    /// sorts the single Human to launch index 0 (⇒ Player1) and the AI to index 1 (⇒ Player2). Online it breaks
    /// completely: <c>AssignedRoster</c> seats peers by ARRIVAL ORDER with no Human/AI concept, so slot 1 is a HUMAN
    /// who is also Player2 — and the AI co-piloted them. Two consequences, both observed live on 2026-08-07/08:
    /// a joining player shared control of their own faction with an AI, and that AI (the float scorer of DW-204)
    /// desynced the FR-39 two-machine match at tick 660 after 10 clean windows.</para>
    ///
    /// <para><b>The rule, stated once.</b> An AI controls a faction iff the launch path MARKED that faction as
    /// AI-driven AND no human occupies it. That is deliberately NOT "switch the AI off online": an AI filling a
    /// genuinely VACANT slot should still play it (the 2-humans-plus-1-AI case). Today's online lobby has no AI-slot
    /// concept at all, so the marked set is empty online and the plan resolves to <see cref="None"/> — the same
    /// immediate outcome as an off-switch, derived from the right rule so it extends instead of blocking.</para>
    ///
    /// <para><b>Fail-closed.</b> <see cref="Mask"/> is folded into <c>MatchAgreementHash</c> (which bumped 3→4 for
    /// this), so two peers that disagree about who the AI controls are REJECTED by <c>HandshakeGate.CheckStart</c>
    /// before tick 0 instead of desyncing 600 ticks later. The plan the hash folds and the plan pushed into the sim
    /// are the SAME stored value (<c>SceneContext.OnlineAiPlan</c>, resolved once at load) — there is no second
    /// derivation that could drift from the folded one.</para>
    ///
    /// <para>Godot-free (<c>src/AI/**</c> is globbed into the Tier-1 sim assembly by <c>SimSources.props</c>); an int
    /// bitmask over <see cref="Faction"/> ordinals — no collection is stored, so <see cref="Controls"/> is allocation-
    /// free on the tick path and the value is trivially foldable into a hash.</para>
    /// </summary>
    public readonly struct AiControlPlan
    {
        /// <summary>The AI-controlled faction set as a bitmask over <see cref="Faction"/> ordinals
        /// (bit <c>(int)Faction.PlayerN</c>). 0 = no faction is AI-driven this match. This is the value folded into
        /// <c>MatchAgreementHash</c>, so it is deliberately a plain int with a stable, order-free encoding.</summary>
        public readonly int Mask;

        private AiControlPlan(int mask) { Mask = mask; }

        /// <summary>No faction is AI-driven — every active slot is human-occupied. The resolved plan for every
        /// online match today (the lobby seats every slot with a connected peer and marks none as AI).</summary>
        public static readonly AiControlPlan None = new(0);

        /// <summary>
        /// The OFFLINE skirmish pairing as it has always run: the AI drives <see cref="Faction.Player2"/>.
        ///
        /// <para>This is the construction-time default of <see cref="AiOpponentSystem"/>, so every golden scenario,
        /// every Tier-1 fixture and every offline playtest behaves EXACTLY as before this change — no golden moves.
        /// Deriving the offline set from the setup screen's <c>SlotKind.Ai</c> rows (so a 3rd AI player, or an AI on
        /// a slot other than Player2, works) is DW-908 leg (1) and is a separate story: it needs
        /// <see cref="AiOpponentSystem"/> to stop being a singleton keyed to one faction.</para>
        /// </summary>
        public static readonly AiControlPlan OfflineDefault = new(1 << (int)Faction.Player2);

        /// <summary>Build a plan from an explicit faction list (test fixtures + the leg-(1) launch path).
        /// <see cref="Faction.Neutral"/> is ignored — it is never a playable slot.</summary>
        public static AiControlPlan Of(params Faction[] factions)
        {
            int mask = 0;
            if (factions != null)
                foreach (Faction f in factions)
                    if (f != Faction.Neutral) mask |= 1 << (int)f;
            return new AiControlPlan(mask);
        }

        /// <summary>
        /// THE RULE: an AI controls a faction iff the launch path marked it AI-driven AND no human occupies it.
        ///
        /// <para><paramref name="markedAi"/> is the launch path's AI-slot set — offline the setup screen's
        /// <c>SlotKind.Ai</c> rows, online whatever the lobby marks as an AI seat (nothing today, so an online call
        /// passes an empty set and this returns <see cref="None"/>). <paramref name="humanOccupied"/> is who is
        /// actually sitting in a slot — online the assigned roster's seated peers.</para>
        ///
        /// <para>Human occupancy always WINS: a faction in both sets is human-driven. That single subtraction is the
        /// whole DW-908 fix — the old code consulted neither set.</para>
        /// </summary>
        public static AiControlPlan Resolve(IEnumerable<Faction>? markedAi, IEnumerable<Faction>? humanOccupied)
        {
            int mask = 0;
            if (markedAi != null)
                foreach (Faction f in markedAi)
                    if (f != Faction.Neutral) mask |= 1 << (int)f;

            if (humanOccupied != null)
                foreach (Faction f in humanOccupied)
                    mask &= ~(1 << (int)f); // a human in the seat always wins

            return new AiControlPlan(mask);
        }

        /// <summary>
        /// The plan for an ONLINE match: <see cref="Resolve"/> over the lobby's AI-marked seats minus the seated
        /// peers. Named separately from <see cref="Resolve"/> so the ONE online call site reads as a decision rather
        /// than as generic set arithmetic, and so the "no AI seats exist online yet" fact has a documented home.
        ///
        /// <para>Today every caller passes an empty <paramref name="aiMarkedFactions"/> (the online lobby has no
        /// AI-slot concept — <c>AssignedRoster</c> models arrival order only), so this returns <see cref="None"/>
        /// unconditionally. That is the correct behaviour and not a stub: when the lobby learns to seat an AI
        /// (DW-908 leg 1 / leg 3), passing that set here is the entire wiring change.</para>
        /// </summary>
        public static AiControlPlan ForOnlineMatch(IEnumerable<Faction>? aiMarkedFactions,
                                                   IEnumerable<Faction>? humanOccupiedFactions)
            => Resolve(aiMarkedFactions, humanOccupiedFactions);

        /// <summary>True when <paramref name="faction"/> is driven by an AI this match. Allocation-free — this is
        /// called on the sim tick path.</summary>
        public bool Controls(Faction faction) => (Mask & (1 << (int)faction)) != 0;

        /// <summary>True when ANY faction is AI-driven this match (the cheap "is the AI live at all" test).</summary>
        public bool Any => Mask != 0;

        public override string ToString() => Mask == 0 ? "AiControlPlan(none)" : $"AiControlPlan(mask=0x{Mask:X2})";
    }
}
