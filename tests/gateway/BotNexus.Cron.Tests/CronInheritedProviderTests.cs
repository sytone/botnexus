using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Cron.Tests;

public sealed class CronInheritedProviderTests
{
    [Theory]
    [InlineData("conversation", "z")]
    [InlineData("job", "a")]
    [InlineData("global", "a")]
    public void Resolve_BareDuplicateModel_PreservesLayerProviderSemantics(string source, string expectedProvider)
    {
        var registry = new ModelRegistry();
        foreach (var provider in new[] { "a", "z" })
            registry.Register(provider, new LlmModel(Id: "shared", Name: "Shared", Provider: provider, Api: "openai-completions", BaseUrl: "https://example.invalid", Reasoning: false, Input: ["text"], Cost: new ModelCost(0, 0, 0, 0), ContextWindow: 1000, MaxTokens: 100));
        var descriptor = new AgentDescriptor { AgentId = AgentId.From("test"), DisplayName = "Test", ApiProvider = "z", ModelId = "shared" };
        var result = CronModelRouting.Resolve("heartbeat", source == "job" ? "shared" : null,
            source == "global" ? "shared" : null, source == "conversation" ? "shared" : null, descriptor, registry);
        result.ShouldNotBeNull();
        result.Provider.ShouldBe(expectedProvider);
        result.ModelId.ShouldBe("shared");
        result.Source.ShouldBe(source);
        result.RequestedModel.ShouldBe("shared");
    }
}
