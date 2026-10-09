using System.Text.Json;
using BotNexus.Agent.Core.ExtensionPoints.ToolExecution;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using Moq;

namespace BotNexus.Agent.Core.Tests.Loop;

public sealed class ToolExecutorRunContextTests
{
    [Theory]
    [InlineData(ToolExecutionMode.Sequential)]
    [InlineData(ToolExecutionMode.Parallel)]
    public async Task Execute_ContextAwareTool_ReceivesConfigRunNotModelArguments(ToolExecutionMode mode)
    {
        var run = AgentRunId.From("admitted-run");
        var tool = new Mock<IContextAwareAgentTool>(MockBehavior.Strict);
        tool.SetupGet(t => t.Name).Returns("consume");
        tool.SetupGet(t => t.Definition).Returns(new Tool("consume", "consume",
            JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone()));
        tool.SetupGet(t => t.ContentSource).Returns(ToolContentSource.Local);
        tool.SetupGet(t => t.DefaultTimeout).Returns((TimeSpan?)null);
        tool.SetupGet(t => t.TimeoutArgument).Returns((ToolTimeoutArgument?)null);
        tool.Setup(t => t.PrepareArgumentsAsync(It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, object?> args, CancellationToken _) => args);
        ToolExecutionContext? observed = null;
        tool.Setup(t => t.ExecuteAsync(It.IsAny<ToolExecutionContext>(), It.IsAny<CancellationToken>(), It.IsAny<AgentToolUpdateCallback?>()))
            .Callback<ToolExecutionContext, CancellationToken, AgentToolUpdateCallback?>((context, _, _) => observed = context)
            .ReturnsAsync(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "retained")]));
        var call = new ToolCallContent("provider-call", "consume", new() { ["agentRunId"] = "untrusted-run" });
        var assistant = new AssistantAgentMessage(string.Empty, ToolCalls: [call]);
        var config = TestHelpers.CreateTestConfig(toolExecutionMode: mode) with { AgentRunId = run };
        var result = await ToolExecutor.ExecuteAsync(new AgentContext(null, [], [tool.Object]), assistant, config,
            _ => Task.CompletedTask, CancellationToken.None);
        result.ShouldHaveSingleItem().IsError.ShouldBeFalse();
        observed.ShouldNotBeNull().AgentRunId.ShouldBe(run);
        observed.ToolCallRequest.Id.ShouldBe("provider-call");
    }
}
