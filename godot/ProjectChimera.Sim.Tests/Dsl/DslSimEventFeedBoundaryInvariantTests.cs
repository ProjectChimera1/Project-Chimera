#nullable enable
using System;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// DW-850 — the <see cref="DslSimEventFeed"/> "empty at the checksum boundary" premise, ENFORCED rather than
    /// claimed. The DW-766 shape, one feed over.
    ///
    /// <para><b>The claim and the gap.</b> <see cref="DslSimEventFeed"/>'s type doc and
    /// <see cref="SimulationHost"/>'s property doc both exclude the feed from <see cref="SimChecksum"/> on the
    /// grounds that <c>ScenarioDirector</c> drains and clears it every tick. The director sits at a FIXED index in
    /// the tick order, and nothing structurally prevented a system registered AFTER it from pushing. That is not a
    /// stale-buffer nuisance: a drained occurrence FIRES TRIGGERS, and those triggers mutate FOLDED state (DSL
    /// variables, <c>WinStateStore</c>) — so residue is an unhashed input to hashed state, exactly the desync-detector
    /// hole DW-766 closed for the <c>DeathFeed</c>. It was not latent by luck either: DW-766's own residue pass had
    /// to be made credit-only specifically so it would not push <c>hero_level</c> into this feed from the index PAST
    /// the director.</para>
    ///
    /// <para><b>The fix under test.</b> <c>SimulationLoop.EnableTickBoundaryInvariants</c> takes the sim-event feed
    /// alongside the death feed and compares both to zero after every system has ticked and before the checksum is
    /// taken; <see cref="SimulationHost"/> arms it with its owned instance.</para>
    ///
    /// <para>Godot-free; the loop-level tests need no world state at all, which is the point — the tripwire fires on
    /// tick ORDER, not on any particular gameplay event.</para>
    /// </summary>
    public class DslSimEventFeedBoundaryInvariantTests
    {
        /// <summary>A stand-in for a future producer registered past the drain: pushes one occurrence every tick.</summary>
        private sealed class SimEventPushingSystem : ISimSystem
        {
            private readonly DslSimEventFeed _feed;
            public SimEventPushingSystem(DslSimEventFeed feed) => _feed = feed;
            public void Tick(EntityWorld world, Fixed dt) =>
                _feed.Push(DslSimEventFeed.KindUnitDamaged, 0, 1, 2, 3);
        }

        /// <summary>A stand-in for the real drain (ScenarioDirector's CollectEvents): consumes the feed.</summary>
        private sealed class SimEventDrainingSystem : ISimSystem
        {
            private readonly DslSimEventFeed _feed;
            public SimEventDrainingSystem(DslSimEventFeed feed) => _feed = feed;
            public void Tick(EntityWorld world, Fixed dt) => _feed.Clear();
        }

        [Fact]
        public void TickBoundaryInvariant_Throws_WhenAProducerIsRegisteredPastTheDirectorsDrain()
        {
            // The DW-850 failure mode, reproduced structurally: drain, THEN a producer. Pre-tripwire this was silent
            // and the checksum was taken over a non-empty feed whose contents drive folded DSL/win state.
            var world = new EntityWorld();
            var feed  = new DslSimEventFeed();
            var loop  = new SimulationLoop(world, new SimEventDrainingSystem(feed), new SimEventPushingSystem(feed));
            loop.EnableTickBoundaryInvariants(new ProjectChimera.Combat.DeathFeed(), feed);

            var ex = Assert.Throws<InvalidOperationException>(() => loop.StepOnce());
            Assert.Contains("DW-850", ex.Message, StringComparison.Ordinal);
            Assert.Contains("DslSimEventFeed", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void TickBoundaryInvariant_IsSilent_WhenTheDrainRunsAfterEveryProducer()
        {
            // Tooth: the tripwire must fire on ORDER, not on the mere existence of a sim event — otherwise every
            // ordinary combat tick would throw.
            var world = new EntityWorld();
            var feed  = new DslSimEventFeed();
            var loop  = new SimulationLoop(world, new SimEventPushingSystem(feed), new SimEventDrainingSystem(feed));
            loop.EnableTickBoundaryInvariants(new ProjectChimera.Combat.DeathFeed(), feed);

            loop.StepOnce();
            loop.Update(1f / SimulationLoop.TICKS_PER_SECOND); // the accumulator path checks it too

            Assert.Equal(0, feed.Count);
        }

        [Fact]
        public void TheDeathFeedArmStillReportsFirst_WhenBothFeedsHaveResidue()
        {
            // Both invariants are armed from one call, so their precedence is worth pinning: a tick that leaks both
            // must name the DeathFeed (DW-766), the older and more consequential of the two, rather than whichever
            // compare happened to be written second.
            var world  = new EntityWorld();
            var deaths = new ProjectChimera.Combat.DeathFeed();
            var feed   = new DslSimEventFeed();
            var loop   = new SimulationLoop(world,
                new SimEventPushingSystem(feed),
                new DeathPushingSystem(deaths));
            loop.EnableTickBoundaryInvariants(deaths, feed);

            var ex = Assert.Throws<InvalidOperationException>(() => loop.StepOnce());
            Assert.Contains("DW-766", ex.Message, StringComparison.Ordinal);
        }

        private sealed class DeathPushingSystem : ISimSystem
        {
            private readonly ProjectChimera.Combat.DeathFeed _feed;
            public DeathPushingSystem(ProjectChimera.Combat.DeathFeed feed) => _feed = feed;
            public void Tick(EntityWorld world, Fixed dt) =>
                _feed.Push(FixedVec3.Zero, Faction.Neutral, Fixed.FromInt(1));
        }

        [Fact]
        public void TheSimEventArm_IsInert_WhenUnarmed()
        {
            // Legacy/test loops that pass only a death feed must be unaffected — the parameter is optional precisely
            // so the pre-DW-850 one-argument call sites keep their exact behaviour.
            var world = new EntityWorld();
            var feed  = new DslSimEventFeed();
            var loop  = new SimulationLoop(world, new SimEventPushingSystem(feed));
            loop.EnableTickBoundaryInvariants(new ProjectChimera.Combat.DeathFeed());

            loop.StepOnce();

            Assert.Equal(1, feed.Count);
        }

        // ── The wiring: the REAL host must arm it, or every test above guards an unused seam ──────────────

        [Fact]
        public void SimulationHost_ArmsTheInvariantWithItsOwnFeed_AndAnOrdinaryTickStaysSilent()
        {
            // The seam is worth nothing unshipped: DW-766's tripwire is armed in production, and this one has to be
            // too. The real host's real tick order cannot be made to leak from outside (the director drains at its
            // fixed index, so any push a test makes BEFORE StepOnce is drained normally) — which is exactly why the
            // failure mode is exercised at the loop level above and the WIRING is asserted here.
            SimulationHost host = SimulationHost.Create(
                NullLogSink.Instance, new FactionRegistry(2), new FactionDefinition(), new FactionDefinition());

            host.DslSimEvents.Push(DslSimEventFeed.KindHeroLevel, 0, 1, 2, 3);
            host.StepOnce();
            Assert.Equal(0, host.DslSimEvents.Count); // the premise the fold-exclusion rests on, on the real host

            var loop = ProjectChimera.Sim.Tests.Sim.ClearCompletenessSweep
                .GetPrivate<SimulationLoop>(host, "_loop");
            var armed = ProjectChimera.Sim.Tests.Sim.ClearCompletenessSweep
                .GetPrivate<DslSimEventFeed?>(loop, "_boundarySimEvents");

            Assert.Same(host.DslSimEvents, armed); // armed with the SAME instance the producers push into
        }
    }
}
