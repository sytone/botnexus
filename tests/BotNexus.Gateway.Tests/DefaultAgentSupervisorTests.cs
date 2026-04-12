using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class DefaultAgentSupervisorTests
{
    [Fact]
    public async Task GetOrCreateAsync_WithConcurrentSameSession_CreatesSingleHandle()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(new AgentDescriptor
        {
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            IsolationStrategy = "test"
        });
        var handle = CreateHandleMock("agent-a", "session-1");
        var strategy = new Mock<IIsolationStrategy>();
        strategy.SetupGet(s => s.Name).Returns("test");
        strategy.Setup(s => s.CreateAsync(It.IsAny<AgentDescriptor>(), It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(40);
                return handle.Object;
            });
        var supervisor = new DefaultAgentSupervisor(registry, [strategy.Object], Mock.Of<ISessionStore>(), NullLogger<DefaultAgentSupervisor>.Instance);

        var tasks = Enumerable.Range(0, 25)
            .Select(_ => supervisor.GetOrCreateAsync("agent-a", "session-1"));
        var results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(h => ReferenceEquals(h, handle.Object));
        strategy.Verify(s => s.CreateAsync(It.IsAny<AgentDescriptor>(), It.Is<AgentExecutionContext>(c => c.SessionId == "session-1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetOrCreateAsync_WithConcurrentDifferentSessions_CreatesPerSession()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(new AgentDescriptor
        {
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            IsolationStrategy = "test"
        });
        var strategy = new Mock<IIsolationStrategy>();
        strategy.SetupGet(s => s.Name).Returns("test");
        strategy.Setup(s => s.CreateAsync(It.IsAny<AgentDescriptor>(), It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns((AgentDescriptor _, AgentExecutionContext context, CancellationToken _) => Task.FromResult(CreateHandleMock("agent-a", context.SessionId).Object));
        var supervisor = new DefaultAgentSupervisor(registry, [strategy.Object], Mock.Of<ISessionStore>(), NullLogger<DefaultAgentSupervisor>.Instance);

        await Task.WhenAll(
            supervisor.GetOrCreateAsync("agent-a", "session-1"),
            supervisor.GetOrCreateAsync("agent-a", "session-2"));

        strategy.Verify(s => s.CreateAsync(It.IsAny<AgentDescriptor>(), It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenMaxConcurrentSessionsReached_ThrowsLimitException()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(new AgentDescriptor
        {
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            IsolationStrategy = "test",
            MaxConcurrentSessions = 1
        });

        var firstHandle = CreateHandleMock("agent-a", "session-1");
        var strategy = new Mock<IIsolationStrategy>();
        strategy.SetupGet(s => s.Name).Returns("test");
        strategy.Setup(s => s.CreateAsync(It.IsAny<AgentDescriptor>(), It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(firstHandle.Object);
        var supervisor = new DefaultAgentSupervisor(registry, [strategy.Object], Mock.Of<ISessionStore>(), NullLogger<DefaultAgentSupervisor>.Instance);

        await supervisor.GetOrCreateAsync("agent-a", "session-1");
        var act = () => supervisor.GetOrCreateAsync("agent-a", "session-2");

        await act.Should().ThrowAsync<AgentConcurrencyLimitExceededException>()
            .WithMessage("*MaxConcurrentSessions (1)*");
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenIsolationStrategyIsUnknown_ThrowsDescriptiveError()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(new AgentDescriptor
        {
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            IsolationStrategy = "missing"
        });

        var strategy = new Mock<IIsolationStrategy>();
        strategy.SetupGet(s => s.Name).Returns("test");
        var supervisor = new DefaultAgentSupervisor(registry, [strategy.Object], Mock.Of<ISessionStore>(), NullLogger<DefaultAgentSupervisor>.Instance);

        var act = () => supervisor.GetOrCreateAsync("agent-a", "session-1");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*IsolationStrategy 'missing' is not registered*")
            .WithMessage("*Available*");
    }

    private static Mock<IAgentHandle> CreateHandleMock(string agentId, string sessionId)
    {
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(agentId);
        handle.SetupGet(h => h.SessionId).Returns(sessionId);
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "ok" });
        handle.Setup(h => h.StreamAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(EmptyStream());
        return handle;
    }

    private static async IAsyncEnumerable<AgentStreamEvent> EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }

    // --- Creation failure and race condition tests ---

    [Fact]
    public async Task GetOrCreateAsync_WhenCreationFails_AllWaitersReceiveException()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(new AgentDescriptor
        {
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-fail"),
            DisplayName = "Agent Fail",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            IsolationStrategy = "test"
        });

        var strategy = new Mock<IIsolationStrategy>();
        strategy.SetupGet(s => s.Name).Returns("test");
        strategy.Setup(s => s.CreateAsync(It.IsAny<AgentDescriptor>(), It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(50); // Simulate slow creation so other callers queue up
                throw new InvalidOperationException("Creation failed!");
            });

        var supervisor = new DefaultAgentSupervisor(registry, [strategy.Object], Mock.Of<ISessionStore>(), NullLogger<DefaultAgentSupervisor>.Instance);

        // Fire 5 concurrent requests — first starts creation, others wait
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => supervisor.GetOrCreateAsync("agent-fail", "session-1"))
            .ToArray();

        var act = () => Task.WhenAll(tasks);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Creation failed*");

        // ALL 5 tasks should have faulted, not just the first
        tasks.Should().OnlyContain(t => t.IsFaulted);
    }

    [Fact]
    public async Task GetOrCreateAsync_AfterCreationFailure_NextCallRetriesCreation()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(new AgentDescriptor
        {
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-retry"),
            DisplayName = "Agent Retry",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            IsolationStrategy = "test"
        });

        var attempt = 0;
        var handle = CreateHandleMock("agent-retry", "session-1");
        var strategy = new Mock<IIsolationStrategy>();
        strategy.SetupGet(s => s.Name).Returns("test");
        strategy.Setup(s => s.CreateAsync(It.IsAny<AgentDescriptor>(), It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref attempt) == 1)
                    throw new InvalidOperationException("First attempt fails");
                return Task.FromResult(handle.Object);
            });

        var supervisor = new DefaultAgentSupervisor(registry, [strategy.Object], Mock.Of<ISessionStore>(), NullLogger<DefaultAgentSupervisor>.Instance);

        // First call fails
        var firstAct = () => supervisor.GetOrCreateAsync("agent-retry", "session-1");
        await firstAct.Should().ThrowAsync<InvalidOperationException>();

        // Second call should retry creation (not return cached error)
        var result = await supervisor.GetOrCreateAsync("agent-retry", "session-1");
        result.Should().BeSameAs(handle.Object);
        attempt.Should().Be(2, "second attempt should create successfully");
    }

    [Fact]
    public async Task StopAllAsync_DisposesAllActiveHandles()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(new AgentDescriptor
        {
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-stop"),
            DisplayName = "Agent Stop",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            IsolationStrategy = "test"
        });

        var handles = new List<Mock<IAgentHandle>>();
        var strategy = new Mock<IIsolationStrategy>();
        strategy.SetupGet(s => s.Name).Returns("test");
        strategy.Setup(s => s.CreateAsync(It.IsAny<AgentDescriptor>(), It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns((AgentDescriptor _, AgentExecutionContext ctx, CancellationToken _) =>
            {
                var h = CreateHandleMock("agent-stop", ctx.SessionId);
                handles.Add(h);
                return Task.FromResult(h.Object);
            });

        var supervisor = new DefaultAgentSupervisor(registry, [strategy.Object], Mock.Of<ISessionStore>(), NullLogger<DefaultAgentSupervisor>.Instance);

        await supervisor.GetOrCreateAsync("agent-stop", "session-1");
        await supervisor.GetOrCreateAsync("agent-stop", "session-2");
        await supervisor.GetOrCreateAsync("agent-stop", "session-3");

        handles.Should().HaveCount(3);

        await supervisor.StopAllAsync();

        foreach (var h in handles)
        {
            h.Verify(x => x.DisposeAsync(), Times.Once);
        }
    }

    [Fact]
    public async Task StopAllAsync_WhenOneDisposeFails_ContinuesDisposingOthers()
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(new AgentDescriptor
        {
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-stop-err"),
            DisplayName = "Agent Stop Err",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            IsolationStrategy = "test"
        });

        var callCount = 0;
        var strategy = new Mock<IIsolationStrategy>();
        strategy.SetupGet(s => s.Name).Returns("test");
        strategy.Setup(s => s.CreateAsync(It.IsAny<AgentDescriptor>(), It.IsAny<AgentExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns((AgentDescriptor _, AgentExecutionContext ctx, CancellationToken _) =>
            {
                var h = CreateHandleMock("agent-stop-err", ctx.SessionId);
                var c = Interlocked.Increment(ref callCount);
                if (c == 2) // Second handle throws on dispose
                    h.Setup(x => x.DisposeAsync()).ThrowsAsync(new InvalidOperationException("Dispose failed"));
                return Task.FromResult(h.Object);
            });

        var supervisor = new DefaultAgentSupervisor(registry, [strategy.Object], Mock.Of<ISessionStore>(), NullLogger<DefaultAgentSupervisor>.Instance);

        await supervisor.GetOrCreateAsync("agent-stop-err", "s1");
        await supervisor.GetOrCreateAsync("agent-stop-err", "s2");
        await supervisor.GetOrCreateAsync("agent-stop-err", "s3");

        // StopAllAsync should not throw even if one dispose fails
        var act = () => supervisor.StopAllAsync();
        await act.Should().NotThrowAsync();
    }
}
