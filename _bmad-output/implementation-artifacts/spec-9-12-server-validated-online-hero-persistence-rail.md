---
title: 'Story 9.12 — Server-validated online hero persistence rail'
type: 'feature'
created: '2026-07-24'
baseline_revision: 'b231901'
status: 'blocked'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/godot/CLAUDE.md'
  - '{project-root}/godot/SimSources.props'
  - '{project-root}/docs/server-deploy/README.md'
warnings: [oversized]
---

<intent-contract>

## Intent

**Problem:** A player's online hero/profile has no server-authoritative home. The offline rail (`LocalProfileSource` → `profiles.json`) is a raw client save-file: anyone can hand-edit it. For online matches, FR-7c/AR-12 require the profile to be the *server's* source of truth — stored server-side, mutated only through a validating server RPC, and attested before a match starts — so a tampered client save-code can never enter online play.

**Approach:** Introduce an `IProfileSource` seam over the existing concrete `LocalProfileSource`, add an `OnlineProfileSource` Nakama adapter whose only write path is a validating **server RPC** (never a raw client storage write), and create the missing Nakama **server-side runtime module** (TypeScript) that writes the profile as an Owner-Read / No-Client-Write storage object and attests it. **Surface an online hero picker inside the online lobby flow** (reuse the existing `HeroPickerOverlay`, backed by `OnlineProfileSource`) and gate that picker's launch/Ready on a successful server attestation — the online rail must be **live** (a real production caller constructs `OnlineProfileSource` and enables attestation), not dormant. Extract the canonical profile-validity rules into a pure Godot-free `HeroProfileValidator` (the single source of truth the init-time apply gate, the client pre-flight, and the TS RPC all obey), Tier-1 tested.

**Anti-tamper model (this EA slice).** The tamper-resistance guarantee rests on the **server owning the record** — the profile is a Nakama storage object the client cannot write; only the validating server RPC mutates it, so a hand-edited local save-code is simply ignored online (the WC3/Battle.net model: the server owns the ladder/account record, the client's editable save is never trusted for online play). The client launch/Ready is additionally gated on `AttestHeroProfileAsync`. **Host-side (ENet `DedicatedServer`) identity enforcement at StartGame is explicitly NOT in this story** — see Boundaries `Never` and the named follow-up in Design Notes.

## Boundaries & Constraints

**Always:**
- The online profile is a Nakama **storage object** written **only** by the server RPC with `permissionRead = 1` (Owner-Read) and `permissionWrite = 0` (No-Client-Write). The client reads it (owner-read) but never calls `WriteStorageObjects` for it.
- One canonical validity rule set. `HeroProfileValidator` (pure C#, Godot-free, float-free) is authoritative; `HeroProfileLoader.LoadInto`'s DW-12 range gate delegates to it (behavior-neutral: existing hero-persistence + golden tests stay byte-identical); the TS module's `validateHeroProfile` mirrors it rule-for-rule.
- Level/XP bounds match today's DW-12 gate exactly: reject when `level < 0 || xp.Raw < 0 || xp.Raw > HeroXpSystem.XpCeiling.Raw`. Never a silent clamp — reject fail-closed.
- The online hero picker's launch is gated on `NakamaService.AttestHeroProfileAsync` succeeding; an unattested/invalid profile cannot be handed to the match-start callback.
- **The rail is live.** A production caller in the online lobby flow (`LobbyUi`) surfaces the online hero picker before Ready, constructs `OnlineProfileSource` (so the online path uses the server object, never `LocalProfileSource`), and enables attestation. Wiring that leaves the rail with no production caller does NOT satisfy this story (review flagged dormancy as the defect).
- **One active online profile per user.** Online, the storage object is a single key per authenticated Nakama user (`heroes`/`profile`). `Save` upserts that one object; `LoadAll` returns 0 or 1 profile. This is a deliberate EA simplification vs. the offline rail's multi-profile `profiles.json` — do not attempt multi-profile online storage or per-profile keys in this story.
- **Attestation failure is fail-closed.** If `AttestHeroProfileAsync` returns invalid/unattested OR the RPC/read cannot complete (Nakama unreachable, timeout, exception), the gate resolves to "cannot enter match": launch is refused and the player is kept in the picker with a surfaced reason. Never fail-open into an online match on an attestation error.
- The offline path is unchanged behaviorally: `LocalProfileSource` keeps its `profiles.json` format and remains the source used when not in an online match.

**Block If:**
- Enforcing No-Client-Write / server-only write is not achievable with the stock Nakama `3.22.0` runtime module API (i.e. would require changing the pinned Nakama image/version). HALT `blocked`, condition `nakama runtime cannot enforce write permission`.
- Any existing golden or `StartStateHash` test changes output as a side effect of this work — that signals the deterministic fold was touched, which this story must not do. HALT `blocked`, condition `determinism fold changed`.

**Never:**
- Do not fold anything new into `SimChecksum`/`StartStateHash` or bump `checksum_algo_version` — the online source yields the same `PlayerProfile` shape `HeroProfileLoader.LoadInto` already folds; no golden re-baseline (per the checksum-fold-timing-rule: no new mid-match-mutable array).
- Do not change the `profiles.json` on-disk format or the offline picker behavior.
- Do not build host-side (ENet `DedicatedServer`) StartGame identity enforcement in this story. The `DedicatedServer` receives **no** Nakama identity today — the Nakama→ENet handoff (`MatchFoundInfo`) is endpoint-only and the server knows peers by transport slot alone — so binding a Nakama attestation into the server StartGame gate would require net-new plumbing (a client→server attestation packet, server-side Nakama trust, and a userId→slot bind in `AssignedRoster`/`HandleReady`). That is a deliberately deferred **named follow-up** (see Design Notes), NOT part of 9.12. For this EA trusted-friends slice the tamper-resistance comes from the server-owned storage object + validating RPC (a client cannot forge the stored profile), and the client launch/Ready is gated on `AttestHeroProfileAsync`. This resolves the prior contradiction between this clause and AC3: AC3's guarantee is scoped to a client-launch gate over a server-authoritative record, and byte-level host-enforced StartGame identity binding is the follow-up.
- Do not deploy/host Nakama or edit production infra — only the module code + local `docker-compose` mount.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Valid profile validation | Well-formed `PlayerProfile` (non-empty ProfileId/HeroDefId, `0 ≤ xp ≤ XpCeiling`, non-negative attribute raws, legal inventory) | `HeroProfileValidator` → `IsValid = true` | none |
| Out-of-range level/xp | `xp.Raw > XpCeiling` or `level < 0` or `xp.Raw < 0` | `IsValid = false`, reason `range` | reject fail-closed, no clamp |
| Empty identity | ProfileId or HeroDefId null/whitespace | `IsValid = false`, reason `identity` | reject |
| Illegal inventory | negative charges or duplicate slot | `IsValid = false`, reason `inventory` | reject |
| Server RPC write, valid | client calls `rpc_write_hero_profile` with valid payload | storage object written `read=1,write=0`, RPC returns stored version | none |
| Server RPC write, invalid | invalid payload | RPC returns error/`{ok:false,reason}`, **no** storage write occurs | rejected server-side |
| Raw client write attempt | client calls `WriteStorageObjects` on the server-owned object | Nakama rejects (No-Client-Write) | surfaced as write failure |
| Attest at StartGame, valid | online picker deploy with attested profile | `OnlineHeroLaunchGate.CanEnterMatch` → true, launch proceeds | none |
| Attest at StartGame, invalid/unattested | attestation returns invalid, or no stored object | gate → false, launch refused, player kept in picker with a surfaced reason | no match entry |
| Attest call fails (network) | Nakama unreachable / RPC exception / timeout during attest or read | gate → false (**fail-closed**), launch refused, reason surfaced | never fail-open into a match |
| Online picker surfaced live | player enters online lobby (`LobbyUi`) and reaches Ready | online hero picker shown, backed by `OnlineProfileSource`; the offline `LocalProfileSource` is NOT used online; Ready is blocked until attested | picker keeps player until a valid attested profile |
| Second online Save | user already has a stored profile, `Save` called again | the single `heroes`/`profile` key is upserted (one active profile per user); prior object replaced | none |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/PlayerProfile.cs` -- the profile value model (integer raws only) — validated/attested; unchanged.
- `godot/src/Core/Definitions/LocalProfileSource.cs` -- offline disk rail — make it implement the new `IProfileSource`; no behavior change.
- `godot/src/Core/Definitions/HeroProfileLoader.cs` -- `LoadInto` DW-12 range gate (lines ~88-94) — delegate the range check to `HeroProfileValidator`.
- `godot/src/Core/HeroStore.cs` -- `HeroId` FNV identity contract ("M2-local loader and M5-server must agree") — reused, unchanged.
- `godot/src/Multiplayer/NakamaService.cs` -- Nakama .NET SDK wrapper (`IClient`/`ISession`/`ISocket`) — add storage-read + RPC-call methods.
- `godot/src/Multiplayer/Party/PartyService.cs` -- precedent for a Nakama-SDK adapter (enqueue→drain) — pattern reference.
- `godot/src/UI/HeroPickerOverlay.cs` -- offline picker (`_source` field, `OnDeployPressed`→`_launch`) — retarget to `IProfileSource` + online attest gate; reused as the online picker surface.
- `godot/src/Core/.../HeroPickerPhase.cs` -- constructs `LocalProfileSource` — construct via `IProfileSource`.
- `godot/src/Multiplayer/LobbyUi.cs` -- online lobby (Find Match → `OnReadyPressed`/`MakeReady` ~line 366, server StartGame). NEW production caller: surface the online hero picker before Ready, construct `OnlineProfileSource`, and gate Ready on attestation. This is the wiring that makes the rail live.
- `godot/src/Multiplayer/DedicatedServer.cs` -- StartGame authority (`CheckStartStateAgreement`, line ~517) — untouched (attestation is client/Nakama-side for this slice; note only).
- `godot/ProjectChimera.Sim.Tests/Persistence/HeroInventoryPersistenceTests.cs` -- test pattern to mirror.
- `docs/server-deploy/docker-compose.yml` -- stock Nakama `3.22.0`, no module mount — add the modules volume.
- `docs/server-deploy/README.md` -- describes Nakama as auth/matchmaking only — document the new module.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Definitions/HeroProfileValidator.cs` -- NEW. Pure static `Validate(PlayerProfile) → ProfileValidation { bool IsValid; ProfileInvalidReason Reason }` (`Reason` enum: `None,Identity,Range,Inventory,Attributes`). Encode the canonical rules (identity non-empty; `level ≥ 0`, `0 ≤ xp.Raw ≤ HeroXpSystem.XpCeiling.Raw`; attribute raws ≥ 0 and no dup keys; inventory charges ≥ 0 and no duplicate non-negative slots). Godot-free, float-free (auto-globbed into Tier-1 + analyzer via `SimSources.props src/Core/**`).
- `godot/src/Core/Definitions/IProfileSource.cs` -- NEW. Interface: `IReadOnlyList<PlayerProfile> LoadAll()`, `void Save(PlayerProfile)`, `void Delete(string profileId)`, `string NextProfileId(string heroDefId)`. Make `LocalProfileSource : IProfileSource`.
- `godot/src/Core/Definitions/OnlineHeroLaunchGate.cs` -- NEW. Pure predicate `bool CanEnterMatch(AttestationOutcome)` (attested && valid). Godot-free, Tier-1 testable (mirror `ServerLobbyPolicy`).
- `godot/src/Core/Definitions/HeroProfileLoader.cs` -- EDIT. Replace the inline DW-12 range predicate with a call to `HeroProfileValidator.Validate(...).IsValid` (range branch). Behavior-neutral.
- `godot/src/Multiplayer/NakamaService.cs` -- EDIT. Add `Task<PlayerProfile?> ReadHeroProfileAsync()` (`ReadStorageObjects`, owner-read), `Task<StorageWriteResult> WriteHeroProfileViaRpcAsync(PlayerProfile)` (`RpcAsync("rpc_write_hero_profile", json)`), `Task<AttestationOutcome> AttestHeroProfileAsync(string profileId)` (`RpcAsync("rpc_attest_hero_profile", json)`). Collection/key constants (`heroes` / `profile`). Enqueue→drain threading like `PartyService`.
- `godot/src/Multiplayer/OnlineProfileSource.cs` -- NEW. `IProfileSource` over `NakamaService`, **single active profile per user** (one `heroes`/`profile` key): `LoadAll` = read the one server object (0 or 1 profile); `Save` upserts that one object and routes **only** through `WriteHeroProfileViaRpcAsync`, throwing on RPC rejection, never calling `WriteStorageObjects`; `Delete` removes the single object when the id matches (else no-op). SDK-coupled (not globbed, untested per repo convention).
- `godot/src/UI/HeroPickerOverlay.cs` + `HeroPickerPhase.cs` -- EDIT. Depend on `IProfileSource`. In online mode, before invoking the launch callback, call `AttestHeroProfileAsync` and gate via `OnlineHeroLaunchGate.CanEnterMatch`; on failure (including a failed/errored attestation call — fail-closed) keep the player in the picker and surface the reason. Offline mode path unchanged.
- `godot/src/Multiplayer/LobbyUi.cs` -- EDIT (activation — REQUIRED, the rail must not be dormant). Surface the online hero picker in the online lobby flow before Ready: construct the picker over `OnlineProfileSource` (which wraps `NakamaService`), present it, and gate `OnReadyPressed`/`MakeReady` so the player cannot Ready until a valid profile has attested. This is the production caller that constructs `OnlineProfileSource` and enables attestation. Offline skirmish flow (`RequestSkirmishLaunch` via `MainMenuPhase`) is untouched.
- `docs/server-deploy/nakama-modules/` -- NEW TS module. `src/validation.ts` (pure `validateHeroProfile(profile)` mirroring `HeroProfileValidator`), `src/main.ts` (`InitModule` registers `rpc_write_hero_profile`: parse→validate→`nk.storageWrite([{collection:'heroes',key:'profile',userId:ctx.userId,value,permissionRead:1,permissionWrite:0}])`; and `rpc_attest_hero_profile`: read stored→validate→return `{attested,reason}`), `package.json`+`tsconfig.json` (nakama-runtime types), a node/vitest test for `validateHeroProfile`, build to a single bundled `build/index.js`.
- `docs/server-deploy/docker-compose.yml` -- EDIT. Mount `./nakama-modules/build:/nakama/data/modules:ro` so Nakama loads the module.
- `docs/server-deploy/README.md` -- EDIT. Document the module, the two RPC ids, and the Owner-Read/No-Client-Write storage contract.
- `godot/ProjectChimera.Sim.Tests/Persistence/HeroProfileValidatorTests.cs` + `Multiplayer/OnlineHeroLaunchGateTests.cs` -- NEW. Cover every I/O-matrix validator row and all gate branches: attested-valid → true; invalid/unattested → false; **attestation-call-failed → false (fail-closed)**. `OnlineHeroLaunchGate.CanEnterMatch` must treat a failed/absent `AttestationOutcome` as "cannot enter".

**Acceptance Criteria:**
- Given a valid `PlayerProfile`, when validated by `HeroProfileValidator`, then `IsValid` is true; given each invalid class (identity/range/inventory/attributes), then `IsValid` is false with the matching reason.
- Given `HeroProfileLoader.LoadInto` after delegating its range gate, when the existing hero-persistence + golden Tier-1 tests run, then all pass byte-identically (no `StartStateHash`/golden movement, `checksum_algo_version` unchanged).
- Given the TS module built and mounted, when a client calls `rpc_write_hero_profile` with a valid profile, then the storage object is created with read=1/write=0; when it calls with an invalid profile, then no object is written and an error/`ok:false` is returned.
- Given the server-owned object exists, when a client attempts a raw `WriteStorageObjects` on it, then Nakama rejects it (No-Client-Write).
- Given the online hero picker at Ready/launch, when the selected profile attests successfully, then `OnlineHeroLaunchGate.CanEnterMatch` is true and launch proceeds; when attestation is invalid/absent, OR when the attestation call itself fails (Nakama unreachable/RPC error/timeout), then the gate is false, launch is refused **fail-closed**, and the player stays in the picker with a surfaced reason.
- Given the online lobby (`LobbyUi`), when a player readies for an online match, then the online hero picker is surfaced and is backed by `OnlineProfileSource` (the server storage object), the offline `LocalProfileSource` is not used for the online path, and Ready is blocked until a valid profile has attested — i.e. the rail has a real production caller and is not dormant.
- Scope boundary (must hold): the ENet `DedicatedServer` StartGame path is **unchanged** by this story; there is no client→server attestation packet and no `HandleReady` identity gate here (that is the named follow-up). AC3's guarantee is a client-launch gate over a server-authoritative, client-unforgeable record — consistent with the trusted-friends EA slice.

## Design Notes

Why a server module at all: "No-Client-Write" and "validating RPC" are *definitionally* server-side — Nakama enforces write permission only on objects the server wrote with `permissionWrite=0`, and an RPC is server runtime code. None exists today (docker-compose runs stock Nakama with no module mount), so it is created here. TypeScript (not Go) is chosen: no cgo/Linux-plugin build, cross-platform for a solo Windows dev, bundles to one JS file — the Nakama-recommended default.

Testability follows the repo idiom (extract a Godot-free/SDK-free pure core, Tier-1 test that; leave the thin SDK adapter untested like `PartyService`/`NakamaService`): the canonical rules live in `HeroProfileValidator` (xUnit) and are re-expressed in `validation.ts` (node test), so both sides are verified without a live Nakama. The two implementations must stay in sync — call this out in both files' headers.

**Why this scope and not host-side enforcement (WC3/Battle.net precedent).** The tamper Alec is defending against is a friend hand-editing a local save to walk into an online match with an illegitimate hero. The decisive defense is *server ownership of the record*: the profile is a Nakama storage object the client cannot write (Owner-Read / No-Client-Write), mutated only by a validating server RPC — so the edited local save-code is simply ignored online. This is exactly the WC3 model: Battle.net owned the ladder/account record server-side and never trusted the client's local save/save-code for online play; WC3 did **not** cryptographically verify each player's identity inside the game host on every match start (that is modern VAC-style anti-cheat). For a trusted-friends EA slice (friends download the build from Alec and play against him), server-owned storage + a validating RPC + a client-launch attestation gate is the right-sized, non-house-of-cards foundation: the expensive-to-retrofit parts (one canonical rule set; server-not-client owning the write) are correct from day one, and the deferred part bolts on behind an attestation gate that already exists.

**Named follow-up — host-side StartGame identity enforcement (post-1.0 fast-follow).** The remaining hole is a friend who patches the game binary to skip the client-side gate. Closing it requires host-side enforcement the ENet `DedicatedServer` cannot do today, because the Nakama→ENet handoff (`MatchFoundInfo`) is endpoint-only and the server knows peers only by transport slot — no Nakama userId/token ever reaches it. A follow-up story must add: (1) a client→server attestation packet carrying a Nakama-issued credential (session token or a signed per-match join ticket) sent on connect or folded into `Ready`; (2) server-side Nakama trust so the `DedicatedServer` can verify that credential (hold Nakama's signing key, or call a Nakama server API / a co-located server-to-server channel — feasible given the VPS co-location); (3) a userId→slot bind in `AssignedRoster.TryFreeze` and a fail-closed gate in `HandleReady` alongside `CheckStartStateAgreement`. This story deliberately does none of that and must leave the `DedicatedServer` StartGame path byte-unchanged. **Action for the re-drive:** file this follow-up in the deferred-work ledger (a `DW-` entry) so the deferral is tracked, not lost.

## Verification

**Commands:**
- `dotnet build godot/godot.sln` -- expected: succeeds (client adapters compile; analyzer gate green — new sim files are float-free/Godot-free).
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all pass, including new validator/gate tests and unchanged golden/hero-persistence tests (proves no determinism movement).
- `npm --prefix docs/server-deploy/nakama-modules install && npm --prefix docs/server-deploy/nakama-modules test && npm --prefix docs/server-deploy/nakama-modules run build` -- expected: `validateHeroProfile` tests pass; `build/index.js` bundle produced.

**Manual checks (if no CLI):**
- If Docker is available: `docker compose -f docs/server-deploy/docker-compose.yml config` resolves the module mount; otherwise inspect the compose diff — the `./nakama-modules/build:/nakama/data/modules` volume is present.
- Confirm `OnlineProfileSource.Save` has no `WriteStorageObjects` call path (grep) — writes route only through the RPC.

## Review Triage Log

### 2026-07-24 — Review pass
- intent_gap: 1: (high 1)
- bad_spec: 0
- patch: 0
- defer: 0
- reject: 0
- addressed_findings:
  - none

Intent-gap short-circuited the pass (cascading order). All four review layers converged that the online rail is **dormant** (no production caller constructs `OnlineProfileSource` or calls `EnableOnlineAttestation`; the hero picker is offline-skirmish-only, `RequestSkirmishLaunch`), and that a client-side-only attestation gate cannot satisfy AC3's "only a server-attested profile can enter an online match" (TOCTOU: the client attests one profile then launches the in-memory object, and the ENet `DedicatedServer` StartGame authority performs no attestation check). Root cause is **inside** `<intent-contract>` — its `Never` clause ("do not build ENet `DedicatedServer` enforcement; attestation gates the client launch") contradicts the source story's AC3 anti-tamper guarantee — so this is intent_gap, not bad_spec. Lower-severity findings (missing level ceiling; off-main-thread post-`await` scene mutation; unguarded `ReadHeroProfileAsync`/`WriteHeroProfileViaRpcAsync` exceptions vs. fail-soft contract; `npm test` running raw `.ts` with no Node pin; C#↔TS parity has no shared-oracle/CI check; single storage key → second Save overwrites + Delete no-op; docker mount of a gitignored `build/` with no build step; `main.ts` RPC handlers untested) were **not individually triaged** — moot under the revert; preserved in the saved patch for the re-drive. Attempted change saved to `intent-gap-attempt-9-12-server-validated-online-hero-persistence-rail.patch`; code reverted to `b231901`.

## Resolution Log

### 2026-07-24 — Escalation resolved (bmad-loop-resolve, with Alec)

The intent gap was a real contradiction inside `<intent-contract>`: the `Never` clause ("do not build ENet `DedicatedServer` enforcement; attestation gates the client launch") vs. the source-story AC3 ("StartGame is gated on that attestation"). Confirmed in code: the `DedicatedServer` receives no Nakama identity (`MatchFoundInfo` endpoint-only; peers known by slot), so host-side StartGame attestation is impossible without net-new plumbing the spec forbade.

**Decision (Alec):** ship the **server-authoritative storage + validating RPC** rail (the WC3/Battle.net model — server owns the record, client can't forge it), **wire it live** so friends can actually download the build and play online (reuse the existing hero picker in `LobbyUi`, gate Ready/launch on attestation, fail-closed), and **defer host-side StartGame identity enforcement to a named follow-up**. AC3 rewritten to a client-launch gate over a server-authoritative, client-unforgeable record — the contradiction is removed. This supersedes the two unresolved questions recorded in the Auto Run Result below.

**For the re-drive:** the previous attempt (saved patch) built the pure cores + RPC but left the rail **dormant** and kept AC3's over-strong wording; it is NOT restored. Re-drive from scratch against this corrected spec — the delta vs. the attempt is (a) the required `LobbyUi` activation wiring, (b) single-active-profile-per-user online storage semantics, (c) explicit fail-closed attestation behavior, and (d) filing the host-side-enforcement follow-up as a `DW-` ledger entry. The lower-severity review findings (level ceiling, off-main-thread post-`await` scene mutation, unguarded RPC exceptions vs. fail-soft, `npm test` Node pin, C#↔TS parity check, docker mount of gitignored `build/` with no build step, untested `main.ts` handlers) remain valid re-drive quality items — address them in the fresh implementation.

## Auto Run Result

Status: blocked
Blocking condition: intent gap

**Summary:** Planned and implemented Story 9.12 (server-validated online hero persistence rail), then blocked at review on an intent gap rooted in the spec's `<intent-contract>`. Code was reverted to baseline `b231901`; the attempted implementation is preserved as a patch.

**Unresolved questions (require Alec / `bmad-loop-resolve`):**
1. **Server-enforcement scope.** AC3 + the story goal require that a tampered client cannot enter an online match with a non-attested profile ("online persistence cannot be tampered with via client save-codes"). A client-side attestation gate at the hero picker cannot guarantee this — TOCTOU, and the ENet `DedicatedServer` (the actual online StartGame authority, per AR-12 "depends on 9.7 for the StartGame surface to gate on") does no attestation check. Does 9.12 build **ENet-side StartGame attestation enforcement** (convey the Nakama attestation to the game host), or is a **client-side gate acceptable for the EA/trusted-friends slice** with server enforcement deferred? The intent-contract pre-decided the latter in a way that contradicts AC3.
2. **Online hero-picker surface / activation.** The picker is offline-skirmish-only today; there is **no online hero-picker entry point**, and nothing wires the online source. UX-DR75 has no detailed surface spec (only a one-line mention in `epics.md`). Where in the online lobby flow (`LobbyUi` Find Match → Ready → server StartGame) does online hero selection attach, and what constructs `OnlineProfileSource` / enables attestation? Not inferable from code.

**Attempted change (reverted, saved as patch):** pure `HeroProfileValidator` + `IProfileSource` seam + `OnlineHeroLaunchGate`; `NakamaService` storage-read/RPC methods + `OnlineProfileSource`; `HeroPickerOverlay`/`HeroPickerPhase` online gate; a Nakama TypeScript server module (`validation.ts`/`main.ts`, Owner-Read/No-Client-Write + two RPCs) with docker-compose mount; Tier-1 + node tests. The pure cores (validator, gate) and their tests were green; the security-load-bearing half (live server enforcement, activation) is the unresolved part.

**Verification performed before block:** `dotnet test` Tier-1 = 3338 passed / 0 real failures (the lone `CanonicalModelHashPerfTests` fail was the known CPU-contention timing flake, confirmed 2/2 in isolation; `HashAlgoVersions_AreUnchanged` confirmed no determinism-fold movement); TS module `npm test` 16/16 + bundle built. These substantiate the validity rules only, not the server rail — which is exactly the blocked concern.

**Residual artifacts (in `git status`, intentionally left in place):** `spec-9-12-server-validated-online-hero-persistence-rail.md` (this spec) and `intent-gap-attempt-9-12-server-validated-online-hero-persistence-rail.patch` (saved attempt). No code changes remain; tree matches `b231901`.
