using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests;

public sealed class WorkspaceContextBuilderTests
{
    private readonly MockFileSystem _fileSystem = new();

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
            var builder = new WorkspaceContextBuilder(manager, _fileSystem);

            var result = await builder.BuildSystemPromptAsync(new AgentDescriptor
            {
                AgentId = BotNexus.Domain.Primitives.AgentId.From("farnsworth"),
                DisplayName = "Farnsworth",
                ModelId = "test-model",
                ApiProvider = "test-provider",
                SystemPromptFiles = ["AGENTS.md", "BOOTSTRAP.md", "TOOLS.md"]
            });

            result.ShouldContain("AGENTS");
            result.ShouldContain("BOOTSTRAP");
            result.ShouldContain("TOOLS");
            result.ShouldNotContain("SOUL");
            _fileSystem.File.Exists(Path.Combine(workspacePath, "BOOTSTRAP.md")).ShouldBeFalse();
        }
        finally
        {
            _fileSystem.Directory.Delete(Path.GetDirectoryName(workspacePath)!, recursive: true);
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
            var builder = new WorkspaceContextBuilder(manager, _fileSystem);

            var result = await builder.BuildSystemPromptAsync(new AgentDescriptor
            {
                AgentId = BotNexus.Domain.Primitives.AgentId.From("farnsworth"),
                DisplayName = "Farnsworth",
                ModelId = "test-model",
                ApiProvider = "test-provider",
                SystemPrompt = "INLINE"
            });

            result.ShouldContain("AGENTS");
            result.ShouldContain("SOUL");
            result.ShouldContain("TOOLS");
            result.ShouldContain("BOOTSTRAP");
            result.ShouldContain("IDENTITY");
            result.ShouldContain("USER");
            _fileSystem.File.Exists(Path.Combine(workspacePath, "BOOTSTRAP.md")).ShouldBeFalse();
        }
        finally
        {
            _fileSystem.Directory.Delete(Path.GetDirectoryName(workspacePath)!, recursive: true);
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
            var builder = new WorkspaceContextBuilder(manager, _fileSystem);

            var result = await builder.BuildSystemPromptAsync(new AgentDescriptor
            {
                AgentId = BotNexus.Domain.Primitives.AgentId.From("farnsworth"),
                DisplayName = "Farnsworth",
                ModelId = "test-model",
                ApiProvider = "test-provider"
            });

            result.ShouldContain("AGENTS");
            result.ShouldContain("BotNexus");
        }
        finally
        {
            _fileSystem.Directory.Delete(agentRootPath, recursive: true);
        }
    }

    [Fact]
    public async Task BuildSystemPromptAsync_DefaultPrompt_IncludesMemorySummaryAndRecentDailyMemoryFiles()
    {
        var todayFileName = $"{DateTime.Now:yyyy-MM-dd}.md";
        var yesterdayFileName = $"{DateTime.Now.AddDays(-1):yyyy-MM-dd}.md";
        var workspacePath = CreateWorkspace(
            ("AGENTS.md", "AGENTS"),
            ("SOUL.md", "SOUL"),
            ("TOOLS.md", "TOOLS"),
            ("BOOTSTRAP.md", "BOOTSTRAP"),
            ("IDENTITY.md", "IDENTITY"),
            ("USER.md", "USER"),
            ("MEMORY.md", "LONG-TERM MEMORY"),
            ($@"memory\{todayFileName}", "TODAY MEMORY ENTRY"),
            ($@"memory\{yesterdayFileName}", "YESTERDAY MEMORY ENTRY"));
        try
        {
            var manager = new StubWorkspaceManager(workspacePath);
            var builder = new WorkspaceContextBuilder(manager, _fileSystem);

            var result = await builder.BuildSystemPromptAsync(new AgentDescriptor
            {
                AgentId = BotNexus.Domain.Primitives.AgentId.From("farnsworth"),
                DisplayName = "Farnsworth",
                ModelId = "test-model",
                ApiProvider = "test-provider"
            });

            result.ShouldContain("LONG-TERM MEMORY");
            result.ShouldContain("TODAY MEMORY ENTRY");
            result.ShouldContain("YESTERDAY MEMORY ENTRY");
        }
        finally
        {
            _fileSystem.Directory.Delete(Path.GetDirectoryName(workspacePath)!, recursive: true);
        }
    }

    [Fact]
    public async Task BuildSystemPromptAsync_DefaultPrompt_LoadsRecentDailyMemoryFilesFromOverridePathInDeterministicOrder()
    {
        var todayFileName = $"{DateTime.Now:yyyy-MM-dd}.md";
        var yesterdayFileName = $"{DateTime.Now.AddDays(-1):yyyy-MM-dd}.md";
        var workspacePath = CreateWorkspace(
            ("AGENTS.md", "AGENTS"),
            ("SOUL.md", "SOUL"),
            ("TOOLS.md", "TOOLS"),
            ("BOOTSTRAP.md", "BOOTSTRAP"),
            ("IDENTITY.md", "IDENTITY"),
            ("USER.md", "USER"),
            ("MEMORY.md", "LONG-TERM MEMORY"),
            ($@"journals\{todayFileName}", "OVERRIDE TODAY"),
            ($@"journals\{yesterdayFileName}", "OVERRIDE YESTERDAY"),
            ($@"memory\{todayFileName}", "DEFAULT MEMORY SHOULD NOT LOAD"));
        try
        {
            var manager = new StubWorkspaceManager(workspacePath);
            var builder = new WorkspaceContextBuilder(manager, _fileSystem);

            var result = await builder.BuildSystemPromptAsync(new AgentDescriptor
            {
                AgentId = BotNexus.Domain.Primitives.AgentId.From("farnsworth"),
                DisplayName = "Farnsworth",
                ModelId = "test-model",
                ApiProvider = "test-provider",
                Memory = new MemoryAgentConfig { Enabled = true, Path = "journals" }
            });

            result.ShouldContain("LONG-TERM MEMORY");
            result.ShouldContain("OVERRIDE TODAY");
            result.ShouldContain("OVERRIDE YESTERDAY");
            result.ShouldNotContain("DEFAULT MEMORY SHOULD NOT LOAD");

            var memoryIndex = result.IndexOf("## MEMORY.md", StringComparison.Ordinal);
            var todayIndex = result.IndexOf($"## journals/{todayFileName}", StringComparison.Ordinal);
            var yesterdayIndex = result.IndexOf($"## journals/{yesterdayFileName}", StringComparison.Ordinal);
            memoryIndex.ShouldBeGreaterThanOrEqualTo(0);
            yesterdayIndex.ShouldBeGreaterThan(memoryIndex);
            todayIndex.ShouldBeGreaterThan(yesterdayIndex);
        }
        finally
        {
            _fileSystem.Directory.Delete(Path.GetDirectoryName(workspacePath)!, recursive: true);
        }
    }

    [Fact]
    public async Task BuildSystemPromptAsync_DefaultPrompt_WithFileOverride_UsesOverrideDirectoryForRecentDailyFiles()
    {
        var todayFileName = $"{DateTime.Now:yyyy-MM-dd}.md";
        var workspacePath = CreateWorkspace(
            ("AGENTS.md", "AGENTS"),
            ("SOUL.md", "SOUL"),
            ("TOOLS.md", "TOOLS"),
            ("BOOTSTRAP.md", "BOOTSTRAP"),
            ("IDENTITY.md", "IDENTITY"),
            ("USER.md", "USER"),
            ($@"journals\{todayFileName}", "FILE OVERRIDE TODAY"),
            ($@"memory\{todayFileName}", "DEFAULT MEMORY SHOULD NOT LOAD"));
        try
        {
            var manager = new StubWorkspaceManager(workspacePath);
            var builder = new WorkspaceContextBuilder(manager, _fileSystem);

            var result = await builder.BuildSystemPromptAsync(new AgentDescriptor
            {
                AgentId = BotNexus.Domain.Primitives.AgentId.From("farnsworth"),
                DisplayName = "Farnsworth",
                ModelId = "test-model",
                ApiProvider = "test-provider",
                Memory = new MemoryAgentConfig { Enabled = true, Path = "journals\\daily.md" }
            });

            result.ShouldContain("FILE OVERRIDE TODAY");
            result.ShouldNotContain("DEFAULT MEMORY SHOULD NOT LOAD");
            result.ShouldContain($"## journals/{todayFileName}");
        }
        finally
        {
            _fileSystem.Directory.Delete(Path.GetDirectoryName(workspacePath)!, recursive: true);
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

        public Task SaveMemoryAsync(string agentName, string? filePath, string content, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SaveMemoryAsync(
            string agentName,
            string? filePath,
            string content,
            string? memoryPathOverride,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public string GetWorkspacePath(string agentName)
            => _workspacePath;
    }

    private string CreateWorkspace(params (string FileName, string Content)[] files)
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "botnexus-workspace-context-tests", Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(rootPath, "workspace");
        _fileSystem.Directory.CreateDirectory(workspacePath);

        foreach (var (fileName, content) in files)
        {
            var filePath = Path.Combine(workspacePath, fileName);
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
                _fileSystem.Directory.CreateDirectory(directory);
            _fileSystem.File.WriteAllText(filePath, content);
        }

        return workspacePath;
    }
}
