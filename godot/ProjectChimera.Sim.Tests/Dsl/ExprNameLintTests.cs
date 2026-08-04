#nullable enable
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// DW-343 (decision 2026-07-30: lint/warn at declaration) — the authoring-surface name lint. The Story 7.3
    /// name policy accepts any non-empty unique string, but CEL-shaped expression text resolves keywords and
    /// built-ins BEFORE variables, so some declarable names are partially or wholly unreferenceable from text.
    /// <see cref="ExprNameLint"/> classifies a would-be declaration; the Trigger Editor refuses Reject-classified
    /// names and declares Warn-classified ones with a note. These tests pin the classification, WITHOUT which the
    /// editor would keep silently declaring 'true'/'false' traps (the DW-343 defect).
    /// </summary>
    public class ExprNameLintTests
    {
        // ── Reject: the silently-diverging pair (in text, the literal ALWAYS wins — it even type-checks) ──

        [Theory]
        [InlineData("true")]
        [InlineData("false")]
        public void BoolLiteralNames_AreRejected(string name)
        {
            ExprNameLintVerdict v = ExprNameLint.CheckVariableName(name, out string msg);
            Assert.Equal(ExprNameLintVerdict.Reject, v);
            Assert.Contains("literal", msg);
        }

        // ── Warn: the closed built-in call vocabulary (7.4 text fns + the 7.13 graph fns), 'length', 'event' ──

        [Theory]
        [InlineData("count")]
        [InlineData("distance")]
        [InlineData("min")]
        [InlineData("max")]
        [InlineData("abs")]
        [InlineData("entity_hp")]
        [InlineData("entity_owner")]
        [InlineData("entity_position")]
        [InlineData("unit_count_tag")]
        [InlineData("unit_count_category")]
        [InlineData("player_resource")]
        [InlineData("region_unit_count")]
        public void BuiltInFunctionNames_WarnButDeclare(string name)
        {
            ExprNameLintVerdict v = ExprNameLint.CheckVariableName(name, out string msg);
            Assert.Equal(ExprNameLintVerdict.Warn, v);
            Assert.Contains("built-in", msg);
        }

        [Fact]
        public void LengthName_Warns_AsTheArrayLengthBuiltIn()
        {
            Assert.Equal(ExprNameLintVerdict.Warn, ExprNameLint.CheckVariableName("length", out string msg));
            Assert.Contains("length(", msg);
        }

        [Fact]
        public void EventName_Warns_AsTheEventParamPrefix()
        {
            Assert.Equal(ExprNameLintVerdict.Warn, ExprNameLint.CheckVariableName("event", out string msg));
            Assert.Contains("event.", msg);
        }

        // ── Warn: names the expression tokenizer cannot produce at all (loud parse failure if attempted) ──

        [Theory]
        [InlineData("my var")]    // space
        [InlineData("hp-total")]  // hyphen (reads as subtraction)
        [InlineData("1abc")]      // digit-leading
        [InlineData("a.b")]       // dot (member-access shaped)
        [InlineData("vär")]       // non-ASCII letter — outside the grammar's [A-Za-z_] identifier set
        public void NonIdentifierNames_WarnButDeclare(string name)
        {
            ExprNameLintVerdict v = ExprNameLint.CheckVariableName(name, out string msg);
            Assert.Equal(ExprNameLintVerdict.Warn, v);
            Assert.Contains("identifier", msg);
        }

        // ── Clean: ordinary identifiers, and CASE-SENSITIVITY (the grammar is Ordinal — 'True' is a variable) ──

        [Theory]
        [InlineData("gold")]
        [InlineData("_x")]
        [InlineData("wave2")]
        [InlineData("True")]   // case-sensitive: not the literal
        [InlineData("COUNT")]  // case-sensitive: not the built-in
        [InlineData("Event")]  // case-sensitive: not the prefix
        [InlineData("lengths")] // longer than the keyword — a plain identifier
        public void OrdinaryIdentifiers_AreClean(string name)
        {
            ExprNameLintVerdict v = ExprNameLint.CheckVariableName(name, out string msg);
            Assert.Equal(ExprNameLintVerdict.Clean, v);
            Assert.Equal("", msg);
        }

        // ── Null/empty is NOT this lint's rule (the declaration gate already refuses empty names) ──

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void NullOrEmpty_IsClean_OwnedByTheDeclarationGate(string? name)
        {
            Assert.Equal(ExprNameLintVerdict.Clean, ExprNameLint.CheckVariableName(name, out _));
        }

        // ── The lint's built-in set can never drift from the grammar's closed vocabulary: every ExprCallFns
        //    entry must classify Warn (a future story appending a built-in gets lint coverage for free — this
        //    tooth fails only if the lint stops reading the shared list). ──

        [Fact]
        public void EveryClosedVocabularyFn_IsWarnClassified()
        {
            foreach (string fn in NodeKinds.ExprCallFns)
            {
                Assert.Equal(ExprNameLintVerdict.Warn, ExprNameLint.CheckVariableName(fn, out string msg));
                Assert.Contains(fn, msg);
            }
        }
    }
}
