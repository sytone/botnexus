using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

public sealed class PreCompactionMemoryFlusherTests
{
    private static readonly AgentId TestAgent = AgentId.From("agent-a");
    private static readonly SessionId TestSessionId = SessionId.From("session-1");

    // ─── ShouldFlush ───────────────────────────────────────────────────────────

    [Fact]
    public void ShouldFlush_WhenEnabled_AndInteractiveSession_AndNotFlushedThisCycle_ReturnsTrue()
    {
        var session = BuildInteractiveSession();
        var flusher = CreateFlusher();
        var options = new CompactionOptions { MemoryFlush = new MemoryFlushOptions { Enabled = true } };

        flusher.ShouldFlush(session, options).ShouldBeTrue();
    }

    [Fact]
    public void ShouldFlush_WhenDisabled_ReturnsFalse()
    {
        var session = BuildInteractiveSession();
        var flusher = CreateFlusher();
        var options = new CompactionOptions { MemoryFlush = new MemoryFlushOptions { Enabled = false } };

        flusher.ShouldFlush(session, options).ShouldBeFalse();
    }

    [Fact]
    public void ShouldFlush_WhenNonInteractiveSession_ReturnsFalse()
    {
        var session = BuildSession(SessionType.Heartbeat);
        var flusher = CreateFlusher();
        var options = new CompactionOptions { MemoryFlush = new MemoryFlushOptions { Enabled = true } };

        flusher.ShouldFlush(session, options).ShouldBeFalse();
    }

    [Fact]
    public void ShouldFlush_WhenAlreadyFlushedThisCycle_ReturnsFalse()
    {
        var session = BuildInteractiveSession();
        // Record that flush ran for cycle 1 (current cycle = 0 summaries, upcoming = 1)
        session.Metadata[MemoryFlushOptions.MetadataKey] = 1;
        var flusher = CreateFlusher();
        var options = new CompactionOptions { MemoryFlush = new MemoryFlushOptions { Enabled = true } };

        flusher.ShouldFlush(session, options).ShouldBeFalse();
    }

    [Fact]
    public void ShouldFlush_AfterPreviousCompaction_ForNextCycle_ReturnsTrue()
    {
        var session = BuildInteractiveSession();
        // One compaction summary already exists (we are on cycle 1)
        session.History.Add(new SessionEntry
        {
            Role = MessageRole.System,
            Content = "summary",
            IsCompactionSummary = true
        });
        // Flush ran for cycle 1 (the one that added the first summary)
        session.Metadata[MemoryFlushOptions.MetadataKey] = 1;
        var flusher = CreateFlusher();
        var options = new CompactionOptions { MemoryFlush = new MemoryFlushOptions { Enabled = true } };

        // Now we're approaching cycle 2 — flush has not run for it yet
        flusher.ShouldFlush(session, options).ShouldBeTrue();
    }

    // ─── FlushAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task FlushAsync_WhenLiveHandleAvailable_CallsSteerAsync()
    {
        var session = BuildInteractiveSession();
        var handleMock = new Mock<IAgentHandle>();
        handleMock.Setup(h => h.SteerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var supervisorMock = new Mock<IAgentSupervisor>();
        supervisorMock.Setup(s => s.GetHandle(TestAgent, TestSessionId))
            .Returns(handleMock.Object);

        var flusher = new PreCompactionMemoryFlusher(supervisorMock.Object, NullLogger<PreCompactionMemoryFlusher>.Instance);
        var options = new CompactionOptions { MemoryFlush = new MemoryFlushOptions { Enabled = true } };

        await flusher.FlushAsync(TestAgent, session, options);

        handleMock.Verify(h => h.SteerAsync(
            It.Is<string>(s => s.Length > 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FlushAsync_WhenNoLiveHandle_DoesNotThrow()
    {
        var session = BuildInteractiveSession();
        var supervisorMock = new Mock<IAgentSupervisor>();
        supervisorMock.Setup(s => s.GetHandle(It.IsAny<AgentId>(), It.IsAny<SessionId>()))
            .Returns((IAgentHandle?)null);

        var flusher = new PreCompactionMemoryFlusher(supervisorMock.Object, NullLogger<PreCompactionMemoryFlusher>.Instance);
        var options = new CompactionOptions { MemoryFlush = new MemoryFlushOptions { Enabled = true } };

        // No live handle — should skip silently without throwing
        var act = async () => await flusher.FlushAsync(TestAgent, session, options);
        await act.ShouldNotThrowAsync();
    }

    [Fact]
    public async Task FlushAsync_WhenSteerThrows_DoesNotRethrow()
    {
        var session = BuildInteractiveSession();
        var handleMock = new Mock<IAgentHandle>();
        handleMock.Setup(h => h.SteerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("agent failed"));

        var supervisorMock = new Mock<IAgentSupervisor>();
        supervisorMock.Setup(s => s.GetHandle(TestAgent, TestSessionId))
            .Returns(handleMock.Object);

        var flusher = new PreCompactionMemoryFlusher(supervisorMock.Object, NullLogger<PreCompactionMemoryFlusher>.Instance);
        var options = new CompactionOptions { MemoryFlush = new MemoryFlushOptions { Enabled = true } };

        // Non-fatal: should not throw
        var act = async () => await flusher.FlushAsync(TestAgent, session, options);
        await act.ShouldNotThrowAsync();
    }

    [Fact]
    public async Task FlushAsync_WhenSucceeds_RecordsFlushCycleInMetadata()
    {
        var session = BuildInteractiveSession();
        var handleMock = new Mock<IAgentHandle>();
        handleMock.Setup(h => h.SteerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var supervisorMock = new Mock<IAgentSupervisor>();
        supervisorMock.Setup(s => s.GetHandle(TestAgent, TestSessionId))
            .Returns(handleMock.Object);

        var flusher = new PreCompactionMemoryFlusher(supervisorMock.Object, NullLogger<PreCompactionMemoryFlusher>.Instance);
        var options = new CompactionOptions { MemoryFlush = new MemoryFlushOptions { Enabled = true } };

        await flusher.FlushAsync(TestAgent, session, options);

        session.Metadata.ContainsKey(MemoryFlushOptions.MetadataKey).ShouldBeTrue();
        Convert.ToInt32(session.Metadata[MemoryFlushOptions.MetadataKey]).ShouldBe(1);
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private static PreCompactionMemoryFlusher CreateFlusher()
    {
        var supervisorMock = new Mock<IAgentSupervisor>();
        supervisorMock.Setup(s => s.GetHandle(It.IsAny<AgentId>(), It.IsAny<SessionId>()))
            .Returns((IAgentHandle?)null);
        return new PreCompactionMemoryFlusher(supervisorMock.Object, NullLogger<PreCompactionMemoryFlusher>.Instance);
    }

    private static Session BuildInteractiveSession()
        => BuildSession(SessionType.UserAgent);

    private static Session BuildSession(SessionType type) => new()
    {
        SessionId = TestSessionId,
        AgentId = TestAgent,
        SessionType = type
    };
}
