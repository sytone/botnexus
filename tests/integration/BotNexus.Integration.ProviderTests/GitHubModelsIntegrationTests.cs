using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Agent.Providers.GitHubModels;
using BotNexus.Agent.Providers.OpenAICompat;

namespace BotNexus.Integration.ProviderTests;

/// <summary>
/// Integration tests for the GitHub Models provider.
/// These tests make real HTTP calls to https://models.inference.ai.azure.com
/// and require a valid GITHUB_TOKEN environment variable.
/// <para>
/// Tests are skipped gracefully when:
/// - GITHUB_TOKEN is not present (local dev runs)
/// - The external API is degraded (returning empty responses / HTTP errors)
/// Rate limiting: tests use Task.Delay between calls to stay under 15 RPM.
/// </para>
/// </summary>
[Trait("Category", "ProviderIntegration")]
[Trait("Provider", "github-models")]
[Collection("GitHubModels")]
public sealed class GitHubModelsIntegrationTests : IAsyncLifetime
{
    private const string ApiDegradedReason =
        "GitHub Models API is degraded (returning empty responses). Skipping integration test.";

    private readonly HttpClient _httpClient = new();
    private OpenAICompatProvider _provider = null!;
    private LlmModel _model = null!;
    private bool _apiAvailable;
    private static readonly long Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public async Task InitializeAsync()
    {
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrEmpty(token))
            return;

        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        _provider = new OpenAICompatProvider(_httpClient);

        var registry = new ModelRegistry();
        GitHubModelsProvider.RegisterModels(registry);
        _model = registry.GetModel("github-models", "gpt-4o-mini")!;

        // Probe the API with a minimal request to detect outages early.
        // If the probe fails or returns empty, all tests will skip gracefully.
        _apiAvailable = await ProbeApiAvailabilityAsync();
    }

    public Task DisposeAsync()
    {
        _httpClient.Dispose();
        return Task.CompletedTask;
    }

    [RequiresGitHubTokenFact]
    public async Task BasicCompletion_ReturnsNonEmptyResponse()
    {
        // Arrange
        Skip.If(!_apiAvailable, ApiDegradedReason);

        var context = new Context(
            SystemPrompt: "You are a helpful assistant. Reply in one sentence.",
            Messages: [new UserMessage(new UserMessageContent("What is 2+2?"), Ts)]);

        // Act
        var result = await CollectStreamAsync(_provider.Stream(_model, context));

        // Assert
        SkipIfDegraded(result);
        result.Text.ShouldNotBeNullOrWhiteSpace();
        result.StopReason.ShouldBe(StopReason.Stop);
    }

    [RequiresGitHubTokenFact]
    public async Task ToolCall_RoundTrip_ReturnsToolUseAndFinalResponse()
    {
        // Arrange: give the model a tool and ask it something that requires the tool
        Skip.If(!_apiAvailable, ApiDegradedReason);
        await Task.Delay(4000); // rate limit spacing

        var weatherTool = new Tool(
            Name: "get_weather",
            Description: "Get the current weather for a location",
            Parameters: System.Text.Json.JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "location": { "type": "string", "description": "City name" }
                },
                "required": ["location"]
            }
            """).RootElement);

        var context = new Context(
            SystemPrompt: "You are a weather assistant. Use the get_weather tool to answer weather questions.",
            Messages: [new UserMessage(new UserMessageContent("What's the weather in Seattle?"), Ts)],
            Tools: [weatherTool]);

        // Act: first call should produce a tool call
        var result = await CollectStreamAsync(_provider.Stream(_model, context));

        // Assert
        SkipIfDegraded(result);
        result.ToolCalls.ShouldNotBeEmpty("Model should call the get_weather tool");
        var toolCall = result.ToolCalls[0];
        toolCall.Name.ShouldBe("get_weather");
        toolCall.Arguments.ShouldContainKey("location");
    }

    [RequiresGitHubTokenFact]
    public async Task Streaming_EmitsTextDeltas()
    {
        // Arrange
        Skip.If(!_apiAvailable, ApiDegradedReason);
        await Task.Delay(4000); // rate limit spacing

        var context = new Context(
            SystemPrompt: "You are helpful. Reply in exactly 3 sentences.",
            Messages: [new UserMessage(new UserMessageContent("Tell me about the moon."), Ts)]);

        // Act
        var stream = _provider.Stream(_model, context);
        var deltaCount = 0;
        string? finalText = null;

        await foreach (var evt in stream)
        {
            switch (evt)
            {
                case TextDeltaEvent delta:
                    deltaCount++;
                    break;
                case DoneEvent done:
                    finalText = done.Message.Content
                        .OfType<TextContent>()
                        .Select(t => t.Text)
                        .FirstOrDefault();
                    break;
            }
        }

        // Assert
        Skip.If(deltaCount == 0 && string.IsNullOrEmpty(finalText), ApiDegradedReason);
        deltaCount.ShouldBeGreaterThan(1, "Streaming should emit multiple text deltas");
        finalText.ShouldNotBeNullOrWhiteSpace();
    }

    [RequiresGitHubTokenFact]
    public async Task SystemPrompt_IsRespected()
    {
        // Arrange: use a very specific system prompt constraint
        Skip.If(!_apiAvailable, ApiDegradedReason);
        await Task.Delay(4000); // rate limit spacing

        var context = new Context(
            SystemPrompt: "You must always respond with exactly the word 'PINEAPPLE' and nothing else.",
            Messages: [new UserMessage(new UserMessageContent("What is your favorite food?"), Ts)]);

        // Act
        var result = await CollectStreamAsync(_provider.Stream(_model, context));

        // Assert
        SkipIfDegraded(result);
        result.Text.ToUpperInvariant().ShouldContain("PINEAPPLE");
    }

    [RequiresGitHubTokenFact]
    public async Task MultiTurn_MaintainsContext()
    {
        // Arrange: multi-turn conversation where second message references first
        Skip.If(!_apiAvailable, ApiDegradedReason);
        await Task.Delay(4000); // rate limit spacing

        var messages = new Message[]
        {
            new UserMessage(new UserMessageContent("My name is Farnsworth."), Ts),
            new AssistantMessage(
                Content: [new TextContent("Nice to meet you, Farnsworth!")],
                Api: "openai-compat",
                Provider: "github-models",
                ModelId: "gpt-4o-mini",
                Usage: Usage.Empty(),
                StopReason: StopReason.Stop,
                ErrorMessage: null,
                ResponseId: "resp_1",
                Timestamp: Ts),
            new UserMessage(new UserMessageContent("What is my name?"), Ts + 1)
        };

        var context = new Context(
            SystemPrompt: "You are a helpful assistant. Always answer accurately.",
            Messages: messages);

        // Act
        var result = await CollectStreamAsync(_provider.Stream(_model, context));

        // Assert
        SkipIfDegraded(result);
        result.Text.ShouldContain("Farnsworth", Case.Insensitive);
    }

    [RequiresGitHubTokenFact]
    public async Task AgentLoop_SmokeTool_CompletesCycle()
    {
        // Arrange: simulate one agent loop iteration with tool call + result
        Skip.If(!_apiAvailable, ApiDegradedReason);
        await Task.Delay(4000); // rate limit spacing

        var calculatorTool = new Tool(
            Name: "calculator",
            Description: "Performs arithmetic. Parameters: expression (string)",
            Parameters: System.Text.Json.JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "expression": { "type": "string", "description": "Math expression" }
                },
                "required": ["expression"]
            }
            """).RootElement);

        // Turn 1: model calls tool
        var context1 = new Context(
            SystemPrompt: "You are a math assistant. Use the calculator tool for all math.",
            Messages: [new UserMessage(new UserMessageContent("What is 7 * 8?"), Ts)],
            Tools: [calculatorTool]);

        var turn1 = await CollectStreamAsync(_provider.Stream(_model, context1));

        SkipIfDegraded(turn1);

        if (turn1.ToolCalls.Count == 0)
        {
            // Model answered directly without tool — acceptable for simple math
            turn1.Text.ShouldContain("56");
            return;
        }

        // Turn 2: provide tool result and get final answer
        var messages2 = new Message[]
        {
            new UserMessage(new UserMessageContent("What is 7 * 8?"), Ts),
            new AssistantMessage(
                Content: [new ToolCallContent(turn1.ToolCalls[0].Id, "calculator",
                    turn1.ToolCalls[0].Arguments)],
                Api: "openai-compat",
                Provider: "github-models",
                ModelId: "gpt-4o-mini",
                Usage: Usage.Empty(),
                StopReason: StopReason.ToolUse,
                ErrorMessage: null,
                ResponseId: "resp_t1",
                Timestamp: Ts),
            new ToolResultMessage(
                ToolCallId: turn1.ToolCalls[0].Id,
                ToolName: "calculator",
                Content: [new TextContent("56")],
                IsError: false,
                Timestamp: Ts + 1)
        };

        var context2 = new Context(
            SystemPrompt: "You are a math assistant. Use the calculator tool for all math.",
            Messages: messages2,
            Tools: [calculatorTool]);

        var turn2 = await CollectStreamAsync(_provider.Stream(_model, context2));

        // Assert
        SkipIfDegraded(turn2);
        turn2.Text.ShouldContain("56");
        turn2.StopReason.ShouldBe(StopReason.Stop);
    }

    /// <summary>
    /// Probes the GitHub Models API with a minimal completion request to detect
    /// whether the service is operational. Returns false if the API returns empty
    /// responses, errors, or is unreachable.
    /// </summary>
    private async Task<bool> ProbeApiAvailabilityAsync()
    {
        if (_provider is null || _model is null)
            return false;

        try
        {
            var context = new Context(
                SystemPrompt: "Reply with OK.",
                Messages: [new UserMessage(new UserMessageContent("ping"), Ts - 1)]);

            var result = await CollectStreamAsync(_provider.Stream(_model, context));
            return !string.IsNullOrWhiteSpace(result.Text);
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Skips the current test if the API returned an empty response, indicating
    /// the provider is degraded (returning 200 with no content).
    /// </summary>
    private static void SkipIfDegraded(StreamResult result)
    {
        Skip.If(
            string.IsNullOrWhiteSpace(result.Text) && result.ToolCalls.Count == 0,
            ApiDegradedReason);
    }

    /// <summary>Collects a stream into a simple result for assertions.</summary>
    private static async Task<StreamResult> CollectStreamAsync(LlmStream stream)
    {
        var textParts = new List<string>();
        var toolCalls = new List<ToolCallInfo>();
        StopReason stopReason = StopReason.Error; // default if no done/error event

        await foreach (var evt in stream)
        {
            switch (evt)
            {
                case TextDeltaEvent delta:
                    textParts.Add(delta.Delta);
                    break;
                case DoneEvent done:
                    stopReason = done.Reason;
                    foreach (var block in done.Message.Content)
                    {
                        if (block is ToolCallContent tc)
                            toolCalls.Add(new ToolCallInfo(tc.Id, tc.Name, tc.Arguments));
                    }
                    break;
                case ErrorEvent error:
                    stopReason = error.Reason;
                    break;
            }
        }

        return new StreamResult(
            Text: string.Join("", textParts),
            StopReason: stopReason,
            ToolCalls: toolCalls);
    }

    private sealed record StreamResult(string Text, StopReason StopReason, List<ToolCallInfo> ToolCalls);
    private sealed record ToolCallInfo(string Id, string Name, Dictionary<string, object?> Arguments);
}
