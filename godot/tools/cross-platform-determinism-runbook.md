# Cross-Platform Determinism Runbook — Windows↔Linux golden gate (Story 1.10c, AR-37)

**The cross-platform half of the determinism floor.** Prove that the simulation produces
**byte-identical `Fixed` checksums on Linux as on Windows**, by running the Tier-1
golden-checksum harness on **both** OSes and confirming the per-tick checksum sequences match.
This is the only real proof Fixed-point determinism holds across the Windows client ↔ Linux
dedicated-server boundary — a float or culture leak that rounds differently per-platform would
silently desync multiplayer, and **this gate is what catches it**.

Unlike the LAN gate (Story 1.9b, which needs two physical machines and is parked), this gate
**can be fully closed on one machine** using the existing WSL/Ubuntu. It is an
AI-orchestrated/­manually-triggered check (AR-37: *not* always-on cloud CI), plus an optional
always-on `ubuntu-latest` CI backstop (see §8).

> **Status:** engineering complete and **run GREEN 2026-06-25** (see the Story 1.10c Change Log).
> Re-run it before cutting any release (§7), and any time you touch `Fixed` math, the JSON→`Fixed`
> boundary, `SimChecksum`, or anything in the hashed tick path.

---

## 0. What "PASS" means

- **Cross-platform PASS:** the Tier-1 golden-checksum suite is **green on Windows AND on Linux
  (WSL/Ubuntu)** in **verify mode**. Because both OSes verify against the *same* committed
  `*.golden.txt` files — which **are** the Windows-recorded sequences — a green Linux run proves,
  tick by tick, that **Linux-computed checksums == committed golden == Windows-computed checksums**
  for all **four** goldens. The check script prints:
  `✅ Windows<->Linux byte-identical … legs: Windows=PASS, WSL=PASS` and exits `0`.
- **FAIL:** either leg is red. The Linux leg surfaces the **first diverging tick**
  (`Checksum drift at tick N: expected 0x… actual 0x…`) and the script exits non-zero. A red Linux
  leg is a **real cross-platform determinism bug** (see §5) — **never** re-record a golden to hide it.

---

## 1. Prerequisite (one-time): .NET 8 SDK in WSL

The Linux leg needs a .NET 8 SDK that satisfies the repo-root `global.json` (pins **8.0.419**,
`rollForward: latestFeature`). Ubuntu 24.04's built-in `apt` feed ships **8.0.1xx** — *below* the
floor — and needs `sudo`. So install via the no-sudo script, which uses `dotnet-install.sh`
(always the latest `8.0.x`, installed to `~/.dotnet`):

```powershell
wsl -d Ubuntu-24.04 -- bash /mnt/d/Projects/Project_Chimera/godot/tools/wsl-dotnet-setup.sh
```

Idempotent; safe to re-run. It also adds `~/.dotnet` to `PATH` in `~/.profile` + `~/.bashrc`.
Verify:

```powershell
wsl -d Ubuntu-24.04 -- bash -lc 'cd /mnt/d/Projects/Project_Chimera && dotnet --version'   # expect 8.0.4xx
```

> **Do NOT** "fix" a `global.json` resolution failure by editing `global.json` — that pin is shared
> with the Windows CI. Install the right SDK in WSL instead.

---

## 2. How the gate works (the transitive diff)

You are **not** building a cross-machine sequence-differ. The golden harness was already engineered
to be cross-platform-safe — goldens are **embedded resources** (no file-path/line-ending fragility),
parsing is `InvariantCulture`, separators are explicit `'\n'`, the parser strips `\r`, and
`SimChecksum` hashes only integer `Fixed.Raw` via manual little-endian byte-mixing (no `BitConverter`,
no float). So the *transport* is already neutral; the only thing that can differ is the **computed hash**.

The committed goldens were **recorded on Windows**. The check therefore runs the existing **verify-mode**
suite on Linux: `GoldenChecksumReplay.CompareSequences` (exact per-tick `uint` equality) compares the
**Linux-computed** sequence against the **committed (Windows) golden**. Green ⇒ the two are byte-identical.
`CompareSequences` **is** the diff. (This is the "transitive" realization; a literal record-both-then-diff
would need a non-destructive dump seam and adds nothing here.)

**Isolation:** the Linux leg builds inside a **WSL-native clone** of committed `HEAD`
(`~/chimera-xplat-check`), never the shared `/mnt/d` tree — so it never reuses Windows `obj/bin`
(which causes CS0579 duplicate-`AssemblyInfo`/stale-output hazards on the 9p mount) and **never
disturbs your Windows build**. The clone tests committed `HEAD`; at release time that == your working
tree. (The always-on `ubuntu-latest` CI leg in §8 covers the full pushed working tree on Linux.)

---

## 3. Run it

```powershell
powershell -File godot/tools/cross-platform-determinism-check.ps1
```

It runs **both** legs (Windows working tree + WSL clone of `HEAD`) in verify mode and prints a verdict.
Useful switches: `-WslDistro <name>` (default `Ubuntu-24.04`), `-SkipWindows`, `-SkipWsl` (diagnostics).

What it does, per leg: `dotnet restore … --locked-mode` then `dotnet test … -c Release --no-restore`
against `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` **by path** — no Godot, no
`CHIMERA_GOLDEN_RECORD` (the script hard-refuses if that env var is set).

---

## 4. Read the verdict

- **Green:** `✅ Windows<->Linux byte-identical … legs: Windows=PASS, WSL=PASS`, exit `0`. Done — the
  four goldens produce identical per-tick `SimChecksum` sequences on both OSes.
- **Red:** `❌ CROSS-PLATFORM DESYNC — a leg FAILED.` plus the first-divergence line from the Linux leg,
  and a non-zero exit. Go to §5.
- Confirm no golden moved: `git status --short -- '*.golden.txt'` should be **empty** (the gate never
  records).

---

## 5. If it's RED — it's a real cross-platform determinism bug

**Do NOT re-record a golden.** A red Linux leg means the sim computed a *different* `Fixed` value on
Linux than the committed Windows baseline — exactly the silent-MP-desync class this gate exists to catch.
Work the suspects in this order (highest-probability first), using the reported diverging tick:

1. **`Fixed.FromFloat` at the JSON deserialize boundary** (`FixedJsonConverter.Read`: `(float)d` then
   `Fixed.FromFloat` → `(int)(value * 65536)`). The `golden-applier-scenario` golden exercises this — it's
   the one float-touch in a hashed golden's *setup*, so it's the prime suspect.
2. **An unexpected `float`/`double` or `CultureInfo`-sensitive parse reaching the hashed path.** The
   determinism analyzer (Story 1.10b, `CHM0001`/`CHM0005`/`CHM0006`) is the static backstop; this gate is
   the runtime backstop for what slips past it.
3. **AI float scoring** — *only if a new AI-active golden was added.* `AiOpponentSystem` uses raw `float`
   in the live tick path (see §6); a golden that drives the AI to actually act could diverge here.

Reproduce a single leg with `-SkipWindows` (Linux only) to iterate. Fix the **code**, **commit it**
(the WSL leg builds a clone of committed `HEAD` — an *uncommitted* fix is **not** tested; see §2), then re-run §3.

---

## 6. Coverage caveat (read this — it bounds what the gate proves)

The four current goldens deliberately keep the **AI quiescent** (`GoldenScenario` starves it: no
production building + zero ore → below the attack threshold), so `AiOpponentSystem`'s `float` scoring
**runs but never changes a hashed value** in any current golden. Therefore this gate proves cross-platform
parity **only for the non-AI-float paths the four goldens exercise**. Its value **scales with golden
coverage**: the known `AiOpponentSystem` `float`→`Fixed` debt (advisory `CHM0005`/`CHM0001`; tracked in
`deferred-work.md`) is the latent hazard a *future* AI-active golden would expose here. That migration is
its own later work — **out of scope for this gate**; do not widen scope to fix it. Just know the boundary.

---

## 7. Record the result (and the release gate)

When you run this gate, record in the relevant story's **Change Log** (Story 1.10c for the first run):
date, the WSL distro + installed `.NET` version, the verdict line (`Windows=PASS, WSL=PASS`), and
confirmation that `git status --short -- '*.golden.txt'` was empty.

**Release gate (hard-on-release, AR-37):** CI cannot run WSL, so for the WSL leg "hard enforcement on a
release branch" = a **release-checklist step**: this gate must be **green before cutting any release**.
(The optional `ubuntu-latest` CI leg in §8 makes the *cloud* Linux signal automatic on every push, but the
WSL leg — which tests your *real* Linux dedicated-server host — stays a manual release-checklist gate.)

---

## 8. The always-on `ubuntu-latest` CI backstop (Story 1.10c, Decision #1 = both)

In addition to this WSL gate, `.github/workflows/determinism-gate.yml` carries a sibling job
**`tier1-golden-gate-linux`** (`ubuntu-latest`) that runs the **same** Tier-1 golden suite on Linux on
**every push** — a continuous cross-platform signal between your manual WSL runs (Ubuntu Actions bill 1×
vs Windows 2×, so it's near-free). It directly answers AR-37's own residual risk ("the runner must have a
scheduled trigger or it silently never runs before releases"). It is **not** a substitute for this WSL
gate: the GitHub runner is a *different, ephemeral* Linux, whereas your WSL Ubuntu is *also the Linux
dedicated-server build host*.

---

## 9. Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| `dotnet: command not found` (WSL) | SDK not installed or not on `PATH`. Run §1's setup script; re-open the shell. The check script also exports `~/.dotnet` itself, so prefer re-running it. |
| `global.json … was not found` / SDK resolution error in WSL | Installed SDK below `8.0.419` (e.g. apt's `8.0.1xx`). Re-run §1's setup (installs latest `8.0.x` via `dotnet-install.sh`). Do **not** edit `global.json`. |
| `CS0579: Duplicate 'TargetFrameworkAttribute'` | A WSL build reused the Windows `obj/`. The check script avoids this by building in a WSL-native clone — don't run `dotnet` against `/mnt/d/...` by hand; use the script. |
| Red Linux leg, `Checksum drift at tick N` | A **real** cross-platform divergence — §5. Never re-record the golden. |
| `git status` shows a `*.golden.txt` changed after a run | Something recorded a golden. The gate never sets `CHIMERA_GOLDEN_RECORD`; revert the golden and find what set it. |
| WSL clone is stale / want a clean slate | `wsl -d Ubuntu-24.04 -- rm -rf ~/chimera-xplat-check` then re-run §3 (it re-clones). |
