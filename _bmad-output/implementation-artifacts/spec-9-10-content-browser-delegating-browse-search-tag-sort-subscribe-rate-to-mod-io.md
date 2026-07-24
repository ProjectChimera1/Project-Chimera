---
title: 'Content browser delegating browse/search/tag/sort/subscribe/rate to mod.io'
type: 'feature'
created: '2026-07-24'
status: 'done'
baseline_revision: '869eb4055448505cf4206e82f8309c9f85b99afd'
final_revision: '77ec7f2769c68a48b2b09867217e74dfbfa27004'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/project-context.md'
  - '{project-root}/_bmad-output/implementation-artifacts/epic-9-context.md'
  - '{project-root}/godot/src/UGC/ModIoService.cs'
  - '{project-root}/godot/src/UI/ContentBrowserPanel.cs'
  - '{project-root}/godot/src/Core/Bootstrap/Phases/ContentBrowserPhase.cs'
  - '{project-root}/godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj'
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** The content browser's Online tab (`ContentBrowserPanel`) can free-text search, download, subscribe, and rate, but three of the six FR-37 verbs are missing or half-delegated: there is **no tag-filter** and **no sort control** (browse is hardcoded to `_sort=-popular`), result cards show **no thumbnail**, and subscribe/rate are **hidden from not-logged-in users** (so they are never prompted to authenticate) rather than delegating that gate through the UI. mod.io already exposes all of this natively; the game just isn't surfacing it.

**Approach:** Extend the Godot-free `ModIoService` to pass mod.io-native `_sort` and `tags` query params on browse, to fetch the game's own tag options (`GET /games/{id}/tags`) and a mod's logo bytes, and to raise explicit subscribe/rate **success** events. Then wire `ContentBrowserPanel` to add a sort dropdown + tag-filter chips (both populated from mod.io, no local index), render each card's thumbnail + mod.io rating/download stats + author-ownership/profile, and always show subscribe/rate — prompting a logged-out user to authenticate instead of hiding the action. Download → integrity-verify already lands via Story 9.9 and must be preserved unchanged.

## Boundaries & Constraints

**Always:**
- Every discovery verb — browse, search, tag-filter, sort, subscribe, rate — delegates ENTIRELY to mod.io-native features through `ModIoService`. No parallel/local rating store, search index, or client-side re-sort/re-filter of the returned list: the sort key and tag set go into the mod.io request; tag options come from `GET /games/{id}/tags`; the rating shown is mod.io's own stats (`ratings_positive/negative`, and `ratings_display_text` when present), never a locally computed score.
- Sort and tag filtering re-issue the browse request with the current search text preserved, so the three compose (search + tag + sort in one mod.io query).
- Subscribe/rate are offered to all users. A not-logged-in user who clicks either is prompted to authenticate (the login panel opens + a status message) and the mod.io call is NOT made until logged in. A logged-in action calls `SubscribeAsync`/`RateAsync` and reflects the result in the UI on success (via new completion events), reverting/re-enabling on error.
- Each online result card shows name, author, thumbnail (mod.io `logo` thumb; a neutral placeholder while loading or if absent), tags, and mod.io rating + download stats; the author's profile/ownership is surfaced from the mod.io entry (clickable profile link + an ownership attribution line).
- The download button keeps Story 9.9's behavior: download via `ModIoService` → `ContentPackager.Unpack` integrity-verify before the package is playable; a mismatch/located error marks it not-playable.
- `ModIoService` stays Godot-free (pure C#, no `using Godot`); new pure query-building logic is Tier-1 tested in `ProjectChimera.Sim.Tests`. All Godot image decode + UI wiring is the presentation seam (verified by inspection + documented live-verify, per the accepted 9.3–9.9 boundary). NakamaClient stays the sole NuGet dependency.

**Block If:**
- Delivering this requires bumping `SimChecksum.AlgoVersion`, `CanonicalModelHash.AlgoVersion`, or `PROTOCOL_VERSION`, or moving a committed golden — this is a UGC/presentation feature strictly outside the sim/wire, so any such need signals a mis-scoped change.

**Never:**
- Never build a local rating, search, favorites, or tag index, or sort/filter the mod list client-side — mod.io is the single source of truth for discovery.
- Never fold anything here into `SimChecksum`/`CanonicalModelHash`; never touch a golden or an algo/protocol version. `ModIoService` is off-tick presentation-adjacent code (outside `SimSources.props` and the determinism analyzer set) — do not move it into the sim gate.
- Never change the Story 9.9 download-integrity semantics; extend the card, don't replace the verify path.
- Never block browse on authentication (browse/search/tag/sort stay available logged-out); only subscribe/rate require login.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Build browse URL, plain | gameId, apiKey, limit/offset, no search/tags, default sort | URL has `_limit`/`_offset`/`_sort=-popular`, no `_q`, no `tags` | n/a |
| Build browse URL, full | search="desert", tags=["1v1","Melee"], sort="-downloads" | URL has `_sort=-downloads`, `_q=desert` (escaped), one `tags=` per tag (escaped) | n/a |
| Sort changed | user picks "Newest" | browse re-issued with that sort token + current search + tags | mod.io error → OnError status line, list unchanged |
| Tag filter toggled | user checks a mod.io tag chip | browse re-issued with `tags=<name>` (ANDed with others) + current search + sort | n/a |
| Tag options fetch | Online tab opened / first browse | `GET /games/{id}/tags` → flat tag-name list → chips rendered | fetch fails → no chips (browse/sort still work), logged |
| Thumbnail present | mod `logo.thumb_320x180` set | bytes fetched async → decoded → shown on the card | decode/http fail → placeholder kept, no crash |
| Thumbnail absent | mod has no logo | neutral placeholder shown | n/a |
| Rating display | mod stats present | shows downloads + mod.io ratings (display text when present, else +N/−N) | missing stats → omitted, no crash |
| Subscribe, logged out | not logged in, click Subscribe | login panel opens + "Log in to subscribe" prompt; no API call | n/a |
| Subscribe, logged in | logged in, click Subscribe | `SubscribeAsync` sent; on success button → "Subscribed ✓" | OnError → re-enabled + message |
| Rate, logged in | logged in, click +/− | `RateAsync` sent; on success the pair reflects the choice | OnError → re-enabled + message |
| Download | any user, click Download | download via `ModIoService` then Story-9.9 `Unpack` integrity-verify before playable | mismatch → "Corrupt ✗" not-playable (9.9) |

</intent-contract>

## Code Map

- `godot/src/UGC/ModIoService.cs` — (Godot-free) add `ModIoLogo` (`filename`, `thumb_320x180`, …) to `ModIoMod`; add mod.io-native rating fields to `ModIoStats` (`ratings_percentage_positive`, `ratings_display_text`). Extract a pure static `BuildModsUrl(baseUrl, gameId, apiKey, limit, offset, searchQuery, sort, tags)` and route `BrowseModsAsync` through it with new `sort`/`tags` params (default sort keeps `-popular`). Add `GetGameTagsAsync()` (`GET /games/{id}/tags` → `OnTagOptionsReady(List<string>)`), `DownloadThumbnailAsync(int modId, string url)` (→ `OnThumbnailReady(int, byte[])`, raw bytes), and success events `OnSubscribeComplete(int)` / `OnRateComplete(int,bool)` fired on 2xx from `SubscribeAsync`/`RateAsync`.
- `godot/src/UI/ContentBrowserPanel.cs` — online tab: add a sort `OptionButton` (mod.io-native tokens, tunable) and a tag-filter chip row (populated from `OnTagOptionsReady`), both re-issuing `BrowseOnline()` with the composed sort+search+tags. `BuildOnlineCard`: add a thumbnail `TextureRect` (request via `DownloadThumbnailAsync`, decode in `OnThumbnailReady`, placeholder default); show mod.io rating (`RatingsDisplayText` when set) + downloads; add an author ownership/attribution line beside the existing profile `LinkButton`. Always render Subscribe + Rate; on logged-out click, open the login panel + prompt instead of calling the API; reflect `OnSubscribeComplete`/`OnRateComplete` and revert on `OnError`. Keep `OnDownloadComplete` (9.9 verify) intact.
- `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` — add `<Compile Include="..\src\UGC\ModIoService.cs" LinkBase="Sim\UGC" />` so `BuildModsUrl` is Tier-1 testable (mirrors the existing UGC single-file includes; keeps it out of `SimSources.props`/the analyzer set).
- `godot/ProjectChimera.Sim.Tests/Definitions/ModIoServiceUrlTests.cs` — **NEW** Tier-1 tests over `BuildModsUrl` covering the I/O-Matrix URL rows.

## Tasks & Acceptance

**Execution:**
- `godot/src/UGC/ModIoService.cs` — add logo + mod.io rating fields; extract `BuildModsUrl` and thread `sort`/`tags` through `BrowseModsAsync`; add `GetGameTagsAsync`, `DownloadThumbnailAsync`, and subscribe/rate success events.
- `godot/src/UI/ContentBrowserPanel.cs` — add sort dropdown + mod.io tag-filter chips; render thumbnail + mod.io rating/download + author ownership/profile per card; always-show subscribe/rate with logged-out auth prompt and success/error reflection; preserve the 9.9 download-verify path.
- `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` — include `ModIoService.cs` as a single Tier-1 compile.
- `godot/ProjectChimera.Sim.Tests/Definitions/ModIoServiceUrlTests.cs` (NEW) — assert every `BuildModsUrl` I/O-Matrix row (default sort, sort override, escaped `_q`, one `tags=` per tag, omit when empty).

**Acceptance Criteria:**
- Given the Online tab, when I set a sort option and/or toggle a mod.io tag chip and/or type a search, then the browse request is re-issued to mod.io with the composed `_sort` + `tags` + `_q`, and the displayed list is exactly mod.io's response (no client-side re-sort/re-filter, no local index).
- Given a browse result, when a card renders, then it shows name, author, a thumbnail (mod.io logo, or a placeholder), tags, and mod.io rating + download stats, and the author's profile/ownership is surfaced from the mod.io entry.
- Given a not-logged-in user, when they click Subscribe or Rate, then they are prompted to authenticate (login panel opens) and no mod.io write is sent; given a logged-in user, the action calls `SubscribeAsync`/`RateAsync` and the UI reflects success (and reverts on error).
- Given a subscribed/selected package, when I download it, then it downloads via `ModIoService` and is integrity-verified per Story 9.9 before becoming playable.
- Given the full suite, when it runs, then `dotnet build` is clean, the new `ModIoServiceUrlTests` pass, every committed golden is byte-identical, and `SimChecksum.AlgoVersion`/`CanonicalModelHash.AlgoVersion`/`PROTOCOL_VERSION` are unchanged (a moved golden = Block-If).

## Spec Change Log

## Review Triage Log

### 2026-07-24 — Follow-up review pass (review_loop_iteration 0)
- intent_gap: 0
- bad_spec: 0
- patch: 2: (high 0, medium 1, low 1)
- defer: 0
- reject: 10
- addressed_findings:
  - `[medium]` `[patch]` The tag-flattening logic in `GetGameTagsAsync` — pure, Godot-free response-parsing carrying an explicit correctness claim (the `"tags":null` malformed-group guard added in the prior pass, plus blank-tag skipping) — was the one discovery-verb seam left unextracted and untested while its sibling `BuildModsUrl` was Tier-1 tested. A future removal of the null-guard would drop the whole chip row with every test still green. Extracted a pure static `ModIoService.FlattenTagNames(IReadOnlyList<ModIoTagOption>?)`, routed `GetGameTagsAsync` through it, and added 4 Tier-1 cases (null groups, in-order flatten, malformed-group-doesn't-drop-all, blank/whitespace skip). Pins the "no local tag index" contract that intent's "pure logic Tier-1 tested" clause requires.
  - `[low]` `[patch]` `OnTagOptionsReady` rebuilt the chip row but never reconciled `_selectedTags`, so a tag re-fetch (after a transient `tags` error resets `_tagsFetched`) whose option set no longer includes a previously-selected tag left that tag in `_selectedTags` — still emitted as `tags=` on every browse — with no chip left to clear it (an invisible filter narrowing results to empty). Added `_selectedTags.IntersectWith(names)` before the rebuild so an orphaned selection can never persist.
- rejected (dropped): no request-generation/sequence token on fire-and-forget browse (out-of-order stale results) — **already captured as a deferred-work ledger entry in the prior pass; not re-deferred to avoid a duplicate**; subscribe/rate success state not surviving a re-browse (`ClearOnlineList` wipes it, re-exposing an actionable button) — mod.io subscribe/rate are idempotent so a re-click is a harmless no-op success, and the card genuinely rebuilt; per-card thumbnail re-download / no cache (already rejected prior pass, EA-acceptable perf); login-gated click not auto-resumed after auth (already rejected prior pass; AC2 requires only the prompt); decoder chosen by URL suffix (already rejected prior pass; two-try fallback + placeholder degrade gracefully); subscribe/rate `OnError` revert-all across concurrent same-verb cards (already rejected prior pass; error event carries no modId, revert-all is the reasonable optimistic-UI tradeoff, no data impact); `mod.SubmittedBy.Username` unguarded deref — not caused by this story (pre-existing code at ContentBrowserPanel.cs:813/815 already derefs it unguarded; `SubmittedBy` is `= new()` default-initialized); `api_key` not `Uri.EscapeDataString`'d — fixed configured alphanumeric key, no realistic trigger; QueueFree-vs-AddChild same-frame duplicate chips — one-frame cosmetic, only on refetch; duplicate mod ids overwriting per-card dicts — mod.io ids are unique (invariant holds); sort options hardcoded client-side rather than mod.io-sourced — blessed by the spec Design Notes (mod.io exposes no sort-fields endpoint; the `_sort` operation is still fully delegated).

### 2026-07-24 — Review pass (review_loop_iteration 0)
- intent_gap: 0
- bad_spec: 0
- patch: 4: (high 0, medium 2, low 2)
- defer: 1
- reject: 8
- addressed_findings:
  - `[medium]` `[patch]` `_tagsFetched` was latched `true` BEFORE `GetGameTagsAsync()` resolved and never reset, so a transient first-fetch failure (offline/5xx/rate-limit) permanently emptied the tag-filter chip row for the whole session. Now `OnError`'s `op=="tags"` branch resets `_tagsFetched=false` so the next browse retries.
  - `[medium]` `[patch]` `OnError` overwrote the shared browse status label for BACKGROUND per-card ops — a single common missing/broken mod logo replaced "N maps found" with "Error (thumbnail): HTTP 404", flapping across a page of cards. `thumbnail`/`tags` errors now log-only and return early before the status-label write; user-initiated ops still surface (and the auth/subscribe/rate re-enable logic is unaffected).
  - `[low]` `[patch]` The new ownership label asserted "Content © {username}" (an unverified copyright claim on every mod, incl. reuploads) and rendered a dangling "© " on a blank username. Reworded to mod.io's actual model — "IP retained by {username} · hosted & distributed via mod.io" (mirroring the Story 9.8 IP-consent framing), with a blank-name fallback of "Hosted & distributed via mod.io".
  - `[low]` `[patch]` `GetGameTagsAsync` iterated `group.Tags` unguarded, so one malformed group (`"tags":null`) threw and dropped ALL tags (caught → whole fetch failed). Added `if (group?.Tags == null) continue;`.
- deferred (appended to `deferred-work.md` as a NEW entry):
  - `[low]` Browse requests are fire-and-forget `Task.Run` with no request-generation/sequence token; rapid sort-change / tag-toggle / search can complete out of order and leave the panel showing a stale mod set under newer controls. Pre-existing async-browse shape (search already re-issued this way), amplified by the new sort/tag controls.
- rejected (dropped): subscribe/rate `OnError` reverts all in-flight cards of that verb rather than only the failed one (the error event carries no modId; leaving a failed action un-reverted would instead stick it at "Subscribing…", so revert-all is the reasonable optimistic-UI tradeoff — successful concurrent actions self-correct via their own completion events; no data impact, narrow concurrent-same-verb window); login-gated click not auto-resumed after auth (AC2 requires only the authenticate PROMPT, which is delivered; auto-resume is beyond intent); prior subscription/rating state not pre-fetched per card (beyond the ACs; needs extra authenticated `/me` calls); thumbnail decoder chosen by URL `.png` suffix (the jpg/png two-try fallback + neutral placeholder degrade gracefully; webp logos are rare and fall to placeholder, never crash); no thumbnail cache / re-download per browse (EA-acceptable perf, same class as 9.9's rejected re-decode-per-reload); `RatingsPercentagePositive` deserialized-but-unused (harmless; the `RatingsDisplayText`→`+N/-N` fallback already satisfies "shows mod.io rating"); new events never unsubscribed (the `ModIoService` is referenced only by the panel, so service lifetime == panel lifetime — the "service outlives panel" precondition is false; also the pre-existing subscribe pattern); intent-alignment "subscribe→download coupling" divergence (epic AC3 reads "when I trigger download", selecting the decoupled reading — the download→Story-9.9-verify path is exactly what is preserved); intent-alignment "test surface" observation (the Godot decode/UI/live-mod.io behavior is the accepted inspection + live-verify presentation seam, sibling 9.3–9.9 boundary — descriptive, no action).

## Design Notes

**Why `BuildModsUrl` is the testable seam.** The only genuinely verifiable-without-a-live-server logic is that the six verbs become mod.io query params, not a local index. Extracting URL construction into a pure static and Tier-1 testing it is the concrete proof of the "delegate ENTIRELY to mod.io" constraint; everything else (image decode, chip layout, button state) is the accepted presentation seam.

**Sort tokens are mod.io-native and tunable.** Default stays `-popular` (already shipped/known-good). The dropdown offers a small curated set of mod.io sort keys — e.g. Popular `-popular`, Most Downloaded `-downloads`, Newest `-date_live`, Name A–Z `name` — declared as a data list with a comment that these are mod.io-native tokens a later story can adjust. An unexpected token must degrade to a surfaced `OnError`, never crash the tab.

**Tag filter comes from mod.io, ANDed.** Chips are built from `GET /games/{id}/tags` (fetched once when the tab first browses), so there is no hardcoded tag list. Multiple selected chips emit multiple `tags=` params (mod.io AND semantics). If the fetch fails, the tab still browses/sorts — the chip row is simply empty.

**Thumbnail flow (Godot-side, presentation seam).** `ModIoService.DownloadThumbnailAsync` returns raw bytes on `OnThumbnailReady(modId, bytes)`; the panel decodes with `Image.LoadJpgFromBuffer`/`LoadPngFromBuffer` (chosen by URL extension, the other tried on failure) → `ImageTexture` → the card's `TextureRect`. Any failure leaves the neutral placeholder; never throws.

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` — expected: compiles clean; determinism analyzer green (`ModIoService` stays Godot-free and outside `SimSources.props`); `DependencyHygieneTests` still see NakamaClient 3.13.0 as the sole dep.
- `dotnet test godot/ProjectChimera.Sim.Tests` — expected: all pass incl. new `ModIoServiceUrlTests`; every pre-existing golden byte-identical; `SimChecksum.AlgoVersion`/`CanonicalModelHash.AlgoVersion`/`PROTOCOL_VERSION` unchanged.
- `dotnet test godot/ProjectChimera.Sim.Tests --filter "FullyQualifiedName~Golden|SimChecksumCoverageGuard|VersionStampConsistency"` — expected: goldens unchanged (moved golden = Block-If).

**Manual checks (Godot-side / live-verify — NOT Tier-1, documented for the live-verify pass):**
- With mod.io Game ID + key configured: open the Online tab → confirm sort dropdown + tag chips populated from mod.io; change sort / toggle a tag / search → list re-fetches from mod.io and matches its ordering; cards show thumbnail + author profile + mod.io rating/downloads.
- Logged out: click Subscribe/Rate → login panel opens with a prompt, no write sent. Logged in: Subscribe → "Subscribed ✓", Rate → reflects the choice; a forced error re-enables the button with a message.
- Download a package → confirms Story 9.9 integrity-verify still runs before it is playable.

## Auto Run Result

Status: done (follow-up review pass on an already-`done`/committed story)

**Summary:** Fresh follow-up review of Story 9.10 (already implemented + committed at `48a0903`). Four review layers (adversarial, edge-case, verification-gap, intent-alignment) ran in parallel. Two findings were auto-patched; the rest were rejected (most as reasonable optimistic-UI tradeoffs already adjudicated in the prior pass, or as invariants/not-caused-by-this-story). The out-of-order-browse finding was recognized as already living in the deferred-work ledger and was **not** re-deferred (no duplicate entry created).

**Files changed this pass:**
- `godot/src/UGC/ModIoService.cs` — extracted pure static `FlattenTagNames(IReadOnlyList<ModIoTagOption>?)` and routed `GetGameTagsAsync` through it (behavior identical; now Tier-1 testable).
- `godot/ProjectChimera.Sim.Tests/Definitions/ModIoServiceUrlTests.cs` — added 4 `FlattenTagNames` Tier-1 tests (null groups, in-order flatten, malformed-group-doesn't-drop-all, blank/whitespace skip).
- `godot/src/UI/ContentBrowserPanel.cs` — added `_selectedTags.IntersectWith(names)` in `OnTagOptionsReady` to prune an orphaned tag selection that would otherwise filter browse invisibly.

**Review findings breakdown:** patches applied 2 (1 medium, 1 low); deferred 0 (1 candidate already in the ledger, not duplicated); rejected 10.

**Follow-up review recommendation:** `false` — patched severities: 0 high, 1 medium, 1 low; score `3×1 + 1×1 = 4` (< 5), no high.

**Verification:**
- `dotnet build godot/godot.csproj` → Build succeeded, 0 errors (13 pre-existing warnings).
- `dotnet test … --filter ModIoServiceUrlTests` → 12/12 passed (8 URL + 4 new flatten).
- `dotnet test … --filter Golden|SimChecksumCoverageGuard|VersionStampConsistency` → 202/202 passed; no golden moved; `SimChecksum.AlgoVersion`/`CanonicalModelHash.AlgoVersion`/`PROTOCOL_VERSION` unchanged (Block-If clear).

**Residual risks:** Out-of-order browse responses under rapid sort/tag/search interaction remain (pre-existing async shape, already in the deferred-work ledger). All Godot-side decode/UI/live-mod.io behavior remains the accepted inspection + live-verify presentation seam (per the 9.3–9.9 boundary), unchanged by this pass. `sprint-status.yaml` was already modified in the working tree before this run and is left untouched (residual artifact, not part of this change).
