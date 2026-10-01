#nullable enable
using System.Text.Json.Serialization;

namespace ProjectChimera.AI.Providers
{
    /// <summary>TRIAL — named request-body DTOs replacing the three providers' anonymous-type bodies (an anonymous type
    /// cannot be a source-gen root). Property order == the old anonymous-type declaration order, so the bytes are identical.</summary>
    internal sealed class LlmMessageBody
    {
        [JsonPropertyName("role")]    public string Role { get; set; } = "";
        [JsonPropertyName("content")] public string Content { get; set; } = "";
    }

    internal sealed class AnthropicRequestBody
    {
        [JsonPropertyName("model")]      public string Model { get; set; } = "";
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
        [JsonPropertyName("system")]     public string System { get; set; } = "";
        [JsonPropertyName("messages")]   public LlmMessageBody[] Messages { get; set; } = global::System.Array.Empty<LlmMessageBody>();
    }

    internal sealed class ChatRequestBody
    {
        [JsonPropertyName("model")]    public string Model { get; set; } = "";
        [JsonPropertyName("messages")] public LlmMessageBody[] Messages { get; set; } = global::System.Array.Empty<LlmMessageBody>();
    }

    internal sealed class OllamaRequestBody
    {
        [JsonPropertyName("model")]    public string Model { get; set; } = "";
        [JsonPropertyName("messages")] public LlmMessageBody[] Messages { get; set; } = global::System.Array.Empty<LlmMessageBody>();
        [JsonPropertyName("stream")]   public bool Stream { get; set; }
    }
}
