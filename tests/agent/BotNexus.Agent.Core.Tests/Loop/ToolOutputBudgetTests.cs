using System.Text;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests.Loop;

/// <summary>
/// Covers the shared central tool-output budget (#3162): the backstop that bounds every tool result
/// regardless of origin, beneath the five existing per-tool caps.
/// </summary>
public class ToolOutputBudgetTests
{
    /// <summary>
    /// AC1: the budget is applied in <see cref="ToolExecutor"/> to a tool that has no cap of its own.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ToolExceedsBudget_ResultIsBounded()
    {
        const int budget = 1024;
        var oversize = new string('a', budget * 4);
        var tool = new FixedOutputTool("bulk", oversize);
        var context = new AgentContext(null, [], [tool]);
        var assistant = CreateAssistantMessage("t1", "bulk");
        var config = TestHelpers.CreateTestConfig() with { MaxToolOutputBytes = budget };

        var results = await ToolExecutor.ExecuteAsync(context, assistant, config, _ => Task.CompletedTask, CancellationToken.None);

        var text = JoinText(results.Single().Result);
        Encoding.UTF8.GetByteCount(text).ShouldBeLessThan(oversize.Length);
        text.ShouldNotContain(oversize);
    }

    /// <summary>
    /// AC2: an oversize result is a SUCCESS carrying a marker, the omitted byte count and the one
    /// shared narrowing-guidance line. Never an error, never a silent drop.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ToolExceedsBudget_ReturnsSuccessWithMarkerCountAndGuidance()
    {
        const int budget = 512;
        var oversize = new string('b', 4096);
        var tool = new FixedOutputTool("bulk", oversize);
        var context = new AgentContext(null, [], [tool]);
        var assistant = CreateAssistantMessage("t1", "bulk");
        var config = TestHelpers.CreateTestConfig() with { MaxToolOutputBytes = budget };

        var results = await ToolExecutor.ExecuteAsync(context, assistant, config, _ => Task.CompletedTask, CancellationToken.None);

        var message = results.Single();
        message.IsError.ShouldBeFalse();

        var text = JoinText(message.Result);
        text.ShouldContain("[tool output truncated:");
        text.ShouldContain($"{4096 - budget} bytes omitted");
        text.ShouldContain(ToolOutputBudget.NarrowingGuidance);

        // Not a silent drop: the retained prefix is still there.
        text.ShouldContain(new string('b', budget));
    }

    /// <summary>
    /// AC3: the cut lands on a UTF-8 rune boundary, so multi-byte CJK content is never sliced into
    /// U+FFFD replacement characters.
    /// </summary>
    [Fact]
    public void Apply_CjkContentCutMidCharacter_IntroducesNoReplacementCharacters()
    {
        // Each CJK ideograph is 3 UTF-8 bytes; a 10-byte budget cannot land on a boundary naturally.
        var content = string.Concat(Enumerable.Repeat("\u4f60\u597d\u4e16\u754c", 64));
        var result = new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, content)]);

        var bounded = ToolOutputBudget.Apply(result, 10);

        var text = JoinText(bounded);
        text.ShouldNotContain("\uFFFD");
        // 10 bytes fits exactly three 3-byte runes.
        text.ShouldStartWith("\u4f60\u597d\u4e16");
        text.ShouldNotStartWith("\u4f60\u597d\u4e16\u754c");
    }

    /// <summary>
    /// AC3: the same guarantee for astral-plane emoji, which are surrogate PAIRS in UTF-16 and
    /// 4 bytes in UTF-8 — the shape most likely to be split by a naive byte cut.
    /// </summary>
    [Fact]
    public void Apply_EmojiContentCutMidCharacter_IntroducesNoLoneSurrogates()
    {
        var content = string.Concat(Enumerable.Repeat("\U0001F52C", 128)); // microscope, 4 UTF-8 bytes
        var result = new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, content)]);

        var bounded = ToolOutputBudget.Apply(result, 10);

        var text = JoinText(bounded);
        text.ShouldNotContain("\uFFFD");
        foreach (var ch in text)
        {
            char.IsLowSurrogate(ch).ShouldBe(false, "a lone low surrogate means a 4-byte rune was split");
        }
        // 10 bytes fits exactly two 4-byte runes; the third does not fit.
        text.ShouldStartWith("\U0001F52C\U0001F52C");
        text.ShouldNotStartWith("\U0001F52C\U0001F52C\U0001F52C");
    }

    /// <summary>
    /// AC5: a non-positive budget disables the backstop entirely, matching the existing
    /// <c>ToolResultPersistenceConfig</c> convention.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Apply_NonPositiveBudget_ReturnsResultUnchanged(int budget)
    {
        var content = new string('c', 100_000);
        var result = new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, content)]);

        var bounded = ToolOutputBudget.Apply(result, budget);

        bounded.ShouldBeSameAs(result);
        JoinText(bounded).ShouldBe(content);
    }

    /// <summary>
    /// A result already within budget is returned verbatim — no marker, no reallocation. This is
    /// what makes the backstop additive (AC6): every existing per-tool cap is smaller than the
    /// default budget, so a self-bounded tool never trips it.
    /// </summary>
    [Fact]
    public void Apply_ResultWithinBudget_ReturnsSameInstance()
    {
        var result = new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "small")]);

        ToolOutputBudget.Apply(result, ToolOutputBudget.DefaultMaxBytes).ShouldBeSameAs(result);
    }

    /// <summary>
    /// AC6 guard: the default budget must stay above the largest first-party per-tool cap
    /// (<c>ExecTool</c>'s 100 KiB), otherwise this backstop would silently RETUNE the per-tool caps
    /// it is supposed to sit beneath, which the issue explicitly places out of scope.
    /// </summary>
    [Fact]
    public void DefaultMaxBytes_ExceedsLargestPerToolCap()
    {
        ToolOutputBudget.DefaultMaxBytes.ShouldBeGreaterThan(100 * 1024);
    }

    /// <summary>
    /// A null configured budget resolves to the documented platform default rather than to
    /// "unbounded" — "not configured" must never mean "unprotected".
    /// </summary>
    [Fact]
    public void EffectiveMaxToolOutputBytes_NullConfig_UsesDefault()
    {
        TestHelpers.CreateTestConfig().EffectiveMaxToolOutputBytes.ShouldBe(ToolOutputBudget.DefaultMaxBytes);
    }

    /// <summary>
    /// An explicitly configured non-positive budget is preserved verbatim so an operator can turn
    /// the backstop off; silently restoring the default would defeat that choice.
    /// </summary>
    [Fact]
    public void EffectiveMaxToolOutputBytes_ZeroConfig_IsPreserved()
    {
        (TestHelpers.CreateTestConfig() with { MaxToolOutputBytes = 0 }).EffectiveMaxToolOutputBytes.ShouldBe(0);
    }

    /// <summary>
    /// An image block is an opaque encoded payload; cutting its bytes would produce a broken image
    /// rather than a smaller one, so it passes through untouched while text is still bounded.
    /// </summary>
    [Fact]
    public void Apply_ImageBlocks_ArePassedThroughUntouched()
    {
        const string image = "data:image/png;base64,AAAA";
        var result = new AgentToolResult(
        [
            new AgentToolContent(AgentToolContentType.Image, image),
            new AgentToolContent(AgentToolContentType.Text, new string('d', 4096))
        ]);

        var bounded = ToolOutputBudget.Apply(result, 256);

        bounded.Content.ShouldContain(block => block.Type == AgentToolContentType.Image && block.Value == image);
        JoinText(bounded).ShouldContain(ToolOutputBudget.NarrowingGuidance);
    }

    private static string JoinText(AgentToolResult result)
        => string.Concat(result.Content.Where(block => block.Type == AgentToolContentType.Text).Select(block => block.Value));

    private static AssistantAgentMessage CreateAssistantMessage(string callId, string toolName)
        => new(
            Content: string.Empty,
            ToolCalls: [new ToolCallContent(callId, toolName, new Dictionary<string, object?>(StringComparer.Ordinal))]);

    /// <summary>A tool with no cap of its own — the stand-in for every MCP or third-party tool.</summary>
    private sealed class FixedOutputTool(string name, string output) : IAgentTool
    {
        public string Name => name;

        public string Label => name;

        public Tool Definition => new(
            name,
            "returns a fixed payload",
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
            => Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, output)]));
    }
}
