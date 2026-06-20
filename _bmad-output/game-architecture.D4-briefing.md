# D4 Briefing - Hero Persistence Model

> Sidecar to the Project Chimera game-architecture pass. Companion to D1 (Bounded Effect-Graph), D2 (Typed Event/Dataflow Graph), D3 (Unified deterministic schema & loader). This is **recommend-and-confirm**: every decision below is a RECOMMENDATION for you to confirm or override, not a final call.

## What D4 decides

The WHAT is locked upstream (decisions #15/#19/#20, FR-7a-e + addendum SectionG): a WC3 `-load`-style persistent-artifact system - a per-scenario **persistence manifest** (which hero attributes carry to the next custom game), per-player **profiles loaded as deterministic initial state**, **server-side validation** to kill the WC3 `-load` forgery exploit, and a first-class **hero-picker save/load UI** as a reusable platform component. Mid-game single-player save/resume (full-world serializer) is explicitly **out of 1.0** and not designed here.

D4 decides **HOW** to build that on Chimera's current scaffold, around two hard sub-problems:

1. **The determinism crux.** In server-authoritative deterministic lockstep, every peer must begin tick 0 from byte-identical sim state. Profiles are **private per-player data** - no peer can independently reproduce another peer's profile - so the agreed initial state must be **exchanged and hashed at init**, never injected mid-game through the command bus (LockstepManager has no snapshot channel; `ApplyOrders` at `LockstepManager.cs:594-601` carries only unit orders). A tampered or mismatched profile must be **rejected before tick 0 (fail-closed)**.
2. **The hero sim model is net-new.** Chimera has no hero/leveling/inventory/skill-tree/currency concept today. Level/XP/items/skill-tree/currency must live as deterministic Fixed-only SoA sim state and integrate with D1's Modifier/ability/Energy-Mana additions. Persistence serializes a **subset** of that model.

---

## Decision (recommended)

**Option C - Two-rail: server-authoritative source-of-truth + local cache, one validation boundary designed in M2.**

Define **one** `PlayerProfile` model, **one** `ContentLoader + Validate(profile, manifest)` boundary, and **one** canonical-model `startStateHash` in M2 - but split the **source** rail:

- **Offline/single-player** reads/writes a local `user://` profile through that same loader+validate. The local value is authoritative only for offline play and is **structurally flagged untrusted**.
- **Online** treats the Nakama server storage object as the **sole** source of truth, delivered by a **trusted host that attests the canonical start-state hash** (corrected B2 - see below), and never accepts a client-written profile.

M2 ships: the sim hero model (HeroStore), manifest authoring (with declared bounds), the loader/validate boundary, the hero-picker UI, the match-end save write, and the offline local rail. The validation boundary and the server-delivery/attestation hook are **designed and stubbed behind an `IProfileSource` interface** in M2, so M5 drops in the Nakama RPCs + server attestation **without re-touching init or hash code**.

The single load-bearing correction the adversarial review forces: **"server-authoritative" must mean a trusted host that COMPUTES or SIGNS the start-state hash and the dedicated server REFUSES StartGame on disagreement - not a relay that compares two self-reported hashes.** Equality of self-reported hashes is *agreement*, not *authority*; two colluding clients pass it. This corrects the original B2 framing, which silently assumed server-side capabilities the relay does not have.

---

## Why C, not A or B

### A - Signed client save-codes (WC3-faithful, client-held) - REJECTED

Each profile is a client-held HMAC/Ed25519-signed blob; peers broadcast, verify signature, apply, and rely on `startStateHash` to catch divergence.

- **Lightest to ship**, single online/offline path, WC3-nostalgic pasteable codes.
- **Disqualified by the project's own non-negotiables and the WC3 research consensus.** A signature proves *who signed this value*, not *that the value is legitimate*. To sign offline the client must hold the key - if the client holds the key, tamper-proofing collapses. A legitimately-signed high-level profile is clonable/replayable across accounts without server custody of the canonical value. `startStateHash` detects *divergence*, not *forgery*: a uniformly-forged profile is deterministic and passes. This directly violates "everything shared between peers must be statically server-validatable, fail-closed."

### B - Pure server-authoritative now (Nakama storage + RPC), maximal-now - CORRECT POSTURE, WRONG TIMING

Profile is a Nakama storage object (Owner-Read, No-Client-Write); a `save_profile` RPC validates before persist; a `load_profiles` RPC returns validated profiles; a trusted host folds them into start state.

- **Closes the WC3 exploit at the root** and honors the maximal-now discipline D1/D2/D3 set.
- **Front-loads the entire Nakama server-runtime** (net-new TS/Go RPCs, storage schema, identity binding, **and** - per the adversarial review - a trusted-host start-state assembly/attestation tier) into M5, far from where the solo dev wants to *feel* hero progression (M2). M2 alone delivers little playable persistence.

### C - Two-rail (RECOMMENDED)

C is B's online rail **plus** an offline rail that shares one model, one loader+validate boundary, and one canonical-model `startStateHash`. It is the only option that satisfies the milestone's hardest constraint **verbatim** - *"the M2 design must NOT bake in client-trust assumptions that M5 then has to rip out... design the validation boundary in M2 even if the server endpoint lands in M5"* - while still giving the solo dev a fully playable offline hero-progression loop (author manifest → play → save → reload hero) in M2 with zero backend. M5 is an additive `IProfileSource` provider swap at the seams that are cheap to change, and the genuinely deferrable piece (the Nakama backend + attestation tier) defers without regret. The offline profile is architecturally walled off from ever entering an MP start-state hash - the one place determinism and fairness actually bind.

**The adversarial review materially changed C in two ways** (detailed in "Adversarial review" below): (1) the online "delivery" is upgraded to **delivery + server attestation + server-side StartGame gating**, because the relay does not validate today; (2) the M5 server scope is **re-sized honestly** as a net-new content-aware/attestation tier, not a "provider swap" - the *interface seam* is the provider swap, the *server capability behind it* is large.

---

## Settled / recommended sub-decisions

Each is a recommendation. Items flagged **[USER CALL]** materially depend on your confirmation of scope or topology.

### D4-A - Online anti-tamper: server-stored, not signed codes
**Recommend:** Server-stored as the sole online source of truth (Nakama storage object: Owner-Read, No-Client-Write, written only via a validating server RPC). Signed client save-codes **rejected** for the online path. A local-only, explicitly-untrusted `user://` profile exists for offline single-player, structurally walled off from any MP hash.
**Rationale:** WC3 history + researchNotes - client-readable state is forgeable; a signature authorizes whatever value was signed and is clonable across accounts without server custody. The Nakama No-Client-Write + validating-RPC recipe maps 1:1 to FR-7c and matches the existing dedicated-server topology. Offline self-cheating is a non-goal to prevent, so a local rail is fine as long as it cannot enter MP.

### D4-B - Init topology: corrected server-authoritative delivery + attestation **[USER CALL on LAN scope]**
**Recommend:** B2, **corrected**. A trusted host must **compute or sign** the canonical `startStateHash` (or sign the validated profile set it delivers); each client verifies its locally-recomputed hash **equals the server-attested hash** (fail-closed). The dedicated server must **gate StartGame on server-side hash agreement** and refuse to broadcast StartGame on any mismatch or any peer reporting zero/missing hash. **Drop the B1 LAN peer-broadcast fallback** (it reintroduces a peer-trust path the architecture lists as a non-goal); route LAN through a local listen-server acting as authority, or carve LAN persisted-profiles out of 1.0.
**Rationale:** The adversarial Determinism and Static-validation lenses both raised **CRITICAL**. `DedicatedServer.HandleReady` (`DedicatedServer.cs:171-191`) broadcasts StartGame the instant `_ready[0] && _ready[1]` flip, with **no hash compare** - the scenarioHash check lives only client-side in `LobbyUi.cs:315`. Equality of two self-reported hashes is agreement, not authority; collusion passes. Fail-closed requires a trusted party to compute/sign the hash and the server to enforce it. **The single biggest correction in this briefing.**

### D4-C - A new canonical-model `startStateHash`, computed pre-apply, server-attested, zero = hard reject
**Recommend:** A **new** `startStateHash` (D3 FNV-64 over `Fixed.Raw`, algo-2, excluding `_editor`/`_ext`) over the full applied initial sim state **including every player's applied profile + the manifest**. Compute it **pre-apply over the canonical MODEL** - never over post-`ApplyScenario` state that passed through `Fixed.FromFloat`. Server-attest it; verify in the handshake alongside `scenarioHash` and `rulesetHash`. Do **not** extend `ComputeFileHash` (raw-bytes; `ScenarioSerializer.cs:59-80`) or `SimChecksum` (P1/P2-only live-state; `SimChecksum.cs:53-54`). **Remove the `hash != 0` skip tolerance** (`LobbyUi.cs:315`) - a missing/zero hash is a hard reject.
**Rationale:** Profiles arrive from multiple sources (server, file, multiple players) whose byte serialization differs even for identical canonical values - only a canonical-model hash is stable. The two producers (M2 local, M5 server) will **diverge structurally** unless both hash the `FromRaw` canonical model pre-apply, because `MainScene.cs:518` today ingests `StartOre` via lossy `Fixed.FromFloat`. The zero-hash skip (`if (ScenarioHash != 0 && peerHash != 0 ...)`) is a trivial single-client bypass: a modded client zeroes its hash field and skips the compare. Clean taxonomy: `scenarioHash`=content, `rulesetHash`=logic graph (D2), `startStateHash`=applied initial state incl. profiles. The disabled-persistence case (decision #15) must still produce an identical `startStateHash` across peers (profiles contribute nothing, deterministically).

### D4-D - Separate sparse HeroStore keyed by stable hero identity; generalize SimChecksum first
**Recommend:** A separate sparse `HeroStore` SoA keyed by a **stable cross-match hero identity (NOT the recycled EntityWorld free-list entity id)**, all fields Fixed/int only, applied via `Fixed.FromRaw`. HeroStore folds into **both** `SimChecksum` and `startStateHash` - which **requires generalizing `SimChecksum` from its P1/P2-only hash to all active factions FIRST**, or P3/P4 hero state is silently dropped from the desync hash (fail-open).
**Rationale:** Heroes are sparse (a few per match), so dense 4096-wide arrays (`EntityWorld.cs:62`) waste memory and pollute the lean combat SoA. D1 is already adding a Modifier SoA + Energy/Mana arrays the HeroStore must integrate with; co-locating is cleaner. **EntityWorld recycles entity ids via a free list** - so the entity id is *not* a stable persistence key; a distinct hero identity is needed for cross-match save/load (raised by the SCOPE lens, decision #19 reference). The P1/P2-only `SimChecksum.cs:53-54` is a *hard dependency* of the fold, not a loose prerequisite.

### D4-E - Engine-enforced real-account binding for online persistence **[USER CALL on global vs per-scenario]**
**Recommend:** Bind online profiles to the Nakama account/userId; make real-account (email) auth an **engine-enforced** precondition - if a scenario's manifest enables online persisted profiles, the lobby path **hard-rejects device-auth sessions** before any profile loads. Prefer a **global** email-auth rule for online persistence in 1.0 (defer the per-scenario conditional to v2 to avoid branching the lobby flow early). Device-auth stays fine for casual/LAN; its profiles are local-untrusted. Treat `NakamaKey='defaultkey'` (`NakamaService.cs:38`) as a deployment secret, not committed.
**Rationale:** `ConnectDeviceAsync` (`NakamaService.cs:100-115`) authenticates on a client-supplied device-id string with `create:true` - ownership is as strong as a forgeable string, defeating FR-7c. A per-scenario *soft* implication is bypassable (Static-validation lens, MAJOR): the **engine, not the creator**, must enforce persistence-enabled-online ⇒ real-account-required. A global rule (SCOPE lens) avoids inventing M5 lobby-branching complexity early.

### D4-F - Fine-grained manifest bounds + a Validate(manifest) engine-ceiling gate
**Recommend:** Fine-grained manifest declaring which categories carry over AND their bounds (max_level, currency_cap, allowed item-ids + per-stack caps, skill-point cap); `Validate(profile, manifest)` enforces every value. **Critical addition:** the manifest itself must pass a `Validate(manifest)` **engine-ceiling gate** (absolute caps, item-id existence against loaded content) *before* it can be the validation oracle; for online/ranked the effective bound is `min(declared, engine-ceiling)`. **Both manifests and profiles traverse the identical D3 ContentLoader + Validate choke point with no bypass, including the AI-generated path.**
**Rationale:** Bounds make server validation real, not a rubber-stamp. But the Static-validation lens raised **MAJOR**: the manifest bounds are themselves attacker/AI-controllable content (the M4 "AI everywhere" pillar). An unbounded manifest (`max_level=MaxInt`) makes Validate rubber-stamp a forged profile - the attacker controls the oracle. The manifest must be bounded before it can validate anything. Bounds stay data-driven (no hardcoded caps in a creator-unreachable path), and they are the exact spec the M5 server RPC validates against - authoring them in M2 is not throwaway.

### D4-G - Keep toggle + bounds; CUT the bespoke normalization enum from 1.0 **[USER CALL on scope]**
**Recommend:** Support persistence in any scenario (creator's choice via the decision #15 toggle + Fork F bounds), but **cut the bespoke "normalization mode" enum from D4 1.0**. If symmetric/capped heroes are wanted, express normalization as **D1 Modifiers** (a clamping Modifier on the hero stat) and/or **D2 graph logic** at match-init - **not** an engine-side capping routine keyed off a manifest enum. Defer any dedicated normalization-ruleset selection to post-1.0.
**Rationale:** Two lenses converged. SCOPE: the normalization enum is invented surface not in the locked requirements, and a redundant second fairness mechanism (Fork F bounds already let a creator author symmetric heroes) on a 1.0 critical path. Brownfield-coherence (MAJOR): an engine-imposed capping routine is a creator-unreachable hardcoded balance path that **violates the data-driven non-negotiable** and **duplicates D1 Modifiers / D2 graph**. Keep the toggle + bounds; route any runtime transform through D1/D2. (Original recommendation defaulted the enum off; the review correctly argues it should not ship as engine code at all.)

---

## Decisions to confirm (the interactive calls)

1. **D4-A** - Confirm server-stored as the sole online truth and signed codes rejected. *(Recommended; low controversy.)*
2. **D4-B** - Confirm corrected B2 (server attests the hash + server gates StartGame). **And decide LAN scope:** drop LAN persisted-profiles for 1.0, or route LAN through a local listen-server authority? *(This is the load-bearing call.)*
3. **D4-C** - Confirm a new `startStateHash` (not extending ComputeFileHash/SimChecksum), computed pre-apply over the canonical model, and the removal of the zero-hash skip.
4. **D4-D** - Confirm separate HeroStore + stable hero identity + SimChecksum generalization as a *blocking* prerequisite of the fold.
5. **D4-E** - Confirm engine-enforced real-account auth for online persistence. **Global rule (recommended) or per-scenario conditional?**
6. **D4-F** - Confirm fine-grained bounds **and** the `Validate(manifest)` engine-ceiling gate.
7. **D4-G** - Confirm cutting the normalization enum from 1.0 and routing fairness transforms through D1/D2. *(Scope reduction - confirm you're comfortable shipping the capability to author unfair persisted-hero PvP, with the warning surfaced in authoring UI.)*
8. **Scope split (SCOPE lens):** Confirm M2 contains only the boundary + HeroStore (+ offline loop), and that `startStateHash` machinery, the multi-hash handshake, and the 2-player generalization land in M5 with the online rail. (See migration sequence - one nuance: the *float-leak cleanup* and *stable-identity* decisions must still be made in M2 even though the MP hash machinery defers.)

---

## Migration sequence (strangler, golden-checksum-gated, always-shippable, milestone-tagged)

Each step is independently shippable and gated by a golden-checksum test where determinism is touched. The hero model and the validation **boundary** land in M2; the online **authority** lands in M5 behind the M2 interface.

**M1 (verification floor - prerequisite hardening, mostly D3-owned, surfaced here because D4 hard-depends):**
1. Add the missing `Validate(model)` gate at `ApplyScenario` (`MainScene.cs:499-558`). Today float `StartOre` is trusted verbatim at line 518 and faction is derived as `(Faction)(slot.Slot+1)` with no bound check. D4's `Validate(profile, manifest)` hangs off this gate.
2. Strangle `Fixed.FromFloat` on the start-state path: convert `StartOre` ingest to `FromRaw` over the canonical model. Establish the invariant **"no `FromFloat` anywhere on the profile/manifest/start-state-hash path"** with an analyzer/test gate mirroring the D3 AOT CI gate.
3. Convert the float-leak trigger/threshold path to Fixed/int: `ScenarioDirector.cs:168` (`ore.ToFloat()`), line 170 (`ToString("F2")`), and the compare/add sites (~`:289`/`:336`). Hero currency/level/XP will feed these comparators - culture/platform-sensitive float formatting inside the tick is a desync hazard.

**M2 (core authoring - the boundary, designed-once, offline-playable):**
4. **HeroStore SoA** (Fixed/int only, stable hero identity key, integrating D1's Modifier SoA + Energy/Mana). *Gate: D1 frozen first.* Golden test: HeroStore fields fold into `SimChecksum`.
5. **PersistenceManifest model** (declared bounds, Fork F) + **`Validate(manifest)` engine-ceiling gate** + per-scenario toggle (#15). Pure data model in the sim/Definitions layer, registered in the D3 source-gen `JsonSerializerContext`, deserialized through the D3 `JsonConverter<Fixed>`.
6. **PlayerProfile model** (serialized hero subset + display metadata mirroring `ContentPackager.cs:38-51`), multiple profiles per player, `schema_version` + migration registry. `FromRaw` only.
7. **`IProfileSource` interface** (pure sim-layer) + **`LocalProfileSource`** impl (presentation/Multiplayer-layer, `user://` file IO). Enforce the local-untrusted wall **by construction** (separate types/methods - `LocalProfileSource` physically cannot supply bytes to an MP start-state assembler), not by a readable `IsTrusted` boolean a modded client can flip.
8. **`Validate(profile, manifest)`** - the single validation routine, authored once, invoked client-side for offline UX in M2 and server-side authoritatively in M5.
9. **Hero-picker UI** (`Control` in `src/UI`): per-save icon/name/level/summary, save/load/overwrite-with-confirm, multi-profile list. Surface the cross-scenario manifest-mismatch UX (clamp/reject/drop unrecognized fields - see watch-items).
10. **Match-end save write** (offline path): derive the new profile subset from final sim state, persist to `user://` through the shared loader. *Decide which peer derives the subset now (see watch-items) even though the online write defers.*
> **Shippable here:** full offline hero-progression loop. No backend.

**M5 (share/discover + online stack - the authority drops in behind the M2 seam):**
11. Generalize 2-player hardcodes to Player1..Player4: `SimChecksum.cs:53-54` (P1/P2-only ore) and `ScenarioDirector.cs:165` (`slot < 2`). *Blocking prerequisite of the HeroStore MP fold.* Golden test: a P3/P4 hero changing state changes `SimChecksum`.
12. Redesign the Ready packet **once** (shared-owned with D2): fixed-length multi-hash structure carrying `scenarioHash + rulesetHash + startStateHash`, reject-on-length-mismatch, **zero/missing hash = hard reject** (remove `LobbyUi.cs:315` skip). *Sequence after D2's rulesetHash packet change.*
13. **`startStateHash`** computed pre-apply over the canonical model, on both rails. Golden test: server-delivered profile and locally-loaded profile with identical canonical values produce identical `startStateHash`.
14. **Dedicated-server trusted-host tier** (D5 project split): server gates StartGame on hash agreement (`DedicatedServer.cs:171-191`) and **computes or signs** the canonical `startStateHash`. *This is net-new server capability, not a relay tweak.*
15. **`NakamaProfileSource`** impl + Nakama server-runtime RPCs (`save_profile` validates against bounds before persist; `load_profiles` returns validated profiles into the match flow) + storage schema (No-Client-Write) + identity binding (Fork E) + key custody.
16. **Replay v2** (decision #19): serialize the applied profiles into the replay so a replay reconstructs the exact start state. *Coordinate with D2's replay format v2.*

---

## Prerequisites surfaced

- **D1 frozen first.** The HeroStore is a subset-serializer of D1's hero sim model (Modifier SoA, Energy/Mana, items/skills-emit-Modifiers). A post-M2 D1 shift re-touches the persisted subset, its bounds, and both rails. Keep the persisted subset deliberately small and `schema_version`-tagged.
- **D3 boundary must exist:** ContentLoader choke point, canonical `JsonSerializerOptions`, `JsonConverter<Fixed>` (NaN/Inf reject), source-gen `JsonSerializerContext`, `schema_version` + migration registry, canonical-model FNV-64 (algo-2), **and the currently-missing `Validate(model)` gate at `ApplyScenario`**.
- **Dedicated server must become a trusted host**, not a pure relay: gate StartGame on server-side hash agreement and compute/sign `startStateHash`. `DedicatedServer.cs:171-191` does neither today. **Large net-new D5 component.**
- **2-player → Player1..Player4 generalization** before the HeroStore MP fold (`SimChecksum.cs:53-54`, `ScenarioDirector.cs:165`).
- **Float-leak cleanup** before persisted currency/level/XP drive triggers (`ScenarioDirector.cs:168/170` and compare/add sites).
- **Ready-packet redesign** sequenced after D2's rulesetHash packet change; PROTOCOL_VERSION reject in Hello (`TryReadHello` at `NetworkCommand.cs:198-205` reads but does not reject today).
- **M5:** Nakama server-runtime (TS/Go) - matchmaking-only today (`NakamaService.cs:16-18`, no storage/RPC); secret/key custody for the signing/RPC keys.

---

## Hand-offs

- **To D1:** D4 needs the final shape of the Modifier SoA, Energy/Mana arrays, and how items/skills emit Modifiers. Hand off the persisted-subset requirement (level/XP/currency/inventory/skill-tree) so D1 lays those out as Fixed/int SoA in/beside the HeroStore. **D1 must freeze before M2.**
- **To D2:** Confirm the hash taxonomy split (`rulesetHash` = logic graph, new `startStateHash` = applied initial state incl. profiles). The Ready-packet redesign is **shared-owned** - sequence D4's startStateHash after D2's rulesetHash packet change. Any profile-seeded trigger state (timers/variables/DslVarTable entries) must use D2's canonical ordering, **not** insertion-order `Dictionary` iteration (`ScenarioDirector.cs:33-34`/`:149`), or peers desync at init.
- **To D3:** Register `PlayerProfile` and `PersistenceManifest` in the source-gen context, the `JsonConverter<NodeBase>`/closed-registry discipline, the migration registry, the canonical-model hash exclusions (`_editor`/`_ext`). Add the `Validate(profile, manifest)` **and** `Validate(manifest)` entrypoints to the D3 Validate gate. Confirm AI-generated profiles/manifests traverse the same choke point with no bypass.
- **To D5/engine (dedicated-server project split):** the trusted-host start-state attestation + StartGame hash-gate + `load_profiles` flow live in D5 scope. Hand off the `IProfileSource` contract so server and local impls are interchangeable. **Re-size honestly: this is content-aware/attestation server capability, not "add two RPCs."**
- **To implementation:**
  - *Boundary placement (sim/presentation discipline):* `PlayerProfile` + `PersistenceManifest` + `IProfileSource` are **pure sim-layer** types (no `using Godot`, no Nakama). `LocalProfileSource` (file IO) and `NakamaProfileSource` (Nakama SDK) are **presentation/Multiplayer-layer** impls depending on the sim interface, never the reverse. Hero-picker `Control` in `src/UI`.
  - *M2 artifacts:* HeroStore SoA; PersistenceManifest (+ engine-ceiling gate); PlayerProfile; `IProfileSource` + `LocalProfileSource`; `Validate(profile, manifest)` + `Validate(manifest)`; hero-picker UI; match-end save write.
  - *M5 artifacts:* `NakamaProfileSource` + RPCs + identity binding + server attestation tier; `startStateHash`; multi-hash handshake; 2-player generalization; replay v2 profile serialization.

---

## Residual risks / watch-items

1. **Boundary leakage (linchpin of C).** The "local-untrusted profile never enters an MP `startStateHash`" invariant must be enforced **by construction** (separate types - `LocalProfileSource` physically cannot feed the MP assembler), not by a readable `IsTrusted` flag a modded client flips. The shared `ApplyScenario` choke point (`MainScene.cs:499`) trusts everything verbatim today - exactly why a runtime flag is fragile. Cover with a golden-checksum regression test, but the type system, not the boolean, must make "local profile in MP hash" unrepresentable.
2. **Authority vs agreement (the corrected B2).** If the server ends up merely relaying self-reported hashes (its current behavior for Checksum at `DedicatedServer.cs:148-157`), online is *not* fail-closed - collusion passes. The server must compute or sign the hash and refuse StartGame on disagreement. Golden test the gate as **"client-folded hash == server-attested hash"**, not "peer A == peer B."
3. **Cross-producer hash determinism.** `startStateHash` must be byte-identical from a server-delivered vs locally-loaded profile for identical canonical values. The two producers are asymmetric (M2 local path vs M5 server path); any `FromFloat` round-trip or serialization-ordering drift desyncs at init. The canonical-model (pre-apply, FromRaw) hash mitigates this but **must be golden-tested across both producers**.
4. **Cross-scenario manifest mismatch (not deferrable - shapes the schema).** A profile saved under manifest X may load into a scenario with different categories/bounds/item-ids. `Validate` must specify **clamp vs reject vs drop** for unrecognized/out-of-bounds fields. Decide before finalizing the PlayerProfile schema.
5. **Match-end save-write trust seam.** *Which peer* derives the end-of-match profile? A client-submitted end-profile reintroduces exactly the trust seam the design exists to close. For online, the trusted host (or a server RPC re-validating against bounds) must derive/validate the result; for offline, the single peer is fine. Decide in M2 even though the online write defers.
6. **Hero identity vs entity-id reuse.** EntityWorld recycles ids via a free list; the persistence key must be a stable cross-match hero identity, not the entity id (decision #19 - surface it explicitly; it was under-surfaced originally).
7. **Device-auth identity gap.** Until real-account binding is engine-enforced (M5), ownership is as forgeable as a device id. If M5 ships server storage but skips the auth hard-gate, FR-7c is tamper-proof in *storage* but not in *ownership* - a partial fix that feels complete.
8. **PvP balance punted to creators (Fork G).** The platform ships the capability to author unfair persisted-hero PvP. Intentional (creator's problem) but a reputational/UX risk; mitigated only by surfacing the warning in authoring UI, not by engine enforcement.
9. **Spectator trust boundary.** `DedicatedServer` broadcasts commands/chat to spectators (`DedicatedServer.cs:164`, `:220`). Define whether spectators receive profile data or `startStateHash`, and how a spectator-injected packet is rejected.
10. **M2/M5 boundary drift.** The `IProfileSource` contract and `Validate(profile, manifest)` signature defined in M2 must stay stable so the M5 Nakama provider drops in cleanly. If D1 shifts after M2, the persisted subset and its bounds shift too - keep the subset small and versioned.

---

## Adversarial review

Four lenses reviewed the proposal; all returned **sound-with-changes**. The findings did not overturn the recommendation (Option C survives) but materially **re-scoped and hardened** it. Summary of what each found and how it changed the briefing:

**Determinism & Lockstep (2 critical, 4 major, 1 minor).** Confirmed against code that `DedicatedServer.cs` is a **pure relay** with no scenario/sim/hash capability, and `HandleReady` broadcasts StartGame with **no hash compare** - so the original B2 "server folds profiles into start state and ships them" described capability that does not exist, and the handshake is **fail-OPEN at the server**. *Folded:* D4-B corrected to require server attestation + server-side StartGame gating; the M5 server scope re-sized as net-new content-aware/attestation tier (prerequisites + watch-item 2). Also surfaced: the `hash != 0` skip is a single-client bypass (folded into D4-C as hard-reject), the `ScenarioDirector` float threshold path leaks float into the tick (folded into M1 cleanup), insertion-order `Dictionary` iteration for profile-seeded trigger state desyncs (hand-off to D2), and the LAN B1 fallback violates the no-P2P non-goal (D4-B drops it). *Rejected:* none material - all findings hold against the code.

**Static-validation & anti-tamper (2 critical, 3 major, 1 minor).** Independently confirmed the relay finding and added the **oracle-control** attack: the manifest bounds are attacker/AI-controllable, so an unbounded manifest makes Validate rubber-stamp a forgery. *Folded:* D4-F gains a `Validate(manifest)` engine-ceiling gate and `min(declared, engine-ceiling)` for ranked; AI-gen path explicitly routed through the same choke point. Also: device-auth gate must be engine-enforced not creator-optional (D4-E hardened), `startStateHash` must be server-computed/signed not relay-compared (watch-item 2), handshake parsing must be fixed-length fail-closed (M5 step 12), and the local-untrusted wall must be structural not a boolean flag (watch-item 1). Surfaced spectator trust boundary (watch-item 9) and `NakamaKey='defaultkey'` secret custody (D4-E).

**Brownfield-fit & D1/D2/D3-coherence (1 critical, 4 major, 2 minor).** Confirmed the relay misdescription a third time and added: the Ready packet is a fixed 5-byte single-hash format whose redesign is **shared-owned with D2** and must be sequenced after D2's rulesetHash change (hand-off + M5 step 12); Fork G normalization duplicates D1 Modifiers / D2 graph and violates the data-driven pillar (D4-G corrected to *cut* the enum, route through D1/D2); the HeroStore→SimChecksum fold inherits the P1/P2 hardcode as a *hard* dependency (D4-D); the float-comparator cleanup spans three more lines than originally cited (M1 step 3); and `PlayerProfile`/`IProfileSource` placement must respect the sim/presentation boundary (hand-off).

**Scope & solo-dev-cost (2 major, 1 minor).** Argued M2 was over-scoped - only the boundary + HeroStore are retrofit-expensive seams that must be in M2; the `startStateHash` machinery, multi-hash handshake, and 2-player generalization bind only when the online rail exists (M5). *Folded:* migration sequence now lands those in M5, with the nuance that the float-leak cleanup, stable-identity decision, and match-end-derivation decision must still be *made* in M2. Argued Fork G normalization is invented surface (folded into D4-G cut) and Fork E should be a global rule to avoid early lobby branching (folded into D4-E). Surfaced cross-scenario manifest mismatch (watch-item 4), match-end save-write determinism (watch-item 5), and hero identity vs entity-id reuse (watch-item 6, decision #19). *Partially rejected:* "no throwaway work" was over-claimed - acknowledged as the D1-freeze ordering gate (prerequisite + watch-item 10) rather than a guarantee.

**Net effect on the recommendation:** Option C stands - it remains the only option satisfying the milestone's "design the boundary in M2 without baking in client-trust" constraint while delivering a playable offline loop. The review's lasting correction is honesty about the *online authority*: "server-authoritative" must be a trusted host that **computes/signs and enforces** the start-state hash, and the M5 server work is a **large net-new tier**, not a provider swap. The interface seam is the swap; the capability behind it is the build.