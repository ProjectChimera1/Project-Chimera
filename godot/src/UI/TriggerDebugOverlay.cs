#nullable enable
using System;
using System.Collections.Generic;
using Godot;
using ProjectChimera.Core;               // Fixed
using ProjectChimera.Core.Definitions;   // ScenarioData, TriggerDefinition
using ProjectChimera.Core.Sim;            // DslVarReadback, TriggerFireLog, TriggerEnabledStore
using ProjectChimera.Dsl;                 // DslValueType, VarScope
using ProjectChimera.UI.Components;        // ChimeraComponents
using ProjectChimera.UI.Theme;             // ThemeTokens, ThemeBuilder, AccentController
using GodotTheme = Godot.Theme;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 7.15 — the in-match trigger-debugging overlay. A code-built <see cref="CanvasLayer"/> (toggle key,
    /// Play-scoped) that reads four PRESENTATION-only data streams, NONE of which perturb <c>SimChecksum</c>:
    /// <list type="bullet">
    /// <item>a live <b>variable watch</b> off the version-stamped <see cref="DslVarReadback"/> read rail (re-format a
    /// row only on a version bump — the <c>CustomUiBridge</c> idiom);</item>
    /// <item>a tick-stamped <b>fired-trigger log</b> (last N, newest-first) fed by the non-folded
    /// <see cref="TriggerFireLog"/> ring;</item>
    /// <item>per-trigger <b>fire counters</b> from the same <see cref="TriggerFireLog"/>;</item>
    /// <item>each trigger's <b>enabled state</b> read directly from the already-folded
    /// <see cref="TriggerEnabledStore"/> (a pure read — presentation NEVER writes it).</item>
    /// </list>
    /// Plus a filter/search box over the variable + trigger rows, and click-to-navigate from a fired-log entry into
    /// the flat trigger editor.
    ///
    /// <para><b>Presentation-only.</b> This overlay performs ZERO sim writes: opening/closing/filtering/navigating
    /// never mutates sim state, and the <see cref="TriggerFireLog"/> it reads is written UNCONDITIONALLY at the sim
    /// fire site whether or not this overlay exists — so a run with the overlay open is byte-identical to one with it
    /// closed. Late-bound <c>() =&gt; _ctx.Scenario</c> getters make it survive the F5 Edit→Play re-apply (rows rebuild
    /// whenever the live scenario reference changes; fire counts/log reset with the sim).</para>
    /// </summary>
    public partial class TriggerDebugOverlay : CanvasLayer
    {
        private const float PANEL_W       = 380f;
        private const int   LOG_DISPLAY_CAP = 40; // last-N fired entries rendered (freed oldest-first past this)

        private GodotTheme        _theme  = null!;

        // ── Deps (late-bound; wired by the phase) ──
        private Func<DslVarReadback?>?      _readbackGetter;
        private Func<TriggerFireLog?>?      _fireLogGetter;
        private Func<TriggerEnabledStore?>? _enabledGetter;
        private Func<ScenarioData?>?        _scenarioGetter;
        private Func<int>?                  _localFactionGetter;
        private Action<int>?                _navigate;

        // ── Chrome (built once) ──
        private LineEdit      _filterBox   = null!;
        private VBoxContainer _watchHost   = null!;
        private VBoxContainer _logHost     = null!;
        private VBoxContainer _triggerHost = null!;

        private string _filter = string.Empty;

        // Rebuilt whenever the live scenario reference changes (F5 re-apply / a different map), the declared row
        // COUNT drifts (an in-place declaration edit that keeps the same ScenarioData reference), or the local
        // faction changes (per-player watch values are slot-specific).
        private ScenarioData? _boundScenario;
        private int _lastFaction = -1; // forces a rebuild on the first Update
        private readonly List<WatchRow> _watchRows = new();
        private readonly List<TrigRow>  _trigRows  = new();
        private long _lastSeenTotal; // TriggerFireLog.TotalRecorded high-water — append/reset detection for the log
        private int  _lastGen = -1;  // TriggerFireLog.Generation — detects an F5 reset even when total lands on the old high-water
        // Signatures of the declared-var (name+scope+array) and authored-trigger (name) SEQUENCES. Rebuild the rows
        // when either changes even if the COUNT and the ScenarioData reference do not — an in-place rename/reorder
        // (ResetToAuthoredStart re-applies the same object) would otherwise leave the positional value↔name zip
        // pointing a variable's live value at the wrong name.
        private int _watchSig;
        private int _trigSig;

        private sealed class WatchRow
        {
            public string  Name = "";
            public Control Container = null!;
            public Label   Value = null!;
            public uint    LastVersion;
            public bool    HasVersion;
        }

        private sealed class TrigRow
        {
            public int     ExecIdx;
            public string  Name = "";
            public Control Container = null!;
            public Label   Count = null!;
            public Label   Enabled = null!;
            public int     LastCount = -1;
            public int     LastEnabled = -1; // -1 unseen, 0 disabled, 1 enabled
        }

        public override void _Ready()
        {
            _theme = ChimeraComponents.EnsureInitialized(this);
            BuildChrome();
            Visible = false;
        }

        /// <summary>Wire the read sources + late-bound getters. Called by the phase after AddChild.</summary>
        public void Initialize(
            Func<DslVarReadback?> readbackGetter,
            Func<TriggerFireLog?> fireLogGetter,
            Func<TriggerEnabledStore?> enabledGetter,
            Func<ScenarioData?> scenarioGetter,
            Func<int> localFactionGetter,
            Action<int> navigate)
        {
            _readbackGetter     = readbackGetter;
            _fireLogGetter      = fireLogGetter;
            _enabledGetter      = enabledGetter;
            _scenarioGetter     = scenarioGetter;
            _localFactionGetter = localFactionGetter;
            _navigate           = navigate;
        }

        private void BuildChrome()
        {
            Layer = 22; // above the quest log (21) — a developer diagnostic overlay

            // Top-left anchored panel (leaves the top-right quest log clear).
            var margin = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Pass };
            margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
            margin.AddThemeConstantOverride("margin_top", 16);
            margin.AddThemeConstantOverride("margin_left", 16);
            AddChild(margin);

            var panel = ChimeraComponents.Panel(ChimeraComponents.PanelVariant.Default);
            panel.CustomMinimumSize = new Vector2(PANEL_W, 0);
            panel.Theme = _theme;
            margin.AddChild(panel);

            var root = new VBoxContainer();
            root.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S3));
            panel.AddChild(root);

            root.AddChild(ChimeraComponents.Heading("Trigger Debug", ThemeTokens.Tlg));

            // ── Filter/search ──
            _filterBox = ChimeraComponents.Input("filter by name…");
            _filterBox.CustomMinimumSize = new Vector2(PANEL_W - 24f, 0);
            _filterBox.TextChanged += OnFilterChanged;
            root.AddChild(_filterBox);

            // ── Variable watch ──
            root.AddChild(ChimeraComponents.FieldLabel("Variable Watch"));
            _watchHost = new VBoxContainer();
            _watchHost.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S1));
            root.AddChild(_watchHost);

            // ── Triggers (fire counters + enabled state) ──
            root.AddChild(ChimeraComponents.FieldLabel("Triggers (fires · state)"));
            _triggerHost = new VBoxContainer();
            _triggerHost.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S1));
            root.AddChild(_triggerHost);

            // ── Fired-trigger log (last N, newest-first, click to navigate) ──
            root.AddChild(ChimeraComponents.FieldLabel("Fired Log (click → edit)"));
            var logScroll = new ScrollContainer();
            logScroll.CustomMinimumSize = new Vector2(PANEL_W - 24f, 160f);
            logScroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
            root.AddChild(logScroll);
            _logHost = new VBoxContainer();
            _logHost.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _logHost.AddThemeConstantOverride("separation", 2);
            logScroll.AddChild(_logHost);
        }

        /// <summary>Toggle in-match visibility (the debug key). No sim write — checksum byte-identical.</summary>
        public void Toggle() => Visible = !Visible;

        /// <summary>Hide the overlay (used when click-to-navigate leaves for the trigger editor, so the diagnostic
        /// panel does not linger over the editor). No sim write.</summary>
        public void Close() => Visible = false;

        private void OnFilterChanged(string text)
        {
            _filter = text.Trim().ToLowerInvariant();
            ApplyFilter();
        }

        private bool Matches(string name) =>
            _filter.Length == 0 || name.ToLowerInvariant().Contains(_filter);

        private void ApplyFilter()
        {
            foreach (WatchRow r in _watchRows) r.Container.Visible = Matches(r.Name);
            foreach (TrigRow r in _trigRows)   r.Container.Visible = Matches(r.Name);
        }

        /// <summary>
        /// Per-frame pump (called from <c>MainScene._Process</c>). Rebuilds rows when the live scenario changes
        /// (F5 re-apply), then refreshes the variable watch (version-gated), the per-trigger fire counters + enabled
        /// state, and the fired-trigger log (append newly-observed fires; reset the log when the sim resets). All
        /// reads — never a sim write.
        /// </summary>
        public void Update()
        {
            // Pumped every frame by MainScene._Process regardless of visibility — but it is a developer diagnostic,
            // so do zero work (no enumeration / allocation / node churn) while closed. When next shown, the
            // scenario-ref check rebuilds rows and RefreshLog rebuilds the fired-log from the ring (any fires that
            // accumulated while hidden are picked up via the TotalRecorded high-water).
            if (!Visible) return;

            ScenarioData? scenario = _scenarioGetter?.Invoke();
            DslVarReadback? readback = _readbackGetter?.Invoke();
            int faction = _localFactionGetter?.Invoke() ?? 0;
            // Enumerate once per frame and share the snapshot with RebuildRows/RefreshWatch (avoids a second pass).
            List<DslVarReadback.WatchVar>? vars = readback?.Enumerate(faction);

            // Rebuild rows on a scenario-reference change (F5 re-apply / different map), a declared-row COUNT drift
            // (an in-place declaration edit that keeps the same ScenarioData reference — ResetToAuthoredStart
            // re-applies the same object, so reference identity alone would miss it), or a local-faction change
            // (per-player watch values are slot-specific and must be re-read for the new slot).
            int varCount = vars?.Count ?? 0;
            // Also rebuild on an identity (not just count) drift: a same-count in-place rename/reorder keeps the
            // reference and the counts but shifts which name each positional slot carries.
            int watchSig = ComputeWatchSig(vars);
            int trigSig  = ComputeTrigSig(scenario);
            if (!ReferenceEquals(scenario, _boundScenario)
                || varCount != _watchRows.Count
                || TriggerCount() != _trigRows.Count
                || faction != _lastFaction
                || watchSig != _watchSig
                || trigSig != _trigSig)
            {
                RebuildRows(scenario, vars, faction);
            }
            _watchSig = watchSig;
            _trigSig  = trigSig;

            RefreshWatch(vars);
            RefreshTriggers();
            RefreshLog();
        }

        private void RebuildRows(ScenarioData? scenario, List<DslVarReadback.WatchVar>? vars, int faction)
        {
            _boundScenario = scenario;
            _lastFaction   = faction;

            // ── Variable watch rows (from the read rail's declared set) ──
            foreach (Node c in _watchHost.GetChildren()) c.QueueFree();
            _watchRows.Clear();
            if (vars != null)
            {
                foreach (DslVarReadback.WatchVar v in vars)
                {
                    var container = new HBoxContainer();
                    container.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));

                    var nameLabel = new Label
                    {
                        Text = $"{v.Name} [{ScopeTag(v)}]",
                        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                        ClipText = true,
                    };
                    nameLabel.AddThemeColorOverride("font_color", _theme.GetColor(ThemeTokens.TextMid, ThemeTokens.Type));
                    container.AddChild(nameLabel);

                    var value = new Label { Text = FormatValue(v) };
                    value.AddThemeColorOverride("font_color", _theme.GetColor(ThemeTokens.TextHi, ThemeTokens.Type));
                    container.AddChild(value);

                    _watchHost.AddChild(container);
                    _watchRows.Add(new WatchRow
                    {
                        Name = v.Name, Container = container, Value = value,
                        LastVersion = v.Version, HasVersion = true,
                    });
                }
            }

            // ── Trigger rows (one per exec/authored trigger; indexed by exec idx) ──
            foreach (Node c in _triggerHost.GetChildren()) c.QueueFree();
            _trigRows.Clear();
            int trigCount = TriggerCount();
            for (int i = 0; i < trigCount; i++)
            {
                var container = new HBoxContainer();
                container.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));

                int authored = AuthoredIndex(i);
                string name = TriggerName(scenario, authored);
                var nameLabel = new Label
                {
                    Text = $"{authored}: {name}",
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    ClipText = true,
                };
                nameLabel.AddThemeColorOverride("font_color", _theme.GetColor(ThemeTokens.TextMid, ThemeTokens.Type));
                container.AddChild(nameLabel);

                var count = new Label { Text = "0" };
                count.AddThemeColorOverride("font_color", _theme.GetColor(ThemeTokens.TextHi, ThemeTokens.Type));
                container.AddChild(count);

                var enabled = new Label { Text = "on" };
                container.AddChild(enabled);

                _triggerHost.AddChild(container);
                _trigRows.Add(new TrigRow
                {
                    ExecIdx = i, Name = name, Container = container,
                    Count = count, Enabled = enabled,
                });
            }

            // ── Fired log — reset the display + high-water on a scenario swap ──
            foreach (Node c in _logHost.GetChildren()) c.QueueFree();
            _lastSeenTotal = 0;

            ApplyFilter();
        }

        private void RefreshWatch(List<DslVarReadback.WatchVar>? vars)
        {
            if (vars == null || _watchRows.Count == 0) return;

            // Positional zip: after a rebuild the row list and this frame's enumeration share order/count (Update
            // rebuilds on any count/faction/scenario drift), so index alignment holds; guard the count defensively.
            int n = Math.Min(vars.Count, _watchRows.Count);
            for (int i = 0; i < n; i++)
            {
                WatchRow row = _watchRows[i];
                DslVarReadback.WatchVar v = vars[i];
                if (row.HasVersion && v.Version == row.LastVersion) continue; // CustomUiBridge short-circuit
                row.LastVersion = v.Version;
                row.HasVersion = true;
                row.Value.Text = FormatValue(v);
            }
        }

        private void RefreshTriggers()
        {
            TriggerFireLog? fireLog = _fireLogGetter?.Invoke();
            TriggerEnabledStore? enabled = _enabledGetter?.Invoke();
            foreach (TrigRow row in _trigRows)
            {
                int count = fireLog?.Count(row.ExecIdx) ?? 0;
                if (count != row.LastCount)
                {
                    row.LastCount = count;
                    row.Count.Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                int en = (enabled?.IsEnabled(row.ExecIdx) ?? true) ? 1 : 0;
                if (en != row.LastEnabled)
                {
                    row.LastEnabled = en;
                    row.Enabled.Text = en == 1 ? "on" : "off";
                    row.Enabled.AddThemeColorOverride("font_color",
                        _theme.GetColor(en == 1 ? ThemeTokens.Ok : ThemeTokens.TextLo, ThemeTokens.Type));
                }
            }
        }

        private void RefreshLog()
        {
            TriggerFireLog? fireLog = _fireLogGetter?.Invoke();
            if (fireLog == null) return;

            // Detect a sim reset (F5 re-apply / scenario clear) via the fire log's GENERATION, not the fire total:
            // after a reset the post-reset total can climb straight back to the pre-reset high-water within a single
            // frame (an F5 of a match_start-heavy scenario), which the total == _lastSeenTotal short-circuit below
            // would misread as "nothing new" and leave stale pre-reset rows on screen. A generation change forces a
            // full re-sync regardless of where the total landed.
            int gen = fireLog.Generation;
            bool reset = gen != _lastGen;
            if (reset) { _lastGen = gen; _lastSeenTotal = 0; }

            long total = fireLog.TotalRecorded;
            if (!reset && total == _lastSeenTotal) return; // nothing new since last frame

            // Rebuild the newest-first display from the ring (cheap: only runs on a fire or a reset). RemoveChild
            // before QueueFree so the freed rows leave the container immediately — QueueFree is deferred to the end
            // of the frame, so without the RemoveChild the old + new sets would both be children for this frame.
            foreach (Node c in _logHost.GetChildren()) { _logHost.RemoveChild(c); c.QueueFree(); }

            ScenarioData? scenario = _scenarioGetter?.Invoke();
            int shown = Math.Min(fireLog.RecentCount, LOG_DISPLAY_CAP);
            for (int i = 0; i < shown; i++)
            {
                TriggerFireLog.FireEntry e = fireLog.Recent(i); // newest-first
                int authored = fireLog.AuthoredIndex(e.ExecIdx); // exec order ≠ authored order under non-default priority
                string name = TriggerName(scenario, authored);

                var btn = new Button
                {
                    Text = $"t{e.Tick}  #{authored} {name}",
                    Flat = true,
                    Alignment = HorizontalAlignment.Left,
                    ClipText = true,
                };
                btn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                btn.Pressed += () => _navigate?.Invoke(authored);
                _logHost.AddChild(btn);
            }

            _lastSeenTotal = total;
        }

        // ── Formatting (presentation-side only — no strings enter the tick) ──

        private static string ScopeTag(DslVarReadback.WatchVar v)
        {
            if (v.IsArray) return "arr";
            return v.Scope == VarScope.PerPlayer ? "pp" : "g";
        }

        private static string FormatValue(DslVarReadback.WatchVar v)
        {
            if (v.IsArray) return $"[{v.ArrayCount}]";
            switch (v.Type)
            {
                case DslValueType.Bool:  return v.Raw0 != 0 ? "true" : "false";
                case DslValueType.Fixed: return Fixed.FromRaw(v.Raw0).ToString();
                case DslValueType.Point: return $"({Fixed.FromRaw(v.Raw0)}, {Fixed.FromRaw(v.Raw1)})";
                default:                 return v.Raw0.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        // ── Trigger identity (best-effort exec idx → authored Triggers[] mapping; flat-trigger target) ──

        private int TriggerCount()
        {
            TriggerFireLog? fireLog = _fireLogGetter?.Invoke();
            if (fireLog != null && fireLog.ExecCount > 0) return fireLog.ExecCount;
            TriggerEnabledStore? enabled = _enabledGetter?.Invoke();
            if (enabled != null && enabled.Count > 0) return enabled.Count;
            return _boundScenario?.Triggers?.Length ?? 0;
        }

        /// <summary>Map an exec index to its authored <c>Triggers[]</c> index via the fire log's mapping (identity
        /// fallback when no fire log is wired). Names + click-to-navigate must resolve to the AUTHORED trigger, which
        /// diverges from the exec index once any trigger uses a non-default Priority.</summary>
        private int AuthoredIndex(int execIdx) => _fireLogGetter?.Invoke()?.AuthoredIndex(execIdx) ?? execIdx;

        private static string TriggerName(ScenarioData? scenario, int authoredIdx)
        {
            TriggerDefinition[]? triggers = scenario?.Triggers;
            if (triggers != null && (uint)authoredIdx < (uint)triggers.Length)
            {
                string? n = triggers[authoredIdx].Name;
                if (!string.IsNullOrEmpty(n)) return n!;
            }
            return $"trigger {authoredIdx}";
        }

        // ── Identity signatures (FNV-1a over the declared-var / authored-trigger name sequences) ──
        // Cheap per-frame drift detector so an in-place rename/reorder that preserves the row COUNT still forces a
        // rebuild (see the _watchSig/_trigSig fields). Only computed while the overlay is Visible.

        private static int ComputeWatchSig(List<DslVarReadback.WatchVar>? vars)
        {
            if (vars == null) return 0;
            int h = unchecked((int)2166136261);
            foreach (DslVarReadback.WatchVar v in vars)
            {
                h = FnvString(h, v.Name);
                h = unchecked((h ^ (int)v.Scope) * 16777619);
                h = unchecked((h ^ (v.IsArray ? 1 : 0)) * 16777619);
            }
            return h;
        }

        private int ComputeTrigSig(ScenarioData? scenario)
        {
            int trigCount = TriggerCount();
            int h = unchecked((int)2166136261);
            for (int i = 0; i < trigCount; i++)
                h = FnvString(h, TriggerName(scenario, AuthoredIndex(i)));
            return h;
        }

        private static int FnvString(int h, string s)
        {
            foreach (char c in s) h = unchecked((h ^ c) * 16777619);
            return h;
        }
    }
}
