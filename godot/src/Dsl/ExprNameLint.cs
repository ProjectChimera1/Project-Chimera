#nullable enable
using System;

namespace ProjectChimera.Dsl
{
    /// <summary>The lint verdict for a declared-variable name (see <see cref="ExprNameLint"/>).</summary>
    public enum ExprNameLintVerdict
    {
        /// <summary>The name has no collision with the expression grammar.</summary>
        Clean,
        /// <summary>Declarable, but flagged: expression text either cannot spell the name (non-identifier
        /// characters) or a built-in wins in SOME position (<c>count(</c> / <c>length(</c> / <c>event.</c>).
        /// The variable stays fully usable from pickers, flat triggers, and raw-IR <c>expr_var</c> nodes,
        /// and a colliding use fails LOUDLY at expression parse — so the declaration proceeds with a warning.</summary>
        Warn,
        /// <summary>Refuse the declaration: in expression text the name ALWAYS parses as something else
        /// (the Bool literals <c>true</c>/<c>false</c>), so a same-named variable silently reads the wrong
        /// value — it even type-checks. The one silently-diverging class.</summary>
        Reject,
    }

    /// <summary>
    /// DW-343 (decision 2026-07-30: lint/warn at declaration) — the AUTHORING-SURFACE name lint for DSL variable
    /// declarations. Godot-free, allocation-light, pure.
    ///
    /// The Story 7.3 name policy accepts ANY non-empty unique string, and the LOAD gate must KEEP doing so —
    /// existing scenarios may already carry exotic names, which flat triggers (picker-carried names) and raw-IR
    /// <c>expr_var</c> nodes reference fine. But CEL-shaped expression TEXT (<see cref="ExprParser"/>) reads a
    /// closed grammar that resolves keywords/built-ins BEFORE variables, so some declarable names are partially
    /// or wholly unreferenceable from text. This classifier tells the declaration UI (the Trigger Editor's
    /// variables section) which names to refuse and which to accept-with-a-warning. It is deliberately NOT wired
    /// into <c>ScenarioValidator</c>: the gate stays permissive so no legacy scenario stops loading (the ledger's
    /// recorded back-compat constraint).
    ///
    /// Classification (matching is case-sensitive Ordinal, exactly like the grammar):
    ///   • <c>true</c>/<c>false</c> → <see cref="ExprNameLintVerdict.Reject"/> — always the Bool literal in text;
    ///     a same-named variable silently reads the literal (no error, type-checks as Bool).
    ///   • non-identifier spellings → <see cref="ExprNameLintVerdict.Warn"/> — text cannot produce the token at
    ///     all (parse fails loudly if attempted); pickers/raw IR still work.
    ///   • <c>event</c> / <c>length</c> / the closed <c>NodeKinds.ExprCallFns</c> vocabulary →
    ///     <see cref="ExprNameLintVerdict.Warn"/> — shadowed only in one position (<c>event.</c> prefix or
    ///     call position); the bare name still reads the variable. The 7.13 state-read fns are included even
    ///     though the TEXT parser dispatches only count/distance/min/max/abs today: they are the closed graph
    ///     vocabulary, so a colliding name is a footgun now (graph channel) or later (text growth).
    /// </summary>
    public static class ExprNameLint
    {
        /// <summary>
        /// Classify a would-be declared variable name against the expression grammar. Returns the verdict and a
        /// human-readable <paramref name="message"/> ("" when <see cref="ExprNameLintVerdict.Clean"/>). A null or
        /// empty name returns Clean — non-emptiness is the declaration gate's existing rule, not this lint's.
        /// </summary>
        public static ExprNameLintVerdict CheckVariableName(string? name, out string message)
        {
            message = "";
            if (string.IsNullOrEmpty(name)) return ExprNameLintVerdict.Clean;

            // The silently-diverging pair: `flag == true` with a variable named "true" reads the LITERAL.
            if (string.Equals(name, "true", StringComparison.Ordinal)
                || string.Equals(name, "false", StringComparison.Ordinal))
            {
                message = $"'{name}' is the Bool literal in expression text — a variable with this name can never " +
                          "be read there (the literal always wins, silently). Pick another name.";
                return ExprNameLintVerdict.Reject;
            }

            // Names the tokenizer cannot produce: unusable from text entirely, but pickers/flat/raw-IR still work.
            if (!ExprParser.IsIdentifier(name))
            {
                message = $"'{name}' cannot be spelled in expression text (identifiers are letters/digits/underscore, " +
                          "not digit-leading). It stays usable from pickers and Raw IR.";
                return ExprNameLintVerdict.Warn;
            }

            // Position-shadowed keywords: the bare name still reads the variable.
            if (string.Equals(name, "event", StringComparison.Ordinal))
            {
                message = "'event' shadows the event-parameter prefix: 'event.<param>' in expression text is always " +
                          "a parameter read. The bare name still reads this variable.";
                return ExprNameLintVerdict.Warn;
            }
            if (string.Equals(name, "length", StringComparison.Ordinal))
            {
                message = "'length' shadows the array-length built-in: 'length(...)' in expression text is always " +
                          "the built-in. The bare name still reads this variable.";
                return ExprNameLintVerdict.Warn;
            }

            // The closed call-fn vocabulary (NodeKinds.ExprCallFns — the single source of truth, so this lint can
            // never drift from the grammar when a future story appends a built-in).
            foreach (string fn in NodeKinds.ExprCallFns)
            {
                if (string.Equals(name, fn, StringComparison.Ordinal))
                {
                    message = $"'{name}' shadows the built-in function '{fn}': '{name}(...)' in expression text is " +
                              "always the built-in. The bare name still reads this variable.";
                    return ExprNameLintVerdict.Warn;
                }
            }

            return ExprNameLintVerdict.Clean;
        }
    }
}
