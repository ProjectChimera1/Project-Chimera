#nullable enable
using Godot;
using ProjectChimera.Core;
using ProjectChimera.Multiplayer;
using System;
using System.Collections.Generic;
using System.IO;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 9.11 — the main-menu / Edit-mode replay browser: lists the <c>.chmr</c> files in
    /// <c>user://replays/</c> with their metadata (map, players/factions, date, duration, result), and offers
    /// Play / Rename / Delete per row. Metadata is read via the lightweight <see cref="ReplayHeader.Read"/> (header
    /// + trailer only) so a legacy/corrupt file lists as "unplayable (old format)" rather than crashing the list.
    /// Mirrors the <see cref="ContentBrowserPanel"/> structure (CanvasLayer overlay, card rows). Hotkey N (wired in
    /// MainScene) toggles it in Edit mode.
    /// </summary>
    public partial class ReplayBrowserPanel : CanvasLayer
    {
        /// <summary>Fired when the user clicks Play — the argument is the absolute OS path to the .chmr file.</summary>
        public event Action<string>? OnPlay;

        private string        _replayDir = "";
        private VBoxContainer _listContainer = null!;
        private Label         _emptyLabel    = null!;
        private Label         _dirLabel      = null!;

        // Faction badge palette (indexed by (int)Faction - 1 = player slot 0..7).
        private static readonly Color[] FACTION_COLORS =
        {
            new Color(0.25f, 0.55f, 1.0f),  // P1
            new Color(1.0f,  0.25f, 0.25f), // P2
            new Color(0.30f, 0.85f, 0.40f), // P3
            new Color(0.95f, 0.80f, 0.25f), // P4
            new Color(0.75f, 0.35f, 0.85f), // P5
            new Color(0.30f, 0.85f, 0.85f), // P6
            new Color(0.95f, 0.55f, 0.20f), // P7
            new Color(0.75f, 0.75f, 0.80f), // P8
        };

        public void Initialize(string replayDirectory = "user://replays/")
        {
            Layer   = 10; // above HUD + chat (mirrors ContentBrowserPanel)
            Visible = false;
            _replayDir = ProjectSettings.GlobalizePath(replayDirectory);

            var root = new PanelContainer();
            root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            root.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.07f, 0.07f, 0.11f, 0.95f) });
            AddChild(root);

            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left",   60);
            margin.AddThemeConstantOverride("margin_right",  60);
            margin.AddThemeConstantOverride("margin_top",    40);
            margin.AddThemeConstantOverride("margin_bottom", 40);
            root.AddChild(margin);

            var outer = new VBoxContainer();
            outer.AddThemeConstantOverride("separation", 10);
            margin.AddChild(outer);

            // Header row.
            var headerRow = new HBoxContainer();
            outer.AddChild(headerRow);
            var title = new Label { Text = "Replays", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            title.AddThemeFontSizeOverride("font_size", 28);
            title.AddThemeColorOverride("font_color", Colors.White);
            headerRow.AddChild(title);
            var refreshBtn = new Button { Text = "Refresh", CustomMinimumSize = new Vector2(100, 36) };
            refreshBtn.Pressed += Refresh;
            headerRow.AddChild(refreshBtn);
            var closeBtn = new Button { Text = "Close  [N]", CustomMinimumSize = new Vector2(110, 36) };
            closeBtn.Pressed += () => Visible = false;
            headerRow.AddChild(closeBtn);

            _dirLabel = new Label();
            _dirLabel.AddThemeFontSizeOverride("font_size", 12);
            _dirLabel.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.5f));
            _dirLabel.AutowrapMode = TextServer.AutowrapMode.Word;
            outer.AddChild(_dirLabel);

            outer.AddChild(new HSeparator());

            var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            outer.AddChild(scroll);

            _listContainer = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _listContainer.AddThemeConstantOverride("separation", 8);
            scroll.AddChild(_listContainer);

            _emptyLabel = new Label
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Visible             = false,
                AutowrapMode        = TextServer.AutowrapMode.Word,
            };
            _emptyLabel.AddThemeFontSizeOverride("font_size", 16);
            _emptyLabel.AddThemeColorOverride("font_color", new Color(0.55f, 0.55f, 0.55f));
            _listContainer.AddChild(_emptyLabel);
        }

        /// <summary>Toggle visibility; refresh the list when opening.</summary>
        public void ToggleVisible()
        {
            Visible = !Visible;
            if (Visible) Refresh();
        }

        private void Refresh()
        {
            _dirLabel.Text = $"Replays folder: {_replayDir}";

            foreach (Node child in _listContainer.GetChildren())
            {
                if (child == _emptyLabel) continue;
                _listContainer.RemoveChild(child);
                child.QueueFree();
            }

            string[] files;
            try
            {
                files = Directory.Exists(_replayDir)
                    ? Directory.GetFiles(_replayDir, "*.chmr")
                    : Array.Empty<string>();
            }
            catch { files = Array.Empty<string>(); }

            // Newest first (by last-write time).
            Array.Sort(files, (a, b) =>
            {
                try { return File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)); }
                catch { return 0; }
            });

            if (files.Length == 0)
            {
                _emptyLabel.Text    = $"No replays yet.\n\nPlay a match to record one into:\n{_replayDir}";
                _emptyLabel.Visible = true;
                return;
            }

            _emptyLabel.Visible = false;
            foreach (string path in files)
                _listContainer.AddChild(BuildRow(path));
        }

        private Control BuildRow(string path)
        {
            ReplayHeader hdr = ReplayHeader.Read(path);

            var card = MakeCardPanel();
            var row  = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            card.AddChild(row);

            // Info column.
            var info = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            info.AddThemeConstantOverride("separation", 4);
            row.AddChild(info);

            var name = new Label { Text = Path.GetFileNameWithoutExtension(path) };
            name.AddThemeFontSizeOverride("font_size", 18);
            name.AddThemeColorOverride("font_color", Colors.White);
            info.AddChild(name);

            string date = "";
            try { date = File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm"); } catch { }

            if (!hdr.IsPlayable)
            {
                var bad = new Label { Text = $"unplayable (old format)   •   {date}", AutowrapMode = TextServer.AutowrapMode.Word };
                bad.AddThemeFontSizeOverride("font_size", 12);
                bad.AddThemeColorOverride("font_color", new Color(0.9f, 0.5f, 0.4f));
                info.AddChild(bad);
            }
            else
            {
                string map = string.IsNullOrEmpty(hdr.ScenarioPath) ? "(unknown map)" : Path.GetFileName(hdr.ScenarioPath);
                var meta = new Label
                {
                    Text = $"{map}   •   {hdr.FactionCount}p   •   {date}   •   " +
                           $"{ReplayFormat.Duration(hdr.FinalTick)}   •   {ReplayFormat.ResultText(hdr.WinnerFaction, hdr.Completed)}",
                    AutowrapMode = TextServer.AutowrapMode.Word,
                };
                meta.AddThemeFontSizeOverride("font_size", 12);
                meta.AddThemeColorOverride("font_color", new Color(0.65f, 0.65f, 0.7f));
                info.AddChild(meta);

                // Roster glyph row (colored per-faction badges).
                if (hdr.Roster != null && hdr.Roster.Length > 0)
                    info.AddChild(BuildRosterRow(hdr.Roster));
            }

            // Actions column.
            var actions = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, CustomMinimumSize = new Vector2(120, 0) };
            actions.AddThemeConstantOverride("separation", 4);
            row.AddChild(actions);

            var playBtn = new Button { Text = "Play", CustomMinimumSize = new Vector2(110, 34), Disabled = !hdr.IsPlayable };
            string capturedPath = path;
            playBtn.Pressed += () => { Visible = false; OnPlay?.Invoke(capturedPath); };
            actions.AddChild(playBtn);

            var renameBtn = new Button { Text = "Rename", CustomMinimumSize = new Vector2(110, 28) };
            renameBtn.Pressed += () => PromptRename(capturedPath);
            actions.AddChild(renameBtn);

            var deleteBtn = new Button { Text = "Delete", CustomMinimumSize = new Vector2(110, 28) };
            deleteBtn.Pressed += () => PromptDelete(capturedPath);
            actions.AddChild(deleteBtn);

            return card;
        }

        private static Control BuildRosterRow(Faction[] roster)
        {
            var rrow = new HBoxContainer();
            rrow.AddThemeConstantOverride("separation", 4);
            foreach (Faction f in roster)
            {
                int slot = (int)f - 1;
                Color c = slot >= 0 && slot < FACTION_COLORS.Length ? FACTION_COLORS[slot] : new Color(0.6f, 0.6f, 0.6f);
                var badge = new PanelContainer();
                badge.AddThemeStyleboxOverride("panel", new StyleBoxFlat
                {
                    BgColor                = c,
                    CornerRadiusTopLeft    = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
                    ContentMarginLeft      = 6, ContentMarginRight = 6, ContentMarginTop = 2, ContentMarginBottom = 2,
                });
                var lbl = new Label { Text = slot >= 0 ? $"P{slot + 1}" : f.ToString() };
                lbl.AddThemeFontSizeOverride("font_size", 11);
                lbl.AddThemeColorOverride("font_color", Colors.White);
                badge.AddChild(lbl);
                rrow.AddChild(badge);
            }
            return rrow;
        }

        private void PromptRename(string path)
        {
            var dlg = new AcceptDialog { Title = "Rename Replay", DialogHideOnOk = true };
            var vb  = new VBoxContainer();
            vb.AddChild(new Label { Text = "New name:" });
            var edit = new LineEdit { Text = Path.GetFileNameWithoutExtension(path), CustomMinimumSize = new Vector2(320, 0) };
            vb.AddChild(edit);
            dlg.AddChild(vb);
            dlg.AddCancelButton("Cancel");
            dlg.Confirmed += () =>
            {
                string? error = DoRename(path, edit.Text);
                dlg.QueueFree();
                if (error != null) ShowMessage("Rename failed", error);
                Refresh();
            };
            dlg.Canceled += () => dlg.QueueFree();
            AddChild(dlg);
            dlg.PopupCentered(new Vector2I(380, 150));
        }

        /// <summary>Rename a .chmr on disk. NEVER clobbers a different existing replay (the "a replay is never
        /// silently discarded" invariant): returns a human-readable error string on refusal, or null on success.</summary>
        private static string? DoRename(string path, string newBaseName)
        {
            newBaseName = (newBaseName ?? "").Trim();
            if (newBaseName.Length == 0) return "Name cannot be empty.";
            foreach (char c in Path.GetInvalidFileNameChars())
                newBaseName = newBaseName.Replace(c, '_');

            string dir  = Path.GetDirectoryName(path) ?? "";
            string dest = Path.Combine(dir, newBaseName + ".chmr");
            if (string.Equals(dest, path, StringComparison.OrdinalIgnoreCase)) return null; // unchanged — no-op
            if (File.Exists(dest)) return "A replay with that name already exists.";
            try
            {
                File.Move(path, dest, overwrite: false); // fail-closed: never overwrite another replay
                return null;
            }
            catch (Exception e) { return e.Message; }
        }

        private void ShowMessage(string title, string text)
        {
            var dlg = new AcceptDialog { Title = title, DialogText = text };
            dlg.Confirmed += () => dlg.QueueFree();
            dlg.Canceled  += () => dlg.QueueFree();
            AddChild(dlg);
            dlg.PopupCentered(new Vector2I(420, 140));
        }

        private void PromptDelete(string path)
        {
            var dlg = new ConfirmationDialog
            {
                Title      = "Delete Replay",
                DialogText = $"Delete '{Path.GetFileName(path)}'? This cannot be undone.",
            };
            dlg.Confirmed += () =>
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception e) { GD.PrintErr($"[ReplayBrowser] Delete failed: {e.Message}"); }
                dlg.QueueFree();
                Refresh();
            };
            dlg.Canceled += () => dlg.QueueFree();
            AddChild(dlg);
            dlg.PopupCentered(new Vector2I(420, 140));
        }

        private static PanelContainer MakeCardPanel()
        {
            var card = new PanelContainer();
            card.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor                = new Color(0.13f, 0.14f, 0.20f, 1f),
                BorderColor            = new Color(0.30f, 0.35f, 0.50f, 0.7f),
                BorderWidthLeft        = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
                CornerRadiusTopLeft    = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
                ContentMarginLeft      = 14, ContentMarginRight = 14, ContentMarginTop = 10, ContentMarginBottom = 10,
            });
            return card;
        }
    }
}
