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
                _events?.Push(CombatEventType.OrderDenied, world.Position[entityId]);
                EndPickupOrder(world, entityId);
                return;
            }

            // Ground → held (same instance; the ref stays stable while carried).
            _items.Held[itemSlot]            = true;
            _items.CarrierHeroSlot[itemSlot] = heroSlot;
            _heroes.Inventory[heroSlot * HeroStore.INVENTORY_SLOTS + free] = itemRef;

            ApplyStatModifierIfAny(entityId, itemSlot, itemRef);

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
        /// no-op. Dispatched from <see cref="OrderApplier"/> before the entity guard (the Train/Revive pattern).</summary>
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
        /// no-op. The revived hero returns empty (inventory-on-persisted-row + WC3 drop — the Story 3.14 obligation).</summary>
        private void OnEntityDestroyed(int entityId)
        {
            if (_world.HeroIndex[entityId] == EntityWorld.HERO_NONE) return;
            if (!_heroes.TryResolveRef(_world.HeroIndex[entityId], out int heroSlot)) return;
            DropAll(heroSlot, _world.Position[entityId]);
        }

        // ─────────────────────────────────────────── Internals ───────────────────────────────────────────

        private void ApplyStatModifierIfAny(int entityId, int itemSlot, int itemRef)
        {
            ItemDefinition? def = _registry.TryGet(_items.DefId[itemSlot]);
            if (def is null || !def.HasStatModifier) return;
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
            _modifiers.Apply(entityId, mod, entityId, _world.FactionOf[entityId]);
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
