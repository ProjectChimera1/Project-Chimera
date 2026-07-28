# Epic 11 Context: Fully Operational Match & Shell

<!-- Generated from planning artifacts. Regenerate with compile-epic-context if planning docs change. -->

## Goal

Epic 11 closes the session-layer blocker cluster the 2026-07-01 gap analysis surfaced: everything that happens between "click Play" and "back at the menu with a score screen." Today a match boots from assumptions other stories merely reference, has no real setup screen, no in-match pause/menu/game-speed, no mid-match save, no end-of-match payoff, and a front end that advertises systems that don't exist. This epic builds the full shell — skirmish setup, staged loading, in-match menu with true single-player pause, concede/leave, victory/defeat score screen, checksum-verified SP save/load, the match-feedback floor (alerts, pings, denial/ack feedback, buff icons, subgroup tabs), depth-5 production queues, and honest video/Mode-Select settings. It is deliberately sequenced **before Epic 9**, because multiplayer verification needs this shell to exist to run against.

## Stories

- Story 11.1: Skirmish setup screen + loading/match-start flow
- Story 11.2: In-match menu / pause / game-speed + concede/leave + victory-defeat score screen (merges former 11.3–11.5)
- Story 11.3: SP save/load — full-world serializer + slots/autosave/format stability (merges former 11.7)
- Story 11.4: Under-attack alerts / minimap pings / event cues + denial/acknowledgment feedback (merges former 11.9)
- Story 11.5: Buff icons, multi-select panel, and subgroup tabs
- Story 11.6: Production queue depth-5 with queue display and cancel/refund
- Story 11.7: Video settings + the Mode Select honesty strip

## Requirements & Constraints

- **Session shell (FR-66):** in-match menu, true SP pause, game-speed control, concede/surrender/leave, victory/defeat score screen, video settings.
- **Mid-match SP save/load (FR-67):** checksum-verified, single-player only (MP save is post-1.0).
- **Setup + loading (FR-68):** skirmish setup screen with map selection and staged loading screen driven by real phase completion.
- **Match-feedback floor (FR-74):** under-attack alerts + minimap pings + camera-view box, denial feedback (resource/supply/placement) + acknowledgment sounds + order-confirmed marker, buff/debuff icons + multi-select subgroup tabs, production queue depth-5 with cancel/refund.
- **Match-scale honesty (FR-65, 11.1):** N-faction setup driven by the PLAYER_COUNT-aware registry — no P1 hardcodes in presentation; teams, per-slot faction/color, and 2–4 start positions.
- **Honesty invariant (11.7):** the front end must advertise only shipped systems — ranked/MMR/live-online-count placeholders removed; a UI sweep confirms nothing points at an unbuilt system.
- **Determinism proof (11.3):** after load, the next 300+ ticks must produce a byte-identical `SimChecksum` stream versus an uninterrupted reference run — this is the acceptance test, run headless in Tier-1. Do not ship a save that only "mostly" resumes.

## Technical Decisions

- **Presentation vs sim separation is the governing constraint.** Nearly every feature here is presentation-layer over the untouched 30 Hz fixed-timestep sim. Alerts, pings, denial/ack feedback, buff icons, subgroup tabs, and multi-select panels must be `SimChecksum`-byte-identical with the feature on vs off (2.7 posture) — zero sim writes.
- **Pause/speed are presentation-loop controls** over the fixed-timestep driver; the sim contract is unchanged. True SP pause halts the tick loop (not a hidden overlay). Speed changes (0.5×–3×) are **tick-stamped so replays reproduce the run** — cadence scales, per-tick math does not.
- **MP asymmetry is explicit:** in MP, Save/Load and speed controls are absent/disabled (server-authority inversion → Epic 9 territory); Settings/Concede/Quit remain. Concede rides the order stream so all peers resolve it identically. Pings replicate as tick-stamped presentation events on the existing chat/order channel.
- **Save format (11.3):** versioned binary with per-section headers, fail-closed on unknown sections. The pure-SoA sim makes this array dumps + store states, not object-graph walking. Must serialize every `EntityWorld` SoA array (incl. free list), `BuildingStore`, `ModifierStore`, item/hero stores, projectile store, DSL variable + trigger runtime state, research/alliance/win-condition state, `SimRng` internal state, tick counter, and per-faction economy, plus scenario/content references. Version guard rejects incompatible saves with a clear message; no cross-version guarantees in 1.0 (a version bump = a documented save-break, stated in-UI).
- **Loading screen** is driven by the real `ISetupPhase` runner (the 1.8c spine): validate content → terrain → nav bake → spawn/init. Load failures fail-safe back to the originating screen surfacing the actual `Validated<T>` error — never a hang or black screen.
- **Production queue fold (11.6):** widening the depth-1 `ProductionQueue` byte (left unfolded-while-dormant by 2.8) into a real depth-5 mutable queue makes it fold-mandatory per the checksum-fold-timing rule — one SimChecksum bump, goldens re-baselined explicitly. Spend-at-queue + refund-on-cancel (WC3 model); orders ride the existing Train wire command through all three apply sites.
- **Single-truth guards (11.4):** denial reasons are emitted by the same guard that rejected the action (2.8) — the UI renders the reason code, never re-derives it. Keeps replay/live parity since guards run in the shared apply path.
- **Score-screen counters** are sim-side deterministic counters (folded or derived-at-verdict, disposition stated per counter) so every MP peer sees identical numbers.
- **All UI composes from the 3.1x design system / kit** (UX-DR70); per-story UX-DR addenda authored as needed (the 3.11 precedent).

## UX & Interaction Patterns

- **Skirmish setup (11.1):** map list (shipped + subscribed + local) with minimap previews and properties; per-slot grid supporting Open/Closed/AI-with-difficulty/Human-local, faction pick from the registry (incl. authored factions), team assignment, and color; validation blocks launch on broken configs with actionable messages.
- **In-match menu (11.2):** Esc/F10 overlay — Resume / Settings / Save / Load / Concede / Quit to Menu; Concede and Quit use confirm dialogs; current game speed shows on the HUD clock.
- **Feedback throttling (11.4):** under-attack alerts throttle per region/time window (named constants) so a sustained raid is one alert stream, not spam; the minimap always shows the camera-view box; toast stack needs a cap/evict/coalesce policy (DW-313).
- **Selection UX (11.5):** type-grouped multi-select grid with per-unit health tints; click a group to sub-select; Tab cycles subgroups (WC3 semantics) with the command card reflecting the active subgroup. Subgroups are presentation-layer selection state — the sim never sees them.
- **Feedback masks lockstep delay:** ack sounds + order-confirmed ground markers play at *issue* time (GDD §6 immediate-feedback promise), while sim effects still occur at exec-tick.
- **Score screen:** per-player rows (units built/lost/killed, buildings razed, resources gathered, army-value-over-time graph, duration, winning team); actions Continue (SP rematch loop) / Save Replay (9.18 when landed, hidden before) / Quit; must render on all MP clients including eliminated ones.

## Cross-Story Dependencies

- **11.2 → 11.1:** the in-match menu, concede, and score screen depend on the real setup/boot flow existing. **11.3 → 11.2:** save/load hangs off the in-match menu and needs `SimulationHost` (1.8a). **11.7 → 11.3:** save slots/autosave/format-stability build on the serializer.
- **11.6 → 2.8:** production queue widening consumes the Train picker semantics and the unfolded `ProductionQueue` byte from 2.8. **11.4 → 2.7/2.8:** feedback rides the CombatEventQueue bus and consumes guard-emitted denial reasons + production/research completion events. **11.5 → 2.2b/3.1c:** buff icons read `ModifierStore`.
- **External hooks:** setup screen resolves the "skirmish setup UI" that 5.7/10.1/10.11 assume, and per-slot AI wiring is consumed by 10.12; concede/leave MP announcements integrate with 9.5 (freeze-floor) and 9.6; loading readiness integrates with 9.7; campaign missions (13.1) drive Mode Select's real mission count and mission-start autosave.
- **DW pointers (correct-course 2026-07-28):** 11.1 owns DW-121 (LoadSelectedFaction failure / unresolved-discovered-faction path); 11.4 owns DW-313 (toast-stack cap/evict/coalesce policy).
- **Sequencing:** whole epic runs before Epic 9 (MP verification needs the shell); interleaved with Epic 15 deferred-work sweeps per the Epic-9 retro sequencing.
