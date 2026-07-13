#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;
using ProjectChimera.Core.Definitions;   // ItemDefinition, ItemDefinitionValidator, ItemLoader, ItemWriter, ContentJson
using ProjectChimera.UI;                  // GameState, GameMode
using ProjectChimera.UI.Components;        // ChimeraComponents, ChimeraTabs, ChimeraDialog, ChimeraTooltip, ChimeraValidationBadge
using ProjectChimera.UI.Theme;             // ThemeTokens, ThemeBuilder, AccentController
using GodotTheme = Godot.Theme;

namespace ProjectChimera.CreationSuite
{
    /// <summary>
    /// Story 3.16 — the Item Card Editor, mirroring the Story 3.4 Unit Card Editor (<see cref="UnitCardPanel"/>): a
    /// code-built (no <c>.tscn</c>) <see cref="Node"/> panel composed from the <see cref="ChimeraComponents"/> kit, with a
    /// Simple/Advanced <c>Segment</c> disclosure, per-field <see cref="ChimeraValidationBadge"/>s keyed by JSON field, a
    /// raw-JSON escape hatch (where a dirty pane wins on Save), <see cref="EditorHistory"/> undo, a New/Duplicate/Delete
    /// toolbar with a <see cref="ChimeraDialog"/> confirm, and an F5 fail-closed gate. Every create/edit/duplicate passes
    /// the SAME fail-closed keyed validator (<see cref="ItemDefinitionValidator.ValidateFields"/>, incl. the missing-icon
    /// check), and each item persists to its OWN <c>resources/data/items/&lt;id&gt;.json</c> via <see cref="ItemWriter"/>
    /// (atomic <c>.tmp</c> write + reload self-check through <see cref="ItemLoader.LoadFromFile"/> + <c>File.Move</c>).
    ///
    /// <para>PURE AUTHORING-TIME: editing a content POCO + rewriting a JSON file touches no sim array/store/checksum
    /// (item definitions are folded into no hash here — that is Story 9.1). The only sim additions this story ships are
    /// the shop <c>BuyItem</c> mint/spend; the editor is presentation.</para>
    /// </summary>
    public partial class ItemCardPanel : Node
    {
        private const int PANEL_W = 460;

        private GodotTheme     _theme  = null!;
        private AccentController? _accent;
        private GameState?     _gameState;
        private string         _itemsDir = "res://resources/data/items";

        // Editor state.
        private readonly List<ItemDefinition> _items = new();
        private int _index;
        private ItemDefinition? _current;
        private string _originalId = "";
        private string _preEditJson = "";
        private bool _building;
        private readonly EditorHistory _history = new();
        private readonly ItemDefinitionValidator _validator = new();
        private readonly Dictionary<string, ChimeraValidationBadge> _badges = new();
        private bool _lastValid = true;

        // Shell nodes.
        private PanelContainer _panel   = null!;
        private VBoxContainer  _bodyHost = null!;
        private VBoxContainer? _advancedHost;
        private Label _counterLabel = null!;
        private Label _statusLabel  = null!;
        private ChimeraTabs _segment = null!;
        private TextEdit? _jsonPane;
        private bool _paneDirty;
        private bool _suppressPaneDirty;
        private Button _saveBtn = null!, _newBtn = null!, _dupBtn = null!, _deleteBtn = null!;

        // ── Lifecycle ────────────────────────────────────────────────────────────

        public void Initialize(string itemsDirResPath, GameState gameState)
        {
            if (!string.IsNullOrEmpty(itemsDirResPath)) _itemsDir = itemsDirResPath;
            _gameState = gameState;
            _gameState.ModeChanged += OnModeChanged;
            LoadItemsFromDir();
        }

        public override void _Ready()
        {
            EnsureKitInitialized();
            BuildUi();
            _panel.Visible = false;
        }

        /// <summary>Toggle the editor open/closed (Edit-mode hotkey). Refreshes on open.</summary>
        public void Toggle()
        {
            _panel.Visible = !_panel.Visible;
            if (_panel.Visible) { LoadItemsFromDir(); Refresh(); }
        }

        public void Close() => _panel.Visible = false;

        private void OnModeChanged(int mode)
        {
            if (mode == (int)GameMode.Play) Close();
        }

        // ── Kit bootstrap (copied verbatim from UnitCardPanel) ─────────────────────

        private void EnsureKitInitialized()
        {
            _theme = ResourceLoader.Load<GodotTheme>(ThemeBuilder.ThemePath, cacheMode: ResourceLoader.CacheMode.Ignore)
                     ?? ThemeBuilder.Build();
            if (!ChimeraComponents.IsInitialized)
            {
                _accent = new AccentController { Name = "AccentController" };
                AddChild(_accent);
                _accent.Initialize(_theme);
                ChimeraComponents.Initialize(_theme, _accent);
            }
        }

        // ── UI construction ────────────────────────────────────────────────────────

        private void BuildUi()
        {
            var canvas = new CanvasLayer { Layer = 11 };
            AddChild(canvas);

            _panel = ChimeraComponents.Panel(ChimeraComponents.PanelVariant.Default);
            _panel.Theme = _theme;
            _panel.CustomMinimumSize = new Vector2(PANEL_W, 0);
            // Top-left docked (not centre-left): under CenterLeft the (20,60) offset was measured from the vertical
            // centre, pushing a ~580px-tall panel off the bottom of the screen — the playtest cutoff. TopLeft +
            // GrowDirection.End measures (20,60) from the top-left corner and grows the panel down-and-right so it sits
            // fully on-screen; the inner ScrollContainer handles the body overflow.
            _panel.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            _panel.GrowHorizontal = Control.GrowDirection.End;
            _panel.GrowVertical   = Control.GrowDirection.End;
            _panel.OffsetLeft = 20;
            _panel.OffsetTop  = 60;
            canvas.AddChild(_panel);

            var root = new VBoxContainer { CustomMinimumSize = new Vector2(PANEL_W - 24, 0) };
            root.AddThemeConstantOverride("separation", 6);
            _panel.AddChild(root);

            // Title row: heading + browse ◀/▶ + counter + Close.
            var titleRow = new HBoxContainer();
            var heading = ChimeraComponents.FieldLabel("Item Card Editor");
            heading.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            titleRow.AddChild(heading);
            var prev = ChimeraComponents.IconButton("◀");
            prev.Pressed += () => Browse(-1);
            titleRow.AddChild(prev);
            _counterLabel = new Label { Text = "0/0" };
            titleRow.AddChild(_counterLabel);
            var next = ChimeraComponents.IconButton("▶");
            next.Pressed += () => Browse(1);
            titleRow.AddChild(next);
            var close = ChimeraComponents.IconButton("✕");
            close.Pressed += Close;
            titleRow.AddChild(close);
            root.AddChild(titleRow);

            // Simple/Advanced disclosure.
            _segment = ChimeraTabs.Create(ChimeraComponents.TabsVariant.Segment, "Simple", "Advanced");
            _segment.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
            _segment.TabChanged += OnSegmentChanged;
            root.AddChild(_segment);

            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(PANEL_W - 24, 460) };
            scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            root.AddChild(scroll);
            _bodyHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _bodyHost.AddThemeConstantOverride("separation", 4);
            scroll.AddChild(_bodyHost);

            _statusLabel = new Label { Text = "" };
            root.AddChild(_statusLabel);

            // Toolbar.
            var toolbar = new HBoxContainer();
            _saveBtn = ChimeraComponents.Button("Save", ChimeraComponents.ButtonVariant.Primary);
            _saveBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _saveBtn.Pressed += DoSave;
            toolbar.AddChild(_saveBtn);
            _newBtn = ChimeraComponents.Button("New", ChimeraComponents.ButtonVariant.Secondary);
            _newBtn.Pressed += DoCreate;
            toolbar.AddChild(_newBtn);
            _dupBtn = ChimeraComponents.Button("Duplicate", ChimeraComponents.ButtonVariant.Ghost);
            _dupBtn.Pressed += DoDuplicate;
            toolbar.AddChild(_dupBtn);
            _deleteBtn = ChimeraComponents.Button("Delete", ChimeraComponents.ButtonVariant.Danger);
            _deleteBtn.Pressed += DoDelete;
            toolbar.AddChild(_deleteBtn);
            root.AddChild(toolbar);
        }

        private void OnSegmentChanged(int index)
        {
            if (_advancedHost != null) _advancedHost.Visible = index == 1;
        }

        // ── Item loading + browse ────────────────────────────────────────────────

        private void LoadItemsFromDir()
        {
            _items.Clear();
            string absDir = ProjectSettings.GlobalizePath(_itemsDir);
            if (!Directory.Exists(absDir)) return;
            foreach (string file in System.IO.Directory.GetFiles(absDir, "*.json"))
            {
                try
                {
                    var def = JsonSerializer.Deserialize<ItemDefinition>(File.ReadAllText(file), ContentJson.Options);
                    if (def != null) _items.Add(def);
                }
                catch (JsonException) { /* skip an unparseable file — it never entered the editable set */ }
            }
            _items.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            if (_index >= _items.Count) _index = System.Math.Max(0, _items.Count - 1);
        }

        private void Browse(int dir)
        {
            if (_items.Count == 0) return;
            _index = (_index + dir + _items.Count) % _items.Count;
            Refresh();
        }

        /// <summary>Rebuild the form for the current item (or the empty state).</summary>
        public void Refresh()
        {
            if (_items.Count == 0)
            {
                _current = null;
                ClearBody();
                _counterLabel.Text = "0/0";
                _statusLabel.Text = "No items — click New to author one.";
                UpdateToolbarEnabled();
                return;
            }
            if (_index >= _items.Count) _index = _items.Count - 1;
            Bind(_items[_index]);
            _counterLabel.Text = $"{_index + 1}/{_items.Count}";
        }

        private void Bind(ItemDefinition def)
        {
            _current = def;
            _originalId = def.Id;
            _preEditJson = ItemWriter.Serialize(def);
            ClearBody();
            BuildBody(def);
            Revalidate();
        }

        private void ClearBody()
        {
            foreach (Node c in _bodyHost.GetChildren()) { _bodyHost.RemoveChild(c); c.QueueFree(); }
            _badges.Clear();
            _advancedHost = null;
            _jsonPane = null;
            _paneDirty = false;
        }
    }
}
