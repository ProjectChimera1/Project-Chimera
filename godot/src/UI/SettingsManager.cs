#nullable enable
using Godot;
using ProjectChimera.Core.Definitions;
using System;
using System.IO;
using System.Text.Json;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Loads, saves, and applies player settings.
    ///
    /// Singleton attached to MainScene. Other systems read settings via
    /// <see cref="SettingsManager.Current"/> and register callbacks via
    /// <see cref="OnSettingsChanged"/>.
    ///
    /// Call <see cref="Apply"/> after changing any field on <see cref="Current"/>
    /// to propagate the values to audio buses, camera, etc.
    /// </summary>
    public partial class SettingsManager : Node
    {
        // ── Singleton ─────────────────────────────────────────────────────────

        public static SettingsManager? Instance { get; private set; }

        // ── Events ────────────────────────────────────────────────────────────

        /// <summary>Fired whenever settings are applied. Consumers update themselves accordingly.</summary>
        public event Action<SettingsData>? OnSettingsChanged;

        // ── State ─────────────────────────────────────────────────────────────

        public SettingsData Current { get; set; } = new();

        private static readonly string SETTINGS_PATH =
            ProjectSettings.GlobalizePath("user://settings.json");

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented        = true,
            ReadCommentHandling  = JsonCommentHandling.Skip,
            AllowTrailingCommas  = true,
        };

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public override void _Ready()
        {
            Instance = this;
            Load();
            Apply();
        }

        // ── Load / Save ───────────────────────────────────────────────────────

        /// <summary>
        /// Load settings from user://settings.json.
        /// Falls back to defaults silently if the file is absent or corrupt.
        /// </summary>
        public void Load()
        {
            if (!File.Exists(SETTINGS_PATH))
            {
                Current = new SettingsData();
                return;
            }

            try
            {
                string json = File.ReadAllText(SETTINGS_PATH);
                Current = JsonSerializer.Deserialize<SettingsData>(json, _jsonOpts)
                          ?? new SettingsData();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[Settings] Failed to load settings: {ex.Message}. Using defaults.");
                Current = new SettingsData();
            }
        }

        /// <summary>
        /// Persist current settings to disk.
        /// </summary>
        public void Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(Current, _jsonOpts);
                File.WriteAllText(SETTINGS_PATH, json);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[Settings] Failed to save settings: {ex.Message}");
            }
        }

        // ── Apply ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Push current settings values to Godot subsystems (audio buses, etc.)
        /// and fire <see cref="OnSettingsChanged"/> so nodes can update themselves.
        /// Call after modifying any field on <see cref="Current"/>.
        /// </summary>
        public void Apply()
        {
            ApplyAudio();
            OnSettingsChanged?.Invoke(Current);
        }

        private void ApplyAudio()
        {
            SetBusVolume("Master", Current.MasterVolume);
            SetBusVolume("SFX",    Current.SfxVolume);
            SetBusVolume("Music",  Current.MusicVolume);
        }

        private static void SetBusVolume(string busName, float linear)
        {
            int idx = AudioServer.GetBusIndex(busName);
            if (idx < 0) return; // bus doesn't exist yet — silently skip

            // Clamp and convert to dB. Linear 0 → -inf dB (mute); 1 → 0 dB.
            float clamped = Mathf.Clamp(linear, 0f, 1f);
            float db      = clamped > 0f ? Mathf.LinearToDb(clamped) : -80f;
            AudioServer.SetBusVolumeDb(idx, db);
            AudioServer.SetBusMute(idx, clamped == 0f);
        }
    }
}
