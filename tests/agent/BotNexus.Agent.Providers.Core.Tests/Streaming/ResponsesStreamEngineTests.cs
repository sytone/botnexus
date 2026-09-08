using System.Text;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Agent.Providers.Core.Tests.Streaming;

/// <summary>
/// Unit coverage for the shared Responses engine's terminal emit shapes that both the OpenAI and
/// Copilot Responses providers collapse onto (step 6/6 of #1377). The full request loop is exercised
/// by the provider-level <c>CopilotResponsesProviderParityTests</c> (byte-identical wire-contract
/// proof); these tests pin the error/abort emit shapes directly.
/// </summary>
public class ResponsesStreamEngineTests
{
    private static LlmModel Model() => new(
        Id: "gpt-5",
        Name: "GPT-5",
        Api: "openai-responses",
        Provider: "openai",
        BaseUrl: "https://api.openai.com",
        Reasoning: true,
        Input: ["text"],
        Cost: new ModelCost(1.0m, 2.0m, 0.5m, 1.5m),
        ContextWindow: 200000,
        MaxTokens: 16384);

    [Fact]
    public async Task EmitError_PushesErrorEventWithEmptyContentAndMessage()
    {
        var stream = new LlmStream();
        ResponsesStreamEngine.EmitError(stream, "openai-responses", Model(), "boom");

        var result = await stream.GetResultAsync();
        result.StopReason.ShouldBe(StopReason.Error);
        result.ErrorMessage.ShouldBe("boom");
        result.Api.ShouldBe("openai-responses");
        result.Content.ShouldBeEmpty();
    }

    [Fact]
    public async Task EmitError_WithPartialContent_CarriesItForward()
    {
        var stream = new LlmStream();
        ResponsesStreamEngine.EmitError(
            stream, "github-copilot-responses", Model(), "boom", [new TextContent("partial")]);

        var result = await stream.GetResultAsync();
        result.Content.Count.ShouldBe(1);
        result.Content[0].ShouldBeOfType<TextContent>().Text.ShouldBe("partial");
        result.Api.ShouldBe("github-copilot-responses");
    }

    [Fact]
    public async Task EmitAborted_PushesDoneEventWithCancelledMessage()
    {
        var stream = new LlmStream();
        ResponsesStreamEngine.EmitAborted(stream, "openai-responses", Model());

        var result = await stream.GetResultAsync();
        result.StopReason.ShouldBe(StopReason.Aborted);
        result.ErrorMessage.ShouldBe("Request was cancelled");
        result.Content.ShouldBeEmpty();
    }

    /// <summary>
    /// Drives real SSE bytes whose terminal <c>response.completed</c> carries a terminal status,
    /// so the in-band parser path is exercised end to end rather than the mapping helper alone.
    /// </summary>
    private static async Task<AssistantMessage> ParseTerminalStatusAsync(string status)
    {
        var sse = new StringBuilder()
            .Append("event: response.created\ndata: {\"response\":{\"id\":\"resp_1\"}}\n\n")
            .Append("event: response.output_text.delta\n")
            .Append("data: {\"item_id\":\"item_1\",\"delta\":\"partial\"}\n\n")
            .Append("event: response.completed\n")
            .Append($"data: {{\"response\":{{\"id\":\"resp_1\",\"status\":\"{status}\"}}}}\n\n")
            .ToString();

        var stream = new LlmStream();
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(sse)));

        await ResponsesStreamParser.ParseAsync(
            stream,
            reader,
            Model(),
            options: null,
            api: "openai-responses",
            logger: NullLogger.Instance,
            emitError: (_, _, _, _) => { },
            onParsedEvent: null,
            resolveConfiguredServiceTier: null,
            ct: CancellationToken.None);

        return await stream.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// AC3 of #3294: a terminal <c>status: "cancelled"</c> must reach the assembled
    /// <see cref="AssistantMessage"/> as <see cref="StopReason.Aborted"/>, matching the out-of-band
    /// <c>EmitAborted</c> path above. Without this the same user cancellation normalised two ways.
    /// </summary>
    [Fact]
    public async Task ParseAsync_TerminalCancelledStatus_YieldsAbortedStopReason()
    {
        var result = await ParseTerminalStatusAsync("cancelled");

        result.StopReason.ShouldBe(StopReason.Aborted);
        result.StopReason.ShouldNotBe(StopReason.Error);
        // The partial content produced before cancellation is still carried forward.
        result.Content.OfType<TextContent>().Select(t => t.Text).ShouldContain("partial");
    }

    /// <summary>
    /// Sad-path counterpart to the above: a terminal <c>status: "failed"</c> is a provider failure
    /// and must remain <see cref="StopReason.Error"/>, proving the fix narrowed only the cancelled arm.
    /// </summary>
    [Fact]
    public async Task ParseAsync_TerminalFailedStatus_StillYieldsErrorStopReason()
    {
        var result = await ParseTerminalStatusAsync("failed");

        result.StopReason.ShouldBe(StopReason.Error);
    }
}
