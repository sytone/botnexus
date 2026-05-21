using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Abstractions.Triggers;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

public sealed class PreCompactionMemoryFlusherTests
{
    private static readonly AgentId TestAgent = AgentId.From("agent-a");

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
    public async Task FlushAsync_WhenTriggerAvailable_CallsCreateSessionAsync()
    {
        var session = BuildInteractiveSession();
        var triggerMock = new Mock<IInternalTrigger>();
        triggerMock.SetupGet(t => t.Type).Returns(TriggerType.Memory);
        triggerMock.Setup(t => t.CreateSessionAsync(
                It.IsAny<AgentId>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<InternalTriggerRequest?>()))
            .ReturnsAsync(SessionId.From("flush-session"));

        var flusher = CreateFlusher(triggerMock.Object);
        var options = new CompactionOptions { MemoryFlush = new MemoryFlushOptions { Enabled = true } };

        await flusher.FlushAsync(TestAgent, session, options);

        triggerMock.Verify(t => t.CreateSessionAsync(
            TestAgent,
            It.Is<string>(s => s.Contains("compaction")),
            It.IsAny<CancellationToken>(),
            It.IsAny<InternalTriggerRequest?>()), Times.Once);
    }

    [Fact]
    public async Task FlushAsync_WhenTriggerThrows_DoesNotRethrow()
    {
        var session = BuildInteractiveSession();
        var triggerMock = new Mock<IInternalTrigger>();
        triggerMock.SetupGet(t => t.Type).Returns(TriggerType.Memory);
        triggerMock.Setup(t => t.CreateSessionAsync(
                It.IsAny<AgentId>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()))
            .ThrowsAsync(new InvalidOperationException("agent not found"));

        var flusher = CreateFlusher(triggerMock.Object);
        var options = new CompactionOptions { MemoryFlush = new MemoryFlushOptions { Enabled = true } };

        // Non-fatal: should not throw
        var act = async () => await flusher.FlushAsync(TestAgent, session, options);
        await act.ShouldNotThrowAsync();
    }

    [Fact]
    public async Task FlushAsync_WhenNoTriggerAvailable_DoesNotThrow()
    {
        var session = BuildInteractiveSession();
        var flusher = CreateFlusher(); // no triggers
        var options = new CompactionOptions { MemoryFlush = new MemoryFlushOptions { Enabled = true } };

        var act = async () => await flusher.FlushAsync(TestAgent, session, options);
        await act.ShouldNotThrowAsync();
    }

    [Fact]
    public async Task FlushAsync_WhenSucceeds_RecordsFlushCycleInMetadata()
    {
        var session = BuildInteractiveSession();
        var triggerMock = new Mock<IInternalTrigger>();
        triggerMock.SetupGet(t => t.Type).Returns(TriggerType.Memory);
        triggerMock.Setup(t => t.CreateSessionAsync(
                It.IsAny<AgentId>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<InternalTriggerRequest?>()))
            .ReturnsAsync(SessionId.From("flush-session"));

        var flusher = CreateFlusher(triggerMock.Object);
        var options = new CompactionOptions { MemoryFlush = new MemoryFlushOptions { Enabled = true } };

        await flusher.FlushAsync(TestAgent, session, options);

        session.Metadata.ContainsKey(MemoryFlushOptions.MetadataKey).ShouldBeTrue();
        Convert.ToInt32(session.Metadata[MemoryFlushOptions.MetadataKey]).ShouldBe(1);
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private static PreCompactionMemoryFlusher CreateFlusher(params IInternalTrigger[] triggers)
        => new(triggers, NullLogger<PreCompactionMemoryFlusher>.Instance);

    private static Session BuildInteractiveSession()
        => BuildSession(SessionType.UserAgent);

    private static Session BuildSession(SessionType type) => new()
    {
        SessionId = SessionId.From(Guid.NewGuid().ToString("N")),
        AgentId = TestAgent,
        SessionType = type
    };
}
