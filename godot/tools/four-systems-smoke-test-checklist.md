# Four-Systems Smoke-Test Checklist — FR-45 (Story 1.11)

**Goal.** Point the now-trustworthy determinism harness (M1, Stories 1.1–1.10c — GREEN) at the four
never-verified systems and prove, per system, that they **function** and that **every sim-touching path is
deterministic or funnelled behind the validator gate** — so none of them can silently inject nondeterminism into
the lockstep tick. Covers **FR-45** (the four systems pass smoke-test checklists), **AR-13** (SimRng is the only
sim randomness), **AR-39** (single fail-closed `ScenarioValidator` gate).

The four systems are **heterogeneous**, so each is smoke-tested where its nondeterminism actually lives:

| System | Where its nondeterminism lives | Smoke-test strategy |
|---|---|---|
| **Utility AI** | `float` scoring INSIDE the tick, but same-machine-stable | Checksum an AI-*active* golden; prove same-machine determinism; document the cross-platform float boundary |
| **Adaptive Input Delay** | `float` + wall-clock, but NEVER enters the hashed tick | Pure-math Tier-1 gate (determinism + commutativity) + a loopback no-desync run |
| **LLM Trigger** | the LLM itself, at authoring time, OFF-tick | Prove the **gate** holds — extend `ScenarioValidator` to validate `Triggers[]` (validated-only) |
| **AI Map Generator** | the LLM itself, at authoring time, OFF-tick | Build a deterministic **procedural** sibling (SimRng-seeded) that genuinely meets "fixed seed → byte-identical" |

---

## 0. Run the whole Tier-1 smoke set

```bash
# From the repo root. Godot-free; no editor needed. Exit code is the gate (no hardcoded test count).
dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release
```

Per-system filters are given in each section below. **NEVER** set `CHIMERA_GOLDEN_RECORD` to "fix" a red run —
a red run is a real finding.

---

## 1. Utility AI — `AiOpponentSystem` (sim system index 7)

**(a) What is verified.** That the as-built utility AI (it scores 6 actions and dispatches the max each tick,
playing Player2) actually *acts* and that its decisions reach `SimChecksum` **deterministically on one machine**.
The existing goldens deliberately STARVE the AI (P2 below the attack threshold, 0 ore); this adds an AI-*active*
golden where P2 has 300 ore + a full idle wave, so the AI builds a Barracks then launches an attack — and those
mutations reach the hash transitively (building spawn → Alive/Health/ConstructionTimer; ore spend → Ore; attack
→ unit Position one tick later).

**(b) How to run.**
```bash
dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release --filter "FullyQualifiedName~AiActive"
```

**(c) PASS criteria.**
- Two in-process runs are byte-identical (`RunsTwiceInProcess_AreByteIdentical`).
- The sequence reproduces the committed `Golden/ai-active-scenario.golden.txt` **on Windows**
  (`MatchesCommittedGolden_OnTheRecordingPlatform`).
- Non-vacuity: Player2's building count grows (`AiActuallyActs_Player2BuildingCountGrows`) — the AI demonstrably
  acted, so the golden pins real behavior, not a no-op.

**(d) Caveat / owner.** ⚠ **PROVEN = same-machine AI-active determinism; NOT PROVEN = cross-platform.**
`AiOpponentSystem` scores with raw `float` and picks the winner via `float >` (`AiOpponentSystem.cs:266-271`) —
the **D2 float→Fixed debt**. The golden-match is therefore Windows-gated (`OperatingSystem.IsWindows()`), and the
AI-active golden is **deliberately EXCLUDED from the 1.10c Win↔Linux cross-platform gate** until the AI is
migrated to `Fixed`. **Do not migrate the AI here** (out of scope) and **do not add this golden to the WSL gate.**
Owner of the migration: the AI-in-lockstep work (`[[chimera-mp-disconnect-ai-takeover-reconnect]]`).

---

## 2. Adaptive Input Delay — `LockstepManager` / `DelayMath`

**(a) What is verified.** That the as-built adaptive input delay (start 4, clamp [2,12], RTT ping/pong,
`DelayProposal` agreement) negotiates **deterministically** and that a delay change does not desync peers. The
delay value never enters the hashed tick — it only shifts which buffer slot a command lands in — so the real risk
is the *apply-tick agreement*: both peers must compute the SAME delay and commit it at the SAME tick.

**(b) How to run.**
```bash
# Pure-math gate (Tier-1):
dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release --filter "FullyQualifiedName~DelayMath"

# Loopback determinism infra (real server + 2 ENet clients, in-process; DEBUG build):
dotnet build godot/godot.csproj -c Debug
"C:\Godot\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64_console.exe" --headless --path godot -- --loopback-test
```

**(c) PASS criteria.**
- Pure-math (`DelayMathTests`, 6 tests): same RTT → same clamped delay; `ComputeTargetDelay(0)=MIN_DELAY=2`,
  `ComputeTargetDelay(huge)=MAX_DELAY=12`; the RTT→delay table is monotonic and in-clamp; `AgreeDelay` is
  **commutative** (`AgreeDelay(a,b)==AgreeDelay(b,a)` — both peers converge to the same delay).
- Loopback self-test prints `RESULT: PASS` / exits 0 (5 clean all-peers-MATCH windows, then both clients HALT on
  an induced divergence) — the real server→verdict path is green after the `DelayMath` extraction.

**(d) Caveat / owner.**
- ⚠ **AC2c — the unclamped receipt is documented, NOT fixed.** `AgreeDelay` (was `Math.Max(myDesired,
  theirDelay)` at `LockstepManager.cs:556`) does **not** re-clamp the untrusted wire byte, so a forged proposal
  could push the delay past `BUFFER_SIZE`. The pure-math test asserts this CURRENT behavior so it will flag the
  day **Story 9.4** (server-dictated delay + receipt re-clamp + ACK-commit) changes it. **Owner: Story 9.4.**
- ⚠ **The live `4→2` delay-transition watch is PARKED (1 machine).** The pure-math gate proves the desync-risk
  surface (both peers compute the same delay); the headless self-test proves the loopback infra; but observing
  the live transition with both peers' per-tick checksums matched across the change needs the 3-window
  `loopback-desync-smoke.ps1` (interactive) or a 2nd LAN box — **same parked posture as 1.9b AC4.** Add the
  transition watch to `lan-determinism-runbook.md` §6 (it already tracks "delay adapts down toward 2 … confirm
  no desync") when a 2nd machine exists.

---

## 3. LLM Trigger — `ScenarioValidator` (validated-only ingestion)

**(a) What is verified.** That LLM/editor-authored triggers can no longer reach the tick unvalidated. **This was a
real gap:** an accepted trigger was written straight into `ScenarioData.Triggers[]` and reached `ScenarioDirector`
*without* passing `ScenarioValidator` (the only check was a bypassable, auto-fixing, presentation-side
`LLMService.Validate`). Decision #1 = **extend the AR-39 gate** to validate `Triggers[]`, so non-deterministic /
crash-inducing trigger content is rejected before any tick.

**(b) How to run.**
```bash
dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release --filter "FullyQualifiedName~TriggerValidation"
```

**(c) PASS criteria.** `TriggerValidationTests` (12 tests + 1 skip):
- A well-formed trigger passes and mints a `Validated<T>` wrapping the same model.
- Each malformed case is rejected with a single located `scenario.triggers[...]` error: unknown event/condition/
  action **type**, faction slot out of `[0,3]` (the `(Faction)(slot+1)→Ore[idx]` OOB), unknown `building_type`,
  invalid operator, out-of-range / out-of-bounds spawn coordinate, dangling `timer_expires`→`create_timer` ref,
  null `Triggers[]`, and purity (no throw) on null sub-arrays.
- No-bypass: `ValidatedMintingTests`' source scan stays green (the change adds no new `new Validated<`).

**(d) Caveat / owner.**
- ⚠ **Residual editor-accept routing seam (Decision #1 follow-up).** The *gate logic* now rejects bad triggers,
  but fully closing the ingestion seam — routing `TriggerEditorPanel.OnAcceptPressed` (`:328`, appends straight
  to `_scenario.Triggers`) and the file-load path through the validator — is a small Godot/editor-side follow-up.
  In scope here: the validator logic + its assertions. **Owner: a Decision #1 follow-up story.**
- ℹ **AR-13 random-effect rule stays RESERVED** — no random trigger-effect *type* exists pre-Epic-2, so there is
  nothing to validate yet (documented as an xUnit `Skip`). **Owner: Epic 2 / Story 2.3** (the effect-validator).

---

## 4. AI Map Generator — LLM path (untouched) + new procedural `ProceduralMapGenerator`

**(a) What is verified.** The as-built generator is **LLM-driven with zero RNG** — there is nothing to seed, so
the AC's "fixed seed via SimRng → byte-identical map" described a generator that did not exist. Decision #2 =
**Option B**: BUILD a new Godot-free, `SimRng`-seeded, integer-only **procedural** generator (a SIBLING to the LLM
path) that genuinely satisfies it. The LLM "describe a map in words" generator is **untouched**.

**(b) How to run.**
```bash
dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release --filter "FullyQualifiedName~ProceduralMapGenerator"
```

**(c) PASS criteria.** `ProceduralMapGeneratorTests` (7 tests):
- Same seed, twice → byte-identical serialization AND equals the pinned FNV-1a golden hash `0xB46313CA`
  (a JSON-format-drift tripwire).
- A different seed → a different map (Id/DisplayName are seed-independent, so the difference is purely the
  generated geometry — proof the seed drives generation).
- Every generated `ScenarioData` (5 seeds incl. 0 and `ulong.MaxValue`) passes `ScenarioValidator`.
- `ProceduralMapGenerator.cs` is **analyzer-clean** (no `float`/`System.Random`/Godot RNG in the generation path —
  the 1.10b analyzer over `src/Core/**` enforces this).

**(d) Caveat / owner.**
- ℹ The **LLM generator stays the authoring-only / non-deterministic "describe a map in words" path** (no RNG to
  seed). The procedural generator is the deterministic "fixed seed via SimRng" path — so FR-45's map-gen
  determinism is now genuinely satisfied, not documented-as-N/A.
- ℹ **Note for later (NOT wired here):** because generation is integer/`Fixed`-only, the procedural generator's
  golden **could** later join the 1.10c WSL cross-platform gate (unlike the AI-active golden, which stays off it).
- ℹ **AC4c presentation wiring (a "Generate procedural (seeded)" button) is DEFERRED** (optional per the AC) — the
  Godot-free core is callable as-is; the button is a thin follow-up for a map-gen UX story.

---

## 5. Per-system verdict (mirror this in the story Change Log)

| System | Verdict | What's proven | Residual gap → owner |
|---|---|---|---|
| **Utility AI** | ✅ (same-machine) | AI-active golden: 2-run identical + golden-match (Win) + non-vacuity | float→Fixed cross-platform = **D2**; golden excluded from WSL gate |
| **Adaptive Input Delay** | ✅ (math + infra) / ⚠ (live transition parked) | `DelayMath` determinism + commutativity; loopback self-test PASS | unclamped receipt → **Story 9.4**; live 4→2 watch parked (1 machine) |
| **LLM Trigger** | ✅ | `Triggers[]` validated-only; malformed rejected, located; no-bypass | editor-accept routing → **Decision #1 follow-up**; AR-13 → **Story 2.3** |
| **AI Map Generator** | ✅ | procedural seed → byte-identical + validates; LLM path documented | presentation button **deferred**; could join WSL gate later |

## 6. Caveat cross-link index

- **AI float→Fixed (D2)** — same-machine-only AI determinism; AI-active golden excluded from the WSL gate.
  See `[[chimera-mp-disconnect-ai-takeover-reconnect]]`, the 1.10c story, and `deferred-work.md`.
- **Delay receipt re-clamp** — the unclamped wire byte in `DelayMath.AgreeDelay` → **Story 9.4**
  (server-dictated delay + ACK-commit). The live transition run is parked with the 1.9b AC4 LAN gate.
- **LLM-trigger editor-accept routing** — the validator logic ships now; routing both editor-accept and
  file-load ingestion through the gate is the **Decision #1 follow-up**.
- **Map-gen procedural generator** — **Decision #2 / Option B**: the new deterministic sibling to the untouched
  LLM path. Its golden could later join the cross-platform gate (integer-only).
