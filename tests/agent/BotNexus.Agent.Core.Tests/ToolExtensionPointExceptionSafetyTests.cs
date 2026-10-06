using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Types;

namespace BotNexus.Agent.Core.Tests;

public sealed class ToolExtensionPointExceptionSafetyTests
{
    private const string HookApi = "hook-safety-api";

    [Fact]
    public async Task PromptAsync_WhenToolExecutionPolicyThrows_BlocksToolCallGracefully()
    {
        using var provider = RegisterToolCallThenStopProvider();
        var initialState = new AgentInitialState(
            SystemPrompt: null,
            Model: TestHelpers.CreateTestModel(HookApi),
            Tools: [new CalculateTool()],
            Messages: []);
        var options = TestHelpers.CreateTestOptions(initialState, model: TestHelpers.CreateTestModel(HookApi))
            with
            {
                ToolExecutionPolicy = (_, _) => throw new InvalidOperationException("before hook exploded")
            };
        var agent = new BotNexus.Agent.Core.Agent(options);

        var result = await agent.PromptAsync("calculate 1+1");

        var toolResult = result.OfType<ToolResultAgentMessage>().ShouldHaveSingleItem();
        toolResult.IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task PromptAsync_WhenToolResultTransformerThrows_ReturnsOriginalToolResult()
    {
        using var provider = RegisterToolCallThenStopProvider();
        var initialState = new AgentInitialState(
            SystemPrompt: null,
            Model: TestHelpers.CreateTestModel(HookApi),
            Tools: [new CalculateTool()],
            Messages: []);
        var options = TestHelpers.CreateTestOptions(initialState, model: TestHelpers.CreateTestModel(HookApi))
            with
            {
                ToolResultTransformer = (_, _) => throw new InvalidOperationException("after hook exploded")
            };
        var agent = new BotNexus.Agent.Core.Agent(options);

        var result = await agent.PromptAsync("calculate 1+1");

        var toolResult = result.OfType<ToolResultAgentMessage>().ShouldHaveSingleItem();
        toolResult.IsError.ShouldBeFalse();
        toolResult.Result.Content[0].Value.ShouldBe("2");
    }

    private static IDisposable RegisterToolCallThenStopProvider()
    {
        var provider = new TestApiProvider(
            HookApi,
            simpleStreamFactory: (_, context, _) =>
            {
                var hasToolResult = context.Messages.OfType<BotNexus.Agent.Providers.Core.Models.ToolResultMessage>().Any();
                if (hasToolResult)
                {
                    return TestStreamFactory.CreateTextResponse("done");
                }

                return TestStreamFactory.CreateToolCallResponse(("call-1", "calculate", new Dictionary<string, object?> { ["expression"] = "1+1" }));
            });

        return TestHelpers.RegisterProvider(provider);
    }
}