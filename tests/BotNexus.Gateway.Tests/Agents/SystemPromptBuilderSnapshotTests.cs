using System.Text;
using System.Text.RegularExpressions;
using BotNexus.Gateway.Agents;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class SystemPromptBuilderSnapshotTests
{
    [Fact]
    public void Build_FullMode_MatchesSnapshot()
    {
        var prompt = SystemPromptBuilder.Build(new SystemPromptParams
        {
            WorkspaceDir = Path.Combine(Path.GetTempPath(), "repo", "workspace"),
            ExtraSystemPrompt = "Custom context line 1\n\nCustom context line 2",
            ToolNames = ["read", "exec", "process", "cron", "update_plan", "gateway", "message"],
            UserTimezone = "Pacific/Auckland",
            ContextFiles =
            [
                new ContextFile("README.md", "Repo overview"),
                new ContextFile("HEARTBEAT.md", "Heartbeat context"),
                new ContextFile("AGENTS.md", "Agent instructions"),
                new ContextFile("SOUL.md", "Persona guidance")
            ],
            SkillsPrompt = "## Available Skills\n- skill-a\n- skill-b",
            HeartbeatPrompt = "HEARTBEAT?",
            DocsPath = Path.Combine(Path.GetTempPath(), "repo", "docs"),
            WorkspaceNotes = ["Note one", "Note two"],
            TtsHint = "Speak naturally.",
            PromptMode = PromptMode.Full,
            Runtime = new RuntimeInfo
            {
                AgentId = "agent-a",
                Host = "host-a",
                Os = "Windows",
                Arch = "x64",
                Provider = "openai",
                Model = "gpt-5",
                DefaultModel = "gpt-5-mini",
                Shell = "pwsh",
                Channel = "telegram",
                Capabilities = ["inlineButtons", "reactions"]
            },
            ModelAliasLines = ["- fast => gpt-5-mini", "- smart => gpt-5"],
            OwnerIdentity = "Owner: Jon",
            ReasoningTagHint = true,
            ReasoningLevel = "medium"
        });

        AssertMatchesSnapshot("gateway-full.prompt.txt", prompt);
    }

    [Fact]
    public void Build_MinimalMode_MatchesSnapshot()
    {
        var prompt = SystemPromptBuilder.Build(new SystemPromptParams
        {
            WorkspaceDir = Path.Combine(Path.GetTempPath(), "repo", "workspace"),
            ExtraSystemPrompt = "Sub-agent context",
            ToolNames = ["read", "exec", "process"],
            UserTimezone = "UTC",
            ContextFiles =
            [
                new ContextFile("HEARTBEAT.md", "Heartbeat context"),
                new ContextFile("AGENTS.md", "Agent instructions")
            ],
            SkillsPrompt = "## Available Skills\n- skill-a",
            HeartbeatPrompt = "HEARTBEAT?",
            DocsPath = Path.Combine(Path.GetTempPath(), "repo", "docs"),
            WorkspaceNotes = ["Minimal note"],
            TtsHint = "Speak naturally.",
            PromptMode = PromptMode.Minimal,
            Runtime = new RuntimeInfo
            {
                AgentId = "agent-a",
                Host = "host-a",
                Os = "Windows",
                Arch = "x64",
                Provider = "openai",
                Model = "gpt-5",
                Shell = "pwsh",
                Channel = "signalr",
                Capabilities = ["reactions"]
            },
            ModelAliasLines = ["- fast => gpt-5-mini"],
            OwnerIdentity = "Owner: Jon",
            ReasoningTagHint = true,
            ReasoningLevel = "off"
        });

        AssertMatchesSnapshot("gateway-minimal.prompt.txt", prompt);
    }

    [Fact]
    public void Build_NoTools_MatchesSnapshot()
    {
        var prompt = SystemPromptBuilder.Build(new SystemPromptParams
        {
            WorkspaceDir = Path.Combine(Path.GetTempPath(), "repo", "workspace"),
            ToolNames = [],
            PromptMode = PromptMode.Full,
            Runtime = new RuntimeInfo
            {
                AgentId = "agent-a",
                Channel = "signalr"
            }
        });

        AssertMatchesSnapshot("gateway-no-tools.prompt.txt", prompt);
    }

    private static void AssertMatchesSnapshot(string fileName, string actual)
    {
        var expectedPath = Path.Combine(FindRepositoryRoot(), "tests", "BotNexus.Gateway.Tests", "Snapshots", fileName);
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

    private static string Normalize(string value)
    {
        var normalized = value.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        normalized = Regex.Replace(normalized, @"^Your working directory is: .*$", "Your working directory is: <workspace>", RegexOptions.Multiline);
        normalized = Regex.Replace(normalized, @"^BotNexus docs: .*$", "BotNexus docs: <docs-path>", RegexOptions.Multiline);
        return normalized;
    }

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