using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class SubAgentParentBudgetPolicyTests
{
    [Fact]
    public void ResolveBudgetPolicy_UnknownParent_UsesGlobalFallback()
    {
        var options = CreateOptions();

        var policy = options.ResolveBudgetPolicy(AgentId.From("unknown"));

        policy.Tier.ShouldBe("global");
        policy.ResolveTimeoutSeconds(0).ShouldBe(600);
        policy.ResolveTimeoutSeconds(9_999).ShouldBe(1800);
        policy.ResolveMaxTurns(0).ShouldBe(30);
        policy.MaxConcurrentPerSession.ShouldBe(5);
    }

    [Fact]
    public void ResolveBudgetPolicy_Farnsworth_UsesOverride()
    {
        var policy = CreateOptions().ResolveBudgetPolicy(AgentId.From("farnsworth"));

        policy.Tier.ShouldBe("parent-override");
        policy.ResolveTimeoutSeconds(0).ShouldBe(3600);
        policy.ResolveTimeoutSeconds(9_999).ShouldBe(3600);
        policy.ResolveMaxTurns(0).ShouldBe(60);
        policy.ResolveMaxTurns(9_999).ShouldBe(90);
        policy.MaxConcurrentPerSession.ShouldBe(8);
    }

    [Fact]
    public void ResolveBudgetPolicy_ParentIdMatchingIsCaseInsensitive()
    {
        CreateOptions().ResolveBudgetPolicy(AgentId.From("FARNSWORTH"))
            .ResolveTimeoutSeconds(0).ShouldBe(3600);
    }

    [Fact]
    public void ResolveBudgetPolicy_PartialOverride_InheritsGlobalValues()
    {
        var options = CreateOptions();
        options.ParentOverrides["partial"] = new SubAgentParentOverrideOptions { MaxTimeoutSeconds = 2400 };

        var policy = options.ResolveBudgetPolicy(AgentId.From("partial"));

        policy.ResolveTimeoutSeconds(0).ShouldBe(600);
        policy.ResolveTimeoutSeconds(9_999).ShouldBe(2400);
        policy.ResolveMaxTurns(0).ShouldBe(30);
        policy.MaxConcurrentPerSession.ShouldBe(5);
    }

    [Fact]
    public void ResolveBudgetPolicy_UsesReloadedOptionsSnapshot()
    {
        var monitor = new MutableOptionsMonitor<GatewayOptions>(new GatewayOptions { SubAgents = CreateOptions() });
        var manager = CreateManager(monitor, out _);
        var request = CreateRequest("farnsworth");

        manager.ResolveBudgetPolicy(request).ResolveTimeoutSeconds(0).ShouldBe(3600);
        monitor.Current = new GatewayOptions { SubAgents = new SubAgentOptions { DefaultTimeoutSeconds = 120, MaxTimeoutSeconds = 240 } };
        manager.ResolveBudgetPolicy(request).ResolveTimeoutSeconds(0).ShouldBe(120);
    }

    [Fact]
    public void ResolveBudgetPolicy_CannotBeSpoofedByTargetOrDisplayName()
    {
        var monitor = new MutableOptionsMonitor<GatewayOptions>(new GatewayOptions { SubAgents = CreateOptions() });
        var manager = CreateManager(monitor, out _);
        var request = CreateRequest("ordinary") with
        {
            Mode = new Mirror(AgentId.From("farnsworth"))
        };

        manager.ResolveBudgetPolicy(request).Tier.ShouldBe("global");
        manager.ResolveBudgetPolicy(request).ResolveTimeoutSeconds(9_999).ShouldBe(1800);
    }

    [Fact]
    public async Task SpawnAsync_ClampLogIncludesTrustedParentAndPolicyTier()
    {
        var monitor = new MutableOptionsMonitor<GatewayOptions>(new GatewayOptions { SubAgents = CreateOptions() });
        var manager = CreateManager(monitor, out var logger);

        _ = await manager.SpawnAsync(CreateRequest("farnsworth") with { TimeoutSeconds = 9_999, MaxTurns = 9_999 });

        logger.VerifyLog(LogLevel.Warning, "ParentAgentId=farnsworth", "PolicyTier=parent-override");
    }

    private static SubAgentOptions CreateOptions() => new()
    {
        DefaultTimeoutSeconds = 600,
        MaxTimeoutSeconds = 1800,
        DefaultMaxTurns = 30,
        MaxTurnsCeiling = 30,
        MaxConcurrentPerSession = 5,
        ParentOverrides = new Dictionary<string, SubAgentParentOverrideOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["farnsworth"] = new()
            {
                DefaultTimeoutSeconds = 3600,
                MaxTimeoutSeconds = 3600,
                DefaultMaxTurns = 60,
                MaxTurnsCeiling = 90,
                MaxConcurrentPerSession = 8
            }
        }
    };

    private static SubAgentSpawnRequest CreateRequest(string parentId) => new()
    {
        ParentAgentId = AgentId.From(parentId),
        ParentSessionId = SessionId.From($"{parentId}-session"),
        Task = "Do work",
        Mode = new Embody(SubAgentArchetype.General, new EmbodyCustomizations { Name = "farnsworth" }),
        InheritedConversationId = ConversationId.From("conversation")
    };

    private static DefaultSubAgentManager CreateManager(
        MutableOptionsMonitor<GatewayOptions> monitor,
        out Mock<ILogger<DefaultSubAgentManager>> logger)
    {
        var handle = new Mock<IAgentHandle>();
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new AgentResponse { Content = "never" };
            });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.Get(It.IsAny<AgentId>())).Returns<AgentId>(id => new AgentDescriptor
        {
            AgentId = id,
            DisplayName = id.Value,
            ModelId = "gpt-5-mini",
            ApiProvider = "copilot"
        });
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);
        logger = new Mock<ILogger<DefaultSubAgentManager>>();
        return new DefaultSubAgentManager(
            supervisor.Object,
            registry.Object,
            Mock.Of<IActivityBroadcaster>(),
            Mock.Of<IChannelDispatcher>(),
            monitor,
            logger.Object);
    }

    private sealed class MutableOptionsMonitor<T>(T current) : Microsoft.Extensions.Options.IOptionsMonitor<T>
    {
        public T Current { get; set; } = current;
        public T CurrentValue => Current;
        public T Get(string? name) => Current;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

internal static class LoggerVerificationExtensions
{
    public static void VerifyLog<T>(this Mock<ILogger<T>> logger, LogLevel level, params string[] fragments)
    {
        logger.Verify(x => x.Log(
            level,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => fragments.All(fragment => (state.ToString() ?? string.Empty).Contains(fragment, StringComparison.Ordinal))),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.AtLeastOnce);
    }
}
