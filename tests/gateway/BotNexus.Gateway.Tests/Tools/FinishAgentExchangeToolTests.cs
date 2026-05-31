using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tools;
using Shouldly;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;

namespace BotNexus.Gateway.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="FinishAgentExchangeTool"/>, the tool an agent invokes to signal
/// completion of an agent-to-agent exchange. The tool's contract (and the bugs it prevents
/// — see F-11 / issue #379) is enforced from two angles:
/// 1. Behavioural: the tool requires an active exchange id; writes the matching payload on success.
/// 2. Security: an out-of-band invocation (no active id) must throw without persisting state.
/// </summary>
public sealed class FinishAgentExchangeToolTests
{
    [Fact]
    public async Task ExecuteAsync_WithActiveExchangeId_WritesFinishPayloadToSessionMetadata()
    {
        var store = new InMemorySessionStore();
        var sid = SessionId.From("test-session-1");
        var session = MakeSession(sid);
        session.Metadata[FinishAgentExchangeTool.ActiveExchangeIdKey] = "active-xchg-123";
        await store.SaveAsync(session, CancellationToken.None);

        var tool = new FinishAgentExchangeTool(store, sid);

        var result = await tool.ExecuteAsync(
            toolCallId: "call-1",
            arguments: new Dictionary<string, object?>
            {
                ["reason"] = "objective met",
                ["summary"] = "Reviewed the PR; no blockers."
            });

        result.Content.ShouldNotBeEmpty();
        var refreshed = (await store.GetAsync(sid, CancellationToken.None))!;
        refreshed.Metadata[FinishAgentExchangeTool.FinishedExchangeIdKey].ShouldBe("active-xchg-123");
        refreshed.Metadata[FinishAgentExchangeTool.FinishedReasonKey].ShouldBe("objective met");
        refreshed.Metadata[FinishAgentExchangeTool.FinishedSummaryKey].ShouldBe("Reviewed the PR; no blockers.");
    }

    [Fact]
    public async Task ExecuteAsync_WithoutActiveExchangeId_ThrowsAndDoesNotPersist()
    {
        // Security regression: the tool MUST refuse when called outside an active exchange — for
        // example if an agent in some other context (heartbeat, user-driven session) hallucinates
        // calling the tool. If this guard fails, that hallucination would silently write a finish
        // payload that could be replayed by a parallel exchange that happens to share the SessionId.
        var store = new InMemorySessionStore();
        var sid = SessionId.From("test-session-2");
        await store.SaveAsync(MakeSession(sid), CancellationToken.None); // no activeAgentExchangeId

        var tool = new FinishAgentExchangeTool(store, sid);

        var act = async () => await tool.ExecuteAsync(
            "call-1",
            new Dictionary<string, object?> { ["reason"] = "trying to finish" });

        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("No active agent-to-agent exchange");

        var refreshed = (await store.GetAsync(sid, CancellationToken.None))!;
        refreshed.Metadata.ShouldNotContainKey(FinishAgentExchangeTool.FinishedExchangeIdKey);
        refreshed.Metadata.ShouldNotContainKey(FinishAgentExchangeTool.FinishedReasonKey);
    }

    [Fact]
    public async Task ExecuteAsync_WithJsonElementActiveId_ToleratesPersistenceRoundTrip()
    {
        // SqliteSessionStore and FileSessionStore round-trip object? metadata as JsonElement.
        // A naive `value as string` cast returns null and the guard above would falsely treat
        // the active id as missing — see the helper MetadataString pattern in CrossWorldFederationController.
        var store = new InMemorySessionStore();
        var sid = SessionId.From("test-session-3");
        var session = MakeSession(sid);
        session.Metadata[FinishAgentExchangeTool.ActiveExchangeIdKey] =
            JsonDocument.Parse("\"active-xchg-456\"").RootElement;
        await store.SaveAsync(session, CancellationToken.None);

        var tool = new FinishAgentExchangeTool(store, sid);

        var result = await tool.ExecuteAsync(
            "call-1",
            new Dictionary<string, object?> { ["reason"] = "done" });

        result.Content.ShouldNotBeEmpty();
        var refreshed = (await store.GetAsync(sid, CancellationToken.None))!;
        refreshed.Metadata[FinishAgentExchangeTool.FinishedExchangeIdKey].ShouldBe("active-xchg-456");
    }

    [Fact]
    public async Task ExecuteAsync_WithoutSummary_RemovesPriorSummaryEntry()
    {
        // Defence in depth against stale-payload replay: if a previous finish wrote a summary
        // and a new finish-without-summary supersedes it, the prior summary must not leak forward.
        var store = new InMemorySessionStore();
        var sid = SessionId.From("test-session-4");
        var session = MakeSession(sid);
        session.Metadata[FinishAgentExchangeTool.ActiveExchangeIdKey] = "active-xchg-789";
        session.Metadata[FinishAgentExchangeTool.FinishedSummaryKey] = "prior summary that should not leak";
        await store.SaveAsync(session, CancellationToken.None);

        var tool = new FinishAgentExchangeTool(store, sid);

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["reason"] = "done" });

        var refreshed = (await store.GetAsync(sid, CancellationToken.None))!;
        refreshed.Metadata.ShouldNotContainKey(FinishAgentExchangeTool.FinishedSummaryKey);
    }

    [Fact]
    public async Task PrepareArgumentsAsync_MissingReason_ThrowsArgumentException()
    {
        var store = new InMemorySessionStore();
        var tool = new FinishAgentExchangeTool(store, SessionId.From("test-session-5"));

        var act = async () => await tool.PrepareArgumentsAsync(new Dictionary<string, object?>());

        var ex = await Should.ThrowAsync<ArgumentException>(act);
        ex.Message.ShouldContain("reason");
    }

    [Fact]
    public async Task ExecuteAsync_SessionNotFound_Throws()
    {
        // If the SessionId the tool was constructed with no longer exists in the store, the tool
        // must refuse — never silently no-op. A silent no-op would let the agent believe completion
        // succeeded when nothing was persisted, leading to a stuck loop on the service side.
        var store = new InMemorySessionStore();
        var tool = new FinishAgentExchangeTool(store, SessionId.From("missing-session"));

        var act = async () => await tool.ExecuteAsync(
            "call-1",
            new Dictionary<string, object?> { ["reason"] = "done" });

        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("session not found");
    }

    private static GatewaySession MakeSession(SessionId sid) => new(
        new Session
        {
            SessionId = sid,
            ChannelType = ChannelKey.From("test"),
            SessionType = SessionType.AgentAgent,
            Status = GatewaySessionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        })
    {
        AgentId = AgentId.From("test-agent")
    };
}
