#nullable enable
using ProjectChimera.Core;

namespace ProjectChimera.Combat
{
    /// <summary>One recorded unit death (Story 3.13): the world position it died at, the victim's faction (so a hero on
    /// the victim's own faction is excluded), and the XP bounty it awards. A plain value struct — snapshotted at the
    /// single death choke point (<see cref="DamageResolver.KillEntity"/>, BEFORE <see cref="EntityWorld.Destroy"/>
    /// recycles the slot) because the corpse is unobservable after that (D-1).</summary>
    public readonly struct DeathRecord
    {
        public readonly FixedVec3 Position;
        public readonly Faction   Faction;
        public readonly Fixed     Bounty;

        public DeathRecord(FixedVec3 position, Faction faction, Fixed bounty)
        {
            Position = position;
            Faction  = faction;
            Bounty   = bounty;
        }
    }

    /// <summary>
    /// A host-owned, per-tick TRANSIENT ring buffer of unit deaths (Story 3.13, D-1). <c>DamageResolver.KillEntity</c>
    /// pushes a <see cref="DeathRecord"/> at the single death choke point; <see cref="HeroXpSystem"/> (index 8, after
    /// combat + projectiles) drains it each tick, credits every hostile hero in range, and <see cref="Clear"/>s it.
    ///
    /// <para>Drained + cleared every tick, so it is EMPTY at the checksum boundary → NOT folded into
    /// <see cref="SimChecksum"/> (exactly like <see cref="CombatEventQueue"/>). Pure C# — no Godot dependency. The cap +
    /// silent-drop-when-full mirror <see cref="CombatEventQueue"/> (a dropped death simply awards no XP — non-critical).</para>
    /// </summary>
    public sealed class DeathFeed
    {
        private const int MAX_DEATHS = 256;

        private readonly DeathRecord[] _buf = new DeathRecord[MAX_DEATHS];
        private int _count;

        /// <summary>Number of deaths recorded this tick.</summary>
        public int Count => _count;

        /// <summary>Returns the death record at index <paramref name="i"/>. No bounds checking (the drain loop reads 0..Count).</summary>
        public DeathRecord Get(int i) => _buf[i];

        /// <summary>Record a death. Silently drops if the buffer is full (the awarded XP is non-critical).</summary>
        public void Push(FixedVec3 position, Faction faction, Fixed bounty)
        {
            if (_count < MAX_DEATHS)
                _buf[_count++] = new DeathRecord(position, faction, bounty);
        }

        /// <summary>Resets the buffer so the next tick starts fresh (called by <see cref="HeroXpSystem"/> at end-of-tick).</summary>
        public void Clear() => _count = 0;
    }
}
