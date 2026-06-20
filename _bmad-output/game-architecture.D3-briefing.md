---
brief: D3 — Data-Driven Definition Schema & Loader
trio: D1 (Bounded Effect-Graph) → D2 (Typed Event/Dataflow Graph) → D3 (Schema & Loader)  [LAST]
role: serializes D1 + D2 hand-offs; resolves the data-driven pillar debt
status: DECIDED 2026-06-20 — Option B (Maximal-now). Alec pulled ALL FOUR defer-items forward (source-gen now · full migration registry now · replay-v2 in lockstep · AOT analyzer as CI gate). Canonical record — game-architecture.md → Architectural Decisions (Step 4) → D3.
workflow: gds-game-architecture Step 4 decision D3
generated: ultracode D3 deep-dive, agents, adversarially verified
decided_inputs: D1 ✅  D2 ✅
decides_here: poly-(de)serialization · reflection-vs-source-gen · loader topology · hash domain · versioning/compat · 7 concrete schema shapes
engine: Godot 4.6.3 (.NET) · C# / .NET 8
sim_constraints: Fixed 16.16 · 30 Hz · ascending-ID · single SimRng · 4096 cap · closed typed nodes · no scripting escape hatch · fixed-at-load
author: architecture facilitator
date: 2026-06-20
resume_anchor: _bmad-output/game-architecture.RESUME.md (Step 4/9, D1✅ D2✅ → D3)
---


# D3 — Data-Driven Definition Schema & Loader: Decision Briefing

*D1 decided what the runtime graph IS. D2 decided what flows through it. D3 decides how all of that becomes bytes on disk, how those bytes are validated before a single tick runs, and how a hash over them stays meaningful across machines and across four authoring tiers — without ever letting a float, a Godot type, or an unknown node into the 30 Hz sim. This is the last decision before the architecture pass closes; it is also the one with the most cross-cutting wire-format blast radius.*

## Changes from draft after adversarial review

**Seven corrections changed the briefing's substance; four critic over-reaches were held.**

1. **The determinism boundary moved from "load-time" to "load-time AND in-tick."** The draft scoped float/culture nondeterminism to the load boundary (FixedConverter) and Dictionary ordering. A critic drew blood on a live in-tick leak: `ScenarioDirector.cs:168/170` does `Ore[...].ToFloat().ToString("F2")` to build a `resource_threshold` event payload, then `:252` does `float.TryParse(f.Data, …)` to read it back — a Fixed→float→culture-string→float-parse round trip **every tick**, gating which triggers fire (verified live). A canonical-model hash agrees at load and the peers still desync at tick N. D3 now owns an in-tick determinism invariant (new A17, rides D3.4). ⚑

2. **The load-gate now runs on every path — including replay and in-memory T4.** The draft's "the loader is the only path to a tick" was false twice: (a) the replay path re-loads the scenario by **path string** and never re-hashes/re-validates (`ReplayPlayer`/`ReplayRecorder`), and (b) the AI-generated and fallback scenarios reach `ApplyScenario` as in-memory `ScenarioData` that never touches the file-parse choke point. The validator is now defined as a function **over the model** invoked at the `ApplyScenario` boundary, and replay-v2 embeds the canonical hash + algo-version and re-gates on playback. ⚑

3. **`min_game_version` enforcement is promoted from "surfaced prerequisite" to a first-class D3.1 deliverable.** Verified: the field is only ever **written** (`ContentPackageManifest.cs:65`, `ContentPackager.cs:44,85`) and **never read** anywhere. The draft leaned the entire strict-region forward-compat story on it as if it were a built gate. It is an unbuilt subsystem (a `CurrentGameVersion` constant + InvariantCulture semver compare + load-gate check + auto-stamp from a per-registry-entry `introduced_in`). Without auto-stamp, creator content is permanently mis-versioned. ⚑

4. **The unknown-property sink is now physically separated from the hash-excluded region.** The draft let `[JsonExtensionData]` catch *any* unmapped property and route it to the (excluded) tolerant region — meaning a gameplay field one level too high, or a future gameplay field an old loader doesn't know, lands in the excluded bag, is dropped by the tick, and two functionally-different scenarios hash identically. The strict gameplay region now uses `Disallow`+throw (no extension bag); only the explicit `_editor`/`_ext` namespace gets verbatim-preserve, with a registry-field denylist check. ⚑

5. **The package byte-hash is decoupled from re-save, and faction/cross-file content is brought under the content hash.** Verified: `Unpack` throws `InvalidDataException` on byte-hash mismatch (`ContentPackager.cs:175-182`), and `ExportMapPackage` re-emits via `SaveToFile` *then* hashes. Unifying options (D3.0) necessarily changes faction-file bytes (`FactionDefinition` has `WriteIndented` off; `ScenarioSerializer` on), so any touched-and-resaved package would fail its own integrity check. The byte-hash is now frozen as algo-1 (a literal-zip tamper check, hashed pre-save), the canonical-model hash is algo-2 (the handshake/equality channel), and the content hash covers **all** gameplay files (faction DTOs, named-effect catalog, N-resource registry), not just `scenario.json`. ⚑

6. **The 0-hash fail-open and the protocol bump are now hard load-gate invariants, correctly located.** Treat hash 0 as "not computed ⇒ block" (remap a legitimate-0 canonical hash to 1, FNV-sentinel trick). The protocol bump belongs in the **Hello** handshake (`PROTOCOL_VERSION`, which today is exchanged but never *checked*) — the `Ready` reader is already len-tolerant, so the real failure mode is a new host requiring a `rulesetHash` an old peer never sends. ⚑

7. **The `rulesetHash` corpus is now closed, and several "bigger than admitted" steps are split out.** The caps corpus is pinned to *every constant the tick reads* (spawn cap, 4096 entity cap, 30 Hz, resource-slot count, damage-table dims incl. Hero), the 50→64 spawn-cap reconciliation is a **hard prerequisite** of `rulesetHash` (not parallel), the unstable `Array.Sort` over triggers becomes a stable total order (Priority desc, node-id tiebreak), and `scenarioHash`/`SimChecksum` are stated to both cover the **full** model/state rather than mirroring each other's current narrow coverage. ⚑

**Held against over-reach:** (a) custom `JsonConverter` over `[JsonPolymorphic]` is forced by R1 (#100057 genuinely open), not preference — kept. (b) Canonical-model hash over `Fixed.Raw` is the only way to honor D2's annotation-exclusion mandate; the cliff/budget gap is a *fix added*, not a reason to revert to byte-hashing — kept, bounded. (c) Enum-as-stable-**name** (not ordinal) in the hash is the correct determinism call (ordinal drifts when Hero inserts before COUNT) — kept. (d) FNV-64 (not SHA-256) is defensible for lockstep-equality (non-adversarial); kept.

**Grounding status — files verified live this session (claims below are consistent with current source):**

- `godot/src/Core/Definitions/ScenarioSerializer.cs` — verified: static class; options `{ ReadCommentHandling=Skip, AllowTrailingCommas=true, WriteIndented=true, Converters={JsonStringEnumConverter} }` (23–29); `LoadFromFile` returns `null` on missing/parse-fail with **zero validation** (35–40); `ComputeFileHash` = FNV-1a **32-bit over RAW FILE BYTES**, chunked 4096B, returns `0u` if missing (59–80).
- `godot/src/Core/Definitions/FactionDefinition.cs` — verified: **separate** static `LoadFromFile`, **different** options `{ Skip, AllowTrailingCommas }` — **no `JsonStringEnumConverter`, no `WriteIndented`** (66–82); holds `List<UnitDefinition> Units/Buildings`; no `SaveToFile`.
- `godot/src/Combat/DamageMatrix.cs` — verified: **hardcoded 4×5 `float[,]`** (38–45), `Fixed.FromFloat` in static ctor (49–57), `DamageType{Normal,Pierce,Siege,Magic,COUNT=4}`, `ArmorType{Unarmored,Light,Medium,Heavy,Fortified,COUNT=5}`; array sizing reads `COUNT` (51–53).
- `godot/src/Core/Definitions/ScenarioData.cs` — verified: flat arrays; `MapBounds` float default `120f`; `WinCondition` enum; **NO `schema_version` field**.
- `godot/src/Core/FixedPoint.cs` — verified: `Raw` is the only field (`int`, ONE=65536); `FromFloat` is **unguarded** `new Fixed((int)(value * ONE))` (27) — no NaN/Inf reject anywhere.
- `godot/src/Core/ScenarioDirector.cs` — verified live this pass: **in-tick float/culture round trip** — `:168` `Ore[...].ToFloat()`, `:170` `…ToString("F2")` into a `FiredEvent`, `:252` `float.TryParse(f.Data, …)` reading it back to gate `EvaluateTriggers`; `Dictionary<string,int>` timers/vars; unstable `Array.Sort` by Priority; `Math.Min(count,50)` spawn clamp.
- `godot/src/Core/Definitions/ContentPackageManifest.cs` / `ContentPackager.cs` — verified live this pass: `min_game_version` **written only** (`Manifest:65`, `Packager:44,85`), **never read/compared** anywhere in the load path; `Pack` hashes `scenario.json` only; `Unpack` re-hashes extracted bytes and **throws `InvalidDataException`** on mismatch.
- `godot/src/Multiplayer/ReplayRecorder.cs` — verified: `VERSION = 1`; magic `CHMR`; fixed-width per-order layout; header = magic+version+pathLen+**path string** (no scenarioHash).
- Cross-referenced (consistent with above): `ReplayPlayer.cs` (re-loads scenario by stored path; no re-hash/re-validate), `MainScene.cs` (scattered per-domain load topology; hash at :303 over file at `ScenarioPath`; AI-gen path applies `_pendingGeneratedScenario` not written to `ScenarioPath` before the hash; fallback path), `NetworkCommand.cs` (`PROTOCOL_VERSION=1` in Hello, never compared; `MakeReady(scenarioHash)`; `TryReadReady` `len>=5` tolerant), `SimChecksum.cs` (hashes only EntityWorld pos/health, building state, Player1/Player2 Ore — not Crystal, not slots 3+, not vars/timers).

---

## 1. D3 in one paragraph

The simulation already knows how to *run* the D1 effect-graph and the D2 event/dataflow graph; what it does **not** have is a single, trustworthy way to turn those graphs into a file, read that file back **identically on every machine**, prove the file is safe **before** the first tick, and compute a hash that means *"these two scenarios will play the same"* rather than *"these two files have the same bytes."* The core tension is that the as-built serialization layer is the opposite of all four: it is **scattered** (5–7 independently-constructed `JsonSerializerOptions` with **three different behaviors**), **unvalidated** (`LoadScenario` trusts everything; `LoadFromFile` returns bare `null`), and **byte-fragile** (the scenarioHash is FNV over raw file bytes, so whitespace, key order, a comment, a `1.0`-vs-`1`, or a moved editor node flips it and falsely rejects). ⚑ **And the non-determinism is not only on disk:** a Fixed→float→`"F2"`-string→`float.TryParse` round trip lives *inside* the 30 Hz tick (`ScenarioDirector.cs:168/170/252`), so even a perfect on-disk hash can agree while the two peers fire different triggers. D3 must replace all of that with a closed, fail-closed, deterministic schema-and-loader: a polymorphic-but-sealed node IR, a canonical hash over the *parsed model in Fixed.Raw integers* (annotations excluded), an authoritative gate that is the **only** path to a tick **on every path including replay and in-memory T4**, an in-tick encoding free of float/culture, and a versioning policy that lets today's unversioned files load tomorrow without spuriously desyncing. The genuine debate is not *whether* — D1/D2 mandate it — but *how far* to build on the 1.0 critical path for a solo dev.

---

## 2. What D3 must decide — the coupled decision axes

**Coupled mechanism cluster** — these constrain each other:

| Axis | The choice | Coupling |
|---|---|---|
| **M1 Polymorphic (de)serialization** | Built-in `[JsonPolymorphic]`/`[JsonDerivedType]` vs **custom `JsonConverter<TBase>` + closed registry** | Couples to M2 and versioning. Custom converter is the only way to combine polymorphism with strict unknown-rejection (§4 R1). |
| **M2 Reflection vs source-gen** | Reflection (status quo) vs source-gen (AOT-ready) vs **hybrid** | Couples to the open NativeAOT-server question and M1 (source-gen + custom converter = metadata mode). |
| **M3 Loader topology** | Per-domain scattered static loaders vs **single `ContentLoader` choke point** | Couples to M4 — no gate without a choke point. ⚑ Plus a model-level gate at `ApplyScenario` for in-memory scenarios. |
| **M4 Load-gate seam** | Where the type-check + graph-lint + cap/cost validation lives | Couples to M3 and to the server/replay/in-memory load paths. |
| **M5 Hash canonicalization** | Byte-hash (hardened) vs **canonical-model hash over Fixed.Raw, annotations excluded** | Couples to the annotation channel (S4) and versioning (algo-version). |
| **M6 Versioning / forward-compat** | Integer `schema_version` + migration registry on `JsonNode` DOM; strict gameplay / tolerant annotation split | Couples to M5, M4, legacy-amnesty, **and to enforced `min_game_version`** (the strict region's only safety valve). ⚑ |

**Schema-shape calls** — independent once M1–M6 are fixed:

- **S1 `damage_table.json`** — lift the hardcoded 4×5 matrix; **+ `Hero` DamageType (5th) + `Hero` ArmorType (6th)** → 5×6. Named-key map vs positional array.
- **S2 N-resources** — generalize Ore+Crystal to a resource-type registry; cost as sparse `{resourceId: amount}` map.
- **S3 Tech-tree** — replace hardcoded `TechTreeChecker` switches with a data-driven id registry; separate section vs inline `prerequisites[]`.
- **S4 Annotation channel** — where T3 positions / T1 preset origin / T4 prompt provenance live, round-tripped but hash-excluded.
- **S5 Named-effect catalog** — top-level id-keyed `EffectDef` map for `NamedEffectReference` (by-id, never inlined).
- **S6 Replay-v2** — `DslEventCommand` binary record in `.chmr`; bump `VERSION` 1→2; **embed canonical scenarioHash + algo-version in the header**. ⚑

---

## 3. Requirements / artifact checklist

| # | Artifact | Source | As-built it replaces | Shape decision it forces |
|---|---|---|---|---|
| A1 | **`EffectDef`/`ModifierDef` DTO family** + string discriminator | D1 | `TriggerAction` fat-nullable flat ECA | Discriminator name (`kind`); custom converter; Modifier inline vs catalog |
| A2 | **Graph IR** — id-keyed node list + two typed sparse edge-lists (exec, data) + persistent ids | D2 | `TriggerDefinition[]`; string-switch `ExecuteActions` | Node-id type + deterministic allocator; ports by-name; subgraph inline vs by-id |
| A3 | **Variable schema** — name/type/scope/initial; closed types incl. `Record` | D2 | `Dictionary<string,int>` (untyped, order-nondeterministic) | Fixed `initial` raw-int encoding; scope enum {Global, PerPlayer 0..7, TriggerLocal} |
| A4 | **Custom-event registry** — names + typed params + per-event allowed-raiser set | D2 | Hardcoded `TriggerEvent` enum-string set | Param-type reuse from A3; deterministic registry ordering |
| A5 | **UI-definition schema** — closed widget tree + Bind/Format/layout | D2 | Net new | Widget discriminator; nested-tree vs flat-parentId; bind-resolution at load |
| A6 | **Annotation channel** — T3 pos / T1 preset / T4 provenance | D2 | Net new | Sidecar-by-node-id; in-file vs separate file; independent `_editor` version |
| A7 | **Canonical serialization + content-hash spec** | D2 + Arch decision 11 | `ComputeFileHash` (byte-FNV) | Field order; Fixed.Raw int; 32 vs 64-bit; annotation exclusion; **covers ALL gameplay files, not just scenario.json** ⚑ |
| A8 | **`damage_table.json`** — 5×6 + `Hero`×`Hero` | D1 | `DamageMatrix._table` 4×5 float[,] | Named-key map vs positional; default-1.0; Hero **before** COUNT |
| A9 | **N-resource registry** + per-faction balance + sparse cost maps | D2-adjacent | `ResourceStore` Ore+Crystal; `cost_ore/crystal` | Inline vs shared catalog; sparse map; PerPlayer 0..7 vs FACTION_COUNT=5 |
| A10 | **Authorable tech-tree** — data-driven id registry | D1/D2 | `TechTreeChecker` 3 switches; `BuildingType` enum | Separate section vs inline `prerequisites[]` |
| A11 | **Named-effect catalog** — id-keyed `EffectDef` subgraph map | D1-stretch | Net new (inline-only) | Catalog id namespace; cycle-lint at load; **referential-integrity across files** ⚑ |
| A12 | **Replay-v2 `DslEventCommand`** binary record (`.chmr`) | D2 | `VERSION=1` fixed-order | Per-record discriminator; Fixed/EntityRef binary; v1 hard-reject; **+ embed scenarioHash + algo-version, re-gate on playback** ⚑ |
| A13 | **`schema_version`** field on `ScenarioData` | D2 | Absent today | Integer monotonic; legacy-amnesty (absent ⇒ v1) |
| A14 | **`checksum_algo_version`** + bootstrap hashing rule | D2 / Arch 11 | Single hardcoded FNV | Bootstrap exclusion; version→algorithm registry |
| A15 | **`rulesetHash`** (caps corpus) at lobby handshake | D2 / Arch 11 | Only scenarioHash in 5-byte packet | **Corpus pinned to every tick-read constant (below); 50→64 reconcile is a hard prereq** ⚑ |
| A16 | **Load-time validator/canonicalizer contract** (gates all above) | D2 | `LoadScenario` zero validation; `LoadFromFile` null | Validator+hasher share one canonicalizer; **invoked over the MODEL at `ApplyScenario`, not only in the file-parse path** ⚑ |
| A17 | **In-tick determinism invariant** (event/threshold encoding) | D3 (critic) | `ScenarioDirector.cs:168/170/252` float/`"F2"`/`TryParse` | FiredEvent payloads carry Fixed.Raw ints; all threshold compares Fixed-vs-Fixed; InvariantCulture pinned process-wide ⚑ |

**Twelve of seventeen artifacts are net-new or replace something inconsistent; only the byte-hash (now repurposed as a tamper check) and the damage matrix are straight lifts.** The latent-bug note matters and is now sharper: the FactionDefinition options divergence has not fired *only* because `UnitDefinition` resolves damage/armor as **strings**, not enums. ⚑ The moment A8 adds `Hero` to the real `DamageType`/`ArmorType` enums, the failure is not "faction loads break" but **"the scenario loader and the faction loader silently bind the same `UnitDefinition` differently"** — a same-file-two-results determinism hole — unless M3 unifies options *first*.

---

## 4. Reference research distilled

**R1 — Built-in `[JsonPolymorphic]` is incompatible with `UnmappedMemberHandling.Disallow`.** With `Disallow`, the `$type` discriminator is itself an unmapped member and throws (`dotnet/runtime` #100057, **OPEN**; proposed fix #110466 **unapproved**). **Lesson:** Chimera wants both strict unknown-property rejection and polymorphism for a closed IR — impossible with the attribute today. → **Custom `JsonConverter<TBase>` + closed registry is the decisive call.**

**R2 — A custom converter with a tag→factory registry is the recommended closed-IR pattern.** Read the discriminator, look it up in a static `Dictionary<string,…>`, lookup-miss arm `throw new JsonException($"Unknown node kind '{kind}'")`. Keep the discriminator the **first** property; leave `AllowOutOfOrderMetadataProperties` **off**. **Lesson:** A1's discriminator is a stable string from a registry **decoupled from the C# class name** (R9/R10). ⚑ Make "kind first" a *canonicalizer-enforced* invariant so the happy path never has to buffer the subtree to find it (see §8 allocation contract).

**R3 — A `JsonConverter<Fixed>` is inherently NaN/Inf-safe.** Guard `if (reader.TokenType != JsonTokenType.Number) throw`, `GetDouble()`, `if (!double.IsFinite(d)) throw` before the 16.16 conversion. **Lesson:** Fronts the verified-unguarded `Fixed.FromFloat` (`NaN→0`, `Inf→int.Min/Max`) at the load boundary.

**R4 — Two-stage parse beats validation-inside-converters.** Converters enforce *local* invariants (known tag, finite Fixed, no unmapped members) but cannot see siblings/ancestors. Stage 1 = STJ → dumb DTOs (syntax fail-closed); stage 2 = pure-C# compile/validate (semantic fail-closed). **Lesson:** A16 is stage 2; ⚑ stage 2 runs over the **model** so it also gates in-memory (AI-gen/fallback) scenarios, and the **DTO stage is transient — it must NOT survive into the tick** (else "allocate once" is false; see §8).

**R5 — `AllowDuplicateProperties` defaults to TRUE (insecure).** Last-value-wins on duplicate keys is a determinism hole. Set it `false`. .NET 10's `Strict` preset bundles `Disallow` (re-triggers R1), so the custom-converter path is right across .NET 8/9/10.

**R6 — Source-gen is the only AOT-supported path; reflection fails silently only after publish.** Custom hand-written `JsonConverter<T>` is AOT-safe; reflective `JsonConverterFactory` is a trap; use generic `JsonStringEnumConverter<TEnum>`, never the non-generic one. **Lesson (revised):** the headline "~40% startup" win is a **fast-path** number; ⚑ any type touched by a custom converter (node/Fixed/graph — exactly the UGC-dominant types) runs in **metadata mode** and gets the **trim/AOT-safety** win, **not** the throughput win. Only converter-free leaf DTOs (manifest, schema_version header) get fast-path. This *weakens* the perf case for paying source-gen ceremony now — see the demoted Decision 4.

**R7 — Byte-hashing is fundamentally broken for a multi-tier tool.** Whitespace, key order, trailing-commas/comments (loader sets `Skip`/`AllowTrailingCommas`, so the file you hash ≠ the file you load), culture number formatting, and the silent killer — .NET Core 3.0's shortest-roundtrip `ToString` change. **Lesson:** A7 hashes the parsed model's `Fixed.Raw` integers, never float text — same domain as `SimChecksum`. This is decisive for M5.

**R8 — RFC 8785 / JCS is the wrong canonicalizer here.** JCS routes numbers through ECMAScript IEEE-754 double serialization (the exact drift path) and loses large integers. **Lesson:** build a bespoke model-walker (fixed field order, length-prefixed UTF-8 strings, enums as stable name, all numerics as `Fixed.Raw` LE int32). Keep human-friendly indented JSON on disk.

**R9 — Versioning: monotonic integer `schema_version`, single global, `JsonNode`-DOM migration registry, strict/tolerant split.** Migrate on the mutable DOM, *then* deserialize; upgrade-on-load-in-memory; never silently rewrite subscribed content. **Lesson:** ⚑ the strict gameplay region uses `Disallow`+throw and **rejects** unknown properties — it does **not** get an extension bag; only the explicit `_editor`/`_ext` namespace gets `[JsonExtensionData]`/verbatim. Forward-compat for the strict region is a trap → gate hard on `min_game_version` — which means `min_game_version` must actually be **enforced** (verified: it is not today). Today's unversioned `scenario.json` gets a one-time legacy amnesty (absent ⇒ v1, migrate up).

**R10 — Node-graph IR: flat id-referenced store, type discriminator decoupled from class name, two edge collections, semantic/cosmetic hard-split, hash a canonical form of the semantic graph only.** Shader Graph flat objectId store; SerializeReference class-name keying destroys instances on rename; Unreal splits `Pins`/`NodeGuid` from cosmetic; glTF `extensions` (validated) vs `extras` (free-form) with verbatim-preserve; RDF canonicalization yields the same hash for any equivalent graph. **Lesson:** A2 = flat node array + two typed sparse edge-lists + persistent string ids + registry-backed discriminator + named ports; A6 = single `_editor` sidecar keyed by node id, verbatim-preserved; A7 hashes the canonical `graph` section with `_editor` excluded. ⚑ Add the symmetric rule: a tier that can't *render* a known node must still **preserve it byte-faithfully** (round-trip unrenderable-but-valid gameplay nodes through `JsonNode`, never through a lossy DTO) — see §5 cross-tier hole.

---

## 5. The three options

Every option uses a **custom converter for the polymorphic node hierarchy** (R1 makes it non-negotiable). The options differ on *everything around* that converter.

### Option A — "Evolve the as-built" (minimal)

Custom `JsonConverter<NodeBase>` + registry and a `FixedConverter`, otherwise the lightest footprint. **Reflection** stays. **Per-domain loaders stay**, hand-aligned to one shared options instance. **Byte-hash stays, hardened** (normalize-on-load before hashing) so same-build peers agree. Annotations inline (hashed; cosmetic edits churn the hash — tolerated). Versioning = bare `schema_version` + a single v0→v1 stub. Validator = minimal check bolted into `ApplyScenario`.

- **Load-gate:** `ApplyScenario` (covers gameplay + imported maps; **misses preview/faction/replay**).
- **Hashes:** byte-FNV-32 over normalized bytes; annotations included.
- **AOT posture:** none — server publish breaks silently later.
- **Build cost:** lowest. **Risk:** highest *latent* (hash churn; AOT debt; annotation-in-hash desyncs on T3 layout). **Brownfield fit:** best diff, but **under-delivers D2** — the annotation channel and clean hash are *required*, not optional.

### Option B — "Unified + future-proof" (maximal)

Single `ContentLoader` is the **one authoritative file-gate**; a **model-level validator** at `ApplyScenario` covers in-memory scenarios. **Source-gen `JsonSerializerContext`** (hybrid, metadata mode, reflection fenced). **Canonical-model hash** (FNV-64) over `Fixed.Raw`, annotations excluded; `checksum_algo_version` + registry; `rulesetHash` over the caps corpus; **enforced `min_game_version`**; Hello protocol bumped *and checked*. Strict gameplay region (`Disallow`+throw, no extension bag) + tolerant `_editor`/`_ext` (verbatim). Full `JsonNode`-DOM migration registry. All seven schema shapes built fully; replay-v2 embeds the canonical hash.

- **AOT posture:** ready now; flip `PublishTrimmed` when a server project exists.
- **Build cost:** highest. **Risk:** highest near-term (largest diff mid-1.0-push). **Brownfield fit:** worst diff, best end-state.

### Option C — "Balanced / recommended" (defensible middle)

Build what is **load-bearing for determinism and the D2 hand-off now**, defer **pure future-proofing with no 1.0 consumer**.

**Pay for now:**
- **Single `ContentLoader` choke point** + one canonical `static readonly JsonSerializerOptions` (`Skip`, `AllowTrailingCommas`, `WriteIndented`, **per-enum `JsonStringEnumConverter<T>`** ⚑, `AllowDuplicateProperties=false`). One lenient case-insensitive ingest options for LLM only.
- **Model-level validator at `ApplyScenario`** so AI-gen, fallback, editor-in-memory, and replay-loaded scenarios all hit the same gate. ⚑
- **Custom node converter + registry + `FixedConverter`** (R1/R3).
- **In-tick determinism fix (A17):** FiredEvent payloads carry `Fixed.Raw`; threshold compares Fixed-vs-Fixed; InvariantCulture pinned. ⚑
- **Canonical-model hash, FNV-64, `Fixed.Raw`, annotations excluded, covering all gameplay files** (faction DTOs, catalog, registry). 0 remaps to 1; 0/short/unknown-algo ⇒ block. ⚑
- **`_editor`/`_ext` annotation sidecar, verbatim-preserved**, with a registry-field denylist so a gameplay key cannot hide in the excluded region. ⚑
- **`schema_version` + migration-registry machinery + bootstrap step + legacy amnesty + `checksum_algo_version` + `rulesetHash`.**
- **Enforced `min_game_version`** (CurrentGameVersion constant + InvariantCulture semver compare + auto-stamp from per-registry `introduced_in`). ⚑
- **Hybrid source-gen context — *threaded in lazily, not now*** (demoted from the draft; see Decision 4). Keep all DTOs/converters Godot-free so they are AOT-eligible the day a server project exists.

**Defer (explicitly):**
- **Shipping** NativeAOT/trimmed server — *and* the project-split it requires (see §8 prereq).
- A migration registry **populated** beyond the bootstrap step — build machinery, populate lazily **until first public release, then by invariant** (see Decision 10). ⚑
- Replay-v2 *can lag* the JSON schema, but bump `VERSION` 1→2 and embed the scenarioHash atomically.

### Comparison matrix

| Dimension | A — Evolve | B — Unified+future-proof | **C — Balanced (rec.)** |
|---|---|---|---|
| Poly node (de)serialize | custom converter + registry | custom converter + registry | custom converter + registry |
| Reflection vs source-gen | reflection only | hybrid source-gen | **reflection now; source-gen lazy (converters AOT-safe from day 1)** ⚑ |
| Loader topology | per-domain, hand-aligned | single `ContentLoader` | **single `ContentLoader`** |
| Load-gate coverage | `ApplyScenario` (misses preview/faction/replay) | file-gate + model-gate (all callers) | **file-gate + model-gate at `ApplyScenario` (incl. AI-gen/fallback/replay)** ⚑ |
| In-tick float/culture | unaddressed | fixed (A17) | **fixed (A17): Fixed.Raw payloads, Fixed compares** ⚑ |
| Hash domain | byte-FNV-32 (normalized) | canonical-model FNV-64 | **canonical-model FNV-64, `Fixed.Raw`, annotations excluded, all gameplay files** |
| 0-hash handling | inherited fail-open | block + remap 0→1 | **block on 0/short/unknown-algo; remap legit-0→1** ⚑ |
| Annotation channel | inline, **in hash** | `_editor`/`_ext` sidecar, excluded, verbatim | **`_editor`/`_ext` sidecar, excluded, verbatim + gameplay-key denylist** ⚑ |
| `min_game_version` | written, unenforced (as today) | enforced gate + auto-stamp | **enforced gate + auto-stamp from `introduced_in`** ⚑ |
| Versioning | bare field + 1 stub | full migration registry | **registry machinery + bootstrap + amnesty (invariant after publish)** |
| `checksum_algo_version` / `rulesetHash` | no / no | yes / yes | **yes / yes (corpus pinned, 50→64 hard prereq)** |
| Package byte-hash | recomputed on save (breaks `.chimera.zip`) | frozen algo-1, hashed pre-save | **frozen algo-1 tamper check, hashed pre-save** ⚑ |
| Protocol bump | none | Hello version checked + bumped | **Hello version checked + bumped (Ready len-tolerant is not enough)** ⚑ |
| AOT readiness | none | ready + analyzer | **AOT-eligible source kept clean; ready deferred with project-split** |
| Build cost (solo-dev weeks) | lowest | highest | **medium** |
| Near-term risk | low | high (big diff mid-push) | medium (front-loaded on unify) |
| Latent/long-term risk | **high** (hash churn, AOT debt, under-delivers D2, in-tick desync) | low | low |
| Delivers D2 hand-off fully | **no** | yes | **yes** |

---

## 6. Recommendation

**Recommended option: C — Balanced.** D2 did not hand off "a hash, if convenient" — it mandated an annotation channel **round-tripped but excluded from the gameplay hash**, which is *impossible* on a byte-hash. The canonical-model hash and the `_editor` sidecar are the literal deliverable, not future-proofing. The single `ContentLoader` is the only home for the authoritative gate and the cheapest moment to fix the already-latent options divergence before A8's enums turn it into a silent same-file-two-results bug. The **in-tick float fix (A17)** and the **enforced `min_game_version` gate** are likewise not optional polish — without them the perfect on-disk hash is defeated at tick N (A17) or the strict region's only forward-compat safety valve does not exist (min_game_version, verified unbuilt). Conversely, **shipping** NativeAOT (and the project-split it needs), a fully-populated migration corpus before any content exists, and an exhaustive replay-v2 have no 1.0 consumer — build machinery, defer content.

⚑ **Source-gen demoted from the draft's "now" column to "lazy."** The draft put source-gen in "now" on retrofit-cost grounds. Two findings undercut that: (1) the ~40% perf win is a fast-path number that does **not** apply to the converter-driven node/Fixed/graph types (R6, metadata mode); (2) there is **no project** a NativeAOT build can live in — verified single `godot.csproj` (`Godot.NET.Sdk/4.6.2`, `EnableDynamicLoading=true`), a configuration fundamentally incompatible with `PublishAot` on the client. AOT could only ever target a *separate* headless server `.csproj` that does not exist and that the engine section owns. Since the converters carry all the AOT-hostile logic and are hand-written AOT-safe **from day one regardless**, the retrofit is later "one `JsonSerializerContext` file per schema family," not a per-type rewrite. So the cheap hedge is: keep DTOs/converters/registry Godot-free in `src/Core/Definitions` (already the rule), surface the real AOT long-pole (the project-split), and add the context lazily.

**Per independent sub-decision — recommended defaults:**

| Sub-decision | Recommended default | Why |
|---|---|---|
| **Poly mechanism** | Custom `JsonConverter<NodeBase>` + closed registry; discriminator `kind`, first property | R1; R2; R10 |
| **Source-gen** | ⚑ **Defer (lazy), cheaply hedge** — converters AOT-safe now, context later | R6 (perf win N/A here); no AOT-capable project exists |
| **Loader topology** | **Single `ContentLoader.LoadAndValidate`** + model-level `Validate(model)` at `ApplyScenario` | 5–7 divergent options; in-memory paths bypass file gate |
| **Hash** | **Canonical-model FNV-64** over `Fixed.Raw`, fixed field order, enums as stable name, annotations excluded, **all gameplay files**; **block on 0/short/unknown-algo, remap legit-0→1**; keep frozen byte-hash as algo-1 tamper check | R7/R8; `SimChecksum` domain; 0-hash fail-open |
| **Enum converter** | ⚑ **Per-enum `JsonStringEnumConverter<DamageType>`/`<ArmorType>`/`<WinCondition>`** — never the non-generic factory | R6 (reflective-factory trap defeats the AOT-eligible claim) |
| **In-tick encoding (A17)** | ⚑ **FiredEvent payloads carry `Fixed.Raw` ints; all threshold compares Fixed-vs-Fixed; InvariantCulture pinned process-wide** | verified `ScenarioDirector:168/170/252` float/`"F2"`/`TryParse` |
| **Versioning** | Integer `schema_version` (absent⇒amnesty v1); DOM migration **machinery** + bootstrap; strict/tolerant split (strict = `Disallow`+throw, **no extension bag**); **enforced `min_game_version` (constant + InvariantCulture semver + auto-stamp)**; `checksum_algo_version` bootstrap-excluded | R9; verified unenforced min_game_version |
| **damage_table (+Hero)** | **Named-key object-of-objects** keyed by enum *name*; `Hero` inserted **before** COUNT (5×6); unspecified cell ⇒ 1.0 | R10; DamageMatrix sizes off COUNT |
| **N-resources** | Top-level ordered **resource registry**; balances/costs as sparse `{resourceId: amount}` maps | Ore+Crystal ceiling |
| **Tech-tree** | **Inline `prerequisites: string[]`** against a data-driven id registry | 3 hardcoded `TechTreeChecker` switches |
| **Annotation channel** | Single **`_editor` sidecar in-file**, keyed by node id, independently versioned, **verbatim `_ext` bag + gameplay-key denylist** | R10; closes the wrong-region leak |
| **Named-effect catalog** | Top-level **id-keyed `EffectDef` map**, by-id, cycle-linted; **cross-file referential-integrity at import** | R10; dangling-ref across zip |
| **Replay-v2** | New `DslEventCommand`, per-record discriminator, `Fixed`/`EntityRef` raw ints; **embed canonical scenarioHash + algo-version; bump VERSION 1→2, hard-reject v1; re-gate on playback** | verified path-only header |
| **rulesetHash corpus** | ⚑ **Pinned to every constant the tick reads**: spawn cap (post-50→64), 4096 entity cap, 30 Hz, resource-slot count, damage-table dims incl. Hero. Any constant consulted in the tick is in the corpus by definition. **50→64 reconcile is a hard prereq.** | rulesetHash else hashes a value the sim ignores |

**Where I expect Alec to override toward "build it fully now":** he overrode the prior two briefings toward maximal-now. Most likely overrides, all toward B: (1) **populate the full migration registry + split the annotation cosmetic versioning now**; (2) **build replay-v2 in lockstep** with the JSON schema; (3) **flip the AOT analyzer pass to a CI gate** immediately; (4) possibly **adopt source-gen now anyway** despite the demotion. None are wrong — they are the safer-but-costlier end of each axis. I would *not* expect or recommend an override away from the custom-converter, canonical-model-hash, A17, or min_game_version-enforcement decisions — the first two are forced by R1/R7, and the latter two are correctness fixes, not preferences.

---

## 7. Migration sequence

A strangler sequence, golden-checksum-gated, slotted into D1's steps 1–9 and D2's D0–D9s. Each D3 step rides an existing step.

| D3 step | Rides | Action | Golden-checksum gate |
|---|---|---|---|
| **D3.0** | D2 **D0** | Land `ContentLoader` skeleton + one canonical `JsonSerializerOptions` (**per-enum `JsonStringEnumConverter<T>`**, `AllowDuplicateProperties=false`) + `FixedConverter` (NaN/Inf reject). **Unify FactionDefinition/ContentPackager options onto it.** ⚑ **Decouple `ExportMapPackage` save from hash — `Pack` hashes pre-save bytes; freeze package byte-hash as algo-1.** Old loaders delegate. | Existing scenarios load byte-identical; `SimChecksum` unchanged. ⚑ **A corpus of existing `.chimera.zip` files still `Unpack` without `InvalidDataException` after unification.** ⚑ **Same `UnitDefinition` JSON via scenario path and faction path yields byte-identical `Fixed.Raw` fields.** |
| **D3.1** | D2 **D1s** | Ship `schema_version` + `checksum_algo_version` + **canonical-model hash (FNV-64) behind algo-2**, byte-hash retained as algo-1. Legacy files ⇒ amnesty v1/algo-1. ⚑ **Land enforced `min_game_version` (CurrentGameVersion constant + InvariantCulture semver compare, checked BEFORE strict deserialize).** ⚑ **Re-point `MainScene.cs:303` from `ComputeFileHash(ScenarioPath)` to the canonical-model hash over the in-memory loaded `ScenarioData`** (fixes the AI-gen stale-file hash). | Golden corpus: model-hash stable across re-save, key-reorder, whitespace, comment-insert. ⚑ **An old-format map without a satisfiable `min_game_version` is rejected with a clean "needs game ≥ X.Y" message, not an opaque unknown-kind throw.** ⚑ **Two peers loading the SAME AI-generated scenario produce matching hashes.** |
| **D3.2** | D1 **step 3** | **`damage_table.json`** (5×6, `Hero`×`Hero`) replaces `DamageMatrix._table`; enums extended **before COUNT**; loaded via `ContentLoader`. | Combat golden: damage outcomes bit-identical to the hardcoded matrix for all 4×5 legacy cells. |
| **D3.3** | D1 **steps 4–6** | `EffectDef`/`ModifierDef` DTOs + custom node converter + registry; thin stage-2 builder. `NamedEffectReference` catalog (A11) lands here with **cross-file referential-integrity at import**. | Round-trip golden: every D1 runtime node ⇄ DTO ⇄ runtime node; unknown `kind` rejected with located error; dangling `NamedEffectReference` id rejected with the specific id. |
| **D3.4** | D2 **D2s–D4s** | Graph IR (A2) + variable schema (A3) + custom-event registry (A4) serialized; `Dictionary<string,int>` stores replaced by dense SoA folded into `SimChecksum`. ⚑ **A17: replace `ScenarioDirector:168/170/252` float/`"F2"`/`TryParse` with `Fixed.Raw` payloads and Fixed-vs-Fixed compares.** ⚑ **Replace the unstable `Array.Sort` over triggers with a stable total order (Priority desc, ascending persistent node-id tiebreak).** | Graph golden: canonical-form hash invariant to node reorder; `SimChecksum` widened to vars/timers/Crystal/all slots. ⚑ **Two equal-priority triggers fire in node-id order on every machine.** ⚑ **A threshold-trigger fires identically with no float/culture in the path.** |
| **D3.5** | D2 **D5s** | N-resource registry (A9) + data-driven tech-tree (A10); cost/start maps migrate. | Economy golden: N=2 (Ore+Crystal) reproduces legacy balances exactly. |
| **D3.6** | D2 **D6s** | **Promote the validator to the authoritative pre-tick gate.** ⚑ **State the contract as: no `ScenarioData` reaches `_scenarioDirector.LoadScenario` without passing `Validate(model)` — covering AI-gen, fallback, editor-in-memory, and replay-loaded paths, not just file parse.** Strict region `Disallow`+throw (no extension bag); tolerant `_editor`/`_ext`. **Reconcile 50→64 spawn cap.** UI-definition schema (A5) + bind-resolution at load. | Gate golden: a corpus of malformed scenarios — **including an AI-generated one** — each rejected with the correct specific error; all valid pass; spawn cap = 64 everywhere. |
| **D3.7** | D2 **D7s–D8s** | Annotation channel (A6) fully live; hash confirmed to **exclude** `_editor`; verbatim round-trip across tiers. ⚑ **Enforce that no registry gameplay-key name appears inside `_editor`/`_ext` (denylist).** ⚑ **Unrenderable-but-valid gameplay nodes round-trip through `JsonNode`, never a lossy DTO.** | Annotation golden: cosmetic-only edit ⇒ **same** scenarioHash; sim-semantic edit ⇒ **different** hash. ⚑ **Author a T4 node, open+resave in the T2 editor — the T4 node survives AND scenarioHash is unchanged.** ⚑ **A gameplay key misplaced into `_editor` fails the load.** |
| **D3.8** | D2 **D1s (lobby)** | **`rulesetHash`** added to the Ready packet alongside scenarioHash; corpus = pinned tick-read constants (50→64 already done). ⚑ **Add the actual `PROTOCOL_VERSION` mismatch REJECTION in the Hello handshake (today it is exchanged but never compared) and bump it there; a host requiring `rulesetHash` rejects an old-protocol peer rather than relying on Ready's `len>=5` tolerance.** Block on hash 0. | Handshake golden: matched caps + matched scenario ⇒ ready; mismatched caps ⇒ blocked with reason; **old-protocol Hello rejected**; either-side hash 0 ⇒ blocked. |
| **D3.9** | D2 **D9s** | **Replay-v2**: `DslEventCommand` record; `VERSION` 1→2; v1 `.chmr` hard-rejected. ⚑ **Embed canonical scenarioHash + algo-version in the header; on playback route the referenced scenario through the model gate and assert recorded-hash == loaded-hash before the first Flush — fail closed on mismatch.** | Replay golden: a recorded match replays bit-identically under v2. ⚑ **Record under schema v1, migrate the scenario to v2, replay must reproduce OR fail closed — never silently desync against a changed world.** |

**Hard ordering invariants:** D3.0's options-unification **must precede** D3.2's enum lift (else scenario and faction loaders silently bind the same `UnitDefinition` differently — verified). The canonical hash (D3.1) **must ship with** `checksum_algo_version` so no existing replay/manifest spuriously desyncs. ⚑ The **50→64 reconciliation (D3.6) is a hard prerequisite of `rulesetHash` (D3.8)** — otherwise rulesetHash hashes a clamp value the sim then ignores. ⚑ **Enforced `min_game_version` (D3.1) must land with — not after — the strict region**, since it is the only thing turning an old loader's "unknown gameplay field" from a silent drop into a clean rejection.

---

## 8. Prerequisites surfaced + residual risks / watch-items

**Prerequisites (must land at or before the cited step):**

- **`Fixed.FromFloat` NaN/Inf reject** — verified unguarded; the `FixedConverter` (D3.0) is the enforcement point.
- ⚑ **In-tick float/culture removal (A17)** — verified live (`ScenarioDirector:168/170/252`); the canonical-model hash agrees at load and peers still desync at tick N without this. Pin `InvariantCulture` process-wide as belt-and-suspenders; the real fix is deleting the string round trip (D3.4).
- **Options unification before any new enum** — verified latent divergence; D3.0 before D3.2.
- **A dense-index var/timer/event-queue store** replacing `Dictionary<string,int>` — required for `SimChecksum` coverage and deterministic scenarioHash ordering (D3.4).
- **Ceiling reconciliation** — D2 scope is PerPlayer 0..7 but `ResourceStore.FACTION_COUNT=5`, `ScenarioDirector` hardcodes `slot<2`, and `Math.Min(count,50)` clamps spawns vs D1's `<=64`. ⚑ This is *in the rulesetHash corpus by definition* — reconcile before D3.8. Engine-section dependency D3 surfaces.
- ⚑ **NativeAOT requires a project-split, not just a source-gen context.** `PublishAot` is unsupported under `Godot.NET.Sdk` with `EnableDynamicLoading=true` (verified single client csproj). AOT can only target a separate headless server `.csproj` (Godot-Sdk-free, sharing sim source via linked files / a Core class-lib). **That extraction is the AOT long-pole** — not the `JsonSerializerContext`. D3 keeps the source AOT-*eligible*; the project-split is an engine-section decision D3 surfaces but does not own.
- ⚑ **`min_game_version` is an unbuilt subsystem, not a field.** Needs `CurrentGameVersion` (sim-layer, no Godot) + InvariantCulture semver-prefix comparer + load-gate check + per-registry-entry `introduced_in` so the saver auto-stamps `max(introduced_in)` (creators will never hand-maintain it). Without auto-stamp the gate is decorative. Lands D3.1.
- **Wire-format / protocol bump** — D3.8 grows the Ready packet *and* adds the missing Hello version check. Coordinate with in-flight netcode.

**Residual risks / watch-items:**

- ⚑ **The replay path is the counterexample to "the gate runs on every path."** Verified: `ReplayRecorder` stores only the scenario **path string**; `ReplayPlayer` re-loads from disk without re-hash/re-validate. A later-edited (or different-machine) scenario at that path replays the recorded command stream into a different world — silent garbage. Fixed by A12/D3.9 (embed hash, re-gate, assert recorded==loaded).
- ⚑ **The 0-hash bypass is an active bug, not just a risk.** `ComputeFileHash` returns 0 for missing files and `TryReadReady` defaults to 0 when `len<5`, so the handshake silently disables the mismatch check. Promoted to a **hard load-gate invariant** (§6): block on 0/short/unknown-algo; reserve 0 as "not computed" and remap a legitimate canonical-0 to 1.
- ⚑ **`scenarioHash`/`SimChecksum` must each cover the FULL model/state, not mirror each other.** `SimChecksum` today covers only EntityWorld pos/health, building state, and Player1/Player2 Ore — not Crystal, not slots 3+, not vars/timers. scenarioHash must cover the full authored model (all resources incl. Crystal, slots 0..7, var/timer initial state, **faction DTOs**), and `SimChecksum` must be **widened** (D3.4) to the sim's full mutable state. Neither scoped to the other's current narrow coverage.
- ⚑ **Cross-file / faction content is uncovered by any cross-peer hash today** — `faction_files` and any sibling catalog/registry are bundled but not hashed (`Pack` hashes `scenario.json` only). A7's content hash must cover all gameplay files (or the manifest records a per-file hash and the handshake sends one combined contentHash), plus an import-time referential-integrity pass that rejects dangling `NamedEffectReference`/`resourceId` ids.
- ⚑ **The two-region model leaks only at the wrong-region direction** — a gameplay key emitted into `_editor`/`_ext`, or a future gameplay field an old loader can't map, would silently fall into the excluded bag. Closed by: strict region = `Disallow`+throw (no bag); `_ext` verbatim only under explicit key; a denylist asserting no registry gameplay-key name appears in `_editor`/`_ext`.
- **`[JsonExtensionData]` round-trip fidelity** is weaker in STJ than Newtonsoft (materializes `JsonElement`, one member per class). Adequate for an opaque annotation blob; keep `_editor`/`_ext` plain numbers/strings.
- ⚑ **Allocation contract — "allocate once" is conditional.** "kind-first" must be a *canonicalizer-enforced* invariant so the converter reads directly into the concrete node via the registry factory **without** an intermediate `JsonElement`; only the cold authoring-import path may buffer. The DTO stage is **transient and must not survive into the tick** — the runtime node graph is allocated once into the SoA/dense stores. Add a load-allocation budget to the D3.4 golden gate.
- ⚑ **Canonical-walk cost on max-caps UGC.** Deterministic ordering means **sorting** every non-intrinsically-ordered collection (nodes by id, edges by `(src,srcPort,dst,dstPort)`, sparse maps by key) with **total/unique** sort keys (node id is the unique tiebreaker). Make the canonicalizer **single-pass** — emit the canonical byte stream once, feed both the FNV-64 accumulator and (optionally) the validator. The caps that bound the DSL bound the hash cost: assert the worst-case canonical hash on a max-caps scenario completes within the lobby-handshake budget (low tens of ms) as a D3.1 golden perf gate. `Fixed.Raw` emitted as fixed-width LE int32 (verified `int`, ONE=65536) — no varint ambiguity.
- **Fast-path silently drops `Converters`** — verify the source-gen context (if/when added) resolves the node hierarchy via the **metadata** resolver, not fast-path, or the custom converter is bypassed (R6 pitfall).
- **Migration determinism** — any culture-sensitive parse or dictionary enumeration *inside* a migration produces different upgraded bytes on different machines ⇒ desync. Pin `InvariantCulture`; keep migrations pure.
- **Discriminator collision window** — during D3.2–D3.4 the retiring `TriggerDefinition` (string `type`) coexists with the new IR; using `kind` avoids the collision; watch it.

---

## 9. Decision prompts

The genuinely-divergent calls. Each: question · options · recommendation.

1. **Overall option.** Build minimal-now (A), maximal-now (B), or balanced (C)?
   → ⭐ **Recommend C.** (Expect possible override to B per Alec's prior maximal-now pattern.)

2. **Polymorphic mechanism.** Built-in `[JsonPolymorphic]`/`[JsonDerivedType]`, or custom `JsonConverter<NodeBase>` + closed registry?
   → ⭐ **Recommend custom converter + registry.** Effectively forced by R1 (Disallow + `$type` incompatible, #100057 open) for a fail-closed closed IR; treat as decided unless you accept losing strict unknown-property rejection.

3. **Discriminator field name.** `type` (reuse legacy), `kind`, or `op`?
   → ⭐ **Recommend `kind`** — avoids collision with the still-present `TriggerDefinition.type` during the migration window.

4. **Source-gen: now or lazy.** ⚑ *(Demoted from the draft's "adopt now.")* Thread a hybrid source-gen `JsonSerializerContext` in at D3.0, or keep reflection now and add the context lazily (converters AOT-safe from day one regardless)?
   → ⭐ **Recommend defer (lazy), cheaply hedge.** The ~40% perf win is fast-path-only and does not apply to the converter-driven node/Fixed/graph types (R6 metadata mode), and there is **no project an AOT build can live in** today (verified single Godot-SDK client csproj). The real AOT long-pole is a server project-split, not the context. Keep DTOs/converters Godot-free; add the context later (one file per schema family). *(Defensible to adopt now if you want zero retrofit and accept paying ceremony with no current consumer.)*

5. **Loader topology.** Single `ContentLoader` choke point, or keep per-domain loaders? And: does the validator also run on **in-memory** scenarios?
   → ⭐ **Recommend single `ContentLoader` PLUS a model-level `Validate(model)` at the `ApplyScenario` boundary.** ⚑ The file-gate alone is bypassed by the AI-gen, fallback, editor-in-memory, and replay-loaded paths (all construct `ScenarioData` without parsing a file). Contract: no `ScenarioData` reaches `LoadScenario` unvalidated.

6. **Hash domain.** Byte-hash (hardened), or canonical-model hash (`Fixed.Raw`, FNV-64, annotations excluded, all gameplay files)?
   → ⭐ **Recommend canonical-model hash.** Forced by D2's annotation-exclusion mandate (impossible on a byte-hash) and R7's .NET-version float-text drift. ⚑ Must cover **all** gameplay files (faction DTOs, catalog, registry), block on 0/short/unknown-algo, and remap a legitimate canonical-0 to 1.

7. **In-tick determinism (A17).** ⚑ *(New — critic-surfaced.)* Leave the verified `ScenarioDirector` Fixed→float→`"F2"`→`TryParse` event/threshold path, or replace it with raw-int payloads + Fixed-vs-Fixed compares?
   → ⭐ **Recommend replace (D3.4), pin InvariantCulture process-wide.** This is the one path that defeats an otherwise-perfect load-time hash; it is not optional. *(No real counter-option; included because it is a substantive new build item, not in the draft.)*

8. **`min_game_version` enforcement.** ⚑ *(Promoted from aside to deliverable.)* Leave it written-but-unenforced (as today), or build the gate (constant + semver compare + auto-stamp from `introduced_in`)?
   → ⭐ **Recommend build the gate at D3.1.** Verified unenforced today; it is the strict region's only forward-compat safety valve, and without auto-stamp creator content is permanently mis-versioned. *(Minimal counter-option: enforce-only, no auto-stamp — cheaper but leaves creator content's version field unreliable.)*

9. **Annotation channel placement & region wall.** Single `_editor` sidecar in-file, a separate sidecar file, or inline-on-nodes? And does the strict region get an extension bag?
   → ⭐ **Recommend `_editor`/`_ext` sidecar in-file**, verbatim-preserved, independently versioned; **strict gameplay region uses `Disallow`+throw with NO extension bag**, plus a denylist rejecting any registry gameplay-key name inside `_editor`/`_ext`. ⚑ Inline forces annotations into the hash; a shared extension bag silently swallows misplaced gameplay fields.

10. **Migration registry depth.** Build machinery + bootstrap and populate lazily, or build + populate fully now?
    → ⭐ **Recommend machinery + bootstrap, populate lazily UNTIL first public/mod.io release — then by invariant.** ⚑ Lazy is safe only while there is no external content to strand; after first publish, make it a CI invariant that *every* `schema_version` gap has a registered `IScenarioMigration` (no holes), guarded by a golden corpus of published-format scenarios. *(Likely override toward "populate now" per Alec's pattern — acceptable, costlier upfront.)*

11. **Replay-v2 timing & binding.** Build in lockstep with the JSON schema or let it lag? Bind the replay to the scenario **path** or the **model hash**?
    → ⭐ **Recommend let it lag slightly, but bump VERSION 1→2 atomically, hard-reject v1, AND bind to the canonical model hash (not the path).** ⚑ Playback re-gates the referenced scenario and asserts recorded-hash == loaded-hash before the first Flush, so a later-migrated/edited world fails closed instead of silently desyncing. *(Override to "build in lockstep" is the safer maximal-now choice.)*

12. **Protocol / handshake hardening.** ⚑ *(Reframed — the bump belongs in Hello, not Ready.)* Where does the protocol bump live, and how does a new host treat an old peer?
    → ⭐ **Recommend: add the missing `PROTOCOL_VERSION` mismatch REJECTION in the Hello handshake and bump it there.** A host requiring `rulesetHash` must **reject** an old-protocol peer (whose Ready will lack it) rather than rely on Ready's `len>=5` tolerance, which silently treats a missing `rulesetHash` as absent rather than mismatched. *(Counter-option: tolerate old peers in a mixed-version lobby — only viable if you accept they cannot be ruleset-verified.)*

13. **AOT analyzer enforcement.** Periodic manual analyzer pass, or a CI gate from D3.6 on?
    → ⭐ **Recommend periodic manual pass** (defer the CI gate until the dedicated-server project-split decision is made — there is no AOT build target to gate against yet). *(Override to CI-gate-now if you want zero AOT debt accrual.)*

14. **`damage_table` shape.** Named-key object map (keyed by enum name) or positional 2D array?
    → ⭐ **Recommend named-key map**, `Hero` inserted before COUNT in both enums (5×6), unspecified cells default 1.0. Positional arrays corrupt silently on enum reorder.

15. **Tech-tree representation.** Inline `prerequisites: string[]` + data-driven registry, or a separate top-level tech-tree graph?
    → ⭐ **Recommend inline + registry** — simplest shape that eliminates the hardcoded `TechTreeChecker` switches. *(Override to a separate graph only if you want authored unlock-trees as a first-class T1/T3 view.)*
