#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.AI.Providers;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// Story 8.2 — each state maps to a distinct, non-empty creator-facing message (the four-state microcopy), in the
    /// UX-DR52 voice (addresses the creator as "Commander").
    /// </summary>
    public class AiAvailabilityMessagesTests
    {
        [Theory]
        [InlineData(AiAvailability.Healthy)]
        [InlineData(AiAvailability.NoProvider)]
        [InlineData(AiAvailability.NoKey)]
        [InlineData(AiAvailability.Unreachable)]
        [InlineData(AiAvailability.FailedValidation)]
        public void Describe_EachState_NonEmpty_Commander(AiAvailability state)
        {
            string msg = AiAvailabilityMessages.Describe(state);
            Assert.False(string.IsNullOrWhiteSpace(msg));
            Assert.Contains("Commander", msg); // UX-DR52 voice
        }

        [Fact]
        public void Describe_AllStates_AreDistinct()
        {
            var states = (AiAvailability[])Enum.GetValues(typeof(AiAvailability));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in states)
                Assert.True(seen.Add(AiAvailabilityMessages.Describe(s)), $"duplicate message for {s}");
            Assert.Equal(states.Length, seen.Count);
        }
    }
}
