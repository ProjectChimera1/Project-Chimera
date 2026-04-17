#nullable enable
using Godot;
using ProjectChimera.Combat;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Presentation-layer audio manager. Drains <see cref="CombatEventQueue"/> each frame
    /// and plays sound effects through a pooled <see cref="AudioStreamPlayer"/> bank.
    ///
    /// All sound files are optional — the manager loads them from
    /// <c>res://resources/audio/sfx/</c> and falls back to silence gracefully when
    /// files are absent. This lets the audio framework be wired and exercised
    /// before any real assets exist.
    ///
    /// Bus routing: all players target the "SFX" bus, which SettingsManager already
    /// controls via <c>AudioServer.SetBusVolumeDb</c>.
    ///
    /// Usage:
    ///   • <see cref="Initialize(CombatEventQueue)"/> — wire the sim event queue.
    ///   • <see cref="PlayBuildingPlaced"/>          — call when a building is placed.
    ///   • <see cref="PlayTrainingComplete"/>        — call when a unit finishes training.
    ///   • <see cref="PlayUiClick"/>                 — call from UI button handlers.
    /// </summary>
    public partial class AudioManager : Node
    {
        // ── Pool configuration ────────────────────────────────────────────────

        private const int   POOL_SIZE  = 8;
        private const float PITCH_BASE = 1.0f;
        private const float PITCH_VAR  = 0.08f; // ±8% pitch randomisation for variety

        // ── Sound file paths (all optional) ──────────────────────────────────

        private const string SFX_ROOT          = "res://resources/audio/sfx/";
        private const string SND_MELEE_HIT     = SFX_ROOT + "melee_hit.ogg";
        private const string SND_RANGED_HIT    = SFX_ROOT + "ranged_hit.ogg";
        private const string SND_SPLASH_HIT    = SFX_ROOT + "explosion.ogg";
        private const string SND_UNIT_KILLED   = SFX_ROOT + "unit_killed.ogg";
        private const string SND_BLDG_PLACED   = SFX_ROOT + "building_placed.ogg";
        private const string SND_TRAIN_DONE    = SFX_ROOT + "training_complete.ogg";
        private const string SND_UI_CLICK      = SFX_ROOT + "ui_click.ogg";

        // ── State ─────────────────────────────────────────────────────────────

        private CombatEventQueue? _events;

        private AudioStreamPlayer[] _pool    = null!;
        private int                 _poolIdx = 0;

        /// <summary>Loaded streams — null when the file is absent.</summary>
        private AudioStream? _sndMeleeHit;
        private AudioStream? _sndRangedHit;
        private AudioStream? _sndSplashHit;
        private AudioStream? _sndUnitKilled;
        private AudioStream? _sndBldgPlaced;
        private AudioStream? _sndTrainDone;
        private AudioStream? _sndUiClick;

        // ── Initialisation ────────────────────────────────────────────────────

        public override void _Ready()
        {
            // Build player pool
            _pool = new AudioStreamPlayer[POOL_SIZE];
            for (int i = 0; i < POOL_SIZE; i++)
            {
                var player = new AudioStreamPlayer();
                player.Bus = "SFX";
                AddChild(player);
                _pool[i] = player;
            }

            // Load streams — TryLoad returns null (not an error) when absent
            _sndMeleeHit   = TryLoad(SND_MELEE_HIT);
            _sndRangedHit  = TryLoad(SND_RANGED_HIT);
            _sndSplashHit  = TryLoad(SND_SPLASH_HIT);
            _sndUnitKilled = TryLoad(SND_UNIT_KILLED);
            _sndBldgPlaced = TryLoad(SND_BLDG_PLACED);
            _sndTrainDone  = TryLoad(SND_TRAIN_DONE);
            _sndUiClick    = TryLoad(SND_UI_CLICK);

            int loaded = CountLoaded();
            GD.Print($"[AudioManager] Ready — {loaded}/{7} SFX streams loaded from {SFX_ROOT}");
        }

        /// <summary>
        /// Wire the simulation combat event queue.
        /// Call this after the sim systems are constructed (before the first Play tick).
        /// </summary>
        public void Initialize(CombatEventQueue events)
        {
            _events = events;
        }

        // ── _Process ──────────────────────────────────────────────────────────

        public override void _Process(double delta)
        {
            if (_events == null) return;

            int count = _events.Count;
            for (int i = 0; i < count; i++)
            {
                var evt = _events.Get(i);
                switch (evt.Type)
                {
                    case CombatEventType.MeleeHit:   PlayOneShot(_sndMeleeHit,   0.9f, true);  break;
                    case CombatEventType.RangedHit:  PlayOneShot(_sndRangedHit,  0.8f, true);  break;
                    case CombatEventType.SplashHit:  PlayOneShot(_sndSplashHit,  1.0f, false); break;
                    case CombatEventType.UnitKilled: PlayOneShot(_sndUnitKilled, 0.85f, false); break;
                }
            }
            // NOTE: Do NOT call _events.Clear() here — CombatFeedbackBridge owns the clear.
        }

        // ── Public one-shot helpers ───────────────────────────────────────────

        /// <summary>Play a building-placed sound effect (presentation layer only).</summary>
        public void PlayBuildingPlaced()  => PlayOneShot(_sndBldgPlaced, 1.0f, false);

        /// <summary>Play a training-complete chime when a unit finishes production.</summary>
        public void PlayTrainingComplete() => PlayOneShot(_sndTrainDone, 1.0f, false);

        /// <summary>Play a soft UI click for button interactions.</summary>
        public void PlayUiClick() => PlayOneShot(_sndUiClick, 0.7f, false);

        // ── Pool ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Assigns <paramref name="stream"/> to the next round-robin pool slot and plays it.
        /// If <paramref name="stream"/> is null (file absent), this is a silent no-op.
        /// </summary>
        /// <param name="volumeDb">Linear volume scale in 0–1 (converted to dB internally).</param>
        /// <param name="pitchRandom">When true, applies ±PITCH_VAR randomisation for variety.</param>
        private void PlayOneShot(AudioStream? stream, float volumeLinear, bool pitchRandom)
        {
            if (stream == null) return;

            var player = _pool[_poolIdx];
            _poolIdx = (_poolIdx + 1) % POOL_SIZE;

            player.Stream     = stream;
            player.VolumeDb   = volumeLinear > 0f ? Mathf.LinearToDb(volumeLinear) : -80f;
            player.PitchScale = pitchRandom
                ? PITCH_BASE + (float)GD.RandRange(-PITCH_VAR, PITCH_VAR)
                : PITCH_BASE;

            player.Play();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Attempts to load an audio stream from <paramref name="path"/>.
        /// Returns null (no error) when the file doesn't exist yet.
        /// </summary>
        private static AudioStream? TryLoad(string path)
        {
            if (!ResourceLoader.Exists(path)) return null;

            try
            {
                return ResourceLoader.Load<AudioStream>(path);
            }
            catch
            {
                return null;
            }
        }

        private int CountLoaded()
        {
            int n = 0;
            if (_sndMeleeHit   != null) n++;
            if (_sndRangedHit  != null) n++;
            if (_sndSplashHit  != null) n++;
            if (_sndUnitKilled != null) n++;
            if (_sndBldgPlaced != null) n++;
            if (_sndTrainDone  != null) n++;
            if (_sndUiClick    != null) n++;
            return n;
        }
    }
}
