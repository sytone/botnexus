using System.Text;
using System.Text.RegularExpressions;
using BotNexus.Gateway.Agents;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class SystemPromptBuilderSnapshotTests
{
    [Fact]
    public void BuildMemorySection_FullPromptInjection_EmitsBehavioralGuidance()
    {
        var lines = InvokeBuildMemorySection(isMinimal: false, "full");

        lines.ShouldNotBeEmpty();
        var text = string.Join('\n', lines).ToLowerInvariant();
        text.ShouldContain("memory");
    }

    [Fact]
    public void BuildMemorySection_SummaryPromptInjection_EmitsConciseGuidanceOnly()
    {
        var full = InvokeBuildMemorySection(isMinimal: false, "full");
        var summary = InvokeBuildMemorySection(isMinimal: false, "summary");

        summary.ShouldNotBeEmpty();
        summary.Count.ShouldBeLessThan(full.Count);
    }

    [Fact]
    public void BuildMemorySection_NonePromptInjection_ReturnsNoMemorySection()
    {
        var lines = InvokeBuildMemorySection(isMinimal: false, "none");
        lines.ShouldBeEmpty();
    }

    [Fact]
    public void BuildMemorySection_MinimalPromptMode_ReturnsNoMemorySection()
    {
        var lines = InvokeBuildMemorySection(isMinimal: true, "full");
        lines.ShouldBeEmpty();
    }

    [Fact]
    public void ContextFileOrdering_DailyMemoryNotes_RenderAfterCacheBoundary()
    {
        var prompt = SystemPromptBuilder.Build(new SystemPromptParams
        {
            WorkspaceDir = Path.Combine(Path.GetTempPath(), "repo", "workspace"),
            ToolNames = ["read"],
            PromptMode = PromptMode.Full,
            ContextFiles =
            [
                new ContextFile("AGENTS.md", "Agent instructions"),
                new ContextFile("MEMORY.md", "Long-term memory summary"),
                new ContextFile("memory/2026-05-07.md", "Older daily note"),
                new ContextFile("memory/2026-05-08.md", "Latest daily note")
            ],
            Runtime = new RuntimeInfo
            {
                AgentId = "agent-a",
                Channel = "signalr"
            }
        });

        var cacheBoundaryIndex = prompt.IndexOf("<!-- BOTNEXUS_CACHE_BOUNDARY -->", StringComparison.Ordinal);
        var memorySummaryIndex = prompt.IndexOf("## MEMORY.md", StringComparison.Ordinal);
        var olderDailyIndex = prompt.IndexOf("## memory/2026-05-07.md", StringComparison.Ordinal);
        var newerDailyIndex = prompt.IndexOf("## memory/2026-05-08.md", StringComparison.Ordinal);

        cacheBoundaryIndex.ShouldBeGreaterThanOrEqualTo(0);
        memorySummaryIndex.ShouldBeGreaterThanOrEqualTo(0);
        olderDailyIndex.ShouldBeGreaterThan(cacheBoundaryIndex);
        newerDailyIndex.ShouldBeGreaterThan(cacheBoundaryIndex);
        memorySummaryIndex.ShouldBeLessThan(cacheBoundaryIndex);
    }

    [Fact]
    public void Build_FullMode_DoesNotIncludeReplyTagsByDefault()
    {
        var prompt = SystemPromptBuilder.Build(new SystemPromptParams
        {
            WorkspaceDir = Path.Combine(Path.GetTempPath(), "repo", "workspace"),
            ToolNames = ["read"],
            PromptMode = PromptMode.Full
        });

        prompt.ShouldNotContain("## Reply Tags");
    }

    [Fact]
    public void Build_MinimalMode_DoesNotIncludeReplyTagsByDefault()
    {
        var prompt = SystemPromptBuilder.Build(new SystemPromptParams
        {
            WorkspaceDir = Path.Combine(Path.GetTempPath(), "repo", "workspace"),
            ToolNames = ["read"],
            PromptMode = PromptMode.Minimal
        });

        prompt.ShouldNotContain("## Reply Tags");
    }

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
        var expectedPath = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "gateway",
            "BotNexus.Gateway.Tests",
            "Snapshots",
            fileName);
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

    private static IReadOnlyList<string> InvokeBuildMemorySection(bool isMinimal, string promptInjection)
    {
        var availableTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "memory_save", "memory_search" };
        var methods = typeof(SystemPromptBuilder)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(method => string.Equals(method.Name, "buildMemorySection", StringComparison.Ordinal))
            .ToList();
        methods.ShouldNotBeEmpty("SystemPromptBuilder.buildMemorySection should exist.");

        var threeArgMethod = methods.SingleOrDefault(method =>
        {
            var parameters = method.GetParameters();
            return parameters.Length == 3 &&
                   parameters[0].ParameterType == typeof(bool) &&
                   parameters[1].ParameterType == typeof(string) &&
                   typeof(IReadOnlySet<string>).IsAssignableFrom(parameters[2].ParameterType);
        });
        if (threeArgMethod is not null)
        {
            var result = threeArgMethod.Invoke(null, [isMinimal, promptInjection, availableTools]);
            result.ShouldNotBeNull();
            return (IReadOnlyList<string>)result!;
        }

        var twoArgMethod = methods.SingleOrDefault(method =>
        {
            var parameters = method.GetParameters();
            return parameters.Length == 2 &&
                   parameters[0].ParameterType == typeof(bool) &&
                   typeof(IReadOnlySet<string>).IsAssignableFrom(parameters[1].ParameterType);
        });

        if (twoArgMethod is not null)
        {
            if (!string.Equals(promptInjection, "full", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SystemPromptBuilder.buildMemorySection must support memory.promptInjection (full/summary/none).");
            var result = twoArgMethod.Invoke(null, [isMinimal, availableTools]);
            result.ShouldNotBeNull();
            return (IReadOnlyList<string>)result!;
        }

        throw new InvalidOperationException("No supported SystemPromptBuilder.buildMemorySection overload found.");
    }
}
