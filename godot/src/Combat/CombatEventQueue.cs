using ProjectChimera.Core;

namespace ProjectChimera.Combat
{
    /// <summary>Types of combat feedback events emitted by simulation systems.</summary>
    public enum CombatEventType
    {
        MeleeHit,   // instant melee damage dealt
        RangedHit,  // projectile hit
        SplashHit,  // AoE detonation centre
        UnitKilled  // entity destroyed (any cause)
    }

    /// <summary>Lightweight event written by sim systems each tick.</summary>
    public struct CombatEvent
    {
        public CombatEventType Type;
        public FixedVec3       Position; // world position of the event
    }

    /// <summary>
    /// Sim-layer ring buffer for combat feedback events.
    ///
    /// Written by CombatSystem / ProjectileSystem each simulation tick.
    /// Drained once per frame by CombatFeedbackBridge, then cleared.
    ///
    /// Pure C# — no Godot dependency.
    /// </summary>
    public class CombatEventQueue
    {
        private const int MAX_EVENTS = 256;

        private readonly CombatEvent[] _buf = new CombatEvent[MAX_EVENTS];
        private int _count;

        public int Count => _count;

        /// <summary>Returns the event at index <paramref name="i"/>. No bounds checking.</summary>
        public CombatEvent Get(int i) => _buf[i];

        /// <summary>Appends an event. Silently drops if the buffer is full (non-critical visual).</summary>
        public void Push(CombatEventType type, FixedVec3 position)
        {
            if (_count < MAX_EVENTS)
                _buf[_count++] = new CombatEvent { Type = type, Position = position };
        }

        /// <summary>Resets the buffer so the next frame starts fresh.</summary>
        public void Clear() => _count = 0;
    }
}
