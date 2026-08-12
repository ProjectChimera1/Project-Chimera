#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Economy;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Sim
{
    /// <summary>
    /// DW-672 — the GENERALIZATION of DW-517's BuildingSystem-only wiring guard to EVERY system
    /// <see cref="SimulationHost"/> injects collaborators into AFTER construction.
    ///
    /// <para><b>The gap this closes.</b> DW-517 proved that the production host wires <c>BuildingSystem</c>'s three
    /// opt-in <c>Set*</c> seams, and its recorded decision deliberately scoped the sweep to that one system. But the
    /// host wires the SAME opt-in pattern on four more: <c>AbilityCastSystem</c>, <c>CombatSystem</c>,
    /// <c>ProjectileSystem</c> and <c>HeroXpSystem</c> all take their <c>DslSimEventFeed</c> through
    /// <c>SetDslSimEvents</c>, and the last three are reached by INDEX out of the fixed-order system array
    /// (<c>((CombatSystem)_systems[9]).SetDslSimEvents(...)</c>). A forgotten or deleted wire there is silently
    /// feature-disabling in exactly the DW-517 way: a <c>_dslSimEvents == null</c> combat system raises no
    /// <c>unit_attacked</c>/<c>unit_died</c> sim event, so every trigger a creator authored against those events stops
    /// firing — with nothing in the suite red, because every DSL-event test wires its own feed by hand.</para>
    ///
    /// <para><b>What this file asserts.</b> Against a REAL <see cref="SimulationHost"/> — the same composition root
    /// <c>ServerBootstrap</c> and the scene bootstrap use — that (1) every discovered post-construction seam on every
    /// wired system type is non-null after construction, (2) the collaborator wired is the host's OWN instance and not
    /// a private copy, and (3) all five systems really do share ONE <c>DslSimEventFeed</c>, which is the property the
    /// index-based casts exist to produce and the one a reordered system array would break.</para>
    ///
    /// <para>The discovery rule is the shared <see cref="WiringSeamSweep"/> (lifted verbatim from DW-517), so a NEW
    /// seam added the same opt-in way — on any system named here — is swept automatically and must not have to edit
    /// this file. A new SYSTEM does need a row in <see cref="WiredSystems"/>; the non-vacuity pin below fails loudly if
    /// a listed system stops being discoverable, and <c>SystemOrderTest</c> owns the array-order contract itself.</para>
    ///
    /// <para>Godot-free and <see cref="Fixed"/>-only: it constructs a host and reads private fields by reflection, and
    /// never ticks the loop — so it folds nothing into <c>SimChecksum</c> and moves no golden.</para>
    /// </summary>
    public class PostConstructionWiringGuardTests
    {
        // ── The systems under guard, and how to reach the host's instance of each ──────────────────

        /// <summary>
        /// Every system type <see cref="SimulationHost"/> hands a collaborator to AFTER constructing it, paired with
        /// the accessor that resolves the host's own instance. <c>BuildingSystem</c> is included deliberately even
        /// though <c>BuildingSystemWiringGuardTests</c> also covers it: this file is the CLASS-level guard, and
        /// leaving the one system with dedicated coverage out would make the sweep's own coverage a special case.
        /// Resolution is by TYPE out of <c>SimulationHost.Systems</c> (not by the array index the host wires with), so
        /// a legitimate reorder does not break this guard and a REMOVED system fails naming itself.
        /// </summary>
        private static readonly (Type SystemType, string Why)[] WiredSystems =
        {
            (typeof(BuildingSystem),    "DW-207/7.13/11.4 — node store + DSL events + combat cues"),
            (typeof(AbilityCastSystem), "Story 7.13 — ability_cast DSL sim events"),
            (typeof(CombatSystem),      "Story 7.13 — unit_attacked/unit_died DSL sim events"),
            (typeof(ProjectileSystem),  "Story 7.13 — projectile-impact DSL sim events"),
            (typeof(HeroXpSystem),      "Story 7.13 — hero level/death DSL sim events"),
        };

        /// <summary>
        /// Per-system seams the production host deliberately leaves unwired. EMPTY today, on every system. A new entry
        /// needs the same written justification the DW-19 clear-sweep allowlist demands — "the host has no such
        /// collaborator" is a design decision, not a test-fixing move, and an unwired seam means the feature behind it
        /// is DISABLED in the shipped game.
        /// </summary>
        private static readonly string[] Allowlist = Array.Empty<string>();

        /// <summary>The seams that exist today, per system. Pinned so a rename/removal cannot silently empty the sweep
        /// and turn the assertions below into a vacuous pass. Containment, not equality — a NEW correctly-wired seam
        /// flows into the sweep automatically and must NOT have to edit this list.</summary>
        private static readonly Dictionary<Type, string[]> KnownSeams = new()
        {
            [typeof(BuildingSystem)]    = new[] { "SetResourceNodes", "SetDslSimEvents", "SetCombatEvents" },
            [typeof(AbilityCastSystem)] = new[] { "SetDslSimEvents" },
            [typeof(CombatSystem)]      = new[] { "SetDslSimEvents" },
            [typeof(ProjectileSystem)]  = new[] { "SetDslSimEvents" },
            [typeof(HeroXpSystem)]      = new[] { "SetDslSimEvents" },
        };

        // ── (1) Every seam on every wired system is filled ────────────────────────────────────────

        [Fact]
        public void ProductionHost_WiresEveryPostConstructionSeam_OnEverySystemItInjectsInto()
        {
            SimulationHost host = NewProductionHost();

            var unwired = new List<string>();
            foreach ((Type systemType, string why) in WiredSystems)
            {
                object system = Resolve(host, systemType);
                foreach ((MethodInfo setter, FieldInfo field) in WiringSeamSweep.Seams(systemType))
                {
                    if (WiringSeamSweep.Contains(Allowlist, setter.Name)) continue;
                    if (field.GetValue(system) is null)
                        unwired.Add($"{systemType.Name}.{setter.Name} -> {field.Name} ({field.FieldType.Name})  [{why}]");
                }
            }

            Assert.True(unwired.Count == 0,
                "SimulationHost left post-construction seam(s) unwired: [" + string.Join(", ", unwired) + "]. Each is " +
                "an opt-in collaborator whose feature is DISABLED while the field is null (DW-672, generalizing " +
                "DW-517's BuildingSystem-only guard across every system the host injects into). Wire it in the " +
                "SimulationHost ctor beside the other Set* calls, or add a justified entry to this file's Allowlist " +
                "explaining why the shipped game runs without it.");
        }

        // ── (2) …with the host's OWN collaborator, not a private copy ─────────────────────────────

        /// <summary>
        /// The other half of DW-517's node-store assertion, generalized: a seam filled with SOME instance of the right
        /// type is not enough. <c>ScenarioDirector</c> drains the host's single <c>DslSimEvents</c> feed, so a system
        /// wired to any other feed instance pushes events nobody reads — indistinguishable from not wiring it at all,
        /// and invisible to a mere non-null check.
        /// </summary>
        [Fact]
        public void ProductionHost_WiresItsOWNCollaborators_NotPrivateCopies()
        {
            SimulationHost host = NewProductionHost();

            var wrong = new List<string>();
            foreach ((Type systemType, string _) in WiredSystems)
            {
                object system = Resolve(host, systemType);
                foreach ((MethodInfo setter, FieldInfo field) in WiringSeamSweep.Seams(systemType))
                {
                    if (WiringSeamSweep.Contains(Allowlist, setter.Name)) continue;
                    object? wired = field.GetValue(system);
                    object? owned = HostCollaboratorOfType(host, field.FieldType);
                    if (owned is null) continue;               // the host owns no store of this type — nothing to compare
                    if (!ReferenceEquals(owned, wired))
                        wrong.Add($"{systemType.Name}.{field.Name}");
                }
            }

            Assert.True(wrong.Count == 0,
                "These systems were wired with an instance that is NOT the host's own collaborator: [" +
                string.Join(", ", wrong) + "]. A private copy is worse than a null field — the feature LOOKS wired " +
                "while every value it produces lands where nothing reads it.");
        }

        /// <summary>The concrete teeth behind the generic assertion above: all five systems must share the host's ONE
        /// <c>DslSimEventFeed</c>. This is exactly what the host's index-based casts
        /// (<c>((CombatSystem)_systems[9]).SetDslSimEvents(...)</c>) exist to produce, and the single property a
        /// dropped line or a mis-indexed cast destroys.</summary>
        [Fact]
        public void ProductionHost_GivesEverySimEventProducer_TheSameFeedTheDirectorDrains()
        {
            SimulationHost host = NewProductionHost();

            foreach ((Type systemType, string why) in WiredSystems)
            {
                object system = Resolve(host, systemType);
                FieldInfo feed = WiringSeamSweep.RequireSoleFieldOfType(systemType, typeof(DslSimEventFeed));
                Assert.True(ReferenceEquals(host.DslSimEvents, feed.GetValue(system)),
                    $"{systemType.Name} does not hold SimulationHost.DslSimEvents ({why}). Every sim-event PRODUCER " +
                    "must push into the single feed ScenarioDirector drains, or the triggers authored against its " +
                    "events silently never fire.");
            }
        }

        // ── (3) Non-vacuity: the sweep must keep seeing what it claims to cover ───────────────────

        [Fact]
        public void WiringSweep_StillSeesEveryKnownSeam_AndTheAllowlistNamesOnlyRealOnes()
        {
            var everyDiscovered = new List<string>();

            foreach ((Type systemType, string _) in WiredSystems)
            {
                var discovered = new List<string>();
                foreach ((MethodInfo setter, FieldInfo _) in WiringSeamSweep.Seams(systemType))
                {
                    discovered.Add(setter.Name);
                    everyDiscovered.Add(setter.Name);
                }

                Assert.True(discovered.Count > 0,
                    $"{systemType.Name} exposes NO post-construction seam to the sweep, so its row in WiredSystems " +
                    "guards nothing. Either it stopped injecting collaborators that way (drop the row deliberately) " +
                    "or the discovery rule stopped matching it, which silently empties the completeness guard.");

                foreach (string seam in KnownSeams[systemType])
                    Assert.True(discovered.Contains(seam),
                        $"{systemType.Name} seam '{seam}' is no longer discovered by the wiring sweep (found: " +
                        $"[{string.Join(", ", discovered)}]). Either it was renamed/removed — update KnownSeams " +
                        "deliberately — or the sweep's discovery rule stopped matching it.");
            }

            foreach (string allowed in Allowlist)
                Assert.True(everyDiscovered.Contains(allowed),
                    $"Wiring-sweep allowlist entry '{allowed}' no longer resolves to a seam on any guarded system. " +
                    "Remove the stale exemption rather than leaving it to exempt nothing.");
        }

        // ── Fixture + resolution ──────────────────────────────────────────────────────────────────

        /// <summary>A minimal faction — the guard never ticks, so nothing here needs content beyond a valid def.</summary>
        private static FactionDefinition GuardFaction()
        {
            var f = new FactionDefinition { Id = "guard", DisplayName = "Guard" };
            f.Units.Add(new UnitDefinition { Id = "grunt", Category = "Melee", Hp = 100f, Speed = 3f, Supply = 1, CostOre = 10 });
            return f;
        }

        /// <summary>The REAL production composition root — the same call ServerBootstrap / the scene bootstrap make.</summary>
        private static SimulationHost NewProductionHost() =>
            SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), GuardFaction(), GuardFaction());

        /// <summary>The host's instance of <paramref name="systemType"/>, resolved BY TYPE out of the registered
        /// system list (plus the two field-held systems the host also exposes as properties). Deliberately not by the
        /// array index the host wires with: the index contract belongs to <c>SystemOrderTest</c>, and this guard is
        /// about the WIRING, which must survive a legitimate reorder.</summary>
        private static object Resolve(SimulationHost host, Type systemType)
        {
            foreach (ISimSystem s in host.Systems)
                if (systemType.IsInstanceOfType(s)) return s;

            throw new InvalidOperationException(
                $"SimulationHost registers no {systemType.Name}, so this file's WiredSystems row names a system the " +
                "production host no longer builds. Reconcile the row deliberately — a dead row guards nothing.");
        }

        /// <summary>The host's OWN collaborator of <paramref name="type"/>, or null when the host exposes none. Read
        /// off the host's public surface by type, so it needs no per-collaborator table.</summary>
        private static object? HostCollaboratorOfType(SimulationHost host, Type type)
        {
            foreach (PropertyInfo p in typeof(SimulationHost).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length != 0) continue;
                if (p.PropertyType != type) continue;
                return p.GetValue(host);
            }
            return null;
        }
    }
}
