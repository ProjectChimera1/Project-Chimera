#nullable enable
using System.Linq;
using System.Text.Json;
using ProjectChimera.Core;              // Fixed
using ProjectChimera.Core.Definitions;  // CanonicalModelHash, ScenarioData
using ProjectChimera.Dsl;               // TriggerGraph, NodeBaseJsonConverter
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// DW-356 — the contract net around <c>NodeBaseJsonConverter</c>'s SINGLE-PASS property scan.
    ///
    /// <para>The v8 <c>CanonicalModelHash</c> cold compute is dominated by <see cref="TriggerGraph.FromJson"/>, and
    /// inside it by repeated per-field property lookups (each <c>JsonElement.TryGetProperty(string)</c> transcodes
    /// the name and re-walks the node's property list). The converter now captures each node object's properties in
    /// ONE enumeration pass and resolves every field against that capture. That is a pure COST change — but only if
    /// four semantics survive intact, and each one is a way a naive "just stream it" rewrite silently breaks:</para>
    /// <list type="number">
    ///   <item>discrimination stays ORDER-INDEPENDENT (the capture completes before <c>kind</c> is read, so a
    ///         <c>kind</c> written last still dispatches — a reader that dispatched on first-seen tokens would not);</item>
    ///   <item>a duplicated name resolves to its FIRST occurrence (DW-729: a DELIBERATE divergence from
    ///         <c>TryGetProperty</c>, which is last-wins — accept/reject is unchanged, only the located message);</item>
    ///   <item>ERROR PRECEDENCE is unchanged: the <c>kind</c> read runs BEFORE the allow-list pass (so the capture
    ///         must not validate as it goes), and the allow-list pass runs BEFORE any field read;</item>
    ///   <item>a node carrying its full 15-field allow-list plus the <c>_editor</c> bag reads every field (the
    ///         capture buffer must GROW past its initial size instead of dropping the tail).</item>
    /// </list>
    /// <para>Plus the determinism surface the optimization exists to serve: a graph authored in non-canonical
    /// property order must fold to the SAME <c>CanonicalModelHash</c> as its canonical spelling.</para>
    /// </summary>
    public class NodeBaseJsonConverterScanTests
    {
        private static string Wrap(string nodesJson) =>
            $$"""{ "nodes": {{nodesJson}}, "exec_edges": [], "data_edges": [] }""";

        // ── (1) Order-independent discrimination ────────────────────────────────────────────────────────────

        [Fact]
        public void KindWrittenLast_StillDispatches_DiscriminationIsOrderIndependent()
        {
            // Every semantic field precedes the discriminator. A converter that dispatched on tokens as they
            // arrive would have to guess the kind before "kind" appeared; the capture-then-dispatch shape does not.
            string json = Wrap("""
                [ { "text": "hello", "count": 7, "faction": 2, "id": 41, "kind": "display_message" } ]
                """);

            TriggerGraph g = TriggerGraph.FromJson(json);

            ActionNode a = Assert.IsType<ActionNode>(Assert.Single(g.Nodes));
            Assert.Equal(41, a.Id);
            Assert.Equal("display_message", a.Kind);
            Assert.Equal("hello", a.Text);
            Assert.Equal(7, a.Count);
            Assert.Equal(2, a.Faction);
        }

        [Fact]
        public void ShuffledPropertyOrder_ParsesIdenticallyToCanonicalOrder()
        {
            // The same trigger node spelled two ways: canonical field order, and fully reversed.
            string canonical = Wrap("""
                [ { "id": 3, "kind": "trigger", "name": "T", "enabled": false, "run_once": true, "cooldown_seconds": 2.5, "priority": 9 } ]
                """);
            string shuffled = Wrap("""
                [ { "priority": 9, "cooldown_seconds": 2.5, "run_once": true, "enabled": false, "name": "T", "kind": "trigger", "id": 3 } ]
                """);

            var a = Assert.IsType<TriggerNode>(Assert.Single(TriggerGraph.FromJson(canonical).Nodes));
            var b = Assert.IsType<TriggerNode>(Assert.Single(TriggerGraph.FromJson(shuffled).Nodes));

            Assert.Equal(a.Id, b.Id);
            Assert.Equal(a.Name, b.Name);
            Assert.Equal(a.Enabled, b.Enabled);
            Assert.Equal(a.RunOnce, b.RunOnce);
            Assert.Equal(a.CooldownSeconds.Raw, b.CooldownSeconds.Raw);
            Assert.Equal(a.Priority, b.Priority);
            Assert.False(a.Enabled);
            Assert.True(a.RunOnce);
            Assert.Equal(9, a.Priority);
        }

        // ── (2) FIRST-occurrence resolution for a duplicated name ───────────────────────────────────────────

        [Fact]
        public void DuplicatedKind_ResolvesToTheFirstOccurrence_NotTheLast()
        {
            // "name" is allow-listed on `trigger` but NOT on `display_message`. Resolving `kind` to the FIRST
            // occurrence ("display_message") makes "name" the stray the allow-list pass reports FIRST, in document
            // order. Resolving to the LAST ("trigger") would instead admit "name" and report the duplicate `kind`.
            // That asymmetry is the observable difference, so this pins NodeScan's first-wins resolution.
            //
            // DW-729 — what this is NOT. It is not parity with TryGetProperty: TryGetProperty is LAST-wins (pinned
            // below by TryGetProperty_ResolvesADuplicatedNameToTheLastOccurrence), so before DW-356 this very JSON
            // resolved `kind` to "trigger" and threw the DUPLICATE message instead. NodeScan diverges deliberately;
            // accept/reject is identical either way, only the located message moved.
            string json = Wrap("""
                [ { "id": 0, "kind": "display_message", "name": "T", "kind": "trigger" } ]
                """);

            JsonException ex = Assert.Throws<JsonException>(() => TriggerGraph.FromJson(json));
            Assert.Contains("name", ex.Message);
            Assert.Contains("unknown property", ex.Message);
            Assert.DoesNotContain("duplicate property", ex.Message);
        }

        [Fact]
        public void TryGetProperty_ResolvesADuplicatedNameToTheLastOccurrence_NotTheFirst()
        {
            // DW-729 — the BCL fact three doc sites asserted backwards for months. JsonDocument stores properties in a
            // row table and TryGetNamedPropertyValue walks it BACKWARD from EndObject, so a duplicated name resolves
            // to the LAST occurrence, while EnumerateObject (what NodeScan and both RejectUnknownProperties passes
            // use) yields document order. Pinned executably so neither doc claim can be re-typed from memory: the
            // NodeScan divergence documented in NodeBaseJsonConverter is only meaningful if this is true, and
            // EffectNodeJsonConverter's reads — which really do go through TryGetProperty — inherit it.
            using JsonDocument doc = JsonDocument.Parse("""{ "kind": "first", "name": "x", "kind": "second" }""");

            Assert.True(doc.RootElement.TryGetProperty("kind", out JsonElement viaTryGet));
            Assert.Equal("second", viaTryGet.GetString());

            Assert.Equal(new[] { "first", "x", "second" },
                doc.RootElement.EnumerateObject().Select(p => p.Value.GetString()).ToArray());
        }

        [Fact]
        public void DuplicatedValueField_CannotSmuggleASecondValue_EvenWhenTheFirstIsValid()
        {
            // First value legal, second illegal: the duplicate must reject rather than either value winning.
            string json = Wrap("""
                [ { "id": 0, "kind": "expr_var", "name": "gold", "faction": 1, "faction": 99 } ]
                """);

            JsonException ex = Assert.Throws<JsonException>(() => TriggerGraph.FromJson(json));
            Assert.Contains("faction", ex.Message);
            Assert.Contains("duplicate property", ex.Message);
        }

        // ── (3) Error precedence ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void MissingKind_IsReportedBeforeAnUnknownProperty_TheCaptureMustNotValidateAsItGoes()
        {
            // No `kind` at all, plus a stray. The kind read runs FIRST, so the missing-discriminator message wins.
            // A capture pass that validated the allow-list while enumerating would report "oops" instead — and
            // there is no allow-list to validate against yet, because the kind is what selects it.
            string json = Wrap("""[ { "id": 0, "oops": 1 } ]""");

            JsonException ex = Assert.Throws<JsonException>(() => TriggerGraph.FromJson(json));
            Assert.Contains("'kind' discriminator", ex.Message);
            Assert.DoesNotContain("unknown property", ex.Message);
        }

        [Fact]
        public void UnknownProperty_IsReportedBeforeAMalformedAllowedField()
        {
            // "priority" is allow-listed but malformed (a string), and "script" is a stray that appears EARLIER.
            // The allow-list pass runs before any field read, so the stray is what gets reported.
            string json = Wrap("""
                [ { "id": 0, "kind": "trigger", "script": "evil()", "priority": "not-an-int" } ]
                """);

            JsonException ex = Assert.Throws<JsonException>(() => TriggerGraph.FromJson(json));
            Assert.Contains("script", ex.Message);
            Assert.Contains("unknown property", ex.Message);
        }

        [Fact]
        public void NonObjectNode_IsReportedAsTheValueKindError_NotAMissingKind()
        {
            // The capture yields nothing for a non-object; ReadNode's ValueKind guard must still fire first.
            string json = Wrap("""[ 42 ]""");

            JsonException ex = Assert.Throws<JsonException>(() => TriggerGraph.FromJson(json));
            Assert.Contains("must be a JSON object", ex.Message);
        }

        // ── (4) Full allow-list + the _editor bag (the capture buffer must grow) ─────────────────────────────

        [Fact]
        public void ActionNodeWithEveryAllowedField_PlusEditorBag_ReadsAllOfThem()
        {
            // 15 allow-listed properties + `_editor` = 16 on one node, so the capture grows past its initial size.
            // A capture that dropped the tail would silently fall back to defaults on the last fields.
            string json = Wrap("""
                [ {
                    "id": 12, "kind": "spawn_unit", "unit_id": "grunt", "faction": 3,
                    "x": 1.5, "z": -2.25, "count": 6, "text": "spawned",
                    "duration": 7.5, "timer_name": "tick", "timer_seconds": 12.5,
                    "amount": 3.25, "value": 11, "variable": "v", "sound_id": "s",
                    "_editor": { "pos": [ 10, 20 ] }
                } ]
                """);

            ActionNode a = Assert.IsType<ActionNode>(Assert.Single(TriggerGraph.FromJson(json).Nodes));

            Assert.Equal(12, a.Id);
            Assert.Equal("spawn_unit", a.Kind);
            Assert.Equal("grunt", a.UnitId);
            Assert.Equal(3, a.Faction);
            Assert.Equal(Fixed.FromFloat(1.5f).Raw, a.X.Raw);
            Assert.Equal(Fixed.FromFloat(-2.25f).Raw, a.Z.Raw);
            Assert.Equal(6, a.Count);
            Assert.Equal("spawned", a.Text);
            Assert.Equal(Fixed.FromFloat(7.5f).Raw, a.Duration.Raw);
            Assert.Equal("tick", a.TimerName);
            Assert.Equal(Fixed.FromFloat(12.5f).Raw, a.TimerSeconds.Raw);
            Assert.Equal(Fixed.FromFloat(3.25f).Raw, a.Amount.Raw);
            Assert.Equal(11, a.Value);
            Assert.Equal("v", a.Variable);
            Assert.Equal("s", a.SoundId);
            Assert.True(a.Editor.HasValue);
            Assert.Equal("""{"pos":[10,20]}""", a.Editor!.Value.GetRawText().Replace(" ", "").Replace("\r", "").Replace("\n", ""));
        }

        [Fact]
        public void EditorBagInALeadingPosition_IsCapturedAndReEmittedLast()
        {
            // The bag is allow-listed on every kind wherever it sits; Write always re-emits it LAST so
            // ToCanonicalJson stays deterministic regardless of where the author put it.
            string json = Wrap("""
                [ { "_editor": { "x": 1 }, "id": 0, "kind": "branch" } ]
                """);

            TriggerGraph g = TriggerGraph.FromJson(json);
            NodeBase n = Assert.Single(g.Nodes);
            Assert.IsType<BranchNode>(n);
            Assert.True(n.Editor.HasValue);

            using JsonDocument round = JsonDocument.Parse(g.ToCanonicalJson());
            JsonElement node = round.RootElement.GetProperty("nodes")[0];
            string[] order = node.EnumerateObject().Select(p => p.Name).ToArray();
            Assert.Equal(new[] { "id", "kind", "_editor" }, order);
        }

        [Fact]
        public void DuplicateEditorBag_IsALocatedReject()
        {
            string json = Wrap("""
                [ { "id": 0, "kind": "branch", "_editor": { "a": 1 }, "_editor": { "b": 2 } } ]
                """);

            JsonException ex = Assert.Throws<JsonException>(() => TriggerGraph.FromJson(json));
            Assert.Contains("_editor", ex.Message);
            Assert.Contains("duplicate property", ex.Message);
        }

        // ── (5) The determinism surface the optimization serves ─────────────────────────────────────────────

        [Fact]
        public void ShuffledPropertyOrder_FoldsToTheSameCanonicalModelHash()
        {
            // The v8 fold walks the PARSED graph, so property spelling order must be hash-invisible. This is the
            // guarantee the parse optimization must not disturb: a load whose graph JSON is byte-different but
            // semantically identical still agrees at the lobby handshake.
            const string canonicalNodes = """
                [ { "id": 0, "kind": "trigger", "name": "T", "enabled": true, "run_once": false, "cooldown_seconds": 1.5, "priority": 4 },
                  { "id": 1, "kind": "match_start", "faction": 0, "amount": 0, "count": 0, "operator": ">=" },
                  { "id": 2, "kind": "display_message", "faction": 0, "x": 0, "z": 0, "count": 1, "text": "go", "duration": 4, "timer_seconds": 30, "amount": 0, "value": 0 } ]
                """;
            const string shuffledNodes = """
                [ { "priority": 4, "cooldown_seconds": 1.5, "run_once": false, "enabled": true, "name": "T", "kind": "trigger", "id": 0 },
                  { "operator": ">=", "count": 0, "amount": 0, "faction": 0, "kind": "match_start", "id": 1 },
                  { "value": 0, "amount": 0, "timer_seconds": 30, "duration": 4, "text": "go", "count": 1, "z": 0, "x": 0, "faction": 0, "kind": "display_message", "id": 2 } ]
                """;

            var canonicalModel = new ScenarioData
            {
                Id = "m", DisplayName = "m", TerrainRef = "", MapBounds = 200f,
                TriggerGraphJson = Wrap(canonicalNodes),
            };
            var shuffledModel = new ScenarioData
            {
                Id = "m", DisplayName = "m", TerrainRef = "", MapBounds = 200f,
                TriggerGraphJson = Wrap(shuffledNodes),
            };

            // Distinct JSON payloads (so the parse memo cannot mask a difference), identical folded hash.
            Assert.NotEqual(canonicalModel.TriggerGraphJson, shuffledModel.TriggerGraphJson);

            CanonicalModelHash.ClearGraphMemoForTests();
            ulong a = CanonicalModelHash.Compute(canonicalModel);
            CanonicalModelHash.ClearGraphMemoForTests();
            ulong b = CanonicalModelHash.Compute(shuffledModel);

            Assert.Equal(a, b);
        }

        [Fact]
        public void ColdAndWarmCompute_AgreeExactly_TheParseMemoIsPure()
        {
            // The memo must be a pure cost optimization: a memo-cold Compute and a memo-warm one over the SAME
            // immutable json fold identically. (The optimization narrows the gap between them; it must never
            // change either value.)
            string json = Wrap("""
                [ { "id": 0, "kind": "trigger", "name": "T" },
                  { "id": 1, "kind": "match_start" },
                  { "id": 2, "kind": "run_trigger", "target_trigger": 0 } ]
                """);
            var model = new ScenarioData
            {
                Id = "m", DisplayName = "m", TerrainRef = "", MapBounds = 200f, TriggerGraphJson = json,
            };

            CanonicalModelHash.ClearGraphMemoForTests();
            ulong cold = CanonicalModelHash.Compute(model);
            ulong warm = CanonicalModelHash.Compute(model);
            CanonicalModelHash.ClearGraphMemoForTests();
            ulong coldAgain = CanonicalModelHash.Compute(model);

            Assert.Equal(cold, warm);
            Assert.Equal(cold, coldAgain);
            Assert.NotEqual(0UL, cold);
        }

        // ── (6) The parse stays byte-faithful end to end ────────────────────────────────────────────────────

        [Fact]
        public void EveryNodeKind_SurvivesACanonicalRoundTrip_ByteIdentically()
        {
            // Re-serializing a parsed canonical payload must reproduce it byte-for-byte: any field the scan
            // failed to resolve would come back as its default and move these bytes.
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "T", Priority = 3, CooldownSeconds = Fixed.FromFloat(1.25f) });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "resource_threshold", Faction = 2, Amount = Fixed.FromInt(50), Count = 4, Operator = "<=" });
            g.Nodes.Add(new ConditionNode { Id = 2, Kind = "unit_in_region", Faction = 1, RegionId = "r0", Count = 3, Operator = ">" });
            g.Nodes.Add(new ActionNode { Id = 3, Kind = "add_resources", Faction = 1, Amount = Fixed.FromInt(25), Text = "t", Variable = "v" });
            g.Nodes.Add(new ExprLiteralNode { Id = 4, ValueType = DslValueType.Fixed, Raw = 98_765 });
            g.Nodes.Add(new ExprVarNode { Id = 5, Name = "gold", Faction = 2 });
            g.Nodes.Add(new ExprCallNode { Id = 6, Fn = "unit_count_tag", Selector = "organic" });
            g.Nodes.Add(new ForEachNode { Id = 7, Source = "region_units", RegionId = "r0", Faction = 1, UpTo = 5, LoopVar = "u" });
            g.Nodes.Add(new RandomChoiceNode { Id = 8, Weights = new[] { 3, 1, 6 } });
            g.Nodes.Add(new OrderUnitsNode { Id = 9, Command = "attack_move", Faction = 1, RegionId = "r0", X = Fixed.FromFloat(4.5f), Z = Fixed.FromFloat(-1.5f) });
            g.Nodes.Add(new ShowObjectiveNode { Id = 10, ObjectiveId = "obj1" });
            g.Nodes.Add(new RaiseEventNode { Id = 11, Name = "ev", Raiser = 2, NextTick = true });
            g.Nodes.Add(new EventNode { Id = 12, Kind = "custom_event", EventName = "ev" });

            string once = g.ToCanonicalJson();
            string twice = TriggerGraph.FromJson(once).ToCanonicalJson();

            Assert.Equal(once, twice);
            Assert.Equal(g.Nodes.Count, TriggerGraph.FromJson(once).Nodes.Count);
            Assert.Equal(
                g.Nodes.Select(n => n.GetType().Name).OrderBy(s => s).ToList(),
                TriggerGraph.FromJson(once).Nodes.Select(n => n.GetType().Name).OrderBy(s => s).ToList());
        }
    }
}
