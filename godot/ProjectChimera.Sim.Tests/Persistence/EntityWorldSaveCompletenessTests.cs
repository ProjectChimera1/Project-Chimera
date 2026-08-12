#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Persistence;
using ProjectChimera.Core.Sim;
using ProjectChimera.Sim.Tests.Golden;  // GoldenApplierScenario (the trigger-less applied fixture)
using ProjectChimera.Sim.Tests.Sim;     // ClearCompletenessSweep.InstanceFieldsOf / BackingField
using Xunit;

namespace ProjectChimera.Sim.Tests.Persistence
{
    /// <summary>
    /// DW-519 — the SAVE-path counterpart of <c>EntityWorldClearCompletenessTests</c>: a reflection-driven
    /// completeness guard over <see cref="EntityWorld"/>'s per-entity SoA lanes and
    /// <see cref="SaveGameState.CaptureFrom"/>/<see cref="SaveGameState.RestoreInto"/>.
    ///
    /// <para><b>The gap this closes.</b> <c>EntityWorldClearCompletenessTests</c> reflects over EVERY field and would
    /// have failed had <see cref="EntityWorld.Clear"/> forgotten one. Nothing equivalent existed for the SAVE path:
    /// the hand-enumerated <c>SaveGameState.EA</c> lane enum was the ONLY record of what a save carries, so a future
    /// per-entity array that genuinely must round-trip a save could be forgotten and ship GREEN — the byte-identical
    /// resume tests only notice a dropped lane if that lane is folded into <c>SimChecksum</c> AND the 300-tick resume
    /// happens to read it, which is exactly not true of the growing unfolded half of the SoA.</para>
    ///
    /// <para><b>How it is closed.</b> Every instance field of <see cref="EntityWorld"/> must land in EXACTLY ONE
    /// bucket: an explicitly justified allowlist entry (below), or — by default — the reflective round-trip sweep.
    /// The sweep does not consult any name list of what is "supposed" to be persisted; it BUMPS every element of
    /// every swept lane on a source host, runs a real capture → restore into a second host, and demands the value
    /// arrived. So a new SoA array is swept the moment it exists, and the only ways to make the suite green again
    /// are to add its save lane or to write down (with a reason) why it must not have one. Adding a name to a list
    /// is never enough on its own — an allowlisted lane is separately pinned as genuinely NOT arriving.</para>
    ///
    /// <para>Godot-free and Tier-1. Nothing here folds into <c>SimChecksum</c>, <c>CanonicalModelHash</c> or
    /// <c>StartStateHash</c> — it is a test-only guard over existing behaviour, so no golden moves.</para>
    /// </summary>
    public class EntityWorldSaveCompletenessTests
    {
        // ══════════════════════════ The buckets (explicit + justified — never a silent skip) ══════════════════════

        /// <summary>
        /// Per-entity lanes the save path DELIBERATELY drops. Each is per-tick/per-leg worker or presentation
        /// bookkeeping whose own XML doc already declares "NOT persisted by SaveGameState", and each is pinned as
        /// genuinely not arriving by <see cref="TransientLanes_AreGenuinelyNotPersisted_SoTheAllowlistCannotGoStale"/>
        /// — so this list cannot quietly become the place a forgotten lane is buried.
        /// </summary>
        private static readonly string[] TransientLanes =
        {
            // Interpolation-only mirror of Position. RestoreEntities SEEDS it to the restored Position and the next
            // tick's SnapshotPositions overwrites it, so carrying the saved value would be pure noise.
            nameof(EntityWorld.PrevPosition),
            // DW-80 — the Streaming closed-gate grace streak. A resumed save restarts the window, which is
            // behaviourally indistinguishable from a fresh boot re-seeking the same node.
            nameof(EntityWorld.GateClosedTicks),
            // DW-532 — the MovingToResource stall streak / yielded sentinel. Same posture; the one known residual
            // (a stranded worker returning as "holding") is documented on the field and bounded.
            nameof(EntityWorld.GatherWalkStallTicks),
            // DW-689 — the rally stand-down NO-PROGRESS budget and its best-approach mark. A resumed save restarts
            // the window (counter 0 == unarmed, which is exactly what EntityWorld.Create leaves and what the first
            // stand-down tick after the load re-arms from the worker's restored position), costing a genuinely stuck
            // worker at most one further grace window. The rally LEG itself survives — DW-690 persists
            // RallyMovePending — so this is the GatherWalkStallTicks posture, not the DW-690 defect repeated.
            nameof(EntityWorld.RallyStandDownTicks),
            nameof(EntityWorld.RallyGoalBestSqr),
        };

        /// <summary>
        /// The reference-typed lanes. They cannot round-trip as ints: <see cref="EntityWorld.SourceDefinition"/>
        /// travels as its def's stable string id (<c>SaveGameState.EntDefId</c>, re-resolved through the slot faction
        /// definitions) and <see cref="EntityWorld.FeedbackProfile"/> is RE-DERIVED from the resolved def. Proved by
        /// <see cref="SavePath_RoundTripsTheDefReferenceLanes_ByDefId"/> rather than by the int sweep.
        /// </summary>
        private static readonly string[] DefDerivedRefLanes =
        {
            nameof(EntityWorld.SourceDefinition),
            nameof(EntityWorld.FeedbackProfile),
        };

        /// <summary>
        /// The private allocator bookkeeping. It IS persisted, but as the scalar/free-list triple
        /// (<c>EntHwm</c>/<c>EntAliveCount</c>/<c>EntFreeList</c> → <see cref="EntityWorld.RestoreAllocation"/>), not
        /// as a per-entity lane — <c>_freeList</c>'s tail past <c>_freeCount</c> is unread by contract, so an
        /// elementwise sweep over it would assert on stale bytes no production path defines. Proved by
        /// <see cref="SavePath_RoundTripsTheAllocatorState_HighWaterMarkAliveCountAndFreeList"/>.
        /// </summary>
        private static readonly string[] AllocatorFields =
        {
            "_freeList",
            "_nextId",
            "_freeCount",
        };

        /// <summary>Persisted, but not per-entity: the shared RNG travels as the <c>RngState</c> scalar and is
        /// re-seeded on restore. Proved by <see cref="SavePath_RoundTripsTheSharedRngState"/>.</summary>
        private static readonly string[] PersistedNonLaneFields =
        {
            "<Rng>k__BackingField",
        };

        /// <summary>
        /// Non-lane state the save path deliberately does NOT capture, matching <see cref="SaveGameState"/>'s stated
        /// contract that authored/derived/transient state is re-established by the load path's scenario re-apply
        /// BEFORE <see cref="SaveGameState.RestoreInto"/> overlays the mutable stores.
        /// </summary>
        private static readonly string[] NotPersistedNonLaneFields =
        {
            // DW-367/DW-551 — the per-tick death log is provably empty at a tick boundary, and CaptureFrom THROWS if
            // it is not (AssertDeathLogDrained), so "not captured" is enforced rather than assumed.
            nameof(EntityWorld.DeathLog),
            // Authored sim-globals re-applied by the load path's scenario apply (the Story 6.3 vision toggle/bonus).
            nameof(EntityWorld.HeightAdvantageVision),
            nameof(EntityWorld.HeightVisionBonusPerStep),
            // Injected terrain/navigation grids: the load path hands them in before apply (SetElevationGrid /
            // SetPathabilityGrid), exactly as a fresh match boot does.
            "_elevationGrid",
            "<Pathability>k__BackingField",
            // Host-lifetime delegate subscriptions (the EntityWorldClearCompletenessTests allowlist rationale): they
            // belong to the SimulationHost, not to the match, and the destination host bound its own at construction.
            nameof(EntityWorld.OnDestroy),
            nameof(EntityWorld.OnUnitDefinitionApplied),
        };

        /// <summary>The alive count is an auto-property; name it through the shared helper so a rename of the
        /// backing-field convention cannot silently turn this entry into a dead string.</summary>
        private static string AliveCountBackingField => ClearCompletenessSweep.BackingField(nameof(EntityWorld.AliveCount));

        private static IEnumerable<string> AllListedNames()
        {
            foreach (string n in TransientLanes) yield return n;
            foreach (string n in DefDerivedRefLanes) yield return n;
            foreach (string n in AllocatorFields) yield return n;
            yield return AliveCountBackingField;
            foreach (string n in PersistedNonLaneFields) yield return n;
            foreach (string n in NotPersistedNonLaneFields) yield return n;
        }

        private static bool IsListed(string name)
        {
            foreach (string n in AllListedNames())
                if (string.Equals(n, name, StringComparison.Ordinal)) return true;
            return false;
        }

        // ══════════════════════════ Lane discovery (everything unlisted is swept) ═════════════════════════════════

        /// <summary>
        /// Every <see cref="EntityWorld"/> instance field that is NOT explicitly bucketed above — i.e. the lanes the
        /// round-trip sweep proves. A field that is neither listed nor a value-typed per-entity array FAILS here with
        /// the decision it demands, instead of being skipped: that is the whole point of DW-519.
        /// </summary>
        private static IReadOnlyList<FieldInfo> SweptLanes()
        {
            var lanes = new List<FieldInfo>();
            foreach (FieldInfo f in ClearCompletenessSweep.InstanceFieldsOf(typeof(EntityWorld)))
            {
                if (IsListed(f.Name)) continue;

                Assert.True(f.FieldType.IsArray,
                    $"EntityWorld.{f.Name} is new non-array state ({f.FieldType.Name}) that the SAVE-completeness " +
                    "sweep cannot round-trip elementwise. Decide explicitly (DW-519): give it a SaveGameState lane " +
                    "and add it to PersistedNonLaneFields with a dedicated round-trip assertion, or add it to " +
                    "NotPersistedNonLaneFields with the reason the load path re-establishes it. Do NOT delete this " +
                    "assertion — an unclassified field is exactly the silent save gap this guard exists to prevent.");

                Type element = f.FieldType.GetElementType()!;
                Assert.True(element.IsValueType,
                    $"EntityWorld.{f.Name} is a NEW reference-typed SoA lane (element {element.Name}). A reference " +
                    "cannot travel in the int lane table: round-trip it by a stable id (the SourceDefinition/EntDefId " +
                    "precedent) or re-derive it on restore, then add it to DefDerivedRefLanes with an assertion of " +
                    "its own — or to TransientLanes with the reason it is dropped.");
                lanes.Add(f);
            }
            Assert.True(lanes.Count > 0, "The save-completeness sweep found NO lanes — the classification is broken.");
            return lanes;
        }

        /// <summary>The number of elements of <paramref name="lane"/> that belong to entity ids [0, <paramref name="hwm"/>):
        /// <c>hwm</c> for a plain per-entity lane, <c>hwm * stride</c> for a flat (strided) one. The save path captures
        /// exactly this prefix; elements past it belong to never-allocated ids and are unread by contract.</summary>
        private static int PrefixLength(Array lane, string name, int hwm)
        {
            Assert.True(lane.Length >= EntityWorld.MAX_ENTITIES && lane.Length % EntityWorld.MAX_ENTITIES == 0,
                $"EntityWorld.{name} has length {lane.Length}, which is not a whole multiple of MAX_ENTITIES " +
                $"({EntityWorld.MAX_ENTITIES}) — so it is not a per-entity SoA lane and the save-completeness sweep " +
                "cannot derive its per-entity prefix. Teach this guard about the new shape (or bucket the field " +
                "explicitly); silently skipping it would reopen the DW-519 gap.");
            return hwm * (lane.Length / EntityWorld.MAX_ENTITIES);
        }

        /// <summary>A value of the SAME type that is guaranteed DIFFERENT from <paramref name="value"/> — derived from
        /// the current value rather than a constant, so the sweep can never be blinded by a filler that happens to
        /// equal what the scenario already wrote (e.g. <c>EntityFlags.Alive == 1</c>). An element type nobody has
        /// taught it about THROWS rather than being silently left undirtied.</summary>
        private static object Bump(object value, string laneName)
        {
            Type t = value.GetType();

            if (t.IsEnum) return Enum.ToObject(t, Convert.ToInt64(value) + 1);

            switch (value)
            {
                case bool b: return !b;
                // +1.0 in RAW units (never through operator+, which could saturate and hand back the same value).
                // A whole unit rather than one raw tick so a failure message is readable at Fixed's print precision.
                case Fixed f: return Fixed.FromRaw(f.Raw + Fixed.One.Raw);
                case FixedVec3 v:
                    return new FixedVec3(Fixed.FromRaw(v.X.Raw + Fixed.One.Raw), Fixed.FromRaw(v.Y.Raw + Fixed.One.Raw),
                                         Fixed.FromRaw(v.Z.Raw + Fixed.One.Raw));
                case byte n: return unchecked((byte)(n + 1));
                case sbyte n: return unchecked((sbyte)(n + 1));
                case short n: return unchecked((short)(n + 1));
                case ushort n: return unchecked((ushort)(n + 1));
                case int n: return unchecked(n + 1);
                case uint n: return unchecked(n + 1u);
                case long n: return unchecked(n + 1L);
                case ulong n: return unchecked(n + 1UL);
            }

            throw new InvalidOperationException(
                $"EntityWorld.{laneName} is an array of '{t.FullName}', a type the SAVE-completeness sweep does not " +
                "know how to move off its current value. Teach Bump() about it — leaving it unhandled would silently " +
                "exempt the lane from the sweep, which is the exact blind spot DW-519 closed.");
        }

        /// <summary>Render one lane element for a failure message. <see cref="Fixed"/>/<see cref="FixedVec3"/> print
        /// rounded, so two values a fraction apart look identical in a diff — always show the raw ints too.</summary>
        private static string Describe(object? value) => value switch
        {
            null => "null",
            Fixed f => $"{f} (raw {f.Raw})",
            FixedVec3 v => $"({v.X}, {v.Y}, {v.Z}) (raw {v.X.Raw}, {v.Y.Raw}, {v.Z.Raw})",
            _ => value.ToString() ?? "null",
        };

        // ══════════════════════════ Fixture ═══════════════════════════════════════════════════════════════════════

        private sealed class Applied
        {
            public SimulationHost Host = null!;
            public FactionDefinition?[] SlotDefs = null!;
            public EntityWorld World => Host.World;
        }

        /// <summary>The trigger-less applier golden scenario (4 workers, 2 pre-built command centers), applied onto a
        /// fresh host — the same fixture <c>SaveLoadTests</c>/<c>DeathLogSaveGuardTests</c> use. Both the source and
        /// the destination host are built from the SAME <see cref="FactionDefinition"/> instance so a def re-resolved
        /// on restore is reference-comparable.</summary>
        private static Applied BuildApplied(FactionDefinition faction)
        {
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;
            slotDefs[(int)Faction.Player2] = faction;

            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);
            ValidationResult r = new ScenarioValidator().Validate(GoldenApplierScenario.BuildModel());
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);
            return new Applied { Host = host, SlotDefs = slotDefs };
        }

        /// <summary>A real in-memory save: capture the source host, then overlay it onto the (independently
        /// scenario-applied) destination host — the exact production sequence, minus the disk framing.
        ///
        /// <para><see cref="SaveGameState.Validate"/> runs BETWEEN the two halves exactly as the disk path does
        /// (<c>ReadBody</c> validates before any host is mutated). That is deliberate extra reach for this guard: a
        /// new lane can be wired into capture/restore and still be WRONG on disk if its cardinality breaks the
        /// per-lane length contract — e.g. a flat/strided lane declared before <c>EA.PatrolWpX</c>, where Validate
        /// demands length == EntHwm. Without this call such a lane would round-trip green in memory and fail-closed
        /// only on a real player's save.</para></summary>
        private static void CaptureThenRestore(Applied from, Applied into)
        {
            var fromTable = CanonicalEffectDescriptorTable.Build(from.Host.AbilityRegistry, from.Host.ItemRegistry);
            SaveGameState state = SaveGameState.CaptureFrom(from.Host, fromTable);
            state.Validate("dw-519 save-completeness sweep");
            var intoTable = CanonicalEffectDescriptorTable.Build(into.Host.AbilityRegistry, into.Host.ItemRegistry);
            state.RestoreInto(into.Host, intoTable, into.SlotDefs);
        }

        // ══════════════════════════ (1) The exhaustive round-trip sweep ═══════════════════════════════════════════

        /// <summary>
        /// Bump EVERY element of EVERY swept lane on the source world, save it, restore into a second world, and
        /// require every bumped value to have arrived. Per lane, per index, the bumped source value is first REQUIRED
        /// to differ from what the destination already held — otherwise the comparison could pass on a lane the save
        /// path never carries at all (the DW-19 per-field-divergence precondition, applied to persistence).
        /// </summary>
        [Fact]
        public void SaveGameState_RoundTripsEveryPerEntityLane_ExhaustiveReflectionSweep()
        {
            FactionDefinition faction = GoldenApplierScenario.BuildFaction();
            Applied source = BuildApplied(faction);
            Applied dest = BuildApplied(faction);

            int hwm = source.World.HighWaterMark;
            Assert.True(hwm > 0, "The fixture spawned no entities, so every lane prefix would be empty and the sweep blind.");

            IReadOnlyList<FieldInfo> lanes = SweptLanes();

            // Snapshot what the destination holds BEFORE the restore, so the precondition compares against the real
            // pre-restore state rather than against an assumption that the two applies agree.
            var before = new Dictionary<string, object?[]>(lanes.Count, StringComparer.Ordinal);
            var bumped = new Dictionary<string, object?[]>(lanes.Count, StringComparer.Ordinal);

            foreach (FieldInfo f in lanes)
            {
                var srcLane = (Array)f.GetValue(source.World)!;
                var dstLane = (Array)f.GetValue(dest.World)!;
                int n = PrefixLength(srcLane, f.Name, hwm);
                Assert.Equal(srcLane.Length, dstLane.Length);

                var pre = new object?[n];
                var want = new object?[n];
                for (int i = 0; i < n; i++)
                {
                    pre[i] = dstLane.GetValue(i);
                    object next = Bump(srcLane.GetValue(i)!, f.Name);
                    srcLane.SetValue(next, i);
                    want[i] = next;
                }
                before[f.Name] = pre;
                bumped[f.Name] = want;
            }

            // Precondition: the sweep must not be blind on any element. A bumped value equal to what the destination
            // already held would let a NEVER-PERSISTED lane pass the assertion below.
            var blind = new List<string>();
            foreach (FieldInfo f in lanes)
            {
                object?[] pre = before[f.Name], want = bumped[f.Name];
                for (int i = 0; i < want.Length; i++)
                    if (Equals(pre[i], want[i])) { blind.Add($"EntityWorld.{f.Name}[{i}] == {Describe(want[i])}"); break; }
            }
            Assert.True(blind.Count == 0,
                "Precondition failed: the dirtied value equals what the destination world already held, so the save " +
                "sweep is BLIND on these lanes — a save path that drops them entirely would still pass GREEN:\n  " +
                string.Join("\n  ", blind) +
                "\nBump() derives its value from the current one, so this means an element wrapped onto its old value; " +
                "widen Bump for that element type.");

            CaptureThenRestore(source, dest);

            Assert.Equal(hwm, dest.World.HighWaterMark);

            var missing = new List<string>();
            foreach (FieldInfo f in lanes)
            {
                var dstLane = (Array)f.GetValue(dest.World)!;
                object?[] want = bumped[f.Name];
                for (int i = 0; i < want.Length; i++)
                {
                    object? got = dstLane.GetValue(i);
                    if (!Equals(want[i], got))
                    {
                        missing.Add($"EntityWorld.{f.Name}[{i}]: saved {Describe(want[i])}, restored {Describe(got)}");
                        break; // one located example per lane is enough to name the culprit
                    }
                }
            }

            Assert.True(missing.Count == 0,
                "These EntityWorld per-entity lanes did NOT survive a SaveGameState capture → restore, i.e. a player " +
                "who saves and resumes silently loses them:\n  " + string.Join("\n  ", missing) +
                "\nAdd the lane to SaveGameState (the EA enum + CaptureEntities + RestoreEntities — all three, and " +
                "keep a new flat/strided lane AFTER EA.PatrolWpX so Validate's per-lane length checks stay correct). " +
                "If the field genuinely must NOT persist, move it to this test's TransientLanes allowlist WITH the " +
                "reason — never make this pass by deleting the assertion.");
        }

        // ══════════════════════════ (2) The bucket reconciliation ═════════════════════════════════════════════════

        /// <summary>
        /// Every allowlist entry must keep naming a REAL field, no field may sit in two buckets, and every
        /// <see cref="EntityWorld"/> instance field must be classified. Without this, a rename would turn an
        /// exemption into a dead string while the renamed field quietly entered (or left) the sweep.
        /// </summary>
        [Fact]
        public void SaveCoverageBuckets_NameOnlyRealFields_AndPartitionEveryEntityWorldField()
        {
            var byName = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
            foreach (FieldInfo f in ClearCompletenessSweep.InstanceFieldsOf(typeof(EntityWorld)))
                byName[f.Name] = f;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in AllListedNames())
            {
                Assert.True(byName.ContainsKey(name),
                    $"Save-coverage allowlist entry '{name}' no longer resolves to an EntityWorld instance field. " +
                    "Reconcile it deliberately: a dead entry hides nothing, but it means whatever replaced the field " +
                    "silently entered the sweep (or, worse, that a real lane is now unclassified).");
                Assert.True(seen.Add(name), $"Save-coverage allowlist entry '{name}' is listed in two buckets — the " +
                                            "buckets must PARTITION EntityWorld, not overlap.");
            }

            // The transient/def-ref buckets may only exempt genuine per-entity arrays — a scalar hidden there would
            // escape both the sweep and the non-lane reasoning.
            foreach (string name in TransientLanes)
                Assert.True(byName[name].FieldType.IsArray, $"TransientLanes entry '{name}' is not an array field.");
            foreach (string name in DefDerivedRefLanes)
                Assert.True(byName[name].FieldType.IsArray && !byName[name].FieldType.GetElementType()!.IsValueType,
                    $"DefDerivedRefLanes entry '{name}' is not a reference-typed array — the int lane sweep should cover it.");

            // Everything not listed must be a value-typed per-entity array (SweptLanes asserts this per field), and
            // the sweep must actually contain the obvious load-bearing lanes — a classification bug that emptied it
            // would otherwise be invisible.
            var swept = new HashSet<string>(StringComparer.Ordinal);
            foreach (FieldInfo f in SweptLanes()) swept.Add(f.Name);

            foreach (string canary in new[] { nameof(EntityWorld.Health), nameof(EntityWorld.Position),
                                              nameof(EntityWorld.Flags), nameof(EntityWorld.Generation),
                                              nameof(EntityWorld.AbilityCooldownTicks) })
                Assert.True(swept.Contains(canary),
                    $"EntityWorld.{canary} fell OUT of the save-completeness sweep — the bucket classification is wrong.");

            Assert.Equal(byName.Count, swept.Count + seen.Count);
        }

        // ══════════════════════════ (3) The allowlist's other half: transient lanes really are dropped ════════════

        /// <summary>
        /// The mirror image of the sweep: bump ONLY the allowlisted transient lanes and require that the saved values
        /// did NOT arrive. Without this, the allowlist would be a one-way valve — a future story could start
        /// persisting one of these lanes (or the sweep could be quietly narrowed) and the exemption would rot in
        /// place, still claiming the field is deliberately dropped.
        /// </summary>
        [Fact]
        public void TransientLanes_AreGenuinelyNotPersisted_SoTheAllowlistCannotGoStale()
        {
            FactionDefinition faction = GoldenApplierScenario.BuildFaction();
            Applied source = BuildApplied(faction);
            Applied dest = BuildApplied(faction);
            int hwm = source.World.HighWaterMark;

            var bumped = new Dictionary<string, object?[]>(StringComparer.Ordinal);
            foreach (string name in TransientLanes)
            {
                // DW-501: probe through the null-CHECKED helper, so a renamed lane fails naming the owner + member
                // rather than as an opaque NRE that reads like the allowlist is fine.
                FieldInfo f = ReflectionProbe.PublicField(typeof(EntityWorld), name);
                var srcLane = (Array)f.GetValue(source.World)!;
                int n = PrefixLength(srcLane, name, hwm);
                var want = new object?[n];
                for (int i = 0; i < n; i++)
                {
                    object next = Bump(srcLane.GetValue(i)!, name);
                    srcLane.SetValue(next, i);
                    want[i] = next;
                }
                bumped[name] = want;
            }

            CaptureThenRestore(source, dest);

            var carried = new List<string>();
            foreach (string name in TransientLanes)
            {
                FieldInfo f = ReflectionProbe.PublicField(typeof(EntityWorld), name);
                var dstLane = (Array)f.GetValue(dest.World)!;
                object?[] want = bumped[name];
                bool anyCarried = false;
                for (int i = 0; i < want.Length; i++)
                    if (Equals(want[i], dstLane.GetValue(i))) { anyCarried = true; break; }
                if (anyCarried) carried.Add($"EntityWorld.{name}");
            }

            Assert.True(carried.Count == 0,
                "These lanes are on this test's TransientLanes allowlist (\"deliberately NOT persisted\") but their " +
                "saved values DID arrive in the restored world:\n  " + string.Join("\n  ", carried) +
                "\nIf a save lane was added for them on purpose, delete the allowlist entry so the exhaustive sweep " +
                "takes over and keeps proving the round-trip; the allowlist must never describe stale intent.");
        }

        // ══════════════════════════ (4) The non-lane buckets, each proved ═════════════════════════════════════════

        /// <summary>
        /// <see cref="EntityWorld.SourceDefinition"/> round-trips by the def's stable string id (not by reference)
        /// and <see cref="EntityWorld.FeedbackProfile"/> is re-derived from the resolved def. Proved in the direction
        /// the destination cannot fake: the source's def ref is CLEARED for one entity, so a save path that dropped
        /// the lane would leave the destination's own scenario-applied def in place.
        /// </summary>
        [Fact]
        public void SavePath_RoundTripsTheDefReferenceLanes_ByDefId()
        {
            FactionDefinition faction = GoldenApplierScenario.BuildFaction();
            // Give the def a presentation profile so the derived FeedbackProfile lane is non-null and its loss is
            // observable (the applier copies it into the SoA through ApplyUnitDefinition).
            faction.Units[0].CombatFeedback = new CombatFeedbackProfile { HitFreezeFrames = 3 };

            Applied source = BuildApplied(faction);
            Applied dest = BuildApplied(faction);
            int hwm = source.World.HighWaterMark;
            Assert.True(hwm >= 2);

            UnitDefinition? applied = source.World.SourceDefinition[0];
            Assert.NotNull(applied);
            Assert.Same(applied!.CombatFeedback, source.World.FeedbackProfile[0]);
            Assert.NotNull(dest.World.SourceDefinition[0]); // the destination applied the same scenario

            // Entity 0 becomes def-LESS in the saved world; entity 1 keeps its def.
            source.World.SourceDefinition[0] = null;
            source.World.FeedbackProfile[0] = null;

            CaptureThenRestore(source, dest);

            Assert.Null(dest.World.SourceDefinition[0]);
            Assert.Null(dest.World.FeedbackProfile[0]);
            Assert.Same(applied, dest.World.SourceDefinition[1]);           // re-resolved by id through the slot defs
            Assert.Same(applied.CombatFeedback, dest.World.FeedbackProfile[1]); // re-derived from the resolved def
        }

        /// <summary>The allocator triple: high-water mark, alive count and the LIFO free list (whose ORDER decides
        /// which id the next spawn lands on, so a reordered restore would desync a resumed match).</summary>
        [Fact]
        public void SavePath_RoundTripsTheAllocatorState_HighWaterMarkAliveCountAndFreeList()
        {
            FactionDefinition faction = GoldenApplierScenario.BuildFaction();
            Applied source = BuildApplied(faction);
            Applied dest = BuildApplied(faction);

            // Free two slots in a known LIFO order — the destination's own apply leaves its free list EMPTY, so the
            // assertion cannot pass on state the destination already had.
            source.World.Destroy(2);
            source.World.Destroy(0);
            Assert.Empty(dest.World.CaptureFreeList());

            int hwm = source.World.HighWaterMark;
            int alive = source.World.AliveCount;
            int[] free = source.World.CaptureFreeList();
            Assert.Equal(new[] { 2, 0 }, free);

            CaptureThenRestore(source, dest);

            Assert.Equal(hwm, dest.World.HighWaterMark);
            Assert.Equal(alive, dest.World.AliveCount);
            Assert.Equal(free, dest.World.CaptureFreeList());
            Assert.False(dest.World.IsAlive(0));
            Assert.False(dest.World.IsAlive(2));
        }

        /// <summary>The shared deterministic RNG travels as the <c>RngState</c> scalar and is re-seeded on restore —
        /// it is folded into <c>SimChecksum</c>, so a dropped state would desync the very next resumed tick.</summary>
        [Fact]
        public void SavePath_RoundTripsTheSharedRngState()
        {
            FactionDefinition faction = GoldenApplierScenario.BuildFaction();
            Applied source = BuildApplied(faction);
            Applied dest = BuildApplied(faction);

            source.World.Rng.NextInt(1000); // advance off DEFAULT_RNG_SEED, which the destination still carries
            ulong saved = source.World.Rng.State;
            Assert.NotEqual(saved, dest.World.Rng.State);

            CaptureThenRestore(source, dest);

            Assert.Equal(saved, dest.World.Rng.State);
        }
    }
}
