---
title: 'Nakama N-player matchmaking, parties API, server-side slot assignment + lobby/matchmaking UI'
type: 'feature'
created: '2026-07-24'
status: 'done'
baseline_revision: '7fbcca14597488463a4190664fe51b6f9c5f0096'
final_revision: '80269c4c006cf315a6221640a981db9b288eb78b'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/project-context.md'
  - '{project-root}/_bmad-output/implementation-artifacts/epic-9-context.md'
  - '{project-root}/godot/src/Multiplayer/NakamaService.cs'
  - '{project-root}/godot/src/Multiplayer/ServerTransport.cs'
  - '{project-root}/godot/src/Multiplayer/DedicatedServer.cs'
  - '{project-root}/godot/src/Multiplayer/LobbyUi.cs'
  - '{project-root}/godot/src/Multiplayer/LockstepManager.cs'
  - '{project-root}/godot/src/Multiplayer/Server/ServerLobbyPolicy.cs'
  - '{project-root}/godot/src/Multiplayer/HandshakeGate.cs'
  - '{project-root}/godot/src/UI/MatchChatOverlay.cs'
  - '{project-root}/godot/src/UI/MainMenuOverlay.cs'
  - '{project-root}/godot/src/UI/Components/ChimeraComponents.cs'
  - '{project-root}/godot/src/Core/Bootstrap/Phases/MatchLifecycleController.cs'
  - '{project-root}/godot/src/Core/MainScene.cs'
  - '{project-root}/godot/src/Core/FactionRegistry.cs'
warnings: [oversized, multiple-goals]
---

<intent-contract>

## Intent

**Problem:** Multiplayer is hardcoded 1v1. The Nakama matchmaker pins `minCount/maxCount=2` (`NakamaService.cs:132-141`); slot/faction is a client-side lexicographic hint (`NakamaService.cs:186-194`); `ServerTransport.MAX_PLAYERS=2` caps live play (`ServerTransport.cs:24`); the sim spins up a hardcoded `FactionRegistry(2)`/`activeFactionCount:2` (`MainScene.cs:339,1978,2014`); there is no parties API (zero `IParty` usage); the lobby (`LobbyUi.cs`) is a two-peer, un-themed Control with no slot grid, no colorblind slot dots/glyphs, no ping, no lobby chat, and no all-ready Start gate; the only entry point is a dev-only Edit-mode `N` keybind (`MainScene.cs:645-650`); `GameServerIp/Port` come only from MainScene `[Export]`s; and Story 9.6's `LockstepManager.OnPlayerDropped` event (`LockstepManager.cs:87`) has zero subscribers.

**Approach:** Raise verified play to N≤4 (architect for 8) by (a) parameterizing the Nakama matchmaker and adding a `PartyService` over Nakama `IParty`, (b) making slot assignment server-authoritative via a frozen `AssignedRoster` and deleting the lexicographic faction hint, (c) lifting `MAX_PLAYERS` to 4 with a dynamic player/spectator slot split, (d) deriving the active-player count from the loaded scenario's `PlayerSlots.Count` (both peers load the identical, agreement-gated scenario — no wire/`PROTOCOL_VERSION` change), and (e) rebuilding `LobbyUi` on the Chimera kit as an N-slot lobby (colorblind dots+glyphs, ready pills, ping, pre-match chat, all-ready Start gate) reachable from a real Multiplayer entry. Every decision lives in a NEW Godot-free Tier-1 core; Nakama/ENet/UI are thin adapters over those cores.

## Boundaries & Constraints

**Always:** All decision logic lives in NEW Godot-free types (Tier-1-testable like `ServerLobbyPolicy`/`DelayController`): `Matchmaking/MatchmakerConfig`, `Server/SlotAllocation`, `Server/AssignedRoster`, `Multiplayer/LobbyReadyModel`, `Party/PartyState`, and a Godot-free `UI/FactionPalette` color/glyph table (RGBA as bytes/uint + glyph string; a presentation-side `ToColor()` converts). Slot identity stays **transport-authoritative** — assigned from the ENet accept-slot, never a client-supplied byte; the `AssignedRoster` is frozen from arrival order at `StartGame`. The active-player count for the match = the loaded scenario's `PlayerSlots.Count`, fed identically to the client `FactionRegistry(N)` and the server's `activeFactionCount`; this is the ONLY source of N so client/server checksum spans cannot diverge. The N=2 code path must remain **byte-identical**: every pre-existing committed golden unchanged, `SimChecksum.AlgoVersion`(21)/`StartStateHash.AlgoVersion`(2)/`PROTOCOL_VERSION`(2) unchanged. `NakamaClient 3.13.0` stays the sole shipped NuGet dep (guarded by `DependencyHygieneTests`) — add NO new package. Colorblind rule: per-slot color is never the only signal — a glyph/label always accompanies the dot. `GameServerIp/Port` + Nakama host/port/key are read from versioned `SettingsData` (per story 8.1's provider-config pattern), falling back to the existing MainScene exports when unset.

**Block If:** Raising `MAX_PLAYERS`/deriving N-from-scenario moves ANY committed golden (`golden-merged-n2`, `golden-scenario`, `golden-multifaction`, `golden-applier-scenario`, `hero-start-state.golden.txt`, the `SimChecksumCoverageGuardTest` pin) or forces a `SimChecksum`/`StartStateHash` algo bump — STOP (means an unintended sim-path change, not a re-baseline). Block if server-side slot assignment or the parties API is found to genuinely require a new `StartGame`/merged-envelope wire field (a `PROTOCOL_VERSION` bump breaking the 9.3-frozen envelope) rather than the scenario-derived count — that is a cross-envelope architectural decision, not an unattended one.

**Never:** NO dynamic per-match server routing / matchmaker-provisioned game servers — 1.0 ships a single static configured endpoint (post-1.0). NO full polished parties lobby UI — ship the parties API + a minimal parties entry only (SD-9 deferrable slice; full parties UI is a fast-follow). Do NOT enable/verify >4 live players or run the 8-peer soak (8 is a documented constant bump). Do NOT implement reconnect/rejoin or drop-to-AI (separate fast-follow). Do NOT fold lobby/party/matchmaking/roster state into `SimChecksum`. Do NOT change the P2P `DelayProposal` path (dormant under server-dictated delay). Do NOT touch `SimChecksum` coverage or the merged-tick sub-bundle layout.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Matchmaker config for N | scenario/lobby wants P players | `MatchmakerConfig` yields `minCount=maxCount=P`, `countMultiple`/`query`/string-props parameterized (not `chimera_1v1`) | P<2 → invalid config rejected |
| Slot classification | connecting peer at accept-slot `i`, player count P, ceiling S | `SlotAllocation.Classify(i,P,S)` → Player (i<P) / Spectator (P≤i<S) / Rejected (i≥S) — split is dynamic per P, not fixed 2/2 | i≥S → reject peer (no free slot) |
| Server-side slot→faction | match starts with connected roster | `AssignedRoster` frozen from arrival order: slot→`FactionRegistry.ToFaction(slot)`; downstream stamps faction from roster, never a packet byte | duplicate/absent slot → roster build rejects |
| Lexicographic hint removed | Nakama match found | `MatchFoundInfo` carries endpoint only (no `SuggestedFaction`); faction comes from the server Hello | n/a (hint deleted) |
| N-slot lobby readiness | P player slots, some ready | `LobbyReadyModel.AllReady()` true only when every occupied player slot is ready; Start enabled iff true | spectator slot never blocks/contributes to all-ready |
| Party lifecycle | leader creates party, member joins/leaves, leader matchmakes | `PartyState` tracks members+leader+per-member ready; only leader may start party matchmaking | join beyond capacity / non-leader start → rejected |
| Pre-match lobby chat | player types in lobby (no `LockstepManager` yet) | `LobbyChat` packet over `ENetTransport`; rendered in the lobby chat pane with faction color+name | malformed packet → dropped, no crash |
| Player dropped mid-match | `LockstepManager.OnPlayerDropped(faction, applyAtTick)` fires | roster/chat surface shows "Player <faction> dropped — slot frozen" | applyAtTick informational only |
| N=2 regression | existing 2-player scenario | identical behavior: every committed golden byte-identical | any golden move = Block-If |

</intent-contract>

## Code Map

- `godot/src/Multiplayer/Matchmaking/MatchmakerConfig.cs` -- **NEW**, Godot-free Tier-1. Parameterizes `minCount/maxCount/countMultiple/query/stringProps` (game-key parameterized off `chimera_1v1`). Built from `SettingsData` + target player count.
- `godot/src/Multiplayer/Server/SlotAllocation.cs` -- **NEW**, Godot-free Tier-1. `Classify(int slot, int playerCount, int slotCeiling) -> SlotRole{Player,Spectator,Rejected}`; the dynamic player/spectator split (SD-9) replacing the fixed 2-players/2-spectators framing.
- `godot/src/Multiplayer/Server/AssignedRoster.cs` -- **NEW**, Godot-free Tier-1. Server-authoritative frozen roster snapshot at match start: slot→`Faction` map + player count, built from transport arrival order; the single source that downstream faction-stamps from. Replaces reliance on live `_slots` + the deleted lexicographic hint.
- `godot/src/Multiplayer/LobbyReadyModel.cs` -- **NEW**, Godot-free Tier-1. Generalizes the two-peer `_readyConfirmed`/`_peerReadyConfirmed` flags into per-slot occupied/ready state + `AllReady(playerCount)` + `StartEnabled` (client/lobby-side analogue of `ServerLobbyPolicy.ShouldStart`).
- `godot/src/Multiplayer/Party/PartyState.cs` -- **NEW**, Godot-free Tier-1. Pure party model: members, leaderId, per-member ready, capacity; add/remove/leader-change/`CanStartMatchmaking(leaderId)`.
- `godot/src/Multiplayer/Party/PartyService.cs` -- **NEW**, Godot-coupled adapter (like `NakamaService`). Wraps Nakama `IParty` (create/join/leave/promote-leader/`AddMatchmakerPartyAsync`) driving `PartyState`; marshals SDK events to the main thread via the existing `ConcurrentQueue<Action>` drain pattern.
- `godot/src/UI/FactionPalette.cs` -- **NEW**, Godot-free Tier-1. Canonical 8-entry Okabe-Ito-derived colorblind-safe palette (RGBA bytes/uint) + per-slot glyph strings + faction name. `ToColor(slot)` extension lives presentation-side. Consolidates the divergent color sources (`FactionVisualsPhase` p1/p2, `MatchChatOverlay` 5-color list).
- `godot/src/Multiplayer/NetworkCommand.cs` -- Godot-free Tier-1. Add a `LobbyChat` `PacketType` (next free code) + `Make/TryReadLobbyChat` codec (faction + text), for pre-match chat over `ENetTransport`. No change to `StartGame`/merged envelope/`PROTOCOL_VERSION`.
- `godot/src/Multiplayer/NakamaService.cs` -- Adapter. Consume `MatchmakerConfig` in `FindMatchAsync` (:123); DELETE the lexicographic faction hint (`:186-194`) — `MatchFoundInfo` carries endpoint only; expose party pass-throughs via `PartyService`. Read endpoint from `SettingsData`.
- `godot/src/Multiplayer/ServerTransport.cs` -- Adapter. `MAX_PLAYERS` 2→4; raise `MAX_SLOTS` to fit 4 players + spectator headroom; route the accept path through `SlotAllocation.Classify` (dynamic split); update the 2-player doc comments (:9,:22).
- `godot/src/Multiplayer/DedicatedServer.cs` -- Adapter. De-binary-fy the `{Waiting,OneConnected,BothConnected,BothReady,InGame}` state enum into N-aware lobby states; freeze the `AssignedRoster` at `StartGame` (:485 region); slot assignment already `SLOT_FACTION`-driven and `ShouldStart`-gated (N-shaped) — verify it flows from the roster.
- `godot/src/Core/MainScene.cs` -- Drive N from the loaded scenario: `new FactionRegistry(scenario.PlayerSlots.Count)` at :339 and :1978, `activeFactionCount: scenario.PlayerSlots.Count` at :2014 (both peers, identical scenario → identical N). Read `GameServerIp/Port`+Nakama from `SettingsData`. Replace the Edit-mode `N` dev keybind (:645-650) with the MainMenu entry.
- `godot/src/UI/MainMenuOverlay.cs` -- Add a **Multiplayer** destination (honoring the honesty invariant at :16-19 — this story is what un-defers it), opening the lobby.
- `godot/src/Multiplayer/LobbyUi.cs` -- Rebuild on `ChimeraComponents` (bootstrap per `MainMenuOverlay.EnsureKitInitialized`): N-slot grid (2-4) with `FactionPalette` dots+glyphs, ready pills (`ChimeraComponents.Tag`), ping display, scenario header + `HandshakeGate` version-hash check surfaced as a header, lobby chat pane over the `LobbyChat` packet, host **Start** button gated on `LobbyReadyModel.AllReady`. Preserve the `OnMatchStart(bool,Faction)` contract + `Initialize`/`Show`/`Close` API. Generalize `TryStartGame` to N via `LobbyReadyModel`.
- `godot/src/Core/Bootstrap/Phases/MatchLifecycleController.cs` -- Subscribe `Lockstep.OnPlayerDropped` (the 9.6-deferred consumer) to surface drops in the roster/chat; wire lobby chat send/receive; fix the `_1v1.chmr` replay name (:154) to be player-count-aware.
- `godot/src/Core/SettingsData.cs` (or the versioned settings type from story 8.1) -- Add `GameServerIp/Port` + Nakama host/port/key fields (versioned, migrated).
- `godot/ProjectChimera.Sim.Tests/**` -- NEW `Matchmaking/MatchmakerConfigTests.cs`, `Server/SlotAllocationTests.cs`, `Server/AssignedRosterTests.cs`, `Multiplayer/LobbyReadyModelTests.cs`, `Party/PartyStateTests.cs`, `UI/FactionPaletteTests.cs`, `LobbyChat` round-trip in `Server/ServerPacketTests.cs`; NEW `Golden/MergedTickN3Scenario.cs` + an n3/n4 merged-tick golden; extend `Server/ServerLobbyPolicyTests.cs` with N=3/4 cases; extend `Multiplayer/LoopbackDesyncSelfTest.cs` to N≥3 in-process peers (join→ready→start→merged agreement).

## Tasks & Acceptance

**Execution:**
- `godot/src/Multiplayer/Matchmaking/MatchmakerConfig.cs` (NEW) -- parameterized min/max/countMultiple/query/props -- kills the `chimera_1v1` 1v1 pin.
- `godot/src/Multiplayer/Server/SlotAllocation.cs` (NEW) -- dynamic player/spectator split classifier -- SD-9 reallocation.
- `godot/src/Multiplayer/Server/AssignedRoster.cs` (NEW) -- frozen server-authoritative slot→faction roster -- AC1 server-side slot assignment.
- `godot/src/Multiplayer/LobbyReadyModel.cs` (NEW) -- N-slot ready/all-ready/start-enabled model -- AC2 Start gate.
- `godot/src/Multiplayer/Party/PartyState.cs` (NEW) -- pure party model -- AC1 parties API core.
- `godot/src/Multiplayer/Party/PartyService.cs` (NEW) -- Nakama `IParty` adapter over `PartyState` -- AC1 distinct parties API.
- `godot/src/UI/FactionPalette.cs` (NEW) -- 8 Okabe-Ito colors + glyphs -- AC2 colorblind dots+glyphs.
- `godot/src/Multiplayer/NetworkCommand.cs` -- add `LobbyChat` packet + codec -- AC2 pre-match lobby chat wire.
- `godot/src/Multiplayer/NakamaService.cs` -- consume `MatchmakerConfig`, DELETE lexicographic hint, party pass-through, config endpoint -- AC1.
- `godot/src/Multiplayer/ServerTransport.cs` -- `MAX_PLAYERS`→4, `MAX_SLOTS` raise, `SlotAllocation` accept path -- AC1/AC3 N≤4.
- `godot/src/Multiplayer/DedicatedServer.cs` -- N-aware lobby states, freeze `AssignedRoster` at start -- AC1/AC3.
- `godot/src/Core/MainScene.cs` -- scenario-derived `FactionRegistry(N)`/`activeFactionCount`, config endpoint, MainMenu entry replaces `N` keybind -- AC1/AC3.
- `godot/src/UI/MainMenuOverlay.cs` -- Multiplayer entry point -- AC2 reachability.
- `godot/src/Multiplayer/LobbyUi.cs` -- N-slot themed lobby: grid, dots+glyphs, ready pills, ping, scenario header+hash check, chat, all-ready Start -- AC2 (UX-DR69).
- `godot/src/Core/Bootstrap/Phases/MatchLifecycleController.cs` -- subscribe `OnPlayerDropped`, wire lobby chat, N-aware replay name -- AC2/9.6 carryover.
- `godot/src/Core/SettingsData.cs` -- versioned server/Nakama config fields -- AC1 "read from configuration".
- `godot/ProjectChimera.Sim.Tests/**` -- Tier-1 tests for every new pure core + `LobbyChat` codec + n3/n4 merged golden + N-slot `ServerLobbyPolicy` cases + loopback N≥3 -- proof of every I/O-matrix row.

**Acceptance Criteria:**
- Given the Nakama matchmaker previously pinned to `minCount/maxCount=2`, when N-player matchmaking is parameterized, then `MatchmakerConfig` exposes configurable `minCount/maxCount/countMultiple`, slot assignment is server-side via `AssignedRoster` (the lexicographic pick at `NakamaService.cs:186-194` is deleted), a distinct `PartyService`/`PartyState` groups players pre-match, and `GameServerIp/Port` are read from `SettingsData` (not hardcoded) — dynamic per-match routing is out of scope (single static configured endpoint).
- Given a lobby of matched/LAN players, when the lobby UI renders (UX-DR69), then it shows a scenario header with a version-match hash check (via `HandshakeGate`), per-slot colorblind dots+glyphs (`FactionPalette`, color never the sole signal), ready pills, and ping, plus lobby chat, and the Start button is gated until every occupied player slot is ready (`LobbyReadyModel.AllReady`).
- Given the raised ceiling, when a match spins up, then the active-player count derives from the loaded scenario's `PlayerSlots.Count` on both client and server (identical N, no `PROTOCOL_VERSION`/envelope change), `MAX_PLAYERS` supports up to 4, and the parameterization supports 8 as a constant bump.
- Given the full suite, when it runs, then every pre-existing committed golden is byte-identical and `SimChecksum.AlgoVersion`(21)/`StartStateHash.AlgoVersion`(2)/`PROTOCOL_VERSION`(2) are unchanged (a moved golden = Block-If); a NEW n3/n4 merged-tick golden proves the raised count merges deterministically across two runs.
- Given `LockstepManager.OnPlayerDropped` (Story 9.6, previously unsubscribed), when a peer drops mid-match, then a lobby/in-match UI consumer surfaces "player dropped — slot frozen" (closing the 9.6-deferred subscriber).

## Design Notes

**Why scenario-derived N (not a wire field).** Both peers already load the identical scenario, gated by the `MatchAgreementHash`/`scenarioHash` agreement (story 9.4). `scenario.PlayerSlots.Count` is therefore a value both sides agree on with zero new wire, no `StartGame` format change, and no `PROTOCOL_VERSION` bump — so the 9.3-frozen merged envelope and every golden stay untouched. `FactionRegistry`, `ScenarioApplier` (loops all `PlayerSlots` ascending), the merged builder (`MERGED_MAX_SUBBUNDLES=8`), quorum, delay, and drop controllers are already N-shaped; the only literals are the three `2`s in `MainScene`. The N≤4 LAN verification scenario must author 4 `player_slots` (the fallback authors only 2, `ScenarioApplier.cs:404`).

**Verification honesty (unattended constraints).** The pure cores are Tier-1 (`dotnet test`). The Godot-coupled adapters (`ServerTransport`/`DedicatedServer`/`LobbyUi`/`NakamaService`/`PartyService`) are excluded from Sim.Tests. The multi-peer wire path is proven by extending `LoopbackDesyncSelfTest` to N≥3 in-process peers exercising the REAL transport/lockstep. The **live Nakama matchmaking + parties + 4-client LAN connect (AC3's `join→chat→ready→start`) requires the running `docs/server-deploy` Nakama server + dedicated game server + four clients and CANNOT be verified in this unattended run** — it is a documented manual check, not a Tier-1 gate.

**Slot split (SD-9).** `SlotAllocation` makes players/spectators a dynamic function of the per-match player count, not a fixed 2/2. `MAX_SLOTS` is the ceiling (players + spectator headroom); classification, not a hard partition, decides each slot's role.

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` -- expected: compiles clean; determinism analyzer green (new Godot-free cores use no `float`/`Dictionary`-enumeration/`System.Random`/wall-clock); `DependencyHygieneTests` still see NakamaClient 3.13.0 as the sole dep.
- `dotnet test godot/ProjectChimera.Sim.Tests` -- expected: all pass incl. new MatchmakerConfig/SlotAllocation/AssignedRoster/LobbyReadyModel/PartyState/FactionPalette/`LobbyChat`-codec tests + N=3/4 `ServerLobbyPolicy` + n3/n4 merged golden; **every pre-existing golden byte-identical**; `SimChecksum.AlgoVersion`(21)/`StartStateHash.AlgoVersion`(2)/`PROTOCOL_VERSION`(2) unchanged.
- `dotnet test godot/ProjectChimera.Sim.Tests --filter "FullyQualifiedName~Golden|SimChecksumCoverageGuard|VersionStampConsistency"` -- expected: existing goldens unchanged (moved golden = Block-If, not a re-baseline); new n3/n4 golden present.

**Manual checks (Godot-side / live-server — NOT Tier-1, documented for the LAN-verify pass):**
- `godot --headless -- --loopback-test` (`LoopbackDesyncSelfTest`, extended to N≥3) -- expected: still `RESULT: PASS`; the added N≥3 phase joins→readies→starts in-process peers and asserts all peers' checksums agree over the raised count with merged ticks flowing.
- LobbyUi render + entry: launch the game, open Multiplayer from the main menu, confirm the N-slot grid renders 2-4 slots with colorblind dots+glyphs, ready pills, ping, scenario header hash check, and chat; Start disabled until all-ready (godot-mcp / check-site visual verify).
- Live Nakama (requires `docs/server-deploy` up): matchmake N players, form a party, and complete a 4-player `join→chat→ready→start` on the configured static endpoint — deferred to a human LAN-verify session; unverifiable unattended.

## Spec Change Log

_None — no bad_spec loopback occurred; the review pass resolved via patches only._

## Review Triage Log

### 2026-07-24 — Review pass (review_loop_iteration 0)
- intent_gap: 0
- bad_spec: 0
- patch: 10: (high 1, medium 5, low 4)
- defer: 1: (high 0, medium 1, low 0)
- reject: 2
- addressed_findings:
  - `[high]` `[patch]` **LobbyChat dead on the dedicated/online path** (adversarial + edge-case lenses). `DedicatedServer.HandlePacket` had no `LobbyChat` case, so lobby chat (an AC2 deliverable) worked only in 2-player P2P. Added a relay case that re-stamps the sender's authoritative faction (`ServerLobbyPolicy.StampChatFaction`, never the client byte) and broadcasts — chat now works for N-player dedicated; this also closes the dedicated-path faction spoofing.
  - `[medium]` `[patch]` **N-slot grid blind to remote slots on the dedicated path** (adversarial). Client inferred occupancy/ready only from local + P2P events, so slots 2..N-1 showed OPEN even when connected/ready (AC2 misrepresentation). Added an additive server→client `LobbyRoster` packet (0x22) broadcast on connect/disconnect/ready and drove the grid from it; joiner occupancy now comes from the authoritative Hello/roster (no phantom slot-1). No merged-envelope/PROTOCOL_VERSION change.
  - `[medium]` `[patch]` **Ceiling split (PLAYER_COUNT=8 vs MAX_PLAYERS=4)** (adversarial + edge-case). A 5-8-slot scenario made the matchmaker group more players than the transport seats → matched players force-spectated. New Godot-free `PlayerCountPolicy` single-sources the seat ceiling (4) and floor (2); matchmaker/lobby targets clamp to the seat ceiling; offline-skirmish sim faction-count still derives from full scenario slots.
  - `[medium]` `[patch]` **Two-peer Start gate + P2P Host offered for N>2** (adversarial). `TryStartGame` start-execution disagreed with the button-enable predicate for N>2 and P2P (maxPeers=1) could start an under-populated N-faction match. Unified both on `LobbyReadyModel.AllReady(PlayerCount)`; disabled the Direct Host path for PlayerCount>2.
  - `[medium]` `[patch]` **Client N-derivation divergent/exception-swallowing** (adversarial). Client raw-re-parse vs server validated model, with a silent `catch → 2` fallback. Now derives from the same loader the server uses, logs failures via the log seam, and a new Tier-1 test asserts the default scenario derives to 2 (guards the N=2 byte-identical invariant at the derivation site).
  - `[medium]` `[patch]` **Loopback integration smoke was N=3** (verification-gap + intent-alignment). The actual N=4 ship ceiling ran over the real transport nowhere. Bumped `LoopbackDesyncSelfTest.PlayerCount` 3→4 so join→ready→start→merged-agreement + drop-continue exercise at N=4.
  - `[low]` `[patch]` **MakeLobbyChat mid-UTF-8 truncation** (adversarial + edge-case). Byte-boundary resize could split a multibyte codepoint; now walks back off continuation bytes.
  - `[low]` `[patch]` **Split ExpectedPlayerCount default (4 vs 2)** (adversarial). Canonicalized `DedicatedServer.ExpectedPlayerCount` default to `PlayerCountPolicy.MpFloor`(2).
  - `[low]` `[patch]` **PartyState capacity vs Nakama MaxSize** (edge-case). `PartyService.SyncFromParty` now sizes `PartyState` from the authoritative `party.MaxSize`, so joining a larger party doesn't silently drop members.
  - `[low]` `[patch]` **TryReadLobbyChat OOB-guard branch untested** (verification-gap). Added a test asserting a declared `msgLen` exceeding the passed buffer length returns false.
- deferred (see `deferred-work.md`, 1 NEW entry): the Godot-coupled `DedicatedServer` fan-in / `AssignedRoster` freeze / `SlotAllocation` classify wiring has no automated xUnit coverage — the pure functions are tested but the adapter's call-with-right-arguments is proven only by the manual loopback smoke; same accepted node-wiring boundary as Stories 9.3/9.4/9.6.
- rejected (2): P2P lobby-chat faction is client-supplied/spoofable — within the epic's trusted-friends-EA threat model ("anti-spam, not anti-cheat"); the dedicated path is server-stamped (P1). `OnPlayerDropped` UI subscriber unverified — presentation-only (log + system message), correct wiring, un-Tier-1-testable and the drop-UI verification is the documented-manual boundary already deferred in 9.6.

### 2026-07-24 — Follow-up review pass (review_loop_iteration 0)
Fresh 4-lens pass (adversarial + edge-case + verification-gap + intent-alignment) on the final committed diff.
- intent_gap: 0
- bad_spec: 0
- patch: 1: (high 0, medium 0, low 1)
- defer: 2: (high 0, medium 0, low 2)
- reject: 13
- addressed_findings:
  - `[low]` `[patch]` **SettingsData v1→v2 endpoint-field migration untested** (verification-gap). `MigrateForward` now null-normalizes the three new string endpoint fields (`GameServerIp`/`NakamaHost`/`NakamaKey`) and bumps `CurrentSchemaVersion` 1→2, but only `LlmBaseUrl` had a null-normalization test — the new fields' migration contract was unpinned on a pure Tier-1 type. Added `MigrateForward_ExplicitNullEndpointFields_NormalizedToEmpty_AndSchemaBumped` asserting the three fields normalize to `""` and the schema version stamps to current. Suite 3204→3205 pass, goldens byte-identical.
- deferred (see `deferred-work.md`, 2 NEW entries):
  - Own lobby-chat line renders twice on the dedicated path (optimistic local echo + server rebroadcast-to-sender) — low UX, self-acknowledged in-code, clean fix needs P2P-vs-dedicated path awareness.
  - A dedicated-server spectator (slot ≥ ExpectedPlayers) sees a bogus 2-player "Host confirmed — click Ready" lobby because a Neutral Hello is indistinguishable from a P2P host confirmation — UI-only misrepresentation on the unshipped/headroom spectator path.
- rejected (13): "Ping dead on the dedicated path" — **false positive** (`DedicatedServer.HandlePacket` echoes a Pong at the `PacketType.Ping` case). 5-8-slot scenarios run idle factions over MP — the sim/MP clamp split (`SimActivePlayers` [2,8] vs `MpTargetPlayers` [2,4]) is the deliberate `PlayerCountPolicy` design from the iter-0 pass, and >4 live play is explicit out-of-scope. VG raised-count-only-merge-math / roster-freeze-wiring-untested / PartyService-untested / dual-boundary-spectator-classification — all restate the spec's documented Tier-1-vs-adapter "Verification honesty" boundary and the already-deferred node-wiring class. Party `_party` background-thread mutation + `JoinAsync`/`LeaveAsync` no-op window — unreachable in the shipped flow (no lobby control calls the party API; full parties UI is a documented fast-follow). `MatchmakerConfig` uses `Dictionary` in the Tier-1 surface — not a real determinism-analyzer violation (build green; construction, not enumeration). Dead 2-byte chat-length width, "arrival order" doc comments, `ExpectedPlayers` unreachable 1-player branch, unguarded `LobbyUi.Show()`, un-state-gated in-game `LobbyChat` relay — cosmetic/latent/no-consequence noise.


## Auto Run Result

Status: done (follow-up review pass, review_loop_iteration 0)

**Summary.** A `done`-spec follow-up review of Story 9-7 (Nakama N-player matchmaking, parties API, server-side slot assignment + lobby/matchmaking UI). Re-ran a fresh 4-lens adversarial/edge-case/verification-gap/intent-alignment sweep over the final committed diff (`7fbcca1..HEAD`, ~3.4k insertions, 37 files). One low-severity verification gap was patched; two low UX findings deferred; the remaining thirteen findings rejected (one an outright false positive, the rest deliberate prior-pass design, documented Tier-1-vs-adapter boundary, unreachable/unwired code, or cosmetic noise). No intent_gap, no bad_spec — the diff implements a defensible reading of the intent fully within its documented scope.

**Files changed this pass:**
- `godot/ProjectChimera.Sim.Tests/Definitions/SettingsProviderConfigTests.cs` — added `MigrateForward_ExplicitNullEndpointFields_NormalizedToEmpty_AndSchemaBumped` pinning the Story-9.7 v1→v2 endpoint-field null-normalization + schema bump.
- `_bmad-output/implementation-artifacts/deferred-work.md` — 2 new defer entries (dedicated-path chat double-render; spectator Neutral-Hello ambiguity).
- (spec) triage-log + Auto Run Result updated.

**Review findings breakdown:** patch 1 (low 1) applied; defer 2 (low 2); reject 13.

**Follow-up review recommendation:** `false`. Patched this pass: 0 high, 0 medium, 1 low → score `3×0 + 1×1 = 1` (< 5), no high → not recommended.

**Verification performed:**
- `dotnet build godot/godot.csproj` → 0 errors (13 pre-existing nullable-annotation warnings), determinism analyzer green.
- `dotnet test godot/ProjectChimera.Sim.Tests` → Failed 0, Passed 3205, Skipped 1 (was 3204 before the new test); every pre-existing golden byte-identical; `SimChecksum.AlgoVersion`(21)/`StartStateHash.AlgoVersion`(2)/`PROTOCOL_VERSION`(2) unchanged.
- `dotnet test --filter "Golden|SimChecksumCoverageGuard|VersionStampConsistency|MigrateForward_ExplicitNullEndpointFields"` → 203 passed; goldens unchanged, new migration test green.

**Residual risks:** The N-player Godot-coupled adapter path (DedicatedServer state machine, LobbyUi wiring, PartyService, live Nakama + 4-client LAN) remains outside the Tier-1 gate by design — proven by the DEBUG loopback smoke and inspection, and covered by the pre-existing + newly deferred node-wiring/UX ledger entries. Party API has no shipped UI caller (documented fast-follow), so its latent thread-safety issues cannot manifest in the current build.

**Residual working-tree artifacts (not part of this change, left in place):** `Snapshot.md`, `_bmad-output/implementation-artifacts/sprint-status.yaml` — both dirty at session start (orchestrator/autosave files), unrelated to this review.
