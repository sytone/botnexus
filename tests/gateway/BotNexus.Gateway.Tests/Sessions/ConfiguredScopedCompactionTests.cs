using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

public sealed class ConfiguredScopedCompactionTests
{
    [Theory]
    [InlineData(null, null, null, 1_050_000, false)]
    [InlineData(null, 64_000, null, 64_000, true)]
    [InlineData(32_000, 64_000, null, 32_000, true)]
    [InlineData(null, null, "fallback-model", 128_000, true)]
    [InlineData(null, null, "missing-model", null, false)]
    public async Task Registration_ToSessionResolver_ToScopedOptions_UsesContextNotOutputCapacity(
        int? conversationWindow, int? agentWindow, string? conversationModel, int? expectedWindow, bool shouldCompact)
    {
        var config = new PlatformConfig
        {
            Providers = new()
            {
                ["dynamic"] = new()
                {
                    Enabled = true, BaseUrl = "https://example.test/v1",
                    Chat = new()
                    {
                        Models = ["large", "fallback-model"],
                        ModelCapacities = new() { ["large"] = new() { ContextWindow = 1_050_000, MaxTokens = 128_000 } }
                    }
                }
            }
        };
        var models = new ModelRegistry();
        using var reconciler = new ConfigDefinedModelRegistryReconciler(
            new TestOptionsMonitor<PlatformConfig>(config), models,
            NullLogger<ConfigDefinedModelRegistryReconciler>.Instance);
        await reconciler.StartAsync(CancellationToken.None);
        var large = models.GetModel("dynamic", "large");
        large.ShouldNotBeNull();
        large.ContextWindow.ShouldBe(1_050_000);
        large.MaxTokens.ShouldBe(128_000);
        var fallback = models.GetModel("dynamic", "fallback-model");
        fallback.ShouldNotBeNull();
        fallback.ContextWindow.ShouldBe(128_000);
        fallback.ContextWindowSource.ShouldBe("fallback");

        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(AgentId.From("a"))).Returns(new AgentDescriptor
        {
            AgentId = AgentId.From("a"), DisplayName = "A", ApiProvider = "dynamic", ModelId = "large",
            ContextWindow = agentWindow
        });
        var conversations = new Mock<IConversationStore>();
        conversations.Setup(s => s.GetAsync(ConversationId.From("c"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Conversation
            {
                AgentId = AgentId.From("a"), ConversationId = ConversationId.From("c"),
                ContextWindowOverride = conversationWindow, ModelOverride = conversationModel
            });
        var client = new LlmClient(new ApiProviderRegistry(), models);
        var resolver = new SessionContextWindowResolver(NullLogger<SessionContextWindowResolver>.Instance,
            registry.Object, conversations.Object, client);
        var resolved = await resolver.ResolveAsync(AgentId.From("a"), ConversationId.From("c"));
        resolved.ShouldBe(expectedWindow);
        var options = new CompactionOptions
        {
            ContextWindowTokens = 900_000, TokenThresholdRatio = 0.6, LargestEntryBytesThreshold = 0
        };
        var scoped = ScopedCompactionWindow.Apply(options, resolved);
        scoped.ContextWindowTokens.ShouldBe(expectedWindow ?? 900_000);
        scoped.TokenThresholdRatio.ShouldBe(0.6);
        scoped.LargestEntryBytesThreshold.ShouldBe(0);
        options.ContextWindowTokens.ShouldBe(900_000);
        if (expectedWindow is null)
            scoped.ShouldBeSameAs(options);
        var session = new Session
        {
            SessionId = SessionId.From("s"), ConversationId = ConversationId.From("c"),
            History = [new SessionEntry { Role = MessageRole.User, Content = new string('a', 100_000 * 4) }]
        };
        var compactor = new LlmSessionCompactor(client, NullLogger<LlmSessionCompactor>.Instance);
        compactor.ShouldCompact(session, scoped).ShouldBe(shouldCompact);
    }
}
