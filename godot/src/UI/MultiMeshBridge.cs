using Godot;
using ProjectChimera.Core;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Reads entity positions from the simulation and updates a MultiMeshInstance3D
    /// each frame. Interpolates between prev and current positions for smooth rendering.
    /// One bridge per faction (or pass Faction.Neutral to render all).
    /// </summary>
    public partial class MultiMeshBridge : MultiMeshInstance3D
    {
        private SimulationLoop _simLoop;
        private Faction _faction;
        private float   _yOffset;
        private bool _initialized;

        /// <summary>
        /// Initialize the bridge with a simulation loop reference, mesh, max instance count,
        /// and optional faction filter (Faction.Neutral = render all).
        /// </summary>
        public void Initialize(SimulationLoop simLoop, Mesh unitMesh, int maxInstances,
            Faction faction = Faction.Neutral, float yOffset = 0f)
        {
            _simLoop = simLoop;
            _faction = faction;
            _yOffset = yOffset;

            Multimesh = new MultiMesh();
            Multimesh.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
            Multimesh.Mesh = unitMesh;
            Multimesh.InstanceCount = maxInstances;

            // Hide all instances initially
            var hidden = Transform3D.Identity.Scaled(Vector3.Zero);
            for (int i = 0; i < maxInstances; i++)
                Multimesh.SetInstanceTransform(i, hidden);

            _initialized = true;
        }

        public override void _Process(double delta)
        {
            if (!_initialized) return;

            EntityWorld world = _simLoop.World;
            float alpha = _simLoop.InterpolationAlpha;
            int simCount = world.HighWaterMark;
            int instanceCount = Multimesh.InstanceCount;
            var hidden = Transform3D.Identity.Scaled(Vector3.Zero);

            // We map alive entities of our faction to consecutive instance slots
            int slot = 0;
            for (int i = 0; i < simCount && slot < instanceCount; i++)
            {
                if (!world.IsAlive(i)) continue;
                if (_faction != Faction.Neutral && world.FactionOf[i] != _faction) continue;

                Vector3 prev = world.PrevPosition[i].ToGodotVector3();
                Vector3 curr = world.Position[i].ToGodotVector3();
                Vector3 renderPos = prev.Lerp(curr, alpha);

                Multimesh.SetInstanceTransform(slot, new Transform3D(Basis.Identity,
                    renderPos + new Vector3(0f, _yOffset, 0f)));
                slot++;
            }

            // Hide unused slots
            for (int s = slot; s < instanceCount; s++)
                Multimesh.SetInstanceTransform(s, hidden);
        }
    }
}
