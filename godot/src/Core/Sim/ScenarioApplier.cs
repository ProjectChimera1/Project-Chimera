#nullable enable
using System;                            // StringComparison
using System.Collections.Generic;       // List<PlacedHero>
using ProjectChimera.Core;              // Faction, Fixed, FixedVec3, BuildingType, GatherState (parent namespace)
using ProjectChimera.Core.Definitions;  // ScenarioData & sub-types, FactionDefinition, UnitDefinition, Validated<T>, HeroProfileLoader.PlacedHero

namespace ProjectChimera.Core.Sim
{
    /// <summary>
    /// Net-new Godot-free SOLE WRITER of sim truth (Story 1.8b / AR-7). Absorbs the scenario-mutation logic that
    /// formerly lived inline in <c>MainScene</c> (<c>ApplyScenario</c>, <c>SpawnScenarioUnit</c>,
    /// <c>ParseBuildingType</c>, <c>ApplyFallbackScenario</c>) plus the sim-half <c>MoveStartPosition</c> faction-base
    /// write — so every sim-truth write for scenario setup funnels through ONE auditable, headless-testable path.
    ///
    /// <para>It <em>composes</em> the 1.8a <see cref="SimulationHost"/> (reading <c>host.World</c>/<c>Nodes</c>/
    /// <c>Resources</c>/<c>BuildSys</c>/<c>ScenarioDirector</c>), never subclasses or mutates it. It consumes only a
    /// <see cref="Validated{T}"/> token (the 1.7 gate) — a raw <see cref="ScenarioData"/> cannot reach a store. All
    /// Godot path resolution is hoisted to a presentation pre-pass that fills the shared
    /// <c>FactionDefinition?[]</c> BEFORE the applier runs, so this class carries zero <c>using Godot</c> /
    /// <c>GD.*</c> / <c>ProjectSettings</c> / <c>res://</c> and compiles into the Godot-free Tier-1 test project.
    /// <see cref="SpawnUnit"/> is allocation-free (pre-resolved def, no LINQ/closures/boxing). Reused verbatim and
    /// headless by <c>ServerBootstrap</c> (Story 1.9a).</para>
    ///
    /// <para>Behavior-preserving extraction: the relocated bodies keep the as-built <c>Fixed.FromFloat</c>
    /// load-time conversions exactly (the as-built <see cref="ScenarioData"/> is still <c>float</c>-typed; a
    /// <c>Fixed</c>-end-to-end model is a separate later migration — D2), pinned by the byte-identical
    /// golden-checksum suite.</para>
    /// </summary>
    public sealed class ScenarioApplier
    {
        private readonly SimulationHost _host;
        private readonly ILogSink _log;

        // The SAME array MainScene owns as _slotFactionDefs. The presentation pre-pass writes resolved defs into it
        // IN PLACE (never reassign, or the shared reference goes stale); Apply/SpawnUnit and the
        // runtime OnSpawnUnit trigger delegate all read this one array. (D1/D4)
        private readonly FactionDefinition?[] _slotFactionDefs;

        // Story 3.9: the placed HERO entities recorded during the last Apply (cleared at the start of Apply). The
        // init-time Apply UNITS LOOP appends one per scenario-placed UnitDefinition.IsHero unit — deliberately NOT the
        // shared SpawnUnit (which the runtime ScenarioDirector.OnSpawnUnit trigger delegate also calls, so a mid-match
        // hero spawn must NOT pollute this init-time record). MainScene reads this AFTER Apply and BEFORE
        // StartStateHash.Compute so HeroProfileLoader can mint a deployed profile into HeroStore for the right entities.
        // Additive-only — no spawn-behavior change.
        private readonly List<HeroProfileLoader.PlacedHero> _lastAppliedHeroes = new();

        // Story 6.3: the finalized-terrain elevation grid the Godot load-time step builds and injects (or null for a
        // flat/legacy map). Set BEFORE Apply so EntityWorld.Create samples it for every scenario-placed spawn. Stays
        // set on the world afterwards, so runtime SpawnUnitAt (trigger/hero respawn) spawns also sample it uniformly.
        private ElevationGrid? _elevationGrid;

        // Story 6.5: the load-time pathability grid (painted ∪ slope-derived blocked cells) the Godot load step
        // builds and injects (or null for a flat/legacy map with nothing blocked). Set BEFORE Apply so it is threaded
        // into EntityWorld before any spawn, letting the fixed sim tick keep units out of blocked cells.
        private ProjectChimera.Navigation.PathabilityGrid? _pathability;

        /// <summary>The hero entities placed during the most recent <see cref="Apply"/> (entity id + unit id), for
        /// Story 3.9's init-time hero mint. Cleared at the start of each <see cref="Apply"/>.</summary>
        public IReadOnlyList<HeroProfileLoader.PlacedHero> LastAppliedHeroes => _lastAppliedHeroes;

        /// <summary>
        /// Construct the applier over a wired 1.8a host.
        /// </summary>
        /// <param name="host">The Godot-free sim composition root whose stores this applier writes.</param>
        /// <param name="log">Injected log seam (NullLogSink for tests/server; GodotLogSink for MainScene). The
        /// applier's ONLY logging is low-frequency diagnostics (unknown unit_id) — never per-tick/per-entity.</param>
        /// <param name="slotFactionDefs">The SAME array MainScene holds as <c>_slotFactionDefs</c>. The presentation
        /// pre-pass writes resolved defs into it in place before Apply; SpawnUnit + the trigger
        /// delegate read it. Never reassigned here.</param>
        public ScenarioApplier(SimulationHost host, ILogSink log, FactionDefinition?[] slotFactionDefs)
        {
            _host = host;
            _log = log;
            _slotFactionDefs = slotFactionDefs;
        }

        /// <summary>
        /// Apply a validated scenario to the sim stores. The <see cref="Validated{T}"/> gate means a raw model
        /// cannot reach a store. Per-slot faction defs come from the constructor-injected <c>_slotFactionDefs</c>
        /// (filled by the presentation pre-pass). Order is part of the determinism contract:
        /// slots (faction def + ore + base) → resource nodes → buildings → units → triggers.
        /// </summary>
        /// <summary>
        /// Story 6.3: inject the finalized-terrain <see cref="ElevationGrid"/> (Godot-built at load time from the
        /// restored Terrain3D heightmap) BEFORE <see cref="Apply"/> spawns any unit. Null ⇒ flat/legacy (elevation
        /// stays <see cref="Fixed.Zero"/>). Godot-free type, so this setter keeps the applier Godot-free.
        /// </summary>
        public void SetElevationGrid(ElevationGrid? grid) => _elevationGrid = grid;

        /// <summary>
        /// Story 6.5: inject the load-time <see cref="ProjectChimera.Navigation.PathabilityGrid"/> (painted ∪
        /// slope-derived blocked cells, Godot-built at load) BEFORE <see cref="Apply"/>. Null ⇒ flat/legacy (blocking
        /// is a no-op). Godot-free type, so this setter keeps the applier Godot-free.
        /// </summary>
        public void SetPathabilityGrid(ProjectChimera.Navigation.PathabilityGrid? grid) => _pathability = grid;

        /// <summary>Story 6.3 / DW-157: the currently-injected finalized-terrain elevation grid (or null for a
        /// flat/legacy map). Exposed so the Edit→Play re-apply path (<c>MainScene.ResetToAuthoredStart</c>) can reuse
        /// the ALREADY-injected grid to re-derive slope-blocked cells without re-baking terrain (DW-157 is
        /// painted/prop/water only — terrain re-bake is out of scope).</summary>
        public ElevationGrid? ElevationGrid => _elevationGrid;

        /// <summary>
        /// DW-157 (Story 14.8) — THE single Godot-free derivation of the static <see cref="ProjectChimera.Navigation.PathabilityGrid"/>
        /// (painted ∪ slope-derived ∪ blocking-prop/water footprint blocked cells, or <c>null</c> when nothing is
        /// blocked). BOTH lifecycle paths route through this ONE recipe — the boot build
        /// (<c>ScenarioLoadPhase.BuildAndInjectPathabilityGrid</c>) and the Edit→Play F5 re-apply
        /// (<c>MainScene.ResetToAuthoredStart</c>) — so the two can never disagree on the blocked-cell set (the set
        /// <see cref="ProjectChimera.Core.Definitions.CanonicalModelHash"/> certified). Centralizing it here structurally
        /// prevents the DW-157 defect class (boot vs. re-apply drifting), mirroring how
        /// <c>PathabilityGrid.BuildBlockingFootprint</c> is the sole footprint recipe for load/hash/validator.
        ///
        /// <para>This is the single float→<see cref="Fixed"/> slope-threshold boundary: <c>Fixed.FromFloat</c> is applied
        /// to the slope threshold ONLY when slope-auto-block is on with a positive threshold (an inert config never
        /// touches it); the derivation itself is pure <see cref="Fixed"/>. The recipe lives in the applier (Core.Sim,
        /// which already references <see cref="ScenarioData"/>/<see cref="ElevationGrid"/>/<see cref="Fixed"/>/
        /// <c>PathabilityGrid</c>) so <c>PathabilityGrid</c> stays Godot- and Definitions-free (keeps taking primitive
        /// arrays). Never throws — <c>Resolve</c> degrades a flat/legacy map to <c>null</c>.</para>
        /// </summary>
        public static ProjectChimera.Navigation.PathabilityGrid? BuildPathabilityGrid(ScenarioData? s, ElevationGrid? elev)
        {
            bool slopeOn = s != null && s.SlopeAutoBlock && s.SlopeBlockThreshold > 0f;
            Fixed threshold = slopeOn ? Fixed.FromFloat(s!.SlopeBlockThreshold) : Fixed.Zero;
            bool[]? footprint = ProjectChimera.Navigation.PathabilityGrid.BuildBlockingFootprint(s?.Props, s?.Water);
            return ProjectChimera.Navigation.PathabilityGrid.Resolve(
                s?.PathabilityBlocked, s?.SlopeAutoBlock ?? false, threshold, elev, footprint);
        }

        public void Apply(Validated<ScenarioData> v)
        {
            ScenarioData s = v.Value; // as-built property name (NOT .Model)
            if (s is null)
            {
                // A default/unproven Validated<ScenarioData> carries a null model (the token-less Fail path used
                // by the null-model early-out). Reject it at the consumption point instead of NRE'ing on
                // s.PlayerSlots — closes the validation-bypass the Story 1.7 review deferred to 1.8b. FIRST, before
                // ANY store write (review follow-up: Configure/ConfigureSupply used to run before this guard, so
                // consuming a failed token silently reset the revival/supply config — a pure no-op now).
                _log.Warn("[ScenarioApplier] Apply received a Validated<ScenarioData> with a null model — skipped.");
                return;
            }
            _lastAppliedHeroes.Clear(); // Story 3.9: fresh record of placed heroes for this apply
            // Story 3.14: resolve the scenario's revival rule (or Default when omitted) into the shared runtime the sim
            // systems hold — the single float→Fixed boundary, done once at apply, never inside a tick.
            _host.RevivalRuntime.Configure(s.RevivalRule);
            // Story 4.4: resolve the scenario's supply config (or compile defaults when omitted) into ResourceStore —
            // unconditional call, mirroring the RevivalRuntime.Configure line above (the resolver, not the call
            // site, owns the null-means-default logic).
            _host.Resources.ConfigureSupply(s.Supply);
            // DW-941: resolve the authored building-placement gap onto the placement gate — the single
            // float→Fixed boundary, done once at apply (the ConfigureSupply pattern; the resolver owns
            // null-means-default and the clamp, and is the SAME one CanonicalModelHash folds).
            _host.BuildSys.MinBuildingGap =
                Fixed.FromFloat(ScenarioData.ResolveBuildingMinGap(s.BuildingMinGap));
            // DW-942: resolve the placement FOG rule onto the host for the presentation placement gate (ghost
            // tint + click refusal). Never a sim input — fog is per-viewer state; see the ScenarioData field doc.
            _host.PlacementFogRule = ScenarioData.ResolvePlacementFogRule(s.PlacementFogRule);

            // ── Story 6.3: thread the height-advantage vision toggle/bonus + the injected elevation grid into the
            //    EntityWorld BEFORE any spawn, so EntityWorld.Create (which every spawn path funnels through) samples
            //    the grid uniformly and EffectiveVisionRange sees the resolved toggle/bonus. The bonus is the single
            //    float→Fixed boundary here; the grid may be null (flat/legacy ⇒ Fixed.Zero elevation). Position.Y stays
            //    Fixed.Zero everywhere — elevation lives ONLY in the dedicated Elevation SoA array. ──
            _host.World.HeightAdvantageVision    = s.HeightAdvantageVision;
            _host.World.HeightVisionBonusPerStep = Fixed.FromFloat(s.HeightVisionBonusPerStep);
            _host.World.SetElevationGrid(_elevationGrid);
            // Story 6.5: thread the pathability grid into the EntityWorld BEFORE any spawn so the fixed sim tick
            // (MovementSystem) keeps live units out of blocked cells uniformly. Null ⇒ no blocking (byte-identical).
            _host.World.SetPathabilityGrid(_pathability);

            // ── DW-148: the load-time spawn-in-blocked-cell guard. The pre-tick ScenarioValidator gate only sees the
            //    AUTHORED blocked layers (painted ∪ prop/water) — slope-DERIVED cells need the terrain heightmap, which
            //    does not exist at that Godot-free gate — so a start base / unit / building / node / spawn_unit trigger
            //    on a slope-auto-blocked cell shipped un-caught. THIS is the first point in the load where the resolved
            //    union grid and the model sit side by side, and every lifecycle path funnels through here (boot,
            //    Edit→Play re-apply, ServerBootstrap), so the check belongs at this chokepoint.
            //    It is a LOUD LOCATED DIAGNOSTIC, deliberately not a second gate: the model already carries a
            //    validation proof, and silently refusing to spawn authored content would be strictly worse than
            //    reporting a bad placement (MovementSystem now confines such a unit to its own cell rather than letting
            //    it walk through the terrain). Pure read — a null/all-clear grid is an exact no-op, so no flat/legacy
            //    map (and no golden) changes behavior. ──
            string? blockedSpawn = ScenarioValidator.CheckSpawnsNotBlocked(s, _pathability);
            if (blockedSpawn != null)
                _log.Warn($"[ScenarioApplier] DW-148 pathability guard: {blockedSpawn}");

            // ── 1. Player slots: faction def + starting ore + base deposit point ─
            foreach (var slot in s.PlayerSlots ?? System.Array.Empty<ScenarioPlayerSlot>())
            {
                var faction = (Faction)(slot.Slot + 1); // slot 0 → Player1, slot 1 → Player2
                if (!InFactionRange(faction))
                {
                    // (Faction)(slot+1) is an UNCHECKED enum cast; shadow mode (the default) applies models that
                    // FAILED validation (D3), so an out-of-range slot can reach here. Skip + warn instead of
                    // indexing the faction-keyed stores out of bounds (restores the pre-1.8b MainScene guard).
                    _log.Warn($"[ScenarioApplier] player_slot.slot={slot.Slot} maps to an out-of-range faction — skipped.");
                    continue;
                }
                var def = _slotFactionDefs[(int)faction]; // pre-resolved by the presentation pre-pass
                if (def != null) _host.BuildSys.SetFactionDef(faction, def);

                _host.Resources.AddOre(faction, Fixed.FromFloat(slot.StartOre));
                _host.Resources.AddCrystal(faction, Fixed.FromFloat(slot.StartCrystal));
                SetFactionBase(faction, new FixedVec3(
                    Fixed.FromFloat(slot.BaseX), Fixed.Zero, Fixed.FromFloat(slot.BaseZ)));
            }

            // ── Story 9.14: seed the sim-owned AllianceStore team-id mask from the scenario's per-slot teams, BEFORE
            //    tick 0. Done HERE (the sole Godot-free sim-truth writer) rather than only in MainScene so EVERY apply
            //    path seeds identically — boot, the Edit→Play re-apply, AND the headless dedicated server
            //    (ServerBootstrap). The server folds AllianceStore into SimChecksum (v20) exactly like the clients, so
            //    seeding must be shared or a teamed match would desync server-vs-client. Seed RESTORES FFA first, so it
            //    composes with ClearForReset's Alliances.Clear() on the reset path (Clear precedes this re-seed). FFA
            //    (every Team==0) leaves the mask at the default TeamId[f]==f — byte-identical to pre-9.14. ──
            AllianceSeeder.Seed(_host.Alliances, s);

            // ── 2. Resource nodes (Story 4.7: collection model / resource type / requires_structure / owner / income) ─
            foreach (var node in s.ResourceNodes ?? System.Array.Empty<ScenarioResourceNode>())
            {
                var pos = new FixedVec3(Fixed.FromFloat(node.X), Fixed.Zero, Fixed.FromFloat(node.Z));

                // owner_slot -1 (unset — the default for GATHER/Streaming, which credit the gathering worker's own
                // faction) or out-of-range has no valid Faction mapping; degrade to Neutral rather than an unchecked
                // (Faction)(slot+1) OOB write (mirrors the InFactionRange guard the units/buildings loops use).
                // Neutral is a safe sentinel here: it is consulted only by the Income pass (validator requires
                // owner_slot when collection_model=Income) and the requires_structure gate, both of which simply
                // never match a Neutral-owned structure/credit target — no OOB, no silent wrong-faction credit.
                Faction ownerFaction = Faction.Neutral;
                if (node.OwnerSlot >= 0)
                {
                    var candidate = (Faction)(node.OwnerSlot + 1);
                    if (InFactionRange(candidate))
                    {
                        ownerFaction = candidate;
                    }
                    else
                    {
                        // Review patch: match the diagnostic the units/buildings loops already emit for the
                        // identical out-of-range-slot condition (only shadow-mode reachable — the validator
                        // requires a declared, in-range owner_slot whenever collection_model=Income).
                        _log.Warn($"[ScenarioApplier] resource_node.owner_slot={node.OwnerSlot} maps to an out-of-range faction — degraded to Neutral.");
                    }
                }

                // DW-230: check the -1 full-store sentinel from Nodes.Create (formerly discarded, so an overflow node
                // vanished silently). Warn + skip, mirroring the item-store guard below. The validator's resource_nodes
                // count cap makes this unreachable on a gate-passed path; it is belt-and-suspenders for shadow/direct
                // callers. Node placement stays in AUTHORED order (out of DW-37 scope — nodes are not ref-addressed).
                int nodeSlot = _host.Nodes.Create(pos, Fixed.FromFloat(node.Supply), Fixed.FromFloat(node.Rate), node.MaxGatherers,
                    ParseCollectionModel(node.CollectionModel),
                    ParseResourceType(node.ResourceType),
                    string.IsNullOrEmpty(node.RequiresStructure) ? null : node.RequiresStructure,
                    Fixed.FromFloat(node.RequiresStructureRadius),
                    ownerFaction,
                    node.IncomePeriodTicks);
                if (nodeSlot < 0)
                    _log.Warn($"[ScenarioApplier] resource_node at ({node.X}, {node.Z}) could not be placed — the resource-node store is full (> {ResourceNodeStore.MAX_NODES}) — skipped.");
            }

            // ── 3. Buildings ──────────────────────────────────────────────────
            // Story 7.11: capture each placed building's BuildingStore slot, index-aligned to the authored
            // Buildings array, so a Landmark-Destruction preset can resolve its structure_index to a runtime slot.
            ScenarioBuilding[] buildingsArr = s.Buildings ?? System.Array.Empty<ScenarioBuilding>();
            int[] buildingSlots = new int[buildingsArr.Length];
            // DW-37: PLACE buildings in the SAME canonical key order CanonicalModelHash sorts by, so the assigned
            // BuildingStore slots (runtime refs) become a deterministic function of the order-independent set — two
            // scenarios with the same buildings in different array order Create in identical slot order. The bodies
            // still write buildingSlots[bi] by the AUTHORED index bi (Story 7.11 structure_index resolves against it),
            // so only the CREATE order changes. A fixture already authored in canonical order yields the identity
            // permutation (see CanonicalBuildingOrder) → byte-identical Create order → no golden move.
            foreach (int bi in CanonicalBuildingOrder(buildingsArr))
            {
                var b = buildingsArr[bi];
                var faction = (Faction)(b.Slot + 1);
                var pos     = new FixedVec3(Fixed.FromFloat(b.X), Fixed.Zero, Fixed.FromFloat(b.Z));
                // Story 6.8: b.Type is "legacy enum name OR authored building-def id". Take the byte-identical
                // PlaceBuildingDirect path ONLY for an exact, case-sensitive, DEFINED, non-numeric enum NAME that is
                // not the Custom sentinel: Enum.TryParse also parses numeric strings ("5"→Custom, "99"→undefined) and
                // the case-sensitive/PascalCase-vs-snake_case disjointness makes real enum names unambiguous. The
                // `bType.ToString() == b.Type` guard rejects any numeric spelling (an authored id like "5" then routes
                // by-id), IsDefined rejects out-of-range numerics, and `!= Custom` keeps the bare "Custom" sentinel off
                // the def-less direct path (the validator already fails a bare "Custom" closed — this is belt-and-braces
                // for shadow-mode/direct callers). Anything else is an authored id → the by-id placement path
                // (BuildingType.Custom or a snake_case built-in id). The former CommandCenter-swallowing default of
                // ParseBuildingType no longer eats a custom id.
                // DW-230: capture the placement return into a local so the -1 full-store sentinel can be diagnosed
                // (warn), mirroring the item-store guard below, before it is recorded into buildingSlots[bi]. The
                // validator's building count cap makes this unreachable on a gate-passed path; it is belt-and-suspenders
                // for shadow/direct callers. A -1 slot is recorded verbatim — WinConditionSystem treats it as unresolved.
                int placedSlot;
                if (Enum.TryParse<BuildingType>(b.Type, out var bType)
                    && Enum.IsDefined(typeof(BuildingType), bType)
                    && bType.ToString() == b.Type
                    && bType != BuildingType.Custom)
                    placedSlot = _host.BuildSys.PlaceBuildingDirect(bType, faction, pos, b.PreBuilt);
                else
                    placedSlot = _host.BuildSys.PlaceBuildingDirectById(b.Type, faction, pos, b.PreBuilt);
                if (placedSlot < 0)
                    _log.Warn($"[ScenarioApplier] Scenario building '{b.Type}' (slot {b.Slot}) could not be placed — the building store is full (> {BuildingStore.MAX_BUILDINGS}) — recorded as unresolved.");
                buildingSlots[bi] = placedSlot;
            }

            // DW-922 — report what actually landed in the store, per faction. A player reported starting without a
            // command center on BOTH screens; the scenario authored one per slot, the applier maps slot→faction as
            // (Slot + 1), and the renderer is symmetric for Player1/Player2, so nothing in the code reproduces it and
            // there was no way to tell a MISSING building from a merely UNSEEN one after the fact. One line at apply
            // time is the difference between "did it spawn?" being a hypothesis and a fact — the DW-918 lesson,
            // applied before the next run rather than after it. Apply-time only (never per tick), and it reads
            // already-placed state, so it cannot affect the sim or SimChecksum.
            if (buildingsArr.Length > 0)
            {
                var perFaction = new System.Text.StringBuilder();
                for (int f = 1; f <= 2; f++)
                {
                    int placed = 0, commandCenters = 0;
                    for (int slot = 0; slot < _host.Buildings.Count; slot++)
                    {
                        if (!_host.Buildings.Alive[slot]) continue;
                        if ((int)_host.Buildings.FactionOf[slot] != f) continue;
                        placed++;
                        if (_host.Buildings.Type[slot] == BuildingType.CommandCenter) commandCenters++;
                    }
                    perFaction.Append($" Player{f}={placed} building(s), {commandCenters} CC;");
                }
                _log.Info($"[ScenarioApplier] Placed {buildingsArr.Length} authored building(s) →{perFaction}");
            }

            // ── 4. Units ──────────────────────────────────────────────────────
            // Story 7.11: capture each placed unit's spawned entity id, index-aligned to the authored Units array,
            // so an Assassination preset can resolve its leader_unit_index to a runtime entity id (-1 = not spawned).
            ScenarioUnit[] unitsArr = s.Units ?? System.Array.Empty<ScenarioUnit>();
            int[] unitEntityIds = new int[unitsArr.Length];
            // DW-37: SPAWN units in the SAME canonical key order CanonicalModelHash sorts by, so the assigned
            // EntityWorld ids (runtime refs) become a deterministic function of the order-independent set — two
            // scenarios with the same units in different array order spawn identical ids. The body still writes
            // unitEntityIds[ui] by the AUTHORED index ui (Story 7.11 leader_unit_index resolves against it), so only
            // the SPAWN order changes. A fixture already authored in canonical order yields the identity permutation
            // (see CanonicalUnitOrder) → byte-identical spawn order → no golden move.
            foreach (int ui in CanonicalUnitOrder(unitsArr))
            {
                unitEntityIds[ui] = -1;
                var u = unitsArr[ui];
                var faction = (Faction)(u.Slot + 1);
                // Look up def from the per-slot faction definition resolved by the pre-pass. Bounds-guard the
                // UNCHECKED (Faction) cast — a shadow-applied invalid model (D3) may carry an out-of-range slot.
                var def = InFactionRange(faction) ? _slotFactionDefs[(int)faction]?.GetUnit(u.UnitId) : null;
                if (def == null)
                {
                    _log.Warn($"[ScenarioApplier] Scenario unit_id '{u.UnitId}' not found in faction (or out-of-range slot) — skipped.");
                    continue;
                }
                int spawnedId = SpawnUnit(def, faction, u.X, u.Z);
                unitEntityIds[ui] = spawnedId;

                // Story 3.9: record a scenario-PLACED hero (init-time only) so MainScene/HeroPickerPhase can mint a
                // deployed PlayerProfile into HeroStore before the start-state hash. Recorded HERE (not in the shared
                // SpawnUnit) so the runtime OnSpawnUnit trigger delegate never appends to the init-time record. A
                // non-hero same-id unit is never recorded, so it can never receive hero state (D-3).
                if (spawnedId >= 0 && def.IsHero)
                {
                    // Story 3.13: capture the def-derived leveling curve / growth / share constants here — the SINGLE
                    // float→Fixed load boundary for hero curves (never quantized inside a tick). def.Hero is coupled to
                    // IsHero by the validator, but null-guard defensively (a degenerate unit yields zero curve → no leveling).
                    HeroDefinition? hd = def.Hero;
                    // Story 15-21: flatten the faction's attribute model × this hero's authored attributes into
                    // per-stat contribution pairs at the SAME single boundary (HeroAttributeResolver — a null model
                    // or attribute block yields all zeros, byte-identical to a pre-15-21 hero).
                    var (attrBase, attrPerLevel) = HeroAttributeResolver.Resolve(
                        InFactionRange(faction) ? _slotFactionDefs[(int)faction]?.AttributeModel : null,
                        hd?.Attributes);
                    _lastAppliedHeroes.Add(new HeroProfileLoader.PlacedHero(
                        spawnedId, def.Id,
                        hd?.MaxLevel ?? 0,
                        Fixed.FromFloat(hd?.BaseXp ?? 0f),
                        Fixed.FromFloat(hd?.XpGrowth ?? 0f),
                        Fixed.FromFloat(hd?.XpShareRadius ?? 0f),
                        Fixed.FromFloat(hd?.HealthPerLevel ?? 0f),
                        Fixed.FromFloat(hd?.DamagePerLevel ?? 0f),
                        Fixed.FromFloat(hd?.ArmorPerLevel ?? 0f),
                        def,        // Story 3.14: the respawn def (a revival re-spawns a fresh entity from it)
                        faction,    // Story 3.14: the owning faction (revive-order anti-cheat + respawn ownership)
                        // DW-26: resolve the per-hero XP-gain multiplier here at the single float→Fixed boundary. Default
                        // 100 (or a null hero-def) → 100/100 = an exact ×1.0 in 16.16 (Fixed.One is raw 65536), so every
                        // existing hero credits the full victim bounty unchanged — no golden move, no SimChecksum fold.
                        Fixed.FromFloat((hd?.XpPerKill ?? 100f) / 100f),
                        attrBase, attrPerLevel)); // Story 15-21
                }
            }

            // ── 4b. Items (Story 3.15) — place ground items + configure the usable inventory-slot cap ────────────
            // Configure the per-scenario usable inventory count (NULL ⇒ the full HeroStore.INVENTORY_SLOTS stride).
            _host.ItemSys.ConfigureUsableSlots(s.InventorySlotCount ?? HeroStore.INVENTORY_SLOTS);
            // DW-37: CREATE ground items in the SAME canonical key order StartStateHash sorts by, so the assigned
            // ItemStore slots / packed refs become a deterministic function of the order-independent set — a
            // PickupItem/inventory ref then resolves to the same physical item on every peer. Order-only change; a
            // fixture already authored in canonical order yields the identity permutation → no golden move.
            ScenarioItem[] itemsArr = s.Items ?? System.Array.Empty<ScenarioItem>();
            foreach (int ii in CanonicalItemOrder(itemsArr))
            {
                var it = itemsArr[ii];
                int defId = _host.ItemRegistry.IndexOf(it.ItemId);
                if (defId < 0)
                {
                    _log.Warn($"[ScenarioApplier] Scenario item_id '{it.ItemId}' not found in the item registry — skipped.");
                    continue;
                }
                var pos = new FixedVec3(Fixed.FromFloat(it.X), Fixed.Zero, Fixed.FromFloat(it.Z));
                int itemSlot = _host.Items.Create(defId, _host.ItemRegistry.Get(defId).Charges, pos);
                if (itemSlot < 0)
                {
                    // Store full (> MAX_ITEMS) — warn + skip rather than silently discard (mirrors the IndexOf < 0 path above).
                    _log.Warn($"[ScenarioApplier] Scenario item_id '{it.ItemId}' could not be placed — the item store is full (> {ItemStore.MAX_ITEMS}) — skipped.");
                    continue;
                }
            }

            // ── 4c. Regions (Story 6.4) — resolve authored float rects → Fixed ONCE here (the single conversion
            //    boundary) into a Godot-free RegionStore, and hand it to the ScenarioDirector BEFORE LoadScenario so
            //    the unit_in_region condition can scan it. Static authored data (never mutated mid-match) ⇒ not in
            //    SimChecksum. An absent/empty Regions collection resolves to RegionStore.Empty (no allocation, no
            //    behavior change) so every pre-6.4 scenario is byte-identical. ──
            RegionStore regionStore = BuildRegionStore(s.Regions);
            _host.ScenarioDirector.SetRegionStore(regionStore);

            // ── 4d. Win condition (Story 7.11) — resolve the applied scenario's built-in enum / T1 preset into the
            //    sim-layer WinConditionSystem, sharing the SAME RegionStore the director scans (mirrors SetRegionStore)
            //    and the placement→runtime-id maps captured above. Resolved BEFORE LoadScenario, alongside regions. ──
            _host.WinCon.Configure(s, regionStore, unitEntityIds, buildingSlots);

            // ── 5. Triggers ────────────────────────────────────────────────────
            _host.ScenarioDirector.LoadScenario(s); // triggers last (same as today)
        }

        /// <summary>
        /// Story 6.4: build the resolved <see cref="RegionStore"/> from the authored <see cref="ScenarioRegion"/>
        /// rows — the SINGLE float→<see cref="Fixed"/> boundary for region bounds (one <see cref="Fixed.FromFloat"/>
        /// per corner). Null/empty ⇒ <see cref="RegionStore.Empty"/> (the common case allocates nothing).
        /// Story 7.7 review: the defensive skips below (null row / non-finite corner / post-quantize degenerate)
        /// are POST-GATE defense-in-depth — the fail-closed validator rejects all three before any Apply, so the
        /// sanctioned flow can no longer reach them; they stay because Apply is also callable by direct/headless
        /// hosts. <c>internal</c> (not private) so the Tier-1 suite pins the skip behavior directly.
        /// </summary>
        internal static RegionStore BuildRegionStore(ScenarioRegion[]? regions)
        {
            if (regions is null || regions.Length == 0) return RegionStore.Empty;
            // Review patch: defensively SKIP any region that is (a) null, (b) has a non-finite corner, or (c)
            // collapses to a degenerate/inverted FixedRect at the float→Fixed boundary — so a `"regions":[null]`
            // shadow-mode apply cannot NRE, and a rect that passed the float-domain validator but degenerated at
            // quantization cannot corrupt the store. float.IsFinite is used ONLY here, at the sanctioned load-time
            // float→Fixed boundary (never on a tick path). ids/rects are appended TOGETHER so the parallel-array
            // index alignment stays exact; the store ends up holding only well-formed rects.
            var ids   = new List<string>(regions.Length);
            var rects = new List<FixedRect>(regions.Length);
            for (int i = 0; i < regions.Length; i++)
            {
                ScenarioRegion r = regions[i];
                if (r is null) continue;
                if (!float.IsFinite(r.MinX) || !float.IsFinite(r.MinZ)
                    || !float.IsFinite(r.MaxX) || !float.IsFinite(r.MaxZ)) continue;
                var rect = new FixedRect(
                    Fixed.FromFloat(r.MinX), Fixed.FromFloat(r.MinZ),
                    Fixed.FromFloat(r.MaxX), Fixed.FromFloat(r.MaxZ));
                if (rect.MinX >= rect.MaxX || rect.MinZ >= rect.MaxZ) continue; // degenerate/inverted post-conversion
                ids.Add(r.Id);
                rects.Add(rect);
            }
            if (ids.Count == 0) return RegionStore.Empty;
            return new RegionStore(ids.ToArray(), rects.ToArray());
        }

        /// <summary>
        /// True when <paramref name="faction"/> is a valid index into the faction-keyed stores
        /// (<c>_slotFactionDefs</c> / <c>Resources.Ore</c> / <c>Resources.FactionBase</c>, all length-5). The
        /// <c>(Faction)(slot + 1)</c> casts in <see cref="Apply"/> are unchecked, and shadow mode (D3) applies
        /// models that failed validation, so an out-of-range slot can reach the apply loops — this guard turns
        /// that into a logged skip instead of an <see cref="System.IndexOutOfRangeException"/>.
        /// </summary>
        private bool InFactionRange(Faction faction)
        {
            int fIdx = (int)faction;
            return fIdx >= 0 && fIdx < _slotFactionDefs.Length;
        }

        /// <summary>
        /// Story 7.7 — the hardcoded fallback SCENARIO MODEL (mirrors alpha_map_01.json so the game is always
        /// playable when the scenario file is missing, unparseable, or rejected by the validator). The legacy
        /// un-tokened <c>ApplyFallback()</c> writer is RETIRED: every fallback boot now routes
        /// <c>Apply(Validate(BuildFallbackMirror()).Value)</c> — one writer path, one token type — with behavior
        /// parity to the legacy writer pinned by <c>FallbackMirrorParityTests</c> (SimChecksum + key world facts).
        ///
        /// <para><b>Relationship to alpha_map_01.json (DW-222 / DW-324 / DW-514 — reconciled 2026-08-06).</b> This model
        /// was SEEDED from that map and the two had DRIFTED apart: the shipped map's start positions carried editor-drag
        /// residue (asymmetric sub-unit float noise at ±38.9 with non-zero Z) and it had gained a slot-0 `mage`, so the
        /// fallback boot and the default map described DIFFERENT starting states — "the game is always playable" and
        /// "the default map" exercised different scenarios. DW-514's recorded decision (2026-08-04) cleaned the map:
        /// its bases are back at the symmetric authored ±45 / 0 (the same tiles its own pre-built command centres sit
        /// on) and the extra mage is gone, so the two now describe the SAME scenario again.
        ///
        /// This is still an INDEPENDENT, always-valid safety net rather than a generated copy of a shipped map — the
        /// mirror must keep validating even if that map is edited or deleted — but the agreement is now ASSERTED, not
        /// assumed: <c>FallbackMirrorParityTests.FallbackMirror_AgreesWithAlphaMap01_OnTheSharedEconomyLiterals</c>
        /// pins map bounds, win condition, per-slot start ore/crystal, the 8 resource nodes, the 2 pre-built command
        /// centres, AND (DW-514) the per-slot start positions and the pre-placed unit roster. Editing either side
        /// alone turns Tier-1 red. The mirror's own base literals are additionally pinned by
        /// <c>FallbackMirror_StartPositions_MatchTheScenarioLoadPhaseMarkerFallback</c>, which is what
        /// <c>ScenarioLoadPhase</c>'s marker fallback duplicates.
        ///
        /// One deliberate asymmetry remains and is NOT a drift: the mirror resolves each slot's worker unit_id BY
        /// CATEGORY from the threaded faction defs (see <see cref="WorkerIdForSlot"/>), so with a custom faction its
        /// ids differ from the map's literal <c>"worker"</c>. The roster pin therefore compares the NO-ARGS mirror,
        /// which is the conventional-id baseline both shipped factions satisfy.</para>
        /// </summary>
        /// <param name="slotFactionDefs">Optional (review follow-up) — the per-slot resolved faction defs (indexed
        /// by <c>(int)Faction</c>, the same length-5 array the applier holds). When threaded, each slot's worker
        /// unit_id is resolved BY CATEGORY from its faction def (the legacy writer's <c>GetUnitByCategory("Worker")</c>
        /// lookup), so a custom faction whose worker is not literally id'd "worker" still spawns workers on the
        /// fallback boot; null (tests / no defs yet) falls back to the conventional "worker" id both shipped
        /// factions declare.</param>
        public static ScenarioData BuildFallbackMirror(
            System.Collections.Generic.IReadOnlyList<FactionDefinition?>? slotFactionDefs = null) => new ScenarioData
        {
            Id           = "fallback",
            DisplayName  = "Fallback",
            MapBounds    = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, StartOre = 200f, StartCrystal = 100f, BaseX = -45f, BaseZ = 0f },
                new ScenarioPlayerSlot { Slot = 1, StartOre = 200f, StartCrystal = 100f, BaseX =  45f, BaseZ = 0f },
            },
            ResourceNodes = new[]
            {
                new ScenarioResourceNode { X = -20f, Z = -15f, Supply = 600f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X = -20f, Z =  15f, Supply = 600f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X =  20f, Z = -15f, Supply = 600f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X =  20f, Z =  15f, Supply = 600f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X =   0f, Z = -25f, Supply = 400f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X =   0f, Z =  25f, Supply = 400f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X = -35f, Z =   0f, Supply = 300f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X =  35f, Z =   0f, Supply = 300f, Rate = 5f, MaxGatherers = 4 },
            },
            Buildings = new[]
            {
                new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = -45f, Z = 0f, PreBuilt = true },
                new ScenarioBuilding { Type = "CommandCenter", Slot = 1, X =  45f, Z = 0f, PreBuilt = true },
            },
            Units = FallbackWorkerRows(slotFactionDefs),
        };

        /// <summary>
        /// DW-652 — build the fallback mirror's pre-placed worker rows: two per player slot, but ONLY for a slot whose
        /// worker id actually resolves (see <see cref="WorkerIdForSlot"/>).
        ///
        /// <para><b>Why the rows are conditional.</b> The mirror is the last-resort safety net: it is validated through
        /// the same fail-closed gate as any scenario, and a REJECTED mirror applies NOTHING — an empty world
        /// (<c>ScenarioLoadPhase.ApplyFallbackThroughApplier</c>, <c>MainScene.ResetToAuthoredStart</c>). Naming a unit
        /// id no threaded faction declares is the one way this engine-authored model can fail that gate, so a slot with
        /// no resolvable unit contributes NO unit rows instead of an unresolvable one: the fallback board still boots
        /// with its bases, resources and command centres (playable, minus that slot's two starting workers) rather than
        /// collapsing to nothing.</para>
        ///
        /// <para>Unchanged on every real path: with no defs threaded (tests, the default) each slot yields the
        /// conventional <c>"worker"</c> id, and with the shipped alpha/beta factions each yields its Worker-category id
        /// — four rows in the same order and at the same coordinates as before, so the mirror's canonical hash and the
        /// fallback-parity pins do not move.</para>
        /// </summary>
        private static ScenarioUnit[] FallbackWorkerRows(
            System.Collections.Generic.IReadOnlyList<FactionDefinition?>? slotFactionDefs)
        {
            var rows = new List<ScenarioUnit>(4);
            AddSlotWorkers(rows, slotFactionDefs, slot: 0, x: -42f);
            AddSlotWorkers(rows, slotFactionDefs, slot: 1, x:  42f);
            return rows.ToArray();

            static void AddSlotWorkers(List<ScenarioUnit> into,
                System.Collections.Generic.IReadOnlyList<FactionDefinition?>? defs, int slot, float x)
            {
                string? id = WorkerIdForSlot(defs, slot);
                if (id == null) return; // no resolvable unit for this slot ⇒ omit rather than author a dangling ref
                into.Add(new ScenarioUnit { UnitId = id, Slot = slot, X = x, Z = -3f });
                into.Add(new ScenarioUnit { UnitId = id, Slot = slot, X = x, Z =  3f });
            }
        }

        /// <summary>Resolve a fallback-mirror slot's worker unit_id by CATEGORY from its faction def (the legacy
        /// writer's lookup), falling back to the conventional "worker" id when no defs are threaded.
        ///
        /// <para>DW-652 — the degenerate fallback is hardened. The old body ended in <c>?? "worker"</c> for EVERY miss,
        /// so a threaded faction declaring no Worker-category unit put the literal id <c>"worker"</c> into the mirror
        /// even when its roster has no such unit; the mirror then failed the fail-closed unit_id gate and the fallback
        /// boot applied NOTHING (empty world). The literal is now only used when it is safe: with NO def threaded
        /// (nothing to resolve against — the gate's own amnesty), or when the roster really declares it. Otherwise the
        /// first declared unit is preferred (any resolvable unit beats a dangling reference), and a faction with no
        /// usable unit at all returns <c>null</c> so the caller omits the rows entirely.</para>
        ///
        /// <para>Deterministic and Godot-free: category match first (the legacy lookup, unchanged for both shipped
        /// factions), then the conventional id, then the first non-null declared unit in authored order.</para>
        /// </summary>
        private static string? WorkerIdForSlot(
            System.Collections.Generic.IReadOnlyList<FactionDefinition?>? slotFactionDefs, int slot)
        {
            int fIdx = slot + 1; // (Faction)(slot + 1), matching Apply's cast
            FactionDefinition? def = (slotFactionDefs != null && fIdx >= 0 && fIdx < slotFactionDefs.Count)
                ? slotFactionDefs[fIdx] : null;
            if (def == null) return "worker"; // nothing threaded ⇒ the conventional id (the gate no-ops with no roster)

            string? byCategory = def.GetUnitByCategory("Worker")?.Id;
            if (!string.IsNullOrEmpty(byCategory)) return byCategory;
            if (def.GetUnit("worker") != null) return "worker"; // declared but uncategorized — still resolves
            // Last resort: the first declared unit that can actually resolve (null list / null elements / blank ids
            // skipped, mirroring the DW-103 hardening on the other FactionDefinition accessors).
            if (def.Units != null)
                foreach (UnitDefinition? u in def.Units)
                    if (u != null && !string.IsNullOrEmpty(u.Id)) return u.Id;
            return null; // no resolvable unit — the caller omits this slot's rows rather than dangle a reference
        }

        /// <summary>
        /// Spawn a unit from a <see cref="UnitDefinition"/>, wiring all SoA fields. The single alloc-free spawn
        /// primitive shared by <see cref="Apply"/> and the runtime
        /// <c>ScenarioDirector.OnSpawnUnit</c> trigger delegate (D5). Returns the new entity id, or -1 if the world
        /// is full. Allocation-free: pre-resolved def, value-type structs, no LINQ/closures/boxing/string alloc.
        /// </summary>
        public int SpawnUnit(UnitDefinition def, Faction faction, float x, float z)
            => SpawnUnitAt(def, faction, Fixed.FromFloat(x), Fixed.FromFloat(z));

        /// <summary>
        /// Story 3.14: the <see cref="Fixed"/>-native spawn primitive — the shared body of <see cref="SpawnUnit"/> AND
        /// the revive-respawn path (a hero respawns at a building's <see cref="Fixed"/> position; the float path would
        /// round-trip float→Fixed→float→Fixed and risk a 1-raw drift). Named distinctly (not an overload) because
        /// <c>Fixed</c> has an implicit <c>int</c> conversion that would make integer-literal <c>SpawnUnit</c> calls
        /// ambiguous. <see cref="SpawnUnit"/> delegates here after its single <c>Fixed.FromFloat</c>, so its callers stay
        /// byte-identical. Never duplicates <see cref="EntityWorld.ApplyUnitDefinition"/> — it reuses the one mapper.
        /// </summary>
        public int SpawnUnitAt(UnitDefinition def, Faction faction, Fixed x, Fixed z)
        {
            var pos = new FixedVec3(x, Fixed.Zero, z);
            var world = _host.World;
            int id  = world.Create(pos, faction,
                                   Fixed.FromFloat(def.Hp), Fixed.FromFloat(def.Speed));
            if (id < 0) return id;

            // Copy the definition's per-entity fields (combat stats, supply, + the Story 1.13 separation/formation
            // fields with the documented collision-radius clamp) via the SINGLE shared mapper, so this path and the
            // live spawn paths (building production, editor placement) can never again drift apart on a per-unit field.
            world.ApplyUnitDefinition(id, def);

            // Presentation: tag the unit type so MultiMeshBridge renders the right mesh. MeshType is a byte
            // excluded from the determinism checksum; the index comes from the pre-resolved faction def.
            int fIdx     = (int)faction;
            var fdef     = (fIdx >= 0 && fIdx < _slotFactionDefs.Length) ? _slotFactionDefs[fIdx] : null;
            int meshType = fdef?.IndexOfUnit(def.Id) ?? -1;
            world.MeshType[id] = (byte)(meshType < 0 ? 0 : meshType);

            // Workers need gatherer state; combat units stay at default (Idle command)
            if (string.Equals(def.Category, "Worker", StringComparison.OrdinalIgnoreCase))
            {
                world.GatherState[id]   = GatherState.Idle;
                world.CarryCapacity[id] = Fixed.FromFloat(20f);
            }

            // NOTE (Story 3.9): hero recording is intentionally NOT here — this primitive is shared with the runtime
            // ScenarioDirector.OnSpawnUnit trigger delegate. The init-time hero record is populated by the Apply units
            // loop above, so a mid-match trigger spawn can never pollute LastAppliedHeroes.
            return id;
        }

        /// <summary>
        /// The single writer of a faction's deposit / rally base point (D6). Both former write sites
        /// (<c>ApplyScenario</c>'s slot loop and the editor's <c>MoveStartPosition</c>) route through here, so after
        /// 1.8b no MainScene code writes <c>Resources.FactionBase</c> directly — the invariant 1.8c's diff asserts.
        /// </summary>
        public void SetFactionBase(Faction faction, FixedVec3 pos) =>
            _host.Resources.FactionBase[(int)faction] = pos;

        // ── DW-37: canonical placement-order permutations ────────────────────────────────────────────────────────
        // Each helper returns an authored-index permutation sorted by the EXACT keys the corresponding hash sorts by
        // (Units/Buildings → CanonicalModelHash; Items → StartStateHash), with the AUTHORED INDEX as the final
        // tiebreaker. That last tiebreaker makes the order a STRICT TOTAL order deterministic across runtimes (no
        // reliance on Array.Sort stability / platform internals) AND makes an already-canonical array yield the
        // IDENTITY permutation — so a fixture authored in canonical order Creates byte-identically (no golden move).
        // Load-time only (once per Apply), never a per-tick path, so the index alloc + Array.Sort here is fine. Core.Sim
        // stays Godot- and LINQ-free (the comparator approach needs neither).

        /// <summary>Units → (<c>Slot</c>, <c>UnitId</c> ordinal, quantized <c>X.Raw</c>, quantized <c>Z.Raw</c>),
        /// authored-index tiebreaker. Matches <see cref="CanonicalModelHash"/>'s Units sort key exactly.</summary>
        private static int[] CanonicalUnitOrder(ScenarioUnit[] u)
        {
            var idx = new int[u.Length];
            for (int i = 0; i < idx.Length; i++) idx[i] = i;
            Array.Sort(idx, (a, b) =>
            {
                int c = u[a].Slot.CompareTo(u[b].Slot);                                 if (c != 0) return c;
                c = string.CompareOrdinal(u[a].UnitId, u[b].UnitId);                    if (c != 0) return c;
                c = Fixed.FromFloat(u[a].X).Raw.CompareTo(Fixed.FromFloat(u[b].X).Raw); if (c != 0) return c;
                c = Fixed.FromFloat(u[a].Z).Raw.CompareTo(Fixed.FromFloat(u[b].Z).Raw); if (c != 0) return c;
                return a.CompareTo(b); // authored-index tiebreaker → strict total order (identity when canonical)
            });
            return idx;
        }

        /// <summary>Buildings → (<c>Slot</c>, <c>Type</c> ordinal, quantized <c>X.Raw</c>, quantized <c>Z.Raw</c>,
        /// <c>PreBuilt</c>), authored-index tiebreaker. Matches <see cref="CanonicalModelHash"/>'s Buildings sort key exactly.</summary>
        private static int[] CanonicalBuildingOrder(ScenarioBuilding[] bs)
        {
            var idx = new int[bs.Length];
            for (int i = 0; i < idx.Length; i++) idx[i] = i;
            Array.Sort(idx, (a, b) =>
            {
                int c = bs[a].Slot.CompareTo(bs[b].Slot);                                   if (c != 0) return c;
                c = string.CompareOrdinal(bs[a].Type, bs[b].Type);                          if (c != 0) return c;
                c = Fixed.FromFloat(bs[a].X).Raw.CompareTo(Fixed.FromFloat(bs[b].X).Raw);   if (c != 0) return c;
                c = Fixed.FromFloat(bs[a].Z).Raw.CompareTo(Fixed.FromFloat(bs[b].Z).Raw);   if (c != 0) return c;
                c = bs[a].PreBuilt.CompareTo(bs[b].PreBuilt);                               if (c != 0) return c;
                return a.CompareTo(b); // authored-index tiebreaker → strict total order (identity when canonical)
            });
            return idx;
        }

        /// <summary>Items → (<c>ItemId</c> ordinal, quantized <c>X.Raw</c>, quantized <c>Z.Raw</c>), authored-index
        /// tiebreaker. Matches <see cref="StartStateHash"/>'s placed-map-items sort key exactly.</summary>
        private static int[] CanonicalItemOrder(ScenarioItem[] items)
        {
            var idx = new int[items.Length];
            for (int i = 0; i < idx.Length; i++) idx[i] = i;
            Array.Sort(idx, (a, b) =>
            {
                int c = string.CompareOrdinal(items[a].ItemId, items[b].ItemId);                    if (c != 0) return c;
                c = Fixed.FromFloat(items[a].X).Raw.CompareTo(Fixed.FromFloat(items[b].X).Raw);      if (c != 0) return c;
                c = Fixed.FromFloat(items[a].Z).Raw.CompareTo(Fixed.FromFloat(items[b].Z).Raw);      if (c != 0) return c;
                return a.CompareTo(b); // authored-index tiebreaker → strict total order (identity when canonical)
            });
            return idx;
        }

        /// <summary>Parse a building type string to its enum value (verbatim from the as-built MainScene helper).</summary>
        public static BuildingType ParseBuildingType(string type) => type switch
        {
            "Barracks"      => BuildingType.Barracks,
            "ArcheryRange"  => BuildingType.ArcheryRange,
            "SiegeWorkshop" => BuildingType.SiegeWorkshop,
            "Aviary"        => BuildingType.Aviary,        // Story 2.8 — else a scenario-placed Aviary silently mis-places as a CommandCenter.
            _               => BuildingType.CommandCenter,
        };

        /// <summary>Story 4.7 — parse a resource_node collection_model string (mirrors <see cref="ParseBuildingType"/>'s
        /// switch style). Unknown strings are rejected at <see cref="ScenarioValidator"/>, so only the closed
        /// vocabulary ever reaches here; the default arm exists for shadow-mode-reachable invalid content.</summary>
        private static ResourceCollectionModel ParseCollectionModel(string model) => model switch
        {
            "Income"    => ResourceCollectionModel.Income,
            "Streaming" => ResourceCollectionModel.Streaming,
            _           => ResourceCollectionModel.Gather,
        };

        /// <summary>Story 4.7 — parse a resource_node resource_type string (mirrors <see cref="ParseBuildingType"/>'s
        /// switch style). Unknown strings are rejected at <see cref="ScenarioValidator"/>.</summary>
        private static ResourceKind ParseResourceType(string type) => type switch
        {
            "Crystal" => ResourceKind.Crystal,
            _         => ResourceKind.Ore,
        };
    }
}
