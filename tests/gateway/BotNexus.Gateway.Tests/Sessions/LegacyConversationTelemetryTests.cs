using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// #2311 audit gate: <see cref="LegacyConversationResolver"/> exists solely to service a
/// completed one-time migration (#615, closed 2026-05-29). Before it can be deleted we
/// must be able to prove from a running environment whether anything still reaches it.
/// Today every session store constructs the resolver with <c>logger: null</c>, so live
/// activations are completely invisible. These tests pin the counter-based telemetry that
/// closes that gap: every resolve/create/bind is attributed to the call path that caused
/// it, so an operator can read a single snapshot and decide whether the shim is dead.
/// </summary>
public sealed class LegacyConversationTelemetryTests
{
    private static LegacyConversationResolver NewResolver(out InMemoryConversationStore store)
    {
        store = new InMemoryConversationStore();
        return new LegacyConversationResolver(store);
    }

    [Fact]
    public async Task Resolve_RecordsActivation_AttributedToCallPath()
    {
        var before = LegacyConversationTelemetry.Snapshot();
        var resolver = NewResolver(out _);

        await resolver.ResolveAsync(AgentId.From("tele-a"), LegacyResolveReason.SaveTimeStamp);

        var after = LegacyConversationTelemetry.Snapshot();
        (after.TotalResolves - before.TotalResolves).ShouldBe(1);
        (after.SaveTimeStampResolves - before.SaveTimeStampResolves).ShouldBe(1);
    }

    [Fact]
    public async Task Resolve_WhenConversationDoesNotExist_RecordsCreate()
    {
        var before = LegacyConversationTelemetry.Snapshot();
        var resolver = NewResolver(out _);

        await resolver.ResolveAsync(AgentId.From("tele-b"), LegacyResolveReason.LoadTimeBackfill);

        var after = LegacyConversationTelemetry.Snapshot();
        (after.TotalCreates - before.TotalCreates).ShouldBe(1);
        (after.LoadTimeBackfillResolves - before.LoadTimeBackfillResolves).ShouldBe(1);
    }

    [Fact]
    public async Task Resolve_WhenConversationAlreadyExists_DoesNotRecordAnotherCreate()
    {
        var resolver = NewResolver(out _);
        var agentId = AgentId.From("tele-c");
        await resolver.ResolveAsync(agentId, LegacyResolveReason.StartupMigration);

        var before = LegacyConversationTelemetry.Snapshot();
        await resolver.ResolveAsync(agentId, LegacyResolveReason.StartupMigration);
        var after = LegacyConversationTelemetry.Snapshot();

        (after.TotalCreates - before.TotalCreates).ShouldBe(0);
        (after.TotalResolves - before.TotalResolves).ShouldBe(1);
    }

    [Fact]
    public async Task Bind_RecordsActivation()
    {
        var resolver = NewResolver(out _);
        var conversation = await resolver.ResolveAsync(AgentId.From("tele-d"), LegacyResolveReason.StartupMigration);

        var before = LegacyConversationTelemetry.Snapshot();
        await resolver.BindActiveSessionIfNoneAsync(conversation, SessionId.From("s-tele-d"));
        var after = LegacyConversationTelemetry.Snapshot();

        (after.TotalBinds - before.TotalBinds).ShouldBe(1);
    }

    [Fact]
    public async Task Bind_WhenPointerAlreadySet_DoesNotRecordAnotherBind()
    {
        var resolver = NewResolver(out _);
        var conversation = await resolver.ResolveAsync(AgentId.From("tele-e"), LegacyResolveReason.StartupMigration);
        await resolver.BindActiveSessionIfNoneAsync(conversation, SessionId.From("s-tele-e-1"));

        var before = LegacyConversationTelemetry.Snapshot();
        await resolver.BindActiveSessionIfNoneAsync(conversation, SessionId.From("s-tele-e-2"));
        var after = LegacyConversationTelemetry.Snapshot();

        (after.TotalBinds - before.TotalBinds).ShouldBe(0);
    }

    [Fact]
    public async Task Snapshot_ReportsWhetherShimIsStillLive()
    {
        // The whole point of the audit gate: an operator reads HasActivity to decide
        // whether the shim can be deleted. A process that did touch the resolver must
        // report true.
        var resolver = NewResolver(out _);
        await resolver.ResolveAsync(AgentId.From("tele-f"), LegacyResolveReason.LoadTimeBackfill);

        LegacyConversationTelemetry.Snapshot().HasActivity.ShouldBeTrue();
    }

    [Fact]
    public void Snapshot_OnFreshCounters_ReportsNoActivity()
    {
        var empty = default(LegacyConversationTelemetrySnapshot);

        empty.HasActivity.ShouldBeFalse();
        empty.TotalResolves.ShouldBe(0);
    }

    [Fact]
    public async Task Resolve_DefaultReason_IsUnspecified()
    {
        // Callers that have not yet been attributed still get counted, so the total is
        // never under-reported - they just land in the Unspecified bucket.
        var before = LegacyConversationTelemetry.Snapshot();
        var resolver = NewResolver(out _);

        await resolver.ResolveAsync(AgentId.From("tele-g"));

        var after = LegacyConversationTelemetry.Snapshot();
        (after.UnspecifiedResolves - before.UnspecifiedResolves).ShouldBe(1);
    }
}
