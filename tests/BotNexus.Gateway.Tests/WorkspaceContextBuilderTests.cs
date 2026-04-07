using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using FluentAssertions;

namespace BotNexus.Gateway.Tests;

public sealed class WorkspaceContextBuilderTests
{
    [Fact]
    public async Task BuildSystemPromptAsync_WithExplicitPromptFiles_LoadsInOrderAndDeletesBootstrap()
    {
        var workspacePath = CreateWorkspace(
            ("AGENTS.md", "AGENTS"),
            ("SOUL.md", "SOUL"),
            ("TOOLS.md", "TOOLS"),
            ("BOOTSTRAP.md", "BOOTSTRAP"));
        try
        {
            var manager = new StubWorkspaceManager(workspacePath);
            var builder = new WorkspaceContextBuilder(manager);

            var result = await builder.BuildSystemPromptAsync(new AgentDescriptor
            {
                AgentId = "farnsworth",
                DisplayName = "Farnsworth",
                ModelId = "test-model",
                ApiProvider = "test-provider",
                SystemPromptFiles = ["AGENTS.md", "BOOTSTRAP.md", "TOOLS.md"]
            });

            result.Should().Be("AGENTS\n\nBOOTSTRAP\n\nTOOLS");
            File.Exists(Path.Combine(workspacePath, "BOOTSTRAP.md")).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(workspacePath)!, recursive: true);
        }
    }

    [Fact]
    public async Task BuildSystemPromptAsync_WhenPromptFilesEmpty_UsesDefaultOrderAndPrependsInlinePrompt()
    {
        var workspacePath = CreateWorkspace(
            ("AGENTS.md", "AGENTS"),
            ("SOUL.md", "SOUL"),
            ("TOOLS.md", "TOOLS"),
            ("BOOTSTRAP.md", "BOOTSTRAP"),
            ("IDENTITY.md", "IDENTITY"),
            ("USER.md", "USER"));
        try
        {
            var manager = new StubWorkspaceManager(workspacePath);
            var builder = new WorkspaceContextBuilder(manager);

            var result = await builder.BuildSystemPromptAsync(new AgentDescriptor
            {
                AgentId = "farnsworth",
                DisplayName = "Farnsworth",
                ModelId = "test-model",
                ApiProvider = "test-provider",
                SystemPrompt = "INLINE"
            });

            result.Should().Be("INLINE\n\nAGENTS\n\nSOUL\n\nTOOLS\n\nBOOTSTRAP\n\nIDENTITY\n\nUSER");
            File.Exists(Path.Combine(workspacePath, "BOOTSTRAP.md")).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(workspacePath)!, recursive: true);
        }
    }

    [Fact]
    public async Task BuildSystemPromptAsync_WithAgentRootPath_ResolvesWorkspaceSubdirectory()
    {
        var workspacePath = CreateWorkspace(("AGENTS.md", "AGENTS"));
        var agentRootPath = Path.GetDirectoryName(workspacePath)!;
        try
        {
            var manager = new StubWorkspaceManager(agentRootPath);
            var builder = new WorkspaceContextBuilder(manager);

            var result = await builder.BuildSystemPromptAsync(new AgentDescriptor
            {
                AgentId = "farnsworth",
                DisplayName = "Farnsworth",
                ModelId = "test-model",
                ApiProvider = "test-provider"
            });

            result.Should().Be("AGENTS");
        }
        finally
        {
            Directory.Delete(agentRootPath, recursive: true);
        }
    }

    private sealed class StubWorkspaceManager : IAgentWorkspaceManager
    {
        private readonly string _workspacePath;

        public StubWorkspaceManager(string workspacePath)
        {
            _workspacePath = workspacePath;
        }

        public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken ct = default)
            => Task.FromResult(new AgentWorkspace(agentName, Soul: string.Empty, Identity: string.Empty, User: string.Empty, Memory: string.Empty));

        public Task SaveMemoryAsync(string agentName, string content, CancellationToken ct = default)
            => Task.CompletedTask;

        public string GetWorkspacePath(string agentName)
            => _workspacePath;
    }

    private static string CreateWorkspace(params (string FileName, string Content)[] files)
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "botnexus-workspace-context-tests", Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(rootPath, "workspace");
        Directory.CreateDirectory(workspacePath);

        foreach (var (fileName, content) in files)
            File.WriteAllText(Path.Combine(workspacePath, fileName), content);

        return workspacePath;
    }
}
