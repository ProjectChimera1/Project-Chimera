#nullable enable
using System.Text.Json.Serialization;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Persistent player settings, serialized to user://settings.json.
    ///
    /// All fields have safe defaults so the file can be absent on first run.
    /// Add new fields with defaults; existing save files will use those defaults.
    /// </summary>
    public class SettingsData
    {
        // ── Camera ────────────────────────────────────────────────────────────

        /// <summary>Camera pan speed multiplier. 1.0 = default; 0.5 = half; 2.0 = double.</summary>
        [JsonPropertyName("camera_speed")]
        public float CameraSpeed { get; set; } = 1.0f;

        /// <summary>Camera zoom sensitivity (scroll wheel).</summary>
        [JsonPropertyName("camera_zoom_speed")]
        public float CameraZoomSpeed { get; set; } = 1.0f;

        /// <summary>Whether edge-of-screen scrolling is enabled at session start.</summary>
        [JsonPropertyName("edge_scroll_enabled")]
        public bool EdgeScrollEnabled { get; set; } = true;

        // ── Audio ─────────────────────────────────────────────────────────────

        /// <summary>Master volume (0.0 – 1.0). Applied to the Master audio bus.</summary>
        [JsonPropertyName("master_volume")]
        public float MasterVolume { get; set; } = 1.0f;

        /// <summary>SFX bus volume (0.0 – 1.0).</summary>
        [JsonPropertyName("sfx_volume")]
        public float SfxVolume { get; set; } = 1.0f;

        /// <summary>Music bus volume (0.0 – 1.0).</summary>
        [JsonPropertyName("music_volume")]
        public float MusicVolume { get; set; } = 0.7f;

        // ── UI / HUD ──────────────────────────────────────────────────────────

        /// <summary>Whether the minimap is shown during gameplay.</summary>
        [JsonPropertyName("show_minimap")]
        public bool ShowMinimap { get; set; } = true;

        /// <summary>Whether to show the FPS counter in the HUD.</summary>
        [JsonPropertyName("show_fps")]
        public bool ShowFps { get; set; } = false;

        // ── Accessibility ─────────────────────────────────────────────────────

        /// <summary>
        /// Colorblind-friendly mode: replaces the Player 2 color from red to orange/purple
        /// so it reads clearly for red-green color-blind players.
        /// </summary>
        [JsonPropertyName("colorblind_mode")]
        public bool ColorblindMode { get; set; } = false;
    }
}
