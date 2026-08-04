#nullable enable
using System.Linq;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 9.6 (FR-39 freeze-and-continue regression gate) — a mid-match disconnect keeps the surviving peers in
    /// deterministic lockstep. Over <see cref="MidMatchDropScenario"/> (Player2 dropped at tick 100, sim run 300+
    /// ticks past the drop through the REAL <see cref="ProjectChimera.Multiplayer.Server.FrozenSlotInjector"/>).
    ///
    /// What IS verified here (DW-416 corrected the over-claiming comment):
    ///   (a) two independent runs of the drop path are byte-identical (the freeze is fully deterministic — no
    ///       static/shared-state leak, no wall-clock in the freeze path);
    ///   (b) the drop run DIVERGES from a no-drop control and from a no-injection reference (non-vacuous: freezing
    ///       Player2's command stream really changed the sim, and the injector really delivers Player1's ongoing
    ///       commands — a stubbed injector or one that kept applying Player2's orders would be caught);
    ///   (c) THE POSITIVE PIN (DW-416): the drop run is BYTE-IDENTICAL to an explicit-idle control in which Player2
    ///       stays connected and submits empty (zero-order) packets every post-drop tick — so the freeze is pinned
    ///       to the CORRECT idle-but-folded state, not merely "deterministic and different". A deterministic-but-
    ///       wrong injector output (garbage orders, wrong sub-bundle shape, a faction dropped from the merge) would
    ///       diverge from the genuine-idle stream and fail this equality.
    ///   (d) DW-413: AC3's named passive-sim straddle cases are CONSTRUCTED and probed — a Player2 projectile
    ///       in flight across the drop tick lands post-drop, and a Player2 unit mid-health-regen at the drop
    ///       completes its regen post-drop.
    ///
    /// The dropped faction is NEVER removed from the sim or <c>SimChecksum</c> — it stays folded (idle) — so no
    /// pre-existing golden and no <c>SimChecksum.AlgoVersion</c> moves (that would be a Block-If, not a re-baseline).
    /// This scenario is deliberately baseline-free: every gate is relative to a control built in the same run.
    /// </summary>
    public class MidMatchDropDesyncTests
    {
        [Fact]
        public void DropPath_IsDeterministic_AcrossTwoRuns_AndKeepsEvolving()
        {
            var run1 = MidMatchDropScenario.RunDrop();
            var run2 = MidMatchDropScenario.RunDrop();

            var div = GoldenChecksumReplay.CompareSequences(run1, run2);
            Assert.True(div is null,
                div is null ? "" : "Two freeze-and-continue runs diverged (nondeterminism in the drop path): "
                                    + GoldenChecksumReplay.DescribeDivergence(div.Value));

            // Non-vacuity: the sim must KEEP EVOLVING for 300+ ticks AFTER the drop (proves the injector keeps
            // delivering Player1's ongoing commands, not a frozen/constant tail). A stalled merge would plateau.
            var postDrop = run1.Where(s => (int)s.Tick > MidMatchDropScenario.DefaultDropTick).ToList();
            Assert.True(postDrop.Count >= 300,
                $"Expected 300+ post-drop samples, got {postDrop.Count}.");
            Assert.True(postDrop.Select(s => s.Hash).Distinct().Count() > 1,
                "The post-drop checksum sub-sequence is CONSTANT — the sim stopped evolving, so the injector is not " +
                "delivering the survivor's ongoing commands (a stalled/no-op merge would look exactly like this).");
        }

        [Fact]
        public void DropPath_MatchesExplicitIdleControl_ByteForByte()
        {
            // DW-416 — the de-relativized POSITIVE assertion. The prior gate proved the drop run was deterministic
            // and DIFFERENT from two references, but never that it was the CORRECT state: a deterministic-but-wrong
            // injector (still different from both references) would have passed. The explicit-idle control pins the
            // meaning of the freeze: Player2 stays connected and genuinely submits ZERO-order packets each post-drop
            // tick — the injected empties must be indistinguishable from that at every checksum, i.e. "frozen" IS
            // "idle-but-still-folded", exactly what the DropDirective doc promises.
            var drop = MidMatchDropScenario.RunDrop();
            var idle = MidMatchDropScenario.RunDropIdleControl();

            var div = GoldenChecksumReplay.CompareSequences(idle, drop);
            Assert.True(div is null,
                div is null ? "" : "The freeze-and-continue run DIVERGED from the explicit-idle control — the " +
                "injected empty stream is not equivalent to the dropped player genuinely idling, so the frozen slot " +
                "is in a WRONG (not merely idle) state: " + GoldenChecksumReplay.DescribeDivergence(div.Value));
        }

        [Fact]
        public void DropPath_DivergesFromNoInjectionReference()
        {
            // The teeth of the gate: a NO-OP injector (Player2 gone AND no empties injected → merge stalls, Player1's
            // post-drop orders never apply) is deterministic, diverges from the no-drop control, and matches the
            // pre-drop tail — so it would slip past every OTHER assertion here except the idle-equality (which it
            // also fails, by stalling). The REAL drop run (Player1 continues, Player2 idle via injected empties)
            // must DIVERGE from that no-injection reference, proving the injector actually delivers Player1's
            // ongoing commands post-drop.
            var drop     = MidMatchDropScenario.RunDrop();
            var noInject = MidMatchDropScenario.RunDropNoInject();

            var div = GoldenChecksumReplay.CompareSequences(noInject, drop);
            Assert.True(div is not null,
                "The injected drop run produced IDENTICAL checksums to the no-injection reference — the injector is " +
                "not delivering the survivor's ongoing commands (a stubbed/no-op injector would pass every other test).");
            Assert.True(div!.Value.Tick >= MidMatchDropScenario.DefaultDropTick,
                $"Divergence from the no-injection reference appeared at tick {div.Value.Tick}, before the drop at " +
                $"{MidMatchDropScenario.DefaultDropTick} — the two must be identical until the injection begins.");
        }

        [Fact]
        public void DropPath_DivergesFromNoDropControl()
        {
            var drop   = MidMatchDropScenario.RunDrop();
            var noDrop = MidMatchDropScenario.RunNoDrop();

            // Up to the drop tick the two runs are identical (both factions submit); AT/after the drop they must part.
            var div = GoldenChecksumReplay.CompareSequences(noDrop, drop);
            Assert.True(div is not null,
                "The freeze-and-continue run produced identical checksums to the no-drop control — the freeze had no " +
                "observable effect, so this scenario cannot detect an injector that keeps applying the dropped faction's orders.");
            Assert.True(div!.Value.Tick >= MidMatchDropScenario.DefaultDropTick,
                $"Divergence appeared at tick {div.Value.Tick}, before the drop at {MidMatchDropScenario.DefaultDropTick} — " +
                "the pre-drop sequence must match the control exactly (only the freeze should change the sim).");
        }

        [Fact]
        public void PreDropSequence_MatchesNoDropControl_ExactlyUntilTheDrop()
        {
            var drop   = MidMatchDropScenario.RunDrop().ToList();
            var noDrop = MidMatchDropScenario.RunNoDrop().ToList();

            // Every sample strictly BEFORE the drop tick must be byte-identical between drop and control runs.
            for (int i = 0; i < drop.Count && drop[i].Tick < MidMatchDropScenario.DefaultDropTick; i++)
                Assert.Equal(noDrop[i], drop[i]);
        }

        [Fact]
        public void Ac3StraddleCases_ProjectileInFlight_AndMidRegen_ContinueAcrossTheDrop()
        {
            // DW-413 — AC3's NAMED passive-sim examples, previously covered only transitively ("the real pipeline
            // ticks, so determinism implies them"), are now constructed and probed at the surface AC3 names:
            //  • a Player2 PROJECTILE is genuinely IN FLIGHT at the drop tick, and shots keep landing after it
            //    (the Neutral target's health keeps falling post-drop);
            //  • a Player2 unit is genuinely MID-HEALTH-REGEN at the drop tick, and completes its regen after it.
            // This drives the same DropDriver + real injector as the golden gate, then inspects the world directly.
            GoldenHarness h = MidMatchDropScenario.BuildDropHost();
            var driver = new MidMatchDropScenario.DropDriver(
                h.World, h.Host.DslEventSink, MidMatchDropScenario.DefaultDropTick);

            for (int i = 0; i < MidMatchDropScenario.DefaultDropTick; i++)
            {
                driver.ApplyTick(i, h.World);
                h.Host.StepOnce();
            }

            // ── At the drop boundary ──────────────────────────────────────────
            bool projectileInFlight = false;
            var store = h.Host.Projectiles;
            for (int p = 0; p < ProjectChimera.Combat.ProjectileStore.MAX_PROJECTILES; p++)
                if (store.Alive[p] && store.Owner[p] == Faction.Player2) { projectileInFlight = true; break; }
            Assert.True(projectileInFlight,
                "No Player2 projectile is in flight at the drop tick — the AC3 straddle case is not constructed " +
                "(retune ProjectileSpeed/positions so a pre-drop shot is still flying at tick 100).");

            Fixed regenAtDrop  = h.World.Health[MidMatchDropScenario.RegenUnitId];
            Fixed targetAtDrop = h.World.Health[MidMatchDropScenario.ProjectileTargetId];
            Assert.True(regenAtDrop > MidMatchDropScenario.RegenStartHealth,
                $"The regen unit has not healed at all by the drop tick (health {regenAtDrop}) — regen is not running.");
            Assert.True(regenAtDrop < MidMatchDropScenario.RegenMaxHealth,
                $"The regen unit is already full ({regenAtDrop}) at the drop tick — it does not STRADDLE the drop; " +
                "lower the heal rate or deepen the pre-damage.");

            // ── Across + past the drop (the freeze) ───────────────────────────
            for (int i = MidMatchDropScenario.DefaultDropTick; i < MidMatchDropScenario.DefaultTicks; i++)
            {
                driver.ApplyTick(i, h.World);
                h.Host.StepOnce();
            }

            Fixed targetAtEnd = h.World.Health[MidMatchDropScenario.ProjectileTargetId];
            Assert.True(targetAtEnd < targetAtDrop,
                $"The projectile target's health did not fall after the drop ({targetAtDrop} → {targetAtEnd}) — " +
                "in-flight/post-drop shots stopped landing, so passive combat did NOT continue across the freeze.");

            Fixed regenAtEnd = h.World.Health[MidMatchDropScenario.RegenUnitId];
            Assert.Equal(MidMatchDropScenario.RegenMaxHealth, regenAtEnd);
        }
    }
}
