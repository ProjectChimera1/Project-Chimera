#nullable enable
using Godot;
using ProjectChimera.Core.Definitions;
using ProjectChimera.UI; // GameMode (the Edit/Play flag reported in the digest)

namespace ProjectChimera.Core
{
    /// <summary>
    /// DW-882 — the DEBUG SEAM that makes the running simulation drivable and observable from the GDScript-only
    /// godot-mcp bridge, so the in-engine gate can be satisfied by an agent instead of a human playtest.
    ///
    /// <para><b>Why this exists.</b> The bridge executes GDScript. It can call marshallable C# methods on scene nodes,
    /// but it cannot reach the pure-C# <see cref="EntityWorld"/> SoA (not a GodotObject; <c>_world</c>/<c>_host</c> are
    /// private), and it cannot construct a <see cref="Fixed"/> to call
    /// <c>SelectionSystem.IssueCastAbilityGroundCommand</c>. Stories 15.11 and 15.13 both stalled on exactly that wall:
    /// the sim behaviour they shipped was fully Tier-1-proven yet UNOBSERVABLE in the running game. Every method here
    /// therefore takes and returns only marshallable primitives (int/float/bool/string) — the float boundary is the
    /// bridge ABI, quantized to <see cref="Fixed"/> at this seam exactly like the screen→world mouse seam does.</para>
    ///
    /// <para><b>What it is NOT.</b> Not a cheat console and not a sim API. The mutating half is gated on
    /// <see cref="OS.IsDebugBuild"/> AND refuses while a lockstep match is online, because a direct SoA write outside
    /// the command stream is a guaranteed desync. The drive half deliberately does NOT write the sim: it issues real
    /// <see cref="UnitCommand.CastAbility"/> orders through the SAME <c>SelectionSystem</c> → <c>OrderApplier</c> path
    /// the mouse uses, so what the gate observes is the production cast path, not a test double.</para>
    ///
    /// <para><b>Return-code convention</b> (mutators return an int; reads return JSON or a sentinel):
    /// <c>0</c> ok · <c>-1</c> seam disabled (release build) · <c>-2</c> refused, online match ·
    /// <c>-3</c> scene not ready · <c>-4</c> bad/dead entity · <c>-5</c> bad slot · <c>-6</c> unknown id.</para>
    /// </summary>
    public partial class MainScene
    {
        // ── Return codes (documented above; named so call sites and the bridge agree) ──
        private const int SEAM_OK            =  0;
        private const int SEAM_DISABLED      = -1;
        private const int SEAM_ONLINE        = -2;
        private const int SEAM_NOT_READY     = -3;
        private const int SEAM_BAD_ENTITY    = -4;
        private const int SEAM_BAD_SLOT      = -5;
        private const int SEAM_UNKNOWN_ID    = -6;

        /// <summary>Bound on how many entities <see cref="BuildStateDict"/> enumerates — the digest is meant to be read
        /// by an agent, so it stays small and cheap rather than dumping a 2000-entity match.</summary>
        private const int DEBUG_DIGEST_MAX_ENTITIES = 12;

        /// <summary>The mutating half is debug-build-only. A release/export build silently refuses every mutator
        /// (<see cref="SEAM_DISABLED"/>), so this cannot become a shipped cheat surface.</summary>
        private static bool DebugSeamEnabled => OS.IsDebugBuild();

        /// <summary>True once the scene's sim composition root is wired (guards every seam call against a pre-_Ready
        /// or torn-down scene — the bridge can call at any moment).</summary>
        private bool SeamReady => _host != null && _world != null && _ctx != null;

        /// <summary>Shared precondition for the MUTATING half: debug build, wired scene, and OFFLINE. The online
        /// refusal is not politeness — these writes bypass the lockstep command stream and would desync every peer.</summary>
        private int GuardMutate()
        {
            if (!DebugSeamEnabled)          return SEAM_DISABLED;
            if (!SeamReady)                 return SEAM_NOT_READY;
            if (_ctx.Lockstep is { IsOnline: true }) return SEAM_ONLINE;
            return SEAM_OK;
        }

        // ────────────────────────────────────────────────────────────────────────────
        //  READ half — always available (observation cannot desync anything)
        // ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The godot-mcp digest hook. Implementing <c>_mcp_state()</c> makes this node appear in
        /// <c>godot_runtime_state digest</c> with real simulation values — tick, checksum, per-faction counts, mode, and
        /// a bounded entity table — which is what an in-engine gate artifact is captured from.
        /// </summary>
        public Godot.Collections.Dictionary _mcp_state() => BuildStateDict();

        /// <summary>Same payload as <see cref="_mcp_state"/> as a JSON string — the guaranteed-marshallable path for
        /// <c>godot_exec</c> (which returns primitives intact but truncates a Dictionary to a 200-char preview).</summary>
        public string DebugSimJson() => Json.Stringify(BuildStateDict());

        /// <summary>Full per-entity simulation state as JSON, including RAW <see cref="Fixed"/> values so an assertion
        /// can be exact rather than float-approximate.</summary>
        public string DebugEntityJson(int entityId) => Json.Stringify(BuildEntityDict(entityId));

        /// <summary>Alive entity count for a faction (<c>1</c> = Player1 …), or −1 if the scene is not ready.</summary>
        public int DebugAliveCount(int faction)
            => SeamReady ? CountFaction((Faction)faction) : -1;

        /// <summary>
        /// The entity id of the <paramref name="index"/>-th alive entity of <paramref name="faction"/> in ascending-id
        /// order, or −1. This is how the bridge picks a caster without being able to enumerate the SoA itself.
        /// </summary>
        public int DebugFindUnit(int faction, int index)
        {
            if (!SeamReady) return -1;
            var want = (Faction)faction;
            int seen = 0, cap = _world.HighWaterMark;
            for (int id = 0; id < cap; id++)
            {
                if (!_world.IsAlive(id) || _world.FactionOf[id] != want) continue;
                if (seen++ == index) return id;
            }
            return -1;
        }

        private Godot.Collections.Dictionary BuildStateDict()
        {
            var dict = new Godot.Collections.Dictionary();
            if (!SeamReady) { dict["ready"] = false; return dict; }

            dict["ready"]      = true;
            dict["tick"]       = (int)_host.CurrentTick;
            // Checksum as a hex STRING: it is a ulong, and the point of surfacing it is exact comparison across two
            // runs, which a lossy Variant round-trip would defeat.
            dict["checksum"]   = $"0x{_host.LastChecksum:X8}";
            dict["mode"]       = _ctx.GameState.Mode == GameMode.Edit ? "EDIT" : "PLAY";
            dict["online"]     = _ctx.Lockstep is { IsOnline: true };
            dict["paused"]     = _paused;
            dict["alive"]      = _world.AliveCount;
            dict["p1"]         = CountFaction(Faction.Player1);
            dict["p2"]         = CountFaction(Faction.Player2);
            dict["seam_debug"] = DebugSeamEnabled;

            var entities = new Godot.Collections.Array();
            int cap = _world.HighWaterMark;
            for (int id = 0; id < cap && entities.Count < DEBUG_DIGEST_MAX_ENTITIES; id++)
            {
                if (!_world.IsAlive(id)) continue;
                entities.Add(BuildEntityDict(id));
            }
            dict["entities"] = entities;
            return dict;
        }

        private Godot.Collections.Dictionary BuildEntityDict(int id)
        {
            var dict = new Godot.Collections.Dictionary();
            if (!SeamReady || id < 0 || id >= _world.HighWaterMark || !_world.IsAlive(id))
            {
                dict["id"] = id;
                dict["alive"] = false;
                return dict;
            }

            FixedVec3 pos = _world.Position[id];
            dict["id"]          = id;
            dict["alive"]       = true;
            dict["faction"]     = (int)_world.FactionOf[id];
            dict["x"]           = pos.X.ToFloat();
            dict["z"]           = pos.Z.ToFloat();
            // RAW Fixed values: the exact deterministic quantity, so two runs can be compared byte-for-byte.
            dict["raw_x"]       = pos.X.Raw;
            dict["raw_z"]       = pos.Z.Raw;
            dict["elevation"]   = _world.Elevation[id].ToFloat();
            dict["raw_elev"]    = _world.Elevation[id].Raw;
            dict["hp"]          = _world.Health[id].ToFloat();
            dict["max_hp"]      = _world.EffectiveMaxHealth[id].ToFloat();
            dict["energy"]      = _world.Energy[id].ToFloat();
            dict["max_energy"]  = _world.MaxEnergy[id].ToFloat();
            dict["moving"]      = (_world.Flags[id] & EntityFlags.Moving) != 0;
            dict["command"]     = _world.CommandState[id].ToString();

            var abilities = new Godot.Collections.Array();
            int abBase = id * EntityWorld.MAX_ABILITIES_PER_UNIT;
            for (int s = 0; s < _world.AbilityCount[id]; s++)
            {
                int regIndex = _world.AbilityId[abBase + s];
                var slot = new Godot.Collections.Dictionary
                {
                    ["slot"]     = s,
                    ["index"]    = regIndex,
                    ["id"]       = regIndex >= 0 && regIndex < _abilityRegistry.Count
                                     ? _abilityRegistry.Get(regIndex).Id : "",
                    ["cooldown"] = _world.AbilityCooldownTicks[abBase + s],
                };
                abilities.Add(slot);
            }
            dict["abilities"] = abilities;
            return dict;
        }

        // ────────────────────────────────────────────────────────────────────────────
        //  DRIVE half — issues REAL orders through the production path
        // ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Issue a GROUND-POINT ability cast exactly as a player's click does: the same
        /// <c>SelectionSystem.IssueCastAbilityGroundCommand</c> → <c>OrderApplier</c> path, with the same single
        /// <see cref="Fixed.FromFloat"/> quantization the screen→world seam applies to a raycast hit. This is the whole
        /// reason the seam exists — GDScript cannot build the two <see cref="Fixed"/> arguments.
        /// </summary>
        public int DebugCastGround(int entityId, int slot, float x, float z)
        {
            int guard = GuardMutate();
            if (guard != SEAM_OK) return guard;
            if (!_world.IsAlive(entityId)) return SEAM_BAD_ENTITY;
            if (slot < 0 || slot >= EntityWorld.MAX_ABILITIES_PER_UNIT) return SEAM_BAD_SLOT;

            _ctx.Selection.IssueCastAbilityGroundCommand(entityId, slot, Fixed.FromFloat(x), Fixed.FromFloat(z));
            return SEAM_OK;
        }

        /// <summary>Issue a TargetUnit / Self ability cast through the production
        /// <c>SelectionSystem.IssueCastAbilityCommand</c> path (<paramref name="targetEntityId"/> −1 = self/none).</summary>
        public int DebugCastTarget(int entityId, int slot, int targetEntityId)
        {
            int guard = GuardMutate();
            if (guard != SEAM_OK) return guard;
            if (!_world.IsAlive(entityId)) return SEAM_BAD_ENTITY;
            if (slot < 0 || slot >= EntityWorld.MAX_ABILITIES_PER_UNIT) return SEAM_BAD_SLOT;

            _ctx.Selection.IssueCastAbilityCommand(entityId, slot, targetEntityId);
            return SEAM_OK;
        }

        // ────────────────────────────────────────────────────────────────────────────
        //  SETUP half — direct SoA writes (debug + offline only; NOT deterministic-safe)
        // ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Put a registry ability into a unit's slot at runtime so a cast can be driven without shipping the ability on
        /// a faction roster. Writes <c>AbilityId</c> (authored, not folded) and, when the slot extends the unit's set,
        /// <c>AbilityCount</c> — which DOES bound the v7 cooldown fold, hence the offline-only guard. The slot's
        /// cooldown is zeroed so the very next cast is legal.
        /// </summary>
        public int DebugGrantAbility(int entityId, int slot, string abilityId)
        {
            int guard = GuardMutate();
            if (guard != SEAM_OK) return guard;
            if (!_world.IsAlive(entityId)) return SEAM_BAD_ENTITY;
            if (slot < 0 || slot >= EntityWorld.MAX_ABILITIES_PER_UNIT) return SEAM_BAD_SLOT;

            int regIndex = _abilityRegistry.IndexOf(abilityId);
            if (regIndex < 0) return SEAM_UNKNOWN_ID;

            int abBase = entityId * EntityWorld.MAX_ABILITIES_PER_UNIT;
            _world.AbilityId[abBase + slot]            = regIndex;
            _world.AbilityCooldownTicks[abBase + slot] = 0;
            if (_world.AbilityCount[entityId] <= slot) _world.AbilityCount[entityId] = (byte)(slot + 1);
            return SEAM_OK;
        }

        /// <summary>Spawn a unit of <paramref name="unitId"/> for <paramref name="faction"/> at a world point, through
        /// the same <c>ScenarioApplier.SpawnUnitAt</c> the scenario/revive paths use. Returns the new entity id, or a
        /// negative seam code. The definition is looked up across both loaded faction rosters.</summary>
        public int DebugSpawnUnit(int faction, float x, float z, string unitId)
        {
            int guard = GuardMutate();
            if (guard != SEAM_OK) return guard;

            UnitDefinition? def = FindUnitDefinition(unitId);
            if (def == null) return SEAM_UNKNOWN_ID;

            return _applier.SpawnUnitAt(def, (Faction)faction, Fixed.FromFloat(x), Fixed.FromFloat(z));
        }

        /// <summary>Set an entity's current health (clamped to its max) — the read/write pair DW-882 asked for, so a
        /// heal/damage effect can be observed from a known starting value.</summary>
        public int DebugSetHealth(int entityId, float hp)
        {
            int guard = GuardMutate();
            if (guard != SEAM_OK) return guard;
            if (!_world.IsAlive(entityId)) return SEAM_BAD_ENTITY;

            Fixed value = Fixed.FromFloat(hp);
            if (value > _world.EffectiveMaxHealth[entityId]) value = _world.EffectiveMaxHealth[entityId];
            _world.Health[entityId] = value;
            return SEAM_OK;
        }

        /// <summary>Set an entity's current energy (clamped to its max) so a cost-bearing ability can be made castable.</summary>
        public int DebugSetEnergy(int entityId, float energy)
        {
            int guard = GuardMutate();
            if (guard != SEAM_OK) return guard;
            if (!_world.IsAlive(entityId)) return SEAM_BAD_ENTITY;

            Fixed value = Fixed.FromFloat(energy);
            if (value > _world.MaxEnergy[entityId]) value = _world.MaxEnergy[entityId];
            _world.Energy[entityId] = value;
            return SEAM_OK;
        }

        /// <summary>Look up a unit definition by id across the loaded faction rosters (null when absent).</summary>
        private UnitDefinition? FindUnitDefinition(string unitId)
        {
            foreach (var u in _factionDef?.Units ?? System.Linq.Enumerable.Empty<UnitDefinition>())
                if (u.Id == unitId) return u;
            foreach (var u in _factionDef2?.Units ?? System.Linq.Enumerable.Empty<UnitDefinition>())
                if (u.Id == unitId) return u;
            return null;
        }
    }
}
