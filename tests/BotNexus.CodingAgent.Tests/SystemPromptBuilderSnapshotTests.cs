using System.IO;
using System.Text;
using BotNexus.CodingAgent;

namespace BotNexus.CodingAgent.Tests;

public sealed class SystemPromptBuilderSnapshotTests
{
    [Fact]
    public void Build_GitAndToolContributions_MatchesSnapshot()
    {
        var builder = new SystemPromptBuilder();
        // Use fixed cross-platform directory path instead of Path.GetTempPath() to avoid platform differences
        var prompt = builder.Build(new SystemPromptContext(
            WorkingDirectory: Path.Combine(Path.GetTempPath(), "repo"),
            GitBranch: "feature/unify-prompts",
            GitStatus: "M src/file.cs",
            PackageManager: "pnpm",
            ToolNames: ["read", "write", "bash", "glob"],
            Skills:
            [
                """
                ---
                name: code-search
                description: Search code quickly
                ---
                Use search tools first.
                """
            ],
            CustomInstructions: "Focus on minimal diffs.",
            CustomPrompt: null,
            AppendSystemPrompt: "Always verify tests.",
            ToolContributions:
            [
                new ToolPromptContribution("read", "Read files with line numbers.", ["Prefer read before edit."]),
                new ToolPromptContribution("write", "Write complete files."),
                new ToolPromptContribution("bash", "Run shell commands.", ["Prefer dedicated tools for search."]),
                new ToolPromptContribution("glob", "Find files by pattern.")
            ],
            ContextFiles:
            [
                new PromptContextFile("AGENTS.md", "Project guidance"),
                new PromptContextFile(".botnexus-agent/AGENTS.md", "Agent-local guidance")
            ],
            CurrentDateTime: new DateTimeOffset(2026, 4, 12, 14, 30, 0, TimeSpan.Zero)));

        AssertMatchesSnapshot("coding-agent-main.prompt.txt", prompt);
    }

    private static void AssertMatchesSnapshot(string fileName, string actual)
    {
        var expectedPath = Path.Combine(FindRepositoryRoot(), "tests", "BotNexus.CodingAgent.Tests", "Snapshots", fileName);
        var normalized = Normalize(actual);

        if (string.Equals(Environment.GetEnvironmentVariable("UPDATE_PROMPT_SNAPSHOTS"), "1", StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            File.WriteAllText(expectedPath, normalized + "\n", Encoding.UTF8);
        }

        File.Exists(expectedPath).ShouldBeTrue($"snapshot file '{expectedPath}' must exist");
        var expected = Normalize(File.ReadAllText(expectedPath)).TrimEnd('\n');
        normalized.ShouldBe(expected);
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BotNexus.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate BotNexus.slnx from test base directory.");
    }
}