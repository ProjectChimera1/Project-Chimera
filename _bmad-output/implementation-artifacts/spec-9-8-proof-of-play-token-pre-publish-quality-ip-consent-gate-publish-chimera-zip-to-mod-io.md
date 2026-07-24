---
title: 'Proof-of-play token + pre-publish quality/IP-consent gate + publish .chimera.zip to mod.io'
type: 'feature'
created: '2026-07-24'
status: 'done'
baseline_revision: 'ad71afd1eaf5df6498f3942a14499bc5b436f14a'
final_revision: '494e0a14c5b51927407f4e5ab96fcdda1de2012d'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/project-context.md'
  - '{project-root}/_bmad-output/implementation-artifacts/epic-9-context.md'
  - '{project-root}/godot/src/Core/Definitions/CanonicalModelHash.cs'
  - '{project-root}/godot/src/Core/Definitions/ContentPackager.cs'
  - '{project-root}/godot/src/Core/Definitions/ContentPackageManifest.cs'
  - '{project-root}/godot/src/Core/Definitions/SecretIds.cs'
  - '{project-root}/godot/src/Core/Definitions/FileSecretStore.cs'
  - '{project-root}/godot/src/Core/Bootstrap/Phases/ScenarioDelegateBinder.cs'
  - '{project-root}/godot/src/UGC/ModIoService.cs'
  - '{project-root}/godot/src/UI/ContentBrowserPanel.cs'
  - '{project-root}/godot/src/Core/Bootstrap/Phases/WinConditionPhase.cs'
warnings: ['multiple-goals', 'oversized']
---

<intent-contract>

## Intent

**Problem:** A creator can publish any scenario to mod.io with no proof they ever beat it and no recorded IP-ownership consent, and there is no unified pre-publish quality gate — packaging (`WinConditionPhase` export) and upload (`ContentBrowserPanel`) are two disconnected surfaces with only scenario-shape validation between them.

**Approach:** Mint a locally-signed proof-of-play token `{scenarioHash, outcome, timestamp, signature}` when the local player wins their own scenario (hooking the existing `ScenarioDirector.OnVictory` delegate), persist it keyed by scenario identity, then add one Godot-free `PublishGate` that refuses upload unless a *valid* token, the min-quality fields (thumbnail, description ≥100 chars, ≥1 screenshot), and an explicit IP-ownership consent are all present — writing token + screenshots + consent into the `.chimera.zip` manifest and only then calling the existing `ModIoService.UploadModAsync`.

## Boundaries & Constraints

**Always:**
- The token's `scenarioHash` is the full 64-bit `CanonicalModelHash.Compute(model)` (NOT the 32-bit `ToWire` fold, NOT the file-byte `ScenarioSerializer.ComputeFileHash`), so it binds to the canonical model identity and re-derives to a mismatch after any content edit.
- Mint a `win` token ONLY when `OnVictory`'s winner slot equals the local faction; a loss or a win by any other faction mints nothing.
- Token signing is HMAC-SHA256 over a canonical payload with a per-install key held in `ISecretStore` (new id `SecretIds.ProofOfPlay`); provision the key presentation-side. Verification recomputes and compares; any hand-edit to a token field fails the check.
- New pure logic (token model, signer, store, publish gate) is Godot-free and lives beside existing UGC/Definitions types so it is Tier-1 testable in `ProjectChimera.Sim.Tests`. Presentation/Godot wiring (mint hook, consent checkbox, upload) stays out of the pure cores.
- Reuse existing infrastructure: `CanonicalModelHash`, `ContentPackager.Pack`, `ContentPackageManifest`, `ModIoService.UploadModAsync`, `FileSecretStore`/`SecretIds`. Do not add a new dependency (NakamaClient stays the sole NuGet).
- Any wall-clock read (token timestamp) uses the established `#pragma warning disable RS0030` exemption pattern (see `ContentPackageManifest.CreatedAt`), and lives off the sim tick path.

**Block If:**
- Minting or hashing cannot be done without touching the sim tick loop, `SimChecksum`, or bumping `CanonicalModelHash.AlgoVersion` (that would move committed goldens — proof-of-play is strictly presentation-side post-match).
- The intended local HMAC signing key cannot be stored/retrieved without a design decision beyond the `ISecretStore` seam.

**Never:**
- Never treat this as anti-cheat: the local HMAC makes the token tamper-evident WITHIN an install for trusted-friends EA; cross-machine forgery resistance and server attestation are explicitly out of scope (that is the 9.12 online rail).
- Never build a parallel rating/search/browse system, a mod.io media/screenshot upload endpoint, or runtime asset ingest here (9.9/9.10).
- Never change `CanonicalModelHash.AlgoVersion`, `SimChecksum`, `PROTOCOL_VERSION`, or any committed golden.
- Never store the signing key or mod.io API key in `SettingsData` or a Godot `[Export]` (keys live only in `ISecretStore`).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Self win | `OnVictory(slot)` where slot == local faction | Signed token `{Compute(model), "win", ts}` stored for that scenario id | n/a |
| Other/loss | `OnVictory(slot)` where slot != local faction | No token minted | n/a |
| Valid publish | token valid + thumbnail + desc≥100 + ≥1 screenshot + consent=true | `PublishGate` passes; token/screenshots/consent written to manifest; `UploadModAsync` invoked; modId surfaced | n/a |
| Missing token | no token (or none minted) | Gate FAILS, reason "no proof-of-play"; upload refused | Located reason returned |
| Tampered token | any token field edited after signing | `Verify` fails → gate FAILS "invalid token" | Rejected, not uploaded |
| Edited scenario | token minted, then scenario content changed | `Compute(model) != token.scenarioHash` → gate FAILS "token stale" | Rejected |
| Short/missing quality | desc<100 chars OR no thumbnail OR 0 screenshots | Gate FAILS with the specific missing field(s) | All failing reasons listed |
| No consent | consent unchecked | Gate FAILS "consent required"; upload blocked | Rejected |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/ProofOfPlayToken.cs` — **NEW**, Godot-free POCO. Fields (System.Text.Json snake_case): `scenario_hash` (string, hex of the 64-bit ulong), `outcome` (string, "win"), `minted_at` (ISO-8601 string), `signature` (string hex), `scenario_id` (string). Data only — no crypto, no DateTime.
- `godot/src/UGC/ProofOfPlaySigner.cs` — **NEW**, Godot-free. `Create(ulong scenarioHash, string outcome, string mintedAt, string scenarioId, byte[] key) -> ProofOfPlayToken` (builds canonical payload, HMAC-SHA256 → hex signature); `bool Verify(ProofOfPlayToken, byte[] key)` (constant-time compare); `bool MatchesScenario(ProofOfPlayToken, ulong currentHash)`.
- `godot/src/UGC/ProofOfPlayStore.cs` — **NEW**, Godot-free over an injected OS-absolute dir (`user://tokens/`), mirroring `LocalProfileSource`/`FileSecretStore`. `Save(string scenarioId, ProofOfPlayToken)`, `bool TryLoad(string scenarioId, out ProofOfPlayToken)`. One JSON file per scenario id (id sanitized to the `^[a-z0-9_-]+$` file-safe rule).
- `godot/src/UGC/PublishGate.cs` — **NEW**, Godot-free unified gate. `PublishGateResult Check(ContentPackageManifest, ProofOfPlayToken?, ulong currentScenarioHash, byte[] signingKey)` → `{ bool Passed; IReadOnlyList<string> Reasons }`. Enforces every Matrix row (valid+signed+non-stale token, thumbnail present, description≥100, screenshots≥1, ip_consent true).
- `godot/src/Core/Definitions/ContentPackageManifest.cs` — add `proof_of_play` (`ProofOfPlayToken?`), `screenshots` (`List<string>` zip-relative paths), `ip_consent` (`bool`). `description`/`thumbnail_file` already exist.
- `godot/src/Core/Definitions/ContentPackager.cs` — `PackOptions` gains `ProofOfPlayToken? Token`, `List<string> ScreenshotPaths` (on-disk PNGs), `bool IpConsent`. `Pack` writes token→manifest, copies screenshots into `screenshots/shot_NN.png` + records `manifest.Screenshots`, sets `manifest.IpConsent`. Existing `scenario_hash` FNV path unchanged.
- `godot/src/Core/Definitions/SecretIds.cs` — add `public const string ProofOfPlay = "proof_of_play";`.
- `godot/src/Core/Bootstrap/Phases/ScenarioDelegateBinder.cs` — extend the `OnVictory` binding (:48) so that, in addition to `ShowGameOver`, when `winnerSlot == ctx.Lockstep.EffectiveLocalFaction` it provisions the signing key (via `SecretStore`, generating 32 random bytes presentation-side on first use), computes `CanonicalModelHash.Compute(activeModel)`, and mints+stores a token via `ProofOfPlaySigner`/`ProofOfPlayStore` with an RS0030-exempt wall-clock `minted_at`. Keep the C3 rule: read no sim state in the delegate body beyond the model already held by the context.
- `godot/src/Core/Bootstrap/Phases/WinConditionPhase.cs` — export flow (`ExportMapPackage`, ~:338) loads the token from `ProofOfPlayStore` by scenario id and captures ≥1 screenshot (e.g. a viewport `get_image()` grab) into `PackOptions.ScreenshotPaths`, so the packaged manifest carries token + screenshots.
- `godot/src/UI/ContentBrowserPanel.cs` — Local-card upload (`BuildLocalCard`, ~:362): add an IP-ownership consent checkbox (upload disabled until checked), run `PublishGate.Check` before `UploadModAsync`, refuse with the returned reasons on failure, surface the modId on success.
- `godot/ProjectChimera.Sim.Tests/UGC/**` — **NEW** Tier-1 tests: `ProofOfPlaySignerTests`, `ProofOfPlayStoreTests`, `PublishGateTests`, `ContentPackagerProofOfPlayTests`, and a `ProofOfPlayMintFlowTests` (build `ScenarioData` → Compute → mint → verify → edit model → assert mismatch/stale).

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Definitions/ProofOfPlayToken.cs` (NEW) — signed-token POCO — the persisted proof artifact.
- `godot/src/UGC/ProofOfPlaySigner.cs` (NEW) — HMAC-SHA256 create/verify + `MatchesScenario` — tamper + stale detection.
- `godot/src/UGC/ProofOfPlayStore.cs` (NEW) — per-scenario token persistence under `user://tokens/`.
- `godot/src/UGC/PublishGate.cs` (NEW) — single quality/consent/token refusal gate returning located reasons.
- `godot/src/Core/Definitions/ContentPackageManifest.cs` — add `proof_of_play`/`screenshots`/`ip_consent` fields.
- `godot/src/Core/Definitions/ContentPackager.cs` — extend `PackOptions`; `Pack` writes token, screenshots dir, consent.
- `godot/src/Core/Definitions/SecretIds.cs` — add `ProofOfPlay` id.
- `godot/src/Core/Bootstrap/Phases/ScenarioDelegateBinder.cs` — mint-on-self-victory hook (key provision, Compute, sign, store).
- `godot/src/Core/Bootstrap/Phases/WinConditionPhase.cs` — load token + capture screenshot into `PackOptions` at export.
- `godot/src/UI/ContentBrowserPanel.cs` — consent checkbox + `PublishGate` refusal before `UploadModAsync` + modId surface.
- `godot/ProjectChimera.Sim.Tests/UGC/**` (NEW) — Tier-1 tests covering every I/O-Matrix row (sign/verify/tamper/stale, store round-trip, gate pass/fail per field, manifest+packager round-trip, mint flow).

**Acceptance Criteria:**
- Given a scenario whose Victory leaf fires for the local player, when `OnVictory` signals their win, then a signed `{scenarioHash=Compute(model), outcome=win, timestamp}` token is stored for that scenario, and a loss or another faction's win mints nothing.
- Given a token whose `scenarioHash` was minted from a model, when the same model is later edited, then `CanonicalModelHash.Compute` no longer equals the stored hash and the gate treats the token as stale/invalid.
- Given the publish flow, when upload is attempted, then it is REFUSED unless a valid (signed, non-stale) token AND thumbnail AND description ≥100 chars AND ≥1 screenshot AND checked IP-ownership consent are all present, and the token + screenshots + consent are written into the manifest.
- Given a complete, consented, quality-passing package, when the creator publishes, then `ContentPackager.Pack` embeds the fields and `ModIoService.UploadModAsync` is invoked, surfacing the returned modId on success.
- Given the full suite, when it runs, then every pre-existing committed golden is byte-identical and `CanonicalModelHash.AlgoVersion`(14)/`SimChecksum.AlgoVersion`(21)/`PROTOCOL_VERSION`(2) are unchanged (a moved golden = Block-If).

## Spec Change Log

_None — no bad_spec loopback occurred; the review pass resolved via patches only._

## Review Triage Log

### 2026-07-24 — Follow-up review pass (re-review of committed `done`)
- intent_gap: 0
- bad_spec: 0
- patch: 0
- defer: 1: (high 0, medium 0, low 1)
- reject: 20
- addressed_findings:
  - none
- deferred: `ContentPackager.RewriteManifest` (added by this story) does delete-then-write-in-place on `ZipArchiveMode.Update` — a throw between `GetEntry("manifest.json").Delete()` and the commit-on-Dispose leaves the shipped `.chimera.zip` with no `manifest.json`, permanently corrupting the creator's own local package (distinct from the iteration-0-rejected non-atomic *token* write, which is fail-soft; this manifest rewrite was introduced in iteration-0 and its atomicity was never triaged). Low: `JsonSerializer.Serialize`/small-buffer `Write` throwing is rare, and the failure is caught by the UI (upload refused) — but the local zip is left corrupt for every future read.
  - rejected (dropped, all re-raises already triaged on INTENT authority across the two prior passes): null-`Lockstep` offline-as-Player1 mis-mint (P1 deliberate repo-wide `?? Player1`, narrow non-current offline-non-Player1 config, trusted-friends EA non-anti-cheat scope); cosmetic `Id` rename orphans the token lookup (fails SAFE / refuses; identity-keyed re-proof defensible); screenshot count-not-content / phantom-manifest-entry validation (own-manifest forgery = anti-cheat, out of scope; floor is a ≥1 COUNT); `ComputeCurrentCanonicalHash` 0-on-error surfaced as "stale" AND 0-as-legal-hash (fail-CLOSED is safe, prior-pass-verified `Compute` never returns 0); empty-key + no-min-length HMAC (wiped-store forgery out of scope); FNV-32 disambiguator can still collide (astronomically unlikely for local scenarios; disambiguator was ADDED iteration-0); screenshot filename `Sanitize` vs collision `FileStem` (synchronous capture-then-pack per export, no cross-scenario coexistence); screenshot no-canonical-sort / `:D2`>99 (single verified capture fed per export; `:D2` is a MIN width); mint hashes live in-memory model vs serialized (`TokenHash_SurvivesRoundTripForPopulatedModel` proves round-trip identity); consent enforced by checkbox widget AND gate (belt-and-suspenders — gate DOES check `IpConsent`); `|`-delimiter injection via unsanitized `ScenarioId` in the HMAC payload (cross-token forgery = anti-cheat out of scope); `winnerSlot=-1` draw casts to `Faction.Neutral` (mints only if local faction == Neutral, which never occurs); UTF-16 code-unit description count vs graphemes (defensible "chars" reading — rejected both prior passes); untested Godot seams — mint hook / export token-load + screenshot capture / upload-handler gate wiring verified via re-implemented mirrors (the accepted presentation-out-of-pure-cores boundary the intent mandates; already tracked as ledger entries for sibling Stories 9.3/9.4/9.6/9.7; iteration-0 added the mint-flow mirror + round-trip coverage).

### 2026-07-24 — Follow-up review pass
- intent_gap: 0
- bad_spec: 0
- patch: 3: (high 0, medium 1, low 2)
- defer: 0
- reject: 13
- addressed_findings:
  - `[medium]` `[patch]` The staleness round-trip test (`TokenHash_SurvivesSerializeLoadRecompute`) round-tripped a bare `{Id, DisplayName, MapBounds}` model — NONE of the collections `CanonicalModelHash.Compute` folds (PlayerSlots/ResourceNodes/Buildings/Units/Regions/Triggers/TriggerGraph). A folded field that failed to survive save/load would make every genuine publish read "token stale" (whole happy path blocked) with the test still green. Added `TokenHash_SurvivesRoundTripForPopulatedModel_NotFalselyStale` exercising a model with ≥1 entry in each folded collection; pack→unpack→load→Compute hash identity holds (test passes — no serialization bug, gap now guarded).
  - `[low]` `[patch]` `ContentBrowserPanel` upload swallowed a `RewriteManifest` failure and uploaded anyway, so a rare rewrite IO error would ship a `.chimera.zip` recording `ip_consent:false` while uploading — contrary to the intent ("consent … written into the manifest and only then calling `UploadModAsync`"). Now fails CLOSED: a rewrite failure surfaces "could not record IP-ownership consent" and refuses the upload.
  - `[low]` `[patch]` The token store directory `"user://tokens"` was a raw string literal duplicated across the mint side (`ScenarioDelegateBinder`) and the export/load side (`WinConditionPhase`, plus the `/screenshots` subdir) — the exact silent mint/export divergence `ProofOfPlayMint` was extracted to prevent, but left un-hoisted. Hoisted to `ProofOfPlayMint.TokenDirGodotPath`; all three sites now derive from the single constant.
- rejected (dropped): screenshot black/editor-frame content validation (intent's quality floor is a ≥1 COUNT, not content — re-raise of a prior-pass reject); gate validates manifest strings vs actual zip entries (own-manifest forgery = anti-cheat, explicitly out of scope); renamed-but-identical map validates (same canonical model = same content = legitimately the same map; cross-scenario forgery out of scope); `ComputeCurrentCanonicalHash` returns 0 → misleading "stale"/0-as-legal-hash (prior-pass-verified `Compute` never returns 0 and fail-closed is safe); `GetOrProvisionSigningKey` conflates null-store with corrupt-key (log-only diagnostic, store always provisioned in bootstrap); empty-key HMAC has no min-length guard (wiped-store forgery = out-of-scope anti-cheat); team/N-player win doesn't mint for the ally (intent is explicitly single-slot "winner slot equals local faction"); screenshot filename uses `Sanitize` not the collision-disambiguated `FileStem` (screenshots are captured-then-packed synchronously per export — no cross-scenario coexistence, so no real collision); unbounded token/screenshot growth (tokens are the intended persistence; screenshots overwrite per-scenario — prior-pass reject); client-side-only advisory gate (server attestation is the 9.12 online rail, out of scope per intent's anti-cheat exclusion); null-`Lockstep` offline-as-Player2 mis-mint (prior-pass P1 deliberately chose the repo-wide `?? Player1` null-safe default; requires an offline non-Player1 config with null Lockstep, minimal harm in trusted-friends EA); cosmetic `Id` rename between win and export orphans the token lookup (identity-keyed persistence requiring re-proof on identity change is defensible; trivial replay workaround); skip-nonexistent-screenshot reindex path untested (defensive glue, verified correct; export controls the paths — low-value coverage nit).

### 2026-07-24 — Review pass (review_loop_iteration 0)
- intent_gap: 0
- bad_spec: 0
- patch: 9: (high 1, medium 4, low 4)
- defer: 0
- reject: 7
- addressed_findings:
  - `[high]` `[patch]` `ScenarioDelegateBinder` mint dereferenced `ctx.Lockstep` unguarded and outside the try (only unguarded site in the repo) — now `ctx.Lockstep?.EffectiveLocalFaction ?? Faction.Player1` inside the try; a null Lockstep at victory can no longer NRE the game-over path.
  - `[medium]` `[patch]` IP consent was stamped only on the in-memory manifest; the shipped `.chimera.zip` still recorded `ip_consent:false` — added `ContentPackager.RewriteManifest` (ZipArchive Update), called after the gate passes and before upload, so the distributed package records consent (Tier-1 test added).
  - `[medium]` `[patch]` A corrupt existing signing key was silently rotated, invalidating every prior token — provisioning now only generates when none exists, leaves a corrupt key untouched and skips the mint (extracted to `ProofOfPlayMint.GetOrProvisionSigningKey` with `SigningKeyStatus`; tests added).
  - `[medium]` `[patch]` The staleness serialize→load→recompute path (the gate's real dependency) was untested — added a pack→unpack→`LoadFromFile`→`Compute` round-trip test asserting hash identity (passed; no serialization bug).
  - `[medium]` `[patch]` The mint rule was only verified by a re-implemented arithmetic mirror and `ResolveScenarioId` was duplicated across mint/pack sides — extracted one Godot-free `ProofOfPlayMint` (`ShouldMint` + single `ResolveScenarioId`) used by both sides and tested directly.
  - `[low]` `[patch]` Description length used raw `.Length` (100 spaces passed) — now trims before the ≥100 check (whitespace-only fails).
  - `[low]` `[patch]` The `"win"` outcome literal was duplicated across mint and gate — hoisted to a single `PublishGate.WinOutcome` constant.
  - `[low]` `[patch]` `ProofOfPlayStore` sanitized ids could collide (`"My-Map"`/`"My Map"`) — filenames now append an FNV-1a of the raw id so distinct ids map to distinct files (test added).
  - `[low]` `[patch]` `EverythingMissing_ListsAllReasons` used `Assert.Contains` — tightened to an exact reason-set assertion.
- rejected (dropped): screenshot-content validation (beyond the intent's ≥1-count floor); hash-0 fail-closed sentinel (verified `CanonicalModelHash.Compute` never returns 0, so no collision); non-atomic token write (fail-soft already handles a corrupt file); client-side-only gate enforcement (server attestation is the 9.12 online rail, out of scope per intent); screenshot temp-file cruft (bounded per-scenario overwrite); misleading "stale" message on IO error (fail-closed is safe); UTF-16 vs glyph description count (defensible "chars" reading).

## Design Notes

**Renumber artifact.** The epics text references "token (from 9.7)"/"token (from 9.8)" — these are merge/renumber scars. Within THIS story the token is BOTH minted (self-victory) AND consumed (publish gate); treat it as one coherent artifact, no external dependency.

**Layering.** `ProofOfPlayToken` is a data-only POCO in `Definitions` so `ContentPackageManifest` can embed it without a `Definitions→UGC` reference. The crypto (`ProofOfPlaySigner`) and IO (`ProofOfPlayStore`, `PublishGate`) live in `src/UGC` beside `ModIoService`, which already uses `HttpClient`/`DateTime` — so no banned-API-analyzer concern and full Tier-1 testability (HMAC is deterministic). Store `scenario_hash` as a hex string, not a JSON number, to avoid any ulong-precision loss across mod.io/JSON interop.

**Signing example (Tier-1 core, ~8 lines):**
```csharp
// payload is canonical + order-fixed so Verify re-derives byte-identically
string payload = $"{token.ScenarioId}|{token.ScenarioHash}|{token.Outcome}|{token.MintedAt}";
using var h = new HMACSHA256(key);
string sig = Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(payload)));
// Verify: recompute sig from the token's fields, CryptographicOperations.FixedTimeEquals vs token.Signature
```

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` — expected: compiles clean; determinism analyzer green (new UGC cores are Godot-free, add no `float`/`System.Random`/tick-path `DateTime`); `DependencyHygieneTests` still see NakamaClient 3.13.0 as the sole dep.
- `dotnet test godot/ProjectChimera.Sim.Tests` — expected: all pass incl. new `UGC/*` tests (sign/verify/tamper/stale, store round-trip, every `PublishGate` pass/fail row, manifest+packager round-trip, mint flow); every pre-existing golden byte-identical; `CanonicalModelHash.AlgoVersion`(14)/`SimChecksum.AlgoVersion`(21)/`PROTOCOL_VERSION`(2) unchanged.
- `dotnet test godot/ProjectChimera.Sim.Tests --filter "FullyQualifiedName~Golden|SimChecksumCoverageGuard|VersionStampConsistency"` — expected: goldens unchanged (moved golden = Block-If, not a re-baseline).

**Manual checks (Godot-side / live-server — NOT Tier-1, documented for the live-verify pass):**
- In-engine: play a scenario as the local faction and win → confirm a token file appears under `user://tokens/`; lose or force another faction's win → confirm none is minted.
- Publish flow: with mod.io Game ID (`MainScene.ModIoGameId`) + `user://secrets/modio.key` configured, open the Content Browser Local tab, attempt upload with a missing field / unchecked consent → confirm refusal with the specific reason; then satisfy all fields + consent → confirm `UploadModAsync` runs end-to-end and the modId surfaces. Requires configured mod.io credentials + network; unverifiable unattended.


## Auto Run Result

Status: done (follow-up re-review of a committed `done` spec — `review_loop_iteration` 0)

**Change under review (already committed at `993674b`, baseline `ad71afd`):** proof-of-play token minted on a local self-win (HMAC-SHA256 over the canonical 64-bit `CanonicalModelHash.Compute`), persisted per scenario, and a single Godot-free `PublishGate` that refuses mod.io upload unless a valid+fresh token, min-quality fields (thumbnail, description ≥100, ≥1 screenshot), and explicit IP-ownership consent are all present — writing token/screenshots/consent into the `.chimera.zip` manifest before `ModIoService.UploadModAsync`.

**Files changed (this review pass):** none in `godot/` — no code patch or spec repair was triggered. Review-output artifacts only:
- `spec-9-8-…-mod-io.md` — new triage-log entry, Auto Run Result, `status`/`followup_review_recommended` frontmatter.
- `deferred-work.md` — one new defer entry appended.

**Review findings breakdown (4 blind layers: adversarial, edge-case, verification-gap, intent-alignment):**
- patches applied: 0
- deferred: 1 — `ContentPackager.RewriteManifest` non-atomic manifest rewrite can corrupt the local `.chimera.zip` on a mid-write throw (real, low-likelihood, not triaged by the two prior passes).
- rejected: 20 — all re-raises of findings already triaged (patched or rejected on intent authority) across this spec's two prior review passes, plus the accepted presentation-out-of-pure-cores Godot-seam testing boundary (already tracked as ledger entries for sibling Stories 9.3/9.4/9.6/9.7). Intent-alignment auditor found no substantive behavioral divergence from the intent.
- intent_gap: 0 · bad_spec: 0

**Follow-up review recommendation:** false. Patched findings this pass = 0 (score 0 < 5, no high-severity patch).

**Verification performed:** static re-inspection of the committed change against the intent contract and I/O matrix — read `PublishGate.Check`, `ProofOfPlayMint`, `ScenarioDelegateBinder.TryMintProofOfPlay`, `ContentPackager.RewriteManifest`, and `ContentBrowserPanel.ComputeCurrentCanonicalHash` to confirm each reviewer claim against real code. No code was modified, so no build/test run was warranted this pass (the committed change's own verification — Tier-1 build + `ProjectChimera.Sim.Tests` — was performed in the implementing passes).

**Residual risks:** (1) the deferred `RewriteManifest` atomicity gap — low likelihood, fails toward a corrupt local package; (2) the intent's explicitly-accepted boundary that the Godot presentation wiring (mint hook, export packaging, upload handler) is verified by inspection + re-implemented mirrors rather than in-engine tests — closure needs the live-verify pass documented in `## Verification` (requires configured mod.io credentials + network).
