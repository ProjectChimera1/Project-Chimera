#nullable enable
using System;
using System.Globalization;
using System.IO;
using ProjectChimera.AI;               // AiControlPlan
using ProjectChimera.Combat;           // DamageTable
using ProjectChimera.Core.Definitions; // FactionDefinition, BuildingDefinition, AbilityRegistry, ItemRegistry, ScenarioData, ScenarioSerializer
using ProjectChimera.Multiplayer;      // UnitOrder, OrderApplier, PlayerCountPolicy
using ProjectChimera.Navigation;       // FlowFieldSystem
using ProjectChimera.UI;               // BuildingNavFootprint (Godot-free, in SimSources.props since A3)

namespace ProjectChimera.Core.Sim
{
    /// <summary>Why <see cref="SimSession.Build(string, string, ulong, bool, ILogSink?)"/> failed: the native ABI maps
    /// <see cref="ContentLoad"/> to <c>-3</c> and <see cref="ScenarioRejected"/> to <c>-4</c> (plan A §3.4).</summary>
    public enum SimSessionFailure
    {
        /// <summary>A content file or the scenario could not be read or parsed.</summary>
        ContentLoad = 1,
        /// <summary>The scenario parsed but <see cref="ScenarioValidator"/> rejected it (fail-closed, as the server).</summary>
        ScenarioRejected = 2,
    }

    /// <summary>A failed <see cref="SimSession"/> build, with its <see cref="SimSessionFailure"/> class.</summary>
    public sealed class SimSessionBuildException : Exception
    {
        /// <summary>Which stage failed.</summary>
        public SimSessionFailure Failure { get; }

        public SimSessionBuildException(SimSessionFailure failure, string message, Exception? inner = null)
            : base(message, inner) => Failure = failure;
    }

    /// <summary>Test-only knobs for <see cref="SimSession"/> (the Tier-1 suite compiles the sim sources, so
    /// <c>internal</c> is visible there; nothing in the native ABI reaches this type).</summary>
    internal sealed class SimSessionOptions
    {
        /// <summary>When false, the flow-field wiring (footprint source, static blocked cells, obstacle rebuild,
        /// steering baseline and path delegates) is skipped: the ServerBootstrap-only composition, used to prove the
        /// wiring matters.</summary>
        public bool FlowFields = true;
    }

    /// <summary>
    /// Unreal trial A3 (plan A §3.2): the ONE Godot-free composition of a match that every trial leg uses (the .NET
    /// CLI, the NativeAOT library called from C++ and from Unreal, and the Godot runner), so "the same checksum as
    /// Godot" compares one recipe, not three. It loads content the way the Godot client does, builds the validated
    /// host through <see cref="ServerBootstrap.Build"/>, then performs — in this order — the match-start wiring the
    /// Godot game adds on top of that spine (plan A F13), each step mirrored to its Godot site:
    /// <list type="number">
    ///   <item>AI control plan (<c>MainScene.cs:2990-2994</c>): <see cref="AiControlPlan.OfflineDefault"/> when the
    ///         AI is on, else <see cref="AiControlPlan.None"/>.</item>
    ///   <item>Match seed (<c>MainScene.cs:3006-3007</c>): <c>World.Rng.Seed(seed)</c>. Godot seeds before its
    ///         re-apply and this seeds after the apply; the apply draws no RNG (<c>MainScene.cs:2998-2999</c>), so the
    ///         two orders give the same state (a Tier-1 test pins <c>Rng.State == DEFAULT_RNG_SEED</c> before the seed).</item>
    ///   <item>Building footprint source (<c>NavigationPhase.cs:95-96</c>) with a definition resolver identical to
    ///         <c>NavigationPhase.ResolveBuildingDef</c> (<c>NavigationPhase.cs:133-162</c>).</item>
    ///   <item>Static blocked cells (<c>ScenarioLoadPhase.cs:314-321</c>):
    ///         <c>SetStaticBlocked(ScenarioApplier.BuildPathabilityGrid(model, null)?.Blocked)</c> — the same recipe
    ///         and inputs <see cref="ServerBootstrap.Build"/> applied (flat: no elevation grid).</item>
    ///   <item><c>FlowFields.RebuildObstacles</c> then <c>Steering.SyncBuildingBaseline</c> (<c>FlowFieldInitPhase.cs:21-26</c>).</item>
    ///   <item>Path delegates as <c>FlowFieldBridge.ToSim</c> (<c>MatchLifecycleController.cs:132-135</c>,
    ///         <c>FlowFieldBridge.cs:50-77</c>): <c>Fixed.FromFloat</c> of the float x/z, y = 0.</item>
    /// </list>
    /// The application of those steps after <see cref="ServerBootstrap.Build"/> rather than interleaved with the
    /// apply is equivalent because <see cref="ScenarioApplier.Apply"/> never touches the flow-field system.
    ///
    /// <para><b>Known deltas from Godot's match start</b>, all checksum-neutral for the trial scenario (no items
    /// placed, no triggers, fog is presentation): no <c>Fog.SetBuildingVisionSource</c> (<c>NavigationPhase.cs:104</c>);
    /// no <c>ScenarioDelegateBinder</c> (trigger delegates stay unbound); no <c>PathRequestSystem</c> per-frame writer
    /// (DW-681, a Godot-side frame-paced write into sim state); orders enter through <see cref="OrderApplier"/>, not the
    /// offline <c>SelectionSystem</c> direct path; the host receives the per-slot P1/P2 definitions (ServerBootstrap's
    /// choice) where the Godot client hands it the default P1/P2 files. The gating MainScene start-state probe (A8b)
    /// checks the resulting start state against the real <c>MainScene</c>.</para>
    ///
    /// <para>The AI level stays <see cref="AiDifficulty.Normal"/>, Godot's inspector default (<c>MainScene.cs:252</c>).
    /// The item registry is loaded and passed (<c>MainScene.cs:431-434</c>, <c>:527-535</c>). The checksum interval is
    /// left at the game default (60); hosts set it.</para>
    /// </summary>
    public sealed class SimSession
    {
        /// <summary>Godot-relative data locations (the <c>MainScene.cs:308-316</c> constants, relative to the
        /// <c>res://</c> root).</summary>
        private const string RES_PREFIX          = "res://";
        private const string P1_FACTION_REL      = "resources/data/factions/alpha_faction.json";
        private const string P2_FACTION_REL      = "resources/data/factions/beta_faction.json";
        private const string DAMAGE_TABLE_REL    = "resources/data/damage_table.json";
        private const string ABILITIES_DIR_REL   = "resources/data/abilities";
        private const string ITEMS_DIR_REL       = "resources/data/items";

        /// <summary>The validated, applied, wired host.</summary>
        public SimulationHost Host { get; }

        /// <summary>The applied scenario model.</summary>
        public ScenarioData Model { get; }

        /// <summary>The per-slot faction definitions the match was built from (index by <see cref="Faction"/>).</summary>
        public FactionDefinition?[] SlotFactionDefs { get; }

        /// <summary>The loaded damage table (or <see cref="DamageTable.Default"/> when the file is absent, as Godot).</summary>
        public DamageTable DamageTable { get; }

        /// <summary>The AI control plan pushed into the host (and folded into the agreement hash).</summary>
        public AiControlPlan AiPlan { get; }

        /// <summary>The match seed the world RNG was seeded with.</summary>
        public ulong Seed { get; }

        /// <summary>The world RNG state read right after <see cref="ServerBootstrap.Build"/> and before the seed: the
        /// evidence that seeding after the apply equals Godot's seed-then-apply order.</summary>
        public ulong RngStateBeforeSeed { get; }

        /// <summary>Whether the flow-field wiring ran (always true outside tests).</summary>
        public bool FlowFieldsWired { get; }

        private readonly ILogSink _log;
        private readonly FactionDefinition _defaultP1;
        private readonly FactionDefinition _defaultP2;
        private readonly Action<int, float, float>? _onRequestPath;
        private readonly Action<int, float, float>? _onRequestAttackMove;
        private readonly Action<int>? _onCancelPath;

        private SimSession(SimulationHost host, ScenarioData model, FactionDefinition?[] slotDefs, DamageTable damage,
            AiControlPlan plan, ulong seed, ulong rngBeforeSeed, bool flowFields, ILogSink log,
            FactionDefinition defaultP1, FactionDefinition defaultP2)
        {
            Host = host; Model = model; SlotFactionDefs = slotDefs; DamageTable = damage; AiPlan = plan; Seed = seed;
            RngStateBeforeSeed = rngBeforeSeed; FlowFieldsWired = flowFields; _log = log;
            _defaultP1 = defaultP1; _defaultP2 = defaultP2;

            if (!flowFields) return; // test-only ServerBootstrap composition: delegates stay null, as in goldens

            // F13 step 6 — path delegates as FlowFieldBridge.ToSim: MatchLifecycleController.cs:132-135 binds
            // (id, x, z) => FlowFieldBridge.RequestPath(id, new Vector3(x, 0f, z)), which calls
            // FlowFieldSteeringSystem.RequestPath(world, id, new FixedVec3(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z)))
            // (FlowFieldBridge.cs:50-77). The float x/z are OrderApplier's Fixed.FromRaw(raw).ToFloat() (NetworkCommand.cs),
            // so the round trip is lossless below 256 world units (plan A F14).
            EntityWorld world = host.World;
            FlowFieldSteeringSystem steering = host.Steering;
            _onRequestPath       = (id, x, z) => steering.RequestPath(world, id, new FixedVec3(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z)));
            _onRequestAttackMove = (id, x, z) => steering.RequestAttackMove(world, id, new FixedVec3(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z)));
            _onCancelPath        = id => steering.CancelPath(id);
        }

        /// <summary>
        /// Build a match. <paramref name="contentRoot"/> is the directory <c>res://</c> names (the Godot project root
        /// that holds <c>resources/data</c>); <paramref name="scenarioPath"/> is a file path, or a <c>res://</c> path
        /// resolved under <paramref name="contentRoot"/>. Throws <see cref="SimSessionBuildException"/> on failure.
        /// </summary>
        public static SimSession Build(string contentRoot, string scenarioPath, ulong seed, bool aiOn, ILogSink? log = null)
            => Build(contentRoot, scenarioPath, seed, aiOn, log, null);

        /// <summary>The full build, with the test-only <paramref name="options"/>.</summary>
        internal static SimSession Build(string contentRoot, string scenarioPath, ulong seed, bool aiOn, ILogSink? log,
            SimSessionOptions? options)
        {
            if (string.IsNullOrEmpty(contentRoot)) throw new ArgumentException("contentRoot is required", nameof(contentRoot));
            if (string.IsNullOrEmpty(scenarioPath)) throw new ArgumentException("scenarioPath is required", nameof(scenarioPath));
            ILogSink sink = log ?? new NullLogSink();
            bool flowFields = options?.FlowFields ?? true;

            // MainScene._EnterTree pins InvariantCulture before any load (MainScene.cs:333-341); ServerBootstrap.Build
            // repeats the pin later (ServerBootstrap.cs:46-51). Pin here too, so the loaders below run under it.
            CultureInfo.DefaultThreadCurrentCulture   = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentCulture   = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

            FactionDefinition p1Def, p2Def;
            FactionDefinition?[] slotDefs;
            DamageTable damageTable;
            AbilityRegistry abilities;
            ItemRegistry items;
            ScenarioData? model;
            try
            {
                // Faction defaults — MainScene.cs:402-411 / :3318-3321 (missing file → empty definition).
                p1Def = LoadFactionOrEmpty(Resolve(contentRoot, P1_FACTION_REL));
                p2Def = LoadFactionOrEmpty(Resolve(contentRoot, P2_FACTION_REL));
                // Slot storage from a registry instance, as MainScene.cs:3328-3331.
                var factions = new FactionRegistry(2);
                slotDefs = factions.SlotDefinitions;
                slotDefs[(int)Faction.Player1] = p1Def;
                slotDefs[(int)Faction.Player2] = p2Def;

                // Damage table — MainScene.cs:518-521 (missing file → DamageTable.Default).
                string dtAbs = Resolve(contentRoot, DAMAGE_TABLE_REL);
                damageTable = File.Exists(dtAbs) ? DamageTable.Load(dtAbs) : DamageTable.Default;

                // Ability registry — MainScene.cs:419-421 (ServerBootstrap resolves the slot defs against it).
                abilities = AbilityRegistry.LoadFromDirectory(Resolve(contentRoot, ABILITIES_DIR_REL),
                    name => sink.Warn($"[SimSession] skipped invalid ability {name}"));

                // Item registry — MainScene.cs:431-434 (Empty when the directory is absent).
                items = ItemRegistry.LoadFromDirectory(Resolve(contentRoot, ITEMS_DIR_REL),
                    name => sink.Warn($"[SimSession] skipped invalid item {name}"));

                // Scenario model — MainScene.cs:3343-3345.
                string scnAbs = Resolve(contentRoot, scenarioPath);
                model = ScenarioSerializer.LoadFromFile(scnAbs, out string? parseError);
                if (model == null)
                    throw new SimSessionBuildException(SimSessionFailure.ContentLoad,
                        $"scenario '{scenarioPath}' missing or unparseable: {parseError}");

                // Per-slot faction resolution — MainScene.cs:3351-3359 (mirrors ScenarioLoadPhase).
                foreach (ScenarioPlayerSlot slot in model.PlayerSlots ?? Array.Empty<ScenarioPlayerSlot>())
                {
                    if (string.IsNullOrEmpty(slot.FactionJson)) continue;
                    Faction f = FactionRegistry.ToFaction(slot.Slot);
                    if ((int)f < 0 || (int)f >= slotDefs.Length) continue;
                    string fAbs = Resolve(contentRoot, slot.FactionJson);
                    if (File.Exists(fAbs)) slotDefs[(int)f] = FactionDefinition.LoadFromFile(fAbs);
                }
            }
            catch (SimSessionBuildException) { throw; }
            catch (Exception e)
            {
                throw new SimSessionBuildException(SimSessionFailure.ContentLoad, "content load failed: " + e.Message, e);
            }

            // Player count — MainScene.cs:3364 (ClampActivePlayers == PlayerCountPolicy.SimActivePlayers, :3410-3411).
            int activePlayers = PlayerCountPolicy.SimActivePlayers(model.PlayerSlots?.Length ?? 0);

            // The validated Godot-free spine — MainScene.cs:3369-3370, plus the loaded items (the Godot client's
            // 8th SimulationHost.Create argument, MainScene.cs:527-535). aiLevel stays Normal (MainScene.cs:252).
            SimulationHost? host = ServerBootstrap.Build(model, slotDefs, damageTable, sink,
                activeFactionCount: activePlayers, abilityRegistry: abilities, itemRegistry: items);
            if (host == null)
                throw new SimSessionBuildException(SimSessionFailure.ScenarioRejected,
                    $"scenario '{scenarioPath}' rejected by ScenarioValidator");

            ulong rngBeforeSeed = host.World.Rng.State;

            // F13 step 1 — AI control plan: MainScene.cs:2990-2994 (no retained skirmish setup ⇒ OfflineDefault).
            AiControlPlan plan = aiOn ? AiControlPlan.OfflineDefault : AiControlPlan.None;
            host.SetAiControlPlan(plan);

            // F13 step 2 — match seed: MainScene.cs:3006-3007 (CHIMERA_MATCH_SEED pins it, MatchSeedProducer.cs:36).
            host.World.Rng.Seed(seed);

            var session = new SimSession(host, model, slotDefs, damageTable, plan, seed, rngBeforeSeed, flowFields, sink,
                p1Def, p2Def);

            if (flowFields)
            {
                // F13 step 3 — footprint source: NavigationPhase.cs:95-96, resolver = NavigationPhase.cs:133-162.
                host.FlowFields.SetBuildingFootprintSource(
                    BuildingNavFootprint.ObstacleExtentSource(host.Buildings, session.ResolveBuildingDef));

                // F13 step 4 — static blocked cells: ScenarioLoadPhase.cs:314-321 (BuildAndInjectPathabilityGrid);
                // the same recipe and inputs ServerBootstrap.Build used for the applier (flat ⇒ no elevation grid).
                host.FlowFields.SetStaticBlocked(ScenarioApplier.BuildPathabilityGrid(model, null)?.Blocked);

                // F13 step 5 — FlowFieldInitPhase.cs:21-26: seed the obstacle map, then the steering baseline.
                host.FlowFields.RebuildObstacles(host.Buildings);
                host.Steering.SyncBuildingBaseline();
                // F13 step 6 (path delegates) is bound in the constructor.
            }
            return session;
        }

        /// <summary>
        /// The one way in: apply <paramref name="order"/> for <paramref name="faction"/> through
        /// <see cref="OrderApplier.Apply"/> with every handle the live lockstep path passes
        /// (<c>LockstepManager.cs:437-439</c>, the same tail as the merged exec-tick path <c>LockstepManager.cs:562-566</c>). Returns <c>true</c> when the order reached a live unit of
        /// <paramref name="faction"/> that can take orders, <c>false</c> when <see cref="OrderApplier.Apply"/> dropped
        /// it at its subject guard (stale or dead ref, another faction's unit, a phased unit; <c>NetworkCommand.cs</c>).
        /// The pre-check only REPORTS: <see cref="OrderApplier.Apply"/> is called either way, so behaviour is identical.
        /// Building-family, DslEvent and Concede orders (no entity subject) report <c>true</c>.
        /// </summary>
        public bool ApplyOrder(Faction faction, in UnitOrder order)
        {
            bool applied = true;
            var cmd = (UnitCommand)((byte)order.Command & UnitOrderFlags.CommandMask);
            if (IsEntitySubject(cmd))
            {
                EntityWorld w = Host.World;
                applied = w.TryResolveRef(order.UnitId, out int id)
                          && w.FactionOf[id] == faction
                          && (w.Flags[id] & EntityFlags.Phased) == 0;
            }

            OrderApplier.Apply(Host.World, in order, faction,
                _onRequestPath, _onRequestAttackMove, _onCancelPath,
                Host.BuildSys, Host.CombatEvents, Host.ItemSys, Host.ResearchSys, Host.DslEventSink,
                Host.WinState, _log, Host.HeroXp);
            return applied;
        }

        /// <summary>Advance exactly one tick (<see cref="SimulationHost.StepOnce"/>).</summary>
        public void Step() => Host.StepOnce();

        /// <summary>The five pre-tick hashes and tick0 for this match (<see cref="PreTickHashes"/>). Call before the
        /// first <see cref="Step"/>.</summary>
        public PreTickHashes PreTick() =>
            PreTickHashes.Compute(Host, Model, SlotFactionDefs, DamageTable, AiPlan);

        /// <summary>
        /// The slot → <see cref="BuildingDefinition"/> resolver, identical to <c>NavigationPhase.ResolveBuildingDef</c>
        /// (<c>NavigationPhase.cs:133-157</c>): the owning slot's definition, then an ascending scan of every slot,
        /// then the default P1/P2 definitions; each lookup null-tolerant (<c>NavigationPhase.cs:161-162</c>).
        /// </summary>
        private BuildingDefinition? ResolveBuildingDef(int slot)
        {
            BuildingStore buildings = Host.Buildings;
            if (slot < 0 || slot >= BuildingStore.MAX_BUILDINGS) return null;

            string defId = buildings.DefinitionId[slot];
            if (string.IsNullOrEmpty(defId)) return null;

            FactionDefinition?[] slots = SlotFactionDefs;
            int owner = (int)buildings.FactionOf[slot];
            if (owner >= 0 && owner < slots.Length)
            {
                BuildingDefinition? d = FindBuilding(slots[owner], defId);
                if (d != null) return d;
            }
            for (int i = 0; i < slots.Length; i++)
            {
                BuildingDefinition? d = FindBuilding(slots[i], defId);
                if (d != null) return d;
            }
            return FindBuilding(_defaultP1, defId) ?? FindBuilding(_defaultP2, defId);
        }

        /// <summary><c>NavigationPhase.FindBuilding</c> (<c>NavigationPhase.cs:161-162</c>).</summary>
        private static BuildingDefinition? FindBuilding(FactionDefinition? faction, string defId)
            => faction?.Buildings == null ? null : faction.GetBuilding(defId);

        /// <summary>Entity-subject commands: everything <see cref="OrderApplier.Apply"/> does not dispatch before its
        /// <c>TryResolveRef</c> subject guard (the building family, DslEvent and Concede are dispatched first).</summary>
        private static bool IsEntitySubject(UnitCommand cmd) =>
            cmd != UnitCommand.Train && cmd != UnitCommand.CancelTrain && cmd != UnitCommand.CancelConstruction
            && cmd != UnitCommand.SetRally && cmd != UnitCommand.ReviveHero && cmd != UnitCommand.BuyItem
            && cmd != UnitCommand.StartResearch && cmd != UnitCommand.CancelResearch
            && cmd != UnitCommand.DslEvent && cmd != UnitCommand.Concede;

        private static FactionDefinition LoadFactionOrEmpty(string abs)
            => File.Exists(abs) ? FactionDefinition.LoadFromFile(abs) : new FactionDefinition();

        /// <summary>Map a <c>res://</c> path under <paramref name="contentRoot"/>; any other path is returned as a
        /// full path (relative paths resolve against <paramref name="contentRoot"/>).</summary>
        private static string Resolve(string contentRoot, string path)
        {
            string rel = path.StartsWith(RES_PREFIX, StringComparison.Ordinal) ? path.Substring(RES_PREFIX.Length) : path;
            rel = rel.Replace('/', Path.DirectorySeparatorChar);
            return Path.IsPathRooted(rel) ? rel : Path.GetFullPath(Path.Combine(contentRoot, rel));
        }
    }
}
