#nullable enable
using System.Linq;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 9.6 (FR-39 freeze-and-continue regression gate) — a mid-match disconnect keeps the surviving peers in
    /// deterministic lockstep. Over <see cref="MidMatchDropScenario"/> (Player2 dropped at tick 100, sim run 300+
    /// ticks past the drop through the REAL <see cref="ProjectChimera.Multiplayer.Server.FrozenSlotInjector"/>):
    ///   (a) two independent runs of the drop path are byte-identical (the freeze is fully deterministic — no
    ///       static/shared-state leak, no wall-clock in the freeze path); and
    ///   (b) the drop run DIVERGES from the no-drop control (non-vacuous: freezing Player2's command stream really
    ///       changed the sim — idle units + a truncated bump fold — so a broken injector that silently kept applying
    ///       Player2's orders, or dropped the faction from the sim, would be caught).
    ///
    /// The dropped faction is NEVER removed from the sim or <c>SimChecksum</c> — it stays folded (idle) — so no
    /// pre-existing golden and no <c>SimChecksum.AlgoVersion</c> moves (that would be a Block-If, not a re-baseline).
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
        public void DropPath_DivergesFromNoInjectionReference()
        {
            // The teeth of the gate: a NO-OP injector (Player2 gone AND no empties injected → merge stalls, Player1's
            // post-drop orders never apply) is deterministic, diverges from the no-drop control, and matches the
            // pre-drop tail — so it would slip past every OTHER assertion here. The REAL drop run (Player1 continues,
            // Player2 idle via injected empties) must DIVERGE from that no-injection reference, proving the injector
            // actually delivers Player1's ongoing commands post-drop.
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
    }
}
