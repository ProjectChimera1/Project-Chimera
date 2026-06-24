#nullable enable
using System;                            // StringComparison
using ProjectChimera.Core;              // Faction, Fixed, FixedVec3, BuildingType, GatherState (parent namespace)
using ProjectChimera.Core.Definitions;  // ScenarioData & sub-types, FactionDefinition, UnitDefinition, Validated<T>

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
        // IN PLACE (never reassign, or the shared reference goes stale); Apply/ApplyFallback/SpawnUnit and the
        // runtime OnSpawnUnit trigger delegate all read this one array. (D1/D4)
        private readonly FactionDefinition?[] _slotFactionDefs;

        /// <summary>
        /// Construct the applier over a wired 1.8a host.
        /// </summary>
        /// <param name="host">The Godot-free sim composition root whose stores this applier writes.</param>
        /// <param name="log">Injected log seam (NullLogSink for tests/server; GodotLogSink for MainScene). The
        /// applier's ONLY logging is low-frequency diagnostics (unknown unit_id) — never per-tick/per-entity.</param>
        /// <param name="slotFactionDefs">The SAME array MainScene holds as <c>_slotFactionDefs</c>. The presentation
        /// pre-pass writes resolved defs into it in place before Apply/ApplyFallback; SpawnUnit + the trigger
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
        public void Apply(Validated<ScenarioData> v)
        {
            ScenarioData s = v.Value; // as-built property name (NOT .Model)

            // ── 1. Player slots: faction def + starting ore + base deposit point ─
            foreach (var slot in s.PlayerSlots ?? System.Array.Empty<ScenarioPlayerSlot>())
            {
                var faction = (Faction)(slot.Slot + 1); // slot 0 → Player1, slot 1 → Player2
                var def = _slotFactionDefs[(int)faction]; // pre-resolved by the presentation pre-pass
                if (def != null) _host.BuildSys.SetFactionDef(faction, def);

                _host.Resources.AddOre(faction, Fixed.FromFloat(slot.StartOre));
                SetFactionBase(faction, new FixedVec3(
                    Fixed.FromFloat(slot.BaseX), Fixed.Zero, Fixed.FromFloat(slot.BaseZ)));
            }

            // ── 2. Resource nodes ─────────────────────────────────────────────
            foreach (var node in s.ResourceNodes ?? System.Array.Empty<ScenarioResourceNode>())
            {
                var pos = new FixedVec3(Fixed.FromFloat(node.X), Fixed.Zero, Fixed.FromFloat(node.Z));
                _host.Nodes.Create(pos, Fixed.FromFloat(node.Supply),
                                   Fixed.FromFloat(node.Rate), node.MaxGatherers);
            }

            // ── 3. Buildings ──────────────────────────────────────────────────
            foreach (var b in s.Buildings ?? System.Array.Empty<ScenarioBuilding>())
            {
                var faction = (Faction)(b.Slot + 1);
                var pos     = new FixedVec3(Fixed.FromFloat(b.X), Fixed.Zero, Fixed.FromFloat(b.Z));
                var bType   = ParseBuildingType(b.Type);
                _host.BuildSys.PlaceBuildingDirect(bType, faction, pos, b.PreBuilt);
            }

            // ── 4. Units ──────────────────────────────────────────────────────
            foreach (var u in s.Units ?? System.Array.Empty<ScenarioUnit>())
            {
                var faction = (Faction)(u.Slot + 1);
                // Look up def from the per-slot faction definition resolved by the pre-pass.
                var def = _slotFactionDefs[(int)faction]?.GetUnit(u.UnitId);
                if (def == null)
                {
                    _log.Warn($"[ScenarioApplier] Scenario unit_id '{u.UnitId}' not found in faction — skipped.");
                    continue;
                }
                SpawnUnit(def, faction, u.X, u.Z);
            }

            // ── 5. Triggers ────────────────────────────────────────────────────
            _host.ScenarioDirector.LoadScenario(s); // triggers last (same as today)
        }

        /// <summary>
        /// Hardcoded fallback used only if the scenario JSON is missing (mirrors alpha_map_01.json so the game is
        /// always playable). Reads <c>_slotFactionDefs</c> for the worker defs (MainScene seeds the P1/P2 defaults
        /// before this runs). Deliberately does NOT call <c>LoadScenario</c> — rerouting through <see cref="Apply"/>
        /// would newly fire <c>match_start</c> triggers and move behavior; the split is preserved.
        /// </summary>
        public void ApplyFallback()
        {
            // Faction bases (D6: both base write sites unified via SetFactionBase)
            SetFactionBase(Faction.Player1, new FixedVec3(Fixed.FromFloat(-45f), Fixed.Zero, Fixed.Zero));
            SetFactionBase(Faction.Player2, new FixedVec3(Fixed.FromFloat(+45f), Fixed.Zero, Fixed.Zero));

            // Starting ore
            _host.Resources.AddOre(Faction.Player1, Fixed.FromFloat(200f));
            _host.Resources.AddOre(Faction.Player2, Fixed.FromFloat(200f));

            // Resource nodes
            var rate = Fixed.FromFloat(5f);
            foreach (var (x, z, supply) in new (float, float, float)[]
            {
                ( -20f, -15f, 600f ), ( -20f,  15f, 600f ),
                (  20f, -15f, 600f ), (  20f,  15f, 600f ),
                (   0f, -25f, 400f ), (   0f,  25f, 400f ),
                ( -35f,   0f, 300f ), (  35f,   0f, 300f ),
            })
            {
                _host.Nodes.Create(
                    new FixedVec3(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z)),
                    Fixed.FromFloat(supply), rate, maxGatherers: 4);
            }

            // Starter command centres
            _host.BuildSys.PlaceBuildingDirect(BuildingType.CommandCenter, Faction.Player1,
                new FixedVec3(Fixed.FromFloat(-45f), Fixed.Zero, Fixed.Zero), preBuilt: true);
            _host.BuildSys.PlaceBuildingDirect(BuildingType.CommandCenter, Faction.Player2,
                new FixedVec3(Fixed.FromFloat(+45f), Fixed.Zero, Fixed.Zero), preBuilt: true);

            // 2 workers per faction — each faction uses its own worker definition
            var workerDef  = _slotFactionDefs[(int)Faction.Player1]?.GetUnitByCategory("Worker");
            var workerDef2 = _slotFactionDefs[(int)Faction.Player2]?.GetUnitByCategory("Worker") ?? workerDef;
            if (workerDef != null)
            {
                SpawnUnit(workerDef,  Faction.Player1, -42f, -3f);
                SpawnUnit(workerDef,  Faction.Player1, -42f, +3f);
            }
            if (workerDef2 != null)
            {
                SpawnUnit(workerDef2, Faction.Player2, +42f, -3f);
                SpawnUnit(workerDef2, Faction.Player2, +42f, +3f);
            }
        }

        /// <summary>
        /// Spawn a unit from a <see cref="UnitDefinition"/>, wiring all SoA fields. The single alloc-free spawn
        /// primitive shared by <see cref="Apply"/>, <see cref="ApplyFallback"/>, and the runtime
        /// <c>ScenarioDirector.OnSpawnUnit</c> trigger delegate (D5). Returns the new entity id, or -1 if the world
        /// is full. Allocation-free: pre-resolved def, value-type structs, no LINQ/closures/boxing/string alloc.
        /// </summary>
        public int SpawnUnit(UnitDefinition def, Faction faction, float x, float z)
        {
            var pos = new FixedVec3(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
            var world = _host.World;
            int id  = world.Create(pos, faction,
                                   Fixed.FromFloat(def.Hp), Fixed.FromFloat(def.Speed));
            if (id < 0) return id;

            world.VisionRange[id]  = Fixed.FromFloat(def.VisionRange);
            world.AttackRange[id]  = Fixed.FromFloat(def.AttackRange);
            world.AttackDamage[id] = Fixed.FromFloat(def.AttackDamage);
            world.AttackSpeed[id]  = Fixed.FromFloat(def.AttackSpeed);
            world.DamageTypeOf[id] = def.ParsedDamageType;
            world.ArmorTypeOf[id]  = def.ParsedArmorType;
            world.SplashRadius[id] = Fixed.FromFloat(def.SplashRadius);
            world.SupplyCost[id]   = (byte)def.Supply;

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
            _               => BuildingType.CommandCenter,
        };
    }
}
