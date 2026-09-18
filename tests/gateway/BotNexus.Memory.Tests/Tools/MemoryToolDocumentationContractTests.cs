using System.Text.RegularExpressions;
using BotNexus.Agent.Core.Tools;
using BotNexus.Gateway.Contracts.Memory;
using BotNexus.Memory.Tools;

namespace BotNexus.Memory.Tests.Tools;

public sealed partial class MemoryToolDocumentationContractTests
{
    [Theory]
    [InlineData("memory_search")]
    [InlineData("memory_save")]
    [InlineData("memory_get")]
    public void WorkspaceGuideSignatureMatchesCurrentToolSchema(string toolName)
    {
        var tool = CreateTool(toolName);
        var schemaProperties = tool.Definition.Parameters
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var guide = File.ReadAllText(FindRepositoryFile("docs", "development", "workspace-and-memory.md"));
        var signature = SignatureBlock(toolName).Match(guide);
        signature.Success.ShouldBeTrue($"The guide must contain a signature block for {toolName}.");

        var documentedProperties = ParameterLine()
            .Matches(signature.Groups["parameters"].Value)
            .Select(match => match.Groups["name"].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

        documentedProperties.ShouldBe(schemaProperties,
            $"The {toolName} guide signature must match the shipped tool schema exactly.");
    }

    private static IAgentTool CreateTool(string toolName)
        => toolName switch
        {
            "memory_search" => new MemorySearchTool(new StubAgentMemory(), "agent-a"),
            "memory_save" => new MemorySaveTool(new StubAgentMemory(), "agent-a"),
            "memory_get" => new MemoryGetTool(null!),
            _ => throw new ArgumentOutOfRangeException(nameof(toolName))
        };

    private static string FindRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate repository file: {Path.Combine(segments)}");
    }

    [GeneratedRegex(@"(?ms)^### (?<tool>memory_(?:search|save|get)) .*?\*\*Signature\*\*:\s*```text\s*\k<tool>\(\s*(?<parameters>.*?)\)\s*```")]
    private static partial Regex SignatureBlockTemplate();

    private static Regex SignatureBlock(string toolName)
        => new(SignatureBlockTemplate().ToString().Replace("(?<tool>memory_(?:search|save|get))", Regex.Escape(toolName)).Replace("\\k<tool>", Regex.Escape(toolName)), RegexOptions.Multiline | RegexOptions.Singleline);

    [GeneratedRegex(@"(?m)^\s*(?<name>[A-Za-z_][A-Za-z0-9_]*):")]
    private static partial Regex ParameterLine();

    private sealed class StubAgentMemory : IAgentMemory
    {
        public Task<AgentMemoryContext> GetPromptContextAsync(AgentMemoryPromptRequest request, CancellationToken ct = default)
            => Task.FromResult(AgentMemoryContext.Empty);
        public Task SaveAsync(AgentMemorySaveRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AgentMemorySearchResult>> SearchAsync(AgentMemorySearchRequest request, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentMemorySearchResult>>([]);
        public Task<AgentMemorySearchResult?> GetAsync(string entryId, CancellationToken ct = default)
            => Task.FromResult<AgentMemorySearchResult?>(null);
        public Task OnSessionCompleteAsync(AgentMemorySessionEvent sessionEvent, CancellationToken ct = default) => Task.CompletedTask;
        public Task ConsolidateAsync(AgentMemoryConsolidateRequest request, CancellationToken ct = default) => Task.CompletedTask;
    }
}
