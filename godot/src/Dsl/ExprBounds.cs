#nullable enable
namespace ProjectChimera.Dsl
{
    /// <summary>
    /// Story 7.4 — the named cost caps for the expression sublanguage, in ONE place (the <c>EffectCaps</c> /
    /// <c>EffectBounds</c> style: named constants at a load-time gate, never inline literals). Every cap is
    /// enforced AT LOAD by <see cref="ExprCompiler"/> / <see cref="ExprParser"/> with a LOCATED error naming the
    /// constant — never silently truncated at runtime.
    ///
    /// These are corpus-validated dials, not free tuning knobs: raising one is a deliberate, recorded decision
    /// (it widens what a hostile/degenerate authored expression can make every peer evaluate per tick).
    /// </summary>
    public static class ExprBounds
    {
        /// <summary>
        /// Maximum postfix ops a single compiled <see cref="ExprProgram"/> may hold. An expression compiling to
        /// more ops is rejected at load (located error naming MaxExprOps). 64 comfortably covers WC3-class
        /// scoreboard/gate logic while bounding per-tick evaluation cost.
        /// </summary>
        public const int MaxExprOps = 64;

        /// <summary>
        /// Maximum expression-node nesting depth (root = depth 1). Deeper subgraphs are rejected at load
        /// (located error naming MaxExprDepth). Also bounds compiler/parser recursion, so a cyclic or maliciously
        /// deep authored subgraph rejects instead of overflowing the stack.
        /// </summary>
        public const int MaxExprDepth = 16;

        /// <summary>
        /// Maximum length (chars) of an authored expression TEXT the <see cref="ExprParser"/> accepts. Longer
        /// input is rejected before tokenizing (located error naming MaxExprTextLength).
        /// </summary>
        public const int MaxExprTextLength = 512;
    }
}
