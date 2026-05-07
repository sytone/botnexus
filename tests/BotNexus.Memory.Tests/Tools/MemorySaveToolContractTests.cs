using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Memory.Tools;

namespace BotNexus.Memory.Tests.Tools;

public sealed class MemorySaveToolContractTests
{
    [Fact]
    public void Contract_ExposesMemorySaveNameOnly()
    {
        var tool = new MemorySaveTool(new StubWorkspaceManager(), "agent-a");

        tool.Name.ShouldBe("memory_save");
        tool.Label.ShouldBe("Memory Save");
        tool.Definition.Description.ShouldContain("Append markdown memory notes");
    }

    [Fact]
    public async Task PrepareArguments_WithFilePath_ProducesSaveArguments()
    {
        var tool = new MemorySaveTool(new StubWorkspaceManager(), "agent-a");

        var prepared = await tool.PrepareArgumentsAsync(
            new Dictionary<string, object?>
            {
                ["content"] = "remember this",
                ["file_path"] = @"memory\2026-01-01.md"
            });

        prepared.Count.ShouldBe(2);
        prepared["content"].ShouldBe("remember this");
        prepared["file_path"].ShouldBe(@"memory\2026-01-01.md");
    }

    private sealed class StubWorkspaceManager : IAgentWorkspaceManager
    {
        public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken ct = default)
            => Task.FromResult(new AgentWorkspace(agentName, Soul: string.Empty, Identity: string.Empty, User: string.Empty, Memory: string.Empty));

        public Task SaveMemoryAsync(string agentName, string content, CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveMemoryAsync(string agentName, string? filePath, string content, CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveMemoryAsync(
            string agentName,
            string? filePath,
            string content,
            string? memoryPathOverride,
            CancellationToken ct = default) => Task.CompletedTask;

        public string GetWorkspacePath(string agentName) => agentName;
    }
}
