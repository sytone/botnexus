using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

public sealed class CopilotProviderTransportArchitectureTests
{
    private static readonly Regex s_wireTransport = new(
        @"\b(CopilotResponsesWireTransport|CopilotResponsesTransportPreference|ICopilotResponsesWebSocketTransport)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void AgentLoopAndGateway_DoNotSelectCopilotWireTransport()
    {
        var repoRoot = FindRepoRoot();
        var roots = new[]
        {
            Path.Combine(repoRoot, "src", "agent", "BotNexus.Agent.Core"),
            Path.Combine(repoRoot, "src", "gateway")
        };

        var violations = roots.SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => s_wireTransport.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(repoRoot, path))
            .ToArray();

        violations.ShouldBeEmpty("Copilot SSE/WebSocket selection is provider-private; agent-loop and gateway code consume normalized LlmStream events only.");
    }

    [Fact]
    public void Fence_CatchesSyntheticTransportSelection()
    {
        s_wireTransport.IsMatch("var selected = CopilotResponsesWireTransport.WebSocket;").ShouldBeTrue();
        s_wireTransport.IsMatch("await foreach (var evt in provider.Stream(model, context)) { }").ShouldBeFalse();
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
