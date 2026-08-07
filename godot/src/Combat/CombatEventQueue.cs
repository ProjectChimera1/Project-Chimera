#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions; // CombatFeedbackProfile (presentation-only ref carried on the event — Story 2.7)

namespace ProjectChimera.Combat
{
    /// <summary>Types of combat feedback events emitted by simulation systems.</summary>
    public enum CombatEventType
    {
        MeleeHit,   // instant melee damage dealt
        RangedHit,  // projectile hit
        SplashHit,  // AoE detonation centre
        UnitKilled, // entity destroyed (any cause)
        // ── Story 2.7 (SD-3): appended AFTER UnitKilled. Presentation-only ability-cast feedback, pushed by
        // AbilityCastSystem on a committed cast and carrying the ability's CombatFeedbackProfile. Never folded —
        // CombatEventQueue is not an input to SimChecksum, so appending an enum value cannot move any golden. ──
        AbilityCast,
        // ── Story 2.9a: appended AFTER AbilityCast. Pushed when a building is razed by combat (melee or projectile
        // impact). Same not-folded property — appending is golden-safe. ──
        BuildingDestroyed,
        // ── Story 2.12 (AC4): appended AFTER BuildingDestroyed. Pushed by OrderApplier when a Shift-queued order is
        // rejected because the entity's order ring is already full (OrderQueueCount == MAX_ORDER_QUEUE). Presentation
        // feedback ONLY — the deterministic reject is the folded OrderQueueCount, so a null event sink (replay) still
        // rejects identically. Story 11.9 consumes this; same not-folded property → appending is golden-safe. ──
        OrderDenied,
        // ── Story 3.14 (hero death & revival): appended AFTER OrderDenied. HeroFell is pushed at DamageResolver.KillEntity
        // when a HERO entity dies (its fall is announced at the death position); HeroRevived is pushed by HeroXpSystem
        // when the countdown completes and the hero respawns at its revive building. Presentation-only — CombatEventQueue
        // is NOT a SimChecksum input, so appending enum values cannot move any golden. ──
        HeroFell,
        HeroRevived,
        // ── Story 3.15 (item & inventory): appended AFTER HeroRevived. ItemPickedUp when a hero claims a ground item into
        // a free slot; ItemUsed when a charged consumable fires; ItemDropped when a carried item returns to the ground
        // (manual drop or death). Presentation-only — CombatEventQueue is NOT a SimChecksum input, so appending enum
        // values cannot move any golden. The full-inventory reject reuses OrderDenied. ──
        ItemPickedUp,
        ItemUsed,
        ItemDropped,
        // ── Story 4.9 (research order path): appended AFTER ItemDropped. Pushed by ResearchSystem.Tick when an
        // in-progress research order completes, at the position it was started (BuildingStore position at issue time).
        // Presentation-only — CombatEventQueue is NOT a SimChecksum input, so appending an enum value cannot move any
        // golden. ──
        ResearchComplete,
        // ── Story 11.4 (FR-74, production-completion cue): appended AFTER ResearchComplete. Pushed by
        // BuildingSystem.SpawnTrainedUnit when a unit finishes training, at the training building's position and
        // carrying the building's Faction (so MatchAlertBridge fires the completion cue ONLY for the local player).
        // Presentation-only — CombatEventQueue is NOT a SimChecksum input, so appending an enum value cannot move any
        // golden (same property the appends above rely on). ──
        TrainingComplete,
        // ── Story 15.13 (DW-248): appended AFTER TrainingComplete. The three PRESENTATION leaves' cue types, pushed by
        // PlayVfxEffect / PlaySoundEffect / ShakeScreenEffect when a closed-vocabulary presentation leaf runs, each
        // carrying the ability's CombatFeedbackProfile. Rendered by CombatFeedbackBridge (PlayVfx flash / ShakeScreen
        // camera shake) and AudioManager (PlaySound). Presentation-only — CombatEventQueue is NOT a SimChecksum input,
        // so appending these enum values cannot move any golden (the same not-folded property the appends above rely
        // on). All three are IsAmbient (battle-volume juice, individually droppable). ──
        PlayVfx,
        PlaySound,
        ShakeScreen
    }

    /// <summary>
    /// Story 11.4 (FR-74) — the specific reason a Train / Build / Research / Ability-cast / Shop order was rejected,
    /// stamped by the SINGLE guard that rejected it (the guard-emits-reason contract) onto an
    /// <see cref="CombatEventType.OrderDenied"/> event. The reactive denial-feedback path (MatchAlertBridge) renders
    /// the reason; it NEVER re-derives it. <see cref="None"/> is the default (a denial with no specific reason, e.g.
    /// an order-ring-full reject).
    ///
    /// Presentation-domain only — carried on the non-folded <see cref="CombatEventQueue"/>, so appending members here
    /// can never move a golden (the same not-folded property the event-type appends rely on).
    /// </summary>
    public enum DenialReason : byte
    {
        None = 0,
        NeedOre,
        NeedCrystal,
        SupplyCapped,
        PrereqMissing,
        OnCooldown,
        NoEnergy,
        InvalidLocation,
        InvalidTarget,
        OutOfRange,
        InventoryFull,
        QueueFull,
        // Story 11.4 review (P5): a resource shortage that is neither specifically ore nor crystal (a sparse/custom
        // cost key, or an unregistered fail-closed key) — surfaced as a generic "not enough resources" rather than
        // fabricating an ore shortage.
        InsufficientResources,
        // DW-266: the caster carries StatusFlags.Silenced — active casting is barred while the debuff is live
        // (auras and while-alive self-passives keep running; silence stops ACTIVE casts only).
        Silenced,
        // DW-266: the caster carries StatusFlags.Stunned — fully incapacitated (no cast, no attack, no movement).
        Stunned
    }

    /// <summary>Lightweight event written by sim systems each tick.</summary>
    public struct CombatEvent
    {
        public CombatEventType Type;
        public FixedVec3       Position; // world position of the event

        /// <summary>
        /// Story 11.4 (FR-74) — the RELEVANT faction for this event: the VICTIM for a hit/kill/razed event (whose
        /// units/buildings were struck), the ACTOR for a denial/completion event (who issued the rejected order / owns
        /// the finished production). Stamped at the push site from state that push site already reads (FactionOf /
        /// building owner). MatchAlertBridge filters on it against <c>EffectiveLocalFaction</c> so an alert/cue fires
        /// ONLY for the local player. Defaults to <see cref="Faction.Neutral"/> for the legacy overloads that don't
        /// carry it. Presentation-domain — the simulation never reads it, and the queue is NOT a SimChecksum input.
        /// </summary>
        public Faction Faction;

        /// <summary>
        /// Story 11.4 (FR-74) — for an <see cref="CombatEventType.OrderDenied"/> event, the specific reason the guard
        /// computed when it rejected the order (single-truth: the reactive UI renders this, never re-derives it).
        /// <see cref="DenialReason.None"/> on every non-denial event and on a reason-less reject. Presentation-domain,
        /// excluded from the determinism hash by construction (the queue is not a SimChecksum input).
        /// </summary>
        public DenialReason Reason;

        /// <summary>
        /// Optional per-source feedback override (Story 2.7), resolved AT PUSH TIME because the source entity may be
        /// dead/recycled by the time the presentation bridge drains the queue (so its identity can't be looked up
        /// later). Null ⇒ the bridge/audio use the tuned event-type default. Presentation-domain reference — the
        /// simulation never reads it, and the queue is NOT an input to SimChecksum, so this field is excluded from
        /// the determinism hash by construction.
        /// </summary>
        public CombatFeedbackProfile? Feedback;
    }

    /// <summary>
    /// Sim-layer event buffer for combat feedback.
    ///
    /// Written by CombatSystem / ProjectileSystem / DamageResolver / AbilityCastSystem each simulation tick.
    /// Drained once per frame by CombatFeedbackBridge (which owns the single Clear()) and read again by AudioManager
    /// and MatchAlertBridge.
    ///
    /// <para><b>DW-469 — two-lane admission.</b> The buffer used to be a flat 256 slots that ANY push could fill and
    /// that silently dropped everything past the cap. Once Story 11.4 routed <see cref="CombatEventType.OrderDenied"/>
    /// / <see cref="CombatEventType.TrainingComplete"/> / <see cref="CombatEventType.ResearchComplete"/> onto the same
    /// queue, a single large battle tick (&gt;256 hit pushes) could starve those cues entirely — the player is simply
    /// never told their order was refused or their unit finished. The buffer is now split into two lanes by
    /// <see cref="IsAmbient"/>: the high-volume BATTLE cues may fill at most <see cref="MAX_AMBIENT_EVENTS"/> slots
    /// (unchanged from the old flat cap, so battle-cue admission is behaviourally identical to before), while the
    /// low-volume NOTIFICATION cues may additionally use the <see cref="PRIORITY_RESERVE"/> slots beyond it. Because an
    /// ambient push can never raise the count past <see cref="MAX_AMBIENT_EVENTS"/>, at least
    /// <see cref="PRIORITY_RESERVE"/> slots are ALWAYS free for notification cues no matter how big the fight.</para>
    ///
    /// <para>Pure C# — no Godot dependency. Never folded into SimChecksum (it is not a Compute input), and no sim
    /// system reads <see cref="Count"/>/<see cref="Get"/> — only the three presentation bridges do — so changing what
    /// this queue admits cannot move a checksum, a golden, or a replay.</para>
    /// </summary>
    public class CombatEventQueue
    {
        /// <summary>
        /// DW-469 — slots the high-volume BATTLE cues (<see cref="IsAmbient"/>) may fill. Deliberately the old flat
        /// capacity: ambient admission is unchanged by the two-lane split, so no battle visual/audio regressed.
        /// </summary>
        public const int MAX_AMBIENT_EVENTS = 256;

        /// <summary>
        /// DW-469 — slots reserved BEYOND <see cref="MAX_AMBIENT_EVENTS"/> that only NOTIFICATION cues (denials,
        /// production/research completions, hero fall/revive, item pickup/use/drop) can occupy. Sized for the
        /// player-driven event rate, which is bounded by clicks and production timers rather than by army size: a
        /// frame carrying more than 64 of these does not occur in play, whereas hit pushes routinely exceed 256.
        /// The extra slots are near-free on the drain side — the two heavy per-frame consumers (CombatFeedbackBridge's
        /// flash pool, AudioManager's voice pool) have no case for any notification type and skip them.
        /// </summary>
        public const int PRIORITY_RESERVE = 64;

        /// <summary>DW-469 — total ring capacity: the ambient ceiling plus the notification reserve.</summary>
        public const int MAX_EVENTS = MAX_AMBIENT_EVENTS + PRIORITY_RESERVE;

        private readonly CombatEvent[] _buf = new CombatEvent[MAX_EVENTS];
        private int _count;

        public int Count => _count;

        /// <summary>Returns the event at index <paramref name="i"/>. No bounds checking.</summary>
        public CombatEvent Get(int i) => _buf[i];

        /// <summary>
        /// DW-469 — true for the BATTLE-VOLUME cue types: the per-swing / per-impact / per-death / per-cast events a
        /// large fight emits by the hundreds in a single tick, and whose individual loss is imperceptible (the drain
        /// side pools only 48 flashes and a handful of audio voices per frame anyway). These are the only types the
        /// <see cref="MAX_AMBIENT_EVENTS"/> ceiling applies to; every other type is a low-volume NOTIFICATION cue that
        /// may also use <see cref="PRIORITY_RESERVE"/>.
        ///
        /// <para>Deliberately a closed list of the SPAMMY types with a fail-safe default: a newly appended
        /// <see cref="CombatEventType"/> that nobody classifies here is treated as a notification, so forgetting this
        /// switch over-protects a cue instead of silently starving it (the safe direction of the
        /// closed-enum-touch-site hazard).</para>
        /// </summary>
        public static bool IsAmbient(CombatEventType type) => type switch
        {
            CombatEventType.MeleeHit          => true,
            CombatEventType.RangedHit         => true,
            CombatEventType.SplashHit         => true,
            CombatEventType.UnitKilled        => true,
            CombatEventType.BuildingDestroyed => true,
            CombatEventType.AbilityCast       => true,
            // Story 15.13 (DW-248): the three presentation-leaf cues are battle-volume juice (a cast can spam them), and
            // an individual dropped flash/sound/shake is imperceptible — so they ride the ambient lane and must NOT draw
            // on the notification reserve (which is sized for the low-volume click/production cues).
            CombatEventType.PlayVfx           => true,
            CombatEventType.PlaySound         => true,
            CombatEventType.ShakeScreen       => true,
            _                                 => false
        };

        /// <summary>
        /// DW-469 — the single admission gate both push paths route through: an ambient cue is admitted only while the
        /// buffer is below <see cref="MAX_AMBIENT_EVENTS"/>, a notification cue while it is below
        /// <see cref="MAX_EVENTS"/>. One rule, so the reserve cannot be honoured on one push overload and lost on
        /// another.
        /// </summary>
        private bool HasRoomFor(CombatEventType type)
            => _count < (IsAmbient(type) ? MAX_AMBIENT_EVENTS : MAX_EVENTS);

        /// <summary>Appends an event with no feedback override (today's default look). Drops if its lane is full.</summary>
        public void Push(CombatEventType type, FixedVec3 position)
            => Push(type, position, Faction.Neutral, null);

        /// <summary>
        /// Appends an event carrying an optional presentation-only feedback override (Story 2.7). Resolve
        /// <paramref name="feedback"/> at the push site while the source is still alive — the bridge cannot recover
        /// source identity at drain time. Drops if its lane is full (non-critical visual).
        /// </summary>
        public void Push(CombatEventType type, FixedVec3 position, CombatFeedbackProfile? feedback)
            => Push(type, position, Faction.Neutral, feedback);

        /// <summary>
        /// Story 11.4 (FR-74) — appends an event stamping the RELEVANT <paramref name="faction"/> (victim for a hit/
        /// kill/razed; actor for a completion), optionally carrying the presentation-only feedback override. Called by
        /// the hit/kill/razed/completion push sites, which already read that faction. Drops if the event's lane is
        /// full (DW-469): an ambient battle cue past <see cref="MAX_AMBIENT_EVENTS"/>, a notification cue only past
        /// the whole <see cref="MAX_EVENTS"/> ring.
        /// </summary>
        public void Push(CombatEventType type, FixedVec3 position, Faction faction, CombatFeedbackProfile? feedback = null)
        {
            if (HasRoomFor(type))
                _buf[_count++] = new CombatEvent
                {
                    Type = type, Position = position, Faction = faction, Reason = DenialReason.None, Feedback = feedback
                };
        }

        /// <summary>
        /// Story 11.4 (FR-74) — appends a guard-sourced <see cref="CombatEventType.OrderDenied"/> event carrying the
        /// specific <paramref name="reason"/> the rejecting guard computed plus the acting <paramref name="faction"/>.
        /// This is the ONLY place the denial reason is authored (single-truth); the reactive UI renders it and never
        /// re-derives it. A denial is a NOTIFICATION cue (DW-469), so it draws on <see cref="PRIORITY_RESERVE"/> and
        /// survives a battle tick that saturates the ambient lane; it drops only once the whole ring is full.
        /// </summary>
        public void PushDenied(FixedVec3 position, Faction faction, DenialReason reason)
        {
            if (HasRoomFor(CombatEventType.OrderDenied))
                _buf[_count++] = new CombatEvent
                {
                    Type = CombatEventType.OrderDenied, Position = position, Faction = faction, Reason = reason, Feedback = null
                };
        }

        /// <summary>Resets the buffer so the next frame starts fresh.</summary>
        public void Clear() => _count = 0;
    }
}
