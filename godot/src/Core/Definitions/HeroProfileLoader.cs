#nullable enable
using System.Collections.Generic;
using System.Text;
using ProjectChimera.Core;      // Fixed, HeroStore, HeroId
using ProjectChimera.Core.Sim;  // ILogSink

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The Godot-free APPLY core of the offline hero rail (Story 3.9, AR-12 M2 / D-1/D-2). Mints a deployed
    /// <see cref="PlayerProfile"/> into <see cref="HeroStore"/> as DETERMINISTIC INITIAL STATE — called AFTER
    /// <c>ScenarioApplier.Apply</c> (which records the placed hero entities) and BEFORE
    /// <see cref="StartStateHash.Compute"/>, so the persisted level/xp fold into the start-state hash automatically
    /// (<see cref="StartStateHash"/> already walks <see cref="HeroStore.FoldOrder"/>). Loading the same profile into the
    /// same scenario twice yields a byte-identical hash (MP-safe); an incompatible/null profile mints nothing so the
    /// hash equals the empty-store (no-profile) value. It also builds the Save-side profile from a manifest shape.
    ///
    /// <para>Pure C#: no <c>using Godot</c>, no wall-clock/RNG, ascending list iteration only (no <c>Dictionary</c>
    /// enumeration) — in-scope for the release analyzer gate.</para>
    /// </summary>
    public static class HeroProfileLoader
    {
        private const ulong FnvOffset = 14695981039346656037UL; // FNV-64 offset basis (same primitive as StartStateHash)
        private const ulong FnvPrime  = 1099511628211UL;        // FNV-64 prime

        /// <summary>A hero entity actually placed by the scenario applier: its runtime <see cref="EntityId"/> and the
        /// <see cref="UnitDefinition.Id"/> it spawned from. The applier records one of these per spawned
        /// <see cref="UnitDefinition.IsHero"/> unit; <see cref="LoadInto"/> mints only the ones whose id matches the
        /// deployed profile (so a stray same-id NON-hero can never receive hero state — D-3).</summary>
        /// <summary>Story 3.13: the placed hero also carries the def-derived leveling curve / growth / share constants,
        /// captured (float→<see cref="Fixed"/>) at the single load boundary by the applier, so <see cref="LoadInto"/>
        /// seeds them into the widened <see cref="HeroStore.Mint"/> (the SoA-recycle contract) without a second def
        /// lookup at load time. Defaulted so the pre-3.13 two-arg construction still compiles (persistence tests).</summary>
        /// <summary>Story 3.14: the placed hero also carries the <see cref="UnitDefinition"/> it spawned from and its
        /// owning <see cref="Faction"/>, so the widened <see cref="HeroStore.Mint"/> can store them as the respawn def +
        /// ownership constant (a revival re-spawns a fresh entity from that def, owned by that faction). Defaulted so the
        /// pre-3.14 construction still compiles.</summary>
        public readonly record struct PlacedHero(int EntityId, string UnitId,
            int MaxLevel = 0, Fixed BaseXp = default, Fixed XpGrowth = default, Fixed XpShareRadius = default,
            Fixed HealthPerLevel = default, Fixed DamagePerLevel = default, Fixed ArmorPerLevel = default,
            UnitDefinition? SourceDef = null, Faction OwnerFaction = default);

        /// <summary>
        /// The DETERMINISTIC hero identity for <paramref name="profile"/> = FNV-64 of its stable <see cref="PlayerProfile.ProfileId"/>
        /// wrapped in a <see cref="HeroId"/> (D-2). Producer-independent + reproducible: the same saved profile always
        /// mints the same id, so a re-load reproduces a byte-identical <see cref="StartStateHash"/>. Unique because
        /// <see cref="PlayerProfile.ProfileId"/>s are unique.
        /// </summary>
        public static HeroId MintId(PlayerProfile profile) => new HeroId(Fnv64(profile.ProfileId));

        /// <summary>
        /// Mint <paramref name="profile"/> into <paramref name="heroes"/> for every placed hero whose
        /// <see cref="PlacedHero.UnitId"/> matches <see cref="PlayerProfile.HeroDefId"/>, at the profile's persisted
        /// <see cref="PlayerProfile.Level"/> / <see cref="PlayerProfile.Xp"/>. Returns the number of rows minted;
        /// <paramref name="profile"/> <c>null</c> ⇒ 0 minted (nothing changes — the no-profile / "play without a hero"
        /// path). A <see cref="HeroStore.Mint"/> returning <c>-1</c> (full store, or the same deterministic id already
        /// live — e.g. two placed heroes share the deployed hero's unit id) is a DETERMINISTIC SKIP + optional log, with
        /// no partial-state divergence: every peer skips the same rows. Iterates <paramref name="placedHeroes"/> in list
        /// order (ascending) — no nondeterministic enumeration.
        /// </summary>
        public static int LoadInto(HeroStore heroes, IReadOnlyList<PlacedHero> placedHeroes, PlayerProfile? profile,
                                   ILogSink? log = null, EntityWorld? world = null)
        {
            if (profile == null || placedHeroes == null) return 0;

            HeroId id  = MintId(profile);
            int level  = profile.Level;
            Fixed xp   = profile.Xp;

            int minted = 0;
            for (int i = 0; i < placedHeroes.Count; i++)
            {
                PlacedHero placed = placedHeroes[i];
                if (placed.UnitId != profile.HeroDefId) continue; // only the deployed hero's placed units

                // Story 3.13: mint with the def-derived curve/growth/share constants captured on the placed hero, so the
                // HeroXpSystem can level + grow it (the SoA-recycle contract writes every live field in Mint).
                int slot = heroes.Mint(id, placed.EntityId, level, xp,
                                       placed.MaxLevel, placed.BaseXp, placed.XpGrowth, placed.XpShareRadius,
                                       placed.HealthPerLevel, placed.DamagePerLevel, placed.ArmorPerLevel,
                                       placed.SourceDef, placed.OwnerFaction); // Story 3.14: respawn def + owner faction
                if (slot >= 0)
                {
                    minted++;
                    // Story 3.13 (D-8): establish the entity→hero link so the XP system can ABA-safely validate the row.
                    // EntityWorld.HeroIndex is otherwise never populated (only reset to HERO_NONE). Null world (Tier-1
                    // persistence tests that don't run the XP system) skips it harmlessly.
                    if (world != null && placed.EntityId >= 0 && placed.EntityId < EntityWorld.MAX_ENTITIES)
                        world.HeroIndex[placed.EntityId] = heroes.PackRef(slot);
                }
                else log?.Warn($"[HeroProfileLoader] Mint refused for profile '{profile.ProfileId}' " +
                               $"(entity {placed.EntityId}) — duplicate live id or full store; skipped deterministically.");
            }
            return minted;
        }

        /// <summary>
        /// Build a <see cref="PlayerProfile"/> from a hero's current init-eligible state, capturing EXACTLY the keys the
        /// scenario's manifest <paramref name="shape"/> selected (D-5). Walks the shape's catalog-ordered slots and
        /// records <c>hero.level</c> as its int and <c>hero.xp</c> as its <see cref="Fixed.Raw"/> — an integer raw, never
        /// a float. This is the Tier-1-tested Save core; the UI supplies the live <paramref name="level"/> /
        /// <paramref name="xp"/> values (pre-3.13: a deployed profile's values, else the authored base).
        /// </summary>
        public static PlayerProfile BuildProfile(string profileId, string heroDefId, string factionId,
                                                 string displayName, string? signatureAbility,
                                                 int level, Fixed xp, PlayerProfileShape shape)
        {
            var values = new List<ProfileAttributeValue>();
            if (shape != null)
            {
                for (int i = 0; i < shape.Slots.Count; i++)
                {
                    string key = shape.Slots[i].Key;
                    if (key == "hero.level")   values.Add(new ProfileAttributeValue(key, level));
                    else if (key == "hero.xp") values.Add(new ProfileAttributeValue(key, xp.Raw));
                    // other keys: no backing store yet (only hero.level/hero.xp are eligible today) — skip.
                }
            }

            return new PlayerProfile
            {
                ProfileId        = profileId,
                HeroDefId        = heroDefId,
                FactionId        = factionId,
                DisplayName      = displayName,
                SignatureAbility = signatureAbility,
                Values           = values,
            };
        }

        /// <summary>FNV-64 over the UTF-8 bytes of <paramref name="s"/> — platform-independent (byte-exact) so the
        /// minted <see cref="HeroId"/> is identical on every peer.</summary>
        private static ulong Fnv64(string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s ?? "");
            ulong h = FnvOffset;
            for (int i = 0; i < bytes.Length; i++)
            {
                h ^= bytes[i];
                h *= FnvPrime;
            }
            return h;
        }
    }
}
