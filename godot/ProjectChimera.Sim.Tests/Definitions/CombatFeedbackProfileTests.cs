#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using ProjectChimera.Core;              // EntityWorld, Fixed, FixedVec3, Faction, FactionRegistry, BuildingStore, ResourceStore, SimChecksum
using ProjectChimera.Core.Definitions;  // CombatFeedbackProfile, CombatFeedbackDefaults, FlashSpec, UnitDefinition, AbilityDefinition, ContentJson
using ProjectChimera.Sim.Tests.Golden;  // GoldenScenario, GoldenChecksumReplay (host-driven AC3 drain proof)
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 2.7 (CombatFeedbackProfile) Tier-1 guards — Godot-free. Three concerns:
    ///   1. The embedded default set reproduces today's as-built bridge constants byte-for-byte (AC1).
    ///   2. The DTO round-trips on BOTH content paths — the lenient unit/faction loader AND the strict
    ///      <see cref="ContentJson.Options"/> ability loader (incl. the 2.5a editor re-emit) (AC2).
    ///   3. The profile is EXCLUDED from the determinism hash: a per-entity <c>FeedbackProfile</c> never moves
    ///      <see cref="SimChecksum.Compute"/>, and draining the <c>CombatEventQueue</c> each tick (what the bridge
    ///      does) cannot perturb the sim — identical golden checksums with vs without the drain (AC1/AC3).
    /// The 10 committed goldens staying byte-identical is enforced by the existing Golden/*Tests (a moved golden
    /// there = a leaked sim read of the profile). This file adds the targeted exclusion teeth.
    /// </summary>
    public class CombatFeedbackProfileTests
    {
        // ── AC1: the embedded default set is byte-for-byte today's as-built CombatFeedbackBridge constants ──

        [Fact]
        public void EmbeddedDefaults_EqualTodaysAsBuiltConstants()
        {
            // From CombatFeedbackBridge.cs: MakeMat colours + the _Process SpawnFlash(scale, duration) literals,
            // and the kill SetShake(0.12f, 0.22f). These ARE the canonical default (UX-DR51 "per as-built bridge").
            AssertFlash(CombatFeedbackDefaults.Melee,  1.0f, 0.50f, 0.10f, emissionMult: 3.0f, scale: 0.9f, durationSec: 0.18f);
            AssertFlash(CombatFeedbackDefaults.Ranged, 1.0f, 0.85f, 0.10f, emissionMult: 2.5f, scale: 0.7f, durationSec: 0.15f);
            AssertFlash(CombatFeedbackDefaults.Splash, 1.0f, 0.20f, 0.05f, emissionMult: 4.0f, scale: 1.8f, durationSec: 0.28f);
            AssertFlash(CombatFeedbackDefaults.Kill,   1.0f, 0.95f, 0.80f, emissionMult: 5.0f, scale: 1.2f, durationSec: 0.25f);

            Assert.Equal(0.12f, CombatFeedbackDefaults.KillShake.DurationSec); // SetShake(duration, strength)
            Assert.Equal(0.22f, CombatFeedbackDefaults.KillShake.Strength);
        }

        private static void AssertFlash(FlashSpec spec, float r, float g, float b,
            float emissionMult, float scale, float durationSec)
        {
            Assert.NotNull(spec.ColorRgb);
            Assert.Equal(3, spec.ColorRgb!.Length);
            Assert.Equal(r, spec.ColorRgb[0]);
            Assert.Equal(g, spec.ColorRgb[1]);
            Assert.Equal(b, spec.ColorRgb[2]);
            Assert.Equal(emissionMult, spec.EmissionMult);
            Assert.Equal(scale, spec.Scale);
            Assert.Equal(durationSec, spec.DurationSec);
        }

        // ── AC2: the SAME POCO must load on BOTH content paths ──

        /// <summary>The REAL lenient unit/faction loader options — Story 2.7 review: reference the production object
        /// (<see cref="FactionDefinition.JsonOptions"/>) directly so this test can never pass against a replica that
        /// has drifted from the actual faction loader.</summary>
        private static readonly JsonSerializerOptions LenientUnitOptions = FactionDefinition.JsonOptions;

        [Fact]
        public void Profile_RoundTrips_OnTheLenientUnitPath()
        {
            // All floats below are exactly representable so equality is bit-exact (no tolerance needed).
            const string json = @"{
                ""id"": ""flame_knight"",
                ""category"": ""Melee"",
                ""combat_feedback"": {
                    ""hit_flash"":   { ""color_rgb"": [0.5, 0.25, 0.75], ""emission_mult"": 6.0, ""scale"": 1.5, ""duration_sec"": 0.5 },
                    ""impact_sound"": ""sfx/custom_hit"",
                    ""shake"":       { ""duration_sec"": 0.25, ""strength"": 0.5 },
                    ""hit_freeze_frames"": 6,
                    ""death_flash"": { ""color_rgb"": [0.125, 0.25, 0.5], ""emission_mult"": 2.0, ""scale"": 2.0, ""duration_sec"": 0.25 },
                    ""death_sound"": ""sfx/custom_death""
                }
            }";

            UnitDefinition? def = JsonSerializer.Deserialize<UnitDefinition>(json, LenientUnitOptions);
            Assert.NotNull(def);
            CombatFeedbackProfile? p = def!.CombatFeedback;
            Assert.NotNull(p);
            Assert.Equal("sfx/custom_hit", p!.ImpactSoundId);
            Assert.Equal("sfx/custom_death", p.DeathSoundId);
            Assert.Equal(6, p.HitFreezeFrames);

            Assert.NotNull(p.HitFlash);
            Assert.Equal(6.0f, p.HitFlash!.EmissionMult);
            Assert.Equal(1.5f, p.HitFlash.Scale);
            Assert.Equal(3, p.HitFlash.ColorRgb!.Length);
            Assert.Equal(0.75f, p.HitFlash.ColorRgb[2]);

            Assert.NotNull(p.Shake);
            Assert.Equal(0.25f, p.Shake!.DurationSec);
            Assert.Equal(0.5f, p.Shake.Strength);

            Assert.NotNull(p.DeathFlash);
            Assert.Equal(2.0f, p.DeathFlash!.EmissionMult);
        }

        [Fact]
        public void Profile_RoundTrips_OnTheStrictAbilityPath_AndThe2_5aEditorReEmit()
        {
            // STRICT path: UnmappedMemberHandling.Disallow reaches the nested POCO reflectively, so EVERY declared
            // sub-field must be accepted and any stray field rejected. This is the asymmetry the story flags.
            const string json = @"{
                ""id"": ""fireball"",
                ""targeting"": ""GroundPoint"",
                ""combat_feedback"": {
                    ""hit_flash"": { ""color_rgb"": [0.5, 0.25, 0.75], ""emission_mult"": 6.0, ""scale"": 1.5, ""duration_sec"": 0.5 },
                    ""impact_sound"": ""sfx/fireball_cast"",
                    ""hit_freeze_frames"": 3
                }
            }";

            AbilityDefinition? ability = JsonSerializer.Deserialize<AbilityDefinition>(json, ContentJson.Options);
            Assert.NotNull(ability);
            Assert.NotNull(ability!.CombatFeedback);
            Assert.Equal("sfx/fireball_cast", ability.CombatFeedback!.ImpactSoundId);
            Assert.Equal(3, ability.CombatFeedback.HitFreezeFrames);
            Assert.NotNull(ability.CombatFeedback.HitFlash);
            Assert.Equal(6.0f, ability.CombatFeedback.HitFlash!.EmissionMult);

            // 2.5a editor round-trip: re-emit through the indented ContentJson.Options, then reload through the SAME
            // strict options. Disallow would HARD-REJECT a re-emitted computed getter or a stray member — so this
            // passing proves combat_feedback is a plain declared auto-prop (NOT a [JsonIgnore]-needing computed getter).
            var indented = new JsonSerializerOptions(ContentJson.Options) { WriteIndented = true };
            string reEmitted = JsonSerializer.Serialize(ability, indented);
            AbilityDefinition? reloaded = JsonSerializer.Deserialize<AbilityDefinition>(reEmitted, ContentJson.Options);
            Assert.NotNull(reloaded);
            Assert.NotNull(reloaded!.CombatFeedback);
            Assert.Equal("sfx/fireball_cast", reloaded.CombatFeedback!.ImpactSoundId);
            Assert.Equal(6.0f, reloaded.CombatFeedback.HitFlash!.EmissionMult);
        }

        // ── AC1/AC3 determinism teeth: the profile is excluded from the hash by construction ──

        [Fact]
        public void FeedbackProfile_IsExcludedFromSimChecksum_AndAlgoVersionIsPinned()
        {
            // 2.7 added NO fold of its own (FeedbackProfile is presentation-read, never hashed). The version has since
            // moved for UNRELATED folds (first 3.12's Delivery + ProjectileSpeed); the FeedbackProfile exclusion teeth
            // below are the real assertion — the version pin just tracks the current value (canonically pinned elsewhere).
            Assert.Equal(22, SimChecksum.AlgoVersion);

            var registry  = new FactionRegistry(2);
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            var world     = new EntityWorld();
            int e = world.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)),
                                 Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));

            uint before = SimChecksum.Compute(world, buildings, resources, registry);

            // Set the per-entity feedback override (EntityWorld's first reference-typed SoA). It MUST NOT move the
            // checksum — presentation-read, never folded (the MeshType/CategoryOf posture). A move here would mean a
            // leaked sim read of the profile: fix the leak, never re-pin.
            world.FeedbackProfile[e] = new CombatFeedbackProfile
            {
                HitFreezeFrames = 9,
                ImpactSoundId = "sfx/anything",
                HitFlash = new FlashSpec { ColorRgb = new[] { 1f, 0f, 0f }, EmissionMult = 9f, Scale = 9f, DurationSec = 9f },
            };

            uint after = SimChecksum.Compute(world, buildings, resources, registry);
            Assert.Equal(before, after);
        }

        [Fact]
        public void DrainingCombatEvents_EachTick_DoesNotPerturbTheChecksum()
        {
            const int ticks = GoldenScenario.DefaultTicks; // 300 ticks of the fighting golden scenario

            // Run A: mimic the presentation CombatFeedbackBridge — drain (read) + Clear the queue after every tick.
            GoldenHarness a = GoldenScenario.Build();
            var seqA = new List<GoldenChecksumReplay.Sample>(ticks);
            a.Host.SetChecksumSink((tick, hash) => seqA.Add(new GoldenChecksumReplay.Sample(tick, hash)));
            int totalDrained = 0;
            for (int i = 0; i < ticks; i++)
            {
                a.Host.StepOnce();
                totalDrained += a.Host.CombatEvents.Count; // the bridge would render these…
                a.Host.CombatEvents.Clear();               // …then clear them (it owns the single Clear()).
            }

            // Run B: never touch the queue (the headless golden harness — exactly how the sim runs in CI).
            GoldenHarness b = GoldenScenario.Build();
            var seqB = new List<GoldenChecksumReplay.Sample>(ticks);
            b.Host.SetChecksumSink((tick, hash) => seqB.Add(new GoldenChecksumReplay.Sample(tick, hash)));
            for (int i = 0; i < ticks; i++) b.Host.StepOnce();

            // Non-vacuity: the scenario actually fights (melee/ranged/kills), so the drained path was exercised — a
            // vacuous "drained nothing" run would prove nothing.
            Assert.True(totalDrained > 0,
                "Expected the golden scenario to produce combat events; the drain proof is vacuous otherwise.");

            // The drain cannot perturb the sim: every per-tick checksum is byte-identical with vs without draining.
            // (The sim never reads CombatEventQueue; SimChecksum.Compute takes no queue argument — proven structurally.)
            GoldenChecksumReplay.Divergence? div = GoldenChecksumReplay.CompareSequences(seqB, seqA);
            Assert.True(div is null,
                div is null ? "" : "Draining CombatEvents perturbed the sim — " + GoldenChecksumReplay.DescribeDivergence(div.Value));
        }
    }
}
