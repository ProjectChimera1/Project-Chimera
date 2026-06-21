# Step 7 — Implementation Patterns — Briefing

> Companion to the **Step 7 — Implementation Patterns** section in `game-architecture.md` (the canonical
> catalog). This sidecar carries the method, the three scope decisions with full option text + Alec's calls,
> the adversarial-review corrections that were folded in, and the deferred implementation forks. Decided
> 2026-06-21.

## Method
Authored via an **11-agent design + adversarial-verify workflow** (ultracode):
1. **Author (5 agents, one per domain):** sim-core/determinism · effects/DSL · multiplayer/handshake ·
   content/loader/validation · presentation/composition/tooling. Each enumerated the *divergence points*
   (where two competent agents would write incompatible code) and authored each as a pattern with a
   real-API-grounded, determinism-safe C# example + an enforcement note. **67 patterns.**
2. **Verify (5 agents):** an adversarial reviewer per domain opened the real files and hunted determinism
   violations (float/`Fixed.FromFloat` in tick, `System.Random`, `GD.Print` in sim, `Dictionary` enumeration,
   non-ascending iteration, wall-clock), API mismatches vs the as-built code, contradictions with D1–D6/Step-6,
   and missing patterns. **49 issues/gaps found.**
3. **Synthesize (1 agent):** merged into 7 Novel Patterns + Standard Patterns + a Consistency Rules table,
   applying every fix and adding every missing pattern.

## The seven Novel Patterns (full designs in the canonical section)
N1 deterministic kernel (SoA + Fixed + SimRng + generalized SimChecksum) · N2 the Effect-Graph executor
(work-stack over a re-rooted `readonly struct EffectContext`, closed registry, allocate-at-load) + N2.5 the
unified `DamageResolver` · N3 Modifier SoA + `ModifierSystem` · N4 the DSL typed event/dataflow graph
(dense-index nodes, ForEach-only iteration, CEL-shaped Fixed-only expressions, acyclic typed event bus) · N5 the
two-rail custom UI (double-buffered versioned read-rail + `DslEventCommand` write-rail on the lockstep bus) · N6
the `Validated<T>` fail-closed content gate + single-owner seams · N7 the canonical-model multi-hash handshake
(stateful-authority attestation, majority-vote HALT).

## Scope decisions — full text + Alec's confirmed calls

### 1. Tier-1 sim test runner → ✅ **xUnit**
*All three candidates are free/open-source (Alec asked — confirmed: no cost).*
- **xUnit (chosen):** standard for new .NET 8 libraries; `[Fact]`/`[Theory]`, parallel by default, clean
  `Assert.Equal` for exact `uint`/`ulong` checksum equality, first-class in `dotnet test`.
- *NUnit:* richer parameterization, heavier attribute surface. *MSTest:* in-box, least expressive for the
  data-driven golden fixtures this project leans on.

### 2. Wire checksum / handshake hash width → ✅ **32-bit wire, 64-bit canonical**
- **Chosen:** the live per-60-tick `SimChecksum` + the wire Ready hashes stay 32-bit (`uint`, as-built — 9-byte
  Checksum packet, `SendChecksum(uint,uint)`); only the load-time canonical model hash is FNV-64, truncated for
  the wire. Least churn for a brownfield strangler.
- *Rejected — widen everything to `ulong`:* marginally safer collision odds across 8 factions but touches the
  wire format + replay golden in one change.

### 3. Authoring-boundary content model numeric shape → ✅ **`Fixed` end-to-end (convert at parse)**
- **Chosen:** `FixedJsonConverter` quantizes + rejects NaN/Inf/over-16.16-range during deserialize; the model
  field IS `Fixed`; the validator checks `Fixed.Raw` sign/range; the canonical hash folds `.Raw` directly — one
  quantization boundary, no second conversion, and the agreement fingerprint is taken from the exact numbers the
  game runs.
- *Rejected — float DTOs through validation, convert in `ScenarioApplier`:* matches the as-built
  `MainScene.cs:518` shape, smaller migration, but two representations of the same field and a lossier hash
  boundary.

## Adversarial-review corrections folded in (the load-bearing ones)
- The grounding **overstated the `SimChecksum` hole**: `Compute` already folds EntityWorld Position/Health +
  BuildingStore Alive/Health/ConstructionTimer + `Ore[P1]`/`Ore[P2]`. The real gap is narrower —
  `Crystal`(all), `Ore` slots 3+, `SupplyUsed`/`SupplyCap`, and the net-new stores. `Mix` is widened
  `private`→`internal` (`InternalsVisibleTo` the test project).
- **`EffectContext` is a `readonly struct`, NOT a `ref struct`** — a ref struct cannot be stored in the
  executor's `Frame[]` work-stack (two source designs were mutually incompatible). Plain readonly struct is
  array-storable + still zero-heap.
- **`SimRng` is a single shared instance referenced (never copied)** so a child effect's draw advances the
  stream the parent/siblings see → draw order = work-stack pop order on every peer.
- **Per-DEPTH `SearchArea` hit buffers** so a nested search never clobbers its parent's buffer.
- **`DamageResolver` formula corrected** to `base * DamageMatrix.Get(...)` (no `- armorValue`; there is no
  per-entity ArmorValue array as-built) and `DamageMatrix.Get` is **static**, returns `Fixed`.
- **`FACTION_COUNT` cardinality pinned:** as-built `FACTION_COUNT=5` is enum cardinality incl. `Neutral`;
  `FactionRegistry` exposes `PLAYER_COUNT=8` (slot loops), `FACTION_ARRAY_SIZE=PLAYER_COUNT+1` (array sizing,
  `Neutral=0`), and the single `ToFaction(slot)=(Faction)(slot+1)` cast.
- The **write rail is genuinely net-new** — as-built `EnqueueOrder` is a 4-arg `UnitOrder` (11 bytes); there is
  no `NetworkCommand` type or event path. Scoped honestly (new `PacketType.DslEvent` + shared applier case).
- Checksum/`LastChecksum`/`OnChecksum` are **`uint`** as-built; the golden test asserts exact `uint` equality.

## Deferred to implementation (M1) — tracked forks
`SimRng` API/state shape · `ILogSink` method set + arg shape · `SimChecksum` coverage-guard (registry vs
reflection) · `EffectContext.Area` rep / Modifier stat-set + stacking / `DslValue` union · Energy/Mana regen
model · cross-faction same-tick event tie-break · server >2-player quorum (D5 ships majority) + spectator
attestation · Abort/HALT recovery policy (UX) · `DslEvent` authz granularity · `DslVarReadback` publish
granularity · `ContentJsonContext` split · `Validated<T>` internal-ctor boundary. Each carries a recommended
default in the canonical section; none changes a pattern's shape.

## Hand-offs
→ **Step 8 (Validation)** checks GDD/UX/architecture/epics alignment + completeness — the pattern enforcement
(analyzers + Tier-1 tests) is the implementation-consistency evidence Step 8 audits. → **M1 implementation**: the
analyzers + the `ProjectChimera.Sim.Tests` golden-checksum harness ARE these patterns made executable; they land
with the Step-6 migration Step 0. → **D1–D6 implementation**: every agent building a D-system follows the matching
pattern here, so the strangler stays checksum-identical.

## Residual risks
The patterns are only as strong as the analyzers + tests that enforce them — an advisory-on-master analyzer that
is never wired up is a convention, not a guard (budget the analyzer build into M1). A few deferred forks
(cross-faction event tie-break, server quorum) are checksum-relevant — pin them before the matching subsystem
ships, not after.
