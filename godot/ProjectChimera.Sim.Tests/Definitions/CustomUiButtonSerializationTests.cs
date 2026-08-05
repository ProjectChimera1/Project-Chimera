#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 7.9 — <see cref="ButtonWidget"/> serialize/deserialize round-trip through the closed-registry
    /// <see cref="WidgetBaseJsonConverter"/>: caption/event/typed-args/local-action fields round-trip; the closed
    /// kind + closed local-action set fail closed on unknown values; an unknown/duplicate property rejects located;
    /// and a Button-free tree still serializes byte-identically (no regression to the 7.8 read rail).
    /// </summary>
    public class CustomUiButtonSerializationTests
    {
        /// <summary>
        /// DW-523 - the PRODUCTION scenario options (<see cref="ContentJson.ScenarioOptions"/>), not a hand-rolled
        /// replica. Same three converters, but the replica's enum converter allowed INTEGER values, so the
        /// closed-enum ("kind", "local_action", "anchor") fail-closed claims below were only ever proven against a
        /// looser posture than the one the loader runs.
        /// </summary>
        private static readonly JsonSerializerOptions Opt = ContentJson.ScenarioOptions;

        private static ScenarioData? RoundTrip(ScenarioData model) =>
            JsonSerializer.Deserialize<ScenarioData>(ScenarioSerializer.Serialize(model), Opt);

        [Fact]
        public void EventButton_RoundTrips_CaptionEventAndTypedArgs()
        {
            var model = new ScenarioData
            {
                CustomUi = new CustomUiTree
                {
                    Widgets = new WidgetBase[]
                    {
                        new ButtonWidget
                        {
                            Id = 9, Anchor = AnchorPoint.BottomRight, X = -180, Y = -64, W = 160, H = 48,
                            Text = "Buy Upgrade", EventName = "buy_upgrade",
                            ArgRaws = new[] { 1 }, ArgTypes = new[] { DslValueType.Int },
                        },
                    },
                },
            };

            var back = RoundTrip(model);
            var btn = Assert.IsType<ButtonWidget>(back!.CustomUi!.Widgets[0]);
            Assert.Equal(9, btn.Id);
            Assert.Equal(AnchorPoint.BottomRight, btn.Anchor);
            Assert.Equal("Buy Upgrade", btn.Text);
            Assert.Equal("buy_upgrade", btn.EventName);
            Assert.Equal(new[] { 1 }, btn.ArgRaws);
            Assert.Equal(new[] { DslValueType.Int }, btn.ArgTypes);
            Assert.Equal(LocalUiAction.None, btn.LocalAction);
        }

        [Fact]
        public void TypedArgs_RoundTrip_Bool_Int_Fixed()
        {
            var model = new ScenarioData
            {
                CustomUi = new CustomUiTree
                {
                    Widgets = new WidgetBase[]
                    {
                        new ButtonWidget
                        {
                            Id = 1, EventName = "cast",
                            ArgRaws  = new[] { 1, Fixed.FromFloat(1.5f).Raw },
                            ArgTypes = new[] { DslValueType.Bool, DslValueType.Fixed },
                        },
                    },
                },
            };
            var btn = Assert.IsType<ButtonWidget>(RoundTrip(model)!.CustomUi!.Widgets[0]);
            Assert.Equal(new[] { DslValueType.Bool, DslValueType.Fixed }, btn.ArgTypes);
            Assert.Equal(1, btn.ArgRaws[0]);                              // Bool true
            Assert.Equal(Fixed.FromFloat(1.5f).Raw, btn.ArgRaws[1]);      // Fixed 1.5 round-trips its raw
        }

        [Fact]
        public void LocalActionButton_RoundTrips()
        {
            var model = new ScenarioData
            {
                CustomUi = new CustomUiTree
                {
                    Widgets = new WidgetBase[]
                    {
                        new ButtonWidget { Id = 1, Text = "Panel", LocalAction = LocalUiAction.ToggleWidgetVisible, LocalTargetWidgetId = 7 },
                        new ButtonWidget { Id = 2, LocalAction = LocalUiAction.SetLocalUiVar, LocalVarName = "tab", LocalVarValue = 3 },
                    },
                },
            };
            var back = RoundTrip(model);
            var b1 = Assert.IsType<ButtonWidget>(back!.CustomUi!.Widgets[0]);
            Assert.Equal(LocalUiAction.ToggleWidgetVisible, b1.LocalAction);
            Assert.Equal(7, b1.LocalTargetWidgetId);
            var b2 = Assert.IsType<ButtonWidget>(back.CustomUi.Widgets[1]);
            Assert.Equal(LocalUiAction.SetLocalUiVar, b2.LocalAction);
            Assert.Equal("tab", b2.LocalVarName);
            Assert.Equal(3, b2.LocalVarValue);
        }

        [Fact]
        public void ButtonFromCanonicalExampleJson_Parses()
        {
            // The Design-Notes canonical example (with `_editor` last).
            const string json =
                "{\"custom_ui\":{\"widgets\":[{\"id\":9,\"kind\":\"Button\",\"anchor\":\"BottomRight\"," +
                "\"x\":-180,\"y\":-64,\"w\":160,\"h\":48,\"text\":\"Buy Upgrade\",\"event\":\"buy_upgrade\"," +
                "\"args\":[1],\"_editor\":{\"note\":\"n\"}}]}}";
            var model = JsonSerializer.Deserialize<ScenarioData>(json, Opt);
            var btn = Assert.IsType<ButtonWidget>(model!.CustomUi!.Widgets[0]);
            Assert.Equal("buy_upgrade", btn.EventName);
            Assert.Equal(new[] { DslValueType.Int }, btn.ArgTypes);
            Assert.NotNull(btn.Editor); // _editor round-trips verbatim, excluded from the hash by construction
        }

        [Fact]
        public void UnknownLocalAction_FailsClosed()
        {
            const string json = "{\"custom_ui\":{\"widgets\":[{\"id\":1,\"kind\":\"Button\",\"local_action\":\"HackTheGibson\"}]}}";
            var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ScenarioData>(json, Opt));
            Assert.Contains("HackTheGibson", ex.Message);
        }

        [Fact]
        public void UnknownPropertyOnButton_FailsClosed()
        {
            const string json = "{\"custom_ui\":{\"widgets\":[{\"id\":1,\"kind\":\"Button\",\"script\":\"evil\"}]}}";
            var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ScenarioData>(json, Opt));
            Assert.Contains("script", ex.Message);
        }

        [Fact]
        public void UnknownWidgetKind_StillFailsClosed_AfterButtonAdded()
        {
            const string json = "{\"custom_ui\":{\"widgets\":[{\"id\":1,\"kind\":\"WebView\"}]}}";
            var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ScenarioData>(json, Opt));
            Assert.Contains("WebView", ex.Message);
        }

        [Fact]
        public void AbsentCustomUi_StillSerializesKeyless()
        {
            var model = new ScenarioData { MapBounds = 120f };
            string json = ScenarioSerializer.Serialize(model);
            Assert.DoesNotContain("custom_ui", json);
            Assert.Null(RoundTrip(model)!.CustomUi);
        }

        // ── Review pass 2 — parse-level fail-closed tightening ─────────────────────────────────────────────────

        [Fact]
        public void ArgsOverWireBudget_RejectsAtParse_NamingTheCap()
        {
            // The gate only re-checks arg counts for EVENT buttons; without the parse cap a local-action-only
            // button could smuggle an unbounded args array through both gates into the canonical fold.
            const string json = "{\"custom_ui\":{\"widgets\":[{\"id\":1,\"kind\":\"Button\"," +
                "\"local_action\":\"CloseSelf\",\"args\":[1,2,3]}]}}";
            var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ScenarioData>(json, Opt));
            Assert.Contains("MaxButtonEventParams", ex.Message);
        }

        [Fact]
        public void ArgTypesWithoutArgs_RejectsLocated_NotSilentlySwallowed()
        {
            const string json = "{\"custom_ui\":{\"widgets\":[{\"id\":1,\"kind\":\"Button\"," +
                "\"local_action\":\"CloseSelf\",\"arg_types\":[\"Int\"]}]}}";
            var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ScenarioData>(json, Opt));
            Assert.Contains("arg_types", ex.Message);
            Assert.Contains("without 'args'", ex.Message);
        }

        [Fact]
        public void EmptyEventString_NormalizesToNull_SoBehaviorAndHashAgree()
        {
            // "" and absent are behaviorally identical everywhere (IsNullOrEmpty gates), so they must fold
            // identically too — the reader canonicalizes "" → null (the writer never emits "").
            const string json = "{\"custom_ui\":{\"widgets\":[{\"id\":1,\"kind\":\"Button\"," +
                "\"event\":\"\",\"local_action\":\"CloseSelf\"}]}}";
            const string jsonAbsent = "{\"custom_ui\":{\"widgets\":[{\"id\":1,\"kind\":\"Button\"," +
                "\"local_action\":\"CloseSelf\"}]}}";
            var model  = JsonSerializer.Deserialize<ScenarioData>(json, Opt);
            var absent = JsonSerializer.Deserialize<ScenarioData>(jsonAbsent, Opt);
            var btn = Assert.IsType<ButtonWidget>(model!.CustomUi!.Widgets[0]);
            Assert.Null(btn.EventName);
            Assert.False(btn.RaisesSimEvent);
            Assert.Equal(CanonicalModelHash.Compute(absent!), CanonicalModelHash.Compute(model));
        }
    }
}
