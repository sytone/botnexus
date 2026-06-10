// Updated CanvasToolTests.cs - tests with conversationId
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Tools;
using Moq;
namespace BotNexus.Gateway.Tests.Tools;
public sealed class CanvasToolTests
{
    [Fact]
    public void Tool_HasExpectedNameAndLabel()
    {
        var tool = new CanvasTool(AgentId.From("agent-a"), null, null);
        tool.Name.ShouldBe("canvas");
        tool.Label.ShouldBe("Canvas");
    }

    [Fact]
    public async Task ExecuteAsync_Render_PublishesHtmlWithConversationId()
    {
        var notifier = new Mock<IAgentCanvasNotifier>();
        var tool = new CanvasTool(AgentId.From("agent-a"), ConversationId.From("conv-1"), canvasNotifiers: [notifier.Object]);
        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "render",
            ["html"] = "<h1>Hello</h1>",
        });
        notifier.Verify(value => value.NotifyCanvasUpdatedAsync(
                "agent-a",
                "conv-1",
                "<h1>Hello</h1>",
                It.IsAny<CancellationToken>()),
            Times.Once);
        ReadText(result).ShouldContain("Canvas rendered");
    }

    [Fact]
    public async Task ExecuteAsync_Render_NullConversationId_PassesEmptyString()
    {
        var notifier = new Mock<IAgentCanvasNotifier>();
        var tool = new CanvasTool(AgentId.From("agent-a"), null, canvasNotifiers: [notifier.Object]);
        await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "render",
            ["html"] = "<h1>Hello</h1>",
        });
        notifier.Verify(value => value.NotifyCanvasUpdatedAsync(
                "agent-a",
                "",
                "<h1>Hello</h1>",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_Clear_PublishesEmptyHtmlWithConversationId()
    {
        var notifier = new Mock<IAgentCanvasNotifier>();
        var tool = new CanvasTool(AgentId.From("agent-a"), ConversationId.From("conv-1"), canvasNotifiers: [notifier.Object]);
        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "clear"
        });
        notifier.Verify(value => value.NotifyCanvasUpdatedAsync(
                "agent-a",
                "conv-1",
                string.Empty,
                It.IsAny<CancellationToken>()),
            Times.Once);
        ReadText(result).ShouldContain("Canvas cleared");
    }

    [Fact]
    public async Task PrepareArgumentsAsync_RenderRequiresHtml()
    {
        var tool = new CanvasTool(AgentId.From("agent-a"), null, null);
        Func<Task> action = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["action"] = "render"
        });
        await action.ShouldThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task PrepareArgumentsAsync_RejectsInvalidAction()
    {
        var tool = new CanvasTool(AgentId.From("agent-a"), null, null);
        Func<Task> action = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["action"] = "paint"
        });
        await action.ShouldThrowAsync<ArgumentException>();
    }

    private static async Task<AgentToolResult> ExecuteAsync(
        IAgentTool tool,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken cancellationToken = default)
    {
        var prepared = await tool.PrepareArgumentsAsync(args, cancellationToken);
        return await tool.ExecuteAsync("call-canvas-test", prepared, cancellationToken);
    }

    private static string ReadText(AgentToolResult result)
        => result.Content.Single(c => c.Type == AgentToolContentType.Text).Value;
}
