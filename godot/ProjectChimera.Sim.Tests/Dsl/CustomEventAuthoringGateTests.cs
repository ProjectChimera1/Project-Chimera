#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;              // Faction, FactionRegistry
using ProjectChimera.Core.Definitions;  // ScenarioCustomEvent
using ProjectChimera.Dsl;               // CustomEventAuthoringGate, DslValueType, EventDispatchPlan
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// DW-384 — the trigger editor's custom-event declaration gate. The panel used to run the closed-registry
    /// check with a hardcoded <c>(int)Faction.Player4</c> ceiling while the load-time gate
    /// (<c>ScenarioValidator</c>) uses <see cref="FactionRegistry.PLAYER_COUNT"/>, so an author could not declare
    /// an event raisable by slots 4–7 that the engine accepts. These tests pin the two ceilings together and cover
    /// the parse/refuse paths extracted alongside the fix.
    /// </summary>
    public class CustomEventAuthoringGateTests
    {
        private static ScenarioCustomEvent[]? Declare(
            string name, string ps, string raisers, out string? error,
            IReadOnlyList<ScenarioCustomEvent>? existing = null)
        {
            CustomEventAuthoringGate.TryDeclare(existing, name, ps, raisers, out var candidate, out error);
            return candidate;
        }

        // ── DW-384: the raiser-slot ceiling must equal the load-time gate's ────────

        [Fact]
        public void RaiserCeiling_IsTheEngineFactionSlotCeiling_NotFour()
        {
            Assert.Equal(FactionRegistry.PLAYER_COUNT, CustomEventAuthoringGate.MaxRaiserSlotExclusive);
            Assert.True(CustomEventAuthoringGate.MaxRaiserSlotExclusive > (int)Faction.Player4,
                "the authoring ceiling regressed to the 4-slot cap DW-384 closed");
        }

        [Theory]
        [InlineData("4")]
        [InlineData("5")]
        [InlineData("6")]
        [InlineData("7")]
        [InlineData("0,1,2,3,4,5,6,7")]
        public void HighSlotRaisers_AreAccepted(string raisers)
        {
            var events = Declare("wave_start", "", raisers, out string? error);

            Assert.Null(error);
            Assert.NotNull(events);
            Assert.Single(events!);
            Assert.NotNull(events![0].AllowedRaisers);
        }

        [Fact]
        public void AcceptedDeclaration_AlsoPassesTheLoadTimeGate()
        {
            // The contract DW-384 broke: anything the editor accepts must survive the pre-tick registry gate at the
            // engine's own ceiling (and vice-versa — the editor must not be the stricter of the two).
            var events = Declare("wave_start", "count:Int", "5,7", out string? error);
            Assert.Null(error);
            Assert.Null(EventDispatchPlan.ValidateRegistry(events, FactionRegistry.PLAYER_COUNT));
        }

        [Fact]
        public void RaiserAtOrAboveTheCeiling_IsRefused()
        {
            Assert.Null(Declare("wave_start", "", FactionRegistry.PLAYER_COUNT.ToString(), out string? error));
            Assert.NotNull(error);
            Assert.Contains("allowed_raisers", error!);
        }

        [Fact]
        public void NegativeRaiserSlot_IsRefused()
        {
            // −1 means "system" on a raise_event's authored raiser, but it is NOT a declarable allowed-raiser slot.
            Assert.Null(Declare("wave_start", "", "-1", out string? error));
            Assert.NotNull(error);
            Assert.Contains("allowed_raisers", error!);
        }

        [Fact]
        public void DuplicateRaiserSlot_IsRefused()
        {
            Assert.Null(Declare("wave_start", "", "5,5", out string? error));
            Assert.NotNull(error);
            Assert.Contains("duplicate", error!);
        }

        // ── Name rules ───────────────────────────────────────────────────────────

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void BlankName_IsRefused(string name)
        {
            Assert.Null(Declare(name, "", "", out string? error));
            Assert.Equal("An event needs a name.", error);
        }

        [Fact]
        public void DuplicateName_IsRefused()
        {
            var existing = new[] { new ScenarioCustomEvent { Name = "wave_start" } };
            Assert.Null(Declare("wave_start", "", "", out string? error, existing));
            Assert.Contains("already declared", error!);
        }

        [Theory]
        [InlineData("match_start")]
        [InlineData("unit_dies")]
        [InlineData("custom_event")]
        [InlineData("player_chat")]   // a Story 7.13 built-in the panel's hand-copied 7-entry list never knew about
        public void BuiltinEventKindName_IsRefused(string name)
        {
            Assert.Null(Declare(name, "", "", out string? error));
            Assert.Contains("shadows a built-in event kind", error!);
        }

        // ── Param text ───────────────────────────────────────────────────────────

        [Fact]
        public void TypedParams_ArePreservedInDeclarationOrder()
        {
            var events = Declare("wave_start", "count:Int, rate:Fixed, boss:Bool", "", out string? error);

            Assert.Null(error);
            var ps = events![0].Params!;
            Assert.Equal(3, ps.Length);
            Assert.Equal("count", ps[0].Name); Assert.Equal(DslValueType.Int,   ps[0].Type);
            Assert.Equal("rate",  ps[1].Name); Assert.Equal(DslValueType.Fixed, ps[1].Type);
            Assert.Equal("boss",  ps[2].Name); Assert.Equal(DslValueType.Bool,  ps[2].Type);
        }

        [Theory]
        [InlineData("count")]            // no type half
        [InlineData("count:Entity")]     // not an authorable custom-event param type
        [InlineData(":Int")]             // no name half
        [InlineData("count:Int:extra")]  // three halves
        public void MalformedParamText_IsRefused(string ps)
        {
            Assert.Null(Declare("wave_start", ps, "", out string? error));
            Assert.Contains("'name:Type' pair", error!);
        }

        [Fact]
        public void NonNumericRaiserText_IsRefused()
        {
            Assert.Null(Declare("wave_start", "", "0,two", out string? error));
            Assert.Equal("'two' is not a faction slot number.", error);
        }

        // ── Atomicity ────────────────────────────────────────────────────────────

        [Fact]
        public void RefusedDeclaration_LeavesNoCandidate_SoNothingHalfPersists()
        {
            var existing = new[] { new ScenarioCustomEvent { Name = "wave_start" } };
            bool ok = CustomEventAuthoringGate.TryDeclare(
                existing, "boss_dies", "", "99", out var candidate, out string? error);

            Assert.False(ok);
            Assert.NotNull(error);
            Assert.Null(candidate);
            Assert.Single(existing);   // the caller's registry is untouched
        }

        [Fact]
        public void AcceptedDeclaration_AppendsToTheExistingRegistry()
        {
            var existing = new[] { new ScenarioCustomEvent { Name = "wave_start" } };
            var events = Declare("boss_dies", "", "6", out string? error, existing);

            Assert.Null(error);
            Assert.Equal(2, events!.Length);
            Assert.Equal("wave_start", events[0].Name);
            Assert.Equal("boss_dies",  events[1].Name);
            Assert.Single(existing);   // appended into a NEW array, not mutated in place
        }
    }
}
