#nullable enable
namespace ProjectChimera.Multiplayer
{
    /// <summary>What a received lobby Hello means for the local client (DW-419/DW-420).</summary>
    public enum LobbyHelloKind
    {
        /// <summary>A dedicated server assigned our faction/slot — mark OUR slot; the server LobbyRoster fills the rest.</summary>
        AssignedPlayer,
        /// <summary>A P2P host confirmed the connection (Neutral faction, no role flags) — a 2-player match: mark
        /// slots 0+1 and offer Ready.</summary>
        P2pHostConfirm,
        /// <summary>A dedicated server seated us as a SPECTATOR (or sent a Neutral Hello on the dedicated path,
        /// which is never a P2P host confirmation) — render a spectator view: no slot-occupancy inference, no
        /// "Host confirmed — click Ready" prompt (the server drops a spectator's Ready anyway).</summary>
        DedicatedSpectator,
    }

    /// <summary>
    /// DW-419/DW-420 — the PURE client-side interpretation of a lobby Hello's (faction, roleFlags) pair, plus the
    /// lobby-chat local-echo decision that hangs off it. Godot-free; single-file-included into the Tier-1 test
    /// assembly like <c>HandshakeGate</c>/<c>DelayMath</c>. Extracted from <c>LobbyUi</c>'s Hello handler so the
    /// discriminator logic is unit-testable:
    ///
    ///   • DW-420: before the flags byte, EVERY Neutral-faction Hello was treated as a P2P host confirmation —
    ///     a dedicated server's spectator Hello (<c>MakeHello(Faction.Neutral)</c> to a slot ≥ ExpectedPlayers)
    ///     rendered a bogus 2-player "Host confirmed — click Ready" lobby. The
    ///     <see cref="TickCommandPacket.HELLO_FLAG_DEDICATED"/>/<see cref="TickCommandPacket.HELLO_FLAG_SPECTATOR"/>
    ///     flags disambiguate; <see cref="Classify"/> routes any flagged Neutral Hello to
    ///     <see cref="LobbyHelloKind.DedicatedSpectator"/>, never to <see cref="LobbyHelloKind.P2pHostConfirm"/>.
    ///
    ///   • DW-419: on the dedicated path the server re-stamps and rebroadcasts a <c>LobbyChat</c> to EVERY peer
    ///     including the sender, so the client's optimistic local echo rendered the sender's own line twice.
    ///     <see cref="ShouldLocalEchoLobbyChat"/> suppresses the local echo whenever the Hello carried any role
    ///     flag (only dedicated servers set flags; a P2P peer never rebroadcasts, so P2P keeps the echo).
    ///
    /// Pure functions: no logging, no side effects; <c>LobbyUi</c> acts on the returned values.
    /// </summary>
    public static class LobbyHelloPolicy
    {
        /// <summary>
        /// Classify a validated Hello (protocol version already checked by the caller, fail-closed) into what the
        /// lobby should render:
        ///   • non-Neutral faction, <see cref="TickCommandPacket.HELLO_FLAG_SPECTATOR"/> clear →
        ///     <see cref="LobbyHelloKind.AssignedPlayer"/> (a dedicated server assigned our slot);
        ///   • the spectator flag set → <see cref="LobbyHelloKind.DedicatedSpectator"/> (decisive — the explicit
        ///     seat classification wins even over a defensively-present faction byte);
        ///   • Neutral faction with <see cref="TickCommandPacket.HELLO_FLAG_DEDICATED"/> set →
        ///     <see cref="LobbyHelloKind.DedicatedSpectator"/> (a dedicated server never P2P-host-confirms, so a
        ///     Neutral Hello on the dedicated path must NOT render the 2-slot confirmation — the DW-420 defect);
        ///   • Neutral faction, no flags → <see cref="LobbyHelloKind.P2pHostConfirm"/> (the unchanged P2P path,
        ///     and the behavior-neutral reading of a legacy 4-byte Hello, whose flags parse as 0).
        /// </summary>
        public static LobbyHelloKind Classify(Core.Faction faction, byte roleFlags)
        {
            if ((roleFlags & TickCommandPacket.HELLO_FLAG_SPECTATOR) != 0)
                return LobbyHelloKind.DedicatedSpectator;
            if (faction != Core.Faction.Neutral)
                return LobbyHelloKind.AssignedPlayer;
            return (roleFlags & TickCommandPacket.HELLO_FLAG_DEDICATED) != 0
                ? LobbyHelloKind.DedicatedSpectator
                : LobbyHelloKind.P2pHostConfirm;
        }

        /// <summary>
        /// DW-419 — whether the client should append its OWN lobby-chat line locally when submitting.
        /// True only when no role flag was received (flags 0 = P2P, where no peer rebroadcasts and the optimistic
        /// echo is the only way the sender sees its line). Any flag means a dedicated server sent the Hello, and
        /// the dedicated LobbyChat relay re-stamps + rebroadcasts to every connected peer INCLUDING the sender —
        /// a local echo there renders the line twice. Pass the flags from the most recent Hello (0 before one
        /// arrives: the dedicated server's Hello is its first packet on connect, so chat before it is P2P-shaped).
        /// </summary>
        public static bool ShouldLocalEchoLobbyChat(byte helloRoleFlags)
            => (helloRoleFlags & (TickCommandPacket.HELLO_FLAG_DEDICATED | TickCommandPacket.HELLO_FLAG_SPECTATOR)) == 0;
    }
}
