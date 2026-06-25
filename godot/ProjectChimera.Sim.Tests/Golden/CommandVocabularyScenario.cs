using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 1.12 (AC6d) — the COMMAND-VOCABULARY golden scenario. A fixed, in-code, all-<see cref="Fixed"/>
    /// world that exercises EVERY order in one run: Move, AttackMove, Stop, HoldPosition, AttackTarget (forced
    /// onto a FAR enemy with a nearer decoy present, to pin force-fire), Patrol (a 3-waypoint route with an enemy
    /// on the lane), and Follow (escorting a moving friendly). Stepped via <see cref="SimulationHost.StepOnce"/>
    /// at ChecksumInterval = 1, the per-tick <see cref="SimChecksum"/> sequence pins the full command vocabulary's
    /// deterministic evolution (movement + combat + the new SoA command fields folded in at v4).
    ///
    /// CROSS-PLATFORM SAFE (unlike the AI-active golden): every authored field and every new hashed field is
    /// integer / <see cref="Fixed"/> only — no float in the hashed path. Player2 is left EMPTY (0 units, 0 ore,
    /// no buildings), so the <see cref="ProjectChimera.AI.AiOpponentSystem"/> at index 7 (which plays Player2)
    /// has nothing to act on and cannot afford anything — it no-ops deterministically, INTEGER-gated, exactly
    /// like <see cref="GoldenScenario"/> (which ships on the 1.10c Win↔Linux gate). So no float scoring ever
    /// reaches the hash, and this golden's match assertion runs on EVERY OS (it is NOT Windows-gated).
    /// </summary>
    public static class CommandVocabularyScenario
    {
        /// <summary>300 ticks = 10s at 30 tps; with ChecksumInterval = 1 that yields 300 samples (ticks 1..300).</summary>
        public const int DefaultTicks = 300;

        /// <summary>
        /// Construct a fresh, fully-wired sim exercising every command. Allocates new stores/systems on every call
        /// (no static/shared mutable state) so two in-process runs are independent and a fresh process reproduces
        /// the committed golden exactly. Mirrors <see cref="GoldenScenario.Build"/>'s host construction.
        /// </summary>
        public static GoldenHarness Build()
        {
            var host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(2),       // P1 + P2 active → checksum faction loop covers both (both 0 ore here)
                new FactionDefinition(),
                new FactionDefinition());
            host.ChecksumInterval = 1;        // checksum every tick

            int patrolUnit = PopulateScenario(host.World);

            // Mirror MainScene's director lifecycle (empty triggers → ScenarioDirector.Tick is a faithful no-op).
            host.ScenarioDirector.LoadScenario(new ScenarioData());

            return new GoldenHarness(host, patrolUnit);
        }

        /// <summary>
        /// Author the fixed scenario. Player1 gets one damage-bearing unit per order (a zero-damage unit would be
        /// skipped by CombatSystem's non-combatant guard and could not Patrol/Follow). Neutral units are passive
        /// targets (0 damage → never fight back; enemies of Player1 because their faction != Player1). Returns the
        /// patrol unit's id as a harmless perturbation handle for <see cref="GoldenHarness"/>.
        /// </summary>
        private static int PopulateScenario(EntityWorld w)
        {
            // ── Player1 command units (ids 0..6), one per order — created in ascending id order ──

            // id 0 — Move: walk right along an empty lane (z=10), then stop on arrival.
            int move = Combatant(w, V(-30, 0, 10), Faction.Player1);
            w.CommandState[move] = UnitCommand.Move;
            w.CommandGoal[move]  = V(-10, 0, 10);
            w.MoveTarget[move]   = V(-10, 0, 10);
            w.Flags[move]       |= EntityFlags.Moving;

            // id 1 — Follow: escort the Move unit (a friendly that moves), tracking within the leash.
            int follow = Combatant(w, V(-30, 0, 16), Faction.Player1);
            w.CommandState[follow]  = UnitCommand.Follow;
            w.CommandTarget[follow] = move;

            // id 2 — AttackMove: march right along z=-10, engaging the lane enemy en route, then resuming.
            int amove = Combatant(w, V(-15, 0, -10), Faction.Player1);
            w.CommandState[amove] = UnitCommand.AttackMove;
            w.CommandGoal[amove]  = V(20, 0, -10);
            w.MoveTarget[amove]   = V(20, 0, -10);
            w.Flags[amove]       |= EntityFlags.Moving;

            // id 3 — Stop: stand at z=20, attack the adjacent enemy that is in range, never chase.
            int stop = Combatant(w, V(5, 0, 20), Faction.Player1);
            w.CommandState[stop] = UnitCommand.Stop;

            // id 4 — HoldPosition: hold at z=-20, attack the adjacent enemy in range, never displaced.
            int hold = Combatant(w, V(5, 0, -20), Faction.Player1);
            w.CommandState[hold] = UnitCommand.HoldPosition;

            // id 5 — AttackTarget: force-fire the FAR forced enemy (set below), ignoring the nearer in-range decoy.
            int atk = Combatant(w, V(0, 0, 30), Faction.Player1);
            w.CommandState[atk] = UnitCommand.AttackTarget;

            // id 6 — Patrol: 3-waypoint route along z=0, engaging the lane enemy, advancing/reversing the route.
            int patrol = Combatant(w, V(-8, 0, 0), Faction.Player1);
            SetPatrolRoute(w, patrol, V(-8, 0, 0), V(0, 0, 0), V(8, 0, 0));
            w.CommandState[patrol] = UnitCommand.Patrol;
            w.MoveTarget[patrol]   = V(0, 0, 0);   // current leg target (matches OrderApplier's fresh-Patrol apply)
            w.Flags[patrol]       |= EntityFlags.Moving;

            // ── Neutral passive targets (ids 7..12). 0 attack damage → CombatSystem's non-combatant guard skips
            //    them, so they never fight back (deterministic, controlled combat). Player2 stays EMPTY → the AI
            //    no-ops, keeping this golden float-free / cross-platform-safe. ──
            Passive(w, V(5, 0, -10));              // id 7  — AttackMove lane enemy
            Passive(w, V(6, 0, 20));               // id 8  — Stop enemy
            Passive(w, V(6, 0, -20));              // id 9  — Hold enemy
            Passive(w, V(0, 0, 31));               // id 10 — AttackTarget DECOY (nearer, in range — must be ignored)
            int forced = Passive(w, V(0, 0, 38));  // id 11 — AttackTarget FORCED target (far, out of range)
            Passive(w, V(-4, 0, 0));               // id 12 — Patrol lane enemy

            w.CommandTarget[atk] = forced;         // force-fire the FAR target, not the nearer decoy

            return patrol;
        }

        /// <summary>A Player1 melee combatant (range &lt;= MELEE_THRESHOLD → instant damage; no projectiles needed).</summary>
        private static int Combatant(EntityWorld w, FixedVec3 pos, Faction f)
        {
            int id = w.Create(pos, f, Fixed.FromInt(100), Fixed.FromInt(3));
            w.AttackDamage[id] = Fixed.FromInt(10);
            w.AttackRange[id]  = Fixed.FromInt(2);
            w.AttackSpeed[id]  = Fixed.FromInt(1);
            w.DamageTypeOf[id] = DamageType.Normal;
            w.ArmorTypeOf[id]  = ArmorType.Unarmored;
            return id;
        }

        /// <summary>A passive Neutral target (0 attack damage → never fights back; an enemy of Player1).</summary>
        private static int Passive(EntityWorld w, FixedVec3 pos)
            => w.Create(pos, Faction.Neutral, Fixed.FromInt(30), Fixed.FromInt(3));

        private static void SetPatrolRoute(EntityWorld w, int id, params FixedVec3[] wps)
        {
            int b = id * EntityWorld.MAX_PATROL_WAYPOINTS;
            for (int k = 0; k < wps.Length; k++) w.PatrolWaypoints[b + k] = wps[k];
            w.PatrolCount[id] = (byte)wps.Length;
            w.PatrolIndex[id] = 1;
            w.PatrolDir[id]   = 1;
        }

        private static FixedVec3 V(int x, int y, int z)
            => new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));
    }
}
