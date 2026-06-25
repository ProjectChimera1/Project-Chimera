#nullable enable
using System.Threading.Tasks;
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
        public async Task OrderBy_does_not_report_CHM0003()
        {
            string[] ids = await Ids(
                "using System.Linq; public class C { public void M(int[] a) { var q = a.OrderBy(x => x); } }");
            Assert.DoesNotContain("CHM0003", ids);
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
    }
}
