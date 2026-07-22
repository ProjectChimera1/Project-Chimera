# Epic 9 Context: Share, Discover & Multiplayer at Scale

<!-- Generated from planning artifacts. Regenerate with compile-epic-context if planning docs change. -->

## Goal

Epic 9 turns Chimera from a validated 2-player lockstep game into a shipped multiplayer-and-sharing platform. Two rails run in parallel. The **multiplayer-at-scale rail** scales the as-built 2-player relay to a verified up-to-4 players (8 as a fast-follow constant bump) by making the dedicated server the single authority over merged tick truth, desync detection, input delay, disconnect handling, and start-state agreement — every wire-touching change gated by a golden-checksum regression test so FR-39 determinism never regresses. The **share/discover rail** lets creators publish scenarios to mod.io behind a proof-of-play gate with explicit IP-ownership consent, lets players browse/subscribe/rate that content with content-hash integrity verification and runtime custom-asset ingest, and delivers viewable/shareable v2 replays plus server-authoritative online hero persistence. It matters because "supports 4 players," "scenarios circulate," and "team RTS exists" become verified facts rather than aggregate assumptions — the M5 promise the whole creation platform stands on.

## Stories

- Story 9.1: Widen SimChecksum + server-side checksum collector (majority-vote desync attribution)
- Story 9.2: Expand faction/player model to 8 + audit every (int)Faction site
- Story 9.3: Server-authoritative merged-tick rewrite (+ client merged-arrival gate, spectator-demux/chat-spoof fix, N=2 golden gate)
- Story 9.4: Server-dictated adaptive input delay + start-state agreement + PROTOCOL_VERSION/rulesetHash gates
- Story 9.5: Local-faction parameterization — remove every Player1 hardcode from presentation (sprints early despite number)
- Story 9.6: Deterministic disconnect freeze-and-continue drop policy
- Story 9.7: Nakama N-player matchmaking, parties API, server-side slot assignment + lobby UI
- Story 9.8: Proof-of-play token + pre-publish quality/IP-consent gate + publish .chimera.zip to mod.io
- Story 9.9: Content-hash integrity verification on download + runtime binary-asset (GLB) ingest
- Story 9.10: Content browser delegating browse/search/tag/sort/subscribe/rate to mod.io
- Story 9.11: Replay v2 (tagged body + scenario re-gate) + replay UX (browser/playback/perspective)
- Story 9.12: Server-validated online hero persistence rail
- Story 9.13: Per-client command-rate throttle / anti-spam on the dedicated server
- Story 9.14: Teams & alliances — lobby teams wired into the sim alliance model
- Story 9.15: Four-player verified end-to-end
- Story 9.16: Full-content pre-match hash handshake

## Requirements & Constraints

- **Ship N<=4, architect for N=8.** Wire format, buffers, checksum, and faction model must be N-shaped from day one so 8 is a documented constant bump + re-verification, never a rearchitecture. The 8-peer soak, full parties lobby UI, transport extraction, and Linux PublishAot build are explicit post-1.0 fast-follows.
- **Determinism is the top constraint.** Every story that touches the wire is golden-checksum-gated at N=2: the same input must produce byte-identical SimChecksums across two runs, and merged-path/new-format runs must reproduce the pre-rewrite baseline byte-for-byte. New N=3/N=4 deterministic harnesses prove the faction expansion introduces no desync.
- **Server is the single source of truth** for merged tick truth, desync detection/attribution, input delay, freeze-on-disconnect, start-state agreement, and command-rate limits. Slot identity is always transport-authoritative — never trusted from a client byte.
- **UGC quality + IP floor:** publishing requires a valid proof-of-play token (creator won their own scenario), min-quality fields (thumbnail, description >=100 chars, >=1 screenshot), and explicit IP-ownership consent (creator retains ownership; platform takes only a non-exclusive host/distribute right). Downloaded packages are content-hash verified including bundled asset bytes.
- **Delegate to mod.io, don't reimplement:** browse/search/tag/sort/subscribe/rate go entirely through mod.io-native features — no parallel local rating or search index. Requires configured Game ID + API key; verify end-to-end.
- **Online persistence must be server-attested:** hero/profile is a Nakama storage object (Owner-Read / No-Client-Write) mutated only via a validating server RPC; StartGame is gated on server attestation.
- **Anti-spam, not anti-cheat:** the command-rate throttle is a pure server-validation layer for trusted-friends EA; it must never touch the sim or determinism, and the cap must sit above worst-case legitimate play (a full 32-order packet every tick at dictated delay).

## Technical Decisions

- **A* = Option A architecture + Option C scope** (D5 briefing). Strangler migration on top of a relay that already works and deliberately does not run the sim; collapse the single-opponent assumption (`int other = 1 - slot`) into one merged-packet readiness model rather than multiplying it into N per-peer streams.
- **Merged tick packet is a distinct server->client type** (`TickCommandsMerged`); client `TickCommands` stays single-faction and client->server only. Server re-stamps faction from `SLOT_FACTION[sourceSlot]`, sorts sub-bundles ascending by faction id, and drops (not clamps) on faction mismatch / over-count / byte-ceiling. A merged-shaped packet received from a client is hard-rejected. Ascending wire order is the canonical intra-tick apply order (unit orders in wire order, then DSL events).
- **Checksum topology inversion:** the server becomes a stateful collector — parse slot-tagged checksums, buffer per-slot per-60-tick-window, majority-vote the canonical value, name the minority in a DesyncAlert, and HALT fail-closed on no majority. SimChecksum is widened from Ore-only to Crystal + SupplyUsed + SupplyCap across all active factions in ascending faction order, with a bumped `checksum_algo_version` and a guard test.
- **Faction model:** extend the enum to Player8, raise FACTION_COUNT, size all per-faction SoA arrays to the new count, audit every `(int)Faction` index site and every literal 2-player loop, and convert the ScenarioDirector victory-threshold loop from float/locale-formatted to Fixed end-to-end. Faction == player for 1.0 (decoupled playerSlot deferred).
- **Server-dictated input delay** is net-new (no Ping/Pong/DelayProposal exists today): server-side RTT collection -> authoritative broadcast -> all-N-ACK commit -> receipt-side re-clamp to [2,12]. Start-state agreement compares a single hash {roster + faction-count + initial-delay + rulesetHash + scenarioHash} fail-closed before tick 0; inbound PROTOCOL_VERSION and per-hash mismatches (hash==0 = hard reject) gate match start.
- **Freeze-and-continue on disconnect** is tick-counted (never wall-clock), ACK-gated like a delay change: empty commands injected for the dropped slot each tick, slot NOT removed from sim or checksum, passive sim continues bit-identically. Drop-to-AI is out of scope (D4 fast-follow).
- **Co-design one tagged envelope** for the merged packet (9.3), the DSL event record, and replay v2 (9.11) so all three share layout; freeze it in 9.3 before any client consumes it. Replay v2 bumps VERSION, embeds roster + faction-count + rulesetHash + canonical scenarioHash + algo-version, hard-rejects v1, and re-gates scenarioHash on playback.
- **Runtime asset ingest** loads custom .glb via `GLTFDocument.AppendFromFile` -> `GenerateScene` (NOT `GD.Load<PackedScene>`) in non-editor builds, runs per-asset validation, registers in a net-new AssetRegistry, and falls back to a box placeholder on invalid/unsafe assets. Content hash folds in asset bytes, extending the existing scenario_hash check.
- **Full-content handshake** extends the canonical FNV-64 model hash (not file-byte hashing) to cover all sim-relevant loaded content; presentation-only content (e.g. CombatFeedbackProfile) stays excluded per the no-fold rule.
- **Alliance data model** already exists (from Story 7.14); Epic 9's teams work is lobby/UI wiring plus targeting/vision/victory integration. FFA = teams-of-1; no in-match diplomacy in 1.0.

## UX & Interaction Patterns

- **Lobby (UX-DR69):** scenario header with version-match hash check, per-slot colorblind dots + glyphs, ready pills, ping, lobby chat; Start gated until all slots ready. All slot grids render 2-4 slots for 1.0 (8 post-1.0). Full join->chat->ready->start LAN flow verified at scale (UX-DR84). Stall/clamp/RTT-change banner (UX-DR28).
- **Faction colors are sacred:** team identity uses the 8 reserved Okabe-Ito-derived colorblind-safe colors; color is never the only signal — glyph/label floor applies to team choice (2v2 / 1v1v1v1 / 3v1) and slot dots.
- **Content browser (UX-DR72):** result cards show name, author, thumbnail, tags, and mod.io rating/download stats; author IP-ownership/profile surfaced from the mod.io entry; not-logged-in users prompted to authenticate before subscribe/rate.
- **Online hero picker (UX-DR75):** at StartGame, hero selection for an online match is gated on server attestation.
- **Replay UX (FR-77):** main-menu replay browser lists .chmr files with metadata (map, players/factions, date, duration, result) + rename/delete; playback has pause/resume, speed steps (1x/2x/4x/8x), seek-forward-to-tick, tick/clock display; no rewind in 1.0 (deterministic re-sim from tick 0 is the seek mechanism); perspective toggle for any player's fog or reveal-all; "Save replay" offered on the score screen.

## Cross-Story Dependencies

- **Linear multiplayer backbone (D5 strangler order):** 9.1 (checksum widen + collector) -> 9.2 (faction expansion, needs 9.1 as safety net) -> 9.3 (merged-tick rewrite, the #1 FR-39 regression gate) -> 9.4 (delay + start-state) -> 9.6 (freeze-and-continue) -> 9.7 (Nakama N + lobby). 9.13 (throttle) depends on 9.3 and leans on 9.6 for its disconnect path.
- **9.5 sprints early** (depends on Story 1.9b, sequenced before 9.6/9.7) despite its late number; it is a presentation-only change proven by the two-client loopback AC.
- **Share/discover chain:** 9.8 proof-of-play token depends only on 9.1's canonical-hash discipline (NOT the D5 wire backbone); publish -> 9.9 (integrity + ingest) -> 9.10 (content browser). 9.11 replay v2 reuses the tagged envelope frozen in 9.3 and the canonical hash from 9.8. 9.12 online hero depends on 9.7's matchmaking/StartGame surface.
- **Scale-honesty closure:** 9.14 teams depends on 7.12/7.14 (alliance model), 9.7, and 11.1; 9.15 (4-player verified e2e) depends on 9.5, 9.14, and 6.7/6.9 (4-start-position map); 9.16 handshake depends on 9.1 and consumes 3.15/4.8 content models as they land. 4-player load perf numbers from 9.15 feed 10.3 (not gated here).
