#nullable enable
using ProjectChimera.Multiplayer;          // HaltReason, TickCommandPacket.PROTOCOL_VERSION
using ProjectChimera.Multiplayer.Server;    // ServerLobbyPolicy
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// DW-397 / DW-398 — the slot-map-aware start-state-agreement gate overload (evaluated over the ACTUAL
    /// ready-player slot set, so a non-contiguous layout cannot read an unoccupied slot's default-0 hash and
    /// false-HALT) and the agreement-reset helper the server calls on the gate's fail branches (so a later
    /// re-Ready can never re-run the gate against stale payloads from an aborted start).
    /// </summary>
    public class StartStateAgreementSlotMapTests
    {
        private const ushort V = TickCommandPacket.PROTOCOL_VERSION;

        /// <summary>Arrays shaped like DedicatedServer's per-slot collection (MAX_SLOTS wide).</summary>
        private static (ulong[] hashes, ushort[] versions) FreshArrays(int slots = 8)
            => (new ulong[slots], new ushort[slots]);

        // ── DW-397: the sparse layout that false-HALTs under the dense gate is allowed by the slot-map gate ──

        [Fact]
        public void SparseReadySlots_AgreeingOnTheOccupiedSlots_Allow_WhereTheDenseGateFalseHalts()
        {
            var (hashes, versions) = FreshArrays();
            hashes[0] = 0xFEEDFACEUL; versions[0] = V;   // player at slot 0
            hashes[2] = 0xFEEDFACEUL; versions[2] = V;   // player at slot 2 — slot 1 UNOCCUPIED (default 0)

            // The slot-map-aware gate reads exactly the occupied slots → agreement → start allowed.
            Assert.Null(ServerLobbyPolicy.CheckStartStateAgreement(hashes, versions, new[] { 0, 2 }));

            // The dense [0, expected) read hits unoccupied slot 1's default payload (version 0 trips the
            // version-first check as ProtocolMismatch; its 0 hash would equally disagree) → the DW-397 false
            // HALT this overload exists to prevent. Pin the false BLOCK, not which reason fires first.
            Assert.NotNull(ServerLobbyPolicy.CheckStartStateAgreement(hashes, versions, 2));
        }

        [Fact]
        public void GarbageInAnUnlistedSlot_DoesNotAffectTheVerdict()
        {
            var (hashes, versions) = FreshArrays();
            hashes[0] = 0xABCDUL; versions[0] = V;
            hashes[2] = 0xABCDUL; versions[2] = V;
            hashes[1] = 0xDEADBEEFUL; versions[1] = (ushort)(V + 9); // stale junk in the unoccupied middle slot

            Assert.Null(ServerLobbyPolicy.CheckStartStateAgreement(hashes, versions, new[] { 0, 2 }));
        }

        // ── The fail-closed semantics carry over to the listed slots ──

        [Fact]
        public void DisagreeingHash_OnAListedSlot_Blocks()
        {
            var (hashes, versions) = FreshArrays();
            hashes[0] = 0x1111UL; versions[0] = V;
            hashes[2] = 0x2222UL; versions[2] = V;

            Assert.Equal(HaltReason.StartStateDisagreement,
                ServerLobbyPolicy.CheckStartStateAgreement(hashes, versions, new[] { 0, 2 }));
        }

        [Fact]
        public void ZeroHash_OnAListedSlot_Blocks()
        {
            var (hashes, versions) = FreshArrays();
            hashes[0] = 0xABCDUL; versions[0] = V;
            versions[2] = V; // slot 2 readied but its hash parsed to 0 (malformed Ready) → fail-closed

            Assert.Equal(HaltReason.StartStateDisagreement,
                ServerLobbyPolicy.CheckStartStateAgreement(hashes, versions, new[] { 0, 2 }));
        }

        [Fact]
        public void VersionSkew_OnAListedSlot_Blocks_CheckedBeforeHash()
        {
            var (hashes, versions) = FreshArrays();
            hashes[0] = 0x1111UL; versions[0] = V;
            hashes[2] = 0x2222UL; versions[2] = (ushort)(V + 1); // version skew AND hash disagreement

            // Version first — the skew is reported as ProtocolMismatch, not as a hash disagreement.
            Assert.Equal(HaltReason.ProtocolMismatch,
                ServerLobbyPolicy.CheckStartStateAgreement(hashes, versions, new[] { 0, 2 }));
        }

        [Fact]
        public void EmptySlotSet_Blocks()
        {
            var (hashes, versions) = FreshArrays();
            Assert.Equal(HaltReason.StartStateDisagreement,
                ServerLobbyPolicy.CheckStartStateAgreement(hashes, versions, new int[0]));
        }

        [Fact]
        public void SlotOutsideTheCollectedArrays_FailsClosed_WithoutThrowing()
        {
            var (hashes, versions) = FreshArrays(slots: 4);
            hashes[0] = 0xABCDUL; versions[0] = V;

            // Slot 9 has no attested payload (beyond the arrays) → fail-closed, never an index crash.
            Assert.Equal(HaltReason.StartStateDisagreement,
                ServerLobbyPolicy.CheckStartStateAgreement(hashes, versions, new[] { 0, 9 }));
            Assert.Equal(HaltReason.StartStateDisagreement,
                ServerLobbyPolicy.CheckStartStateAgreement(hashes, versions, new[] { 0, -1 }));
        }

        // ── DW-398: the agreement-fail branch wipes ALL per-slot agreement state ──

        [Fact]
        public void ResetAgreement_ClearsHashesVersionsAndReadyFlags()
        {
            var hashes   = new ulong[]  { 0xAAUL, 0xBBUL, 0xCCUL, 0UL };
            var versions = new ushort[] { V, V, (ushort)(V + 1), 0 };
            var ready    = new[]        { true, true, false, true };

            ServerLobbyPolicy.ResetAgreement(hashes, versions, ready);

            for (int s = 0; s < hashes.Length; s++)
            {
                Assert.Equal(0UL, hashes[s]);
                Assert.Equal(0, versions[s]);
                Assert.False(ready[s]);
            }

            // The wiped state is exactly what the gate must refuse: a re-run without a genuine fresh Ready
            // from every player stays fail-closed (wiped version 0 ≠ PROTOCOL_VERSION, wiped hash 0 disagrees),
            // never fail-open on stale data. Pin the BLOCK, not which fail-closed reason fires first.
            Assert.NotNull(ServerLobbyPolicy.CheckStartStateAgreement(hashes, versions, new[] { 0, 1 }));
        }
    }
}
