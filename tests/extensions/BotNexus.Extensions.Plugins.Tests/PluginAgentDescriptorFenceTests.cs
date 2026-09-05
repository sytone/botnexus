using System.Reflection;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Extensions.Plugins.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>
/// Behaviour tests for the #2685 privilege fence. The fence governs what a plugin-shipped agent
/// descriptor may DECLARE; runtime sandboxing of the resulting agent is explicitly out of scope.
/// </summary>
public sealed class PluginAgentDescriptorFenceTests
{
    private static AgentDescriptor Minimal() => new()
    {
        AgentId = AgentId.From("plugin-agent"),
        DisplayName = "Plugin Agent",
        ModelId = "gpt-5",
        ApiProvider = "github-copilot"
    };

    private static FileAccessPolicy Ceiling() => new()
    {
        AllowedReadPaths = ["/home/user/workspace", "/home/user/docs"],
        AllowedWritePaths = ["/home/user/workspace"],
        DeniedPaths = ["/home/user/workspace/.secrets"]
    };

    // ---- happy path -------------------------------------------------------

    [Fact]
    public void Apply_Accepts_ADescriptorThatDeclaresOnlyPermittedMembers()
    {
        var candidate = Minimal() with
        {
            Emoji = "🔌",
            Description = "A plugin-shipped agent.",
            SystemPrompt = "You are helpful.",
            ToolIds = ["read", "write"],
            Thinking = "medium",
            ContextWindow = 128_000
        };

        var result = PluginAgentDescriptorFence.Apply(candidate, ceiling: null);

        result.IsAccepted.ShouldBeTrue(
            "a descriptor declaring only permitted members must survive the fence. Rejections: "
            + string.Join("; ", result.Rejections));
        result.Descriptor.ShouldNotBeNull();
        result.Descriptor!.ToolIds.ShouldBe(["read", "write"]);
        result.Descriptor.Thinking.ShouldBe("medium");
        result.Descriptor.ContextWindow.ShouldBe(128_000);
    }

    [Fact]
    public void Apply_Accepts_DefaultValuedFencedMembers()
    {
        // A fenced member left at its default is not an escalation - it is the absence of a
        // declaration. Rejecting it would make every plugin agent unloadable.
        var candidate = Minimal() with
        {
            Kind = AgentKind.Named,
            IsolationStrategy = "in-process",
            SessionAccessLevel = "own",
            ConversationAccessLevel = "own"
        };

        var result = PluginAgentDescriptorFence.Apply(candidate, ceiling: null);

        result.IsAccepted.ShouldBeTrue(
            "fenced members at their default value carry no declaration and must not be rejected. "
            + "Rejections: " + string.Join("; ", result.Rejections));
    }

    // ---- clause 2: isolation escalation is REJECTED, naming the field -----

    [Fact]
    public void Apply_Rejects_IsolationStrategyEscalation_NamingTheField()
    {
        var candidate = Minimal() with { IsolationStrategy = "container" };

        var result = PluginAgentDescriptorFence.Apply(candidate, ceiling: null);

        result.IsAccepted.ShouldBeFalse(
            "a plugin agent declaring a non-default isolation strategy is a privilege escalation "
            + "and must be rejected at load (#2685 clause 2).");
        result.Descriptor.ShouldBeNull();
        result.Rejections.ShouldContain(
            r => r.Contains(nameof(AgentDescriptor.IsolationStrategy), StringComparison.Ordinal),
            "the rejection message must NAME the offending field so the plugin author can fix it. "
            + "Actual: " + string.Join("; ", result.Rejections));
    }

    [Fact]
    public void Apply_Rejects_IsolationOptionsEscalation_NamingTheField()
    {
        var candidate = Minimal() with
        {
            IsolationOptions = new Dictionary<string, object?> { ["privileged"] = true }
        };

        var result = PluginAgentDescriptorFence.Apply(candidate, ceiling: null);

        result.IsAccepted.ShouldBeFalse();
        result.Rejections.ShouldContain(
            r => r.Contains(nameof(AgentDescriptor.IsolationOptions), StringComparison.Ordinal),
            "Actual: " + string.Join("; ", result.Rejections));
    }

    [Fact]
    public void Apply_Rejects_SubAgentKind_NamingTheField()
    {
        var candidate = Minimal() with { Kind = AgentKind.SubAgent };

        var result = PluginAgentDescriptorFence.Apply(candidate, ceiling: null);

        result.IsAccepted.ShouldBeFalse();
        result.Rejections.ShouldContain(
            r => r.Contains(nameof(AgentDescriptor.Kind), StringComparison.Ordinal),
            "Actual: " + string.Join("; ", result.Rejections));
    }

    [Fact]
    public void Apply_Rejects_HooksAndMcpServersDeclaredThroughExtensionConfig()
    {
        // Hooks and MCP servers reach the descriptor through the extension bag; the Claude Code
        // constraint adopted by #2685 forbids a plugin agent declaring either.
        var extensions = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["botnexus-mcp"] = System.Text.Json.JsonDocument.Parse("""{"servers":["evil"]}""").RootElement
        };
        var candidate = Minimal() with { ExtensionConfig = extensions };

        var result = PluginAgentDescriptorFence.Apply(candidate, ceiling: null);

        result.IsAccepted.ShouldBeFalse(
            "a plugin agent may not declare hooks or MCP servers via ExtensionConfig (#2685).");
        result.Rejections.ShouldContain(
            r => r.Contains(nameof(AgentDescriptor.ExtensionConfig), StringComparison.Ordinal),
            "Actual: " + string.Join("; ", result.Rejections));
    }

    [Fact]
    public void Apply_Rejects_CustomShellCommand_NamingTheField()
    {
        var candidate = Minimal() with { ShellCommand = ["cmd.exe", "/c"] };

        var result = PluginAgentDescriptorFence.Apply(candidate, ceiling: null);

        result.IsAccepted.ShouldBeFalse();
        result.Rejections.ShouldContain(
            r => r.Contains(nameof(AgentDescriptor.ShellCommand), StringComparison.Ordinal),
            "Actual: " + string.Join("; ", result.Rejections));
    }

    [Fact]
    public void Apply_Rejects_EverySettableFencedMember_WhenSetToANonDefaultValue()
    {
        // Non-vacuity: prove the fence bites on the whole fenced set, not just the one member a
        // single test happens to exercise.
        var fenced = PluginAgentDescriptorFence.FencedMembers;
        fenced.ShouldNotBeEmpty("a fence with no fenced members is vacuous.");

        foreach (var member in fenced)
        {
            var candidate = MutateToNonDefault(Minimal(), member);
            if (candidate is null)
                continue;

            var result = PluginAgentDescriptorFence.Apply(candidate, ceiling: null);
            result.IsAccepted.ShouldBeFalse(
                $"fenced member '{member}' was set to a non-default value and the fence accepted "
                + "it. Every fenced member must be rejected when declared.");
            result.Rejections.ShouldContain(
                r => r.Contains(member, StringComparison.Ordinal),
                $"the rejection for '{member}' must name that field. Actual: "
                + string.Join("; ", result.Rejections));
        }
    }

    // ---- clause 3: fileAccess is NARROWED to the ceiling ------------------

    [Fact]
    public void Apply_Narrows_FileAccessBeyondTheCeiling_ToTheCeiling()
    {
        var candidate = Minimal() with
        {
            FileAccess = new FileAccessPolicy
            {
                AllowedReadPaths = ["/home/user/workspace/sub", "/etc"],
                AllowedWritePaths = ["/home/user/workspace/out", "/"],
                DeniedPaths = []
            }
        };

        var result = PluginAgentDescriptorFence.Apply(candidate, Ceiling());

        result.IsAccepted.ShouldBeTrue(
            "fileAccess beyond the ceiling is NARROWED, not rejected (#2685 clause 3). "
            + "Rejections: " + string.Join("; ", result.Rejections));

        var effective = result.Descriptor!.FileAccess.ShouldNotBeNull();

        effective.AllowedReadPaths.ShouldContain("/home/user/workspace/sub",
            "a declared path inside the ceiling must survive narrowing.");
        effective.AllowedReadPaths.ShouldNotContain("/etc",
            "a declared read path outside the installing user's ceiling must be dropped - that is "
            + "the escalation the fence exists to prevent.");
        effective.AllowedWritePaths.ShouldContain("/home/user/workspace/out");
        effective.AllowedWritePaths.ShouldNotContain("/",
            "declaring the filesystem root must not grant the filesystem root.");
        effective.DeniedPaths.ShouldContain("/home/user/workspace/.secrets",
            "the ceiling's denials always apply; a plugin cannot un-deny a path by omitting it.");
    }

    [Fact]
    public void Apply_Narrows_FileAccessToNothing_WhenTheCeilingIsWorkspaceOnly()
    {
        // A null ceiling means workspace-only access. A plugin agent therefore gets no extra
        // grants at all - not the grants it asked for.
        var candidate = Minimal() with
        {
            FileAccess = new FileAccessPolicy { AllowedReadPaths = ["/etc"], AllowedWritePaths = ["/"] }
        };

        var result = PluginAgentDescriptorFence.Apply(candidate, ceiling: null);

        result.IsAccepted.ShouldBeTrue("Rejections: " + string.Join("; ", result.Rejections));
        var effective = result.Descriptor!.FileAccess;
        (effective is null || (effective.AllowedReadPaths.Count == 0 && effective.AllowedWritePaths.Count == 0))
            .ShouldBeTrue(
                "with no ceiling to narrow to, a plugin-declared fileAccess policy must grant "
                + "nothing. Actual reads: "
                + string.Join(",", effective?.AllowedReadPaths ?? [])
                + " writes: " + string.Join(",", effective?.AllowedWritePaths ?? []));
    }

    [Fact]
    public void Apply_LeavesFileAccessAlone_WhenTheDescriptorDeclaresNone()
    {
        var result = PluginAgentDescriptorFence.Apply(Minimal(), Ceiling());

        result.IsAccepted.ShouldBeTrue();
        result.Descriptor!.FileAccess.ShouldBeNull(
            "a plugin agent that declares no fileAccess must not be handed the ceiling as a grant.");
    }

    [Fact]
    public void Apply_Records_ANarrowingDiagnostic_WhenPathsWereDropped()
    {
        var candidate = Minimal() with
        {
            FileAccess = new FileAccessPolicy { AllowedReadPaths = ["/etc"] }
        };

        var result = PluginAgentDescriptorFence.Apply(candidate, Ceiling());

        result.Narrowings.ShouldNotBeEmpty(
            "narrowing must be observable - silently shrinking a declared policy leaves the plugin "
            + "author with no way to know their declaration did not take effect.");
        result.Narrowings.ShouldContain(
            n => n.Contains(nameof(AgentDescriptor.FileAccess), StringComparison.Ordinal),
            "Actual: " + string.Join("; ", result.Narrowings));
    }

    // ---- structural completeness -----------------------------------------

    [Fact]
    public void Classification_Covers_EverySettableDescriptorMember()
    {
        var settable = typeof(AgentDescriptor)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.SetMethod is not null && p.SetMethod.IsPublic)
            .Select(p => p.Name)
            .ToArray();

        settable.Length.ShouldBeGreaterThan(
            20,
            $"reflection returned {settable.Length} settable members, which is implausibly few - "
            + "the query broke and this test is vacuous.");

        var classified = PluginAgentDescriptorFence.DeclarableMembers
            .Concat(PluginAgentDescriptorFence.FencedMembers)
            .Concat(PluginAgentDescriptorFence.NarrowedMembers)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var member in settable)
        {
            classified.ShouldContain(
                member,
                $"settable member '{member}' has no fence classification. The structural default "
                + "must still FENCE it (see StructurallyFencedByDefault), but it should be "
                + "classified explicitly so the decision is reviewed.");
        }
    }

    [Fact]
    public void UnclassifiedMembers_AreFencedByDefault_NotPermitted()
    {
        // The load-bearing structural property of clause 4: the fence's default is deny.
        foreach (var member in PluginAgentDescriptorFence.FencedMembers)
        {
            PluginAgentDescriptorFence.DeclarableMembers.ShouldNotContain(
                member,
                $"'{member}' is fenced yet reported as declarable - the classifications overlap.");
            PluginAgentDescriptorFence.IsDeclarable(member).ShouldBeFalse(
                $"fenced member '{member}' must not be declarable.");
        }

        PluginAgentDescriptorFence.IsDeclarable("SomeMemberAddedTomorrow").ShouldBeFalse(
            "a member the fence has never heard of must be fenced, not permitted. If this fails, "
            + "the default is permit and a property added to AgentDescriptor tomorrow becomes a "
            + "plugin-declarable privilege surface the moment it exists.");
    }

    private static AgentDescriptor? MutateToNonDefault(AgentDescriptor baseline, string member) =>
        member switch
        {
            nameof(AgentDescriptor.Kind) => baseline with { Kind = AgentKind.SubAgent },
            nameof(AgentDescriptor.IsolationStrategy) => baseline with { IsolationStrategy = "container" },
            nameof(AgentDescriptor.IsolationOptions) => baseline with
            {
                IsolationOptions = new Dictionary<string, object?> { ["privileged"] = true }
            },
            nameof(AgentDescriptor.ExtensionConfig) => baseline with
            {
                ExtensionConfig = new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["x"] = System.Text.Json.JsonDocument.Parse("{}").RootElement
                }
            },
            nameof(AgentDescriptor.ShellCommand) => baseline with { ShellCommand = ["sh"] },
            nameof(AgentDescriptor.SubAgentIds) => baseline with { SubAgentIds = ["other"] },
            nameof(AgentDescriptor.SubAgentRoles) => baseline with { SubAgentRoles = ["admin"] },
            nameof(AgentDescriptor.SessionAccessLevel) => baseline with { SessionAccessLevel = "all" },
            nameof(AgentDescriptor.SessionAllowedAgents) => baseline with { SessionAllowedAgents = ["other"] },
            nameof(AgentDescriptor.ConversationAccessLevel) => baseline with { ConversationAccessLevel = "all" },
            nameof(AgentDescriptor.ConversationAllowedAgents) => baseline with
            {
                ConversationAllowedAgents = ["other"]
            },
            // A newly fenced member with no mutation recipe here is skipped rather than silently
            // asserted - the architecture fence is what guarantees it is fenced at all.
            _ => null
        };
}
