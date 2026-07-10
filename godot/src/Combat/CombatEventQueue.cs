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
        ResearchComplete
    }

    /// <summary>Lightweight event written by sim systems each tick.</summary>
    public struct CombatEvent
    {
        public CombatEventType Type;
        public FixedVec3       Position; // world position of the event

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
    /// Drained once per frame by CombatFeedbackBridge (which owns the single Clear()) and read again by AudioManager.
    ///
    /// Pure C# — no Godot dependency. Never folded into SimChecksum (it is not a Compute input).
    /// </summary>
    public class CombatEventQueue
    {
        private const int MAX_EVENTS = 256;

        private readonly CombatEvent[] _buf = new CombatEvent[MAX_EVENTS];
        private int _count;

        public int Count => _count;

        /// <summary>Returns the event at index <paramref name="i"/>. No bounds checking.</summary>
        public CombatEvent Get(int i) => _buf[i];

        /// <summary>Appends an event with no feedback override (today's default look). Silently drops if full.</summary>
        public void Push(CombatEventType type, FixedVec3 position)
            => Push(type, position, null);

        /// <summary>
        /// Appends an event carrying an optional presentation-only feedback override (Story 2.7). Resolve
        /// <paramref name="feedback"/> at the push site while the source is still alive — the bridge cannot recover
        /// source identity at drain time. Silently drops if the buffer is full (non-critical visual).
        /// </summary>
        public void Push(CombatEventType type, FixedVec3 position, CombatFeedbackProfile? feedback)
        {
            if (_count < MAX_EVENTS)
                _buf[_count++] = new CombatEvent { Type = type, Position = position, Feedback = feedback };
        }

        /// <summary>Resets the buffer so the next frame starts fresh.</summary>
        public void Clear() => _count = 0;
    }
}
