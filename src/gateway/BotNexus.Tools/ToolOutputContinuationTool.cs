using System.Text.Json;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Tools;

/// <summary>
/// Reads back the remainder of a tool result that exceeded the central output budget, using the
/// continuation handle carried in the truncation marker (issue #2760).
/// </summary>
/// <remarks>
/// <para>
/// Before this tool existed, exceeding the cap destroyed the omitted bytes: the model saw a prefix
/// and a suggestion to "narrow the scope", which for a 3.6x-overshoot list call is not a dial it can
/// turn - the forensics window recorded the identical call retried four times at exactly the same
/// byte count. A handle is unconditionally actionable, which is the whole point: a large-but-valid
/// result becomes paginated rather than lost.
/// </para>
/// <para>
/// This tool is a pure reader over an in-memory store. It never re-executes the original tool, so it
/// cannot re-run a side-effecting command, and it introduces no new access to the filesystem or the
/// network beyond what the original call already surfaced.
/// </para>
/// </remarks>
public sealed class ToolOutputContinuationTool(ToolOutputContinuationStore? store = null) : IAgentTool
{
    /// <summary>Default bytes returned per continuation call when the caller does not choose.</summary>
    public const int DefaultChunkBytes = 32 * 1024;

    private readonly ToolOutputContinuationStore _store = store ?? ToolOutputContinuationStore.Shared;

    /// <inheritdoc />
    public string Name => ToolOutputBudget.ContinuationToolName;

    /// <inheritdoc />
    public string Label => "Continue Tool Output";

    /// <summary>
    /// Content source classification for turn-taint accumulation (#2519).
    /// </summary>
    /// <remarks>
    /// Deliberately <see cref="ToolContentSource.Unknown"/>, the fail-closed value. The bytes this
    /// tool returns are the SAME bytes some earlier tool produced, and the store does not carry that
    /// tool's classification forward, so the true origin is genuinely not established here.
    /// Declaring <c>local</c> because the buffer is in-memory would launder remote content: any
    /// oversized foreign payload could shed its taint simply by being large enough to be truncated.
    /// </remarks>
    public string ContentSource => ToolContentSource.Unknown;

    /// <inheritdoc />
    public Tool Definition => new(
        Name,
        "Retrieve the next portion of a tool result that was truncated because it exceeded the output size budget. Use the handle and offset given in the truncation marker.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "handle": {
                  "type": "string",
                  "description": "The continuation handle from the truncation marker."
                },
                "offset": {
                  "type": "integer",
                  "description": "Byte offset to resume from. Use the offset named in the marker, then the offset returned by the previous continuation call."
                },
                "max_bytes": {
                  "type": "integer",
                  "description": "Maximum bytes to return in this call. Defaults to 32768."
                }
              },
              "required": ["handle"]
            }
            """).RootElement.Clone());

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var handle = arguments.TryGetValue("handle", out var raw) ? raw?.ToString() : null;
        if (string.IsNullOrWhiteSpace(handle))
        {
            throw new ArgumentException("handle cannot be empty.");
        }

        var prepared = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["handle"] = handle,
            ["offset"] = ReadLong(arguments, "offset", 0),
            ["max_bytes"] = (long)ReadLong(arguments, "max_bytes", DefaultChunkBytes)
        };

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(prepared);
    }

    /// <inheritdoc />
    public Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(arguments);

        var handle = arguments.TryGetValue("handle", out var raw) ? raw?.ToString() : null;
        var offset = ReadLong(arguments, "offset", 0);
        var maxBytes = (int)Math.Min(int.MaxValue, ReadLong(arguments, "max_bytes", DefaultChunkBytes));

        var slice = _store.Read(handle, offset, maxBytes);

        var text = slice.Status switch
        {
            ToolOutputContinuationStatus.UnknownHandle =>
                $"No stored output for handle '{handle}'. It was never issued, or it has been evicted - rerun the original tool with a narrower scope.",
            ToolOutputContinuationStatus.OffsetOutOfRange =>
                $"Offset {offset} is outside the stored output (total {slice.TotalBytes} bytes). Resume from an offset within range.",
            _ => slice.IsComplete
                ? $"{slice.Text}\n[continuation complete: {slice.NextOffset} of {slice.TotalBytes} bytes returned]"
                : $"{slice.Text}\n[continuation: next offset {slice.NextOffset} of {slice.TotalBytes} bytes] {ToolOutputBudget.ContinuationGuidance(handle!, slice.NextOffset)}"
        };

        return Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, text)]));
    }

    /// <summary>
    /// Reads an integral argument, tolerating the boxed numeric shapes a JSON deserialiser produces.
    /// </summary>
    private static long ReadLong(IReadOnlyDictionary<string, object?> arguments, string key, long fallback)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
        {
            return fallback;
        }

        return value switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var parsed) => parsed,
            _ => long.TryParse(value.ToString(), out var parsed) ? parsed : fallback
        };
    }
}
