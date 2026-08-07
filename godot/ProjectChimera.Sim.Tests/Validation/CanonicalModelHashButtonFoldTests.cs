#nullable enable
using System.Text.Json;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 7.9 — the v11 <see cref="CanonicalModelHash"/> write-rail Button fold sensitivity suite. Each folded
    /// Button field (Text / EventName / arg raws / LocalAction / target) MOVES the handshake hash so divergent
    /// widget trees (one peer has the button, one doesn't) are rejected at the lobby; the authoring-only ArgTypes and
    /// a cosmetic `_editor`/re-save do NOT move it; and SimChecksum stays 18 (no new folded sim state). Mirrors
    /// <c>CanonicalModelHashCustomUiTests</c>.
    /// </summary>
    public class CanonicalModelHashButtonFoldTests
    {
        private static ScenarioData Base() => new() { MapBounds = 120f };

        private static ScenarioData WithButton(ButtonWidget b)
        {
            var m = Base();
            m.CustomUi = new CustomUiTree { Widgets = new WidgetBase[] { b } };
            return m;
        }

        private static void AssertMoves(ScenarioData a, ScenarioData b) =>
            Assert.NotEqual(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));

        private static void AssertEqual(ScenarioData a, ScenarioData b) =>
            Assert.Equal(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));

        [Fact]
        public void AlgoVersion_IsPinned() => Assert.Equal(15, CanonicalModelHash.AlgoVersion);

        [Fact]
        public void SimChecksumAlgoVersion_IsPinned() => Assert.Equal(24, ProjectChimera.Core.SimChecksum.AlgoVersion);

        [Fact]
        public void PresentButton_MovesHashVsNoUi()
        {
            AssertMoves(Base(), WithButton(new ButtonWidget { Id = 1, LocalAction = LocalUiAction.CloseSelf }));
        }

        [Fact]
        public void DivergentTrees_OnePeerHasButton_HashDiffer()
        {
            var withButton = WithButton(new ButtonWidget { Id = 1, EventName = "buy" });
            var withoutButton = Base();
            withoutButton.CustomUi = new CustomUiTree { Widgets = new WidgetBase[] { new PanelWidget { Id = 1 } } };
            AssertMoves(withButton, withoutButton);
        }

        [Fact]
        public void ChangedText_MovesHash() => AssertMoves(
            WithButton(new ButtonWidget { Id = 1, EventName = "buy", Text = "Buy" }),
            WithButton(new ButtonWidget { Id = 1, EventName = "buy", Text = "Sell" }));

        [Fact]
        public void ChangedEventName_MovesHash() => AssertMoves(
            WithButton(new ButtonWidget { Id = 1, EventName = "buy" }),
            WithButton(new ButtonWidget { Id = 1, EventName = "sell" }));

        [Fact]
        public void ChangedArgRaw_MovesHash() => AssertMoves(
            WithButton(new ButtonWidget { Id = 1, EventName = "buy", ArgRaws = new[] { 1 }, ArgTypes = new[] { DslValueType.Int } }),
            WithButton(new ButtonWidget { Id = 1, EventName = "buy", ArgRaws = new[] { 2 }, ArgTypes = new[] { DslValueType.Int } }));

        [Fact]
        public void ChangedArgCount_MovesHash() => AssertMoves(
            WithButton(new ButtonWidget { Id = 1, EventName = "buy", ArgRaws = new[] { 1 }, ArgTypes = new[] { DslValueType.Int } }),
            WithButton(new ButtonWidget { Id = 1, EventName = "buy", ArgRaws = new[] { 1, 2 }, ArgTypes = new[] { DslValueType.Int, DslValueType.Int } }));

        [Fact]
        public void ChangedLocalAction_MovesHash() => AssertMoves(
            WithButton(new ButtonWidget { Id = 1, LocalAction = LocalUiAction.CloseSelf }),
            WithButton(new ButtonWidget { Id = 1, LocalAction = LocalUiAction.OpenSubPanel, LocalTargetWidgetId = 1 }));

        [Fact]
        public void ChangedLocalTarget_MovesHash() => AssertMoves(
            WithButton(new ButtonWidget { Id = 1, LocalAction = LocalUiAction.ToggleWidgetVisible, LocalTargetWidgetId = 1 }),
            WithButton(new ButtonWidget { Id = 1, LocalAction = LocalUiAction.ToggleWidgetVisible, LocalTargetWidgetId = 2 }));

        [Fact]
        public void ChangedLocalVarNameAndValue_MoveHash()
        {
            AssertMoves(
                WithButton(new ButtonWidget { Id = 1, LocalAction = LocalUiAction.SetLocalUiVar, LocalVarName = "a", LocalVarValue = 1 }),
                WithButton(new ButtonWidget { Id = 1, LocalAction = LocalUiAction.SetLocalUiVar, LocalVarName = "b", LocalVarValue = 1 }));
            AssertMoves(
                WithButton(new ButtonWidget { Id = 1, LocalAction = LocalUiAction.SetLocalUiVar, LocalVarName = "a", LocalVarValue = 1 }),
                WithButton(new ButtonWidget { Id = 1, LocalAction = LocalUiAction.SetLocalUiVar, LocalVarName = "a", LocalVarValue = 2 }));
        }

        [Fact]
        public void ChangedArgTypesOnly_DoesNotMoveHash()
        {
            // ArgTypes is authoring/gate-only — it is NOT folded, so two buttons with identical raws but different
            // authored types hash identically (the raws are what drive the sim).
            AssertEqual(
                WithButton(new ButtonWidget { Id = 1, EventName = "buy", ArgRaws = new[] { 1 }, ArgTypes = new[] { DslValueType.Int } }),
                WithButton(new ButtonWidget { Id = 1, EventName = "buy", ArgRaws = new[] { 1 }, ArgTypes = new[] { DslValueType.Bool } }));
        }

        [Fact]
        public void EditorBagContent_DoesNotMoveHash()
        {
            var a = WithButton(new ButtonWidget { Id = 1, EventName = "buy" });
            a.CustomUi!.Widgets[0].Editor = JsonSerializer.Deserialize<JsonElement>("{\"note\":\"here\"}");
            var b = WithButton(new ButtonWidget { Id = 1, EventName = "buy" });
            b.CustomUi!.Widgets[0].Editor = JsonSerializer.Deserialize<JsonElement>("{\"note\":\"elsewhere\",\"z\":9}");
            AssertEqual(a, b);
        }

        [Fact]
        public void ReSave_IsHashNeutral()
        {
            var a = WithButton(new ButtonWidget { Id = 1, EventName = "buy", Text = "Buy", ArgRaws = new[] { 5 }, ArgTypes = new[] { DslValueType.Int } });
            var opt = new JsonSerializerOptions
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(), new FixedJsonConverter(), new WidgetBaseJsonConverter() },
            };
            var back = JsonSerializer.Deserialize<ScenarioData>(ScenarioSerializer.Serialize(a), opt);
            AssertEqual(a, back!);
        }
    }
}
