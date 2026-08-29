using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Tools;
using Moq;

namespace BotNexus.Gateway.Tests.Tools;

public sealed class SubAgentToolTests
{
    [Fact]
    public void SpawnTool_HasCorrectNameAndLabel()
    {
        var tool = new SubAgentSpawnTool(new Mock<ISubAgentManager>().Object, AgentId.From("parent-agent"), SessionId.From("parent-session"), ConversationId.From("conv-1"));

        tool.Name.ShouldBe("spawn_subagent");
        tool.Label.ShouldBe("Spawn Sub-Agent");
    }

    [Fact]
    public async Task SpawnTool_RequiresTask()
    {
        var tool = new SubAgentSpawnTool(new Mock<ISubAgentManager>().Object, AgentId.From("parent-agent"), SessionId.From("parent-session"), ConversationId.From("conv-1"));

        Func<Task> act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>());

        (await act.ShouldThrowAsync<ArgumentException>())
            .Message.ShouldContain("task");
    }

    [Fact]
    public async Task SpawnTool_SpawnsSubAgent_WithDefaults()
    {
        SubAgentSpawnRequest? captured = null;
        var manager = new Mock<ISubAgentManager>();
        manager.Setup(m => m.SpawnAsync(It.IsAny<SubAgentSpawnRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SubAgentSpawnRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(CreateSubAgentInfo());
        var tool = new SubAgentSpawnTool(manager.Object, AgentId.From("parent-agent"), SessionId.From("parent-session"), ConversationId.From("conv-parent"));

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["task"] = "Investigate issue" });

        captured.ShouldNotBeNull();
        captured!.ParentAgentId.Value.ShouldBe("parent-agent");
        captured.ParentSessionId.Value.ShouldBe("parent-session");
        captured.Task.ShouldBe("Investigate issue");
        captured.MaxTurns.ShouldBe(30);
        captured.TimeoutSeconds.ShouldBe(600);
        captured.InheritedConversationId.Value.ShouldBe("conv-parent");
        var embody = captured.Mode.ShouldBeOfType<Embody>();
        embody.Role.ShouldBe(SubAgentArchetype.General);
        embody.Customizations.ModelOverride.ShouldBeNull();
        embody.Customizations.ToolIds.ShouldBeNull();
        embody.Customizations.SystemPromptOverride.ShouldBeNull();
    }

    [Fact]
    public async Task SpawnTool_SpawnsSubAgent_WithOverrides()
    {
        SubAgentSpawnRequest? captured = null;
        var manager = new Mock<ISubAgentManager>();
        manager.Setup(m => m.SpawnAsync(It.IsAny<SubAgentSpawnRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SubAgentSpawnRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(CreateSubAgentInfo());
        var tool = new SubAgentSpawnTool(manager.Object, AgentId.From("parent-agent"), SessionId.From("parent-session"), ConversationId.From("conv-1"));

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["task"] = "Investigate issue",
            ["model"] = "gpt-5-mini",
            ["tools"] = new[] { "read", "write" },
            ["systemPrompt"] = "Focus on failures",
            ["maxTurns"] = 12,
            ["timeoutSeconds"] = 45,
            ["archetype"] = "reviewer"
        });

        captured.ShouldNotBeNull();
        captured!.MaxTurns.ShouldBe(12);
        captured.TimeoutSeconds.ShouldBe(45);
        var embody = captured.Mode.ShouldBeOfType<Embody>();
        embody.Role.ShouldBe(SubAgentArchetype.Reviewer);
        embody.Customizations.ModelOverride.ShouldBe("gpt-5-mini");
        embody.Customizations.ToolIds.ShouldBe(new[] { "read", "write" });
        embody.Customizations.SystemPromptOverride.ShouldBe("Focus on failures");
    }

    [Fact]
    public async Task SpawnTool_ReturnsSubAgentInfo()
    {
        var manager = new Mock<ISubAgentManager>();
        manager.Setup(m => m.SpawnAsync(It.IsAny<SubAgentSpawnRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSubAgentInfo(
                subAgentId: "sub-123",
                childSessionId: SessionId.From("parent-session::subagent::sub-123"),
                name: "Research Task"));
        var tool = new SubAgentSpawnTool(manager.Object, AgentId.From("parent-agent"), SessionId.From("parent-session"), ConversationId.From("conv-1"));

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["task"] = "Investigate issue" });
        using var document = JsonDocument.Parse(ReadText(result));

        document.RootElement.GetProperty("subAgentId").GetString().ShouldBe("sub-123");
        document.RootElement.GetProperty("sessionId").GetString().ShouldBe("parent-session::subagent::sub-123");
        document.RootElement.GetProperty("status").GetInt32().ShouldBe((int)SubAgentStatus.Running);
        document.RootElement.GetProperty("name").GetString().ShouldBe("Research Task");
    }

    // ---------------------------------------------------------------------
    // Phase 5 / F-6 step 3 (#562): Mode = Embody | Mirror.
    // The tool translates the flat JSON shape (preserved for the agent-facing
    // contract) into the closed Mode union, and rejects mode-mixing.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task SpawnTool_BuildsMode_AsEmbodyGeneral_WhenNoCustomisations()
    {
        var captured = await CaptureSpawnRequest(new Dictionary<string, object?> { ["task"] = "T" });

        var embody = captured.Mode.ShouldBeOfType<Embody>();
        embody.Role.ShouldBe(SubAgentArchetype.General);
        embody.Customizations.ShouldBeSameAs(EmbodyCustomizations.Default);
    }

    [Fact]
    public async Task SpawnTool_BuildsMode_AsEmbodyWithArchetype_WhenArchetypeOnlySupplied()
    {
        var captured = await CaptureSpawnRequest(new Dictionary<string, object?>
        {
            ["task"] = "T",
            ["archetype"] = "reviewer"
        });

        var embody = captured.Mode.ShouldBeOfType<Embody>();
        embody.Role.ShouldBe(SubAgentArchetype.Reviewer);
        embody.Customizations.ShouldBeSameAs(EmbodyCustomizations.Default);
    }

    [Fact]
    public async Task SpawnTool_BuildsMode_AsEmbodyWithCustomisations_WhenAnyOverrideSupplied()
    {
        var captured = await CaptureSpawnRequest(new Dictionary<string, object?>
        {
            ["task"] = "T",
            ["archetype"] = "coder",
            ["name"] = "my-coder",
            ["model"] = "gpt-5-mini",
            ["apiProvider"] = "openai",
            ["tools"] = new[] { "read", "write" },
            ["systemPrompt"] = "Focus on tests"
        });

        var embody = captured.Mode.ShouldBeOfType<Embody>();
        embody.Role.ShouldBe(SubAgentArchetype.Coder);
        embody.Customizations.Name.ShouldBe("my-coder");
        embody.Customizations.ModelOverride.ShouldBe("gpt-5-mini");
        embody.Customizations.ApiProviderOverride.ShouldBe("openai");
        embody.Customizations.ToolIds.ShouldBe(new[] { "read", "write" });
        embody.Customizations.SystemPromptOverride.ShouldBe("Focus on tests");
    }

    [Fact]
    public async Task SpawnTool_BuildsMode_AsMirror_WhenTargetAgentIdOnlySupplied()
    {
        var captured = await CaptureSpawnRequest(new Dictionary<string, object?>
        {
            ["task"] = "T",
            ["targetAgentId"] = "alex"
        });

        var mirror = captured.Mode.ShouldBeOfType<Mirror>();
        mirror.TargetAgentId.Value.ShouldBe("alex");
        mirror.RunName.ShouldBeNull(
            "targetAgentId alone must not synthesise a run label (#3570 clause 4 - unchanged behaviour).");
    }

    /// <summary>
    /// #3570 clause 1 + 2: `name` labels the RUN, not the descriptor, so it must be
    /// accepted alongside `targetAgentId` and carried through to the Mirror mode as the
    /// run label. Previously this pair was refused outright, failing an automated
    /// PR-review workflow on 100% of its invocations.
    /// </summary>
    [Fact]
    public async Task SpawnTool_BuildsMode_AsMirror_CarryingRunName_WhenTargetAgentIdAndNameSupplied()
    {
        var captured = await CaptureSpawnRequest(new Dictionary<string, object?>
        {
            ["task"] = "T",
            ["targetAgentId"] = "alex",
            ["name"] = "pr-review-run"
        });

        var mirror = captured.Mode.ShouldBeOfType<Mirror>();
        mirror.TargetAgentId.Value.ShouldBe("alex",
            "the target's descriptor must still be mirrored verbatim - a run label changes nothing about identity.");
        // Non-vacuity: the SUPPLIED name, not merely non-null.
        mirror.RunName.ShouldBe("pr-review-run");
    }

    /// <summary>
    /// #3570 clause 2: a whitespace-only name is not a label. It must normalise to null
    /// rather than titling the run with blanks.
    /// </summary>
    [Fact]
    public async Task SpawnTool_MirrorRunName_IsNull_WhenNameIsWhitespace()
    {
        var captured = await CaptureSpawnRequest(new Dictionary<string, object?>
        {
            ["task"] = "T",
            ["targetAgentId"] = "alex",
            ["name"] = "   "
        });

        captured.Mode.ShouldBeOfType<Mirror>().RunName.ShouldBeNull();
    }

    [Theory]
    [InlineData("model", "gpt-5-mini")]
    [InlineData("apiProvider", "openai")]
    [InlineData("systemPrompt", "Custom")]
    [InlineData("archetype", "coder")]
    public async Task SpawnTool_RejectsMixing_TargetAgentId_WithSingleEmbodyField(string conflictKey, object conflictValue)
    {
        var args = new Dictionary<string, object?>
        {
            ["task"] = "T",
            ["targetAgentId"] = "alex",
            [conflictKey] = conflictValue
        };
        var tool = CreateSpawnTool(out _);

        var ex = await Should.ThrowAsync<ArgumentException>(
            () => tool.ExecuteAsync("call-1", args));

        ex.Message.ShouldContain("targetAgentId");
        ex.Message.ShouldContain(conflictKey);
    }

    [Fact]
    public async Task SpawnTool_RejectsMixing_TargetAgentId_WithToolsArray()
    {
        var args = new Dictionary<string, object?>
        {
            ["task"] = "T",
            ["targetAgentId"] = "alex",
            ["tools"] = new[] { "read" }
        };
        var tool = CreateSpawnTool(out _);

        var ex = await Should.ThrowAsync<ArgumentException>(
            () => tool.ExecuteAsync("call-1", args));

        ex.Message.ShouldContain("tools");
    }

    [Fact]
    public async Task SpawnTool_RejectsMixing_ReportsAllConflictingFields_InOneMessage()
    {
        var args = new Dictionary<string, object?>
        {
            ["task"] = "T",
            ["targetAgentId"] = "alex",
            ["model"] = "y",
            ["systemPrompt"] = "z"
        };
        var tool = CreateSpawnTool(out _);

        var ex = await Should.ThrowAsync<ArgumentException>(
            () => tool.ExecuteAsync("call-1", args));

        ex.Message.ShouldContain("model");
        ex.Message.ShouldContain("systemPrompt");
    }

    /// <summary>
    /// #3570 clause 3: the refusal must enumerate only the descriptor fields the caller
    /// actually supplied. A run label supplied alongside them is accepted, so it must not
    /// appear in the conflict list, and neither may a descriptor field that was never sent.
    /// </summary>
    [Fact]
    public async Task SpawnTool_RejectsMixing_NamesOnlyTheFieldsActuallySupplied()
    {
        var args = new Dictionary<string, object?>
        {
            ["task"] = "T",
            ["targetAgentId"] = "alex",
            ["name"] = "pr-review-run",
            ["model"] = "gpt-5-mini"
        };
        var tool = CreateSpawnTool(out _);

        var ex = await Should.ThrowAsync<ArgumentException>(
            () => tool.ExecuteAsync("call-1", args));

        // Parse the enumerated conflict list out of the message so the assertion is exact
        // rather than a substring smell test: "...embody-only fields: a, b. Mirror mode..."
        var marker = "embody-only fields: ";
        var start = ex.Message.IndexOf(marker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, "the refusal must enumerate the conflicting fields.");
        var listStart = start + marker.Length;
        var listEnd = ex.Message.IndexOf('.', listStart);
        listEnd.ShouldBeGreaterThan(listStart);
        var reported = ex.Message[listStart..listEnd]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        reported.ShouldBe(new[] { "model" },
            "only the descriptor fields actually supplied may be named. 'name' is a run label " +
            "and is accepted; apiProvider/tools/systemPrompt/archetype were never supplied.");
    }

    private static SubAgentSpawnTool CreateSpawnTool(out Mock<ISubAgentManager> manager)
    {
        manager = new Mock<ISubAgentManager>();
        manager.Setup(m => m.SpawnAsync(It.IsAny<SubAgentSpawnRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSubAgentInfo());
        return new SubAgentSpawnTool(
            manager.Object,
            AgentId.From("parent-agent"),
            SessionId.From("parent-session"),
            ConversationId.From("conv-1"));
    }

    private static async Task<SubAgentSpawnRequest> CaptureSpawnRequest(IReadOnlyDictionary<string, object?> args)
    {
        SubAgentSpawnRequest? captured = null;
        var manager = new Mock<ISubAgentManager>();
        manager.Setup(m => m.SpawnAsync(It.IsAny<SubAgentSpawnRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SubAgentSpawnRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(CreateSubAgentInfo());
        var tool = new SubAgentSpawnTool(
            manager.Object,
            AgentId.From("parent-agent"),
            SessionId.From("parent-session"),
            ConversationId.From("conv-1"));

        await tool.ExecuteAsync("call-1", args);

        captured.ShouldNotBeNull();
        captured!.Mode.ShouldNotBeNull();
        return captured;
    }

    [Fact]
    public void ListTool_HasCorrectNameAndLabel()
    {
        var tool = new SubAgentListTool(new Mock<ISubAgentManager>().Object, SessionId.From("parent-session"));

        tool.Name.ShouldBe("list_subagents");
        tool.Label.ShouldBe("List Sub-Agents");
    }

    [Fact]
    public async Task ListTool_ReturnsEmptyArray_WhenNoSubAgents()
    {
        var manager = new Mock<ISubAgentManager>();
        manager.Setup(m => m.ListAsync(SessionId.From("parent-session"), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var tool = new SubAgentListTool(manager.Object, SessionId.From("parent-session"));

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>());
        using var document = JsonDocument.Parse(ReadText(result));

        document.RootElement.GetProperty("subAgents").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task ListTool_ReturnsSubAgents_ForSession()
    {
        var manager = new Mock<ISubAgentManager>();
        manager.Setup(m => m.ListAsync(SessionId.From("parent-session"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                CreateSubAgentInfo(subAgentId: "sub-1"),
                CreateSubAgentInfo(subAgentId: "sub-2")
            ]);
        var tool = new SubAgentListTool(manager.Object, SessionId.From("parent-session"));

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>());
        using var document = JsonDocument.Parse(ReadText(result));
        var subAgents = document.RootElement.GetProperty("subAgents");

        subAgents.GetArrayLength().ShouldBe(2);
        subAgents[0].GetProperty("subAgentId").GetString().ShouldBe("sub-1");
        subAgents[1].GetProperty("subAgentId").GetString().ShouldBe("sub-2");
    }

    [Fact]
    public void ManageTool_HasCorrectNameAndLabel()
    {
        var tool = new SubAgentManageTool(new Mock<ISubAgentManager>().Object, SessionId.From("parent-session"));

        tool.Name.ShouldBe("manage_subagent");
        tool.Label.ShouldBe("Manage Sub-Agent");
    }

    [Theory]
    [MemberData(nameof(InvalidManageArgs))]
    public async Task ManageTool_RequiresSubAgentIdAndAction(IReadOnlyDictionary<string, object?> args)
    {
        var tool = new SubAgentManageTool(new Mock<ISubAgentManager>().Object, SessionId.From("parent-session"));

        Func<Task> act = () => tool.PrepareArgumentsAsync(args);

        await act.ShouldThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ManageTool_Status_ReturnsSubAgentInfo()
    {
        var manager = new Mock<ISubAgentManager>();
        manager.Setup(m => m.GetAsync("sub-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSubAgentInfo(
                subAgentId: "sub-123",
                status: SubAgentStatus.Completed,
                resultSummary: "Done"));
        var tool = new SubAgentManageTool(manager.Object, SessionId.From("parent-session"));

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["subAgentId"] = "sub-123",
            ["action"] = "status"
        });
        using var document = JsonDocument.Parse(ReadText(result));

        document.RootElement.GetProperty("subAgentId").GetString().ShouldBe("sub-123");
        document.RootElement.GetProperty("status").GetInt32().ShouldBe((int)SubAgentStatus.Completed);
        document.RootElement.GetProperty("resultSummary").GetString().ShouldBe("Done");
    }

    [Fact]
    public async Task ManageTool_Kill_CallsKillAsync()
    {
        var manager = new Mock<ISubAgentManager>();
        manager.Setup(m => m.KillAsync("sub-123", SessionId.From("parent-session"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var tool = new SubAgentManageTool(manager.Object, SessionId.From("parent-session"));

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["subAgentId"] = "sub-123",
            ["action"] = "kill"
        });
        using var document = JsonDocument.Parse(ReadText(result));

        manager.Verify(m => m.KillAsync("sub-123", SessionId.From("parent-session"), It.IsAny<CancellationToken>()), Times.Once);
        document.RootElement.GetProperty("subAgentId").GetString().ShouldBe("sub-123");
        document.RootElement.GetProperty("killed").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ManageTool_Kill_Returns404_WhenNotFound()
    {
        var manager = new Mock<ISubAgentManager>();
        manager.Setup(m => m.KillAsync("missing-sub-agent", SessionId.From("parent-session"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var tool = new SubAgentManageTool(manager.Object, SessionId.From("parent-session"));

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["subAgentId"] = "missing-sub-agent",
            ["action"] = "kill"
        });
        using var document = JsonDocument.Parse(ReadText(result));

        document.RootElement.GetProperty("subAgentId").GetString().ShouldBe("missing-sub-agent");
        document.RootElement.GetProperty("killed").GetBoolean().ShouldBeFalse();
    }

    public static IEnumerable<object[]> InvalidManageArgs()
    {
        yield return
        [
            new Dictionary<string, object?>
            {
                ["action"] = "status"
            }
        ];
        yield return
        [
            new Dictionary<string, object?>
            {
                ["subAgentId"] = "sub-1"
            }
        ];
        yield return
        [
            new Dictionary<string, object?>
            {
                ["subAgentId"] = "sub-1",
                ["action"] = "invalid"
            }
        ];
    }

    private static string ReadText(AgentToolResult result)
        => result.Content.Single(c => c.Type == AgentToolContentType.Text).Value;

    private static SubAgentInfo CreateSubAgentInfo(
        string? subAgentId = null,
        BotNexus.Domain.Primitives.SessionId? childSessionId = null,
        string? name = null,
        SubAgentStatus status = SubAgentStatus.Running,
        string? resultSummary = null)
        => new()
        {
            SubAgentId = subAgentId ?? "sub-default",
            ParentSessionId = BotNexus.Domain.Primitives.SessionId.From("parent-session"),
            ChildSessionId = childSessionId ?? BotNexus.Domain.Primitives.SessionId.From("parent-session::subagent::sub-default"),
            Name = name,
            Task = "Investigate issue",
            Model = "gpt-5-mini",
            Status = status,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = status == SubAgentStatus.Running ? null : DateTimeOffset.UtcNow,
            ResultSummary = resultSummary
        };
}
