using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace BotNexus.Gateway.Tests.Configuration;

public sealed class AgentConfigurationDebounceTests
{
    [Fact]
    public async Task RapidConfigChanges_AreDebounced_IntoSingleApply()
    {
        // Arrange: a source that fires multiple rapid change notifications
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetAll()).Returns([]);
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);

        var applyCount = 0;
        registry.Setup(r => r.Register(It.IsAny<AgentDescriptor>()))
            .Callback(() => Interlocked.Increment(ref applyCount));

        var source = new CallbackAgentConfigurationSource();
        var service = new AgentConfigurationHostedService(
            [source],
            registry.Object,
            NullLogger<AgentConfigurationHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        // Act: fire 10 rapid changes within the debounce window
        var descriptor = CreateDescriptor("agent-1");
        for (var i = 0; i < 10; i++)
        {
            source.FireChange([descriptor]);
            await Task.Delay(20); // well within debounce window
        }

        // Wait for debounce to settle (default is 5 seconds + buffer)
        await Task.Delay(6000);

        // Assert: only applied once (or at most twice if first fires before debounce kicks in)
        applyCount.ShouldBeLessThanOrEqualTo(2,
            "Rapid config changes within debounce window should coalesce into at most one apply after the initial load");

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ChangeAfterDebounceWindow_TriggersNewApply()
    {
        // Arrange
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetAll()).Returns([]);
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);

        var applyCount = 0;
        registry.Setup(r => r.Register(It.IsAny<AgentDescriptor>()))
            .Callback(() => Interlocked.Increment(ref applyCount));

        var source = new CallbackAgentConfigurationSource();
        var service = new AgentConfigurationHostedService(
            [source],
            registry.Object,
            NullLogger<AgentConfigurationHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        // Act: fire first change, wait for debounce, then fire second
        source.FireChange([CreateDescriptor("agent-1")]);
        await Task.Delay(6000); // wait for debounce to settle

        var countAfterFirst = applyCount;

        source.FireChange([CreateDescriptor("agent-1"), CreateDescriptor("agent-2")]);
        await Task.Delay(6000); // wait for second debounce

        // Assert: second change triggered a new apply
        applyCount.ShouldBeGreaterThan(countAfterFirst,
            "A change after the debounce window should trigger a new apply");

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task InitialLoad_AppliesImmediately_WithoutDebounce()
    {
        // Arrange
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetAll()).Returns([]);
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);

        var source = new CallbackAgentConfigurationSource(
            initialDescriptors: [CreateDescriptor("agent-initial")]);

        var service = new AgentConfigurationHostedService(
            [source],
            registry.Object,
            NullLogger<AgentConfigurationHostedService>.Instance);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert: registered immediately during StartAsync (no debounce for initial load)
        registry.Verify(r => r.Register(It.Is<AgentDescriptor>(
            d => d.AgentId == AgentId.From("agent-initial"))), Times.Once);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_CancelsDebounceTimer()
    {
        // Arrange
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetAll()).Returns([]);
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);

        var applyCount = 0;
        registry.Setup(r => r.Register(It.IsAny<AgentDescriptor>()))
            .Callback(() => Interlocked.Increment(ref applyCount));

        var source = new CallbackAgentConfigurationSource();
        var service = new AgentConfigurationHostedService(
            [source],
            registry.Object,
            NullLogger<AgentConfigurationHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        // Act: fire change then immediately stop (before debounce window expires)
        source.FireChange([CreateDescriptor("agent-1")]);
        await Task.Delay(100); // short delay
        await service.StopAsync(CancellationToken.None);

        var countAtStop = applyCount;
        await Task.Delay(6000); // wait past what would have been the debounce window

        // Assert: no additional applies after stop
        applyCount.ShouldBe(countAtStop,
            "No applies should fire after StopAsync cancels the debounce timer");
    }

    [Fact]
    public async Task DebounceLogMessage_IsEmitted_OnCoalescedApply()
    {
        // Arrange
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetAll()).Returns([]);
        registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns(false);

        var logMessages = new List<string>();
        var logger = new CapturingLogger<AgentConfigurationHostedService>(logMessages);

        var source = new CallbackAgentConfigurationSource();
        var service = new AgentConfigurationHostedService(
            [source],
            registry.Object,
            logger);

        await service.StartAsync(CancellationToken.None);

        // Act: fire multiple rapid changes
        for (var i = 0; i < 5; i++)
        {
            source.FireChange([CreateDescriptor("agent-1")]);
            await Task.Delay(20);
        }

        await Task.Delay(6000);

        // Assert: log message indicates debounced reload
        logMessages.ShouldContain(m => m.Contains("debounced", StringComparison.OrdinalIgnoreCase),
            "Should log a single debounced reload message for coalesced changes");

        await service.StopAsync(CancellationToken.None);
    }

    private static AgentDescriptor CreateDescriptor(string id) => new()
    {
        AgentId = AgentId.From(id),
        DisplayName = id,
        ModelId = "test-model",
        ApiProvider = "test"
    };

    /// <summary>
    /// Test-only agent configuration source that allows programmatic change notification.
    /// </summary>
    private sealed class CallbackAgentConfigurationSource(
        IReadOnlyList<AgentDescriptor>? initialDescriptors = null) : IAgentConfigurationSource
    {
        private Action<IReadOnlyList<AgentDescriptor>>? _callback;

        public Task<IReadOnlyList<AgentDescriptor>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(initialDescriptors ?? (IReadOnlyList<AgentDescriptor>)[]);

        public IDisposable? Watch(Action<IReadOnlyList<AgentDescriptor>> onChanged)
        {
            _callback = onChanged;
            return null;
        }

        public void FireChange(IReadOnlyList<AgentDescriptor> descriptors)
            => _callback?.Invoke(descriptors);
    }

    /// <summary>
    /// Simple capturing logger for verifying log output in tests.
    /// </summary>
    private sealed class CapturingLogger<T>(List<string> messages) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            lock (messages)
            {
                messages.Add(message);
            }
        }
    }
}
