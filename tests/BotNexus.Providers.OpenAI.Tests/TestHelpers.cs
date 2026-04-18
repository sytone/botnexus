using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Providers.OpenAI.Tests;

internal static class TestHelpers
{
    private static readonly long Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static LlmModel MakeModel(
        string id = "gpt-4o",
        string api = "openai-completions",
        string provider = "openai",
        bool reasoning = false,
        Core.Compatibility.OpenAICompletionsCompat? compat = null) => new(
        Id: id,
        Name: id,
        Api: api,
        Provider: provider,
        BaseUrl: "https://api.openai.com/v1",
        Reasoning: reasoning,
        Input: ["text", "image"],
        Cost: new ModelCost(2.5m, 10.0m, 0, 0),
        ContextWindow: 128000,
        MaxTokens: 16384,
        Compat: compat);

    public static Context MakeContext(string? systemPrompt = "You are helpful") => new(
        SystemPrompt: systemPrompt,
        Messages: [new UserMessage(new UserMessageContent("hello"), Ts)]);
}
