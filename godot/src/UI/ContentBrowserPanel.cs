#nullable enable
using Godot;
using ProjectChimera.Core.Definitions;
using ProjectChimera.UGC;
using System;
using System.Collections.Generic;
using System.IO;

namespace ProjectChimera.UI
{
    /// <summary>
    /// In-game content browser — Edit-mode panel for discovering and loading maps.
    ///
    /// Two tabs:
    ///   Local  — scans user://packages/ for locally installed .chimera.zip files.
    ///   Online — browses and downloads maps from mod.io (requires ModIoGameId +
    ///            ModIoApiKey set in MainScene's Inspector exports).
    ///
    /// Usage:
    ///   var browser = new ContentBrowserPanel();
    ///   AddChild(browser);
    ///   browser.Initialize(scanDirectory: "user://packages/", modIo: _modIoService);
    ///   browser.OnLoadMap += HandleLoadMap;
    ///
    /// Key "O" (wired in MainScene) toggles the panel in Edit mode.
    /// </summary>
    public partial class ContentBrowserPanel : CanvasLayer
    {
        // ── Events ────────────────────────────────────────────────────────────

        /// <summary>
        /// Fired when the user clicks Load on a local package card.
        /// Argument is the absolute OS path to the .chimera.zip file.
        /// </summary>
        public event Action<string>? OnLoadMap;

        // ── Configuration ─────────────────────────────────────────────────────

        private string         _scanDir = "";
        private ModIoService?  _modIo;

        // ── Tab containers ────────────────────────────────────────────────────

        private Control _localTab  = null!;
        private Control _onlineTab = null!;
        private Button  _localTabBtn  = null!;
        private Button  _onlineTabBtn = null!;

        // ── Local tab widgets ─────────────────────────────────────────────────

        private VBoxContainer _listContainer = null!;
        private Label         _emptyLabel    = null!;
        private Label         _dirLabel      = null!;

        // ── Online tab widgets ────────────────────────────────────────────────

        private Label         _authStatusLabel   = null!;
        private Button        _loginToggleBtn    = null!;
        private Control       _loginPanel        = null!;
        private LineEdit      _emailField        = null!;
        private Button        _requestCodeBtn    = null!;
        private LineEdit      _codeField         = null!;
        private Button        _exchangeCodeBtn   = null!;
        private LineEdit      _searchField       = null!;
        private Label         _onlineStatusLabel = null!;
        private VBoxContainer _onlineListContainer = null!;

        // Download state: modId → (button label, progress 0-1)
        private readonly Dictionary<int, Label>  _downloadLabels   = new();
        private readonly Dictionary<int, float>  _downloadProgress = new();
        private readonly HashSet<int>            _downloadComplete = new();

        // Tag badge color palette (cycling).
        private static readonly Color[] TAG_COLORS =
        {
            new Color(0.25f, 0.55f, 1.0f, 0.85f),
            new Color(0.2f,  0.7f,  0.4f, 0.85f),
            new Color(0.85f, 0.5f,  0.1f, 0.85f),
            new Color(0.7f,  0.3f,  0.8f, 0.85f),
        };

        // ── Initialization ────────────────────────────────────────────────────

        /// <summary>
        /// Build the panel UI and start the mod.io service event loop.
        /// </summary>
        /// <param name="scanDirectory">Godot path (user:// or res://) to scan for local packages.</param>
        /// <param name="modIo">Optional mod.io service. When null, the Online tab is hidden.</param>
        public void Initialize(string scanDirectory, ModIoService? modIo = null)
        {
            Layer   = 10; // above HUD (8) and chat overlay (8)
            Visible = false;

            _scanDir = ProjectSettings.GlobalizePath(scanDirectory);
            _modIo   = modIo;

            WireModIoEvents();

            // ── Root panel (full-screen semi-transparent overlay) ─────────────
            var root = new PanelContainer();
            root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            root.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = new Color(0.07f, 0.07f, 0.11f, 0.95f),
            });
            AddChild(root);

            // ── Outer margin ──────────────────────────────────────────────────
            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left",   60);
            margin.AddThemeConstantOverride("margin_right",  60);
            margin.AddThemeConstantOverride("margin_top",    40);
            margin.AddThemeConstantOverride("margin_bottom", 40);
            root.AddChild(margin);

            var outerVbox = new VBoxContainer();
            outerVbox.AddThemeConstantOverride("separation", 10);
            margin.AddChild(outerVbox);

            // ── Header row ────────────────────────────────────────────────────
            var headerRow = new HBoxContainer();
            outerVbox.AddChild(headerRow);

            var title = new Label { Text = "Map Browser" };
            title.AddThemeFontSizeOverride("font_size", 28);
            title.AddThemeColorOverride("font_color", Colors.White);
            title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            headerRow.AddChild(title);

            var closeBtn = new Button { Text = "Close  [O]", CustomMinimumSize = new Vector2(110, 36) };
            closeBtn.AddThemeFontSizeOverride("font_size", 14);
            closeBtn.Pressed += () => Visible = false;
            headerRow.AddChild(closeBtn);

            // ── Tab row ───────────────────────────────────────────────────────
            var tabRow = new HBoxContainer();
            tabRow.AddThemeConstantOverride("separation", 4);
            outerVbox.AddChild(tabRow);

            _localTabBtn = MakeTabButton("Local Packages");
            tabRow.AddChild(_localTabBtn);
            _localTabBtn.Pressed += () => SwitchTab(local: true);

            if (modIo != null)
            {
                _onlineTabBtn = MakeTabButton("Browse Online (mod.io)");
                tabRow.AddChild(_onlineTabBtn);
                _onlineTabBtn.Pressed += () => SwitchTab(local: false);
            }

            // Spacer + per-tab action buttons (Refresh on local, Browse on online).
            tabRow.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

            outerVbox.AddChild(new HSeparator());

            // ── Local tab content ─────────────────────────────────────────────
            _localTab = BuildLocalTab();
            outerVbox.AddChild(_localTab);

            // ── Online tab content (only when mod.io is configured) ───────────
            if (modIo != null)
            {
                _onlineTab = BuildOnlineTab();
                _onlineTab.Visible = false;
                outerVbox.AddChild(_onlineTab);
            }

            SwitchTab(local: true);
        }

        // ── _Process — drain mod.io events & update download progress ─────────

        public override void _Process(double delta)
        {
            _modIo?.DrainEvents();

            // Update download progress labels.
            foreach (var (modId, pct) in _downloadProgress)
            {
                if (_downloadLabels.TryGetValue(modId, out var lbl))
                    lbl.Text = $"Downloading… {pct * 100:0}%";
            }
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Toggle panel visibility; refresh local list when opening.</summary>
        public void ToggleVisible()
        {
            Visible = !Visible;
            if (Visible && _localTab.Visible)
                RefreshLocal();
        }

        // ── Tab switching ─────────────────────────────────────────────────────

        private void SwitchTab(bool local)
        {
            _localTab.Visible = local;
            if (_onlineTab != null!) _onlineTab.Visible = !local;

            SetTabActive(_localTabBtn, local);
            if (_onlineTabBtn != null!) SetTabActive(_onlineTabBtn, !local);

            if (local) RefreshLocal();
        }

        private static void SetTabActive(Button btn, bool active)
        {
            btn.AddThemeColorOverride("font_color",
                active ? Colors.White : new Color(0.6f, 0.6f, 0.6f));
        }

        private static Button MakeTabButton(string text)
        {
            var btn = new Button
            {
                Text             = text,
                CustomMinimumSize = new Vector2(180, 34),
                ToggleMode        = false,
            };
            btn.AddThemeFontSizeOverride("font_size", 14);
            return btn;
        }

        // ── Local tab ─────────────────────────────────────────────────────────

        private Control BuildLocalTab()
        {
            var tab = new VBoxContainer();
            tab.AddThemeConstantOverride("separation", 8);
            tab.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

            // Toolbar: directory label + Refresh button.
            var toolbar = new HBoxContainer();
            tab.AddChild(toolbar);

            _dirLabel = new Label();
            _dirLabel.AddThemeFontSizeOverride("font_size", 12);
            _dirLabel.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.5f));
            _dirLabel.AutowrapMode = TextServer.AutowrapMode.Word;
            _dirLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            toolbar.AddChild(_dirLabel);

            var refreshBtn = new Button
            {
                Text             = "Refresh",
                CustomMinimumSize = new Vector2(90, 30),
            };
            refreshBtn.AddThemeFontSizeOverride("font_size", 13);
            refreshBtn.Pressed += RefreshLocal;
            toolbar.AddChild(refreshBtn);

            // Scrollable package list.
            var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            tab.AddChild(scroll);

            _listContainer = new VBoxContainer();
            _listContainer.AddThemeConstantOverride("separation", 8);
            _listContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
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

            return tab;
        }

        private void RefreshLocal()
        {
            if (!System.IO.Directory.Exists(_scanDir))
            {
                try { System.IO.Directory.CreateDirectory(_scanDir); }
                catch { /* best-effort */ }
            }

            _dirLabel.Text = $"Packages folder: {_scanDir}";

            foreach (Node child in _listContainer.GetChildren())
            {
                if (child == _emptyLabel) continue;
                _listContainer.RemoveChild(child);
                child.QueueFree();
            }

            var packages = new List<(string ZipPath, ContentPackageManifest Manifest)>(
                ContentPackager.ScanDirectory(_scanDir));

            if (packages.Count == 0)
            {
                _emptyLabel.Text    = $"No .chimera.zip packages found.\n\nDrop map packages into:\n{_scanDir}";
                _emptyLabel.Visible = true;
            }
            else
            {
                _emptyLabel.Visible = false;
                foreach (var (zipPath, manifest) in packages)
                    _listContainer.AddChild(BuildLocalCard(zipPath, manifest));
            }
        }

        private Control BuildLocalCard(string zipPath, ContentPackageManifest manifest)
        {
            var card = MakeCardPanel();
            var row  = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            card.AddChild(row);

            // Info column.
            var info = new VBoxContainer();
            info.AddThemeConstantOverride("separation", 4);
            info.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(info);

            AddNameLabel(info, manifest.DisplayName);
            AddMetaLabel(info, $"by {manifest.Author}   •   v{manifest.Version}" +
                               (manifest.PlayerCount > 0 ? $"   •   {manifest.PlayerCount}p" : ""));
            if (!string.IsNullOrWhiteSpace(manifest.Description))
                AddDescLabel(info, manifest.Description);
            if (manifest.Tags.Count > 0)
                AddTagRow(info, manifest.Tags);

            // Right column: hash + Load + optional Upload button.
            var rightCol = new VBoxContainer
            {
                Alignment         = BoxContainer.AlignmentMode.Center,
                CustomMinimumSize = new Vector2(140, 0),
            };
            row.AddChild(rightCol);

            if (manifest.ScenarioHash != 0)
            {
                var hashLabel = new Label
                {
                    Text                = $"Hash: 0x{manifest.ScenarioHash:X8}",
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                hashLabel.AddThemeFontSizeOverride("font_size", 10);
                hashLabel.AddThemeColorOverride("font_color", new Color(0.4f, 0.4f, 0.45f));
                rightCol.AddChild(hashLabel);
            }

            var loadBtn = new Button
            {
                Text             = "Load Map",
                CustomMinimumSize = new Vector2(130, 36),
            };
            loadBtn.AddThemeFontSizeOverride("font_size", 14);
            string capturedZip = zipPath;
            loadBtn.Pressed += () => { Visible = false; OnLoadMap?.Invoke(capturedZip); };
            rightCol.AddChild(loadBtn);

            // "Upload to mod.io" — only shown when mod.io service is configured and logged in.
            if (_modIo != null)
            {
                var uploadBtn = new Button
                {
                    Text             = _modIo.IsLoggedIn ? "Upload to mod.io" : "Log in to upload",
                    Disabled         = !_modIo.IsLoggedIn,
                    CustomMinimumSize = new Vector2(130, 30),
                };
                uploadBtn.AddThemeFontSizeOverride("font_size", 12);
                string capturedZipForUpload = zipPath;
                ContentPackageManifest capturedManifest = manifest;
                uploadBtn.Pressed += () =>
                {
                    if (_modIo is { IsLoggedIn: true })
                    {
                        uploadBtn.Text     = "Uploading…";
                        uploadBtn.Disabled = true;
                        _modIo.UploadModAsync(
                            capturedZipForUpload,
                            capturedManifest.DisplayName,
                            capturedManifest.Description,
                            capturedManifest.Version,
                            capturedManifest.Tags);
                    }
                };
                rightCol.AddChild(uploadBtn);
            }

            return card;
        }

        // ── Online tab ────────────────────────────────────────────────────────

        private Control BuildOnlineTab()
        {
            var tab = new VBoxContainer();
            tab.AddThemeConstantOverride("separation", 8);
            tab.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

            // ── Auth row ──────────────────────────────────────────────────────
            var authRow = new HBoxContainer();
            authRow.AddThemeConstantOverride("separation", 10);
            tab.AddChild(authRow);

            _authStatusLabel = new Label { Text = "Not logged in (browse is still available)" };
            _authStatusLabel.AddThemeFontSizeOverride("font_size", 13);
            _authStatusLabel.AddThemeColorOverride("font_color", new Color(0.65f, 0.65f, 0.65f));
            _authStatusLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            authRow.AddChild(_authStatusLabel);

            _loginToggleBtn = new Button
            {
                Text             = "Log In",
                CustomMinimumSize = new Vector2(90, 30),
            };
            _loginToggleBtn.AddThemeFontSizeOverride("font_size", 13);
            _loginToggleBtn.Pressed += ToggleLoginPanel;
            authRow.AddChild(_loginToggleBtn);

            // ── Login form (collapsed by default) ─────────────────────────────
            _loginPanel = BuildLoginForm();
            _loginPanel.Visible = false;
            tab.AddChild(_loginPanel);

            // ── Search row ────────────────────────────────────────────────────
            var searchRow = new HBoxContainer();
            searchRow.AddThemeConstantOverride("separation", 6);
            tab.AddChild(searchRow);

            _searchField = new LineEdit
            {
                PlaceholderText  = "Search maps…",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 32),
            };
            _searchField.AddThemeFontSizeOverride("font_size", 14);
            _searchField.TextSubmitted += (_) => BrowseOnline();
            searchRow.AddChild(_searchField);

            var browseBtn = new Button
            {
                Text             = "Browse",
                CustomMinimumSize = new Vector2(90, 32),
            };
            browseBtn.AddThemeFontSizeOverride("font_size", 14);
            browseBtn.Pressed += BrowseOnline;
            searchRow.AddChild(browseBtn);

            // ── Status label ──────────────────────────────────────────────────
            _onlineStatusLabel = new Label { Text = "Press Browse to fetch maps from mod.io." };
            _onlineStatusLabel.AddThemeFontSizeOverride("font_size", 13);
            _onlineStatusLabel.AddThemeColorOverride("font_color", new Color(0.55f, 0.6f, 0.7f));
            tab.AddChild(_onlineStatusLabel);

            // ── Scrollable online mod list ────────────────────────────────────
            var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            tab.AddChild(scroll);

            _onlineListContainer = new VBoxContainer();
            _onlineListContainer.AddThemeConstantOverride("separation", 8);
            _onlineListContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            scroll.AddChild(_onlineListContainer);

            return tab;
        }

        private Control BuildLoginForm()
        {
            var panel = new PanelContainer();
            panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor           = new Color(0.10f, 0.11f, 0.17f, 1f),
                BorderColor       = new Color(0.25f, 0.35f, 0.55f, 0.6f),
                BorderWidthLeft   = 1, BorderWidthRight  = 1,
                BorderWidthTop    = 1, BorderWidthBottom = 1,
                CornerRadiusTopLeft = 6, CornerRadiusTopRight    = 6,
                CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
                ContentMarginLeft = 14, ContentMarginRight  = 14,
                ContentMarginTop  = 10, ContentMarginBottom = 10,
            });

            var vbox = new VBoxContainer();
            vbox.AddThemeConstantOverride("separation", 6);
            panel.AddChild(vbox);

            var info = new Label
            {
                Text = "Enter your mod.io email. A one-time security code will be sent.",
                AutowrapMode = TextServer.AutowrapMode.Word,
            };
            info.AddThemeFontSizeOverride("font_size", 12);
            info.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
            vbox.AddChild(info);

            // Email row.
            var emailRow = new HBoxContainer();
            emailRow.AddThemeConstantOverride("separation", 6);
            vbox.AddChild(emailRow);

            _emailField = new LineEdit
            {
                PlaceholderText   = "your@email.com",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize  = new Vector2(0, 30),
            };
            _emailField.AddThemeFontSizeOverride("font_size", 13);
            emailRow.AddChild(_emailField);

            _requestCodeBtn = new Button
            {
                Text             = "Send Code",
                CustomMinimumSize = new Vector2(100, 30),
            };
            _requestCodeBtn.AddThemeFontSizeOverride("font_size", 13);
            _requestCodeBtn.Pressed += RequestAuthCode;
            emailRow.AddChild(_requestCodeBtn);

            // Code row.
            var codeRow = new HBoxContainer();
            codeRow.AddThemeConstantOverride("separation", 6);
            vbox.AddChild(codeRow);

            _codeField = new LineEdit
            {
                PlaceholderText   = "5-digit code",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize  = new Vector2(0, 30),
            };
            _codeField.AddThemeFontSizeOverride("font_size", 13);
            _codeField.TextSubmitted += (_) => ExchangeAuthCode();
            codeRow.AddChild(_codeField);

            _exchangeCodeBtn = new Button
            {
                Text             = "Log In",
                CustomMinimumSize = new Vector2(100, 30),
            };
            _exchangeCodeBtn.AddThemeFontSizeOverride("font_size", 13);
            _exchangeCodeBtn.Pressed += ExchangeAuthCode;
            codeRow.AddChild(_exchangeCodeBtn);

            return panel;
        }

        // ── Online tab actions ────────────────────────────────────────────────

        private void ToggleLoginPanel()
        {
            if (_modIo!.IsLoggedIn)
            {
                _modIo.Logout();
                _authStatusLabel.Text  = "Not logged in (browse is still available)";
                _loginToggleBtn.Text   = "Log In";
                _loginPanel.Visible    = false;
                RefreshLocal(); // refresh local cards to disable upload buttons
            }
            else
            {
                _loginPanel.Visible = !_loginPanel.Visible;
                _loginToggleBtn.Text = _loginPanel.Visible ? "Cancel" : "Log In";
            }
        }

        private void RequestAuthCode()
        {
            string email = _emailField.Text.Trim();
            if (string.IsNullOrEmpty(email))
            {
                _onlineStatusLabel.Text = "Enter an email address first.";
                return;
            }
            _requestCodeBtn.Disabled = true;
            _onlineStatusLabel.Text  = $"Sending code to {email}…";
            _modIo!.AuthenticateEmailRequestAsync(email);
        }

        private void ExchangeAuthCode()
        {
            string code = _codeField.Text.Trim();
            if (string.IsNullOrEmpty(code))
            {
                _onlineStatusLabel.Text = "Enter the security code from your email.";
                return;
            }
            _exchangeCodeBtn.Disabled = true;
            _onlineStatusLabel.Text   = "Verifying code…";
            _modIo!.AuthenticateEmailExchangeAsync(code);
        }

        private void BrowseOnline()
        {
            _onlineStatusLabel.Text = "Fetching mod list…";
            ClearOnlineList();
            _modIo!.BrowseModsAsync(limit: 20, searchQuery: _searchField.Text.Trim());
        }

        private void ClearOnlineList()
        {
            foreach (Node child in _onlineListContainer.GetChildren())
            {
                _onlineListContainer.RemoveChild(child);
                child.QueueFree();
            }
            _downloadLabels.Clear();
            _downloadProgress.Clear();
            _downloadComplete.Clear();
        }

        private void PopulateOnlineList(List<ModIoMod> mods)
        {
            ClearOnlineList();
            _onlineStatusLabel.Text = mods.Count == 0
                ? "No mods found. Try a different search."
                : $"{mods.Count} map{(mods.Count != 1 ? "s" : "")} found.";

            foreach (var mod in mods)
                _onlineListContainer.AddChild(BuildOnlineCard(mod));
        }

        private Control BuildOnlineCard(ModIoMod mod)
        {
            var card = MakeCardPanel();
            var row  = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            card.AddChild(row);

            // Info column.
            var info = new VBoxContainer();
            info.AddThemeConstantOverride("separation", 4);
            info.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(info);

            AddNameLabel(info, mod.Name);

            // Meta row: clickable author name + stats.
            var metaRow = new HBoxContainer();
            metaRow.AddThemeConstantOverride("separation", 6);
            info.AddChild(metaRow);

            // Author name is a LinkButton that opens their mod.io profile.
            string profileUrl     = mod.SubmittedBy.ProfileUrl;
            bool   hasProfileUrl  = !string.IsNullOrEmpty(profileUrl);
            string authorDisplay  = $"by {mod.SubmittedBy.Username}";

            if (hasProfileUrl)
            {
                var authorLink = new LinkButton { Text = authorDisplay, TooltipText = profileUrl };
                authorLink.AddThemeFontSizeOverride("font_size", 12);
                string capturedProfile = profileUrl;
                authorLink.Pressed += () => OS.ShellOpen(capturedProfile);
                metaRow.AddChild(authorLink);
            }
            else
            {
                var authorLbl = new Label { Text = authorDisplay };
                authorLbl.AddThemeFontSizeOverride("font_size", 12);
                authorLbl.AddThemeColorOverride("font_color", new Color(0.65f, 0.65f, 0.7f));
                metaRow.AddChild(authorLbl);
            }

            string statsMeta = "";
            if (mod.Stats.DownloadsTotal > 0)   statsMeta += $"   •   {mod.Stats.DownloadsTotal} downloads";
            if (mod.Stats.RatingsPositive + mod.Stats.RatingsNegative > 0)
                statsMeta += $"   •   +{mod.Stats.RatingsPositive} / -{mod.Stats.RatingsNegative}";
            if (!string.IsNullOrEmpty(statsMeta))
            {
                var statsLbl = new Label { Text = statsMeta };
                statsLbl.AddThemeFontSizeOverride("font_size", 12);
                statsLbl.AddThemeColorOverride("font_color", new Color(0.65f, 0.65f, 0.7f));
                metaRow.AddChild(statsLbl);
            }

            if (!string.IsNullOrWhiteSpace(mod.Summary))
                AddDescLabel(info, mod.Summary);

            if (mod.Tags.Count > 0)
            {
                var tagNames = new List<string>();
                foreach (var t in mod.Tags) tagNames.Add(t.Name);
                AddTagRow(info, tagNames);
            }

            // Right column: Download + Subscribe + Rate.
            var rightCol = new VBoxContainer
            {
                Alignment         = BoxContainer.AlignmentMode.Center,
                CustomMinimumSize = new Vector2(150, 0),
            };
            row.AddChild(rightCol);

            // Download button.
            var downloadLabel = new Label
            {
                Text                = _downloadComplete.Contains(mod.Id)
                                        ? "Downloaded" : "Download",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            downloadLabel.AddThemeFontSizeOverride("font_size", 13);
            _downloadLabels[mod.Id] = downloadLabel;

            var downloadBtn = new Button
            {
                CustomMinimumSize = new Vector2(140, 36),
                Disabled          = _downloadComplete.Contains(mod.Id),
            };
            downloadBtn.AddChild(downloadLabel);
            downloadBtn.AddThemeFontSizeOverride("font_size", 13);

            int   capturedId  = mod.Id;
            string capturedUrl = mod.Modfile?.Download.BinaryUrl ?? "";
            downloadBtn.Pressed += () =>
            {
                if (string.IsNullOrEmpty(capturedUrl) || _modIo == null) return;
                downloadBtn.Disabled = true;
                downloadLabel.Text   = "Downloading…";
                _downloadProgress[capturedId] = 0f;

                string destPath = Path.Combine(
                    ProjectSettings.GlobalizePath("user://packages/"),
                    $"{capturedId}.chimera.zip");
                _modIo.DownloadModFileAsync(capturedId, capturedUrl, destPath);
            };
            rightCol.AddChild(downloadBtn);

            // Subscribe button (logged-in only).
            if (_modIo?.IsLoggedIn == true)
            {
                var subBtn = new Button
                {
                    Text             = "Subscribe",
                    CustomMinimumSize = new Vector2(140, 30),
                };
                subBtn.AddThemeFontSizeOverride("font_size", 12);
                subBtn.Pressed += () =>
                {
                    subBtn.Text     = "Subscribed";
                    subBtn.Disabled = true;
                    _modIo!.SubscribeAsync(capturedId);
                };
                rightCol.AddChild(subBtn);

                // Thumbs up / down row. Declare both before wiring closures so
                // each button can reference the other (CS0841 guard).
                var rateRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
                rateRow.AddThemeConstantOverride("separation", 4);
                rightCol.AddChild(rateRow);

                Button thumbUp   = new() { Text = "+", CustomMinimumSize = new Vector2(44, 28), TooltipText = "Rate positive" };
                Button thumbDown = new() { Text = "−", CustomMinimumSize = new Vector2(44, 28), TooltipText = "Rate negative" };

                thumbUp.AddThemeFontSizeOverride("font_size", 16);
                thumbUp.Pressed += () =>
                {
                    thumbUp.Disabled   = true;
                    thumbDown.Disabled = true;
                    _modIo!.RateAsync(capturedId, positive: true);
                };

                thumbDown.AddThemeFontSizeOverride("font_size", 16);
                thumbDown.Pressed += () =>
                {
                    thumbUp.Disabled   = true;
                    thumbDown.Disabled = true;
                    _modIo!.RateAsync(capturedId, positive: false);
                };

                rateRow.AddChild(thumbUp);
                rateRow.AddChild(thumbDown);
            }

            return card;
        }

        // ── ModIoService event wiring ─────────────────────────────────────────

        private void WireModIoEvents()
        {
            if (_modIo == null) return;

            _modIo.OnBrowseComplete += mods =>
            {
                PopulateOnlineList(mods);
            };

            _modIo.OnDownloadProgress += (modId, pct) =>
            {
                _downloadProgress[modId] = pct;
            };

            _modIo.OnDownloadComplete += (modId, localPath) =>
            {
                _downloadProgress.Remove(modId);
                _downloadComplete.Add(modId);
                if (_downloadLabels.TryGetValue(modId, out var lbl))
                    lbl.Text = "Downloaded ✓";
                // Refresh local tab so the new package appears immediately.
                RefreshLocal();
                GD.Print($"[ContentBrowser] Downloaded mod {modId} → {localPath}");
            };

            _modIo.OnAuthCodeSent += () =>
            {
                _requestCodeBtn.Disabled = false;
                _onlineStatusLabel.Text  = "Code sent! Check your email, then enter it below.";
            };

            _modIo.OnLoginSuccess += username =>
            {
                _loginPanel.Visible     = false;
                _loginToggleBtn.Text    = "Log Out";
                _authStatusLabel.Text   = $"Logged in as {username}";
                _authStatusLabel.AddThemeColorOverride("font_color", new Color(0.4f, 0.9f, 0.5f));
                _onlineStatusLabel.Text = "Logged in. You can now upload, subscribe, and rate.";
                _exchangeCodeBtn.Disabled = false;
                // Refresh local cards to enable upload buttons.
                if (_localTab.Visible) RefreshLocal();
            };

            _modIo.OnUploadComplete += modId =>
            {
                _onlineStatusLabel.Text = $"Uploaded successfully (mod.io ID: {modId}).";
                GD.Print($"[ContentBrowser] Upload complete — mod.io ID {modId}");
            };

            _modIo.OnError += (op, msg) =>
            {
                _onlineStatusLabel.Text = $"Error ({op}): {msg}";
                GD.PrintErr($"[ContentBrowser] mod.io error in '{op}': {msg}");
                // Re-enable buttons that may have been disabled optimistically.
                if (op == "auth_request")  _requestCodeBtn.Disabled  = false;
                if (op == "auth_exchange") _exchangeCodeBtn.Disabled = false;
            };
        }

        // ── Shared card builder helpers ───────────────────────────────────────

        private static PanelContainer MakeCardPanel()
        {
            var card = new PanelContainer();
            card.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor                 = new Color(0.13f, 0.14f, 0.20f, 1f),
                BorderColor             = new Color(0.30f, 0.35f, 0.50f, 0.7f),
                BorderWidthLeft         = 1, BorderWidthRight  = 1,
                BorderWidthTop          = 1, BorderWidthBottom = 1,
                CornerRadiusTopLeft     = 6, CornerRadiusTopRight    = 6,
                CornerRadiusBottomLeft  = 6, CornerRadiusBottomRight = 6,
                ContentMarginLeft       = 14f, ContentMarginRight  = 14f,
                ContentMarginTop        = 10f, ContentMarginBottom = 10f,
            });
            return card;
        }

        private static void AddNameLabel(Control parent, string text)
        {
            var lbl = new Label { Text = text };
            lbl.AddThemeFontSizeOverride("font_size", 18);
            lbl.AddThemeColorOverride("font_color", Colors.White);
            parent.AddChild(lbl);
        }

        private static void AddMetaLabel(Control parent, string text)
        {
            var lbl = new Label { Text = text };
            lbl.AddThemeFontSizeOverride("font_size", 12);
            lbl.AddThemeColorOverride("font_color", new Color(0.65f, 0.65f, 0.7f));
            parent.AddChild(lbl);
        }

        private static void AddDescLabel(Control parent, string text)
        {
            var lbl = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.Word };
            lbl.AddThemeFontSizeOverride("font_size", 12);
            lbl.AddThemeColorOverride("font_color", new Color(0.75f, 0.75f, 0.75f));
            parent.AddChild(lbl);
        }

        private static void AddTagRow(Control parent, List<string> tags)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 4);
            parent.AddChild(row);
            for (int i = 0; i < tags.Count; i++)
            {
                var badge = new PanelContainer();
                badge.AddThemeStyleboxOverride("panel", new StyleBoxFlat
                {
                    BgColor                 = TAG_COLORS[i % TAG_COLORS.Length],
                    CornerRadiusTopLeft     = 4, CornerRadiusTopRight    = 4,
                    CornerRadiusBottomLeft  = 4, CornerRadiusBottomRight = 4,
                    ContentMarginLeft       = 6f, ContentMarginRight  = 6f,
                    ContentMarginTop        = 2f, ContentMarginBottom = 2f,
                });
                var tagLbl = new Label { Text = tags[i] };
                tagLbl.AddThemeFontSizeOverride("font_size", 11);
                tagLbl.AddThemeColorOverride("font_color", Colors.White);
                badge.AddChild(tagLbl);
                row.AddChild(badge);
            }
        }
    }
}
