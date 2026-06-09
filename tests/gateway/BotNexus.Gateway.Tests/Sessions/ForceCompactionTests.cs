using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// Tests that user-initiated /compact (force=true) overrides preserved-turn limits
/// so compaction always proceeds regardless of session size.
/// </summary>
public sealed class ForceCompactionTests
{
    private static readonly AgentId TestAgent = AgentId.From("test-agent");

    /// <summary>
    /// Proves that with default PreservedTurns=3 and only 2 user turns,
    /// force=false yields Succeeded=false (nothing to summarize).
    /// </summary>
    [Fact]
    public async Task CompactAsync_DefaultForce_FewUserTurns_DoesNotCompact()
    {
        var session = CreateSessionWithUserTurns(2);
        var coordinator = CreateCoordinator(preservedTurns: 3, out var compactor);

        // The real compactor will hit the "toSummarize.Count == 0" path and return Succeeded=false
        compactor
            .Setup(c => c.CompactAsync(session, It.Is<CompactionOptions>(o => o.PreservedTurns == 3), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompactionResult
            {
                Summary = string.Empty,
                Succeeded = false,
                EntriesSummarized = 0,
                EntriesPreserved = 2,
                TokensBefore = 100,
                TokensAfter = 100,
                SnapshotDestructiveVersion = 0,
                SnapshotHistoryCount = 2
            });

        var outcome = await coordinator.CompactAsync(TestAgent, session, CancellationToken.None, force: false);

        outcome.Succeeded.ShouldBeFalse();
        compactor.Verify(c => c.CompactAsync(session, It.Is<CompactionOptions>(o => o.PreservedTurns == 3), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Proves that force=true reduces PreservedTurns to 1, enabling compaction
    /// even when there are fewer user turns than the configured threshold.
    /// </summary>
    [Fact]
    public async Task CompactAsync_ForceTrue_FewUserTurns_OverridesPreservedTurns()
    {
        var session = CreateSessionWithUserTurns(2);
        var coordinator = CreateCoordinator(preservedTurns: 3, out var compactor);

        compactor
            .Setup(c => c.CompactAsync(session, It.Is<CompactionOptions>(o => o.PreservedTurns == 1), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompactionResult
            {
                Summary = "Forced compaction summary",
                Succeeded = true,
                CompactedHistory = [new SessionEntry { Role = MessageRole.System, Content = "summary", IsCompactionSummary = true }],
                EntriesSummarized = 1,
                EntriesPreserved = 1,
                TokensBefore = 100,
                TokensAfter = 50,
                SnapshotDestructiveVersion = 0,
                SnapshotHistoryCount = 2
            });

        var outcome = await coordinator.CompactAsync(TestAgent, session, CancellationToken.None, force: true);

        outcome.Succeeded.ShouldBeTrue();
        outcome.Applied.ShouldBeTrue();
        // Verify the compactor was called with PreservedTurns=1 (force override)
        compactor.Verify(c => c.CompactAsync(session, It.Is<CompactionOptions>(o => o.PreservedTurns == 1), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// When PreservedTurns is already 1, force=true should not change anything
    /// (no need to override an already-minimal value).
    /// </summary>
    [Fact]
    public async Task CompactAsync_ForceTrue_PreservedTurnsAlready1_NoChange()
    {
        var session = CreateSessionWithUserTurns(5);
        var coordinator = CreateCoordinator(preservedTurns: 1, out var compactor);

        compactor
            .Setup(c => c.CompactAsync(session, It.Is<CompactionOptions>(o => o.PreservedTurns == 1), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompactionResult
            {
                Summary = "Summary",
                Succeeded = true,
                CompactedHistory = [new SessionEntry { Role = MessageRole.System, Content = "summary", IsCompactionSummary = true }],
                EntriesSummarized = 4,
                EntriesPreserved = 1,
                TokensBefore = 500,
                TokensAfter = 100,
                SnapshotDestructiveVersion = 0,
                SnapshotHistoryCount = 5
            });

        var outcome = await coordinator.CompactAsync(TestAgent, session, CancellationToken.None, force: true);

        outcome.Succeeded.ShouldBeTrue();
        compactor.Verify(c => c.CompactAsync(session, It.Is<CompactionOptions>(o => o.PreservedTurns == 1), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Auto-compaction (force=false) should pass the configured PreservedTurns unmodified.
    /// </summary>
    [Fact]
    public async Task CompactAsync_ForceFalse_PreservesConfiguredTurns()
    {
        var session = CreateSessionWithUserTurns(10);
        var coordinator = CreateCoordinator(preservedTurns: 5, out var compactor);

        compactor
            .Setup(c => c.CompactAsync(session, It.Is<CompactionOptions>(o => o.PreservedTurns == 5), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompactionResult
            {
                Summary = "Summary",
                Succeeded = true,
                CompactedHistory = [new SessionEntry { Role = MessageRole.System, Content = "summary", IsCompactionSummary = true }],
                EntriesSummarized = 5,
                EntriesPreserved = 5,
                TokensBefore = 1000,
                TokensAfter = 500,
                SnapshotDestructiveVersion = 0,
                SnapshotHistoryCount = 10
            });

        var outcome = await coordinator.CompactAsync(TestAgent, session, CancellationToken.None, force: false);

        outcome.Succeeded.ShouldBeTrue();
        compactor.Verify(c => c.CompactAsync(session, It.Is<CompactionOptions>(o => o.PreservedTurns == 5), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static GatewaySession CreateSessionWithUserTurns(int userTurns)
    {
        var session = new GatewaySession();
        session.HydrateAgentId(TestAgent);
        for (var i = 0; i < userTurns; i++)
        {
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = $"Message {i}" });
            session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = $"Response {i}" });
        }
        return session;
    }

    private static SessionCompactionCoordinator CreateCoordinator(
        int preservedTurns,
        out Mock<ISessionCompactor> compactor)
    {
        compactor = new Mock<ISessionCompactor>();
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.StopAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var channelManager = new Mock<IChannelManager>();
        var options = Options.Create(new CompactionOptions { PreservedTurns = preservedTurns });
        var optionsMonitor = new Mock<IOptionsMonitor<CompactionOptions>>();
        optionsMonitor.Setup(o => o.CurrentValue).Returns(options.Value);

        return new SessionCompactionCoordinator(
            compactor.Object,
            sessions.Object,
            supervisor.Object,
            channelManager.Object,
            optionsMonitor.Object,
            NullLogger<SessionCompactionCoordinator>.Instance);
    }
}
