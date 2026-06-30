#nullable enable
using System.Collections.Generic;
using Godot;
using ProjectChimera.Combat;
using ProjectChimera.Core.Definitions; // CombatFeedbackProfile, FlashSpec, ShakeSpec, CombatFeedbackDefaults (Story 2.7)

namespace ProjectChimera.UI
{
    /// <summary>
    /// Presentation-layer bridge that converts CombatEventQueue entries into pooled visual effects (Story 2.7:
    /// profile-driven). Each frame:
    ///   1. Drains the event queue and spawns a hit-flash MeshInstance3D from a pre-allocated pool.
    ///   2. Resolves each event's look two-level: <c>evt.Feedback?.HitFlash/DeathFlash/Shake ?? the tuned default</c>
    ///      (the embedded <see cref="CombatFeedbackDefaults"/> — byte-for-byte today's as-built constants).
    ///   3. Ticks active flashes: scales them down and hides them when expired.
    ///   4. Kill events additionally trigger a camera shake; AbilityCast events render only if the ability opted in.
    ///   5. Optional presentation-only HIT-FREEZE (SD-7) holds the flash shrink for N render frames — it NEVER pauses
    ///      the simulation (the drain + the single Clear() run unconditionally every frame; only the shrink dt is zeroed).
    ///
    /// Default looks (null profile ⇒ today's exact behaviour):
    ///   MeleeHit   — orange  (scale 0.9, 0.18 s)   RangedHit  — yellow (scale 0.7, 0.15 s)
    ///   SplashHit  — red     (scale 1.8, 0.28 s)   UnitKilled — white  (scale 1.2, 0.25 s) + shake(0.12, 0.22)
    ///
    /// The CombatFeedbackProfile carried on each event is presentation-domain — the simulation never reads it and it
    /// is excluded from SimChecksum/the canonical hash. This node and its Dictionary cache live entirely in presentation.
    /// </summary>
    public partial class CombatFeedbackBridge : Node3D
    {
        private const int MAX_FLASHES = 48;

        /// <summary>Story 2.7 review: cap for the presentation hit-freeze (~0.5 s @ 60 fps). HitFreezeFrames is
        /// unbounded creator data; clamping here stops a huge authored value from freezing every flash and starving
        /// the pool. Purely cosmetic — the sim is never touched.</summary>
        private const int MAX_FREEZE_FRAMES = 30;

        private CombatEventQueue?    _events;
        private RtsCameraController? _camCtrl;

        private MeshInstance3D[] _flashNodes    = new MeshInstance3D[MAX_FLASHES];
        private float[]          _flashTimer    = new float[MAX_FLASHES];
        private float[]          _flashDuration = new float[MAX_FLASHES];
        private float[]          _flashScale    = new float[MAX_FLASHES];

        /// <summary>Presentation-only material cache keyed by FlashSpec reference: the four default looks are pre-warmed
        /// in <see cref="Initialize"/> and each distinct authored override look builds one material on first use
        /// (sources share the same FlashSpec instance). A Dictionary is fine here — this is presentation, not sim.</summary>
        private readonly Dictionary<FlashSpec, StandardMaterial3D> _matCache = new();

        /// <summary>Story 2.7 (SD-7): render frames remaining in the hit-freeze hold. While &gt; 0, the flash-shrink dt
        /// is forced to 0 so flashes hold; the queue drain + Clear() are UNAFFECTED. Default 0 = off (today's look).</summary>
        private int _freezeFrames;

        public void Initialize(CombatEventQueue events, RtsCameraController camCtrl)
        {
            _events  = events;
            _camCtrl = camCtrl;

            _matCache.Clear(); // Story 2.7 review: idempotent re-init — drop any prior-session override materials so a re-Initialize cannot accumulate them.

            // Pre-warm the four default looks. Byte-identical to the former inline MakeMat constants, now sourced from
            // the embedded CombatFeedbackDefaults — the SAME set the Tier-1 "default-equals-constants" test pins.
            MaterialFor(CombatFeedbackDefaults.Melee);
            MaterialFor(CombatFeedbackDefaults.Ranged);
            MaterialFor(CombatFeedbackDefaults.Splash);
            MaterialFor(CombatFeedbackDefaults.Kill);

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

            // ── Spawn flashes for new events (profile-driven; null profile/sub-spec ⇒ the tuned event-type default) ──
            int evtCount = _events.Count;
            for (int e = 0; e < evtCount; e++)
            {
                var evt = _events.Get(e);
                Vector3 pos = evt.Position.ToGodotVector3();
                pos.Y += 0.5f; // lift slightly above ground

                CombatFeedbackProfile? fb = evt.Feedback;

                switch (evt.Type)
                {
                    case CombatEventType.MeleeHit:
                        SpawnFlashFromSpec(pos, fb?.HitFlash ?? CombatFeedbackDefaults.Melee);
                        break;

                    case CombatEventType.RangedHit:
                        SpawnFlashFromSpec(pos, fb?.HitFlash ?? CombatFeedbackDefaults.Ranged);
                        break;

                    case CombatEventType.SplashHit:
                        SpawnFlashFromSpec(pos, fb?.HitFlash ?? CombatFeedbackDefaults.Splash);
                        break;

                    case CombatEventType.UnitKilled:
                        SpawnFlashFromSpec(pos, fb?.DeathFlash ?? CombatFeedbackDefaults.Kill);
                        ApplyShake(fb?.Shake ?? CombatFeedbackDefaults.KillShake);
                        break;

                    case CombatEventType.AbilityCast:
                        // Abilities opt INTO cast juice via their profile — a null HitFlash/Shake ⇒ no extra effect
                        // (there is no default cast look, mirroring AudioManager's no default cast clip).
                        if (fb?.HitFlash != null) SpawnFlashFromSpec(pos, fb.HitFlash);
                        if (fb?.Shake != null)    ApplyShake(fb.Shake);
                        break;
                }

                // Hit-freeze accrues from any event's profile (take the strongest this frame). 0 = off (today's look).
                // Clamp to MAX_FREEZE_FRAMES — HitFreezeFrames is unbounded creator data (negatives are naturally
                // ignored by the > comparison below; a huge value would otherwise freeze every flash and starve the pool).
                int frames = fb?.HitFreezeFrames ?? 0;
                if (frames > MAX_FREEZE_FRAMES) frames = MAX_FREEZE_FRAMES;
                if (frames > _freezeFrames) _freezeFrames = frames;
            }
            _events.Clear(); // CRITICAL: the bridge owns the single Clear(). It runs EVERY frame — NEVER gated by the freeze.

            // ── Tick active flashes: shrink and hide when expired ─────────────
            // Hit-freeze (SD-7): hold the shrink by forcing dt = 0 for _freezeFrames frames. The drain + Clear above
            // already ran unconditionally, so a freeze NEVER starves AudioManager (second consumer) or overflows the
            // 256-slot queue. The sim is wholly separate (MainScene._Process drives StepOnce/Update) — this never
            // touches Engine.TimeScale / GetTree().Paused / _host; it is pure presentation timing.
            float dt = (float)delta;
            if (_freezeFrames > 0)
            {
                _freezeFrames--;
                dt = 0f;
            }

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

        /// <summary>Apply a shake spec to the camera — note the camera takes (duration, strength) in THAT order.</summary>
        private void ApplyShake(ShakeSpec shake) => _camCtrl?.SetShake(shake.DurationSec, shake.Strength);

        /// <summary>Spawn a flash from a resolved <see cref="FlashSpec"/> (override or default look).</summary>
        private void SpawnFlashFromSpec(Vector3 pos, FlashSpec spec)
            => SpawnFlash(pos, spec.Scale, spec.DurationSec, MaterialFor(spec));

        /// <summary>Claims the next free flash slot and starts the effect.</summary>
        private void SpawnFlash(Vector3 pos, float baseScale, float duration, StandardMaterial3D mat)
        {
            // Story 2.7 review: floor a non-positive authored duration_sec. Otherwise the flash spawns Visible=true with
            // timer<=0, the shrink loop skips it (its guard is timer<=0), and it stays stuck on screen until the slot is
            // reused. A small floor guarantees every flash animates out and frees its slot. Cosmetic-only.
            if (duration <= 0f) duration = 0.05f;

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

        /// <summary>Resolve (and cache by reference) the Unshaded emissive material for a flash look.</summary>
        private StandardMaterial3D MaterialFor(FlashSpec spec)
        {
            if (_matCache.TryGetValue(spec, out StandardMaterial3D? cached)) return cached;
            StandardMaterial3D mat = MakeMat(ColorOf(spec.ColorRgb), spec.EmissionMult);
            _matCache[spec] = mat;
            return mat;
        }

        /// <summary>Convert a presentation-domain float[] colour to a Godot Color (defaults to white if unspecified).</summary>
        private static Color ColorOf(float[]? rgb)
        {
            float r = rgb != null && rgb.Length > 0 ? rgb[0] : 1f;
            float g = rgb != null && rgb.Length > 1 ? rgb[1] : 1f;
            float b = rgb != null && rgb.Length > 2 ? rgb[2] : 1f;
            return new Color(r, g, b);
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
