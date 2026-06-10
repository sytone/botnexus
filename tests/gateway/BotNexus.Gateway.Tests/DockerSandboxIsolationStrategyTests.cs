using System.Text.Json;
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

    private static AgentDescriptor MakeDescriptor(
        string agentId = "test-agent",
        IReadOnlyDictionary<string, object?>? isolationOptions = null) => new()
    {
        AgentId = AgentId.From(agentId),
        DisplayName = "Test Agent",
        ModelId = "model",
        ApiProvider = "provider",
        IsolationStrategy = "docker-sandbox",
        IsolationOptions = isolationOptions ?? new Dictionary<string, object?>()
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

    [Fact]
    public async Task CreateAsync_WithPerAgentImage_PassesImageToRunner()
    {
        _runner.Available = true;

        var options = new Dictionary<string, object?>
        {
            ["image"] = "custom-sandbox:v2"
        };
        var descriptor = MakeDescriptor(isolationOptions: options);
        var context = MakeContext();

        await _strategy.CreateAsync(descriptor, context);

        _runner.LastOptions.ShouldNotBeNull();
        _runner.LastOptions!.Image.ShouldBe("custom-sandbox:v2");
    }

    [Fact]
    public async Task CreateAsync_WithoutPerAgentOptions_UsesGlobalDefaults()
    {
        _runner.Available = true;
        _options.Image = "global-image:latest";
        _options.NetworkEnabled = true;
        _options.MemoryLimit = "2g";

        var descriptor = MakeDescriptor();
        var context = MakeContext();

        await _strategy.CreateAsync(descriptor, context);

        _runner.LastOptions.ShouldNotBeNull();
        _runner.LastOptions!.Image.ShouldBe("global-image:latest");
        _runner.LastOptions!.NetworkEnabled.ShouldBeTrue();
        _runner.LastOptions!.MemoryLimit.ShouldBe("2g");
    }

    [Fact]
    public async Task CreateAsync_WithPerAgentNetworkAndMemory_OverridesGlobals()
    {
        _runner.Available = true;
        _options.NetworkEnabled = false;
        _options.MemoryLimit = "512m";

        var options = new Dictionary<string, object?>
        {
            ["networkEnabled"] = true,
            ["memoryLimit"] = "1g"
        };
        var descriptor = MakeDescriptor(isolationOptions: options);
        var context = MakeContext();

        await _strategy.CreateAsync(descriptor, context);

        _runner.LastOptions.ShouldNotBeNull();
        _runner.LastOptions!.NetworkEnabled.ShouldBeTrue();
        _runner.LastOptions!.MemoryLimit.ShouldBe("1g");
    }

    [Fact]
    public async Task CreateAsync_WithJsonElementOptions_ParsesCorrectly()
    {
        _runner.Available = true;

        // Simulate JSON deserialization: values come as JsonElement
        var json = JsonSerializer.SerializeToElement(new
        {
            image = "json-image:3.0",
            networkEnabled = true,
            memoryLimit = "256m",
            idleTimeout = "00:02:30"
        });

        var options = new Dictionary<string, object?>();
        foreach (var prop in json.EnumerateObject())
        {
            options[prop.Name] = prop.Value;
        }

        var descriptor = MakeDescriptor(isolationOptions: options);
        var context = MakeContext();

        await _strategy.CreateAsync(descriptor, context);

        _runner.LastOptions.ShouldNotBeNull();
        _runner.LastOptions!.Image.ShouldBe("json-image:3.0");
        _runner.LastOptions!.NetworkEnabled.ShouldBeTrue();
        _runner.LastOptions!.MemoryLimit.ShouldBe("256m");
        _runner.LastOptions!.IdleTimeout.ShouldBe(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task CheckIdleTimeouts_UsesPerAgentTimeout()
    {
        _runner.Available = true;
        _options.IdleTimeout = TimeSpan.FromMinutes(60); // Global: very long

        // Agent with short per-agent timeout
        var shortOptions = new Dictionary<string, object?>
        {
            ["idleTimeout"] = "00:00:00.050" // 50ms
        };
        var descriptor = MakeDescriptor("short-timeout-agent", shortOptions);

        await _strategy.CreateAsync(descriptor, MakeContext());
        await Task.Delay(100);

        await _strategy.CheckIdleTimeoutsAsync();

        _strategy.GetStatus(descriptor.AgentId).ShouldBe(SandboxLifecycleStatus.Stopped);
    }

    [Fact]
    public async Task GetResolvedOptions_AfterCreate_ReturnsResolvedConfig()
    {
        _runner.Available = true;

        var options = new Dictionary<string, object?>
        {
            ["image"] = "my-image:1.0",
            ["networkEnabled"] = false,
            ["memoryLimit"] = "768m"
        };
        var descriptor = MakeDescriptor(isolationOptions: options);

        await _strategy.CreateAsync(descriptor, MakeContext());

        var resolved = _strategy.GetResolvedOptions(descriptor.AgentId);
        resolved.ShouldNotBeNull();
        resolved!.Image.ShouldBe("my-image:1.0");
        resolved.NetworkEnabled.ShouldBeFalse();
        resolved.MemoryLimit.ShouldBe("768m");
    }

    [Fact]
    public void GetResolvedOptions_BeforeCreate_ReturnsNull()
    {
        var agentId = AgentId.From("never-created");
        _strategy.GetResolvedOptions(agentId).ShouldBeNull();
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
    public ResolvedDockerSandboxOptions? LastOptions { get; private set; }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Available);

    public Task CreateAsync(string name, ResolvedDockerSandboxOptions options, CancellationToken cancellationToken = default)
    {
        if (ThrowOnCreate)
            throw new InvalidOperationException("Docker create failed");

        CreatedSandboxes.Add(name);
        HealthySandboxes.Add(name);
        LastOptions = options;
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

    public List<(string Name, string HostPath, string SandboxPath)> CopiedToSandbox { get; } = [];
    public List<(string Name, string SandboxPath, string HostPath)> CopiedFromSandbox { get; } = [];

    public Task CopyToSandboxAsync(string name, string hostPath, string sandboxPath, CancellationToken cancellationToken = default)
    {
        CopiedToSandbox.Add((name, hostPath, sandboxPath));
        return Task.CompletedTask;
    }

    public Task CopyFromSandboxAsync(string name, string sandboxPath, string hostPath, CancellationToken cancellationToken = default)
    {
        CopiedFromSandbox.Add((name, sandboxPath, hostPath));
        return Task.CompletedTask;
    }
}
