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
    /// <c>SimChecksumCoverageGuardTest.KnownWorldState_ProducesPinnedV9Hash</c>. It is repeated here for a complete
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
        /// v9 (Story 2.12): folded the per-entity shift-queue order ring (count-driven) + the per-building rally point
        /// (D-1). v8 (Story 2.6): folded per-entity EffectiveArmor (the buffable armor stat). v7 (Story 2.4a): folded
        /// per-entity AbilityCooldownTicks (count-driven). v6 (Story 2.2b): Effective* / Energy / StatusFlagsOf +
        /// the ModifierStore instance state.</summary>
        private const int ExpectedSimChecksumAlgoVersion = 9;

        /// <summary>Load-time canonical start-state hash algorithm version (lobby handshake value).
        /// v3 (Story 2.9b follow-up): folded ScenarioPlayerSlot.StartCrystal (sim-affecting per-slot start-state).</summary>
        private const int ExpectedCanonicalModelHashAlgoVersion = 3;

        /// <summary>Lockstep Hello-handshake wire protocol version.</summary>
        private const ushort ExpectedProtocolVersion = 1;

        /// <summary>.chmr replay file-format version.</summary>
        private const ushort ExpectedReplayFormatVersion = 2;

        /// <summary>Default minimum game version a packaged .chimera.zip declares it requires.</summary>
        private const string ExpectedManifestMinGameVersion = "0.1";

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
        /// Forward tripwire for the one unbuilt stamp with a KNOWN home. Today <see cref="ScenarioData"/> carries no
        /// <c>schema_version</c>; when D3.1 adds it for forward-compat migrations, this test goes red — which is the
        /// signal to wire the new stamp into THIS registry (pin its expected value above) so it joins the
        /// "move together" checkpoint instead of floating unpinned. (CurrentGameVersion has no code home yet, so it
        /// is documented in <see cref="UnbuiltStamps"/> rather than tripwired.)
        /// </summary>
        [Fact]
        public void SchemaVersionStamp_IsStillUnbuilt_OrHasBeenWiredIntoThisRegistry()
        {
            bool hasSchemaVersion = typeof(ScenarioData)
                .GetMembers(BindingFlags.Public | BindingFlags.Instance)
                .Any(m => (m.MemberType is MemberTypes.Property or MemberTypes.Field)
                          && (m.Name.Equals("SchemaVersion", StringComparison.OrdinalIgnoreCase)
                              || m.Name.Replace("_", "").Equals("schemaversion", StringComparison.OrdinalIgnoreCase)));

            Assert.False(hasSchemaVersion,
                "ScenarioData now has a schema_version member — the D3.1 scenario-format versioning has landed. " +
                "Wire it into VersionStampConsistencyTests: add an Expected…SchemaVersion pin above and assert it " +
                "in a [Fact], so the new stamp joins the version-stamp consistency surface (it gates forward-compat " +
                "migration and must not drift silently). Then delete or invert this tripwire.");
        }

        /// <summary>
        /// Documentation guard — the unbuilt D3.1 stamps, recorded so this registry is the obvious home when they
        /// are built. Not an enforcement assertion (you cannot pin a value that does not exist); it exists so the
        /// list is carried in the test file, next to the pins, rather than only in the architecture doc.
        /// </summary>
        private static readonly string[] UnbuiltStamps =
        {
            "CurrentGameVersion — game/app semver constant; gates min_game_version at load (D3.1). No code home yet.",
            "schema_version (ScenarioData) — scenario-JSON format version for forward-compat migrations (D3.1). Tripwired above.",
        };

        [Fact]
        public void UnbuiltStamps_AreDocumented()
        {
            // A trivial assertion that simply keeps the UnbuiltStamps list referenced (so it is not dead code) and
            // documents the count. When a D3.1 stamp is built and pinned above, remove its line from UnbuiltStamps.
            Assert.True(UnbuiltStamps.Length == 2,
                $"Expected exactly 2 documented unbuilt D3.1 version stamps, found {UnbuiltStamps.Length}. When one " +
                $"is built and pinned into this registry, remove its entry from {nameof(UnbuiltStamps)} and update this count.");
        }
    }
}
