#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace ProjectChimera.Sim.Tests.Sim
{
    /// <summary>
    /// DW-672 — the SHARED, type-agnostic post-construction WIRING-SEAM discovery, lifted out of
    /// <c>BuildingSystemWiringGuardTests</c> (where DW-517 first built it for <c>BuildingSystem</c> alone) so every
    /// system <c>SimulationHost</c> injects collaborators into AFTER construction can be swept the same way.
    ///
    /// <para><b>The gap this closes.</b> DW-517's sweep was BuildingSystem-scoped, as its recorded decision specified.
    /// But <c>AbilityCastSystem</c>, <c>CombatSystem</c>, <c>ProjectileSystem</c> and <c>HeroXpSystem</c> take their
    /// <c>DslSimEventFeed</c> through the IDENTICAL opt-in <c>Set*</c> pattern — and the host wires those four out of a
    /// FIXED-ORDER array by index (<c>((CombatSystem)_systems[9]).SetDslSimEvents(...)</c>), which is strictly more
    /// fragile than the three direct <c>BuildSys.Set*</c> calls the DW-517 guard covers: reordering the system array
    /// would make those casts throw, but DELETING a line, or adding a new system that takes a collaborator the same
    /// way, is silent. A forgotten wire there disables the feature behind it with nothing red — a
    /// <c>_dslSimEvents == null</c> combat system simply raises no <c>unit_attacked</c>/<c>unit_died</c> DSL event, so
    /// every trigger authored against it stops firing and the suite stays green.</para>
    ///
    /// <para><b>The discovery rule</b> (verbatim from DW-517, now parameterised by system type): a DECLARED public
    /// instance method returning <c>void</c>, named <c>Set*</c>, taking exactly ONE reference-typed non-string
    /// parameter — precisely the shape of an injected collaborator, and deliberately excluding two-argument per-slot
    /// overrides (<c>BuildingSystem.SetFactionDef(Faction, def)</c>) and bool-returning player ORDERS
    /// (<c>BuildingSystem.SetRallyCommand(...)</c>). The parameter TYPE resolves the backing field, so a field rename
    /// cannot break a guard built on this.</para>
    ///
    /// <para>Godot-free, reflection-only; it inspects types and reads fields and never ticks anything, so it folds
    /// nothing into <c>SimChecksum</c> and moves no golden.</para>
    /// </summary>
    public static class WiringSeamSweep
    {
        /// <summary>
        /// Every post-construction collaborator seam declared on <paramref name="systemType"/>, paired with the field
        /// it fills, in deterministic (ordinal setter-name) order so a failure message is stable.
        /// </summary>
        public static IReadOnlyList<(MethodInfo Setter, FieldInfo Field)> Seams(Type systemType)
        {
            var seams = new List<(MethodInfo, FieldInfo)>();
            foreach (MethodInfo m in systemType.GetMethods(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.IsSpecialName) continue;                                    // property accessors
                if (m.ReturnType != typeof(void)) continue;
                if (!m.Name.StartsWith("Set", StringComparison.Ordinal)) continue;

                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != 1) continue;

                Type t = ps[0].ParameterType;
                if (!t.IsClass || t == typeof(string)) continue;                  // reference-typed collaborators only

                seams.Add((m, RequireSoleFieldOfType(systemType, t)));
            }

            seams.Sort((a, b) => string.CompareOrdinal(a.Item1.Name, b.Item1.Name));
            return seams;
        }

        /// <summary>The single instance field of <paramref name="systemType"/> whose type is
        /// <paramref name="fieldType"/> — the field a <c>Set*</c> seam of that parameter type fills. Walks the full
        /// base-type chain (private base fields included), like the DW-196 clear sweep.</summary>
        public static FieldInfo RequireSoleFieldOfType(Type systemType, Type fieldType)
        {
            var matches = new List<FieldInfo>();
            foreach (FieldInfo f in ClearCompletenessSweep.InstanceFieldsOf(systemType))
                if (f.FieldType == fieldType) matches.Add(f);

            Assert.True(matches.Count == 1,
                $"Expected {systemType.Name} to hold EXACTLY ONE {fieldType.Name} field (the one its Set* seam fills) " +
                $"but found {matches.Count}. The wiring sweep resolves a seam's backing field by parameter type; " +
                "if a second field of this type is legitimate, teach the sweep how to disambiguate rather than " +
                "deleting the guard.");
            return matches[0];
        }

        /// <summary>Case-sensitive membership test used by the per-system allowlists.</summary>
        public static bool Contains(IReadOnlyCollection<string> names, string name)
        {
            foreach (string n in names)
                if (string.Equals(n, name, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
