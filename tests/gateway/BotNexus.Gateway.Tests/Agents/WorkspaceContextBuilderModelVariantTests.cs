using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>
/// End-to-end coverage that model-specific instruction-file variants are selected on the real
/// prompt-build path, per the conversation's EFFECTIVE model (#2435).
/// </summary>
/// <remarks>
/// The unit tests in <c>ContextFileVariantsTests</c> prove the grammar and ladder in isolation.
/// These prove the wiring: that the effective settings threaded in by #2796 — not the descriptor
/// default — choose the file, which is what makes "one conversation on GPT and another on Claude
/// read different files from the SAME workspace" true rather than merely designed.
/// </remarks>
public sealed class WorkspaceContextBuilderModelVariantTests
{
    private readonly MockFileSystem _fileSystem = new();

    [Fact]
    public async Task BuildSystemPromptAsync_SelectsMostSpecificVariant_ForTheEffectiveModel()
    {
        var workspacePath = CreateWorkspace(
            ("AGENTS.md", "BASE-AGENTS"),
            ("AGENTS.gpt.md", "GPT-FAMILY-AGENTS"),
            ("AGENTS.gpt-5.md", "GPT-MAJOR-AGENTS"),
            ("AGENTS.gpt-5-6.md", "GPT-MINOR-AGENTS"),
            ("AGENTS.claude-opus.md", "CLAUDE-AGENTS"));

        var result = await BuildAsync(workspacePath, effectiveModel: "gpt-5.6", effectiveProvider: "openai");

        result.ShouldContain("GPT-MINOR-AGENTS");
        result.ShouldNotContain("BASE-AGENTS");
        result.ShouldNotContain("GPT-FAMILY-AGENTS");
        result.ShouldNotContain("GPT-MAJOR-AGENTS");
        result.ShouldNotContain("CLAUDE-AGENTS");
    }

    [Fact]
    public async Task BuildSystemPromptAsync_SelectsADifferentVariant_ForADifferentModel_InTheSameWorkspace()
    {
        // The headline scenario of #2435: identical workspace, two conversations, two files.
        var workspacePath = CreateWorkspace(
            ("AGENTS.md", "BASE-AGENTS"),
            ("AGENTS.gpt.md", "GPT-AGENTS"),
            ("AGENTS.claude-opus.md", "CLAUDE-AGENTS"));

        var gptPrompt = await BuildAsync(workspacePath, "gpt-5.6", "openai");
        var claudePrompt = await BuildAsync(workspacePath, "claude-opus-5", "anthropic");

        gptPrompt.ShouldContain("GPT-AGENTS");
        gptPrompt.ShouldNotContain("CLAUDE-AGENTS");
        claudePrompt.ShouldContain("CLAUDE-AGENTS");
        claudePrompt.ShouldNotContain("GPT-AGENTS");
    }

    [Fact]
    public async Task BuildSystemPromptAsync_PrefersEffectiveSettingsOverDescriptorModel()
    {
        // #2796's contract, exercised through the variant seam: a conversation pinned to Claude
        // must read the Claude file even though the descriptor still says GPT.
        var workspacePath = CreateWorkspace(
            ("AGENTS.md", "BASE-AGENTS"),
            ("AGENTS.gpt.md", "GPT-AGENTS"),
            ("AGENTS.claude.md", "CLAUDE-AGENTS"));

        var result = await BuildAsync(
            workspacePath,
            effectiveModel: "claude-opus-5",
            effectiveProvider: "anthropic",
            descriptorModel: "gpt-5.6",
            descriptorProvider: "openai");

        result.ShouldContain("CLAUDE-AGENTS");
        result.ShouldNotContain("GPT-AGENTS");
    }

    [Fact]
    public async Task BuildSystemPromptAsync_FallsBackToBaseFile_WhenSuffixIsMalformed()
    {
        // Sad path. An uppercase suffix, a doubled separator and an unknown family must ALL leave
        // the base file in force -- not throw, and emphatically not load the wrong instructions.
        var workspacePath = CreateWorkspace(
            ("AGENTS.md", "BASE-AGENTS"),
            ("AGENTS.GPT.md", "UPPERCASE-AGENTS"),
            ("AGENTS.gpt--5.md", "DOUBLE-SEPARATOR-AGENTS"),
            ("AGENTS.mistral.md", "UNKNOWN-FAMILY-AGENTS"));

        var result = await BuildAsync(workspacePath, "gpt-5.6", "openai");

        result.ShouldContain("BASE-AGENTS");
        result.ShouldNotContain("UPPERCASE-AGENTS");
        result.ShouldNotContain("DOUBLE-SEPARATOR-AGENTS");
        result.ShouldNotContain("UNKNOWN-FAMILY-AGENTS");
    }

    [Fact]
    public async Task BuildSystemPromptAsync_FallsBackToBaseFile_WhenOnlyAnotherFamilysVariantExists()
    {
        var workspacePath = CreateWorkspace(
            ("AGENTS.md", "BASE-AGENTS"),
            ("AGENTS.claude-opus.md", "CLAUDE-AGENTS"));

        var result = await BuildAsync(workspacePath, "gpt-5.6", "openai");

        result.ShouldContain("BASE-AGENTS");
        result.ShouldNotContain("CLAUDE-AGENTS");
    }

    [Fact]
    public async Task BuildSystemPromptAsync_VariantSortsAtItsBaseFilesPosition()
    {
        // AC2 through the real prompt: the GPT variant of AGENTS.md must still precede SOUL.md and
        // MEMORY.md, exactly where AGENTS.md sits.
        var workspacePath = CreateWorkspace(
            ("AGENTS.md", "BASE-AGENTS"),
            ("AGENTS.gpt.md", "GPT-AGENTS"),
            ("SOUL.md", "SOUL-CONTENT"),
            ("MEMORY.md", "MEMORY-CONTENT"));

        var result = await BuildAsync(workspacePath, "gpt-5.6", "openai");

        result.IndexOf("GPT-AGENTS", StringComparison.Ordinal)
            .ShouldBeLessThan(result.IndexOf("SOUL-CONTENT", StringComparison.Ordinal));
        result.IndexOf("SOUL-CONTENT", StringComparison.Ordinal)
            .ShouldBeLessThan(result.IndexOf("MEMORY-CONTENT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildSystemPromptAsync_ResolvesWorldFileVariant()
    {
        var workspacePath = CreateWorkspace(("AGENTS.md", "BASE-AGENTS"));
        var homePath = Path.GetDirectoryName(workspacePath)!;
        _fileSystem.File.WriteAllText(Path.Combine(homePath, "WORLD.md"), "BASE-WORLD");
        _fileSystem.File.WriteAllText(Path.Combine(homePath, "WORLD.claude.md"), "CLAUDE-WORLD");

        var builder = new WorkspaceContextBuilder(
            new StubVariantWorkspaceManager(workspacePath), _fileSystem, new BotNexusHome(homePath));

        var result = await builder.BuildSystemPromptAsync(
            Descriptor("claude-opus-5", "anthropic"),
            executionContext: null,
            new EffectiveExecutionSettings("anthropic", "claude-opus-5", "claude-opus-5", null, null));

        result.ShouldContain("CLAUDE-WORLD");
        result.ShouldNotContain("BASE-WORLD");
    }

    [Fact]
    public async Task BuildSystemPromptAsync_DeletesBootstrapVariant_AfterReadingIt()
    {
        // BOOTSTRAP is consumed on read. A variant that survived would re-run its one-shot
        // instructions every turn, which is a worse failure than not supporting variants at all.
        var workspacePath = CreateWorkspace(
            ("AGENTS.md", "BASE-AGENTS"),
            ("BOOTSTRAP.md", "BASE-BOOTSTRAP"),
            ("BOOTSTRAP.gpt.md", "GPT-BOOTSTRAP"));

        var result = await BuildAsync(workspacePath, "gpt-5.6", "openai");

        result.ShouldContain("GPT-BOOTSTRAP");
        _fileSystem.File.Exists(Path.Combine(workspacePath, "BOOTSTRAP.gpt.md")).ShouldBeFalse();
        // The base bootstrap was never read this turn, so it must NOT have been consumed.
        _fileSystem.File.Exists(Path.Combine(workspacePath, "BOOTSTRAP.md")).ShouldBeTrue();
    }

    [Fact]
    public async Task BuildSystemPromptAsync_SuppressesMemoryVariant_WhenPromptInjectionIsNone()
    {
        // A variant of withheld content is still withheld: MEMORY.gpt.md must obey the same
        // memory.promptInjection switch that MEMORY.md obeys.
        var workspacePath = CreateWorkspace(
            ("AGENTS.md", "BASE-AGENTS"),
            ("MEMORY.md", "BASE-MEMORY"),
            ("MEMORY.gpt.md", "GPT-MEMORY"));

        var descriptor = Descriptor("gpt-5.6", "openai") with
        {
            Memory = new MemoryAgentConfig { PromptInjection = "none" }
        };

        var builder = new WorkspaceContextBuilder(new StubVariantWorkspaceManager(workspacePath), _fileSystem);
        var result = await builder.BuildSystemPromptAsync(
            descriptor,
            executionContext: null,
            new EffectiveExecutionSettings("openai", "gpt-5.6", "gpt-5.6", null, null));

        result.ShouldNotContain("GPT-MEMORY");
        result.ShouldNotContain("BASE-MEMORY");
    }

    private async Task<string> BuildAsync(
        string workspacePath,
        string effectiveModel,
        string effectiveProvider,
        string? descriptorModel = null,
        string? descriptorProvider = null)
    {
        var builder = new WorkspaceContextBuilder(new StubVariantWorkspaceManager(workspacePath), _fileSystem);

        return await builder.BuildSystemPromptAsync(
            Descriptor(descriptorModel ?? effectiveModel, descriptorProvider ?? effectiveProvider),
            executionContext: null,
            new EffectiveExecutionSettings(effectiveProvider, effectiveModel, descriptorModel ?? effectiveModel, null, null));
    }

    private static AgentDescriptor Descriptor(string modelId, string providerId) => new()
    {
        AgentId = BotNexus.Domain.Primitives.AgentId.From("farnsworth"),
        DisplayName = "Farnsworth",
        ModelId = modelId,
        ApiProvider = providerId
    };

    private string CreateWorkspace(params (string FileName, string Content)[] files)
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "botnexus-variant-tests", Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(rootPath, "workspace");
        _fileSystem.Directory.CreateDirectory(workspacePath);

        foreach (var (fileName, content) in files)
            _fileSystem.File.WriteAllText(Path.Combine(workspacePath, fileName), content);

        return workspacePath;
    }

    private sealed class StubVariantWorkspaceManager : IAgentWorkspaceManager
    {
        private readonly string _workspacePath;

        public StubVariantWorkspaceManager(string workspacePath) => _workspacePath = workspacePath;

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

        public string GetWorkspacePath(string agentName) => _workspacePath;
    }
}
