#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions; // ItemRegistry / ItemDefinition
using ProjectChimera.Effects;          // EffectExecutor / EffectContext / Modifier / ModifierStore

namespace ProjectChimera.Combat
{
    /// <summary>
    /// Story 3.15 — the deterministic item / inventory runtime. An <see cref="ISimSystem"/> registered after the
    /// combat/projectile/hero-XP cluster (see <c>SimulationHost</c>). Owns three surfaces:
    ///   1. <b>Pickup</b> (per-tick, in <see cref="Tick"/>): for every live hero in <see cref="HeroStore.FoldOrder"/>
    ///      (ascending <see cref="HeroId"/>) whose <see cref="EntityWorld.CommandState"/> is
    ///      <see cref="UnitCommand.PickupItem"/>, drive its MoveTarget toward the targeted ground item and, on proximity,
    ///      CLAIM it (ground→held) into the first free inventory slot — or deny (full ring → <see cref="CombatEventType.OrderDenied"/>).
    ///      Ascending-hero iteration + immediate <c>Held</c> flip give the lower-id claimant the win when two heroes race.
    ///   2. <b>Use / drop</b> (command-driven, via <see cref="OrderApplier"/> → <see cref="UseItemCommand"/> /
    ///      <see cref="DropItemCommand"/>): a charged consumable fires its authored graph through the SHARED
    ///      <see cref="EffectExecutor"/> (RNG from <c>world.Rng</c>, no second generator), decrements a charge, and is
    ///      deleted at zero; a manual drop returns the item to the ground and removes its stat modifier.
    ///   3. <b>Death drop</b> (via <see cref="EntityWorld.OnDestroy"/> → <see cref="OnEntityDestroyed"/>): a dying hero's
    ///      carried items drop to the ground at the death position (position still valid pre-recycle) and the inventory
    ///      clears — so a revived hero returns empty and can re-collect (discharges the Story 3.14 items obligation).
    ///
    /// <para>A carried STAT item (any non-zero modifier delta) applies a permanent <see cref="Modifier"/> to its carrier
    /// via the FOLDED <see cref="ModifierStore.Apply"/>, keyed by a deterministic per-item id (<see cref="ItemModifierId"/>)
    /// so it is removed precisely on drop/death/consume. Determinism: <see cref="Fixed"/> (16.16) only, no float/RNG-off-tick
    /// beyond the shared world RNG, ascending-order iteration.</para>
    /// </summary>
    public sealed class ItemSystem : ISimSystem
    {
        /// <summary>Distinctive high base for a carried stat item's <see cref="Modifier.Id"/> (Story 3.15). Offset by the
        /// item's packed ItemStore ref so each held item's modifier is its own instance, removable by exact id. Chosen
        /// above <c>HeroXpSystem.HeroGrowthModifierId</c> (0x31330000) so a base+ref sum can never collide with growth or
        /// a low authored ability-modifier id.</summary>
        public const int ItemModifierIdBase = 0x4954_0000; // "IT"

        /// <summary>Proximity radius (world units, <see cref="Fixed"/>) at which a moving hero claims its targeted ground
        /// item. Comfortably larger than the unit contact distance so a hero pathing onto the item claims reliably.</summary>
        public static readonly Fixed PickupRadius = Fixed.FromInt(2);

        private readonly EntityWorld     _world;
        private readonly HeroStore       _heroes;
        private readonly ItemStore       _items;
        private readonly ModifierStore   _modifiers;
        private readonly ItemRegistry    _registry;
        private readonly CombatEventQueue? _events;
        private readonly DamageTable     _damageTable;

        // Shared graph-running executor (its own pre-allocated work-stack; a consumable's ApplyModifier leaf re-enters
        // the STORE's dedicated executor, never this one — the AbilityCastSystem re-entrancy posture).
        private readonly EffectExecutor _executor = new EffectExecutor();

        /// <summary>The per-scenario USABLE inventory slot count (Story 3.15, D-6) — caps the usable slots at/below the
        /// physical <see cref="HeroStore.INVENTORY_SLOTS"/> stride. Configured once at scenario-apply from
        /// <c>inventory_slot_count</c> (default = the full stride); clamped to <c>[1, INVENTORY_SLOTS]</c>.</summary>
        public int UsableSlots { get; private set; } = HeroStore.INVENTORY_SLOTS;

        public ItemSystem(EntityWorld world, HeroStore heroes, ItemStore items, ModifierStore modifiers,
                          ItemRegistry registry, CombatEventQueue? events, DamageTable? damageTable = null)
        {
            _world       = world;
            _heroes      = heroes;
            _items       = items;
            _modifiers   = modifiers;
            _registry    = registry;
            _events      = events;
            _damageTable = damageTable ?? DamageTable.Default;
            // Death-drop hook: a dying hero's carried items must drop at the death position BEFORE the slot recycles.
            world.OnDestroy += OnEntityDestroyed;
        }

        /// <summary>The deterministic per-item modifier id for a carried stat item at packed ref <paramref name="itemRef"/>.</summary>
        public static int ItemModifierId(int itemRef) => ItemModifierIdBase + itemRef;

        // ─────────────────────────────────────── Story 3.16 shop-buy surface (read for BuildingSystem.BuyItemCommand) ──

        /// <summary>The item registry this system resolves defs against (Story 3.16 — the shop resolves stock ids here).</summary>
        public ItemRegistry Registry => _registry;

        /// <summary>Resolve a hero ENTITY id to its live <see cref="HeroStore"/> slot (alive + hero-linked). False for a
        /// dead entity or a non-hero. The shop-buy guard uses this to validate the buyer.</summary>
        public bool TryResolveHero(int heroEntityId, out int heroSlot) => ResolveHeroSlot(heroEntityId, out heroSlot);

        /// <summary>The buyer's world position (Story 3.16 shop-radius proximity check). Caller must have resolved the hero.</summary>
        public FixedVec3 HeroPosition(int heroEntityId) => _world.Position[heroEntityId];

        /// <summary>The faction owning the hero entity (Story 3.16 owned-hero anti-cheat gate).</summary>
        public Faction HeroFaction(int heroEntityId) => _world.FactionOf[heroEntityId];

        /// <summary>True when the hero at <paramref name="heroSlot"/> has a free USABLE inventory slot (Story 3.16 — the
        /// full-inventory reject checks this BEFORE any spend). Reuses the pickup <see cref="FirstFreeSlot"/> logic.</summary>
        public bool HeroHasFreeSlot(int heroSlot) => FirstFreeSlot(heroSlot) >= 0;

        /// <summary>Mint a PURCHASED item (def index <paramref name="defIndex"/>) directly into the buyer's first free
        /// inventory slot (Story 3.16), REUSING the pickup claim block (ground→held flip, <c>Inventory[]</c> write,
        /// stat-modifier apply). The caller (<c>BuildingSystem.BuyItemCommand</c>) must have already gated
        /// ownership/capability/proximity/free-slot/affordability and SPENT the cost. Returns the packed <c>ItemStore</c>
        /// ref, or -1 when the item store is full / the hero is no longer resolvable (caller then refunds — atomic).</summary>
        public int GrantPurchasedItem(int heroEntityId, int defIndex)
        {
            if (!ResolveHeroSlot(heroEntityId, out int heroSlot)) return -1;
            int free = FirstFreeSlot(heroSlot);
            if (free < 0) return -1;
            ItemDefinition? def = _registry.TryGet(defIndex);
            if (def is null) return -1;
            int itemRef = _items.Create(defIndex, def.Charges, _world.Position[heroEntityId]);
            if (itemRef < 0) return -1; // item store full — caller refunds
            if (!_items.TryResolveRef(itemRef, out int itemSlot)) return -1;
            _items.Held[itemSlot]            = true;
            _items.CarrierHeroSlot[itemSlot] = heroSlot;
            _heroes.Inventory[heroSlot * HeroStore.INVENTORY_SLOTS + free] = itemRef;
            ApplyStatModifierIfAny(heroEntityId, itemSlot, itemRef);
            return itemRef;
        }

        /// <summary>Configure the usable inventory slot count from <c>inventory_slot_count</c> (clamped <c>[1, INVENTORY_SLOTS]</c>).</summary>
        public void ConfigureUsableSlots(int slotCount)
        {
            if (slotCount < 1) slotCount = 1;
            if (slotCount > HeroStore.INVENTORY_SLOTS) slotCount = HeroStore.INVENTORY_SLOTS;
            UsableSlots = slotCount;
        }

        // ─────────────────────────────────────────────── Pickup (per tick) ──────────────────────────────────────────

        public void Tick(EntityWorld world, Fixed dt)
        {
            int[] order = _heroes.FoldOrder(); // ascending HeroId — deterministic; lower-id claimant wins a race
            for (int oi = 0; oi < order.Length; oi++)
            {
                int slot = order[oi];
                if (!_heroes.Alive[slot]) continue;
                int entityId = _heroes.EntityId[slot];
                if (!IsLiveLinkedHero(slot, entityId)) continue;
                if (world.CommandState[entityId] != UnitCommand.PickupItem) continue;

                ResolvePickup(world, slot, entityId);
            }
        }

        private void ResolvePickup(EntityWorld world, int heroSlot, int entityId)
        {
            int itemRef = world.CommandTarget[entityId];
            // Target gone (picked up by someone / consumed / never existed) → void the order (no crash).
            if (!_items.TryResolveRef(itemRef, out int itemSlot) || _items.Held[itemSlot])
            {
                EndPickupOrder(world, entityId);
                return;
            }

            var itemPos = new FixedVec3(_items.PosX[itemSlot], world.Position[entityId].Y, _items.PosZ[itemSlot]);

            // Within claim radius? (long-widened raw squared distance — cannot overflow on a map-sized separation.)
            FixedVec3 hp = world.Position[entityId];
            long dxr = (long)hp.X.Raw - itemPos.X.Raw;
            long dzr = (long)hp.Z.Raw - itemPos.Z.Raw;
            long sqrDist = ((dxr * dxr) >> 16) + ((dzr * dzr) >> 16);
            long rr = ((long)PickupRadius.Raw * PickupRadius.Raw) >> 16;
            if (sqrDist > rr)
            {
                // Not yet in range — steer toward the item (ItemSystem runs after MovementSystem, so this lands next tick).
                world.MoveTarget[entityId] = itemPos;
                world.Flags[entityId]      = (world.Flags[entityId] | EntityFlags.Moving) & ~EntityFlags.Attacking;
                return;
            }

            // In range — claim into the first free USABLE slot, or deny when full.
            int free = FirstFreeSlot(heroSlot);
            if (free < 0)
            {
                // Story 11.4 (FR-74, P6): a local hero's full-inventory reject must carry faction + reason so the
                // denial cue is not filtered out as Neutral/None. Presentation-only (the queue is not folded).
                _events?.PushDenied(world.Position[entityId], world.FactionOf[entityId], DenialReason.InventoryFull);
                EndPickupOrder(world, entityId);
                return;
            }

            // Ground → held (same instance; the ref stays stable while carried). Tentative — a stat item whose modifier
            // the carrier's full modifier ring refuses (DW-34) is rolled back below so a capped hero never consumes an
            // item into a slot for zero stat benefit.
            _items.Held[itemSlot]            = true;
            _items.CarrierHeroSlot[itemSlot] = heroSlot;
            int invIdx = heroSlot * HeroStore.INVENTORY_SLOTS + free;
            _heroes.Inventory[invIdx] = itemRef;

            if (!ApplyStatModifierIfAny(entityId, itemSlot, itemRef))
            {
                // Modifier refused (hero at EffectCaps.MaxModifiersPerEntity) → roll back the three tentative claim
                // writes so the deny is state-identical to never having attempted it, then deny like a full inventory.
                _items.Held[itemSlot]            = false;
                _items.CarrierHeroSlot[itemSlot] = ItemStore.NO_CARRIER;
                _heroes.Inventory[invIdx]        = HeroStore.INVENTORY_EMPTY;
                // Story 11.4 (FR-74, P6): modifier-capped reject → same faction+reason stamping as the full-inventory
                // deny (it denies like a full inventory; the hero cannot take on another stat item).
                _events?.PushDenied(world.Position[entityId], world.FactionOf[entityId], DenialReason.InventoryFull);
                EndPickupOrder(world, entityId);
                return;
            }

            _events?.Push(CombatEventType.ItemPickedUp, world.Position[entityId]);
            EndPickupOrder(world, entityId);
        }

        /// <summary>End a pickup order deterministically (claimed / denied / voided): stop moving, back to Idle.</summary>
        private static void EndPickupOrder(EntityWorld world, int entityId)
        {
            world.CommandState[entityId]   = UnitCommand.Idle;
            world.ActiveOrderCmd[entityId] = (byte)UnitCommand.Idle;
            world.Flags[entityId]          = world.Flags[entityId] & ~EntityFlags.Moving;
            world.MoveTarget[entityId]     = world.Position[entityId];
        }

        // ─────────────────────────────────────────── Use / drop (command-driven) ────────────────────────────────────

        /// <summary>Use the consumable in inventory <paramref name="slot"/> of the hero embodied by
        /// <paramref name="heroEntityId"/> (Story 3.15). Runs the authored effect graph through the shared executor,
        /// decrements a charge, and deletes the item (freeing the instance + clearing the slot + removing any modifier)
        /// at zero. An empty/invalid slot, a non-hero, or a non-consumable (0 charges / no effect) is a deterministic
        /// no-op. Dispatched from <see cref="OrderApplier"/> AFTER the IsAlive/FactionOf entity-ownership guard (the hero
        /// entity is guarded like a normal unit command — the 3.15 anti-cheat that stops forcing an ENEMY hero to use
        /// items; NOT the Train/Revive pre-guard pattern).</summary>
        public void UseItemCommand(int heroEntityId, int slot, CombatEventQueue? events = null)
        {
            if (!ResolveHeroSlot(heroEntityId, out int heroSlot)) return;
            if (slot < 0 || slot >= HeroStore.INVENTORY_SLOTS) return;
            int invIdx = heroSlot * HeroStore.INVENTORY_SLOTS + slot;
            int itemRef = _heroes.Inventory[invIdx];
            if (itemRef == HeroStore.INVENTORY_EMPTY) return;
            if (!_items.TryResolveRef(itemRef, out int itemSlot)) return;

            ItemDefinition? def = _registry.TryGet(_items.DefId[itemSlot]);
            if (def is null || def.EffectGraph is null || _items.Charges[itemSlot] <= 0) return; // non-consumable → no-op

            // Run the consumable graph through the SHARED executor (self/graph-targeted; RNG from world.Rng).
            Faction faction = _world.FactionOf[heroEntityId];
            var ctx = new EffectContext(_world, heroEntityId, heroEntityId, faction,
                                        _damageTable, spatial: null, events ?? _events, stats: null, modifierStore: _modifiers);
            _executor.Run(def.EffectGraph, in ctx);

            // The authored graph runs arbitrary content: a self-damaging consumable can KILL its own carrier mid-use
            // (→ EntityWorld.OnDestroy → OnEntityDestroyed → DropAll has ALREADY dropped this item to the ground and
            // cleared its inventory slot), and a future graph could otherwise mutate the inventory. Re-verify the
            // carrier is still alive AND this exact instance still occupies the slot before consuming it — else the
            // charge decrement / Destroy below would corrupt an already-dropped/recycled instance (double-drop, a
            // wrong-slot charge decrement, or a double-free). State-only checks → deterministic. Shipped self-heal
            // keeps the hero alive and the item in place, so this is a no-op for it (golden bytes unchanged).
            if (!_world.IsAlive(heroEntityId) || !_items.TryResolveRef(itemRef, out itemSlot)
                || _heroes.Inventory[invIdx] != itemRef)
                return;

            (events ?? _events)?.Push(CombatEventType.ItemUsed, _world.Position[heroEntityId]);

            // Decrement a charge; delete the item at zero (free the instance, clear the slot, remove any modifier).
            _items.Charges[itemSlot]--;
            if (_items.Charges[itemSlot] <= 0)
            {
                _modifiers.RemoveByModifierId(heroEntityId, ItemModifierId(itemRef));
                _heroes.Inventory[invIdx] = HeroStore.INVENTORY_EMPTY;
                _items.Destroy(itemSlot);
            }
        }

        /// <summary>Drop the item in inventory <paramref name="slot"/> back onto the ground at the hero's position
        /// (Story 3.15). Removes the item's stat modifier, flips the instance held→ground, and clears the slot. An
        /// empty/invalid slot or a non-hero is a deterministic no-op.</summary>
        public void DropItemCommand(int heroEntityId, int slot, CombatEventQueue? events = null)
        {
            if (!ResolveHeroSlot(heroEntityId, out int heroSlot)) return;
            if (slot < 0 || slot >= HeroStore.INVENTORY_SLOTS) return;
            DropOne(heroSlot, slot, heroEntityId, _world.Position[heroEntityId], events ?? _events);
        }

        /// <summary>Drop EVERY carried item of the hero at <paramref name="heroSlot"/> onto the ground at
        /// <paramref name="pos"/> (Story 3.15, D-2 — WC3 death drop). Called from the entity-death hook. Modifier removal
        /// is a no-op when the entity is already gone (ModifierStore.ClearEntity ran first) — the items still drop.</summary>
        public void DropAll(int heroSlot, FixedVec3 pos)
        {
            if (heroSlot < 0 || heroSlot >= HeroStore.MAX_HEROES) return;
            int heroEntity = _heroes.EntityId[heroSlot];
            for (int s = 0; s < HeroStore.INVENTORY_SLOTS; s++)
                DropOne(heroSlot, s, heroEntity, pos, _events);
        }

        private void DropOne(int heroSlot, int slot, int heroEntityId, FixedVec3 pos, CombatEventQueue? events)
        {
            int invIdx = heroSlot * HeroStore.INVENTORY_SLOTS + slot;
            int itemRef = _heroes.Inventory[invIdx];
            if (itemRef == HeroStore.INVENTORY_EMPTY) return;

            if (_items.TryResolveRef(itemRef, out int itemSlot))
            {
                _items.Held[itemSlot]            = false;
                _items.CarrierHeroSlot[itemSlot] = ItemStore.NO_CARRIER;
                _items.PosX[itemSlot]            = pos.X;
                _items.PosZ[itemSlot]            = pos.Z;
            }
            // Remove the carried modifier (no-op if the entity is dead/gone → its modifiers were already cleared).
            _modifiers.RemoveByModifierId(heroEntityId, ItemModifierId(itemRef));
            _heroes.Inventory[invIdx] = HeroStore.INVENTORY_EMPTY;
            events?.Push(CombatEventType.ItemDropped, pos);
        }

        // ─────────────────────────────────────────── Death hook ───────────────────────────────────────────

        /// <summary>Subscriber for <see cref="EntityWorld.OnDestroy"/> — when a HERO entity dies, drop its carried items
        /// at the (still-valid) death position and clear its inventory. Non-hero entities (HeroIndex == HERO_NONE) are a
        /// no-op. The revived hero returns empty (inventory-on-persisted-row + WC3 drop — the Story 3.14 obligation).
        ///
        /// <para>DW-43: the packed <see cref="EntityWorld.HeroIndex"/> handle is <c>(Generation&lt;&lt;8)|slot</c>. Every
        /// in-match death keeps the <see cref="HeroStore"/> row Alive for revival, so <see cref="HeroStore.TryResolveRef"/>
        /// succeeds and the live-row <see cref="DropAll"/> path runs byte-for-byte as before. The fallback below handles the
        /// opposite teardown order — the row freed BEFORE the entity — which makes the packed handle fail to resolve. That
        /// order is NOT hypothetical: the DW-52 editor delete (<c>EntityPlacer.DeleteUnit</c>/<c>BuildDeleteUnit</c> call
        /// <see cref="HeroStore.DestroyByRef"/> before <c>world.Destroy</c>) already produces it on every hero deletion, and
        /// a future permanent (non-revivable) in-match hero removal would too. Before this fix that path early-returned and
        /// ORPHANED the carried items (<c>Held</c> forever, carrier gone, unreachable). The fallback drops them keyed off the
        /// handle's carrier slot (its low 8 bits survive a stale ref).</para>
        /// <para>Golden-neutral in practice: the fallback runs today only on editor delete, which is NOT a sim-tick golden
        /// path, and an edit-mode hero carries no items (items are acquired only during play, and the Edit↔Play reset
        /// <c>Clear</c>s the <see cref="ItemStore"/>), so the scan finds nothing to drop. No goldens move (verified: full
        /// Tier-1 suite unchanged) and no <c>AlgoVersion</c> bump is needed.</para></summary>
        private void OnEntityDestroyed(int entityId)
        {
            int packed = _world.HeroIndex[entityId];
            if (packed == EntityWorld.HERO_NONE) return;
            if (_heroes.TryResolveRef(packed, out int heroSlot))
            {
                DropAll(heroSlot, _world.Position[entityId]); // live-row path (every in-match death) — UNCHANGED
                return;
            }
            // DW-43 fallback: the row no longer resolves (freed before the entity — editor delete, or a future permanent
            // in-match removal). Drop by physical carrier slot so nothing is orphaned regardless of teardown order.
            DropOrphansByCarrierSlot(packed & 0xFF, entityId, _world.Position[entityId]);
        }

        /// <summary>DW-43: drop the carried items whose carrier is the physical hero slot <paramref name="carrierSlot"/>,
        /// used when the packed handle no longer resolves to a live row (the row was freed before the entity — the DW-52
        /// editor delete today, or a future permanent hero removal). Scans the <see cref="ItemStore"/> — NOT the inventory
        /// ring — because in the
        /// recycled case the orphans are no longer in any ring. For each held item carried by that slot: flip it to the
        /// ground at <paramref name="pos"/>, remove its stat modifier (keyed off the dying <paramref name="entityId"/> —
        /// a no-op once the entity's modifiers were cleared on destroy), and clear any stale reference to it in that
        /// slot's ring. When the slot has been RECYCLED to a new LIVE hero (<see cref="HeroStore.Alive"/>), an item still
        /// referenced by that ring is the live occupant's OWN item — it is skipped so only true orphans are dropped.</summary>
        private void DropOrphansByCarrierSlot(int carrierSlot, int entityId, FixedVec3 pos)
        {
            // A valid hero ref is always slot 0-63 (HERO_NONE is filtered by the caller); guard before indexing Alive/Inventory.
            if (carrierSlot < 0 || carrierSlot >= HeroStore.MAX_HEROES) return;
            bool slotLive = _heroes.Alive[carrierSlot]; // true ⇒ the slot was recycled to a different live hero

            for (int itemSlot = 0; itemSlot < _items.Count; itemSlot++) // ascending slot — deterministic
            {
                if (!_items.Alive[itemSlot] || !_items.Held[itemSlot]) continue;
                if (_items.CarrierHeroSlot[itemSlot] != carrierSlot) continue;

                int itemRef = _items.PackRef(itemSlot);
                int ringIdx = FindInventoryRefSlot(carrierSlot, itemRef); // -1 when this item is not in the slot's ring

                // Recycled slot: the ring now belongs to the live occupant. An in-ring item is ITS item — leave untouched.
                if (slotLive && ringIdx >= 0) continue;

                _items.Held[itemSlot]            = false;
                _items.CarrierHeroSlot[itemSlot] = ItemStore.NO_CARRIER;
                _items.PosX[itemSlot]            = pos.X;
                _items.PosZ[itemSlot]            = pos.Z;
                // No-op once the dying entity's modifiers were cleared on destroy; correct key when they were not.
                _modifiers.RemoveByModifierId(entityId, ItemModifierId(itemRef));
                // Freed-not-recycled: Destroy leaves the dead ring referencing the orphan — clear that stale ref.
                if (ringIdx >= 0) _heroes.Inventory[ringIdx] = HeroStore.INVENTORY_EMPTY;
                _events?.Push(CombatEventType.ItemDropped, pos);
            }
        }

        /// <summary>DW-43: the flat <see cref="HeroStore.Inventory"/> ring index (<c>heroSlot*stride + s</c>) that holds
        /// the packed ref <paramref name="itemRef"/>, or -1 when this slot's ring does not reference it.</summary>
        private int FindInventoryRefSlot(int heroSlot, int itemRef)
        {
            int baseIdx = heroSlot * HeroStore.INVENTORY_SLOTS;
            for (int s = 0; s < HeroStore.INVENTORY_SLOTS; s++)
                if (_heroes.Inventory[baseIdx + s] == itemRef) return baseIdx + s;
            return -1;
        }

        // ─────────────────────────────────────────── Internals ───────────────────────────────────────────

        /// <summary>Apply the carried stat modifier for the item at <paramref name="itemSlot"/>. Returns <c>true</c> when
        /// nothing needed applying (non-stat item) OR the modifier installed; <c>false</c> only when a stat item's
        /// modifier was REFUSED by a full modifier ring (the DW-34 cap-deny signal the pickup site rolls back on).</summary>
        private bool ApplyStatModifierIfAny(int entityId, int itemSlot, int itemRef)
        {
            ItemDefinition? def = _registry.TryGet(_items.DefId[itemSlot]);
            return ApplyItemStatModifier(_modifiers, _world, def, entityId, itemRef);
        }

        /// <summary>Story 3.16: the SHARED init/runtime stat-modifier apply, used by BOTH the pickup/buy runtime path
        /// (<see cref="ApplyStatModifierIfAny"/>) and the persisted-inventory re-mint
        /// (<c>HeroProfileLoader.ReMintInventory</c>), so a carried stat item applies the SAME deterministic per-item
        /// modifier (<see cref="ItemModifierId"/>) whether it was picked up, bought, or reloaded from a saved profile.
        /// A non-stat item (no non-zero delta) or a dead/unresolvable entity is a deterministic no-op.
        /// <para><b>Returns</b> <c>true</c> when there was nothing to apply (null/non-stat def → no modifier depends on
        /// the ring) or the modifier was installed; <c>false</c> only when a stat item's modifier was REFUSED by a full
        /// modifier ring (DW-34). The persisted re-mint (<c>HeroProfileLoader.ReMintInventory</c>) and the buy path keep
        /// their current behavior — they ignore the result and do not deny.</para></summary>
        public static bool ApplyItemStatModifier(ModifierStore modifiers, EntityWorld world,
                                                 ItemDefinition? def, int entityId, int itemRef)
        {
            if (def is null || !def.HasStatModifier) return true; // nothing to apply → claim succeeds
            var mod = new Modifier(
                ItemModifierId(itemRef),
                durationTicks: -1,               // permanent while carried (removed by RemoveByModifierId on drop)
                StackRule.Ignore,                // unique per-item id → never re-stacks; Ignore is a safe re-apply guard
                maxStacks: 1,
                maxHealthDelta:    def.MaxHealthDelta,
                attackDamageDelta: def.AttackDamageDelta,
                moveSpeedDelta:    def.MoveSpeedDelta,
                status: StatusFlags.None,
                periodEffect: null,
                periodTicks: 0,
                armorDelta:        def.ArmorDelta);
            return modifiers.Apply(entityId, mod, entityId, world.FactionOf[entityId]);
        }

        private int FirstFreeSlot(int heroSlot)
        {
            int baseIdx = heroSlot * HeroStore.INVENTORY_SLOTS;
            for (int s = 0; s < UsableSlots; s++)
                if (_heroes.Inventory[baseIdx + s] == HeroStore.INVENTORY_EMPTY) return s;
            return -1;
        }

        private bool ResolveHeroSlot(int entityId, out int heroSlot)
        {
            heroSlot = -1;
            if (!_world.IsAlive(entityId)) return false;
            if (_world.HeroIndex[entityId] == EntityWorld.HERO_NONE) return false;
            return _heroes.TryResolveRef(_world.HeroIndex[entityId], out heroSlot);
        }

        private bool IsLiveLinkedHero(int heroSlot, int entityId)
        {
            if (!_heroes.Alive[heroSlot]) return false;
            if (!_world.IsAlive(entityId)) return false;
            return _heroes.TryResolveRef(_world.HeroIndex[entityId], out int linked) && linked == heroSlot;
        }
    }
}
