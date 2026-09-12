using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class WorkspaceContextBuilderPathCaseContainmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "botnexus-path-case-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BuildSystemPromptAsync_CaseDistinctPromptSibling_FollowsHostPathIdentity()
    {
        var workspace = Path.Combine(_root, "workspace");
        var caseDistinctPath = Path.Combine(_root, "Workspace");
        Directory.CreateDirectory(workspace);

        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(Path.Combine(workspace, "AGENTS.gpt.md"), "CASE-EQUIVALENT-PROMPT");
        }
        else
        {
            Directory.CreateDirectory(caseDistinctPath);
            await File.WriteAllTextAsync(Path.Combine(caseDistinctPath, "AGENTS.gpt.md"), "CASE-DISTINCT-SIBLING");
        }

        var result = await BuildAsync(workspace, "../Workspace/AGENTS.md");

        if (OperatingSystem.IsWindows())
            result.ShouldContain("CASE-EQUIVALENT-PROMPT");
        else
            result.ShouldNotContain("CASE-DISTINCT-SIBLING");
    }

    [Fact]
    public async Task BuildSystemPromptAsync_CaseDistinctBootstrapSibling_IsNotReadOrDeleted()
    {
        if (OperatingSystem.IsWindows())
            return;

        var workspace = Path.Combine(_root, "workspace");
        var sibling = Path.Combine(_root, "Workspace");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(sibling);
        var bootstrap = Path.Combine(sibling, "BOOTSTRAP.gpt.md");
        await File.WriteAllTextAsync(bootstrap, "OUTSIDE-BOOTSTRAP");

        var result = await BuildAsync(workspace, "../Workspace/BOOTSTRAP.md");

        result.ShouldNotContain("OUTSIDE-BOOTSTRAP");
        File.Exists(bootstrap).ShouldBeTrue();
    }

    [Fact]
    public async Task BuildSystemPromptAsync_CaseDistinctMemorySibling_FallsBackInsideWorkspace()
    {
        if (OperatingSystem.IsWindows())
            return;

        var workspace = Path.Combine(_root, "workspace");
        var localMemory = Path.Combine(workspace, "memory");
        var siblingMemory = Path.Combine(_root, "Workspace", "memory");
        Directory.CreateDirectory(localMemory);
        Directory.CreateDirectory(siblingMemory);
        var today = DateTime.Now.ToString("yyyy-MM-dd") + ".md";
        await File.WriteAllTextAsync(Path.Combine(localMemory, today), "LOCAL-MEMORY");
        await File.WriteAllTextAsync(Path.Combine(siblingMemory, today), "OUTSIDE-MEMORY");

        var result = await BuildAsync(workspace, "AGENTS.md", new MemoryAgentConfig
        {
            Enabled = true,
            Path = "../Workspace/memory"
        });

        result.ShouldContain("LOCAL-MEMORY");
        result.ShouldNotContain("OUTSIDE-MEMORY");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static async Task<string> BuildAsync(string workspace, string promptFile, MemoryAgentConfig? memory = null)
    {
        var builder = new WorkspaceContextBuilder(new StubWorkspaceManager(workspace), new FileSystem());
        return await builder.BuildSystemPromptAsync(
            new AgentDescriptor
            {
                AgentId = BotNexus.Domain.Primitives.AgentId.From("case-containment"),
                DisplayName = "Case containment",
                ModelId = "gpt-5.6",
                ApiProvider = "openai",
                SystemPromptFiles = [promptFile],
                Memory = memory
            },
            executionContext: null,
            new EffectiveExecutionSettings("openai", "gpt-5.6", "gpt-5.6", null, null));
    }

    private sealed class StubWorkspaceManager(string workspacePath) : IAgentWorkspaceManager
    {
        public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken ct = default)
            => Task.FromResult(new AgentWorkspace(agentName, string.Empty, string.Empty, string.Empty, string.Empty));

        public Task SaveMemoryAsync(string agentName, string content, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveMemoryAsync(string agentName, string? filePath, string content, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveMemoryAsync(string agentName, string? filePath, string content, string? memoryPathOverride, CancellationToken ct = default) => Task.CompletedTask;
        public string GetWorkspacePath(string agentName) => workspacePath;
    }
}
