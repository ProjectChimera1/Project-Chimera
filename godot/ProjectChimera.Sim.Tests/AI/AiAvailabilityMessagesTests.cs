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
        [InlineData(AiAvailability.HostRestricted)]
        public void Describe_EachState_NonEmpty_Commander(AiAvailability state)
        {
            string msg = AiAvailabilityMessages.Describe(state);
            Assert.False(string.IsNullOrWhiteSpace(msg));
            Assert.Contains("Commander", msg); // UX-DR52 voice
        }

        [Fact]
        public void Describe_HostRestricted_NamesTheLoopbackRestriction()
        {
            // DW-370 (recorded decision: keep loopback-only, name the restriction in the unavailable message): a
            // LAN-hosted Ollama must not be voiced as the generic "no AI provider is configured" — the message must
            // name the loopback-only policy so the creator understands WHY the host is rejected.
            string msg = AiAvailabilityMessages.Describe(AiAvailability.HostRestricted);
            Assert.Contains("loopback", msg, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(AiAvailabilityMessages.Describe(AiAvailability.NoProvider), msg);
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
