#nullable enable
using System;
using System.Collections.Generic;
using Godot;
using ProjectChimera.Core.Definitions;   // ScenarioData, ResolvedObjective, ObjectiveResolver, ObjectiveState
using ProjectChimera.Core.Sim;             // DslVarReadback
using ProjectChimera.UI.Components;         // ChimeraComponents, ChimeraToastHost
using ProjectChimera.UI.Theme;              // ThemeTokens, ThemeBuilder, AccentController
using GodotTheme = Godot.Theme;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 7.14 — the in-match quest-log panel. A code-built <see cref="CanvasLayer"/> overlay (toggle key + a
    /// new-objective toast) that reflects <c>show_objective</c>/<c>complete_objective</c>/<c>fail_objective</c> state
    /// LIVE by reading each authored objective's reserved DSL variable through the version-stamped
    /// <see cref="DslVarReadback"/> read rail (the <c>CustomUiBridge</c> pattern) — re-formatting a row only on a
    /// version change. The synthesized default objective (a scenario with no authored objectives) has no reserved var,
    /// so its state renders directly from the resolver as <see cref="ObjectiveState.Active"/>.
    ///
    /// <para><b>Presentation-only.</b> This overlay NEVER writes sim state and is explicitly NOT folded into
    /// <c>SimChecksum</c> (the read rail is a copy of already-checksummed state, AR-32), so opening/closing it leaves
    /// the checksum byte-identical and it can never desync. Late-bound <c>() =&gt; _ctx.Scenario</c> getters make it
    /// survive the F5 Edit→Play re-apply (the resolved objective set is rebuilt whenever the live scenario changes).</para>
    /// </summary>
    public partial class ObjectiveLogOverlay : CanvasLayer
    {
        private const float PANEL_W = 340f;

        private GodotTheme         _theme  = null!;
        private ChimeraToastHost   _toastHost = null!;

        // ── Deps (late-bound; wired by the phase) ──
        private Func<DslVarReadback?>? _readbackGetter;
        private Func<ScenarioData?>?   _scenarioGetter;
        private Func<int>?             _localFactionGetter;

        private VBoxContainer _rowHost = null!;

        // Rebuilt whenever the live scenario reference changes (F5 re-apply). Each row tracks its read-rail version
        // and last-observed state so it re-formats only on change and toasts only on a Hidden→Active rising edge.
        private ScenarioData? _boundScenario;
        private readonly List<Row> _rows = new();

        private sealed class Row
        {
            public ResolvedObjective Obj;
            public Control Container = null!;
            public Label Title = null!;
            public Label State = null!;
            public uint LastVersion;
            public bool HasVersion;
            public int LastState = -1; // seeded to the objective's InitialState at RebuildRows (so a match-start reveal still toasts); -1 only if left unseeded
        }

        public override void _Ready()
        {
            _theme = ChimeraComponents.EnsureInitialized(this);
            BuildChrome();
            Visible = false;
        }

        /// <summary>Wire the read rail + late-bound scenario / local-faction getters. Called by the phase after AddChild.</summary>
        public void Initialize(Func<DslVarReadback?> readbackGetter, Func<ScenarioData?> scenarioGetter, Func<int> localFactionGetter)
        {
            _readbackGetter     = readbackGetter;
            _scenarioGetter     = scenarioGetter;
            _localFactionGetter = localFactionGetter;
        }

        private void BuildChrome()
        {
            Layer = 21;

            _toastHost = ChimeraToastHost.Create();
            AddChild(_toastHost);

            // Top-right anchored panel (an in-match HUD affordance, not a modal — no scrim).
            var margin = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
            margin.AddThemeConstantOverride("margin_top", 16);
            margin.AddThemeConstantOverride("margin_right", 16);
            margin.GrowHorizontal = Control.GrowDirection.Begin;
            AddChild(margin);

            var panel = ChimeraComponents.Panel(ChimeraComponents.PanelVariant.Default);
            panel.CustomMinimumSize = new Vector2(PANEL_W, 0);
            panel.Theme = _theme;
            margin.AddChild(panel);

            var root = new VBoxContainer();
            root.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S3));
            panel.AddChild(root);

            root.AddChild(ChimeraComponents.Heading("Objectives", ThemeTokens.Tlg));

            _rowHost = new VBoxContainer();
            _rowHost.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            root.AddChild(_rowHost);
        }

        /// <summary>Toggle in-match visibility (the quest-log key). No sim write — checksum byte-identical.</summary>
        public void Toggle() => Visible = !Visible;

        /// <summary>
        /// Per-frame pump (called from <c>MainScene._Process</c>). Rebuilds rows when the live scenario changes
        /// (F5 re-apply), then refreshes each row from the read rail, re-formatting only on a version change and
        /// firing a single toast on a Hidden→Active rising edge.
        /// </summary>
        public void Update()
        {
            ScenarioData? scenario = _scenarioGetter?.Invoke();
            if (!ReferenceEquals(scenario, _boundScenario))
                RebuildRows(scenario);

            DslVarReadback? readback = _readbackGetter?.Invoke();
            int faction = _localFactionGetter?.Invoke() ?? 0;

            foreach (Row row in _rows)
            {
                int state;
                uint version = 0;
                if (row.Obj.HasReservedVar && readback != null
                    && readback.TryGetScalar(row.Obj.ReservedVarName, faction, out _, out int raw0, out _, out uint ver))
                {
                    state = raw0;
                    version = ver;
                }
                else
                {
                    state = (int)row.Obj.InitialState; // presentation-only default (or read rail not ready yet)
                }

                // Toast on the Hidden→Active rising edge (once, version-gated by LastState — never per frame).
                if (row.LastState == (int)ObjectiveState.Hidden && state == (int)ObjectiveState.Active)
                    _toastHost.Show("New Objective", row.Obj.Title, ChimeraToastHost.ToastVariant.Default);

                bool stateChanged = row.LastState != state;
                row.LastState = state;

                // Re-format only on a read-rail version change (the CustomUiBridge short-circuit) or a state change.
                if (row.HasVersion && version == row.LastVersion && !stateChanged) continue;
                row.LastVersion = version;
                row.HasVersion = true;

                // A Hidden objective is not yet revealed — hide its row.
                row.Container.Visible = state != (int)ObjectiveState.Hidden;
                ApplyState(row, state);
            }
        }

        private void RebuildRows(ScenarioData? scenario)
        {
            _boundScenario = scenario;
            foreach (Node c in _rowHost.GetChildren()) c.QueueFree();
            _rows.Clear();

            foreach (ResolvedObjective o in ObjectiveResolver.Resolve(scenario))
            {
                var container = new VBoxContainer();
                container.AddThemeConstantOverride("separation", 2);

                var title = new Label { Text = o.Title, AutowrapMode = TextServer.AutowrapMode.WordSmart };
                title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                title.AddThemeColorOverride("font_color", _theme.GetColor(ThemeTokens.TextHi, ThemeTokens.Type));
                container.AddChild(title);

                Label state = ChimeraComponents.FieldLabel("In Progress");
                container.AddChild(state);

                _rowHost.AddChild(container);
                // Seed LastState from the objective's initial state (NOT the -1 unobserved sentinel) so a Hidden→Active
                // reveal that happens at/before the overlay's first Update (e.g. a match_start show_objective) still
                // registers the Hidden→Active rising edge and fires the "New Objective" toast. An objective seeded Active
                // starts == its first observed state, so it correctly does NOT toast.
                _rows.Add(new Row { Obj = o, Container = container, Title = title, State = state, LastState = (int)o.InitialState });
            }
        }

        private void ApplyState(Row row, int state)
        {
            (string text, StringName color) = state switch
            {
                (int)ObjectiveState.Complete => ("Complete", ThemeTokens.Ok),
                (int)ObjectiveState.Failed   => ("Failed", ThemeTokens.Danger),
                _                            => ("In Progress", ThemeTokens.Accent),
            };
            row.State.Text = text;
            row.State.AddThemeColorOverride("font_color", _theme.GetColor(color, ThemeTokens.Type));

            // Completed/failed titles read muted; active stays hi-contrast.
            StringName titleColor = state == (int)ObjectiveState.Active || state == (int)ObjectiveState.Hidden
                ? ThemeTokens.TextHi : ThemeTokens.TextMid;
            row.Title.AddThemeColorOverride("font_color", _theme.GetColor(titleColor, ThemeTokens.Type));
        }
    }
}
