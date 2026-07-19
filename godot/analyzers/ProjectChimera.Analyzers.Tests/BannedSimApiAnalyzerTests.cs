#nullable enable
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ProjectChimera.Analyzers.Tests
{
    /// <summary>
    /// TDD coverage for every <see cref="BannedSimApiAnalyzer"/> rule (CHM0001..CHM0006), each with a positive
    /// (must fire) and a negative (must NOT fire) case so a vacuous pass is impossible. Snippets are crafted to
    /// isolate the rule under test; assertions are on specific diagnostic IDs via Contains/DoesNotContain.
    /// </summary>
    public class BannedSimApiAnalyzerTests
    {
        private static Task<string[]> Ids(string source)
            => AnalyzerTestHarness.GetIdsAsync(source, new BannedSimApiAnalyzer());

        private static Task<ImmutableArray<Diagnostic>> Diags(string source)
            => AnalyzerTestHarness.GetDiagnosticsAsync(source, new BannedSimApiAnalyzer());

        // A minimal Fixed mirroring ProjectChimera.Core.Fixed so the semantic model resolves FromFloat/ToFloat.
        private const string FixedDef =
            "namespace ProjectChimera.Core { public struct Fixed { " +
            "public static Fixed FromFloat(float v) => default; public float ToFloat() => 0f; } }\n";

        // ── CHM0001 — float/double primitive ban ──────────────────────────────────────────────

        [Fact]
        public async Task Float_field_declaration_reports_CHM0001()
        {
            string[] ids = await Ids("public class C { float speed; }");
            Assert.Contains("CHM0001", ids);
        }

        [Fact]
        public async Task Double_cast_reports_CHM0001()
        {
            string[] ids = await Ids("public class C { public object M(int x) => (double)x; }");
            Assert.Contains("CHM0001", ids);
        }

        [Fact]
        public async Task Fixed_only_code_does_not_report_CHM0001()
        {
            string[] ids = await Ids(
                "namespace ProjectChimera.Core { public struct Fixed { public int Raw; } }\n" +
                "public class C { ProjectChimera.Core.Fixed value; }");
            Assert.DoesNotContain("CHM0001", ids);
        }

        [Fact]
        public async Task FloatMemberAccess_does_not_double_report_CHM0001()
        {
            // `float.Parse` uses the float keyword as a member-access receiver — RS0030 (off-the-shelf) owns
            // that; CHM0001 deliberately skips it to avoid double-reporting the same site.
            string[] ids = await Ids("public class C { public object M(string s) => float.Parse(s); }");
            Assert.DoesNotContain("CHM0001", ids);
        }

        [Fact]
        public async Task NullableFloat_field_reports_CHM0001()
        {
            string[] ids = await Ids("public class C { float? speed; }");
            Assert.Contains("CHM0001", ids);
        }

        [Fact]
        public async Task ListOfFloat_field_reports_CHM0001()
        {
            string[] ids = await Ids(
                "using System.Collections.Generic; public class C { List<float> xs; }");
            Assert.Contains("CHM0001", ids);
        }

        [Fact]
        public async Task Tuple_float_element_reports_CHM0001()
        {
            string[] ids = await Ids("public class C { public void M((float a, int b) t) { } }");
            Assert.Contains("CHM0001", ids);
        }

        [Fact]
        public async Task Lambda_float_parameter_reports_CHM0001()
        {
            string[] ids = await Ids(
                "using System; public class C { public void M() { System.Func<float,float> f = (float x) => x; } }");
            Assert.Contains("CHM0001", ids);
        }

        [Fact]
        public async Task SystemSingle_field_reports_CHM0001()
        {
            // Fully-qualified System.Single is a type reference the keyword path cannot see — the IdentifierName path owns it.
            string[] ids = await Ids("public class C { System.Single value; }");
            Assert.Contains("CHM0001", ids);
        }

        [Fact]
        public async Task Var_inferred_float_reports_CHM0001()
        {
            string[] ids = await Ids("public class C { public void M() { var x = 1f; } }");
            Assert.Contains("CHM0001", ids);
        }

        [Fact]
        public async Task Var_inferred_double_reports_CHM0001()
        {
            string[] ids = await Ids("public class C { public void M() { var d = 1.0; } }");
            Assert.Contains("CHM0001", ids);
        }

        [Fact]
        public async Task SystemSingleMemberAccess_does_not_report_CHM0001()
        {
            // `System.Single.Parse` / `Single.MaxValue` are member access — owned by RS0030/CHM0006, not CHM0001.
            string[] ids = await Ids("public class C { public object M(string s) => System.Single.Parse(s); }");
            Assert.DoesNotContain("CHM0001", ids);
        }

        [Fact]
        public async Task Var_inferred_int_does_not_report_CHM0001()
        {
            // The var path is float/double-only — a non-float inferred local must stay clean (pins the SpecialType guard).
            string[] ids = await Ids("public class C { public void M() { var x = 1; } }");
            Assert.DoesNotContain("CHM0001", ids);
        }

        [Fact]
        public async Task Nameof_single_does_not_report_CHM0001()
        {
            // Bare `Single` (using System) is an IdentifierName — not member access — so only the nameof guard can
            // exempt it. nameof yields the string "Single" and computes no float value; it must not fire.
            string[] ids = await Ids("using System; public class C { public string M() => nameof(Single); }");
            Assert.DoesNotContain("CHM0001", ids);
        }

        // ── CHM0002 — Dictionary/HashSet enumeration ──────────────────────────────────────────

        [Fact]
        public async Task Foreach_over_dictionary_reports_CHM0002()
        {
            string[] ids = await Ids(
                "using System.Collections.Generic; public class C { " +
                "public void M(Dictionary<int,int> d) { foreach (var kv in d) { } } }");
            Assert.Contains("CHM0002", ids);
        }

        [Fact]
        public async Task Foreach_over_hashset_reports_CHM0002()
        {
            string[] ids = await Ids(
                "using System.Collections.Generic; public class C { " +
                "public void M(HashSet<int> h) { foreach (var x in h) { } } }");
            Assert.Contains("CHM0002", ids);
        }

        [Fact]
        public async Task Foreach_over_list_does_not_report_CHM0002()
        {
            string[] ids = await Ids(
                "using System.Collections.Generic; public class C { " +
                "public void M(List<int> list) { foreach (var x in list) { } } }");
            Assert.DoesNotContain("CHM0002", ids);
        }

        [Fact]
        public async Task Foreach_over_dictionary_keys_reports_CHM0002()
        {
            string[] ids = await Ids(
                "using System.Collections.Generic; public class C { " +
                "public void M(Dictionary<int,int> d) { foreach (var k in d.Keys) { } } }");
            Assert.Contains("CHM0002", ids);
        }

        [Fact]
        public async Task Foreach_over_dictionary_values_reports_CHM0002()
        {
            string[] ids = await Ids(
                "using System.Collections.Generic; public class C { " +
                "public void M(Dictionary<int,int> d) { foreach (var v in d.Values) { } } }");
            Assert.Contains("CHM0002", ids);
        }

        [Fact]
        public async Task Linq_select_on_dictionary_reports_CHM0002()
        {
            string[] ids = await Ids(
                "using System.Collections.Generic; using System.Linq; public class C { " +
                "public void M(Dictionary<int,int> d) { var q = d.Select(kv => kv.Key).ToList(); } }");
            Assert.Contains("CHM0002", ids);
        }

        [Fact]
        public async Task Linq_first_on_hashset_reports_CHM0002()
        {
            string[] ids = await Ids(
                "using System.Collections.Generic; using System.Linq; public class C { " +
                "public int M(HashSet<int> h) { return h.First(); } }");
            Assert.Contains("CHM0002", ids);
        }

        [Fact]
        public async Task OrderBy_on_dictionary_does_not_report_CHM0002()
        {
            // An ordering operator imposes a deterministic order, so it is exempt.
            string[] ids = await Ids(
                "using System.Collections.Generic; using System.Linq; public class C { " +
                "public void M(Dictionary<int,int> d) { var q = d.OrderBy(kv => kv.Key).ToList(); } }");
            Assert.DoesNotContain("CHM0002", ids);
        }

        [Fact]
        public async Task GetEnumerator_on_dictionary_reports_CHM0002()
        {
            string[] ids = await Ids(
                "using System.Collections.Generic; public class C { " +
                "public void M(Dictionary<int,int> d) { var e = d.GetEnumerator(); } }");
            Assert.Contains("CHM0002", ids);
        }

        [Fact]
        public async Task Linq_select_on_list_does_not_report_CHM0002()
        {
            string[] ids = await Ids(
                "using System.Collections.Generic; using System.Linq; public class C { " +
                "public void M(List<int> a) { var q = a.Select(x => x).ToList(); } }");
            Assert.DoesNotContain("CHM0002", ids);
        }

        [Fact]
        public async Task Linq_count_on_dictionary_does_not_report_CHM0002()
        {
            // Count()/Any()/Sum() are order-insensitive reducers — the result is already deterministic, so flagging
            // them on a dictionary is a false positive. Only order-exposing operators (Select/First/…) fire.
            string[] ids = await Ids(
                "using System.Collections.Generic; using System.Linq; public class C { " +
                "public int M(Dictionary<int,int> d) { return d.Count(); } }");
            Assert.DoesNotContain("CHM0002", ids);
        }

        // ── CHM0003 — unstable sort ───────────────────────────────────────────────────────────

        [Fact]
        public async Task ArraySort_reports_CHM0003()
        {
            string[] ids = await Ids("public class C { public void M(int[] a) { System.Array.Sort(a); } }");
            Assert.Contains("CHM0003", ids);
        }

        [Fact]
        public async Task ListSort_reports_CHM0003()
        {
            string[] ids = await Ids(
                "using System.Collections.Generic; public class C { " +
                "public void M(List<int> a) { a.Sort(); } }");
            Assert.Contains("CHM0003", ids);
        }

        [Fact]
        public async Task Sort_with_comparer_does_not_report_CHM0003()
        {
            // A List.Sort(Comparison<T>) is a developer-controlled total order — the shape of the two real sim sites
            // (ScenarioDirector.cs:483, LocalProfileSource.cs:121). It must NOT fire.
            string[] ids = await Ids(
                "using System.Collections.Generic; public class C { " +
                "public void M(List<int> a) { a.Sort((x, y) => x.CompareTo(y)); } }");
            Assert.DoesNotContain("CHM0003", ids);
        }

        [Fact]
        public async Task ArraySort_with_comparer_does_not_report_CHM0003()
        {
            string[] ids = await Ids(
                "using System.Collections.Generic; public class C { " +
                "public void M(int[] a, IComparer<int> cmp) { System.Array.Sort(a, cmp); } }");
            Assert.DoesNotContain("CHM0003", ids);
        }

        [Fact]
        public async Task SpanSort_reports_CHM0003()
        {
            // Span<T>.Sort() (System.MemoryExtensions) is comparerless and unstable — it must fire.
            string[] ids = await Ids(
                "using System; public class C { public void M(Span<int> s) { s.Sort(); } }");
            Assert.Contains("CHM0003", ids);
        }

        // ── CHM0004 — magic cap literal ───────────────────────────────────────────────────────

        [Fact]
        public async Task Relational_bound_literal_reports_CHM0004()
        {
            string[] ids = await Ids("public class C { public void M(int n) { if (n > 64) { } } }");
            Assert.Contains("CHM0004", ids);
        }

        [Fact]
        public async Task ArraySize_literal_reports_CHM0004()
        {
            string[] ids = await Ids("public class C { public void M() { var a = new int[64]; } }");
            Assert.Contains("CHM0004", ids);
        }

        [Fact]
        public async Task Named_const_cap_does_not_report_CHM0004()
        {
            string[] ids = await Ids(
                "public class C { const int Max = 64; public void M(int n) { if (n > Max) { } } }");
            Assert.DoesNotContain("CHM0004", ids);
        }

        [Fact]
        public async Task Small_literal_does_not_report_CHM0004()
        {
            string[] ids = await Ids("public class C { public void M(int n) { if (n > 4) { } } }");
            Assert.DoesNotContain("CHM0004", ids);
        }

        [Fact]
        public async Task For_loop_bound_does_not_report_CHM0004()
        {
            string[] ids = await Ids("public class C { public void M() { for (int i = 0; i < 100; i++) { } } }");
            Assert.DoesNotContain("CHM0004", ids);
        }

        [Fact]
        public async Task While_loop_bound_does_not_report_CHM0004()
        {
            string[] ids = await Ids("public class C { public void M(int i) { while (i < 100) { } } }");
            Assert.DoesNotContain("CHM0004", ids);
        }

        [Fact]
        public async Task Negated_relational_bound_reports_CHM0004_with_negative_value()
        {
            ImmutableArray<Diagnostic> diags = await Diags("public class C { public void M(int x) { if (x < -64) { } } }");
            Diagnostic d = Assert.Single(diags, x => x.Id == "CHM0004");
            Assert.Contains("-64", d.GetMessage());
        }

        [Fact]
        public async Task Static_readonly_cap_reports_CHM0004()
        {
            string[] ids = await Ids("public class C { static readonly int Max = 64; }");
            Assert.Contains("CHM0004", ids);
        }

        [Fact]
        public async Task Do_while_loop_bound_does_not_report_CHM0004()
        {
            // A do-while condition bound is loop control flow like for/while — it must not be flagged as a cap.
            string[] ids = await Ids("public class C { public void M(int i) { do { i++; } while (i < 100); } }");
            Assert.DoesNotContain("CHM0004", ids);
        }

        [Fact]
        public async Task Plain_static_field_does_not_report_CHM0004()
        {
            // The static-readonly cap fires only on static AND readonly — a non-readonly static field stays clean
            // (pins the modifier conjunction so a regression to `||` or a dropped readonly check is caught).
            string[] ids = await Ids("public class C { static int Max = 64; }");
            Assert.DoesNotContain("CHM0004", ids);
        }

        [Fact]
        public async Task Instance_readonly_field_does_not_report_CHM0004()
        {
            // Instance readonly is out of scope for the static-readonly cap rule — it must stay clean.
            string[] ids = await Ids("public class C { readonly int Max = 64; }");
            Assert.DoesNotContain("CHM0004", ids);
        }

        // ── CHM0005 — Fixed.FromFloat/ToFloat outside the converter allow-list ─────────────────

        [Fact]
        public async Task FromFloat_outside_converter_reports_CHM0005()
        {
            string[] ids = await Ids(
                "using ProjectChimera.Core;\n" + FixedDef +
                "public class C { public void M() { var x = Fixed.FromFloat(1.5f); } }");
            Assert.Contains("CHM0005", ids);
        }

        [Fact]
        public async Task ToFloat_outside_converter_reports_CHM0005()
        {
            string[] ids = await Ids(
                "using ProjectChimera.Core;\n" + FixedDef +
                "public class C { public float M(Fixed f) { return f.ToFloat(); } }");
            Assert.Contains("CHM0005", ids);
        }

        [Fact]
        public async Task FromFloat_inside_converter_does_not_report_CHM0005()
        {
            string[] ids = await Ids(
                "using ProjectChimera.Core;\n" + FixedDef +
                "namespace ProjectChimera.Core.Definitions { public class FixedJsonConverter { " +
                "public void M() { var x = Fixed.FromFloat(1.5f); } } }");
            Assert.DoesNotContain("CHM0005", ids);
        }

        [Fact]
        public async Task FromFloat_inside_samename_converter_in_other_namespace_reports_CHM0005()
        {
            // The allow-list is namespace-anchored to ProjectChimera.Core.Definitions: a type that merely shares
            // the name FixedJsonConverter elsewhere must NOT exempt itself (the real AR-14 boundary is the only one).
            string[] ids = await Ids(
                "using ProjectChimera.Core;\n" + FixedDef +
                "namespace Some.Other.Place { public class FixedJsonConverter { " +
                "public void M() { var x = Fixed.FromFloat(1.5f); } } }");
            Assert.Contains("CHM0005", ids);
        }

        // ── CHM0006 — float/double Parse/ToString (culture-nondeterministic, A17) ──────────────

        [Fact]
        public async Task FloatParse_reports_CHM0006()
        {
            string[] ids = await Ids("public class C { public object M(string s) => float.Parse(s); }");
            Assert.Contains("CHM0006", ids);
        }

        [Fact]
        public async Task FloatToString_reports_CHM0006()
        {
            string[] ids = await Ids("public class C { public string M(float f) => f.ToString(\"F4\"); }");
            Assert.Contains("CHM0006", ids);
        }

        [Fact]
        public async Task IntToString_does_not_report_CHM0006()
        {
            // CHM0006 is float/double-specific — a non-float ToString is deterministic and must not fire.
            string[] ids = await Ids("public class C { public string M(int n) => n.ToString(); }");
            Assert.DoesNotContain("CHM0006", ids);
        }

        // ── Analyzer robustness — never throws (AD0001) ───────────────────────────────────────

        [Theory]
        [InlineData("using System; using System.Collections.Generic; using System.Linq; " +
                    "public class C { public void M(Dictionary<int,int> d, Span<int> s) { " +
                    "var q = d.Keys.Select(k => k).ToList(); s.Sort(); foreach (var kv in d) { } " +
                    "if (int.TryParse(\"1\", out var n)) { } } }")]
        [InlineData("public class C { public void M() { var e = default(SomeMissingType); } }")]   // error-typed input
        [InlineData("using System.Collections.Generic; public class C { " +
                    "public void M() { var d = new Dictionary<int,int>(); var e = d.GetEnumerator(); } }")]
        [InlineData("public class C { public void M(int i) { do { i++; } while (i < 100); for (int j = 0; j < -64; j--) { } } }")]
        public async Task Analyzer_never_reports_AD0001(string source)
        {
            // GetAnalyzerDiagnosticsAsync surfaces an analyzer exception as an AD0001 diagnostic. Every new semantic
            // path (var inference, KeyCollection, comparer scan, receiver resolution, unary-minus cap) must be
            // crash-safe even on odd/error-typed input, or enforcement silently dies while the suite stays green.
            string[] ids = await Ids(source);
            Assert.DoesNotContain("AD0001", ids);
        }
    }
}
