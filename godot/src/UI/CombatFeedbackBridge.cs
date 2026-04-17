#nullable enable
using Godot;
using ProjectChimera.Combat;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Presentation-layer bridge that converts CombatEventQueue entries into pooled visual effects.
    ///
    /// Each frame:
    ///   1. Drains the event queue and spawns a hit-flash MeshInstance3D from a pre-allocated pool.
    ///   2. Ticks active flashes: scales them down and hides them when expired.
    ///   3. Kill events additionally trigger a brief camera shake via RtsCameraController.
    ///
    /// Flash types:
    ///   MeleeHit   — small orange burst  (scale 0.9, 0.18 s)
    ///   RangedHit  — small yellow burst  (scale 0.7, 0.15 s)
    ///   SplashHit  — large red burst     (scale 1.8, 0.28 s)
    ///   UnitKilled — medium white burst  (scale 1.2, 0.25 s) + camera shake
    /// </summary>
    public partial class CombatFeedbackBridge : Node3D
    {
        private const int MAX_FLASHES = 48;

        private CombatEventQueue?    _events;
        private RtsCameraController? _camCtrl;

        private MeshInstance3D[] _flashNodes    = new MeshInstance3D[MAX_FLASHES];
        private float[]          _flashTimer    = new float[MAX_FLASHES];
        private float[]          _flashDuration = new float[MAX_FLASHES];
        private float[]          _flashScale    = new float[MAX_FLASHES];

        private StandardMaterial3D _matMelee   = null!;
        private StandardMaterial3D _matRanged  = null!;
        private StandardMaterial3D _matSplash  = null!;
        private StandardMaterial3D _matDeath   = null!;

        public void Initialize(CombatEventQueue events, RtsCameraController camCtrl)
        {
            _events  = events;
            _camCtrl = camCtrl;

            _matMelee  = MakeMat(new Color(1.0f, 0.50f, 0.10f), 3.0f); // orange
            _matRanged = MakeMat(new Color(1.0f, 0.85f, 0.10f), 2.5f); // yellow
            _matSplash = MakeMat(new Color(1.0f, 0.20f, 0.05f), 4.0f); // red
            _matDeath  = MakeMat(new Color(1.0f, 0.95f, 0.80f), 5.0f); // white

            // Shared low-poly sphere mesh for all flash slots
            var mesh = new SphereMesh();
            mesh.Radius        = 0.3f;
            mesh.Height        = 0.6f;
            mesh.RadialSegments = 6;
            mesh.Rings          = 4;

            for (int i = 0; i < MAX_FLASHES; i++)
            {
                var node = new MeshInstance3D();
                node.Mesh    = mesh;
                node.Visible = false;
                AddChild(node);
                _flashNodes[i] = node;
            }
        }

        public override void _Process(double delta)
        {
            if (_events == null) return;

            // ── Spawn flashes for new events ──────────────────────────────────
            int evtCount = _events.Count;
            for (int e = 0; e < evtCount; e++)
            {
                var evt = _events.Get(e);
                Vector3 pos = evt.Position.ToGodotVector3();
                pos.Y += 0.5f; // lift slightly above ground

                switch (evt.Type)
                {
                    case CombatEventType.MeleeHit:
                        SpawnFlash(pos, 0.9f, 0.18f, _matMelee);
                        break;

                    case CombatEventType.RangedHit:
                        SpawnFlash(pos, 0.7f, 0.15f, _matRanged);
                        break;

                    case CombatEventType.SplashHit:
                        SpawnFlash(pos, 1.8f, 0.28f, _matSplash);
                        break;

                    case CombatEventType.UnitKilled:
                        SpawnFlash(pos, 1.2f, 0.25f, _matDeath);
                        _camCtrl?.SetShake(0.12f, 0.22f);
                        break;
                }
            }
            _events.Clear();

            // ── Tick active flashes: shrink and hide when expired ─────────────
            float dt = (float)delta;
            for (int i = 0; i < MAX_FLASHES; i++)
            {
                if (_flashTimer[i] <= 0f) continue;

                _flashTimer[i] -= dt;
                if (_flashTimer[i] <= 0f)
                {
                    _flashTimer[i]     = 0f;
                    _flashNodes[i].Visible = false;
                    continue;
                }

                // Linear scale-down: full size at spawn, zero at expiry
                float t = _flashTimer[i] / _flashDuration[i];
                _flashNodes[i].Scale = Vector3.One * (_flashScale[i] * t);
            }
        }

        /// <summary>Claims the next free flash slot and starts the effect.</summary>
        private void SpawnFlash(Vector3 pos, float baseScale, float duration, StandardMaterial3D mat)
        {
            for (int i = 0; i < MAX_FLASHES; i++)
            {
                if (_flashTimer[i] > 0f) continue;

                _flashNodes[i].Position = pos;
                _flashNodes[i].Scale    = Vector3.One * baseScale;
                _flashNodes[i].SetSurfaceOverrideMaterial(0, mat);
                _flashNodes[i].Visible  = true;

                _flashTimer[i]    = duration;
                _flashDuration[i] = duration;
                _flashScale[i]    = baseScale;
                return;
            }
            // Pool exhausted — silently drop (non-critical cosmetic)
        }

        private static StandardMaterial3D MakeMat(Color color, float emissionMult)
        {
            var mat = new StandardMaterial3D();
            mat.ShadingMode     = BaseMaterial3D.ShadingModeEnum.Unshaded;
            mat.AlbedoColor     = color;
            mat.EmissionEnabled = true;
            mat.Emission        = color * emissionMult;
            return mat;
        }
    }
}
