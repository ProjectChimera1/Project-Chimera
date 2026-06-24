using Godot;
using ProjectChimera.Combat;
using ProjectChimera.Navigation;
using ProjectChimera.UI;

namespace ProjectChimera.Core
{
    /// <summary>
    /// P0.1 Stress Test: Spawns two factions of units that seek and fight each other.
    /// Validates MultiMesh rendering, SimulationLoop, MovementSystem, and CombatSystem
    /// at 500 units with target 60 FPS.
    /// </summary>
    public partial class StressTest : Node3D
    {
        [Export] public int UnitsPerFaction { get; set; } = 250; // 500 total
        [Export] public float SpawnRadius { get; set; } = 40.0f;
        [Export] public float SpawnOffsetX { get; set; } = 30.0f; // Factions spawn apart

        // Combat stats
        private static readonly float ATTACK_RANGE = 5.0f;
        private static readonly float ATTACK_DAMAGE = 10.0f;
        private static readonly float ATTACK_SPEED = 1.0f;  // 1 attack/sec
        private static readonly float UNIT_HEALTH = 100.0f;
        private static readonly float UNIT_SPEED_MIN = 3.0f;
        private static readonly float UNIT_SPEED_MAX = 5.0f;

        private SimulationLoop _simLoop;
        private MultiMeshBridge _meshBridgeP1;
        private MultiMeshBridge _meshBridgeP2;
        private Label _debugLabel;
        private RandomNumberGenerator _rng;

        public override void _Ready()
        {
            _rng = new RandomNumberGenerator();
            _rng.Randomize();

            var world       = new EntityWorld();
            var projectiles = new Combat.ProjectileStore();
            _simLoop = new SimulationLoop(world,
                new MovementSystem(),
                new CombatSystem(projectiles),
                new Combat.ProjectileSystem(projectiles));

            SpawnFaction(world, Faction.Player1, -SpawnOffsetX, ArmorType.Light, DamageType.Pierce);
            SpawnFaction(world, Faction.Player2, +SpawnOffsetX, ArmorType.Medium, DamageType.Normal);

            // Two MultiMesh instances — one per faction color
            _meshBridgeP1 = CreateBridge(world, new Color(0.2f, 0.5f, 1.0f), Faction.Player1);
            _meshBridgeP2 = CreateBridge(world, new Color(1.0f, 0.3f, 0.2f), Faction.Player2);

            SetupCamera();
            SetupLighting();
            SetupGround();
            SetupDebugLabel();

            GD.Print($"[StressTest] Spawned {world.AliveCount} units ({UnitsPerFaction} per faction)");
        }

        public override void _Process(double delta)
        {
            int ticks = _simLoop.Update((float)delta);
            EntityWorld world = _simLoop.World;

            _debugLabel.Text =
                $"FPS: {Engine.GetFramesPerSecond()}\n" +
                $"Alive: {world.AliveCount}  (P1: {CountFaction(world, Faction.Player1)}  P2: {CountFaction(world, Faction.Player2)})\n" +
                $"Sim Tick: {_simLoop.CurrentTick}   Ticks/Frame: {ticks}";
        }

        // --- Spawning ---

        private void SpawnFaction(EntityWorld world, Faction faction, float xOffset,
            ArmorType armor, DamageType dmgType)
        {
            for (int i = 0; i < UnitsPerFaction; i++)
            {
                var pos = new FixedVec3(
                    Fixed.FromFloat(xOffset + _rng.RandfRange(-SpawnRadius, SpawnRadius)),
                    Fixed.Zero,
                    Fixed.FromFloat(_rng.RandfRange(-SpawnRadius, SpawnRadius))
                );
                Fixed speed = Fixed.FromFloat(_rng.RandfRange(UNIT_SPEED_MIN, UNIT_SPEED_MAX));
                int id = world.Create(pos, faction, Fixed.FromFloat(UNIT_HEALTH), speed);
                if (id < 0) break;

                world.AttackRange[id] = Fixed.FromFloat(ATTACK_RANGE);
                world.AttackDamage[id] = Fixed.FromFloat(ATTACK_DAMAGE);
                world.AttackSpeed[id] = Fixed.FromFloat(ATTACK_SPEED);
                world.DamageTypeOf[id] = dmgType;
                world.ArmorTypeOf[id] = armor;
            }
        }

        // --- Scene setup ---

        private MultiMeshBridge CreateBridge(EntityWorld world, Color color, Faction faction)
        {
            var mesh = new BoxMesh();
            mesh.Size = new Vector3(0.6f, 1.2f, 0.6f);
            var mat = new StandardMaterial3D();
            mat.AlbedoColor = color;
            mesh.Material = mat;

            var bridge = new MultiMeshBridge();
            AddChild(bridge);
            bridge.Initialize(_simLoop, mesh, EntityWorld.MAX_ENTITIES, faction);
            return bridge;
        }

        private void SetupCamera()
        {
            var camera = new Camera3D();
            camera.Position = new Vector3(0, 80, 80);
            camera.LookAtFromPosition(camera.Position, Vector3.Zero);
            AddChild(camera);
        }

        private void SetupLighting()
        {
            var light = new DirectionalLight3D();
            light.Rotation = new Vector3(Mathf.DegToRad(-50), Mathf.DegToRad(30), 0);
            light.LightEnergy = 1.2f;
            AddChild(light);

            var ambient = new WorldEnvironment();
            var env = new Godot.Environment();
            env.AmbientLightSource = Godot.Environment.AmbientSource.Color;
            env.AmbientLightColor = new Color(0.3f, 0.3f, 0.35f);
            env.AmbientLightEnergy = 0.5f;
            ambient.Environment = env;
            AddChild(ambient);
        }

        private void SetupGround()
        {
            var ground = new MeshInstance3D();
            var planeMesh = new PlaneMesh();
            planeMesh.Size = new Vector2(300, 300);
            var mat = new StandardMaterial3D();
            mat.AlbedoColor = new Color(0.12f, 0.18f, 0.12f);
            planeMesh.Material = mat;
            ground.Mesh = planeMesh;
            AddChild(ground);
        }

        private void SetupDebugLabel()
        {
            _debugLabel = new Label();
            _debugLabel.Position = new Vector2(10, 10);
            _debugLabel.AddThemeColorOverride("font_color", Colors.White);
            _debugLabel.AddThemeFontSizeOverride("font_size", 20);
            var canvas = new CanvasLayer();
            canvas.AddChild(_debugLabel);
            AddChild(canvas);
        }

        // --- Helpers ---

        private static int CountFaction(EntityWorld world, Faction faction)
        {
            int count = 0;
            int cap = world.HighWaterMark;
            for (int i = 0; i < cap; i++)
                if (world.IsAlive(i) && world.FactionOf[i] == faction) count++;
            return count;
        }
    }
}
