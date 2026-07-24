using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Tests;

public class SessionConsistencyCheckerTests
{
    private const string AgentName = "agent-1";

    private static SessionConsistencyChecker CreateChecker(
        ISessionStore sessions,
        IConversationStore conversations,
        SessionConsistencyOptions? options = null,
        ISessionTurnTracker? turnTracker = null)
    {
        return new SessionConsistencyChecker(
            conversations,
            sessions,
            Options.Create(options ?? new SessionConsistencyOptions()),
            NullLogger<SessionConsistencyChecker>.Instance,
            turnTracker,
            TimeProvider.System);
    }

    private static GatewaySession CreateSession(
        string sessionId,
        string conversationId,
        SessionStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset? updatedAt = null)
    {
        return new GatewaySession
        {
            SessionId = SessionId.From(sessionId),
            AgentId = Domain.Primitives.AgentId.From(AgentName),
            ConversationId = ConversationId.From(conversationId),
            Status = status,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt ?? createdAt,
        };
    }

    private static Conversation CreateConversation(
        string conversationId,
        string? activeSessionId,
        ConversationKind kind = ConversationKind.HumanAgent)
    {
        return new Conversation
        {
            ConversationId = ConversationId.From(conversationId),
            AgentId = Domain.Primitives.AgentId.From(AgentName),
            Kind = kind,
            ActiveSessionId = activeSessionId is null ? null : SessionId.From(activeSessionId),
        };
    }

    [Fact]
    public async Task DetectsAndRepairs_CronPoisonedHumanConversation_ToLatestLiveSession()
    {
        var sessions = new InMemorySessionStore();
        var conversations = new InMemoryConversationStore();

        const string conv = "c_poison";
        var cron = CreateSession("cron:20260101:abc", conv, SessionStatus.Active,
            DateTimeOffset.UtcNow.AddDays(-10));
        var live = CreateSession("live-signalr", conv, SessionStatus.Active,
            DateTimeOffset.UtcNow.AddMinutes(-5));
        await sessions.SaveAsync(cron);
        await sessions.SaveAsync(live);

        var conversation = CreateConversation(conv, "cron:20260101:abc");
        await conversations.CreateAsync(conversation);

        var checker = CreateChecker(sessions, conversations);

        var report = await checker.RunOnceAsync(dryRun: false);

        var updated = await conversations.GetAsync(ConversationId.From(conv));
        updated.ShouldNotBeNull();
        updated!.ActiveSessionId!.Value.Value.ShouldBe("live-signalr");
        report.Discrepancies.ShouldContain(d => d.Repaired && d.Invariant == "active-session-cron-poison");
    }

    [Fact]
    public async Task LeavesConsistentConversationUntouched()
    {
        var sessions = new InMemorySessionStore();
        var conversations = new InMemoryConversationStore();

        const string conv = "c_ok";
        var live = CreateSession("live-signalr", conv, SessionStatus.Active,
            DateTimeOffset.UtcNow.AddMinutes(-5));
        await sessions.SaveAsync(live);

        var conversation = CreateConversation(conv, "live-signalr");
        await conversations.CreateAsync(conversation);

        var checker = CreateChecker(sessions, conversations);

        var report = await checker.RunOnceAsync(dryRun: false);

        var updated = await conversations.GetAsync(ConversationId.From(conv));
        updated!.ActiveSessionId!.Value.Value.ShouldBe("live-signalr");
        report.Discrepancies.ShouldBeEmpty();
    }

    [Fact]
    public async Task ClearsPointer_WhenActiveSessionMissing()
    {
        var sessions = new InMemorySessionStore();
        var conversations = new InMemoryConversationStore();

        const string conv = "c_missing";
        var conversation = CreateConversation(conv, "gone-session");
        await conversations.CreateAsync(conversation);

        var checker = CreateChecker(sessions, conversations);

        var report = await checker.RunOnceAsync(dryRun: false);

        var updated = await conversations.GetAsync(ConversationId.From(conv));
        updated!.ActiveSessionId.ShouldBeNull();
        report.Discrepancies.ShouldContain(d => d.Repaired && d.Invariant == "active-session-missing");
    }

    [Fact]
    public async Task SealsOverStaleActiveCronSession()
    {
        var sessions = new InMemorySessionStore();
        var conversations = new InMemoryConversationStore();

        var cron = CreateSession("cron:20260101:stale", "c_other", SessionStatus.Active,
            DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-10));
        await sessions.SaveAsync(cron);

        var options = new SessionConsistencyOptions
        {
            StaleActiveCronThreshold = TimeSpan.FromHours(6),
        };
        var checker = CreateChecker(sessions, conversations, options);

        var report = await checker.RunOnceAsync(dryRun: false);

        var updated = await sessions.GetAsync(SessionId.From("cron:20260101:stale"));
        updated!.Status.ShouldBe(SessionStatus.Sealed);
        report.Discrepancies.ShouldContain(d => d.Repaired && d.Invariant == "stale-active-cron");
    }

    [Fact]
    public async Task DryRun_ReportsButDoesNotMutate()
    {
        var sessions = new InMemorySessionStore();
        var conversations = new InMemoryConversationStore();

        const string conv = "c_poison";
        var cron = CreateSession("cron:20260101:abc", conv, SessionStatus.Active,
            DateTimeOffset.UtcNow.AddDays(-10));
        var live = CreateSession("live-signalr", conv, SessionStatus.Active,
            DateTimeOffset.UtcNow.AddMinutes(-5));
        await sessions.SaveAsync(cron);
        await sessions.SaveAsync(live);

        var conversation = CreateConversation(conv, "cron:20260101:abc");
        await conversations.CreateAsync(conversation);

        var checker = CreateChecker(sessions, conversations);

        var report = await checker.RunOnceAsync(dryRun: true);

        var updated = await conversations.GetAsync(ConversationId.From(conv));
        updated!.ActiveSessionId!.Value.Value.ShouldBe("cron:20260101:abc");
        report.Discrepancies.ShouldContain(d => !d.Repaired && d.Invariant == "active-session-cron-poison");
    }

    [Fact]
    public async Task Idempotent_SecondRunFindsNothing()
    {
        var sessions = new InMemorySessionStore();
        var conversations = new InMemoryConversationStore();

        const string conv = "c_poison";
        var cron = CreateSession("cron:20260101:abc", conv, SessionStatus.Active,
            DateTimeOffset.UtcNow.AddDays(-10));
        var live = CreateSession("live-signalr", conv, SessionStatus.Active,
            DateTimeOffset.UtcNow.AddMinutes(-5));
        await sessions.SaveAsync(cron);
        await sessions.SaveAsync(live);

        var conversation = CreateConversation(conv, "cron:20260101:abc");
        await conversations.CreateAsync(conversation);

        var options = new SessionConsistencyOptions { StaleActiveCronThreshold = TimeSpan.FromDays(365) };
        var checker = CreateChecker(sessions, conversations, options);

        await checker.RunOnceAsync(dryRun: false);
        var second = await checker.RunOnceAsync(dryRun: false);

        second.Discrepancies.ShouldBeEmpty();
    }

    [Fact]
    public async Task SkipsRepair_WhenLiveTurnOnPoisonedPointer()
    {
        var sessions = new InMemorySessionStore();
        var conversations = new InMemoryConversationStore();

        const string conv = "c_poison";
        var cron = CreateSession("cron:20260101:abc", conv, SessionStatus.Active,
            DateTimeOffset.UtcNow.AddDays(-10));
        var live = CreateSession("live-signalr", conv, SessionStatus.Active,
            DateTimeOffset.UtcNow.AddMinutes(-5));
        await sessions.SaveAsync(cron);
        await sessions.SaveAsync(live);

        var conversation = CreateConversation(conv, "cron:20260101:abc");
        await conversations.CreateAsync(conversation);

        var tracker = new FakeTurnTracker("cron:20260101:abc");
        var checker = CreateChecker(sessions, conversations, turnTracker: tracker);

        var report = await checker.RunOnceAsync(dryRun: false);

        var updated = await conversations.GetAsync(ConversationId.From(conv));
        updated!.ActiveSessionId!.Value.Value.ShouldBe("cron:20260101:abc");
        report.Discrepancies.ShouldContain(d => !d.Repaired && d.Invariant == "active-session-cron-poison");
    }

    private sealed class FakeTurnTracker(params string[] liveSessionIds) : ISessionTurnTracker
    {
        private readonly HashSet<string> _live = new(liveSessionIds, StringComparer.Ordinal);

        public IDisposable BeginTurn(string sessionId) => new Noop();

        public bool HasLiveTurn(string sessionId) => _live.Contains(sessionId);

        private sealed class Noop : IDisposable
        {
            public void Dispose() { }
        }
    }
}
