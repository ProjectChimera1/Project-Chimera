#nullable enable
using System;
using Godot;
using ProjectChimera.Combat;            // DamageType
using ProjectChimera.Core;              // Fixed
using ProjectChimera.Core.Definitions;  // AbilityDraft, DraftNode, DraftKind, DraftVocabulary, AbilityDefinition, AbilityValidator, AbilityLoader
using ProjectChimera.Effects;           // EffectCaps, TargetFilter, StackRule, StatusFlags

namespace ProjectChimera.CreationSuite
{
    /// <summary>
    /// Story 2.5b — the Ability Editor's <b>advanced structured multi-effect graph composer</b> (the partial that
    /// augments the 2.5a <see cref="AbilityEditorPanel"/> shell). Replaces the Advanced tab's raw-JSON-only body
    /// with a visual effect-<i>tree</i> builder (pick each node's kind from a dropdown, fill its fields, add /
    /// remove / reorder / nest — no JSON typing), kept in two-way sync with the kept (collapsible) raw-JSON pane and
    /// reconciled with the common header. It emits the SAME <see cref="AbilityDefinition"/> JSON 2.3 loads and 2.4
    /// attaches — no new runtime path, no sim state, no checksum fold (AC4).
    ///
    /// The composer owns a MUTABLE <see cref="AbilityDraft"/> (the runtime <c>EffectNode</c>/<c>Modifier</c> graph is
    /// immutable, so add/remove/reorder/nest/kind-change are trivial edits on the draft and the immutable tree is
    /// MATERIALIZED only at Show-JSON / validate / save time — never <c>seq.Children[i] = …</c>). The structured tree
    /// is canonical; a manually-edited (dirty) raw-JSON pane wins on Save and is folded back into the tree, so the
    /// three model sources (structured / raw-JSON / header) never silently diverge (AC2).
    /// </summary>
    public partial class AbilityEditorPanel
    {
        // ── Advanced composer state ──
        private AbilityDraft   _draft       = new();
        private VBoxContainer  _treeBox     = null!;   // hosts the rendered effect-tree rows
        private VBoxContainer  _advCostSection = null!;// wraps the cost header + box so passive mode hides it (Decision #4)
        private VBoxContainer  _advCostBox  = null!;   // hosts the Advanced cost/cooldown rows (bound to the draft)
        private bool           _paneDirty;             // the raw-JSON pane has manual edits not yet folded into the tree
        private bool           _suppressPaneDirty;     // guard: a programmatic SetPaneText must not mark the pane dirty

        // ── Advanced pane construction (replaces 2.5a's raw-JSON-only BuildAdvancedPane body) ──

        private Control BuildAdvancedPane()
        {
            var pane = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            pane.AddThemeConstantOverride("separation", 6);

            // Structured effect-tree composer.
            AddSectionHeader(pane, "Effect graph (structured)");
            var hint = new Label
            {
                Text = "Compose the effect tree with controls — pick each node's kind and fill its fields. Add, remove, reorder and nest; no JSON typing.",
                AutowrapMode = TextServer.AutowrapMode.Word,
            };
            hint.AddThemeFontSizeOverride("font_size", 10);
            hint.AddThemeColorOverride("font_color", HintText);
            pane.AddChild(hint);

            _treeBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _treeBox.AddThemeConstantOverride("separation", 4);
            pane.AddChild(_treeBox);

            // Costs & cooldown (top-level AbilityDefinition fields; Simple authors these in its own rows). Wrapped in a
            // section so passive mode can hide it wholesale (Decision #4 — a passive carries no cost/cooldown).
            _advCostSection = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _advCostSection.AddThemeConstantOverride("separation", 6);
            pane.AddChild(_advCostSection);
            _advCostSection.AddChild(new HSeparator());
            AddSectionHeader(_advCostSection, "Costs & cooldown");
            _advCostBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _advCostBox.AddThemeConstantOverride("separation", 6);
            _advCostSection.AddChild(_advCostBox);

            // Raw-JSON escape hatch — kept, but collapsible (Decision #10) so the tree gets vertical room.
            pane.AddChild(new HSeparator());
            var rawToggle = new Button
            {
                Text = "▸ Raw JSON (escape hatch)", ToggleMode = true,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                Alignment = HorizontalAlignment.Left,
            };
            rawToggle.AddThemeFontSizeOverride("font_size", 12);
            pane.AddChild(rawToggle);

            var rawBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false };
            rawBox.AddThemeConstantOverride("separation", 6);
            pane.AddChild(rawBox);
            var rawHint = new Label
            {
                Text = "Edit the ability JSON directly. 'Apply' parses + validates and rebuilds the structured tree; 'Show' re-serialises the composed tree. The precision path (finer than the SpinBox steps).",
                AutowrapMode = TextServer.AutowrapMode.Word,
            };
            rawHint.AddThemeFontSizeOverride("font_size", 10);
            rawHint.AddThemeColorOverride("font_color", HintText);
            rawBox.AddChild(rawHint);

            _jsonPane = MakeJsonPane();
            _jsonPane.TextChanged += () => { if (!_suppressPaneDirty) _paneDirty = true; };
            rawBox.AddChild(_jsonPane);

            var btnRow = new HBoxContainer();
            btnRow.AddThemeConstantOverride("separation", 8);
            rawBox.AddChild(btnRow);
            var showBtn = new Button { Text = "Show JSON", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "Re-serialise the composed structured tree into the pane." };
            showBtn.Pressed += ShowJson;
            btnRow.AddChild(showBtn);
            var applyBtn = new Button { Text = "Apply JSON", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "Parse + validate the pane and rebuild the structured tree from it." };
            applyBtn.Pressed += ApplyJson;
            btnRow.AddChild(applyBtn);

            rawToggle.Toggled += on =>
            {
                rawBox.Visible = on;
                rawToggle.Text = (on ? "▾" : "▸") + " Raw JSON (escape hatch)";
                if (on && !_paneDirty) ShowJson();   // populate from the composed tree on reveal — but never clobber unsaved manual edits (Review P2)
            };

            RebuildAdvancedCostRows();
            RenderTree();
            return pane;
        }

        /// <summary>Clear + rebuild the Advanced cost/cooldown rows, bound to the owned <see cref="_draft"/>.</summary>
        private void RebuildAdvancedCostRows()
        {
            if (_advCostBox == null!) return;
            foreach (Node c in _advCostBox.GetChildren()) { _advCostBox.RemoveChild(c); c.QueueFree(); }
            AddSpinRow(_advCostBox, "Cost: Energy", 0, FixedSpinMax, 1, FixedToDouble(_draft.CostEnergy), v => _draft.CostEnergy = ToFixed(v));
            AddSpinRow(_advCostBox, "Cost: Ore", 0, 99999, 1, _draft.CostOre, v => _draft.CostOre = (int)v);
            AddSpinRow(_advCostBox, "Cost: Crystal", 0, 99999, 1, _draft.CostCrystal, v => _draft.CostCrystal = (int)v);
            AddSpinRow(_advCostBox, "Cooldown (s)", 0, FixedSpinMax, 0.5, FixedToDouble(_draft.Cooldown), v => _draft.Cooldown = ToFixed(v));
        }

        // ── Bidirectional sync (Task 2) ──────────────────────────────────────────

        /// <summary>Entering Advanced from Simple: seed the structured composer from the current Simple model so the
        /// preset's graph appears in the tree (editable), then serialise it into the raw-JSON pane.</summary>
        private void EnterAdvancedFromSimple()
        {
            SeedDraftFromDef(BuildSimpleModel());   // BuildSimpleModel is a pure preset build — does not throw
            ShowJson();
        }

        /// <summary>Seed the owned draft (and rebuild the tree + cost rows) from a parsed/validated ability. The ONE
        /// place the structured tree is rebuilt from a model — funnelled by ReflectModelIntoForm (Task 2.4).</summary>
        private void SeedDraftFromDef(AbilityDefinition def)
        {
            _draft = AbilityDraft.FromDefinition(def);
            RebuildAdvancedCostRows();
            RenderTree();
        }

        /// <summary>Materialise the Advanced model: sync the shared header (id/name/targeting) into the draft, then
        /// build the immutable <see cref="AbilityDefinition"/>. May throw <see cref="InvalidOperationException"/> on a
        /// structurally-incomplete tree (e.g. a Search Area with no child) — callers surface it inline.</summary>
        private AbilityDefinition BuildAdvancedModel()
        {
            _draft.Id          = _idEdit.Text.Trim();   // raw (Decision #8 blocks an un-sanitised id at save), not silently sanitised
            _draft.DisplayName = _nameEdit.Text;
            _draft.Targeting      = _targetingName;
            _draft.TargetAffinity = _targetAffinity;     // Story 15.11 (DW-286) — carry the affinity hint into the saved model
            _draft.Activation     = _activationName;     // Story 2.6 — carry the activation into the saved model
            // Decision #4 — a passive carries no cost/cooldown; force zero so the validator passes regardless of any
            // stale value the (hidden) cost rows held before the activation switch.
            if (IsPassive) { _draft.CostEnergy = Fixed.Zero; _draft.CostOre = 0; _draft.CostCrystal = 0; _draft.Cooldown = Fixed.Zero; }
            return _draft.ToDefinition();
        }

        /// <summary>
        /// Resolve the canonical Advanced model to a VALIDATED <see cref="AbilityDefinition"/> (or null + an inline
        /// error). The structured tree is canonical; a manually-edited (dirty) raw-JSON pane wins and is folded back
        /// into the tree (so the sources reconverge). Either branch returns a validator-minted def (the editor can't
        /// mint <c>Validated&lt;T&gt;</c> itself — it routes through the validator/loader).
        /// </summary>
        private AbilityDefinition? ResolveAdvancedDef()
        {
            if (_paneDirty)
            {
                AbilityValidationResult r = AbilityLoader.Load(_jsonPane.Text, CurrentEditorId());
                if (!r.Ok) { ShowError(r.Error); return null; }
                ReflectModelIntoForm(r.Value.Value);   // fold manual edits back into header + tree (Review P1: the shared header must reconverge too, not just the tree)
                _paneDirty = false;
                return r.Value.Value;
            }

            AbilityDefinition built;
            try { built = BuildAdvancedModel(); }
            catch (InvalidOperationException ex) { ShowError(ex.Message); return null; }   // incomplete tree

            AbilityValidationResult vr = new AbilityValidator().Validate(built);
            if (!vr.Ok) { ShowError(vr.Error); return null; }
            return vr.Value.Value;
        }

        /// <summary>Set the raw-JSON pane text WITHOUT marking it dirty (a programmatic render, not a manual edit).</summary>
        private void SetPaneText(string text)
        {
            _suppressPaneDirty = true;
            _jsonPane.Text = text;       // Godot emits text_changed synchronously here; the guard swallows it
            _suppressPaneDirty = false;
            _paneDirty = false;
        }

        // ── Tree rendering (Task 1) ──────────────────────────────────────────────

        /// <summary>Render context threaded down the tree: composition depth (for MaxEffectDepth) and the count of
        /// SearchArea nodes from the root to (and including) the node (for MaxSearchAreaDepth). Drives the in-UI caps
        /// guardrail (Task 1.5); the AbilityValidator remains the authoritative gate on save.</summary>
        private readonly struct TreeCtx
        {
            public readonly int CompDepth;        // composition nodes strictly above this node (root composition = 0)
            public readonly int SearchAncestors;  // SearchArea nodes from root to this node INCLUSIVE
            public TreeCtx(int compDepth, int searchAncestors) { CompDepth = compDepth; SearchAncestors = searchAncestors; }
            public TreeCtx Nest(DraftNode child) =>
                new TreeCtx(CompDepth + 1, SearchAncestors + (child.Kind == DraftKind.SearchArea ? 1 : 0));
        }

        /// <summary>Clear + rebuild the whole structured tree from the owned draft (full rebuild per edit — the
        /// TriggerEditorPanel.RefreshList pattern; ≤64 nodes so cheap, and it sidesteps every stale-closure trap).</summary>
        private void RenderTree()
        {
            if (_treeBox == null!) return;
            foreach (Node c in _treeBox.GetChildren()) { _treeBox.RemoveChild(c); c.QueueFree(); }

            if (_draft.Effect is null)
            {
                BuildAddRow(_treeBox, "+ Add root effect", AddableKinds(-1, 0), "",
                    k => { _draft.Effect = DraftNode.NewDefault(k); RenderTree(); });
                return;
            }

            var rootCtx = new TreeCtx(0, _draft.Effect.Kind == DraftKind.SearchArea ? 1 : 0);
            RenderNode(_treeBox, _draft.Effect, rootCtx,
                remove: () => { _draft.Effect = null; RenderTree(); }, moveUp: null, moveDown: null);
        }

        private void RenderNode(Control parent, DraftNode node, TreeCtx ctx, Action remove, Action? moveUp, Action? moveDown)
        {
            var panel = new PanelContainer();
            panel.AddThemeStyleboxOverride("panel", Card(CardBg, CardBorder, 6, 8, 6));
            parent.AddChild(panel);
            var card = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            card.AddThemeConstantOverride("separation", 4);
            panel.AddChild(card);

            // Header: kind dropdown (the closed 7) + reorder/remove.
            var header = new HBoxContainer();
            header.AddThemeConstantOverride("separation", 6);
            card.AddChild(header);
            OptionButton kindDd = MakeStyledDropdown(KindItems(), (int)node.Kind, id =>
            {
                var k = (DraftKind)id;
                if (k != node.Kind) { node.ResetKind(k); RenderTree(); }   // change kind = in-place reset (preserve nothing, Task 1.4)
            });
            kindDd.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            header.AddChild(kindDd);
            if (moveUp is not null)   { Button up = SmallBtn("↑"); up.TooltipText = "Move up";   up.Pressed += () => moveUp();   header.AddChild(up); }
            if (moveDown is not null) { Button dn = SmallBtn("↓"); dn.TooltipText = "Move down"; dn.Pressed += () => moveDown(); header.AddChild(dn); }
            Button rm = SmallBtn("✕"); rm.TooltipText = "Remove this node (and its subtree)"; rm.Pressed += () => remove(); header.AddChild(rm);

            // Per-kind field editors (REUSE the panel's existing builders).
            switch (node.Kind)
            {
                case DraftKind.DirectHpDelta:
                    AddSpinRow(card, "HP Delta (− = cost)", -FixedSpinMax, FixedSpinMax, 1, FixedToDouble(node.Delta), v => node.Delta = ToFixed(v));
                    break;

                case DraftKind.Heal:
                    AddSpinRow(card, "Heal Amount", 0, FixedSpinMax, 1, FixedToDouble(node.Amount), v => node.Amount = ToFixed(v));
                    break;

                case DraftKind.Damage:
                    AddSpinRow(card, "Damage Amount", 0, FixedSpinMax, 1, FixedToDouble(node.Amount), v => node.Amount = ToFixed(v));
                    AddDropdownRow(card, "Damage Type", DamageTypeItems(), (int)node.DamageType, id => node.DamageType = (DamageType)id);
                    break;

                case DraftKind.ApplyModifier:
                    RenderModifierEditor(card, node.Modifier, ctx);
                    break;

                case DraftKind.Sequence:
                    RenderSequenceChildren(card, node, ctx);
                    break;

                case DraftKind.SearchArea:
                    AddSpinRow(card, "Radius", 0, FixedSpinMax, 0.5, FixedToDouble(node.Radius), v => node.Radius = ToFixed(v));
                    // Story 2.6 (Task 8) — Filter is a [Flags] set: author COMBINATIONS via checkboxes (Ally + Alive …).
                    // The offered set is whatever DraftVocabulary.Filters declares — closed, but NOT "allegiance + Alive
                    // only": Story 2.9a ADDED the Air/Ground/Structure domain bits (TargetMatcher evaluates them off the
                    // shared DomainClassifier), so they are author-selectable here. Pinned by FlagCombinationVocabularyTests.
                    AddFlagChecks(card, "Filter", DraftVocabulary.Filters, () => node.Filter, v => node.Filter = v);
                    RenderChildSlot(card, "Child effect", ctx, () => node.Child, n => node.Child = n);
                    break;

                case DraftKind.Persistent:
                    AddSpinRow(card, "Period (ticks)", 0, 99999, 1, node.PeriodTicks, v => node.PeriodTicks = (int)v);
                    AddSpinRow(card, "Period count", 0, 99999, 1, node.PeriodCount, v => node.PeriodCount = (int)v);
                    RenderChildSlot(card, "Initial effect", ctx, () => node.Initial, n => node.Initial = n);
                    RenderChildSlot(card, "Period effect",  ctx, () => node.Period,  n => node.Period  = n);
                    RenderChildSlot(card, "Expire effect",  ctx, () => node.Expire,  n => node.Expire  = n);
                    // Story 2.6 (Task 7.3) — the 2.5b deferred no-op traps, surfaced where they bite passives hardest.
                    if (node.Initial is null && node.Period is null && node.Expire is null)
                        AddDimNote(card, "A Persistent with no initial / period / expire effect does nothing.");
                    if (node.Period is not null && node.PeriodTicks == 0)
                        AddDimNote(card, "Period (ticks) = 0 with a period effect — the period effect never fires.");
                    break;
            }
        }

        private void RenderSequenceChildren(Control card, DraftNode seq, TreeCtx ctx)
        {
            for (int i = 0; i < seq.Children.Count; i++)
            {
                int idx = i;                       // capture for the closures (the TriggerEditorPanel pattern)
                DraftNode child = seq.Children[idx];
                Action? up = idx > 0
                    ? () => { DraftNode t = seq.Children[idx - 1]; seq.Children[idx - 1] = seq.Children[idx]; seq.Children[idx] = t; RenderTree(); }
                    : (Action?)null;
                Action? down = idx < seq.Children.Count - 1
                    ? () => { DraftNode t = seq.Children[idx + 1]; seq.Children[idx + 1] = seq.Children[idx]; seq.Children[idx] = t; RenderTree(); }
                    : (Action?)null;
                RenderNode(card, child, ctx.Nest(child),
                    remove: () => { seq.Children.RemoveAt(idx); RenderTree(); }, moveUp: up, moveDown: down);
            }

            bool full = seq.Children.Count >= EffectCaps.MaxSequenceChildren;
            DraftKind[] addable = full ? Array.Empty<DraftKind>() : AddableKinds(ctx.CompDepth, ctx.SearchAncestors);
            string reason = full ? $"max {EffectCaps.MaxSequenceChildren} children" : "node limit reached";
            BuildAddRow(card, "+ Add child", addable, reason, k => { seq.Children.Add(DraftNode.NewDefault(k)); RenderTree(); });
            if (seq.Children.Count == 0)
                AddDimNote(card, "A sequence needs at least one child to be valid.");
        }

        private void RenderChildSlot(Control card, string label, TreeCtx ctx, Func<DraftNode?> get, Action<DraftNode?> set)
        {
            DraftNode? child = get();
            if (child is null)
            {
                DraftKind[] addable = AddableKinds(ctx.CompDepth, ctx.SearchAncestors);
                BuildAddRow(card, $"+ Set {label.ToLowerInvariant()}", addable, "node limit reached",
                    k => { set(DraftNode.NewDefault(k)); RenderTree(); });
                return;
            }
            AddSlotLabel(card, label + ":");
            RenderNode(card, child, ctx.Nest(child),
                remove: () => { set(null); RenderTree(); }, moveUp: null, moveDown: null);
        }

        private void RenderModifierEditor(Control card, DraftModifier m, TreeCtx ctx)
        {
            AddSpinRow(card, "Modifier Id", 0, 999999, 1, m.Id, v => m.Id = (int)v);
            AddSpinRow(card, "Duration (ticks; <0 = permanent)", -1, 99999, 1, m.DurationTicks, v => m.DurationTicks = (int)v);
            AddDropdownRow(card, "Stacking", StackRuleItems(), (int)m.Stacking, id => m.Stacking = (StackRule)id);
            AddSpinRow(card, "Max Stacks", 1, 999, 1, m.MaxStacks, v => m.MaxStacks = (int)v);
            AddSpinRow(card, "Max Health Δ", -FixedSpinMax, FixedSpinMax, 1, FixedToDouble(m.MaxHealthDelta), v => m.MaxHealthDelta = ToFixed(v));
            AddSpinRow(card, "Attack Damage Δ", -FixedSpinMax, FixedSpinMax, 1, FixedToDouble(m.AttackDamageDelta), v => m.AttackDamageDelta = ToFixed(v));
            AddSpinRow(card, "Move Speed Δ", -FixedSpinMax, FixedSpinMax, 0.5, FixedToDouble(m.MoveSpeedDelta), v => m.MoveSpeedDelta = ToFixed(v));
            // Story 2.6 (Task 7.4) — the buffable armor stat (e.g. the aura's +armor grant); round-trips via armor_delta.
            AddSpinRow(card, "Armor Δ", -FixedSpinMax, FixedSpinMax, 1, FixedToDouble(m.ArmorDelta), v => m.ArmorDelta = ToFixed(v));
            // Story 2.6 (Task 8) — Status is a [Flags] set: author COMBINATIONS via checkboxes (Stunned + Silenced …),
            // closed to the real status bits. Supersedes the 2.5b single-select dropdown + loaded-combo workaround.
            AddFlagChecks(card, "Status", DraftVocabulary.Statuses, () => m.Status, v => m.Status = v);
            AddSpinRow(card, "Period (ticks)", 0, 99999, 1, m.PeriodTicks, v => m.PeriodTicks = (int)v);
            RenderChildSlot(card, "Period effect", ctx, () => m.Period, n => m.Period = n);
            // Story 2.6 (Task 7.3) — surface the 2.5b deferred no-op trap (a period effect that never fires).
            if (m.Period is not null && m.PeriodTicks == 0)
                AddDimNote(card, "Period (ticks) = 0 with a period effect — the period effect never fires.");
        }

        // ── Add affordance + caps guardrail (Task 1.4 / 1.5) ─────────────────────

        /// <summary>The kinds addable at a position, with the in-UI EffectCaps guardrail applied (the validator
        /// remains the gate): excludes everything when the 64-node budget is spent, excludes a nested composition past
        /// MaxEffectDepth, and excludes a SearchArea past MaxSearchAreaDepth. Offers only authorable kinds (AC5).</summary>
        private DraftKind[] AddableKinds(int parentCompDepth, int searchAncestorsInclParent)
        {
            int total = _draft.Effect?.CountNodes() ?? 0;
            if (total >= EffectCaps.MaxTotalEffectNodes) return Array.Empty<DraftKind>();

            int childCompDepth = parentCompDepth + 1;
            var list = new System.Collections.Generic.List<DraftKind>(DraftVocabulary.Kinds.Length);
            foreach (DraftKind k in DraftVocabulary.Kinds)
            {
                bool isComposition = k is DraftKind.Sequence or DraftKind.SearchArea or DraftKind.Persistent;
                if (isComposition && childCompDepth >= EffectCaps.MaxEffectDepth) continue;
                if (k == DraftKind.SearchArea && searchAncestorsInclParent + 1 > EffectCaps.MaxSearchAreaDepth) continue;
                list.Add(k);
            }
            return list.ToArray();
        }

        private void BuildAddRow(Control parent, string buttonLabel, DraftKind[] addable, string disabledReason, Action<DraftKind> onAdd)
        {
            if (addable.Length == 0)
            {
                AddDimNote(parent, $"{buttonLabel} — {(string.IsNullOrEmpty(disabledReason) ? "unavailable" : disabledReason)}");
                return;
            }
            var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            row.AddThemeConstantOverride("separation", 6);
            parent.AddChild(row);
            OptionButton dd = MakeStyledDropdown(KindItems(addable), (int)addable[0], _ => { });
            dd.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(dd);
            var btn = new Button { Text = buttonLabel };
            btn.AddThemeFontSizeOverride("font_size", 12);
            btn.Pressed += () => { int id = dd.GetSelectedId(); if (id >= 0) onAdd((DraftKind)id); };
            row.AddChild(btn);
        }

        // ── Dropdown item builders — the CLOSED authorable sets (AC5-COMPOSER), built from DraftVocabulary ──

        private static (string Label, int Id)[] KindItems() => KindItems(DraftVocabulary.Kinds);

        private static (string Label, int Id)[] KindItems(DraftKind[] kinds)
        {
            var items = new (string, int)[kinds.Length];
            for (int i = 0; i < kinds.Length; i++) items[i] = (KindLabel(kinds[i]), (int)kinds[i]);
            return items;
        }

        private static string KindLabel(DraftKind k) => k switch
        {
            DraftKind.DirectHpDelta => "Direct HP Delta",
            DraftKind.Heal          => "Heal",
            DraftKind.Damage        => "Damage",
            DraftKind.ApplyModifier => "Apply Modifier",
            DraftKind.Sequence      => "Sequence",
            DraftKind.SearchArea    => "Search Area",
            DraftKind.Persistent    => "Persistent",
            _                       => k.ToString(),
        };

        // damage_type: the 5 real types — NEVER COUNT (AC5-COMPOSER).
        private static (string Label, int Id)[] DamageTypeItems() => EnumItems(DraftVocabulary.DamageTypes, t => (int)t);
        private static (string Label, int Id)[] StackRuleItems()  => EnumItems(DraftVocabulary.StackRules, s => (int)s);
        // (Story 2.6, Task 8) filter + status are now authored via AddFlagChecks ([Flags] multi-select checkboxes), not
        // single-select dropdowns — so the former FilterItems/StatusItems builders + the IncludingCurrent loaded-combo
        // dropdown workaround (2.5b review P3) are removed: checkboxes display + round-trip any combination natively.

        private static (string Label, int Id)[] EnumItems<TEnum>(TEnum[] values, Func<TEnum, int> toId) where TEnum : struct, Enum
        {
            var items = new (string, int)[values.Length];
            for (int i = 0; i < values.Length; i++) items[i] = (values[i].ToString()!, toId(values[i]));
            return items;
        }

        // ── Small UI helpers ─────────────────────────────────────────────────────

        private static Button SmallBtn(string text)
        {
            var b = new Button { Text = text, CustomMinimumSize = new Vector2(30, 30) };
            b.AddThemeFontSizeOverride("font_size", 13);
            return b;
        }

        private static void AddDimNote(Control parent, string text)
        {
            var note = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.Word };
            note.AddThemeFontSizeOverride("font_size", 11);
            note.AddThemeColorOverride("font_color", DimText);
            parent.AddChild(note);
        }

        private static void AddSlotLabel(Control parent, string text)
        {
            var lbl = new Label { Text = text };
            lbl.AddThemeFontSizeOverride("font_size", 11);
            lbl.AddThemeColorOverride("font_color", HintText);
            parent.AddChild(lbl);
        }

        /// <summary>
        /// Story 2.6 (Task 8) — a CLOSED multi-select for a <c>[Flags]</c> enum: one checkbox per real bit in
        /// <paramref name="bits"/>, so the creator authors COMBINATIONS (e.g. <c>Ally + Alive</c>, <c>Stunned + Silenced</c>)
        /// directly. The zero/None value is intentionally NOT a checkbox — it is the state of no boxes checked. Because
        /// the offered bits come from <see cref="DraftVocabulary"/>, the composer can only ever build a value inside the
        /// closed AC5 set — the load-bearing closed-vocabulary guarantee, now for combinations too. (The vocabulary is
        /// the single source of truth for WHICH bits are offered; since Story 2.9a it INCLUDES the Air/Ground/Structure
        /// domain bits, which are no longer reserved.) Supersedes the 2.5b single-select dropdown + the
        /// loaded-combo (IncludingCurrent) workaround: checkboxes display + round-trip any combination natively.
        /// </summary>
        private void AddFlagChecks<TEnum>(Control card, string label, TEnum[] bits, Func<TEnum> get, Action<TEnum> set)
            where TEnum : struct, Enum
        {
            AddSlotLabel(card, label + " (check any combination):");
            var wrap = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            card.AddChild(wrap);
            foreach (TEnum bit in bits)
            {
                int b = Convert.ToInt32(bit);
                if (b == 0) continue;   // the empty value (None) is "no boxes checked", not its own checkbox
                var cb = new CheckBox { Text = bit.ToString(), ButtonPressed = (Convert.ToInt32(get()) & b) == b };
                cb.AddThemeFontSizeOverride("font_size", 12);
                cb.AddThemeColorOverride("font_color", BodyText);
                cb.Toggled += on =>
                {
                    int cur = Convert.ToInt32(get());
                    cur = on ? (cur | b) : (cur & ~b);
                    set((TEnum)Enum.ToObject(typeof(TEnum), cur));
                };
                wrap.AddChild(cb);
            }
        }
    }
}
