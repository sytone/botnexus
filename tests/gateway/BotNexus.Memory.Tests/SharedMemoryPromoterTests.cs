using BotNexus.Memory;
using BotNexus.Memory.Learning;
using BotNexus.Memory.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Memory.Tests;

public sealed class SharedMemoryPromoterTests
{
    private readonly Mock<ISharedMemoryStoreRegistry> _registry = new();
    private readonly Mock<IMemoryStore> _store = new();
    private readonly SharedMemoryPromoter _promoter;

    public SharedMemoryPromoterTests()
    {
        _promoter = new SharedMemoryPromoter(_registry.Object, NullLogger.Instance);
    }

    [Fact]
    public async Task PromoteAsync_NoTargetStore_SkipsAll()
    {
        var items = new List<ExtractedKnowledge>
        {
            new() { Content = "test", Category = KnowledgeCategory.Fact, Confidence = 0.9, SourceSessionId = "s1", SourceTurnIndex = 1, TargetStore = null }
        };

        var result = await _promoter.PromoteAsync("agent-1", items);

        result.ShouldBe(0);
        _store.Verify(s => s.InsertAsync(It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PromoteAsync_NoWriteAccess_SkipsItem()
    {
        _registry.Setup(r => r.CanWrite("agent-1", "shared-store")).Returns(false);

        var items = new List<ExtractedKnowledge>
        {
            new() { Content = "test", Category = KnowledgeCategory.Decision, Confidence = 0.9, SourceSessionId = "s1", SourceTurnIndex = 1, TargetStore = "shared-store" }
        };

        var result = await _promoter.PromoteAsync("agent-1", items);

        result.ShouldBe(0);
    }

    [Fact]
    public async Task PromoteAsync_StoreNotFound_SkipsItem()
    {
        _registry.Setup(r => r.CanWrite("agent-1", "shared-store")).Returns(true);
        _registry.Setup(r => r.GetStore("shared-store")).Returns((IMemoryStore?)null);

        var items = new List<ExtractedKnowledge>
        {
            new() { Content = "test", Category = KnowledgeCategory.Pattern, Confidence = 0.8, SourceSessionId = "s1", SourceTurnIndex = 1, TargetStore = "shared-store" }
        };

        var result = await _promoter.PromoteAsync("agent-1", items);

        result.ShouldBe(0);
    }

    [Fact]
    public async Task PromoteAsync_DuplicateContent_SkipsItem()
    {
        _registry.Setup(r => r.CanWrite("agent-1", "shared-store")).Returns(true);
        _registry.Setup(r => r.GetStore("shared-store")).Returns(_store.Object);

        var existingEntry = new MemoryEntry
        {
            Id = "existing-1",
            AgentId = "other-agent",
            SourceType = "dreaming",
            Content = "This is some important architectural decision about the system design",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        _store.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<MemorySearchFilter?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry> { existingEntry });

        var items = new List<ExtractedKnowledge>
        {
            new() { Content = "This is some important architectural decision about the system design", Category = KnowledgeCategory.Decision, Confidence = 0.9, SourceSessionId = "s1", SourceTurnIndex = 1, TargetStore = "shared-store" }
        };

        var result = await _promoter.PromoteAsync("agent-1", items);

        result.ShouldBe(0);
        _store.Verify(s => s.InsertAsync(It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PromoteAsync_NewContent_InsertsEntry()
    {
        _registry.Setup(r => r.CanWrite("agent-1", "shared-store")).Returns(true);
        _registry.Setup(r => r.GetStore("shared-store")).Returns(_store.Object);
        _store.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<MemorySearchFilter?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry>());

        var items = new List<ExtractedKnowledge>
        {
            new() { Content = "New insight about deployment patterns", Category = KnowledgeCategory.Pattern, Confidence = 0.85, SourceSessionId = "s1", SourceTurnIndex = 3, TargetStore = "shared-store" }
        };

        var result = await _promoter.PromoteAsync("agent-1", items);

        result.ShouldBe(1);
        _store.Verify(s => s.InsertAsync(
            It.Is<MemoryEntry>(e =>
                e.AgentId == "agent-1" &&
                e.SourceType == "dreaming" &&
                e.Content == "New insight about deployment patterns"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PromoteAsync_MultipleItems_PromotesOnlyEligible()
    {
        _registry.Setup(r => r.CanWrite("agent-1", "shared-store")).Returns(true);
        _registry.Setup(r => r.CanWrite("agent-1", "no-access-store")).Returns(false);
        _registry.Setup(r => r.GetStore("shared-store")).Returns(_store.Object);
        _store.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<MemorySearchFilter?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry>());

        var items = new List<ExtractedKnowledge>
        {
            new() { Content = "Item 1", Category = KnowledgeCategory.Fact, Confidence = 0.9, SourceSessionId = "s1", SourceTurnIndex = 1, TargetStore = "shared-store" },
            new() { Content = "Item 2", Category = KnowledgeCategory.Fact, Confidence = 0.9, SourceSessionId = "s1", SourceTurnIndex = 2, TargetStore = "no-access-store" },
            new() { Content = "Item 3", Category = KnowledgeCategory.Fact, Confidence = 0.9, SourceSessionId = "s1", SourceTurnIndex = 3, TargetStore = null },
        };

        var result = await _promoter.PromoteAsync("agent-1", items);

        result.ShouldBe(1);
    }

    [Theory]
    [InlineData("identical content here", "identical content here", true)]
    [InlineData("completely different text about dogs", "unrelated words about the weather today", false)]
    [InlineData("the system uses sqlite for persistence", "the system uses sqlite for data persistence layer", true)]
    public void ComputeJaccardSimilarity_CorrectlyIdentifiesDuplicates(string a, string b, bool shouldBeAboveThreshold)
    {
        var similarity = SharedMemoryPromoter.ComputeJaccardSimilarity(a, b);

        if (shouldBeAboveThreshold)
            similarity.ShouldBeGreaterThanOrEqualTo(0.7);
        else
            similarity.ShouldBeLessThan(0.7);
    }
}
