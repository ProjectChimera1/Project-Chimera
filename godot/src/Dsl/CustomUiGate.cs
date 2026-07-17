#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core.Definitions; // CustomUiTree / WidgetBase / AnchorPoint

namespace ProjectChimera.Dsl
{
    /// <summary>
    /// Story 7.8 — the WHOLE-TREE structural + binding rulebook for the custom-UI widget tree, shared — like
    /// <see cref="GraphStructureGate"/> — by BOTH load gates: <c>ScenarioValidator</c> (the authoritative pre-tick
    /// gate, wrapping the return in <c>ValidationResult.Fail</c>) and <c>ScenarioDirector.LoadScenario</c> (the
    /// fail-closed backstop for direct callers, wrapping it in a located <c>JsonException</c>). ONE implementation,
    /// invoked at both, so the rules are identical by construction.
    ///
    /// Checks, in deterministic first-fail (pre-order) order:
    ///   • widget count ≤ <see cref="DslBounds.MaxWidgetCount"/> — rejected AT LOAD naming the constant, never
    ///     clamped (the renderer additionally asserts it);
    ///   • nesting depth ≤ <see cref="DslBounds.MaxWidgetDepth"/> (root = depth 1) — rejected naming the constant;
    ///   • duplicate widget ids reject;
    ///   • anchor validity (a defined 9-point <see cref="AnchorPoint"/>);
    ///   • a data-bound repeater's authored row cap ≤ <see cref="DslBounds.MaxListRows"/> — rejected naming the
    ///     constant;
    ///   • every <c>{variable}</c> bind resolves against the declared-variable registry AND type-matches: a scalar-
    ///     display widget (Label/Counter/ProgressBar/Timer) binds Int/Fixed; a repeater (Leaderboard/ItemList) binds
    ///     an <c>Array&lt;scalar&gt;</c>; a visibility bind resolves to a truthy scalar (Int/Fixed/Bool). An
    ///     UNDECLARED name, or a <see cref="VarScope.TriggerLocal"/>-scoped one (per-firing scratch, never in the
    ///     read rail), or a type mismatch is a located reject <c>scenario.custom_ui.widgets[i]…</c>.
    ///
    /// Pure, Godot-free, Fixed/int-only; every reject is a LOCATED error. Returns the first located error, or null when
    /// the tree is sound (or <paramref name="tree"/> is null — nothing to check).
    /// </summary>
    public static class CustomUiGate
    {
        public static string? Check(
            CustomUiTree? tree,
            IReadOnlyDictionary<string, (DslValueType Type, VarScope Scope)> declaredVarInfo,
            IReadOnlyDictionary<string, (DslValueType Elem, int Capacity)> declaredArrayInfo)
        {
            if (tree is null) return null; // absent custom_ui — nothing to validate

            var seenIds = new HashSet<int>();
            int count = 0;
            WidgetBase[] roots = tree.Widgets ?? Array.Empty<WidgetBase>();
            for (int i = 0; i < roots.Length; i++)
            {
                string? err = CheckWidget(roots[i], $"scenario.custom_ui.widgets[{i}]", depth: 1,
                    seenIds, ref count, declaredVarInfo, declaredArrayInfo);
                if (err != null) return err;
            }
            return null;
        }

        private static string? CheckWidget(
            WidgetBase? w, string path, int depth,
            HashSet<int> seenIds, ref int count,
            IReadOnlyDictionary<string, (DslValueType Type, VarScope Scope)> declaredVarInfo,
            IReadOnlyDictionary<string, (DslValueType Elem, int Capacity)> declaredArrayInfo)
        {
            if (w is null) return $"{path} is null.";

            // ── Caps (rejected at load, never clamped). ──
            if (++count > DslBounds.MaxWidgetCount)
                return $"{path}: custom UI has more than DslBounds.MaxWidgetCount={DslBounds.MaxWidgetCount} widgets.";
            if (depth > DslBounds.MaxWidgetDepth)
                return $"{path}: widget nesting exceeds DslBounds.MaxWidgetDepth={DslBounds.MaxWidgetDepth}.";

            // ── Duplicate ids (whole-tree). ──
            if (!seenIds.Add(w.Id))
                return $"{path}.id={w.Id} is a duplicate (widget ids must be unique within the tree).";

            // ── Anchor validity (belt-and-suspenders; the converter already gates the enum). ──
            if (!Enum.IsDefined(w.Anchor))
                return $"{path}.anchor='{w.Anchor}' is not a defined 9-point anchor.";

            // ── Repeater row cap. ──
            if (w.ExpectsArrayBind && w.MaxRows > DslBounds.MaxListRows)
                return $"{path}.rows={w.MaxRows} exceeds DslBounds.MaxListRows={DslBounds.MaxListRows}.";

            // ── Visibility bind (optional) — a declared truthy scalar (Int/Fixed/Bool), never TriggerLocal. ──
            if (w.VisibleBind is string vb)
            {
                string? err = CheckScalarBind(vb, $"{path}.visible_bind", declaredVarInfo,
                    allowBool: true, kindNoun: "visibility bind");
                if (err != null) return err;
            }

            // ── Value bind (optional) — scalar for display widgets, Array<scalar> for repeaters. ──
            if (w.ValueBind is string bind)
            {
                if (w.ExpectsArrayBind)
                {
                    string? err = CheckArrayBind(bind, $"{path}.bind", declaredVarInfo, declaredArrayInfo);
                    if (err != null) return err;
                }
                else
                {
                    string? err = CheckScalarBind(bind, $"{path}.bind", declaredVarInfo,
                        allowBool: false, kindNoun: "value bind");
                    if (err != null) return err;
                }
            }

            // ── Recurse children (depth + 1). ──
            WidgetBase[] children = w.Children ?? Array.Empty<WidgetBase>();
            for (int i = 0; i < children.Length; i++)
            {
                string? err = CheckWidget(children[i], $"{path}.children[{i}]", depth + 1,
                    seenIds, ref count, declaredVarInfo, declaredArrayInfo);
                if (err != null) return err;
            }
            return null;
        }

        /// <summary>Resolve a scalar bind: declared, non-TriggerLocal, and Int/Fixed (plus Bool when
        /// <paramref name="allowBool"/> — visibility binds).</summary>
        private static string? CheckScalarBind(
            string name, string path,
            IReadOnlyDictionary<string, (DslValueType Type, VarScope Scope)> declaredVarInfo,
            bool allowBool, string kindNoun)
        {
            if (!declaredVarInfo.TryGetValue(name, out (DslValueType Type, VarScope Scope) info))
                return $"{path}: {kindNoun} '{name}' is not a declared variable (unresolved bind).";
            if (info.Scope == VarScope.TriggerLocal)
                return $"{path}: {kindNoun} '{name}' is TriggerLocal-scoped (per-firing scratch is never in the read rail).";
            bool ok = info.Type == DslValueType.Int || info.Type == DslValueType.Fixed
                      || (allowBool && info.Type == DslValueType.Bool);
            if (!ok)
                return $"{path}: {kindNoun} '{name}' is {info.Type}-typed — a {kindNoun} must be " +
                       (allowBool ? "Int/Fixed/Bool." : "Int or Fixed.");
            return null;
        }

        /// <summary>Resolve a repeater bind: declared as an <c>Array&lt;scalar&gt;</c> variable.</summary>
        private static string? CheckArrayBind(
            string name, string path,
            IReadOnlyDictionary<string, (DslValueType Type, VarScope Scope)> declaredVarInfo,
            IReadOnlyDictionary<string, (DslValueType Elem, int Capacity)> declaredArrayInfo)
        {
            if (!declaredVarInfo.ContainsKey(name) && !declaredArrayInfo.ContainsKey(name))
                return $"{path}: repeater bind '{name}' is not a declared variable (unresolved bind).";
            if (!declaredArrayInfo.ContainsKey(name))
                return $"{path}: repeater bind '{name}' is not an Array-typed variable (a Leaderboard/ItemList binds Array<scalar>).";
            return null;
        }
    }
}
