#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// An index↔<see cref="BehaviorDefinition"/> table (Story 3.6, D-1) — a structural clone of
    /// <see cref="AbilityRegistry"/>. The Unit Card Editor + <see cref="UnitDefinitionValidator"/> read it to offer the
    /// behavior picker and to reject undefined / archetype-incompatible behavior refs. Indices are assigned by ascending
    /// <see cref="BehaviorDefinition.Id"/> (ordinal, stable) so the mapping is deterministic regardless of load order.
    ///
    /// <para><b>Authoring only.</b> Pure C# (no <c>using Godot;</c>, no <c>float</c>). Nothing in the sim consumes a
    /// behavior this story (D-2), so there is no <c>Resolve*</c> and no <see cref="SimulationHost"/> plumbing — only the
    /// game/MainScene path calls <see cref="LoadFromDirectory"/>; every other caller takes a pre-built registry or the
    /// <see cref="Empty"/> default.</para>
    /// </summary>
    public sealed class BehaviorRegistry
    {
        // index → def, sorted ascending by Id (ordinal). The stable index a unit's behaviors reference.
        private readonly BehaviorDefinition[] _byIndex;

        /// <summary>Number of indexed behaviors.</summary>
        public int Count => _byIndex.Length;

        /// <summary>The behaviors in stable ascending-Id index order.</summary>
        public IReadOnlyList<BehaviorDefinition> All => _byIndex;

        /// <summary>The empty registry (no behaviors) — the default when none are loaded.</summary>
        public static readonly BehaviorRegistry Empty = new BehaviorRegistry(Array.Empty<BehaviorDefinition>());

        /// <summary>
        /// In-memory ctor (tests / the game path): index a set of behaviors. Sorts by
        /// <see cref="BehaviorDefinition.Id"/> ascending (ordinal, stable <c>OrderBy</c> — never the unstable
        /// <c>Array.Sort</c>) so index assignment is deterministic regardless of input order.
        /// </summary>
        public BehaviorRegistry(IReadOnlyList<BehaviorDefinition> behaviors)
        {
            _byIndex = (behaviors ?? (IReadOnlyList<BehaviorDefinition>)Array.Empty<BehaviorDefinition>())
                .OrderBy(d => d.Id, StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>The behavior at <paramref name="index"/> (the stable ascending-Id index).</summary>
        public BehaviorDefinition Get(int index) => _byIndex[index];

        /// <summary>
        /// Index of the behavior whose <see cref="BehaviorDefinition.Id"/> equals <paramref name="id"/>, or −1 if absent.
        /// Load/link-time only (linear scan over the few behaviors) — never called in the tick.
        /// </summary>
        public int IndexOf(string id)
        {
            for (int i = 0; i < _byIndex.Length; i++)
                if (_byIndex[i].Id == id) return i;
            return -1;
        }

        /// <summary>
        /// Game/MainScene path: load every <c>*.json</c> under <paramref name="absDir"/>, keep only those that pass a
        /// minimal validity check (non-empty id + every <c>compatible_archetypes</c> token inside the 6-archetype set),
        /// and index them. A null/missing directory yields <see cref="Empty"/>. Files are visited in deterministic
        /// ordinal order (the final index order is by id regardless, but a deterministic walk keeps any
        /// <paramref name="onSkipped"/> reporting stable). Pass an absolute OS path (resolve <c>res://</c> via
        /// <c>ProjectSettings.GlobalizePath</c> in the presentation layer first).
        /// </summary>
        public static BehaviorRegistry LoadFromDirectory(string absDir, Action<string>? onSkipped = null)
        {
            if (string.IsNullOrEmpty(absDir) || !Directory.Exists(absDir))
                return Empty;

            var defs = new List<BehaviorDefinition>();
            foreach (string file in Directory.GetFiles(absDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                BehaviorDefinition? def = TryLoad(file);
                if (def != null) defs.Add(def);
                else onSkipped?.Invoke(Path.GetFileName(file));
            }
            return new BehaviorRegistry(defs);
        }

        /// <summary>Parse + minimally validate one behavior file; null (with the caller reporting a skip) on any failure.</summary>
        private static BehaviorDefinition? TryLoad(string file)
        {
            try
            {
                BehaviorDefinition? def = JsonSerializer.Deserialize<BehaviorDefinition>(File.ReadAllText(file));
                if (def == null || string.IsNullOrEmpty(def.Id)) return null;
                // A compatible_archetypes token outside the 6-archetype closed set names a non-existent archetype, so the
                // whole behavior is rejected (dropped at load). The closed set is the shared source of truth
                // UnitCategories.All (derived from the UnitCategory enum) — the same set UnitDefinitionValidator checks
                // against, so the two can never disagree on what an archetype is.
                if (def.CompatibleArchetypes != null)
                {
                    foreach (string token in def.CompatibleArchetypes)
                        if (Array.IndexOf(UnitCategories.All, token) < 0) return null;   // unknown archetype token → reject
                }
                return def;
            }
            catch
            {
                return null;
            }
        }
    }
}
