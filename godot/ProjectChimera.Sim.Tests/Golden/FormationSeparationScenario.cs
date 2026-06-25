using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 1.13 (AC6d) — the FORMATION-SEPARATION golden scenario. A fixed, in-code, all-<see cref="Fixed"/>
    /// world that exercises the new separation behaviour through the real 9-system tick:
    ///   • a MOVING unit walking +X through a cluster of IDLE units (exercises AC1 moving-vs-idle bias via Position),
    ///   • idle units of DIFFERENT CollisionRadius (exercises AC2b summed-radii contact via Position),
    ///   • a Push unit contacted by a Yield unit, off the lane (exercises AC2c push-beats-yield via Position).
    /// Stepped via <see cref="SimulationHost.StepOnce"/> at ChecksumInterval = 1, the per-tick
    /// <see cref="SimChecksum"/> sequence (v5 — CollisionRadius + SeparationPriorityOf folded) pins the
    /// deterministic evolution of the separation rewrite.
    ///
    /// CROSS-PLATFORM SAFE (mirrors <see cref="CommandVocabularyScenario"/>, NOT the Windows-gated AI golden):
    /// every authored field and every hashed field is integer / <see cref="Fixed"/> only — no float in the hashed
    /// path. All units are Player1 with ZERO attack damage (CombatSystem's non-combatant guard skips them → they
    /// only move/separate, never fight), and Player2 is left EMPTY so the float-scoring AI no-ops deterministically.
    /// So this golden's match assertion runs on EVERY OS (it is NOT Windows-gated) — compared on both CI legs.
    /// </summary>
    public static class FormationSeparationScenario
    {
        /// <summary>300 ticks = 10s at 30 tps; with ChecksumInterval = 1 that yields 300 samples (ticks 1..300).</summary>
        public const int DefaultTicks = 300;

        /// <summary>
        /// Construct a fresh, fully-wired sim exercising the separation rewrite. Allocates new stores/systems on
        /// every call (no static/shared mutable state) so two in-process runs are independent and a fresh process
        /// reproduces the committed golden exactly. Mirrors <see cref="CommandVocabularyScenario.Build"/>.
        /// </summary>
        public static GoldenHarness Build()
        {
            var host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(2),       // P1 + P2 active; P2 empty so the AI no-ops (hash stays float-free)
                new FactionDefinition(),
                new FactionDefinition());
            host.ChecksumInterval = 1;        // checksum every tick

            int mover = PopulateScenario(host.World);

            // Mirror MainScene's director lifecycle (empty triggers → ScenarioDirector.Tick is a faithful no-op).
            host.ScenarioDirector.LoadScenario(new ScenarioData());

            return new GoldenHarness(host, mover);
        }

        /// <summary>
        /// Author the fixed scenario. All units are Player1 non-combatants (0 attack damage → never fight), so the
        /// only thing the tick does to them is MOVE + SEPARATE — exactly the surface 1.13 changed. Returns the
        /// moving unit's id as a harmless perturbation handle for <see cref="GoldenHarness"/>.
        /// </summary>
        private static int PopulateScenario(EntityWorld w)
        {
            // id 0 — MOVING unit: walks +X (over 300 ticks at speed 3 it travels ~30 units, from x=-20 to ~x=10),
            // passing through the idle cluster. Idle neighbours yield MORE than the mover (AC1 bias).
            int mover = Unit(w, V(-20, 0, 0));
            w.CommandState[mover] = UnitCommand.Move;
            w.CommandGoal[mover]  = V(40, 0, 0);
            w.MoveTarget[mover]   = V(40, 0, 0);
            w.Flags[mover]       |= EntityFlags.Moving;

            // ids 1..4 — idle cluster on/near the lane (offset in z so the push is 2D), with DIFFERENT radii:
            // a small-radius (0.5) unit contacts later than the default-radius (1.0) units (AC2b summed radii).
            int small = Unit(w, V(0, 0, 1));
            w.CollisionRadius[small] = Fixed.Half;
            Unit(w, V(4,  0, -1)); // id 2 — default radius
            Unit(w, V(8,  0,  1)); // id 3 — default radius
            Unit(w, V(12, 0, -1)); // id 4 — default radius

            // ids 5..6 — a Push unit contacted by a Yield unit, OFF the lane (z=5) so it is isolated: the mover
            // never reaches x=20. The Push unit holds its ground; the Yield unit drifts away (AC2c).
            int push = Unit(w, V(20, 0, 5));
            w.SeparationPriorityOf[push] = SeparationPriority.Push;
            int yield = Unit(w, new FixedVec3(Fixed.FromInt(20) + Fixed.Half, Fixed.Zero, Fixed.FromInt(5))); // x=20.5
            w.SeparationPriorityOf[yield] = SeparationPriority.Yield;

            return mover;
        }

        /// <summary>A Player1 non-combatant (0 attack damage by Create() default → never fights; only moves/separates).</summary>
        private static int Unit(EntityWorld w, FixedVec3 pos)
            => w.Create(pos, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));

        private static FixedVec3 V(int x, int y, int z)
            => new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));
    }
}
