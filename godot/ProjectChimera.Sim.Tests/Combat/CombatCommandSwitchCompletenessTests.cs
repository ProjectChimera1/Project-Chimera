#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Multiplayer;
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// DW-645 — the COMMAND-SWITCH COMPLETENESS guard, closing DW-206's defect CLASS.
    ///
    /// <para><b>The defect.</b> <see cref="CombatSystem"/>'s per-tick command switch routed unlisted commands through
    /// <c>default:</c> → <c>TickIdleCombat</c>. <see cref="UnitCommand.Build"/> was never given a case, so a unit
    /// walking to a build site silently inherited idle auto-combat: it acquired targets, chased them, and had the
    /// MoveTarget its builder needed overwritten every tick. The noncombatant-command-gate bundle added the Build arm
    /// and closed that INSTANCE. Nothing closed the CLASS: <see cref="UnitCommand"/> has 24 members and grows every
    /// epic, and the next one that PERSISTS as a <c>CommandState</c> would inherit the exact same behaviour by
    /// omission, with no compiler error and no failing test.</para>
    ///
    /// <para><b>The guard, in four layers.</b>
    /// <list type="number">
    /// <item>Every enum member is CLASSIFIED persisting/non-persisting by
    /// <see cref="UnitCommandTraits.PersistsAsCommandState"/> — an appended member is unclassified and throws.</item>
    /// <item>The declared persisting set is PINNED here, so a reclassification is a deliberate edit, not a drift.</item>
    /// <item>The declaration is checked against OBSERVED behaviour: every command that survives a real
    /// <see cref="OrderApplier.Apply"/> as the entity's stored <c>CommandState</c> must be declared persisting. This is
    /// the layer that needs no human action — a new wire command that quietly persists turns the suite red by itself.</item>
    /// <item>Every declared-persisting command must own an EXPLICIT case label in BOTH of <c>CombatSystem</c>'s command
    /// switches (the combatant one and the non-combatant router), read out of the shipping source between the DW-645
    /// guard anchors. Inheriting <c>default:</c> — the literal DW-206 defect — is what this forbids.</item>
    /// </list></para>
    ///
    /// <para><b>Non-vacuity.</b> The arm extractor is exercised against a DOCTORED copy of the real switch with the
    /// Build arm removed, and must report exactly the pre-DW-206 state. The classifier is exercised against an
    /// undeclared enum value, standing in for tomorrow's appended member.</para>
    ///
    /// <para>Godot-free, no <c>Fixed</c>-to-float, ascending-id — runs on every OS leg.</para>
    /// </summary>
    public class CombatCommandSwitchCompletenessTests
    {
        /// <summary>One tick at the 30 tps sim rate.</summary>
        private static readonly Fixed Dt = Fixed.One / Fixed.FromInt(30);

        /// <summary>
        /// The AUDITED persisting set: the commands that are stored in <c>EntityWorld.CommandState</c> and therefore
        /// drive per-tick routing. Editing this list means you have decided a command changed kind — which also means
        /// its routing arms must be added to (or removed from) <c>CombatSystem</c>'s two switches.
        /// </summary>
        private static readonly UnitCommand[] AuditedPersisting =
        {
            UnitCommand.Idle,
            UnitCommand.Move,
            UnitCommand.AttackMove,
            UnitCommand.Stop,
            UnitCommand.HoldPosition,
            UnitCommand.Build,
            UnitCommand.AttackTarget,
            UnitCommand.Patrol,
            UnitCommand.Follow,
            UnitCommand.AttackBuilding,
            UnitCommand.PickupItem,
        };

        /// <summary>
        /// Commands declared persisting that a wire order canNOT be observed to store — i.e. a command written into
        /// <c>CommandState</c> only by an internal system, never by <see cref="OrderApplier"/>. Empty today: every
        /// persisting command is reachable from the wire (Build included — it has no <c>ApplyActiveOrder</c> case, so
        /// the blind pre-switch store leaves it in place). Adding a name here is a claim that must be justified in
        /// review; it must never be used to silence layer 3 for a command the wire really does store.
        /// </summary>
        private static readonly UnitCommand[] DeclaredPersistingButNotWireObservable = Array.Empty<UnitCommand>();

        private static UnitCommand[] AllCommands() =>
            ((UnitCommand[])Enum.GetValues(typeof(UnitCommand))).OrderBy(c => (byte)c).ToArray();

        private static UnitCommand[] DeclaredPersisting() =>
            AllCommands().Where(UnitCommandTraits.PersistsAsCommandState).ToArray();

        // ── Layer 1 — every member is classified ──────────────────────────────────────────────────────────

        [Fact]
        public void EveryUnitCommandMember_IsClassifiedPersistingOrNot()
        {
            var unclassified = new List<string>();
            foreach (UnitCommand cmd in AllCommands())
            {
                try { UnitCommandTraits.PersistsAsCommandState(cmd); }
                catch (ArgumentOutOfRangeException) { unclassified.Add($"{cmd} ({(byte)cmd})"); }
            }

            Assert.True(unclassified.Count == 0,
                "DW-645: these UnitCommand members are not classified in UnitCommandTraits.PersistsAsCommandState: " +
                string.Join(", ", unclassified) + ". Decide whether each is STORED in EntityWorld.CommandState (a " +
                "per-tick control state) or CONSUMED at apply time by OrderApplier, and add its arm. If it persists, " +
                "it also needs explicit case labels in CombatSystem's two command switches — that is the whole point " +
                "of this guard (DW-206: Build persisted with no arm and silently inherited idle auto-combat).");
        }

        [Fact]
        public void AnUnclassifiedValue_Throws_SoAnAppendedMemberCannotPassSilently()
        {
            // Non-vacuity for layer 1: stands in for tomorrow's appended member, which the switch above cannot see.
            // 200 is outside the enum AND outside the 0x3F wire budget, so it can never collide with a real member.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => UnitCommandTraits.PersistsAsCommandState((UnitCommand)200));
        }

        // ── Layer 2 — the declared set is pinned ──────────────────────────────────────────────────────────

        [Fact]
        public void TheDeclaredPersistingSet_MatchesTheAudit()
        {
            Assert.Equal(
                AuditedPersisting.OrderBy(c => (byte)c).ToArray(),
                DeclaredPersisting());
        }

        [Fact]
        public void EveryNonPersistingCommand_IsAWireOrBuildingOrder_NotAnEntityControlState()
        {
            // The complement, pinned from the other side so a member cannot quietly drop OUT of the persisting set
            // (which would strand its routing arms as dead code, the mirror-image drift).
            var declaredNonPersisting = AllCommands().Where(c => !UnitCommandTraits.PersistsAsCommandState(c)).ToArray();
            Assert.Equal(AllCommands().Length - AuditedPersisting.Length, declaredNonPersisting.Length);
            Assert.DoesNotContain(UnitCommand.Idle, declaredNonPersisting);
        }

        // ── Layer 3 — the declaration is checked against observed OrderApplier behaviour ───────────────────

        [Fact]
        public void EveryCommandTheWireCanStore_IsDeclaredPersisting()
        {
            var observed = ObserveWireStoredCommands();

            var undeclared = observed.Where(c => !UnitCommandTraits.PersistsAsCommandState(c)).ToArray();
            Assert.True(undeclared.Length == 0,
                "DW-645: OrderApplier.Apply leaves these commands stored in EntityWorld.CommandState, so they DO " +
                "persist and drive per-tick routing, yet UnitCommandTraits declares them non-persisting: " +
                string.Join(", ", undeclared.Select(c => c.ToString())) + ". Either give the command an early return " +
                "in OrderApplier (it is consumed at apply time) or declare it persisting AND give it explicit arms in " +
                "CombatSystem's command switches — otherwise it inherits idle auto-combat exactly as Build did (DW-206).");

            // The other direction, allowlisted: a declared-persisting command the wire never stores would mean the
            // declaration is aspirational. Empty allowlist today.
            var declaredNotObserved = DeclaredPersisting()
                .Where(c => !observed.Contains(c) && !DeclaredPersistingButNotWireObservable.Contains(c))
                .ToArray();
            Assert.True(declaredNotObserved.Length == 0,
                "DW-645: these commands are declared persisting but a real OrderApplier.Apply did not leave them in " +
                "CommandState: " + string.Join(", ", declaredNotObserved.Select(c => c.ToString())) + ". If the " +
                "command is written only by an internal system, add it to DeclaredPersistingButNotWireObservable with " +
                "a justification; otherwise the declaration is wrong.");
        }

        /// <summary>
        /// Drive EVERY <see cref="UnitCommand"/> through the real <see cref="OrderApplier.Apply"/> on a live, owned
        /// entity with every optional system handle null (the golden/headless wiring), and report the commands that
        /// are still the entity's <c>CommandState</c> afterwards. That post-state IS the definition of "persists":
        /// <c>ApplyActiveOrder</c> blind-stores the command before its switch, so a command persists unless something
        /// returns before that store (a building/item/faction order) or rewrites it afterwards (PatrolAppend → Patrol,
        /// CastAbility → the prior order).
        /// </summary>
        private static UnitCommand[] ObserveWireStoredCommands()
        {
            var stored = new List<UnitCommand>();
            foreach (UnitCommand cmd in AllCommands())
            {
                var w = new EntityWorld();
                int u = w.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero),
                                 Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));

                // Seed a state that is never equal to the command under test, so "unchanged" can never be misread as
                // "stored" (Idle would otherwise self-satisfy — it is Create()'s default).
                w.CommandState[u] = cmd == UnitCommand.HoldPosition ? UnitCommand.Stop : UnitCommand.HoldPosition;

                // TargetX/TargetZ carry a ground point for the movement orders and a raw packed id/index for the
                // rest; the persistence question is decided before any of them is interpreted, so one encoding
                // (raw 0) probes every command identically.
                OrderApplier.Apply(w, new UnitOrder(u, cmd, Fixed.FromRaw(0), Fixed.FromRaw(0)), Faction.Player1);

                if (w.CommandState[u] == cmd) stored.Add(cmd);
            }
            return stored.ToArray();
        }

        // ── Layer 4 — every persisting command owns an explicit arm in BOTH switches ──────────────────────

        [Fact]
        public void EveryPersistingCommand_HasItsOwnArmInTheCombatantCommandSwitch()
        {
            string body = StrippedSwitchBody("combatant command switch");
            string[] missing = MissingArms(DeclaredPersisting(), body);

            Assert.True(missing.Length == 0,
                "DW-645: these PERSISTING commands have no case label in CombatSystem.Tick's command switch and so " +
                "reach `default:` → TickIdleCombat — they auto-acquire, chase, and have their MoveTarget overwritten " +
                "every tick, which is the DW-206 defect verbatim: " + string.Join(", ", missing) + ". Add an explicit " +
                "arm for each between the DW-645 guard anchors.");
        }

        [Fact]
        public void EveryPersistingCommand_HasItsOwnArmInTheNonCombatantRouter()
        {
            string body = StrippedSwitchBody("non-combatant command switch");
            string[] missing = MissingArms(DeclaredPersisting(), body);

            Assert.True(missing.Length == 0,
                "DW-645: these PERSISTING commands have no case label in CombatSystem.TickNonCombatant. That router " +
                "has no `default:` at all, so an un-cased command is silently INERT for a zero-damage unit — the " +
                "DW-242 half of the same class (an order no system can advance, complete, or normalize): " +
                string.Join(", ", missing) + ". Route it, or add it to the explicit no-op case group with a reason.");
        }

        [Fact]
        public void NeitherSwitch_CasesACommandThatCannotPersist()
        {
            // The mirror-image rot: an arm for a command that is never stored is dead code that reads like live
            // routing (and quietly documents a contract that is not true).
            foreach (string label in new[] { "combatant command switch", "non-combatant command switch" })
            {
                string[] dead = ArmsIn(StrippedSwitchBody(label))
                    .Where(c => !UnitCommandTraits.PersistsAsCommandState(c))
                    .Select(c => c.ToString())
                    .ToArray();

                Assert.True(dead.Length == 0,
                    $"DW-645: CombatSystem's {label} has case labels for commands that never persist as a " +
                    $"CommandState, so those arms are unreachable: {string.Join(", ", dead)}.");
            }
        }

        [Fact]
        public void TheCombatantSwitch_StillKeepsADefaultArm_ForCorruptOrNonPersistingBytes()
        {
            // The `default:` is deliberately KEPT (a corrupt save restores CommandState through a raw int cast, and
            // resting that unit beats throwing inside the tick). This pins that decision so a future "make it total"
            // edit is conscious: what DW-645 forbids is a PERSISTING command relying on it, not its existence.
            Assert.Contains("default:", StrippedSwitchBody("combatant command switch"), StringComparison.Ordinal);
        }

        // ── Non-vacuity — the extractor must actually catch the DW-206 state ──────────────────────────────

        [Fact]
        public void TheArmExtractor_ReportsTheMissingArm_WhenBuildIsRemoved_ThePreDw206State()
        {
            // Reconstruct the exact pre-DW-206 source shape from the LIVE switch (not a hand-written fixture, which
            // would rot) and prove the guard names Build. Without this, a broken anchor or regex would leave every
            // assertion above green while checking nothing.
            string real = StrippedSwitchBody("combatant command switch");
            Assert.Contains("case UnitCommand.Build:", real, StringComparison.Ordinal);

            string doctored = real.Replace("case UnitCommand.Build:", "", StringComparison.Ordinal);

            Assert.Equal(new[] { nameof(UnitCommand.Build) }, MissingArms(DeclaredPersisting(), doctored));
        }

        [Fact]
        public void TheGuardAnchors_ArePresentInTheShippingSource()
        {
            // A deleted anchor must fail loudly rather than silently disabling the guard: StrippedSwitchBody throws.
            Assert.NotEmpty(StrippedSwitchBody("combatant command switch"));
            Assert.NotEmpty(StrippedSwitchBody("non-combatant command switch"));
        }

        // ── Behaviour — what an inherited `default:` would actually do to the unit ────────────────────────

        [Theory]
        [InlineData(UnitCommand.Move)]
        [InlineData(UnitCommand.Build)]
        [InlineData(UnitCommand.PickupItem)]
        public void APureNavigationCommand_NeverInheritsIdlesGlobalChase(UnitCommand cmd)
        {
            var (w, combat) = NewSim();
            int u = Combatant(w, V(0, 0), Faction.Player1);
            Combatant(w, V(20, 0), Faction.Player2);   // far outside attack range — only a global chase reaches it
            w.CommandState[u] = cmd;
            w.MoveTarget[u]   = V(5, 5);               // the navigation goal its owning system is steering toward

            combat.Tick(w, Dt);

            AssertVec(V(5, 5), w.MoveTarget[u]);
            Assert.Equal(-1, w.AttackTarget[u]);
            Assert.True((w.Flags[u] & EntityFlags.Attacking) == 0,
                $"{cmd} is pure navigation: no acquisition, no chase, no swing. If this fails, its arm in " +
                $"CombatSystem's command switch was removed and it is inheriting TickIdleCombat (DW-206/DW-645).");
        }

        [Fact]
        public void Idle_DoesChaseADistantEnemy_TheBehaviourAnUnarmedCommandWouldInherit()
        {
            // The positive control for the theory above: proves the scenario really does produce a visible chase, so
            // those assertions are testing the arm and not an inert setup.
            var (w, combat) = NewSim();
            int u = Combatant(w, V(0, 0), Faction.Player1);
            Combatant(w, V(20, 0), Faction.Player2);
            w.CommandState[u] = UnitCommand.Idle;
            w.MoveTarget[u]   = V(5, 5);

            combat.Tick(w, Dt);

            AssertVec(V(20, 0), w.MoveTarget[u]);
            Assert.True((w.Flags[u] & EntityFlags.Moving) != 0);
        }

        // ── sim helpers (mirroring NonCombatantCommandGateTests) ──────────────────────────────────────────

        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        private static (EntityWorld world, CombatSystem combat) NewSim()
            => (new EntityWorld(), new CombatSystem(new ProjectileStore()));

        /// <summary>A damage-bearing, non-gathering melee unit — the exact shape DW-206 said would misbehave.</summary>
        private static int Combatant(EntityWorld w, FixedVec3 pos, Faction f)
        {
            int id = w.Create(pos, f, Fixed.FromInt(100), Fixed.FromInt(3));
            w.EffectiveAttackDamage[id] = Fixed.FromInt(10);
            w.AttackRange[id]  = Fixed.FromInt(2);
            w.AttackSpeed[id]  = Fixed.Zero;
            w.Delivery[id]     = AttackDelivery.Hitscan;
            w.DamageTypeOf[id] = DamageType.Normal;
            w.ArmorTypeOf[id]  = ArmorType.Unarmored;
            return id;
        }

        private static void AssertVec(FixedVec3 expected, FixedVec3 actual)
        {
            Assert.Equal(expected.X.Raw, actual.X.Raw);
            Assert.Equal(expected.Z.Raw, actual.Z.Raw);
        }

        // ── source scanning ───────────────────────────────────────────────────────────────────────────────

        private static readonly Regex CaseLabel = new(
            @"case\s+UnitCommand\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*:", RegexOptions.Compiled);

        /// <summary>The <see cref="UnitCommand"/> members carrying a case label in a stripped switch body.</summary>
        private static UnitCommand[] ArmsIn(string strippedBody) =>
            CaseLabel.Matches(strippedBody)
                .Select(m => Enum.Parse<UnitCommand>(m.Groups[1].Value))
                .Distinct()
                .OrderBy(c => (byte)c)
                .ToArray();

        /// <summary>The required commands with no case label in <paramref name="strippedBody"/>, ascending.</summary>
        private static string[] MissingArms(IEnumerable<UnitCommand> required, string strippedBody)
        {
            var present = ArmsIn(strippedBody);
            return required.Where(c => !present.Contains(c)).OrderBy(c => (byte)c).Select(c => c.ToString()).ToArray();
        }

        /// <summary>
        /// The text between the DW-645 BEGIN/END guard anchors named <paramref name="label"/> in the shipping
        /// <c>CombatSystem.cs</c>, with comments and string literals blanked so neither this codebase's heavy prose
        /// nor an assertion message can masquerade as a case label.
        /// </summary>
        private static string StrippedSwitchBody(string label)
        {
            string source = File.ReadAllText(Path.Combine(SrcRoot(), "Combat", "CombatSystem.cs"));
            string begin  = "DW-645 GUARD ANCHOR: BEGIN " + label;
            string end    = "DW-645 GUARD ANCHOR: END " + label;

            int b = source.IndexOf(begin, StringComparison.Ordinal);
            int e = source.IndexOf(end,   StringComparison.Ordinal);
            if (b < 0 || e < 0 || e <= b)
                throw new InvalidOperationException(
                    $"DW-645 completeness guard could not find the '{label}' anchors in CombatSystem.cs " +
                    $"(begin@{b}, end@{e}). The anchors delimit the switch this guard reads; if the switch moved or " +
                    $"was renamed, move the anchors with it — do NOT delete them, that silently disables the guard.");

            return StripCommentsAndLiterals(source.Substring(b, e - b));
        }

        /// <summary>Blank out block comments, line comments and string/char literals (the PositionWriterGuardTests recipe).</summary>
        private static string StripCommentsAndLiterals(string text)
        {
            text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            text = Regex.Replace(text, @"//[^\n]*", " ");
            text = Regex.Replace(text, @"@""(?:[^""]|"""")*""", @""" """);
            text = Regex.Replace(text, @"""(?:\\.|[^""\\\n])*""", @""" """);
            text = Regex.Replace(text, @"'(?:\\.|[^'\\\n])'", "' '");
            return text;
        }

        /// <summary>godot/src — two directories up from this file (…/ProjectChimera.Sim.Tests/Combat/), then into src.</summary>
        private static string SrcRoot([CallerFilePath] string thisFilePath = "")
        {
            string dir = Path.GetDirectoryName(thisFilePath)
                         ?? throw new InvalidOperationException("Could not resolve this test's source directory via [CallerFilePath].");
            string root = Path.GetFullPath(Path.Combine(dir, "..", "..", "src"));
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException(
                    $"DW-645 completeness guard could not locate the shipping source tree. Resolved path: '{root}'.");
            return root;
        }
    }
}
