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
            dict["heroes"]   = BuildHeroArray(); // Story 15-24e
            return dict;
        }

        /// <summary>
        /// Story 15-24e — the HERO half of the digest. The seam read NOTHING from <see cref="HeroStore"/> before
        /// this story, so the hero runtime (level, XP, banked attribute points, the per-stat spent totals, and the
        /// resolved spend MODE) was unobservable from the bridge and no in-engine gate could produce a numeric
        /// claim about it. Emitted in <see cref="HeroStore.FoldOrder"/> order (ascending HeroId — the same
        /// producer-independent order the checksum folds), with RAW <see cref="Fixed"/> values so an assertion can
        /// be exact rather than float-approximate.
        /// </summary>
        private Godot.Collections.Array BuildHeroArray()
        {
            var arr = new Godot.Collections.Array();
            if (!SeamReady) return arr;
            HeroStore h = _host.Heroes;
            int[] order = h.FoldOrder();
            for (int k = 0; k < order.Length; k++)
            {
                int slot = order[k];
                var spent = new Godot.Collections.Array();
                int aBase = slot * AttributeStats.Count;
                for (int s = 0; s < AttributeStats.Count; s++) spent.Add(h.AttrStatSpent[aBase + s].Raw);

                var declared = new Godot.Collections.Array();
                var attrs = h.AttrModelOf[slot]?.Attributes;
                if (attrs != null)
                    foreach (AttributeDeclaration a in attrs) declared.Add(a?.Id ?? "");

                int entity = h.EntityId[slot];
                arr.Add(new Godot.Collections.Dictionary
                {
                    ["slot"]        = slot,
                    ["entity"]      = entity,
                    ["faction"]     = (int)h.OwnerFaction[slot],
                    ["level"]       = h.Level[slot],
                    ["xp_raw"]      = h.Xp[slot].Raw,
                    ["unspent"]     = h.UnspentPoints[slot],
                    ["spend_mode"]  = h.IsPlayerSpent(slot) ? "player_spent" : "auto",
                    ["authored_mode"] = (h.AttrModelOf[slot]?.SpendMode) ?? "(absent)",
                    ["ai_mask"]     = h.AiControlMask,
                    ["attributes"]  = declared,
                    ["spent_raw"]   = spent,
                    // The three stats a spend is easiest to READ on: the entity's live effective channels, which
                    // is where the spent modifier's vector actually lands.
                    ["eff_max_hp_raw"] = entity >= 0 && entity < _world.HighWaterMark && _world.IsAlive(entity)
                                            ? _world.EffectiveMaxHealth[entity].Raw : 0,
                    ["eff_damage_raw"] = entity >= 0 && entity < _world.HighWaterMark && _world.IsAlive(entity)
                                            ? _world.EffectiveAttackDamage[entity].Raw : 0,
                    ["eff_armor_raw"]  = entity >= 0 && entity < _world.HighWaterMark && _world.IsAlive(entity)
                                            ? _world.EffectiveArmor[entity].Raw : 0,
                });
            }
            return arr;
        }

        /// <summary>Story 15-24e — the hero digest as a JSON string (the guaranteed-marshallable
        /// <c>godot_exec</c> path, mirroring <see cref="DebugBuildingJson"/>).</summary>
        public string DebugHeroJson() => Json.Stringify(BuildHeroArray());

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

        // ────────────────────────────────────────────────────────────────────────────
        //  BUILDING half (2026-08-11) — added for the combined DW-917/920/921/923/929 in-engine gate pass:
        //  the original seam covered units only, so a building could not be observed, selected, or placed
        //  from the bridge and every building-facing verify line was stuck on a human playtest.
        // ────────────────────────────────────────────────────────────────────────────

        /// <summary>All alive buildings as JSON (READ half — always available). Slot, authored id, enum type, faction,
        /// position, health, built state and rally point; bounded by <see cref="BuildingStore.MAX_BUILDINGS"/>.</summary>
        public string DebugBuildingJson()
        {
            var arr = new Godot.Collections.Array();
            if (SeamReady)
            {
                BuildingStore b = _ctx.Buildings;
                for (int i = 0; i < BuildingStore.MAX_BUILDINGS; i++)
                {
                    if (!b.Alive[i]) continue;
                    arr.Add(new Godot.Collections.Dictionary
                    {
                        ["slot"]    = i,
                        ["id"]      = b.DefinitionId[i] ?? "",
                        ["type"]    = b.Type[i].ToString(),
                        ["faction"] = (int)b.FactionOf[i],
                        ["x"]       = b.Position[i].X.ToFloat(),
                        ["z"]       = b.Position[i].Z.ToFloat(),
                        ["hp"]      = b.Health[i].ToFloat(),
                        ["max_hp"]  = b.MaxHealth[i].ToFloat(),
                        ["built"]   = b.ConstructionTimer[i] <= Fixed.Zero,
                        ["rally_x"] = b.RallyPoint[i].X.ToFloat(),
                        ["rally_z"] = b.RallyPoint[i].Z.ToFloat(),
                    });
                }
            }
            return Json.Stringify(arr);
        }

        /// <summary>Local-viewer fog state at a world point (READ half): <c>"visible"</c>, <c>"explored"</c> or
        /// <c>"hidden"</c> (<c>"none"</c> before the scene is wired). This is the DW-920/923 gate probe — it reads the
        /// same unfolded <c>FogOfWarSystem</c> grid the render bridges consult.</summary>
        public string DebugFogAt(float x, float z)
        {
            if (!SeamReady) return "none";
            if (_ctx.Fog.IsVisible(x, z))  return "visible";
            if (_ctx.Fog.IsExplored(x, z)) return "explored";
            return "hidden";
        }

        /// <summary>Select a building exactly as a click's fall-through arm does (via
        /// <c>SelectionSystem.DebugSelectBuilding</c>). Selection is presentation-only local state — what the command
        /// card reads — so unlike the mutators this is NOT refused online; a click could set it at any time.</summary>
        public int DebugSelectBuilding(int slot)
        {
            if (!DebugSeamEnabled) return SEAM_DISABLED;
            if (!SeamReady)        return SEAM_NOT_READY;
            return _ctx.Selection.DebugSelectBuilding(slot) ? SEAM_OK : SEAM_BAD_ENTITY;
        }

        /// <summary>Single-select a unit exactly as a click's unit arm does. Presentation-only, allowed online
        /// (see <see cref="DebugSelectBuilding(int)"/>).</summary>
        public int DebugSelectUnit(int entityId)
        {
            if (!DebugSeamEnabled) return SEAM_DISABLED;
            if (!SeamReady)        return SEAM_NOT_READY;
            return _ctx.Selection.DebugSelectUnit(entityId) ? SEAM_OK : SEAM_BAD_ENTITY;
        }

        /// <summary>Place a building for a faction through the same <c>BuildingSystem.PlaceBuildingDirectById</c> the
        /// scenario/editor paths use (bypasses ore; <paramref name="preBuilt"/> zeroes the construction timer). SETUP
        /// half: debug + OFFLINE only — this writes the store outside the command stream, exactly the DW-405 desync
        /// class the wire order exists for. Obstacle routing needs no manual sync (the flow-field obstacle map is
        /// rebuilt on the tick, per DW-918's closure). Returns the building slot, or a negative seam code.</summary>
        public int DebugPlaceBuilding(int faction, string buildingId, float x, float z, bool preBuilt)
        {
            int guard = GuardMutate();
            if (guard != SEAM_OK) return guard;
            int slot = _ctx.BuildSys.PlaceBuildingDirectById(
                buildingId, (Faction)faction,
                new FixedVec3(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z)), preBuilt);
            return slot < 0 ? SEAM_BAD_SLOT : slot;
        }

        // ────────────────────────────────────────────────────────────────────────────
        //  HERO half (Story 15-24e) — SETUP mutators (debug + offline only; NOT deterministic-safe)
        //
        //  Shipped content declares NO hero unit and NO faction attribute model (the seven models under
        //  resources/data/attribute-models/ are creator PRESETS, referenced by nothing), so there is no map on
        //  which the hero attribute runtime can be observed in-engine at all. These two mutators construct that
        //  situation from a shipped preset — they are the only way the required in-engine gate for a hero-growth
        //  story can produce numbers, and every number they produce is checkable against the preset JSON.
        // ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Mint a HERO row onto an existing entity using a shipped attribute-model preset
        /// (<c>resources/data/attribute-models/{presetId}.json</c>), so the 15-21/15-24c/15-24e hero attribute
        /// runtime can be driven and read from the bridge.
        ///
        /// <para><paramref name="spendMode"/> is the authored token (<c>""</c>/<c>"auto"</c>/<c>"player_spent"</c>)
        /// written onto a CLONE of the preset — the shipped JSON and every other hero are untouched, and the clone
        /// also exercises <c>AttributeModelDefinition.Clone</c>'s spend-mode line. Every declared attribute gets
        /// <paramref name="attrBase"/> at level 1 and <paramref name="attrPerLevel"/> per level, and the FIRST
        /// declared attribute is flagged primary, so the resulting contributions are a hand-checkable product of
        /// the preset's own <c>per_point</c> numbers. The XP curve is pinned to base 100 × growth 1.0 so exactly
        /// 100 XP buys exactly one level.</para>
        ///
        /// <para>Writes FOLDED state (the HeroStore row) outside the command stream — hence
        /// <see cref="GuardMutate"/>'s debug-build + OFFLINE gate, the same posture as
        /// <see cref="DebugGrantAbility"/>. Returns the hero slot, or a negative seam code.</para>
        /// </summary>
        public int DebugMintHero(int entityId, string presetId, string spendMode, int level,
                                 float attrBase, float attrPerLevel)
        {
            int guard = GuardMutate();
            if (guard != SEAM_OK) return guard;
            if (entityId < 0 || entityId >= _world.HighWaterMark || !_world.IsAlive(entityId)) return SEAM_BAD_ENTITY;
            // A second mint on the same entity would re-point HeroIndex at the new row and ORPHAN the old one: still
            // Alive, still folded into SimChecksum every tick, but never ticked again (its link no longer round-trips
            // through IsLiveLinkedHero). Refuse rather than silently corrupt the store the gate is measuring.
            if (_host.Heroes.TryResolveRef(_world.HeroIndex[entityId], out _)) return SEAM_BAD_SLOT;

            AttributeModelDefinition? model = LoadAttributeModelPreset(presetId);
            if (model?.Attributes == null || model.Attributes.Count == 0) return SEAM_UNKNOWN_ID;
            model = model.Clone();                                          // never mutate the shipped preset
            model.SpendMode = string.IsNullOrEmpty(spendMode) ? null : spendMode;

            var attrs = new HeroAttributesDefinition
            {
                Primary  = model.Attributes[0]?.Id,
                Base     = new System.Collections.Generic.Dictionary<string, float>(),
                PerLevel = new System.Collections.Generic.Dictionary<string, float>(),
            };
            foreach (AttributeDeclaration a in model.Attributes)
            {
                if (string.IsNullOrWhiteSpace(a?.Id)) continue;
                attrs.Base![a!.Id!]     = attrBase;
                attrs.PerLevel![a!.Id!] = attrPerLevel;
            }

            // A SYNTHETIC source definition carrying only the hero block: HeroStore.SourceDef is read for the
            // respawn def and for this hero's authored attributes, and the roster's real UnitDefinition (shared,
            // authored) must never be mutated to add one.
            var synthetic = new UnitDefinition
            {
                Id   = (_world.SourceDefinition[entityId]?.Id ?? "debug_hero"),
                Hero = new HeroDefinition
                {
                    MaxLevel = 10, BaseXp = 100f, XpGrowth = 1f, XpShareRadius = 12f,
                    HealthPerLevel = 0f, DamagePerLevel = 0f, ArmorPerLevel = 0f,
                    Attributes = attrs,
                },
            };

            var (cBase, cPerLevel) = HeroAttributeResolver.Resolve(model, attrs);
            int slot = _host.Heroes.Mint(
                new HeroId(0xDEB0_0000UL + (ulong)(uint)entityId), entityId,
                level < 1 ? 1 : level, Fixed.Zero,
                maxLevel: 10, baseXp: Fixed.FromInt(100), xpGrowth: Fixed.One, xpShareRadius: Fixed.FromInt(12),
                healthPerLevel: Fixed.Zero, damagePerLevel: Fixed.Zero, armorPerLevel: Fixed.Zero,
                sourceDef: synthetic, ownerFaction: _world.FactionOf[entityId],
                xpGainFactor: null,
                attrStatBase: cBase, attrStatPerLevel: cPerLevel,
                attrModel: model);
            if (slot < 0) return SEAM_BAD_SLOT;

            _world.HeroIndex[entityId] = _host.Heroes.PackRef(slot); // the entity→hero link the XP runtime validates
            return slot;
        }

        /// <summary>Credit raw XP onto a minted hero row so the NEXT tick's <c>HeroXpSystem.AdvanceLevels</c> runs
        /// the REAL level path (bank-a-point under <c>player_spent</c>, apply-the-vector under <c>auto</c>) rather
        /// than the seam faking a level. Writes the folded <c>HeroStore.Xp</c>, hence the offline-only guard.</summary>
        public int DebugGrantHeroXp(int entityId, float xp)
        {
            int guard = GuardMutate();
            if (guard != SEAM_OK) return guard;
            if (entityId < 0 || entityId >= _world.HighWaterMark || !_world.IsAlive(entityId)) return SEAM_BAD_ENTITY;
            if (!_host.Heroes.TryResolveRef(_world.HeroIndex[entityId], out int slot)) return SEAM_BAD_ENTITY;
            _host.Heroes.Xp[slot] = _host.Heroes.Xp[slot] + Fixed.FromFloat(xp);
            return SEAM_OK;
        }

        /// <summary>Load a shipped attribute-model preset by file id (e.g. <c>"wc3"</c>). Null when the file is
        /// missing or unreadable — the seam then returns <see cref="SEAM_UNKNOWN_ID"/>.</summary>
        private static AttributeModelDefinition? LoadAttributeModelPreset(string presetId)
        {
            if (string.IsNullOrWhiteSpace(presetId)) return null;
            string path = ProjectSettings.GlobalizePath($"res://resources/data/attribute-models/{presetId}.json");
            try
            {
                if (!System.IO.File.Exists(path)) return null;
                using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("attribute_model", out var m)) return null;
                return System.Text.Json.JsonSerializer.Deserialize<AttributeModelDefinition>(
                    m.GetRawText(), FactionDefinition.JsonOptions);
            }
            catch (System.Exception e)
            {
                GD.Print($"[Seam] attribute-model preset '{presetId}' unreadable: {e.Message}");
                return null;
            }
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
