#nullable enable
using System.Collections.Generic;
using Godot;
using ProjectChimera.Core.Definitions;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 6.6 — renders placed doodads/props from <see cref="ScenarioData.Props"/> using ONE
    /// <see cref="MultiMeshInstance3D"/> per distinct prop mesh (never a node per prop — the 10.2 frame budget stays
    /// presentation-only at hundreds of props). Each per-instance transform is position + yaw (<c>rot</c>) + uniform
    /// <c>scale</c>. Purely presentation: it reads the live scenario each frame (late-bound via the injected getter,
    /// so it survives the F5 Edit→Play re-apply and scene reloads) and rebuilds only when the prop set changes
    /// (reference swap — the sync path replaces the array — or a cheap content signature that also catches in-place
    /// R-key rotation). The sim never touches props; a <c>blocks_pathing</c> prop's footprint reaches the sim only
    /// through the load-time <c>PathabilityGrid</c>, never through this renderer.
    /// </summary>
    public partial class PropRenderer : Node3D
    {
        private System.Func<ScenarioData?>? _scenarioGetter;

        // One MultiMeshInstance3D per distinct prop_id (mesh). Rebuilt wholesale when the prop set changes.
        private readonly Dictionary<string, MultiMeshInstance3D> _byPropId = new();

        // Change-detection: the last-seen Props array reference + a cheap content signature (so an in-place rot/scale
        // edit — which does NOT swap the array reference — still triggers a rebuild).
        private ScenarioProp[]? _lastRef;
        private long            _lastSignature = -1L; // sentinel; a real signature is any 64-bit FNV value

        /// <summary>Wire the late-bound scenario source. Called once after the scenario has loaded.</summary>
        public void Initialize(System.Func<ScenarioData?> scenarioGetter)
        {
            _scenarioGetter = scenarioGetter;
            Rebuild();
        }

        public override void _Process(double delta)
        {
            ScenarioProp[]? props = _scenarioGetter?.Invoke()?.Props;
            if (ReferenceEquals(props, _lastRef))
            {
                // Same array reference — only a cheap in-place edit (R-key rot / scale) could have changed content.
                long sig = Signature(props);
                if (sig == _lastSignature) return;
            }
            Rebuild();
        }

        /// <summary>Rebuild every prop MultiMesh from the live <see cref="ScenarioData.Props"/>. Idempotent.</summary>
        public void Rebuild()
        {
            ScenarioProp[]? props = _scenarioGetter?.Invoke()?.Props;
            _lastRef       = props;
            _lastSignature = Signature(props);

            // Bucket props by prop_id (mesh), preserving encounter order for deterministic instance layout.
            var buckets = new Dictionary<string, List<ScenarioProp>>();
            if (props != null)
                foreach (ScenarioProp p in props)
                {
                    if (p == null) continue;
                    string key = p.PropId ?? "";
                    if (!buckets.TryGetValue(key, out var list)) { list = new List<ScenarioProp>(); buckets[key] = list; }
                    list.Add(p);
                }

            // Free any renderer for a prop_id that no longer exists.
            var stale = new List<string>();
            foreach (var kv in _byPropId) if (!buckets.ContainsKey(kv.Key)) stale.Add(kv.Key);
            foreach (string key in stale)
            {
                _byPropId[key].QueueFree();
                _byPropId.Remove(key);
            }

            // Rebuild each bucket's MultiMesh.
            foreach (var kv in buckets)
            {
                MultiMeshInstance3D mmi = GetOrCreate(kv.Key);
                MultiMesh mm = mmi.Multimesh!;
                List<ScenarioProp> list = kv.Value;
                mm.InstanceCount = list.Count;
                for (int i = 0; i < list.Count; i++)
                {
                    ScenarioProp p = list[i];
                    float scale = p.Scale ?? 1f;
                    var basis = new Basis(Vector3.Up, p.Rot).Scaled(new Vector3(scale, scale, scale));
                    mm.SetInstanceTransform(i, new Transform3D(basis, new Vector3(p.X, 0f, p.Z)));
                }
            }
        }

        private MultiMeshInstance3D GetOrCreate(string propId)
        {
            if (_byPropId.TryGetValue(propId, out var existing)) return existing;

            var mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh            = MeshForProp(propId),
            };
            var mmi = new MultiMeshInstance3D { Name = $"Prop_{propId}", Multimesh = mm };
            AddChild(mmi);
            _byPropId[propId] = mmi;
            return mmi;
        }

        /// <summary>A built-in mesh per prop id (no external prop-asset pipeline yet — decorative primitives keyed by a
        /// small vocabulary; any unknown id falls back to a box). The mesh choice is presentation-only.</summary>
        private static Mesh MeshForProp(string propId) => propId switch
        {
            "tree"    => new CylinderMesh { TopRadius = 0.1f, BottomRadius = 0.6f, Height = 2.5f },
            "rock"    => new BoxMesh      { Size = new Vector3(1.4f, 1.0f, 1.4f) },
            "crystal" => new PrismMesh    { Size = new Vector3(1.0f, 2.0f, 1.0f) },
            "bush"    => new SphereMesh   { Radius = 0.7f, Height = 1.2f },
            _         => new BoxMesh      { Size = new Vector3(1.0f, 1.0f, 1.0f) },
        };

        /// <summary>Exact content signature over the prop set — a 64-bit FNV-1a fold of count + each prop's x/z/rot/scale
        /// (over their raw IEEE-754 bits, not lossy float arithmetic) + id length, so an in-place R-key rotation (same
        /// array reference) always forces a rebuild. Review fix: the prior lossy <c>double</c> accumulator could collide
        /// two distinct prop sets (or round away a small nudge) and leave the MultiMesh stale; an exact integer fold
        /// cannot. Not a security hash; just a reliable change detector.</summary>
        private static long Signature(ScenarioProp[]? props)
        {
            if (props == null) return 0L;
            unchecked
            {
                long h = 1469598103934665603L; // FNV-1a 64-bit offset basis
                const long prime = 1099511628211L;
                h = (h ^ props.Length) * prime;
                foreach (ScenarioProp p in props)
                {
                    if (p == null) continue;
                    h = (h ^ System.BitConverter.SingleToInt32Bits(p.X)) * prime;
                    h = (h ^ System.BitConverter.SingleToInt32Bits(p.Z)) * prime;
                    h = (h ^ System.BitConverter.SingleToInt32Bits(p.Rot)) * prime;
                    h = (h ^ System.BitConverter.SingleToInt32Bits(p.Scale ?? 1f)) * prime;
                    h = (h ^ (p.PropId?.Length ?? 0)) * prime;
                }
                return h;
            }
        }
    }
}
