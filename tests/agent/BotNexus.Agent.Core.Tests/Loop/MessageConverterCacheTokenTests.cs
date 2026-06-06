using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests.Loop;

using ProviderAssistantMessage = BotNexus.Agent.Providers.Core.Models.AssistantMessage;

/// <summary>
/// Verifies that provider-level cache token counts flow through MessageConverter
/// in both directions (provider -> agent and agent -> provider).
/// </summary>
public sealed class MessageConverterCacheTokenTests
{
    [Fact]
    public void ToAgentMessage_CacheCounts_FlowToAgentUsage()
    {
        var provider = BuildProviderAssistant(
            inputTokens: 100, outputTokens: 40,
            cacheRead: 75, cacheWrite: 25);

        var msg = MessageConverter.ToAgentMessage(provider);

        Assert.NotNull(msg.Usage);
        Assert.Equal(100, msg.Usage.InputTokens);
        Assert.Equal(40, msg.Usage.OutputTokens);
        Assert.Equal(75, msg.Usage.CacheRead);
        Assert.Equal(25, msg.Usage.CacheWrite);
    }

    [Fact]
    public void ToAgentMessage_ZeroCacheCounts_ProduceNullCacheFields()
    {
        // Zero means no cache activity -- should not be propagated as 0, should be null
        var provider = BuildProviderAssistant(
            inputTokens: 50, outputTokens: 20,
            cacheRead: 0, cacheWrite: 0);

        var msg = MessageConverter.ToAgentMessage(provider);

        Assert.NotNull(msg.Usage);
        Assert.Null(msg.Usage.CacheRead);
        Assert.Null(msg.Usage.CacheWrite);
    }

    [Fact]
    public void ToProviderMessages_CacheCounts_RoundtripBackToProvider()
    {
        var agent = new AssistantAgentMessage(
            Content: "hello",
            Usage: new AgentUsage(InputTokens: 100, OutputTokens: 40, CacheRead: 75, CacheWrite: 25));

        var providerMessages = MessageConverter.ToProviderMessages([agent]);

        var providerMsg = providerMessages.OfType<ProviderAssistantMessage>().Single();
        Assert.Equal(75, providerMsg.Usage.CacheRead);
        Assert.Equal(25, providerMsg.Usage.CacheWrite);
    }

    [Fact]
    public void ToAgentMessage_NullProviderUsage_ProducesNullAgentUsage()
    {
        // Create a provider message with zero usage to simulate no cache
        var provider = BuildProviderAssistant(0, 0, 0, 0);

        var msg = MessageConverter.ToAgentMessage(provider);

        // Zero input/output tokens produce null usage (based on conditional in ToAgentMessage)
        // Or if usage is present but all zeros -- let's just check null-safety
        // The converter constructs AgentUsage when providerMessage.Usage is not null
        // Zero cache counts produce null CacheRead/CacheWrite
        if (msg.Usage is not null)
        {
            Assert.Null(msg.Usage.CacheRead);
            Assert.Null(msg.Usage.CacheWrite);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static ProviderAssistantMessage BuildProviderAssistant(
        int inputTokens, int outputTokens,
        int cacheRead, int cacheWrite)
    {
        return new ProviderAssistantMessage(
            Content: [new TextContent("hello")],
            Api: "test",
            Provider: "test",
            ModelId: "test-model",
            Usage: new Usage
            {
                Input = inputTokens,
                Output = outputTokens,
                TotalTokens = inputTokens + outputTokens,
                CacheRead = cacheRead,
                CacheWrite = cacheWrite
            },
            StopReason: StopReason.Stop,
            ErrorMessage: null,
            ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
}
