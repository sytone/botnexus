using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// Resolve ambiguity: the Gateway enum is what SessionCleanupService uses
using SessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Tests;

public class SessionCleanupServiceTests
{
    private static SessionCleanupService CreateService(
        ISessionStore store,
        SessionCleanupOptions options,
        SessionLifecycleEvents? lifecycle = null)
    {
        return new SessionCleanupService(
            store,
            Options.Create(options),
            NullLogger<SessionCleanupService>.Instance,
            lifecycle);
    }

    private static GatewaySession CreateSession(
        string sessionId,
        string agentId,
        SessionStatus status,
        DateTimeOffset updatedAt)
    {
        return new GatewaySession
        {
            SessionId = SessionId.From(sessionId),
            AgentId = AgentId.From(agentId),
            Status = status,
            UpdatedAt = updatedAt,
        };
    }

    [Fact]
    public async Task RunCleanupOnce_ExpiresActiveSessions_PastTtl()
    {
        var store = new InMemorySessionStore();
        var oldSession = CreateSession("s-old", "agent-1", SessionStatus.Active,
            DateTimeOffset.UtcNow.AddHours(-25));
        var freshSession = CreateSession("s-fresh", "agent-1", SessionStatus.Active,
            DateTimeOffset.UtcNow.AddMinutes(-5));

        await store.SaveAsync(oldSession);
        await store.SaveAsync(freshSession);

        var options = new SessionCleanupOptions { SessionTtl = TimeSpan.FromHours(24) };
        var service = CreateService(store, options);

        await service.RunCleanupOnceAsync();

        var expired = await store.GetAsync(SessionId.From("s-old"));
        expired.ShouldNotBeNull();
        expired!.Status.ShouldBe(SessionStatus.Expired);
        expired.ExpiresAt.ShouldNotBeNull();

        var stillActive = await store.GetAsync(SessionId.From("s-fresh"));
        stillActive.ShouldNotBeNull();
        stillActive!.Status.ShouldBe(SessionStatus.Active);
    }

    [Fact]
    public async Task RunCleanupOnce_DoesNotExpireAlreadyExpiredSessions()
    {
        var store = new InMemorySessionStore();
        var session = CreateSession("s-expired", "agent-1", SessionStatus.Expired,
            DateTimeOffset.UtcNow.AddHours(-48));

        await store.SaveAsync(session);

        var options = new SessionCleanupOptions { SessionTtl = TimeSpan.FromHours(24) };
        var service = CreateService(store, options);

        await service.RunCleanupOnceAsync();

        var result = await store.GetAsync(SessionId.From("s-expired"));
        result.ShouldNotBeNull();
        result!.Status.ShouldBe(SessionStatus.Expired);
    }

    [Fact]
    public async Task RunCleanupOnce_DeletesSealedSessions_PastRetention()
    {
        var store = new InMemorySessionStore();
        var session = CreateSession("s-sealed", "agent-1", SessionStatus.Sealed,
            DateTimeOffset.UtcNow.AddDays(-8));

        await store.SaveAsync(session);

        var options = new SessionCleanupOptions
        {
            SessionTtl = TimeSpan.FromHours(24),
            ClosedSessionRetention = TimeSpan.FromDays(7)
        };
        var service = CreateService(store, options);

        await service.RunCleanupOnceAsync();

        var deleted = await store.GetAsync(SessionId.From("s-sealed"));
        deleted.ShouldBeNull("sealed session past retention should be deleted");
    }

    [Fact]
    public async Task RunCleanupOnce_KeepsSealedSessions_WithinRetention()
    {
        var store = new InMemorySessionStore();
        var session = CreateSession("s-sealed-recent", "agent-1", SessionStatus.Sealed,
            DateTimeOffset.UtcNow.AddDays(-3));

        await store.SaveAsync(session);

        var options = new SessionCleanupOptions
        {
            SessionTtl = TimeSpan.FromHours(24),
            ClosedSessionRetention = TimeSpan.FromDays(7)
        };
        var service = CreateService(store, options);

        await service.RunCleanupOnceAsync();

        var kept = await store.GetAsync(SessionId.From("s-sealed-recent"));
        kept.ShouldNotBeNull("sealed session within retention should be kept");
        kept!.Status.ShouldBe(SessionStatus.Sealed);
    }

    [Fact]
    public async Task RunCleanupOnce_DoesNotDeleteSealed_WhenRetentionNotConfigured()
    {
        var store = new InMemorySessionStore();
        var session = CreateSession("s-sealed-noret", "agent-1", SessionStatus.Sealed,
            DateTimeOffset.UtcNow.AddDays(-365));

        await store.SaveAsync(session);

        var options = new SessionCleanupOptions
        {
            SessionTtl = TimeSpan.FromHours(24),
            ClosedSessionRetention = null
        };
        var service = CreateService(store, options);

        await service.RunCleanupOnceAsync();

        var kept = await store.GetAsync(SessionId.From("s-sealed-noret"));
        kept.ShouldNotBeNull("sealed sessions should not be deleted when no retention is configured");
    }

    [Fact]
    public async Task RunCleanupOnce_EmptyStore_DoesNotThrow()
    {
        var store = new InMemorySessionStore();
        var options = new SessionCleanupOptions();
        var service = CreateService(store, options);

        Func<Task> act = () => service.RunCleanupOnceAsync();
        await act.ShouldNotThrowAsync();
    }

    [Fact]
    public async Task RunCleanupOnce_PublishesLifecycleEvents_OnExpiry()
    {
        var store = new InMemorySessionStore();
        var oldSession = CreateSession("s-lifecycle", "agent-1", SessionStatus.Active,
            DateTimeOffset.UtcNow.AddHours(-25));
        await store.SaveAsync(oldSession);

        var lifecycle = new SessionLifecycleEvents(NullLogger<SessionLifecycleEvents>.Instance);
        var events = new List<SessionLifecycleEvent>();
        lifecycle.SessionChanged += (evt, ct) =>
        {
            events.Add(evt);
            return Task.CompletedTask;
        };

        var options = new SessionCleanupOptions { SessionTtl = TimeSpan.FromHours(24) };
        var service = CreateService(store, options, lifecycle);

        await service.RunCleanupOnceAsync();

        events.ShouldHaveSingleItem();
        events[0].Type.ShouldBe(SessionLifecycleEventType.Expired);
        events[0].SessionId.ShouldBe("s-lifecycle");
    }

    [Fact]
    public async Task RunCleanupOnce_PublishesLifecycleEvents_OnDeletion()
    {
        var store = new InMemorySessionStore();
        var session = CreateSession("s-del-lifecycle", "agent-1", SessionStatus.Sealed,
            DateTimeOffset.UtcNow.AddDays(-8));
        await store.SaveAsync(session);

        var lifecycle = new SessionLifecycleEvents(NullLogger<SessionLifecycleEvents>.Instance);
        var events = new List<SessionLifecycleEvent>();
        lifecycle.SessionChanged += (evt, ct) =>
        {
            events.Add(evt);
            return Task.CompletedTask;
        };

        var options = new SessionCleanupOptions
        {
            SessionTtl = TimeSpan.FromHours(24),
            ClosedSessionRetention = TimeSpan.FromDays(7)
        };
        var service = CreateService(store, options, lifecycle);

        await service.RunCleanupOnceAsync();

        events.ShouldHaveSingleItem();
        events[0].Type.ShouldBe(SessionLifecycleEventType.Deleted);
    }

    [Fact]
    public async Task RunCleanupOnce_WithCancellation_StopsProcessing()
    {
        var store = new InMemorySessionStore();
        // Add several sessions
        for (int i = 0; i < 10; i++)
        {
            var session = CreateSession($"s-cancel-{i}", "agent-1", SessionStatus.Active,
                DateTimeOffset.UtcNow.AddHours(-25));
            await store.SaveAsync(session);
        }

        var options = new SessionCleanupOptions { SessionTtl = TimeSpan.FromHours(24) };
        var service = CreateService(store, options);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => service.RunCleanupOnceAsync(cts.Token);
        await act.ShouldThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RunCleanupOnce_DefaultTtl_WhenZero_FallsBackTo24Hours()
    {
        var store = new InMemorySessionStore();

        // Session updated 23 hours ago — should NOT be expired with 24h fallback
        var session = CreateSession("s-default-ttl", "agent-1", SessionStatus.Active,
            DateTimeOffset.UtcNow.AddHours(-23));
        await store.SaveAsync(session);

        var options = new SessionCleanupOptions { SessionTtl = TimeSpan.Zero };
        var service = CreateService(store, options);

        await service.RunCleanupOnceAsync();

        var result = await store.GetAsync(SessionId.From("s-default-ttl"));
        result.ShouldNotBeNull();
        result!.Status.ShouldBe(SessionStatus.Active, "23h-old session shouldn't expire with 24h fallback TTL");
    }

    [Fact]
    public async Task RunCleanupOnce_ConcurrentCalls_DoNotCorrupt()
    {
        var store = new InMemorySessionStore();
        for (int i = 0; i < 20; i++)
        {
            var session = CreateSession($"s-concurrent-{i}", "agent-1", SessionStatus.Active,
                DateTimeOffset.UtcNow.AddHours(-25));
            await store.SaveAsync(session);
        }

        var options = new SessionCleanupOptions { SessionTtl = TimeSpan.FromHours(24) };
        var service = CreateService(store, options);

        // Run cleanup concurrently
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => service.RunCleanupOnceAsync())
            .ToArray();

        Func<Task> act = () => Task.WhenAll(tasks);
        await act.ShouldNotThrowAsync("concurrent cleanup should not corrupt state");

        // All sessions should be expired
        var sessions = await store.ListAsync();
        sessions.ShouldAllBe(s => s.Status == SessionStatus.Expired);
    }
}
