#nullable enable
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 8.1 — the curated <see cref="LlmProviderCatalog"/>: three providers present (anthropic/ollama/
    /// openrouter), default model <c>claude-sonnet-5</c>, each provider has a base URL + ≥1 curated model, and
    /// <see cref="LlmProviderCatalog.TryGet"/> resolves known ids and rejects unknown ones. Godot-free / Tier-1.
    /// </summary>
    public class LlmProviderCatalogTests
    {
        [Fact]
        public void ThreeProviders_Present_WithExpectedIds()
        {
            Assert.Equal(3, LlmProviderCatalog.Providers.Count);
            Assert.True(LlmProviderCatalog.TryGet("anthropic", out _));
            Assert.True(LlmProviderCatalog.TryGet("ollama", out _));
            Assert.True(LlmProviderCatalog.TryGet("openrouter", out _));
        }

        [Fact]
        public void DefaultModel_IsClaudeSonnet46()
        {
            Assert.Equal("claude-sonnet-5", LlmProviderCatalog.DefaultModel);
        }

        [Fact]
        public void DefaultProviderId_IsAnthropic_AndResolves()
        {
            Assert.Equal("anthropic", LlmProviderCatalog.DefaultProviderId);
            Assert.True(LlmProviderCatalog.TryGet(LlmProviderCatalog.DefaultProviderId, out _));
        }

        [Fact]
        public void EveryProvider_HasBaseUrl_AndAtLeastOneModel()
        {
            foreach (var p in LlmProviderCatalog.Providers)
            {
                Assert.False(string.IsNullOrWhiteSpace(p.Id));
                Assert.False(string.IsNullOrWhiteSpace(p.DisplayName));
                Assert.False(string.IsNullOrWhiteSpace(p.DefaultBaseUrl), $"provider '{p.Id}' missing base URL");
                Assert.NotNull(p.Models);
                Assert.NotEmpty(p.Models);
            }
        }

        [Fact]
        public void TryGet_KnownId_ReturnsCuratedModelList()
        {
            Assert.True(LlmProviderCatalog.TryGet("anthropic", out var anthropic));
            Assert.NotNull(anthropic);
            Assert.Contains(LlmProviderCatalog.DefaultModel, anthropic!.Models); // default model is in the anthropic list
        }

        [Fact]
        public void TryGet_UnknownId_ReturnsFalseAndNull()
        {
            Assert.False(LlmProviderCatalog.TryGet("no-such-provider", out var info));
            Assert.Null(info);
        }
    }
}
