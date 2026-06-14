using BotNexus.Cli.Commands;

namespace BotNexus.Cli.Tests;

public sealed class DebugMemoryCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _agentsDir;

    public DebugMemoryCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"botnexus-test-{Guid.NewGuid():N}");
        _agentsDir = Path.Combine(_tempDir, "agents");
        Directory.CreateDirectory(_agentsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void CollectAgentMemoryInfo_NoAgents_ReturnsEmpty()
    {
        var result = DebugMemoryCommand.CollectAgentMemoryInfo(_agentsDir, null);
        result.ShouldBeEmpty();
    }

    [Fact]
    public void CollectAgentMemoryInfo_AgentWithNoMemory_ReturnsEmpty()
    {
        var agentDir = Path.Combine(_agentsDir, "test-agent", "workspace");
        Directory.CreateDirectory(agentDir);

        var result = DebugMemoryCommand.CollectAgentMemoryInfo(_agentsDir, null);
        result.ShouldBeEmpty();
    }

    [Fact]
    public void CollectAgentMemoryInfo_AgentWithMemoryMd_ReturnsInfo()
    {
        var workspaceDir = Path.Combine(_agentsDir, "test-agent", "workspace");
        Directory.CreateDirectory(workspaceDir);
        File.WriteAllText(Path.Combine(workspaceDir, "MEMORY.md"), "# Memory\nSome content here.");

        var result = DebugMemoryCommand.CollectAgentMemoryInfo(_agentsDir, null);

        result.ShouldHaveSingleItem();
        result[0].AgentId.ShouldBe("test-agent");
        result[0].HasMemoryMd.ShouldBeTrue();
        result[0].MemoryMdSizeBytes.ShouldBeGreaterThan(0);
        result[0].DailyNoteCount.ShouldBe(0);
    }

    [Fact]
    public void CollectAgentMemoryInfo_AgentWithDailyNotes_ReturnsInfo()
    {
        var memoryDir = Path.Combine(_agentsDir, "test-agent", "workspace", "memory");
        Directory.CreateDirectory(memoryDir);
        File.WriteAllText(Path.Combine(memoryDir, "2026-06-10.md"), "Note 1");
        File.WriteAllText(Path.Combine(memoryDir, "2026-06-11.md"), "Note 2 with more content");

        var result = DebugMemoryCommand.CollectAgentMemoryInfo(_agentsDir, null);

        result.ShouldHaveSingleItem();
        result[0].AgentId.ShouldBe("test-agent");
        result[0].DailyNoteCount.ShouldBe(2);
        result[0].LastDailyNote.ShouldBe("2026-06-11");
        result[0].TotalMemoryDirSizeBytes.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void CollectAgentMemoryInfo_MultipleAgents_ReturnsSortedByName()
    {
        CreateAgentWithMemory("zeta-agent");
        CreateAgentWithMemory("alpha-agent");

        var result = DebugMemoryCommand.CollectAgentMemoryInfo(_agentsDir, null);

        result.Count.ShouldBe(2);
        result[0].AgentId.ShouldBe("alpha-agent");
        result[1].AgentId.ShouldBe("zeta-agent");
    }

    [Fact]
    public void CollectAgentMemoryInfo_FilterByAgent_ReturnsOnlyMatching()
    {
        CreateAgentWithMemory("agent-a");
        CreateAgentWithMemory("agent-b");

        var result = DebugMemoryCommand.CollectAgentMemoryInfo(_agentsDir, "agent-b");

        result.ShouldHaveSingleItem();
        result[0].AgentId.ShouldBe("agent-b");
    }

    [Fact]
    public void Execute_MissingAgentsDir_Returns1()
    {
        var result = DebugMemoryCommand.Execute(
            Path.Combine(_tempDir, "nonexistent"), null, "table");
        result.ShouldBe(1);
    }

    [Fact]
    public void Execute_WithAgents_Returns0()
    {
        CreateAgentWithMemory("test-agent");
        var result = DebugMemoryCommand.Execute(_agentsDir, null, "table");
        result.ShouldBe(0);
    }

    [Fact]
    public void Execute_JsonFormat_Returns0()
    {
        CreateAgentWithMemory("test-agent");
        var result = DebugMemoryCommand.Execute(_agentsDir, null, "json");
        result.ShouldBe(0);
    }

    [Fact]
    public void Execute_DetailView_Returns0()
    {
        CreateAgentWithMemory("test-agent");
        var result = DebugMemoryCommand.Execute(_agentsDir, "test-agent", "table");
        result.ShouldBe(0);
    }

    [Fact]
    public void CollectAgentMemoryInfo_TotalSize_IncludesBothMemoryMdAndDailyNotes()
    {
        var workspaceDir = Path.Combine(_agentsDir, "test-agent", "workspace");
        var memoryDir = Path.Combine(workspaceDir, "memory");
        Directory.CreateDirectory(memoryDir);
        File.WriteAllText(Path.Combine(workspaceDir, "MEMORY.md"), "consolidated");
        File.WriteAllText(Path.Combine(memoryDir, "2026-06-11.md"), "daily note");

        var result = DebugMemoryCommand.CollectAgentMemoryInfo(_agentsDir, null);

        result[0].TotalSizeBytes.ShouldBe(result[0].MemoryMdSizeBytes + result[0].TotalMemoryDirSizeBytes);
    }

    private void CreateAgentWithMemory(string agentId)
    {
        var memoryDir = Path.Combine(_agentsDir, agentId, "workspace", "memory");
        Directory.CreateDirectory(memoryDir);
        File.WriteAllText(Path.Combine(memoryDir, "2026-06-11.md"), $"Notes for {agentId}");
    }
}
