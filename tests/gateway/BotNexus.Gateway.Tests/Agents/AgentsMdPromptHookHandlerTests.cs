using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Hooks;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class AgentsMdPromptHookHandlerTests
{
    // ── Helpers ────────────────────────────────────────────────────────────

    private static BeforePromptBuildEvent MakeEvent(string agentId = "test-agent")
        => new(AgentId.From(agentId), new AgentDescriptor
        {
            AgentId = AgentId.From(agentId),
            DisplayName = agentId,
            ModelId = "test-model",
            ApiProvider = "test"
        }, "system prompt", []);

    private static StubWorkspaceManager MakeManager(string workspacePath)
        => new(workspacePath);

    private sealed class StubWorkspaceManager : IAgentWorkspaceManager
    {
        private readonly string _path;
        public StubWorkspaceManager(string path) => _path = path;
        public string GetWorkspacePath(string agentId) => _path;
        public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken ct = default)
            => Task.FromResult(new AgentWorkspace(agentName, string.Empty, string.Empty, string.Empty, string.Empty));
        public Task SaveMemoryAsync(string agentName, string content, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveMemoryAsync(string agentName, string? filePath, string content, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveMemoryAsync(string agentName, string? filePath, string content, string? memoryPathOverride, CancellationToken ct = default) => Task.CompletedTask;
    }

    // ── CollectAgentsMdFiles tests ─────────────────────────────────────────

    [Fact]
    public void CollectAgentsMdFiles_NoGitRepo_ReturnsEmpty()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory("/workspace");
        // No .git anywhere
        var handler = new AgentsMdPromptHookHandler(MakeManager("/workspace"), fs);

        var result = handler.CollectAgentsMdFiles("/workspace");

        result.ShouldBeEmpty();
    }

    [Fact]
    public void CollectAgentsMdFiles_GitRepoWithNoAgentsMd_ReturnsEmpty()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory("/repo/.git");
        fs.AddDirectory("/repo/workspace");
        // .git exists but no AGENTS.md anywhere
        var handler = new AgentsMdPromptHookHandler(MakeManager("/repo/workspace"), fs);

        var result = handler.CollectAgentsMdFiles("/repo/workspace");

        result.ShouldBeEmpty();
    }

    [Fact]
    public void CollectAgentsMdFiles_RootAgentsMdOnly_ReturnsRootFile()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory("/repo/.git");
        fs.AddFile("/repo/AGENTS.md", new MockFileData("# Repo AGENTS"));
        fs.AddDirectory("/repo/workspace");

        var handler = new AgentsMdPromptHookHandler(MakeManager("/repo/workspace"), fs);
        var result = handler.CollectAgentsMdFiles("/repo/workspace");

        result.ShouldHaveSingleItem();
        result[0].Content.ShouldBe("# Repo AGENTS");
    }

    [Fact]
    public void CollectAgentsMdFiles_MultipleAgentsMd_ReturnedRootFirst()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory("/repo/.git");
        fs.AddFile("/repo/AGENTS.md", new MockFileData("root instructions"));
        fs.AddDirectory("/repo/src");
        fs.AddFile("/repo/src/AGENTS.md", new MockFileData("src instructions"));
        fs.AddDirectory("/repo/src/gateway");

        var handler = new AgentsMdPromptHookHandler(MakeManager("/repo/src/gateway"), fs);
        var result = handler.CollectAgentsMdFiles("/repo/src/gateway");

        result.Count.ShouldBe(2);
        result[0].Content.ShouldBe("root instructions");
        result[1].Content.ShouldBe("src instructions");
    }

    [Fact]
    public void CollectAgentsMdFiles_WorkspaceNotInRepo_ReturnsEmpty()
    {
        var fs = new MockFileSystem();
        // Workspace is at /standalone, no .git anywhere in the tree
        fs.AddDirectory("/standalone");

        var handler = new AgentsMdPromptHookHandler(MakeManager("/standalone"), fs);
        var result = handler.CollectAgentsMdFiles("/standalone");

        result.ShouldBeEmpty();
    }

    [Fact]
    public void CollectAgentsMdFiles_EmptyAgentsMdFileSkipped()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory("/repo/.git");
        fs.AddFile("/repo/AGENTS.md", new MockFileData("   ")); // whitespace only
        fs.AddDirectory("/repo/workspace");

        var handler = new AgentsMdPromptHookHandler(MakeManager("/repo/workspace"), fs);
        var result = handler.CollectAgentsMdFiles("/repo/workspace");

        result.ShouldBeEmpty();
    }

    // ── HandleAsync tests ──────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_NoRepo_ReturnsNull()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory("/workspace");

        var handler = new AgentsMdPromptHookHandler(MakeManager("/workspace"), fs);
        var result = await handler.HandleAsync(MakeEvent(), CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task HandleAsync_WithAgentsMd_AppendsToSystemContext()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory("/repo/.git");
        fs.AddFile("/repo/AGENTS.md", new MockFileData("# Global Rules"));
        fs.AddDirectory("/repo/workspace");

        var handler = new AgentsMdPromptHookHandler(MakeManager("/repo/workspace"), fs);
        var result = await handler.HandleAsync(MakeEvent(), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.AppendSystemContext.ShouldNotBeNullOrWhiteSpace();
        result.AppendSystemContext!.ShouldContain("# Global Rules");
    }
}
