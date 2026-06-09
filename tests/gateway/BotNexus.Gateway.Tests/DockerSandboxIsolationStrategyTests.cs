using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Isolation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests;

public sealed class DockerSandboxIsolationStrategyTests
{
    private readonly FakeDockerSandboxRunner _runner = new();
    private readonly DockerSandboxOptions _options = new() { IdleTimeout = TimeSpan.FromMinutes(10) };
    private readonly DockerSandboxIsolationStrategy _strategy;

    public DockerSandboxIsolationStrategyTests()
    {
        _strategy = new DockerSandboxIsolationStrategy(
            _runner,
            Options.Create(_options),
            NullLogger<DockerSandboxIsolationStrategy>.Instance);
    }

    private static AgentDescriptor MakeDescriptor(string agentId = "test-agent") => new()
    {
        AgentId = AgentId.From(agentId),
        DisplayName = "Test Agent",
        ModelId = "model",
        ApiProvider = "provider",
        IsolationStrategy = "docker-sandbox"
    };

    private static AgentExecutionContext MakeContext(string sessionId = "session-1") => new()
    {
        SessionId = SessionId.From(sessionId)
    };

    [Fact]
    public void Name_ReturnsDockerSandbox()
    {
        _strategy.Name.ShouldBe("docker-sandbox");
    }

    [Fact]
    public async Task CreateAsync_WhenDockerUnavailable_ThrowsInvalidOperation()
    {
        _runner.Available = false;

        var descriptor = MakeDescriptor();
        var context = MakeContext();

        var act = () => _strategy.CreateAsync(descriptor, context);

        var ex = await act.ShouldThrowAsync<InvalidOperationException>();
        ex.Message.ShouldContain("not available");
        ex.Message.ShouldContain("test-agent");
    }

    [Fact]
    public async Task CreateAsync_FirstDispatch_CreatesSandboxAndTransitionsToRunning()
    {
        _runner.Available = true;

        var descriptor = MakeDescriptor();
        var context = MakeContext();

        _strategy.GetStatus(descriptor.AgentId).ShouldBe(SandboxLifecycleStatus.None);

        var handle = await _strategy.CreateAsync(descriptor, context);

        handle.ShouldNotBeNull();
        handle.AgentId.ShouldBe(descriptor.AgentId);
        handle.SessionId.ShouldBe(context.SessionId);
        _strategy.GetStatus(descriptor.AgentId).ShouldBe(SandboxLifecycleStatus.Running);
        _runner.CreatedSandboxes.ShouldContain("agent-test-agent");
    }

    [Fact]
    public async Task CreateAsync_SubsequentDispatch_ReusesSandboxWithoutRecreating()
    {
        _runner.Available = true;

        var descriptor = MakeDescriptor();
        var context1 = MakeContext("session-1");
        var context2 = MakeContext("session-2");

        await _strategy.CreateAsync(descriptor, context1);
        _runner.CreatedSandboxes.Count.ShouldBe(1);

        await _strategy.CreateAsync(descriptor, context2);
        _runner.CreatedSandboxes.Count.ShouldBe(1); // No second creation
    }

    [Fact]
    public async Task CreateAsync_UnhealthySandbox_RecreatesSandbox()
    {
        _runner.Available = true;

        var descriptor = MakeDescriptor();
        var context = MakeContext();

        await _strategy.CreateAsync(descriptor, context);
        _runner.CreatedSandboxes.Count.ShouldBe(1);

        // Mark sandbox as unhealthy
        _runner.HealthySandboxes.Remove("agent-test-agent");

        await _strategy.CreateAsync(descriptor, context);
        _runner.CreatedSandboxes.Count.ShouldBe(2); // Recreated
    }

    [Fact]
    public async Task CheckIdleTimeouts_RunningAndIdle_StopsSandbox()
    {
        _runner.Available = true;
        _options.IdleTimeout = TimeSpan.FromMilliseconds(50);

        var descriptor = MakeDescriptor();
        var context = MakeContext();

        await _strategy.CreateAsync(descriptor, context);
        _strategy.GetStatus(descriptor.AgentId).ShouldBe(SandboxLifecycleStatus.Running);

        // Wait for idle timeout to elapse
        await Task.Delay(100);

        await _strategy.CheckIdleTimeoutsAsync();

        _strategy.GetStatus(descriptor.AgentId).ShouldBe(SandboxLifecycleStatus.Stopped);
        _runner.StoppedSandboxes.ShouldContain("agent-test-agent");
    }

    [Fact]
    public async Task CheckIdleTimeouts_RunningAndActive_DoesNotStop()
    {
        _runner.Available = true;
        _options.IdleTimeout = TimeSpan.FromMinutes(10);

        var descriptor = MakeDescriptor();
        var context = MakeContext();

        await _strategy.CreateAsync(descriptor, context);

        await _strategy.CheckIdleTimeoutsAsync();

        _strategy.GetStatus(descriptor.AgentId).ShouldBe(SandboxLifecycleStatus.Running);
        _runner.StoppedSandboxes.ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateAsync_AfterStoppedByIdleTimeout_RecreatesSandbox()
    {
        _runner.Available = true;
        _options.IdleTimeout = TimeSpan.FromMilliseconds(50);

        var descriptor = MakeDescriptor();
        var context = MakeContext();

        // Create, let idle, stop
        await _strategy.CreateAsync(descriptor, context);
        await Task.Delay(100);
        await _strategy.CheckIdleTimeoutsAsync();
        _strategy.GetStatus(descriptor.AgentId).ShouldBe(SandboxLifecycleStatus.Stopped);

        // Next dispatch recreates
        await _strategy.CreateAsync(descriptor, context);
        _strategy.GetStatus(descriptor.AgentId).ShouldBe(SandboxLifecycleStatus.Running);
        _runner.CreatedSandboxes.Count.ShouldBe(2);
    }

    [Fact]
    public async Task DisposeAsync_StopsAllRunningSandboxes()
    {
        _runner.Available = true;

        var desc1 = MakeDescriptor("agent-1");
        var desc2 = MakeDescriptor("agent-2");

        await _strategy.CreateAsync(desc1, MakeContext("s1"));
        await _strategy.CreateAsync(desc2, MakeContext("s2"));

        await _strategy.DisposeAsync();

        _runner.StoppedSandboxes.ShouldContain("agent-agent-1");
        _runner.StoppedSandboxes.ShouldContain("agent-agent-2");
    }

    [Fact]
    public async Task CreateAsync_DifferentAgents_CreateSeparateSandboxes()
    {
        _runner.Available = true;

        var desc1 = MakeDescriptor("agent-alpha");
        var desc2 = MakeDescriptor("agent-beta");

        await _strategy.CreateAsync(desc1, MakeContext("s1"));
        await _strategy.CreateAsync(desc2, MakeContext("s2"));

        _runner.CreatedSandboxes.ShouldContain("agent-agent-alpha");
        _runner.CreatedSandboxes.ShouldContain("agent-agent-beta");
        _strategy.GetStatus(desc1.AgentId).ShouldBe(SandboxLifecycleStatus.Running);
        _strategy.GetStatus(desc2.AgentId).ShouldBe(SandboxLifecycleStatus.Running);
    }

    [Fact]
    public async Task CreateAsync_WhenRunnerThrows_PropagatesException()
    {
        _runner.Available = true;
        _runner.ThrowOnCreate = true;

        var descriptor = MakeDescriptor();
        var context = MakeContext();

        var act = () => _strategy.CreateAsync(descriptor, context);

        await act.ShouldThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DockerSandboxAgentHandle_PromptAsync_ThrowsNotSupported()
    {
        _runner.Available = true;

        var descriptor = MakeDescriptor();
        var context = MakeContext();

        var handle = await _strategy.CreateAsync(descriptor, context);

        var act = () => handle.PromptAsync("hello");

        await act.ShouldThrowAsync<NotSupportedException>();
    }
}

/// <summary>
/// Fake implementation of <see cref="IDockerSandboxRunner"/> for testing.
/// </summary>
internal sealed class FakeDockerSandboxRunner : IDockerSandboxRunner
{
    public bool Available { get; set; } = true;
    public bool ThrowOnCreate { get; set; }
    public List<string> CreatedSandboxes { get; } = [];
    public List<string> StoppedSandboxes { get; } = [];
    public HashSet<string> HealthySandboxes { get; } = [];

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Available);

    public Task CreateAsync(string name, CancellationToken cancellationToken = default)
    {
        if (ThrowOnCreate)
            throw new InvalidOperationException("Docker create failed");

        CreatedSandboxes.Add(name);
        HealthySandboxes.Add(name);
        return Task.CompletedTask;
    }

    public Task StopAsync(string name, CancellationToken cancellationToken = default)
    {
        StoppedSandboxes.Add(name);
        HealthySandboxes.Remove(name);
        return Task.CompletedTask;
    }

    public Task<bool> IsHealthyAsync(string name, CancellationToken cancellationToken = default)
        => Task.FromResult(HealthySandboxes.Contains(name));
}
