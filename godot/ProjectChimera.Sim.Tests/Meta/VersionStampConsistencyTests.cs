#nullable enable
using System;
using System.Linq;
using System.Reflection;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Multiplayer;
using Xunit;

namespace ProjectChimera.Sim.Tests.Meta
{
    /// <summary>
    /// Story 1.10c (Decision #3 scope expansion — the "D3 version-stamp consistency check") — the SINGLE place
    /// that pins the project's cross-version / cross-peer COMPATIBILITY stamps so none can drift silently, and so
    /// any bump forces a conscious decision about whether sibling stamps (and the goldens) must move together.
    ///
    /// This is the guard-test realization the architecture's "check-runner checks the version stamps move
    /// together" calls for. It does NOT enforce a single shared value (the stamps legitimately have independent
    /// values and meanings); it enforces that every stamp passes through ONE reviewed checkpoint when it changes —
    /// the realistic, buildable form of "move together."
    ///
    /// ── Honest state of the surface (probed 2026-06-25, Story 1.10c) ─────────────────────────────────────────
    /// The architecture names FIVE stamps. Two of them are NOT BUILT YET (D3.1 work — see <see cref="UnbuiltStamps"/>):
    ///   • CurrentGameVersion — a game/app semver constant gating <c>min_game_version</c>; no home in code yet.
    ///   • schema_version (on <see cref="ScenarioData"/>) — the scenario-JSON format version for forward-compat
    ///     migrations; not a field yet.
    /// Three exist and are pinned below; plus two sibling stamps the same architecture decision implicates
    /// (<see cref="ReplayRecorder.VERSION"/>, <see cref="CanonicalModelHash.AlgoVersion"/>). So this guard pins
    /// FIVE existing stamps today and documents the two unbuilt ones as TODO(D3.1), with a tripwire (
    /// <see cref="SchemaVersionStamp_IsStillUnbuilt_OrHasBeenWiredIntoThisRegistry"/>) that fires the moment the
    /// scenario <c>schema_version</c> is added so it cannot land outside this consistency surface.
    ///
    /// NOTE: <see cref="SimChecksum.AlgoVersion"/> is ALSO canonically pinned (with a known-state hash) by
    /// <c>SimChecksumCoverageGuardTest.KnownWorldState_ProducesPinnedV24Hash</c>. It is repeated here for a complete
    /// single-view registry; a deliberate bump must update BOTH guards (and re-baseline the goldens) in one commit.
    ///
    /// All five stamps are reachable from this Godot-free Tier-1 assembly: <c>src/Core/**</c> (SimChecksum,
    /// CanonicalModelHash, ContentPackageManifest) and the three Godot-free <c>src/Multiplayer</c> files
    /// (NetworkCommand, ReplayRecorder) are in the <c>SimSources.props</c> compile set — so this is a plain
    /// compile-time reference, no reflection-on-uncompilable-types needed.
    /// </summary>
    public class VersionStampConsistencyTests
    {
        // ── The pinned registry (expected current values). An INTENTIONAL bump edits the constant HERE, in the
        //    same commit as the source change — that edit is the "did the siblings + goldens move too?" checkpoint.

        /// <summary>Runtime desync-checksum algorithm version. Bump ⇒ re-baseline ALL goldens (same commit).
        /// v24 (Story 15-22, Phase C): a RE-RECORD GENERATION MARKER, NOT a fold change — the only entry in this
        /// list that is. Nothing was added to, removed from, or reordered within the hashed set; twelve bounded
        /// simulation corrections (DW-548/549/570/658/659/664/674/678/766/803/837/838) moved folded VALUES only,
        /// and the bump exists so a Phase-B and a Phase-C recording cannot both stamp checksum_algo_version 23
        /// while holding different bytes. The full per-version history — including v21/v22/v23, which this
        /// summary predates — lives ONCE on SimChecksum.AlgoVersion's own XML doc; that is authoritative.
        /// v20 (Story 7.12): first-ever fold of the AllianceStore — the sim-owned per-faction team-id mask that
        /// generalizes win resolution to N-faction, team-aware last-team-standing. One team-id Mix per ACTIVE faction
        /// (ascending), immediately AFTER the WinStateStore block and before SimRng. Every existing golden is FFA
        /// (team id == slot index, the only populated state in 1.0 — lobby team wiring is Story 9.15), so the fold
        /// adds one Mix((int)f) per active faction and moves the hash even with the default mask (a null store folds
        /// byte-identically to default FFA). The preset PARAMS already fold into the CanonicalModelHash handshake
        /// (v12, unchanged this story). The ONE scheduled re-baseline of ALL per-tick goldens.
        /// v19 (Story 7.11): first-ever fold of the WinStateStore — the sim-layer win-condition runtime state (the
        /// scalar MatchTicks grace/elapsed counter, then per ACTIVE faction KothHoldTicks / SurvivalRemaining /
        /// Verdict, AFTER the DslEventQueue fold and before SimRng). Win evaluation moved out of presentation into the
        /// deterministic WinConditionSystem, so its live state must be peer-checksummed; the preset PARAMS fold into
        /// the CanonicalModelHash handshake (v12), not here. The ONE scheduled re-baseline of ALL 24 per-tick goldens.
        /// v18 (Story 7.5, landed via the 7-5 re-land merge): first-ever fold of the DslEventQueue — the PENDING
        /// next-tick custom-event raises (registry index + raiser + the fixed MaxEventParams param-raw stride,
        /// count-prefixed in enqueue order, AFTER the DslLoopState fold and before SimRng). Live CROSS-TICK sim
        /// state (non-empty at the checksum boundary whenever feedback is pending), unlike the provably-drained
        /// CombatEventQueue/DeathFeed. The custom-event DECLARATIONS fold into the CanonicalModelHash handshake
        /// (v10), not here.
        /// v17 (Story 7.6): the bounded-loop / array / fuel layer — the DslVarTable fold gains an ARRAYS section
        /// (count-prefixed live elements per declared array) and the new DslLoopState store folds after the table
        /// (for_each_batched continuation rows + the per-tick DSL fuel consumed), before SimRng (which stays last).
        /// v16 (Story 7.3): first-ever fold of the DslVarTable — live typed/scoped DSL variables (Global then
        /// Per-player, EVERY slot 0..7 per declaration — writes can land on inactive slots, so all slots fold) +
        /// named timers. Declarations stay OUT of the CanonicalModelHash/
        /// StartStateHash handshake (Triggers/Regions basis); only the live per-tick values fold here.
        /// v15 (Story 6.3): folded per-entity Elevation (the spawn-sampled terrain height, a dedicated SoA array —
        /// NOT Position.Y). The height-advantage vision toggle/bonus are deliberately NOT folded (they only affect the
        /// non-folded fog Grid), so CanonicalModelHash/StartStateHash stay put.
        /// v14 (Story 4.10): first-ever fold of ResearchStore (InProgressIndex/RemainingTicks + the per-research
        /// CompletedLevels/cumulative stat deltas), deferred from 4.9's mid-match-mutable order path.
        /// v13 (Story 4.7): first-ever fold of ResourceNodeStore (SupplyRemaining/Active/AssignedGatherers) plus the
        /// new mutable IncomeTicksElapsed counter. v12 (Story 3.15): folded the mutable ItemStore + per-hero inventory.
        /// v11 (Story 3.13): folded per-entity XpBounty + the mutable HeroStore state (Level/Xp/GrowthStacks + reserved
        /// 3.14 revival fields). v10 (Story 3.12): folded per-entity Delivery + ProjectileSpeed.
        /// v9 (Story 2.12): folded the per-entity shift-queue order ring (count-driven) + the per-building rally point
        /// (D-1). v8 (Story 2.6): folded per-entity EffectiveArmor (the buffable armor stat). v7 (Story 2.4a): folded
        /// per-entity AbilityCooldownTicks (count-driven). v6 (Story 2.2b): Effective* / Energy / StatusFlagsOf +
        /// the ModifierStore instance state.</summary>
        private const int ExpectedSimChecksumAlgoVersion = 24;

        /// <summary>Load-time canonical start-state hash algorithm version (lobby handshake value).
        /// v3 (Story 2.9b follow-up): folded ScenarioPlayerSlot.StartCrystal (sim-affecting per-slot start-state).
        /// v4 (Story 4.4): folded ScenarioData.Supply's resolved values (sim-affecting supply/cap config).
        /// v5 (Story 4.7): folded ScenarioResourceNode's 6 new fields (collection model / resource type /
        /// requires_structure gate / owner slot / income period — all sim-affecting).
        /// v6 (Story 6.5): folded the authored pathability layer (painted blocked bitset digest + slope-auto-block
        /// config) — lockstep-critical because it feeds MOVEMENT (Position is checksummed), so a mismatched blocked
        /// layer must reject at the handshake instead of desyncing in-sim.
        /// v7 (Story 6.6): folded the BLOCKING-prop + WATER footprints (their union of FlowField.WorldToCell cells) —
        /// the inverse-free extension of v6 (they become blocked cells in the same PathabilityGrid). Non-blocking
        /// props, cameras, and every rotation/scale stay EXCLUDED (cosmetic).
        /// v8 (Story 7.7): folded the FULL trigger/DSL sim-semantic model — Regions (total-ordered), flat Triggers
        /// (declaration order), Variables, Timers, and the parsed TriggerGraphJson graph IR as a TYPED fold (never
        /// JSON bytes). The `_editor` per-node bag and the schema_version/checksum_algo_version stamps stay
        /// EXCLUDED. The ONE named re-baseline: hero-start-state golden re-recorded (StartStateHash VALUE moves via
        /// the canonical seed; its AlgoVersion stays 2), independent-FNV pins recomputed, exclusion tests inverted
        /// to sensitivity tests. SimChecksum (17) and the 24 per-tick world goldens are UNTOUCHED.
        /// v9 (Story 7.8): folded the ScenarioData.CustomUi widget tree as a TYPED recursive walk (present marker +
        /// canvas dims + count prefix + per-widget id/kind/anchor/offset/size/binds, recursing children) appended
        /// AFTER the v8 trigger-graph fold. Divergent widget trees now hash differently so HandshakeGate blocks the
        /// lobby start. The per-widget `_editor` bag is EXCLUDED by construction; absent/empty custom_ui folds a 0
        /// marker (byte-identical to v8). The ONE named re-baseline: the hero-start-state golden re-recorded (its
        /// StartStateHash value moves via the canonical seed; StartStateHash.AlgoVersion stays 2). SimChecksum (17)
        /// and the 24 per-tick world goldens are UNTOUCHED (the read rail is presentation-only, NOT folded).
        /// v10 (Story 7.5, landed via the 7-5 re-land merge): folded the ScenarioData.CustomEvents registry
        /// (count-prefixed declaration order: per event its name, typed params, allowed-raiser set) appended AFTER
        /// the v9 CustomUi fold, and the typed graph fold gained the three 7.5 node kinds (raise_event's
        /// name/raiser/next_tick, expr_event_param's name, custom_event's event_name on the EventNode case).
        /// 7.5's original exclusion basis (at v7, Triggers/Variables stayed out of the handshake) dissolved when
        /// v8 folded the full trigger/DSL model — divergent registries/graphs must reject at the lobby instead of
        /// desyncing in-sim. Named re-baseline: hero-start-state golden re-recorded (StartStateHash value moves
        /// via the canonical seed; its AlgoVersion stays 2). The 24 per-tick goldens move too — via the SEPARATE
        /// SimChecksum v18 bump landing in the same merge, not via this fold.
        /// v11 (Story 7.9): the custom-UI WRITE RAIL entered the folded widget tree — MixWidget gained a Button case
        /// folding Text, EventName, an arg-count prefix + each authored arg RAW (the parallel ArgTypes is
        /// authoring/gate-only, NOT folded), the LocalAction (enum by NAME) and its target (widget id / local var
        /// name+value). Divergent widget trees (one peer has the button, one doesn't) hash differently so
        /// HandshakeGate blocks the lobby start. The per-widget `_editor` bag stays EXCLUDED. The ONE named
        /// re-baseline: the hero-start-state golden re-recorded (StartStateHash value moves via the canonical seed;
        /// StartStateHash.AlgoVersion stays 2). SimChecksum (18) and the 24 per-tick world goldens are UNTOUCHED
        /// (the write rail enters the EXISTING checksum-folded DslEventQueue — no new folded sim state).
        /// v12 (Story 7.11): folded the ScenarioData.WinConditionSpec preset params, appended IMMEDIATELY AFTER the
        /// built-in WinCondition enum fold. A null / None spec folds NOTHING (byte-identical to v11 apart from this
        /// AlgoVersion bump — the omit-when-default discipline; the serializer normalizes a None spec to null); a
        /// preset folds a present marker + kind name + params (KotH region_id/hold_ticks; Survival
        /// faction_slot/survive_ticks; Assassination leader_unit_index; Landmark structure_index) so divergent
        /// victory rules reject at the lobby handshake. The ONE named re-baseline: hero-start-state golden re-recorded
        /// (StartStateHash value moves via the canonical seed; StartStateHash.AlgoVersion stays 2). The 24 per-tick
        /// world goldens move via the SEPARATE SimChecksum v19 bump in the same commit, not via this fold.
        /// v13 (Story 7.13): the typed graph walk gained explicit fold arms for the new node kinds (order_units/
        /// move_camera/cinematic_mode/play_vfx field values, ExprCallNode.Selector, random_choice weighted structure,
        /// enable/disable/run_trigger target id). A no-new-kind scenario folds byte-identical apart from the bump.
        /// v14 (Story 7.14): the typed graph walk gained explicit fold arms for the three objective action-leaf kinds
        /// (show_objective/complete_objective/fail_objective — each folds its objective_id). The authored `objectives`
        /// array itself is hash-EXCLUDED (authoring/presentation data). A scenario carrying none of the three kinds
        /// folds byte-identical apart from this AlgoVersion bump — the ONE named re-baseline: hero-start-state golden
        /// re-recorded (StartStateHash value moves via the canonical seed; StartStateHash.AlgoVersion stays 2).
        /// SimChecksum stays 21 and the 24 per-tick world goldens are UNTOUCHED (objective state rides the existing
        /// v16 DslVarTable fold; a scenario with no authored objectives declares no reserved var).
        /// v15 (DW-272 / Story 15.12): CanonicalFold.MixModifier now folds the new Modifier.PeriodicStacking field (by
        /// name, unconditionally). No shipped scenario embeds an apply_modifier run_effect, so every shipped model folds
        /// byte-identical apart from this AlgoVersion bump — the ONE named re-baseline: hero-start-state golden
        /// re-recorded (StartStateHash.AlgoVersion stays 2). SimChecksum stays 24 and the per-tick world goldens are
        /// UNTOUCHED (regen_rate defaults to 0 ⇒ the new EnergyRegenSystem is a no-op write).</summary>
        /// <summary>DW-941: 15→16 — the building_min_gap fold (sim-affecting: it gates worker-placement order
        /// acceptance, so a lobby mismatch must handshake-reject). Both machines rebuild together; an old client
        /// computes a v15 hash and is rejected at the lobby, which is the intended migration impact.</summary>
        private const int ExpectedCanonicalModelHashAlgoVersion = 16;

        /// <summary>Load-time canonical START-STATE hash algorithm version (Story 3.2, AC3) — a NEW, distinct FNV-64
        /// over the full init state = the <see cref="CanonicalModelHash"/> content seed PLUS the HeroStore rows.
        /// v1 = initial. Its arrival is purely ADDITIVE: it did NOT move <see cref="SimChecksum"/> (9) or
        /// <see cref="CanonicalModelHash"/> (3). Wiring it into the server-attested handshake is Epic 9 (D-3); the
        /// value itself is pinned by the independent-FNV pin (StartStateHashTests) + the hero-start-state golden.</summary>
        private const int ExpectedStartStateHashAlgoVersion = 2;

        /// <summary>Lockstep Hello-handshake wire protocol version.
        /// v2 (Story 9.4): the coordinated wire bump for the server-dictated delay exchange
        /// (<c>DelayDirective</c>/<c>DelayAck</c>) + the widened Ready packet (protocol version + 64-bit
        /// match-agreement hash). The version is now VALIDATED fail-closed on the client's inbound Hello and the
        /// server's inbound Ready (closing the D3.8 gap), so a v1 build can no longer complete the handshake against
        /// a v2 build. No golden/checksum re-baseline — this is handshake/wire only, no sim-array fold.
        /// v3 (Story 15.11, DW-280): the coordinated wire bump for the widened 11→12-byte UnitOrder stride (the ability
        /// slot moved to its own byte so a CastAbility can carry a ground point). Still handshake/wire only — the wire is
        /// not a SimChecksum input, so no golden/checksum re-baseline.</summary>
        private const ushort ExpectedProtocolVersion = 3;

        /// <summary>Story 9.4 — the net-new ruleset-fingerprint hash over the <see cref="EffectCaps"/> structural
        /// caps, folded into <see cref="MatchAgreementHash"/>. v1 = initial (AlgoVersion + every cap in file order).
        /// DW-534 bumped v1→v2: <see cref="EffectCaps.MaxSearchRadius"/> — the authored SearchArea radius ceiling —
        /// joins the fold as an eleventh cap. DW-272 / Story 15.12 bumped v2→v3:
        /// <see cref="EffectCaps.MaxPeriodicStackScale"/> — the stacked-periodic-pulse scaling ceiling — joins as a
        /// twelfth cap. Handshake-only: no sim-array fold, so no golden moved, and no PROTOCOL_VERSION bump
        /// (RulesetHash is precisely the mechanism that makes a cap change handshake-rejectable WITHOUT a protocol bump).
        /// A bump changes the value old clients compute for the start-state handshake — update this pin in the same
        /// commit.</summary>
        private const int ExpectedRulesetHashAlgoVersion = 3;

        /// <summary>Story 9.4 — the single 64-bit start-state-agreement hash on the widened Ready packet. v1 =
        /// initial (AlgoVersion + RulesetHash + initial-delay + faction-count + roster + StartStateHash). Story 9.14
        /// bumped v1→v2: the per-slot TEAM ordinal now folds beside each roster ordinal (a team mismatch fails the
        /// start closed). Story 9.16 bumped v2→v3: <see cref="ContentHash"/> (the loaded content definitions) now
        /// folds in immediately after RulesetHash (a content-byte mismatch fails the start closed). DW-908 bumped
        /// v3→v4: the AI-CONTROLLED FACTION SET (<see cref="AI.AiControlPlan.Mask"/>) now folds beside the roster, so
        /// peers that disagree about who the AI drives are rejected pre-tick-0 instead of desyncing mid-match. A bump
        /// changes the handshake value — update this pin in the same commit.</summary>
        private const int ExpectedMatchAgreementHashAlgoVersion = 4;

        /// <summary>Story 9.16 — the net-new content-definitions fingerprint (factions/units/buildings/research, the
        /// ability + item registries, the damage table) folded into <see cref="MatchAgreementHash"/>. v1 = initial.
        /// A bump changes the value old clients compute for the handshake — update this pin in the same commit.</summary>
        private const int ExpectedContentHashAlgoVersion = 1;

        /// <summary>.chmr replay file-format version. Story 7.9 bumped 2→3 (DslEvent orders). Story 9.11 bumped 3→4
        /// ("replay v2": self-describing tagged body via the frozen MergedTickPacket envelope + a result trailer, and
        /// a header embedding the scenario/ruleset/algo hashes + roster; ReplayPlayer hard-rejects all pre-v4 files).
        /// Story 15.11 (DW-280) bumped 4→5: the UnitOrder wire stride widened 11→12, so a v4 body decoded at the v5
        /// stride would misalign — ReplayPlayer hard-rejects all pre-v5 files at the version gate ("please re-record").</summary>
        private const ushort ExpectedReplayFormatVersion = 5;

        /// <summary>Default minimum game version a packaged .chimera.zip declares it requires.</summary>
        private const string ExpectedManifestMinGameVersion = "0.1";

        /// <summary>Story 7.7 (D3.1 landed): the scenario-JSON FORMAT version <c>ScenarioSerializer.Serialize</c>
        /// stamps into <c>schema_version</c>. Absent ⇒ v1 legacy amnesty; newer-than-current rejects at the
        /// validator. Bump only on a real format change (with a migration/amnesty decision) in the same commit.</summary>
        private const int ExpectedScenarioSchemaVersion = 1;

        [Fact]
        public void DeterminismAlgorithmStamps_ArePinned()
        {
            // These two version the hashing algorithms. A change to either DIVERGES every peer/replay that used the
            // old algorithm — so a bump is never silent: it must re-baseline the goldens (SimChecksum) and/or the
            // canonical-hash expectations (CanonicalModelHash) in the SAME commit.
            Assert.True(SimChecksum.AlgoVersion == ExpectedSimChecksumAlgoVersion,
                $"SimChecksum.AlgoVersion is {SimChecksum.AlgoVersion}, expected {ExpectedSimChecksumAlgoVersion}. " +
                $"If this is an INTENTIONAL checksum-algorithm change: re-baseline ALL goldens, update " +
                $"SimChecksumCoverageGuardTest's pinned known-state hash, AND update " +
                $"{nameof(ExpectedSimChecksumAlgoVersion)} here — all in the same commit. If not, the determinism " +
                $"algorithm drifted; investigate before doing anything else.");

            Assert.True(CanonicalModelHash.AlgoVersion == ExpectedCanonicalModelHashAlgoVersion,
                $"CanonicalModelHash.AlgoVersion is {CanonicalModelHash.AlgoVersion}, expected " +
                $"{ExpectedCanonicalModelHashAlgoVersion}. This is the lobby start-state handshake hash; a bump " +
                $"changes the value old clients computed. Confirm the handshake/migration impact and update " +
                $"{nameof(ExpectedCanonicalModelHashAlgoVersion)} in the same commit.");
        }

        [Fact]
        public void StartStateHashAlgorithmStamp_IsPinned()
        {
            // Story 3.2 (AC3 / AC5): the NEW init-state hash (heroes + content seed). Its arrival is ADDITIVE — it did
            // NOT move SimChecksum (9) or CanonicalModelHash (3). A bump here changes the value old clients would
            // compute for the (Epic 9) start-state handshake, so it must re-record the hero-start-state golden AND the
            // independent-FNV pin (StartStateHashTests) in the SAME commit.
            Assert.True(StartStateHash.AlgoVersion == ExpectedStartStateHashAlgoVersion,
                $"StartStateHash.AlgoVersion is {StartStateHash.AlgoVersion}, expected {ExpectedStartStateHashAlgoVersion}. " +
                $"This is the Story 3.2 init-state hash (heroes + content). A bump must re-record the hero-start-state " +
                $"golden AND update {nameof(ExpectedStartStateHashAlgoVersion)} here in the same commit.");
        }

        [Fact]
        public void StartStateAgreementHashStamps_ArePinned()
        {
            // Story 9.4: the two net-new handshake hashes folded into the start-state-agreement value. A bump to
            // either changes the value old clients compute for the widened Ready handshake — so a bump must update
            // the corresponding pin here (and re-check any recorded hash pins) in the same commit.
            Assert.True(RulesetHash.AlgoVersion == ExpectedRulesetHashAlgoVersion,
                $"RulesetHash.AlgoVersion is {RulesetHash.AlgoVersion}, expected {ExpectedRulesetHashAlgoVersion}. " +
                $"This fingerprints the EffectCaps structural caps folded into MatchAgreementHash; a bump changes " +
                $"the handshake value. Update {nameof(ExpectedRulesetHashAlgoVersion)} in the same commit.");

            Assert.True(MatchAgreementHash.AlgoVersion == ExpectedMatchAgreementHashAlgoVersion,
                $"MatchAgreementHash.AlgoVersion is {MatchAgreementHash.AlgoVersion}, expected " +
                $"{ExpectedMatchAgreementHashAlgoVersion}. This is the single 64-bit start-state-agreement value on " +
                $"the widened Ready packet; a bump changes the handshake value. Update " +
                $"{nameof(ExpectedMatchAgreementHashAlgoVersion)} in the same commit.");

            Assert.True(ContentHash.AlgoVersion == ExpectedContentHashAlgoVersion,
                $"ContentHash.AlgoVersion is {ContentHash.AlgoVersion}, expected {ExpectedContentHashAlgoVersion}. " +
                $"This fingerprints the loaded content definitions folded into MatchAgreementHash; a bump changes " +
                $"the handshake value. Update {nameof(ExpectedContentHashAlgoVersion)} in the same commit.");
        }

        [Fact]
        public void WireAndReplayFormatStamps_ArePinned()
        {
            // These gate peer-to-peer and replay compatibility. A bump means an old build cannot interoperate; the
            // architecture's "move together" intent is that a wire change is a deliberate, coordinated event.
            Assert.True(TickCommandPacket.PROTOCOL_VERSION == ExpectedProtocolVersion,
                $"TickCommandPacket.PROTOCOL_VERSION is {TickCommandPacket.PROTOCOL_VERSION}, expected " +
                $"{ExpectedProtocolVersion}. A protocol bump must be matched by actual version-mismatch rejection " +
                $"in the Hello handshake (the D3.8 gap) and coordinated with peers. Update " +
                $"{nameof(ExpectedProtocolVersion)} here in the same commit as an intentional bump.");

            Assert.True(ReplayRecorder.VERSION == ExpectedReplayFormatVersion,
                $"ReplayRecorder.VERSION is {ReplayRecorder.VERSION}, expected {ExpectedReplayFormatVersion}. " +
                $"A replay-format bump must keep ReplayPlayer able to read (or explicitly reject) older versions. " +
                $"Update {nameof(ExpectedReplayFormatVersion)} here in the same commit.");
        }

        [Fact]
        public void ContentCompatibilityStamp_IsPinned()
        {
            // The packaging default that gates content/game-version compatibility on download. Today it is only
            // WRITTEN (the load-time enforcement is the unbuilt D3.1 CurrentGameVersion compare); pinning the
            // default keeps it from drifting before that enforcement lands.
            string actual = new ContentPackageManifest().MinGameVersion;
            Assert.True(actual == ExpectedManifestMinGameVersion,
                $"ContentPackageManifest.MinGameVersion default is '{actual}', expected " +
                $"'{ExpectedManifestMinGameVersion}'. This is the content↔game compatibility floor; when the D3.1 " +
                $"CurrentGameVersion load-gate is built it will compare against this. Update " +
                $"{nameof(ExpectedManifestMinGameVersion)} here in the same commit as an intentional change.");
        }

        /// <summary>
        /// Story 7.7 — the D3.1 scenario-format stamp LANDED (the former "still unbuilt" tripwire is inverted, as
        /// it demanded): <see cref="ScenarioData"/> carries <c>schema_version</c> + <c>checksum_algo_version</c>
        /// (absent ⇒ v1 amnesty), <c>ScenarioSerializer.Serialize</c> stamps the current values, and this registry
        /// pins them so neither can drift silently. Both stamps are EXCLUDED from <see cref="CanonicalModelHash"/>
        /// (a legacy re-save must not move the handshake).
        /// </summary>
        [Fact]
        public void SchemaVersionStamp_IsBuilt_AndPinned()
        {
            bool hasSchemaVersion = typeof(ScenarioData)
                .GetMembers(BindingFlags.Public | BindingFlags.Instance)
                .Any(m => (m.MemberType is MemberTypes.Property or MemberTypes.Field)
                          && (m.Name.Equals("SchemaVersion", StringComparison.OrdinalIgnoreCase)
                              || m.Name.Replace("_", "").Equals("schemaversion", StringComparison.OrdinalIgnoreCase)));
            Assert.True(hasSchemaVersion,
                "ScenarioData no longer carries a schema_version member — the D3.1 stamp landed in Story 7.7 and " +
                "must not silently vanish (legacy amnesty + future-reject depend on it).");

            Assert.True(ScenarioSerializer.CurrentSchemaVersion == ExpectedScenarioSchemaVersion,
                $"ScenarioSerializer.CurrentSchemaVersion is {ScenarioSerializer.CurrentSchemaVersion}, expected " +
                $"{ExpectedScenarioSchemaVersion}. This is the scenario-JSON format stamp Serialize writes; a bump " +
                $"needs a forward-compat migration/amnesty decision. Update {nameof(ExpectedScenarioSchemaVersion)} " +
                "here in the same commit as an intentional bump.");

            // checksum_algo_version stamps CanonicalModelHash.AlgoVersion — already pinned above, so just assert
            // the serializer actually writes both stamps (a legacy model gains them on save).
            var legacy = new ScenarioData { MapBounds = 120f };
            string json = ScenarioSerializer.Serialize(legacy);
            Assert.Contains($"\"schema_version\": {ExpectedScenarioSchemaVersion}", json);
            Assert.Contains($"\"checksum_algo_version\": {CanonicalModelHash.AlgoVersion}", json);
            Assert.Null(legacy.SchemaVersion);        // Serialize must not mutate the caller's model
            Assert.Null(legacy.ChecksumAlgoVersion);
        }

        /// <summary>
        /// Documentation guard — the unbuilt D3.1 stamps, recorded so this registry is the obvious home when they
        /// are built. Not an enforcement assertion (you cannot pin a value that does not exist); it exists so the
        /// list is carried in the test file, next to the pins, rather than only in the architecture doc.
        /// </summary>
        private static readonly string[] UnbuiltStamps =
        {
            "CurrentGameVersion — game/app semver constant; gates min_game_version at load (D3.1). No code home yet.",
            // schema_version (ScenarioData) — BUILT in Story 7.7 and pinned above (SchemaVersionStamp_IsBuilt_AndPinned).
        };

        [Fact]
        public void UnbuiltStamps_AreDocumented()
        {
            // A trivial assertion that simply keeps the UnbuiltStamps list referenced (so it is not dead code) and
            // documents the count. When a D3.1 stamp is built and pinned above, remove its line from UnbuiltStamps.
            Assert.True(UnbuiltStamps.Length == 1,
                $"Expected exactly 1 documented unbuilt D3.1 version stamp, found {UnbuiltStamps.Length}. When one " +
                $"is built and pinned into this registry, remove its entry from {nameof(UnbuiltStamps)} and update this count.");
        }
    }
}
