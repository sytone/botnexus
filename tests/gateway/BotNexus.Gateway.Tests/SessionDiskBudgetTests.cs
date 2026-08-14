using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using SessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Covers the session-directory disk budget (issue #2848): the disabled contract, Warn mode,
/// Enforce-mode oldest-first eviction and its stopping point, the in-flight-run guard, and
/// default-configuration parity with the pre-budget sweep.
/// </summary>
public class SessionDiskBudgetTests
{
    private static SessionCleanupService CreateService(
        ISessionStore store,
        SessionCleanupOptions options,
        SessionLifecycleEvents? lifecycle = null,
        ISessionTurnTracker? turnTracker = null) =>
        new(store, Options.Create(options), NullLogger<SessionCleanupService>.Instance, lifecycle, turnTracker);

    /// <summary>
    /// Creates a session whose accounted footprint is at least <paramref name="payloadBytes"/>,
    /// by giving it a single ASCII transcript entry of that length.
    /// </summary>
    private static GatewaySession CreateSession(
        string sessionId,
        SessionStatus status,
        DateTimeOffset updatedAt,
        int payloadBytes,
        string agentId = "agent-1")
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From(sessionId),
            AgentId = AgentId.From(agentId),
            Status = status,
            UpdatedAt = updatedAt,
        };
        session.History.Add(new SessionEntry
        {
            Role = MessageRole.Assistant,
            Content = new string('x', payloadBytes),
        });
        return session;
    }

    private static SessionDiskUsage Usage(
        string sessionId,
        SessionStatus status,
        DateTimeOffset updatedAt,
        long bytes,
        string agentId = "agent-1") =>
        new(sessionId, agentId, status, updatedAt, bytes);

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- AC1 / AC2: disabled contract

    [Fact]
    public void Options_DefaultDiskBudget_IsDisabled()
    {
        var options = new SessionCleanupOptions();

        options.MaxDiskBytes.ShouldBeNull();
        options.HighWaterBytes.ShouldBeNull();
        options.DiskBudgetMode.ShouldBe(SessionDiskBudgetMode.Warn);
        options.ResolveMaxDiskBytes().ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void ResolveMaxDiskBytes_NullZeroOrNegative_IsDisabled(long? configured)
    {
        var options = new SessionCleanupOptions { MaxDiskBytes = configured };

        options.ResolveMaxDiskBytes().ShouldBeNull(
            "a null, zero, or negative budget disables the budget - it is never a zero-byte budget (openclaw#119422)");
    }

    [Fact]
    public void ResolveHighWaterBytes_Unset_DefaultsTo80Percent()
    {
        var options = new SessionCleanupOptions { MaxDiskBytes = 1000 };

        options.ResolveHighWaterBytes(1000).ShouldBe(800);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    [InlineData(2000L)] // above the budget: nonsensical, falls back
    public void ResolveHighWaterBytes_OutOfRange_FallsBackTo80Percent(long configured)
    {
        var options = new SessionCleanupOptions { MaxDiskBytes = 1000, HighWaterBytes = configured };

        options.ResolveHighWaterBytes(1000).ShouldBe(800);
    }

    [Fact]
    public void BuildPlan_ZeroBudget_EvictsNothing_AndReportsDisabled()
    {
        var usages = new[]
        {
            Usage("s-1", SessionStatus.Sealed, T0, 1_000_000),
            Usage("s-2", SessionStatus.Expired, T0, 1_000_000),
        };
        var options = new SessionCleanupOptions
        {
            MaxDiskBytes = 0,
            DiskBudgetMode = SessionDiskBudgetMode.Enforce,
        };

        var plan = SessionDiskBudgetPlanner.BuildPlan(usages, options, _ => false);

        plan.BudgetEnabled.ShouldBeFalse();
        plan.OverBudget.ShouldBeFalse();
        plan.Evictions.ShouldBeEmpty("a zero budget must delete NOTHING (openclaw#119422)");
    }

    [Fact]
    public async Task RunCleanupOnce_ZeroBudgetInEnforceMode_DeletesNothing()
    {
        // The #119422 regression pin, end-to-end through the service: a zero budget in the most
        // dangerous mode must leave every session artifact in place.
        var store = new InMemorySessionStore();
        await store.SaveAsync(CreateSession("s-sealed", SessionStatus.Sealed, DateTimeOffset.UtcNow.AddMinutes(-1), 4096));
        await store.SaveAsync(CreateSession("s-expired", SessionStatus.Expired, DateTimeOffset.UtcNow.AddMinutes(-1), 4096));
        await store.SaveAsync(CreateSession("s-active", SessionStatus.Active, DateTimeOffset.UtcNow, 4096));

        var options = new SessionCleanupOptions
        {
            SessionTtl = TimeSpan.FromDays(3650),
            ClosedSessionRetention = null,
            CronNoopRetention = null,
            MaxDiskBytes = 0,
            DiskBudgetMode = SessionDiskBudgetMode.Enforce,
        };

        await CreateService(store, options).RunCleanupOnceAsync();

        var remaining = await store.ListAsync();
        remaining.Count.ShouldBe(3, "MaxDiskBytes = 0 disables the budget and must delete nothing");
    }

    [Fact]
    public async Task RunCleanupOnce_NegativeBudgetInEnforceMode_DeletesNothing()
    {
        var store = new InMemorySessionStore();
        await store.SaveAsync(CreateSession("s-sealed", SessionStatus.Sealed, DateTimeOffset.UtcNow.AddMinutes(-1), 4096));

        var options = new SessionCleanupOptions
        {
            SessionTtl = TimeSpan.FromDays(3650),
            MaxDiskBytes = -1,
            DiskBudgetMode = SessionDiskBudgetMode.Enforce,
        };

        await CreateService(store, options).RunCleanupOnceAsync();

        (await store.ListAsync()).Count.ShouldBe(1);
    }

    // ---------------------------------------------------------------------------- AC3: Warn mode

    [Fact]
    public void BuildPlan_WarnMode_OverBudget_EvictsNothing()
    {
        var usages = new[]
        {
            Usage("s-1", SessionStatus.Sealed, T0, 600),
            Usage("s-2", SessionStatus.Sealed, T0.AddHours(1), 600),
        };
        var options = new SessionCleanupOptions
        {
            MaxDiskBytes = 1000,
            DiskBudgetMode = SessionDiskBudgetMode.Warn,
        };

        var plan = SessionDiskBudgetPlanner.BuildPlan(usages, options, _ => false);

        plan.BudgetEnabled.ShouldBeTrue();
        plan.TotalBytes.ShouldBe(1200);
        plan.OverBudget.ShouldBeTrue("pressure must still be reported in Warn mode");
        plan.Evictions.ShouldBeEmpty("Warn mode logs only");
    }

    [Fact]
    public async Task RunCleanupOnce_WarnMode_OverBudget_SessionCountUnchanged()
    {
        var store = new InMemorySessionStore();
        for (var i = 0; i < 5; i++)
        {
            await store.SaveAsync(CreateSession(
                $"s-warn-{i}", SessionStatus.Sealed, DateTimeOffset.UtcNow.AddHours(-i), 4096));
        }

        var options = new SessionCleanupOptions
        {
            SessionTtl = TimeSpan.FromDays(3650),
            ClosedSessionRetention = null,
            MaxDiskBytes = 1024,          // deliberately far below the ~20KB footprint
            DiskBudgetMode = SessionDiskBudgetMode.Warn,
        };

        await CreateService(store, options).RunCleanupOnceAsync();

        (await store.ListAsync()).Count.ShouldBe(5, "Warn mode must not delete anything");
    }

    // ---------------------------------------------- AC4: Enforce mode order and stopping point

    [Fact]
    public void BuildPlan_EnforceMode_EvictsOldestFirst_AndStopsAtHighWater()
    {
        // Total 1000; budget 900; high water 500 -> must evict 500 bytes' worth, oldest first.
        var usages = new[]
        {
            Usage("s-newest", SessionStatus.Sealed, T0.AddHours(3), 250),
            Usage("s-oldest", SessionStatus.Sealed, T0, 250),
            Usage("s-middle", SessionStatus.Sealed, T0.AddHours(1), 250),
            Usage("s-later", SessionStatus.Sealed, T0.AddHours(2), 250),
        };
        var options = new SessionCleanupOptions
        {
            MaxDiskBytes = 900,
            HighWaterBytes = 500,
            DiskBudgetMode = SessionDiskBudgetMode.Enforce,
        };

        var plan = SessionDiskBudgetPlanner.BuildPlan(usages, options, _ => false);

        plan.TotalBytes.ShouldBe(1000);
        plan.HighWaterBytes.ShouldBe(500);
        plan.Evictions.Select(e => e.SessionId).ShouldBe(new[] { "s-oldest", "s-middle" });
        // Stopping point: exactly enough to reach the high-water mark, not one session more.
        (plan.TotalBytes - plan.Evictions.Sum(e => e.Bytes)).ShouldBe(500);
    }

    [Fact]
    public void BuildPlan_EnforceMode_UnderBudget_EvictsNothing()
    {
        var usages = new[] { Usage("s-1", SessionStatus.Sealed, T0, 100) };
        var options = new SessionCleanupOptions
        {
            MaxDiskBytes = 1000,
            DiskBudgetMode = SessionDiskBudgetMode.Enforce,
        };

        var plan = SessionDiskBudgetPlanner.BuildPlan(usages, options, _ => false);

        plan.OverBudget.ShouldBeFalse();
        plan.Evictions.ShouldBeEmpty();
    }

    [Fact]
    public void BuildPlan_EnforceMode_EvictsSealedBeforeExpired_AndNeverActive()
    {
        var usages = new[]
        {
            Usage("s-active-ancient", SessionStatus.Active, T0.AddYears(-5), 400),
            Usage("s-expired-old", SessionStatus.Expired, T0, 400),
            Usage("s-sealed-new", SessionStatus.Sealed, T0.AddDays(10), 400),
        };
        var options = new SessionCleanupOptions
        {
            MaxDiskBytes = 500,
            HighWaterBytes = 400,
            DiskBudgetMode = SessionDiskBudgetMode.Enforce,
        };

        var plan = SessionDiskBudgetPlanner.BuildPlan(usages, options, _ => false);

        plan.Evictions.Select(e => e.SessionId).ShouldBe(new[] { "s-sealed-new", "s-expired-old" });
        plan.Evictions.ShouldNotContain(e => e.Status == SessionStatus.Active,
            "an active session is live work and is never evicted by the size path");
    }

    [Fact]
    public async Task RunCleanupOnce_EnforceMode_DeletesOldestFirst_AndKeepsTheRest()
    {
        var store = new InMemorySessionStore();
        var now = DateTimeOffset.UtcNow;
        // Four sealed sessions of ~1KB each.
        await store.SaveAsync(CreateSession("s-oldest", SessionStatus.Sealed, now.AddHours(-4), 1024));
        await store.SaveAsync(CreateSession("s-old", SessionStatus.Sealed, now.AddHours(-3), 1024));
        await store.SaveAsync(CreateSession("s-recent", SessionStatus.Sealed, now.AddHours(-2), 1024));
        await store.SaveAsync(CreateSession("s-newest", SessionStatus.Sealed, now.AddHours(-1), 1024));

        var options = new SessionCleanupOptions
        {
            SessionTtl = TimeSpan.FromDays(3650),
            ClosedSessionRetention = null,
            MaxDiskBytes = 3000,
            HighWaterBytes = 2200,
            DiskBudgetMode = SessionDiskBudgetMode.Enforce,
        };

        await CreateService(store, options).RunCleanupOnceAsync();

        var remaining = (await store.ListAsync()).Select(s => s.SessionId.Value).ToList();
        remaining.ShouldNotContain("s-oldest");
        remaining.ShouldContain("s-newest", "eviction is oldest-first and stops at the high-water mark");
        remaining.ShouldContain("s-recent");
    }

    // --------------------------------------------------------------- AC5: in-flight run is safe

    [Fact]
    public void BuildPlan_NeverEvictsSessionWithInFlightRun_EvenWhenOldest()
    {
        var usages = new[]
        {
            Usage("s-live-oldest", SessionStatus.Sealed, T0, 500),
            Usage("s-idle-newer", SessionStatus.Sealed, T0.AddHours(1), 500),
        };
        var options = new SessionCleanupOptions
        {
            MaxDiskBytes = 600,
            HighWaterBytes = 500,
            DiskBudgetMode = SessionDiskBudgetMode.Enforce,
        };

        var plan = SessionDiskBudgetPlanner.BuildPlan(
            usages, options, id => id == "s-live-oldest");

        plan.Evictions.Select(e => e.SessionId).ShouldBe(new[] { "s-idle-newer" });
    }

    [Fact]
    public async Task RunCleanupOnce_EnforceMode_DoesNotEvictSessionWithInFlightRun()
    {
        var store = new InMemorySessionStore();
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(CreateSession("s-live", SessionStatus.Sealed, now.AddHours(-9), 2048));
        await store.SaveAsync(CreateSession("s-idle", SessionStatus.Sealed, now.AddHours(-1), 2048));

        var tracker = new SessionTurnTracker();
        using var scope = tracker.BeginTurn("s-live");

        var options = new SessionCleanupOptions
        {
            SessionTtl = TimeSpan.FromDays(3650),
            ClosedSessionRetention = null,
            MaxDiskBytes = 1024,   // hard over budget; the planner would evict everything it may
            HighWaterBytes = 512,
            DiskBudgetMode = SessionDiskBudgetMode.Enforce,
        };

        await CreateService(store, options, lifecycle: null, turnTracker: tracker).RunCleanupOnceAsync();

        (await store.GetAsync(SessionId.From("s-live"))).ShouldNotBeNull(
            "a session with a live run must never be evicted by the size path, even as the oldest");
        (await store.GetAsync(SessionId.From("s-idle"))).ShouldBeNull(
            "the eligible session is still evicted, so the guard is not vacuously passing");
    }

    // --------------------------------------------------------------------- AC6: default parity

    [Fact]
    public async Task RunCleanupOnce_DefaultOptions_DeleteSetIsIdenticalToPreBudgetSweep()
    {
        // Parity: build one store, run with stock defaults, and assert the surviving set is
        // exactly what the age predicates alone would leave - no session is deleted that the
        // pre-budget sweep would have kept.
        static async Task<InMemorySessionStore> SeedAsync()
        {
            var store = new InMemorySessionStore();
            var now = DateTimeOffset.UtcNow;
            await store.SaveAsync(CreateSession("s-fresh", SessionStatus.Active, now.AddMinutes(-5), 100_000));
            await store.SaveAsync(CreateSession("s-stale", SessionStatus.Active, now.AddHours(-25), 100_000));
            await store.SaveAsync(CreateSession("s-sealed-ancient", SessionStatus.Sealed, now.AddDays(-400), 100_000));
            await store.SaveAsync(CreateSession("s-expired", SessionStatus.Expired, now.AddDays(-400), 100_000));
            return store;
        }

        var store = await SeedAsync();
        await CreateService(store, new SessionCleanupOptions()).RunCleanupOnceAsync();
        var withDefaults = (await store.ListAsync())
            .Select(s => $"{s.SessionId.Value}:{s.Status}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        // The pre-budget expectation, stated independently rather than derived from the run:
        // TTL expires the stale active session; nothing is deleted because ClosedSessionRetention
        // defaults to null and the sessions are not cron noops.
        withDefaults.ShouldBe(new[]
        {
            "s-expired:Expired",
            "s-fresh:Active",
            "s-sealed-ancient:Sealed",
            "s-stale:Expired",
        });
    }

    [Fact]
    public void BuildPlan_DefaultOptions_IsDisabled_RegardlessOfFootprint()
    {
        var usages = new[]
        {
            Usage("s-huge", SessionStatus.Sealed, T0, long.MaxValue / 4),
        };

        var plan = SessionDiskBudgetPlanner.BuildPlan(usages, new SessionCleanupOptions(), _ => false);

        plan.BudgetEnabled.ShouldBeFalse();
        plan.Evictions.ShouldBeEmpty("the default configuration must preserve current behaviour exactly");
    }

    // ------------------------------------------------------------------------ accounting basics

    [Fact]
    public void Measure_LargerTranscript_AccountsMoreBytes()
    {
        var small = CreateSession("s-small", SessionStatus.Sealed, T0, 10);
        var large = CreateSession("s-large", SessionStatus.Sealed, T0, 10_000);

        SessionDiskAccounting.Measure(large).ShouldBeGreaterThan(SessionDiskAccounting.Measure(small));
        SessionDiskAccounting.Measure(small).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void ToUsage_ProjectsIdentityAndStatus()
    {
        var session = CreateSession("s-proj", SessionStatus.Expired, T0, 32, agentId: "agent-9");

        var usage = SessionDiskAccounting.ToUsage(session);

        usage.SessionId.ShouldBe("s-proj");
        usage.AgentId.ShouldBe("agent-9");
        usage.Status.ShouldBe(SessionStatus.Expired);
        usage.UpdatedAt.ShouldBe(T0);
        usage.Bytes.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void BuildPlan_BudgetIsPerAgent_TotalsOnlyTheSuppliedGroup()
    {
        var usages = new[]
        {
            Usage("a1-s1", SessionStatus.Sealed, T0, 400, agentId: "agent-1"),
            Usage("a1-s2", SessionStatus.Sealed, T0.AddHours(1), 400, agentId: "agent-1"),
        };
        var options = new SessionCleanupOptions { MaxDiskBytes = 1000 };

        SessionDiskBudgetPlanner.BuildPlan(usages, options, _ => false).OverBudget.ShouldBeFalse();
    }
}
