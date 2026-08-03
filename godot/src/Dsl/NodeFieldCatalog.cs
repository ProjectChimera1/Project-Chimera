#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectChimera.Core;   // Fixed

namespace ProjectChimera.Dsl
{
    /// <summary>The editor control class a <see cref="NodeFieldDef"/> renders as (the T3 inspector's closed
    /// row vocabulary). Values always travel as invariant STRINGS through <see cref="NodeFieldDef.Get"/> /
    /// <see cref="NodeFieldDef.Set"/>, so the seam stays Godot-free and Tier-1-testable.</summary>
    public enum NodeFieldEditorKind
    {
        /// <summary>Free-form single-line text.</summary>
        Text,
        /// <summary>A whole number (invariant digits, optional leading '-').</summary>
        Int,
        /// <summary>A boolean ("true"/"false").</summary>
        Bool,
        /// <summary>A 16.16 Fixed decimal (plain decimal digits, no exponent; parsed with exact integer math).</summary>
        Fixed,
        /// <summary>One member of a closed vocabulary (<see cref="NodeFieldDef.Choices"/>).</summary>
        Choice,
        /// <summary>A comma-separated list of non-negative whole numbers (random_choice weights).</summary>
        IntList,
    }

    /// <summary>
    /// One editable field of a selected graph node, bound to that node instance: a stable key, a display label,
    /// the editor kind (+ closed choices where applicable), and string-typed <see cref="Get"/>/<see cref="Set"/>
    /// accessors. <see cref="Set"/> validates BEFORE mutating and returns a located error message (or null on
    /// success) — it must reject every value the canonical serialize→re-parse round-trip would reject, so an
    /// inspector edit can never brick the stored graph channel (the <see cref="NodePaletteFactory"/> guarantee,
    /// extended to edits).
    /// </summary>
    public sealed class NodeFieldDef
    {
        /// <summary>Stable field key (matches the canonical JSON property name).</summary>
        public string Key { get; }

        /// <summary>Human-readable row label.</summary>
        public string Label { get; }

        /// <summary>The editor control class this field renders as.</summary>
        public NodeFieldEditorKind Editor { get; }

        /// <summary>The closed vocabulary for <see cref="NodeFieldEditorKind.Choice"/> fields; null otherwise.</summary>
        public IReadOnlyList<string>? Choices { get; }

        /// <summary>Read the bound node's current value as an invariant string (always re-<see cref="Set"/>-able).</summary>
        public Func<string> Get { get; }

        /// <summary>Validate + apply a new value to the bound node. Returns null on success, else the per-field
        /// located error message (the node is left UNCHANGED on error).</summary>
        public Func<string, string?> Set { get; }

        public NodeFieldDef(string key, string label, NodeFieldEditorKind editor,
            Func<string> get, Func<string, string?> set, IReadOnlyList<string>? choices = null)
        {
            Key = key;
            Label = label;
            Editor = editor;
            Get = get;
            Set = set;
            Choices = choices;
        }
    }

    /// <summary>
    /// DW-179 (with DW-195's target-field half) — the Godot-free T3 INSPECTOR seam: the editable fields of every
    /// palette node kind, so the visual graph editor can author a COMPLETE construct end-to-end (a palette-added
    /// node is no longer locked to its factory defaults). Field coverage per kind is EXACTLY the kind's
    /// serialized surface (<c>NodeBaseJsonConverter.Write</c>'s allow-list) — a field that would not persist is
    /// never shown, and a value that would not re-parse is never accepted (see <see cref="NodeFieldDef.Set"/>).
    /// The two payload-less kinds (branch, run_effect — whose payload is the embedded effect subgraph, edited
    /// via the ability-editor pattern) expose no fields.
    ///
    /// <para>Float-free by construction (src/Dsl is inside the determinism analyzer's globbed set): Fixed values
    /// parse and render via exact integer math (the <c>ReadExprFixedExact</c> rulebook — round-half-up, ≤16
    /// fraction digits, no exponent). CONTENT Fixed fields (which persist through the float-quantizing
    /// <c>FixedJsonConverter</c>, AR-14) additionally reject the top ~0.004 sliver under +32768 whose nearest
    /// float rounds UP to 32768f — a value the converter would then REJECT on reload (persist-then-cannot-load).</para>
    /// </summary>
    public static class NodeFieldCatalog
    {
        private static readonly NodeFieldDef[] NoFields = Array.Empty<NodeFieldDef>();

        // ── Closed selector vocabularies for the 7.13 state-read built-ins. Members MUST resolve through the
        //    corresponding NodeKinds.TryResolve*Selector (Tier-1-pinned, so the lists can never drift from the
        //    compiler's resolvers). region_unit_count's selector is free-form (runtime-resolved via RegionStore).
        private static readonly string[] TagSelectors      = { "organic", "mechanical", "magical" };
        private static readonly string[] CategorySelectors = { "worker", "melee", "ranged", "siege", "air", "structure" };
        private static readonly string[] ResourceSelectors = { "ore", "crystal" };

        /// <summary>The literal-able expr_literal value types (the converter's closed Int/Fixed/Bool set).</summary>
        private static readonly string[] LiteralTypes = { "Int", "Fixed", "Bool" };

        /// <summary>The for_each_batched entity sources (parse rejects "array" on a batched loop).</summary>
        private static readonly string[] BatchedSources = { "faction_units", "region_units" };

        /// <summary>The editable fields of <paramref name="n"/>, bound to it. Empty for kinds with no editable
        /// payload (branch, run_effect). Rebuild after a successful Set — a field's editor kind or the field SET
        /// itself can depend on current values (expr_literal's value type, expr_call's per-fn selector).</summary>
        public static IReadOnlyList<NodeFieldDef> FieldsOf(NodeBase n)
        {
            switch (n)
            {
                case TriggerNode t:
                    return new[]
                    {
                        TextField("name", "Name", () => t.Name, v => t.Name = v),
                        BoolField("enabled", "Enabled", () => t.Enabled, v => t.Enabled = v),
                        BoolField("run_once", "Run once", () => t.RunOnce, v => t.RunOnce = v),
                        FixedField("cooldown_seconds", "Cooldown (s)", () => t.CooldownSeconds, v => t.CooldownSeconds = v),
                        IntField("priority", "Priority", int.MinValue, int.MaxValue, () => t.Priority, v => t.Priority = v),
                    };

                case EventNode e when e.Kind == NodeKinds.CustomEvent:
                    // custom_event serializes kind + event_name ONLY; an empty name is a serialize-time throw.
                    return new[]
                    {
                        ReqText("event_name", "Event name", () => e.EventName ?? "", v => e.EventName = v),
                    };

                case EventNode e:
                    return new[]
                    {
                        FactionField("faction", () => e.Faction, v => e.Faction = v),
                        OptText("building_type", "Building type", () => e.BuildingType, v => e.BuildingType = v),
                        OptText("timer_name", "Timer name", () => e.TimerName, v => e.TimerName = v),
                        FixedField("amount", "Amount", () => e.Amount, v => e.Amount = v),
                        IntField("count", "Count", int.MinValue, int.MaxValue, () => e.Count, v => e.Count = v),
                        ChoiceField("operator", "Operator", NodeKinds.Operators, () => e.Operator, v => e.Operator = v),
                    };

                case RaiseEventNode r:
                    return new[]
                    {
                        // parse REJECTS an empty raise name (fail-closed) — an accepted empty here would brick
                        // the stored channel on the next load, so it is a field-level reject too.
                        ReqText("name", "Event name", () => r.Name, v => r.Name = v),
                        IntField("raiser", "Raiser slot", -1, DslVarTable.PlayerSlots - 1, () => r.Raiser, v => r.Raiser = v),
                        BoolField("next_tick", "Next tick", () => r.NextTick, v => r.NextTick = v),
                    };

                case ConditionNode c:
                    return new[]
                    {
                        FactionField("faction", () => c.Faction, v => c.Faction = v),
                        OptText("building_type", "Building type", () => c.BuildingType, v => c.BuildingType = v),
                        FixedField("amount", "Amount", () => c.Amount, v => c.Amount = v),
                        IntField("count", "Count", int.MinValue, int.MaxValue, () => c.Count, v => c.Count = v),
                        OptText("variable", "Variable", () => c.Variable, v => c.Variable = v),
                        OptText("region_id", "Region id", () => c.RegionId, v => c.RegionId = v),
                        IntField("value", "Value", int.MinValue, int.MaxValue, () => c.Value, v => c.Value = v),
                        ChoiceField("operator", "Operator", NodeKinds.Operators, () => c.Operator, v => c.Operator = v),
                    };

                case ActionNode a:
                    return new[]
                    {
                        OptText("unit_id", "Unit id", () => a.UnitId, v => a.UnitId = v),
                        FactionField("faction", () => a.Faction, v => a.Faction = v),
                        FixedField("x", "X", () => a.X, v => a.X = v),
                        FixedField("z", "Z", () => a.Z, v => a.Z = v),
                        IntField("count", "Count", int.MinValue, int.MaxValue, () => a.Count, v => a.Count = v),
                        OptText("text", "Text", () => a.Text, v => a.Text = v),
                        FixedField("duration", "Duration (s)", () => a.Duration, v => a.Duration = v),
                        OptText("timer_name", "Timer name", () => a.TimerName, v => a.TimerName = v),
                        FixedField("timer_seconds", "Timer seconds", () => a.TimerSeconds, v => a.TimerSeconds = v),
                        FixedField("amount", "Amount", () => a.Amount, v => a.Amount = v),
                        IntField("value", "Value", int.MinValue, int.MaxValue, () => a.Value, v => a.Value = v),
                        OptText("variable", "Variable", () => a.Variable, v => a.Variable = v),
                        OptText("sound_id", "Sound id", () => a.SoundId, v => a.SoundId = v),
                    };

                case ExprLiteralNode l:
                    return new[]
                    {
                        new NodeFieldDef("type", "Type", NodeFieldEditorKind.Choice,
                            () => LiteralTypeName(l.ValueType),
                            v => SetLiteralType(l, v),
                            LiteralTypes),
                        LiteralValueField(l),
                    };

                case ExprVarNode ev:
                    return new[]
                    {
                        // An empty variable name parses fine (the gate rejects it LOCATED — work-in-progress
                        // stays a badge, per the palette posture), so it is not a field-level reject.
                        TextField("name", "Variable", () => ev.Name, v => ev.Name = v),
                        IntField("faction", "Player slot", -1, DslVarTable.PlayerSlots - 1, () => ev.Faction, v => ev.Faction = v),
                    };

                case ExprUnaryNode eu:
                    return new[] { ChoiceField("op", "Op", NodeKinds.ExprUnaryOps, () => eu.Op, v => eu.Op = v) };

                case ExprBinaryNode eb:
                    return new[] { ChoiceField("op", "Op", NodeKinds.ExprBinaryOps, () => eb.Op, v => eb.Op = v) };

                case ExprCallNode ec:
                    return CallFields(ec);

                case ForEachNode fe:
                    return new[]
                    {
                        ChoiceField("source", "Source", NodeKinds.ForEachSources, () => fe.Source, v => fe.Source = v),
                        OptText("array_name", "Array name", () => fe.ArrayName, v => fe.ArrayName = v),
                        IntField("faction", "Faction", -1, DslVarTable.PlayerSlots - 1, () => fe.Faction, v => fe.Faction = v),
                        OptText("region_id", "Region id", () => fe.RegionId, v => fe.RegionId = v),
                        IntField("up_to", "Up to", 0, DslBounds.MaxForEachItems, () => fe.UpTo, v => fe.UpTo = v),
                        OptText("loop_var", "Loop var", () => fe.LoopVar, v => fe.LoopVar = v),
                    };

                case ForEachBatchedNode fb:
                    return new[]
                    {
                        ChoiceField("source", "Source", BatchedSources, () => fb.Source, v => fb.Source = v),
                        IntField("faction", "Faction", -1, DslVarTable.PlayerSlots - 1, () => fb.Faction, v => fb.Faction = v),
                        OptText("region_id", "Region id", () => fb.RegionId, v => fb.RegionId = v),
                        IntField("batch_size", "Batch size", 0, DslBounds.MaxForEachItems, () => fb.BatchSize, v => fb.BatchSize = v),
                    };

                case ExprArrayGetNode ag:
                    return new[] { TextField("name", "Array", () => ag.Name, v => ag.Name = v) };

                case ExprArrayLenNode al:
                    return new[] { TextField("name", "Array", () => al.Name, v => al.Name = v) };

                case ExprEventParamNode ep:
                    // parse REJECTS an empty param name — field-level reject (the round-trip-brick rule).
                    return new[] { ReqText("name", "Param name", () => ep.Name, v => ep.Name = v) };

                // ── Story 7.13 action leaves ──

                case OrderUnitsNode ou:
                    return new[]
                    {
                        ChoiceField("command", "Command", NodeKinds.OrderCommands, () => ou.Command, v => ou.Command = v),
                        FactionField("faction", () => ou.Faction, v => ou.Faction = v),
                        OptText("region_id", "Region id", () => ou.RegionId, v => ou.RegionId = v),
                        FixedField("x", "X", () => ou.X, v => ou.X = v),
                        FixedField("z", "Z", () => ou.Z, v => ou.Z = v),
                    };

                case MoveCameraNode mc:
                    return new[] { TextField("camera_name", "Camera", () => mc.CameraName, v => mc.CameraName = v) };

                case CinematicModeNode cm:
                    return new[] { BoolField("enabled", "Enabled", () => cm.Enabled, v => cm.Enabled = v) };

                case PlayVfxNode pv:
                    return new[]
                    {
                        TextField("vfx_id", "VFX id", () => pv.VfxId, v => pv.VfxId = v),
                        FixedField("x", "X", () => pv.X, v => pv.X = v),
                        FixedField("z", "Z", () => pv.Z, v => pv.Z = v),
                    };

                case RandomChoiceNode rc:
                    return new[]
                    {
                        new NodeFieldDef("weights", "Weights", NodeFieldEditorKind.IntList,
                            () => JoinInts(rc.Weights),
                            v => SetWeights(rc, v)),
                    };

                case EnableTriggerNode en:
                    return new[] { TargetTriggerField(() => en.TargetTriggerId, v => en.TargetTriggerId = v) };

                case DisableTriggerNode di:
                    return new[] { TargetTriggerField(() => di.TargetTriggerId, v => di.TargetTriggerId = v) };

                case RunTriggerNode rt:
                    return new[] { TargetTriggerField(() => rt.TargetTriggerId, v => rt.TargetTriggerId = v) };

                // ── Story 7.14 objective leaves (parse rejects a BLANK objective id — field-level reject) ──

                case ShowObjectiveNode so:
                    return new[] { ObjectiveIdField(() => so.ObjectiveId, v => so.ObjectiveId = v) };

                case CompleteObjectiveNode co:
                    return new[] { ObjectiveIdField(() => co.ObjectiveId, v => co.ObjectiveId = v) };

                case FailObjectiveNode fo:
                    return new[] { ObjectiveIdField(() => fo.ObjectiveId, v => fo.ObjectiveId = v) };

                // BranchNode + EffectActionNode: no editable payload (run_effect's embedded effect subgraph is
                // authored through the ability-editor pattern, not this inspector).
                default:
                    return NoFields;
            }
        }

        // ── expr_call: fn + a per-fn selector field (closed choice for tag/category/resource, free text for
        //    region, ABSENT for the selector-less builtins — a stray selector is a located compile reject). ──

        private static IReadOnlyList<NodeFieldDef> CallFields(ExprCallNode ec)
        {
            var fields = new List<NodeFieldDef>
            {
                new NodeFieldDef("fn", "Fn", NodeFieldEditorKind.Choice,
                    () => ec.Fn,
                    v =>
                    {
                        string? err = CheckChoice(v, NodeKinds.ExprCallFns);
                        if (err != null) return err;
                        ec.Fn = v;
                        // A selector only means something on a selector fn — clear it on switch so the node
                        // never strands a stray selector the compiler would reject located.
                        if (!NodeKinds.FnUsesSelector(v)) ec.Selector = "";
                        return null;
                    },
                    NodeKinds.ExprCallFns),
            };

            string[]? selectorChoices = ec.Fn switch
            {
                "unit_count_tag"      => TagSelectors,
                "unit_count_category" => CategorySelectors,
                "player_resource"     => ResourceSelectors,
                _                     => null,
            };
            if (selectorChoices != null)
            {
                fields.Add(new NodeFieldDef("selector", "Selector", NodeFieldEditorKind.Choice,
                    () => ec.Selector,
                    v =>
                    {
                        string? err = CheckChoice(v, selectorChoices);
                        if (err != null) return err;
                        ec.Selector = v;
                        return null;
                    },
                    selectorChoices));
            }
            else if (ec.Fn == "region_unit_count")
            {
                // Free-form: the region selector resolves at RUNTIME via RegionStore (never at compile).
                fields.Add(new NodeFieldDef("selector", "Region", NodeFieldEditorKind.Text,
                    () => ec.Selector,
                    v => { ec.Selector = v; return null; }));
            }
            return fields;
        }

        // ── expr_literal: the type switch re-encodes the CURRENT value into the new type (value-preserving,
        //    integer math only); the value field parses/renders per the CURRENT type. ──

        private static string LiteralTypeName(DslValueType t) => t switch
        {
            DslValueType.Fixed => "Fixed",
            DslValueType.Bool  => "Bool",
            _                  => "Int",
        };

        private static string? SetLiteralType(ExprLiteralNode l, string typeName)
        {
            string? err = CheckChoice(typeName, LiteralTypes);
            if (err != null) return err;
            DslValueType next = typeName switch
            {
                "Fixed" => DslValueType.Fixed,
                "Bool"  => DslValueType.Bool,
                _       => DslValueType.Int,
            };
            if (next == l.ValueType) return null;

            int raw = l.Raw;
            switch (l.ValueType)
            {
                case DslValueType.Int:
                    if (next == DslValueType.Fixed)
                    {
                        // An Int literal beyond the 16.16 integer range cannot be re-encoded losslessly.
                        if (raw > 32767 || raw < -32768)
                            return $"{raw} is outside the Fixed range [-32768, 32768) — edit the value first.";
                        raw <<= 16;
                    }
                    else raw = raw != 0 ? 1 : 0; // → Bool
                    break;
                case DslValueType.Fixed:
                    if (next == DslValueType.Int)
                    {
                        // Round-half-away-from-zero on the magnitude (the ReadExprFixedExact rounding family).
                        long mag = raw < 0 ? -(long)raw : raw;
                        long rounded = (mag + (Fixed.ONE / 2)) >> 16;
                        raw = (int)(raw < 0 ? -rounded : rounded);
                    }
                    else raw = raw != 0 ? 1 : 0; // → Bool
                    break;
                default: // Bool (raw already 0/1)
                    if (next == DslValueType.Fixed) raw <<= 16;
                    break;
            }
            l.ValueType = next;
            l.Raw = raw;
            return null;
        }

        private static NodeFieldDef LiteralValueField(ExprLiteralNode l)
        {
            switch (l.ValueType)
            {
                case DslValueType.Fixed:
                    return new NodeFieldDef("value", "Value", NodeFieldEditorKind.Fixed,
                        () => RenderFixedRaw(l.Raw),
                        v =>
                        {
                            string? err = TryParseFixedRaw(v, contentField: false, out int raw);
                            if (err != null) return err;
                            l.Raw = raw;
                            return null;
                        });
                case DslValueType.Bool:
                    return new NodeFieldDef("value", "Value", NodeFieldEditorKind.Bool,
                        () => l.Raw != 0 ? "true" : "false",
                        v =>
                        {
                            string? err = TryParseBool(v, out bool b);
                            if (err != null) return err;
                            l.Raw = b ? 1 : 0;
                            return null;
                        });
                default:
                    return new NodeFieldDef("value", "Value", NodeFieldEditorKind.Int,
                        () => l.Raw.ToString(CultureInfo.InvariantCulture),
                        v =>
                        {
                            string? err = TryParseInt(v, int.MinValue, int.MaxValue, out int i);
                            if (err != null) return err;
                            l.Raw = i;
                            return null;
                        });
            }
        }

        // ── Field factories ─────────────────────────────────────────────────────

        private static NodeFieldDef TextField(string key, string label, Func<string> get, Action<string> apply)
            => new(key, label, NodeFieldEditorKind.Text, get, v => { apply(v); return null; });

        /// <summary>An optional string field: empty input stores null (the converter omits null — the canonical
        /// missing==null mirror), so clearing a field truly clears it from the stored JSON.</summary>
        private static NodeFieldDef OptText(string key, string label, Func<string?> get, Action<string?> apply)
            => new(key, label, NodeFieldEditorKind.Text,
                () => get() ?? "",
                v => { apply(v.Length == 0 ? null : v); return null; });

        /// <summary>A REQUIRED non-empty string field (serialize or re-parse fails closed on empty — accepting
        /// one would brick the stored graph channel, so it is a field-level reject).</summary>
        private static NodeFieldDef ReqText(string key, string label, Func<string> get, Action<string> apply)
            => new(key, label, NodeFieldEditorKind.Text, get,
                v =>
                {
                    if (string.IsNullOrWhiteSpace(v)) return $"{label} is required (must not be empty).";
                    apply(v);
                    return null;
                });

        private static NodeFieldDef IntField(string key, string label, int min, int max, Func<int> get, Action<int> apply)
            => new(key, label, NodeFieldEditorKind.Int,
                () => get().ToString(CultureInfo.InvariantCulture),
                v =>
                {
                    string? err = TryParseInt(v, min, max, out int i);
                    if (err != null) return err;
                    apply(i);
                    return null;
                });

        /// <summary>A faction-slot field: −1 = any/bare, else a 0-based player slot (the DslVarTable range).</summary>
        private static NodeFieldDef FactionField(string key, Func<int> get, Action<int> apply)
            => IntField(key, "Faction", -1, DslVarTable.PlayerSlots - 1, get, apply);

        private static NodeFieldDef BoolField(string key, string label, Func<bool> get, Action<bool> apply)
            => new(key, label, NodeFieldEditorKind.Bool,
                () => get() ? "true" : "false",
                v =>
                {
                    string? err = TryParseBool(v, out bool b);
                    if (err != null) return err;
                    apply(b);
                    return null;
                });

        /// <summary>A CONTENT Fixed field (persists through the float-quantizing FixedJsonConverter, AR-14).</summary>
        private static NodeFieldDef FixedField(string key, string label, Func<Fixed> get, Action<Fixed> apply)
            => new(key, label, NodeFieldEditorKind.Fixed,
                () => RenderFixedRaw(get().Raw),
                v =>
                {
                    string? err = TryParseFixedRaw(v, contentField: true, out int raw);
                    if (err != null) return err;
                    apply(Fixed.FromRaw(raw));
                    return null;
                });

        private static NodeFieldDef ChoiceField(string key, string label, string[] choices, Func<string> get, Action<string> apply)
            => new(key, label, NodeFieldEditorKind.Choice, get,
                v =>
                {
                    string? err = CheckChoice(v, choices);
                    if (err != null) return err;
                    apply(v);
                    return null;
                },
                choices);

        /// <summary>enable/disable/run_trigger target: parse REJECTS a negative id, so the field does too.</summary>
        private static NodeFieldDef TargetTriggerField(Func<int> get, Action<int> apply)
            => IntField("target_trigger", "Target trigger id", 0, int.MaxValue, get, apply);

        /// <summary>show/complete/fail_objective target: parse REJECTS a blank id, so the field does too.</summary>
        private static NodeFieldDef ObjectiveIdField(Func<string> get, Action<string> apply)
            => ReqText("objective_id", "Objective id", get, apply);

        // ── Parsers / renderers (invariant, integer math only — src/Dsl is float-free) ──────────────────────────

        private static string? TryParseInt(string input, int min, int max, out int value)
        {
            value = 0;
            string t = input.Trim();
            if (!int.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
                return $"'{input}' is not a whole number.";
            if (value < min || value > max)
                return $"{value} is out of range ({min}..{max}).";
            return null;
        }

        private static string? TryParseBool(string input, out bool value)
        {
            if (bool.TryParse(input.Trim(), out value)) return null;
            return $"'{input}' is not true/false.";
        }

        private static string? CheckChoice(string value, string[] choices)
        {
            for (int i = 0; i < choices.Length; i++)
                if (string.Equals(choices[i], value, StringComparison.Ordinal))
                    return null;
            return $"'{value}' is not one of: {string.Join(", ", choices)}.";
        }

        /// <summary>random_choice weights: comma-separated non-negative ints; empty clears (the factory default —
        /// the load gate badges an empty/zero-total set located, keeping the work-in-progress posture).</summary>
        private static string? SetWeights(RandomChoiceNode rc, string input)
        {
            string t = input.Trim();
            if (t.Length == 0) { rc.Weights = Array.Empty<int>(); return null; }
            string[] parts = t.Split(',');
            var result = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (!int.TryParse(p, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int w))
                    return $"'{p}' is not a whole number.";
                if (w < 0) return $"weight {w} is negative (each weight must be >= 0).";
                result[i] = w;
            }
            rc.Weights = result;
            return null;
        }

        private static string JoinInts(int[] values)
        {
            if (values.Length == 0) return "";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(values[i].ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>5^16 — the exact per-unit decimal weight of one 16.16 raw step (1/65536 = 152587890625e-16);
        /// the same constant <c>NodeBaseJsonConverter</c>'s exact expr codec uses.</summary>
        private const ulong FRAC_DECIMAL_WEIGHT = 152_587_890_625UL;

        /// <summary>Render a 16.16 raw as its EXACT terminating decimal (≤16 fraction digits, integer math only) —
        /// always re-parseable by <see cref="TryParseFixedRaw"/>, so Get output can round-trip through Set.</summary>
        private static string RenderFixedRaw(int raw)
        {
            ulong mag = raw < 0 ? (ulong)(-(long)raw) : (ulong)raw;
            ulong ip = mag >> 16;
            ulong fr = mag & 0xFFFF;
            string sign = raw < 0 ? "-" : "";
            if (fr == 0) return sign + ip.ToString(CultureInfo.InvariantCulture);
            string frac = (fr * FRAC_DECIMAL_WEIGHT).ToString("D16", CultureInfo.InvariantCulture).TrimEnd('0');
            return sign + ip.ToString(CultureInfo.InvariantCulture) + "." + frac;
        }

        /// <summary>
        /// Parse a plain Fixed decimal to its 16.16 raw with exact integer math (round-half-up on the magnitude —
        /// the <c>ReadExprFixedExact</c>/<c>ExprParser</c> rulebook; ≤16 fraction digits so every
        /// <see cref="RenderFixedRaw"/> output re-parses; no exponent). For CONTENT fields
        /// (<paramref name="contentField"/>) additionally rejects raws above <c>int.MaxValue − 256</c>: those
        /// values' nearest float is 32768f, which <c>FixedJsonConverter</c> would REJECT on reload — a
        /// persist-then-cannot-load brick this seam must never admit.
        /// </summary>
        private static string? TryParseFixedRaw(string input, bool contentField, out int raw)
        {
            raw = 0;
            string t = input.Trim();
            if (t.Length == 0) return "a number is required.";

            int pos = 0;
            bool neg = t[0] == '-';
            if (neg) pos++;

            long intPart = 0;
            int intDigits = 0;
            while (pos < t.Length && t[pos] >= '0' && t[pos] <= '9')
            {
                intDigits++;
                if (intPart <= 32768) intPart = intPart * 10 + (t[pos] - '0'); // saturating past the ceiling
                pos++;
            }
            if (intDigits == 0) return $"'{input}' is not a plain decimal number.";

            long frac = 0;
            int fracDigits = 0;
            if (pos < t.Length && t[pos] == '.')
            {
                pos++;
                while (pos < t.Length && t[pos] >= '0' && t[pos] <= '9')
                {
                    fracDigits++;
                    if (fracDigits > 16)
                        return "at most 16 fraction digits are supported (exact 16.16 values need 16).";
                    frac = frac * 10 + (t[pos] - '0');
                    pos++;
                }
                if (fracDigits == 0) return "a decimal point must be followed by at least one digit.";
            }
            if (pos != t.Length)
                return $"'{input}' is not a plain decimal (no exponent notation).";
            if (intPart > 32768)
                return $"{input} is out of the 16.16 range [-32768, 32768).";

            // raw = round_half_up((intPart + frac/10^k) * 65536), exact in Int128.
            Int128 pow10 = 1;
            for (int i = 0; i < fracDigits; i++) pow10 *= 10;
            Int128 mag = ((Int128)intPart * Fixed.ONE * pow10 + (Int128)frac * Fixed.ONE + pow10 / 2) / pow10;
            Int128 signed = neg ? -mag : mag;
            if (signed > int.MaxValue || signed < int.MinValue)
                return $"{input} is out of the 16.16 range [-32768, 32768).";
            if (contentField && signed > int.MaxValue - 256)
                return $"{input} is above the content Fixed ceiling (~32767.996) — the float save boundary would reject it on reload.";
            raw = (int)signed;
            return null;
        }
    }
}
