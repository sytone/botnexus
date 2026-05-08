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
        var tool = new SubAgentSpawnTool(new Mock<ISubAgentManager>().Object, "parent-agent", "parent-session");

        tool.Name.ShouldBe("spawn_subagent");
        tool.Label.ShouldBe("Spawn Sub-Agent");
    }

    [Fact]
    public async Task SpawnTool_RequiresTask()
    {
        var tool = new SubAgentSpawnTool(new Mock<ISubAgentManager>().Object, "parent-agent", "parent-session");

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
        var tool = new SubAgentSpawnTool(manager.Object, "parent-agent", "parent-session");

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["task"] = "Investigate issue" });

        captured.ShouldNotBeNull();
        captured!.ParentAgentId.Value.ShouldBe("parent-agent");
        captured.ParentSessionId.Value.ShouldBe("parent-session");
        captured.Task.ShouldBe("Investigate issue");
        captured.ModelOverride.ShouldBeNull();
        captured.ToolIds.ShouldBeNull();
        captured.SystemPromptOverride.ShouldBeNull();
        captured.MaxTurns.ShouldBe(30);
        captured.TimeoutSeconds.ShouldBe(600);
        captured.Archetype.ShouldBe(SubAgentArchetype.General);
    }

    [Fact]
    public async Task SpawnTool_SpawnsSubAgent_WithOverrides()
    {
        SubAgentSpawnRequest? captured = null;
        var manager = new Mock<ISubAgentManager>();
        manager.Setup(m => m.SpawnAsync(It.IsAny<SubAgentSpawnRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SubAgentSpawnRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(CreateSubAgentInfo());
        var tool = new SubAgentSpawnTool(manager.Object, "parent-agent", "parent-session");

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
        captured!.ModelOverride.ShouldBe("gpt-5-mini");
        captured.ToolIds.ShouldBe(new[] { "read", "write" });
        captured.SystemPromptOverride.ShouldBe("Focus on failures");
        captured.MaxTurns.ShouldBe(12);
        captured.TimeoutSeconds.ShouldBe(45);
        captured.Archetype.ShouldBe(SubAgentArchetype.Reviewer);
    }

    [Fact]
    public async Task SpawnTool_ReturnsSubAgentInfo()
    {
        var manager = new Mock<ISubAgentManager>();
        manager.Setup(m => m.SpawnAsync(It.IsAny<SubAgentSpawnRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSubAgentInfo(
                subAgentId: "sub-123",
                childSessionId: "parent-session::subagent::sub-123",
                name: "Research Task"));
        var tool = new SubAgentSpawnTool(manager.Object, "parent-agent", "parent-session");

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?> { ["task"] = "Investigate issue" });
        using var document = JsonDocument.Parse(ReadText(result));

        document.RootElement.GetProperty("subAgentId").GetString().ShouldBe("sub-123");
        document.RootElement.GetProperty("sessionId").GetString().ShouldBe("parent-session::subagent::sub-123");
        document.RootElement.GetProperty("status").GetInt32().ShouldBe((int)SubAgentStatus.Running);
        document.RootElement.GetProperty("name").GetString().ShouldBe("Research Task");
    }

    [Fact]
    public void ListTool_HasCorrectNameAndLabel()
    {
        var tool = new SubAgentListTool(new Mock<ISubAgentManager>().Object, "parent-session");

        tool.Name.ShouldBe("list_subagents");
        tool.Label.ShouldBe("List Sub-Agents");
    }

    [Fact]
    public async Task ListTool_ReturnsEmptyArray_WhenNoSubAgents()
    {
        var manager = new Mock<ISubAgentManager>();
        manager.Setup(m => m.ListAsync("parent-session", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var tool = new SubAgentListTool(manager.Object, "parent-session");

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>());
        using var document = JsonDocument.Parse(ReadText(result));

        document.RootElement.GetProperty("subAgents").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task ListTool_ReturnsSubAgents_ForSession()
    {
        var manager = new Mock<ISubAgentManager>();
        manager.Setup(m => m.ListAsync("parent-session", It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                CreateSubAgentInfo(subAgentId: "sub-1"),
                CreateSubAgentInfo(subAgentId: "sub-2")
            ]);
        var tool = new SubAgentListTool(manager.Object, "parent-session");

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
        var tool = new SubAgentManageTool(new Mock<ISubAgentManager>().Object, "parent-session");

        tool.Name.ShouldBe("manage_subagent");
        tool.Label.ShouldBe("Manage Sub-Agent");
    }

    [Theory]
    [MemberData(nameof(InvalidManageArgs))]
    public async Task ManageTool_RequiresSubAgentIdAndAction(IReadOnlyDictionary<string, object?> args)
    {
        var tool = new SubAgentManageTool(new Mock<ISubAgentManager>().Object, "parent-session");

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
        var tool = new SubAgentManageTool(manager.Object, "parent-session");

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
        manager.Setup(m => m.KillAsync("sub-123", "parent-session", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var tool = new SubAgentManageTool(manager.Object, "parent-session");

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["subAgentId"] = "sub-123",
            ["action"] = "kill"
        });
        using var document = JsonDocument.Parse(ReadText(result));

        manager.Verify(m => m.KillAsync("sub-123", "parent-session", It.IsAny<CancellationToken>()), Times.Once);
        document.RootElement.GetProperty("subAgentId").GetString().ShouldBe("sub-123");
        document.RootElement.GetProperty("killed").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ManageTool_Kill_Returns404_WhenNotFound()
    {
        var manager = new Mock<ISubAgentManager>();
        manager.Setup(m => m.KillAsync("missing-sub-agent", "parent-session", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var tool = new SubAgentManageTool(manager.Object, "parent-session");

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
