#nullable enable
using System.Collections.Generic;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Persistence;
using ProjectChimera.Core.Sim;
using ProjectChimera.Core.Stats;
using ProjectChimera.Economy;
using ProjectChimera.Effects;
using ProjectChimera.Sim.Tests.Golden;   // GoldenApplierScenario (the trigger-less applied fixture)
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// Story 15-24d — veterancy: plain units earn rank growth from kills. Covers EVERY row of the story's
    /// I/O &amp; edge-case matrix plus the four extra proofs the spec names:
    /// <list type="bullet">
    ///   <item><description><b>The opt-in gates the INCREMENT</b>, not merely the checksum fold — a unit with no
    ///   authored <c>veterancy</c> block never leaves 0, which is what keeps every recorded golden
    ///   byte-identical (the frozen v22 differential control is the standing proof; this file pins the
    ///   mechanism).</description></item>
    ///   <item><description>credit follows <c>ResolveKillerPayload</c>'s packed-ref rule verbatim: a killer that
    ///   DIED this tick keeps its credit, a RECYCLED slot credits nothing, an attacker-less death credits
    ///   nothing, and friendly fire / self-kills credit nothing;</description></item>
    ///   <item><description>the installed vector is the CURRENT rank's cumulative total — never the sum of all
    ///   ranks — is not re-applied on a tick where the rank did not change, and does NOT heal on rank-up (the
    ///   DW-85 teeth-test);</description></item>
    ///   <item><description>the counter round-trips a save, the rank RE-DERIVES on load, and the minted
    ///   descriptor rides DW-997's by-value <c>KindMintedModifier</c>;</description></item>
    ///   <item><description>two identical runs are byte-identical over the folded <c>SimChecksum</c>.</description></item>
    /// </list>
    /// Godot-free, <see cref="Fixed"/>-only, Tier-1.
    /// </summary>
    public class VeterancyTests
    {
        // ══════════════════════════ Fixtures ═══════════════════════════════════════════════════════════════

        /// <summary>A two-rank ladder: 3 kills → +40 max health, 6 kills → +100 max health AND +5 attack damage.
        /// CUMULATIVE, so a rank-2 unit carries exactly the rank-2 numbers, never rank-1 + rank-2.</summary>
        private static VeterancyDefinition TwoRankLadder() => new VeterancyDefinition
        {
            Ranks = new List<VeterancyRank>
            {
                new VeterancyRank { Kills = 3, StatDeltas = new Dictionary<string, float> { ["max_health"] = 40f } },
                new VeterancyRank { Kills = 6, StatDeltas = new Dictionary<string, float> { ["max_health"] = 100f, ["attack_damage"] = 5f } },
            },
        };

        private static UnitDefinition VeteranDef(VeterancyDefinition? ladder = null) => new UnitDefinition
        {
            Id = "veteran", DisplayName = "Veteran", Category = "Melee", Hp = 100f, Speed = 4f, AttackDamage = 10f,
            Veterancy = ladder ?? TwoRankLadder(),
        };

        private static UnitDefinition PlainDef() => new UnitDefinition
        {
            Id = "plain", DisplayName = "Plain", Category = "Melee", Hp = 100f, Speed = 4f, AttackDamage = 10f,
        };

        private static SimulationHost Host()
        {
            var host = SimulationHost.Create(
                NullLogSink.Instance, new FactionRegistry(2), new FactionDefinition(), new FactionDefinition());
            host.ChecksumInterval = 1;
            return host;
        }

        /// <summary>Spawn a def-based unit through the SINGLE mapper (never a hand-copied spawn path).</summary>
        private static int Spawn(EntityWorld w, UnitDefinition def, Faction faction, int x = 0)
        {
            int id = w.Create(new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.Zero), faction,
                              Fixed.FromFloat(def.Hp), Fixed.FromFloat(def.Speed));
            w.ApplyUnitDefinition(id, def);
            return id;
        }

        /// <summary>Run the veterancy runtime alone (no other system can perturb the assertion).</summary>
        private static void TickVeterancy(SimulationHost host)
            => new VeterancySystem(host.Modifiers).Tick(host.World, SimulationLoop.FixedDt);

        /// <summary>Kill <paramref name="victim"/> through the single death choke point, crediting <paramref name="killerId"/>.</summary>
        private static void Kill(EntityWorld w, int victim, int killerId)
        {
            w.Health[victim] = Fixed.Zero;
            Faction killerFaction = killerId >= 0 ? w.FactionOf[killerId] : Faction.Neutral;
            DamageResolver.KillEntity(w, victim, killerFaction, null, null, null, attackerId: killerId);
        }

        /// <summary>The vector currently installed on a host's veterancy slot (empty when none is).</summary>
        private static StatDelta[] Installed(SimulationHost host, int id)
        {
            int n = host.Modifiers.CountAt(id);
            for (int s = 0; s < n; s++)
                if (host.Modifiers.ModifierIdAt(id, s) == VeterancySystem.VeterancyModifierId)
                    return host.Modifiers.ModifierRefAt(id, s)!.StatDeltas;
            return StatVocabulary.EmptyDeltas;
        }

        private static Modifier? InstalledRef(SimulationHost host, int id)
        {
            int n = host.Modifiers.CountAt(id);
            for (int s = 0; s < n; s++)
                if (host.Modifiers.ModifierIdAt(id, s) == VeterancySystem.VeterancyModifierId)
                    return host.Modifiers.ModifierRefAt(id, s);
            return null;
        }

        // ══════════════════════════ Matrix row 1 — opted-in killer, hostile kill ═══════════════════════════

        [Fact]
        public void OptedInKiller_HostileKill_IncrementsTheCounter()
        {
            var host = Host();
            EntityWorld w = host.World;
            int killer = Spawn(w, VeteranDef(), Faction.Player1);

            for (int k = 1; k <= 3; k++)
            {
                int victim = Spawn(w, PlainDef(), Faction.Player2, x: 5);
                Kill(w, victim, killer);
                Assert.Equal(k, w.VeterancyKills[killer]);
            }
        }

        [Fact]
        public void CrossingAThreshold_InstallsThatRanksCumulativeVector_NeverTheSumOfRanks()
        {
            var host = Host();
            EntityWorld w = host.World;
            int killer = Spawn(w, VeteranDef(), Faction.Player1);

            // Below the first threshold: nothing installed at all (no empty slot burned — the DW-678 rule).
            w.VeterancyKills[killer] = 2;
            TickVeterancy(host);
            Assert.Empty(Installed(host, killer));
            Assert.Equal(Fixed.FromInt(100), w.EffectiveMaxHealth[killer]);

            // Rank 1.
            w.VeterancyKills[killer] = 3;
            TickVeterancy(host);
            Assert.Equal(Fixed.FromInt(40), StatVocabulary.DeltaOf(Installed(host, killer), StatId.MaxHealth));
            Assert.Equal(Fixed.FromInt(140), w.EffectiveMaxHealth[killer]);

            // Rank 2 REPLACES rank 1 (cumulative), so max health is 100 + 100, NOT 100 + 40 + 100.
            w.VeterancyKills[killer] = 6;
            TickVeterancy(host);
            StatDelta[] v = Installed(host, killer);
            Assert.Equal(2, v.Length);
            Assert.Equal(Fixed.FromInt(100), StatVocabulary.DeltaOf(v, StatId.MaxHealth));
            Assert.Equal(Fixed.FromInt(5), StatVocabulary.DeltaOf(v, StatId.AttackDamage));
            Assert.Equal(Fixed.FromInt(200), w.EffectiveMaxHealth[killer]);
            Assert.Equal(Fixed.FromInt(15), w.EffectiveAttackDamage[killer]);
        }

        [Fact]
        public void RankUnchangedTick_DoesNotReapplyTheModifier()
        {
            var host = Host();
            EntityWorld w = host.World;
            int killer = Spawn(w, VeteranDef(), Faction.Player1);
            w.VeterancyKills[killer] = 4;
            TickVeterancy(host);

            // Reference identity is the teeth: a swap REMOVES then applies a FRESH descriptor instance, so the
            // stored reference would change. (StackRule.Ignore alone would not prove it — an Ignore re-apply is a
            // no-op against a live instance, so only the remove-then-apply is observable.)
            Modifier? first = InstalledRef(host, killer);
            Assert.NotNull(first);
            for (int t = 0; t < 5; t++) TickVeterancy(host);
            Assert.Same(first, InstalledRef(host, killer));
        }

        // ══════════════════════════ Matrix row 2 — a non-opted unit never increments ═══════════════════════

        [Fact]
        public void NonOptedKiller_NeverIncrements_AndFoldsNothing()
        {
            var host = Host();
            EntityWorld w = host.World;
            int killer = Spawn(w, PlainDef(), Faction.Player1);       // no veterancy block
            int bystander = Spawn(w, PlainDef(), Faction.Player1, x: 9);

            uint before = SimChecksum.Compute(w, host.Buildings, host.Resources, new FactionRegistry(2), host.Modifiers);

            for (int k = 0; k < 5; k++)
            {
                int victim = Spawn(w, PlainDef(), Faction.Player2, x: 5);
                Kill(w, victim, killer);
            }
            Assert.Equal(0, w.VeterancyKills[killer]);
            TickVeterancy(host);
            Assert.Empty(Installed(host, killer));

            // Byte-identity of the FOLD is the story's golden-neutrality claim in miniature, and the comparison is
            // deliberately ASYMMETRIC: world 1 ran five full spawn-and-kill cycles, world 2 ran none. Their two
            // survivors must still hash identically to the pre-kill baseline, because the victims are destroyed
            // (the entity loop skips dead slots) and the ONE piece of state a kill could have added — the killer's
            // VeterancyKills — never left 0 for a non-opted def, so the bounded v28 arm folds nothing.
            var host2 = Host();
            EntityWorld w2 = host2.World;
            int killer2 = Spawn(w2, PlainDef(), Faction.Player1);
            int bystander2 = Spawn(w2, PlainDef(), Faction.Player1, x: 9);
            Assert.Equal(bystander, bystander2);
            Assert.Equal(killer, killer2);
            uint after2 = SimChecksum.Compute(w2, host2.Buildings, host2.Resources, new FactionRegistry(2), host2.Modifiers);
            Assert.Equal(before, after2); // the pre-kill baseline, reproduced — the v28 arm contributes nothing at 0
        }

        [Fact]
        public void CounterAtZero_FoldsNothing_ButANonZeroCounterMoves_TheChecksum()
        {
            // The bound's two halves, pinned together: at 0 the arm is silent (goldens cannot move), and the moment
            // a real veteran exists it is genuinely folded (no silent desync surface).
            var host = Host();
            EntityWorld w = host.World;
            int u = Spawn(w, VeteranDef(), Faction.Player1);
            var reg = new FactionRegistry(2);

            uint atZero = SimChecksum.Compute(w, host.Buildings, host.Resources, reg, host.Modifiers);
            w.VeterancyKills[u] = 1;
            uint atOne = SimChecksum.Compute(w, host.Buildings, host.Resources, reg, host.Modifiers);
            Assert.NotEqual(atZero, atOne);

            w.VeterancyKills[u] = 0;
            Assert.Equal(atZero, SimChecksum.Compute(w, host.Buildings, host.Resources, reg, host.Modifiers));
        }

        // ══════════════════════════ Matrix rows 3-6 — the credit gates ═════════════════════════════════════

        [Fact]
        public void KillerDiedInTheSameTick_KeepsItsCredit()
        {
            var host = Host();
            EntityWorld w = host.World;
            int killer = Spawn(w, VeteranDef(), Faction.Player1);
            int victim = Spawn(w, PlainDef(), Faction.Player2, x: 5);

            // The killer dies FIRST (its slot is a corpse but not yet recycled), then its own killing blow resolves —
            // the ResolveKillerPayload rule: a dead killer keeps credit.
            w.Health[killer] = Fixed.Zero;
            w.Destroy(killer);
            Assert.False(w.IsAlive(killer));

            Kill(w, victim, killer);
            Assert.Equal(1, w.VeterancyKills[killer]);
        }

        [Fact]
        public void RecycledKillerSlot_CreditsNothing_NeverTheNewOccupant()
        {
            // Driven through the REAL production carrier of a stale killer ref: an in-flight projectile, whose
            // ProjectileStore.SourceId is a PACKED ref resolved at impact with the attribution rule. Any other way
            // of reaching KillEntity hands it a raw, live attacker id, so this is the only path on which a
            // generation mismatch can actually occur.
            var host = Host();
            EntityWorld w = host.World;
            int shooter = Spawn(w, VeteranDef(), Faction.Player1, x: 20);
            int packedOld = w.PackRef(shooter);
            int victim = Spawn(w, PlainDef(), Faction.Player2, x: 0);

            var store = new ProjectileStore();
            var projectiles = new ProjectileSystem(store);
            store.Spawn(w.Position[victim], victim, w.Position[victim],
                        Fixed.FromInt(1000), DamageType.Normal, ArmorType.Unarmored, Faction.Player1,
                        Fixed.FromInt(18), sourceId: packedOld);

            // The shooter dies and its slot is RECYCLED by a new (also opted-in) unit before the shell lands.
            w.Destroy(shooter);
            int newOccupant = Spawn(w, VeteranDef(), Faction.Player1, x: 20);
            Assert.Equal(shooter, newOccupant);
            Assert.NotEqual(packedOld, w.PackRef(newOccupant));

            projectiles.Tick(w, SimulationLoop.FixedDt);

            Assert.False(w.IsAlive(victim));
            Assert.Equal(-1, w.KillerOf[victim]);           // the attribution rule already degraded the ref
            Assert.Equal(0, w.VeterancyKills[newOccupant]);  // …so the new occupant is credited nothing, silently
        }

        [Fact]
        public void AttackerLessDeath_CreditsNothing()
        {
            var host = Host();
            EntityWorld w = host.World;
            int veteran = Spawn(w, VeteranDef(), Faction.Player1);
            int victim = Spawn(w, PlainDef(), Faction.Player2, x: 5);

            // The ModifierStore ceiling-collapse form: Neutral, attacker -1.
            w.Health[victim] = Fixed.Zero;
            DamageResolver.KillEntity(w, victim, Faction.Neutral, null, null, null);
            Assert.Equal(0, w.VeterancyKills[veteran]);
        }

        [Fact]
        public void FriendlyFire_AndSelfKill_CreditNothing()
        {
            var host = Host();
            EntityWorld w = host.World;
            int killer = Spawn(w, VeteranDef(), Faction.Player1);
            int ally = Spawn(w, PlainDef(), Faction.Player1, x: 5);

            Kill(w, ally, killer);
            Assert.Equal(0, w.VeterancyKills[killer]);   // same faction ⇒ no credit

            int selfLethal = Spawn(w, VeteranDef(), Faction.Player2, x: 7);
            Kill(w, selfLethal, selfLethal);
            Assert.Equal(0, w.VeterancyKills[selfLethal]); // killer == victim ⇒ no credit
        }

        [Fact]
        public void NeutralVictim_StillCredits_ABecauseTheFactionsDiffer()
        {
            var host = Host();
            EntityWorld w = host.World;
            int killer = Spawn(w, VeteranDef(), Faction.Player1);
            int creep = Spawn(w, PlainDef(), Faction.Neutral, x: 5);

            Kill(w, creep, killer);
            Assert.Equal(1, w.VeterancyKills[killer]);
        }

        // ══════════════════════════ Matrix rows 7-8 — the authoring gate (ALL located errors) ══════════════

        private static IReadOnlyList<(string FieldPath, string Message)> Validate(UnitDefinition def)
            => new UnitDefinitionValidator().Validate(def, null, null).Errors;

        [Fact]
        public void RanksBelowOne_OrNotStrictlyAscending_AreRejected_AllErrorsAtOnce()
        {
            var def = VeteranDef(new VeterancyDefinition
            {
                Ranks = new List<VeterancyRank>
                {
                    new VeterancyRank { Kills = 0 },   // below the >= 1 floor
                    new VeterancyRank { Kills = 5 },
                    new VeterancyRank { Kills = 5 },   // not strictly ascending
                    new VeterancyRank { Kills = 2 },   // descending
                },
            });

            var errors = Validate(def);
            Assert.Contains(errors, e => e.FieldPath == "veterancy.ranks[0].kills");
            Assert.Contains(errors, e => e.FieldPath == "veterancy.ranks[2].kills");
            Assert.Contains(errors, e => e.FieldPath == "veterancy.ranks[3].kills");
            // D-9: every offending field badges at once — not just the first.
            Assert.Equal(3, CountWithPrefix(errors, "veterancy.ranks["));
        }

        [Fact]
        public void EmptyOrOverlongRankLadder_IsRejected()
        {
            var empty = VeteranDef(new VeterancyDefinition { Ranks = new List<VeterancyRank>() });
            Assert.Contains(Validate(empty), e => e.FieldPath == "veterancy.ranks");

            var missing = VeteranDef(new VeterancyDefinition());   // ranks omitted entirely
            Assert.Contains(Validate(missing), e => e.FieldPath == "veterancy.ranks");

            var tooMany = new VeterancyDefinition { Ranks = new List<VeterancyRank>() };
            for (int i = 1; i <= 9; i++) tooMany.Ranks!.Add(new VeterancyRank { Kills = i });
            Assert.Contains(Validate(VeteranDef(tooMany)), e => e.FieldPath == "veterancy.ranks");
        }

        [Fact]
        public void UnknownOrNonAuthorableStat_IsRejected_OneErrorPerBadKey()
        {
            var def = VeteranDef(new VeterancyDefinition
            {
                Ranks = new List<VeterancyRank>
                {
                    new VeterancyRank
                    {
                        Kills = 1,
                        StatDeltas = new Dictionary<string, float>
                        {
                            ["not_a_stat"]  = 1f,      // outside the closed vocabulary
                            ["max_energy"]  = 5f,      // declared but NOT ModifierAuthorable (the 15.12 energy pair)
                            ["max_health"]  = 10f,     // fine — must NOT produce an error
                        },
                    },
                },
            });

            var errors = Validate(def);
            Assert.Contains(errors, e => e.FieldPath == "veterancy.ranks[0].stat_deltas.not_a_stat");
            Assert.Contains(errors, e => e.FieldPath == "veterancy.ranks[0].stat_deltas.max_energy");
            Assert.DoesNotContain(errors, e => e.FieldPath == "veterancy.ranks[0].stat_deltas.max_health");
            Assert.Equal(2, CountWithPrefix(errors, "veterancy.ranks[0].stat_deltas."));
        }

        [Fact]
        public void OverBoundOrNonFiniteDelta_IsRejected()
        {
            var def = VeteranDef(new VeterancyDefinition
            {
                Ranks = new List<VeterancyRank>
                {
                    new VeterancyRank
                    {
                        Kills = 1,
                        StatDeltas = new Dictionary<string, float>
                        {
                            // Far past DW-488's MaxStatDeltaTotalRaw (~4096 stat units).
                            ["max_health"]   = 100000f,
                            ["attack_speed"] = float.NaN,
                        },
                    },
                },
            });

            var errors = Validate(def);
            Assert.Contains(errors, e => e.FieldPath == "veterancy.ranks[0].stat_deltas.max_health");
            Assert.Contains(errors, e => e.FieldPath == "veterancy.ranks[0].stat_deltas.attack_speed");
        }

        [Fact]
        public void AWellFormedLadder_Validates_AndAUnitWithNoBlockIsUntouched()
        {
            Assert.Empty(Validate(VeteranDef()));
            Assert.Empty(Validate(PlainDef()));
        }

        [Fact]
        public void ZeroValuedNonAuthorableStat_IsStillRejected()
        {
            // The gate must NOT be delegated to Modifier.CheckAuthoringBounds: a delta that quantizes to 0
            // canonicalizes to the EMPTY vector, so the probe's bounds walk visits no entry and its own
            // ModifierAuthorable arm is never reached. Delegating admitted `"max_energy": 0` silently, which
            // contradicts the I/O matrix row "unknown or non-authorable stat id -> fail-closed".
            var def = VeteranDef(new VeterancyDefinition
            {
                Ranks = new List<VeterancyRank>
                {
                    new VeterancyRank
                    {
                        Kills = 1,
                        StatDeltas = new Dictionary<string, float> { ["max_energy"] = 0f },
                    },
                },
            });

            var errors = Validate(def);
            Assert.Contains(errors, e => e.FieldPath == "veterancy.ranks[0].stat_deltas.max_energy");
        }

        [Fact]
        public void ZeroValuedAuthorableStat_IsFine()
        {
            // The complement, so the rule above cannot be "reject every zero": an authorable stat at 0 is inert,
            // not invalid (the rank simply installs nothing - the DW-678 rule).
            var def = VeteranDef(new VeterancyDefinition
            {
                Ranks = new List<VeterancyRank>
                {
                    new VeterancyRank
                    {
                        Kills = 1,
                        StatDeltas = new Dictionary<string, float> { ["max_health"] = 0f },
                    },
                },
            });

            Assert.Empty(Validate(def));
        }

        [Fact]
        public void VeterancyOnABuilding_IsRejected()
        {
            // BuildingDefinition INHERITS the block from UnitDefinition and ContentHash folds it through the shared
            // FoldUnitCommon arm, so without a gate a creator could author, validate, hash and SAVE a building
            // ladder that can never run: VeterancySystem sweeps EntityWorld, buildings live in BuildingStore.
            var building = new BuildingDefinition
            {
                Id = "barracks", DisplayName = "Barracks", Category = "Structure", Hp = 800f,
                ConstructionTime = 30f, ProducesCategory = "Melee",
                Veterancy = TwoRankLadder(),
            };

            var errors = new UnitDefinitionValidator()
                .Validate(building, null, null, null, null, kind: "building").Errors;
            Assert.Contains(errors, e => e.FieldPath == "veterancy");
            Assert.Contains(errors, e => e.Message.Contains("veterancy is a UNIT progression"));

            // …and the same building with no ladder raises no veterancy error at all.
            var clean = new BuildingDefinition
            {
                Id = "barracks", DisplayName = "Barracks", Category = "Structure", Hp = 800f,
                ConstructionTime = 30f, ProducesCategory = "Melee",
            };
            Assert.DoesNotContain(new UnitDefinitionValidator().Validate(clean, null, null, null, null, kind: "building").Errors,
                                  e => e.FieldPath == "veterancy");
        }

        private static int CountWithPrefix(IReadOnlyList<(string FieldPath, string Message)> errors, string prefix)
        {
            int n = 0;
            for (int i = 0; i < errors.Count; i++)
                if (errors[i].FieldPath.StartsWith(prefix, System.StringComparison.Ordinal)) n++;
            return n;
        }

        // ══════════════════════════ Matrix row 9 — an all-zero rank installs nothing ═══════════════════════

        [Fact]
        public void RankThatCanonicalizesToEmpty_InstallsNothing_AndRemovesAPriorSlot()
        {
            var host = Host();
            EntityWorld w = host.World;
            var ladder = new VeterancyDefinition
            {
                Ranks = new List<VeterancyRank>
                {
                    new VeterancyRank { Kills = 1, StatDeltas = new Dictionary<string, float> { ["max_health"] = 40f } },
                    new VeterancyRank { Kills = 2, StatDeltas = new Dictionary<string, float> { ["max_health"] = 0f } },
                },
            };
            int u = Spawn(w, VeteranDef(ladder), Faction.Player1);

            w.VeterancyKills[u] = 1;
            TickVeterancy(host);
            Assert.Single(Installed(host, u));
            int slotsAtRank1 = host.Modifiers.CountAt(u);

            // Rank 2's vector is all-zero ⇒ the slot is RELEASED, never re-installed inert (DW-678).
            w.VeterancyKills[u] = 2;
            TickVeterancy(host);
            Assert.Empty(Installed(host, u));
            Assert.Equal(slotsAtRank1 - 1, host.Modifiers.CountAt(u));
            Assert.Equal(Fixed.FromInt(100), w.EffectiveMaxHealth[u]);
        }

        // ===================== Orphaned-rank reconciliation (both Tick early-outs) =====================

        [Fact]
        public void CounterBackToZero_StripsAnOrphanedRankVector()
        {
            // Reachable across a save/load: the minted descriptor round-trips BY VALUE (DW-997's
            // KindMintedModifier) while the v13 lane FLOORS a tampered negative count to 0 - so a live rank vector
            // can arrive with a counter that says "no rank". A permanent StackRule.Ignore modifier expires on
            // nothing, so an early-out that merely continued would leave the stats applying forever.
            var host = Host();
            EntityWorld w = host.World;
            int u = Spawn(w, VeteranDef(), Faction.Player1);

            w.VeterancyKills[u] = 6;
            TickVeterancy(host);
            Assert.Equal(2, Installed(host, u).Length);
            Assert.Equal(Fixed.FromInt(200), w.EffectiveMaxHealth[u]);

            w.VeterancyKills[u] = 0;          // the restore floor's output
            TickVeterancy(host);

            Assert.Empty(Installed(host, u));
            Assert.Equal(Fixed.FromInt(100), w.EffectiveMaxHealth[u]);   // the ceiling really came back down
            Assert.Equal(Fixed.FromInt(10), w.EffectiveAttackDamage[u]); // ...and so did the damage
        }

        [Fact]
        public void LadderRemovedFromTheDef_StripsAnOrphanedRankVector()
        {
            // The second reachable shape: SaveGameState re-resolves SourceDefinition BY ID, so a `veterancy` block
            // edited out of the faction file between save and load restores a live rank vector with no ladder
            // behind it. The counter is untouched - only the def changed.
            var host = Host();
            EntityWorld w = host.World;
            UnitDefinition def = VeteranDef();
            int u = Spawn(w, def, Faction.Player1);

            w.VeterancyKills[u] = 6;
            TickVeterancy(host);
            Assert.Equal(2, Installed(host, u).Length);

            def.Veterancy = null;             // the ladder is gone from the def under a live unit
            TickVeterancy(host);

            Assert.Empty(Installed(host, u));
            Assert.Equal(6, w.VeterancyKills[u]);                        // the COUNTER is not the thing being repaired
            Assert.Equal(Fixed.FromInt(100), w.EffectiveMaxHealth[u]);
        }

        [Fact]
        public void StrippingAnOrphanedRank_DoesNotHeal_NorLeaveHealthAboveTheCeiling()
        {
            // The strip carries the same DW-85 discipline as the swap: snapshot, remove, re-clamp.
            var host = Host();
            EntityWorld w = host.World;
            int u = Spawn(w, VeteranDef(), Faction.Player1);

            w.VeterancyKills[u] = 6;
            TickVeterancy(host);
            w.Health[u] = w.EffectiveMaxHealth[u];   // 200 / 200 at rank 2

            w.VeterancyKills[u] = 0;
            TickVeterancy(host);

            Assert.Equal(Fixed.FromInt(100), w.EffectiveMaxHealth[u]);
            Assert.Equal(Fixed.FromInt(100), w.Health[u]);   // clamped into the lowered ceiling, never above it
        }

        [Fact]
        public void UnrankedUnits_AreUntouchedByTheOrphanSweep()
        {
            // The hot path: the strip must be a pure no-op for the entities every recorded golden is made of.
            var host = Host();
            EntityWorld w = host.World;
            int plain = Spawn(w, PlainDef(), Faction.Player1);
            int vetNoKills = Spawn(w, VeteranDef(), Faction.Player1, x: 5);

            int slotsBefore = host.Modifiers.CountAt(plain) + host.Modifiers.CountAt(vetNoKills);
            Fixed hpBefore = w.Health[plain];
            for (int t = 0; t < 5; t++) TickVeterancy(host);

            Assert.Equal(slotsBefore, host.Modifiers.CountAt(plain) + host.Modifiers.CountAt(vetNoKills));
            Assert.Equal(hpBefore.Raw, w.Health[plain].Raw);
            Assert.Empty(Installed(host, vetNoKills));
        }

        // ===================== The ring can REFUSE the install =====================

        /// <summary>Fill every one of a host's EffectCaps.MaxModifiersPerEntity ring slots with inert markers.</summary>
        private static void FillRing(SimulationHost host, int id)
        {
            for (int k = 0; k < EffectCaps.MaxModifiersPerEntity; k++)
            {
                var filler = new Modifier(0x7000_0000 + k, -1, StackRule.Ignore, 1,
                    StatVocabulary.Canonicalize(new List<StatDelta> { new StatDelta(StatId.Armor, Fixed.One) }),
                    StatusFlags.None, null, 0);
                Assert.True(host.Modifiers.Apply(id, filler, id, host.World.FactionOf[id]));
            }
            Assert.Equal(EffectCaps.MaxModifiersPerEntity, host.Modifiers.CountAt(id));
        }

        [Fact]
        public void RingFull_RefusesTheRankInstall_AndTalliesTheRefusal()
        {
            var host = Host();
            EntityWorld w = host.World;
            int u = Spawn(w, VeteranDef(), Faction.Player1);
            FillRing(host, u);

            Fixed hpBefore = w.Health[u];
            Fixed ceilingBefore = w.EffectiveMaxHealth[u];
            int storeRefusedBefore = host.Modifiers.RefusedInstallCount;

            // ONE system instance across the whole test: the tally is per-system, and a fresh instance per tick
            // would reset it (which is exactly what made an earlier version of this test vacuous).
            var sys = new VeterancySystem(host.Modifiers);
            w.VeterancyKills[u] = 6;
            sys.Tick(w, SimulationLoop.FixedDt);

            // The rank is NOT installed and the refusal is VISIBLE. Without honouring Apply's bool return the
            // outcome would be indistinguishable from an un-blooded unit: the ring stays full, the ceiling never
            // moves, and the sweep silently re-attempts forever.
            Assert.Empty(Installed(host, u));
            Assert.Equal(ceilingBefore.Raw, w.EffectiveMaxHealth[u].Raw);
            Assert.Equal(hpBefore.Raw, w.Health[u].Raw);   // no Health re-statement on a tick where the ring never moved
            Assert.Equal(1, sys.RefusedRankInstalls);
            Assert.True(host.Modifiers.RefusedInstallCount > storeRefusedBefore);

            // …and it keeps being counted for as long as the ring stays full (the retry is deliberate, not a leak).
            sys.Tick(w, SimulationLoop.FixedDt);
            sys.Tick(w, SimulationLoop.FixedDt);
            Assert.Equal(3, sys.RefusedRankInstalls);
        }

        [Fact]
        public void ASuccessfulRankInstall_TalliesNoRefusal()
        {
            // The complement, so the tally cannot be "always climbing".
            var host = Host();
            EntityWorld w = host.World;
            int u = Spawn(w, VeteranDef(), Faction.Player1);

            var sys = new VeterancySystem(host.Modifiers);
            w.VeterancyKills[u] = 6;
            sys.Tick(w, SimulationLoop.FixedDt);

            Assert.Equal(2, Installed(host, u).Length);
            Assert.Equal(0, sys.RefusedRankInstalls);
        }

        [Fact]
        public void RingFull_ThenFreed_InstallsTheRankOnTheNextTick()
        {
            // The retry is deliberate: a rank must land the moment an unrelated buff expires and frees a slot.
            var host = Host();
            EntityWorld w = host.World;
            int u = Spawn(w, VeteranDef(), Faction.Player1);
            FillRing(host, u);

            var sys = new VeterancySystem(host.Modifiers);
            w.VeterancyKills[u] = 6;
            sys.Tick(w, SimulationLoop.FixedDt);
            Assert.Empty(Installed(host, u));
            Assert.Equal(1, sys.RefusedRankInstalls);

            Assert.True(host.Modifiers.RemoveByModifierId(u, 0x7000_0000));   // a slot frees up
            sys.Tick(w, SimulationLoop.FixedDt);

            Assert.Equal(2, Installed(host, u).Length);
            Assert.Equal(Fixed.FromInt(200), w.EffectiveMaxHealth[u]);
            Assert.Equal(1, sys.RefusedRankInstalls);   // no further refusal once the slot exists
        }

        // ===================== Runtime fail-safes in QuantizeDeltas =====================

        // These matter because NO LOADER calls UnitDefinitionValidator - only the CreationSuite panels and the LLM
        // draft gate do - so raw-JSON faction content reaches the sim unvalidated. The two `continue`s in
        // QuantizeDeltas are therefore live runtime policy, not belt-and-braces behind a gate that always ran.

        [Fact]
        public void Runtime_DropsANonAuthorableStatKey_ItNeverReachesTheInstalledVector()
        {
            var host = Host();
            EntityWorld w = host.World;
            var ladder = new VeterancyDefinition
            {
                Ranks = new List<VeterancyRank>
                {
                    new VeterancyRank
                    {
                        Kills = 1,
                        StatDeltas = new Dictionary<string, float> { ["max_energy"] = 25f, ["max_health"] = 40f },
                    },
                },
            };
            int u = Spawn(w, VeteranDef(ladder), Faction.Player1);

            w.VeterancyKills[u] = 1;
            TickVeterancy(host);

            StatDelta[] v = Installed(host, u);
            Assert.Single(v);                                                     // ONLY max_health survived
            Assert.Equal(StatId.MaxHealth, v[0].Stat);
            Assert.Equal(Fixed.Zero.Raw, StatVocabulary.DeltaOf(v, StatId.MaxEnergy).Raw);
        }

        [Fact]
        public void Runtime_DropsANonFiniteDelta_ItNeverReachesTheInstalledVector()
        {
            var host = Host();
            EntityWorld w = host.World;
            var ladder = new VeterancyDefinition
            {
                Ranks = new List<VeterancyRank>
                {
                    new VeterancyRank
                    {
                        Kills = 1,
                        StatDeltas = new Dictionary<string, float>
                        {
                            ["attack_damage"] = float.PositiveInfinity,
                            ["max_health"]    = 40f,
                        },
                    },
                },
            };
            int u = Spawn(w, VeteranDef(ladder), Faction.Player1);
            Fixed damageBefore = w.EffectiveAttackDamage[u];

            w.VeterancyKills[u] = 1;
            TickVeterancy(host);

            StatDelta[] v = Installed(host, u);
            Assert.Single(v);
            Assert.Equal(StatId.MaxHealth, v[0].Stat);
            Assert.Equal(damageBefore.Raw, w.EffectiveAttackDamage[u].Raw);   // the infinity never reached the stat
        }

        [Fact]
        public void Runtime_DropsAnUnknownStatKey()
        {
            var host = Host();
            EntityWorld w = host.World;
            var ladder = new VeterancyDefinition
            {
                Ranks = new List<VeterancyRank>
                {
                    new VeterancyRank
                    {
                        Kills = 1,
                        StatDeltas = new Dictionary<string, float> { ["not_a_stat"] = 999f },
                    },
                },
            };
            int u = Spawn(w, VeteranDef(ladder), Faction.Player1);

            w.VeterancyKills[u] = 1;
            TickVeterancy(host);

            Assert.Empty(Installed(host, u));   // an all-unknown rank canonicalizes empty - nothing installed (DW-678)
        }

        // ══════════════════════════ Matrix row 11 — kills past the last threshold ══════════════════════════

        [Fact]
        public void KillsPastTheLastThreshold_KeepCounting_ButTheVectorStops()
        {
            var host = Host();
            EntityWorld w = host.World;
            int u = Spawn(w, VeteranDef(), Faction.Player1);

            w.VeterancyKills[u] = 6;
            TickVeterancy(host);
            Modifier? atMax = InstalledRef(host, u);
            Assert.NotNull(atMax);

            w.VeterancyKills[u] = 4000;
            TickVeterancy(host);
            Assert.Same(atMax, InstalledRef(host, u));                 // no swap: still the last rank's vector
            Assert.Equal(Fixed.FromInt(200), w.EffectiveMaxHealth[u]);
            Assert.Equal(4000, w.VeterancyKills[u]);                    // the counter itself keeps counting
        }

        // ══════════════════════════ The DW-85 teeth-test ═══════════════════════════════════════════════════

        [Fact]
        public void MaxHealthRankUp_DoesNotHeal_TheUnit()
        {
            var host = Host();
            EntityWorld w = host.World;
            int u = Spawn(w, VeteranDef(), Faction.Player1);
            w.Health[u] = Fixed.FromInt(60);   // wounded: 60 / 100

            w.VeterancyKills[u] = 3;
            TickVeterancy(host);
            Assert.Equal(Fixed.FromInt(140), w.EffectiveMaxHealth[u]);
            Assert.Equal(Fixed.FromInt(60), w.Health[u]);   // the ceiling rose; the unit did NOT heal

            w.VeterancyKills[u] = 6;
            TickVeterancy(host);
            Assert.Equal(Fixed.FromInt(200), w.EffectiveMaxHealth[u]);
            Assert.Equal(Fixed.FromInt(60), w.Health[u]);   // and the SWAP is not a free heal either
        }

        [Fact]
        public void FullHealthRankUp_KeepsItsHealth_TheRemoveClampDoesNotStealIt()
        {
            var host = Host();
            EntityWorld w = host.World;
            int u = Spawn(w, VeteranDef(), Faction.Player1);

            w.VeterancyKills[u] = 3;
            TickVeterancy(host);
            w.Health[u] = w.EffectiveMaxHealth[u];   // topped up at rank 1 (140/140)

            w.VeterancyKills[u] = 6;
            TickVeterancy(host);
            // Without the DW-85 snapshot the remove would clamp 140 down to 100 and the re-apply would then heal by
            // the realized ceiling change — landing somewhere else entirely. The re-statement keeps 140.
            Assert.Equal(Fixed.FromInt(200), w.EffectiveMaxHealth[u]);
            Assert.Equal(Fixed.FromInt(140), w.Health[u]);
        }

        // ══════════════════════════ Recycle + editor-undo residue ══════════════════════════════════════════

        [Fact]
        public void RecycledSlot_CarriesNoPriorVeterancy()
        {
            var host = Host();
            EntityWorld w = host.World;
            int u = Spawn(w, VeteranDef(), Faction.Player1);
            w.VeterancyKills[u] = 42;

            w.Destroy(u);
            int reused = Spawn(w, VeteranDef(), Faction.Player1);
            Assert.Equal(u, reused);
            Assert.Equal(0, w.VeterancyKills[reused]);   // the Create reset — the SoA-recycle trap, closed
        }

        [Fact]
        public void EditorDeleteThenUndo_PreservesTheEarnedRank()
        {
            var host = Host();
            EntityWorld w = host.World;
            int u = Spawn(w, VeteranDef(), Faction.Player1);
            w.VeterancyKills[u] = 7;

            UnitSnapshot snap = w.SnapshotUnit(u);
            w.Destroy(u);
            int restored = w.RestoreUnit(in snap);

            Assert.True(restored >= 0);
            Assert.Equal(7, w.VeterancyKills[restored]);   // the CLAUDE.md residue rule (hand-enumerated)
            TickVeterancy(host);
            // 7 kills is past the second threshold (6), so the restored unit comes back at rank 2, not rank 1.
            Assert.Equal(Fixed.FromInt(100), StatVocabulary.DeltaOf(Installed(host, restored), StatId.MaxHealth));
        }

        // ══════════════════════════ Determinism: two identical runs are byte-identical ═════════════════════

        /// <summary>Drive one host through a scripted veterancy match, sampling the folded checksum every tick.</summary>
        private static List<uint> ScriptedRun()
        {
            var host = Host();
            EntityWorld w = host.World;
            var samples = new List<uint>(60);
            host.SetChecksumSink((t, h) => samples.Add(h));

            UnitDefinition vet = VeteranDef();
            UnitDefinition plain = PlainDef();
            int killer = Spawn(w, vet, Faction.Player1);

            for (int t = 0; t < 60; t++)
            {
                if (t % 7 == 0)
                {
                    int victim = Spawn(w, plain, Faction.Player2, x: 5);
                    Kill(w, victim, killer);
                }
                host.StepOnce();
            }
            return samples;
        }

        [Fact]
        public void TwoIdenticalVeterancyRuns_AreByteIdentical()
        {
            List<uint> a = ScriptedRun();
            List<uint> b = ScriptedRun();
            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
                Assert.True(a[i] == b[i], $"checksum diverged at sample {i}: 0x{a[i]:X8} vs 0x{b[i]:X8}");
        }

        [Fact]
        public void TheScriptedRun_ActuallyRanksUp_SoTheDeterminismProofIsNotVacuous()
        {
            // Guards the test above from passing over a run in which veterancy never fired.
            var host = Host();
            EntityWorld w = host.World;
            int killer = Spawn(w, VeteranDef(), Faction.Player1);
            for (int t = 0; t < 60; t++)
            {
                if (t % 7 == 0) { int v = Spawn(w, PlainDef(), Faction.Player2, x: 5); Kill(w, v, killer); }
                host.StepOnce();
            }
            Assert.True(w.VeterancyKills[killer] >= 6, $"expected the scripted run to reach rank 2, got {w.VeterancyKills[killer]} kills.");
            Assert.Equal(Fixed.FromInt(100), StatVocabulary.DeltaOf(Installed(host, killer), StatId.MaxHealth));
        }

        // ══════════════════════════ Save / load mid-rank ═══════════════════════════════════════════════════

        private sealed class Applied
        {
            public SimulationHost Host = null!;
            public FactionDefinition?[] SlotDefs = null!;
            public EntityWorld World => Host.World;
        }

        /// <summary>The trigger-less applier fixture, but with the faction's worker OPTED IN to veterancy — so the
        /// def re-resolved on restore carries the ladder and the rank can re-derive.</summary>
        private static FactionDefinition VeterancyFaction()
        {
            FactionDefinition f = GoldenApplierScenario.BuildFaction();
            f.Units[0].Veterancy = TwoRankLadder();
            return f;
        }

        private static Applied BuildApplied(FactionDefinition faction)
        {
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;
            slotDefs[(int)Faction.Player2] = faction;

            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            host.ChecksumInterval = 1;
            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);
            ValidationResult r = new ScenarioValidator().Validate(GoldenApplierScenario.BuildModel());
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);
            return new Applied { Host = host, SlotDefs = slotDefs };
        }

        [Fact]
        public void SaveLoadMidRank_RoundTripsTheCounter_AndReDerivesTheSameInstalledVector()
        {
            FactionDefinition faction = VeterancyFaction();
            Applied source = BuildApplied(faction);
            Applied dest = BuildApplied(faction);
            EntityWorld w = source.World;

            // Find an applied worker (the opted-in def) and put it mid-rank.
            int veteran = -1;
            for (int i = 0; i < w.HighWaterMark; i++)
                if (w.IsAlive(i) && w.SourceDefinition[i]?.Veterancy != null) { veteran = i; break; }
            Assert.True(veteran >= 0, "the applied fixture spawned no veterancy-capable unit.");

            w.VeterancyKills[veteran] = 14;             // past the last threshold — rank 2
            TickVeterancy(source.Host);
            StatDelta[] expected = Installed(source.Host, veteran);
            Assert.Equal(2, expected.Length);

            // Capture → Validate (the per-lane length contract the disk path enforces) → restore into a fresh host.
            var fromTable = CanonicalEffectDescriptorTable.Build(source.Host.AbilityRegistry, source.Host.ItemRegistry);
            SaveGameState state = SaveGameState.CaptureFrom(source.Host, fromTable);
            state.Validate("15-24d veterancy save round-trip");
            var intoTable = CanonicalEffectDescriptorTable.Build(dest.Host.AbilityRegistry, dest.Host.ItemRegistry);
            state.RestoreInto(dest.Host, intoTable, dest.SlotDefs);

            // (a) the counter arrived,
            Assert.Equal(14, dest.World.VeterancyKills[veteran]);
            // (b) the MINTED descriptor round-tripped by value (DW-997's KindMintedModifier) — present BEFORE any tick,
            StatDelta[] restored = Installed(dest.Host, veteran);
            Assert.Equal(expected.Length, restored.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i].Stat, restored[i].Stat);
                Assert.Equal(expected[i].Delta.Raw, restored[i].Delta.Raw);
            }
            // (c) and the runtime re-derives the SAME rank on the first post-load tick (no swap, no drift).
            Modifier? beforeTick = InstalledRef(dest.Host, veteran);
            TickVeterancy(dest.Host);
            Assert.Same(beforeTick, InstalledRef(dest.Host, veteran));
            Assert.Equal(w.EffectiveMaxHealth[veteran].Raw, dest.World.EffectiveMaxHealth[veteran].Raw);
        }

        [Fact]
        public void SaveLoadMidRank_ResumesAByteIdenticalChecksumStream()
        {
            FactionDefinition faction = VeterancyFaction();
            Applied reference = BuildApplied(faction);
            Applied saved = BuildApplied(faction);

            int veteran = -1;
            for (int i = 0; i < reference.World.HighWaterMark; i++)
                if (reference.World.IsAlive(i) && reference.World.SourceDefinition[i]?.Veterancy != null) { veteran = i; break; }
            Assert.True(veteran >= 0);

            reference.World.VeterancyKills[veteran] = 4;
            saved.World.VeterancyKills[veteran] = 4;
            for (int t = 0; t < 20; t++) { reference.Host.StepOnce(); saved.Host.StepOnce(); }

            var refSeq = new List<uint>();
            reference.Host.SetChecksumSink((t, h) => refSeq.Add(h));
            for (int t = 0; t < 60; t++) reference.Host.StepOnce();

            Applied resumed = BuildApplied(faction);
            var fromTable = CanonicalEffectDescriptorTable.Build(saved.Host.AbilityRegistry, saved.Host.ItemRegistry);
            SaveGameState state = SaveGameState.CaptureFrom(saved.Host, fromTable);
            state.Validate("15-24d veterancy resume");
            var intoTable = CanonicalEffectDescriptorTable.Build(resumed.Host.AbilityRegistry, resumed.Host.ItemRegistry);
            state.RestoreInto(resumed.Host, intoTable, resumed.SlotDefs);

            var resumedSeq = new List<uint>();
            resumed.Host.SetChecksumSink((t, h) => resumedSeq.Add(h));
            for (int t = 0; t < 60; t++) resumed.Host.StepOnce();

            Assert.Equal(refSeq.Count, resumedSeq.Count);
            for (int i = 0; i < refSeq.Count; i++)
                Assert.True(refSeq[i] == resumedSeq[i],
                    $"resumed veterancy stream diverged at sample {i}: 0x{refSeq[i]:X8} vs 0x{resumedSeq[i]:X8}");
        }

        // ══════════════════════════ Content plumbing: hash, writer, id collision ═══════════════════════════

        [Fact]
        public void VeterancyModifierId_CollidesWithNoOtherMintedId()
        {
            Assert.NotEqual(HeroXpSystem.HeroGrowthModifierId,    VeterancySystem.VeterancyModifierId);
            Assert.NotEqual(HeroXpSystem.HeroAttrBaseModifierId,  VeterancySystem.VeterancyModifierId);
            Assert.NotEqual(HeroXpSystem.HeroThresholdModifierId, VeterancySystem.VeterancyModifierId);
            Assert.NotEqual(HeroXpSystem.HeroSpentModifierId,     VeterancySystem.VeterancyModifierId); // Story 15-24e
            // …and the 15-24e id is distinct from every OTHER hero-minted id too (this family is the only place
            // the four hero slots are compared, so a colliding append would otherwise silently overwrite a live
            // instance through ModifierStore's id-keyed Ignore/Remove).
            Assert.NotEqual(HeroXpSystem.HeroGrowthModifierId,    HeroXpSystem.HeroSpentModifierId);
            Assert.NotEqual(HeroXpSystem.HeroAttrBaseModifierId,  HeroXpSystem.HeroSpentModifierId);
            Assert.NotEqual(HeroXpSystem.HeroThresholdModifierId, HeroXpSystem.HeroSpentModifierId);
            Assert.False(HeroXpSystem.HeroSpentModifierId >= ItemSystem.ItemModifierIdBase &&
                         HeroXpSystem.HeroSpentModifierId < ItemSystem.ItemModifierIdBase + 0x10000);
            Assert.False(HeroXpSystem.HeroSpentModifierId >= ResearchSystem.ResearchModifierIdBase &&
                         HeroXpSystem.HeroSpentModifierId < ResearchSystem.ResearchModifierIdBase + 0x10000);
            // The two BASE+index families: assert the id is outside each family's whole reachable range.
            Assert.False(VeterancySystem.VeterancyModifierId >= ItemSystem.ItemModifierIdBase &&
                         VeterancySystem.VeterancyModifierId < ItemSystem.ItemModifierIdBase + 0x10000);
            Assert.False(VeterancySystem.VeterancyModifierId >= ResearchSystem.ResearchModifierIdBase &&
                         VeterancySystem.VeterancyModifierId < ResearchSystem.ResearchModifierIdBase + 0x10000);
        }

        [Fact]
        public void ContentHash_MovesWithTheLadder_AndIsOrderIndependentInsideARank()
        {
            static ulong Of(UnitDefinition u) => ContentHash.Compute(
                new List<FactionDefinition> { new FactionDefinition { Id = "f", Units = { u } } },
                AbilityRegistry.Empty, ItemRegistry.Empty, DamageTable.Default);

            ulong plain = Of(PlainDef());
            ulong ranked = Of(VeteranDef());
            Assert.NotEqual(plain, ranked);   // an authored ladder must reject at the lobby against one without

            var a = new Dictionary<string, float> { ["max_health"] = 100f, ["attack_damage"] = 5f };
            var b = new Dictionary<string, float> { ["attack_damage"] = 5f, ["max_health"] = 100f };
            ulong ha = Of(VeteranDef(new VeterancyDefinition { Ranks = new List<VeterancyRank> { new() { Kills = 1, StatDeltas = a } } }));
            ulong hb = Of(VeteranDef(new VeterancyDefinition { Ranks = new List<VeterancyRank> { new() { Kills = 1, StatDeltas = b } } }));
            Assert.Equal(ha, hb);             // ORDINAL key order, never dictionary enumeration order

            ulong shifted = Of(VeteranDef(new VeterancyDefinition
            {
                Ranks = new List<VeterancyRank> { new() { Kills = 2, StatDeltas = a } },
            }));
            Assert.NotEqual(ha, shifted);     // the threshold itself is folded
        }

        [Fact]
        public void FactionWriter_RoundTripsTheLadder_AndOmitsItWhenUnauthored()
        {
            const string factionJson = """
            {
              "id": "alpha",
              "display_name": "Alpha",
              "units": [ { "id": "grunt", "display_name": "Grunt", "category": "Melee", "hp": 100 } ]
            }
            """;

            UnitDefinition edited = System.Text.Json.JsonSerializer
                .Deserialize<FactionDefinition>(factionJson, FactionDefinition.JsonOptions)!.GetUnit("grunt")!;
            edited.Veterancy = TwoRankLadder();
            string outJson = FactionWriter.PatchFactionJson(factionJson,
                new UnitEdit { Kind = UnitEditKind.Update, TargetId = "grunt", Def = edited });

            UnitDefinition reloaded = System.Text.Json.JsonSerializer
                .Deserialize<FactionDefinition>(outJson, FactionDefinition.JsonOptions)!.GetUnit("grunt")!;
            Assert.NotNull(reloaded.Veterancy);
            Assert.Equal(2, reloaded.Veterancy!.Ranks!.Count);
            Assert.Equal(6, reloaded.Veterancy.Ranks[1].Kills);
            Assert.Equal(100f, reloaded.Veterancy.Ranks[1].StatDeltas!["max_health"]);

            // An unauthored block writes NO key — so no shipped faction file churns.
            UnitDefinition untouched = System.Text.Json.JsonSerializer
                .Deserialize<FactionDefinition>(factionJson, FactionDefinition.JsonOptions)!.GetUnit("grunt")!;
            untouched.Hp = 250f;
            string plainOut = FactionWriter.PatchFactionJson(factionJson,
                new UnitEdit { Kind = UnitEditKind.Update, TargetId = "grunt", Def = untouched });
            Assert.DoesNotContain("veterancy", plainOut);
        }

        [Fact]
        public void VeterancyDefinition_Clone_IsADeepCopy()
        {
            VeterancyDefinition src = TwoRankLadder();
            VeterancyDefinition copy = src.Clone();

            copy.Ranks![0].Kills = 99;
            copy.Ranks[0].StatDeltas!["max_health"] = 1f;
            Assert.Equal(3, src.Ranks![0].Kills);
            Assert.Equal(40f, src.Ranks[0].StatDeltas!["max_health"]);
        }
    }
}
