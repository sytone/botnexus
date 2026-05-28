using System.Diagnostics;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Diagnostics;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Providers.IntegrationMock;

/// <summary>
/// Deterministic LLM provider that returns scripted responses keyed by the trimmed text of
/// the last <see cref="UserMessage"/> in the request. Designed for integration, UI, and
/// concurrency tests where a real provider would be slow, costly, or non-deterministic.
///
/// <para>Scripts are sourced from a <see cref="MockCatalog"/> resolved by
/// <see cref="MockCatalogLoader"/>; see that type for path precedence. When no script matches
/// the key the provider emits a single text event <c>"NO_SCRIPT:&lt;key&gt;"</c> and stops —
/// failing loud rather than silently behaving like a default LLM.</para>
/// </summary>
public sealed class IntegrationMockProvider : IApiProvider
{
    private readonly MockCatalogLoader _loader;

    /// <summary>The API identifier this provider serves.</summary>
    public string Api => IntegrationMockModels.ApiName;

    /// <summary>
    /// Create a provider. Pass <paramref name="loader"/> in tests to inject a fixed catalog;
    /// production wiring uses the default loader which reads the path from <c>model.BaseUrl</c>,
    /// the <c>BOTNEXUS_MOCK_CATALOG</c> env var, or the built-in default.
    /// </summary>
    public IntegrationMockProvider(MockCatalogLoader? loader = null)
    {
        _loader = loader ?? new MockCatalogLoader();
    }

    /// <inheritdoc />
    public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
    {
        var stream = new LlmStream();

        _ = Task.Run(async () =>
        {
            using var activity = ProviderDiagnostics.Source.StartActivity(
                "provider.integration-mock.stream", ActivityKind.Client);
            activity?.SetTag("botnexus.provider.name", model.Provider);
            activity?.SetTag("botnexus.model", model.Id);
            activity?.SetTag("botnexus.model.api", model.Api);

            try
            {
                await StreamCoreAsync(model, context, options, stream);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                var errorMessage = CreateErrorMessage(model, ex.Message);
                stream.Push(new ErrorEvent(StopReason.Error, errorMessage));
                stream.End(errorMessage);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            }
        });

        return stream;
    }

    /// <inheritdoc />
    public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
        => Stream(model, context, options);

    private async Task StreamCoreAsync(LlmModel model, Context context, StreamOptions? options, LlmStream stream)
    {
        var ct = options?.CancellationToken ?? CancellationToken.None;
        var catalog = _loader.Resolve(string.IsNullOrWhiteSpace(model.BaseUrl) ? null : model.BaseUrl);
        var key = ExtractKey(context);

        // Track running state for partial AssistantMessage snapshots emitted with each event.
        var contentBlocks = new List<ContentBlock>();
        var textBuffers = new Dictionary<int, System.Text.StringBuilder>();
        var thinkingBuffers = new Dictionary<int, System.Text.StringBuilder>();
        var currentIndex = -1;

        AssistantMessage Snapshot(StopReason stopReason = StopReason.Stop, string? errorMessage = null)
            => new(
                Content: contentBlocks.ToArray(),
                Api: Api,
                Provider: model.Provider,
                ModelId: model.Id,
                Usage: Usage.Empty(),
                StopReason: stopReason,
                ErrorMessage: errorMessage,
                ResponseId: null,
                Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        stream.Push(new StartEvent(Snapshot()));

        var script = _loader.Lookup(catalog, key);
        if (script is null)
        {
            await EmitNoScriptAsync(stream, model, key, contentBlocks, Snapshot, ct);
            return;
        }

        foreach (var step in script)
        {
            ct.ThrowIfCancellationRequested();

            if (step.DelayMs is > 0)
                await Task.Delay(step.DelayMs.Value, ct);

            switch (step.Type)
            {
                case "text_delta":
                    {
                        var (index, builder) = EnsureTextBlock(contentBlocks, textBuffers, ref currentIndex);
                        if (builder.Length == 0)
                            stream.Push(new TextStartEvent(index, Snapshot()));
                        var delta = step.Delta ?? string.Empty;
                        builder.Append(delta);
                        contentBlocks[index] = new TextContent(builder.ToString());
                        stream.Push(new TextDeltaEvent(index, delta, Snapshot()));
                        break;
                    }
                case "text_end":
                    {
                        if (currentIndex >= 0 && textBuffers.TryGetValue(currentIndex, out var builder))
                        {
                            var text = builder.ToString();
                            contentBlocks[currentIndex] = new TextContent(text);
                            stream.Push(new TextEndEvent(currentIndex, text, Snapshot()));
                            currentIndex = -1;
                        }
                        break;
                    }
                case "thinking_delta":
                    {
                        var (index, builder) = EnsureThinkingBlock(contentBlocks, thinkingBuffers, ref currentIndex);
                        if (builder.Length == 0)
                            stream.Push(new ThinkingStartEvent(index, Snapshot()));
                        var delta = step.Delta ?? string.Empty;
                        builder.Append(delta);
                        contentBlocks[index] = new ThinkingContent(builder.ToString());
                        stream.Push(new ThinkingDeltaEvent(index, delta, Snapshot()));
                        break;
                    }
                case "thinking_end":
                    {
                        if (currentIndex >= 0 && thinkingBuffers.TryGetValue(currentIndex, out var builder))
                        {
                            var content = builder.ToString();
                            contentBlocks[currentIndex] = new ThinkingContent(content);
                            stream.Push(new ThinkingEndEvent(currentIndex, content, Snapshot()));
                            currentIndex = -1;
                        }
                        break;
                    }
                case "tool_call":
                    {
                        var toolName = step.ToolName
                            ?? throw new InvalidOperationException("tool_call step requires toolName.");
                        var toolCallId = step.ToolCallId ?? $"mock-{Guid.NewGuid():N}";
                        var args = step.ToolArguments ?? new Dictionary<string, object?>();
                        var toolCall = new ToolCallContent(toolCallId, toolName, args);
                        contentBlocks.Add(toolCall);
                        var index = contentBlocks.Count - 1;
                        stream.Push(new ToolCallStartEvent(index, Snapshot(StopReason.ToolUse)));
                        stream.Push(new ToolCallEndEvent(index, toolCall, Snapshot(StopReason.ToolUse)));
                        currentIndex = -1;
                        break;
                    }
                case "done":
                    {
                        var reason = ParseStopReason(step.StopReason) ?? StopReason.Stop;
                        var message = Snapshot(reason);
                        stream.Push(new DoneEvent(reason, message));
                        return;
                    }
                case "error":
                    {
                        var reason = ParseStopReason(step.StopReason) ?? StopReason.Error;
                        var errorText = step.ErrorMessage ?? "Mock error.";
                        var message = Snapshot(reason, errorText);
                        stream.Push(new ErrorEvent(reason, message));
                        stream.End(message);
                        return;
                    }
                default:
                    throw new InvalidOperationException(
                        $"Unknown scripted step type '{step.Type}' for key '{key}'.");
            }
        }

        // Script ended without an explicit done/error — synthesize a normal stop.
        var fallback = Snapshot();
        stream.Push(new DoneEvent(StopReason.Stop, fallback));
    }

    private async Task EmitNoScriptAsync(
        LlmStream stream,
        LlmModel model,
        string key,
        List<ContentBlock> contentBlocks,
        Func<StopReason, string?, AssistantMessage> snapshot,
        CancellationToken ct)
    {
        // Allow callers to await the result.
        await Task.Yield();

        var text = $"NO_SCRIPT:{key}";
        contentBlocks.Add(new TextContent(text));
        var index = contentBlocks.Count - 1;
        stream.Push(new TextStartEvent(index, snapshot(StopReason.Stop, null)));
        stream.Push(new TextDeltaEvent(index, text, snapshot(StopReason.Stop, null)));
        stream.Push(new TextEndEvent(index, text, snapshot(StopReason.Stop, null)));
        stream.Push(new DoneEvent(StopReason.Stop, snapshot(StopReason.Stop, null)));
    }

    private static (int Index, System.Text.StringBuilder Builder) EnsureTextBlock(
        List<ContentBlock> blocks,
        Dictionary<int, System.Text.StringBuilder> buffers,
        ref int currentIndex)
    {
        if (currentIndex >= 0 && blocks[currentIndex] is TextContent && buffers.ContainsKey(currentIndex))
            return (currentIndex, buffers[currentIndex]);

        blocks.Add(new TextContent(string.Empty));
        var index = blocks.Count - 1;
        var builder = new System.Text.StringBuilder();
        buffers[index] = builder;
        currentIndex = index;
        return (index, builder);
    }

    private static (int Index, System.Text.StringBuilder Builder) EnsureThinkingBlock(
        List<ContentBlock> blocks,
        Dictionary<int, System.Text.StringBuilder> buffers,
        ref int currentIndex)
    {
        if (currentIndex >= 0 && blocks[currentIndex] is ThinkingContent && buffers.ContainsKey(currentIndex))
            return (currentIndex, buffers[currentIndex]);

        blocks.Add(new ThinkingContent(string.Empty));
        var index = blocks.Count - 1;
        var builder = new System.Text.StringBuilder();
        buffers[index] = builder;
        currentIndex = index;
        return (index, builder);
    }

    private static StopReason? ParseStopReason(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return raw.Trim().ToLowerInvariant() switch
        {
            "stop" => StopReason.Stop,
            "length" => StopReason.Length,
            "tooluse" or "tool_use" or "tool-use" => StopReason.ToolUse,
            "error" => StopReason.Error,
            "aborted" => StopReason.Aborted,
            "refusal" => StopReason.Refusal,
            "sensitive" => StopReason.Sensitive,
            _ => null
        };
    }

    /// <summary>
    /// Extract the lookup key from <paramref name="context"/>. The key is the trimmed text of
    /// the final <see cref="UserMessage"/>; multi-block user messages concatenate their
    /// <see cref="TextContent"/> blocks. Returns an empty string when no user message is found.
    /// </summary>
    public static string ExtractKey(Context context)
    {
        for (var i = context.Messages.Count - 1; i >= 0; i--)
        {
            if (context.Messages[i] is not UserMessage user)
                continue;

            if (user.Content.IsText)
                return (user.Content.Text ?? string.Empty).Trim();

            if (user.Content.Blocks is { Count: > 0 } blocks)
            {
                var combined = string.Concat(blocks.OfType<TextContent>().Select(t => t.Text));
                return combined.Trim();
            }

            return string.Empty;
        }

        return string.Empty;
    }

    private AssistantMessage CreateErrorMessage(LlmModel model, string error)
        => new(
            Content: [new TextContent(error)],
            Api: Api,
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: Usage.Empty(),
            StopReason: StopReason.Error,
            ErrorMessage: error,
            ResponseId: null,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
