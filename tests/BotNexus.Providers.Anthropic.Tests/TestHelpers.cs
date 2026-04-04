using BotNexus.Providers.Core.Models;

namespace BotNexus.Providers.Anthropic.Tests;

internal static class TestHelpers
{
    private static readonly long Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static LlmModel MakeModel(
        string id = "claude-sonnet-4",
        string api = "anthropic-messages",
        string provider = "anthropic",
        bool reasoning = true,
        int maxTokens = 16384) => new(
        Id: id,
        Name: id,
        Api: api,
        Provider: provider,
        BaseUrl: "https://api.anthropic.com",
        Reasoning: reasoning,
        Input: ["text", "image"],
        Cost: new ModelCost(3.0m, 15.0m, 0.3m, 3.75m),
        ContextWindow: 200000,
        MaxTokens: maxTokens);

    public static Context MakeContext(string? systemPrompt = "You are helpful") => new(
        SystemPrompt: systemPrompt,
        Messages: [new UserMessage(new UserMessageContent("hello"), Ts)]);
}
