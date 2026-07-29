#nullable enable
namespace ProjectChimera.Core
{
    /// <summary>
    /// Sparse Struct-of-Arrays storage for item INSTANCES (Story 3.15) — on-ground or held. A structural clone of the
    /// <see cref="BuildingStore"/>/<see cref="HeroStore"/> recycle contract: a monotonic <see cref="Count"/> high-water
    /// fold bound, a LIFO free-list for slot recycling, a per-slot <see cref="Generation"/> counter for ABA-armor, and a
    /// <see cref="PackRef"/>/<see cref="TryResolveRef"/> generation-stamped handle so a stale cross-tick reference
    /// (a pickup order that names a since-recycled instance) resolves <c>false</c> rather than retargeting a NEW item.
    ///
    /// <para>NOT an <see cref="EntityWorld"/> entity (mirrors the <see cref="ResourceNodeStore"/> non-unit-map-object
    /// precedent): items are map objects / inventory contents, not combat entities. Its mutable numeric state
    /// (<see cref="DefId"/>/<see cref="Charges"/>/<see cref="PosX"/>/<see cref="PosZ"/>/<see cref="Held"/>/
    /// <see cref="CarrierHeroSlot"/>) folds into <see cref="SimChecksum"/> (v12), count-driven ascending slot.</para>
    ///
    /// <para><b>SoA-recycle contract.</b> EVERY live field is written in <see cref="Create"/>; a recycled slot therefore
    /// carries NONE of the prior occupant's <see cref="DefId"/>/<see cref="Charges"/> — the SoA-recycle trap the
    /// <c>ItemStore</c> recycle-guard test catches. Pure C#: no <c>using Godot</c>, <see cref="Fixed"/> (16.16) for
    /// gameplay numerics, ascending-order iteration only.</para>
    /// </summary>
    public class ItemStore
    {
        /// <summary>Maximum simultaneous item instances (ground + held). Mirrors <see cref="BuildingStore.MAX_BUILDINGS"/>
        /// (the <see cref="PackRef"/> encoding reserves the low 8 bits for the slot). Ample for a playable map.</summary>
        public const int MAX_ITEMS = 64;

        /// <summary><see cref="CarrierHeroSlot"/> sentinel for an on-ground (unheld) item. −1, distinct from any valid
        /// hero slot (0..MAX_HEROES-1).</summary>
        public const int NO_CARRIER = -1;

        // ── Live per-item data (folded into SimChecksum v12) ──
        /// <summary>Slot occupancy. A recycled slot is <c>false</c>; the fold/iteration skip it.</summary>
        public readonly bool[]  Alive           = new bool[MAX_ITEMS];
        /// <summary>Registry index of this instance's <c>ItemDefinition</c> (the ascending-Id index). A recycled slot
        /// must never carry the prior occupant's def (written in <see cref="Create"/>).</summary>
        public readonly int[]   DefId           = new int[MAX_ITEMS];
        /// <summary>Remaining consumable charges (0 for a stat item). Decremented on use; the instance is destroyed at 0.</summary>
        public readonly int[]   Charges         = new int[MAX_ITEMS];
        /// <summary>Ground X (<see cref="Fixed"/>). Valid while <see cref="Held"/> is false (unread while held).</summary>
        public readonly Fixed[] PosX            = new Fixed[MAX_ITEMS];
        /// <summary>Ground Z (<see cref="Fixed"/>). Valid while <see cref="Held"/> is false.</summary>
        public readonly Fixed[] PosZ            = new Fixed[MAX_ITEMS];
        /// <summary>True when this instance is carried in a hero's inventory (vs on the ground).</summary>
        public readonly bool[]  Held            = new bool[MAX_ITEMS];
        /// <summary>The carrier's HeroStore slot when <see cref="Held"/>; <see cref="NO_CARRIER"/> (−1) on the ground.</summary>
        public readonly int[]   CarrierHeroSlot = new int[MAX_ITEMS];

        // ── Management (monotonic high-water fold/iteration bound) ──
        /// <summary>One past the highest slot ever allocated — the iteration/fold upper bound. A monotonic high-water
        /// mark: recycling reuses dead slots BELOW Count, so Count only grows.</summary>
        public int Count { get; private set; }

        // ── Free-list recycling (mirrors BuildingStore / HeroStore) — UNFOLDED bookkeeping. ──
        private readonly int[] _freeList = new int[MAX_ITEMS];
        private int            _freeCount;

        // ── Generation counter (ABA-armor, mirrors BuildingStore.Generation) — UNFOLDED (deterministic recycle). ──
        /// <summary>Per-slot recycle generation, bumped each time <see cref="Create"/> reuses a dead slot. Every
        /// cross-tick item reference (inventory refs, a pickup-order target) is PACKED as
        /// <c>(Generation[slot] &lt;&lt; 8) | slot</c> and validated on deref by <see cref="TryResolveRef"/>, so a stale
        /// ref to a since-recycled slot reverts CLEANLY. Golden-neutral: generation starts at 0.</summary>
        public readonly int[] Generation = new int[MAX_ITEMS];

        /// <summary>Create a new ON-GROUND item instance of def index <paramref name="defId"/> with
        /// <paramref name="charges"/> charges at <paramref name="pos"/>. Returns a PACKED cross-tick reference
        /// (<see cref="PackRef"/>), or −1 if the store is full. Recycles a dead slot first (bumping its generation so
        /// stale packed refs to the prior occupant fail <see cref="TryResolveRef"/>), else appends a fresh one. EVERY
        /// live field is written here (the SoA-recycle contract).</summary>
        public int Create(int defId, int charges, FixedVec3 pos)
        {
            int slot;
            if (_freeCount > 0)
            {
                slot = _freeList[--_freeCount]; // reuse a dead slot (LIFO, deterministic)
                Generation[slot]++;             // bump generation so stale packed refs to the prior occupant fail TryResolveRef
            }
            else if (Count < MAX_ITEMS)
                slot = Count++;                 // append a fresh slot (Generation stays 0 — never recycled)
            else
                return -1;                      // all MAX_ITEMS slots are simultaneously live

            Alive[slot]           = true;
            DefId[slot]           = defId;
            Charges[slot]         = charges;
            PosX[slot]            = pos.X;
            PosZ[slot]            = pos.Z;
            Held[slot]            = false;
            CarrierHeroSlot[slot] = NO_CARRIER;
            return PackRef(slot);
        }

        /// <summary>Destroy an item instance — marks the slot dead and returns it to the free-list for reuse.
        /// Bounds + double-free guarded (mirrors BuildingStore.Destroy).</summary>
        public void Destroy(int slot)
        {
            if (slot < 0 || slot >= Count || !Alive[slot]) return;
            Alive[slot] = false;
            _freeList[_freeCount++] = slot;
        }

        /// <summary>Restore the store to its EXACT post-construction state for the Edit↔Play reset — zero every SoA
        /// array + the free-list + generation counters and reset <see cref="Count"/> to 0. A cleared store is
        /// byte-for-byte equal to <c>new ItemStore()</c> (items ARE folded into SimChecksum, so this must be exact).</summary>
        public void Clear()
        {
            System.Array.Clear(Alive);           System.Array.Clear(DefId);       System.Array.Clear(Charges);
            System.Array.Clear(PosX);            System.Array.Clear(PosZ);        System.Array.Clear(Held);
            System.Array.Clear(CarrierHeroSlot); System.Array.Clear(Generation);  System.Array.Clear(_freeList);
            _freeCount = 0;
            Count      = 0;
        }

        /// <summary>
        /// Story 11.3 (SP save/load): restore the private high-water <see cref="Count"/> + free-list after the
        /// persistence layer has written the SoA arrays (incl. the public <see cref="Generation"/>) directly. Godot-free
        /// integer bookkeeping only; copies at most <see cref="MAX_ITEMS"/> free-list entries (defensive).
        /// </summary>
        /// <summary>Story 11.3 (SP save/load): the active portion of the recycle free-list (LIFO order preserved) for a
        /// save — the exact next slot <see cref="Create"/> will reuse. Returns a fresh copy.</summary>
        public int[] CaptureFreeList()
        {
            var copy = new int[_freeCount];
            System.Array.Copy(_freeList, copy, _freeCount);
            return copy;
        }

        public void RestoreManagement(int count, int[] freeList, int freeCount)
        {
            Count = count < 0 ? 0 : (count > MAX_ITEMS ? MAX_ITEMS : count);
            System.Array.Clear(_freeList);
            int n = freeCount < 0 ? 0 : (freeCount > MAX_ITEMS ? MAX_ITEMS : freeCount);
            if (freeList != null)
                for (int i = 0; i < n && i < freeList.Length; i++) _freeList[i] = freeList[i];
            _freeCount = n;
        }

        /// <summary>Pack a live item slot into a generation-stamped CROSS-TICK reference
        /// <c>(Generation[slot] &lt;&lt; 8) | slot</c>. GOLDEN-NEUTRAL: at generation 0, <c>PackRef(slot) == slot</c>.</summary>
        public int PackRef(int slot) => (Generation[slot] << 8) | slot;

        /// <summary>Resolve a packed item reference back to a live slot. Returns true and the slot iff it is in bounds,
        /// ALIVE, and the generation still matches (the SAME instance occupies it). A stale ref returns false. The −1
        /// sentinel resolves false (<c>−1 &amp; 0xFF == 255 ≥ Count</c>).</summary>
        public bool TryResolveRef(int packed, out int slot)
        {
            slot = packed & 0xFF;
            return slot < Count && Alive[slot] && Generation[slot] == (packed >> 8);
        }
    }
}
