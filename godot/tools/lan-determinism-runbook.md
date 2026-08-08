# LAN Determinism Runbook — FR-39 two-machine gate (Story 1.9b)

**The #1 ship-risk gate.** Prove that two players on **separate physical machines** run the lockstep
simulation in deterministic lockstep — a full match, **300+ ticks, ZERO desync** — and that a real
divergence trips the terminal HALT. This is a **manual** gate (it needs two machines); everything that
*can* be automated is already green (the server PASS/FAIL verdict, the headless `--loopback-test`
self-test over real sockets, and the canonical-scenario determinism test). This runbook is the
push-button procedure for the physical run.

> **Status:** the engineering (Story 1.9b AC1–3, AC5) is complete and verified on one machine. This
> physical two-machine run is **AC4**, performed by Alec when a second LAN machine is available. Record
> the outcome in the story's Change Log when done.

---

## 0. What "PASS" means

- **Determinism PASS:** over a full match (≥300 ticks ≈ ≥5 checksum windows; play several minutes for a
  meaningful sample), the dedicated server's console prints a `[Determinism] … window #N` line **every**
  comparison window and **never** a `DESYNC`/`HALT` line, and the final `MATCH SUMMARY` reads
  `… 0 desync — PASS`. Both clients' HUD hashes match every window.

> ⚠ **READ THE WINDOW COUNT, NOT THE WORD "PASS".** The server prints `PASS` whenever it saw zero
> desyncs — *including when it compared almost nothing*. The 2026-08-07 first run ended at
> `MATCH SUMMARY: 1 windows compared, 0 desync, 0 abandoned — PASS` because both peers dropped early:
> that is a truthful log line and a worthless gate result. **Fewer than 5 windows is not a PASS of this
> gate**, it is an aborted run. Treat the window count as the primary number and `PASS` as a secondary
> condition on it.
- **HALT-path PASS:** when you deliberately induce a desync (press **F9** on one client), the server
  prints `GLOBAL DESYNC … Broadcasting terminal HALT` and **both** clients show the red **"MATCH HALTED"**
  overlay.

Both must hold for the gate to be green.

---

## 1. Prerequisites (both machines)

1. **Godot 4.6.3 mono (.NET)** installed, and **this repo cloned** at the same commit on both machines.
   (Story 1.9b runs from source via `godot --path` — there is no exported build yet; that's Epic 10.)
2. Both machines on the **same LAN** (same router/subnet, e.g. `192.168.1.x`).
3. On the **server machine** (Machine A), allow inbound **UDP 7777** through Windows Firewall:
   - Windows Security → Firewall & network protection → Advanced settings → Inbound Rules → New Rule →
     Port → UDP → 7777 → Allow. (Or temporarily allow the Godot app through the firewall.)
4. Build the C# once on each machine so the assembly is current: `dotnet build godot/godot.csproj`.
5. **Terrain3D must load on both machines.** Its GDExtension binaries under
   `godot/addons/terrain_3d/bin/` were excluded by a `bin/` .gitignore rule until 2026-08-07 (DW-905).
   If Godot greets you with *"Unable to load addon script … terrain_3d/src/editor_plugin.gd … Disabling
   the addon"*, that machine has no terrain: slope-derived blocked cells and spawn elevation go
   client-divergent (DW-828) and you will get a **desync that has nothing to do with determinism**.
   `git pull` past `289e795f` and confirm the warning is gone before running the gate.
6. **No remote-desktop session may be minimised, backgrounded, or screenshotted during a scored run.**
   A backgrounded window stops processing, stops submitting ticks, and the server drops that peer as a
   timeout. This is not a theoretical risk — it ended both 2026-08-07 attempts (at tick 94 and tick 114).
   If you must drive the machines remotely, set **both** to never sleep, never blank the display, and
   **not to lock on disconnect**, then start the run and leave the sessions strictly alone.
7. **Start a FRESH server for every match.** A server reused across matches carries frozen-slot and tick
   state from the previous one (DW-598, DW-599, DW-600). The launcher now cleans stale instances by
   default; do not defeat it with `-NoClean` unless you know why.

> **Note (2026-08-07):** `lan-desync-smoke.ps1` no longer needs editing per machine — it derives the
> project path from its own location and probes for the Godot binary (`-GodotExe <path>` overrides).
> The old hardcoded `$Godot`/`$Proj` are gone.

---

## 2. Topology (pinned — Story 1.9b D3)

There is no listen-server mode, and the gate requires the **server-collected** checksums (the 1.9a quorum
lives in the dedicated server). So the run is **3 processes across 2 machines**:

```
  Machine A (192.168.1.100, say)          Machine B
  ┌───────────────────────────┐           ┌───────────────────────┐
  │  [1] dedicated SERVER      │◄──LAN─────│  [3] client 2         │
  │  [2] client 1 (→127.0.0.1) │           │     (→192.168.1.100)  │
  └───────────────────────────┘           └───────────────────────┘
```

The proof is real: **client 1 (Machine A)** and **client 2 (Machine B)** run independent sims on
different physical machines; the server compares their checksums. The server co-locating with client 1
on A is irrelevant — in 1.9a the server is the **arbiter**, it does not tick a match.

Find Machine A's LAN IP: run `ipconfig` on A and read the active adapter's **IPv4 Address**
(e.g. `192.168.1.100`).

---

## 3. Scenario (canonical "P2.4")

The match scenario is the `ScenarioPath` **export** on the `MainScene` root node — **not** a lobby map
picker. Both clients load whatever `ScenarioPath` points to.

- **Canonical P2.4 = `res://resources/data/scenarios/map_02_iron_crossing.json`** (symmetric 2-player —
  no advantaged slot; economy + combat to exercise the sim). **As of 2026-08-07 this is COMMITTED as the
  `ScenarioPath` override on the `MainScene` root in `scenes/main.tscn`**, so both machines pick it up
  from git and there is nothing to set by hand. Verified live: the client logs
  `[MainScene] Loaded scenario: "Iron Crossing" (map_02_iron_crossing)`.
- **Expected hashes with the canonical scenario** — both machines must print these identically at boot,
  and both lobby slots must Ready at the same match-agreement hash:

  | | |
  |---|---|
  | Scenario hash | `0xCF0128F3` |
  | Start-state hash (algo v2) | `0x5231ED0610A3186A` |
  | Match-agreement hash (algo v3) | `0x771C516961CEBD73` |

  If a machine prints `0x8D79360D` / `Alpha Skirmish`, it is on the **old default** — it has not pulled,
  or its `main.tscn` was overwritten (see the warning below).
- **The old zero-config fallback was `alpha_map_01.json`**, the C# default in `MainScene.cs:218`. Note it
  is **asymmetric** — slot 0 starts with 200 ore + 100 crystal, slot 1 with only 100 ore — which is why
  it is not the canonical gate scenario. (Both scenarios are verified valid + deterministic by
  `CanonicalScenarioTests`.)

> ⚠ **If the Godot editor has `main.tscn` open, do not save the scene** until you reload it. The editor
> holds the version it loaded at startup; saving would write that back and silently drop the committed
> `ScenarioPath` override, putting that machine back on `alpha_map_01` while the other stays on Iron
> Crossing — a guaranteed content-hash mismatch at Ready.

> **CRITICAL invariant:** both machines must use the **same** `ScenarioPath`. Different scenario files =
> guaranteed desync. The lobby helps catch this — at Ready it compares scenario hashes and **blocks** the
> match when **both** sides report a valid, **non-zero** hash that disagree ("Your map: 0x… / Peer map: 0x…").
> **Caveat — fail-open:** if either side's scenario failed validation it publishes hash `0`, and a `0` hash is
> **not** blocked (the strict content-sync handshake is Epic 9 / Story 9-4). So do **not** treat the lobby as a
> guarantee: confirm by eye that **both** lobby map hashes are identical **and non-zero** before you Ready.
> If you see the mismatch block, fix `ScenarioPath` to match on both machines.

---

## 4. Run the match

### Push-button (auto-join) — fastest

On **Machine A** (run each in its own PowerShell):
```powershell
# [1] the dedicated server — BLOCKS: this window IS the server, and its output is the verdict.
powershell -File godot/tools/lan-desync-smoke.ps1 -Role server
# [2] client 1, joining the local server (a SECOND window — [1] never returns)
powershell -File godot/tools/lan-desync-smoke.ps1 -Role client -ServerIp 127.0.0.1
```
On **Machine B** (use Machine A's LAN IP):
```powershell
powershell -File godot/tools/lan-desync-smoke.ps1 -Role client -ServerIp 192.168.1.100
```
Both clients auto-join and auto-ready; the server broadcasts StartGame and the match begins.

Every process also writes a timestamped log to `<repo>/lan-logs/` (gitignored) — `…-server.log` and
`…-client-<ip>.out.log`. Read those instead of scrolling a console, especially on a remote session.

> **The server role runs `--headless` in the foreground.** Before 2026-08-07 it was launched detached
> via `Start-Process`, which sent its stdout nowhere — the `[Determinism]` verdict this whole runbook
> exists to read was unobservable, and every run leaked an orphan holding UDP 7777 (DW-906).

### Manual lobby — if you want to confirm the content-sync hash by eye

Launch the server as above. On each client, run `godot --path godot` (no `--autojoin`), open the lobby,
enter Machine A's IP in the host-IP field, click **Join**, confirm the **map-hash line matches** the
peer, then **Ready**.

### Play

**First, click `Continue` on the pre-match briefing on BOTH clients.** Its scrim has
`MouseFilter = Stop`, so until you dismiss it every click is swallowed and the match looks unresponsive.
(The sim itself is never gated by the briefing — `MatchBriefingOverlay.Dismiss` is presentation only —
so an undismissed briefing does *not* stall lockstep. It just means you cannot play.)

Play a **full match — at least 300 ticks (≈10 s), but several minutes is better**. Move units, fight,
build — give the sim real work. Let the match run long enough that the **adaptive input delay** settles
(see §6).

> **Ticks are not wall-clock.** Under lockstep the tick counter only advances when *every* peer submits,
> so a stalled peer freezes the count while real time keeps passing. Five minutes that produced ~100
> ticks does not mean the match was fast — it means a client was barely processing. Judge progress by
> the server's window count, not by how long you waited.

---

## 5. Read the verdict

- **Server console — the window you launched `-Role server` in** (it blocks; it *is* the server), and
  the same text in `<repo>/lan-logs/<stamp>-server.log`. Expect a stream of
  `[Determinism] tick N: all 2 peers matched 0x........ (window #k)` lines — one per comparison window,
  with **no** `DESYNC`/`HALT` line. On match end (or when you close a client), the server prints
  `[Determinism] MATCH SUMMARY: {k} windows compared, 0 desync, 0 abandoned — PASS.`
  A healthy start also shows slot assignment and quorum:
  `Peer connected → slot 0 (Player)` · `Slot 0 connected → assigned Player1` · `State → Lobby (2/2 …)` ·
  `Slot N is Ready (protocol v3, match hash 0x…)` — **both slots must report the same match hash** ·
  `All 2 players ready — broadcasting StartGame. Match begins (quorum N=2).`
- **Each client HUD (top line):** `… Hash 0x........  ONLINE`. The hash on **both** machines must be the
  **same value** at the same tick, every window.

If the summary says `… 0 desync, 0 abandoned — PASS` and the HUD hashes matched throughout →
**determinism PASS.**

A non-zero **abandoned** count (DW-239) is not a desync — it means a peer fell a full ring-window behind and
those ticks were never compared at all, so the PASS covers less of the match than `{k}` suggests. Each one
also prints its own `[Determinism] tick N: comparison window ABANDONED …` line.

---

## 6. Watch-item: adaptive input delay (Story 1.9b D9)

Lockstep starts at 4 ticks of input delay and adapts **down toward 2** on a low-RTT LAN. Within a few
seconds both machines should log `[Lockstep] RTT sample: Xms` and a `[Lockstep] Delay: 4 → 2 ticks`
transition. **Determinism must hold across that change** — confirm no desync appears around the delay
reduction. Play long enough for it to happen.

Server-side the same negotiation appears as a three-line cycle per change, and all of it must complete:
`Dictating input delay → N ticks, applyAtTick T (awaiting all-2 ACK)` → `Delay change committed → N ticks
at tick T (all 2 players ACKed)`.

**Use the delay as a health gauge.** On a working LAN it settles at 2–3. If you see it climb to
**`MAX_DELAY` = 12**, round-trip latency has collapsed — on a wired/Wi-Fi LAN that almost never means the
network, it means a **client stopped processing** (backgrounded remote session, minimised window, machine
asleep). Expect a peer drop shortly after. Observed exactly this on 2026-08-07: `→ 12 ticks at tick 99`,
then `Player2 dropped mid-match — freezing at tick 114`.

---

## 7. HALT drill (AC4 second half — the desync→HALT path over the wire)

With both clients in a live match, click **one** client window and press **F9** (a DEBUG-only divergence
inducer that nudges that peer's sim). Expected:
- Server console: `[Determinism] tick N: GLOBAL DESYNC — no canonical hash. Broadcasting terminal HALT.`
- **Both** clients show the red terminal **"MATCH HALTED"** overlay (distinct from the transient stall
  banner), offering "Return to Menu".

That confirms a real divergence is detected, attributed, and terminated end-to-end across two machines.

---

## 8. Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| Client B can't connect | Firewall on A blocking UDP 7777 (allow it); wrong IP (re-check `ipconfig` on A); not on the same subnet. |
| Lobby blocks Ready with a map-hash mismatch | The two machines have different `ScenarioPath` (or different repo state), both with valid non-zero hashes. Make them identical and rebuild. **Note:** a `0` hash (a validation-rejected scenario) is fail-open and is **not** blocked — verify both hashes are non-zero by eye. |
| Match never starts | Both players must be connected **and** ready. With auto-join this is automatic; in manual lobby, both must click Ready. |
| Leftover/stale Godot windows holding the port | The launcher now cleans stale instances **by default**, role-aware (`-Role server` kills only servers, `-Role client` only clients, never the editor). `-NoClean` opts out. |
| `ERROR: Couldn't create an ENet host` / `Failed to listen on port 7777: CantCreate` | A previous server is still running and holding UDP 7777. The launcher's pre-flight check now names the owning PID; otherwise `Get-NetUDPEndpoint -LocalPort 7777` then `Stop-Process -Id <pid> -Force`. |
| Immediate desync at tick 0 | Different scenario or different build/commit on the two machines. Re-sync the repo + rebuild on both. **Also check Terrain3D loaded on both** (§1.5) — a machine with the addon disabled derives different terrain. |
| Peer dropped mid-match at a low tick, delay pinned at 12 | A client stopped processing — backgrounded/minimised remote session, machine slept, or window occluded. Not a network fault. See §1.6. |
| `MATCH SUMMARY … PASS` but with 1–2 windows | **Not a pass.** The run aborted before it compared anything meaningful. See §0. |
| Clients ignore all mouse input | The pre-match briefing is still up; its scrim swallows clicks. Click **Continue** on each client. |
| Server log shows `Slot N disconnected.` twice per peer | Cosmetic double-report seen on 2026-08-07; ENet appears to surface both a disconnect and a timeout event. Does not affect the freeze path, which committed correctly. |

---

## 9. Record the result

When this gate is run, record in the Story 1.9b **Change Log**:
- date, the two machines + LAN, the scenario used, the final `MATCH SUMMARY` line (window count + PASS),
  confirmation the HUD hashes matched, the adaptive-delay transition observed, and the F9 HALT drill
  result on both machines.

That closes FR-39 — the #1 ship risk.

---

## 10. What the 2026-08-07 first attempt already established

The gate is **NOT** closed — both attempts aborted early (1 comparison window) because the driving
remote-desktop sessions backgrounded. But these were observed live across two physical machines
(PC / RTX 3060 / Player1 ↔ laptop / GTX 1650 / Player2, `alpha_map_01`, commit `289e795f`), and do not
need re-deriving:

| Observed | Evidence |
|---|---|
| Content agreement across machines | Both slots Ready at match hash `0xBE1EFB623E0049E8`; scenario `0x8D79360D`, start-state `0x013912C889112CC0` (algo v2), and the full content breakdown identical on both |
| Server slot assignment + quorum start | `slot 0 → Player1`, `slot 1 → Player2`, `State → Lobby (2/2)`, `All 2 players ready — broadcasting StartGame (quorum N=2)` |
| Cross-machine sim checksum agreement | `[Determinism] tick 60: all 2 peers matched 0xE4FE8ED9 (window #1)` |
| Delay negotiation with full ACK round-trip | Three `Dictating → applyAtTick → committed (all 2 players ACKed)` cycles (2, 12, 11 ticks) |
| Adaptive delay adapting to real conditions | `4 → 2` on healthy LAN, `→ 12` (MAX) under a stalled peer, `→ 11` recovering — closes the pending checklist at `Snapshot.md:204-215` |
| Drop → freeze-and-continue (Story 9.5) | `Player2 dropped mid-match — freezing at tick 114, awaiting survivor ACK(s). Match continues.` then `Freeze committed for slot 1 … Injecting empty commands; quorum reduced.` |

**Still unproven, and the reason the gate stays open:** sustained agreement over 300+ ticks / ≥5 windows,
and the F9 HALT drill (§7), which has never been fired across two machines.

**Known hazard for the next run:** `AiOpponentSystem` plays `Faction.Player2` unconditionally
(`AiOpponentSystem.cs:32`, registered as system [14] in every `SimulationHost`, `AiDifficulty` has no
`None`), so in a 2-human match the AI **co-pilots the human Player2's faction** — and its scorer runs on
`float`, which its own docs (`AiOpponentSystem.cs:253-254`) state does not unblock lockstep MP pending
the float→Fixed migration (DW-204 / Story 10.11). A long run is therefore also a live test of whether
that float scorer diverges across two different CPUs. If a desync appears with no F9 pressed, suspect
this first.
