using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Reflection;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Tests;

public sealed class SessionStoreBaseContractTests
{
    public static IEnumerable<object[]> StoreHarnesses()
    {
        yield return ["in-memory", () => new InMemoryHarness()];
        yield return ["file", () => new FileHarness()];
        yield return ["sqlite", () => new SqliteHarness()];
    }

    [Theory]
    [MemberData(nameof(StoreHarnesses))]
    public async Task GetOrCreateAsync_AppliesSharedCreationDefaults(
        string _,
        Func<IStoreHarness> createHarness)
    {
        using var harness = createHarness();
        var before = DateTimeOffset.UtcNow;

        var session = await harness.Store.GetOrCreateAsync(SessionId.From("defaults"), AgentId.From("agent-defaults"));

        session.Status.ShouldBe(GatewaySessionStatus.Active);
        session.CreatedAt.ShouldBeGreaterThanOrEqualTo(before);
        session.UpdatedAt.ShouldBeGreaterThanOrEqualTo(session.CreatedAt);
    }

    [Theory]
    [MemberData(nameof(StoreHarnesses))]
    public async Task ListAsync_StatusFiltering_WorksConsistentlyAcrossStores(
        string _,
        Func<IStoreHarness> createHarness)
    {
        using var harness = createHarness();
        await harness.Store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("active"),
            AgentId = AgentId.From("agent-a"),
            Status = GatewaySessionStatus.Active
        });
        await harness.Store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("suspended"),
            AgentId = AgentId.From("agent-a"),
            Status = GatewaySessionStatus.Suspended
        });
        await harness.Store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("sealed"),
            AgentId = AgentId.From("agent-a"),
            Status = GatewaySessionStatus.Sealed
        });

        var filtered = await ListByStatusAsync(harness.Store, AgentId.From("agent-a"), GatewaySessionStatus.Suspended);

        filtered.Select(s => s.SessionId.Value).ShouldHaveSingleItem().ShouldBe("suspended");
    }

    [Theory]
    [MemberData(nameof(StoreHarnesses))]
    public async Task ListByChannelAsync_ChannelFiltering_WorksConsistentlyAcrossStores(
        string _,
        Func<IStoreHarness> createHarness)
    {
        using var harness = createHarness();
        await harness.Store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("old"),
            AgentId = AgentId.From("agent-a"),
            ChannelType = ChannelKey.From("web chat"),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        });
        await harness.Store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("new"),
            AgentId = AgentId.From("agent-a"),
            ChannelType = ChannelKey.From("web chat"),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        await harness.Store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("other-channel"),
            AgentId = AgentId.From("agent-a"),
            ChannelType = ChannelKey.From("telegram")
        });
        await harness.Store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("other-agent"),
            AgentId = AgentId.From("agent-b"),
            ChannelType = ChannelKey.From("web chat")
        });

        var sessions = await harness.Store.ListByChannelAsync(AgentId.From("agent-a"), ChannelKey.From("web chat"));

        sessions.Select(s => s.SessionId.Value).ShouldBe(new[] { "new", "old" }, ignoreOrder: false);
    }

    [Theory]
    [MemberData(nameof(StoreHarnesses))]
    public async Task ListByChannelAsync_WebChatAlias_FindsSignalrSessionsAcrossStores(
        string _,
        Func<IStoreHarness> createHarness)
    {
        using var harness = createHarness();
        await harness.Store.SaveAsync(new GatewaySession
        {
            SessionId = SessionId.From("signalr-session"),
            AgentId = AgentId.From("agent-a"),
            ChannelType = ChannelKey.From("signalr"),
            CreatedAt = DateTimeOffset.UtcNow
        });

        var sessions = await harness.Store.ListByChannelAsync(AgentId.From("agent-a"), ChannelKey.From("web chat"));

        sessions.Select(s => s.SessionId.Value).ShouldHaveSingleItem().ShouldBe("signalr-session");
    }

    [Theory]
    [MemberData(nameof(StoreHarnesses))]
    public void Stores_InheritSessionStoreBaseBehavior(
        string _,
        Func<IStoreHarness> createHarness)
    {
        using var harness = createHarness();

        harness.Store.GetType().BaseType.ShouldNotBeNull();
        harness.Store.GetType().BaseType!.Name.ShouldBe("SessionStoreBase");
    }

    private static async Task<IReadOnlyList<GatewaySession>> ListByStatusAsync(
        ISessionStore store,
        AgentId agentId,
        GatewaySessionStatus status)
    {
        var statusListMethod = store.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name == nameof(ISessionStore.ListAsync))
            .FirstOrDefault(method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(GatewaySessionStatus) ||
                parameter.ParameterType == typeof(Nullable<GatewaySessionStatus>)));

        statusListMethod.ShouldNotBeNull("SessionStoreBase contract requires status filtering support in ListAsync");

        var parameters = statusListMethod!.GetParameters();
        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].ParameterType == typeof(AgentId) || parameters[i].ParameterType == typeof(AgentId?))
                args[i] = agentId;
            else if (parameters[i].ParameterType == typeof(GatewaySessionStatus) || parameters[i].ParameterType == typeof(Nullable<GatewaySessionStatus>))
                args[i] = status;
            else if (parameters[i].ParameterType == typeof(CancellationToken))
                args[i] = CancellationToken.None;
            else
                args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
        }

        var task = statusListMethod.Invoke(store, args).ShouldBeAssignableTo<Task<IReadOnlyList<GatewaySession>>>();
        return await task!;
    }

    public interface IStoreHarness : IDisposable
    {
        ISessionStore Store { get; }
    }

    private sealed class InMemoryHarness : IStoreHarness
    {
        public ISessionStore Store { get; } = new InMemorySessionStore();
        public void Dispose() { }
    }

    private sealed class FileHarness : IStoreHarness
    {
        private readonly MockFileSystem _fileSystem = new();
        private readonly string _storePath = Path.Combine(Path.GetTempPath(), "SessionStoreBaseContractTests", Guid.NewGuid().ToString("N"));

        public FileHarness()
        {
            _fileSystem.Directory.CreateDirectory(_storePath);
            Store = new FileSessionStore(_storePath, NullLogger<FileSessionStore>.Instance, _fileSystem);
        }

        public ISessionStore Store { get; }

        public void Dispose()
        {
            if (_fileSystem.Directory.Exists(_storePath))
                _fileSystem.Directory.Delete(_storePath, true);
        }
    }

    private sealed class SqliteHarness : IStoreHarness
    {
        private readonly string _directoryPath;

        public SqliteHarness()
        {
            _directoryPath = Path.Combine(AppContext.BaseDirectory, "SessionStoreBaseContractTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directoryPath);
            var dbPath = Path.Combine(_directoryPath, "sessions.db");
            Store = new SqliteSessionStore($"Data Source={dbPath};Pooling=False", NullLogger<SqliteSessionStore>.Instance);
        }

        public ISessionStore Store { get; }

        public void Dispose()
        {
            if (Directory.Exists(_directoryPath))
                Directory.Delete(_directoryPath, recursive: true);
        }
    }
}
