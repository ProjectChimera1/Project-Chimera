#nullable enable
using System.Collections.Generic;
using System.Text;
using Godot;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Effects;
using ProjectChimera.UI.Components;
using ProjectChimera.UI.Theme;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 11.5 (FR-74) — the bottom-bar multi-select readout: a WC3 type-grouped, health-tinted selection grid, a
    /// <see cref="ChimeraTabs"/> subgroup strip, and the focus unit's buff/debuff icon row. A PURE presentation layer
    /// over the untouched 30 Hz sim — every value is read from an already-populated array
    /// (<see cref="SelectionSystem.Subgroups"/>/<c>FocusId</c>, <see cref="EntityWorld"/> health/status,
    /// <see cref="ModifierStore"/> per-slot detail). It writes nothing to the sim, folds nothing, and re-baselines no
    /// golden — <c>SimChecksum</c> is byte-identical with the panel on vs off.
    ///
    /// <para>Composed from the 3.1x kit (built after <c>MatchAlertPhase.EnsureKitInitialized</c>). Node children rebuild
    /// only when a cheap structural signature changes; per-frame it refreshes health tints and remaining-duration text
    /// off stored references, so a live match does not churn the whole tree every tick.</para>
    /// </summary>
    public sealed partial class SelectionSubgroupPanel : Node
    {
        // ── Deps (injected by MatchAlertPhase) ──
        private SelectionSystem _selection = null!;
        private EntityWorld     _world     = null!;
        private ModifierStore   _modifiers = null!;

        // ── UI tree (built once in Initialize) ──
        private PanelContainer _root   = null!;   // bottom-center backing panel (whole-panel visibility gate)
        private HBoxContainer  _tabRow  = null!;   // holds the ChimeraTabs subgroup strip (2+ subgroups)
        private GridContainer  _grid    = null!;   // health-tinted type-grouped cells (2+ own units)
        private HBoxContainer  _buffRow  = null!;   // focus unit's status + stat/DoT icons

        private ChimeraTabs? _tabs;                 // rebuilt when the subgroup structure changes

        // ── Per-frame refresh references (avoids node churn) ──
        private readonly List<(Panel cell, StyleBoxFlat style, int id)> _cells = new(); // health tint refresh
        private readonly List<(int slot, Label label)> _durations = new();              // remaining-duration refresh
        private int _durationFocus = -1;

        private string _gridSig = "\0";  // structural signature of the grid + tab strip
        private string _buffSig = "\0";  // structural signature of the buff row

        private const int MAX_CELLS = 48; // cap the grid so a huge box-select cannot spawn unbounded nodes

        /// <summary>Wire the presentation reads and build the (initially hidden) UI under <paramref name="uiCanvas"/>.</summary>
        public void Initialize(SelectionSystem selection, EntityWorld world, ModifierStore modifiers, CanvasLayer uiCanvas)
        {
            _selection = selection;
            _world     = world;
            _modifiers = modifiers;
            BuildUi(uiCanvas);
        }

        private void BuildUi(CanvasLayer uiCanvas)
        {
            _root = ChimeraComponents.Panel(ChimeraComponents.PanelVariant.Surface2);
            // Anchor bottom-centre, grow upward; content-sized.
            _root.AnchorLeft = 0.5f; _root.AnchorRight = 0.5f;
            _root.AnchorTop = 1f;    _root.AnchorBottom = 1f;
            _root.GrowHorizontal = Control.GrowDirection.Both;
            _root.GrowVertical   = Control.GrowDirection.Begin;
            _root.OffsetBottom   = -96f; // sit above the command card / HUD bottom edge
            _root.MouseFilter    = Control.MouseFilterEnum.Ignore; // do not steal box-select / command clicks over the map
            _root.Visible        = false;

            var vbox = new VBoxContainer();
            vbox.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            _root.AddChild(vbox);

            _buffRow = new HBoxContainer();
            _buffRow.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S1));
            vbox.AddChild(_buffRow);

            _tabRow = new HBoxContainer();
            _tabRow.Visible = false;
            vbox.AddChild(_tabRow);

            _grid = new GridContainer { Columns = 8 };
            _grid.AddThemeConstantOverride("h_separation", ChimeraComponents.Const(ThemeTokens.S1));
            _grid.AddThemeConstantOverride("v_separation", ChimeraComponents.Const(ThemeTokens.S1));
            _grid.Visible = false;
            vbox.AddChild(_grid);

            uiCanvas.AddChild(_root);
        }

        // ── Per-frame refresh (drained by MainScene in the presentation tail) ──────────────────────────────────────

        /// <summary>Refresh the panel from the live selection. Cheap when nothing changed structurally.</summary>
        public void Update()
        {
            if (_root == null) return;

            int selCount = _selection.SelectedIds.Count;
            int focus    = _selection.FocusId;

            // Whole panel hidden on an empty or building-only selection (SelectedIds is own-units-only).
            if (selCount < 1)
            {
                if (_root.Visible) HideAll();
                return;
            }
            _root.Visible = true;

            RefreshGridAndTabs(selCount);
            RefreshBuffRow(focus);
        }

        private void HideAll()
        {
            _root.Visible = false;
            _grid.Visible = false;
            _tabRow.Visible = false;
            _gridSig = "\0";
            _buffSig = "\0";
            _cells.Clear();
            _durations.Clear();
        }

        // ── Grid + subgroup tabs ───────────────────────────────────────────────────────────────────────────────────

        private void RefreshGridAndTabs(int selCount)
        {
            IReadOnlyList<SelectionSubgroups.Subgroup> subs = _selection.Subgroups;
            int activeIdx = _selection.ActiveSubgroupIndex;

            // Structural signature (excludes health + active index so a tint change / Tab press does NOT rebuild nodes).
            string sig = BuildGridSignature(subs);
            if (sig != _gridSig)
            {
                _gridSig = sig;
                RebuildTabs(subs, activeIdx);
                RebuildGridCells(subs);
            }

            // Tabs visible only with 2+ distinct subgroups; grid visible with 2+ own units.
            _tabRow.Visible = subs.Count >= 2;
            _grid.Visible   = selCount >= 2;

            // Reflect the active subgroup in the strip (Tab key / death may have moved it) without a redundant emit.
            if (_tabs != null && activeIdx >= 0 && activeIdx < subs.Count && _tabs.Active != activeIdx)
                _tabs.SetActive(activeIdx);

            // Live health tint per cell (green→red on current/EffectiveMaxHealth).
            foreach (var (cell, style, id) in _cells)
            {
                if (!_world.IsAlive(id)) continue;
                style.BgColor = HealthTint(id);
                cell.AddThemeStyleboxOverride("panel", style);
            }
        }

        private string BuildGridSignature(IReadOnlyList<SelectionSubgroups.Subgroup> subs)
        {
            var sb = new StringBuilder();
            for (int g = 0; g < subs.Count; g++)
            {
                sb.Append(subs[g].Key).Append(':');
                var members = subs[g].Members;
                for (int i = 0; i < members.Count; i++)
                    sb.Append(members[i]).Append(',');
                sb.Append('|');
            }
            return sb.ToString();
        }

        private void RebuildTabs(IReadOnlyList<SelectionSubgroups.Subgroup> subs, int activeIdx)
        {
            // Free the previous strip.
            if (_tabs != null && GodotObject.IsInstanceValid(_tabs)) _tabs.QueueFree();
            _tabs = null;
            foreach (Node c in _tabRow.GetChildren()) c.QueueFree();

            if (subs.Count < 2) return;

            var labels = new string[subs.Count];
            for (int g = 0; g < subs.Count; g++)
                labels[g] = $"{TypeLabel(subs[g])} {subs[g].Members.Count}";

            var tabs = ChimeraTabs.Create(ChimeraComponents.TabsVariant.Segment, labels);
            // Connect AFTER Create (the construction-time SetActive(0) emit is swallowed — no listener yet).
            tabs.TabChanged += OnTabChanged;
            _tabRow.AddChild(tabs);
            _tabs = tabs;
            if (activeIdx >= 0 && activeIdx < subs.Count && activeIdx != tabs.Active)
                tabs.SetActive(activeIdx);
        }

        private void OnTabChanged(int index) => _selection.SetActiveSubgroup(index);

        private void RebuildGridCells(IReadOnlyList<SelectionSubgroups.Subgroup> subs)
        {
            foreach (Node c in _grid.GetChildren()) c.QueueFree();
            _cells.Clear();

            int shown = 0;      // cells actually built (capped at MAX_CELLS)
            int aliveTotal = 0; // alive members across all subgroups (uncapped — for the overflow marker)
            for (int g = 0; g < subs.Count; g++)
            {
                var members = subs[g].Members;
                for (int i = 0; i < members.Count; i++)
                {
                    int id = members[i];
                    if (!_world.IsAlive(id)) continue;
                    aliveTotal++;
                    if (shown >= MAX_CELLS) continue; // keep counting so the "+N" marker is exact
                    var (cell, style) = BuildCell(id);
                    _grid.AddChild(cell);
                    _cells.Add((cell, style, id));
                    shown++;
                }
            }

            // Review #6: when the grid truncated, show a "+N" overflow cell so the cell count never contradicts the
            // tab label's full member count.
            if (aliveTotal > shown)
                _grid.AddChild(BuildOverflowCell(aliveTotal - shown));
        }

        private Control BuildOverflowCell(int overflow)
        {
            var style = new StyleBoxFlat
            {
                BgColor = ChimeraComponents.Col(ThemeTokens.Surface3),
                BorderColor = ChimeraComponents.Col(ThemeTokens.Line),
                BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            };
            var cell = new Panel { CustomMinimumSize = new Vector2(52, 40), FocusMode = Control.FocusModeEnum.None };
            cell.AddThemeStyleboxOverride("panel", style);
            var label = new Label
            {
                Text = $"+{overflow}",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            label.AddThemeColorOverride("font_color", ChimeraComponents.Col(ThemeTokens.TextHi));
            cell.AddChild(label);
            return cell;
        }

        private (Panel cell, StyleBoxFlat style) BuildCell(int id)
        {
            var style = new StyleBoxFlat
            {
                BgColor = HealthTint(id),
                BorderColor = ChimeraComponents.Col(ThemeTokens.Line),
                BorderWidthTop = 1, BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            };
            var cell = new Panel { CustomMinimumSize = new Vector2(52, 40), FocusMode = Control.FocusModeEnum.None }; // review #7
            cell.AddThemeStyleboxOverride("panel", style);

            var label = new Label
            {
                Text = TypeLabelOf(id),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            label.AddThemeFontSizeOverride("font_size", ChimeraComponents.SizeOf(ThemeTokens.T2xs));
            label.AddThemeColorOverride("font_color", new Color(0.05f, 0.05f, 0.05f)); // dark ink over the bright tint
            cell.AddChild(label);
            return (cell, style);
        }

        // ── Buff / debuff icon row (focus unit) ────────────────────────────────────────────────────────────────────

        private void RefreshBuffRow(int focus)
        {
            bool alive = focus >= 0 && _world.IsAlive(focus);
            string sig = alive ? BuildBuffSignature(focus) : "\0";
            if (sig != _buffSig)
            {
                _buffSig = sig;
                RebuildBuffRow(focus, alive);
            }

            // Refresh remaining-duration text every frame (excluded from the signature so it does not churn nodes).
            if (_durationFocus == focus && _world.IsAlive(focus))
            {
                foreach (var (slot, label) in _durations)
                {
                    if (slot >= _modifiers.CountAt(focus)) { label.Text = ""; continue; }
                    label.Text = DurationText(focus, slot);
                }
            }
        }

        private string BuildBuffSignature(int focus)
        {
            var sb = new StringBuilder();
            sb.Append(focus).Append('#');
            sb.Append((int)_world.StatusFlagsOf[focus]).Append('#');
            int count = _modifiers.CountAt(focus);
            for (int s = 0; s < count; s++)
            {
                Modifier? m = _modifiers.ModifierRefAt(focus, s);
                bool permanent = _modifiers.RemainingTicksAt(focus, s) == ModifierStore.PERMANENT;
                sb.Append(_modifiers.ModifierIdAt(focus, s)).Append('.')
                  .Append(_modifiers.StackCountAt(focus, s)).Append('.')
                  .Append(permanent ? 'P' : 'T').Append('.')
                  .Append(m == null ? 'p' : 'm').Append(',');
            }
            return sb.ToString();
        }

        private void RebuildBuffRow(int focus, bool alive)
        {
            foreach (Node c in _buffRow.GetChildren()) c.QueueFree();
            _durations.Clear();
            _durationFocus = focus;
            if (!alive) return;

            // 1. The five StatusFlags → one status icon each (source: StatusFlagsOf — never re-drawn from a modifier slot).
            // Glyphs are short BMP/ASCII tags: the default Godot theme font cannot render most emoji (tofu boxes).
            StatusFlags flags = _world.StatusFlagsOf[focus];
            AddStatusIcon(flags, StatusFlags.Stunned,      "STUN", "Stunned",      "This unit is stunned.");
            AddStatusIcon(flags, StatusFlags.Rooted,       "ROOT", "Rooted",       "This unit is rooted in place.");
            AddStatusIcon(flags, StatusFlags.Silenced,     "SIL",  "Silenced",     "This unit cannot cast abilities.");
            AddStatusIcon(flags, StatusFlags.Disarmed,     "DIS",  "Disarmed",     "This unit cannot attack.");
            AddStatusIcon(flags, StatusFlags.Invulnerable, "INV",  "Invulnerable", "This unit takes no damage.");

            // 2. Stat/DoT-HoT modifier slots → a polarity-tinted icon each (pure-status slots are shown above, skip them).
            int count = _modifiers.CountAt(focus);
            for (int s = 0; s < count; s++)
            {
                Modifier? m = _modifiers.ModifierRefAt(focus, s);
                if (m != null)
                {
                    bool hasDeltas = m.MaxHealthDelta.Raw != 0 || m.AttackDamageDelta.Raw != 0
                                  || m.MoveSpeedDelta.Raw != 0 || m.ArmorDelta.Raw != 0;
                    bool hasPeriod = ModifierPolarity.HasPeriod(m);
                    if (!hasDeltas && !hasPeriod) continue; // pure-status slot → already shown via StatusFlagsOf
                    AddModifierIcon(focus, s, m);
                }
                else if (_modifiers.PersistentRefAt(focus, s) != null)
                {
                    // A pure PersistentEffect DoT/HoT instance (no Modifier descriptor) — show a period icon.
                    AddPersistentIcon(focus, s);
                }
            }
        }

        private void AddStatusIcon(StatusFlags flags, StatusFlags bit, string glyph, string term, string body)
        {
            if ((flags & bit) == 0) return;
            var btn = ChimeraComponents.IconButton(glyph);
            btn.Disabled = true; // display-only
            btn.FocusMode = Control.FocusModeEnum.None; // review #7: never trap Tab away from SelectionSystem._UnhandledInput
            btn.AddThemeFontSizeOverride("font_size", ChimeraComponents.SizeOf(ThemeTokens.T2xs)); // short tag fits the 36px square
            btn.Modulate = new Color(1f, 0.55f, 0.45f); // debuff-ish tint (Invulnerable re-tinted below)
            if (bit == StatusFlags.Invulnerable) btn.Modulate = new Color(0.55f, 0.85f, 1f);
            ChimeraTooltip.Attach(btn, term, body);
            _buffRow.AddChild(btn);
        }

        private void AddModifierIcon(int focus, int slot, Modifier m)
        {
            ModifierPolarity.Polarity p = ModifierPolarity.Classify(m);
            var col = new VBoxContainer();

            var btn = ChimeraComponents.IconButton(ModifierPolarity.Glyph(m));
            btn.Disabled = true;
            btn.FocusMode = Control.FocusModeEnum.None; // review #7
            btn.AddThemeFontSizeOverride("font_size", ChimeraComponents.SizeOf(ThemeTokens.T2xs)); // "DoT"/"HoT"/marker fits
            btn.Modulate = PolarityTint(p);
            int stacks = _modifiers.StackCountAt(focus, slot);
            string tip = $"{PolarityWord(p)} modifier" + (stacks > 1 ? $" (x{stacks})" : "");
            ChimeraTooltip.Attach(btn, PolarityWord(p), tip);
            col.AddChild(btn);

            // Stack badge (>1) — one icon, one shared duration (stacks expire together).
            if (stacks > 1)
            {
                var badge = new Label { Text = $"x{stacks}", HorizontalAlignment = HorizontalAlignment.Center };
                badge.AddThemeFontSizeOverride("font_size", ChimeraComponents.SizeOf(ThemeTokens.T2xs));
                badge.AddThemeColorOverride("font_color", ChimeraComponents.Col(ThemeTokens.TextHi));
                col.AddChild(badge);
            }

            // Remaining-duration readout (suppressed for PERMANENT / aura-passive).
            var dur = new Label { HorizontalAlignment = HorizontalAlignment.Center, Text = DurationText(focus, slot) };
            dur.AddThemeFontSizeOverride("font_size", ChimeraComponents.SizeOf(ThemeTokens.T2xs));
            dur.AddThemeColorOverride("font_color", ChimeraComponents.Col(ThemeTokens.TextMid));
            col.AddChild(dur);
            _durations.Add((slot, dur));

            _buffRow.AddChild(col);
        }

        private void AddPersistentIcon(int focus, int slot)
        {
            // A bare PersistentEffect DoT/HoT (no Modifier descriptor): derive polarity from its own period effect —
            // damage → red "DoT" (debuff), heal → green "HoT" (buff). Never assume beneficial (review #1).
            PersistentEffect? pe = _modifiers.PersistentRefAt(focus, slot);
            int sign = pe != null ? ModifierPolarity.PeriodSign(pe.PeriodEffect) : 0;
            bool damaging = sign < 0;
            var btn = ChimeraComponents.IconButton(damaging ? "DoT" : "HoT");
            btn.Disabled = true;
            btn.FocusMode = Control.FocusModeEnum.None; // review #7
            btn.AddThemeFontSizeOverride("font_size", ChimeraComponents.SizeOf(ThemeTokens.T2xs));
            btn.Modulate = damaging ? new Color(1f, 0.55f, 0.45f) : new Color(0.6f, 0.9f, 0.6f);
            ChimeraTooltip.Attach(btn, damaging ? "Damage over time" : "Heal over time",
                                  damaging ? "This unit is taking periodic damage." : "This unit is being periodically healed.");
            _buffRow.AddChild(btn);
        }

        // ── Helpers ────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Green→red health tint on current/<c>EffectiveMaxHealth</c> (matches the floating HP bar).</summary>
        private Color HealthTint(int id)
        {
            float maxHp = _world.EffectiveMaxHealth[id].ToFloat();
            float curHp = _world.Health[id].ToFloat();
            float ratio = maxHp > 0f ? Mathf.Clamp(curHp / maxHp, 0f, 1f) : 0f;
            return ratio > 0.5f
                ? new Color(1f - (ratio - 0.5f) * 2f, 1f, 0f)
                : new Color(1f, ratio * 2f, 0f);
        }

        /// <summary>Remaining-duration readout in seconds (30 ticks/s), or "" for a PERMANENT (aura/passive) slot.</summary>
        private string DurationText(int focus, int slot)
        {
            int remaining = _modifiers.RemainingTicksAt(focus, slot);
            if (remaining == ModifierStore.PERMANENT) return ""; // aura/passive → no timer
            if (remaining <= 0) return "";
            int secs = (int)(((long)remaining + 29) / 30); // ceil to whole seconds (long guards a near-int.MaxValue duration)
            return $"{secs}s";
        }

        private static Color PolarityTint(ModifierPolarity.Polarity p) => p switch
        {
            ModifierPolarity.Polarity.Buff   => new Color(0.55f, 0.95f, 0.60f),
            ModifierPolarity.Polarity.Debuff => new Color(1f, 0.55f, 0.45f),
            _ => new Color(0.8f, 0.8f, 0.8f),
        };

        private static string PolarityWord(ModifierPolarity.Polarity p) => p switch
        {
            ModifierPolarity.Polarity.Buff   => "Buff",
            ModifierPolarity.Polarity.Debuff => "Debuff",
            _ => "Effect",
        };

        /// <summary>A subgroup's per-type label (from any live member's DisplayName / category).</summary>
        private string TypeLabel(SelectionSubgroups.Subgroup sub)
        {
            var members = sub.Members;
            for (int i = 0; i < members.Count; i++)
                if (_world.IsAlive(members[i])) return TypeLabelOf(members[i]);
            return "Unit";
        }

        /// <summary>An entity's type label — its <see cref="UnitDefinition.DisplayName"/> if present, else its category.</summary>
        private string TypeLabelOf(int id)
        {
            UnitDefinition def = _world.SourceDefinition[id];
            if (def != null && !string.IsNullOrEmpty(def.DisplayName)) return def.DisplayName;
            return _world.CategoryOf[id].ToString();
        }
    }
}
