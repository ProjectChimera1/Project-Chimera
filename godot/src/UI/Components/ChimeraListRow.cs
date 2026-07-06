#nullable enable
using Godot;
using ProjectChimera.UI.Theme;

namespace ProjectChimera.UI.Components
{
    /// <summary>
    /// list-row (UX-DR25) as a thin stateful component (Story 3.1b D-2): a surface-1 inset row (cut 5)
    /// that hovers to surface-2, selects to an accent ring + accent-wash (shared, registered → retints on
    /// an accent switch), and locks to 0.6 opacity + non-interactive. Rows in a <see cref="ListRowGroup"/>
    /// are single-select. Add content via <see cref="Content"/>; the <see cref="Create"/> convenience adds
    /// a ui-font label.
    ///
    /// Presentation layer. Reads all styling through the <see cref="ChimeraComponents"/> factory (same
    /// assembly), so a single main.tres is the source of truth.
    /// </summary>
    public partial class ChimeraListRow : PanelContainer
    {
        /// <summary>Emitted when the row becomes selected (by click or group selection).</summary>
        [Signal]
        public delegate void SelectedEventHandler();

        private StyleBoxFlat _normal = null!;
        private StyleBoxFlat _hover = null!;
        private StyleBoxFlat _selectedBox = null!;
        private bool _isSelected;
        private bool _isLocked;
        private ListRowGroup? _group;

        /// <summary>The row's content container — add labels/icons/chips here.</summary>
        public HBoxContainer Content { get; private set; } = null!;

        /// <summary>Whether the row is currently selected.</summary>
        public bool IsSelected => _isSelected;

        /// <summary>Build a row with a single ui-font label. Optionally join a single-select group.</summary>
        public static ChimeraListRow Create(string text, ListRowGroup? group = null)
        {
            var row = new ChimeraListRow();
            row.Build(group);
            var lbl = new Label { Text = text };
            lbl.AddThemeFontOverride("font", ChimeraComponents.FontOf(ThemeTokens.FontUi));
            lbl.AddThemeFontSizeOverride("font_size", ChimeraComponents.SizeOf(ThemeTokens.Tsm));
            lbl.AddThemeColorOverride("font_color", ChimeraComponents.Col(ThemeTokens.TextMid));
            lbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.Content.AddChild(lbl);
            return row;
        }

        private void Build(ListRowGroup? group)
        {
            _group = group;
            int cut = ChimeraComponents.Const(ThemeTokens.CutSm);
            int padH = ChimeraComponents.Const(ThemeTokens.S3);
            int padV = ChimeraComponents.Const(ThemeTokens.S2);

            _normal = ChimeraStyleBox.Chamfer(cut, ChimeraComponents.Col(ThemeTokens.Surface1), ChimeraComponents.Col(ThemeTokens.Line));
            _normal.WithContentMargins(padH, padV);
            _hover = ChimeraStyleBox.Chamfer(cut, ChimeraComponents.Col(ThemeTokens.Surface2), ChimeraComponents.Col(ThemeTokens.Line));
            _hover.WithContentMargins(padH, padV);

            // Shared, registered selected box — accent ring + accent-wash. One box for ALL rows.
            _selectedBox = ChimeraComponents.SharedAccentBox("listrow/selected", () =>
            {
                var b = ChimeraStyleBox.Chamfer(cut, ChimeraComponents.Col(ThemeTokens.AccentWash), ChimeraComponents.Col(ThemeTokens.Accent), 2);
                b.WithContentMargins(padH, padV);
                return b;
            }, ChimeraComponents.Fill(ThemeTokens.AccentWash), ChimeraComponents.Border(ThemeTokens.Accent));

            AddThemeStyleboxOverride("panel", _normal);
            MouseFilter = MouseFilterEnum.Stop;

            Content = new HBoxContainer();
            Content.AddThemeConstantOverride("separation", padV);
            AddChild(Content);

            MouseEntered += OnMouseEntered;
            MouseExited += OnMouseExited;
        }

        /// <inheritdoc/>
        public override void _GuiInput(InputEvent @event)
        {
            if (_isLocked) return;
            if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                if (_group != null) _group.Select(this);
                else SetSelected(!_isSelected);
                EmitSignal(SignalName.Selected);
                AcceptEvent();
            }
        }

        private void OnMouseEntered()
        {
            if (!_isSelected && !_isLocked) AddThemeStyleboxOverride("panel", _hover);
        }

        private void OnMouseExited()
        {
            if (!_isSelected && !_isLocked) AddThemeStyleboxOverride("panel", _normal);
        }

        /// <summary>
        /// Set the selected state (accent ring + wash). When the row belongs to a <see cref="ListRowGroup"/>
        /// this routes through the group so the single-select invariant is preserved (selecting deselects
        /// the previous row; deselecting clears the group). Standalone rows apply the state directly.
        /// </summary>
        public void SetSelected(bool selected)
        {
            if (_group != null)
            {
                if (selected) _group.Select(this);
                else _group.Deselect(this);
                return;
            }
            ApplySelected(selected);
        }

        // Raw state + stylebox change with NO group interaction — the group calls this (via
        // SetSelectedFromGroup) to apply selection without re-entering its own Select/Deselect (no recursion).
        private void ApplySelected(bool selected)
        {
            _isSelected = selected;
            AddThemeStyleboxOverride("panel", selected ? _selectedBox : _normal);
        }

        /// <summary>Lock the row: 0.6 opacity + non-interactive (mouse ignored). UX-DR25 is-locked.</summary>
        public void SetLocked(bool locked)
        {
            _isLocked = locked;
            Modulate = new Color(1, 1, 1, locked ? 0.6f : 1f);
            MouseFilter = locked ? MouseFilterEnum.Ignore : MouseFilterEnum.Stop;
        }

        // Called by the owning group to flip selection state without re-entering group logic.
        internal void SetSelectedFromGroup(bool selected) => ApplySelected(selected);
    }

    /// <summary>
    /// A single-select scope for <see cref="ChimeraListRow"/>s: selecting one deselects the previously
    /// selected row. Plain C# (not a Node) — holds only the current selection, guarded for freed rows.
    /// </summary>
    public sealed class ListRowGroup
    {
        private ChimeraListRow? _selected;

        /// <summary>The currently selected row in this group (null if none).</summary>
        public ChimeraListRow? Selected => _selected;

        /// <summary>Select <paramref name="row"/>, deselecting the previous selection.</summary>
        public void Select(ChimeraListRow row)
        {
            if (_selected == row) return;
            if (_selected != null && GodotObject.IsInstanceValid(_selected))
                _selected.SetSelectedFromGroup(false);
            _selected = row;
            row.SetSelectedFromGroup(true);
        }

        /// <summary>Deselect <paramref name="row"/> if it is the current selection (clears the group).</summary>
        public void Deselect(ChimeraListRow row)
        {
            if (_selected != row) return;
            if (GodotObject.IsInstanceValid(_selected))
                _selected.SetSelectedFromGroup(false);
            _selected = null;
        }
    }
}
