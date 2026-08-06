#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;              // EntityWorld, Fixed, FixedVec3, Faction, UnitSnapshot, PinnedAbilityWiring
using ProjectChimera.Core.Definitions;  // UnitDefinition, AbilityDefinition, AbilityRegistry
using Xunit;

namespace ProjectChimera.Sim.Tests.Core
{
    /// <summary>
    /// DW-54 — <see cref="EntityWorld.SnapshotUnit"/> pins the RESOLVED ability wiring, so a delete→undo that lands
    /// after the source def moved under it restores the abilities the deleted unit actually had.
    ///
    /// <para><b>The defect.</b> Story 3.17 deliberately re-derives a restored unit's authored fields by routing its
    /// pinned <see cref="UnitDefinition"/> back through <see cref="EntityWorld.ApplyUnitDefinition"/>. That is right
    /// for authored VALUES, but the ability slots are not authored data — they are <see cref="AbilityRegistry"/>
    /// indices back-filled once at scenario link by <see cref="UnitDefinition.ResolveAbilities"/>. The def a snapshot
    /// pins is the LIVE, SHARED roster object: <c>UnitCardPanel</c> assigns <c>def.Abilities</c> in place (both the
    /// per-field editor row and the Simple-mode composition preset), and the balance-apply path swaps a whole
    /// JSON-round-tripped clone into <c>faction.Units[i]</c>. So by the time an undo runs, the pinned def's
    /// resolution may name DIFFERENT abilities, or (after a re-resolve against a different/absent registry) none at
    /// all — and the restored unit silently came back wrong. Pre-fix, every assertion below that names an ORIGINAL
    /// index fails.</para>
    ///
    /// <para>Godot-free + integer-only, like the rest of Tier-1. Touches no folded value in any unchanged-def
    /// scenario: with no mutation between capture and restore the pin writes exactly what the def would have
    /// (<see cref="RestoreUnit_WithAnUnchangedDef_WritesExactlyWhatTheDefWouldHave"/>), so no golden moves.</para>
    /// </summary>
    public class SnapshotAbilityPinTests
    {
        // Ids sort ordinally → registry indices are stable and distinct: active_x=0, active_y=1, aura_x=2,
        // aura_y=3, onhit_x=4, onhit_y=5, selfreg_x=6, selfreg_y=7. Two of every kind so a mutation can swap
        // each slot to a DIFFERENT real index (not merely to −1) — the pin has to be exact, not just non-empty.
        private static AbilityRegistry TwoOfEachKind() => new AbilityRegistry(new[]
        {
            new AbilityDefinition { Id = "active_x",  Activation = "active" },
            new AbilityDefinition { Id = "active_y",  Activation = "active" },
            new AbilityDefinition { Id = "aura_x",    Activation = "aura" },
            new AbilityDefinition { Id = "aura_y",    Activation = "aura" },
            new AbilityDefinition { Id = "onhit_x",   Activation = "on_hit" },
            new AbilityDefinition { Id = "onhit_y",   Activation = "on_hit" },
            new AbilityDefinition { Id = "selfreg_x", Activation = "while_alive" },
            new AbilityDefinition { Id = "selfreg_y", Activation = "while_alive" },
        });

        private static UnitDefinition AbilityDef() => new UnitDefinition
        {
            Id = "pin_test_unit", DisplayName = "Pin Test Unit", Category = "Ranged",
            Hp = 100f, Speed = 4f, MaxEnergy = 50f,
        };

        /// <summary>Spawn one def-based unit through the single mapper and hand back its id.</summary>
        private static int Spawn(EntityWorld w, UnitDefinition def) =>
            SpawnAt(w, def, w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromFloat(def.Hp), Fixed.FromFloat(def.Speed)));

        private static int SpawnAt(EntityWorld w, UnitDefinition def, int id)
        {
            w.ApplyUnitDefinition(id, def);
            return id;
        }

        private static int Slot(int id, int s) => id * EntityWorld.MAX_ABILITIES_PER_UNIT + s;

        // ── The headline case: the def is EDITED IN PLACE between the delete and the undo ───────────────────────
        [Fact]
        public void RestoreUnit_AfterTheDefWasEditedInPlace_RestoresThePinnedAbilities()
        {
            AbilityRegistry registry = TwoOfEachKind();
            UnitDefinition def = AbilityDef();
            def.Abilities = new[] { "active_x", "aura_x", "onhit_x", "selfreg_x" };
            def.ResolveAbilities(registry);

            var w = new EntityWorld();
            int original = Spawn(w, def);

            int eActive = registry.IndexOf("active_x");
            int eAura   = registry.IndexOf("aura_x");
            int eOnHit  = registry.IndexOf("onhit_x");
            int eSelf   = registry.IndexOf("selfreg_x");
            Assert.Equal(eActive, w.AbilityId[Slot(original, 0)]); // precondition: the spawn wired the ORIGINAL set
            Assert.Equal(eSelf,   w.SelfPassiveAbilityIndex[original]);

            UnitSnapshot snap = w.SnapshotUnit(original);
            w.Destroy(original);

            // The Unit Card editor rewriting def.Abilities in place, then a re-link re-resolving it (SlotFactionResolver
            // /ServerBootstrap/MainScene all re-run ResolveAbilities over the SHARED roster defs). Same object — the
            // snapshot's pinned reference now names a completely different ability set.
            def.Abilities = new[] { "active_y", "aura_y", "onhit_y", "selfreg_y" };
            def.ResolveAbilities(registry);
            Assert.NotEqual(eActive, def.AbilityIndices[0]); // the def really did move

            int restored = w.RestoreUnit(snap);
            Assert.True(restored >= 0);

            // The undo restores the unit AS DELETED — not as the edited def would now spawn it.
            Assert.Equal((byte)1, w.AbilityCount[restored]);
            Assert.Equal(eActive, w.AbilityId[Slot(restored, 0)]);
            Assert.Equal(eAura,   w.AuraAbilityIndex[restored]);
            Assert.Equal(eOnHit,  w.OnHitAbilityIndex[restored]);
            Assert.Equal(eSelf,   w.SelfPassiveAbilityIndex[restored]);

            // Teeth: each restored slot is NOT the def's post-edit value (pre-fix, all four are).
            Assert.NotEqual(registry.IndexOf("active_y"),  w.AbilityId[Slot(restored, 0)]);
            Assert.NotEqual(registry.IndexOf("aura_y"),    w.AuraAbilityIndex[restored]);
            Assert.NotEqual(registry.IndexOf("onhit_y"),   w.OnHitAbilityIndex[restored]);
            Assert.NotEqual(registry.IndexOf("selfreg_y"), w.SelfPassiveAbilityIndex[restored]);
        }

        // ── The ledger's "unresolved def" case: a re-resolve against an EMPTY registry clears every index ───────
        [Fact]
        public void RestoreUnit_AfterTheDefWasReResolvedAgainstAnEmptyRegistry_StillRestoresEveryAbility()
        {
            AbilityRegistry registry = TwoOfEachKind();
            UnitDefinition def = AbilityDef();
            def.Abilities = new[] { "active_x", "active_y", "aura_x", "onhit_x", "selfreg_x" };
            def.ResolveAbilities(registry);

            var w = new EntityWorld();
            int original = Spawn(w, def);
            Assert.Equal((byte)2, w.AbilityCount[original]); // two ACTIVE abilities → two castable slots

            int eSlot0 = w.AbilityId[Slot(original, 0)], eSlot1 = w.AbilityId[Slot(original, 1)];
            int eAura  = w.AuraAbilityIndex[original];
            int eOnHit = w.OnHitAbilityIndex[original];
            int eSelf  = w.SelfPassiveAbilityIndex[original];

            UnitSnapshot snap = w.SnapshotUnit(original);
            w.Destroy(original);

            // A re-link that hands the shared def a registry which no longer holds its ids (a mod/registry reload, a
            // faction re-scan before the abilities dir is indexed): ResolveAbilities wipes the resolution outright.
            def.ResolveAbilities(AbilityRegistry.Empty);
            Assert.Empty(def.AbilityIndices);
            Assert.Equal(-1, def.SelfPassiveAbilityIndex);

            int restored = w.RestoreUnit(snap);
            Assert.True(restored >= 0);

            // Pre-fix this is the silent-total-loss case: count 0 and −1 in all three passive slots.
            Assert.Equal((byte)2, w.AbilityCount[restored]);
            Assert.Equal(eSlot0,  w.AbilityId[Slot(restored, 0)]);
            Assert.Equal(eSlot1,  w.AbilityId[Slot(restored, 1)]);
            Assert.Equal(eAura,   w.AuraAbilityIndex[restored]);
            Assert.Equal(eOnHit,  w.OnHitAbilityIndex[restored]);
            Assert.Equal(eSelf,   w.SelfPassiveAbilityIndex[restored]);
            Assert.NotEqual(-1,   w.AuraAbilityIndex[restored]);
            Assert.NotEqual(-1,   w.OnHitAbilityIndex[restored]);
            Assert.NotEqual(-1,   w.SelfPassiveAbilityIndex[restored]);
        }

        // ── Ordering teeth: the pin must be in the SoA BEFORE the passive-install seam fires, or the installed
        //    while-alive passive and SelfPassiveAbilityIndex disagree for the entity's whole life. ───────────────
        [Fact]
        public void RestoreUnit_PassiveInstallSeam_ObservesThePinnedSelfPassive_NotTheDefs()
        {
            AbilityRegistry registry = TwoOfEachKind();
            UnitDefinition def = AbilityDef();
            def.Abilities = new[] { "selfreg_x" };
            def.ResolveAbilities(registry);

            var w = new EntityWorld();
            int original = Spawn(w, def);
            int ePinned = registry.IndexOf("selfreg_x");
            Assert.Equal(ePinned, w.SelfPassiveAbilityIndex[original]);

            UnitSnapshot snap = w.SnapshotUnit(original);
            w.Destroy(original);

            def.Abilities = new[] { "selfreg_y" };
            def.ResolveAbilities(registry);

            // Subscribe AFTER the spawn so only the restore is observed — this is exactly what AbilityCastSystem
            // .InstallSelfPassive reads (world.SelfPassiveAbilityIndex[id]) when the seam fires.
            var seen = new List<int>();
            w.OnUnitDefinitionApplied += id => seen.Add(w.SelfPassiveAbilityIndex[id]);

            int restored = w.RestoreUnit(snap);
            Assert.True(restored >= 0);

            int seenIdx = Assert.Single(seen);
            Assert.Equal(ePinned, seenIdx);                              // the installer saw the PINNED passive…
            Assert.Equal(ePinned, w.SelfPassiveAbilityIndex[restored]);  // …and the SoA agrees with it
            Assert.NotEqual(registry.IndexOf("selfreg_y"), seenIdx);
        }

        // ── Pin, not merge: a def that GAINED abilities after the delete must not smuggle them into the undo. ───
        [Fact]
        public void RestoreUnit_AfterTheDefGainedAbilities_RestoresOnlyTheDeletedUnitsSet()
        {
            AbilityRegistry registry = TwoOfEachKind();
            UnitDefinition def = AbilityDef();
            def.Abilities = new[] { "active_x" };
            def.ResolveAbilities(registry);

            var w = new EntityWorld();
            int original = Spawn(w, def);
            Assert.Equal((byte)1, w.AbilityCount[original]);

            UnitSnapshot snap = w.SnapshotUnit(original);
            w.Destroy(original);

            // The Simple-mode composition preset filling a whole bundle onto the same object.
            def.Abilities = new[] { "active_x", "active_y", "aura_x" };
            def.ResolveAbilities(registry);
            Assert.Equal(2, def.AbilityIndices.Length);

            int restored = w.RestoreUnit(snap);
            Assert.Equal((byte)1, w.AbilityCount[restored]);
            Assert.Equal(registry.IndexOf("active_x"), w.AbilityId[Slot(restored, 0)]);
            Assert.Equal(-1, w.AuraAbilityIndex[restored]); // the aura the def gained is NOT smuggled in
            // The unused slot keeps Create's −1 sentinel — the count is what bounds every reader.
            Assert.Equal(-1, w.AbilityId[Slot(restored, 1)]);
        }

        // ── The no-op guarantee: with an UNCHANGED def the pin writes exactly what the def would have, so every
        //    existing spawn/undo scenario (and therefore every golden) is byte-identical. ────────────────────────
        [Fact]
        public void RestoreUnit_WithAnUnchangedDef_WritesExactlyWhatTheDefWouldHave()
        {
            AbilityRegistry registry = TwoOfEachKind();
            UnitDefinition def = AbilityDef();
            def.Abilities = new[] { "active_x", "active_y", "aura_x", "onhit_x", "selfreg_x" };
            def.ResolveAbilities(registry);

            var w = new EntityWorld();
            int original = Spawn(w, def);
            UnitSnapshot snap = w.SnapshotUnit(original);
            w.Destroy(original);
            int restored = w.RestoreUnit(snap);

            // The restored wiring equals the DEF's own resolution, field for field (the pin is not a divergence).
            Assert.Equal((byte)def.AbilityIndices.Length, w.AbilityCount[restored]);
            for (int s = 0; s < def.AbilityIndices.Length; s++)
                Assert.Equal(def.AbilityIndices[s], w.AbilityId[Slot(restored, s)]);
            Assert.Equal(def.AuraAbilityIndex,        w.AuraAbilityIndex[restored]);
            Assert.Equal(def.OnHitAbilityIndex,       w.OnHitAbilityIndex[restored]);
            Assert.Equal(def.SelfPassiveAbilityIndex, w.SelfPassiveAbilityIndex[restored]);
        }

        // ── A plain unit (no abilities, no passives) pins the SHARED None wiring — no per-snapshot allocation,
        //    and the restore still lands Create's empty wiring. ───────────────────────────────────────────────
        [Fact]
        public void SnapshotUnit_UnabilitiedUnit_PinsTheSharedNoneWiring()
        {
            UnitDefinition def = AbilityDef(); // no Abilities → ResolveAbilities never needed
            var w = new EntityWorld();
            int original = Spawn(w, def);

            UnitSnapshot snap = w.SnapshotUnit(original);
            Assert.Same(PinnedAbilityWiring.None, snap.Abilities);

            w.Destroy(original);
            int restored = w.RestoreUnit(snap);
            Assert.Equal((byte)0, w.AbilityCount[restored]);
            Assert.Equal(-1, w.AuraAbilityIndex[restored]);
            Assert.Equal(-1, w.OnHitAbilityIndex[restored]);
            Assert.Equal(-1, w.SelfPassiveAbilityIndex[restored]);
        }

        // ── The def-less restore branch is unchanged: no def, no pinned wiring worth anything, empty slots. ────
        [Fact]
        public void RestoreUnit_DefLessUnit_KeepsTheEmptyAbilityWiring()
        {
            var w = new EntityWorld();
            int original = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(80), Fixed.FromInt(4));
            Assert.Null(w.SourceDefinition[original]); // def-less spawn

            UnitSnapshot snap = w.SnapshotUnit(original);
            Assert.Null(snap.Def);
            Assert.Same(PinnedAbilityWiring.None, snap.Abilities);

            w.Destroy(original);
            int restored = w.RestoreUnit(snap);
            Assert.True(restored >= 0);
            Assert.Equal((byte)0, w.AbilityCount[restored]);
            Assert.Equal(-1, w.AuraAbilityIndex[restored]);
            Assert.Equal(-1, w.OnHitAbilityIndex[restored]);
            Assert.Equal(-1, w.SelfPassiveAbilityIndex[restored]);
        }

        // ── A spawn path passes NO pin, so it keeps deriving from the def — the A2 single-mapper contract is
        //    unchanged for everything except the restore path. ───────────────────────────────────────────────
        [Fact]
        public void ApplyUnitDefinition_WithoutAPin_StillDerivesTheWiringFromTheDef()
        {
            AbilityRegistry registry = TwoOfEachKind();
            UnitDefinition def = AbilityDef();
            def.Abilities = new[] { "active_y", "aura_y", "onhit_y", "selfreg_y" };
            def.ResolveAbilities(registry);

            var w = new EntityWorld();
            int id = Spawn(w, def);

            Assert.Equal((byte)1, w.AbilityCount[id]);
            Assert.Equal(registry.IndexOf("active_y"),  w.AbilityId[Slot(id, 0)]);
            Assert.Equal(registry.IndexOf("aura_y"),    w.AuraAbilityIndex[id]);
            Assert.Equal(registry.IndexOf("onhit_y"),   w.OnHitAbilityIndex[id]);
            Assert.Equal(registry.IndexOf("selfreg_y"), w.SelfPassiveAbilityIndex[id]);
        }

        // ── The pin is IMMUTABLE: it copies the array it is handed, so a caller that recycles a buffer (or an
        //    undo entry held for a whole session) can never have its captured resolution rewritten underneath. ─
        [Fact]
        public void PinnedAbilityWiring_CopiesItsSourceArray_SoAHeldPinCannotBeMutated()
        {
            var source = new[] { 3, 7 };
            var pin = new PinnedAbilityWiring(source, auraIndex: 1, onHitIndex: 2, selfPassiveIndex: 5);

            source[0] = 99;
            source[1] = 99;

            Assert.Equal(2, pin.Count);
            Assert.Equal(3, pin.ActiveAt(0));
            Assert.Equal(7, pin.ActiveAt(1));
            Assert.Equal(1, pin.AuraIndex);
            Assert.Equal(2, pin.OnHitIndex);
            Assert.Equal(5, pin.SelfPassiveIndex);
        }
    }
}
