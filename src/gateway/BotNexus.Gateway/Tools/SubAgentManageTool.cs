using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Gateway.Tools;

public sealed class SubAgentManageTool(
    ISubAgentManager subAgentManager,
    SessionId sessionId) : BotNexus.Agent.Core.ExtensionPoints.ToolExecution.IContextAwareAgentTool
{
    public string Name => "manage_subagent";
    public string Label => "Manage Sub-Agent";

    /// <summary>Content source classification for turn-taint accumulation (#2519). Gateway-owned sub-agent lifecycle state.</summary>
    public string ContentSource => ToolContentSource.Local;

    public TimeSpan? DefaultTimeout => TimeSpan.FromSeconds(1810);
    public ToolTimeoutArgument? TimeoutArgument => new("subAgentWaitTimeoutSeconds", ToolTimeoutUnit.Seconds);

    public Tool Definition => new(
        Name,
        "Get status, wait for a terminal result, or kill a sub-agent for this session. Terminal results are returned once across tool calls; completion never creates an inbound turn.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "subAgentId": {
                  "type": "string",
                  "description": "Sub-agent identifier."
                },
                "action": {
                  "type": "string",
                  "enum": ["status", "wait", "kill"],
                  "description": "Management action."
                }
              },
              "required": ["subAgentId", "action"]
            }
            """).RootElement.Clone());

    public async Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var subAgentId = ReadString(arguments, "subAgentId");
        if (string.IsNullOrWhiteSpace(subAgentId))
            throw new ArgumentException("Missing required argument: subAgentId.");

        var action = ReadString(arguments, "action");
        if (!string.Equals(action, "status", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(action, "kill", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(action, "wait", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Argument 'action' must be 'status', 'wait' or 'kill'.");

        var prepared = new Dictionary<string, object?>(arguments);
        prepared.Remove("subAgentWaitTimeoutSeconds");
        // This private prepared hint comes from the authoritative run budget, not a caller override.
        if (string.Equals(action, "wait", StringComparison.OrdinalIgnoreCase))
        {
            var info = await subAgentManager.GetAsync(subAgentId, cancellationToken).ConfigureAwait(false);
            if (info is not null && info.ParentSessionId == sessionId)
            {
                prepared["subAgentWaitTimeoutSeconds"] = info.EffectiveTimeoutSeconds ?? 600;
                return prepared;
            }
        }
        return prepared;
    }

    public Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
        => ExecuteCoreAsync(toolCallId, arguments, cancellationToken, null);

    /// <inheritdoc />
    public Task<AgentToolResult> ExecuteAsync(
        BotNexus.Agent.Core.ExtensionPoints.ToolExecution.ToolExecutionContext context,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
        => ExecuteCoreAsync(context.ToolCallRequest.Id, context.ValidatedArgs, cancellationToken,
            context.AgentRunId is { } run ? BotNexus.Domain.Primitives.AgentRunId.From(run.Value) : null);

    private async Task<AgentToolResult> ExecuteCoreAsync(string toolCallId,
        IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken, BotNexus.Domain.Primitives.AgentRunId? agentRunId)
    {
        var subAgentId = ReadString(arguments, "subAgentId")
            ?? throw new ArgumentException("Missing required argument: subAgentId.");
        var action = ReadString(arguments, "action")
            ?? throw new ArgumentException("Missing required argument: action.");

        if (string.Equals(action, "kill", StringComparison.OrdinalIgnoreCase))
        {
            var killed = await subAgentManager.KillAsync(subAgentId, sessionId, cancellationToken).ConfigureAwait(false);
            var response = JsonSerializer.Serialize(new { SubAgentId = subAgentId, Killed = killed }, JsonOptions);
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, response)]);
        }

        var info = string.Equals(action, "wait", StringComparison.OrdinalIgnoreCase)
            ? await subAgentManager.WaitAsync(subAgentId, sessionId, cancellationToken).ConfigureAwait(false)
            : await subAgentManager.GetAsync(subAgentId, cancellationToken).ConfigureAwait(false);
        if (info is null)
            throw new KeyNotFoundException($"Sub-agent '{subAgentId}' was not found.");
        if (info.ParentSessionId != sessionId)
            throw new UnauthorizedAccessException("Sub-agent does not belong to the current session.");

        if (SubAgentStatusPolicy.IsTerminal(info.Status))
            info = await subAgentManager.WaitAsync(subAgentId, sessionId, cancellationToken).ConfigureAwait(false);
        var result = JsonSerializer.Serialize(SubAgentRunDetail.FromLive(info), JsonOptions);
        if (SubAgentStatusPolicy.IsTerminal(info.Status))
            result = await subAgentManager.ConsumeResultAsync(info, toolCallId, Name,
                JsonSerializer.Serialize(arguments), result, cancellationToken, agentRunId).ConfigureAwait(false);

        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, result)]);
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.ToString(),
            _ => value.ToString()
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
