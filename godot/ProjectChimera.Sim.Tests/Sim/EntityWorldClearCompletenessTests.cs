#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using ProjectChimera.Combat;   // AttackDelivery
using ProjectChimera.Core;
using ProjectChimera.Effects;  // StatusFlags
using Xunit;

namespace ProjectChimera.Sim.Tests.Sim
{
    /// <summary>
    /// DW-19 — an EXHAUSTIVE, self-pinning completeness guard for <see cref="EntityWorld.Clear"/>, the inverse-of-the-
    /// constructor call the Edit↔Play reset (<c>SimulationHost.ClearForReset</c>) is built on.
    ///
    /// <para>DW-196 lifted the machinery this class pioneered (the reflection sweep, the per-field dirty
    /// precondition, the synthetic array fill) into the shared <see cref="ClearCompletenessSweep"/>, and
    /// <c>StoreClearCompletenessTests</c> now drives the SAME sweep over every sibling store
    /// <c>SimulationHost.ClearForReset</c> fans out over — closing the "exhaustive over EntityWorld AND NOTHING
    /// ELSE" scope caveat this file used to carry. This class remains EntityWorld's fixture + its
    /// delegate-allowlist reconciliation guard.</para>
    ///
    /// <para>The existing keystone <c>SimResetTests.ClearForReset_LeavesEveryStoreEqualToFreshlyConstructed</c> is
    /// HAND-ENUMERATED: it asserts ~12 of <see cref="EntityWorld"/>'s ~70 SoA arrays, so a NEW per-entity array that a
    /// future story forgets to add to <see cref="EntityWorld.Clear"/> passes it silently and only surfaces as a
    /// multiplayer desync / a "reset != fresh boot" divergence much later. This test closes that blind spot by
    /// reflecting over EVERY instance field (public AND private, arrays AND scalars) and comparing a dirtied-then-
    /// <see cref="EntityWorld.Clear"/>ed world against a freshly-constructed one, naming the diverging field.</para>
    ///
    /// <para>Godot-free and integer/<see cref="Fixed"/>-only, like the rest of the Tier-1 suite. It asserts existing
    /// CORRECT behavior — it is a tripwire for future regressions, not a bug hunt.</para>
    /// </summary>
    public class EntityWorldClearCompletenessTests
    {
        // ── The exclusion allowlist (explicit + commented — never a silent skip) ────────────────

        /// <summary>
        /// The ONLY fields exempt from the fresh==cleared sweep. Both are host-lifetime delegate subscriptions that
        /// <see cref="EntityWorld.Clear"/> preserves by OMISSION — its body never touches either delegate, so whatever
        /// the <c>SimulationHost</c> bound at construction survives every reset. That is deliberate: the subscriptions
        /// belong to the host, not to the match, and clearing them would orphan every system that subscribed once at
        /// construction. (The behavior is pinned directly by
        /// <see cref="ClearAllowlist_NamesOnlyRealDelegateFields_SoARenameCannotWidenIt"/>, so it cannot silently
        /// change.) Nothing else may be added here without the same kind of justification.
        /// </summary>
        private static readonly string[] Allowlist =
        {
            nameof(EntityWorld.OnDestroy),               // Destroy-time modifier cleanup hook (host-owned)
            nameof(EntityWorld.OnUnitDefinitionApplied), // A2 def→SoA passive installer hook (host-owned)
        };

        // ── DW-19: the exhaustive sweep (driven through the shared DW-196 machinery) ───────────

        [Fact]
        public void Clear_LeavesEveryFieldEqualToFreshlyConstructed_ExhaustiveReflectionSweep()
        {
            var dirty = new EntityWorld();
            var fx = new StoreResetFixture("EntityWorld.Clear()", new EntityWorld(), dirty, () => dirty.Clear())
            {
                Allowlist = Allowlist,
                DirtyNonArrayState = () => HandDirtyWorld(dirty),
            };
            ClearCompletenessSweep.AssertClearRestoresFreshState(fx);
        }

        /// <summary>
        /// The allowlist must keep naming REAL fields. Without this, renaming (or deleting) <c>OnDestroy</c> would turn
        /// its allowlist entry into a dead string while the renamed field silently entered the sweep — or, worse, a
        /// future rename could be "fixed" by widening the allowlist to a name that no longer exists, quietly excluding
        /// nothing and hiding the real divergence. Also pins that the allowlist stays exactly the delegate set.
        /// </summary>
        [Fact]
        public void ClearAllowlist_NamesOnlyRealDelegateFields_SoARenameCannotWidenIt()
        {
            // The sweep enumerates fields via ClearCompletenessSweep.InstanceFieldsOf, which walks the full
            // base-type chain (DeclaredOnly per level) — so unlike the pre-DW-196 GetFields(Instance) sweep, a base
            // class inserted above EntityWorld can no longer silently shrink the swept set. This test therefore
            // reconciles the allowlist against that same chain-walking enumeration.
            var delegateFields = new List<string>();
            foreach (FieldInfo f in ClearCompletenessSweep.InstanceFieldsOf(typeof(EntityWorld)))
                if (typeof(Delegate).IsAssignableFrom(f.FieldType)) delegateFields.Add(f.Name);
            delegateFields.Sort(StringComparer.Ordinal);

            var allowed = new List<string>(Allowlist);
            allowed.Sort(StringComparer.Ordinal);

            Assert.True(delegateFields.Count == allowed.Count,
                $"EntityWorld's delegate-typed fields are [{string.Join(", ", delegateFields)}] but the sweep's " +
                $"allowlist is [{string.Join(", ", allowed)}]. Every delegate field must be an explicitly justified " +
                "allowlist entry (Clear() preserves them by omission) — and nothing that is NOT a delegate may be " +
                "exempt. Reconcile the two deliberately; do not just widen the allowlist to make this pass.");
            Assert.Equal(allowed, delegateFields);

            foreach (string name in Allowlist)
            {
                FieldInfo? f = typeof(EntityWorld).GetField(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.True(f is not null, $"Clear-sweep allowlist entry '{name}' no longer resolves to an EntityWorld field.");
                Assert.True(typeof(Delegate).IsAssignableFrom(f!.FieldType),
                    $"Clear-sweep allowlist entry '{name}' is no longer a delegate — only host-lifetime subscriptions " +
                    "may be exempt from the fresh==cleared sweep.");
            }

            // And the preserved-by-design behavior the allowlist exists for: Clear() keeps the subscriptions attached.
            var w = new EntityWorld();
            Action<int> noop = _ => { };
            w.OnDestroy = noop;
            w.OnUnitDefinitionApplied = noop;
            w.Clear();
            Assert.Same(noop, w.OnDestroy);
            Assert.Same(noop, w.OnUnitDefinitionApplied);
        }

        // ── Fixture: a world dirtied across every kind of state Clear() must restore ────────────

        /// <summary>Spawn units (dirtying the SoA arrays + free-list + id counters), advance the shared RNG, and set
        /// the non-array sim-globals (height-vision toggle/bonus, the elevation grid, the pathability grid) — the
        /// state classes a public-array-only sweep would miss. The shared sweep's reflection-driven synthetic fill
        /// runs AFTER this and overwrites every element of every array field, so the per-entity writes below are NOT
        /// what dirties those arrays for the assertion — the Create/Destroy calls drive the real allocation,
        /// free-list and id-counter paths (AliveCount, _nextId, _freeCount and Rng are the state the synthetic fill
        /// CANNOT reach), and a realistically-shaped world documents what these arrays hold.</summary>
        private static void HandDirtyWorld(EntityWorld w)
        {
            // Sim-globals first, so the spawns below sample the injected elevation grid (dirtying Elevation too).
            w.HeightAdvantageVision    = true;
            w.HeightVisionBonusPerStep = Fixed.FromInt(9);
            w.SetElevationGrid(new ElevationGrid(new[] { Fixed.FromInt(3) }, 1, 1, Fixed.Zero, Fixed.Zero, Fixed.One));

            for (int i = 0; i < 8; i++)
            {
                int id = w.Create(new FixedVec3(Fixed.FromInt(i), Fixed.Zero, Fixed.FromInt(i * 2)),
                                  Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
                w.AttackTarget[id]            = (id + 1) % 8;
                w.CommandTarget[id]           = id;
                w.CommandState[id]            = UnitCommand.AttackMove;
                w.StatusFlagsOf[id]           = StatusFlags.Stunned;
                w.AbilityCount[id]            = 1;
                w.AbilityCooldownTicks[id * EntityWorld.MAX_ABILITIES_PER_UNIT + 0] = 7;
                w.AbilityId[id * EntityWorld.MAX_ABILITIES_PER_UNIT + 0]            = 0;
                w.Energy[id]                  = Fixed.FromInt(25);
                w.MaxEnergy[id]               = Fixed.FromInt(50);
                w.Delivery[id]                = AttackDelivery.Projectile;
                w.ProjectileSpeed[id]         = Fixed.FromInt(6);
                w.XpBounty[id]                = Fixed.FromInt(11);
                w.SupplyCost[id]              = 4;
                w.CarryAmount[id]             = Fixed.FromInt(5);
                w.SplashRadius[id]            = Fixed.FromInt(2);
            }

            // Destroy a few so the free-list / _freeCount / KillerOf / generation-ish state is dirty too.
            w.Destroy(1);
            w.Destroy(4);

            w.Rng.NextInt(1000); // advance the folded RNG state off DEFAULT_RNG_SEED

            // Story 6.5 sim-global: the pathability grid Clear() drops. The ctor only accepts an exactly
            // PathabilityGrid.CELL_COUNT-long mask (anything else is silently replaced by an empty one).
            w.SetPathabilityGrid(new ProjectChimera.Navigation.PathabilityGrid(
                new bool[ProjectChimera.Navigation.PathabilityGrid.CELL_COUNT]));
        }
    }
}
