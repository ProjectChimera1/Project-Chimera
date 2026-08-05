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
        [InlineData(AiAvailability.HostNotAllowlisted)]
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
        public void Describe_HostNotAllowlisted_NamesThePinnedHosts()
        {
            // DW-589 (the cloud half of the DW-370 honest-UX class): a cloud provider whose base URL points off the
            // pinned allowlist must not be voiced as "no AI provider is configured" — the message must NAME the
            // pinned hosts so the creator corrects the base URL instead of the provider picker. Asserted against
            // LlmHostAllowlist's own constants so the copy can never drift from the enforced policy.
            string msg = AiAvailabilityMessages.Describe(AiAvailability.HostNotAllowlisted);
            foreach (string host in LlmHostAllowlist.PinnedCloudHosts)
                Assert.Contains(host, msg, StringComparison.Ordinal);
            Assert.NotEqual(AiAvailabilityMessages.Describe(AiAvailability.NoProvider), msg);
            // Distinct from ollama's loopback refusal — a cloud rejection must not tell the creator about loopback.
            Assert.NotEqual(AiAvailabilityMessages.Describe(AiAvailability.HostRestricted), msg);
            Assert.DoesNotContain("loopback", msg, StringComparison.OrdinalIgnoreCase);
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

        [Fact]
        public void Describe_EveryDeclaredState_OwnsCopy_NotTheUnknownFallback()
        {
            // Regression net for the defect class DW-370/DW-589 belong to: a state with no arm of its own silently
            // inherits generic copy that misdescribes the creator's actual problem. Every DECLARED state must own its
            // message, so an added state cannot ship voiced as "AI availability is unknown".
            string unknown = AiAvailabilityMessages.Describe((AiAvailability)(-1));
            foreach (AiAvailability s in (AiAvailability[])Enum.GetValues(typeof(AiAvailability)))
                Assert.NotEqual(unknown, AiAvailabilityMessages.Describe(s));
        }
    }
}
