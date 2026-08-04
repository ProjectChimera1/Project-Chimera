#nullable enable
using System.Text.Json;
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
        // ── Schema versioning ─────────────────────────────────────────────────

        /// <summary>Story 8.1: the settings schema version, stamped by <see cref="MigrateForward"/> on load. The
        /// current version this build writes. Bump when a forward-incompatible field shape is added so a future
        /// <see cref="MigrateForward"/> can normalize older files. Story 9.7: bumped 1→2 for the multiplayer
        /// endpoint fields (server/Nakama host/port/key), whose null-string values <see cref="MigrateForward"/>
        /// normalizes to "". Story 11.7: bumped 2→3 for the six video fields (resolution/window mode/vsync/quality
        /// preset/UI scale), whose enum strings <see cref="MigrateForward"/> normalizes to their default on an unknown
        /// value and whose <c>ui_scale</c> it clamps to [0.75, 1.5].</summary>
        public const int CurrentSchemaVersion = 3;

        /// <summary>Story 8.1: the persisted schema version. An older <c>settings.json</c> that predates this field
        /// deserializes to <c>0</c>; <see cref="MigrateForward"/> stamps <see cref="CurrentSchemaVersion"/> so a
        /// subsequent <c>Save</c> persists the version. DW-482: a HIGHER persisted version (file written by a newer
        /// build) is preserved verbatim — <see cref="MigrateForward"/> bails rather than downgrade-stamping. Never
        /// governs alone — always run through <see cref="MigrateForward"/> on load.</summary>
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; } = 0;

        // ── AI provider (Story 8.1) ───────────────────────────────────────────

        /// <summary>Story 8.1: the selected LLM provider id (curated in <see cref="LlmProviderCatalog"/> —
        /// <c>anthropic</c>/<c>ollama</c>/<c>openrouter</c>). Defaults to the curated default provider; an unknown
        /// value is reset to the default by <see cref="MigrateForward"/>. The API KEY is NEVER stored here — it lives
        /// only in the <see cref="ISecretStore"/>.</summary>
        [JsonPropertyName("llm_provider")]
        public string LlmProvider { get; set; } = LlmProviderCatalog.DefaultProviderId;

        /// <summary>Story 8.1: the selected model — either a curated pick from the provider's catalog list OR a
        /// free-text override; both persist and round-trip verbatim. An empty value is reset to
        /// <see cref="LlmProviderCatalog.DefaultModel"/> (<c>claude-sonnet-5</c>) by <see cref="MigrateForward"/>.
        /// No separate override field is needed — one persisted model field holds either kind.</summary>
        [JsonPropertyName("llm_model")]
        public string LlmModel { get; set; } = LlmProviderCatalog.DefaultModel;

        /// <summary>Story 8.1: an optional base-URL override for the provider endpoint. Empty means "use the
        /// provider's catalog default base URL" (resolved by Story 8.2's provider stack); a non-empty value persists
        /// and round-trips verbatim.</summary>
        [JsonPropertyName("llm_base_url")]
        public string LlmBaseUrl { get; set; } = "";

        // ── Camera ────────────────────────────────────────────────────────────

        /// <summary>Camera pan speed multiplier. 1.0 = default; 0.5 = half; 2.0 = double.</summary>
        [JsonPropertyName("camera_speed")]
        public float CameraSpeed { get; set; } = 1.0f;

        /// <summary>Camera zoom sensitivity (scroll wheel).</summary>
        [JsonPropertyName("camera_zoom_speed")]
        public float CameraZoomSpeed { get; set; } = 1.0f;

        /// <summary>Whether edge-of-screen scrolling is enabled at session start. Defaults OFF so a fresh start does
        /// not fling the camera to a corner when the cursor rests near a screen edge; toggle it on in-game with E or
        /// in Settings. This is the AUTHORITATIVE default — CameraPhase pushes it onto RtsCameraController at boot, so
        /// the property initializer on the controller never governs.</summary>
        [JsonPropertyName("edge_scroll_enabled")]
        public bool EdgeScrollEnabled { get; set; } = false;

        // ── Video / Display (Story 11.7) ───────────────────────────────────────

        /// <summary>Story 11.7: persisted window width, applied via <c>DisplayServer.WindowSetSize</c>. Primarily
        /// meaningful in windowed/borderless mode. Defaults to 1920 (16:9 1080p). Absent in an older file → default.</summary>
        [JsonPropertyName("resolution_width")]
        public int ResolutionWidth { get; set; } = 1920;

        /// <summary>Story 11.7: persisted window height (see <see cref="ResolutionWidth"/>). Defaults to 1080.</summary>
        [JsonPropertyName("resolution_height")]
        public int ResolutionHeight { get; set; } = 1080;

        /// <summary>Story 11.7: the window mode — one of <c>windowed</c> / <c>borderless</c> (windowed-fullscreen) /
        /// <c>fullscreen</c> (exclusive). Maps in <c>SettingsManager.ApplyVideo</c> to Godot's
        /// <c>WindowMode.Windowed</c> / <c>WindowMode.Fullscreen</c> / <c>WindowMode.ExclusiveFullscreen</c>. An unknown
        /// value is reset to the default (<c>windowed</c>) by <see cref="MigrateForward"/>.</summary>
        [JsonPropertyName("window_mode")]
        public string WindowMode { get; set; } = "windowed";

        /// <summary>Story 11.7: whether vertical sync is enabled. Applied via <c>DisplayServer.WindowSetVsyncMode</c>.
        /// Defaults on.</summary>
        [JsonPropertyName("vsync")]
        public bool Vsync { get; set; } = true;

        /// <summary>Story 11.7: the render quality preset — <c>low</c> / <c>medium</c> / <c>high</c>. Drives shadows,
        /// shadow-atlas size, and MSAA (see <c>SettingsManager.ApplyVideo</c> + <c>MainScene.ApplySettingsToSystems</c>).
        /// An unknown value is reset to the default (<c>medium</c>) by <see cref="MigrateForward"/>.</summary>
        [JsonPropertyName("quality_preset")]
        public string QualityPreset { get; set; } = "medium";

        /// <summary>Story 11.7: the UI content-scale (DPI) factor, applied via <c>GetWindow().ContentScaleFactor</c>.
        /// Clamped by <see cref="MigrateForward"/> to [0.75, 1.5]. Defaults to 1.0.</summary>
        [JsonPropertyName("ui_scale")]
        public float UiScale { get; set; } = 1.0f;

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

        // ── Creation Suite ─────────────────────────────────────────────────────

        /// <summary>Story 3.5 (AR-5): the res:// folder the Unit Card model Browse dialog last opened, so it
        /// reopens there next time. Empty = open at the default root. Absent in an older settings file → ""
        /// (no migration needed — the field defaults).</summary>
        [JsonPropertyName("last_used_asset_folder")]
        public string LastUsedAssetFolder { get; set; } = "";

        /// <summary>Story 3.9 (AR-5): the folder the offline hero-persistence rail (<c>LocalProfileSource</c>) saves and
        /// loads hero profiles from. Defaults to <c>user://hero_profiles</c>; the Godot layer globalizes it before
        /// handing it to the Godot-free <c>LocalProfileSource</c> (mirrors <see cref="LastUsedAssetFolder"/>). Absent in
        /// an older settings file → the default (no migration needed).</summary>
        [JsonPropertyName("hero_profile_folder")]
        public string HeroProfileFolder { get; set; } = "user://hero_profiles";

        // ── Multiplayer endpoint (Story 9.7, provider-config pattern from Story 8.1) ──

        /// <summary>Story 9.7: the dedicated ENet game-server host. Empty = fall back to the MainScene
        /// <c>GameServerIp</c> Inspector export (the pre-9.7 source). Read at lobby composition, preferring a
        /// non-empty configured value.</summary>
        [JsonPropertyName("game_server_ip")]
        public string GameServerIp { get; set; } = "";

        /// <summary>Story 9.7: the dedicated ENet game-server port. 0 = fall back to the MainScene
        /// <c>GameServerPort</c> Inspector export.</summary>
        [JsonPropertyName("game_server_port")]
        public int GameServerPort { get; set; } = 0;

        /// <summary>Story 9.7: the Nakama matchmaking host. Empty = fall back to the MainScene <c>NakamaHost</c> export.</summary>
        [JsonPropertyName("nakama_host")]
        public string NakamaHost { get; set; } = "";

        /// <summary>Story 9.7: the Nakama port. 0 = fall back to the MainScene <c>NakamaPort</c> export.</summary>
        [JsonPropertyName("nakama_port")]
        public int NakamaPort { get; set; } = 0;

        /// <summary>Story 9.7: the Nakama server key. Empty = fall back to the MainScene <c>NakamaKey</c> export.</summary>
        [JsonPropertyName("nakama_key")]
        public string NakamaKey { get; set; } = "";

        // ── Onboarding ────────────────────────────────────────────────────────

        /// <summary>Story 5.9 (NFR-2): whether the first-time "Your First Scenario" guided onboarding overlay has
        /// already been shown/dismissed. Defaults false so a fresh (or old, pre-5.9) save file always auto-offers
        /// onboarding once; the Settings panel's "Replay onboarding" action resets this back to false on demand.</summary>
        [JsonPropertyName("has_seen_onboarding")]
        public bool HasSeenOnboarding { get; set; } = false;

        // ── Forward migration (Story 8.1) ─────────────────────────────────────

        /// <summary>
        /// Story 8.1: normalize this instance forward and stamp the current schema version. Because absent JSON
        /// fields deserialize to the C# property initializers, an old file already lands on the new defaults; this
        /// additionally resets an UNKNOWN provider id to the catalog default and an EMPTY model to
        /// <see cref="LlmProviderCatalog.DefaultModel"/>, then sets <see cref="SchemaVersion"/> to
        /// <see cref="CurrentSchemaVersion"/> so a subsequent <c>Save</c> persists the version. Idempotent — call it
        /// in <c>SettingsManager.Load</c> after deserialize. Returns <c>this</c> for fluent use.
        ///
        /// DW-482: a file whose persisted version is AHEAD of this build (written by a newer build, then the game was
        /// downgraded) is returned untouched — no normalization, no stamp. Every rule below encodes CURRENT-build
        /// semantics; applying them to a future file would downgrade it in place (clamp/reset values that are legal
        /// under the newer schema, then stamp the older version so the next <c>Save</c> persists the downgrade as if
        /// it were canonical). Bailing keeps the file's version stamp and known-field values intact across a Save
        /// round-trip, so the newer build re-normalizes under its OWN rules on the next upgrade instead of trusting a
        /// downgraded stamp. In-session the presentation layer tolerates out-of-band values (<c>ApplyVideo</c> maps
        /// unknown enum strings via switch defaults and caps resolution to the screen). Residual: fields this build's
        /// DTO does not declare are still dropped by deserialize→Save (no <c>[JsonExtensionData]</c> round-trip) —
        /// only the version stamp and the fields both builds share survive a downgrade Save.
        /// </summary>
        public SettingsData MigrateForward()
        {
            // DW-482: never drag a newer build's file backward — bail before touching anything (see summary).
            if (SchemaVersion > CurrentSchemaVersion)
                return this;

            // Unknown provider id → curated default (a free-text/legacy value that no longer maps to a provider).
            if (!LlmProviderCatalog.TryGet(LlmProvider, out _))
                LlmProvider = LlmProviderCatalog.DefaultProviderId;

            // Empty model → default. A non-empty model (curated OR free-text override) is preserved verbatim.
            if (string.IsNullOrWhiteSpace(LlmModel))
                LlmModel = LlmProviderCatalog.DefaultModel;

            // Explicit JSON null (`"llm_base_url": null`) deserializes the property to null; normalize it to "" so
            // "no override" is a single representation and Story 8.2's base-URL resolution never sees a null.
            LlmBaseUrl ??= "";

            // Story 9.7: normalize the multiplayer endpoint strings the same way (explicit JSON null → "" so the
            // "fall back to the export" test is a single is-empty check).
            GameServerIp ??= "";
            NakamaHost   ??= "";
            NakamaKey    ??= "";

            // Story 11.7: reset an unknown window-mode / quality-preset enum string to its default (a legacy/typo'd
            // value that no longer maps), and clamp the UI scale into the supported band.
            if (WindowMode is not ("windowed" or "borderless" or "fullscreen"))
                WindowMode = "windowed";
            if (QualityPreset is not ("low" or "medium" or "high"))
                QualityPreset = "medium";
            // Guard NaN/Inf before clamping — Math.Clamp passes NaN straight through, which would become a NaN
            // ContentScaleFactor and blank the UI; a non-finite value falls back to the 1.0 default.
            UiScale = float.IsFinite(UiScale) ? System.Math.Clamp(UiScale, 0.75f, 1.5f) : 1.0f;

            // Story 11.7 review-2: guard a corrupt/hand-edited resolution. ApplyVideo caps to the screen with
            // Mathf.Min but has no floor, so a sub-pixel width (e.g. resolution_width:1) would restore a 1×1 window
            // on boot — where safe-revert never arms — locking the player out. Reset BOTH to the 1080p default if
            // either falls outside a sane [640×480, 16384×16384] band (keeps the aspect coherent).
            if (ResolutionWidth < 640 || ResolutionHeight < 480 ||
                ResolutionWidth > 16384 || ResolutionHeight > 16384)
            {
                ResolutionWidth  = 1920;
                ResolutionHeight = 1080;
            }

            SchemaVersion = CurrentSchemaVersion;
            return this;
        }

        /// <summary>
        /// Story 8.1: the Godot-free deserialize-then-migrate seam that <c>SettingsManager.Load</c> calls for a
        /// present <c>settings.json</c> — deserialize with the given options, fall back to a fresh instance on a null
        /// result, then <see cref="MigrateForward"/>. Extracted from the Node so the load-time normalization contract
        /// (unknown provider / empty model / null base-url / schema stamp all applied on load) is Tier-1 testable
        /// without the Godot <c>SettingsManager</c> — the Node's <c>Load</c> is a thin wrapper over this plus its
        /// try/catch fail-soft. Never returns null, but DOES throw <see cref="JsonException"/> on malformed JSON —
        /// deliberately not swallowed here so the Godot caller can log it (<c>SettingsManager.Load</c> wraps this call
        /// in try/catch → <c>GD.PrintErr</c> + defaults). Any other caller that needs fail-soft must catch likewise.
        /// </summary>
        public static SettingsData FromJson(string json, JsonSerializerOptions options)
            => (JsonSerializer.Deserialize<SettingsData>(json, options) ?? new SettingsData()).MigrateForward();
    }
}
