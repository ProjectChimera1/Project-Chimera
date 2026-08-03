#nullable enable
using System;
using System.Collections;
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
    /// <para>SCOPE — read this before trusting the word "exhaustive". It is exhaustive over <see cref="EntityWorld"/>
    /// AND NOTHING ELSE. <c>SimulationHost.ClearForReset</c> clears ~two dozen further stores (<c>ProjectileStore</c>,
    /// <c>HeroStore</c>, <c>ItemStore</c>, <c>ModifierStore</c>, <c>ResearchStore</c>, <c>WinStateStore</c>,
    /// <c>DslVarTable</c>, …) whose own <c>Clear()</c> methods are still verified by HAND-ENUMERATED assertions —
    /// i.e. they retain in full the blind spot this class closes for EntityWorld. Dropping an <c>Array.Clear</c> from
    /// <c>ProjectileStore</c>/<c>HeroStore</c>/<c>ItemStore</c> ships GREEN through the whole suite today. Do not read
    /// a pass here as "the reset is field-complete"; read it as "EntityWorld is field-complete". Widening the sweep to
    /// the sibling stores is tracked as deferred work — the machinery below is deliberately type-agnostic so it can be
    /// lifted into a shared helper and driven over a (type, dirty-fixture) list.</para>
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

        private const BindingFlags AllInstanceFields =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // ── DW-19: the exhaustive sweep ────────────────────────────────────────────────────────

        [Fact]
        public void Clear_LeavesEveryFieldEqualToFreshlyConstructed_ExhaustiveReflectionSweep()
        {
            var fresh = new EntityWorld();
            EntityWorld dirty = BuildDirtyWorld();

            // TEETH — PER FIELD, not globally. A global "something diverged" check is nearly free to satisfy and leaves
            // the sweep BLIND on every field the fixture never moved off its fresh value: such a field sits equal in
            // BOTH worlds, so a Clear() that omits it still passes. Demand instead that EVERY swept field was actually
            // dirtied, so a field Clear() forgets can never hide behind an undirtied fixture.
            var dirtiedFields = new HashSet<string>();
            foreach ((string field, string _) in DivergingFields(fresh, dirty)) dirtiedFields.Add(field);

            var sweptFields = new HashSet<string>(SweptFieldNames());
            var undirtied = new List<string>();
            foreach (string name in sweptFields)
                if (!dirtiedFields.Contains(name)) undirtied.Add(name);
            undirtied.Sort(StringComparer.Ordinal);

            Assert.True(undirtied.Count == 0,
                "Precondition failed: BuildDirtyWorld() left these swept EntityWorld fields at their FRESH value, so " +
                "the sweep is blind on them — a Clear() that omits any of them would still pass GREEN:\n  " +
                string.Join("\n  ", undirtied) +
                "\nExtend BuildDirtyWorld() to move each of them off its freshly-constructed value (array fields are " +
                "filled automatically by the reflection-driven synthetic fill; a non-array field needs an explicit " +
                "write) — or the completeness guarantee this test claims does not hold.");

            dirty.Clear();

            IReadOnlyList<(string Field, string Detail)> after = DivergingFields(fresh, dirty);
            var afterText = new List<string>();
            foreach ((string field, string detail) in after) afterText.Add($"EntityWorld.{field}{detail}");
            Assert.True(after.Count == 0,
                "EntityWorld.Clear() left these fields diverging from a freshly-constructed world, i.e. a re-Play does " +
                "NOT start from the same state as a fresh boot. For any field folded into SimChecksum that is a " +
                "determinism/desync leak; for the presentation-only arrays this sweep also covers (MeshType, " +
                "FeedbackProfile, SourceDefinition) it is a state-cleanliness leak — stale visuals/definitions carried " +
                "across the reset — not a desync. Either way Clear() must restore it:\n  " +
                string.Join("\n  ", afterText));
        }

        /// <summary>
        /// The allowlist must keep naming REAL fields. Without this, renaming (or deleting) <c>OnDestroy</c> would turn
        /// its allowlist entry into a dead string while the renamed field silently entered the sweep — or, worse, a
        /// future rename could be "fixed" by widening the allowlist to a name that no longer exists, quietly excluding
        /// nothing and hiding the real divergence. Also pins that the allowlist stays exactly two entries wide.
        /// </summary>
        [Fact]
        public void ClearAllowlist_NamesOnlyRealDelegateFields_SoARenameCannotWidenIt()
        {
            // GetFields(Instance) does NOT return a base class's private fields. EntityWorld is a root, non-sealed
            // class today, so the sweep is genuinely exhaustive — but inserting a base class would silently shrink it
            // with no failure anywhere, in the one test whose entire thesis is that it cannot silently shrink.
            Assert.True(typeof(EntityWorld).BaseType == typeof(object),
                $"EntityWorld now derives from {typeof(EntityWorld).BaseType}. GetFields(BindingFlags.Instance) does " +
                "not see a base type's PRIVATE fields, so the Clear-completeness sweep just stopped being exhaustive " +
                "without failing. Walk the base-type chain in DivergingFields()/SweptFieldNames()/SyntheticallyFillArrays().");

            // The real exclusion guarantee: the allowlisted names must be the ONLY delegate-typed instance fields on
            // EntityWorld. A future host-lifetime subscription added WITHOUT an allowlist entry now fails the sweep
            // loudly (its own entry is required, with justification), and one added WITH an entry fails here unless it
            // is genuinely a delegate. This is what the old `Assert.Equal(2, Allowlist.Length)` count-check gestured at
            // but could not deliver — that assertion was defeated by editing the literal on the same line as the entry.
            var delegateFields = new List<string>();
            foreach (FieldInfo f in typeof(EntityWorld).GetFields(AllInstanceFields))
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
                FieldInfo? f = typeof(EntityWorld).GetField(name, AllInstanceFields);
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
        /// state classes a public-array-only sweep would miss.</summary>
        private static EntityWorld BuildDirtyWorld()
        {
            var w = new EntityWorld();

            // Sim-globals first, so the spawns below sample the injected elevation grid (dirtying Elevation too).
            w.HeightAdvantageVision    = true;
            w.HeightVisionBonusPerStep = Fixed.FromInt(9);
            w.SetElevationGrid(new ElevationGrid(new[] { Fixed.FromInt(3) }, 1, 1, Fixed.Zero, Fixed.Zero, Fixed.One));

            // NOTE ON THE PER-ENTITY WRITES BELOW: they are NOT what dirties these arrays for the assertion —
            // SyntheticallyFillArrays() runs last and overwrites every element of every array field, superseding all
            // of them. They are kept because the Create/Destroy calls themselves drive the real allocation, free-list
            // and id-counter paths (AliveCount, _nextId, _freeCount and Rng are the state the synthetic fill CANNOT
            // reach), and because a realistically-shaped world documents what these arrays hold. Do not read them as
            // load-bearing coverage; the coverage guarantee comes from the reflection fill plus the per-field
            // precondition above.
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

            // DW-367: dirty the per-tick death log. The Destroys above are the NON-combat path (which never records);
            // push a record directly so the per-field precondition sees the field moved off its fresh value and the
            // sweep stays non-blind on it — Clear() must count-reset it (records past Count are unread by contract).
            w.DeathLog.Push(1, 0, 2, 1);

            w.Rng.NextInt(1000); // advance the folded RNG state off DEFAULT_RNG_SEED

            // Story 6.5 sim-global: the pathability grid Clear() drops. The ctor only accepts an exactly
            // PathabilityGrid.CELL_COUNT-long mask (anything else is silently replaced by an empty one).
            w.SetPathabilityGrid(new ProjectChimera.Navigation.PathabilityGrid(
                new bool[ProjectChimera.Navigation.PathabilityGrid.CELL_COUNT]));

            // ── The SELF-PINNING half: fill EVERY swept array with a non-default value in EVERY element. ──
            // Hand-dirtying only reaches the arrays someone remembered; a NEW per-entity array added by a future story
            // would sit at its fresh value in both worlds and Clear() could omit it forever. Reflection makes the
            // fixture grow with the type, so a new ARRAY field is auto-dirtied with no edit to this test.
            SyntheticallyFillArrays(w);
            return w;
        }

        /// <summary>Write a non-default value into every element of every swept array field of
        /// <paramref name="w"/>. Dispatches on the array's ELEMENT type; an element type nobody has taught it about
        /// throws rather than being silently skipped (a silent skip would re-open exactly the blind spot this
        /// machinery exists to close).</summary>
        private static void SyntheticallyFillArrays(EntityWorld w)
        {
            foreach (FieldInfo f in typeof(EntityWorld).GetFields(AllInstanceFields))
            {
                if (Array.IndexOf(Allowlist, f.Name) >= 0) continue;
                if (f.GetValue(w) is not Array arr) continue;

                // DescribeArrayDivergence deliberately handles rank > 1; this fill does not (Array.SetValue(object,int)
                // throws ArgumentException on a multidimensional array). Fail with the instruction rather than that
                // bare reflection error, so the two halves cannot drift apart silently.
                if (arr.Rank > 1)
                    throw new InvalidOperationException(
                        $"EntityWorld.{f.Name} is a rank-{arr.Rank} array. The comparison half of this sweep already " +
                        "supports multidimensional arrays, but the synthetic fill does not — teach " +
                        "SyntheticallyFillArrays() to walk it, or the field enters the sweep undirtied and a Clear() " +
                        "that omits it passes GREEN.");

                Type et = arr.GetType().GetElementType()!;
                object filler = NonDefaultValue(et, f.Name);
                for (int i = 0; i < arr.Length; i++) arr.SetValue(filler, i);
            }
        }

        /// <summary>A value guaranteed != <c>default(<paramref name="elementType"/>)</c>, for the synthetic fill.</summary>
        private static object NonDefaultValue(Type elementType, string fieldName)
        {
            if (elementType == typeof(bool)) return true;

            if (elementType.IsEnum) return Enum.ToObject(elementType, 1);

            if (elementType == typeof(byte) || elementType == typeof(sbyte) ||
                elementType == typeof(short) || elementType == typeof(ushort) ||
                elementType == typeof(int) || elementType == typeof(uint) ||
                elementType == typeof(long) || elementType == typeof(ulong))
                return Convert.ChangeType(7, elementType);

            if (elementType == typeof(Fixed)) return Fixed.FromInt(7);

            if (elementType == typeof(FixedVec3))
                return new FixedVec3(Fixed.FromInt(7), Fixed.FromInt(7), Fixed.FromInt(7));

            if (elementType == typeof(string)) return "dirty";

            if (!elementType.IsValueType && !elementType.IsAbstract && !elementType.IsArray)
            {
                // Activator.CreateInstance THROWS (MissingMethodException) for a reference type with no public
                // parameterless ctor — which would escape as a bare reflection error and bury the guidance below, the
                // one case that guidance exists for. Catch it and fall through to the explicit instruction instead.
                try
                {
                    object? instance = Activator.CreateInstance(elementType);
                    if (instance is not null) return instance;
                }
                catch (MissingMemberException) { /* fall through to the guiding throw */ }
            }

            throw new InvalidOperationException(
                $"EntityWorld.{fieldName} is an array of '{elementType.FullName}', a type the Clear-completeness " +
                "synthetic fill does not know how to dirty. Teach NonDefaultValue() about it — leaving it unhandled " +
                "would silently exempt the field from the sweep, which is the exact blind spot DW-19 closed.");
        }

        /// <summary>Every <see cref="EntityWorld"/> instance field the sweep covers (i.e. all of them bar the
        /// allowlisted host-lifetime delegates).</summary>
        private static IReadOnlyList<string> SweptFieldNames()
        {
            var names = new List<string>();
            foreach (FieldInfo f in typeof(EntityWorld).GetFields(AllInstanceFields))
                if (Array.IndexOf(Allowlist, f.Name) < 0) names.Add(f.Name);
            return names;
        }

        // ── The comparison core ────────────────────────────────────────────────────────────────

        /// <summary>Reflect over EVERY instance field of <see cref="EntityWorld"/> and return the names (with a short
        /// reason) of the fields on which <paramref name="actual"/> differs from <paramref name="expected"/>. Arrays
        /// compare elementwise (null-safe, so the <c>UnitDefinition[]</c>/<c>CombatFeedbackProfile[]</c> reference
        /// arrays are covered); everything else compares by value/null.</summary>
        private static IReadOnlyList<(string Field, string Detail)> DivergingFields(EntityWorld expected, EntityWorld actual)
        {
            var diverged = new List<(string, string)>();

            foreach (FieldInfo f in typeof(EntityWorld).GetFields(AllInstanceFields))
            {
                if (Array.IndexOf(Allowlist, f.Name) >= 0) continue; // delegates Clear() deliberately keeps

                object? a = f.GetValue(expected);
                object? b = f.GetValue(actual);

                if (a is SimRng ra && b is SimRng rb)
                {
                    // Rng is a fixed reference with mutable internal state (never reassigned), so reference equality
                    // is meaningless — Clear() RE-SEEDS it. Compare the folded state, which is what the checksum reads.
                    if (ra.State != rb.State)
                        diverged.Add((f.Name, $".State: expected 0x{ra.State:X16}, actual 0x{rb.State:X16}"));
                    continue;
                }

                if (a is DeathLog da && b is DeathLog db)
                {
                    // DW-367: like Rng, the death log is a fixed reference with mutable internal state (never
                    // reassigned), so reference equality is meaningless — Clear() count-resets it. Compare the
                    // OBSERVABLE state: Count plus every record up to Count (slots past Count are unread by
                    // contract — the PatrolWaypoints discipline, so stale bytes there are not divergence).
                    string? why = DescribeDeathLogDivergence(da, db);
                    if (why is not null) diverged.Add((f.Name, why));
                    continue;
                }

                if (a is Array ea && b is Array eb)
                {
                    string? why = DescribeArrayDivergence(ea, eb);
                    if (why is not null) diverged.Add((f.Name, why));
                    continue;
                }

                if (!Equals(a, b))
                    diverged.Add((f.Name, $": expected '{a ?? "null"}', actual '{b ?? "null"}'"));
            }

            return diverged;
        }

        /// <summary>Null-safe elementwise array comparison; returns a located description of the FIRST differing
        /// element (or a length mismatch), or null when the arrays are equal.
        ///
        /// <para>Handles two shapes <see cref="EntityWorld"/> does not use TODAY but this class — an explicitly
        /// future-proof tripwire — must not break on: MULTIDIMENSIONAL arrays (<c>Array.GetValue(int)</c> throws
        /// <see cref="ArgumentException"/> on rank &gt; 1, so those are walked via flat enumeration) and JAGGED arrays
        /// (whose elements are themselves arrays, which reference-compare as unequal under
        /// <see cref="object.Equals(object,object)"/> — so this recurses into them).</para></summary>
        private static string? DescribeArrayDivergence(Array expected, Array actual)
        {
            if (expected.Rank != actual.Rank)
                return $": rank {expected.Rank} vs {actual.Rank}";

            if (expected.Length != actual.Length)
                return $": length {expected.Length} vs {actual.Length}";

            if (expected.Rank > 1)
            {
                // Flat enumeration: index-free, but valid for any rank (and the dimension lengths are checked first so
                // two same-Length arrays of different shape are still reported).
                for (int d = 0; d < expected.Rank; d++)
                    if (expected.GetLength(d) != actual.GetLength(d))
                        return $": dimension {d} length {expected.GetLength(d)} vs {actual.GetLength(d)}";

                IEnumerator ae = expected.GetEnumerator(), be = actual.GetEnumerator();
                int flat = 0;
                while (ae.MoveNext() && be.MoveNext())
                {
                    string? why = DescribeElementDivergence(ae.Current, be.Current);
                    if (why is not null) return $"[flat {flat}]{why}";
                    flat++;
                }
                return null;
            }

            for (int i = 0; i < expected.Length; i++)
            {
                string? why = DescribeElementDivergence(expected.GetValue(i), actual.GetValue(i));
                if (why is not null) return $"[{i}]{why}";
            }

            return null;
        }

        /// <summary>DW-367: the death log's observable state (Count + the records up to Count) — the exact state the
        /// director's unit_dies drain reads. Returns a located description of the first divergence, or null.</summary>
        private static string? DescribeDeathLogDivergence(DeathLog expected, DeathLog actual)
        {
            if (expected.Count != actual.Count)
                return $".Count: expected {expected.Count}, actual {actual.Count}";
            for (int i = 0; i < expected.Count; i++)
                if (expected.VictimAt(i) != actual.VictimAt(i) || expected.VictimSlotAt(i) != actual.VictimSlotAt(i) ||
                    expected.KillerAt(i) != actual.KillerAt(i) || expected.KillerSlotAt(i) != actual.KillerSlotAt(i))
                    return $"[{i}]: expected ({expected.VictimAt(i)}, {expected.VictimSlotAt(i)}, {expected.KillerAt(i)}, " +
                           $"{expected.KillerSlotAt(i)}), actual ({actual.VictimAt(i)}, {actual.VictimSlotAt(i)}, " +
                           $"{actual.KillerAt(i)}, {actual.KillerSlotAt(i)})";
            return null;
        }

        /// <summary>Compare one array element, recursing when the element is itself an <see cref="Array"/> (jagged
        /// arrays would otherwise compare by REFERENCE and report every equal-valued inner array as divergent).</summary>
        private static string? DescribeElementDivergence(object? a, object? b)
        {
            if (a is Array ia && b is Array ib) return DescribeArrayDivergence(ia, ib);
            if (a is Array || b is Array) return $": expected '{a ?? "null"}', actual '{b ?? "null"}'";
            return Equals(a, b) ? null : $": expected '{a ?? "null"}', actual '{b ?? "null"}'";
        }
    }
}
