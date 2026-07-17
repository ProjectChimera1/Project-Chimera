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

                _host.Nodes.Create(pos, Fixed.FromFloat(node.Supply), Fixed.FromFloat(node.Rate), node.MaxGatherers,
                    ParseCollectionModel(node.CollectionModel),
                    ParseResourceType(node.ResourceType),
                    string.IsNullOrEmpty(node.RequiresStructure) ? null : node.RequiresStructure,
                    Fixed.FromFloat(node.RequiresStructureRadius),
                    ownerFaction,
                    node.IncomePeriodTicks);
            }

            // ── 3. Buildings ──────────────────────────────────────────────────
            foreach (var b in s.Buildings ?? System.Array.Empty<ScenarioBuilding>())
            {
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
                if (Enum.TryParse<BuildingType>(b.Type, out var bType)
                    && Enum.IsDefined(typeof(BuildingType), bType)
                    && bType.ToString() == b.Type
                    && bType != BuildingType.Custom)
                    _host.BuildSys.PlaceBuildingDirect(bType, faction, pos, b.PreBuilt);
                else
                    _host.BuildSys.PlaceBuildingDirectById(b.Type, faction, pos, b.PreBuilt);
            }

            // ── 4. Units ──────────────────────────────────────────────────────
            foreach (var u in s.Units ?? System.Array.Empty<ScenarioUnit>())
            {
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
                        faction));  // Story 3.14: the owning faction (revive-order anti-cheat + respawn ownership)
                }
            }

            // ── 4b. Items (Story 3.15) — place ground items + configure the usable inventory-slot cap ────────────
            // Configure the per-scenario usable inventory count (NULL ⇒ the full HeroStore.INVENTORY_SLOTS stride).
            _host.ItemSys.ConfigureUsableSlots(s.InventorySlotCount ?? HeroStore.INVENTORY_SLOTS);
            foreach (var it in s.Items ?? System.Array.Empty<ScenarioItem>())
            {
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
            _host.ScenarioDirector.SetRegionStore(BuildRegionStore(s.Regions));

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
        /// Keep these literal values in sync with alpha_map_01.json.
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
            Units = new[]
            {
                new ScenarioUnit { UnitId = WorkerIdForSlot(slotFactionDefs, 0), Slot = 0, X = -42f, Z = -3f },
                new ScenarioUnit { UnitId = WorkerIdForSlot(slotFactionDefs, 0), Slot = 0, X = -42f, Z =  3f },
                new ScenarioUnit { UnitId = WorkerIdForSlot(slotFactionDefs, 1), Slot = 1, X =  42f, Z = -3f },
                new ScenarioUnit { UnitId = WorkerIdForSlot(slotFactionDefs, 1), Slot = 1, X =  42f, Z =  3f },
            },
        };

        /// <summary>Resolve a fallback-mirror slot's worker unit_id by CATEGORY from its faction def (the legacy
        /// writer's lookup), falling back to the conventional "worker" id when no defs are threaded or the faction
        /// declares no Worker-category unit.</summary>
        private static string WorkerIdForSlot(
            System.Collections.Generic.IReadOnlyList<FactionDefinition?>? slotFactionDefs, int slot)
        {
            int fIdx = slot + 1; // (Faction)(slot + 1), matching Apply's cast
            FactionDefinition? def = (slotFactionDefs != null && fIdx >= 0 && fIdx < slotFactionDefs.Count)
                ? slotFactionDefs[fIdx] : null;
            return def?.GetUnitByCategory("Worker")?.Id ?? "worker";
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
