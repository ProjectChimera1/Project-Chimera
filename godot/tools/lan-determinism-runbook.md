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
  no advantaged slot; economy + combat to exercise the sim). To use it: open `scenes/main.tscn` in the
  Godot editor, select the `MainScene` root, set **ScenarioPath** to
  `res://resources/data/scenarios/map_02_iron_crossing.json`, and save. Commit it so **both** machines
  pick up the identical value (or set it identically on each).
- **Zero-config fallback = `alpha_map_01.json`** — the `ScenarioPath` **default**. If you change nothing,
  both machines load this. (Both scenarios are verified valid + deterministic by
  `CanonicalScenarioTests`.)

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
# [1] the dedicated server
powershell -File godot/tools/lan-desync-smoke.ps1 -Role server
# [2] client 1, joining the local server
powershell -File godot/tools/lan-desync-smoke.ps1 -Role client -ServerIp 127.0.0.1
```
On **Machine B** (use Machine A's LAN IP):
```powershell
powershell -File godot/tools/lan-desync-smoke.ps1 -Role client -ServerIp 192.168.1.100
```
Both clients auto-join and auto-ready; the server broadcasts StartGame and the match begins.

### Manual lobby — if you want to confirm the content-sync hash by eye

Launch the server as above. On each client, run `godot --path godot` (no `--autojoin`), open the lobby,
enter Machine A's IP in the host-IP field, click **Join**, confirm the **map-hash line matches** the
peer, then **Ready**.

### Play

Play a **full match — at least 300 ticks (≈10 s), but several minutes is better**. Move units, fight,
build — give the sim real work. Let the match run long enough that the **adaptive input delay** settles
(see §6).

---

## 5. Read the verdict

- **Server console (Machine A's server window):** a stream of
  `[Determinism] tick N: all 2 peers matched 0x........ (window #k)` lines — one per comparison window,
  with **no** `DESYNC`/`HALT` line. On match end (or when you close a client), the server prints
  `[Determinism] MATCH SUMMARY: {k} windows compared, 0 desync — PASS.`
- **Each client HUD (top line):** `… Hash 0x........  ONLINE`. The hash on **both** machines must be the
  **same value** at the same tick, every window.

If the summary says `… 0 desync — PASS` and the HUD hashes matched throughout → **determinism PASS.**

---

## 6. Watch-item: adaptive input delay (Story 1.9b D9)

Lockstep starts at 4 ticks of input delay and adapts **down toward 2** on a low-RTT LAN. Within a few
seconds both machines should log `[Lockstep] RTT sample: Xms` and a `[Lockstep] Delay: 4 → 2 ticks`
transition. **Determinism must hold across that change** — confirm no desync appears around the delay
reduction. Play long enough for it to happen.

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
| Leftover/stale Godot windows holding the port | Close them, or run any launcher with `-CleanFirst` once before the fresh server launch (kills only `--server`/`--autojoin`/`--headless` instances, never the editor). |
| Immediate desync at tick 0 | Different scenario or different build/commit on the two machines. Re-sync the repo + rebuild on both. |

---

## 9. Record the result

When this gate is run, record in the Story 1.9b **Change Log**:
- date, the two machines + LAN, the scenario used, the final `MATCH SUMMARY` line (window count + PASS),
  confirmation the HUD hashes matched, the adaptive-delay transition observed, and the F9 HALT drill
  result on both machines.

That closes FR-39 — the #1 ship risk.
