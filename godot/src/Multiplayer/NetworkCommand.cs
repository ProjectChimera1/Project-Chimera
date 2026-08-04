#nullable enable
using System;
using System.Runtime.InteropServices;
using ProjectChimera.Core;
using ProjectChimera.Combat;  // CombatEventQueue / CombatEventType (Story 2.12: OrderDenied on a full-ring reject)
using ProjectChimera.Economy; // BuildingSystem (Story 2.8: Train command applies at exec-tick; 2.12: SetRally)

namespace ProjectChimera.Multiplayer
{
    // ── Packet type discriminator ─────────────────────────────────────────────

    /// <summary>First byte of every packet on the wire.</summary>
    public enum PacketType : byte
    {
        /// <summary>Lobby handshake — host sends to confirm connection.</summary>
        Hello        = 0x01,
        /// <summary>Client signals it has loaded and is ready to start.</summary>
        Ready        = 0x02,
        /// <summary>Host broadcasts: both peers ready, start the sim on this tick.</summary>
        StartGame    = 0x03,
        /// <summary>Per-tick command bundle (UnitCommand orders for this tick).</summary>
        TickCommands = 0x10,
        /// <summary>
        /// Story 9.3: server → client ONLY. The authoritative merged tick — one packet per tick fanned in
        /// from every player's single-faction <see cref="TickCommands"/>, with faction re-stamped from the
        /// transport-authoritative slot and sub-bundles sorted ascending by faction id (ascending wire order
        /// IS the canonical apply order). Every peer + spectator applies these byte-identical bytes in the same
        /// order — the FR-39 determinism invariant that scales to N players. Wire layout: see
        /// <see cref="MergedTickPacket"/>. A packet of this type received FROM a client is hard-rejected.
        /// </summary>
        TickCommandsMerged = 0x14,
        /// <summary>World-state checksum for desync detection.</summary>
        Checksum     = 0x11,
        /// <summary>
        /// Server → a minority peer: the authoritative collector found this peer diverged from the
        /// strict-majority canonical hash. Wire: type(1) + tick(4 LE) + canonicalHash(4 LE) = 9 bytes.
        /// Server-GENERATED in 1.9a (no longer client-sent / opaquely relayed). See <see cref="TickCommandPacket.MakeDesyncAlert"/>.
        /// </summary>
        DesyncAlert  = 0x12,
        /// <summary>
        /// Server → everyone: no strict-majority canonical hash exists (global desync) — terminal HALT.
        /// Wire: type(1) + tick(4 LE) + reason(1) = 6 bytes. See <see cref="TickCommandPacket.MakeHalt"/>.
        /// </summary>
        Halt         = 0x13,
        /// <summary>
        /// Story 9.4: server → client ONLY. The authoritative server-dictated input-delay change — the server
        /// measures per-slot RTT, computes ONE delay for the whole match, and broadcasts this directive to every
        /// peer with the tick it takes effect at. The client re-clamps the delay to [MIN_DELAY, MAX_DELAY] on
        /// receipt, schedules the change via the existing apply-tick machinery, and replies with a
        /// <see cref="DelayAck"/>. Wire: type(1) + delay(1) + applyAtTick(4 LE) = 6 bytes.
        /// </summary>
        DelayDirective = 0x15,
        /// <summary>
        /// Story 9.6: server → client ONLY. A peer disconnected mid-match; the server dictates a deterministic
        /// freeze-and-continue for that faction's slot. Carries the dropped <see cref="Core.Faction"/> and the
        /// tick-counted <c>applyAtTick</c> (the informational "idle-from" marker) so every surviving client can
        /// surface it in UI and ACK it. The determinism truth is the merged stream (the server injects an empty
        /// command for the dropped slot each tick), NOT this marker. Wire: type(1) + faction(1) + applyAtTick(4 LE)
        /// = 6 bytes. ACK-gated exactly like <see cref="DelayDirective"/>.
        /// </summary>
        DropDirective = 0x16,
        /// <summary>
        /// In-game chat message.
        /// Wire format: type(1) + faction(1) + msgLen(2 LE) + msgBytes(UTF-8, max 200).
        /// Relayed by DedicatedServer to all connected peers.
        /// </summary>
        Chat          = 0x20,
        /// <summary>
        /// Story 9.7: PRE-MATCH lobby chat (no <see cref="LockstepManager"/> yet). Same wire layout as
        /// <see cref="Chat"/> — type(1) + faction(1) + msgLen(2 LE) + msgBytes(UTF-8, max 200) — but a DISTINCT type
        /// so it rides <c>ENetTransport</c> directly during the lobby (before the sim/lockstep stack exists) without
        /// colliding with the in-match Chat relay. Rendered in the lobby chat pane with faction color + name.
        /// </summary>
        LobbyChat     = 0x21,
        /// <summary>
        /// Story 9.7: server → client ONLY. The authoritative LOBBY roster/ready snapshot (pre-match), broadcast by
        /// <c>DedicatedServer</c> whenever a player slot's occupancy or readiness changes, so every client's N-slot
        /// lobby grid shows the true state of remote slots (which it cannot otherwise observe on the dedicated path).
        /// Wire: type(1) + slotCount(1) + slotCount bytes, one per slot (bit0 = occupied, bit1 = ready). Additive
        /// lobby-phase packet — it does NOT touch the merged envelope or PROTOCOL_VERSION.
        /// </summary>
        LobbyRoster   = 0x22,
        /// <summary>
        /// Story 11.4 (FR-74): a minimap map-ping (Alt-click). Presentation-only display cue — it rides the RELIABLE
        /// side-channel (like <see cref="Chat"/>), NOT the tick/checksum stream, so it folds NO sim state and needs no
        /// PROTOCOL_VERSION bump (an additive packet, ignored by an older build). The initiator shows its ping locally
        /// the instant it clicks; this replicates it to allies. Wire: type(1) + faction(1) + x(4 int LE) + z(4 int LE)
        /// = 10 bytes (world XZ rounded to whole units — a ping needs no sub-unit precision).
        /// </summary>
        MapPing       = 0x23,
        /// <summary>RTT probe sent by either peer. Wire: type(1) + seq(1) + senderMs(4 LE).</summary>
        Ping          = 0x40,
        /// <summary>RTT probe reply. Same wire format as Ping — echoes seq + senderMs back.</summary>
        Pong          = 0x41,
        /// <summary>
        /// Proposal to change the input-delay budget.
        /// Wire: type(1) + proposedDelay(1) + applyAtTick(4 LE).
        /// Both peers must agree before the change takes effect; see LockstepManager.
        /// </summary>
        DelayProposal = 0x42,
        /// <summary>
        /// Story 9.4: client → server. Acknowledges a server-dictated <see cref="DelayDirective"/>, echoing the
        /// (clamped) delay + applyAtTick so the server can confirm every player committed the same change. Same
        /// wire format as <see cref="DelayDirective"/>: type(1) + delay(1) + applyAtTick(4 LE) = 6 bytes.
        /// </summary>
        DelayAck      = 0x43,
        /// <summary>
        /// Story 9.6: client → server. A surviving player acknowledges a server-dictated
        /// <see cref="DropDirective"/>, echoing the (dropped) faction + applyAtTick so the server can confirm every
        /// survivor committed the same freeze before it begins injecting empty commands for the dropped slot. Same
        /// wire format as <see cref="DropDirective"/>: type(1) + faction(1) + applyAtTick(4 LE) = 6 bytes.
        /// </summary>
        DropAck       = 0x44,
    }

    // ── HALT reason ───────────────────────────────────────────────────────────

    /// <summary>
    /// Why the server issued a terminal <see cref="PacketType.Halt"/>. Byte-wide and extensible
    /// (ProtocolMismatch / StartStateDisagreement etc. arrive with the Epic-9 server-authority work).
    /// </summary>
    public enum HaltReason : byte
    {
        /// <summary>No strict-majority canonical hash for the executed tick — global desync.</summary>
        NoMajority = 0,
        /// <summary>Story 9.4: a peer's Ready carried a <see cref="TickCommandPacket.PROTOCOL_VERSION"/> that
        /// differs from the server's — the peers cannot interoperate. Fail-closed: the match never starts.</summary>
        ProtocolMismatch = 1,
        /// <summary>Story 9.4: the per-slot <see cref="TickCommandPacket.MakeReady"/> match-agreement hashes did
        /// NOT all agree on one non-zero value (a mismatched ruleset / initial-delay / roster / start-state, or a
        /// hash of 0). Fail-closed: the match never starts.</summary>
        StartStateDisagreement = 2,
    }

    // ── Per-unit order (11 bytes) ─────────────────────────────────────────────

    /// <summary>
    /// One unit command issued on a given tick.
    /// Serialised as 11 bytes: unitId(2) + command(1) + targetX(4) + targetZ(4).
    /// Y is not sent — the sim keeps units on the terrain height, determined locally.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct UnitOrder
    {
        /// <summary>Entity ID (fits in ushort — max 4096 entities).</summary>
        public readonly ushort UnitId;
        /// <summary>The command being issued.</summary>
        public readonly UnitCommand Command;
        /// <summary>World target X (Fixed raw int, 0 for Stop/Hold/Idle).</summary>
        public readonly int TargetX;
        /// <summary>World target Z (Fixed raw int, 0 for Stop/Hold/Idle).</summary>
        public readonly int TargetZ;

        public UnitOrder(int unitId, UnitCommand command, Fixed targetX, Fixed targetZ)
        {
            UnitId  = (ushort)unitId;
            Command = command;
            TargetX = targetX.Raw;
            TargetZ = targetZ.Raw;
        }

        public const int SIZE = 11; // bytes on the wire
    }

    // ── OrderApplier (the SINGLE deterministic command→world step) ──────────────

    /// <summary>
    /// Applies one <see cref="UnitOrder"/> to the <see cref="EntityWorld"/> — the deterministic command→world
    /// step shared verbatim by BOTH apply paths (<c>LockstepManager.ApplyOrders</c> live and
    /// <c>ReplayPlayer.ApplyOrders</c> on playback). Story 1.12 extracted this from the two formerly-duplicated
    /// switches so they can NEVER diverge: a command handled in one path but not the other is a guaranteed
    /// replay-vs-live desync (AR-17 / the epic's #1 trap), and that whole class of bug is now structurally
    /// impossible — there is exactly one switch.
    ///
    /// Godot-free (System.* + Core only), so it compiles into the Tier-1 test assembly and the parity is unit-
    /// testable directly (CommandApplyParityTests). The path-request delegates are PRESENTATION hooks (flow-field
    /// smoothing wired by MainScene) — null in the headless/golden/replay paths; the deterministic sim truth is
    /// always the MoveTarget + Moving/Attacking flags and the SoA fields set here.
    /// </summary>
    public static class OrderApplier
    {
        /// <summary>
        /// Apply <paramref name="o"/> to <paramref name="world"/> for the unit it names. No-ops if that unit is
        /// dead or not owned by <paramref name="expectedFaction"/> (the anti-cheat guard both paths share).
        /// </summary>
        public static void Apply(EntityWorld world, in UnitOrder o, Faction expectedFaction,
            Action<int, float, float>? onRequestPath = null,
            Action<int, float, float>? onRequestAttackMove = null,
            Action<int>? onCancelPath = null,
            BuildingSystem? buildings = null,
            CombatEventQueue? events = null,
            ItemSystem? items = null,
            ResearchSystem? research = null,
            // Story 7.9 — a narrow handle to ScenarioDirector.TryEnqueueExternalDslEvent (eventIndex, raiserSlot,
            // arg0, arg1) → bool. Null on golden/headless/replay-without-a-director paths → a DslEvent order is a
            // deterministic no-op (exactly like `buildings`/`items`/`research` == null for their commands).
            Func<int, int, int, int, bool>? dslSink = null,
            // Story 11.2 (FR-66) — the folded WinStateStore handle for a Concede order (which names a faction, not an
            // entity). Null on golden/headless/replay-without-win-state paths → a Concede is a deterministic no-op
            // (goldens never issue Concede=22, so the dormant command changes no golden — the checksum-fold-timing
            // rule). Threaded through BOTH live and replay apply paths (the one-switch parity rule).
            WinStateStore? winState = null)
        {
            // Story 2.12 (Decision #2): mask the wire's queued flag (0x80) off the Command byte FIRST, so every
            // downstream compare + the command→state switch sees only the real 0-13 UnitCommand — never a flagged
            // 0x80|cmd value (which is not a valid enum member and must never reach CommandState or SimChecksum).
            // `queued` then decides append-vs-replace. A rally/train byte is never Shift-issued, so masking is a
            // harmless no-op for them (the flag is off), but we mask before the early-returns for uniform safety.
            var cmd    = (UnitCommand)((byte)o.Command & UnitOrderFlags.CommandMask);
            bool queued = ((byte)o.Command & UnitOrderFlags.Queued) != 0;

            // Story 2.8 (D-1): Train names a BUILDING (UnitId = buildingId), not an entity — handle it BEFORE the
            // entity-ownership guard below, which would misread the building id as an EntityWorld slot (rejecting or
            // corrupting whatever entity shares that index) and clobber its CommandState. The building-ownership
            // guard + the deterministic exec-tick ore/supply spend live in BuildingSystem.TrainUnitCommand.
            // `buildings` is null on headless/golden/replay-without-buildings paths → Train is a deterministic
            // no-op (goldens never train via the wire). TargetX carries the chosen unit index as a RAW int (read
            // directly; NEVER via .ToFloat() — the 1.12/2.4a packed-int lesson).
            if (cmd == UnitCommand.Train)
            {
                // Story 11.4 (FR-74): thread the presentation event sink so TrainUnit's afford/prereq/supply/queue-full
                // rejections surface a guard-sourced OrderDenied cue. `events` null (golden/headless/replay) → silent,
                // deterministic no-op (the queue is not a SimChecksum input).
                buildings?.TrainUnitCommand(o.UnitId, expectedFaction, o.TargetX, events);
                return;
            }

            // Story 11.6 (FR-74): CancelTrain ALSO names a BUILDING (UnitId = buildingId) — handle it beside Train,
            // BEFORE the entity-ownership guard, for the same reason (the building id must not be misread as an
            // EntityWorld slot). TargetX carries the queue slot index as a RAW int (0-4; read directly, NEVER via
            // .ToFloat() — the packed-int lesson). BuildingSystem.CancelTrainCommand does the building-ownership
            // anti-cheat + slot guards and the deterministic exec-tick 100% refund + remove/shift. NEVER persists as a
            // CommandState. `buildings` null → deterministic no-op (golden/replay-without-buildings paths, like Train).
            if (cmd == UnitCommand.CancelTrain)
            {
                buildings?.CancelTrainCommand(o.UnitId, expectedFaction, o.TargetX, events);
                return;
            }

            // Story 2.12 (AC3): SetRally ALSO names a BUILDING (UnitId = buildingId) — handle it beside Train, BEFORE
            // the entity guard, for the same reason. The rally point rides TargetX/TargetZ as Fixed raw (the Move
            // encoding); BuildingSystem.SetRallyCommand does the bounds + Alive + faction anti-cheat check then writes
            // the store. It NEVER persists as a CommandState. `buildings` null → deterministic no-op (golden/replay).
            if (cmd == UnitCommand.SetRally)
            {
                buildings?.SetRallyCommand(o.UnitId, expectedFaction, Fixed.FromRaw(o.TargetX), Fixed.FromRaw(o.TargetZ));
                return;
            }

            // Story 3.14 (D-4): ReviveHero ALSO names a BUILDING (UnitId = buildingId) — handle it beside Train/SetRally,
            // BEFORE the entity guard, for the same reason. The awaiting-hero slot rides TargetX as a RAW int (read
            // directly, NEVER via .ToFloat()). BuildingSystem.ReviveHeroCommand does the building-ownership + capability
            // + affordability guards and the deterministic exec-tick spend + countdown start. It NEVER persists as a
            // CommandState. `buildings` null → deterministic no-op (golden/replay-without-buildings paths).
            if (cmd == UnitCommand.ReviveHero)
            {
                buildings?.ReviveHeroCommand(o.UnitId, expectedFaction, Fixed.FromRaw(o.TargetX), events);
                return;
            }

            // Story 3.16: BuyItem ALSO names a SHOP BUILDING (UnitId = buildingId) — handle it beside Train/Revive, BEFORE
            // the entity guard, for the same reason. The stock index rides TargetX and the buying hero entity id rides
            // TargetZ, both RAW ints (read directly, NEVER via .ToFloat()). BuildingSystem.BuyItemCommand does the
            // building-ownership + sells_items + stock/proximity/free-slot/affordability guards and the atomic exec-tick
            // spend + mint. NEVER persists as a CommandState. `buildings`/`items` null ⇒ deterministic no-op (golden/replay).
            if (cmd == UnitCommand.BuyItem)
            {
                buildings?.BuyItemCommand(o.UnitId, expectedFaction, o.TargetX, o.TargetZ, items, events);
                return;
            }

            // Story 4.9: StartResearch/CancelResearch ALSO name a BUILDING (UnitId = buildingId) — handle them beside
            // Train/SetRally/ReviveHero/BuyItem, BEFORE the entity guard, for the same reason. StartResearch's TargetX
            // carries the chosen research's index into FactionDefinition.Research as a RAW int (read directly, NEVER
            // via .ToFloat()); CancelResearch's TargetX is unused/reserved. ResearchSystem.StartResearchCommand/
            // CancelResearchCommand do the building-ownership + gate/refund guards and the deterministic exec-tick
            // spend. NEVER persists as a CommandState. `research` null ⇒ deterministic no-op (golden/replay-without-
            // research paths, like `buildings`/`items`).
            if (cmd == UnitCommand.StartResearch)
            {
                research?.StartResearchCommand(o.UnitId, expectedFaction, o.TargetX);
                return;
            }
            if (cmd == UnitCommand.CancelResearch)
            {
                research?.CancelResearchCommand(o.UnitId, expectedFaction);
                return;
            }

            // Story 7.9 (custom-UI write rail): a DslEvent order names a custom-event REGISTRY INDEX (UnitId), not an
            // entity — handle it beside Train/Research, BEFORE the entity-ownership guard (which would misread the
            // event index as an EntityWorld slot). The wire packs eventIndex→UnitId, arg0→TargetX.Raw, arg1→TargetZ.Raw
            // (read directly as raws, NEVER via .ToFloat() — the packed-int lesson). The RAISER is the command's own
            // faction (the anti-cheat truth: a peer can only inject into its own command stream), converted to the
            // 0-based faction SLOT the event's allowed_raisers use (Player1→0). The sim-side gate authorizes it
            // deterministically on every peer + in replay; `dslSink` null ⇒ deterministic no-op (golden/replay). A
            // Neutral raiser (spectator stream — never carries DslEvents) is dropped rather than mapped to −1/system.
            if (cmd == UnitCommand.DslEvent)
            {
                if (dslSink == null || expectedFaction == Faction.Neutral) return;
                dslSink(o.UnitId, (int)expectedFaction - 1, o.TargetX, o.TargetZ);
                return;
            }

            // Story 11.2 (FR-66): Concede NAMES A FACTION (the command's expectedFaction — the anti-cheat truth: a peer
            // can only concede its own faction), NOT an entity — handle it beside DslEvent, BEFORE the entity-ownership
            // guard (there is no entity to guard; the UnitId is unused/reserved). It latches the ALREADY-FOLDED
            // WinStateStore.Verdict[expectedFaction]=VERDICT_LOST only when currently VERDICT_NONE (monotone — a
            // re-concede or an already-resolved faction is a deterministic no-op, mirroring the store's own
            // never-overwrite rule). WinConditionSystem's next ApplyLastTeamStanding then awards the sole remaining live
            // team (AnyLost() is now true). `winState` null ⇒ deterministic no-op (golden/headless/replay-without-win-
            // state), exactly like `buildings`/`dslSink` == null for their commands. A Neutral raiser (spectator stream —
            // never carries a Concede) is dropped. No new folded store, no golden re-baseline.
            if (cmd == UnitCommand.Concede)
            {
                if (winState != null && expectedFaction != Faction.Neutral
                    && winState.Verdict[(int)expectedFaction] == WinStateStore.VERDICT_NONE)
                    winState.Verdict[(int)expectedFaction] = WinStateStore.VERDICT_LOST;
                return;
            }

            int id = o.UnitId;
            if (!world.IsAlive(id)) return;
            if (world.FactionOf[id] != expectedFaction) return; // anti-cheat: only command your own units

            // Story 3.15: UseItem / DropItem name the HERO ENTITY (== id, not a building like Train/Revive), so they are
            // gated by the ownership guard ABOVE — NOT dispatched by the building-command pre-guard pattern. Placing them
            // AFTER the guard prevents a player from forcing an ENEMY hero to use/drop items (anti-cheat). ItemSystem then
            // does its own hero-resolution + validation. The inventory slot rides TargetX as a RAW int (read directly,
            // NEVER via .ToFloat()). `items` null → deterministic no-op (golden/replay-without-items paths), exactly like
            // `buildings == null`. They never persist as a CommandState (each returns). PickupItem is different: it DOES
            // persist as a CommandState, so it rides ApplyActiveOrder (below) through this same guard.
            if (cmd == UnitCommand.UseItem)
            {
                items?.UseItemCommand(id, o.TargetX, events);
                return;
            }
            if (cmd == UnitCommand.DropItem)
            {
                items?.DropItemCommand(id, o.TargetX, events);
                return;
            }

            // Story 2.12 (AC1.2): a Shift-issued (queued) order APPENDS to the entity's order ring and does NOT touch
            // CommandState — OrderQueueSystem pops + dispatches it when the active order completes. A plain (non-flagged)
            // order CLEARS the ring (replace semantics) then applies immediately through the shared core, so a fresh
            // plain command always wipes any stale queued orders (WC3 behaviour).
            if (queued)
            {
                AppendOrder(world, id, cmd, o.TargetX, o.TargetZ, events);
                return;
            }
            world.OrderQueueCount[id] = 0; // plain (replace) — discard any pending queued orders before applying
            ApplyActiveOrder(world, id, cmd, o.TargetX, o.TargetZ, onRequestPath, onRequestAttackMove, onCancelPath);
        }

        /// <summary>
        /// Apply an ACTIVE (non-queued) order to a live, owned entity — the SINGLE command→<c>CommandState</c> mapping
        /// shared by the wire-entry <see cref="Apply"/> (plain/replace path) AND <c>OrderQueueSystem</c> (queue
        /// pop→dispatch). Extracted in Story 2.12 (Decision #4) so a popped queued order and a directly-issued one can
        /// NEVER diverge (the 1.12 one-switch rule — a second dispatch path is the epic's #1 desync trap). Callers
        /// guarantee <paramref name="id"/> is alive and owned; <paramref name="cmd"/> is the MASKED 0-13 command (a
        /// flagged 0x80 byte must never reach here). The pop path calls THIS directly (never the flag-wrapping
        /// <see cref="Apply"/>), so dispatching queued order 1 of N does not re-clear the ring. The path-request
        /// delegates are presentation hooks — null on the pop and all headless/golden/replay paths.
        /// </summary>
        public static void ApplyActiveOrder(EntityWorld world, int id, UnitCommand cmd, int targetX, int targetZ,
            Action<int, float, float>? onRequestPath = null,
            Action<int, float, float>? onRequestAttackMove = null,
            Action<int>? onCancelPath = null)
        {
            UnitCommand prior = world.CommandState[id]; // captured for CastAbility's restore (a cast must not clobber the order)
            world.CommandState[id] = cmd;

            switch (cmd)
            {
                case UnitCommand.Move:
                {
                    var target = new FixedVec3(Fixed.FromRaw(targetX), Fixed.Zero, Fixed.FromRaw(targetZ));
                    world.CommandGoal[id]  = target;
                    world.MoveTarget[id]   = target;
                    world.Flags[id]        = (world.Flags[id] | EntityFlags.Moving) & ~EntityFlags.Attacking;
                    world.AttackTarget[id] = -1;
                    ClearPatrolRoute(world, id);
                    onRequestPath?.Invoke(id, Fixed.FromRaw(targetX).ToFloat(), Fixed.FromRaw(targetZ).ToFloat());
                    break;
                }
                case UnitCommand.AttackMove:
                {
                    var target = new FixedVec3(Fixed.FromRaw(targetX), Fixed.Zero, Fixed.FromRaw(targetZ));
                    world.CommandGoal[id]  = target;
                    world.MoveTarget[id]   = target;
                    world.Flags[id]        = (world.Flags[id] | EntityFlags.Moving) & ~EntityFlags.Attacking;
                    world.AttackTarget[id] = -1;
                    ClearPatrolRoute(world, id);
                    onRequestAttackMove?.Invoke(id, Fixed.FromRaw(targetX).ToFloat(), Fixed.FromRaw(targetZ).ToFloat());
                    break;
                }
                case UnitCommand.Stop:
                case UnitCommand.HoldPosition:
                {
                    world.Flags[id]        = world.Flags[id] & ~(EntityFlags.Moving | EntityFlags.Attacking);
                    world.AttackTarget[id] = -1;
                    ClearPatrolRoute(world, id);
                    onCancelPath?.Invoke(id);
                    break;
                }
                case UnitCommand.AttackTarget:
                {
                    // The forced enemy id rides in TargetX as a RAW int (Fixed.FromRaw at issue time) — read it
                    // back directly, never via .ToFloat() (that path is float and would corrupt the id). Seed the
                    // transient AttackTarget too; CombatSystem.TickAttackTargetCombat drives MoveTarget each tick
                    // (the target moves), so we do NOT request a one-shot path here.
                    world.CommandTarget[id] = targetX;
                    world.AttackTarget[id]  = targetX;
                    world.Flags[id]        &= ~EntityFlags.Attacking;
                    ClearPatrolRoute(world, id);
                    break;
                }
                case UnitCommand.AttackBuilding:
                {
                    // Story 2.9a: the enemy BUILDING id rides in TargetX as a RAW int (Fixed.FromRaw at issue). Blind-store
                    // it in CommandTarget (disambiguated by CommandState==AttackBuilding, exactly like AttackTarget/Follow
                    // share CommandTarget) — CombatSystem.TickAttackBuildingCombat validates it (bounds/Alive/friendly/
                    // Structure-domain) in-tick. Set AttackTarget[id] = -1: a building id must NEVER enter the entity-space
                    // AttackTarget[] array (that would make world.IsAlive/FactionOf misread a building id as an entity).
                    world.CommandTarget[id] = targetX;
                    world.AttackTarget[id]  = -1;
                    world.Flags[id]        &= ~EntityFlags.Attacking;
                    ClearPatrolRoute(world, id);
                    break;
                }
                case UnitCommand.Follow:
                {
                    // Friendly id packed in TargetX as a raw int. CombatSystem.TickFollowCombat drives movement.
                    world.CommandTarget[id] = targetX;
                    world.Flags[id]        &= ~EntityFlags.Attacking;
                    ClearPatrolRoute(world, id);
                    break;
                }
                case UnitCommand.Patrol:
                {
                    // START a fresh route: leg 0 = current position (the return anchor), leg 1 = the clicked
                    // point. PatrolIndex=1 heads toward the clicked point first; dir=+1. (N=2 is the classic
                    // ping-pong.) TargetX/TargetZ are a Fixed.Raw ground point, exactly like Move.
                    int baseIdx = id * EntityWorld.MAX_PATROL_WAYPOINTS;
                    var clicked = new FixedVec3(Fixed.FromRaw(targetX), Fixed.Zero, Fixed.FromRaw(targetZ));
                    world.PatrolWaypoints[baseIdx + 0] = world.Position[id];
                    world.PatrolWaypoints[baseIdx + 1] = clicked;
                    world.PatrolCount[id]  = 2;
                    world.PatrolIndex[id]  = 1;
                    world.PatrolDir[id]    = 1;
                    world.CommandGoal[id]  = clicked;
                    world.MoveTarget[id]   = clicked;
                    world.Flags[id]        = (world.Flags[id] | EntityFlags.Moving) & ~EntityFlags.Attacking;
                    world.AttackTarget[id] = -1;
                    break;
                }
                case UnitCommand.PatrolAppend:
                {
                    // APPEND a waypoint to the far end of an existing route, WITHOUT disturbing the current leg
                    // (PatrolIndex/MoveTarget untouched). If not already patrolling, behave like a fresh Patrol.
                    // Appends past the cap are silently ignored (deterministic no-op). Then force CommandState
                    // back to Patrol (overriding the cmd set above) so CombatSystem never sees PatrolAppend.
                    int baseIdx = id * EntityWorld.MAX_PATROL_WAYPOINTS;
                    var clicked = new FixedVec3(Fixed.FromRaw(targetX), Fixed.Zero, Fixed.FromRaw(targetZ));
                    int n = world.PatrolCount[id];
                    if (n >= 2 && n < EntityWorld.MAX_PATROL_WAYPOINTS)
                    {
                        world.PatrolWaypoints[baseIdx + n] = clicked;
                        world.PatrolCount[id]              = (byte)(n + 1);
                    }
                    else if (n < 2)
                    {
                        // Not already patrolling → fresh route, identical to the Patrol case.
                        world.PatrolWaypoints[baseIdx + 0] = world.Position[id];
                        world.PatrolWaypoints[baseIdx + 1] = clicked;
                        world.PatrolCount[id]  = 2;
                        world.PatrolIndex[id]  = 1;
                        world.PatrolDir[id]    = 1;
                        world.CommandGoal[id]  = clicked;
                        world.MoveTarget[id]   = clicked;
                        world.Flags[id]        = (world.Flags[id] | EntityFlags.Moving) & ~EntityFlags.Attacking;
                        world.AttackTarget[id] = -1;
                    }
                    // else: route is full — silently ignore the extra waypoint.
                    world.CommandState[id] = UnitCommand.Patrol;
                    break;
                }
                case UnitCommand.PickupItem:
                {
                    // Story 3.15: the target ground-item's PACKED ItemStore ref rides in TargetX as a RAW int (read
                    // directly, NEVER via .ToFloat() — the AttackTarget/packed-int lesson). Blind-store it in CommandTarget
                    // (disambiguated by CommandState==PickupItem, like AttackTarget/Follow/AttackBuilding share it);
                    // ItemSystem resolves it each tick, drives MoveTarget toward the item, and claims on proximity. Set
                    // Moving; hold at the current position until ItemSystem redirects (it runs after MovementSystem, so the
                    // first steer lands next tick). AttackTarget = -1: an item ref must never enter the entity-space array.
                    world.CommandTarget[id] = targetX;
                    world.MoveTarget[id]    = world.Position[id];
                    world.Flags[id]         = (world.Flags[id] | EntityFlags.Moving) & ~EntityFlags.Attacking;
                    world.AttackTarget[id]  = -1;
                    ClearPatrolRoute(world, id);
                    break;
                }
                case UnitCommand.CastAbility:
                {
                    // Story 2.4a (FR-11): a fire-and-forget cast INTENT. The ability slot rides in TargetX (raw int,
                    // packed at issue time via Fixed.FromRaw(slot)) and the target entity id in TargetZ (raw int,
                    // -1 = Self/None) — read back directly as targetX/targetZ, NEVER via .ToFloat() (that path is
                    // float and would corrupt the packed int — the 1.12 AttackTarget lesson). We write ONLY the
                    // pending-cast SoA; the effect graph runs in AbilityCastSystem INSIDE the tick. Running it here
                    // would advance the shared SimRng / query a stale SpatialHash off-tick (OrderApplier runs at input
                    // time, OUTSIDE the tick on the offline path) → desync vs the online/replay path. An instant cast
                    // must not interrupt the unit's order, so restore CommandState to its prior value (mirrors how
                    // PatrolAppend rewrites CommandState back to Patrol above).
                    world.CommandState[id] = prior;
                    int slot = targetX;
                    if (slot < 0 || slot >= world.AbilityCount[id]) break; // no such slot → deterministic no-op
                    world.PendingCastSlot[id]   = (byte)slot;
                    world.PendingCastTarget[id] = targetZ;
                    break;
                }
            }

            // Story 2.12 (R1 fix): stamp the sim-authoritative ACTIVE order = the FINAL CommandState (post the
            // CastAbility "restore prior" and PatrolAppend "rewrite to Patrol" cases). OrderQueueSystem reads this,
            // NOT CommandState, so a presentation Move→Stop flip can never strand queued orders. Presentation never
            // writes ActiveOrderCmd, so it holds the true active order until the next deterministic dispatch.
            world.ActiveOrderCmd[id] = (byte)world.CommandState[id];
        }

        /// <summary>
        /// Append a Shift-queued order to the entity's ring (Story 2.12, AC1.2 / AC4). Rejects DETERMINISTICALLY when
        /// the ring is already full (<see cref="EntityWorld.OrderQueueCount"/> == <see cref="EntityWorld.MAX_ORDER_QUEUE"/>):
        /// no append, no overflow, no crash — state unchanged. On the paths that wire a live <paramref name="events"/>
        /// sink it also pushes a presentation-only <see cref="CombatEventType.OrderDenied"/> at the entity's position
        /// (Story 11.9 consumes it). The reject DECISION reads the FOLDED OrderQueueCount, so every peer rejects
        /// identically and a null sink (replay) rejects the same way — only the visual feedback is skipped. Called ONLY
        /// from the single wire-entry <see cref="Apply"/> (never the pop path), so appending never touches CommandState.
        /// </summary>
        private static void AppendOrder(EntityWorld world, int id, UnitCommand cmd, int targetX, int targetZ,
            CombatEventQueue? events)
        {
            int count = world.OrderQueueCount[id];
            if (count >= EntityWorld.MAX_ORDER_QUEUE)
            {
                // Story 11.4 (FR-74): stamp the commanding unit's faction + a QueueFull reason so MatchAlertBridge
                // surfaces the ring-full reject as a local denial cue (presentation-only; the deterministic reject is
                // still the folded OrderQueueCount, so a null sink / replay rejects identically).
                events?.PushDenied(world.Position[id], world.FactionOf[id], DenialReason.QueueFull);
                return; // ring full — deterministic reject (mirrors PatrolAppend's "route full → ignore", plus the event)
            }
            int slot = id * EntityWorld.MAX_ORDER_QUEUE + count;
            world.OrderQueueCmd[slot]     = (byte)cmd; // the MASKED 0-13 command (the 0x80 wire flag is already stripped)
            world.OrderQueueTargetX[slot] = targetX;
            world.OrderQueueTargetZ[slot] = targetZ;
            world.OrderQueueCount[id]     = (byte)(count + 1);
        }

        /// <summary>
        /// Reset the patrol-route ring to "no route" (Review, Story 1.12). Called whenever a unit is given a
        /// NON-patrol command, so a later <see cref="UnitCommand.PatrolAppend"/> (whose "already patrolling?"
        /// gate keys on <c>PatrolCount</c>) cannot resurrect an abandoned route, and the v4 checksum stops
        /// folding a dead route the unit no longer walks. Patrol/PatrolAppend own this state and do NOT call it.
        /// </summary>
        private static void ClearPatrolRoute(EntityWorld world, int id)
        {
            world.PatrolCount[id] = 0;
            world.PatrolIndex[id] = 0;
            world.PatrolDir[id]   = 1;
        }
    }

    // ── TickCommandPacket ──────────────────────────────────────────────────────

    /// <summary>
    /// All orders a player issues on a single tick.
    /// Wire format: PacketType(1) + tick(4) + faction(1) + orderCount(1) + orders(11 each).
    /// Max 32 orders per tick (352 bytes total worst-case) — well under 1 MTU.
    /// </summary>
    public static class TickCommandPacket
    {
        public const int MAX_ORDERS   = 32;
        public const int HEADER_BYTES = 7;  // type(1) + tick(4) + faction(1) + count(1)

        /// <summary>Serialise a tick command packet into <paramref name="buf"/>.</summary>
        /// <returns>Number of bytes written.</returns>
        public static int Write(byte[] buf, uint tick, Faction faction, UnitOrder[] orders, int orderCount)
            => Write(buf, tick, faction, orders, 0, orderCount);

        /// <summary>
        /// Serialise a tick command packet from a flat buffer slice starting at <paramref name="baseIdx"/>.
        /// </summary>
        /// <returns>Number of bytes written.</returns>
        public static int Write(byte[] buf, uint tick, Faction faction,
                                UnitOrder[] orders, int baseIdx, int orderCount)
        {
            if (orderCount > MAX_ORDERS) orderCount = MAX_ORDERS;
            int totalBytes = HEADER_BYTES + orderCount * UnitOrder.SIZE;
            if (buf.Length < totalBytes)
                throw new ArgumentException($"Buffer too small: need {totalBytes}, got {buf.Length}");

            int pos = 0;
            buf[pos++] = (byte)PacketType.TickCommands;

            // tick (4 bytes, little-endian)
            buf[pos++] = (byte)(tick);
            buf[pos++] = (byte)(tick >> 8);
            buf[pos++] = (byte)(tick >> 16);
            buf[pos++] = (byte)(tick >> 24);

            buf[pos++] = (byte)faction;
            buf[pos++] = (byte)orderCount;

            for (int i = 0; i < orderCount; i++)
            {
                var o = orders[baseIdx + i];
                buf[pos++] = (byte)(o.UnitId);
                buf[pos++] = (byte)(o.UnitId >> 8);
                buf[pos++] = (byte)o.Command;
                WriteInt(buf, ref pos, o.TargetX);
                WriteInt(buf, ref pos, o.TargetZ);
            }

            return totalBytes;
        }

        /// <summary>
        /// Parse a TickCommands packet. Returns false if the buffer is malformed.
        /// </summary>
        public static bool TryRead(byte[] buf, int len, out uint tick, out Faction faction,
                                   UnitOrder[] outOrders, out int orderCount)
        {
            tick = 0; faction = Faction.Neutral; orderCount = 0;
            if (len < HEADER_BYTES) return false;
            if ((PacketType)buf[0] != PacketType.TickCommands) return false;

            int pos = 1;
            tick = ReadUint(buf, ref pos);
            faction = (Faction)buf[pos++];
            orderCount = buf[pos++];

            if (orderCount > MAX_ORDERS) return false;
            if (len < HEADER_BYTES + orderCount * UnitOrder.SIZE) return false;

            for (int i = 0; i < orderCount; i++)
            {
                ushort unitId = (ushort)(buf[pos] | (buf[pos + 1] << 8)); pos += 2;
                var command = (UnitCommand)buf[pos++];
                int tx = ReadInt(buf, ref pos);
                int tz = ReadInt(buf, ref pos);
                outOrders[i] = new UnitOrder(unitId, command, Fixed.FromRaw(tx), Fixed.FromRaw(tz));
            }

            return true;
        }

        // ── ChecksumPacket helpers ─────────────────────────────────────────────

        /// <summary>Serialise a checksum packet (9 bytes).</summary>
        public static int WriteChecksum(byte[] buf, uint tick, uint checksum)
        {
            int pos = 0;
            buf[pos++] = (byte)PacketType.Checksum;
            WriteUint(buf, ref pos, tick);
            WriteUint(buf, ref pos, checksum);
            return pos; // 9
        }

        /// <summary>Parse a checksum packet.</summary>
        public static bool TryReadChecksum(byte[] buf, int len, out uint tick, out uint checksum)
        {
            tick = 0; checksum = 0;
            if (len < 9) return false;
            if ((PacketType)buf[0] != PacketType.Checksum) return false;
            int pos = 1;
            tick = ReadUint(buf, ref pos);
            checksum = ReadUint(buf, ref pos);
            return true;
        }

        // ── DesyncAlert / Halt helpers (server-generated, Story 1.9a) ──────────

        /// <summary>
        /// Server → a minority peer. Wire: type(1) + tick(4 LE) + canonicalHash(4 LE) = 9 bytes
        /// (mirrors <see cref="WriteChecksum"/>). 32-bit width — the wire is never widened to ulong (D12).
        /// </summary>
        public static byte[] MakeDesyncAlert(uint tick, uint canonicalHash)
        {
            var b = new byte[9];
            int p = 0;
            b[p++] = (byte)PacketType.DesyncAlert;
            WriteUint(b, ref p, tick);
            WriteUint(b, ref p, canonicalHash);
            return b;
        }

        /// <summary>Parse a DesyncAlert packet. Returns false if malformed/truncated.</summary>
        public static bool TryReadDesyncAlert(byte[] buf, int len, out uint tick, out uint canonicalHash)
        {
            tick = 0; canonicalHash = 0;
            if (len < 9) return false;
            if ((PacketType)buf[0] != PacketType.DesyncAlert) return false;
            int pos = 1;
            tick = ReadUint(buf, ref pos);
            canonicalHash = ReadUint(buf, ref pos);
            return true;
        }

        /// <summary>
        /// Server → everyone, on no strict-majority canonical hash. Wire: type(1) + tick(4 LE) + reason(1) = 6 bytes.
        /// Terminal HALT (recovery/rejoin is deferred post-1.0).
        /// </summary>
        public static byte[] MakeHalt(uint tick, HaltReason reason)
        {
            var b = new byte[6];
            int p = 0;
            b[p++] = (byte)PacketType.Halt;
            WriteUint(b, ref p, tick);
            b[p] = (byte)reason;
            return b;
        }

        /// <summary>Parse a Halt packet. Returns false if malformed/truncated.</summary>
        public static bool TryReadHalt(byte[] buf, int len, out uint tick, out HaltReason reason)
        {
            tick = 0; reason = default;
            if (len < 6) return false;
            if ((PacketType)buf[0] != PacketType.Halt) return false;
            int pos = 1;
            tick = ReadUint(buf, ref pos);
            reason = (HaltReason)buf[pos];
            return true;
        }

        // ── Lobby packet helpers ───────────────────────────────────────────────

        /// <summary>Hello packet: type(1) + protocolVersion(2) + faction(1) + roleFlags(1) (DW-419/DW-420).</summary>
        /// <remarks>Story 9.4: bumped 1→2 — the coordinated wire change that adds the server-dictated
        /// <see cref="PacketType.DelayDirective"/>/<see cref="PacketType.DelayAck"/> exchange and the widened
        /// Ready packet (protocol version + 64-bit match-agreement hash). The version is now VALIDATED fail-closed
        /// on the client's inbound Hello and the server's inbound Ready (closing the D3.8 gap), so an old build
        /// (v1) can no longer complete the handshake against a v2 build.</remarks>
        public const ushort PROTOCOL_VERSION = 2;

        /// <summary>
        /// DW-419/DW-420 — Hello role flag: the sender is a DEDICATED SERVER (not a P2P host). Tells the client
        /// (a) a Neutral-faction Hello is NOT a P2P host confirmation, and (b) the server rebroadcasts
        /// <see cref="PacketType.LobbyChat"/> back to the sender, so the client must NOT locally echo its own line.
        /// A P2P host's Hello carries flags 0 (and a legacy 4-byte Hello reads back as flags 0 — same behavior).
        /// </summary>
        public const byte HELLO_FLAG_DEDICATED = 0x01;

        /// <summary>
        /// DW-420 — Hello role flag: the RECIPIENT's transport slot was classified a SPECTATOR seat
        /// (<c>SlotAllocation.Classify</c> ≥ the match's player count). The lobby renders a spectator view instead
        /// of the P2P "Host confirmed — click Ready" 2-slot confirmation. Always sent together with
        /// <see cref="HELLO_FLAG_DEDICATED"/> (only dedicated servers classify spectators).
        /// </summary>
        public const byte HELLO_FLAG_SPECTATOR = 0x02;

        /// <summary>
        /// Create a Hello packet: type(1) + protocolVersion(2 LE) + faction(1) + roleFlags(1) = 5 bytes.
        /// The server sends one to each connecting client, embedding the faction assigned to that client and the
        /// DW-419/DW-420 role flags (<see cref="HELLO_FLAG_DEDICATED"/> / <see cref="HELLO_FLAG_SPECTATOR"/>).
        /// P2P mode: both sides use Faction.Neutral + flags 0 (faction determined by host/client role).
        /// The flags byte is an ADDITIVE lobby-phase extension (the LobbyRoster/MapPing precedent — no
        /// <see cref="PROTOCOL_VERSION"/> bump): a reader of the old 4-byte layout ignores it, and
        /// <see cref="TryReadHello(byte[], int, out Core.Faction, out ushort, out byte)"/> defaults flags to 0
        /// when the byte is absent, which reproduces the pre-flag P2P behavior.
        /// </summary>
        public static byte[] MakeHello(Core.Faction faction = Core.Faction.Neutral, byte roleFlags = 0) => new byte[] {
            (byte)PacketType.Hello,
            (byte)PROTOCOL_VERSION,
            (byte)(PROTOCOL_VERSION >> 8),
            (byte)faction,
            roleFlags
        };

        /// <summary>Parse a Hello packet. Returns Faction.Neutral if the packet has no faction byte.</summary>
        public static bool TryReadHello(byte[] buf, int len, out Core.Faction faction)
            => TryReadHello(buf, len, out faction, out _, out _);

        /// <summary>
        /// Story 9.4 — parse a Hello packet, exposing BOTH the assigned faction and the peer's
        /// <see cref="PROTOCOL_VERSION"/> (2 LE bytes at offset 1) so the client can validate it fail-closed
        /// before proceeding (the D3.8 gap). Faction is Neutral if the packet has no faction byte.
        /// </summary>
        public static bool TryReadHello(byte[] buf, int len, out Core.Faction faction, out ushort version)
            => TryReadHello(buf, len, out faction, out version, out _);

        /// <summary>
        /// DW-419/DW-420 — parse a Hello packet, additionally exposing the sender-role flags byte
        /// (<see cref="HELLO_FLAG_DEDICATED"/> / <see cref="HELLO_FLAG_SPECTATOR"/>, offset 4). Flags default to 0
        /// when the packet predates the flags byte (a legacy 4-byte Hello), which reads back as "P2P host" — the
        /// exact pre-flag interpretation, so the extension is behavior-neutral for old senders.
        /// </summary>
        public static bool TryReadHello(byte[] buf, int len, out Core.Faction faction, out ushort version,
                                        out byte roleFlags)
        {
            faction   = Core.Faction.Neutral;
            version   = 0;
            roleFlags = 0;
            if (len < 3) return false;
            if ((PacketType)buf[0] != PacketType.Hello) return false;
            version = (ushort)(buf[1] | (buf[2] << 8));
            if (len >= 4) faction   = (Core.Faction)buf[3];
            if (len >= 5) roleFlags = buf[4];
            return true;
        }

        /// <summary>
        /// Story 9.4 — the widened Ready packet: type(1) + protocolVersion(2 LE) + matchAgreementHash(8 LE) = 11
        /// bytes. Supersedes the old type(1) + scenarioHash(4) layout (the coordinated <see cref="PROTOCOL_VERSION"/>
        /// 1→2 wire bump). The server validates the version (→ <see cref="HaltReason.ProtocolMismatch"/>) and
        /// collects the 64-bit hash per slot (→ <see cref="HaltReason.StartStateDisagreement"/> unless all slots
        /// share one non-zero value); P2P peers compare via the widened <c>HandshakeGate</c>. The hash is the single
        /// fail-closed start-state-agreement value (<c>MatchAgreementHash</c>) — a superset of the old scenario hash.
        /// </summary>
        public static byte[] MakeReady(ushort protocolVersion, ulong matchAgreementHash)
        {
            var buf = new byte[11];
            buf[0] = (byte)PacketType.Ready;
            buf[1] = (byte)protocolVersion;
            buf[2] = (byte)(protocolVersion >> 8);
            int pos = 3;
            WriteULong(buf, ref pos, matchAgreementHash);
            return buf;
        }

        /// <summary>
        /// Parse a widened Ready packet (Story 9.4), reading back the protocol version + 64-bit match-agreement
        /// hash. Fail-closed: a wrong type OR a short/undersized packet returns <c>false</c> (version 0 / hash 0),
        /// which routes into the start-blocking gate on both the server and P2P paths.
        /// </summary>
        public static bool TryReadReady(byte[] buf, int len, out ushort protocolVersion, out ulong matchAgreementHash)
        {
            protocolVersion = 0;
            matchAgreementHash = 0;
            if (len < 11) return false;
            if ((PacketType)buf[0] != PacketType.Ready) return false;
            protocolVersion = (ushort)(buf[1] | (buf[2] << 8));
            int pos = 3;
            matchAgreementHash = ReadULong(buf, ref pos);
            return true;
        }

        /// <summary>StartGame packet: type(1) + startTick(4). Host sends this to both peers.</summary>
        public static byte[] MakeStartGame(uint startTick)
        {
            var buf = new byte[5];
            buf[0] = (byte)PacketType.StartGame;
            int pos = 1;
            WriteUint(buf, ref pos, startTick);
            return buf;
        }

        // ── Chat packet helpers ────────────────────────────────────────────────

        /// <summary>
        /// Maximum chat message length in bytes (UTF-8 encoded).
        /// ~200 ASCII characters; shorter for multi-byte Unicode.
        /// </summary>
        public const int MAX_CHAT_BYTES = 200;

        /// <summary>
        /// Serialise a chat message.
        /// Wire: type(1) + faction(1) + msgLen(2 LE) + msgBytes.
        /// </summary>
        public static byte[] MakeChat(Core.Faction faction, string message)
        {
            byte[] msgBytes = System.Text.Encoding.UTF8.GetBytes(message);
            if (msgBytes.Length > MAX_CHAT_BYTES)
                System.Array.Resize(ref msgBytes, MAX_CHAT_BYTES);

            int total = 4 + msgBytes.Length;
            var buf = new byte[total];
            buf[0] = (byte)PacketType.Chat;
            buf[1] = (byte)faction;
            buf[2] = (byte)msgBytes.Length;
            buf[3] = (byte)(msgBytes.Length >> 8);
            msgBytes.CopyTo(buf, 4);
            return buf;
        }

        /// <summary>Parse a Chat packet. Returns false if malformed.</summary>
        public static bool TryReadChat(byte[] buf, int len,
                                       out Core.Faction faction, out string message)
        {
            faction = Core.Faction.Neutral; message = "";
            if (len < 4) return false;
            if ((PacketType)buf[0] != PacketType.Chat) return false;
            faction = (Core.Faction)buf[1];
            int msgLen = buf[2] | (buf[3] << 8);
            if (msgLen < 0 || msgLen > MAX_CHAT_BYTES) return false;
            if (len < 4 + msgLen) return false;
            message = System.Text.Encoding.UTF8.GetString(buf, 4, msgLen);
            return true;
        }

        // ── Lobby chat packet helpers (Story 9.7) ──────────────────────────────

        /// <summary>
        /// Story 9.7 — serialise a PRE-MATCH lobby chat message. Same layout as <see cref="MakeChat"/>
        /// (type(1) + faction(1) + msgLen(2 LE) + msgBytes) but with the <see cref="PacketType.LobbyChat"/>
        /// discriminator so it rides <c>ENetTransport</c> during the lobby before the lockstep stack exists.
        /// </summary>
        public static byte[] MakeLobbyChat(Core.Faction faction, string message)
        {
            byte[] msgBytes = System.Text.Encoding.UTF8.GetBytes(message);
            if (msgBytes.Length > MAX_CHAT_BYTES)
            {
                // Story 9.7 (P6): truncate on a UTF-8 CODEPOINT boundary, not a raw byte boundary — a cut in the
                // middle of a multibyte sequence would decode to a replacement glyph for recipients. Walk the cut
                // point back off any continuation bytes (0x80–0xBF) so the surviving prefix is well-formed.
                int cut = MAX_CHAT_BYTES;
                while (cut > 0 && (msgBytes[cut] & 0xC0) == 0x80) cut--;
                System.Array.Resize(ref msgBytes, cut);
            }

            int total = 4 + msgBytes.Length;
            var buf = new byte[total];
            buf[0] = (byte)PacketType.LobbyChat;
            buf[1] = (byte)faction;
            buf[2] = (byte)msgBytes.Length;
            buf[3] = (byte)(msgBytes.Length >> 8);
            msgBytes.CopyTo(buf, 4);
            return buf;
        }

        /// <summary>Parse a <see cref="PacketType.LobbyChat"/> packet. Returns false (dropped, no crash) if malformed.</summary>
        public static bool TryReadLobbyChat(byte[] buf, int len, out Core.Faction faction, out string message)
        {
            faction = Core.Faction.Neutral; message = "";
            if (len < 4) return false;
            if ((PacketType)buf[0] != PacketType.LobbyChat) return false;
            faction = (Core.Faction)buf[1];
            int msgLen = buf[2] | (buf[3] << 8);
            if (msgLen < 0 || msgLen > MAX_CHAT_BYTES) return false;
            if (len < 4 + msgLen) return false;
            message = System.Text.Encoding.UTF8.GetString(buf, 4, msgLen);
            return true;
        }

        // ── Lobby roster packet helpers (Story 9.7) ────────────────────────────

        /// <summary>Max player slots a lobby-roster snapshot carries (== the transport seat ceiling). References the
        /// Godot-free <see cref="PlayerCountPolicy.MpSeatCeiling"/> (not the Godot-coupled ServerTransport) so this
        /// codec stays Tier-1-compilable.</summary>
        public const int MAX_ROSTER_SLOTS = PlayerCountPolicy.MpSeatCeiling;

        /// <summary>
        /// Story 9.7 — serialise a lobby roster/ready snapshot (server → client). Wire: type(1) + slotCount(1) +
        /// slotCount bytes, one per slot (bit0 = occupied, bit1 = ready). <paramref name="occupied"/> and
        /// <paramref name="ready"/> are read for slots 0..<paramref name="slotCount"/>-1.
        /// </summary>
        public static byte[] MakeLobbyRoster(int slotCount, bool[] occupied, bool[] ready)
        {
            if (slotCount < 0) slotCount = 0;
            if (slotCount > MAX_ROSTER_SLOTS) slotCount = MAX_ROSTER_SLOTS;
            var buf = new byte[2 + slotCount];
            buf[0] = (byte)PacketType.LobbyRoster;
            buf[1] = (byte)slotCount;
            for (int s = 0; s < slotCount; s++)
            {
                byte b = 0;
                if (s < occupied.Length && occupied[s]) b |= 0x01;
                if (s < ready.Length    && ready[s])    b |= 0x02;
                buf[2 + s] = b;
            }
            return buf;
        }

        /// <summary>Parse a <see cref="PacketType.LobbyRoster"/> packet into caller-owned arrays (at least
        /// <see cref="MAX_ROSTER_SLOTS"/> long). Returns false (dropped, no crash) if malformed/truncated.</summary>
        public static bool TryReadLobbyRoster(byte[] buf, int len, out int slotCount, bool[] occupied, bool[] ready)
        {
            slotCount = 0;
            if (len < 2) return false;
            if ((PacketType)buf[0] != PacketType.LobbyRoster) return false;
            int n = buf[1];
            if (n > MAX_ROSTER_SLOTS || len < 2 + n) return false;
            for (int s = 0; s < n; s++)
            {
                byte b = buf[2 + s];
                if (s < occupied.Length) occupied[s] = (b & 0x01) != 0;
                if (s < ready.Length)    ready[s]    = (b & 0x02) != 0;
            }
            slotCount = n;
            return true;
        }

        // ── Map-ping helpers (Story 11.4, FR-74) ──────────────────────────────

        /// <summary>Serialise a minimap map-ping (10 bytes): type(1) + faction(1) + x(4 int LE) + z(4 int LE).
        /// World XZ is rounded to whole units — a display ping needs no sub-unit precision.</summary>
        public static byte[] MakeMapPing(Core.Faction faction, int worldX, int worldZ)
        {
            var buf = new byte[10];
            buf[0] = (byte)PacketType.MapPing;
            buf[1] = (byte)faction;
            int pos = 2;
            WriteInt(buf, ref pos, worldX);
            WriteInt(buf, ref pos, worldZ);
            return buf;
        }

        /// <summary>Parse a map-ping packet. Returns false (dropped, no crash) if malformed/truncated.</summary>
        public static bool TryReadMapPing(byte[] buf, int len, out Core.Faction faction, out int worldX, out int worldZ)
        {
            faction = Core.Faction.Neutral; worldX = 0; worldZ = 0;
            if (len < 10) return false;
            if ((PacketType)buf[0] != PacketType.MapPing) return false;
            faction = (Core.Faction)buf[1];
            int pos = 2;
            worldX = ReadInt(buf, ref pos);
            worldZ = ReadInt(buf, ref pos);
            return true;
        }

        // ── RTT probe helpers ─────────────────────────────────────────────────

        /// <summary>Serialise a Ping packet (6 bytes). seq wraps at 255.</summary>
        public static byte[] MakePing(byte seq, uint senderMs)
        {
            var buf = new byte[6];
            buf[0] = (byte)PacketType.Ping;
            buf[1] = seq;
            int pos = 2;
            WriteUint(buf, ref pos, senderMs);
            return buf;
        }

        /// <summary>Serialise a Pong reply by echoing the Ping's seq and timestamp.</summary>
        public static byte[] MakePong(byte seq, uint senderMs)
        {
            var buf = new byte[6];
            buf[0] = (byte)PacketType.Pong;
            buf[1] = seq;
            int pos = 2;
            WriteUint(buf, ref pos, senderMs);
            return buf;
        }

        /// <summary>Parse a Pong packet. Returns false if malformed.</summary>
        public static bool TryReadPong(byte[] buf, int len, out byte seq, out uint senderMs)
        {
            seq = 0; senderMs = 0;
            if (len < 6) return false;
            if ((PacketType)buf[0] != PacketType.Pong) return false;
            seq = buf[1];
            int pos = 2;
            senderMs = ReadUint(buf, ref pos);
            return true;
        }

        // ── Delay-proposal helpers ────────────────────────────────────────────

        /// <summary>Serialise a DelayProposal packet (6 bytes).</summary>
        public static byte[] MakeDelayProposal(byte proposedDelay, uint applyAtTick)
        {
            var buf = new byte[6];
            buf[0] = (byte)PacketType.DelayProposal;
            buf[1] = proposedDelay;
            int pos = 2;
            WriteUint(buf, ref pos, applyAtTick);
            return buf;
        }

        /// <summary>Parse a DelayProposal packet. Returns false if malformed.</summary>
        public static bool TryReadDelayProposal(byte[] buf, int len,
                                                out byte proposedDelay, out uint applyAtTick)
        {
            proposedDelay = 0; applyAtTick = 0;
            if (len < 6) return false;
            if ((PacketType)buf[0] != PacketType.DelayProposal) return false;
            proposedDelay = buf[1];
            int pos = 2;
            applyAtTick = ReadUint(buf, ref pos);
            return true;
        }

        // ── Server-dictated delay helpers (Story 9.4) ─────────────────────────

        /// <summary>Serialise a server→client <see cref="PacketType.DelayDirective"/> (6 bytes):
        /// type(1) + delay(1) + applyAtTick(4 LE).</summary>
        public static byte[] MakeDelayDirective(byte delay, uint applyAtTick)
        {
            var buf = new byte[6];
            buf[0] = (byte)PacketType.DelayDirective;
            buf[1] = delay;
            int pos = 2;
            WriteUint(buf, ref pos, applyAtTick);
            return buf;
        }

        /// <summary>Parse a <see cref="PacketType.DelayDirective"/> packet. Returns false if malformed.</summary>
        public static bool TryReadDelayDirective(byte[] buf, int len, out byte delay, out uint applyAtTick)
        {
            delay = 0; applyAtTick = 0;
            if (len < 6) return false;
            if ((PacketType)buf[0] != PacketType.DelayDirective) return false;
            delay = buf[1];
            int pos = 2;
            applyAtTick = ReadUint(buf, ref pos);
            return true;
        }

        /// <summary>Serialise a client→server <see cref="PacketType.DelayAck"/> (6 bytes):
        /// type(1) + delay(1) + applyAtTick(4 LE). Echoes the CLAMPED delay the client committed.</summary>
        public static byte[] MakeDelayAck(byte delay, uint applyAtTick)
        {
            var buf = new byte[6];
            buf[0] = (byte)PacketType.DelayAck;
            buf[1] = delay;
            int pos = 2;
            WriteUint(buf, ref pos, applyAtTick);
            return buf;
        }

        /// <summary>Parse a <see cref="PacketType.DelayAck"/> packet. Returns false if malformed.</summary>
        public static bool TryReadDelayAck(byte[] buf, int len, out byte delay, out uint applyAtTick)
        {
            delay = 0; applyAtTick = 0;
            if (len < 6) return false;
            if ((PacketType)buf[0] != PacketType.DelayAck) return false;
            delay = buf[1];
            int pos = 2;
            applyAtTick = ReadUint(buf, ref pos);
            return true;
        }

        // ── Deterministic disconnect freeze-and-continue helpers (Story 9.6) ───

        /// <summary>Serialise a server→client <see cref="PacketType.DropDirective"/> (6 bytes):
        /// type(1) + faction(1) + applyAtTick(4 LE). <paramref name="faction"/> is the DROPPED faction
        /// (<c>SLOT_FACTION[droppedSlot]</c>); <paramref name="applyAtTick"/> is the tick-counted idle-from marker.</summary>
        public static byte[] MakeDropDirective(byte faction, uint applyAtTick)
        {
            var buf = new byte[6];
            buf[0] = (byte)PacketType.DropDirective;
            buf[1] = faction;
            int pos = 2;
            WriteUint(buf, ref pos, applyAtTick);
            return buf;
        }

        /// <summary>Parse a <see cref="PacketType.DropDirective"/> packet. Returns false if malformed.</summary>
        public static bool TryReadDropDirective(byte[] buf, int len, out byte faction, out uint applyAtTick)
        {
            faction = 0; applyAtTick = 0;
            if (len < 6) return false;
            if ((PacketType)buf[0] != PacketType.DropDirective) return false;
            faction = buf[1];
            int pos = 2;
            applyAtTick = ReadUint(buf, ref pos);
            return true;
        }

        /// <summary>Serialise a client→server <see cref="PacketType.DropAck"/> (6 bytes):
        /// type(1) + faction(1) + applyAtTick(4 LE). Echoes the DROPPED faction + applyAtTick the survivor is
        /// acknowledging; the server maps the faction byte back to a slot via <c>SLOT_FACTION</c> (never trusts it
        /// as a slot index) and treats the transport-authoritative source slot as the acking survivor.</summary>
        public static byte[] MakeDropAck(byte faction, uint applyAtTick)
        {
            var buf = new byte[6];
            buf[0] = (byte)PacketType.DropAck;
            buf[1] = faction;
            int pos = 2;
            WriteUint(buf, ref pos, applyAtTick);
            return buf;
        }

        /// <summary>Parse a <see cref="PacketType.DropAck"/> packet. Returns false if malformed.</summary>
        public static bool TryReadDropAck(byte[] buf, int len, out byte faction, out uint applyAtTick)
        {
            faction = 0; applyAtTick = 0;
            if (len < 6) return false;
            if ((PacketType)buf[0] != PacketType.DropAck) return false;
            faction = buf[1];
            int pos = 2;
            applyAtTick = ReadUint(buf, ref pos);
            return true;
        }

        // ── Little-endian helpers ──────────────────────────────────────────────

        private static void WriteInt(byte[] buf, ref int pos, int v)
        {
            buf[pos++] = (byte)v;
            buf[pos++] = (byte)(v >> 8);
            buf[pos++] = (byte)(v >> 16);
            buf[pos++] = (byte)(v >> 24);
        }

        private static void WriteUint(byte[] buf, ref int pos, uint v)
        {
            buf[pos++] = (byte)v;
            buf[pos++] = (byte)(v >> 8);
            buf[pos++] = (byte)(v >> 16);
            buf[pos++] = (byte)(v >> 24);
        }

        private static int ReadInt(byte[] buf, ref int pos)
        {
            int v = buf[pos] | (buf[pos + 1] << 8) | (buf[pos + 2] << 16) | (buf[pos + 3] << 24);
            pos += 4;
            return v;
        }

        private static uint ReadUint(byte[] buf, ref int pos)
        {
            uint v = (uint)(buf[pos] | (buf[pos + 1] << 8) | (buf[pos + 2] << 16) | (buf[pos + 3] << 24));
            pos += 4;
            return v;
        }

        /// <summary>Write a 64-bit value little-endian (Story 9.4 — the widened Ready match-agreement hash).</summary>
        private static void WriteULong(byte[] buf, ref int pos, ulong v)
        {
            buf[pos++] = (byte)v;
            buf[pos++] = (byte)(v >> 8);
            buf[pos++] = (byte)(v >> 16);
            buf[pos++] = (byte)(v >> 24);
            buf[pos++] = (byte)(v >> 32);
            buf[pos++] = (byte)(v >> 40);
            buf[pos++] = (byte)(v >> 48);
            buf[pos++] = (byte)(v >> 56);
        }

        /// <summary>Read a 64-bit little-endian value (Story 9.4).</summary>
        private static ulong ReadULong(byte[] buf, ref int pos)
        {
            ulong v = (ulong)buf[pos]
                    | ((ulong)buf[pos + 1] << 8)
                    | ((ulong)buf[pos + 2] << 16)
                    | ((ulong)buf[pos + 3] << 24)
                    | ((ulong)buf[pos + 4] << 32)
                    | ((ulong)buf[pos + 5] << 40)
                    | ((ulong)buf[pos + 6] << 48)
                    | ((ulong)buf[pos + 7] << 56);
            pos += 8;
            return v;
        }
    }

    // ── MergedTickPacket ────────────────────────────────────────────────────────

    /// <summary>
    /// Story 9.3 — the server-authoritative merged tick codec (server → client ONLY). Co-located with the
    /// <see cref="UnitOrder"/> / DSL-order layout it wraps so the sub-bundle body reuses the identical 11-byte
    /// order encoding as <see cref="TickCommandPacket"/>.
    ///
    /// Wire layout:  type(1) + tick(4 LE) + subBundleCount(1) + [ faction(1) + orderCount(1) + orders(orderCount*11) ]…
    /// Sub-bundles are written ASCENDING by faction id at build time, so wire order IS the canonical intra-tick
    /// apply order (unit orders in wire order, DSL-event orders included in their sent position, per faction).
    ///
    /// Ceilings are DROP-NOT-CLAMP (the <c>AppendOrder</c> / read-side-reject precedent, never the silent
    /// write-side clamp): <see cref="TryRead"/> returns <c>false</c> — never truncates — on ANY breach of
    /// <see cref="MERGED_MAX_SUBBUNDLES"/>, <see cref="TickCommandPacket.MAX_ORDERS"/> per sub-bundle, or
    /// <see cref="MERGED_MAX_BYTES"/>. A merged-shaped packet is server-authored only; the builder that produces
    /// it (<c>MergedTickBuilder</c>) hard-rejects one submitted by a client.
    /// </summary>
    public static class MergedTickPacket
    {
        /// <summary>type(1) + tick(4) + subBundleCount(1).</summary>
        public const int HEADER_BYTES = 6;

        /// <summary>faction(1) + orderCount(1) prefixing each sub-bundle's orders.</summary>
        public const int SUBBUNDLE_HEADER_BYTES = 2;

        /// <summary>
        /// Max sub-bundles (one per active player). Sized to <see cref="FactionRegistry.PLAYER_COUNT"/> (8) so the
        /// merged packet is N-shaped from day one — enabling &gt;2 live players is Story 9.7/9.15, but the wire
        /// never has to grow. Enforced drop-not-clamp on both build and read.
        /// </summary>
        public const int MERGED_MAX_SUBBUNDLES = FactionRegistry.PLAYER_COUNT;

        /// <summary>
        /// Absolute byte ceiling: the worst case of <see cref="MERGED_MAX_SUBBUNDLES"/> full
        /// (<see cref="TickCommandPacket.MAX_ORDERS"/>-order) sub-bundles. In normal N≤8 play this is never the
        /// binding constraint (the sub-bundle count + per-bundle order cap already bound the total); it is the
        /// deterministic safety valve so a pathological assembly drops the overflowing sub-bundle rather than
        /// producing an over-length packet.
        /// </summary>
        public const int MERGED_MAX_BYTES =
            HEADER_BYTES + MERGED_MAX_SUBBUNDLES * (SUBBUNDLE_HEADER_BYTES + TickCommandPacket.MAX_ORDERS * UnitOrder.SIZE);

        /// <summary>
        /// Serialise a pre-sorted, pre-filtered set of sub-bundles into <paramref name="buf"/>. The caller
        /// (<c>MergedTickBuilder</c>) is responsible for supplying sub-bundles ascending by faction id and for
        /// having already applied the drop-not-clamp ceilings; this is a straight serializer. Sub-bundle <c>b</c>'s
        /// orders live at <paramref name="ordersFlat"/><c>[b * MAX_ORDERS + i]</c>.
        /// </summary>
        /// <returns>Number of bytes written.</returns>
        public static int Write(byte[] buf, uint tick, Faction[] factions, int[] orderCounts,
                                UnitOrder[] ordersFlat, int subBundleCount)
        {
            int pos = 0;
            buf[pos++] = (byte)PacketType.TickCommandsMerged;
            buf[pos++] = (byte)tick;
            buf[pos++] = (byte)(tick >> 8);
            buf[pos++] = (byte)(tick >> 16);
            buf[pos++] = (byte)(tick >> 24);
            buf[pos++] = (byte)subBundleCount;

            for (int b = 0; b < subBundleCount; b++)
            {
                int count = orderCounts[b];
                buf[pos++] = (byte)factions[b];
                buf[pos++] = (byte)count;
                int baseIdx = b * TickCommandPacket.MAX_ORDERS;
                for (int i = 0; i < count; i++)
                {
                    var o = ordersFlat[baseIdx + i];
                    buf[pos++] = (byte)o.UnitId;
                    buf[pos++] = (byte)(o.UnitId >> 8);
                    buf[pos++] = (byte)o.Command;
                    WriteInt(buf, ref pos, o.TargetX);
                    WriteInt(buf, ref pos, o.TargetZ);
                }
            }

            return pos;
        }

        /// <summary>
        /// Parse (and fully validate) a merged tick packet into the caller-owned scratch arrays. Returns
        /// <c>false</c> — leaving <paramref name="subBundleCount"/> at 0 — on a wrong type, a truncated buffer, or
        /// ANY ceiling breach (drop-not-clamp: an over-count sub-bundle, too many sub-bundles, or a length past
        /// <see cref="MERGED_MAX_BYTES"/> all reject the WHOLE packet rather than truncating it).
        /// <paramref name="outFactions"/> / <paramref name="outOrderCounts"/> must be at least
        /// <see cref="MERGED_MAX_SUBBUNDLES"/> long; <paramref name="outOrdersFlat"/> at least
        /// <see cref="MERGED_MAX_SUBBUNDLES"/> * <see cref="TickCommandPacket.MAX_ORDERS"/> long.
        /// </summary>
        public static bool TryRead(byte[] buf, int len, out uint tick, Faction[] outFactions,
                                   int[] outOrderCounts, UnitOrder[] outOrdersFlat, out int subBundleCount)
        {
            tick = 0; subBundleCount = 0;
            if (len < HEADER_BYTES) return false;
            if (len > MERGED_MAX_BYTES) return false;                      // byte-ceiling read-side reject
            if ((PacketType)buf[0] != PacketType.TickCommandsMerged) return false;

            int pos = 1;
            tick = (uint)(buf[pos] | (buf[pos + 1] << 8) | (buf[pos + 2] << 16) | (buf[pos + 3] << 24));
            pos += 4;
            int n = buf[pos++];
            if (n > MERGED_MAX_SUBBUNDLES) return false;                   // sub-bundle-count ceiling reject

            for (int b = 0; b < n; b++)
            {
                if (pos + SUBBUNDLE_HEADER_BYTES > len) return false;
                var faction = (Faction)buf[pos++];
                int count   = buf[pos++];
                if (count > TickCommandPacket.MAX_ORDERS) return false;    // per-sub-bundle over-count reject
                if (pos + count * UnitOrder.SIZE > len) return false;

                int baseIdx = b * TickCommandPacket.MAX_ORDERS;
                for (int i = 0; i < count; i++)
                {
                    ushort unitId = (ushort)(buf[pos] | (buf[pos + 1] << 8)); pos += 2;
                    var command = (UnitCommand)buf[pos++];
                    int tx = ReadInt(buf, ref pos);
                    int tz = ReadInt(buf, ref pos);
                    outOrdersFlat[baseIdx + i] = new UnitOrder(unitId, command, Fixed.FromRaw(tx), Fixed.FromRaw(tz));
                }
                outFactions[b]    = faction;
                outOrderCounts[b] = count;
            }

            subBundleCount = n;
            return true;
        }

        /// <summary>Peek only the tick field of a merged packet (for ring-buffer keying) without a full decode.</summary>
        public static bool TryPeekTick(byte[] buf, int len, out uint tick)
        {
            tick = 0;
            if (len < HEADER_BYTES) return false;
            if ((PacketType)buf[0] != PacketType.TickCommandsMerged) return false;
            tick = (uint)(buf[1] | (buf[2] << 8) | (buf[3] << 16) | (buf[4] << 24));
            return true;
        }

        private static void WriteInt(byte[] buf, ref int pos, int v)
        {
            buf[pos++] = (byte)v;
            buf[pos++] = (byte)(v >> 8);
            buf[pos++] = (byte)(v >> 16);
            buf[pos++] = (byte)(v >> 24);
        }

        private static int ReadInt(byte[] buf, ref int pos)
        {
            int v = buf[pos] | (buf[pos + 1] << 8) | (buf[pos + 2] << 16) | (buf[pos + 3] << 24);
            pos += 4;
            return v;
        }
    }
}
