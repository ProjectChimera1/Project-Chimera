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

        /// <summary>DW-134: the settings serializer shape now lives in the Godot-free
        /// <see cref="SettingsJson.Options"/> so the Tier-1 round-trip suites can reference the REAL options this
        /// Node loads/saves with instead of hand-rolling a replica that could silently drift from it. Byte-identical
        /// posture to the private options this replaced — an alias, not a change.</summary>
        private static readonly JsonSerializerOptions _jsonOpts = SettingsJson.Options;

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
            }
            else
            {
                try
                {
                    string json = File.ReadAllText(SETTINGS_PATH);
                    // Story 8.1: deserialize + forward-migrate through the Godot-free seam (Tier-1 tested), so an
                    // unknown provider / empty model / null base-url is normalized and the schema version stamped.
                    Current = SettingsData.FromJson(json, _jsonOpts);
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[Settings] Failed to load settings: {ex.Message}. Using defaults.");
                    Current = new SettingsData();
                }
            }

            // Forward-migrate again to cover the absent / corrupt-fallback paths (idempotent for the FromJson path
            // above). Guarantees Current is normalized regardless of which branch produced it.
            Current.MigrateForward();
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
            ApplyVideo();
            OnSettingsChanged?.Invoke(Current);
        }

        private void ApplyAudio()
        {
            SetBusVolume("Master", Current.MasterVolume);
            SetBusVolume("SFX",    Current.SfxVolume);
            SetBusVolume("Music",  Current.MusicVolume);
        }

        /// <summary>
        /// Story 11.7 (FR-66): push the global video/display state that is reachable without a live match scene —
        /// window mode, resolution, vsync, UI content-scale, MSAA, and the directional shadow-atlas size. Called from
        /// <see cref="Apply"/> (which <c>_Ready</c> runs after <see cref="Load"/>), so a persisted display mode is
        /// restored on relaunch before the first frame. The one scene-coupled knob — the directional light's
        /// <c>ShadowEnabled</c> per quality tier — rides the <see cref="OnSettingsChanged"/> bridge into
        /// <c>MainScene.ApplySettingsToSystems</c> instead (the light is absent in menus).
        ///
        /// Skipped on a headless/dedicated-server run (no real display server or 3D viewport to configure).
        /// </summary>
        private void ApplyVideo()
        {
            // No window/viewport to configure on a headless server (the dedicated-server boot path).
            if (DisplayServer.GetName() == "headless" || OS.HasFeature("dedicated_server"))
                return;

            var s = Current;

            // ── Window mode (Godot 4 map: borderless == windowed-fullscreen == WindowMode.Fullscreen) ──
            DisplayServer.WindowMode mode = s.WindowMode switch
            {
                "fullscreen" => DisplayServer.WindowMode.ExclusiveFullscreen,
                "borderless" => DisplayServer.WindowMode.Fullscreen,
                _            => DisplayServer.WindowMode.Windowed,
            };
            DisplayServer.WindowSetMode(mode);

            // ── DW-930 (DW-924 clue 3) readback tripwire ──
            // Godot 4.6.3/Windows engine bug: a window CREATED fullscreen (project.godot window/size/mode=3)
            // converts any later Windowed request into ExclusiveFullscreen — permanently; no in-process call
            // escapes it. The boot default is therefore Windowed (window/size/mode=0) and settings drive the
            // mode from there, where every transition works. This readback is the tripwire: if a future change
            // reintroduces a fullscreen boot (export preset, editor write-back), every log names the regression.
            DisplayServer.WindowMode reported = DisplayServer.WindowGetMode();
            if (reported != mode)
            {
                GD.PrintErr($"[Settings] window mode requested {mode} but engine reports {reported}");

                // One deferred retry — harmless, and can help platforms where the first set lands late.
                // Do NOT rely on it: on the 4.6.3 Windows fullscreen-boot bug no retry ever escapes.
                DisplayServer.WindowMode wanted = mode;
                Callable.From(() =>
                {
                    DisplayServer.WindowSetMode(wanted);
                    DisplayServer.WindowMode after = DisplayServer.WindowGetMode();
                    if (after != wanted)
                        GD.PrintErr(
                            $"[Settings] deferred window-mode retry failed: requested {wanted}, engine reports {after}");
                }).CallDeferred();
            }

            // ── Resolution — only meaningful in true Windowed mode. Borderless (WindowMode.Fullscreen) and exclusive
            //    fullscreen own their own sizing, so a WindowSetSize there fights the mode. Clamp to the current
            //    screen so a settings.json carrying a larger resolution than the display (e.g. 4K persisted, opened on
            //    a 1080p monitor) can never force an off-screen window on boot restore (safe-revert only arms on
            //    interactive changes, never on this restore path). ──
            if (mode == DisplayServer.WindowMode.Windowed &&
                s.ResolutionWidth > 0 && s.ResolutionHeight > 0)
            {
                Vector2I screen = DisplayServer.ScreenGetSize();
                int w = screen.X > 0 ? Mathf.Min(s.ResolutionWidth,  screen.X) : s.ResolutionWidth;
                int h = screen.Y > 0 ? Mathf.Min(s.ResolutionHeight, screen.Y) : s.ResolutionHeight;
                DisplayServer.WindowSetSize(new Vector2I(w, h));
            }

            // ── VSync ──
            DisplayServer.WindowSetVsyncMode(
                s.Vsync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);

            // ── UI content-scale (DPI lever) ──
            var win = GetWindow();
            if (win != null) win.ContentScaleFactor = s.UiScale;

            // ── Quality tier → MSAA + shadow-atlas (the ShadowEnabled toggle rides the MainScene bridge) ──
            var vp = GetViewport();
            if (vp != null)
                vp.Msaa3D = s.QualityPreset switch
                {
                    "high"   => Viewport.Msaa.Msaa4X,
                    "medium" => Viewport.Msaa.Msaa2X,
                    _        => Viewport.Msaa.Disabled,
                };

            int atlas = s.QualityPreset switch
            {
                "high"   => 8192,
                "medium" => 4096,
                _        => 2048, // low: shadows are disabled by the tier, so the atlas size is inert
            };
            RenderingServer.DirectionalShadowAtlasSetSize(atlas, true);
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
