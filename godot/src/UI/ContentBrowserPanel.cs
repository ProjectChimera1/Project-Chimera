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
    ///   Online — browses and downloads maps from mod.io (requires ModIoGameId set in MainScene's Inspector
    ///            export, plus a mod.io key in the ISecretStore — user://secrets/modio.key, Story 8.1 — not a
    ///            plaintext [Export] field).
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

        /// <summary>DW-425 — where a failed-integrity download is moved. A SIBLING of user://packages/ (never inside
        /// it) so the RefreshLocal scan can never re-list a quarantined package as a playable local card.</summary>
        private const string QUARANTINE_DIR = "user://packages_quarantine/";

        private string         _scanDir = "";
        private ModIoService?  _modIo;
        // Story 9.8: the secret store supplies the per-install proof-of-play HMAC key so the publish gate can VERIFY a
        // package's token before upload. Null ⇒ no key available ⇒ the gate fails a token as invalid (fail-closed).
        private ISecretStore?  _secretStore;

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

        // Story 9.10: sort dropdown + mod.io tag-filter chips (both re-issue browse with the composed sort+search+tags).
        private OptionButton    _sortDropdown = null!;
        private HFlowContainer  _tagChipRow   = null!;
        private readonly List<CheckBox>  _tagChips      = new();
        private readonly HashSet<string> _selectedTags  = new();
        private bool                     _tagsFetched   = false;

        // Story 9.10: mod.io-native _sort tokens. Default -popular is the already-shipped, known-good browse order;
        // the rest are a small curated set a later story can tune. An unexpected token surfaces via OnError (the
        // mod.io response), never a crash.
        private static readonly (string Label, string Token)[] SORT_OPTIONS =
        {
            ("Popular",         "-popular"),
            ("Most Downloaded", "-downloads"),
            ("Newest",          "-date_live"),
            ("Name A–Z",        "name"),
        };

        // Download state: modId → (button label, progress 0-1)
        private readonly Dictionary<int, Label>  _downloadLabels   = new();
        private readonly Dictionary<int, float>  _downloadProgress = new();
        private readonly HashSet<int>            _downloadComplete = new();

        // Story 9.10: per-card thumbnail targets + the logo URL used to pick the decoder (jpg/png).
        private readonly Dictionary<int, TextureRect> _thumbnails    = new();
        private readonly Dictionary<int, string>      _thumbnailUrls = new();
        private ImageTexture? _placeholderTex;

        // Story 9.10: per-card subscribe/rate buttons + which mods have a committed rating (so an OnError revert
        // never re-enables an already-succeeded action).
        private readonly Dictionary<int, Button>                 _subscribeButtons = new();
        private readonly Dictionary<int, (Button Up, Button Down)> _rateButtons    = new();
        private readonly HashSet<int>                            _ratedMods        = new();

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
        /// <param name="secretStore">Story 9.8 — optional secret store holding the proof-of-play HMAC key, used by the
        /// pre-publish gate to verify a package's token. Null ⇒ the gate fails-closed on any token.</param>
        public void Initialize(string scanDirectory, ModIoService? modIo = null, ISecretStore? secretStore = null)
        {
            Layer   = 10; // above HUD (8) and chat overlay (8)
            Visible = false;

            _scanDir = ProjectSettings.GlobalizePath(scanDirectory);
            _modIo   = modIo;
            _secretStore = secretStore;

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

            var closeBtn = new Button { Text = EditorHotkeys.CloseLabel(EditorPanelId.ContentBrowser), CustomMinimumSize = new Vector2(150, 36) };
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

            // "Upload to mod.io" — only shown when mod.io service is configured and logged in. Story 9.8: gated behind
            // an explicit IP-ownership consent checkbox AND the unified PublishGate (proof-of-play token + thumbnail +
            // description ≥100 + ≥1 screenshot + consent) — refused with the located reason(s) on failure.
            if (_modIo != null)
            {
                string capturedZipForUpload = zipPath;
                ContentPackageManifest capturedManifest = manifest;

                // Refusal/status line for this card's publish gate.
                var gateStatus = new Label
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    AutowrapMode        = TextServer.AutowrapMode.Word,
                    Visible             = false,
                };
                gateStatus.AddThemeFontSizeOverride("font_size", 10);
                gateStatus.AddThemeColorOverride("font_color", new Color(0.95f, 0.55f, 0.4f));

                var uploadBtn = new Button
                {
                    Text             = _modIo.IsLoggedIn ? "Upload to mod.io" : "Log in to upload",
                    CustomMinimumSize = new Vector2(130, 30),
                };
                uploadBtn.AddThemeFontSizeOverride("font_size", 12);

                // IP-ownership consent: upload disabled until checked (and logged in).
                var consentCheck = new CheckBox
                {
                    Text = "I own / may distribute this",
                };
                consentCheck.AddThemeFontSizeOverride("font_size", 10);
                void SyncUploadEnabled() =>
                    uploadBtn.Disabled = !(_modIo!.IsLoggedIn && consentCheck.ButtonPressed);
                consentCheck.Toggled += _ => SyncUploadEnabled();
                SyncUploadEnabled();
                rightCol.AddChild(consentCheck);

                uploadBtn.Pressed += () =>
                {
                    if (_modIo is not { IsLoggedIn: true }) return;

                    // Consent is a LIVE choice — stamp it onto the (in-memory) manifest the gate evaluates. The token +
                    // screenshots + thumbnail already rode in from packaging.
                    capturedManifest.IpConsent = consentCheck.ButtonPressed;
                    ulong  currentHash = ComputeCurrentCanonicalHash(capturedZipForUpload);
                    byte[] key = LoadSigningKey();

                    var result = ProjectChimera.UGC.PublishGate.Check(
                        capturedManifest, capturedManifest.ProofOfPlay, currentHash, key);
                    if (!result.Passed)
                    {
                        gateStatus.Text    = "Cannot publish: " + string.Join("; ", result.Reasons);
                        gateStatus.Visible = true;
                        return;
                    }

                    // Story 9.8 (review P2): persist the approved consent (and evaluated fields) INTO the shipped zip
                    // so the on-disk package records ip_consent:true, not the export-time default. Follow-up review:
                    // fail CLOSED — the intent requires consent be written into the manifest and ONLY THEN the upload;
                    // if the record cannot be written, refuse rather than ship a package that records ip_consent:false
                    // while uploading (the IP-consent record is the whole reason this field exists).
                    try { ContentPackager.RewriteManifest(capturedZipForUpload, capturedManifest); }
                    catch (Exception ex)
                    {
                        GD.PrintErr($"[ContentBrowser] Manifest rewrite failed: {ex.Message}");
                        gateStatus.Text    = "Cannot publish: could not record IP-ownership consent into the package.";
                        gateStatus.Visible = true;
                        return;
                    }

                    gateStatus.Visible = false;
                    uploadBtn.Text     = "Uploading…";
                    uploadBtn.Disabled = true;
                    _modIo.UploadModAsync(
                        capturedZipForUpload,
                        capturedManifest.DisplayName,
                        capturedManifest.Description,
                        capturedManifest.Version,
                        capturedManifest.Tags);
                };
                rightCol.AddChild(uploadBtn);
                rightCol.AddChild(gateStatus);
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

            // ── Sort row (mod.io-native _sort tokens) ─────────────────────────
            var sortRow = new HBoxContainer();
            sortRow.AddThemeConstantOverride("separation", 6);
            tab.AddChild(sortRow);

            var sortLabel = new Label { Text = "Sort:" };
            sortLabel.AddThemeFontSizeOverride("font_size", 13);
            sortLabel.AddThemeColorOverride("font_color", new Color(0.65f, 0.65f, 0.7f));
            sortRow.AddChild(sortLabel);

            _sortDropdown = new OptionButton { CustomMinimumSize = new Vector2(170, 30) };
            _sortDropdown.AddThemeFontSizeOverride("font_size", 13);
            for (int i = 0; i < SORT_OPTIONS.Length; i++)
                _sortDropdown.AddItem(SORT_OPTIONS[i].Label, i);
            _sortDropdown.Selected = 0;
            // Re-issue browse with the newly chosen sort, preserving current search + tags.
            _sortDropdown.ItemSelected += _ => BrowseOnline();
            sortRow.AddChild(_sortDropdown);

            var tagsLabel = new Label { Text = "   Tags:" };
            tagsLabel.AddThemeFontSizeOverride("font_size", 13);
            tagsLabel.AddThemeColorOverride("font_color", new Color(0.65f, 0.65f, 0.7f));
            sortRow.AddChild(tagsLabel);

            // Tag chips wrap onto multiple lines; populated from GET /games/{id}/tags (no local tag index).
            _tagChipRow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            sortRow.AddChild(_tagChipRow);

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

        /// <summary>
        /// Story 9.10: a logged-out user clicked a login-gated action (subscribe/rate). Open the login panel and
        /// surface a prompt; the mod.io write is NOT sent until they log in.
        /// </summary>
        private void PromptLoginFor(string action)
        {
            _loginPanel.Visible  = true;
            _loginToggleBtn.Text = "Cancel";
            _onlineStatusLabel.Text = $"Log in to {action}. Enter your mod.io email above to receive a code.";
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

            // Fetch the game's tag options once (first browse of the tab), so the chip row is mod.io-driven.
            if (!_tagsFetched)
            {
                _tagsFetched = true;
                _modIo!.GetGameTagsAsync();
            }

            int idx = (int)_sortDropdown.Selected;
            if (idx < 0 || idx >= SORT_OPTIONS.Length) idx = 0;
            string sortToken = SORT_OPTIONS[idx].Token;

            List<string>? tags = _selectedTags.Count > 0 ? new List<string>(_selectedTags) : null;

            // Compose search + tag + sort into ONE mod.io query (no client-side re-sort/re-filter).
            _modIo!.BrowseModsAsync(
                limit: 20,
                searchQuery: _searchField.Text.Trim(),
                sort: sortToken,
                tags: tags);
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
            _thumbnails.Clear();
            _thumbnailUrls.Clear();
            _subscribeButtons.Clear();
            _rateButtons.Clear();
            _ratedMods.Clear();
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

            // Thumbnail (mod.io logo). A neutral placeholder shows while loading or when the mod has no logo; the
            // real bytes are fetched async via DownloadThumbnailAsync and decoded in OnThumbnailReady.
            var thumb = new TextureRect
            {
                CustomMinimumSize = new Vector2(96, 54),
                ExpandMode        = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode       = TextureRect.StretchModeEnum.KeepAspectCentered,
                Texture           = PlaceholderThumbnail(),
            };
            row.AddChild(thumb);
            _thumbnails[mod.Id] = thumb;
            string logoUrl = mod.Logo?.Thumb320x180 ?? "";
            if (!string.IsNullOrEmpty(logoUrl))
            {
                _thumbnailUrls[mod.Id] = logoUrl;
                _modIo?.DownloadThumbnailAsync(mod.Id, logoUrl);
            }

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

            // mod.io-native stats: downloads + rating. Prefer mod.io's own weighted display text ("94% (128)")
            // when present; otherwise fall back to the raw +N/−N counts. Never a locally computed score.
            string statsMeta = "";
            if (mod.Stats.DownloadsTotal > 0)   statsMeta += $"   •   {mod.Stats.DownloadsTotal} downloads";
            if (!string.IsNullOrWhiteSpace(mod.Stats.RatingsDisplayText))
                statsMeta += $"   •   {mod.Stats.RatingsDisplayText}";
            else if (mod.Stats.RatingsPositive + mod.Stats.RatingsNegative > 0)
                statsMeta += $"   •   +{mod.Stats.RatingsPositive} / -{mod.Stats.RatingsNegative}";
            if (!string.IsNullOrEmpty(statsMeta))
            {
                var statsLbl = new Label { Text = statsMeta };
                statsLbl.AddThemeFontSizeOverride("font_size", 12);
                statsLbl.AddThemeColorOverride("font_color", new Color(0.65f, 0.65f, 0.7f));
                metaRow.AddChild(statsLbl);
            }

            // Author ownership / attribution line — surfaced from the mod.io entry (beside the profile link above).
            // Reflects mod.io's actual model (author retains IP; platform takes a host/distribute right, mirroring the
            // Story 9.8 IP-consent framing) — no unverified "©" ownership assertion. Guarded against a blank username.
            string ownerName = mod.SubmittedBy.Username;
            var ownershipLbl = new Label
            {
                Text = string.IsNullOrWhiteSpace(ownerName)
                    ? "Hosted & distributed via mod.io"
                    : $"IP retained by {ownerName} · hosted & distributed via mod.io",
                AutowrapMode = TextServer.AutowrapMode.Word,
            };
            ownershipLbl.AddThemeFontSizeOverride("font_size", 10);
            ownershipLbl.AddThemeColorOverride("font_color", new Color(0.5f, 0.52f, 0.6f));
            info.AddChild(ownershipLbl);

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

            // Subscribe + Rate are ALWAYS shown (Story 9.10). A logged-out click opens the login panel + prompts
            // instead of calling mod.io; a logged-in click sends the request and reflects success/error events.
            var subBtn = new Button
            {
                Text             = "Subscribe",
                CustomMinimumSize = new Vector2(140, 30),
            };
            subBtn.AddThemeFontSizeOverride("font_size", 12);
            subBtn.Pressed += () =>
            {
                if (_modIo == null) return;
                if (!_modIo.IsLoggedIn) { PromptLoginFor("subscribe"); return; }
                subBtn.Text     = "Subscribing…";
                subBtn.Disabled = true;
                _modIo.SubscribeAsync(capturedId);
            };
            rightCol.AddChild(subBtn);
            _subscribeButtons[capturedId] = subBtn;

            // Thumbs up / down row. Declare both before wiring closures so each button can reference the other
            // (CS0841 guard).
            var rateRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            rateRow.AddThemeConstantOverride("separation", 4);
            rightCol.AddChild(rateRow);

            Button thumbUp   = new() { Text = "+", CustomMinimumSize = new Vector2(44, 28), TooltipText = "Rate positive" };
            Button thumbDown = new() { Text = "−", CustomMinimumSize = new Vector2(44, 28), TooltipText = "Rate negative" };

            thumbUp.AddThemeFontSizeOverride("font_size", 16);
            thumbUp.Pressed += () =>
            {
                if (_modIo == null) return;
                if (!_modIo.IsLoggedIn) { PromptLoginFor("rate"); return; }
                thumbUp.Disabled   = true;
                thumbDown.Disabled = true;
                _modIo.RateAsync(capturedId, positive: true);
            };

            thumbDown.AddThemeFontSizeOverride("font_size", 16);
            thumbDown.Pressed += () =>
            {
                if (_modIo == null) return;
                if (!_modIo.IsLoggedIn) { PromptLoginFor("rate"); return; }
                thumbUp.Disabled   = true;
                thumbDown.Disabled = true;
                _modIo.RateAsync(capturedId, positive: false);
            };

            rateRow.AddChild(thumbUp);
            rateRow.AddChild(thumbDown);
            _rateButtons[capturedId] = (thumbUp, thumbDown);

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

            // Story 9.10: build the tag-filter chips from mod.io's own game tags (no local tag index). If the fetch
            // failed, OnError fires instead and the chip row simply stays empty — browse/sort still work.
            _modIo.OnTagOptionsReady += names =>
            {
                foreach (var chip in _tagChips) chip.QueueFree();
                _tagChips.Clear();

                // Prune any selected tag no longer offered by mod.io, so a stale selection can't keep filtering
                // browse with no chip left to clear it (would otherwise silently narrow results to empty).
                _selectedTags.IntersectWith(names);

                var seen = new HashSet<string>();
                foreach (var name in names)
                {
                    if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                    var chip = new CheckBox { Text = name, ButtonPressed = _selectedTags.Contains(name) };
                    chip.AddThemeFontSizeOverride("font_size", 12);
                    string capturedName = name;
                    chip.Toggled += pressed =>
                    {
                        if (pressed) _selectedTags.Add(capturedName);
                        else         _selectedTags.Remove(capturedName);
                        BrowseOnline(); // re-issue with the composed sort + search + tags
                    };
                    _tagChips.Add(chip);
                    _tagChipRow.AddChild(chip);
                }
            };

            // Story 9.10: decode fetched logo bytes into the card's TextureRect. Any failure leaves the placeholder;
            // never throws.
            _modIo.OnThumbnailReady += (modId, bytes) =>
            {
                if (!_thumbnails.TryGetValue(modId, out var rect)) return;
                try
                {
                    var img = new Image();
                    string url = _thumbnailUrls.TryGetValue(modId, out var u) ? u : "";
                    bool jpgFirst = !url.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

                    Error err = jpgFirst ? img.LoadJpgFromBuffer(bytes) : img.LoadPngFromBuffer(bytes);
                    if (err != Error.Ok)
                        err = jpgFirst ? img.LoadPngFromBuffer(bytes) : img.LoadJpgFromBuffer(bytes);

                    if (err == Error.Ok && img.GetWidth() > 0)
                        rect.Texture = ImageTexture.CreateFromImage(img);
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[ContentBrowser] Thumbnail decode failed for mod {modId}: {ex.Message}");
                    // Placeholder stays.
                }
            };

            // Story 9.10: reflect a successful subscribe.
            _modIo.OnSubscribeComplete += modId =>
            {
                if (_subscribeButtons.TryGetValue(modId, out var b))
                {
                    b.Text     = "Subscribed ✓";
                    b.Disabled = true;
                }
            };

            // Story 9.10: reflect a successful rating — the chosen thumb is highlighted and the pair stays disabled.
            _modIo.OnRateComplete += (modId, positive) =>
            {
                _ratedMods.Add(modId);
                if (_rateButtons.TryGetValue(modId, out var pair))
                {
                    pair.Up.Disabled   = true;
                    pair.Down.Disabled = true;
                    var chosen = positive ? pair.Up : pair.Down;
                    chosen.AddThemeColorOverride("font_color", new Color(0.4f, 0.9f, 0.5f));
                }
            };

            _modIo.OnDownloadProgress += (modId, pct) =>
            {
                _downloadProgress[modId] = pct;
            };

            _modIo.OnDownloadComplete += (modId, localPath) =>
            {
                _downloadProgress.Remove(modId);

                // Story 9.9: integrity-verify the freshly downloaded package (scenario + terrain + asset bytes) BEFORE
                // it is playable. A hash mismatch or a missing/disallowed/oversized listed entry throws
                // InvalidDataException → the download is marked not-playable (never added to _downloadComplete, so its
                // card is not offered as ready) and the located reason is surfaced. DW-425: the rejected .chimera.zip
                // is also quarantined OUT of user://packages/ — leaving it there let RefreshLocal (which scans that
                // directory) re-list the rejected package as a playable local card on the next refresh/launch. The
                // bundled assets are NOT ingested here: ReloadCurrentScene rebuilds the AssetRegistry, so the render
                // ingest runs on the load path (FactionVisualsPhase) into the registry the bridges actually read.
                // This extraction is verify-only.
                string cacheDir = Path.Combine(
                    ProjectSettings.GlobalizePath("user://package_cache/"), modId.ToString());
                try
                {
                    var result = ContentPackager.Unpack(localPath, cacheDir);

                    _downloadComplete.Add(modId);
                    if (_downloadLabels.TryGetValue(modId, out var okLbl))
                        okLbl.Text = "Downloaded ✓";
                    // Refresh local tab so the new package appears immediately.
                    RefreshLocal();
                    GD.Print($"[ContentBrowser] Downloaded + verified mod {modId} → {localPath} " +
                             $"({result.Manifest.AssetFiles?.Count ?? 0} asset(s)).");
                }
                catch (InvalidDataException ex)
                {
                    if (_downloadLabels.TryGetValue(modId, out var badLbl))
                        badLbl.Text = "Corrupt ✗";
                    QuarantineRejectedDownload(modId, localPath);
                    _onlineStatusLabel.Text = $"Download rejected — {ex.Message}";
                    GD.PrintErr($"[ContentBrowser] Integrity check failed for mod {modId}: {ex.Message}");
                }
                catch (Exception ex)
                {
                    if (_downloadLabels.TryGetValue(modId, out var errLbl))
                        errLbl.Text = "Verify failed ✗";
                    QuarantineRejectedDownload(modId, localPath);
                    _onlineStatusLabel.Text = $"Download could not be verified — {ex.Message}";
                    GD.PrintErr($"[ContentBrowser] Verify error for mod {modId}: {ex.Message}");
                }
                finally
                {
                    // Story 9.9 (review P8): this is a verify-only cache never read for rendering — always clean it up,
                    // which also drops a partial extraction left by a rejected/failed download so it doesn't leak.
                    try { if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true); }
                    catch { /* best-effort */ }
                }
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
                GD.PrintErr($"[ContentBrowser] mod.io error in '{op}': {msg}");

                // Story 9.10: thumbnail + tag-option fetches are BACKGROUND, per-card/one-shot ops — their failures
                // must NOT clobber the user-facing browse status ("N maps found"). Log only. For tags, reset the
                // pre-call latch so the next browse retries the fetch (a transient offline/5xx must not permanently
                // disable the chip row). These ops never touch the auth/subscribe/rate button state, so return early.
                if (op == "thumbnail") return;
                if (op == "tags") { _tagsFetched = false; return; }

                // User-initiated ops surface to the status label.
                _onlineStatusLabel.Text = $"Error ({op}): {msg}";
                // Re-enable buttons that may have been disabled optimistically.
                if (op == "auth_request")  _requestCodeBtn.Disabled  = false;
                if (op == "auth_exchange") _exchangeCodeBtn.Disabled = false;

                // Story 9.10: revert an in-flight subscribe (its button reads "Subscribing…") back to actionable.
                if (op == "subscribe")
                    foreach (var b in _subscribeButtons.Values)
                        if (b.Text == "Subscribing…") { b.Disabled = false; b.Text = "Subscribe"; }

                // Story 9.10: re-enable rate pairs that were optimistically disabled but never committed.
                if (op == "rate")
                    foreach (var (modId, pair) in _rateButtons)
                        if (!_ratedMods.Contains(modId))
                        {
                            pair.Up.Disabled   = false;
                            pair.Down.Disabled = false;
                        }
            };
        }

        // ── DW-425 — rejected-download quarantine ─────────────────────────────

        /// <summary>
        /// Move a download that failed integrity verification OUT of the local-packages scan directory (into
        /// <see cref="QUARANTINE_DIR"/>, bytes kept for diagnostics; deleted as a fallback if the move fails), then
        /// refresh the local tab so any card the rejected file was shown as disappears. Without this, the rejected
        /// .chimera.zip stayed in user://packages/ and RefreshLocal re-listed it as an unverified playable card on
        /// the next launch (the DW-425 defect).
        /// </summary>
        private void QuarantineRejectedDownload(int modId, string localPath)
        {
            string? quarantined = ContentPackager.QuarantineRejectedPackage(
                localPath, ProjectSettings.GlobalizePath(QUARANTINE_DIR));
            GD.PrintErr(quarantined != null
                ? $"[ContentBrowser] Rejected download for mod {modId} quarantined → {quarantined}"
                : $"[ContentBrowser] Rejected download for mod {modId} removed from the packages directory " +
                  "(quarantine move unavailable).");
            // Drop any local card the rejected file may have been listed as (mirrors the success branch's refresh).
            RefreshLocal();
        }

        // ── Story 9.8 publish-gate inputs ─────────────────────────────────────

        /// <summary>Re-derive the CURRENT canonical model hash of a package's scenario (extract scenario.json → load →
        /// <see cref="CanonicalModelHash.Compute"/>) so the gate can detect an edited-after-win (stale) token.
        /// Fail-closed: any error returns 0, which never matches a real token hash ⇒ the gate rejects it as stale.</summary>
        private static ulong ComputeCurrentCanonicalHash(string zipPath)
        {
            string tmp = Path.Combine(
                ProjectSettings.GlobalizePath("user://tmp"), "publish_gate_" + Guid.NewGuid().ToString("N"));
            try
            {
                var result   = ContentPackager.Unpack(zipPath, tmp);
                var scenario = ScenarioSerializer.LoadFromFile(result.ScenarioPath);
                return scenario == null ? 0UL : CanonicalModelHash.Compute(scenario);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ContentBrowser] Canonical hash compute failed: {ex.Message}");
                return 0UL; // fail-closed → the gate treats the token as stale
            }
            finally
            {
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); } catch { /* best-effort */ }
            }
        }

        /// <summary>Load the per-install proof-of-play HMAC key (hex) from the secret store. Absent/unreadable ⇒ empty
        /// key, so the gate's signature verify fails-closed and refuses the token as invalid.</summary>
        private byte[] LoadSigningKey()
        {
            try
            {
                string hex = _secretStore?.Get(SecretIds.ProofOfPlay) ?? "";
                return string.IsNullOrEmpty(hex) ? Array.Empty<byte>() : Convert.FromHexString(hex);
            }
            catch { return Array.Empty<byte>(); }
        }

        // ── Shared card builder helpers ───────────────────────────────────────

        /// <summary>Story 9.10: a lazily-built neutral placeholder texture shown until a mod's logo decodes (or
        /// permanently when a mod has no logo). Reused across all cards.</summary>
        private ImageTexture PlaceholderThumbnail()
        {
            if (_placeholderTex == null)
            {
                var img = Image.CreateEmpty(96, 54, false, Image.Format.Rgba8);
                img.Fill(new Color(0.18f, 0.20f, 0.28f, 1f));
                _placeholderTex = ImageTexture.CreateFromImage(img);
            }
            return _placeholderTex;
        }

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
