using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Regression coverage for #3561.
/// <para>
/// The reload watcher reaches its "already in the registry" branch for two structurally different
/// reasons, and before #3561 both produced the same <c>[WRN] ... registered by a non-config source</c>
/// line - including on every ordinary <c>POST /api/agents</c> create, where no non-config source
/// exists at all. These tests pin the LEVEL and the MESSAGE of both branches so they cannot silently
/// collapse back into one: an equivalent descriptor is adopted at Debug, a genuinely different one
/// still warns actionably.
/// </para>
/// </summary>
public sealed class AgentConfigurationAlreadyRegisteredDiagnosticTests
{
    private const string NonConfigSourceClaim = "non-config source";

    /// <summary>
    /// Clause 1: the ordinary create path. The REST handler registers the descriptor and writes the
    /// same shape to config; the reload then observes it for the first time. That must not warn.
    /// </summary>
    [Fact]
    public async Task StartAsync_AgentAlreadyRegisteredWithEquivalentDescriptor_LogsDebugAdoptionNotWarning()
    {
        var descriptor = CreateDescriptor("portal-agent");
        var registry = new StubAgentRegistry();
        // Simulate the REST create: the agent is in the registry before the reload observes it, but it
        // was NOT present at StartAsync time, so it is not treated as a code-based registration.
        var logger = new ListLogger<AgentConfigurationHostedService>();
        var source = CreateSource([descriptor], out _, registry, () => registry.SeedForCreatePath(descriptor));
        var service = CreateService([source], registry, logger);

        await service.StartAsync(CancellationToken.None);

        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToArray();
        warnings.ShouldBeEmpty(
            "an agent registered by the same config write is not shadowed by anything; " +
            $"got: {string.Join(" | ", warnings.Select(w => w.Message))}");
        logger.Entries.ShouldNotContain(e => e.Message.Contains(NonConfigSourceClaim, StringComparison.OrdinalIgnoreCase));

        var adoption = logger.Entries
            .Where(e => e.Message.Contains("Adopting config-based agent", StringComparison.Ordinal))
            .ToArray()
            .ShouldHaveSingleItem();
        adoption.Level.ShouldBe(LogLevel.Debug);
        adoption.Message.ShouldContain("portal-agent");
    }

    /// <summary>
    /// Clause 2: a genuine shadow - a differently-shaped descriptor is registered under the same id,
    /// so config edits for that agent really will not take effect. That still warns, and the warning
    /// must state something a reader can act on.
    /// </summary>
    [Fact]
    public async Task StartAsync_AgentAlreadyRegisteredWithDifferentDescriptor_LogsActionableWarning()
    {
        var configDescriptor = CreateDescriptor("shadowed-agent", "From config");
        var shadowing = CreateDescriptor("shadowed-agent", "Registered by something else");
        var registry = new StubAgentRegistry();
        var logger = new ListLogger<AgentConfigurationHostedService>();
        var source = CreateSource([configDescriptor], out _, registry, () => registry.SeedForCreatePath(shadowing));
        var service = CreateService([source], registry, logger);

        await service.StartAsync(CancellationToken.None);

        var warning = logger.Entries
            .Where(e => e.Level == LogLevel.Warning)
            .ToArray()
            .ShouldHaveSingleItem();
        warning.Message.ShouldContain("shadowed-agent");
        warning.Message.ShouldContain("different descriptor");
        warning.Message.ShouldContain("not being applied");
        // The old message asserted a cause it had not established. It must not come back.
        warning.Message.ShouldNotContain(NonConfigSourceClaim);

        // The shadowing registration is left alone - the guard against double registration still holds.
        registry.Get(AgentId.From("shadowed-agent"))!.DisplayName.ShouldBe("Registered by something else");
        registry.RegisterOperations.ShouldBeEmpty();
    }

    /// <summary>
    /// Clause 4: because the equivalent descriptor was adopted into the applied map, a later
    /// config-driven edit of a portal-created agent takes the UPDATE path (unregister + re-register
    /// with the new shape) rather than re-entering the first-seen branch and being dropped.
    /// </summary>
    [Fact]
    public async Task OnSourceChange_AfterAdoption_ConfigEditTakesUpdatePathNotFirstSeenPath()
    {
        var created = CreateDescriptor("portal-agent", "Created via portal");
        var edited = CreateDescriptor("portal-agent", "Edited via config");
        var registry = new StubAgentRegistry();
        var logger = new ListLogger<AgentConfigurationHostedService>();
        var source = CreateSource([created], out var callbackHolder, registry, () => registry.SeedForCreatePath(created));
        var service = CreateService([source], registry, logger);

        await service.StartAsync(CancellationToken.None);
        logger.Entries.Clear();

        var callback = callbackHolder.Value;
        callback.ShouldNotBeNull();
        callback!([edited]);
        await service.PendingDebounceTask;

        // Update path: unregister then re-register with the edited shape.
        registry.UnregisterOperations.ShouldContain("portal-agent");
        registry.RegisterOperations.ShouldContain("portal-agent");
        registry.Get(AgentId.From("portal-agent"))!.DisplayName.ShouldBe("Edited via config");

        var update = logger.Entries
            .Where(e => e.Message.Contains("Updated agent", StringComparison.Ordinal))
            .ToArray()
            .ShouldHaveSingleItem();
        update.Level.ShouldBe(LogLevel.Information);
        // Neither first-seen outcome may reappear on an edit.
        logger.Entries.ShouldNotContain(e => e.Level == LogLevel.Warning);
        logger.Entries.ShouldNotContain(e => e.Message.Contains("Adopting config-based agent", StringComparison.Ordinal));
    }

    private static IAgentConfigurationSource CreateSource(
        IReadOnlyList<AgentDescriptor> descriptors,
        out CallbackHolder callbackHolder,
        StubAgentRegistry registry,
        Action onLoaded)
    {
        var holder = new CallbackHolder();
        callbackHolder = holder;
        var source = new Mock<IAgentConfigurationSource>();
        source.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                // The REST create registers into IAgentRegistry and persists to config in one
                // operation; the reload observes both together. Seeding at load time reproduces
                // that ordering: the registry already holds the agent, StartAsync's code-based
                // snapshot (taken before this point) does not.
                onLoaded();
                return descriptors;
            });
        source.Setup(s => s.Watch(It.IsAny<Action<IReadOnlyList<AgentDescriptor>>>()))
            .Callback<Action<IReadOnlyList<AgentDescriptor>>>(cb => holder.Value = cb)
            .Returns(Mock.Of<IDisposable>());
        return source.Object;
    }

    private static AgentConfigurationHostedService CreateService(
        IEnumerable<IAgentConfigurationSource> sources,
        IAgentRegistry registry,
        ILogger<AgentConfigurationHostedService> logger)
        => new(sources, registry, logger, (_, _) => Task.CompletedTask);

    private static AgentDescriptor CreateDescriptor(string agentId, string? displayName = null)
        => new()
        {
            AgentId = AgentId.From(agentId),
            DisplayName = displayName ?? $"Display {agentId}",
            ModelId = "model",
            ApiProvider = "provider"
        };

    private sealed class CallbackHolder
    {
        public Action<IReadOnlyList<AgentDescriptor>>? Value { get; set; }
    }

    private sealed class StubAgentRegistry : IAgentRegistry
    {
        private readonly Dictionary<string, AgentDescriptor> _agents = new(StringComparer.OrdinalIgnoreCase);

        public List<string> RegisterOperations { get; } = [];

        public List<string> UnregisterOperations { get; } = [];

        /// <summary>
        /// Places a descriptor directly into the registry without recording a Register operation -
        /// standing in for the REST create path, which registered before this reload ran.
        /// </summary>
        public void SeedForCreatePath(AgentDescriptor descriptor)
            => _agents[descriptor.AgentId.Value] = descriptor;

        public void Register(AgentDescriptor descriptor)
        {
            if (_agents.ContainsKey(descriptor.AgentId.Value))
                throw new InvalidOperationException($"Agent '{descriptor.AgentId}' already exists.");

            _agents[descriptor.AgentId.Value] = descriptor;
            RegisterOperations.Add(descriptor.AgentId.Value);
        }

        public void Unregister(AgentId agentId)
        {
            _agents.Remove(agentId.Value);
            UnregisterOperations.Add(agentId.Value);
        }

        public AgentDescriptor? Get(AgentId agentId)
            => _agents.TryGetValue(agentId.Value, out var descriptor) ? descriptor : null;

        public IReadOnlyList<AgentDescriptor> GetAll() => _agents.Values.ToArray();

        public bool Contains(AgentId agentId) => _agents.ContainsKey(agentId.Value);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        // Debug must be enabled: the adoption branch is only observable at Debug, and a logger that
        // filtered it out would make the "no warning" assertion vacuously true.
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }

    private sealed record LogEntry(LogLevel Level, string Message);
}
