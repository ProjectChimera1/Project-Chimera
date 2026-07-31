#nullable enable
using ProjectChimera.Core.Bootstrap;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Bootstrap
{
    /// <summary>
    /// DW-229 Tier-1 coverage for the pure reset step of <see cref="SlotFactionResolver"/> — the
    /// <see cref="SlotFactionReset.ToSeeded"/> prologue that makes a cleared/repointed slot faction_json revert to
    /// its _Ready-seeded default and makes re-resolution idempotent. The file-IO half of Resolve
    /// (GlobalizePath / File.Exists / LoadFromFile) is Godot-coupled and verified behind the in-engine gate; only
    /// the reset/revert-to-seeded semantics run here.
    /// </summary>
    public class SlotFactionResolverTests
    {
        /// <summary>Reset-to-seeded overwrite: a slot resolved to a different def since boot is restored to its
        /// seeded default; a slot seeded null (an empty/unassigned faction_json's baseline) reverts to null.</summary>
        [Fact]
        public void ToSeeded_RestoresEverySlotToItsSeededDefault()
        {
            var alpha = new FactionDefinition();
            var beta  = new FactionDefinition();
            var seeded = new FactionDefinition?[] { alpha, beta, null }; // the _Ready baseline [P1, P2, rest null]

            // Simulate a prior resolve having overwritten every slot with something else.
            var other = new FactionDefinition();
            var live = new FactionDefinition?[] { other, other, other };

            SlotFactionReset.ToSeeded(live, seeded);

            Assert.Same(alpha, live[0]);
            Assert.Same(beta, live[1]);
            Assert.Null(live[2]); // the cleared/empty slot reverts to its seeded default (null)
        }

        /// <summary>The reset mutates the live array IN PLACE (never reassigns) and does not alias the seeded
        /// baseline — a later mutation of the live array must not bleed into the seeded defaults.</summary>
        [Fact]
        public void ToSeeded_MutatesInPlace_AndDoesNotAliasSeeded()
        {
            var alpha = new FactionDefinition();
            var seeded = new FactionDefinition?[] { alpha, null };
            var live = new FactionDefinition?[] { null, null };
            FactionDefinition?[] liveRef = live;

            SlotFactionReset.ToSeeded(live, seeded);

            Assert.Same(liveRef, live);      // same backing array — resolver aliasing invariant
            Assert.Same(alpha, live[0]);

            // Mutating the live array after the reset must not touch the seeded baseline (distinct arrays).
            live[0] = new FactionDefinition();
            Assert.Same(alpha, seeded[0]);
        }

        /// <summary>Idempotency: applying the reset repeatedly (many Edit↔Play re-applies) yields the identical
        /// baseline every time regardless of the live array's prior contents.</summary>
        [Fact]
        public void ToSeeded_IsIdempotentAcrossRepeatedApplies()
        {
            var alpha = new FactionDefinition();
            var beta  = new FactionDefinition();
            var seeded = new FactionDefinition?[] { alpha, beta, null };
            var live = new FactionDefinition?[] { null, null, null };

            for (int i = 0; i < 5; i++)
            {
                live[i % 3] = new FactionDefinition(); // arbitrary drift between applies
                SlotFactionReset.ToSeeded(live, seeded);
                Assert.Same(alpha, live[0]);
                Assert.Same(beta, live[1]);
                Assert.Null(live[2]);
            }
        }
    }
}
