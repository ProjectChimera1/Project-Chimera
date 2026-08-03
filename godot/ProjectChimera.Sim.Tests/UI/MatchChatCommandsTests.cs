#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Dsl;
using ProjectChimera.Multiplayer;
using ProjectChimera.UI;
using Xunit;

namespace ProjectChimera.Sim.Tests.UI
{
    /// <summary>
    /// DW-374 — the presentation-side chat-string→code map behind <c>MatchChatOverlay</c>'s new
    /// <c>LockstepManager.SendPlayerChat</c> raise. Before this closure the Story 7.13 (Arm D) <c>player_chat</c>
    /// rail existed end-to-end (wire, queue, dispatch, replay) but NOTHING in presentation ever raised it, so an
    /// authored trigger keyed on <c>player_chat</c> validated/saved/loaded clean then silently never fired.
    ///
    /// <para>Two legs: (1) the dash-command grammar — the authorable contract that typing <c>-N</c> maps to chat
    /// code N and nothing else is ever sim-visible; (2) an end-to-end regression driving a PARSED code through the
    /// exact <c>UnitCommand.DslEvent</c> order shape <c>EnqueueDslEvent</c> builds (what <c>SendPlayerChat</c>
    /// sends), proving a typed dash-command fires an authored <c>player_chat</c> trigger with that code.</para>
    /// </summary>
    public class MatchChatCommandsTests
    {
        // ── Leg 1: the authorable dash-command grammar ────────────────────────────

        [Theory]
        [InlineData("-0", 0)]
        [InlineData("-7", 7)]
        [InlineData("-42", 42)]
        [InlineData("-901", 901)]
        [InlineData("-1023", 1023)]     // MaxChatCode - 1: the top representable code
        [InlineData("-0042", 42)]       // leading zeros are the same command
        [InlineData("  -7  ", 7)]       // surrounding whitespace is trimmed
        [InlineData("\t-13\n", 13)]     // any whitespace kind, either side
        public void DashCommand_ParsesToItsBoundedCode(string typed, int expected)
        {
            Assert.True(MatchChatCommands.TryParseChatCode(typed, out int code));
            Assert.Equal(expected, code);
            Assert.InRange(code, 0, EventBounds.MaxChatCode - 1); // the parse can never yield a sim-rejected code
        }

        [Theory]
        [InlineData(null)]              // no message
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("gg")]              // free text is display-only
        [InlineData("gg wp")]
        [InlineData("42")]              // no dash — not a command
        [InlineData("-")]               // bare dash
        [InlineData("- 42")]            // space after dash
        [InlineData("-4 2")]            // embedded space
        [InlineData("--42")]            // double dash
        [InlineData("-42x")]            // trailing text
        [InlineData("x-42")]            // leading text
        [InlineData("gg -42")]          // command must be the whole message
        [InlineData("-42.0")]           // no decimal forms
        [InlineData("+42")]             // no plus forms
        [InlineData("-٤٢")]   // Arabic-Indic ٤٢ — non-ASCII digits are free text (culture-proof)
        [InlineData("-４２")]   // fullwidth ４２ — same
        [InlineData("-1024")]           // == MaxChatCode: out of [0, MaxChatCode)
        [InlineData("-99999")]          // far out of range
        [InlineData("-999999999999999999")] // would overflow int without the accumulation cap
        public void NonCommandOrMalformed_IsRejected_WithZeroedCode(string? typed)
        {
            Assert.False(MatchChatCommands.TryParseChatCode(typed, out int code));
            Assert.Equal(0, code);
        }

        [Fact]
        public void Bound_IsEventBoundsMaxChatCode_NotAHardcodedTwin()
        {
            // The parser's ceiling must track the sim's authoritative bound: the first rejected value is exactly
            // EventBounds.MaxChatCode (which TryEnqueueExternalDslEvent would deterministically drop anyway).
            Assert.True(MatchChatCommands.TryParseChatCode($"-{EventBounds.MaxChatCode - 1}", out int top));
            Assert.Equal(EventBounds.MaxChatCode - 1, top);
            Assert.False(MatchChatCommands.TryParseChatCode($"-{EventBounds.MaxChatCode}", out _));
        }

        // ── Leg 2: end-to-end — a typed dash-command fires an authored player_chat trigger ──

        private static ScenarioVariable IntVar(string name) =>
            new() { Name = name, Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.Zero };

        /// <summary>One trigger subscribed to <c>player_chat</c> for sender slot 0 whose action writes
        /// <c>score = event.code</c> (the ReplayPlayerChatTests scenario shape).</summary>
        private static ScenarioData ChatScenario()
        {
            ScenarioVariable[] vars = { IntVar("score") };

            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "H", Enabled = true });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "player_chat", Faction = 0 }); // sender slot 0
            g.Nodes.Add(new ExprEventParamNode { Id = 2, Name = "code" });
            g.Nodes.Add(new ActionNode { Id = 3, Kind = "set_variable", Variable = "score", Faction = 0 });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 3, TriggerGraph.ActionExecInPort));
            g.DataEdges.Add(new DataEdge(2, TriggerGraph.ExprDataOutPort, 3, TriggerGraph.ActionValueInPort, DataWireType.Int));

            return new ScenarioData { Variables = vars, TriggerGraphJson = g.ToCanonicalJson() };
        }

        private static SimulationHost NewHost()
        {
            var host = SimulationHost.Create(new NullLogSink(), new FactionRegistry(2));
            host.ScenarioDirector.LoadScenario(ChatScenario());
            return host;
        }

        /// <summary>Deliver a parsed chat code exactly as the overlay's send path does: the same
        /// <c>UnitCommand.DslEvent</c> order <c>LockstepManager.EnqueueDslEvent</c> builds (UnitId = the reserved
        /// player_chat rail sentinel, TargetX = the code), applied through the shared <c>OrderApplier</c> into the
        /// director's DSL sink — byte-identical to <c>SendPlayerChat</c> offline, and to what every peer applies
        /// online at the exec-tick.</summary>
        private static void DeliverLikeSendPlayerChat(SimulationHost host, int chatCode)
        {
            var order = new UnitOrder(EventBounds.PlayerChatRailCode, UnitCommand.DslEvent,
                Fixed.FromRaw(chatCode), Fixed.FromRaw(0));
            OrderApplier.Apply(host.World, order, Faction.Player1, dslSink: host.DslEventSink);
        }

        [Fact]
        public void TypedDashCommand_FiresTheAuthoredPlayerChatTrigger_WithItsCode()
        {
            // The DW-374 defect: an authored player_chat trigger never fired because nothing mapped typed chat to
            // a code raise. This walks the closure end-to-end: typed text → parser → the SendPlayerChat order
            // shape → the folded event queue → the subscribed trigger.
            SimulationHost host = NewHost();

            Assert.True(MatchChatCommands.TryParseChatCode("-7", out int code));
            DeliverLikeSendPlayerChat(host, code);
            for (int t = 0; t < 3; t++) host.StepOnce(); // next-tick rail: dequeued at the following tick start

            Assert.Equal(7, host.Vars.GetInt("score", 0)); // the trigger fired and read event.code
        }

        [Fact]
        public void FreeText_NeverEntersTheTick_TriggerStaysCold()
        {
            // The other half of the contract: free text (and near-miss command shapes) parses to NOTHING, so the
            // overlay raises nothing and the authored trigger stays cold — chat strings are display-only.
            SimulationHost host = NewHost();

            Assert.False(MatchChatCommands.TryParseChatCode("gg wp", out _));
            Assert.False(MatchChatCommands.TryParseChatCode("-gg", out _));
            // (nothing delivered — the overlay only raises on a successful parse)
            for (int t = 0; t < 3; t++) host.StepOnce();

            Assert.Equal(0, host.Vars.GetInt("score", 0));
        }
    }
}
