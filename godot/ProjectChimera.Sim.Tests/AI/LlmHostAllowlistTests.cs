#nullable enable
using System;
using ProjectChimera.AI.Providers;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// Story 8.2 — the pinned-host guard: allowlisted cloud hosts pass, an arbitrary cloud host is rejected, and
    /// ollama is permitted only on loopback.
    /// </summary>
    public class LlmHostAllowlistTests
    {
        [Theory]
        [InlineData("anthropic", "https://api.anthropic.com/v1/messages", true)]
        [InlineData("anthropic", "https://API.ANTHROPIC.COM/v1/messages", true)] // host compare is case-insensitive
        [InlineData("anthropic", "https://evil.example.com/v1/messages", false)]
        [InlineData("anthropic", "https://api.anthropic.com.evil.com/x", false)]
        [InlineData("openrouter", "https://openrouter.ai/api/v1/chat/completions", true)]
        [InlineData("openrouter", "https://not-openrouter.ai/x", false)]
        [InlineData("ollama", "http://localhost:11434/api/chat", true)]
        [InlineData("ollama", "http://127.0.0.1:11434/api/chat", true)]
        [InlineData("ollama", "http://192.168.1.5:11434/api/chat", false)] // remote host not permitted for local provider
        [InlineData("ollama", "https://api.anthropic.com/api/chat", false)]
        [InlineData("unknown", "https://api.anthropic.com/x", false)]        // unknown provider id → never allowed
        public void IsAllowed_MatchesPinnedPolicy(string providerId, string url, bool expected)
        {
            var uri = new Uri(url);
            Assert.Equal(expected, LlmHostAllowlist.IsAllowed(providerId, uri));
        }
    }
}
