#nullable enable
using System;
using System.Collections.Generic;
using System.Linq; // Story 7.4 — canonical-order edge iteration for the expression compile pass
using ProjectChimera.Core; // Faction, FactionRegistry, BuildingType
using ProjectChimera.Dsl; // Story 7.3 — TriggerGraph / NodeBase / EffectActionNode (trigger_graph gate)
using ProjectChimera.Effects; // Story 7.3 — EffectBounds (trigger-embedded effect load-time bounds gate)
using ProjectChimera.Navigation; // PathabilityGrid (Story 6.5 blocked-cell fail-closed)

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The single fail-closed pre-tick gate (Story 1.7, AR-39). Every scenario entry path funnels a
    /// <see cref="ScenarioData"/> through <see cref="Validate"/> before it is applied to the simulation. On
    /// success it mints a <see cref="Validated{T}"/> (the proof-of-validation token); on the FIRST failed check
    /// it returns a located <see cref="ValidationResult"/> error (field path + offending value). It is pure: it
    /// NEVER throws and NEVER logs — the call site surfaces the located error. Story 7.7: the gate is
    /// FAIL-CLOSED EVERYWHERE, unconditionally — shadow mode and its env toggle are removed; proceeding requires
    /// <c>Ok</c>, and only a passing model ever mints a <see cref="Validated{T}"/>.
    /// Godot-free (src/Core/Definitions), so it compiles into the Tier-1 test assembly and the AOT-eligible sim
    /// layer.
    ///
    /// The model is the as-built <see cref="ScenarioData"/> (still <c>float</c>-based in 1.7). The validator
    /// replicates the finiteness/range checks the <see cref="FixedJsonConverter"/> would do (the model does not
    /// route through that converter today); when D3 later converts the model to <see cref="Fixed"/>, those
    /// become redundant. A distinct canonical <c>ScenarioModel</c> is NOT introduced here (D2).
    /// </summary>
    public sealed class ScenarioValidator
    {
        /// <summary>
        /// Mint token for <see cref="Validated{T}"/>. Public TYPE (so <see cref="Validated{T}"/> can name it as
        /// a ctor param) with an <c>internal</c> CONSTRUCTOR (so only this sim assembly can construct one).
        ///
        /// NOTE (D1 correction): the story's D1 proposed a PRIVATE Proof ctor on the premise that "the enclosing
        /// type can call its nested type's private constructor" — that is FALSE in C# (it raises CS0122; the
        /// access rule is one-directional: a nested type sees the enclosing type's privates, not the reverse).
        /// The equivalent guarantee is <c>internal</c> ctor + the belt-and-suspenders source scan
        /// (ValidatedSoleMinterTest) that fails the build if any <c>new Validated&lt;</c> appears outside this
        /// file. Together: nothing outside the assembly can mint, and inside the assembly only this validator does.
        /// </summary>
        public sealed class Proof { internal Proof() { } }

        // The validator's single proof token, reused on every successful Validate. The ONLY `new Validated<` in
        // the codebase is below in Validate (guarded by ValidatedSoleMinterTest).
        private static readonly Proof _proof = new Proof();

        /// <summary>
        /// The 16.16 representable range limit (mirrors <see cref="FixedJsonConverter"/>'s FixedRangeLimit).
        /// Valid values satisfy [-Range, Range): -32768 is exactly representable (raw int.MinValue); +32768 and
        /// beyond overflow <c>Fixed.FromFloat</c>'s (int)(value*65536) cast and wrap.
        /// </summary>
        private const float Range = 32768f;

        /// <summary>Worst-case hero level a revival cost/time curve is evaluated at (mirrors the global
        /// <c>UnitDefinitionValidator.HeroLevelMax</c>). The linear curve <c>base + perLevel × level</c> must stay in the
        /// 16.16 range at this level or the runtime quantize overflows and wraps negative (Story 3.14 / 3.13 class).</summary>
        private const int MaxRevivableLevel = 100;

        // Exact set of BuildingType NAMES the applier (MainScene.ParseBuildingType) recognizes. Cached so the
        // building-type check allocates nothing. Validating by name (not Enum.TryParse, which also accepts
        // numeric strings like "5") matches how scenario JSON is authored and rejects unknown names instead of
        // silently defaulting them to CommandCenter the way the applier does (D4).
        private static readonly string[] _buildingTypeNames = Enum.GetNames(typeof(BuildingType));

        // Story 4.7: the closed resource_type vocabulary (mirrors _buildingTypeNames — small, hand-authored,
        // allocated once). Only Ore/Crystal have real ResourceStore-backed balances today.
        private static readonly string[] _resourceTypeNames = { "Ore", "Crystal" };

        /// <summary>
        /// Validate a scenario model. Returns <see cref="ValidationResult.Pass"/> with a minted
        /// <see cref="Validated{T}"/> on success, or <see cref="ValidationResult.Fail"/> with a located error on
        /// the first failed check. Pure — never throws, never logs.
        /// </summary>
        /// <param name="slotFactionDefs">Story 6.8 (optional) — the per-slot resolved <see cref="FactionDefinition"/>s
        /// (indexed by <c>(int)Faction</c>, the same length-5 array the applier holds), so a pre-placed building's
        /// <c>type</c> can be accepted as an authored building-def id present in its OWNER faction's <c>Buildings</c>
        /// (the retired enum gate), not only as a legacy enum name. NULL (the default — every legacy caller/test) keeps
        /// the enum-name-only behavior, byte-identical: an authored custom id then still fails closed. Trigger
        /// <c>building_type</c> checks stay enum-only regardless (they have no single owner faction; the trigger-DSL
        /// building resolution is Epic 7 territory).</param>
        public ValidationResult Validate(ScenarioData m, IReadOnlyList<FactionDefinition?>? slotFactionDefs = null)
        {
            if (m is null) return ValidationResult.Fail("scenario is null.");

            // Story 7.7 (proof discipline): the Validated<ScenarioData> token is minted ONLY at the Pass return at
            // the BOTTOM of this method, after every check has passed. A failed validation carries NO token — the
            // 1.7/1.8b shadow-mode apply-on-fail plumbing is retired, so an invalid model can never reach the
            // applier. The Pass site below is the codebase's sole `new Validated<ScenarioData>` (guarded by
            // ValidatedMintingTests' sole-minter source scan).

            // ── Story 7.7 (D3 versioning stamps): schema_version / checksum_algo_version. Absent (null) ⇒ v1
            //    legacy amnesty, no migration; a value NEWER than this build understands fails closed with a
            //    located error (never a silent misparse of a future format). Both stamps are EXCLUDED from
            //    CanonicalModelHash, so a re-save that adds them to a legacy file never moves the handshake. ──
            if (m.SchemaVersion is int schemaV && schemaV > ScenarioSerializer.CurrentSchemaVersion)
                return ValidationResult.Fail(
                    $"scenario.schema_version={schemaV} is newer than this build's supported schema version " +
                    $"{ScenarioSerializer.CurrentSchemaVersion} — a newer-format file cannot be safely loaded.");
            // Review follow-up: an EXPLICIT 0/negative stamp is nonsense (versions start at 1; only an ABSENT
            // stamp gets the v1 amnesty) — fail it closed rather than treating garbage as legacy.
            if (m.SchemaVersion is int schemaLow && schemaLow < 1)
                return ValidationResult.Fail(
                    $"scenario.schema_version={schemaLow} must be >= 1 (omit the stamp entirely for a legacy file).");
            if (m.ChecksumAlgoVersion is int algoV && algoV > CanonicalModelHash.AlgoVersion)
                return ValidationResult.Fail(
                    $"scenario.checksum_algo_version={algoV} is newer than this build's CanonicalModelHash.AlgoVersion=" +
                    $"{CanonicalModelHash.AlgoVersion} — a newer-algo file cannot be safely loaded.");
            if (m.ChecksumAlgoVersion is int algoLow && algoLow < 1)
                return ValidationResult.Fail(
                    $"scenario.checksum_algo_version={algoLow} must be >= 1 (omit the stamp entirely for a legacy file).");

            // ── Map bounds: finite, > 0, inside the Fixed range (it is a coordinate ceiling), and inside the FIXED
            //    sim-grid extent ──
            if (!Finite(m.MapBounds) || m.MapBounds <= 0f)
                return ValidationResult.Fail($"scenario.map_bounds={m.MapBounds} must be finite and > 0.");
            if (m.MapBounds >= Range)
                return ValidationResult.Fail(
                    $"scenario.map_bounds={m.MapBounds} exceeds the 16.16 range [0, {Range}).");
            // DW-158: the 16.16 range is NOT the real ceiling. The sim grids (flow field, fog, pathability) are FIXED
            // at ±MapSizes.MaxHalfExtent (== FlowField.WORLD_HALF_INT == 128, pinned by GridDimensionConsistencyTests),
            // and FlowField.WorldToCell CLAMPS anything past them onto the edge cells. So with an authored half-extent
            // ABOVE 128 the every-coordinate ±map_bounds gate below admits positions the grid cannot represent, and two
            // distinct far-out positions ALIAS onto one cell: a blocking prop / water volume outside the grid stamps an
            // edge-cell footprint it does not occupy, which can then false-flag an UNRELATED in-grid start position as
            // blocked (and vice-versa). Deterministic, but semantically wrong — reject the authoring lie here so every
            // downstream ±map_bounds check is also a "fits the grid" check. map_bounds == 128 (MapSize.Large, the
            // shipped maximum) stays legal: the exact +128 boundary line's clamp is the deliberately-documented
            // playable ceiling (ledgered separately as DW-162), not this aliasing class.
            if (m.MapBounds > MapSizes.MaxHalfExtent)
                return ValidationResult.Fail(
                    $"scenario.map_bounds={m.MapBounds} exceeds the fixed sim-grid half-extent {MapSizes.MaxHalfExtent} " +
                    "(the flow-field / fog / pathability grids are fixed at ±128, so any position beyond that aliases " +
                    "onto an edge cell); the supported map sizes are Small 80 / Medium 120 / Large 128.");

            float bounds = m.MapBounds;

            // ── Story 6.5: the slope-auto-block threshold is a float→Fixed boundary folded into CanonicalModelHash.
            //    An out-of-range/non-finite value would overflow Fixed.FromFloat with a platform-unspecified result
            //    (peers on different runtimes could then fold different Raw ⇒ false handshake reject), so gate it
            //    finite, non-negative, and inside the Fixed range like map_bounds. Default 0f passes unchanged. ──
            if (!Finite(m.SlopeBlockThreshold) || m.SlopeBlockThreshold < 0f || m.SlopeBlockThreshold >= Range)
                return ValidationResult.Fail(
                    $"scenario.slope_block_threshold={m.SlopeBlockThreshold} must be finite and within [0, {Range}).");

            // ── Story 6.7 / 9.2: suggested_players is authoring metadata (engine ceiling now Faction.Player8).
            //    Omit-when-default (0) ⇒ "unspecified" ⇒ nothing to validate (every existing scenario passes
            //    unchanged). A PRESENT value must be in [2, PLAYER_COUNT]; 1 or above PLAYER_COUNT fails closed.
            //    This is a hard fail (an unshippable design intent), distinct from the SOFT below-suggested
            //    advisory in CollectAdvisories. ──
            if (m.SuggestedPlayers != 0 && (m.SuggestedPlayers < 2 || m.SuggestedPlayers > FactionRegistry.PLAYER_COUNT))
                return ValidationResult.Fail(
                    $"scenario.suggested_players={m.SuggestedPlayers} must be in [2,{FactionRegistry.PLAYER_COUNT}].");

            // ── Collections must be present. A null array is malformed input the applier would NRE on, so the
            // validator rejects it (located) rather than silently treating it as empty via the `?? Array.Empty`
            // guards below — those are then belt-and-suspenders. [Story 1.7 review patch] ──
            if (m.PlayerSlots is null)   return ValidationResult.Fail("scenario.player_slots is null.");
            if (m.ResourceNodes is null) return ValidationResult.Fail("scenario.resource_nodes is null.");
            if (m.Buildings is null)     return ValidationResult.Fail("scenario.buildings is null.");
            if (m.Units is null)         return ValidationResult.Fail("scenario.units is null.");
            // Story 1.11 (AC3): triggers are now gated too. A null array would NRE in ScenarioDirector.LoadScenario
            // (new bool[_triggers.Length]); reject it located, like the four collections above.
            if (m.Triggers is null)      return ValidationResult.Fail("scenario.triggers is null.");

            // ── DW-230: fail-closed store-capacity caps. The applier writes resource nodes into a fixed
            //    ResourceNodeStore (MAX_NODES) and buildings into a fixed BuildingStore (MAX_BUILDINGS); past the cap
            //    the store's Create returns -1 and the overflow entry vanishes (silently, pre-fix). Reject an
            //    over-capacity scenario AT THE GATE — located, naming the field, its count, and the cap — so overflow
            //    is a diagnosed rejection, not a vanished entity. Checked here, after the null-collection checks
            //    guarantee non-null. ──
            if (m.ResourceNodes.Length > ResourceNodeStore.MAX_NODES)
                return ValidationResult.Fail(
                    $"scenario.resource_nodes has {m.ResourceNodes.Length} nodes, exceeding the store capacity of {ResourceNodeStore.MAX_NODES}.");
            if (m.Buildings.Length > BuildingStore.MAX_BUILDINGS)
                return ValidationResult.Fail(
                    $"scenario.buildings has {m.Buildings.Length} buildings, exceeding the store capacity of {BuildingStore.MAX_BUILDINGS}.");

            // ── Story 6.6: structurally validate props / cameras / water BEFORE decoding the blocked union (their
            //    coords quantize into the footprint mask below, so a non-finite/out-of-range one must fail first) and
            //    before any position check. NULL collections (every existing scenario) ⇒ nothing to validate ⇒ the
            //    pass path is unchanged. First-fail located error, mirroring the buildings/units/regions loops. ──
            ScenarioProp[] props = m.Props ?? Array.Empty<ScenarioProp>();
            for (int i = 0; i < props.Length; i++)
            {
                ScenarioProp p = props[i];
                if (p is null) return ValidationResult.Fail($"scenario.props[{i}] is null.");
                // x/z are hash-folded (the footprint cell) — gate finite + in the 16.16 range + within map bounds.
                string? pe = CheckCoord($"scenario.props[{i}].x", p.X, bounds)
                          ?? CheckCoord($"scenario.props[{i}].z", p.Z, bounds);
                if (pe != null) return ValidationResult.Fail(pe);
                if (!Finite(p.Rot))
                    return ValidationResult.Fail($"scenario.props[{i}].rot={p.Rot} must be finite.");
                if (p.Scale is float sc && (!Finite(sc) || sc <= 0f))
                    return ValidationResult.Fail($"scenario.props[{i}].scale={sc} must be finite and > 0.");
            }

            var declaredCameras = new HashSet<string>(StringComparer.Ordinal);
            ScenarioCamera[] cameras = m.Cameras ?? Array.Empty<ScenarioCamera>();
            for (int i = 0; i < cameras.Length; i++)
            {
                ScenarioCamera cam = cameras[i];
                if (cam is null) return ValidationResult.Fail($"scenario.cameras[{i}] is null.");
                if (string.IsNullOrEmpty(cam.Name))
                    return ValidationResult.Fail($"scenario.cameras[{i}].name must be a non-empty name.");
                if (!declaredCameras.Add(cam.Name))
                    return ValidationResult.Fail($"scenario.cameras[{i}].name='{cam.Name}' is a duplicate.");
                // Cameras never fold into any hash, but a non-finite position/target/fov would still break the in-editor
                // preview and the MoveCamera action, so gate well-formedness fail-closed.
                if (!Finite(cam.X) || !Finite(cam.Y) || !Finite(cam.Z)
                    || !Finite(cam.TargetX) || !Finite(cam.TargetY) || !Finite(cam.TargetZ))
                    return ValidationResult.Fail($"scenario.cameras[{i}] has a non-finite position/target coordinate.");
                if (!Finite(cam.Fov) || cam.Fov <= 0f || cam.Fov >= 180f)
                    return ValidationResult.Fail($"scenario.cameras[{i}].fov={cam.Fov} must be finite and in (0, 180).");
            }

            ScenarioWater[] water = m.Water ?? Array.Empty<ScenarioWater>();
            for (int i = 0; i < water.Length; i++)
            {
                ScenarioWater w = water[i];
                if (w is null) return ValidationResult.Fail($"scenario.water[{i}] is null.");
                // x/z (min corner) and x+w / z+h (max corner) are hash-folded (the footprint rect) — every corner must
                // be finite, in the 16.16 range, and within map bounds; extents must be positive (a well-formed rect).
                string? we = CheckCoord($"scenario.water[{i}].x", w.X, bounds)
                          ?? CheckCoord($"scenario.water[{i}].z", w.Z, bounds);
                if (we != null) return ValidationResult.Fail(we);
                if (!InRange(w.W) || w.W <= 0f)
                    return ValidationResult.Fail($"scenario.water[{i}].w={w.W} must be finite and > 0.");
                if (!InRange(w.H) || w.H <= 0f)
                    return ValidationResult.Fail($"scenario.water[{i}].h={w.H} must be finite and > 0.");
                string? wce = CheckCoord($"scenario.water[{i}] max_x", w.X + w.W, bounds)
                           ?? CheckCoord($"scenario.water[{i}] max_z", w.Z + w.H, bounds);
                if (wce != null) return ValidationResult.Fail(wce);
                if (!Finite(w.Y))
                    return ValidationResult.Fail($"scenario.water[{i}].y={w.Y} must be finite.");
            }

            // ── Story 6.5 / 6.6: decode the authored blocked-cell UNION ONCE (null/all-clear ⇒ no grid ⇒ every
            //    position check below is a no-op, so a flat/legacy map's pass path is unchanged). The union is the
            //    PAINTED bitset OR'd with each blocking-prop's single-cell footprint and each water volume's rect
            //    footprint — the SAME PathabilityGrid.StampPropInto/StampWaterInto derivation the load-time grid and
            //    the CanonicalModelHash fold use, so validator↔sim↔hash agree on cell identity. Slope-DERIVED cells
            //    depend on the terrain heightmap (unavailable at this Godot-free gate); they are recomputed at load and
            //    never carry a start/spawn (the editor overlay lets the author see and avoid them). Positions resolve
            //    through the SAME 128²/2-unit FlowField.WorldToCell mapping the sim enforces. ──
            PathabilityGrid? painted = null;
            {
                // Story 6.6 (review V1): OR the PAINTED bitset with the ONE shared blocking-prop/water footprint
                // derivation (m.Props/m.Water — the exact input the hash folds) so validator ↔ hash ↔ runtime grid
                // provably agree on the blocked cell set. No hand-copied stamp loop can drift here anymore.
                bool[] mask = PathabilityGrid.FromBase64(m.PathabilityBlocked);
                bool[]? footprint = PathabilityGrid.BuildBlockingFootprint(m.Props, m.Water);
                if (footprint != null)
                    for (int i = 0; i < mask.Length; i++)
                        if (footprint[i]) mask[i] = true;
                var g = new PathabilityGrid(mask);
                if (g.AnyBlocked) painted = g;
            }

            // ── Player slots: range / non-negative ore / in-bounds base / engine ceiling / uniqueness ──
            // declared = the set of slots a PlayerSlot actually declares; buildings/units must reference one of
            // these or they are dangling.
            ScenarioPlayerSlot[] slots = m.PlayerSlots ?? Array.Empty<ScenarioPlayerSlot>();
            var declared = new HashSet<int>();
            for (int i = 0; i < slots.Length; i++)
            {
                ScenarioPlayerSlot s = slots[i];

                if (s.Slot < 0 || s.Slot >= FactionRegistry.PLAYER_COUNT)
                    return ValidationResult.Fail(
                        $"scenario.player_slots[{i}].slot={s.Slot} is out of [0,{FactionRegistry.PLAYER_COUNT}).");

                // The engine faction ceiling: FactionRegistry.ToFaction(slot) must map to a defined Faction enum
                // member. Story 9.2 extended the enum to Player8 and widened every per-faction array to
                // FACTION_ARRAY_SIZE (9), so slot in [0,8) now maps to a real, backed Faction. The redundant-with-
                // PLAYER_COUNT-above check is kept as a defensive engine-ceiling assertion tied to the enum itself.
                if (s.Slot + 1 > (int)Faction.Player8)
                    return ValidationResult.Fail(
                        $"scenario.player_slots[{i}].slot={s.Slot} maps to an undefined Faction " +
                        $"(engine ceiling: slot <= {(int)Faction.Player8 - 1}).");

                if (!declared.Add(s.Slot))
                    return ValidationResult.Fail(
                        $"scenario.player_slots[{i}].slot={s.Slot} is a duplicate.");

                string? e = CheckNonNeg($"scenario.player_slots[{i}].start_ore", s.StartOre)
                         ?? CheckNonNeg($"scenario.player_slots[{i}].start_crystal", s.StartCrystal)
                         ?? CheckCoord($"scenario.player_slots[{i}].base_x", s.BaseX, bounds)
                         ?? CheckCoord($"scenario.player_slots[{i}].base_z", s.BaseZ, bounds);
                if (e != null) return ValidationResult.Fail(e);

                // Story 6.5: a start position on a PAINTED blocked cell fails closed — a unit could never legally
                // occupy it, so the map is unplayable. Fail before any tick with a clear message.
                string? be = CheckNotBlocked($"scenario.player_slots[{i}]", "start base", s.BaseX, s.BaseZ, painted);
                if (be != null) return ValidationResult.Fail(be);
            }

            // ── Resource nodes: in-bounds position, non-negative supply/rate, non-negative gatherer cap ──
            ScenarioResourceNode[] nodes = m.ResourceNodes ?? Array.Empty<ScenarioResourceNode>();
            for (int i = 0; i < nodes.Length; i++)
            {
                ScenarioResourceNode n = nodes[i];
                string? e = CheckCoord($"scenario.resource_nodes[{i}].x", n.X, bounds)
                         ?? CheckCoord($"scenario.resource_nodes[{i}].z", n.Z, bounds)
                         // Story 6.5: a resource node on a painted blocked cell is unreachable by gatherers (soft-lock)
                         // — fail closed, the same principle as start bases/units below.
                         ?? CheckNotBlocked($"scenario.resource_nodes[{i}]", "resource node", n.X, n.Z, painted)
                         ?? CheckNonNeg($"scenario.resource_nodes[{i}].supply", n.Supply)
                         ?? CheckNonNeg($"scenario.resource_nodes[{i}].rate", n.Rate);
                if (e != null) return ValidationResult.Fail(e);
                if (n.MaxGatherers < 0)
                    return ValidationResult.Fail(
                        $"scenario.resource_nodes[{i}].max_gatherers={n.MaxGatherers} must be >= 0.");

                // ── Story 4.7: collection model / resource type / requires_structure gate / owner slot / income period ──
                // collection_model reuses the Story 4.3 closed vocabulary (ResourceDefinition.KnownCollectionModels)
                // so the two authoring surfaces never drift. resource_type is its own small closed set — only Ore/
                // Crystal have real ResourceStore-backed balances today.
                if (Array.IndexOf(ResourceDefinition.KnownCollectionModels, n.CollectionModel) < 0)
                    return ValidationResult.Fail(
                        $"scenario.resource_nodes[{i}].collection_model='{n.CollectionModel}' is not a known collection model " +
                        $"({string.Join("/", ResourceDefinition.KnownCollectionModels)}).");
                if (!IsKnownResourceType(n.ResourceType))
                    return ValidationResult.Fail(
                        $"scenario.resource_nodes[{i}].resource_type='{n.ResourceType}' is not a known resource type " +
                        $"({string.Join("/", _resourceTypeNames)}).");
                string? se = CheckNonNeg($"scenario.resource_nodes[{i}].requires_structure_radius", n.RequiresStructureRadius)
                          ?? CheckNonNeg($"scenario.resource_nodes[{i}].income_period_ticks", n.IncomePeriodTicks);
                if (se != null) return ValidationResult.Fail(se);
                // owner_slot is only load-bearing for Income (no assigned worker to infer a faction from) — required
                // AND must reference a declared player_slot, exactly like the buildings/units slot-reference check
                // above. Inert (unvalidated) for GATHER/Streaming, which credit the gathering worker's own faction.
                if (n.CollectionModel == "Income" && !declared.Contains(n.OwnerSlot))
                    return ValidationResult.Fail(
                        $"scenario.resource_nodes[{i}].owner_slot={n.OwnerSlot} references no declared player_slot " +
                        $"(required when collection_model=Income).");
                // Review patch: income_period_ticks=0 passed the bare non-negative check above but makes
                // IncomeTicksElapsed's `< IncomePeriodTicks` comparison true on tick 1 forever — a degenerate
                // "credit every tick" mode, not the intended periodic trickle. Only meaningful (and only gated) for
                // Income; GATHER/Streaming ignore the field entirely, so a 0 there is inert, not an authoring error.
                if (n.CollectionModel == "Income" && n.IncomePeriodTicks <= 0)
                    return ValidationResult.Fail(
                        $"scenario.resource_nodes[{i}].income_period_ticks={n.IncomePeriodTicks} must be > 0 " +
                        $"(required when collection_model=Income).");
            }

            // ── Buildings: in-bounds position, slot references a declared PlayerSlot, known building type ──
            ScenarioBuilding[] buildings = m.Buildings ?? Array.Empty<ScenarioBuilding>();
            for (int i = 0; i < buildings.Length; i++)
            {
                ScenarioBuilding b = buildings[i];
                string? e = CheckCoord($"scenario.buildings[{i}].x", b.X, bounds)
                         ?? CheckCoord($"scenario.buildings[{i}].z", b.Z, bounds)
                         // Story 6.5: a pre-placed building on a painted blocked cell is an authoring error (its
                         // spawn/rally point would be impassable) — fail closed, consistent with the start-base check.
                         ?? CheckNotBlocked($"scenario.buildings[{i}]", "building position", b.X, b.Z, painted);
                if (e != null) return ValidationResult.Fail(e);
                if (!declared.Contains(b.Slot))
                    return ValidationResult.Fail(
                        $"scenario.buildings[{i}].slot={b.Slot} references no declared player_slot.");
                // Story 6.8: the retired enum gate. b.Type is accepted as a legacy BuildingType enum name OR an
                // authored building-def id present in the OWNER faction's Buildings (resolved from slotFactionDefs by
                // the building's slot). A truly unknown id — no enum name and no matching faction building-def — fails
                // closed with a message naming it. When no faction defs are threaded (null), this is enum-name-only.
                if (!IsKnownBuildingType(b.Type, OwnerFactionDef(slotFactionDefs, b.Slot)))
                    return ValidationResult.Fail(
                        $"scenario.buildings[{i}].type='{b.Type}' is not a known BuildingType enum name or an authored building id in the owner faction.");
            }

            // ── Units: in-bounds position, slot references a declared PlayerSlot ──
            ScenarioUnit[] units = m.Units ?? Array.Empty<ScenarioUnit>();
            for (int i = 0; i < units.Length; i++)
            {
                ScenarioUnit u = units[i];
                string? e = CheckCoord($"scenario.units[{i}].x", u.X, bounds)
                         ?? CheckCoord($"scenario.units[{i}].z", u.Z, bounds);
                if (e != null) return ValidationResult.Fail(e);
                if (!declared.Contains(u.Slot))
                    return ValidationResult.Fail(
                        $"scenario.units[{i}].slot={u.Slot} references no declared player_slot.");

                // Story 6.5: a pre-placed unit on a PAINTED blocked cell fails closed (same cell domain as the sim).
                string? ube = CheckNotBlocked($"scenario.units[{i}]", "unit position", u.X, u.Z, painted);
                if (ube != null) return ValidationResult.Fail(ube);

                // DW-240: the pre-placed unit's unit_id must RESOLVE in its OWNER faction's roster. The applier does
                // exactly `_slotFactionDefs[(int)faction]?.GetUnit(u.UnitId)` and, on null, logs a warning and
                // `continue`s — the unit silently vanishes from the match. That is how a cross-faction launch shipped
                // an AI opponent with zero workers (Story 11.1's in-engine gate, 2026-07-28): fail-closed at LOAD is
                // what turns "silently missing army" into a located rejection the editor can surface. The predicate
                // here is the applier's predicate, so a PASS provably means every authored unit spawns.
                // Same owner-def amnesty as the Story 6.8 building-type gate: when no faction defs are threaded (the
                // default — every legacy caller/test) or the owner slot resolves to no def, there is nothing to
                // resolve against and the check is a no-op, byte-identical to the pre-DW-240 gate.
                if (!IsKnownUnitId(u.UnitId, OwnerFactionDef(slotFactionDefs, u.Slot)))
                    return ValidationResult.Fail(UnknownUnitIdError($"scenario.units[{i}].unit_id", u.UnitId,
                        OwnerFactionDef(slotFactionDefs, u.Slot), u.Slot));
            }

            // ── Regions (Story 6.4) — fail-closed well-formedness so a malformed/cheat region can never reach the
            //    RegionStore or a unit_in_region scan. NULL Regions (every existing scenario — omit-when-null) ⇒ no
            //    regions to validate ⇒ the pass path is unchanged (no golden/behavior move). Each region needs a
            //    unique, non-empty id; a proper (non-degenerate, non-inverted) rect MinX<MaxX && MinZ<MaxZ; and all
            //    four corners within MapBounds (the existing CheckCoord pattern). declaredRegions feeds the
            //    dangling-region_id check in the triggers loop below (mirrors the timer_expires dangling check). ──
            var declaredRegions = new HashSet<string>(StringComparer.Ordinal);
            ScenarioRegion[] regions = m.Regions ?? Array.Empty<ScenarioRegion>();
            for (int i = 0; i < regions.Length; i++)
            {
                ScenarioRegion rg = regions[i];
                if (rg is null)
                    return ValidationResult.Fail($"scenario.regions[{i}] is null.");
                if (string.IsNullOrEmpty(rg.Id))
                    return ValidationResult.Fail($"scenario.regions[{i}].id must be a non-empty id.");
                if (!declaredRegions.Add(rg.Id))
                    return ValidationResult.Fail($"scenario.regions[{i}].id='{rg.Id}' is a duplicate.");
                string? re = CheckCoord($"scenario.regions[{i}].min_x", rg.MinX, bounds)
                          ?? CheckCoord($"scenario.regions[{i}].min_z", rg.MinZ, bounds)
                          ?? CheckCoord($"scenario.regions[{i}].max_x", rg.MaxX, bounds)
                          ?? CheckCoord($"scenario.regions[{i}].max_z", rg.MaxZ, bounds);
                if (re != null) return ValidationResult.Fail(re);
                if (rg.MinX >= rg.MaxX)
                    return ValidationResult.Fail(
                        $"scenario.regions[{i}] has min_x={rg.MinX} >= max_x={rg.MaxX} (must be min_x < max_x).");
                if (rg.MinZ >= rg.MaxZ)
                    return ValidationResult.Fail(
                        $"scenario.regions[{i}] has min_z={rg.MinZ} >= max_z={rg.MaxZ} (must be min_z < max_z).");
                // Review patch (follow-up): the float min<max checks above are necessary but NOT sufficient. The
                // applier resolves these corners to Fixed (16.16) exactly once and SKIPS any rect that degenerated at
                // that quantization (ScenarioApplier.BuildRegionStore). A rect narrower than the Fixed step (~1/65536
                // world units) is min<max in float yet collapses to min==max after Fixed.FromFloat — it would pass
                // here but be silently dropped from the RegionStore, leaving any unit_in_region trigger that names it
                // dead forever with no diagnostic. Make the validator authoritative in the SAME domain the sim uses:
                // a region that passes here is GUARANTEED to survive BuildRegionStore (validator↔applier agree).
                var fr = new FixedRect(
                    Fixed.FromFloat(rg.MinX), Fixed.FromFloat(rg.MinZ),
                    Fixed.FromFloat(rg.MaxX), Fixed.FromFloat(rg.MaxZ));
                if (fr.MinX >= fr.MaxX || fr.MinZ >= fr.MaxZ)
                    return ValidationResult.Fail(
                        $"scenario.regions[{i}] collapses to a degenerate rect at 16.16 resolution " +
                        $"(min_x={rg.MinX}, max_x={rg.MaxX}, min_z={rg.MinZ}, max_z={rg.MaxZ}); each axis must span " +
                        "at least ~1/65536 world units so the region survives the float→Fixed resolution.");
            }

            // ── Win-condition preset spec (Story 7.11) — fail-closed when a preset is set so an invalid/missing
            //    required param is rejected at LOAD with ONE located error naming the preset and the offending param
            //    (never a runtime crash, never a silently un-winnable match). NULL / None (every pre-7.11 scenario,
            //    and the bare built-in enum path) ⇒ nothing to validate ⇒ the pass path is unchanged. Placement-index
            //    params validate against the AUTHORED placement arrays (the validator sees the model, never runtime
            //    entity counts) — the units/buildings length pattern above. ──
            WinConditionSpec? wspec = m.WinConditionSpec;
            if (wspec != null && wspec.Preset != WinPresetKind.None)
            {
                switch (wspec.Preset)
                {
                    case WinPresetKind.KingOfTheHill:
                        if (string.IsNullOrEmpty(wspec.RegionId) || !declaredRegions.Contains(wspec.RegionId))
                            return ValidationResult.Fail(
                                $"scenario.win_condition_spec.region_id='{wspec.RegionId}' references no declared region " +
                                "(required for the KingOfTheHill preset).");
                        if (wspec.HoldTicks <= 0)
                            return ValidationResult.Fail(
                                $"scenario.win_condition_spec.hold_ticks={wspec.HoldTicks} must be > 0 " +
                                "(required for the KingOfTheHill preset).");
                        break;

                    case WinPresetKind.TimedSurvival:
                    {
                        // Review P4 — the ENGINE faction ceiling (the canonical CheckFactionSlot bound, [0,7]),
                        // checked BEFORE the declared-slot rule so the ceiling error is the one surfaced: the sim
                        // tracks Faction 1-8 (FACTION_COUNT=9 arrays after Story 9.2), so a designated slot ≥ 8 would
                        // pass a declared-only check, be silently skipped by WinConditionSystem.Configure's seeding,
                        // and read as "eliminated" on tick 1 — the wrong faction wins. The win stores widened with
                        // the player_slots ceiling in Story 9.2, so this ceiling now admits slots 4-7.
                        string? sfe = CheckFactionSlot("scenario.win_condition_spec.faction_slot", wspec.FactionSlot);
                        if (sfe != null)
                            return ValidationResult.Fail(sfe + " (the TimedSurvival preset designates an engine-tracked faction).");
                        if (!declared.Contains(wspec.FactionSlot))
                            return ValidationResult.Fail(
                                $"scenario.win_condition_spec.faction_slot={wspec.FactionSlot} references no declared player_slot " +
                                "(required for the TimedSurvival preset).");
                        if (wspec.SurviveTicks <= 0)
                            return ValidationResult.Fail(
                                $"scenario.win_condition_spec.survive_ticks={wspec.SurviveTicks} must be > 0 " +
                                "(required for the TimedSurvival preset).");
                        break;
                    }

                    case WinPresetKind.Assassination:
                    {
                        if (wspec.LeaderUnitIndex < 0 || wspec.LeaderUnitIndex >= units.Length)
                            return ValidationResult.Fail(
                                $"scenario.win_condition_spec.leader_unit_index={wspec.LeaderUnitIndex} is out of range " +
                                $"[0,{units.Length}) (the Assassination preset references the authored units placement list).");
                        // Review P4 — the DESIGNATED placement's owner must be engine-tracked too (same ceiling as
                        // above). Today the units loop's declared-slot rule already caps placements at slot 3, so
                        // this is post-gate defense-in-depth that becomes the load-fatal guard when Story 9.2
                        // relaxes the declared ceiling ahead of the length-5 win stores.
                        string? lfe = CheckFactionSlot(
                            $"scenario.win_condition_spec.leader_unit_index → scenario.units[{wspec.LeaderUnitIndex}].slot",
                            units[wspec.LeaderUnitIndex].Slot);
                        if (lfe != null)
                            return ValidationResult.Fail(lfe + " (the Assassination preset's designated leader must belong to an engine-tracked faction).");
                        break;
                    }

                    case WinPresetKind.LandmarkDestruction:
                    {
                        if (wspec.StructureIndex < 0 || wspec.StructureIndex >= buildings.Length)
                            return ValidationResult.Fail(
                                $"scenario.win_condition_spec.structure_index={wspec.StructureIndex} is out of range " +
                                $"[0,{buildings.Length}) (the LandmarkDestruction preset references the authored buildings placement list).");
                        // Review P4 — same engine-ceiling defense-in-depth as the Assassination case above.
                        string? bfe = CheckFactionSlot(
                            $"scenario.win_condition_spec.structure_index → scenario.buildings[{wspec.StructureIndex}].slot",
                            buildings[wspec.StructureIndex].Slot);
                        if (bfe != null)
                            return ValidationResult.Fail(bfe + " (the LandmarkDestruction preset's designated structure must belong to an engine-tracked faction).");
                        break;
                    }

                    // Review P3 — an UNKNOWN preset value (hand-edited/cheat JSON deserializes any integer into the
                    // enum) must fail closed: it would pass every case-specific rule above and then evaluate
                    // NOTHING at runtime — a silently un-winnable match, exactly what this gate exists to prevent.
                    default:
                        return ValidationResult.Fail(
                            $"scenario.win_condition_spec.preset={(int)wspec.Preset} is not a known win-condition preset " +
                            "(known: None, KingOfTheHill, TimedSurvival, Assassination, LandmarkDestruction).");
                }
            }

            // ── Variables (Story 7.3, AR-39) — fail-closed when present so a hand-edited/cheat declaration (a blank or
            //    duplicate variable name) is rejected at the pre-tick gate. type/scope are closed enums structurally
            //    (an unknown JSON name fails closed at deserialize via JsonStringEnumConverter), so only name
            //    well-formedness/uniqueness is checked here. NULL (every existing scenario) ⇒ nothing to validate ⇒
            //    the pass path is unchanged. Story 7.7: declarations now ALSO fold into the MP start-state
            //    handshake (CanonicalModelHash v8), alongside Triggers/Regions. First-fail located error. ──
            // declaredVarInfo maps each declared variable NAME → its (Type, Scope), so the triggers loop below can
            // fail-closed a set_variable/variable_comparison that references a non-Int or (for a condition read) a
            // TriggerLocal-scoped variable (Story 7.3 P4/P5). An UNDECLARED name is NOT in the map and stays legal
            // (it resolves to a Global/Int/0 default at runtime — the legacy GetVariable/SetVariable semantics).
            var declaredVarInfo = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal);
            if (m.Variables != null)
            {
                var declaredVars = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < m.Variables.Length; i++)
                {
                    ScenarioVariable v = m.Variables[i];
                    if (v is null) return ValidationResult.Fail($"scenario.variables[{i}] is null.");
                    if (string.IsNullOrWhiteSpace(v.Name))
                        return ValidationResult.Fail($"scenario.variables[{i}].name must be a non-empty name.");
                    // Story 7.14 — the reserved 'objective:' namespace bar on authored variable names AND the objective
                    // declaration rules are enforced by the shared ObjectiveResolver.CheckDeclarations rulebook below
                    // (run identically by the LoadScenario backstop — gate/backstop parity, no drift).
                    if (!declaredVars.Add(v.Name))
                        return ValidationResult.Fail($"scenario.variables[{i}].name='{v.Name}' is a duplicate.");
                    // Review (7.3 follow-up): the initial must be representable in the declared type — an Int initial
                    // with a fractional part would silently truncate at load (ScenarioDirector.ScopeInitialRaw →
                    // ToInt: 2.5 → 2), and a Bool initial outside {0,1} would store a non-normalized truthy int.
                    // Fail-closed instead of silently rewriting what the author declared. (Negative whole Ints pass:
                    // their 16.16 fractional bits are zero.)
                    if (v.Type == DslValueType.Int && (v.Initial.Raw & (Fixed.ONE - 1)) != 0)
                        return ValidationResult.Fail(
                            $"scenario.variables[{i}].initial must be a whole number for an Int-typed variable (it would silently truncate).");
                    if (v.Type == DslValueType.Bool && v.Initial != Fixed.Zero && v.Initial != Fixed.One)
                        return ValidationResult.Fail(
                            $"scenario.variables[{i}].initial must be 0 or 1 for a Bool-typed variable.");
                    declaredVarInfo[v.Name] = (v.Type, v.Scope);
                }
            }

            // ── Story 7.14 — objectives + reserved-namespace declaration rules (fail-closed when present): no null
            //    element, unique non-empty ids, non-empty title, no id in the reserved 'objective:' namespace, and no
            //    authored variable name in that namespace. Enforced through the SHARED ObjectiveResolver.CheckDeclarations
            //    rulebook the LoadScenario backstop runs identically (gate/backstop parity — one rulebook, no drift).
            //    NULL (every pre-7.14 scenario) ⇒ nothing to validate. Authoring/presentation data — hash-excluded. ──
            string? objectiveDeclErr = ObjectiveResolver.CheckDeclarations(m);
            if (objectiveDeclErr != null) return ValidationResult.Fail(objectiveDeclErr);

            // ── Story 7.6 — array declaration rules (element_type/capacity present + in range, Global scope only,
            //    no stray array fields on scalars), shared with the LoadScenario backstop via DslLoopGate. ──
            {
                string? declErr = DslLoopGate.CheckDeclarations(m.Variables);
                if (declErr != null) return ValidationResult.Fail(declErr);
            }
            // Declared Array names → (element type, capacity) — consumed by the expression compiler (arr[i] /
            // length(arr)) and the loop gate below.
            Dictionary<string, (DslValueType Elem, int Capacity)> declaredArrayInfo = DslLoopGate.BuildArrayDecls(m.Variables);

            // ── Timers (Story 7.3, AR-39) — fail-closed when present: a blank or duplicate timer name is rejected.
            //    A create_timer/timer_expires that names a variable is inert; a declared timer is a real timer the
            //    dangling-timer check below accepts (declaredTimers is seeded with these names). NULL ⇒ nothing to
            //    validate. Declarations are NOT folded into the MP handshake (Triggers/Regions basis). ──
            var declaredTimerNames = new HashSet<string>(StringComparer.Ordinal);
            if (m.Timers != null)
            {
                for (int i = 0; i < m.Timers.Length; i++)
                {
                    ScenarioTimer tm = m.Timers[i];
                    if (tm is null) return ValidationResult.Fail($"scenario.timers[{i}] is null.");
                    if (string.IsNullOrWhiteSpace(tm.Name))
                        return ValidationResult.Fail($"scenario.timers[{i}].name must be a non-empty name.");
                    if (!declaredTimerNames.Add(tm.Name))
                        return ValidationResult.Fail($"scenario.timers[{i}].name='{tm.Name}' is a duplicate.");
                    // Review (7.3 follow-up): a declared duration must be positive — the load path clamps via
                    // Math.Max(1, SecondsToTicks(seconds)), so a zero/negative declaration would be silently rewritten
                    // into a 1-tick timer firing on the first tick. The create_timer ACTION already requires > 0 at
                    // runtime; the declaration path must not be more permissive than the action it formalizes.
                    if (tm.Seconds <= Fixed.Zero)
                        return ValidationResult.Fail(
                            $"scenario.timers[{i}].seconds must be > 0 (a zero/negative duration would silently clamp to a 1-tick timer).");
                }
            }

            // ── Custom events (Story 7.5, AR-39) — the CLOSED registry, validated fail-closed when present via
            //    the SHARED EventDispatchPlan routine (the exact rules the LoadScenario backstop mirrors): count ≤
            //    MaxCustomEvents; unique non-blank names disjoint from the built-in event-kind set; param count ≤
            //    MaxEventParams, unique identifier names, types ∈ {Int, Fixed, Bool}; allowed-raiser slots pass the
            //    engine faction-slot ceiling (the CheckFactionSlot bound), no duplicates. NULL (every existing
            //    scenario) ⇒ nothing to validate ⇒ the pass path is unchanged. Declarations are sim-semantic
            //    (param slots/types shape every dispatch frame), so they FOLD into the MP handshake like the other
            //    declaration blocks (CanonicalModelHash v10 — the 7.7 Variables/Timers/TriggerGraph basis); the
            //    LIVE pending next-tick queue folds separately (SimChecksum v18). ──
            if (m.CustomEvents != null)
            {
                string? ceErr = EventDispatchPlan.ValidateRegistry(m.CustomEvents, FactionRegistry.PLAYER_COUNT);
                if (ceErr != null)
                    return ValidationResult.Fail($"scenario.{ceErr}");
            }

            // ── Triggers (Story 1.11, AC3 — Decision #1: extend THIS gate rather than add a second validator) ──
            // The as-built path wrote accepted LLM/editor triggers straight into Triggers[] and reached
            // ScenarioDirector WITHOUT any validation; AR-39 now inspects them too, so non-deterministic /
            // crash-inducing trigger content can never reach the tick. Each check below maps to a concrete
            // ScenarioDirector behavior: an out-of-range faction slot does (Faction)(slot+1) → Ore[idx] = an OOB
            // crash; an unknown event/condition/action type or operator silently NEVER fires (a dead trigger that
            // is almost certainly an authoring/LLM error); an unknown building_type silently never matches; a
            // spawn coordinate outside the 16.16 range / map bounds overflows when the spawn delegate converts it
            // to Fixed; a timer_expires that names no create_timer is a dangling no-op. Triggers are validated as
            // INPUT ONLY — they are deliberately NOT folded into SimChecksum / CanonicalModelHash (that is Epic 7).
            // First failure returns a single located error, mirroring the buildings/units loops above.
            TriggerDefinition[] triggers = m.Triggers ?? Array.Empty<TriggerDefinition>();

            // ── Trigger graph channel, parse gate (Story 7.3, AR-39; review follow-up hoisted it ABOVE the flat
            //    passes) — the trigger_graph canonical IR is a SEPARATE execution channel that ScenarioDirector MERGES
            //    with the flat Triggers, so it must clear the SAME pre-tick gate: (1) TriggerGraph.FromJson parses
            //    fail-closed through the closed-registry converter; (2) BuildExecutionOrder runs 7.2's fail-closed
            //    exec-chain CYCLE guard — previously a cyclic authored graph passed validation and threw the same
            //    JsonException inside LoadScenario, i.e. the exact PARTIAL-APPLY crash this gate exists to close.
            //    Both throw located JsonExceptions; System.Text.Json can also surface NotSupportedException on hostile
            //    input, so the catch covers it (a gate whose purpose is containing hand-edited/cheat input must not
            //    crash on it). Story 7.7: the formerly-deferred deep STRUCTURAL validation now runs HERE too —
            //    GraphStructureGate (dup node ids, dangling endpoints, port legality, exec/data forks, stray data
            //    edges, unconsumed-expression compiles) over the WHOLE graph, unconditionally, BEFORE the
            //    execution-order walk (the same shared rulebook ScenarioDirector.LoadScenario runs — parity by
            //    construction). NULL/whitespace (every existing scenario) ⇒ nothing to validate ⇒ pass unchanged. ──
            TriggerGraph? parsedGraph = null;
            // Story 7.6 loop gate AND Story 7.5 dispatch-plan gate both analyze this execution view.
            List<TriggerGraph.TriggerExec>? graphExecs = null;
            if (!string.IsNullOrWhiteSpace(m.TriggerGraphJson))
            {
                try
                {
                    parsedGraph = TriggerGraph.FromJson(m.TriggerGraphJson!);
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
                {
                    return ValidationResult.Fail($"scenario.trigger_graph is malformed: {ex.Message}");
                }

                // Story 7.7 — the whole-graph structural rulebook (unreachable nodes included), shared with the
                // LoadScenario backstop. Runs before BuildExecutionOrder so malformed structure rejects located
                // rather than relying on the walker's tolerances.
                string? structErr = GraphStructureGate.Check(parsedGraph, declaredVarInfo, declaredArrayInfo);
                if (structErr != null)
                    return ValidationResult.Fail($"scenario.trigger_graph: {structErr}");

                try
                {
                    // The 7.2 cycle guard (extended by 7.6 to body/then/else sub-chains rejoining any ancestor),
                    // run AT the gate instead of mid-apply. Story 7.6 keeps the execution view for the loop gate.
                    graphExecs = parsedGraph.BuildExecutionOrder();
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
                {
                    return ValidationResult.Fail($"scenario.trigger_graph is malformed: {ex.Message}");
                }
            }

            // Pass 1: collect every timer name a create_timer action declares, so a timer_expires reference can be
            // checked against it (the only cross-trigger reference; variables default to 0 at read, so they need none).
            // Story 7.3: seed it with the declared scenario timers too, so a timer_expires naming a declared timer
            // (never created by any create_timer action) is accepted rather than flagged dangling.
            var declaredTimers = new HashSet<string>(declaredTimerNames, StringComparer.Ordinal);
            for (int i = 0; i < triggers.Length; i++)
            {
                if (triggers[i] is null) continue; // located-rejected in pass 2; don't NRE while collecting
                TriggerAction[] collectActions = triggers[i].Actions ?? Array.Empty<TriggerAction>();
                for (int a = 0; a < collectActions.Length; a++)
                    if (collectActions[a].Type == "create_timer" && !string.IsNullOrEmpty(collectActions[a].TimerName))
                        declaredTimers.Add(collectActions[a].TimerName!);
            }
            // Review (7.3 follow-up): the graph channel participates in the SAME cross-channel timer namespace (the
            // director merges both channels into one execution graph), so its create_timer actions seed the union too —
            // otherwise a flat timer_expires referencing a graph-created timer is false-flagged as dangling.
            if (parsedGraph != null)
                foreach (NodeBase gn in parsedGraph.Nodes)
                    if (gn is ActionNode gCollect && gCollect.Kind == "create_timer" && !string.IsNullOrEmpty(gCollect.TimerName))
                        declaredTimers.Add(gCollect.TimerName!);

            // Pass 2: validate each trigger's events, conditions, and actions in declaration order.
            for (int i = 0; i < triggers.Length; i++)
            {
                TriggerDefinition t = triggers[i];
                if (t is null) return ValidationResult.Fail($"scenario.triggers[{i}] is null.");

                TriggerEvent[] events = t.Events ?? Array.Empty<TriggerEvent>();
                for (int j = 0; j < events.Length; j++)
                {
                    TriggerEvent e = events[j];
                    string ep = $"scenario.triggers[{i}].events[{j}]";
                    if (!InSet(_triggerEventTypes, e.Type))
                        return ValidationResult.Fail($"{ep}.type='{e.Type}' is not a known trigger event type.");
                    string? fe = CheckFactionSlot($"{ep}.faction", e.Faction);
                    if (fe != null) return ValidationResult.Fail(fe);
                    // DW-170 — FACTION-QUALIFIED building refs. building_type is accepted as a legacy BuildingType
                    // enum name OR an authored building-def id present in the faction this event already names
                    // (its `faction` slot IS the qualifier — a building_completed occurrence only ever matches its
                    // own builder slot), resolved through the SAME OwnerFactionDef/GetBuilding path as the
                    // scenario-buildings gate above. With no faction defs threaded this stays enum-name-only.
                    if (!string.IsNullOrEmpty(e.BuildingType)
                        && !IsKnownBuildingType(e.BuildingType, OwnerFactionDef(slotFactionDefs, e.Faction)))
                        return ValidationResult.Fail(
                            $"{ep}.building_type='{e.BuildingType}' is not a known BuildingType enum name or an authored building id in faction slot {e.Faction}.");
                    if (!InSet(_operators, e.Operator))
                        return ValidationResult.Fail($"{ep}.operator='{e.Operator}' is not a known comparison operator.");
                    if (e.Type == "timer_expires" && !string.IsNullOrEmpty(e.TimerName) && !declaredTimers.Contains(e.TimerName))
                        return ValidationResult.Fail(
                            $"{ep}.timer_name='{e.TimerName}' references no timer created by any create_timer action.");
                }

                TriggerCondition[] conds = t.Conditions ?? Array.Empty<TriggerCondition>();
                for (int j = 0; j < conds.Length; j++)
                {
                    TriggerCondition c = conds[j];
                    string cp = $"scenario.triggers[{i}].conditions[{j}]";
                    if (!InSet(_conditionTypes, c.Type))
                        return ValidationResult.Fail($"{cp}.type='{c.Type}' is not a known trigger condition type.");
                    string? fe = CheckFactionSlot($"{cp}.faction", c.Faction);
                    if (fe != null) return ValidationResult.Fail(fe);
                    // DW-170 — same faction-qualified resolution as the event arm: a building_exists condition
                    // scans ONLY the faction it names, so that slot is the qualifier for an authored building id.
                    if (!string.IsNullOrEmpty(c.BuildingType)
                        && !IsKnownBuildingType(c.BuildingType, OwnerFactionDef(slotFactionDefs, c.Faction)))
                        return ValidationResult.Fail(
                            $"{cp}.building_type='{c.BuildingType}' is not a known BuildingType enum name or an authored building id in faction slot {c.Faction}.");
                    if (!InSet(_operators, c.Operator))
                        return ValidationResult.Fail($"{cp}.operator='{c.Operator}' is not a known comparison operator.");
                    // Story 7.3 (P4/P5): a variable_comparison READS a variable BEFORE the trigger-local scope is
                    // entered, so a TriggerLocal-scoped var would read 0 (it is action-write-scratch only) — reject it
                    // fail-closed. It must also be Int-typed (non-Int typed read is Story 7.4). Only DECLARED names
                    // are checked; an undeclared name stays legal (Global/Int/0 default). Its faction (the PerPlayer
                    // slot selector) is already range-gated by the canonical CheckFactionSlot above — the engine
                    // ceiling [0,7] is a strict subset of the DSL slot range [0,DslVarTable.PlayerSlots), so a
                    // separate PlayerSlots check here would be unreachable dead code (review follow-up removed it).
                    if (c.Type == "variable_comparison" && !string.IsNullOrEmpty(c.Variable)
                        && declaredVarInfo.TryGetValue(c.Variable, out var cInfo))
                    {
                        if (cInfo.Scope == VarScope.TriggerLocal)
                            return ValidationResult.Fail(
                                $"{cp}.variable='{c.Variable}' is TriggerLocal-scoped and cannot be read in a condition " +
                                $"(conditions evaluate before the trigger-local scope is entered — it would read 0).");
                        if (cInfo.Type != DslValueType.Int)
                            return ValidationResult.Fail(
                                $"{cp}.variable='{c.Variable}' is {cInfo.Type}-typed; a variable_comparison reads only Int-typed variables (7.3).");
                    }
                    // Story 6.4: a unit_in_region condition must name a DECLARED region (dangling-ref fail-closed,
                    // mirroring the timer_expires dangling check) — an undefined/empty region_id would silently
                    // never match at runtime, an almost-certain authoring/LLM error.
                    if (c.Type == "unit_in_region")
                    {
                        // Review patch: also fail-closed on an out-of-range faction slot here — the unit_in_region
                        // scan does (Faction)(c.Faction + 1) and compares live entities against it. Reuses the
                        // canonical trigger-faction bound (CheckFactionSlot, engine ceiling Faction.Player8) the
                        // general condition check above already applies, co-located with the region check as
                        // belt-and-suspenders fail-closed defense.
                        string? rfe = CheckFactionSlot($"{cp}.faction", c.Faction);
                        if (rfe != null) return ValidationResult.Fail(rfe);
                        if (!declaredRegions.Contains(c.RegionId ?? ""))
                            return ValidationResult.Fail(
                                $"{cp}.region_id='{c.RegionId}' references no declared region.");
                    }
                }

                TriggerAction[] actions = t.Actions ?? Array.Empty<TriggerAction>();
                for (int j = 0; j < actions.Length; j++)
                {
                    TriggerAction a = actions[j];
                    string ap = $"scenario.triggers[{i}].actions[{j}]";
                    if (!InSet(_actionTypes, a.Type))
                        return ValidationResult.Fail($"{ap}.type='{a.Type}' is not a known trigger action type.");
                    string? fe = CheckFactionSlot($"{ap}.faction", a.Faction);
                    if (fe != null) return ValidationResult.Fail(fe);
                    // Story 7.3 (P5): a set_variable WRITES a variable — a declared referent must be Int-typed
                    // (non-Int typed write is Story 7.4). TriggerLocal IS allowed here (set_variable is the
                    // action-write-scratch path). Only DECLARED names are checked (undeclared → Global/Int default).
                    // Its faction (the PerPlayer slot selector) is already range-gated by CheckFactionSlot above —
                    // the engine ceiling [0,7] ⊂ [0,DslVarTable.PlayerSlots), so a separate PlayerSlots check here
                    // would be unreachable dead code (review follow-up removed it).
                    if (a.Type == "set_variable" && !string.IsNullOrEmpty(a.Variable)
                        && declaredVarInfo.TryGetValue(a.Variable, out var aInfo))
                    {
                        if (aInfo.Type != DslValueType.Int)
                            return ValidationResult.Fail(
                                $"{ap}.variable='{a.Variable}' is {aInfo.Type}-typed; set_variable writes only Int-typed variables (7.3).");
                    }
                    if (a.Type == "spawn_unit")
                    {
                        // Story 7.6 — the LOUD spawn-count gate: a count beyond the named structural cap rejects
                        // at load instead of silently truncating at runtime (the Math.Min(count, 50) literal is
                        // retired; the runtime clamp is now EffectCaps.MaxSpawnCount, a seatbelt only). Review:
                        // the low side rejects too — a count < 1 is a spawn that silently does nothing.
                        if (a.Count < 1 || a.Count > EffectCaps.MaxSpawnCount)
                            return ValidationResult.Fail(
                                $"{ap}.count={a.Count} is out of range 1..EffectCaps.MaxSpawnCount={EffectCaps.MaxSpawnCount}.");
                        // Story 7.1: a.X/a.Z are now Fixed. Finiteness and the 16.16 range are guaranteed
                        // structurally by the Fixed type and enforced at the JSON boundary by FixedJsonConverter,
                        // so the former range/NaN branch is unreachable here; the remaining semantic gate is
                        // map_bounds — a spawn coordinate outside ±map_bounds is still an authoring error.
                        string? ce = CheckCoordFixed($"{ap}.x", a.X, bounds) ?? CheckCoordFixed($"{ap}.z", a.Z, bounds);
                        if (ce != null) return ValidationResult.Fail(ce);
                        // Story 6.5: a spawn_unit trigger that would place a unit on a PAINTED blocked cell fails
                        // closed (same cell domain as the sim) — a spawned unit could never legally occupy it.
                        string? sbe = CheckNotBlocked(ap, "spawn_unit position", a.X, a.Z, painted);
                        if (sbe != null) return ValidationResult.Fail(sbe);
                        // DW-240: the spawned unit_id must RESOLVE in the SPAWNING faction's roster — the exact
                        // predicate ScenarioDelegateBinder.OnSpawnUnit applies (`SlotFactionDefs[slot+1]?.GetUnit(id)`,
                        // warn + return on null). Pre-DW-240 an unknown id was a trigger that fired forever and spawned
                        // nothing, diagnosable only from the runtime log. Owner-def amnesty as above: no threaded def
                        // for this faction slot ⇒ nothing to resolve against ⇒ no-op.
                        if (!IsKnownUnitId(a.UnitId, OwnerFactionDef(slotFactionDefs, a.Faction)))
                            return ValidationResult.Fail(UnknownUnitIdError($"{ap}.unit_id", a.UnitId,
                                OwnerFactionDef(slotFactionDefs, a.Faction), a.Faction));
                    }
                }
            }

            // ── Trigger graph channel, semantic gate (Story 7.3, AR-39; parse + cycle guard ran ABOVE Pass 1) —
            //    the graph channel MERGES into the same execution walk as the flat Triggers, so its nodes get the
            //    SAME per-leaf rulebook the flat loops above apply (review follow-up: previously the graph channel
            //    escaped every semantic check — a graph condition could read a TriggerLocal/Fixed-typed variable or
            //    index an out-of-range faction that the flat channel rejects):
            //      • EffectBounds.Validate on every run_effect embed — the SAME load-time bounds gate every other
            //        effect source gets (AbilityValidator/ItemDefinitionValidator), instead of silent runtime
            //        truncation.
            //      • CheckFactionSlot on every event/condition/action node faction (the engine-ceiling OOB crash
            //        class is identical in both channels).
            //      • The P4/P5 declared-variable rules (TriggerLocal not readable in a condition; Int-only leaves).
            //      • The dangling-timer check for graph timer_expires events, against the cross-channel union.
            //    Deep STRUCTURAL graph validation (dangling/forked/dup-id edges, port legality, unconsumed
            //    expression compiles) already ran ABOVE via GraphStructureGate (Story 7.7 closed the deferral). ──
            if (parsedGraph != null)
            {
                // ── Story 7.5 — the SHARED custom-event analysis (the exact routine LoadScenario's backstop
                //    runs): registry re-check, custom_event/raise_event usage rules (declared names, raiser
                //    membership, single-subscription), raise-arg edge arity/type/wire + compile (raise args may
                //    read declared arrays too, hence declaredArrayInfo), and the same-tick DAG proof + EventBounds
                //    caps (fan-out/depth/transitive ops — errors name the constant). It also yields each trigger's
                //    event-parameter map, which the 7.4 expression compile loop below threads into TryCompile so a
                //    handler's condition/value expressions may read event.<param>. ──
                if (!EventDispatchPlan.TryBuild(m.CustomEvents, parsedGraph, graphExecs!, declaredVarInfo,
                        declaredArrayInfo, maxRaiserSlotExclusive: FactionRegistry.PLAYER_COUNT,
                        out EventDispatchPlan? evPlan, out string? evErr))
                    return ValidationResult.Fail($"scenario.trigger_graph: {evErr}");

                // Story 7.4 — id lookup + the set of set_variable actions fed by a value-in expression edge. A
                // fed action's declared target may be Int/Fixed/Bool (the type-equality check happens in the
                // compile pass below); the literal path keeps the 7.3 Int-only rule.
                var graphById = new Dictionary<int, NodeBase>(parsedGraph.Nodes.Count);
                foreach (NodeBase gn in parsedGraph.Nodes)
                    graphById[gn.Id] = gn;
                var exprValueTargets = new HashSet<int>();
                foreach (DataEdge de in parsedGraph.DataEdges)
                    if (de.DstPort == TriggerGraph.ActionValueInPort
                        && graphById.TryGetValue(de.Src, out NodeBase? vSrc) && ExprCompiler.IsExprNode(vSrc)
                        && graphById.TryGetValue(de.Dst, out NodeBase? vDst) && vDst is ActionNode)
                        exprValueTargets.Add(de.Dst);

                foreach (NodeBase node in parsedGraph.Nodes)
                {
                    switch (node)
                    {
                        case EffectActionNode effectNode:
                        {
                            EffectBoundsResult effBounds = EffectBounds.Validate(effectNode.Effect);
                            if (!effBounds.IsValid)
                                return ValidationResult.Fail(
                                    $"scenario.trigger_graph run_effect node {node.Id}: {effBounds.Error}");
                            break;
                        }
                        case EventNode ge:
                        {
                            string gp = $"scenario.trigger_graph event node {ge.Id}";
                            string? fe = CheckFactionSlot($"{gp}.faction", ge.Faction);
                            if (fe != null) return ValidationResult.Fail(fe);
                            if (ge.Kind == "timer_expires" && !string.IsNullOrEmpty(ge.TimerName)
                                && !declaredTimers.Contains(ge.TimerName))
                                return ValidationResult.Fail(
                                    $"{gp}.timer_name='{ge.TimerName}' references no declared timer or create_timer action (either channel).");
                            break;
                        }
                        case ConditionNode gc:
                        {
                            string gp = $"scenario.trigger_graph condition node {gc.Id}";
                            string? fe = CheckFactionSlot($"{gp}.faction", gc.Faction);
                            if (fe != null) return ValidationResult.Fail(fe);
                            if (gc.Kind == "variable_comparison" && !string.IsNullOrEmpty(gc.Variable)
                                && declaredVarInfo.TryGetValue(gc.Variable, out var gcInfo))
                            {
                                if (gcInfo.Scope == VarScope.TriggerLocal)
                                    return ValidationResult.Fail(
                                        $"{gp}.variable='{gc.Variable}' is TriggerLocal-scoped and cannot be read in a condition " +
                                        $"(conditions evaluate before the trigger-local scope is entered — it would read 0).");
                                if (gcInfo.Type != DslValueType.Int)
                                    return ValidationResult.Fail(
                                        $"{gp}.variable='{gc.Variable}' is {gcInfo.Type}-typed; a variable_comparison reads only Int-typed variables (7.3).");
                            }
                            break;
                        }
                        case ActionNode ga:
                        {
                            string gp = $"scenario.trigger_graph action node {ga.Id}";
                            string? fe = CheckFactionSlot($"{gp}.faction", ga.Faction);
                            if (fe != null) return ValidationResult.Fail(fe);
                            // Story 7.6 — the graph channel gets the SAME loud spawn-count gate as the flat pass
                            // (review: including the low side — a count < 1 is a spawn that silently does nothing).
                            if (ga.Kind == "spawn_unit" && (ga.Count < 1 || ga.Count > EffectCaps.MaxSpawnCount))
                                return ValidationResult.Fail(
                                    $"{gp}.count={ga.Count} is out of range 1..EffectCaps.MaxSpawnCount={EffectCaps.MaxSpawnCount}.");
                            // DW-240 — the graph channel gets the SAME unknown-unit_id gate as the flat pass (both
                            // channels merge into one execution walk and hit the identical OnSpawnUnit resolution).
                            if (ga.Kind == "spawn_unit"
                                && !IsKnownUnitId(ga.UnitId, OwnerFactionDef(slotFactionDefs, ga.Faction)))
                                return ValidationResult.Fail(UnknownUnitIdError($"{gp}.unit_id", ga.UnitId,
                                    OwnerFactionDef(slotFactionDefs, ga.Faction), ga.Faction));
                            // Story 7.4 widening: the Int-only rule now governs only the LITERAL path — an action
                            // fed by a value-in expression edge may target Int/Fixed/Bool (its type equality is
                            // enforced by the expression compile pass below).
                            if (ga.Kind == "set_variable" && !string.IsNullOrEmpty(ga.Variable)
                                && declaredVarInfo.TryGetValue(ga.Variable, out var gaInfo)
                                && gaInfo.Type != DslValueType.Int
                                && !exprValueTargets.Contains(ga.Id))
                                return ValidationResult.Fail(
                                    $"{gp}.variable='{ga.Variable}' is {gaInfo.Type}-typed; set_variable writes only Int-typed variables (7.3).");
                            break;
                        }

                        case ForEachNode gfe:
                        {
                            // Story 7.6 — the engine-ceiling faction policy check (validator-owned, the 7.4
                            // precedent); -1 = "any faction" is legal. The structural/semantic loop rules run in
                            // the shared DslLoopGate pass below.
                            if (gfe.Faction != -1)
                            {
                                string? fe = CheckFactionSlot($"scenario.trigger_graph for_each node {gfe.Id}.faction", gfe.Faction);
                                if (fe != null) return ValidationResult.Fail(fe);
                            }
                            break;
                        }

                        case ForEachBatchedNode gfb:
                        {
                            if (gfb.Faction != -1)
                            {
                                string? fe = CheckFactionSlot($"scenario.trigger_graph for_each_batched node {gfb.Id}.faction", gfb.Faction);
                                if (fe != null) return ValidationResult.Fail(fe);
                            }
                            break;
                        }

                        case ExprVarNode gv:
                        {
                            // Story 7.4 — a slotted per-player read (name[k]) passes the SAME engine-ceiling gate
                            // every other graph-node faction gets; a bare read carries -1 (no slot) and skips it.
                            if (gv.Faction >= 0)
                            {
                                string? fe = CheckFactionSlot($"scenario.trigger_graph expr_var node {gv.Id}.faction", gv.Faction);
                                if (fe != null) return ValidationResult.Fail(fe);
                            }
                            break;
                        }
                    }
                }

                // ── Story 7.4 — expression consumer edges: run the ExprCompiler (the full load-time rulebook:
                //    strict typing, literal-zero divisor, undeclared/mis-scoped variables, TriggerLocal-in-condition,
                //    ref-typed reads, missing/forked operand edges, wire-type = inferred type, ExprBounds caps) for
                //    every expression root actually CONSUMED by a trigger condition-in port or a set_variable
                //    value-in port. Unconsumed expression subgraphs were already compile-checked by
                //    GraphStructureGate above (Story 7.7 — no orphan semantic skip). Edges iterate in
                //    canonical tuple order so the first-fail is deterministic. ──
                List<DataEdge> sortedGraphData = parsedGraph.DataEdges.OrderBy(e => e).ToList();
                var seenValueInPorts = new HashSet<int>();
                foreach (DataEdge de in sortedGraphData)
                {
                    bool srcIsExpr = graphById.TryGetValue(de.Src, out NodeBase? src) && ExprCompiler.IsExprNode(src);
                    graphById.TryGetValue(de.Dst, out NodeBase? dst);

                    // Review patch — a value-in edge is CONSUMED by the runtime, so BOTH of its halves are gated
                    // here (not left to 7.7 "unconsumed" scope): a run_effect dst would surface through
                    // BuildExecutionOrder and throw mid-apply in LoadScenario; a non-expression src is silently
                    // ignored at runtime (the literal Value would win against the authored wiring).
                    if (de.DstPort == TriggerGraph.ActionValueInPort && dst is EffectActionNode)
                        return ValidationResult.Fail(
                            $"scenario.trigger_graph run_effect node {dst.Id}: a value-in edge is not allowed on run_effect (only a set_variable action takes a value expression).");
                    if (de.DstPort == TriggerGraph.ActionValueInPort && dst is ActionNode && !srcIsExpr)
                        return ValidationResult.Fail(
                            $"scenario.trigger_graph action node {dst.Id}: the value-in edge source (node {de.Src}) is not an expression node.");

                    if (!srcIsExpr || dst is null) continue;

                    // Review (7.4 pass 2): a CONSUMED expression edge must leave its src on ExprDataOutPort (= 0) —
                    // the only port expression nodes emit on. A stray src_port previously compiled, validated, and
                    // round-tripped untouched (a silently-tolerated non-canonical encoding, against the fail-closed
                    // posture this gate applies everywhere else). The compiler rejects the same on operand edges.
                    bool consumedHere = (dst is TriggerNode && de.DstPort == TriggerGraph.TriggerConditionInPort)
                                     || (dst is ActionNode && de.DstPort == TriggerGraph.ActionValueInPort);
                    if (consumedHere && de.SrcPort != TriggerGraph.ExprDataOutPort)
                        return ValidationResult.Fail(
                            $"scenario.trigger_graph expr node {de.Src}: the consumer edge into node {de.Dst} leaves src port {de.SrcPort}; expression nodes emit only on port {TriggerGraph.ExprDataOutPort}.");

                    if (dst is TriggerNode && de.DstPort == TriggerGraph.TriggerConditionInPort)
                    {
                        // Story 7.5: the trigger's event-parameter map (from the dispatch plan) rides along so a
                        // handler condition may read event.<param>; non-handlers get a null map (event.* rejects).
                        if (!ExprCompiler.TryCompile(parsedGraph, de.Src, declaredVarInfo, inCondition: true,
                                eventParams: evPlan!.ParamMapFor(dst.Id), out ExprProgram? cp, out string? cErr,
                                declaredArrayInfo))
                            return ValidationResult.Fail($"scenario.trigger_graph: {cErr}");
                        if (cp!.ResultType != DslValueType.Bool)
                            return ValidationResult.Fail(
                                $"scenario.trigger_graph expr node {de.Src}: a trigger condition expression must evaluate to Bool, got {cp.ResultType}.");
                        if (de.Wire != DataWireType.Boolean)
                            return ValidationResult.Fail(
                                $"scenario.trigger_graph expr node {de.Src}: the condition-in edge must carry the Boolean wire, got '{de.Wire}'.");
                    }
                    else if (dst is ActionNode act && de.DstPort == TriggerGraph.ActionValueInPort)
                    {
                        // Story 7.6 widening: array_push/array_set ALSO take a value-in expression edge (their
                        // element typing is enforced by the shared DslLoopGate pass below).
                        if (act.Kind != "set_variable" && !NodeKinds.IsArrayActionKind(act.Kind))
                            return ValidationResult.Fail(
                                $"scenario.trigger_graph action node {act.Id}: a value-in expression edge is only allowed on a set_variable, array_push, or array_set action (kind '{act.Kind}').");
                        if (!seenValueInPorts.Add(act.Id))
                            return ValidationResult.Fail(
                                $"scenario.trigger_graph action node {act.Id}: multiple value-in expression edges (forked; exactly one allowed).");
                        if (NodeKinds.IsArrayActionKind(act.Kind))
                            continue; // typing/required-edge rules run in DslLoopGate.CheckGraph below
                        if (string.IsNullOrEmpty(act.Variable))
                            return ValidationResult.Fail(
                                $"scenario.trigger_graph action node {act.Id}: a set_variable with a value expression needs a target variable.");
                        // Story 7.5: same event-parameter map threading as the condition compile above (keyed by
                        // the consuming action's id — the plan maps every chained action of a handler trigger).
                        if (!ExprCompiler.TryCompile(parsedGraph, de.Src, declaredVarInfo, inCondition: false,
                                eventParams: evPlan!.ParamMapFor(act.Id), out ExprProgram? vp, out string? vErr,
                                declaredArrayInfo))
                            return ValidationResult.Fail($"scenario.trigger_graph: {vErr}");
                        DslValueType target = declaredVarInfo.TryGetValue(act.Variable!, out var tInfo) ? tInfo.Type : DslValueType.Int;
                        if (target != DslValueType.Int && target != DslValueType.Fixed && target != DslValueType.Bool)
                            return ValidationResult.Fail(
                                $"scenario.trigger_graph action node {act.Id}: set_variable target '{act.Variable}' is {target}-typed; expression assignment targets Int/Fixed/Bool variables only.");
                        if (vp!.ResultType != target)
                            return ValidationResult.Fail(
                                $"scenario.trigger_graph expr node {de.Src}: value expression result type {vp.ResultType} does not match target variable '{act.Variable}' ({target}).");
                        if (de.Wire != ExprCompiler.WireOf(target))
                            return ValidationResult.Fail(
                                $"scenario.trigger_graph action node {act.Id}: the value-in edge wire '{de.Wire}' does not match target variable '{act.Variable}' ({target}).");
                    }
                    // Other destinations/ports: already gated structurally by GraphStructureGate (port legality),
                    // and unconsumed expression roots were compile-checked there too (Story 7.7).
                }

                // ── Story 7.6 — the shared loop/branch/array-action gate + static cap-product cost check (one
                //    implementation, DslLoopGate, also run by the LoadScenario backstop — parity by construction).
                //    Uses the execution view captured above (graphExecs is non-null whenever parsedGraph is). ──
                // Story 7.14 — the set of MUTABLE objective ids an objective action may target: the authored,
                // reserved-var-backed objectives ONLY. The synthesized default (a preset-only scenario with no authored
                // objectives) is presentation-only — it declares no reserved var, so at runtime an action targeting it
                // is a silent no-op. Excluding it here makes such an action a located LOAD reject instead of a dead
                // action a designer gets no diagnostic for (matches the runtime _objectiveVarNameById set exactly).
                var mutableObjectiveIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (ResolvedObjective ro in ObjectiveResolver.Resolve(m))
                    if (ro.HasReservedVar) mutableObjectiveIds.Add(ro.Id);
                string? loopErr = DslLoopGate.CheckGraph(parsedGraph, graphExecs!, declaredVarInfo, declaredArrayInfo,
                    id => declaredRegions.Contains(id),
                    id => mutableObjectiveIds.Contains(id));
                if (loopErr != null)
                    return ValidationResult.Fail($"scenario.trigger_graph: {loopErr}");

                // ── Story 7.13 — the closed-vocab selectors that need SCENARIO context to validate (the fixed-enum
                //    tag/category/resource selectors reject in ExprCompiler; camera + region selectors need the
                //    scenario's declared cameras/regions). A located reject on an unknown name; runtime is total. ──
                var graphCameras = new HashSet<string>(StringComparer.Ordinal);
                foreach (ScenarioCamera cam in m.Cameras ?? Array.Empty<ScenarioCamera>())
                    if (cam != null && !string.IsNullOrEmpty(cam.Name)) graphCameras.Add(cam.Name);
                foreach (NodeBase gn in parsedGraph.Nodes)
                {
                    if (gn is MoveCameraNode mcn && !graphCameras.Contains(mcn.CameraName))
                        return ValidationResult.Fail(
                            $"scenario.trigger_graph: move_camera node {mcn.Id} camera_name='{mcn.CameraName}' references no declared camera.");
                    if (gn is ExprCallNode ecn && ecn.Fn == "region_unit_count" && !declaredRegions.Contains(ecn.Selector))
                        return ValidationResult.Fail(
                            $"scenario.trigger_graph: region_unit_count node {ecn.Id} selector='{ecn.Selector}' references no declared region.");
                    // Story 7.14 — cross-reference each objective action's target id against the effective objectives.
                    if (gn is ShowObjectiveNode son && !mutableObjectiveIds.Contains(son.ObjectiveId))
                        return ValidationResult.Fail(
                            $"scenario.trigger_graph: show_objective node {son.Id} objective_id='{son.ObjectiveId}' references no declared objective.");
                    if (gn is CompleteObjectiveNode con && !mutableObjectiveIds.Contains(con.ObjectiveId))
                        return ValidationResult.Fail(
                            $"scenario.trigger_graph: complete_objective node {con.Id} objective_id='{con.ObjectiveId}' references no declared objective.");
                    if (gn is FailObjectiveNode fon && !mutableObjectiveIds.Contains(fon.ObjectiveId))
                        return ValidationResult.Fail(
                            $"scenario.trigger_graph: fail_objective node {fon.Id} objective_id='{fon.ObjectiveId}' references no declared objective.");
                }

                // ── Story 7.5 (merge hardening) — the strict compiles DslLoopGate's static pass SKIPS when a
                //    subgraph reads event.<param> (it holds no event-parameter maps): every branch condition and
                //    array-action value/index root recompiles here WITH the owning trigger's map, re-asserting the
                //    Bool / element-type / Int-index rules — the exact checks the LoadScenario backstop applies in
                //    CompileItems, so a gate PASS still guarantees a loadable scenario (the 7.7 invariant). Roots
                //    free of event reads recompile to the identical verdict DslLoopGate already gave. ──
                for (int ti = 0; ti < graphExecs!.Count; ti++)
                {
                    string? evRootErr = CheckEventParamRoots(parsedGraph, graphExecs[ti],
                        evPlan!.ParamMapFor(graphExecs[ti].Trigger.Id), declaredVarInfo, declaredArrayInfo);
                    if (evRootErr != null)
                        return ValidationResult.Fail($"scenario.trigger_graph: {evRootErr}");
                }
            }

            // ── Custom UI widget tree (Story 7.8, AR-32) — fail-closed when present: the SHARED CustomUiGate
            //    rulebook (widget/depth/list-row caps naming the constant, duplicate ids, anchor validity, and every
            //    {variable} bind resolving + type-matching the declared-variable registry). The SAME implementation
            //    the ScenarioDirector.LoadScenario backstop runs (parity by construction, the GraphStructureGate
            //    precedent). Reuses declaredVarInfo/declaredArrayInfo built above. NULL (every existing scenario) ⇒
            //    nothing to validate ⇒ the pass path is unchanged. First-fail located error. ──
            if (m.CustomUi != null)
            {
                // Story 7.9 — pass the declared custom-event registry so the gate can resolve a Button's raise target,
                // enforce the ≤ MaxButtonEventParams wire budget, and type-match its authored args.
                string? uiErr = CustomUiGate.Check(m.CustomUi, declaredVarInfo, declaredArrayInfo, m.CustomEvents);
                if (uiErr != null) return ValidationResult.Fail(uiErr);
            }

            // AR-13 (forbidden-until-SimRng) — RESERVED, intentionally NOT implemented here. SimRng shipped in
            // Story 1.5 and is unconditionally present (EntityWorld.Rng, non-null, no flag), and no effect/ability
            // schema exists yet (Epic 2). The rule's failing condition ("SimRng absent") can never occur and there
            // is no random-effect model to inspect, so adding a presence check would be unreachable scaffolding.
            // This validator OWNS the rule's home and discharges AR-13 by reservation; the mature form ("a random
            // effect is valid only if it draws from world.Rng") is a static check over the effect graph, enforced
            // by Epic 2's effect-validator (Story 2.3) — the first point an effect schema exists.

            // ── Persistence manifest (Story 3.8, AR-12 / AR-39 / D-3) — the SAME rule core the editor Save uses, so a
            // hand-edited/cheat manifest (an unknown, mid-game-only, or duplicate attribute key reachable only by
            // editing the scenario JSON directly) is rejected fail-closed at the pre-tick D3 gate too. A null manifest
            // (every existing scenario) ⇒ Valid ⇒ the pass path is unchanged (no golden/behavior move). Multi-error
            // located list ⇒ first-fail here (mirroring the buildings/units/triggers loops); the message is already
            // self-describing ("persistence_manifest.attributes.<key>: <reason>"). Authoring-only — deliberately NOT
            // folded into any checksum/hash (D-2). ──
            // Guard on non-null so the common case (every existing scenario has no manifest) allocates no validator on
            // this pre-tick path; only a present manifest is validated (and only an ENABLED one has rules to fail).
            if (m.PersistenceManifest != null)
            {
                var mr = new PersistenceManifestValidator().Validate(m.PersistenceManifest);
                if (!mr.Ok) return ValidationResult.Fail(mr.Errors[0].Message);
            }

            // ── Revival rule (Story 3.14, AR-39) — fail-closed when present so a hand-edited/cheat rule (non-finite or
            // out-of-range cost/time, or a revive_hp_fraction outside (0,1] that would spawn a 0-HP dead-on-arrival or
            // over-max hero) is rejected at the editor Save AND the pre-tick gate. A null rule (every existing scenario)
            // ⇒ use RevivalRule.Default ⇒ no rule to validate ⇒ the pass path is unchanged (no golden/behavior move).
            // Costs/times must be finite & in [0, Range) (they quantize to Fixed); the HP fraction must be finite & in
            // (0, 1] (Fixed-safe, and a positive spawn HP). Authoring-only — NOT folded into any checksum/hash. ──
            if (m.RevivalRule != null)
            {
                RevivalRule r = m.RevivalRule;
                string? e =
                       CheckNonNeg("scenario.revival_rule.cost_ore_base", r.CostOreBase)
                    ?? CheckNonNeg("scenario.revival_rule.cost_ore_per_level", r.CostOrePerLevel)
                    ?? CheckNonNeg("scenario.revival_rule.cost_crystal_base", r.CostCrystalBase)
                    ?? CheckNonNeg("scenario.revival_rule.cost_crystal_per_level", r.CostCrystalPerLevel)
                    ?? CheckNonNeg("scenario.revival_rule.time_base_seconds", r.TimeBaseSeconds)
                    ?? CheckNonNeg("scenario.revival_rule.time_per_level_seconds", r.TimePerLevelSeconds);
                if (e != null) return ValidationResult.Fail(e);
                // The FIELDS are non-negative, but the COMPOSED curve base + perLevel × level is evaluated at the hero's
                // level (up to MaxRevivableLevel) and quantizes to Fixed — so the curve AT MAX LEVEL must stay in the
                // 16.16 range, else the runtime cost/timer overflows and wraps negative (free-money / instant-revive, the
                // Story 3.13 overflow class the per-field non-neg check does NOT catch).
                if (RevivalCurveOverflows(r.CostOreBase, r.CostOrePerLevel))
                    return ValidationResult.Fail($"scenario.revival_rule ore cost (base {r.CostOreBase} + {r.CostOrePerLevel}/level) exceeds the 16.16 range [0, {Range}) at level {MaxRevivableLevel}.");
                if (RevivalCurveOverflows(r.CostCrystalBase, r.CostCrystalPerLevel))
                    return ValidationResult.Fail($"scenario.revival_rule crystal cost (base {r.CostCrystalBase} + {r.CostCrystalPerLevel}/level) exceeds the 16.16 range [0, {Range}) at level {MaxRevivableLevel}.");
                if ((double)r.TimeBaseSeconds + (double)r.TimePerLevelSeconds * MaxRevivableLevel >= Range)
                    return ValidationResult.Fail($"scenario.revival_rule time (base {r.TimeBaseSeconds} + {r.TimePerLevelSeconds}/level) exceeds the 16.16 range [0, {Range}) at level {MaxRevivableLevel}.");
                // Reject a fraction that is positive-but-quantizes-to-Fixed.Zero (e.g. 1e-5) — the pre-quantization (0,1]
                // check alone would let it through and respawn a 0-HP dead-on-arrival hero (validate the QUANTIZED value).
                if (!Finite(r.ReviveHpFraction) || r.ReviveHpFraction <= 0f || r.ReviveHpFraction > 1f
                    || ProjectChimera.Core.Fixed.FromFloat(r.ReviveHpFraction) <= ProjectChimera.Core.Fixed.Zero)
                    return ValidationResult.Fail(
                        $"scenario.revival_rule.revive_hp_fraction={r.ReviveHpFraction} must be finite and in (0, 1] and quantize to a positive 16.16 value.");
            }

            // ── Inventory slot count (Story 3.15, AR-39) — fail-closed when present so a hand-edited/cheat count outside
            // [1, INVENTORY_SLOTS] (a hero with 0 or > the physical stride of usable slots) is rejected. NULL ⇒ the full
            // stride (no rule to validate). Authoring-only — NOT folded into any checksum. ──
            if (m.InventorySlotCount is int isc
                && (isc < 1 || isc > ProjectChimera.Core.HeroStore.INVENTORY_SLOTS))
                return ValidationResult.Fail(
                    $"scenario.inventory_slot_count={isc} must be in [1, {ProjectChimera.Core.HeroStore.INVENTORY_SLOTS}].");

            // ── Resource registry (Story 4.3, AR-39) — fail-closed when present so a hand-edited/cheat registry
            // (a duplicate/blank id, a non-finite/negative starting_amount, or an unrecognized collection_model) is
            // rejected at the pre-tick gate. A null registry (every existing scenario) ⇒ nothing to validate ⇒ the
            // pass path is unchanged (no golden/behavior move). Authoring-only — NOT folded into any checksum/hash;
            // self-contained (no FactionDefinition awareness — see the spec's Design Notes on why a cost-map key is
            // not cross-referenced against this registry). First-fail, like the other loops. ──
            if (m.Resources != null)
            {
                var resourceIds = new HashSet<string>();
                for (int i = 0; i < m.Resources.Length; i++)
                {
                    ResourceDefinition r = m.Resources[i];
                    if (r is null)
                        return ValidationResult.Fail($"scenario.resources[{i}] is null.");
                    if (string.IsNullOrWhiteSpace(r.Id))
                        return ValidationResult.Fail($"scenario.resources[{i}].id must be a non-empty id.");
                    if (!resourceIds.Add(r.Id))
                        return ValidationResult.Fail(
                            $"scenario.resources[{i}].id='{r.Id}' is a duplicate.");
                    string? e = CheckNonNeg($"scenario.resources[{i}].starting_amount", r.StartingAmount);
                    if (e != null) return ValidationResult.Fail(e);
                    if (System.Array.IndexOf(ResourceDefinition.KnownCollectionModels, r.CollectionModel) < 0)
                        return ValidationResult.Fail(
                            $"scenario.resources[{i}].collection_model='{r.CollectionModel}' is not a known collection model " +
                            $"({string.Join("/", ResourceDefinition.KnownCollectionModels)}).");
                }
            }

            // ── Supply config (Story 4.4, AR-39) — fail-closed when present so a hand-edited/cheat config (an
            // out-of-range starting_cap, or a hard_ceiling below starting_cap) is rejected at the pre-tick gate. A
            // null config (every existing scenario) ⇒ use today's hardcoded default ⇒ nothing to validate ⇒ the pass
            // path is unchanged (no golden/behavior move). starting_cap/hard_ceiling are both int fields flowing
            // through the float-typed CheckNonNeg range-check helper (lossless implicit widening, matches the
            // existing validator-helper reuse convention). ──
            if (m.Supply != null)
            {
                SupplyConfig sc = m.Supply;
                string? e = CheckNonNeg("scenario.supply.starting_cap", sc.StartingCap);
                if (e != null) return ValidationResult.Fail(e);
                if (sc.HardCeiling is int ceiling)
                {
                    string? ce = CheckNonNeg("scenario.supply.hard_ceiling", ceiling);
                    if (ce != null) return ValidationResult.Fail(ce);
                    if (ceiling < sc.StartingCap)
                        return ValidationResult.Fail(
                            $"scenario.supply.hard_ceiling={ceiling} must be >= scenario.supply.starting_cap={sc.StartingCap}.");
                }
            }

            // Story 7.7 (proof discipline): every check above passed — mint the proof-of-validation token HERE and
            // only here. This is the codebase's sole `new Validated<ScenarioData>` (ValidatedMintingTests scan).
            return ValidationResult.Pass(new Validated<ScenarioData>(m, _proof));
        }

        /// <summary>
        /// Story 6.7 — a SEPARATE, NON-FATAL advisory channel, deliberately distinct from <see cref="Validate"/>'s
        /// binary <see cref="ValidationResult"/> pass/fail gate (which every fail-closed call site depends on). It
        /// returns human-readable warnings the editor surfaces (badge/toast) but which NEVER block a save or a tick.
        /// Today the sole advisory is "placed start positions below suggested_players" (the AC's "warns, not errors"
        /// case): when <see cref="ScenarioData.SuggestedPlayers"/> is specified (non-zero) and fewer start slots are
        /// placed than suggested, the map is playable but under-populated for its stated player count. Pure — never
        /// throws, never logs. Returns an empty list when there is nothing to advise (the common case).
        /// </summary>
        public IReadOnlyList<string> CollectAdvisories(ScenarioData m)
        {
            var advisories = new List<string>();
            if (m is null) return advisories;

            int suggested = m.SuggestedPlayers;
            int placed    = m.PlayerSlots?.Length ?? 0;
            if (suggested >= 2 && placed < suggested)
                advisories.Add(
                    $"Only {placed} start position(s) placed for a {suggested}-player map. " +
                    $"Place at least {suggested} start positions before publishing.");

            // Story 6.7 (patch 4) — warn (non-fatally) when a start position lies outside the current map bounds, which
            // a map-size shrink can cause. A subsequent hard Validate would otherwise fail with a cryptic message, so
            // this surfaces the cause up front.
            if (m.PlayerSlots != null)
                foreach (var s in m.PlayerSlots)
                    if (OutOfBounds(s.BaseX, s.BaseZ, m.MapBounds))
                        advisories.Add(
                            $"Start position P{s.Slot + 1} is outside the current map bounds ({m.MapBounds}).");

            // Story 6.7 (review pass 2) — the map-size-shrink strand-out is not unique to start positions: a shrink can
            // push ANY authored content past the new bounds, at which point the next hard Validate fails to LOAD the map
            // (CheckCoord, ±map_bounds — the SAME predicate/threshold as OutOfBounds below) while Export/New-Map only
            // ever ran advisories on start slots. Extend the same non-fatal early warning to every coordinate-bearing
            // collection the hard validator gates (buildings/units/resource nodes/props/water), so a shrink that would
            // strand content surfaces a visible cause up front instead of a silent, unloadable package. Counts, not a
            // per-entity spam, keep the toast readable.
            int nContent = OutOfBoundsCount(m.Buildings,     b => (b.X, b.Z), m.MapBounds)
                         + OutOfBoundsCount(m.Units,         u => (u.X, u.Z), m.MapBounds)
                         + OutOfBoundsCount(m.ResourceNodes, n => (n.X, n.Z), m.MapBounds)
                         + OutOfBoundsCount(m.Props,         p => (p.X, p.Z), m.MapBounds)
                         + OutOfBoundsCount(m.Water,         w => (w.X, w.Z), m.MapBounds);
            if (nContent > 0)
                advisories.Add(
                    $"{nContent} placed object(s) are outside the current map bounds ({m.MapBounds}) — " +
                    $"a smaller map size can strand content; move or delete them before saving/exporting.");

            return advisories;
        }

        // ── DW-148: the LOAD-TIME spawn-in-blocked-cell guard (the resolved-union half the pre-tick gate cannot see) ──

        /// <summary>
        /// DW-148 — re-run the blocked-cell placement checks against the RESOLVED load-time pathability union
        /// (painted ∪ blocking-prop/water footprint ∪ SLOPE-DERIVED steep cells), i.e. the grid
        /// <c>ScenarioApplier.BuildPathabilityGrid</c> produces once the terrain heightmap exists.
        ///
        /// <para><b>Why this exists.</b> <see cref="Validate"/> — the Godot-free pre-tick gate — can only decode the
        /// AUTHORED layers; slope-derived cells depend on the terrain heightmap, which is not available there (the same
        /// blind spot <see cref="MapWriteGate"/> documents). So a start base / pre-placed unit / building / resource
        /// node / <c>spawn_unit</c> trigger sitting on a slope-auto-blocked cell shipped completely un-caught: no unit
        /// can legally occupy that cell, and (pre-DW-148) a unit that spawned inside one was exempt from blocking
        /// altogether and walked through the terrain. This method closes the diagnosis half at the one point in the
        /// load where the FULL blocked set and the model exist side by side.</para>
        ///
        /// <para>It checks the SAME five surfaces, in the SAME declaration order, through the SAME cell-identity
        /// lookup (<see cref="PathabilityGrid.IsBlocked"/> over <c>FlowField.WorldToCell</c>) the authored-layer checks
        /// in <see cref="Validate"/> use — one shared derivation, no second hand-copied stamp loop that could drift.
        /// Returns the FIRST located error, or <c>null</c> when every placement is clear.</para>
        ///
        /// <para>Pure — never throws, never logs, mutates nothing. A null / all-clear grid (every flat and legacy map)
        /// returns <c>null</c> immediately, so this is an exact no-op on the common path. It is a LOCATED DIAGNOSTIC,
        /// not a second gate: the caller (<c>ScenarioApplier.Apply</c> — the Godot-free chokepoint every lifecycle path
        /// funnels through: boot, Edit→Play re-apply, server bootstrap) already holds a validation proof for the model
        /// and applies it regardless, because silently dropping authored content would be far worse than a reported
        /// bad spawn; <c>MovementSystem</c> then confines such a unit to its own cell.</para>
        /// </summary>
        /// <param name="m">The scenario being applied (null ⇒ nothing to check).</param>
        /// <param name="resolved">The resolved load-time union grid (null / all-clear ⇒ nothing to check).</param>
        /// <returns>The first located "spawn is on a blocked cell" error, or null when all placements are clear.</returns>
        public static string? CheckSpawnsNotBlocked(ScenarioData? m, PathabilityGrid? resolved)
        {
            if (m is null || resolved is null || !resolved.AnyBlocked) return null;

            ScenarioPlayerSlot[] slots = m.PlayerSlots ?? Array.Empty<ScenarioPlayerSlot>();
            for (int i = 0; i < slots.Length; i++)
            {
                ScenarioPlayerSlot s = slots[i];
                if (s is null) continue; // Validate() already located a null element; never NRE here
                string? e = CheckNotBlockedResolved($"scenario.player_slots[{i}]", "start base", s.BaseX, s.BaseZ, resolved);
                if (e != null) return e;
            }

            ScenarioResourceNode[] nodes = m.ResourceNodes ?? Array.Empty<ScenarioResourceNode>();
            for (int i = 0; i < nodes.Length; i++)
            {
                ScenarioResourceNode n = nodes[i];
                if (n is null) continue;
                string? e = CheckNotBlockedResolved($"scenario.resource_nodes[{i}]", "resource node", n.X, n.Z, resolved);
                if (e != null) return e;
            }

            ScenarioBuilding[] buildings = m.Buildings ?? Array.Empty<ScenarioBuilding>();
            for (int i = 0; i < buildings.Length; i++)
            {
                ScenarioBuilding b = buildings[i];
                if (b is null) continue;
                string? e = CheckNotBlockedResolved($"scenario.buildings[{i}]", "building position", b.X, b.Z, resolved);
                if (e != null) return e;
            }

            ScenarioUnit[] units = m.Units ?? Array.Empty<ScenarioUnit>();
            for (int i = 0; i < units.Length; i++)
            {
                ScenarioUnit u = units[i];
                if (u is null) continue;
                string? e = CheckNotBlockedResolved($"scenario.units[{i}]", "unit position", u.X, u.Z, resolved);
                if (e != null) return e;
            }

            TriggerDefinition[] triggers = m.Triggers ?? Array.Empty<TriggerDefinition>();
            for (int i = 0; i < triggers.Length; i++)
            {
                if (triggers[i] is null) continue;
                TriggerAction[] actions = triggers[i].Actions ?? Array.Empty<TriggerAction>();
                for (int a = 0; a < actions.Length; a++)
                {
                    TriggerAction act = actions[a];
                    if (act is null || act.Type != "spawn_unit") continue;
                    // X/Z are already Fixed here (Story 7.1) — passed straight through, no float boundary.
                    string? e = CheckNotBlockedResolved(
                        $"scenario.triggers[{i}].actions[{a}]", "spawn_unit position", act.X, act.Z, resolved);
                    if (e != null) return e;
                }
            }

            return null;
        }

        // ── Helpers (return a located error string, or null when the field is OK) ──

        /// <summary>Story 6.7 (review pass 2) — the advisory out-of-bounds predicate. Matches the hard validator's
        /// <see cref="CheckCoord"/> threshold exactly (strict <c>&gt; bounds</c>, i.e. an on-edge coordinate at exactly
        /// ±bounds is IN-bounds for both) so the early advisory never disagrees with the hard load gate.</summary>
        private static bool OutOfBounds(float x, float z, float bounds)
            => System.Math.Abs(x) > bounds || System.Math.Abs(z) > bounds;

        /// <summary>Story 6.7 (review pass 2) — count the entries in a nullable collection whose (x,z) lies outside the
        /// map bounds. Null collection ⇒ 0 (a legacy scenario that omits the array is unaffected).</summary>
        private static int OutOfBoundsCount<T>(T[]? items, System.Func<T, (float x, float z)> coord, float bounds)
        {
            if (items == null) return 0;
            int n = 0;
            foreach (var it in items)
            {
                var (x, z) = coord(it);
                if (OutOfBounds(x, z, bounds)) n++;
            }
            return n;
        }

        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        /// <summary>True when the linear integer curve <c>base + perLevel × MaxRevivableLevel</c> reaches the 16.16 ceiling
        /// (computed in <c>long</c> so the check itself never overflows).</summary>
        private static bool RevivalCurveOverflows(int baseVal, int perLevel) =>
            (long)baseVal + (long)perLevel * MaxRevivableLevel >= (long)Range;

        /// <summary>In the 16.16 representable range [-Range, Range) and finite — mirrors FixedJsonConverter.</summary>
        private static bool InRange(float v) => Finite(v) && v >= -Range && v < Range;

        /// <summary>Coordinate check: finite + in 16.16 range + within ±map_bounds.</summary>
        private static string? CheckCoord(string path, float v, float bounds)
        {
            if (!InRange(v))
                return $"{path}={v} is non-finite or outside the 16.16 range [-{Range}, {Range}).";
            if (v < -bounds || v > bounds)
                return $"{path}={v} is outside map_bounds (±{bounds}).";
            return null;
        }

        /// <summary>Story 7.1 — coordinate check for a <see cref="Fixed"/>-typed authored coordinate (trigger
        /// spawn_unit X/Z). Finiteness and the 16.16 representable range are guaranteed structurally by the
        /// <see cref="Fixed"/> type (its Raw is an int) and by <c>FixedJsonConverter</c> at the JSON boundary, so
        /// only the map_bounds gate remains — mirroring <see cref="CheckCoord"/>'s map_bounds branch (±bounds,
        /// inclusive edge). The <c>Fixed.FromFloat</c> here is the sanctioned load-time boundary (this validator
        /// already quantizes region corners / blocked-cell coords the same way).</summary>
        private static string? CheckCoordFixed(string path, Fixed v, float bounds)
        {
            Fixed b = Fixed.FromFloat(bounds);
            if (v < -b || v > b)
                return $"{path}={v} is outside map_bounds (±{bounds}).";
            return null;
        }

        /// <summary>Non-negative scalar check: finite + in 16.16 range + &gt;= 0.</summary>
        private static string? CheckNonNeg(string path, float v)
        {
            if (!InRange(v))
                return $"{path}={v} is non-finite or outside the 16.16 range [-{Range}, {Range}).";
            if (v < 0f)
                return $"{path}={v} must be >= 0.";
            return null;
        }

        /// <summary>
        /// Story 6.5: fail-closed if (x, z) resolves to a PAINTED blocked cell, using the SAME
        /// <c>FlowField.WorldToCell</c> 128²/2-unit mapping the sim enforces (validator↔sim agree on cell identity).
        /// Null grid (no paint / all-clear) ⇒ always OK (no behavior change for flat/legacy maps). The float→Fixed
        /// conversion here is the sanctioned load-time boundary (this validator already quantizes region corners the
        /// same way).
        /// </summary>
        private static string? CheckNotBlocked(string path, string what, float x, float z, PathabilityGrid? painted)
        {
            if (painted == null) return null;
            if (painted.IsBlocked(Fixed.FromFloat(x), Fixed.FromFloat(z)))
                return $"{path} {what} ({x}, {z}) is on an impassable (blocked) cell — painted, or a blocking prop / water footprint — no unit can occupy it.";
            return null;
        }

        /// <summary>Story 7.1 — <see cref="Fixed"/> overload for trigger spawn_unit X/Z (now Fixed-typed). Same
        /// painted-blocked-cell check as the float overload, but passes the coordinates straight through with no
        /// <c>Fixed.FromFloat</c> (they are already Fixed).</summary>
        private static string? CheckNotBlocked(string path, string what, Fixed x, Fixed z, PathabilityGrid? painted)
        {
            if (painted == null) return null;
            if (painted.IsBlocked(x, z))
                return $"{path} {what} ({x}, {z}) is on an impassable (blocked) cell — painted, or a blocking prop / water footprint — no unit can occupy it.";
            return null;
        }

        /// <summary>
        /// DW-148 — the <see cref="CheckSpawnsNotBlocked"/> counterpart of <see cref="CheckNotBlocked"/>: the SAME
        /// clamped integer cell lookup, but against the RESOLVED load-time union grid, so the message can name the
        /// slope-derived possibility the authored-layer check cannot produce (an author staring at a clean paint layer
        /// needs to be told the terrain derived that cell). The <c>Fixed.FromFloat</c> is the same sanctioned load-time
        /// boundary the authored-layer overload uses.
        /// </summary>
        private static string? CheckNotBlockedResolved(string path, string what, float x, float z, PathabilityGrid grid)
            => CheckNotBlockedResolved(path, what, Fixed.FromFloat(x), Fixed.FromFloat(z), grid);

        /// <summary><see cref="Fixed"/> overload (trigger <c>spawn_unit</c> X/Z are already Fixed — no conversion).</summary>
        private static string? CheckNotBlockedResolved(string path, string what, Fixed x, Fixed z, PathabilityGrid grid)
        {
            if (!grid.IsBlocked(x, z)) return null;
            return $"{path} {what} ({x}, {z}) is on an impassable (blocked) cell of the RESOLVED load-time pathability " +
                   "grid — painted, a blocking prop / water footprint, or a SLOPE-DERIVED steep cell — no unit can occupy it.";
        }

        /// <summary>Story 6.8 — a known building type is an EXACT <see cref="BuildingType"/> enum name (case-sensitive;
        /// rejects numeric strings) OR an authored building-def id present in <paramref name="ownerDef"/>'s
        /// <c>Buildings</c>. A null <paramref name="ownerDef"/> (no faction threaded, or an out-of-range slot) restricts
        /// to enum names only — byte-identical to the pre-6.8 gate. The bare <c>"Custom"</c> sentinel name is NOT a
        /// placeable identity (it resolves no def → a stat-less, unrendered ghost), so it is excluded from the enum-name
        /// match — a custom building must name its authored id; a lowercase authored id such as <c>"custom"</c> is still
        /// accepted through <paramref name="ownerDef"/>.
        /// DW-170 — the trigger gate now calls this with the OWNER faction of the event/condition's own
        /// <c>faction</c> slot (its faction qualifier), so a trigger can name an authored custom building exactly
        /// like a pre-placed scenario building does. <c>ScenarioDirector</c> resolves the same two vocabularies at
        /// runtime (enum name → <c>BuildingStore.Type</c>, authored id → <c>BuildingStore.DefinitionId</c>).</summary>
        private static bool IsKnownBuildingType(string? type, FactionDefinition? ownerDef = null)
        {
            if (type is null) return false;
            for (int i = 0; i < _buildingTypeNames.Length; i++)
                if (_buildingTypeNames[i] == type)
                    return type != nameof(BuildingType.Custom); // the Custom sentinel is not a placeable Type
            return ownerDef?.GetBuilding(type) != null;
        }

        /// <summary>
        /// DW-240 — a known unit id is one that RESOLVES in <paramref name="ownerDef"/>'s roster, i.e. exactly what
        /// <c>FactionDefinition.GetUnit</c> (the applier's and the spawn delegate's own lookup) returns non-null for.
        /// There is no enum/legacy fallback the way <see cref="IsKnownBuildingType"/> has one — a unit identity is
        /// ALWAYS a faction-authored id.
        ///
        /// <para>A null <paramref name="ownerDef"/> (no per-slot faction defs threaded — the default for every legacy
        /// caller and test — or a slot that resolves to no def) means there is nothing to resolve against, so the
        /// check accepts: byte-identical to the pre-DW-240 gate. Mirrors the Story 6.8 building-type amnesty.</para>
        /// </summary>
        private static bool IsKnownUnitId(string? unitId, FactionDefinition? ownerDef)
        {
            if (ownerDef is null) return true;               // nothing threaded to resolve against ⇒ no-op (6.8 precedent)
            if (string.IsNullOrEmpty(unitId)) return false;  // a blank id can never resolve — the applier would drop it
            return ownerDef.GetUnit(unitId) != null;
        }

        /// <summary>DW-240 — the shared located message for an unresolvable unit id. Names the field path, the
        /// offending id, the owning faction (so the author knows WHICH roster was searched), and the slot. Kept in one
        /// place so the pre-placed and both spawn_unit channels report identically.</summary>
        private static string UnknownUnitIdError(string path, string? unitId, FactionDefinition? ownerDef, int slot)
        {
            string faction = string.IsNullOrEmpty(ownerDef?.Id) ? "(unnamed)" : ownerDef!.Id;
            return $"{path}='{unitId}' names no unit in the roster of the faction that owns slot {slot} " +
                   $"('{faction}') — it would resolve to no UnitDefinition and be silently dropped at spawn.";
        }

        /// <summary>Story 6.8 — resolve a pre-placed building's owner <see cref="FactionDefinition"/> from the
        /// per-slot defs (indexed by <c>(int)Faction</c> = slot+1). Null when no defs are threaded or the slot is out
        /// of range — the caller then falls back to enum-name-only building-type acceptance. DW-240 reuses it for the
        /// pre-placed / spawn_unit unit-id resolution domain (the same per-slot roster the applier reads).</summary>
        private static FactionDefinition? OwnerFactionDef(IReadOnlyList<FactionDefinition?>? slotFactionDefs, int slot)
        {
            if (slotFactionDefs is null) return null;
            int fIdx = slot + 1; // (Faction)(slot + 1), matching the applier's cast
            return fIdx >= 0 && fIdx < slotFactionDefs.Count ? slotFactionDefs[fIdx] : null;
        }

        /// <summary>True only for an EXACT resource_type name (case-sensitive) — "Ore" or "Crystal".</summary>
        private static bool IsKnownResourceType(string? type)
        {
            if (type is null) return false;
            for (int i = 0; i < _resourceTypeNames.Length; i++)
                if (_resourceTypeNames[i] == type) return true;
            return false;
        }

        // ── Trigger vocabulary (Story 1.11, AC3; Story 7.7 unification) — the CLOSED, typed sets ScenarioDirector
        //    actually handles. Story 7.7: these fields ALIAS the NodeKinds registry (the one vocabulary source) —
        //    the former hand-kept string copies are gone, so the flat gate and the graph converter can never drift
        //    (NodeKindsLockstepTests asserts the aliasing by reference). The flat action set is NodeKinds'
        //    FlatActionTypes (the graph set minus the graph-channel-only array kinds ToFlat skips). The operator
        //    set aliases NodeKinds.Operators too (review follow-up), so the graph converter's parse-time operator
        //    membership check and this flat gate share the one vocabulary. ──
        private static readonly string[] _operators          = NodeKinds.Operators;
        private static readonly string[] _triggerEventTypes  = NodeKinds.EventTypes;
        private static readonly string[] _conditionTypes     = NodeKinds.ConditionTypes;
        private static readonly string[] _actionTypes        = NodeKinds.FlatActionTypes;

        /// <summary>Exact-match membership in a closed string set (case-sensitive). Null is never a member.</summary>
        private static bool InSet(string[] set, string? value)
        {
            if (value is null) return false;
            for (int i = 0; i < set.Length; i++)
                if (set[i] == value) return true;
            return false;
        }

        /// <summary>
        /// A trigger faction slot must map to a defined <see cref="Faction"/>: ScenarioDirector does
        /// <c>(Faction)(slot + 1)</c> and indexes the FACTION_ARRAY_SIZE (9) per-faction arrays, so an out-of-range
        /// slot crashes the tick (OOB) — exactly the engine ceiling the player-slot check enforces, reused here.
        /// Returns a located error or null when OK.
        /// </summary>
        private static string? CheckFactionSlot(string path, int slot)
        {
            if (slot < 0 || slot + 1 > (int)Faction.Player8)
                return $"{path}={slot} is out of the valid faction-slot range [0,{(int)Faction.Player8 - 1}].";
            return null;
        }

        /// <summary>Story 7.5 (merge hardening) — strict map-aware compiles for the roots DslLoopGate's static
        /// pass skips when they read <c>event.&lt;param&gt;</c>: branch conditions (must be Bool) and array-action
        /// value/index roots (element type / Int), walked over the nested execution view exactly as the
        /// LoadScenario backstop's <c>CompileItems</c> compiles them. Recursion depth is bounded by the
        /// MaxExecWalkDepth cap DslLoopGate already enforced above. Returns the first located error, else null.</summary>
        private static string? CheckEventParamRoots(TriggerGraph graph, TriggerGraph.TriggerExec ex,
            IReadOnlyDictionary<string, (int Slot, DslValueType Type)>? paramMap,
            IReadOnlyDictionary<string, (DslValueType Type, VarScope Scope)> declaredVars,
            IReadOnlyDictionary<string, (DslValueType Elem, int Capacity)> arrayDecls)
        {
            return Walk(ex.Items);

            string? Walk(TriggerGraph.ExecItem[] items)
            {
                foreach (TriggerGraph.ExecItem it in items)
                {
                    switch (it.Node)
                    {
                        case BranchNode br when it.CondExprRoot >= 0:
                        {
                            if (!ExprCompiler.TryCompile(graph, it.CondExprRoot, declaredVars, inCondition: false,
                                    paramMap, out ExprProgram? cp, out string? cErr, arrayDecls))
                                return $"trigger '{ex.Trigger.Name}' branch condition: {cErr}";
                            if (cp!.ResultType != DslValueType.Bool)
                                return $"trigger '{ex.Trigger.Name}' branch node {br.Id}: the condition expression must evaluate to Bool, got {cp.ResultType}.";
                            break;
                        }
                        case ActionNode act when NodeKinds.IsArrayActionKind(act.Kind):
                        {
                            DslValueType elem = arrayDecls.TryGetValue(act.Variable ?? "", out (DslValueType Elem, int Capacity) ad)
                                ? ad.Elem : DslValueType.Int;
                            if (it.ValueExprRoot >= 0)
                            {
                                if (!ExprCompiler.TryCompile(graph, it.ValueExprRoot, declaredVars, inCondition: false,
                                        paramMap, out ExprProgram? vp, out string? vErr, arrayDecls))
                                    return $"trigger '{ex.Trigger.Name}' {act.Kind} value expression: {vErr}";
                                if (vp!.ResultType != elem)
                                    return $"trigger '{ex.Trigger.Name}' action node {act.Id} ({act.Kind}): value expression type {vp.ResultType} does not match array '{act.Variable}' element type {elem}.";
                            }
                            if (it.IndexExprRoot >= 0)
                            {
                                if (!ExprCompiler.TryCompile(graph, it.IndexExprRoot, declaredVars, inCondition: false,
                                        paramMap, out ExprProgram? ip, out string? iErr, arrayDecls))
                                    return $"trigger '{ex.Trigger.Name}' {act.Kind} index expression: {iErr}";
                                if (ip!.ResultType != DslValueType.Int)
                                    return $"trigger '{ex.Trigger.Name}' action node {act.Id} ({act.Kind}): the index expression must be Int, got {ip.ResultType}.";
                            }
                            break;
                        }
                    }
                    string? sub = Walk(it.Body) ?? Walk(it.Then) ?? Walk(it.Else);
                    if (sub != null) return sub;
                }
                return null;
            }
        }
    }
}
