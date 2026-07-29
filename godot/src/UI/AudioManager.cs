#nullable enable
using System.Collections.Generic;
using Godot;
using ProjectChimera.Combat;
using ProjectChimera.Core.Definitions; // CombatFeedbackProfile (presentation-only override carried on the event — Story 2.7)

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
        // Story 11.4 (FR-74) — match-feedback cues. All optional (graceful-silent until assets ship — the story's
        // scope is the hook + a default clip, not voice-set production).
        private const string SND_UNDER_ATTACK  = SFX_ROOT + "under_attack.ogg";
        private const string SND_DENIED        = SFX_ROOT + "order_denied.ogg";
        private const string SND_ORDER_ACK     = SFX_ROOT + "order_ack.ogg";
        private const string SND_RESEARCH_DONE = SFX_ROOT + "research_complete.ogg";
        private const string SND_PING          = SFX_ROOT + "minimap_ping.ogg";

        // ── State ─────────────────────────────────────────────────────────────

        private CombatEventQueue? _events;

        /// <summary>Story 2.7: cache of override streams keyed by sound id (caches null too, so a missing asset is
        /// probed once and is graceful-silent thereafter). Presentation-only.</summary>
        private readonly Dictionary<string, AudioStream?> _overrideCache = new();

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
        // Story 11.4 (FR-74) match-feedback cues.
        private AudioStream? _sndUnderAttack;
        private AudioStream? _sndDenied;
        private AudioStream? _sndOrderAck;
        private AudioStream? _sndResearchDone;
        private AudioStream? _sndPing;

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
            _sndUnderAttack  = TryLoad(SND_UNDER_ATTACK);
            _sndDenied       = TryLoad(SND_DENIED);
            _sndOrderAck     = TryLoad(SND_ORDER_ACK);
            _sndResearchDone = TryLoad(SND_RESEARCH_DONE);
            _sndPing         = TryLoad(SND_PING);

            int loaded = CountLoaded();
            GD.Print($"[AudioManager] Ready — {loaded}/{12} SFX streams loaded from {SFX_ROOT}");
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
                CombatFeedbackProfile? fb = evt.Feedback;

                // Story 2.7 (SD-2): profile-first. An override's sound id plays for ANY event type — including the new
                // AbilityCast (which has NO default clip, so the override's ImpactSoundId is the ONLY sound a cast makes).
                if (fb != null)
                {
                    string? id = evt.Type == CombatEventType.UnitKilled ? fb.DeathSoundId : fb.ImpactSoundId;
                    // Story 2.7 review: treat "" like null — an empty id falls back to the default clip below instead of
                    // forcing silence AND suppressing the default (null ⇒ default is the documented contract).
                    if (!string.IsNullOrEmpty(id))
                    {
                        bool pitch = evt.Type == CombatEventType.MeleeHit || evt.Type == CombatEventType.RangedHit;
                        PlayOneShot(ResolveOverrideStream(id), VolumeFor(evt.Type), pitch); // graceful-silent if absent
                        continue;
                    }
                }

                // Fallback: today's per-event legacy clips. AbilityCast is intentionally absent → silence unless the
                // ability authored an ImpactSoundId above (mirrors CombatFeedbackBridge's no-default-cast-flash).
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

        // ── Story 11.4 (FR-74) match-feedback cues (all graceful-silent until assets ship) ────────────────

        /// <summary>Play the under-attack alert cue (a local unit/building took a hit off-screen).</summary>
        public void PlayUnderAttack() => PlayOneShot(_sndUnderAttack, 1.0f, false);

        /// <summary>Play the order-denied cue (a local Train/Build/Research/Cast/Shop order was rejected).</summary>
        public void PlayDenied() => PlayOneShot(_sndDenied, 0.9f, false);

        /// <summary>Play a research-complete cue.</summary>
        public void PlayResearchComplete() => PlayOneShot(_sndResearchDone, 1.0f, false);

        /// <summary>Play the minimap-ping cue.</summary>
        public void PlayPing() => PlayOneShot(_sndPing, 0.9f, false);

        /// <summary>Play the order-acknowledgment cue at ISSUE time. Resolves the selected unit's per-unit
        /// <paramref name="ackSoundId"/> (from its <c>CombatFeedbackProfile.AckSoundId</c>) if authored, else the
        /// default ack clip. Both graceful-silent when the asset is absent (the story ships the hook + one default).</summary>
        public void PlayOrderAck(string? ackSoundId)
        {
            if (!string.IsNullOrEmpty(ackSoundId))
            {
                PlayOneShot(ResolveOverrideStream(ackSoundId), 0.8f, false); // graceful-silent if the override asset is absent
                return;
            }
            PlayOneShot(_sndOrderAck, 0.8f, false);
        }

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
        /// Resolve (and cache) an override sound stream by its profile sound id. A bare id resolves under
        /// <see cref="SFX_ROOT"/>; a full <c>res://</c> path is used verbatim. Null (missing asset) is cached too,
        /// so the probe happens once and playback is graceful-silent thereafter.
        /// </summary>
        private AudioStream? ResolveOverrideStream(string id)
        {
            if (_overrideCache.TryGetValue(id, out AudioStream? cached)) return cached;
            string path = id.StartsWith("res://") ? id : SFX_ROOT + id;
            AudioStream? stream = TryLoad(path);
            _overrideCache[id] = stream;
            return stream;
        }

        /// <summary>Playback volume for an override sound — matches the per-event-type default so an override melee is
        /// mixed like a melee. AbilityCast (and any future type) plays at unity.</summary>
        private static float VolumeFor(CombatEventType type) => type switch
        {
            CombatEventType.MeleeHit   => 0.9f,
            CombatEventType.RangedHit  => 0.8f,
            CombatEventType.SplashHit  => 1.0f,
            CombatEventType.UnitKilled => 0.85f,
            _                          => 1.0f,
        };

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
            if (_sndUnderAttack  != null) n++;
            if (_sndDenied       != null) n++;
            if (_sndOrderAck     != null) n++;
            if (_sndResearchDone != null) n++;
            if (_sndPing         != null) n++;
            return n;
        }
    }
}
