#nullable enable
using Godot;
using ProjectChimera.Core.Definitions;
using ProjectChimera.UI;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 1.8c "ScenarioLoad" phase (runtime position 12). The presentation orchestration that resolves the
    /// scenario (AI-generated → file → hardcoded fallback), runs the faction-resolution pre-pass and the Story-1.7
    /// validation gate, and hands the validated model to the Godot-free <see cref="ScenarioApplier"/> (the sole
    /// writer of sim truth), then builds the start-position markers. Publishes Scenario / FallbackMirror /
    /// ScenarioApplied / StartPosBridge on the context (read by the _Ready scenario-hash tail, MoveStartPosition,
    /// CheckWinCondition, and the win/trigger/map-gen UI phases). Behavior-identical to the former
    /// MainScene.LoadAndApplyScenario family; no sim-write path changes.
    /// </summary>
    public sealed class ScenarioLoadPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public ScenarioLoadPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "ScenarioLoad";

        /// <summary>The single pre-tick validation gate (Godot-free). Shadow-mode on master.</summary>
        private readonly ScenarioValidator _validator = new();
        /// <summary>Fail-closed toggle (CHIMERA_VALIDATE_FAILCLOSED, default off). Flip only on a release branch.</summary>
        private static readonly bool _failClosed = ScenarioGate.IsFailClosed();

        /// <summary>
        /// Pending AI-generated scenario: written by the MapGenerator before the scene reload, consumed here.
        /// Static so it survives the Godot scene reload cycle (the new scene's ScenarioLoadPhase reads it).
        /// </summary>
        internal static ScenarioData? PendingGeneratedScenario;

        public void Run() => LoadAndApplyScenario();

        /// <summary>
        /// Resolve <see cref="MainScene.ScenarioPath"/>, load the JSON, and apply it. Falls back to a hardcoded
        /// default if the file is missing or fails to parse.
        /// </summary>
        private void LoadAndApplyScenario()
        {
            // Check for an AI-generated scenario passed across the scene reload boundary.
            if (PendingGeneratedScenario != null)
            {
                var generated = PendingGeneratedScenario;
                PendingGeneratedScenario = null;
                _ctx.Scenario = generated;
                ApplyScenarioThroughApplier(generated, "ApplyScenario");
                GD.Print($"[MainScene] Loaded AI-generated scenario: \"{generated.DisplayName}\"");
                SetupStartPositionBridge();
                return;
            }

            string abs = ProjectSettings.GlobalizePath(_ctx.Scene.ScenarioPath);
            var scenario = ScenarioSerializer.LoadFromFile(abs);

            if (scenario == null)
            {
                GD.PrintErr($"[MainScene] Scenario not found or failed to parse: {_ctx.Scene.ScenarioPath} — using defaults.");
                ApplyFallbackThroughApplier();
            }
            else
            {
                _ctx.Scenario = scenario;
                ApplyScenarioThroughApplier(scenario, "ApplyScenario");
                GD.Print($"[MainScene] Loaded scenario: \"{scenario.DisplayName}\" ({scenario.Id})");
            }

            SetupStartPositionBridge();
        }

        /// <summary>
        /// Story 1.7 shadow-mode gate: run the model through the validator and return its result. On failure, log
        /// a LOCATED rejection (presentation-side — the Godot-free validator never logs). The caller applies
        /// <c>result.Value</c> when <see cref="ScenarioGate.ShouldProceed"/> permits. Never throws.
        /// </summary>
        private ValidationResult ValidateBeforeApply(ScenarioData model, string pathLabel)
        {
            ValidationResult result = _validator.Validate(model);
            if (!result.Ok)
                GD.PrintErr($"[ScenarioValidator] {pathLabel} REJECTED: {result.Error}");
            return result;
        }

        /// <summary>
        /// Story 1.8b (D4) — presentation faction-resolution pre-pass. Resolves each player slot's res:// faction
        /// JSON to an absolute OS path and populates ctx.SlotFactionDefs IN PLACE before the Godot-free applier
        /// runs. The ONLY ProjectSettings.GlobalizePath on the scenario-apply path. Slots without an explicit
        /// faction_json keep their _Ready-seeded defaults.
        /// </summary>
        private void ResolveSlotFactionDefs(ScenarioData scenario)
        {
            foreach (var slot in scenario.PlayerSlots ?? System.Array.Empty<ScenarioPlayerSlot>())
            {
                if (string.IsNullOrEmpty(slot.FactionJson)) continue;
                var faction = (Faction)(slot.Slot + 1); // slot 0 → Player1, slot 1 → Player2
                string abs = ProjectSettings.GlobalizePath(slot.FactionJson);
                if (System.IO.File.Exists(abs))
                    _ctx.SlotFactionDefs[(int)faction] = FactionDefinition.LoadFromFile(abs);
            }
        }

        /// <summary>
        /// Story 1.8b — presentation orchestration for a parsed scenario: faction pre-pass, the 1.7 validation
        /// gate, then (when shadow / fail-closed policy permits) hand the validated model to the applier.
        /// </summary>
        private void ApplyScenarioThroughApplier(ScenarioData scenario, string pathLabel)
        {
            ResolveSlotFactionDefs(scenario);                            // the one Godot path-resolution, hoisted
            ValidationResult r = ValidateBeforeApply(scenario, pathLabel);
            if (ScenarioGate.ShouldProceed(r.Ok, _failClosed))          // shadow proceeds even when r.Ok == false
            {
                _ctx.Applier.Apply(r.Value);
                _ctx.ScenarioApplied = true; // reached only when the gate permits applying (Story 1.7 review patch)
            }
        }

        /// <summary>
        /// Story 1.8b — fallback path (scenario JSON missing): build the ScenarioData mirror so it passes the same
        /// validation gate and yields a real canonical-model hash, then apply the hardcoded fallback through the
        /// applier (the always-applied safety net; its gate result is shadow-validation only).
        /// </summary>
        private void ApplyFallbackThroughApplier()
        {
            _ctx.FallbackMirror = BuildFallbackMirror();
            ValidateBeforeApply(_ctx.FallbackMirror, "fallback"); // shadow-validation only (result intentionally not used)
            _ctx.ScenarioApplied = true; // the fallback is the always-applied safety net (Story 1.7 review patch)
            _ctx.Applier.ApplyFallback();
        }

        /// <summary>
        /// Story 1.7: a ScenarioData mirror of the hardcoded ScenarioApplier.ApplyFallback layout, used ONLY to
        /// feed the validation gate and the canonical-model hash. Keep these literal values in sync with the
        /// applier's fallback; unit_id "worker" is the conventional worker id.
        /// </summary>
        private static ScenarioData BuildFallbackMirror() => new ScenarioData
        {
            Id           = "fallback",
            DisplayName  = "Fallback",
            MapBounds    = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, StartOre = 200f, BaseX = -45f, BaseZ = 0f },
                new ScenarioPlayerSlot { Slot = 1, StartOre = 200f, BaseX =  45f, BaseZ = 0f },
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
                new ScenarioUnit { UnitId = "worker", Slot = 0, X = -42f, Z = -3f },
                new ScenarioUnit { UnitId = "worker", Slot = 0, X = -42f, Z =  3f },
                new ScenarioUnit { UnitId = "worker", Slot = 1, X =  42f, Z = -3f },
                new ScenarioUnit { UnitId = "worker", Slot = 1, X =  42f, Z =  3f },
            },
        };

        /// <summary>
        /// Create flag-pole markers for the two player start positions. Reads initial XZ from the live scenario
        /// (or fallback defaults). Publishes ctx.StartPosBridge (used by MoveStartPosition).
        /// </summary>
        private void SetupStartPositionBridge()
        {
            var positions = new (float x, float z)[2];

            if (_ctx.Scenario != null)
            {
                foreach (var slot in _ctx.Scenario.PlayerSlots)
                {
                    int idx = System.Math.Clamp(slot.Slot, 0, 1);
                    positions[idx] = (slot.BaseX, slot.BaseZ);
                }
            }
            else
            {
                // Fallback positions matching ScenarioApplier.ApplyFallback
                positions[0] = (-45f, 0f);
                positions[1] = (+45f, 0f);
            }

            var startPosBridge = new StartPositionBridge();
            _ctx.Scene.AddChild(startPosBridge);
            startPosBridge.Initialize(positions);
            _ctx.StartPosBridge = startPosBridge;
        }
    }
}
