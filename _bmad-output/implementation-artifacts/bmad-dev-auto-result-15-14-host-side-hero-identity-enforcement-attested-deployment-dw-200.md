---
status: blocked
story: 15.14
ledger: DW-200
---

# BMad Dev Auto Result — Story 15.14 / DW-200

Host-side (ENet `DedicatedServer`) hero identity enforcement + deterministic cross-peer in-match deployment of the attested online hero.

## Auto Run Result

Status: blocked
Blocking condition: unresolvable hard dependency + required human architecture decision — server-side Nakama trust does not exist in the current infra, no viable trust mechanism is specified by the decision, and the full-rail behavior cannot be built-and-verified unattended (needs a live two-client Nakama match; the in-engine bridge is single-client and no Nakama is hosted). Same class as Story 15.1 ("DEFERRED; needs a human, not bmad-loop").

**Why this halts before a spec was written.** The intent is clear and fully specified (DW-200 + spec-9-12's Design Notes + Alec's 2026-07-30 decision to "build the full rail"). It did not fail on ambiguity. It fails the READY-FOR-DEVELOPMENT standard on **Sufficient** (a known dependency gap that cannot be resolved unattended) and **Testable / Surface-anchored** (the outermost surface the intent lives at is unobservable in this environment), and the residual gap is a decision reserved to Alec, not a spec-quality defect dev-auto may repair.

### Evidence (investigated this run, cited to code)

DW-200's decision enumerates five components: (a) client→server attestation packet with a Nakama-issued credential; (b) server-side Nakama trust to verify it; (c) userId→slot bind in `AssignedRoster.TryFreeze`; (d) fail-closed `HandleReady` gate; (e) server distribution of all peers' profiles → identical multi-hero `HeroStore` (StartStateHash re-baseline).

1. **Server-side Nakama trust (b) has no foundation and no specified mechanism.**
   - `DedicatedServer.cs` has zero Nakama surface (grep: no `Nakama`/`ISession`/`AuthToken`). It knows a peer only as a transport-slot int (`HandleConnect(int slot)` :289; `SLOT_FACTION[slot]` :317). No per-slot identity is captured anywhere.
   - The Nakama→ENet handoff `MatchFoundInfo` (`NakamaService.cs:348-351`) carries **endpoint only** (`ServerIp`, `ServerPort`) — no userId, no session token. The client holds a full session (`_session.UserId`/`AuthToken`) but never forwards it over ENet.
   - Nakama is **not hosted**: `docs/server-deploy/` is a local docker-compose recipe (`nakama:3.22.0`) an operator provisions on a self-run VPS; the hero-profile RPC module is a **gitignored, unbuilt** TS bundle (`README.md:145-165`) — Nakama refuses to start until a manual `npm build`.
   - Consequence: every viable trust mechanism is an unmade architecture decision that drags in deferred infra — (i) the dedicated server becomes a Nakama client and validates the session token via a live Nakama call (requires hosted, reachable Nakama), or (ii) Nakama's module signs a JWT credential the server verifies against a shared key (requires Nakama module changes + shared-secret distribution + matching deployed config). Both contradict spec-9-12's Boundary "Do not deploy/host Nakama or edit production infra." Choosing between them is Alec/architect territory.

2. **The behavior cannot be verified unattended.** Host enforcement is only meaningful across **two** ENet peers presenting **distinct real Nakama credentials** — the point is that a binary-patching friend gets rejected. The in-engine gate is single-client (one godot-mcp bridge) and there is no hosted Nakama. DW-435 already recorded that even the 9-12 *client* rail's reachability "needs a live two-client Nakama match" the sandbox cannot run. There is no path to a green, non-fabricated verification artifact for (a)–(d) here.

3. **The determinism half (e) is real but isolated, and dormant without enforcement.** `HeroStore` is folded into `SimChecksum` (`SimChecksum.cs:548-573`, v12) and `StartStateHash` (`StartStateHash.cs:76-87`, `AlgoVersion` 2), which `MatchAgreementHash` folds and the fail-closed handshake gates before tick 0 (`HandshakeGate.CheckStart`; server `CheckStartStateAgreement`). A cross-peer multi-hero fold bumps `StartStateHash.AlgoVersion` and re-records the hero start-state golden plus the pinned tripwires; if the per-tick hero fold shifts, it re-baselines all ~33 `*.golden.txt` (precedent: spec-4-10). Per the project's batch rule and epic-15-context, that re-baseline is an isolated story on its own. Shipping it *without* the enforcement it serves (because enforcement is unverifiable) is a large golden churn for a dormant half-feature — the exact "dormancy" defect 9-12's review rejected.

### What IS ready, if Alec wants to split the work (his call, not taken here)

The Godot-free, Tier-1-verifiable slice — the multi-hero `HeroStore` fold + `StartStateHash`/`MatchAgreementHash` re-baseline, the attestation packet serialization, the `AssignedRoster` userId→slot bind, and the `HandleReady` gate *predicate* — could be specced and unit-tested in isolation. It is deliberately **not** specced this run because (1) it leaves the rail dormant/unenforced end-to-end, and (2) DW-200's recorded decision is "build the full rail" — narrowing it is a scope decision reserved to Alec (memory: "close-vs-build is Alec's call").

### Decisions needed from Alec to unblock

1. **Trust mechanism:** live server→Nakama token validation, or a signed-JWT shared-secret scheme? Each implies a different (and previously deferred) infra commitment.
2. **Host Nakama for real?** DW-200 cannot be verified without a running, configured Nakama and a two-client test. Is standing that up in scope now, or does 15.14 defer like 15.1 (needs a human)?
3. **Scope:** full rail in one story, or split off the Tier-1 determinism foundation (fold + re-baseline + packet/bind/gate predicate) as its own isolated story and keep live host-enforcement as a human-run follow-up?

**Recommendation:** treat 15.14/DW-200 as DEFERRED (needs a human), mirroring Story 15.1. DW-200 stays `open`; a `seen-again` note was appended to the ledger entry recording this finding so a future dispatch does not silently re-hit the same wall.

**Verification:** none run — halted at planning before any code change. Working tree was clean on `master` at start; no files were modified except this result artifact and the DW-200 `seen-again` annotation in `deferred-work.md`.
