#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// An index↔<see cref="AbilityDefinition"/> table over a set of VALIDATED abilities (Story 2.4a, FR-11). The
    /// runtime cast path stores a per-slot <c>int</c> index into this table (<see cref="EntityWorld.AbilityId"/>),
    /// not an id string — so the tick never enumerates a dictionary or compares strings. Indices are assigned by
    /// ascending ability <c>Id</c> (ordinal, STABLE) so the mapping is deterministic regardless of input/load order.
    ///
    /// <para><b>Determinism.</b> Pure C# (no <c>using Godot;</c>, no <c>float</c>). The id→index lookup is a linear
    /// scan over the (few) abilities — used ONLY at load/link time (<see cref="UnitDefinition.ResolveAbilities"/>),
    /// never in the tick (the tick uses the already-resolved int index into <see cref="All"/>). The registry must
    /// NOT read the filesystem inside <c>SimulationHost</c> (the host stays Godot-free + golden-hermetic): the host
    /// takes a pre-built registry (or the <see cref="Empty"/> default); only the game/MainScene path calls
    /// <see cref="LoadFromDirectory"/>.</para>
    /// </summary>
    public sealed class AbilityRegistry
    {
        // index → def, sorted ascending by Id (ordinal). The stable index a unit's AbilityIndices reference.
        private readonly AbilityDefinition[] _byIndex;

        /// <summary>Number of indexed abilities.</summary>
        public int Count => _byIndex.Length;

        /// <summary>The abilities in stable ascending-Id index order.</summary>
        public IReadOnlyList<AbilityDefinition> All => _byIndex;

        /// <summary>The empty registry (no abilities) — the default <see cref="Core.Sim.SimulationHost"/> uses when
        /// no abilities are loaded, so existing 2-faction + golden callers stay scenario-identical.</summary>
        public static readonly AbilityRegistry Empty = new AbilityRegistry(Array.Empty<AbilityDefinition>());

        /// <summary>
        /// In-memory ctor (tests / golden / the host): index a set of ALREADY-VALIDATED abilities. Sorts by
        /// <see cref="AbilityDefinition.Id"/> ascending (ordinal, stable <see cref="Enumerable.OrderBy{TSource,TKey}(IEnumerable{TSource},Func{TSource,TKey},IComparer{TKey})"/>
        /// — never the unstable <c>Array.Sort</c>) so the index assignment is deterministic regardless of input order.
        /// </summary>
        public AbilityRegistry(IReadOnlyList<AbilityDefinition> validated)
        {
            _byIndex = (validated ?? (IReadOnlyList<AbilityDefinition>)Array.Empty<AbilityDefinition>())
                .OrderBy(d => d.Id, StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>The ability at <paramref name="index"/> (the stable ascending-Id index a unit slot stores).</summary>
        public AbilityDefinition Get(int index) => _byIndex[index];

        /// <summary>
        /// Index of the ability whose <see cref="AbilityDefinition.Id"/> equals <paramref name="id"/>, or −1 if
        /// absent. Load/link-time only (linear scan over the few abilities) — never called in the tick.
        /// </summary>
        public int IndexOf(string id)
        {
            for (int i = 0; i < _byIndex.Length; i++)
                if (_byIndex[i].Id == id) return i;
            return -1;
        }

        /// <summary>
        /// Game/MainScene path (NOT the host): load + validate every <c>*.json</c> under <paramref name="absDir"/>
        /// through <see cref="AbilityLoader.LoadFromFile"/>, keep only <see cref="AbilityValidationResult.Ok"/>
        /// results, and index them. A null/missing directory yields <see cref="Empty"/>. Files are visited in a
        /// deterministic ordinal order (the final index order is by ability Id regardless, but a deterministic walk
        /// keeps any <paramref name="onSkipped"/> reporting stable). Pass an absolute OS path (resolve <c>res://</c>
        /// via <c>ProjectSettings.GlobalizePath</c> in the presentation layer first).
        /// </summary>
        public static AbilityRegistry LoadFromDirectory(string absDir, Action<string>? onSkipped = null)
        {
            if (string.IsNullOrEmpty(absDir) || !Directory.Exists(absDir))
                return Empty;

            var defs = new List<AbilityDefinition>();
            foreach (string file in Directory.GetFiles(absDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                AbilityValidationResult r = AbilityLoader.LoadFromFile(file);
                if (r.Ok) defs.Add(r.Value.Value);
                else onSkipped?.Invoke(Path.GetFileName(file));
            }
            return new AbilityRegistry(defs);
        }
    }
}
