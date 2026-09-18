using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Hooks;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests.Loop;

public class ToolResultSanitizerTests
{
    [Theory]
    [InlineData(ToolExecutionMode.Sequential)]
    [InlineData(ToolExecutionMode.Parallel)]
    public async Task ExecuteAsync_SanitizesToolTextBeforeModelVisibleResult(ToolExecutionMode mode)
    {
        const string secret = "github_pat_11AA22BB33CC44DD55EE66FF77GG88HH99II00JJ";
        var tool = new FixedResultTool("generic", new AgentToolResult(
        [
            new AgentToolContent(AgentToolContentType.Text, "prefix github_pat_11AA22BB33CC44"),
            new AgentToolContent(AgentToolContentType.Text, "DD55EE66FF77GG88HH99II00JJ suffix"),
            new AgentToolContent(AgentToolContentType.Image, "data:image/png;base64,AAAA")
        ]));
        var context = new AgentContext(null, [], [tool]);
        var assistant = CreateAssistantMessage("t1", "generic");
        var config = TestHelpers.CreateTestConfig(toolExecutionMode: mode) with
        {
            SanitizeToolResultText = RedactSyntheticSecret
        };

        var result = (await ToolExecutor.ExecuteAsync(
            context,
            assistant,
            config,
            _ => Task.CompletedTask,
            CancellationToken.None)).Single();

        var text = string.Concat(result.Result.Content
            .Where(block => block.Type == AgentToolContentType.Text)
            .Select(block => block.Value));
        text.ShouldNotContain(secret);
        text.ShouldContain("prefix [REDACTED] suffix");
        result.Result.Content.ShouldContain(block =>
            block.Type == AgentToolContentType.Image &&
            block.Value == "data:image/png;base64,AAAA");
        result.ToolCallId.ShouldBe("t1");
        result.ToolName.ShouldBe("generic");
        result.IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_SanitizesErrorAndAfterHookReplacement()
    {
        const string secret = "github_pat_11AA22BB33CC44DD55EE66FF77GG88HH99II00JJ";
        var tool = new FixedResultTool("generic", new AgentToolResult(
            [new AgentToolContent(AgentToolContentType.Text, "original")]));
        var context = new AgentContext(null, [], [tool]);
        var assistant = CreateAssistantMessage("t1", "generic");
        var config = TestHelpers.CreateTestConfig(
            afterToolCall: (_, _) => Task.FromResult<AfterToolCallResult?>(new AfterToolCallResult(
                [new AgentToolContent(AgentToolContentType.Text, $"replacement {secret}")],
                IsError: true))) with
        {
            SanitizeToolResultText = RedactSyntheticSecret
        };

        var result = (await ToolExecutor.ExecuteAsync(
            context,
            assistant,
            config,
            _ => Task.CompletedTask,
            CancellationToken.None)).Single();

        result.IsError.ShouldBeTrue();
        result.Result.Content.Single().Value.ShouldBe("replacement [REDACTED]");
    }

    [Fact]
    public void Apply_BeforeBudget_StoresOnlySanitizedContinuationText()
    {
        const string secret = "github_pat_11AA22BB33CC44DD55EE66FF77GG88HH99II00JJ";
        var store = new ToolOutputContinuationStore();
        var raw = new AgentToolResult(
        [
            new AgentToolContent(AgentToolContentType.Text, new string('a', 32) + "github_pat_11AA22BB33CC44"),
            new AgentToolContent(AgentToolContentType.Text, "DD55EE66FF77GG88HH99II00JJ" + new string('z', 32))
        ]);

        var sanitized = ToolResultSanitizer.Apply(raw, RedactSyntheticSecret);
        var bounded = ToolOutputBudget.Apply(sanitized, 16, store);
        var rendered = string.Concat(bounded.Content
            .Where(block => block.Type == AgentToolContentType.Text)
            .Select(block => block.Value));
        var handle = System.Text.RegularExpressions.Regex.Match(rendered, "handle=\"(?<handle>toc_[a-f0-9]+)\"")
            .Groups["handle"].Value;
        var continuation = store.Read(handle, 0, maxBytes: 0);

        continuation.Status.ShouldBe(ToolOutputContinuationStatus.Ok);
        continuation.Text.ShouldNotContain(secret);
        continuation.Text.ShouldContain("[REDACTED]");
    }

    [Fact]
    public void Apply_PreservesNonsecretTextUnicodeImagesAndLoginInstructions()
    {
        var original = new AgentToolResult(
        [
            new AgentToolContent(AgentToolContentType.Text, "ordinary source: api_key_name; visit https://example.com/device and enter code ABCD-EFGH — 你好"),
            new AgentToolContent(AgentToolContentType.Image, "data:image/png;base64,AAAA")
        ], Details: new { retained = true });

        var sanitized = ToolResultSanitizer.Apply(original, RedactSyntheticSecret);

        sanitized.Content[0].Value.ShouldBe(original.Content[0].Value);
        sanitized.Content[1].ShouldBe(original.Content[1]);
        sanitized.Details.ShouldBeSameAs(original.Details);
    }

    private static string RedactSyntheticSecret(string text)
        => System.Text.RegularExpressions.Regex.Replace(
            text,
            "github_pat_[A-Za-z0-9_]{40}",
            "[REDACTED]");

    private static AssistantAgentMessage CreateAssistantMessage(string callId, string toolName)
        => new(
            Content: string.Empty,
            ToolCalls: [new ToolCallContent(callId, toolName, new Dictionary<string, object?>(StringComparer.Ordinal))]);

    private sealed class FixedResultTool(string name, AgentToolResult result) : IAgentTool
    {
        public string Name => name;
        public string Label => name;
        public Tool Definition => new(
            name,
            "returns a fixed result",
            System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }));

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default)
            => Task.FromResult(arguments);

        public Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default,
            AgentToolUpdateCallback? onUpdate = null)
            => Task.FromResult(result);
    }
}
