using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

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
}
