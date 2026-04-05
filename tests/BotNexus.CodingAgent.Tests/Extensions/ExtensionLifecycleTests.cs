using BotNexus.AgentCore.Hooks;
using BotNexus.AgentCore.Tools;
using BotNexus.AgentCore.Types;
using BotNexus.CodingAgent.Extensions;
using BotNexus.CodingAgent.Session;
using BotNexus.Providers.Core.Models;
using FluentAssertions;
using Moq;

namespace BotNexus.CodingAgent.Tests.Extensions;

public sealed class ExtensionLifecycleTests
{
    [Fact]
    public void LoadExtensions_WhenAssemblyContainsExtension_DiscoversExtension()
    {
        var loader = new ExtensionLoader();

        var result = loader.LoadExtensions(AppContext.BaseDirectory);

        result.Extensions.Should().Contain(item => item.Name == DiscoverableExtension.NameValue);
    }

    [Fact]
    public async Task OnToolCallAsync_WhenInvoked_DispatchesToAllExtensions()
    {
        var calls = 0;
        var first = new Mock<IExtension>();
        first.SetupGet(item => item.Name).Returns("first");
        first.Setup(item => item.GetTools()).Returns([]);
        first.Setup(item => item.OnToolCallAsync(It.IsAny<ToolCallLifecycleContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls++)
            .Returns(ValueTask.FromResult<BeforeToolCallResult?>(null));

        var second = new Mock<IExtension>();
        second.SetupGet(item => item.Name).Returns("second");
        second.Setup(item => item.GetTools()).Returns([]);
        second.Setup(item => item.OnToolCallAsync(It.IsAny<ToolCallLifecycleContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls++)
            .Returns(ValueTask.FromResult<BeforeToolCallResult?>(null));

        var runner = new ExtensionRunner([first.Object, second.Object]);
        await runner.OnToolCallAsync(new ToolCallLifecycleContext(
            ToolCallLifecycleStage.BeforeExecution,
            "call-1",
            "read",
            new Dictionary<string, object?>()));

        calls.Should().Be(2);
    }

    [Fact]
    public async Task OnToolCallAsync_WhenExtensionBlocks_ReturnsBlockingResult()
    {
        var blockResult = new BeforeToolCallResult(Block: true, Reason: "blocked");
        var first = new Mock<IExtension>();
        first.SetupGet(item => item.Name).Returns("first");
        first.Setup(item => item.GetTools()).Returns([]);
        first.Setup(item => item.OnToolCallAsync(It.IsAny<ToolCallLifecycleContext>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult<BeforeToolCallResult?>(blockResult));

        var second = new Mock<IExtension>();
        second.SetupGet(item => item.Name).Returns("second");
        second.Setup(item => item.GetTools()).Returns([]);

        var runner = new ExtensionRunner([first.Object, second.Object]);
        var result = await runner.OnToolCallAsync(new ToolCallLifecycleContext(
            ToolCallLifecycleStage.BeforeExecution,
            "call-1",
            "read",
            new Dictionary<string, object?>()));

        result.Should().Be(blockResult);
    }

    [Fact]
    public async Task OnToolResultAsync_WhenMultipleExtensionsRespond_AppliesLatestOverride()
    {
        var first = new Mock<IExtension>();
        first.SetupGet(item => item.Name).Returns("first");
        first.Setup(item => item.GetTools()).Returns([]);
        first.Setup(item => item.OnToolResultAsync(It.IsAny<ToolResultLifecycleContext>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult<AfterToolCallResult?>(new AfterToolCallResult(Details: "first")));

        var second = new Mock<IExtension>();
        second.SetupGet(item => item.Name).Returns("second");
        second.Setup(item => item.GetTools()).Returns([]);
        second.Setup(item => item.OnToolResultAsync(It.IsAny<ToolResultLifecycleContext>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult<AfterToolCallResult?>(new AfterToolCallResult(Details: "second")));

        var runner = new ExtensionRunner([first.Object, second.Object]);
        var result = await runner.OnToolResultAsync(new ToolResultLifecycleContext(
            ToolCallId: "call-1",
            ToolName: "read",
            Arguments: new Dictionary<string, object?>(),
            Result: new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "done")]),
            IsError: false));

        result!.Details.Should().Be("second");
    }

    [Fact]
    public async Task SessionLifecycleEvents_WhenInvoked_FireInOrder()
    {
        var order = new List<string>();
        var first = new Mock<IExtension>();
        first.SetupGet(item => item.Name).Returns("first");
        first.Setup(item => item.GetTools()).Returns([]);
        first.Setup(item => item.OnSessionStartAsync(It.IsAny<SessionLifecycleContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("start"))
            .Returns(ValueTask.CompletedTask);
        first.Setup(item => item.OnSessionEndAsync(It.IsAny<SessionLifecycleContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("end"))
            .Returns(ValueTask.CompletedTask);

        var runner = new ExtensionRunner([first.Object]);
        var context = new SessionLifecycleContext(
            new SessionInfo("session", "Session", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, "model", @"Q:\repos\botnexus"),
            @"Q:\repos\botnexus",
            "model");

        await runner.OnSessionStartAsync(context);
        await runner.OnSessionEndAsync(context);

        order.Should().ContainInOrder("start", "end");
    }

    [Fact]
    public async Task OnToolCallAsync_WhenExtensionThrows_DoesNotCrashRunner()
    {
        var throwing = new Mock<IExtension>();
        throwing.SetupGet(item => item.Name).Returns("throwing");
        throwing.Setup(item => item.GetTools()).Returns([]);
        throwing.Setup(item => item.OnToolCallAsync(It.IsAny<ToolCallLifecycleContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var runner = new ExtensionRunner([throwing.Object]);
        var act = () => runner.OnToolCallAsync(new ToolCallLifecycleContext(
            ToolCallLifecycleStage.BeforeExecution,
            "call-1",
            "read",
            new Dictionary<string, object?>()));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task OnModelRequestAsync_WhenExtensionsOverridePayload_AppliesOverridesSequentially()
    {
        var first = new Mock<IExtension>();
        first.SetupGet(item => item.Name).Returns("first");
        first.Setup(item => item.GetTools()).Returns([]);
        first.Setup(item => item.OnModelRequestAsync(It.IsAny<ModelRequestLifecycleContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ModelRequestLifecycleContext context, CancellationToken _) =>
            {
                var payload = (Dictionary<string, object?>)context.Payload;
                payload["first"] = true;
                return payload;
            });

        var second = new Mock<IExtension>();
        second.SetupGet(item => item.Name).Returns("second");
        second.Setup(item => item.GetTools()).Returns([]);
        second.Setup(item => item.OnModelRequestAsync(It.IsAny<ModelRequestLifecycleContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ModelRequestLifecycleContext context, CancellationToken _) =>
            {
                var payload = (Dictionary<string, object?>)context.Payload;
                payload["second"] = true;
                return payload;
            });

        var runner = new ExtensionRunner([first.Object, second.Object]);
        var payload = new Dictionary<string, object?>();
        var result = await runner.OnModelRequestAsync(payload, MakeModel());

        ((Dictionary<string, object?>)result).Should().ContainKey("second");
    }

    private static LlmModel MakeModel() => new(
        Id: "model",
        Name: "Model",
        Api: "test-api",
        Provider: "test-provider",
        BaseUrl: "https://example.com",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 8192,
        MaxTokens: 1024);

    public sealed class DiscoverableExtension : IExtension
    {
        public const string NameValue = "discoverable-wave-one-extension";
        public string Name => NameValue;

        public IReadOnlyList<IAgentTool> GetTools() => [];
    }
}
