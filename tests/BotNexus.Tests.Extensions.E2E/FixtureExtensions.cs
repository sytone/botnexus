using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using Microsoft.Extensions.Configuration;

namespace BotNexus.Tests.Extensions.E2E;

public sealed class FixtureLlmProvider(IConfiguration configuration) : ILlmProvider
{
    private readonly string _defaultModel = configuration["DefaultModel"] ?? "fixture-model";

    public string DefaultModel => _defaultModel;

    public GenerationSettings Generation { get; set; } = new();

    public Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<string>>(new[] { _defaultModel });
    }

    public Task<LlmResponse> ChatAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        var lastUserMessage = request.Messages.LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content ?? string.Empty;
        var toolMessage = request.Messages.LastOrDefault(m => m.Content.StartsWith("fixture-tool:", StringComparison.OrdinalIgnoreCase))?.Content;
        var hasFixtureTool = request.Tools?.Any(t => string.Equals(t.Name, FixtureEchoTool.ToolName, StringComparison.OrdinalIgnoreCase)) == true;

        if (!string.IsNullOrWhiteSpace(toolMessage))
        {
            return Task.FromResult(new LlmResponse(
                $"provider[{_defaultModel}]:tool-finished:{toolMessage}",
                FinishReason.Stop));
        }

        if (lastUserMessage.Contains("use tool", StringComparison.OrdinalIgnoreCase) && hasFixtureTool)
        {
            return Task.FromResult(new LlmResponse(
                string.Empty,
                FinishReason.ToolCalls,
                [new ToolCallRequest(
                    "fixture-call-1",
                    FixtureEchoTool.ToolName,
                    new Dictionary<string, object?> { ["text"] = "from-provider" })]));
        }

        return Task.FromResult(new LlmResponse($"provider[{_defaultModel}]:{lastUserMessage}", FinishReason.Stop));
    }

    public async IAsyncEnumerable<string> ChatStreamAsync(
        ChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return "fixture-stream";
        await Task.CompletedTask;
    }
}

public sealed class FixtureEchoTool : ITool
{
    public const string ToolName = "fixture_echo_tool";

    public ToolDefinition Definition { get; } = new(
        ToolName,
        "Echo fixture tool for integration tests",
        new Dictionary<string, ToolParameterSchema>
        {
            ["text"] = new("string", "Text to echo", true)
        });

    public Task<string> ExecuteAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
    {
        var text = arguments.TryGetValue("text", out var value)
            ? Convert.ToString(value) ?? string.Empty
            : string.Empty;
        return Task.FromResult($"fixture-tool:{text}");
    }
}

public sealed class FixtureChannel(IConfiguration configuration) : IChannel
{
    public string Name { get; } = configuration["Name"] ?? "fixture-channel";
    public string DisplayName => "Fixture Channel";
    public bool IsRunning { get; private set; }
    public bool SupportsStreaming => false;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        IsRunning = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        IsRunning = false;
        return Task.CompletedTask;
    }

    public Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SendDeltaAsync(string chatId, string delta, IReadOnlyDictionary<string, object>? metadata = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public bool IsAllowed(string senderId) => true;
}
