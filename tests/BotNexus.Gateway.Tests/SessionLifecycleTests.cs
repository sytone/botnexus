using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Tests;

public sealed class SessionLifecycleTests
{
    [Fact]
    public void NewSession_HasActiveStatus()
    {
        var session = new GatewaySession { SessionId = "s1", AgentId = "agent-a" };

        session.Status.Should().Be(SessionStatus.Active);
        session.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task SessionStatus_TransitionsToExpired_AfterTTL()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.UpdatedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(30);
        await store.SaveAsync(session);

        var service = CreateService(store, new SessionCleanupOptions { SessionTtl = TimeSpan.FromHours(24) });
        await service.RunCleanupOnceAsync();

        var updated = await store.GetAsync("s1");
        updated!.Status.Should().Be(SessionStatus.Expired);
        updated.ExpiresAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SessionCleanupService_RunsPeriodically()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.UpdatedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1);
        await store.SaveAsync(session);

        var service = CreateService(
            store,
            new SessionCleanupOptions
            {
                CheckInterval = TimeSpan.FromMilliseconds(50),
                SessionTtl = TimeSpan.FromMilliseconds(1)
            });

        await service.StartAsync(CancellationToken.None);
        try
        {
            var expired = await WaitForConditionAsync(async () =>
            {
                var current = await store.GetAsync("s1");
                return current?.Status == SessionStatus.Expired;
            });

            expired.Should().BeTrue();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SessionCleanupService_SkipsActiveSessions_WithRecentActivity()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.UpdatedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2);
        await store.SaveAsync(session);

        var service = CreateService(store, new SessionCleanupOptions { SessionTtl = TimeSpan.FromHours(1) });
        await service.RunCleanupOnceAsync();

        var updated = await store.GetAsync("s1");
        updated!.Status.Should().Be(SessionStatus.Active);
        updated.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task ClosedSessions_AreDeletedAfterRetention()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("closed", "agent-a");
        session.Status = SessionStatus.Sealed;
        session.UpdatedAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(8);
        await store.SaveAsync(session);

        var service = CreateService(store, new SessionCleanupOptions
        {
            SessionTtl = TimeSpan.FromHours(24),
            ClosedSessionRetention = TimeSpan.FromDays(7)
        });

        await service.RunCleanupOnceAsync();

        (await store.GetAsync("closed")).Should().BeNull();
    }

    [Fact]
    public async Task SessionStatus_Serialization_RoundTrips()
    {
        using var fixture = new SessionStoreFixture();
        var store = fixture.CreateStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.Status = SessionStatus.Suspended;
        session.ExpiresAt = DateTimeOffset.UtcNow.AddHours(1);
        await store.SaveAsync(session);

        var reloaded = await fixture.CreateStore().GetAsync("s1");

        reloaded!.Status.Should().Be(SessionStatus.Suspended);
        reloaded.ExpiresAt.Should().BeCloseTo(session.ExpiresAt!.Value, TimeSpan.FromSeconds(1));
    }

    private static SessionCleanupService CreateService(InMemorySessionStore store, SessionCleanupOptions options)
        => new(
            store,
            Options.Create(options),
            NullLogger<SessionCleanupService>.Instance);

    private static async Task<bool> WaitForConditionAsync(Func<Task<bool>> condition, int maxAttempts = 20)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (await condition())
                return true;

            await Task.Delay(100);
        }

        return false;
    }

    private sealed class SessionStoreFixture : IDisposable
    {
        public SessionStoreFixture()
        {
            StorePath = Path.Combine(
                AppContext.BaseDirectory,
                "SessionLifecycleTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(StorePath);
        }

        public string StorePath { get; }

        public FileSessionStore CreateStore()
            => new(StorePath, NullLogger<FileSessionStore>.Instance, new FileSystem());

        public void Dispose()
        {
            if (Directory.Exists(StorePath))
                Directory.Delete(StorePath, recursive: true);
        }
    }
}

