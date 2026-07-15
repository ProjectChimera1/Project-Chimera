#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core; // Faction, FactionRegistry, BuildingType
using ProjectChimera.Navigation; // PathabilityGrid (Story 6.5 blocked-cell fail-closed)

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The single fail-closed pre-tick gate (Story 1.7, AR-39). Every scenario entry path funnels a
    /// <see cref="ScenarioData"/> through <see cref="Validate"/> before it is applied to the simulation. On
    /// success it mints a <see cref="Validated{T}"/> (the proof-of-validation token); on the FIRST failed check
    /// it returns a located <see cref="ValidationResult"/> error (field path + offending value). It is pure: it
    /// NEVER throws and NEVER logs — the presentation call site decides shadow vs fail-closed policy
    /// (<see cref="ScenarioGate"/>). Godot-free (src/Core/Definitions), so it compiles into the Tier-1 test
    /// assembly and the AOT-eligible sim layer.
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

            // D3 (Story 1.8b): mint the proof-of-validation token ONCE here (m is non-null). It is carried by BOTH
            // Pass and every Fail below, so 1.7 shadow-mode can still apply the model on a FAILED validation (the
            // applier consumes only Validated<ScenarioData>). Golden-neutral: the same model is applied as today.
            // This is the codebase's sole `new Validated<` (ValidatedSoleMinterTest guards it).
            var validated = new Validated<ScenarioData>(m, _proof);

            // ── Map bounds: finite, > 0, and inside the Fixed range (it is a coordinate ceiling) ──
            if (!Finite(m.MapBounds) || m.MapBounds <= 0f)
                return ValidationResult.Fail($"scenario.map_bounds={m.MapBounds} must be finite and > 0.", validated);
            if (m.MapBounds >= Range)
                return ValidationResult.Fail(
                    $"scenario.map_bounds={m.MapBounds} exceeds the 16.16 range [0, {Range}).", validated);

            float bounds = m.MapBounds;

            // ── Story 6.5: the slope-auto-block threshold is a float→Fixed boundary folded into CanonicalModelHash.
            //    An out-of-range/non-finite value would overflow Fixed.FromFloat with a platform-unspecified result
            //    (peers on different runtimes could then fold different Raw ⇒ false handshake reject), so gate it
            //    finite, non-negative, and inside the Fixed range like map_bounds. Default 0f passes unchanged. ──
            if (!Finite(m.SlopeBlockThreshold) || m.SlopeBlockThreshold < 0f || m.SlopeBlockThreshold >= Range)
                return ValidationResult.Fail(
                    $"scenario.slope_block_threshold={m.SlopeBlockThreshold} must be finite and within [0, {Range}).", validated);

            // ── Story 6.7: suggested_players is authoring metadata (2–4 for 1.0; engine ceiling Faction.Player4).
            //    Omit-when-default (0) ⇒ "unspecified" ⇒ nothing to validate (every existing scenario passes
            //    unchanged). A PRESENT value must be in [2,4]; 1 or 5+ fails closed. This is a hard fail (an
            //    unshippable design intent), distinct from the SOFT below-suggested advisory in CollectAdvisories. ──
            if (m.SuggestedPlayers != 0 && (m.SuggestedPlayers < 2 || m.SuggestedPlayers > 4))
                return ValidationResult.Fail(
                    $"scenario.suggested_players={m.SuggestedPlayers} must be in [2,4] (1.0 ships 2–4 players).", validated);

            // ── Collections must be present. A null array is malformed input the applier would NRE on, so the
            // validator rejects it (located) rather than silently treating it as empty via the `?? Array.Empty`
            // guards below — those are then belt-and-suspenders. [Story 1.7 review patch] ──
            if (m.PlayerSlots is null)   return ValidationResult.Fail("scenario.player_slots is null.", validated);
            if (m.ResourceNodes is null) return ValidationResult.Fail("scenario.resource_nodes is null.", validated);
            if (m.Buildings is null)     return ValidationResult.Fail("scenario.buildings is null.", validated);
            if (m.Units is null)         return ValidationResult.Fail("scenario.units is null.", validated);
            // Story 1.11 (AC3): triggers are now gated too. A null array would NRE in ScenarioDirector.LoadScenario
            // (new bool[_triggers.Length]); reject it located, like the four collections above.
            if (m.Triggers is null)      return ValidationResult.Fail("scenario.triggers is null.", validated);

            // ── Story 6.6: structurally validate props / cameras / water BEFORE decoding the blocked union (their
            //    coords quantize into the footprint mask below, so a non-finite/out-of-range one must fail first) and
            //    before any position check. NULL collections (every existing scenario) ⇒ nothing to validate ⇒ the
            //    pass path is unchanged. First-fail located error, mirroring the buildings/units/regions loops. ──
            ScenarioProp[] props = m.Props ?? Array.Empty<ScenarioProp>();
            for (int i = 0; i < props.Length; i++)
            {
                ScenarioProp p = props[i];
                if (p is null) return ValidationResult.Fail($"scenario.props[{i}] is null.", validated);
                // x/z are hash-folded (the footprint cell) — gate finite + in the 16.16 range + within map bounds.
                string? pe = CheckCoord($"scenario.props[{i}].x", p.X, bounds)
                          ?? CheckCoord($"scenario.props[{i}].z", p.Z, bounds);
                if (pe != null) return ValidationResult.Fail(pe, validated);
                if (!Finite(p.Rot))
                    return ValidationResult.Fail($"scenario.props[{i}].rot={p.Rot} must be finite.", validated);
                if (p.Scale is float sc && (!Finite(sc) || sc <= 0f))
                    return ValidationResult.Fail($"scenario.props[{i}].scale={sc} must be finite and > 0.", validated);
            }

            var declaredCameras = new HashSet<string>(StringComparer.Ordinal);
            ScenarioCamera[] cameras = m.Cameras ?? Array.Empty<ScenarioCamera>();
            for (int i = 0; i < cameras.Length; i++)
            {
                ScenarioCamera cam = cameras[i];
                if (cam is null) return ValidationResult.Fail($"scenario.cameras[{i}] is null.", validated);
                if (string.IsNullOrEmpty(cam.Name))
                    return ValidationResult.Fail($"scenario.cameras[{i}].name must be a non-empty name.", validated);
                if (!declaredCameras.Add(cam.Name))
                    return ValidationResult.Fail($"scenario.cameras[{i}].name='{cam.Name}' is a duplicate.", validated);
                // Cameras never fold into any hash, but a non-finite position/target/fov would still break the in-editor
                // preview and the MoveCamera action, so gate well-formedness fail-closed.
                if (!Finite(cam.X) || !Finite(cam.Y) || !Finite(cam.Z)
                    || !Finite(cam.TargetX) || !Finite(cam.TargetY) || !Finite(cam.TargetZ))
                    return ValidationResult.Fail($"scenario.cameras[{i}] has a non-finite position/target coordinate.", validated);
                if (!Finite(cam.Fov) || cam.Fov <= 0f || cam.Fov >= 180f)
                    return ValidationResult.Fail($"scenario.cameras[{i}].fov={cam.Fov} must be finite and in (0, 180).", validated);
            }

            ScenarioWater[] water = m.Water ?? Array.Empty<ScenarioWater>();
            for (int i = 0; i < water.Length; i++)
            {
                ScenarioWater w = water[i];
                if (w is null) return ValidationResult.Fail($"scenario.water[{i}] is null.", validated);
                // x/z (min corner) and x+w / z+h (max corner) are hash-folded (the footprint rect) — every corner must
                // be finite, in the 16.16 range, and within map bounds; extents must be positive (a well-formed rect).
                string? we = CheckCoord($"scenario.water[{i}].x", w.X, bounds)
                          ?? CheckCoord($"scenario.water[{i}].z", w.Z, bounds);
                if (we != null) return ValidationResult.Fail(we, validated);
                if (!InRange(w.W) || w.W <= 0f)
                    return ValidationResult.Fail($"scenario.water[{i}].w={w.W} must be finite and > 0.", validated);
                if (!InRange(w.H) || w.H <= 0f)
                    return ValidationResult.Fail($"scenario.water[{i}].h={w.H} must be finite and > 0.", validated);
                string? wce = CheckCoord($"scenario.water[{i}] max_x", w.X + w.W, bounds)
                           ?? CheckCoord($"scenario.water[{i}] max_z", w.Z + w.H, bounds);
                if (wce != null) return ValidationResult.Fail(wce, validated);
                if (!Finite(w.Y))
                    return ValidationResult.Fail($"scenario.water[{i}].y={w.Y} must be finite.", validated);
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
                        $"scenario.player_slots[{i}].slot={s.Slot} is out of [0,{FactionRegistry.PLAYER_COUNT}).", validated);

                // The AR-39 length-5 overflow guard: the as-built Faction enum tops at Player4, so FactionRegistry
                // .ToFaction(slot) is only defined for slot <= 3. A slot in [4,8) is < PLAYER_COUNT but overflows
                // the [5] per-faction arrays. This relaxes automatically when Story 9.2 extends Faction to Player8.
                if (s.Slot + 1 > (int)Faction.Player4)
                    return ValidationResult.Fail(
                        $"scenario.player_slots[{i}].slot={s.Slot} maps to an undefined Faction " +
                        $"(engine ceiling: slot <= {(int)Faction.Player4 - 1}).", validated);

                if (!declared.Add(s.Slot))
                    return ValidationResult.Fail(
                        $"scenario.player_slots[{i}].slot={s.Slot} is a duplicate.", validated);

                string? e = CheckNonNeg($"scenario.player_slots[{i}].start_ore", s.StartOre)
                         ?? CheckNonNeg($"scenario.player_slots[{i}].start_crystal", s.StartCrystal)
                         ?? CheckCoord($"scenario.player_slots[{i}].base_x", s.BaseX, bounds)
                         ?? CheckCoord($"scenario.player_slots[{i}].base_z", s.BaseZ, bounds);
                if (e != null) return ValidationResult.Fail(e, validated);

                // Story 6.5: a start position on a PAINTED blocked cell fails closed — a unit could never legally
                // occupy it, so the map is unplayable. Fail before any tick with a clear message.
                string? be = CheckNotBlocked($"scenario.player_slots[{i}]", "start base", s.BaseX, s.BaseZ, painted);
                if (be != null) return ValidationResult.Fail(be, validated);
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
                if (e != null) return ValidationResult.Fail(e, validated);
                if (n.MaxGatherers < 0)
                    return ValidationResult.Fail(
                        $"scenario.resource_nodes[{i}].max_gatherers={n.MaxGatherers} must be >= 0.", validated);

                // ── Story 4.7: collection model / resource type / requires_structure gate / owner slot / income period ──
                // collection_model reuses the Story 4.3 closed vocabulary (ResourceDefinition.KnownCollectionModels)
                // so the two authoring surfaces never drift. resource_type is its own small closed set — only Ore/
                // Crystal have real ResourceStore-backed balances today.
                if (Array.IndexOf(ResourceDefinition.KnownCollectionModels, n.CollectionModel) < 0)
                    return ValidationResult.Fail(
                        $"scenario.resource_nodes[{i}].collection_model='{n.CollectionModel}' is not a known collection model " +
                        $"({string.Join("/", ResourceDefinition.KnownCollectionModels)}).", validated);
                if (!IsKnownResourceType(n.ResourceType))
                    return ValidationResult.Fail(
                        $"scenario.resource_nodes[{i}].resource_type='{n.ResourceType}' is not a known resource type " +
                        $"({string.Join("/", _resourceTypeNames)}).", validated);
                string? se = CheckNonNeg($"scenario.resource_nodes[{i}].requires_structure_radius", n.RequiresStructureRadius)
                          ?? CheckNonNeg($"scenario.resource_nodes[{i}].income_period_ticks", n.IncomePeriodTicks);
                if (se != null) return ValidationResult.Fail(se, validated);
                // owner_slot is only load-bearing for Income (no assigned worker to infer a faction from) — required
                // AND must reference a declared player_slot, exactly like the buildings/units slot-reference check
                // above. Inert (unvalidated) for GATHER/Streaming, which credit the gathering worker's own faction.
                if (n.CollectionModel == "Income" && !declared.Contains(n.OwnerSlot))
                    return ValidationResult.Fail(
                        $"scenario.resource_nodes[{i}].owner_slot={n.OwnerSlot} references no declared player_slot " +
                        $"(required when collection_model=Income).", validated);
                // Review patch: income_period_ticks=0 passed the bare non-negative check above but makes
                // IncomeTicksElapsed's `< IncomePeriodTicks` comparison true on tick 1 forever — a degenerate
                // "credit every tick" mode, not the intended periodic trickle. Only meaningful (and only gated) for
                // Income; GATHER/Streaming ignore the field entirely, so a 0 there is inert, not an authoring error.
                if (n.CollectionModel == "Income" && n.IncomePeriodTicks <= 0)
                    return ValidationResult.Fail(
                        $"scenario.resource_nodes[{i}].income_period_ticks={n.IncomePeriodTicks} must be > 0 " +
                        $"(required when collection_model=Income).", validated);
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
                if (e != null) return ValidationResult.Fail(e, validated);
                if (!declared.Contains(b.Slot))
                    return ValidationResult.Fail(
                        $"scenario.buildings[{i}].slot={b.Slot} references no declared player_slot.", validated);
                // Story 6.8: the retired enum gate. b.Type is accepted as a legacy BuildingType enum name OR an
                // authored building-def id present in the OWNER faction's Buildings (resolved from slotFactionDefs by
                // the building's slot). A truly unknown id — no enum name and no matching faction building-def — fails
                // closed with a message naming it. When no faction defs are threaded (null), this is enum-name-only.
                if (!IsKnownBuildingType(b.Type, OwnerFactionDef(slotFactionDefs, b.Slot)))
                    return ValidationResult.Fail(
                        $"scenario.buildings[{i}].type='{b.Type}' is not a known BuildingType enum name or an authored building id in the owner faction.", validated);
            }

            // ── Units: in-bounds position, slot references a declared PlayerSlot ──
            ScenarioUnit[] units = m.Units ?? Array.Empty<ScenarioUnit>();
            for (int i = 0; i < units.Length; i++)
            {
                ScenarioUnit u = units[i];
                string? e = CheckCoord($"scenario.units[{i}].x", u.X, bounds)
                         ?? CheckCoord($"scenario.units[{i}].z", u.Z, bounds);
                if (e != null) return ValidationResult.Fail(e, validated);
                if (!declared.Contains(u.Slot))
                    return ValidationResult.Fail(
                        $"scenario.units[{i}].slot={u.Slot} references no declared player_slot.", validated);

                // Story 6.5: a pre-placed unit on a PAINTED blocked cell fails closed (same cell domain as the sim).
                string? ube = CheckNotBlocked($"scenario.units[{i}]", "unit position", u.X, u.Z, painted);
                if (ube != null) return ValidationResult.Fail(ube, validated);
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
                    return ValidationResult.Fail($"scenario.regions[{i}] is null.", validated);
                if (string.IsNullOrEmpty(rg.Id))
                    return ValidationResult.Fail($"scenario.regions[{i}].id must be a non-empty id.", validated);
                if (!declaredRegions.Add(rg.Id))
                    return ValidationResult.Fail($"scenario.regions[{i}].id='{rg.Id}' is a duplicate.", validated);
                string? re = CheckCoord($"scenario.regions[{i}].min_x", rg.MinX, bounds)
                          ?? CheckCoord($"scenario.regions[{i}].min_z", rg.MinZ, bounds)
                          ?? CheckCoord($"scenario.regions[{i}].max_x", rg.MaxX, bounds)
                          ?? CheckCoord($"scenario.regions[{i}].max_z", rg.MaxZ, bounds);
                if (re != null) return ValidationResult.Fail(re, validated);
                if (rg.MinX >= rg.MaxX)
                    return ValidationResult.Fail(
                        $"scenario.regions[{i}] has min_x={rg.MinX} >= max_x={rg.MaxX} (must be min_x < max_x).", validated);
                if (rg.MinZ >= rg.MaxZ)
                    return ValidationResult.Fail(
                        $"scenario.regions[{i}] has min_z={rg.MinZ} >= max_z={rg.MaxZ} (must be min_z < max_z).", validated);
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
                        "at least ~1/65536 world units so the region survives the float→Fixed resolution.", validated);
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

            // Pass 1: collect every timer name a create_timer action declares, so a timer_expires reference can be
            // checked against it (the only cross-trigger reference; variables default to 0 at read, so they need none).
            var declaredTimers = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < triggers.Length; i++)
            {
                if (triggers[i] is null) continue; // located-rejected in pass 2; don't NRE while collecting
                TriggerAction[] collectActions = triggers[i].Actions ?? Array.Empty<TriggerAction>();
                for (int a = 0; a < collectActions.Length; a++)
                    if (collectActions[a].Type == "create_timer" && !string.IsNullOrEmpty(collectActions[a].TimerName))
                        declaredTimers.Add(collectActions[a].TimerName!);
            }

            // Pass 2: validate each trigger's events, conditions, and actions in declaration order.
            for (int i = 0; i < triggers.Length; i++)
            {
                TriggerDefinition t = triggers[i];
                if (t is null) return ValidationResult.Fail($"scenario.triggers[{i}] is null.", validated);

                TriggerEvent[] events = t.Events ?? Array.Empty<TriggerEvent>();
                for (int j = 0; j < events.Length; j++)
                {
                    TriggerEvent e = events[j];
                    string ep = $"scenario.triggers[{i}].events[{j}]";
                    if (!InSet(_triggerEventTypes, e.Type))
                        return ValidationResult.Fail($"{ep}.type='{e.Type}' is not a known trigger event type.", validated);
                    string? fe = CheckFactionSlot($"{ep}.faction", e.Faction);
                    if (fe != null) return ValidationResult.Fail(fe, validated);
                    if (!string.IsNullOrEmpty(e.BuildingType) && !IsKnownBuildingType(e.BuildingType))
                        return ValidationResult.Fail($"{ep}.building_type='{e.BuildingType}' is not a known BuildingType.", validated);
                    if (!InSet(_operators, e.Operator))
                        return ValidationResult.Fail($"{ep}.operator='{e.Operator}' is not a known comparison operator.", validated);
                    if (e.Type == "timer_expires" && !string.IsNullOrEmpty(e.TimerName) && !declaredTimers.Contains(e.TimerName))
                        return ValidationResult.Fail(
                            $"{ep}.timer_name='{e.TimerName}' references no timer created by any create_timer action.", validated);
                }

                TriggerCondition[] conds = t.Conditions ?? Array.Empty<TriggerCondition>();
                for (int j = 0; j < conds.Length; j++)
                {
                    TriggerCondition c = conds[j];
                    string cp = $"scenario.triggers[{i}].conditions[{j}]";
                    if (!InSet(_conditionTypes, c.Type))
                        return ValidationResult.Fail($"{cp}.type='{c.Type}' is not a known trigger condition type.", validated);
                    string? fe = CheckFactionSlot($"{cp}.faction", c.Faction);
                    if (fe != null) return ValidationResult.Fail(fe, validated);
                    if (!string.IsNullOrEmpty(c.BuildingType) && !IsKnownBuildingType(c.BuildingType))
                        return ValidationResult.Fail($"{cp}.building_type='{c.BuildingType}' is not a known BuildingType.", validated);
                    if (!InSet(_operators, c.Operator))
                        return ValidationResult.Fail($"{cp}.operator='{c.Operator}' is not a known comparison operator.", validated);
                    // Story 6.4: a unit_in_region condition must name a DECLARED region (dangling-ref fail-closed,
                    // mirroring the timer_expires dangling check) — an undefined/empty region_id would silently
                    // never match at runtime, an almost-certain authoring/LLM error.
                    if (c.Type == "unit_in_region")
                    {
                        // Review patch: also fail-closed on an out-of-range faction slot here — the unit_in_region
                        // scan does (Faction)(c.Faction + 1) and compares live entities against it. Reuses the
                        // canonical trigger-faction bound (CheckFactionSlot, engine ceiling Faction.Player4) the
                        // general condition check above already applies, co-located with the region check as
                        // belt-and-suspenders fail-closed defense.
                        string? rfe = CheckFactionSlot($"{cp}.faction", c.Faction);
                        if (rfe != null) return ValidationResult.Fail(rfe, validated);
                        if (!declaredRegions.Contains(c.RegionId ?? ""))
                            return ValidationResult.Fail(
                                $"{cp}.region_id='{c.RegionId}' references no declared region.", validated);
                    }
                }

                TriggerAction[] actions = t.Actions ?? Array.Empty<TriggerAction>();
                for (int j = 0; j < actions.Length; j++)
                {
                    TriggerAction a = actions[j];
                    string ap = $"scenario.triggers[{i}].actions[{j}]";
                    if (!InSet(_actionTypes, a.Type))
                        return ValidationResult.Fail($"{ap}.type='{a.Type}' is not a known trigger action type.", validated);
                    string? fe = CheckFactionSlot($"{ap}.faction", a.Faction);
                    if (fe != null) return ValidationResult.Fail(fe, validated);
                    if (a.Type == "spawn_unit")
                    {
                        string? ce = CheckCoord($"{ap}.x", a.X, bounds) ?? CheckCoord($"{ap}.z", a.Z, bounds);
                        if (ce != null) return ValidationResult.Fail(ce, validated);
                        // Story 6.5: a spawn_unit trigger that would place a unit on a PAINTED blocked cell fails
                        // closed (same cell domain as the sim) — a spawned unit could never legally occupy it.
                        string? sbe = CheckNotBlocked(ap, "spawn_unit position", a.X, a.Z, painted);
                        if (sbe != null) return ValidationResult.Fail(sbe, validated);
                    }
                }
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
                if (!mr.Ok) return ValidationResult.Fail(mr.Errors[0].Message, validated);
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
                if (e != null) return ValidationResult.Fail(e, validated);
                // The FIELDS are non-negative, but the COMPOSED curve base + perLevel × level is evaluated at the hero's
                // level (up to MaxRevivableLevel) and quantizes to Fixed — so the curve AT MAX LEVEL must stay in the
                // 16.16 range, else the runtime cost/timer overflows and wraps negative (free-money / instant-revive, the
                // Story 3.13 overflow class the per-field non-neg check does NOT catch).
                if (RevivalCurveOverflows(r.CostOreBase, r.CostOrePerLevel))
                    return ValidationResult.Fail($"scenario.revival_rule ore cost (base {r.CostOreBase} + {r.CostOrePerLevel}/level) exceeds the 16.16 range [0, {Range}) at level {MaxRevivableLevel}.", validated);
                if (RevivalCurveOverflows(r.CostCrystalBase, r.CostCrystalPerLevel))
                    return ValidationResult.Fail($"scenario.revival_rule crystal cost (base {r.CostCrystalBase} + {r.CostCrystalPerLevel}/level) exceeds the 16.16 range [0, {Range}) at level {MaxRevivableLevel}.", validated);
                if ((double)r.TimeBaseSeconds + (double)r.TimePerLevelSeconds * MaxRevivableLevel >= Range)
                    return ValidationResult.Fail($"scenario.revival_rule time (base {r.TimeBaseSeconds} + {r.TimePerLevelSeconds}/level) exceeds the 16.16 range [0, {Range}) at level {MaxRevivableLevel}.", validated);
                // Reject a fraction that is positive-but-quantizes-to-Fixed.Zero (e.g. 1e-5) — the pre-quantization (0,1]
                // check alone would let it through and respawn a 0-HP dead-on-arrival hero (validate the QUANTIZED value).
                if (!Finite(r.ReviveHpFraction) || r.ReviveHpFraction <= 0f || r.ReviveHpFraction > 1f
                    || ProjectChimera.Core.Fixed.FromFloat(r.ReviveHpFraction) <= ProjectChimera.Core.Fixed.Zero)
                    return ValidationResult.Fail(
                        $"scenario.revival_rule.revive_hp_fraction={r.ReviveHpFraction} must be finite and in (0, 1] and quantize to a positive 16.16 value.", validated);
            }

            // ── Inventory slot count (Story 3.15, AR-39) — fail-closed when present so a hand-edited/cheat count outside
            // [1, INVENTORY_SLOTS] (a hero with 0 or > the physical stride of usable slots) is rejected. NULL ⇒ the full
            // stride (no rule to validate). Authoring-only — NOT folded into any checksum. ──
            if (m.InventorySlotCount is int isc
                && (isc < 1 || isc > ProjectChimera.Core.HeroStore.INVENTORY_SLOTS))
                return ValidationResult.Fail(
                    $"scenario.inventory_slot_count={isc} must be in [1, {ProjectChimera.Core.HeroStore.INVENTORY_SLOTS}].", validated);

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
                        return ValidationResult.Fail($"scenario.resources[{i}] is null.", validated);
                    if (string.IsNullOrWhiteSpace(r.Id))
                        return ValidationResult.Fail($"scenario.resources[{i}].id must be a non-empty id.", validated);
                    if (!resourceIds.Add(r.Id))
                        return ValidationResult.Fail(
                            $"scenario.resources[{i}].id='{r.Id}' is a duplicate.", validated);
                    string? e = CheckNonNeg($"scenario.resources[{i}].starting_amount", r.StartingAmount);
                    if (e != null) return ValidationResult.Fail(e, validated);
                    if (System.Array.IndexOf(ResourceDefinition.KnownCollectionModels, r.CollectionModel) < 0)
                        return ValidationResult.Fail(
                            $"scenario.resources[{i}].collection_model='{r.CollectionModel}' is not a known collection model " +
                            $"({string.Join("/", ResourceDefinition.KnownCollectionModels)}).", validated);
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
                if (e != null) return ValidationResult.Fail(e, validated);
                if (sc.HardCeiling is int ceiling)
                {
                    string? ce = CheckNonNeg("scenario.supply.hard_ceiling", ceiling);
                    if (ce != null) return ValidationResult.Fail(ce, validated);
                    if (ceiling < sc.StartingCap)
                        return ValidationResult.Fail(
                            $"scenario.supply.hard_ceiling={ceiling} must be >= scenario.supply.starting_cap={sc.StartingCap}.", validated);
                }
            }

            return ValidationResult.Pass(validated);
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

        /// <summary>Story 6.8 — a known building type is an EXACT <see cref="BuildingType"/> enum name (case-sensitive;
        /// rejects numeric strings) OR an authored building-def id present in <paramref name="ownerDef"/>'s
        /// <c>Buildings</c>. A null <paramref name="ownerDef"/> (no faction threaded, or an out-of-range slot) restricts
        /// to enum names only — byte-identical to the pre-6.8 gate. The bare <c>"Custom"</c> sentinel name is NOT a
        /// placeable identity (it resolves no def → a stat-less, unrendered ghost), so it is excluded from the enum-name
        /// match — a custom building must name its authored id; a lowercase authored id such as <c>"custom"</c> is still
        /// accepted through <paramref name="ownerDef"/>.</summary>
        private static bool IsKnownBuildingType(string? type, FactionDefinition? ownerDef = null)
        {
            if (type is null) return false;
            for (int i = 0; i < _buildingTypeNames.Length; i++)
                if (_buildingTypeNames[i] == type)
                    return type != nameof(BuildingType.Custom); // the Custom sentinel is not a placeable Type
            return ownerDef?.GetBuilding(type) != null;
        }

        /// <summary>Story 6.8 — resolve a pre-placed building's owner <see cref="FactionDefinition"/> from the
        /// per-slot defs (indexed by <c>(int)Faction</c> = slot+1). Null when no defs are threaded or the slot is out
        /// of range — the caller then falls back to enum-name-only building-type acceptance.</summary>
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

        // ── Trigger vocabulary (Story 1.11, AC3) — the CLOSED, typed sets ScenarioDirector actually handles.
        //    Each mirrors a switch in ScenarioDirector (EventMatches / EvalCondition / ExecuteActions / Compare);
        //    a value outside the set is silently inert at runtime, so the gate rejects it instead. Static =
        //    allocated once, so the per-trigger checks allocate nothing (mirrors _buildingTypeNames). ──
        private static readonly string[] _operators          = { ">", "<", ">=", "<=", "==", "!=" };
        private static readonly string[] _triggerEventTypes  = { "match_start", "unit_dies", "building_completed", "timer_expires", "resource_threshold", "unit_count_threshold" };
        private static readonly string[] _conditionTypes     = { "always", "building_exists", "resource_comparison", "unit_count", "variable_comparison", "unit_in_region" };
        private static readonly string[] _actionTypes        = { "spawn_unit", "display_message", "victory", "defeat", "create_timer", "add_resources", "set_variable", "play_sound" };

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
        /// <c>(Faction)(slot + 1)</c> and indexes the size-5 per-faction arrays, so an out-of-range slot crashes
        /// the tick (OOB) — exactly the engine ceiling the player-slot check enforces, reused here. Returns a
        /// located error or null when OK.
        /// </summary>
        private static string? CheckFactionSlot(string path, int slot)
        {
            if (slot < 0 || slot + 1 > (int)Faction.Player4)
                return $"{path}={slot} is out of the valid faction-slot range [0,{(int)Faction.Player4 - 1}].";
            return null;
        }
    }
}
