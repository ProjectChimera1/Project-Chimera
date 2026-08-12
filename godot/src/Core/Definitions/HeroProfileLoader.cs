#nullable enable
using System.Collections.Generic;
using System.Text;
using ProjectChimera.Core;      // Fixed, HeroStore, HeroId
using ProjectChimera.Core.Sim;  // ILogSink
using ProjectChimera.Combat;    // ItemSystem.ApplyItemStatModifier (shared carried-stat-item apply)
using ProjectChimera.Effects;   // ModifierStore

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
        /// <summary>DW-26: the placed hero also carries its resolved per-hero XP-gain multiplier (<c>xp_per_kill / 100</c>,
        /// captured float→<see cref="Fixed"/> at the applier boundary), threaded into <see cref="HeroStore.Mint"/> so the
        /// XP runtime scales each kill credit. Nullable-null (the default) → the neutral <see cref="Fixed.One"/> in Mint,
        /// so every pre-DW-26 construction (persistence tests, bare callers) still credits the full bounty.</summary>
        /// <summary>Story 15-21: the placed hero also carries its RESOLVED attribute contributions (the
        /// <see cref="HeroAttributeResolver"/> output — per-stat base/per-level pairs in
        /// <see cref="AttributeStats"/> order, flattened from the faction's attribute model × the hero's authored
        /// attributes at the applier's single float→<see cref="Fixed"/> boundary). Null (the default) = no
        /// attributes — every pre-15-21 construction mints all-zero contributions, byte-identical.</summary>
        public readonly record struct PlacedHero(int EntityId, string UnitId,
            int MaxLevel = 0, Fixed BaseXp = default, Fixed XpGrowth = default, Fixed XpShareRadius = default,
            Fixed HealthPerLevel = default, Fixed DamagePerLevel = default, Fixed ArmorPerLevel = default,
            UnitDefinition? SourceDef = null, Faction OwnerFaction = default,
            Fixed? XpGainFactor = null,
            Fixed[]? AttrStatBase = null, Fixed[]? AttrStatPerLevel = null);

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
        ///
        /// <para>DW-12 (fail-closed range/cap gate): a whole profile with an out-of-range persisted level (&lt; 0) or xp
        /// (raw &lt; 0, or above <see cref="Combat.HeroXpSystem.XpCeiling"/>) is REJECTED (0 minted + <c>log?.Warn</c>) so a
        /// cheated/corrupt value never folds into <see cref="StartStateHash"/> - never a silent clamp. Per placed hero, an
        /// over-cap level (<c>placed.MaxLevel &gt; 0 &amp;&amp; level &gt; placed.MaxLevel</c>) skips THAT hero deterministically.</para>
        ///
        /// <para>DW-13 (owner-slot scoping): when <paramref name="ownerSlot"/> is non-null, only placed heroes whose
        /// <see cref="PlacedHero.OwnerFaction"/> equals it are minted (the local player's profile lands on the local
        /// player's placed hero, not an enemy's same-id hero). <c>null</c> (the default) preserves today's behaviour.</para>
        /// </summary>
        public static int LoadInto(HeroStore heroes, IReadOnlyList<PlacedHero> placedHeroes, PlayerProfile? profile,
                                   ILogSink? log = null, EntityWorld? world = null,
                                   ItemStore? items = null, ItemRegistry? registry = null,
                                   ModifierStore? modifiers = null, int usableSlots = HeroStore.INVENTORY_SLOTS,
                                   Faction? ownerSlot = null)
        {
            if (profile == null || placedHeroes == null) return 0;

            HeroId id  = MintId(profile);
            int level  = profile.Level;
            Fixed xp   = profile.Xp;

            // DW-12: fail-closed range gate on the whole profile. Reject (0 minted) an out-of-range persisted level/xp
            // rather than folding a cheat/corrupt value into the start-state hash. Deterministic (every peer rejects the
            // same profile); never a silent clamp. XpCeiling is the same saturation the runtime enforces, so a persisted
            // xp above it is by definition unreachable through legitimate play. Level lower-bound is < 0 (NOT < 1): an
            // inventory-only profile whose manifest omits hero.level legitimately mints level == 0 today.
            // Story 9.12: the range predicate is now the ONE canonical rule in HeroProfileValidator.IsLevelXpInRange
            // (behaviour-identical to the former inline `level < 0 || xp.Raw < 0 || xp.Raw > XpCeiling.Raw`), so this
            // apply gate, the client pre-flight, and the TS server RPC never drift. Scoped to the RANGE branch ONLY — the
            // other validator rules (identity/attributes/inventory) are NOT gated here, keeping this apply path byte-identical.
            if (!HeroProfileValidator.IsLevelXpInRange(level, xp.Raw))
            {
                log?.Warn($"[HeroProfileLoader] Profile '{profile.ProfileId}' has an out-of-range level ({level}) or xp " +
                          $"(raw {xp.Raw}, ceiling {HeroXpSystem.XpCeiling.Raw}) - rejected deterministically; 0 minted.");
                return 0;
            }

            int minted = 0;
            int ownerFiltered = 0; // DW-13: unit-id-matching placed heroes skipped PURELY by the owner-slot gate
            for (int i = 0; i < placedHeroes.Count; i++)
            {
                PlacedHero placed = placedHeroes[i];
                if (placed.UnitId != profile.HeroDefId) continue; // only the deployed hero's placed units

                // DW-13: owner-slot scoping - skip a placed hero owned by a different faction than the local caller's slot.
                // Null ownerSlot (every pre-existing caller/test) bypasses the filter, preserving mint-into-every-match.
                if (ownerSlot != null && placed.OwnerFaction != ownerSlot.Value) { ownerFiltered++; continue; }

                // DW-12: per-hero over-cap gate - skip (don't mint) a placed hero whose authored MaxLevel is below the
                // persisted level. MaxLevel == 0 means "no cap declared" (pre-3.13 construction), so the gate is inert then.
                if (placed.MaxLevel > 0 && level > placed.MaxLevel)
                {
                    log?.Warn($"[HeroProfileLoader] Profile '{profile.ProfileId}' level {level} exceeds placed hero " +
                              $"(entity {placed.EntityId}) MaxLevel {placed.MaxLevel} - skipped deterministically.");
                    continue;
                }

                // Story 3.13: mint with the def-derived curve/growth/share constants captured on the placed hero, so the
                // HeroXpSystem can level + grow it (the SoA-recycle contract writes every live field in Mint).
                int slot = heroes.Mint(id, placed.EntityId, level, xp,
                                       placed.MaxLevel, placed.BaseXp, placed.XpGrowth, placed.XpShareRadius,
                                       placed.HealthPerLevel, placed.DamagePerLevel, placed.ArmorPerLevel,
                                       placed.SourceDef, placed.OwnerFaction, // Story 3.14: respawn def + owner faction
                                       placed.XpGainFactor,                   // DW-26: per-hero XP-gain multiplier
                                       placed.AttrStatBase, placed.AttrStatPerLevel); // Story 15-21: resolved attribute contributions
                if (slot >= 0)
                {
                    minted++;
                    // Story 3.13 (D-8): establish the entity→hero link so the XP system can ABA-safely validate the row.
                    // EntityWorld.HeroIndex is otherwise never populated (only reset to HERO_NONE). Null world (Tier-1
                    // persistence tests that don't run the XP system) skips it harmlessly.
                    if (world != null && placed.EntityId >= 0 && placed.EntityId < EntityWorld.MAX_ENTITIES)
                        world.HeroIndex[placed.EntityId] = heroes.PackRef(slot);
                    // Story 3.16: re-mint the persisted inventory into ItemStore + HeroStore.Inventory[] as deterministic
                    // init state (AFTER Mint zeroes the row, BEFORE StartStateHash.Compute folds it). Skipped when the
                    // stores are absent (pre-3.16 callers / persistence-only tests) — inventory stays empty then.
                    if (items != null && registry != null)
                        ReMintInventory(heroes, items, registry, slot, placed.EntityId, profile,
                                        modifiers, world, usableSlots, log);
                }
                else log?.Warn($"[HeroProfileLoader] Mint refused for profile '{profile.ProfileId}' " +
                               $"(entity {placed.EntityId}) — duplicate live id or full store; skipped deterministically.");
            }

            // DW-13 (review): the profile's hero id WAS placed, but every placement is owned by a slot other than the
            // owning one, so nothing minted. The picker offers a profile whenever its hero id is placed in ANY slot
            // (HeroPickerOverlay.IsCompatible/TryResolvePrimaryHero are slot-agnostic), so a deploy that lands on no
            // owned hero must be diagnosable rather than a silent 0-mint — the whole loadout would otherwise vanish quietly.
            if (minted == 0 && ownerSlot != null && ownerFiltered > 0)
                log?.Warn($"[HeroProfileLoader] Profile '{profile.ProfileId}' matched {ownerFiltered} placed hero(es), " +
                          $"none owned by slot {ownerSlot.Value} — 0 minted (deployed hero is not on the owning player's slot).");

            return minted;
        }

        /// <summary>Story 3.16: re-mint a profile's persisted inventory into <paramref name="items"/> + the hero row's
        /// <c>Inventory[]</c> at init-time — <see cref="ItemRegistry.IndexOf"/> → <see cref="ItemStore.Create"/> → mark
        /// held + carrier → write the ring. SLOT-FAITHFUL: an item carrying a persisted <see cref="ProfileInventoryItem.Slot"/>
        /// is restored to that exact grid slot (a legacy <c>Slot &lt; 0</c> falls back to the first free slot, the old
        /// contiguous behaviour). Honors the active <paramref name="usableSlots"/> cap — a persisted item mapped beyond the
        /// cap (or a duplicate/occupied slot) is a deterministic skip + optional log, never landed over-capacity. Charges are
        /// clamped to <c>[0, def.Charges]</c> (a corrupt/hand-edited profile can't mint a negative- or over-charge item).
        /// A held item's ground position is irrelevant (unread while held) so it is minted at the origin, deterministically.
        /// After minting, each carried STAT item applies its per-item modifier (via the shared
        /// <see cref="ItemSystem.ApplyItemStatModifier"/>, keyed by <see cref="ItemSystem.ItemModifierId"/>) in ASCENDING
        /// slot order — deterministically, BEFORE <see cref="StartStateHash.Compute"/> — so a reloaded stat item is NOT inert
        /// (its Effective* bonus matches the pickup/buy path). The modifier apply is skipped when no
        /// <paramref name="modifiers"/>/<paramref name="world"/> is supplied (persistence-only tests).</summary>
        private static void ReMintInventory(HeroStore heroes, ItemStore items, ItemRegistry registry, int heroSlot,
                                            int entityId, PlayerProfile profile, ModifierStore? modifiers,
                                            EntityWorld? world, int usableSlots, ILogSink? log)
        {
            var inv = profile.Inventory;
            int baseIdx = heroSlot * HeroStore.INVENTORY_SLOTS;
            int cap = usableSlots < 1 ? 1
                    : usableSlots > HeroStore.INVENTORY_SLOTS ? HeroStore.INVENTORY_SLOTS : usableSlots;

            for (int i = 0; i < inv.Count; i++)
            {
                ProfileInventoryItem entry = inv[i];

                // Resolve the destination slot: a persisted index restores slot-faithfully (honouring the usable cap);
                // a legacy/unspecified slot (< 0) falls back to the first free usable slot (the old contiguous pack).
                int targetSlot;
                if (entry.Slot >= 0)
                {
                    targetSlot = entry.Slot;
                    if (targetSlot >= cap)
                    {
                        log?.Warn($"[HeroProfileLoader] Inventory item '{entry.ItemId}' for profile '{profile.ProfileId}' " +
                                  $"maps to slot {targetSlot} beyond the usable cap {cap} — skipped deterministically.");
                        continue;
                    }
                    if (heroes.Inventory[baseIdx + targetSlot] != HeroStore.INVENTORY_EMPTY)
                    {
                        log?.Warn($"[HeroProfileLoader] Inventory slot {targetSlot} for profile '{profile.ProfileId}' " +
                                  "is already occupied (duplicate persisted slot) — skipped deterministically.");
                        continue;
                    }
                }
                else
                {
                    targetSlot = FirstFreeSlot(heroes, baseIdx, cap);
                    if (targetSlot < 0) break; // no free usable slot — deterministic stop
                }

                int defIndex = registry.IndexOf(entry.ItemId);
                if (defIndex < 0)
                {
                    log?.Warn($"[HeroProfileLoader] Inventory item '{entry.ItemId}' for profile '{profile.ProfileId}' " +
                              "is not in the loaded item registry — skipped deterministically.");
                    continue;
                }
                int charges = ClampCharges(entry.Charges, registry.TryGet(defIndex));
                int itemRef = items.Create(defIndex, charges, FixedVec3.Zero);
                if (itemRef < 0) break; // item store full — deterministic stop
                if (!items.TryResolveRef(itemRef, out int itemSlot)) break;
                items.Held[itemSlot]            = true;
                items.CarrierHeroSlot[itemSlot] = heroSlot;
                heroes.Inventory[baseIdx + targetSlot] = itemRef;
            }

            // Apply each carried stat item's modifier in ASCENDING slot order — deterministic, after all mints, before the
            // start-state hash. No-op without a runtime store/world (Tier-1 persistence tests that only re-mint the ring).
            if (modifiers != null && world != null)
            {
                for (int s = 0; s < HeroStore.INVENTORY_SLOTS; s++)
                {
                    int itemRef = heroes.Inventory[baseIdx + s];
                    if (itemRef == HeroStore.INVENTORY_EMPTY) continue;
                    if (!items.TryResolveRef(itemRef, out int itemSlot)) continue;
                    ItemDefinition? def = registry.TryGet(items.DefId[itemSlot]);
                    ItemSystem.ApplyItemStatModifier(modifiers, world, def, entityId, itemRef);
                }
            }
        }

        /// <summary>First free inventory slot in <c>[0, cap)</c> for the hero at <paramref name="baseIdx"/>, or −1 if the
        /// usable ring is full — the deterministic contiguous fallback for a legacy (slot-less) persisted item.</summary>
        private static int FirstFreeSlot(HeroStore heroes, int baseIdx, int cap)
        {
            for (int s = 0; s < cap; s++)
                if (heroes.Inventory[baseIdx + s] == HeroStore.INVENTORY_EMPTY) return s;
            return -1;
        }

        /// <summary>Clamp a persisted charge count to <c>[0, def.Charges]</c> — a corrupt/hand-edited profile (e.g.
        /// <c>charges=-5</c>, or a stat item with authored 0 charges hand-set to 5) can never re-mint a negative- or
        /// over-charge instance. A stat item (authored <c>Charges == 0</c>) always clamps back to 0.</summary>
        private static int ClampCharges(int charges, ItemDefinition? def)
        {
            if (charges < 0) charges = 0;
            if (def != null && def.Charges >= 0 && charges > def.Charges) charges = def.Charges;
            return charges;
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
                                                 int level, Fixed xp, PlayerProfileShape shape,
                                                 IReadOnlyList<ProfileInventoryItem>? inventory = null)
        {
            var values = new List<ProfileAttributeValue>();
            var inv    = new List<ProfileInventoryItem>();
            if (shape != null)
            {
                for (int i = 0; i < shape.Slots.Count; i++)
                {
                    string key = shape.Slots[i].Key;
                    if (key == "hero.level")   values.Add(new ProfileAttributeValue(key, level));
                    else if (key == "hero.xp") values.Add(new ProfileAttributeValue(key, xp.Raw));
                    // Story 3.16: hero.inventory rides its own serialized list (the (key,raw) shape can't express it).
                    else if (key == "hero.inventory" && inventory != null) inv.AddRange(inventory);
                    // other keys: no backing store yet — skip.
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
                Inventory        = inv,
            };
        }

        /// <summary>Story 3.16: capture a live hero's carried inventory as (item-def id + charges) pairs for the Save path
        /// — resolve each occupied <c>HeroStore.Inventory[]</c> ref through <see cref="ItemStore.TryResolveRef"/> →
        /// <c>DefId</c> → <see cref="ItemRegistry"/> id (never the volatile packed ref). Ascending slot order. Godot-free.</summary>
        public static List<ProfileInventoryItem> CaptureInventory(HeroStore heroes, ItemStore items,
                                                                  ItemRegistry registry, int heroSlot)
        {
            var list = new List<ProfileInventoryItem>();
            if (heroSlot < 0 || heroSlot >= HeroStore.MAX_HEROES) return list;
            int baseIdx = heroSlot * HeroStore.INVENTORY_SLOTS;
            for (int s = 0; s < HeroStore.INVENTORY_SLOTS; s++)
            {
                int refPacked = heroes.Inventory[baseIdx + s];
                if (refPacked == HeroStore.INVENTORY_EMPTY) continue;
                if (!items.TryResolveRef(refPacked, out int itemSlot)) continue;
                ItemDefinition? def = registry.TryGet(items.DefId[itemSlot]);
                if (def == null) continue;
                // Slot-faithful (Story 3.16 review): persist the OCCUPIED grid index `s` so re-mint restores this exact
                // slot (a non-contiguous loadout round-trips to the same slots, not repacked contiguously).
                list.Add(new ProfileInventoryItem(def.Id, items.Charges[itemSlot], s));
            }
            return list;
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
