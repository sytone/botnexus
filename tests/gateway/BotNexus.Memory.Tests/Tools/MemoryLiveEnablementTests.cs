using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Contracts.Memory;
using BotNexus.Memory.Models;
using BotNexus.Memory.Tools;
using Moq;

namespace BotNexus.Memory.Tests.Tools;

/// <summary>
/// Issue #3361: memory tools must consult live enablement at INVOCATION time, not only at
/// construction time, so that disabling memory revokes a retained tool immediately.
/// </summary>
/// <remarks>
/// The load-bearing assertion in every disabled case is <see cref="MockBehavior.Strict"/> plus
/// <c>VerifyNoOtherCalls</c>: the refusal message alone would still pass for a tool that reads the
/// store and then throws the result away, which is exactly the leak the issue is about.
/// </remarks>
public sealed class MemoryLiveEnablementTests
{
    /// <summary>
    /// A live enablement source whose answer can be flipped AFTER the tools are constructed,
    /// standing in for an operator editing agent configuration while a handle is cached.
    /// </summary>
    private sealed class ToggleEnablement : IMemoryEnablementProvider
    {
        public bool Enabled { get; set; } = true;

        public int CallCount { get; private set; }

        public bool IsMemoryEnabled()
        {
            CallCount++;
            return Enabled;
        }
    }

    // ---- AC1 + AC2 + AC3: disabled after construction => refusal, zero store access ----

    [Fact]
    public async Task MemorySaveTool_WhenMemoryDisabledAfterConstruction_RefusesAndTouchesNoStore()
    {
        var agentMemory = new Mock<IAgentMemory>(MockBehavior.Strict);
        var sharedRegistry = new Mock<ISharedMemoryStoreRegistry>(MockBehavior.Strict);
        var toggle = new ToggleEnablement();
        var tool = new MemorySaveTool(agentMemory.Object, "agent-a", sharedRegistry.Object, toggle);

        // Constructed while enabled - the tool is already bound to a live handle.
        toggle.Enabled.ShouldBeTrue();
        toggle.Enabled = false;

        var result = await tool.ExecuteAsync(
            "call-1",
            new Dictionary<string, object?> { ["content"] = "should never be written" });

        GetText(result).ShouldBe(MemoryEnablementGate.RefusalMessage);
        agentMemory.VerifyNoOtherCalls();
        sharedRegistry.VerifyNoOtherCalls();
        toggle.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task MemorySaveTool_WhenMemoryDisabled_DoesNotReachSharedStoreEither()
    {
        // The shared-store branch is a SEPARATE write path inside the same tool. A gate placed
        // after the branch would leave this one open, so it is asserted independently.
        var agentMemory = new Mock<IAgentMemory>(MockBehavior.Strict);
        var sharedRegistry = new Mock<ISharedMemoryStoreRegistry>(MockBehavior.Strict);
        var toggle = new ToggleEnablement { Enabled = false };
        var tool = new MemorySaveTool(agentMemory.Object, "agent-a", sharedRegistry.Object, toggle);

        var result = await tool.ExecuteAsync(
            "call-2",
            new Dictionary<string, object?> { ["content"] = "shared write", ["store"] = "team" });

        GetText(result).ShouldBe(MemoryEnablementGate.RefusalMessage);
        sharedRegistry.VerifyNoOtherCalls();
        agentMemory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MemorySearchTool_WhenMemoryDisabledAfterConstruction_RefusesAndTouchesNoStore()
    {
        var agentMemory = new Mock<IAgentMemory>(MockBehavior.Strict);
        var sharedRegistry = new Mock<ISharedMemoryStoreRegistry>(MockBehavior.Strict);
        var toggle = new ToggleEnablement();
        var tool = new MemorySearchTool(agentMemory.Object, "agent-a", null, sharedRegistry.Object, toggle);

        toggle.Enabled = false;

        var result = await tool.ExecuteAsync(
            "call-3",
            new Dictionary<string, object?> { ["query"] = "anything" });

        GetText(result).ShouldBe(MemoryEnablementGate.RefusalMessage);
        agentMemory.VerifyNoOtherCalls();
        sharedRegistry.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MemorySearchTool_WhenMemoryDisabled_DoesNotReachStoreScopedSearchEither()
    {
        var agentMemory = new Mock<IAgentMemory>(MockBehavior.Strict);
        var sharedRegistry = new Mock<ISharedMemoryStoreRegistry>(MockBehavior.Strict);
        var toggle = new ToggleEnablement { Enabled = false };
        var tool = new MemorySearchTool(agentMemory.Object, "agent-a", null, sharedRegistry.Object, toggle);

        var result = await tool.ExecuteAsync(
            "call-4",
            new Dictionary<string, object?> { ["query"] = "anything", ["store"] = "team" });

        GetText(result).ShouldBe(MemoryEnablementGate.RefusalMessage);
        sharedRegistry.VerifyNoOtherCalls();
        agentMemory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MemoryGetTool_WhenMemoryDisabledAfterConstruction_RefusesAndTouchesNoStore()
    {
        var store = new Mock<IMemoryStore>(MockBehavior.Strict);
        var toggle = new ToggleEnablement();
        var tool = new MemoryGetTool(store.Object, MemoryGetTool.DefaultMaxLimit, toggle);

        toggle.Enabled = false;

        var byId = await tool.ExecuteAsync("call-5", new Dictionary<string, object?> { ["id"] = "entry-1" });
        var bySession = await tool.ExecuteAsync("call-6", new Dictionary<string, object?> { ["sessionId"] = "session-1" });

        GetText(byId).ShouldBe(MemoryEnablementGate.RefusalMessage);
        GetText(bySession).ShouldBe(MemoryEnablementGate.RefusalMessage);
        store.VerifyNoOtherCalls();
    }

    /// <summary>
    /// The refusal has to be actionable, per the issue's expected behaviour: naming the condition,
    /// stating that nothing happened, and pointing at the remedy. A bare "denied" would be
    /// indistinguishable from an empty store.
    /// </summary>
    [Fact]
    public void RefusalMessage_IsActionable()
    {
        MemoryEnablementGate.RefusalMessage.ShouldContain("Memory is disabled");
        MemoryEnablementGate.RefusalMessage.ShouldContain("No memory was read or written");
        MemoryEnablementGate.RefusalMessage.ShouldContain("configuration");
    }

    // ---- AC4: the enabled path is unchanged ----

    [Fact]
    public async Task MemorySaveTool_WhenMemoryStillEnabled_SavesExactlyAsBefore()
    {
        var agentMemory = new Mock<IAgentMemory>(MockBehavior.Strict);
        agentMemory
            .Setup(memory => memory.SaveAsync(It.IsAny<AgentMemorySaveRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var toggle = new ToggleEnablement { Enabled = true };
        var tool = new MemorySaveTool(agentMemory.Object, "agent-a", null, toggle);

        var result = await tool.ExecuteAsync(
            "call-7",
            new Dictionary<string, object?> { ["content"] = "real note" });

        GetText(result).ShouldBe("Appended memory note to default memory target.");
        agentMemory.Verify(
            memory => memory.SaveAsync(
                It.Is<AgentMemorySaveRequest>(request => request.Content == "real note" && request.AgentId == "agent-a"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task MemorySearchTool_WhenMemoryStillEnabled_SearchesExactlyAsBefore()
    {
        var agentMemory = new Mock<IAgentMemory>(MockBehavior.Strict);
        agentMemory
            .Setup(memory => memory.SearchAsync(It.IsAny<AgentMemorySearchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new AgentMemorySearchResult(
                    Id: "entry-1",
                    Content: "hit",
                    SourceType: "tool",
                    SessionId: null,
                    CreatedAt: DateTimeOffset.UtcNow,
                    RelevanceScore: 0.9)
            ]);
        var toggle = new ToggleEnablement { Enabled = true };
        var tool = new MemorySearchTool(agentMemory.Object, "agent-a", null, null, toggle);

        var result = await tool.ExecuteAsync(
            "call-8",
            new Dictionary<string, object?> { ["query"] = "hit" });

        GetText(result).ShouldContain("ID: entry-1");
        agentMemory.Verify(
            memory => memory.SearchAsync(It.IsAny<AgentMemorySearchRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Null-is-passthrough: every existing construction site omits the provider, so omitting it must
    /// mean "enabled" and not "fail closed". Registration-time gating is what protects a
    /// never-enabled agent; this only governs the retained-tool case.
    /// </summary>
    [Fact]
    public async Task MemoryGetTool_WithNoEnablementProvider_BehavesAsEnabled()
    {
        var store = new Mock<IMemoryStore>(MockBehavior.Strict);
        store
            .Setup(memoryStore => memoryStore.GetByIdAsync("entry-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryEntry
            {
                Id = "entry-1",
                Content = "stored",
                SourceType = "tool",
                AgentId = "agent-a",
                CreatedAt = DateTimeOffset.UtcNow
            });
        var tool = new MemoryGetTool(store.Object);

        var result = await tool.ExecuteAsync("call-9", new Dictionary<string, object?> { ["id"] = "entry-1" });

        GetText(result).ShouldContain("ID: entry-1");
        store.Verify(memoryStore => memoryStore.GetByIdAsync("entry-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void MemoryEnablementGate_NullProvider_IsPassthrough()
        => MemoryEnablementGate.Refuse(null).ShouldBeNull();

    [Fact]
    public void MemoryEnablementGate_EnabledProvider_IsPassthrough()
        => MemoryEnablementGate.Refuse(new ToggleEnablement { Enabled = true }).ShouldBeNull();

    /// <summary>
    /// The check is re-evaluated per call, not memoised. A tool that cached the first answer would
    /// pass every single-invocation test above and still fail to revoke in production.
    /// </summary>
    [Fact]
    public async Task MemoryGetTool_ConsultsEnablementOnEveryInvocation()
    {
        var store = new Mock<IMemoryStore>(MockBehavior.Strict);
        store
            .Setup(memoryStore => memoryStore.GetByIdAsync("entry-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((MemoryEntry?)null);
        var toggle = new ToggleEnablement { Enabled = true };
        var tool = new MemoryGetTool(store.Object, MemoryGetTool.DefaultMaxLimit, toggle);

        var allowed = await tool.ExecuteAsync("call-a", new Dictionary<string, object?> { ["id"] = "entry-1" });
        GetText(allowed).ShouldBe("Memory entry not found.");

        toggle.Enabled = false;
        var refused = await tool.ExecuteAsync("call-b", new Dictionary<string, object?> { ["id"] = "entry-1" });

        GetText(refused).ShouldBe(MemoryEnablementGate.RefusalMessage);
        toggle.CallCount.ShouldBe(2);
        store.Verify(memoryStore => memoryStore.GetByIdAsync("entry-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static string GetText(AgentToolResult result)
        => result.Content.Single(content => content.Type == AgentToolContentType.Text).Value;
}
