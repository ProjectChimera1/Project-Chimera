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
    /// Story 7.8 — <see cref="ScenarioData.CustomUi"/> serialize/deserialize round-trip through the closed-registry
    /// <see cref="WidgetBaseJsonConverter"/>: kinds/fields/anchors/binds + the verbatim <c>_editor</c> bag +
    /// nested children round-trip; fail-closed rejects (unknown kind, unknown/duplicate property, oversized
    /// <c>_editor</c> bag); and a scenario WITHOUT custom UI (or with an empty tree) serializes BYTE-IDENTICALLY
    /// keyless (absent round-trips absent) so no golden moves.
    /// </summary>
    public class CustomUiSerializationTests
    {
        private static readonly JsonSerializerOptions Opt = new()
        {
            Converters = { new JsonStringEnumConverter(), new FixedJsonConverter(), new WidgetBaseJsonConverter() },
        };

        private static ScenarioData? RoundTrip(ScenarioData model) =>
            JsonSerializer.Deserialize<ScenarioData>(ScenarioSerializer.Serialize(model), Opt);

        [Fact]
        public void WidgetTree_RoundTrips_KindsFieldsAnchorsBinds()
        {
            var editor = JsonSerializer.Deserialize<JsonElement>("{\"note\":\"top-right\",\"px\":12}");
            var model = new ScenarioData
            {
                CustomUi = new CustomUiTree
                {
                    Widgets = new WidgetBase[]
                    {
                        new CounterWidget { Id = 1, Anchor = AnchorPoint.TopRight, X = -220, Y = 24, W = 200, H = 48, Bind = "score", Editor = editor },
                        new TimerWidget { Id = 2, Anchor = AnchorPoint.TopCenter, Bind = "clock", VisibleBind = "show" },
                        new LeaderboardWidget { Id = 3, Anchor = AnchorPoint.CenterLeft, Bind = "board", Rows = 10 },
                    },
                },
            };

            ScenarioData? back = RoundTrip(model);
            Assert.NotNull(back!.CustomUi);
            Assert.Equal(3, back.CustomUi!.Widgets.Length);

            var counter = Assert.IsType<CounterWidget>(back.CustomUi.Widgets[0]);
            Assert.Equal(1, counter.Id);
            Assert.Equal(AnchorPoint.TopRight, counter.Anchor);
            Assert.Equal(-220, counter.X);
            Assert.Equal(48, counter.H);
            Assert.Equal("score", counter.Bind);
            Assert.NotNull(counter.Editor);                                // _editor round-trips verbatim
            Assert.Equal("top-right", counter.Editor!.Value.GetProperty("note").GetString());

            var timer = Assert.IsType<TimerWidget>(back.CustomUi.Widgets[1]);
            Assert.Equal("clock", timer.Bind);
            Assert.Equal("show", timer.VisibleBind);

            var board = Assert.IsType<LeaderboardWidget>(back.CustomUi.Widgets[2]);
            Assert.Equal("board", board.Bind);
            Assert.Equal(10, board.Rows);
        }

        [Fact]
        public void NestedChildren_RoundTrip()
        {
            var model = new ScenarioData
            {
                CustomUi = new CustomUiTree
                {
                    Widgets = new WidgetBase[]
                    {
                        new PanelWidget
                        {
                            Id = 1, Anchor = AnchorPoint.Center, W = 400, H = 300,
                            Children = new WidgetBase[]
                            {
                                new LabelWidget { Id = 2, Text = "Score", Anchor = AnchorPoint.TopLeft },
                                new CounterWidget { Id = 3, Bind = "score", Anchor = AnchorPoint.TopRight },
                            },
                        },
                    },
                },
            };

            ScenarioData? back = RoundTrip(model);
            var panel = Assert.IsType<PanelWidget>(back!.CustomUi!.Widgets[0]);
            Assert.Equal(2, panel.Children.Length);
            Assert.IsType<LabelWidget>(panel.Children[0]);
            var counter = Assert.IsType<CounterWidget>(panel.Children[1]);
            Assert.Equal("score", counter.Bind);
        }

        [Fact]
        public void UnknownKind_FailsClosed_LocatedError()
        {
            const string json = "{\"custom_ui\":{\"widgets\":[{\"id\":1,\"kind\":\"WebView\"}]}}";
            var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ScenarioData>(json, Opt));
            Assert.Contains("WebView", ex.Message);
        }

        [Fact]
        public void UnknownProperty_FailsClosed()
        {
            const string json = "{\"custom_ui\":{\"widgets\":[{\"id\":1,\"kind\":\"Counter\",\"script\":\"evil\"}]}}";
            var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ScenarioData>(json, Opt));
            Assert.Contains("script", ex.Message);
        }

        [Fact]
        public void DuplicateProperty_FailsClosed()
        {
            const string json = "{\"custom_ui\":{\"widgets\":[{\"id\":1,\"kind\":\"Counter\",\"bind\":\"a\",\"bind\":\"b\"}]}}";
            var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ScenarioData>(json, Opt));
            Assert.Contains("duplicate", ex.Message.ToLowerInvariant());
        }

        [Fact]
        public void UnknownAnchor_FailsClosed()
        {
            const string json = "{\"custom_ui\":{\"widgets\":[{\"id\":1,\"kind\":\"Counter\",\"anchor\":\"Nowhere\"}]}}";
            var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ScenarioData>(json, Opt));
            Assert.Contains("anchor", ex.Message.ToLowerInvariant());
        }

        [Fact]
        public void OversizedEditorBag_FailsClosed_NamingTheCap()
        {
            string bigNote = new string('x', DslBounds.MaxEditorBagBytes + 100);
            string json = "{\"custom_ui\":{\"widgets\":[{\"id\":1,\"kind\":\"Counter\",\"_editor\":{\"n\":\"" + bigNote + "\"}}]}}";
            var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ScenarioData>(json, Opt));
            Assert.Contains("MaxEditorBagBytes", ex.Message);
        }

        [Fact]
        public void AbsentCustomUi_SerializesKeyless_AndRoundTripsAbsent()
        {
            var model = new ScenarioData { MapBounds = 120f };
            string json = ScenarioSerializer.Serialize(model);
            Assert.DoesNotContain("custom_ui", json);
            Assert.Null(RoundTrip(model)!.CustomUi);
        }

        [Fact]
        public void EmptyTree_NormalizesToKeyless()
        {
            var model = new ScenarioData { CustomUi = new CustomUiTree() }; // no widgets
            string json = ScenarioSerializer.Serialize(model);
            Assert.DoesNotContain("custom_ui", json);
        }

        [Fact]
        public void StrayKeyOnCustomUiObject_FailsClosed()
        {
            // A stray/typo'd key on the custom_ui container object must fail closed (JsonUnmappedMemberHandling.Disallow
            // on CustomUiTree), matching the widget-level converter's RejectUnknownProperties posture — not silently drop.
            const string json = "{\"custom_ui\":{\"widgets\":[],\"evil\":\"x\"}}";
            var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ScenarioData>(json, Opt));
            Assert.Contains("evil", ex.Message);
        }
    }
}
