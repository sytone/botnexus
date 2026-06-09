using System.Reflection;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Commands;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Commands;

/// <summary>
/// Regression tests for /compact slash command wiring (Issue #618).
/// </summary>
public sealed class CompactCommandTests
{
    [Fact]
    public void GetCommands_IncludesCompactCommand()
    {
        var contributor = CreateContributor(out _, out _, out _);

        var commands = InvokeGetCommands(contributor);

        var names = commands.Select(c => c.Name).ToList();
        names.ShouldContain("/compact");
    }

    [Fact]
    public async Task ExecuteAsync_Compact_WithNoSession_ReturnsError()
    {
        var contributor = CreateContributor(out _, out _, out _);

        var result = await InvokeExecuteAsync(contributor, "/compact", "/compact",
            agentId: null, sessionId: null);

        result.IsError.ShouldBeTrue();
        result.Body.ShouldContain("session");
    }

    [Fact]
    public async Task ExecuteAsync_Compact_SessionNotFound_ReturnsError()
    {
        var coordinator = new Mock<ISessionCompactionCoordinator>();
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.GetAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((GatewaySession?)null);

        var contributor = CreateContributor(out _, out store, out _,
            coordinatorOverride: coordinator.Object,
            sessionStoreOverride: store.Object);

        var result = await InvokeExecuteAsync(contributor, "/compact", "/compact",
            agentId: "agent-a", sessionId: "s-missing");

        result.IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_Compact_SuccessfulCompaction_ReturnsNotificationText()
    {
        var session = new GatewaySession();
        session.HydrateAgentId(AgentId.From("agent-a"));

        var outcome = new SessionCompactionOutcome(
            Succeeded: true, Applied: true,
            HistoryOutcome: HistoryReplaceOutcome.Applied,
            EntriesSummarized: 5, EntriesPreserved: 3,
            TokensBefore: 1000, TokensAfter: 200,
            FailureReason: null);

        var coordinator = new Mock<ISessionCompactionCoordinator>();
        coordinator
            .Setup(c => c.CompactAsync(It.IsAny<AgentId>(), It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(outcome);
        coordinator
            .Setup(c => c.BuildNotificationText(outcome))
            .Returns("[Session context compacted: 5 older messages summarised, 3 recent messages preserved]");

        var store = new Mock<ISessionStore>();
        store.Setup(s => s.GetAsync(SessionId.From("s-ok"), It.IsAny<CancellationToken>()))
             .ReturnsAsync(session);

        var contributor = CreateContributor(out _, out _, out _,
            coordinatorOverride: coordinator.Object,
            sessionStoreOverride: store.Object);

        var result = await InvokeExecuteAsync(contributor, "/compact", "/compact",
            agentId: "agent-a", sessionId: "s-ok");

        result.IsError.ShouldBeFalse();
        result.Body.ShouldContain("compacted");
    }

    [Fact]
    public async Task ExecuteAsync_Compact_FailedCompaction_ReturnsErrorWithReason()
    {
        var session = new GatewaySession();
        session.HydrateAgentId(AgentId.From("agent-b"));

        var outcome = new SessionCompactionOutcome(
            Succeeded: false, Applied: false,
            HistoryOutcome: HistoryReplaceOutcome.Aborted,
            EntriesSummarized: 0, EntriesPreserved: 0,
            TokensBefore: 0, TokensAfter: 0,
            FailureReason: "Compaction aborted: empty response.");

        var coordinator = new Mock<ISessionCompactionCoordinator>();
        coordinator
            .Setup(c => c.CompactAsync(It.IsAny<AgentId>(), It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(outcome);
        coordinator
            .Setup(c => c.BuildNotificationText(outcome))
            .Returns("Compaction aborted: empty response.");

        var store = new Mock<ISessionStore>();
        store.Setup(s => s.GetAsync(SessionId.From("s-fail"), It.IsAny<CancellationToken>()))
             .ReturnsAsync(session);

        var contributor = CreateContributor(out _, out _, out _,
            coordinatorOverride: coordinator.Object,
            sessionStoreOverride: store.Object);

        var result = await InvokeExecuteAsync(contributor, "/compact", "/compact",
            agentId: "agent-b", sessionId: "s-fail");

        result.IsError.ShouldBeTrue();
        result.Body.ShouldContain("aborted");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static IReadOnlyList<CommandDescriptor> InvokeGetCommands(object contributor)
    {
        var method = contributor.GetType().GetMethod("GetCommands",
            BindingFlags.Instance | BindingFlags.Public);
        method.ShouldNotBeNull();
        return (IReadOnlyList<CommandDescriptor>)method!.Invoke(contributor, null)!;
    }

    private static async Task<CommandResult> InvokeExecuteAsync(
        object contributor,
        string commandName,
        string rawInput,
        string? agentId = "test-agent",
        string? sessionId = "test-session-id")
    {
        var method = contributor.GetType().GetMethod(
            "ExecuteAsync",
            BindingFlags.Instance | BindingFlags.Public,
            [typeof(string), typeof(CommandExecutionContext), typeof(CancellationToken)]);
        method.ShouldNotBeNull();

        var context = new CommandExecutionContext
        {
            RawInput = rawInput,
            AgentId = agentId,
            SessionId = sessionId,
            HomeDirectory = @"Q:\repos\botnexus"
        };

        var task = (Task<CommandResult>)method!.Invoke(contributor, [commandName, context, CancellationToken.None])!;
        return await task;
    }

    private static object CreateContributor(
        out Mock<IAgentRegistry> registry,
        out Mock<ISessionStore> sessionStore,
        out Mock<IServiceProvider> serviceProvider,
        ISessionCompactionCoordinator? coordinatorOverride = null,
        ISessionStore? sessionStoreOverride = null)
    {
        registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetAll()).Returns([]);

        sessionStore = new Mock<ISessionStore>();
        sessionStore
            .Setup(s => s.ListAsync(It.IsAny<AgentId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetAllInstances()).Returns([]);

        serviceProvider = new Mock<IServiceProvider>();

        var coordinatorMock = new Mock<ISessionCompactionCoordinator>();

        var overrides = new Dictionary<Type, object>
        {
            [typeof(IAgentRegistry)] = registry.Object,
            [typeof(IAgentSupervisor)] = supervisor.Object,
            [typeof(ISessionStore)] = sessionStoreOverride ?? sessionStore.Object,
            [typeof(IServiceProvider)] = serviceProvider.Object,
            [typeof(ISessionCompactionCoordinator)] = coordinatorOverride ?? coordinatorMock.Object
        };

        var contributorType = Type.GetType(
            "BotNexus.Gateway.Commands.BuiltInCommandContributor, BotNexus.Gateway")
            ?? throw new InvalidOperationException("BuiltInCommandContributor type not found.");

        return CreateInstance(contributorType, overrides);
    }

    private static object CreateInstance(Type type, IReadOnlyDictionary<Type, object> overrides)
    {
        foreach (var ctor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                     .OrderByDescending(c => c.GetParameters().Length))
        {
            if (!TryBuildArguments(ctor.GetParameters(), overrides, out var args))
                continue;
            try
            {
                return ctor.Invoke(args);
            }
            catch
            {
                // try next constructor
            }
        }
        throw new InvalidOperationException($"Cannot construct {type.FullName}");
    }

    private static bool TryBuildArguments(
        IReadOnlyList<ParameterInfo> parameters,
        IReadOnlyDictionary<Type, object> overrides,
        out object?[] arguments)
    {
        arguments = new object?[parameters.Count];
        for (var i = 0; i < parameters.Count; i++)
        {
            var p = parameters[i];
            if (overrides.TryGetValue(p.ParameterType, out var v))
            {
                arguments[i] = v;
                continue;
            }
            if (TryCreateDefault(p.ParameterType, out var def))
            {
                arguments[i] = def;
                continue;
            }
            if (p.HasDefaultValue)
            {
                arguments[i] = p.DefaultValue;
                continue;
            }
            return false;
        }
        return true;
    }

    private static bool TryCreateDefault(Type t, out object? value)
    {
        if (t == typeof(string)) { value = @"Q:\repos\botnexus"; return true; }

        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Microsoft.Extensions.Logging.ILogger<>))
        {
            var lt = typeof(NullLogger<>).MakeGenericType(t.GetGenericArguments()[0]);
            value = lt.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
            return true;
        }

        if (t.IsInterface)
        {
            var mock = Activator.CreateInstance(typeof(Mock<>).MakeGenericType(t))!;
            var prop = mock.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .First(p => p.Name == "Object" && t.IsAssignableFrom(p.PropertyType));
            value = prop.GetValue(mock);
            return true;
        }

        if (t.IsValueType) { value = Activator.CreateInstance(t); return true; }
        if (t.GetConstructor(Type.EmptyTypes) is not null) { value = Activator.CreateInstance(t); return true; }

        value = null;
        return false;
    }
}
