using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Persists a sub-agent tool invocation before execution can cross the tool boundary.
/// This is the issue #2113 slice of the platform-wide audit pipeline tracked by #2127.
/// </summary>
internal sealed class SubAgentToolWriteAhead(
    ISessionStore? sessionStore,
    ISecretRedactor redactor,
    SessionId sessionId,
    ILogger logger)
{
    private static readonly HashSet<string> FailClosedTools =
        new(["exec", "shell", "process"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Writes the redacted invocation into the child transcript. Process-capable tools fail closed
    /// when durability is unavailable; other tools preserve the existing best-effort behaviour.
    /// </summary>
    public async Task PersistAsync(
        string toolCallId,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var store = sessionStore
                ?? throw new InvalidOperationException("Sub-agent session persistence is unavailable.");
            var session = await store.GetAsync(sessionId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Sub-agent session '{sessionId}' does not exist.");
            session.AddEntry(new SessionEntry
            {
                Role = MessageRole.Tool,
                Content = $"Tool '{toolName}' started.",
                ToolName = toolName,
                ToolCallId = toolCallId,
                ToolArgs = redactor.Redact(JsonSerializer.Serialize(arguments))
            });
            await store.SaveAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            GatewayTelemetry.SubAgentToolWriteAheadFailures.Add(1,
                new KeyValuePair<string, object?>("botnexus.tool.name", toolName),
                new KeyValuePair<string, object?>("botnexus.session.id", sessionId.Value));
            logger.LogError(ex,
                "Failed to persist sub-agent tool start for tool '{ToolName}', call '{ToolCallId}', session '{SessionId}'.",
                toolName, toolCallId, sessionId);

            if (FailClosedTools.Contains(toolName))
            {
                throw new InvalidOperationException(
                    $"Tool '{toolName}' was blocked because its invocation could not be durably recorded.", ex);
            }
        }
    }
}
