#nullable enable
using System;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Sim
{
    /// <summary>
    /// DW-762 — the teeth for <see cref="ClearCompletenessSweep"/>'s <c>NormalizeFresh</c> audit.
    ///
    /// <para><b>The hole.</b> <c>NormalizeFresh</c> runs on the FRESH instance after the dirty one has been dirtied,
    /// and it exists for exactly one idiom: a reset that deliberately RETAINS capacity (ResearchStore's
    /// never-shrinking inner arrays, TriggerEnabledStore's never-shrinking buffer) leaves the dirty side longer than a
    /// virgin fresh one, so the final compare would trip on an incidental length mismatch instead of measuring "did
    /// every value reset". Nothing constrained the hook, and the obvious-looking way to "match a retained buffer" is
    /// to replay the store's own reset/grow path on the fresh side — which normalizes fresh straight INTO the dirty
    /// state and makes the field the hook was written for vacuous. The sweep is the load-bearing reset-completeness
    /// guard across 25 store cases, so a hook that can quietly blind one field per fixture is a hole in a
    /// test-of-tests.</para>
    ///
    /// <para><b>These tests drive the shared machinery against a purpose-built store</b> whose reset retains capacity
    /// exactly like the two real stores that need the hook — so each mutant below is the realistic wrong version of a
    /// hook someone would actually write, not a contrived one. Each asserts on the DW-762 diagnosis by name: before
    /// the audit landed, two of the three surfaced (much later, and blamed on the wrong thing — a generic "the fixture
    /// never dirtied this field") and the third — a hook that silently does nothing at all — did not surface at
    /// all.</para>
    ///
    /// <para>Godot-free, no <c>Fixed</c>-to-float, no sim state — this is machinery-on-machinery.</para>
    /// </summary>
    public class ClearSweepNormalizeFreshGuardTests
    {
        /// <summary>
        /// A stand-in for the real stores that need <c>NormalizeFresh</c>: <see cref="Clear"/> zeroes the live values
        /// but deliberately NEVER shrinks <see cref="Buffer"/> (the host holds the store by reference and reuses it in
        /// place), so a dirtied instance stays longer than a freshly-constructed one and the fixture must grow the
        /// fresh side to match.
        /// </summary>
        private sealed class RetainingStore
        {
            public int Count;
            public int[] Buffer = Array.Empty<int>();

            public void Grow(int n)
            {
                if (Buffer.Length < n) Array.Resize(ref Buffer, n);
            }

            /// <summary>The reset ClearForReset would call: values wiped, capacity retained.</summary>
            public void Clear()
            {
                Count = 0;
                Array.Clear(Buffer, 0, Buffer.Length);
            }
        }

        private const int RetainedCapacity = 3;

        /// <summary>The fixture every case below varies: a dirtied store with a grown, filled buffer and a non-zero
        /// count, plus whatever <paramref name="normalizeFresh"/> the case is probing.</summary>
        private static StoreResetFixture Fixture(
            Func<RetainingStore, RetainingStore, Action?> normalizeFresh)
        {
            var fresh = new RetainingStore();
            var dirty = new RetainingStore();
            return new StoreResetFixture("RetainingStore.Clear()", fresh, dirty, dirty.Clear)
            {
                DirtyNonArrayState = () => { dirty.Grow(RetainedCapacity); dirty.Count = 5; },
                NormalizeFresh     = normalizeFresh(fresh, dirty),
            };
        }

        // ── The positive control: the hook used the way it is meant to be used ────────────────────────────

        [Fact]
        public void ACapacityOnlyNormalizeFresh_StillPasses()
        {
            // Grow the fresh side to the retained length and NOTHING else — the ResearchStore/TriggerEnabledStore
            // idiom. The buffer is [0,0,0] against the dirty [7,7,7], so it stays measurably dirty and the post-Clear
            // compare is real. If this ever fails, the audit has become over-strict and every real fixture is at risk.
            ClearCompletenessSweep.AssertClearRestoresFreshState(
                Fixture((fresh, _) => () => fresh.Grow(RetainedCapacity)));
        }

        [Fact]
        public void NoNormalizeFreshAtAll_IsUnaffected()
        {
            // The audit must be inert for the 23 fixtures that declare no hook — it runs only when one exists. Here
            // the length mismatch (0 vs 3) is what fails, i.e. the pre-existing behaviour, NOT a DW-762 diagnosis.
            var ex = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
                () => ClearCompletenessSweep.AssertClearRestoresFreshState(Fixture((_, _) => null)));
            Assert.DoesNotContain("DW-762", ex.Message, StringComparison.Ordinal);
        }

        // ── The three mutants ─────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void NormalizeFresh_ThatNormalizesFreshIntoTheDirtyState_IsRejected_NamingTheHookAndTheField()
        {
            // THE DW-762 defect verbatim: instead of matching the retained CAPACITY, the hook copies the dirty side's
            // VALUES across. Buffer is then equal on both sides BEFORE the reset runs, so a Clear() that forgets to
            // wipe the buffer could never fail this case again — while Count still diverges, so the fixture looks
            // healthy. Pre-audit this surfaced only as the generic "the fixture left these swept fields at their
            // FRESH value" precondition, which points the author at dirtying harder — the wrong cause entirely.
            var ex = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
                () => ClearCompletenessSweep.AssertClearRestoresFreshState(
                    Fixture((fresh, dirty) => () =>
                    {
                        fresh.Grow(RetainedCapacity);
                        Array.Copy(dirty.Buffer, fresh.Buffer, RetainedCapacity);
                    })));

            Assert.Contains("DW-762", ex.Message, StringComparison.Ordinal);
            Assert.Contains("NormalizeFresh", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Buffer", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void NormalizeFresh_ThatReplaysTheStoresOwnResetOnTheDirtySide_IsRejected()
        {
            // The other half of the ledger's "replaying the store's own Clear()" hazard: the hook reaches across to
            // the DIRTY instance. That erases the divergence the whole sweep measures — the reset is then compared
            // against state it never had to restore, and every field of the case is vacuous at once.
            var ex = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
                () => ClearCompletenessSweep.AssertClearRestoresFreshState(
                    Fixture((fresh, dirty) => () =>
                    {
                        fresh.Grow(RetainedCapacity);
                        dirty.Clear();
                    })));

            Assert.Contains("DW-762", ex.Message, StringComparison.Ordinal);
            Assert.Contains("mutated the DIRTY instance", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void NormalizeFresh_ThatChangesNothing_IsRejectedAsADeadHook()
        {
            // A hook whose store shape has since changed (or that was written against a field this sweep cannot see)
            // moves nothing. Pre-audit that shipped GREEN and silently: the fixture kept a justification comment
            // describing a normalization that was not happening.
            var ex = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
                () => ClearCompletenessSweep.AssertClearRestoresFreshState(
                    Fixture((fresh, _) => () => { /* rot: this hook no longer does anything */ })));

            Assert.Contains("DW-762", ex.Message, StringComparison.Ordinal);
            Assert.Contains("changed NOTHING", ex.Message, StringComparison.Ordinal);
        }

        // ── The snapshot has to see an IN-PLACE mutation, or rule 1 is vacuous ─────────────────────────────

        [Fact]
        public void TheAudit_SeesAnInPlaceCollectionMutation_NotJustAReplacedReference()
        {
            // Rule 1 ("the hook must move something") is only meaningful if the before/after snapshot COPIES
            // collections — the real hooks mutate arrays in place (Array.Resize on an inner array, a direct element
            // write), and comparing a live array against itself would report "changed nothing" for all of them. This
            // pins that: the hook writes elements in place into an already-correctly-sized fresh buffer, and the
            // audit must accept it as a genuine normalization rather than flagging a dead hook.
            var fresh = new RetainingStore();
            var dirty = new RetainingStore();
            fresh.Grow(RetainedCapacity); // already the retained length — only the ELEMENTS change below

            var fx = new StoreResetFixture("RetainingStore.Clear()", fresh, dirty, dirty.Clear)
            {
                DirtyNonArrayState = () => { dirty.Grow(RetainedCapacity); dirty.Count = 5; },
                // In-place element writes to a buffer whose reference never changes. Values 1..3 are distinct from
                // both the fresh zeros and the dirty 7s, so the field stays diverging in both directions.
                NormalizeFresh = () =>
                {
                    for (int i = 0; i < RetainedCapacity; i++) fresh.Buffer[i] = i + 1;
                },
            };

            // It must NOT report a dead hook. (The case still fails at step 3 — the hook left junk the reset cannot
            // restore — which is correct and is exactly the pre-existing final compare doing its job.)
            var ex = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
                () => ClearCompletenessSweep.AssertClearRestoresFreshState(fx));
            Assert.DoesNotContain("changed NOTHING", ex.Message, StringComparison.Ordinal);
            Assert.Contains("RetainingStore.Buffer", ex.Message, StringComparison.Ordinal);
        }
    }
}
