---
title: 'Story 9.12 — Server-validated online hero persistence rail'
type: 'feature'
created: '2026-07-24'
baseline_revision: 'dd896a6'
final_revision: '91b3b88'
status: 'done'
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

- `godot/src/Core/Definitions/PlayerProfile.cs` -- profile value model, integer raws only (`ProfileId`, `HeroDefId`, `FactionId`, `DisplayName`, `Values[]`, `Inventory[]`; computed `Level = RawOf("hero.level")`, `Xp = Fixed.FromRaw(RawOf("hero.xp"))`). Validated/attested; unchanged.
- `godot/src/Core/Definitions/LocalProfileSource.cs:38-108` -- offline disk rail (`LoadAll`/`Save`/`Delete`/`NextProfileId` over `profiles.json`, fail-soft read). Make it implement `IProfileSource`; no behavior change.
- `godot/src/Core/Definitions/HeroProfileLoader.cs:89-94` -- the DW-12 range gate (`level < 0 || xp.Raw < 0 || xp.Raw > HeroXpSystem.XpCeiling.Raw`, whole-profile reject, inclusive ceiling). Delegate the range check to `HeroProfileValidator`.
- `godot/src/Combat/HeroXpSystem.cs:35` -- `XpCeiling = Fixed.FromInt(30000)`; compared as `.Raw` (inclusive) in the gate.
- `godot/src/Core/HeroStore.cs:18-31` -- `HeroId` FNV identity (M2-local loader and M5-server must agree) — reused, unchanged.
- `godot/src/Multiplayer/NakamaService.cs:79-237` -- Nakama .NET SDK wrapper: holds `_client` (concrete `Client`) + `_session`; enqueue→drain marshaling via `_pending`/`DrainEvents()` (`:200`) and `Enqueue` (`:237`). Add storage-read + RPC methods (use existing `_client`/`_session`).
- `godot/src/Multiplayer/Party/PartyService.cs:24-61,191` -- precedent Nakama-SDK adapter (injected `Action<Action> enqueue`, subscribe→`Enqueue`→drain) — pattern reference.
- `godot/src/UI/HeroPickerOverlay.cs:41,78,373` -- offline picker: `_source` (typed `LocalProfileSource`), `Initialize(scenario, source, slotFactionDefs, Action<PlayerProfile?> launch)`, `OnDeployPressed`→`_launch`. Retarget to `IProfileSource` + online attest gate; reused as the online picker surface.
- `godot/src/Core/Bootstrap/Phases/HeroPickerPhase.cs:26-73` -- constructs `LocalProfileSource` (`:29`), wires the overlay (`:31-33`), offline `Launch` mint (`:48-73`). Construct via `IProfileSource`.
- `godot/src/Multiplayer/LobbyUi.cs:36,304,366,470,561,579` -- online lobby: owns `_nakama` (`:36`), `_assignedFaction` (server-assigned, `:470`), `OnReadyPressed` (`:366`)→`TryStartGame` (`:561`)→`FireMatchStart`/`OnMatchStart` (`:579`). NEW production caller: construct `OnlineProfileSource`, surface the online picker before Ready, gate `OnReadyPressed` on attestation. This is the wiring that makes the rail live.
- `godot/src/Multiplayer/MatchLifecycleController.cs:55-67,98-167` -- builds `LobbyUi`, subscribes `OnMatchStart`; the handler starts the match by going lockstep-online + `GameState → Play` and mints **no** hero today. Untouched here; the deferral note (Design Notes) is anchored on it.
- `godot/src/Core/MainScene.cs:538` -- the sole boot-time `PendingHeroProfile` mint (null online → nothing minted). Context for the in-match-deployment deferral.
- `godot/ProjectChimera.Sim.Tests/Persistence/HeroInventoryPersistenceTests.cs` -- test pattern to mirror.
- `docs/server-deploy/docker-compose.yml` -- stock Nakama `3.22.0`, no module mount — add the modules volume.
- `docs/server-deploy/README.md` -- describes Nakama as auth/matchmaking only — document the new module.
- `docs/server-deploy/nakama-modules/` -- NEW TS server module (does not exist today).
- `_bmad-output/implementation-artifacts/deferred-work.md` -- ledger; append the named follow-up as a new `DW-` entry.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Definitions/HeroProfileValidator.cs` -- NEW. Pure static `Validate(PlayerProfile) → ProfileValidation { bool IsValid; ProfileInvalidReason Reason }` (`Reason` enum `None,Identity,Range,Inventory,Attributes`). Rules: identity non-empty (`ProfileId` and `HeroDefId`); range mirrors DW-12 **exactly** (`level ≥ 0`, `0 ≤ xp.Raw ≤ HeroXpSystem.XpCeiling.Raw` inclusive — **no** added upper level ceiling; behavior-neutral); attribute raws `≥ 0` and no duplicate keys; inventory charges `≥ 0` and no duplicate non-negative slots. Godot-free, float-free (auto-globbed into Tier-1 + analyzer via `SimSources.props src/Core/**`).
- `godot/src/Core/Definitions/IProfileSource.cs` -- NEW. Interface: `IReadOnlyList<PlayerProfile> LoadAll()`, `void Save(PlayerProfile)`, `void Delete(string profileId)`, `string NextProfileId(string heroDefId)`. Make `LocalProfileSource : IProfileSource` (signatures already match — no behavior change).
- `godot/src/Core/Definitions/OnlineHeroLaunchGate.cs` -- NEW. Pure predicate `bool CanEnterMatch(AttestationOutcome)` (attested && valid; a failed/absent `AttestationOutcome` → false). Godot-free, Tier-1 testable (mirror `ServerLobbyPolicy`). Define the `AttestationOutcome` type (e.g. `readonly record struct AttestationOutcome(bool Attested, bool CallSucceeded, ProfileInvalidReason Reason)`) alongside it, integer/enum only.
- `godot/src/Core/Definitions/HeroProfileLoader.cs` -- EDIT (`:89-94`). Replace the inline DW-12 range predicate with a call to `HeroProfileValidator.Validate(...)` range branch. Behavior-neutral: same reject set, same log, same `0` minted.
- `godot/src/Multiplayer/NakamaService.cs` -- EDIT. Add `Task<PlayerProfile?> ReadHeroProfileAsync()` (`ReadStorageObjectsAsync`, owner-read; **catch → `null`**, fail-soft like `LocalProfileSource.LoadAll`), `Task<StorageWriteResult> WriteHeroProfileViaRpcAsync(PlayerProfile)` (`RpcAsync("rpc_write_hero_profile", json)`; **catch → failure result**), `Task<AttestationOutcome> AttestHeroProfileAsync(string profileId)` (`RpcAsync("rpc_attest_hero_profile", json)`; **catch/timeout → `CallSucceeded=false` = fail-closed**). Collection/key constants (`heroes` / `profile`). Marshal any UI-facing callback via `Enqueue`. No unguarded exception may escape these methods.
- `godot/src/Multiplayer/OnlineProfileSource.cs` -- NEW. `IProfileSource` over `NakamaService`, **single active profile per user** (one `heroes`/`profile` key): `LoadAll` = read the one server object (0 or 1 profile); `Save` upserts that one object routing **only** through `WriteHeroProfileViaRpcAsync`, throwing on RPC rejection, never calling `WriteStorageObjects`; `Delete` removes the single object when the id matches (else no-op). SDK-coupled (not globbed, untested per repo convention).
- `godot/src/UI/HeroPickerOverlay.cs` + `godot/src/Core/Bootstrap/Phases/HeroPickerPhase.cs` -- EDIT. Depend on `IProfileSource`. In online mode, on deploy: upsert the selected profile via `Save` (RPC write) then call `AttestHeroProfileAsync` and gate via `OnlineHeroLaunchGate.CanEnterMatch`; on any failure (including a failed/errored attestation call — **fail-closed**) keep the player in the picker and surface the reason. **All post-`await` Godot scene/UI mutation must be marshaled to the main thread** (`CallDeferred`/enqueue→drain) — no off-thread node access. Offline mode path unchanged.
- `godot/src/Multiplayer/LobbyUi.cs` -- EDIT (activation — **REQUIRED**, the rail must not be dormant). Construct `OnlineProfileSource` over the owned `_nakama`; surface the online hero picker in the online lobby flow before Ready (compatibility-filtered by the local `_assignedFaction`'s `FactionDefinition`); gate `OnReadyPressed`/`MakeReady` so the player cannot Ready until a valid profile has attested. This is the production caller that constructs `OnlineProfileSource` and enables attestation. Offline skirmish flow (`RequestSkirmishLaunch` via `MainMenuPhase`) is untouched.
- `docs/server-deploy/nakama-modules/` -- NEW TS module. `src/validation.ts` (pure `validateHeroProfile(profile)` mirroring `HeroProfileValidator` rule-for-rule), `src/main.ts` (`InitModule` registers `rpc_write_hero_profile`: parse→validate→`nk.storageWrite([{collection:'heroes',key:'profile',userId:ctx.userId,value,permissionRead:1,permissionWrite:0}])`; and `rpc_attest_hero_profile`: read stored→validate→return `{attested,reason}`). **Extract the RPC handler bodies into pure functions** so they are unit-testable with a mocked `nk`. `package.json` (pin Node via `engines`, vitest test runner — not raw `.ts` execution), `tsconfig.json` (nakama-runtime types), tests for `validateHeroProfile` **and** both handler bodies, build to a single bundled `build/index.js`.
- `docs/server-deploy/nakama-modules/test/fixtures/validation-cases.json` (or equivalent shared path) -- NEW. A shared test-vector oracle (valid + one case per invalid class) consumed by **both** the C# validator test and the TS validation test, so C#↔TS parity is verified against one source of truth, not two hand-kept copies.
- `docs/server-deploy/docker-compose.yml` -- EDIT. Mount `./nakama-modules/build:/nakama/data/modules:ro` so Nakama loads the module. Note in `README.md` that `build/` is gitignored and must be produced (`npm run build`) before `compose up`.
- `docs/server-deploy/README.md` -- EDIT. Document the module, the two RPC ids, the Owner-Read/No-Client-Write storage contract, the single-active-profile-per-user semantics, and the build-before-compose step.
- `godot/ProjectChimera.Sim.Tests/Definitions/HeroProfileValidatorTests.cs` + `godot/ProjectChimera.Sim.Tests/Multiplayer/OnlineHeroLaunchGateTests.cs` -- NEW. Cover every I/O-matrix validator row (driven by the shared fixture) and all gate branches: attested-valid → true; invalid/unattested → false; **attestation-call-failed → false (fail-closed)**.
- `_bmad-output/implementation-artifacts/deferred-work.md` -- EDIT. Append a new `DW-` entry (next free id) capturing the named follow-up: host-side (ENet `DedicatedServer`) StartGame identity enforcement **and** deterministic cross-peer in-match deployment of the attested online hero — both blocked on the same missing Nakama→ENet identity/profile plumbing and a start-state fold this story must not touch.

**Acceptance Criteria:**
- Given a valid `PlayerProfile`, when validated by `HeroProfileValidator`, then `IsValid` is true; given each invalid class (identity/range/inventory/attributes), then `IsValid` is false with the matching reason.
- Given `HeroProfileLoader.LoadInto` after delegating its range gate, when the existing hero-persistence + golden Tier-1 tests run, then all pass byte-identically (no `StartStateHash`/golden movement, `checksum_algo_version` unchanged).
- Given the TS module built and mounted, when a client calls `rpc_write_hero_profile` with a valid profile, then the storage object is created with `read=1`/`write=0`; when it calls with an invalid profile, then no object is written and an error/`ok:false` is returned.
- Given the server-owned object exists, when a client attempts a raw `WriteStorageObjects` on it, then Nakama rejects it (No-Client-Write).
- Given the online hero picker at Ready/launch, when the selected profile attests successfully, then `OnlineHeroLaunchGate.CanEnterMatch` is true and launch proceeds; when attestation is invalid/absent, OR when the attestation call itself fails (Nakama unreachable/RPC error/timeout), then the gate is false, launch is refused **fail-closed**, and the player stays in the picker with a surfaced reason.
- Given the online lobby (`LobbyUi`), when a player readies for an online match, then the online hero picker is surfaced and is backed by `OnlineProfileSource` (the server storage object), the offline `LocalProfileSource` is not used for the online path, and Ready is blocked until a valid profile has attested — i.e. the rail has a real production caller and is not dormant.
- Scope boundary (must hold): the ENet `DedicatedServer` StartGame path is **unchanged** by this story (no client→server attestation packet, no `HandleReady` identity gate), **and** the attested online hero is **not** minted into the lockstep match this story — online matches still reach StartGame with `StartStateHash` agreement unchanged. Both host-side enforcement and deterministic in-match deployment are the named `DW-` follow-up. AC's guarantee is a client-launch gate over a server-authoritative, client-unforgeable record — consistent with the trusted-friends EA slice.

## Spec Change Log

<!-- Append-only. Populated by step-04 during review loops. -->

## Review Triage Log

### 2026-07-24 — Review pass (previous attempt, reverted)
- intent_gap: 1: (high 1)
- bad_spec: 0
- patch: 0
- defer: 0
- reject: 0
- addressed_findings:
  - none

Intent-gap short-circuited the pass (cascading order). All four review layers converged that the online rail was **dormant** (no production caller constructed `OnlineProfileSource` or enabled attestation; the hero picker was offline-skirmish-only), and that a client-side-only attestation gate cannot satisfy AC3's original "only a server-attested profile can enter an online match" wording (TOCTOU; the ENet `DedicatedServer` StartGame authority performs no attestation check). Root cause was a contradiction **inside** `<intent-contract>` — its `Never` clause vs. the source story's AC3 — so intent_gap, not bad_spec. Lower-severity findings (missing level ceiling; off-main-thread post-`await` scene mutation; unguarded RPC exceptions vs. fail-soft; `npm test` running raw `.ts` with no Node pin; C#↔TS parity has no shared oracle; single storage key second-Save overwrite / Delete no-op; docker mount of a gitignored `build/` with no build step; untested `main.ts` handlers) were not individually triaged — preserved in the saved patch. Attempt saved to `intent-gap-attempt-9-12-server-validated-online-hero-persistence-rail.patch`; code reverted to `b231901`.

### 2026-07-24 — Review pass (re-drive)
- intent_gap: 0
- bad_spec: 0
- patch: 12: (high 1, medium 5, low 6)
- defer: 0
- reject: 4
- addressed_findings:
  - `[high]` `[patch]` TS `validation.ts` `raw|0` truncation + non-array coercion let out-of-Int32 / non-array payloads bypass the server validator (C#↔TS drift): added `isInt32` guard + array-shape reject, added boundary fixture cases (TS-only, with a C# theory proving rejection at the deserialization boundary).
  - `[medium]` `[patch]` `OnlineProfileSource` blocked the Godot main thread (`.GetAwaiter().GetResult()`): rewritten cache-backed — `LoadAll` non-blocking, `SaveAsync` async, online picker load/save now await + `CallDeferred`.
  - `[medium]` `[patch]` attest race: whole footer frozen during the await; `FinishOnlineDeploy`/`OnOnlineHeroChosen` bail if the overlay was dismissed or the lobby is no longer online/connected — a late attestation can't ready a torn-down lobby.
  - `[medium]` `[patch]` fail-closed precondition untested: extracted `AttestationReplyParser` (Godot-free) + `AttestationReplyParserTests` feeding the exact TS reply strings incl. empty/garbled → fail-closed.
  - `[medium]` `[patch]` online Delete showed a false "Deleted" toast over a no-op: Delete disabled online with an honest message.
  - `[medium]` `[patch]` write RPC persisted the raw payload: `sanitizeProfile` reconstructs from a field whitelist + enforces a max serialized size.
  - `[low]` `[patch]` validator doc claimed a nonexistent client pre-flight caller — corrected.
  - `[low]` `[patch]` overstated "only write path is the RPC" comment — reworded to rest the guarantee on server-owned write + attest-time re-validation.
  - `[low]` `[patch]` malformed/empty attest reply now surfaces "server error — try again" (distinct from `not_found`), both fail-closed.
  - `[low]` `[patch]` client now always sends a non-empty `ProfileId`; blank id → `CallFailed`.
  - `[low]` `[patch]` `OnlineProfileSource.NextProfileId` documented as intentional single-active-profile-per-user semantics.
  - `[low]` `[patch]` docker entrypoint now fails loudly if `build/index.js` is absent; README build step promoted to a required pre-`compose up` gate.

Rejected (4): cosmetic-gate / hero-not-deployed (intended scope on intent authority — DW-200, `Never` clause); No-Client-Write only flag-asserted (inherent no-live-Nakama limitation, compensated by the spec's manual check); integration surface untested (repo convention — the load-bearing `OnlineHeroLaunchGate` is Tier-1 tested); direct-IP dedicated join ungated (consistent with the host-side-enforcement deferral).

### 2026-07-24 — Follow-up review pass (fresh review of the done story)
- intent_gap: 0
- bad_spec: 0
- patch: 1: (high 0, medium 1, low 0)
- defer: 4
- reject: 10
- addressed_findings:
  - `[medium]` `[patch]` `HeroPickerOverlay` "Play without a saved hero" stayed clickable in online mode (tooltip already said "(offline)" but `UpdateButtonStates` set `_playWithoutBtn.Disabled = false` unconditionally); online it Hide()s the picker + `_launch(null)`, which the lobby ignores fail-closed, silently leaving the player un-readied with the picker dismissed — a dead-end contradicting the intent's "keep the player until an attested profile". Fixed: `_playWithoutBtn.Disabled = _online != null` so online exits are only an attested Deploy or Cancel. Verified: `dotnet build` clean, Sim suite 3375/0/1 (no golden/determinism movement), TS suite 33/33.

Intent-alignment auditor confirmed the diff aligns with `<intent-contract>` under the defensible operational readings (A2/A3 + B2 + C): the rail is genuinely LIVE (`LobbyUi` constructs `OnlineProfileSource` and fail-closed-gates Ready on `AttestHeroProfileAsync`), not dormant — so no intent_gap. Deferred (4, new ledger entries): online picker reachability needs a live-Nakama manual verification (soft-lock risk if the pre-match lobby has no placeable-hero scenario); O(n^2) TS validation before the size cap (CPU-DoS hardening); TS vitest suite not in CI (one-sided C#<->TS parity enforcement); no C#-serialized-payload round-trip test (wire-format drift). Rejected (10) as noise / out-of-scope-per-intent / by-design: content-provenance "anti-cheat" overstatement (EA server-owned-record model, DW-200); first-time raw-write "forgery" (grants no capability beyond in-range content a legit RPC already accepts; AC scoped to an existing object); `esbuild --format=cjs` allegedly hiding `InitModule` (FALSE POSITIVE — verified the bundle emits `InitModule` as a flat top-level declaration → global under goja; 33/33 TS tests pass); blank-payload attest id binding (cosmetic under single-active-profile-per-user); mirror-match faction filter (profiles are the user's own; moot until the deferred in-match deployment); item_id-not-validated (opaque, not an intent rule); UTF-16-vs-byte size cap (the proposed `Buffer` fix is goja-unsafe and impact is negligible); gitignored `build/` root-owned-dir on Linux (solo Windows dev, README-documented); C# `Validate` has no production caller (by-design parity oracle, already documented); `NextProfileId` single-key overwrite (by-design single-active-profile).

## Resolution Log

### 2026-07-24 — Escalation resolved (bmad-loop-resolve, with Alec)

The intent gap was a real contradiction inside `<intent-contract>`: the `Never` clause ("do not build ENet `DedicatedServer` enforcement; attestation gates the client launch") vs. the source-story AC3 ("StartGame is gated on that attestation"). Confirmed in code: the `DedicatedServer` receives no Nakama identity (`MatchFoundInfo` endpoint-only; peers known by slot), so host-side StartGame attestation is impossible without net-new plumbing the spec forbade.

**Decision (Alec):** ship the **server-authoritative storage + validating RPC** rail (the WC3/Battle.net model — server owns the record, client can't forge it), **wire it live** so friends can actually download the build and play online (reuse the existing hero picker in `LobbyUi`, gate Ready/launch on attestation, fail-closed), and **defer host-side StartGame identity enforcement to a named follow-up**. AC rewritten to a client-launch gate over a server-authoritative, client-unforgeable record — the contradiction is removed. This supersedes the two unresolved questions in the prior Auto Run Result.

**Re-plan note (2026-07-24, this pass):** codebase trace confirmed that the online path mints **no** hero profile today (`MatchLifecycleController.OnMatchStart` never calls `HeroProfileLoader.LoadInto`; `PendingHeroProfile` is only set by the offline picker). Therefore in-match deployment of the attested hero is deliberately **out of scope**: minting one peer's hero would diverge `HeroStore` across peers → `StartStateHash` disagreement → online matches would fail to start, and deterministic cross-peer deployment needs a start-state fold the `Never`/`Block If` clauses forbid. In-match deployment is folded into the named follow-up alongside host-side enforcement. The previous attempt's saved patch (pure cores + RPC + TS module, but a dormant rail) is NOT restored; the re-drive adds (a) the `LobbyUi` activation wiring, (b) single-active-profile online semantics, (c) explicit fail-closed attestation, (d) the `DW-` ledger entry, and addresses the lower-severity review items (main-thread marshaling, guarded RPC exceptions, Node-pinned vitest, shared C#↔TS parity fixture, build-before-compose, tested TS handlers).

## Design Notes

**Why a server module at all:** "No-Client-Write" and "validating RPC" are *definitionally* server-side — Nakama enforces write permission only on objects the server wrote with `permissionWrite=0`, and an RPC is server runtime code. None exists today (docker-compose runs stock Nakama with no module mount), so it is created here. TypeScript (not Go) is chosen: no cgo/Linux-plugin build, cross-platform for a solo Windows dev, bundles to one JS file — the Nakama-recommended default.

**Testability follows the repo idiom** (extract a Godot-free/SDK-free pure core, Tier-1 test that; leave the thin SDK adapter untested like `PartyService`/`NakamaService`): the canonical rules live in `HeroProfileValidator` (xUnit) and are re-expressed in `validation.ts` (vitest). A **shared JSON fixture** of validation cases is the single oracle both sides run, so the two implementations cannot silently drift — call the sync requirement out in both files' headers.

**Level ceiling clarification (preempts the prior low finding):** the validator's range branch mirrors DW-12 **exactly** — `level ≥ 0` (floor only, no upper ceiling) and `0 ≤ xp.Raw ≤ XpCeiling.Raw`. Adding an upper level ceiling would break the behavior-neutral requirement (the golden/hero-persistence tests must stay byte-identical). The xp ceiling is the real cap; both C# and TS check it.

**Why gate Ready when the attested hero does not deploy this story:** the picker + attestation gate is forward-looking, load-bearing infrastructure — it proves every online player holds a *valid, server-owned* hero record before entering online play, which is exactly what the future deterministic-deployment feature will consume. The expensive-to-retrofit parts (one canonical rule set; server-not-client owning the write; a live attestation gate) are correct from day one; the deferred part (deploying the hero into the sim, and host-side StartGame enforcement) bolts on behind the gate that already exists. Online matches today mint no hero, so this story adds the server-owned profile + attestation gate without regressing online play.

**Why this scope and not host-side enforcement (WC3/Battle.net precedent).** The tamper Alec is defending against is a friend hand-editing a local save to walk into an online match with an illegitimate hero. The decisive defense is *server ownership of the record*: the profile is a Nakama storage object the client cannot write (Owner-Read / No-Client-Write), mutated only by a validating server RPC — so the edited local save-code is simply ignored online. This is exactly the WC3 model: Battle.net owned the ladder/account record server-side and never trusted the client's local save for online play; WC3 did not cryptographically verify each player's identity inside the game host on every match start. For a trusted-friends EA slice, server-owned storage + a validating RPC + a client-launch attestation gate is the right-sized, non-house-of-cards foundation.

**Named follow-up — host-side StartGame identity enforcement + deterministic in-match hero deployment (post-1.0 fast-follow).** The remaining holes are (1) a friend who patches the game binary to skip the client-side gate, and (2) actually deploying the attested hero into the match. Both require host-side plumbing the ENet `DedicatedServer` cannot do today: the Nakama→ENet handoff (`MatchFoundInfo`) is endpoint-only and the server knows peers only by transport slot — no Nakama userId/token reaches it, and no peer's profile reaches the other peers. A follow-up story must add: (a) a client→server attestation packet carrying a Nakama-issued credential; (b) server-side Nakama trust so the `DedicatedServer` can verify it; (c) a userId→slot bind in `AssignedRoster.TryFreeze` + a fail-closed gate in `HandleReady`; and (d) server distribution of every peer's attested profile so all peers mint an identical multi-hero `HeroStore` (a deterministic fold, re-baselining `StartStateHash`). This story deliberately does none of that and leaves the `DedicatedServer` StartGame path byte-unchanged. **Action:** file this as a `DW-` ledger entry so the deferral is tracked.

## Verification

**Commands:**
- `dotnet build godot/godot.sln` -- expected: succeeds (client adapters compile; analyzer gate green — new sim files are float-free/Godot-free).
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all pass, including new validator/gate tests and unchanged golden/hero-persistence tests (proves no determinism movement; the lone `CanonicalModelHashPerfTests` timing flake, if it appears, is a known CPU-contention flake — confirm in isolation, not a regression).
- `npm --prefix docs/server-deploy/nakama-modules install && npm --prefix docs/server-deploy/nakama-modules test && npm --prefix docs/server-deploy/nakama-modules run build` -- expected: `validateHeroProfile` + RPC-handler tests pass; `build/index.js` bundle produced.

**Manual checks (if no CLI):**
- If Docker is available: `docker compose -f docs/server-deploy/docker-compose.yml config` resolves the module mount; otherwise inspect the compose diff — the `./nakama-modules/build:/nakama/data/modules` volume is present.
- Confirm `OnlineProfileSource.Save` has no `WriteStorageObjects` call path (grep) — writes route only through the RPC.
- Confirm the activation is live (grep): `LobbyUi` constructs `OnlineProfileSource` and gates `OnReadyPressed`/Ready on `AttestHeroProfileAsync` — no dormant rail.

## Auto Run Result

Status: done (follow-up review pass over the previously-shipped story `15472e0`).

**Summary of change (this pass).** A fresh four-layer review (adversarial / edge-case / verification-gap / intent-alignment) of the whole diff since `dd896a6`. The intent-alignment auditor confirmed the shipped rail aligns with `<intent-contract>` under its defensible operational readings — the online rail is genuinely LIVE (`LobbyUi` constructs `OnlineProfileSource` and fail-closed-gates Ready on `AttestHeroProfileAsync`), not dormant — so no intent gap and no spec-level defect. One medium UX patch was applied; four real-but-out-of-scope/unverifiable findings were deferred to the ledger; ten findings were rejected as noise or intent-scoped-out.

**Files changed (this pass).**
- `godot/src/UI/HeroPickerOverlay.cs` — patch: disable "Play without a saved hero" in online mode (`_playWithoutBtn.Disabled = _online != null`), removing a fail-closed dead-end where an online player could dismiss the picker into an un-readied state.
- `_bmad-output/implementation-artifacts/deferred-work.md` — appended four new defer entries (online reachability / O(n²) validation DoS / TS-tests-not-in-CI / wire-format round-trip test).
- `_bmad-output/implementation-artifacts/spec-9-12-...md` — this triage log + Auto Run Result + frontmatter (`status: done`, `followup_review_recommended: false`).

**Review findings breakdown.** 1 patch applied (medium: online "Play without" dead-end). 4 deferred (online picker end-to-end reachability needs live-Nakama verification; O(n²) TS validation before size cap; TS vitest not in CI; no C#-serialized round-trip parity test). 10 rejected — notably the `esbuild --format=cjs` "InitModule hidden from Nakama" claim was empirically **disproven** (built the bundle; `InitModule` is a flat top-level declaration → global under goja; 33/33 TS tests pass), and the "server doesn't verify earned stats / raw-write forgery" claims are intent-scoped-out (EA server-owned-record model; residual host enforcement is DW-200).

**Follow-up review recommendation:** `false`. Patched findings this pass: 0 high, 1 medium, 0 low → 3×1 + 1×0 = 3 (< 5) and no high ⇒ false.

**Verification performed.**
- `dotnet build godot/godot.sln` — succeeded, 0 errors (13 pre-existing warnings).
- `dotnet test godot/ProjectChimera.Sim.Tests` — Passed 3375, Failed 0, Skipped 1 (no golden/`StartStateHash` movement; the patch touches only UI).
- `npm --prefix docs/server-deploy/nakama-modules install && test && run build` — 33/33 tests pass; `build/index.js` (6.9kb) produced; `InitModule` confirmed top-level in the bundle.

**Residual risks.** The deferred online-reachability item is the material one: the fail-closed Ready gate is only as usable as the online picker's ability to present a saveable hero, which was not exercisable in the review sandbox (no live Nakama). Recommend a two-client live-Nakama smoke test before relying on online play. The build/ bundle is gitignored (produced by `npm run build`) and is not committed. `node_modules/` from the verification `npm install` is gitignored and left in place.

