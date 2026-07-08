#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// An index↔<see cref="ItemDefinition"/> table over a set of VALIDATED items (Story 3.15) — mirrors
    /// <see cref="AbilityRegistry"/>. Runtime references an <c>int</c> index (stored in <c>ItemStore.DefId</c>), not an
    /// id string, so the tick never enumerates a dictionary or compares strings. Indices are assigned by ascending
    /// item <c>Id</c> (ordinal, STABLE) so the mapping is deterministic regardless of input/load order.
    ///
    /// <para><b>Determinism.</b> Pure C# (no <c>using Godot;</c>, no <c>float</c>). The id→index lookup is a linear
    /// scan used ONLY at load/link time (scenario placement), never in the tick. The host takes a pre-built registry
    /// (or the <see cref="Empty"/> default); only the game/MainScene path calls <see cref="LoadFromDirectory"/>.</para>
    /// </summary>
    public sealed class ItemRegistry
    {
        private readonly ItemDefinition[] _byIndex;

        /// <summary>Number of indexed items.</summary>
        public int Count => _byIndex.Length;

        /// <summary>The items in stable ascending-Id index order.</summary>
        public IReadOnlyList<ItemDefinition> All => _byIndex;

        /// <summary>The empty registry — the default the host uses when no items are loaded, so existing golden/2-faction
        /// callers stay scenario-identical.</summary>
        public static readonly ItemRegistry Empty = new ItemRegistry(Array.Empty<ItemDefinition>());

        /// <summary>In-memory ctor (tests / golden / the host): index a set of ALREADY-VALIDATED items. Sorts by
        /// <see cref="ItemDefinition.Id"/> ascending (ordinal, stable) so the index assignment is deterministic.</summary>
        public ItemRegistry(IReadOnlyList<ItemDefinition> validated)
        {
            _byIndex = (validated ?? (IReadOnlyList<ItemDefinition>)Array.Empty<ItemDefinition>())
                .OrderBy(d => d.Id, StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>The item at <paramref name="index"/> (the stable ascending-Id index an <c>ItemStore</c> instance stores).</summary>
        public ItemDefinition Get(int index) => _byIndex[index];

        /// <summary>The item at <paramref name="index"/>, or null if the index is out of range (defensive read for the tick).</summary>
        public ItemDefinition? TryGet(int index) => (uint)index < (uint)_byIndex.Length ? _byIndex[index] : null;

        /// <summary>Index of the item whose <see cref="ItemDefinition.Id"/> equals <paramref name="id"/>, or −1 if
        /// absent. Load/link-time only (linear scan over the few items) — never called in the tick.</summary>
        public int IndexOf(string id)
        {
            for (int i = 0; i < _byIndex.Length; i++)
                if (_byIndex[i].Id == id) return i;
            return -1;
        }

        /// <summary>Game/MainScene path (NOT the host): load + validate every <c>*.json</c> under <paramref name="absDir"/>
        /// through <see cref="ItemLoader.LoadFromFile"/>, keep only <see cref="ItemValidationResult.Ok"/> results, and
        /// index them. A null/missing directory yields <see cref="Empty"/>. Files are visited in a deterministic ordinal
        /// order. Pass an absolute OS path (resolve <c>res://</c> via <c>ProjectSettings.GlobalizePath</c> first).</summary>
        public static ItemRegistry LoadFromDirectory(string absDir, Action<string>? onSkipped = null)
        {
            if (string.IsNullOrEmpty(absDir) || !Directory.Exists(absDir))
                return Empty;

            var defs = new List<ItemDefinition>();
            foreach (string file in Directory.GetFiles(absDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                ItemValidationResult r = ItemLoader.LoadFromFile(file);
                if (r.Ok) defs.Add(r.Value.Value);
                else onSkipped?.Invoke(Path.GetFileName(file));
            }
            return new ItemRegistry(defs);
        }
    }
}
