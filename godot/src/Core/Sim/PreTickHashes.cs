#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.AI;               // AiControlPlan
using ProjectChimera.Combat;           // DamageTable
using ProjectChimera.Core.Definitions; // StartStateHash, CanonicalModelHash, ContentHash, RulesetHash, MatchAgreementHash

namespace ProjectChimera.Core.Sim
{
    /// <summary>
    /// Unreal trial A3 (plan A §3.2, "H0"): the five 64-bit pre-tick agreement hashes plus <see cref="Tick0"/>, the
    /// <see cref="SimChecksum"/> of the world before the first step. ONE helper computes them for every caller
    /// (<see cref="SimSession"/>, the CLI, the NativeAOT library and the gating MainScene start-state probe), so two
    /// legs can only disagree through their INPUTS, never through a second copy of the recipe.
    ///
    /// <para>The inputs mirror the Godot client's own hash sites: the loaded faction list is every non-null per-slot
    /// definition in slot order (<c>MainScene.GatherLoadedFactions</c>, <c>MainScene.cs:925-932</c>, and the
    /// agreement gather at <c>MainScene.cs:745-748</c>), the ability and item registries are the ones the host holds
    /// (<c>MainScene.cs:953</c>), the damage table is the loaded one, and the initial input delay is
    /// <see cref="INITIAL_DELAY"/> (= <c>LockstepManager.INPUT_DELAY</c>, <c>LockstepManager.cs:45</c>, which is
    /// Godot-coupled and therefore not referenced from here). The AI plan is the one THIS match runs, the same value
    /// pushed into <see cref="SimulationHost.SetAiControlPlan"/> (<c>MatchAgreementHash.cs:62-67</c> requires it).</para>
    ///
    /// <para>Pure reads only: no hash here mutates a store, so computing them changes nothing about the run.</para>
    /// </summary>
    public readonly struct PreTickHashes : IEquatable<PreTickHashes>
    {
        /// <summary>The match's starting input delay folded into <see cref="MatchAgreementHash"/>; mirrors
        /// <c>LockstepManager.INPUT_DELAY</c> (<c>LockstepManager.cs:45</c>).</summary>
        public const int INITIAL_DELAY = 4;

        /// <summary>Number of values <see cref="CopyTo"/> writes: the five hashes, then <see cref="Tick0"/>.</summary>
        public const int VALUE_COUNT = 6;

        /// <summary><see cref="StartStateHash.Compute"/> of the applied model and the host's hero store.</summary>
        public readonly ulong StartState;
        /// <summary><see cref="CanonicalModelHash.Compute"/> of the applied model.</summary>
        public readonly ulong CanonicalModel;
        /// <summary><see cref="ContentHash.Compute"/> of the loaded factions, abilities, items and damage table.</summary>
        public readonly ulong Content;
        /// <summary><see cref="RulesetHash.Compute"/>.</summary>
        public readonly ulong Ruleset;
        /// <summary><see cref="MatchAgreementHash.Compute"/> (folds the ruleset, content, delay, roster, teams, AI plan
        /// and start state).</summary>
        public readonly ulong Agreement;
        /// <summary><see cref="SimulationHost.ComputeChecksumNow"/> before the first step (the 32-bit sim checksum).</summary>
        public readonly uint Tick0;

        public PreTickHashes(ulong startState, ulong canonicalModel, ulong content, ulong ruleset, ulong agreement, uint tick0)
        {
            StartState     = startState;
            CanonicalModel = canonicalModel;
            Content        = content;
            Ruleset        = ruleset;
            Agreement      = agreement;
            Tick0          = tick0;
        }

        /// <summary>
        /// Compute the five hashes and <see cref="Tick0"/> for <paramref name="host"/>, which must hold the applied
        /// <paramref name="model"/>. <paramref name="slotFactionDefs"/> is the per-slot definition array the match was
        /// built from (null entries are skipped, as the Godot gather does). Call it before the first
        /// <see cref="SimulationHost.StepOnce"/>; later it still returns the CURRENT checksum as <see cref="Tick0"/>.
        /// </summary>
        public static PreTickHashes Compute(SimulationHost host, ScenarioData model,
            IReadOnlyList<FactionDefinition?> slotFactionDefs, DamageTable damageTable, AiControlPlan aiPlan,
            int initialDelay = INITIAL_DELAY)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (damageTable == null) throw new ArgumentNullException(nameof(damageTable));

            List<FactionDefinition> loaded = GatherLoadedFactions(slotFactionDefs);
            return new PreTickHashes(
                StartStateHash.Compute(model, host.Heroes),
                CanonicalModelHash.Compute(model),
                ContentHash.Compute(loaded, host.AbilityRegistry, host.ItemRegistry, damageTable),
                RulesetHash.Compute(),
                MatchAgreementHash.Compute(initialDelay, model, host.Heroes, loaded,
                    host.AbilityRegistry, host.ItemRegistry, damageTable, aiPlan),
                host.ComputeChecksumNow());
        }

        /// <summary>Every non-null definition of <paramref name="slotFactionDefs"/>, in slot order — the exact gather
        /// <c>MainScene.GatherLoadedFactions</c> (<c>MainScene.cs:925-932</c>) performs over <c>SlotFactionDefs</c>.</summary>
        public static List<FactionDefinition> GatherLoadedFactions(IReadOnlyList<FactionDefinition?>? slotFactionDefs)
        {
            var list = new List<FactionDefinition>();
            if (slotFactionDefs == null) return list;
            for (int i = 0; i < slotFactionDefs.Count; i++)
            {
                FactionDefinition? fd = slotFactionDefs[i];
                if (fd != null) list.Add(fd);
            }
            return list;
        }

        /// <summary>Write the values in ABI order (start-state, canonical model, content, ruleset, agreement, tick0)
        /// into <paramref name="destination"/>, which must hold at least <see cref="VALUE_COUNT"/> entries.</summary>
        public void CopyTo(Span<ulong> destination)
        {
            if (destination.Length < VALUE_COUNT)
                throw new ArgumentException($"destination needs {VALUE_COUNT} entries", nameof(destination));
            destination[0] = StartState;
            destination[1] = CanonicalModel;
            destination[2] = Content;
            destination[3] = Ruleset;
            destination[4] = Agreement;
            destination[5] = Tick0;
        }

        public bool Equals(PreTickHashes other) =>
            StartState == other.StartState && CanonicalModel == other.CanonicalModel && Content == other.Content
            && Ruleset == other.Ruleset && Agreement == other.Agreement && Tick0 == other.Tick0;

        public override bool Equals(object? obj) => obj is PreTickHashes other && Equals(other);

        public override int GetHashCode() => unchecked((int)(StartState ^ CanonicalModel ^ Content ^ Ruleset ^ Agreement) ^ (int)Tick0);

        /// <summary>The trace-header spelling (plan A §3.5): <c>hash.&lt;name&gt;</c> keys, 16 hex digits, tick0 8.</summary>
        public override string ToString() =>
            "start_state=0x" + StartState.ToString("X16", System.Globalization.CultureInfo.InvariantCulture)
            + " canonical_model=0x" + CanonicalModel.ToString("X16", System.Globalization.CultureInfo.InvariantCulture)
            + " content=0x" + Content.ToString("X16", System.Globalization.CultureInfo.InvariantCulture)
            + " ruleset=0x" + Ruleset.ToString("X16", System.Globalization.CultureInfo.InvariantCulture)
            + " agreement=0x" + Agreement.ToString("X16", System.Globalization.CultureInfo.InvariantCulture)
            + " tick0=0x" + Tick0.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
    }
}
