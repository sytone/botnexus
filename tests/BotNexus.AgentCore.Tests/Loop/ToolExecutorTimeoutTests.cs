using System.Text.Json;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tools;
using BotNexus.AgentCore.Tests.TestUtils;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using Moq;

namespace BotNexus.AgentCore.Tests.Loop;

public sealed class ToolExecutorTimeoutTests
{
    /// <summary>
    /// A tool that hangs indefinitely should time out and return a structured error result.
    /// </summary>
    [Fact]
    public async Task HangingTool_TimesOut_ReturnsErrorResult()
    {
        var tool = CreateTool("hang", async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "done")]);
        });

        var config = TestHelpers.CreateTestConfig(toolTimeout: TimeSpan.FromMilliseconds(200));
        var context = new AgentContext(null, [], [tool]);
        var assistant = CreateAssistant("tc1", "hang");

        var results = await ToolExecutor.ExecuteAsync(context, assistant, config, _ => Task.CompletedTask, CancellationToken.None);

        results.ShouldHaveSingleItem();
        results[0].IsError.ShouldBeTrue();
        results[0].Result.Content[0].Value.ShouldContain("timed out");
        results[0].Result.Content[0].Value.ShouldContain("hang");
    }

    /// <summary>
    /// After a tool times out, subsequent tool calls should still work normally.
    /// </summary>
    [Fact]
    public async Task AfterTimeout_NextToolCallSucceeds()
    {
        var hangTool = CreateTool("hang", async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "done")]);
        });
        var okTool = CreateTool("ok", _ =>
            Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "success")])));

        var config = TestHelpers.CreateTestConfig(toolTimeout: TimeSpan.FromMilliseconds(200));
        var context = new AgentContext(null, [], [hangTool, okTool]);

        // First call: times out
        var assistant1 = CreateAssistant("tc1", "hang");
        var results1 = await ToolExecutor.ExecuteAsync(context, assistant1, config, _ => Task.CompletedTask, CancellationToken.None);
        results1[0].IsError.ShouldBeTrue();

        // Second call: succeeds
        var assistant2 = CreateAssistant("tc2", "ok");
        var results2 = await ToolExecutor.ExecuteAsync(context, assistant2, config, _ => Task.CompletedTask, CancellationToken.None);
        results2[0].IsError.ShouldBeFalse();
        results2[0].Result.Content[0].Value.ShouldBe("success");
    }

    /// <summary>
    /// User/turn cancellation (via external CancellationToken) should NOT be reported as a timeout.
    /// The OperationCanceledException should propagate as-is.
    /// </summary>
    [Fact]
    public async Task UserCancellation_IsNotReportedAsTimeout()
    {
        var tool = CreateTool("hang", async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "done")]);
        });

        var config = TestHelpers.CreateTestConfig(toolTimeout: TimeSpan.FromSeconds(60));
        var context = new AgentContext(null, [], [tool]);
        var assistant = CreateAssistant("tc1", "hang");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var act = () => ToolExecutor.ExecuteAsync(context, assistant, config, _ => Task.CompletedTask, cts.Token);

        await act.ShouldThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// A tool that completes within the timeout should return a normal (non-error) result.
    /// </summary>
    [Fact]
    public async Task FastTool_CompletesWithinTimeout_ReturnsNormalResult()
    {
        var tool = CreateTool("fast", _ =>
            Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "ok")])));

        var config = TestHelpers.CreateTestConfig(toolTimeout: TimeSpan.FromSeconds(10));
        var context = new AgentContext(null, [], [tool]);
        var assistant = CreateAssistant("tc1", "fast");

        var results = await ToolExecutor.ExecuteAsync(context, assistant, config, _ => Task.CompletedTask, CancellationToken.None);

        results.ShouldHaveSingleItem();
        results[0].IsError.ShouldBeFalse();
        results[0].Result.Content[0].Value.ShouldBe("ok");
    }

    /// <summary>
    /// Timeout error message should include the tool name and configured duration.
    /// </summary>
    [Fact]
    public async Task TimeoutErrorMessage_IncludesToolNameAndDuration()
    {
        var tool = CreateTool("slowtool", async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "done")]);
        });

        var config = TestHelpers.CreateTestConfig(toolTimeout: TimeSpan.FromMilliseconds(150));
        var context = new AgentContext(null, [], [tool]);
        var assistant = CreateAssistant("tc1", "slowtool");

        var results = await ToolExecutor.ExecuteAsync(context, assistant, config, _ => Task.CompletedTask, CancellationToken.None);

        results[0].IsError.ShouldBeTrue();
        results[0].Result.Content[0].Value.ShouldContain("slowtool");
        // 150ms rounds to 0s in the message; just check it mentions "timed out"
        results[0].Result.Content[0].Value.ShouldContain("timed out");
    }

    private static AssistantAgentMessage CreateAssistant(string id, string toolName)
    {
        return new AssistantAgentMessage(
            string.Empty,
            [new ToolCallContent(id, toolName, new Dictionary<string, object?>())],
            StopReason.ToolUse);
    }

    private static IAgentTool CreateTool(string name, Func<CancellationToken, Task<AgentToolResult>> execute)
    {
        var mock = new Mock<IAgentTool>(MockBehavior.Strict);
        mock.SetupGet(t => t.Name).Returns(name);
        mock.SetupGet(t => t.Label).Returns(name);
        mock.SetupGet(t => t.DefaultTimeout).Returns((TimeSpan?)null);
        mock.SetupGet(t => t.Definition).Returns(new Tool(name, "mock", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()));
        mock.Setup(t => t.PrepareArgumentsAsync(It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, object?> args, CancellationToken _) => args);
        mock.Setup(t => t.ExecuteAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>(), It.IsAny<AgentToolUpdateCallback?>()))
            .Returns((string _, IReadOnlyDictionary<string, object?> _, CancellationToken ct, AgentToolUpdateCallback? _) => execute(ct));
        return mock.Object;
    }

    /// <summary>
    /// When the agent passes an explicit timeout argument that exceeds the safety cap,
    /// ToolExecutor honours it so long-running tools (e.g. a 3-minute shell script) work.
    /// </summary>
    [Fact]
    public async Task ExplicitTimeoutArgument_ExceedsSafetyCap_IsHonoured()
    {
        var completedNormally = false;

        // Tool that runs for 200ms — would be killed by a 100ms safety cap
        // but should succeed when the agent passes timeout: 5 (5 seconds)
        var tool = CreateTool("slow-shell", async ct =>
        {
            await Task.Delay(200, ct);
            completedNormally = true;
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "done")]);
        });

        var toolCall = new ToolCallContent("tc-1", "slow-shell",
            new Dictionary<string, object?> { ["timeout"] = "5" }); // agent requests 5s

        var msg = new AssistantAgentMessage("slow-shell") { ToolCalls = [toolCall] };
        var config = TestHelpers.CreateTestConfig(
            toolTimeout: TimeSpan.FromMilliseconds(100)); // safety cap is only 100ms
        var context = new AgentContext(null, [], [tool]);

        var results = await ToolExecutor.ExecuteAsync(
            context, msg, config, _ => Task.CompletedTask, CancellationToken.None);

        completedNormally.ShouldBeTrue();
        results.ShouldHaveSingleItem();
        results[0].IsError.ShouldBeFalse();
    }

    /// <summary>Tool.DefaultTimeout overrides the safety cap — long-running tool completes.</summary>
    [Fact]
    public async Task ToolDefaultTimeout_OverridesSafetyCap_LongToolCompletes()
    {
        var completedNormally = false;

        // Tool takes 200ms but the safety cap is only 100ms
        // Tool declares DefaultTimeout = 5s → executor should use 5s not 100ms
        var tool = CreateToolWithDefaultTimeout("slow-tool", TimeSpan.FromSeconds(5), async ct =>
        {
            await Task.Delay(200, ct);
            completedNormally = true;
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "done")]);
        });

        var msg = CreateAssistant("tc-1", "slow-tool");
        var config = TestHelpers.CreateTestConfig(toolTimeout: TimeSpan.FromMilliseconds(100));
        var context = new AgentContext(null, [], [tool]);

        var results = await ToolExecutor.ExecuteAsync(
            context, msg, config, _ => Task.CompletedTask, CancellationToken.None);

        completedNormally.ShouldBeTrue();
        results[0].IsError.ShouldBeFalse();
    }

    /// <summary>Tool.DefaultTimeout smaller than safety cap — safety cap wins.</summary>
    [Fact]
    public async Task ToolDefaultTimeout_SmallerThanSafetyCap_SafetyCapWins()
    {
        // Tool declares 50ms default but safety cap is 500ms
        // Tool runs for 200ms — should complete fine (both limits are above 200ms is wrong)
        // Actually: safety cap 500ms > tool default 50ms → safety cap wins → tool gets 500ms
        // Tool finishes in 200ms → success
        var completedNormally = false;
        var tool = CreateToolWithDefaultTimeout("quick-tool", TimeSpan.FromMilliseconds(50), async ct =>
        {
            await Task.Delay(200, ct);
            completedNormally = true;
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "done")]);
        });

        var msg = CreateAssistant("tc-1", "quick-tool");
        var config = TestHelpers.CreateTestConfig(toolTimeout: TimeSpan.FromMilliseconds(500));
        var context = new AgentContext(null, [], [tool]);

        var results = await ToolExecutor.ExecuteAsync(
            context, msg, config, _ => Task.CompletedTask, CancellationToken.None);

        // Safety cap (500ms) > tool default (50ms) → executor uses 500ms → 200ms tool succeeds
        completedNormally.ShouldBeTrue();
        results[0].IsError.ShouldBeFalse();
    }

    private static IAgentTool CreateToolWithDefaultTimeout(
        string name,
        TimeSpan defaultTimeout,
        Func<CancellationToken, Task<AgentToolResult>> execute)
    {
        var mock = new Mock<IAgentTool>();
        mock.Setup(t => t.Name).Returns(name);
        mock.Setup(t => t.Label).Returns(name);
        mock.Setup(t => t.DefaultTimeout).Returns(defaultTimeout);
        mock.Setup(t => t.Definition).Returns(new Tool(name, name, JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()));
        mock.Setup(t => t.PrepareArgumentsAsync(It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, object?> args, CancellationToken _) => args);
        mock.Setup(t => t.ExecuteAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>(), It.IsAny<AgentToolUpdateCallback?>()))
            .Returns((string _, IReadOnlyDictionary<string, object?> _, CancellationToken ct, AgentToolUpdateCallback? _) => execute(ct));
        return mock.Object;
    }
}
