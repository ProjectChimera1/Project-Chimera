#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// DW-469 — the two-lane admission policy on <see cref="CombatEventQueue"/>.
    ///
    /// <para>The queue used to be a flat 256-slot ring that ANY push could fill and that silently dropped everything
    /// past the cap. Story 11.4 then routed the player-facing NOTIFICATION cues (<c>OrderDenied</c>,
    /// <c>TrainingComplete</c>, <c>ResearchComplete</c>) onto that same lossy ring with no priority, so one large
    /// battle tick — trivially &gt;256 hit pushes at the 500-2000 entity target — could consume every slot and the
    /// player would simply never be told their order was refused or their unit finished training. The fix reserves
    /// <see cref="CombatEventQueue.PRIORITY_RESERVE"/> slots that the high-volume battle cues can never reach.</para>
    ///
    /// <para>Each test below fails against the pre-fix flat ring: the ambient flood fills all 256 slots and the
    /// notification push that follows is dropped. Nothing here can move a golden — the queue is not a SimChecksum
    /// input and no simulation system reads it (only the three presentation bridges do).</para>
    /// </summary>
    public class CombatEventQueueCapacityTests
    {
        /// <summary>Every declared event type, so the classification tests below cannot go stale when one is appended.</summary>
        private static IReadOnlyList<CombatEventType> AllEventTypes()
            => (CombatEventType[])Enum.GetValues(typeof(CombatEventType));

        /// <summary>Saturate the ambient lane the way a large single-tick battle does.</summary>
        private static void FloodWithBattleCues(CombatEventQueue q)
        {
            // Deliberately pushes far more than the ring holds — the point is that the overflow is real.
            for (int i = 0; i < CombatEventQueue.MAX_EVENTS * 2; i++)
                q.Push(CombatEventType.MeleeHit, FixedVec3.Zero, Faction.Player1);
        }

        // ── the reserve itself ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The load-bearing guarantee: a battle that pushes twice the whole ring in hit cues still leaves the full
        /// notification reserve free. Pre-fix this stops at 256 and every denial below is dropped.
        /// </summary>
        [Fact]
        public void BattleFlood_CannotFillPastTheAmbientCeiling()
        {
            var q = new CombatEventQueue();
            FloodWithBattleCues(q);

            Assert.Equal(CombatEventQueue.MAX_AMBIENT_EVENTS, q.Count);
            Assert.Equal(CombatEventQueue.PRIORITY_RESERVE, CombatEventQueue.MAX_EVENTS - q.Count);
        }

        /// <summary>
        /// A denial raised during that same saturated tick still reaches the queue — reason and acting faction intact,
        /// so <c>MatchAlertBridge</c> can render it. THE regression: pre-fix the denial vanished.
        /// </summary>
        [Fact]
        public void DenialRaisedDuringABattleFlood_IsStillAdmittedWithItsReason()
        {
            var q = new CombatEventQueue();
            FloodWithBattleCues(q);

            q.PushDenied(new FixedVec3(Fixed.FromInt(3), Fixed.Zero, Fixed.FromInt(4)),
                         Faction.Player2, DenialReason.SupplyCapped);

            Assert.Equal(CombatEventQueue.MAX_AMBIENT_EVENTS + 1, q.Count);
            CombatEvent denial = q.Get(q.Count - 1);
            Assert.Equal(CombatEventType.OrderDenied, denial.Type);
            Assert.Equal(DenialReason.SupplyCapped, denial.Reason);
            Assert.Equal(Faction.Player2, denial.Faction);
            Assert.Equal(Fixed.FromInt(3), denial.Position.X);
        }

        /// <summary>The production/research completion cues Story 11.4 added survive the same saturated tick.</summary>
        [Theory]
        [InlineData(CombatEventType.TrainingComplete)]
        [InlineData(CombatEventType.ResearchComplete)]
        public void CompletionCueRaisedDuringABattleFlood_IsStillAdmitted(CombatEventType type)
        {
            var q = new CombatEventQueue();
            FloodWithBattleCues(q);

            q.Push(type, FixedVec3.Zero, Faction.Player1);

            Assert.Equal(CombatEventQueue.MAX_AMBIENT_EVENTS + 1, q.Count);
            Assert.Equal(type, q.Get(q.Count - 1).Type);
            Assert.Equal(Faction.Player1, q.Get(q.Count - 1).Faction);
        }

        /// <summary>
        /// Totality: EVERY non-ambient type — not just the three Story 11.4 named — is admitted after a flood. An
        /// event type appended later inherits the protection automatically (<see cref="CombatEventQueue.IsAmbient"/>
        /// defaults to notification), and this test proves it rather than trusting the default.
        /// </summary>
        [Fact]
        public void EveryNotificationType_IsAdmittedAfterABattleFlood()
        {
            foreach (CombatEventType type in AllEventTypes())
            {
                if (CombatEventQueue.IsAmbient(type)) continue;

                var q = new CombatEventQueue();
                FloodWithBattleCues(q);
                q.Push(type, FixedVec3.Zero, Faction.Player1);

                Assert.True(q.Count == CombatEventQueue.MAX_AMBIENT_EVENTS + 1,
                            $"notification cue {type} was dropped by a saturated ambient lane.");
                Assert.Equal(type, q.Get(q.Count - 1).Type);
            }
        }

        /// <summary>The whole reserve is usable, not just its first slot — 64 back-to-back denials all land.</summary>
        [Fact]
        public void TheWholeReserve_IsAvailableToNotificationCues()
        {
            var q = new CombatEventQueue();
            FloodWithBattleCues(q);

            for (int i = 0; i < CombatEventQueue.PRIORITY_RESERVE; i++)
                q.PushDenied(FixedVec3.Zero, Faction.Player1, DenialReason.OnCooldown);

            Assert.Equal(CombatEventQueue.MAX_EVENTS, q.Count);
            for (int i = CombatEventQueue.MAX_AMBIENT_EVENTS; i < q.Count; i++)
                Assert.Equal(CombatEventType.OrderDenied, q.Get(i).Type);
        }

        // ── bounds safety ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The reserve is a floor, not an unbounded lane: notification cues past the full ring are dropped like any
        /// other overflow. Nothing writes past <c>_buf</c> and nothing throws.
        /// </summary>
        [Fact]
        public void NotificationCuesPastTheFullRing_AreDroppedNotOverrun()
        {
            var q = new CombatEventQueue();
            FloodWithBattleCues(q);

            for (int i = 0; i < CombatEventQueue.MAX_EVENTS * 2; i++)
                q.PushDenied(FixedVec3.Zero, Faction.Player1, DenialReason.NeedOre);

            Assert.Equal(CombatEventQueue.MAX_EVENTS, q.Count);
            Assert.Equal(CombatEventType.OrderDenied, q.Get(CombatEventQueue.MAX_EVENTS - 1).Type);
        }

        /// <summary>
        /// A notification-only flood cannot starve the ambient lane's shape either: the ring is shared, so once the
        /// count is at the ambient ceiling no further battle cue is admitted, and the ring never exceeds capacity.
        /// </summary>
        [Fact]
        public void NotificationFloodThenBattleCues_StillRespectsTheAmbientCeiling()
        {
            var q = new CombatEventQueue();
            for (int i = 0; i < CombatEventQueue.MAX_EVENTS * 2; i++)
                q.PushDenied(FixedVec3.Zero, Faction.Player1, DenialReason.QueueFull);

            Assert.Equal(CombatEventQueue.MAX_EVENTS, q.Count);

            q.Push(CombatEventType.MeleeHit, FixedVec3.Zero, Faction.Player1);
            Assert.Equal(CombatEventQueue.MAX_EVENTS, q.Count); // dropped: already past the ambient ceiling
        }

        // ── the lane classification ───────────────────────────────────────────────────────────────

        /// <summary>
        /// The battle cues — the types a fight emits per swing/impact/death/cast — are the ambient lane; the
        /// player-facing cues are not. Pins the split so a later edit cannot quietly move a denial into the lane the
        /// flood saturates (which would silently re-open DW-469 with every test above still green on hit spam).
        /// </summary>
        [Theory]
        [InlineData(CombatEventType.MeleeHit,          true)]
        [InlineData(CombatEventType.RangedHit,         true)]
        [InlineData(CombatEventType.SplashHit,         true)]
        [InlineData(CombatEventType.UnitKilled,        true)]
        [InlineData(CombatEventType.BuildingDestroyed, true)]
        [InlineData(CombatEventType.AbilityCast,       true)]
        [InlineData(CombatEventType.OrderDenied,       false)]
        [InlineData(CombatEventType.TrainingComplete,  false)]
        [InlineData(CombatEventType.ResearchComplete,  false)]
        [InlineData(CombatEventType.HeroFell,          false)]
        [InlineData(CombatEventType.HeroRevived,       false)]
        [InlineData(CombatEventType.ItemPickedUp,      false)]
        [InlineData(CombatEventType.ItemUsed,          false)]
        [InlineData(CombatEventType.ItemDropped,       false)]
        public void LaneClassification_IsPinnedPerEventType(CombatEventType type, bool ambient)
            => Assert.Equal(ambient, CombatEventQueue.IsAmbient(type));

        /// <summary>
        /// The table above must keep covering the whole enum: a newly appended type must be classified DELIBERATELY
        /// (it defaults to the protected lane, which is safe but should still be a conscious choice, and an
        /// unclassified high-volume type would eat the reserve).
        /// </summary>
        [Fact]
        public void LaneClassificationTable_CoversEveryEventType()
            => Assert.Equal(14, AllEventTypes().Count);

        // ── capacity + reset invariants ───────────────────────────────────────────────────────────

        /// <summary>
        /// The ambient ceiling is EXACTLY the pre-DW-469 flat capacity, so no battle visual or sound regressed: the
        /// fix only added notification headroom on top. If someone lowers this they are trading away hit feedback.
        /// </summary>
        [Fact]
        public void AmbientCeiling_MatchesThePreFixFlatCapacity()
        {
            Assert.Equal(256, CombatEventQueue.MAX_AMBIENT_EVENTS);
            Assert.Equal(64,  CombatEventQueue.PRIORITY_RESERVE);
            Assert.Equal(CombatEventQueue.MAX_AMBIENT_EVENTS + CombatEventQueue.PRIORITY_RESERVE,
                         CombatEventQueue.MAX_EVENTS);
        }

        /// <summary>Clear() releases BOTH lanes — the next frame starts with the full ambient ceiling available.</summary>
        [Fact]
        public void Clear_ReleasesBothLanes()
        {
            var q = new CombatEventQueue();
            FloodWithBattleCues(q);
            q.PushDenied(FixedVec3.Zero, Faction.Player1, DenialReason.NeedCrystal);

            q.Clear();

            Assert.Equal(0, q.Count);
            q.Push(CombatEventType.MeleeHit, FixedVec3.Zero, Faction.Player1);
            Assert.Equal(1, q.Count);
            Assert.Equal(CombatEventType.MeleeHit, q.Get(0).Type);

            FloodWithBattleCues(q);
            Assert.Equal(CombatEventQueue.MAX_AMBIENT_EVENTS, q.Count); // the ceiling is per-frame, not cumulative
        }

        /// <summary>
        /// Ordering is untouched: the drain walks [0, Count) in push order, so a denial raised after the flood is read
        /// after the hits it followed (the bridges' single-pass drain depends on this).
        /// </summary>
        [Fact]
        public void PushOrder_IsPreservedAcrossTheLanes()
        {
            var q = new CombatEventQueue();
            q.Push(CombatEventType.MeleeHit, FixedVec3.Zero, Faction.Player1);
            q.PushDenied(FixedVec3.Zero, Faction.Player1, DenialReason.NeedOre);
            q.Push(CombatEventType.RangedHit, FixedVec3.Zero, Faction.Player1);

            Assert.Equal(3, q.Count);
            Assert.Equal(CombatEventType.MeleeHit,    q.Get(0).Type);
            Assert.Equal(CombatEventType.OrderDenied, q.Get(1).Type);
            Assert.Equal(CombatEventType.RangedHit,   q.Get(2).Type);
        }
    }
}
