using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Core.Streaming;

/// <summary>
/// Event protocol for assistant message streaming.
/// Port of pi-mono's AssistantMessageEvent discriminated union.
/// </summary>
public abstract record AssistantMessageEvent(string Type);

/// <summary>
/// Represents start event.
/// </summary>
public sealed record StartEvent(
    AssistantMessage Partial
) : AssistantMessageEvent("start");

/// <summary>
/// Represents text start event.
/// </summary>
public sealed record TextStartEvent(
    int ContentIndex,
    AssistantMessage Partial
) : AssistantMessageEvent("text_start");

/// <summary>
/// Represents text delta event.
/// </summary>
public sealed record TextDeltaEvent(
    int ContentIndex,
    string Delta,
    AssistantMessage Partial
) : AssistantMessageEvent("text_delta");

/// <summary>
/// Represents text end event.
/// </summary>
public sealed record TextEndEvent(
    int ContentIndex,
    string Content,
    AssistantMessage Partial
) : AssistantMessageEvent("text_end");

/// <summary>
/// Represents thinking start event.
/// </summary>
public sealed record ThinkingStartEvent(
    int ContentIndex,
    AssistantMessage Partial
) : AssistantMessageEvent("thinking_start");

/// <summary>
/// Represents thinking delta event.
/// </summary>
public sealed record ThinkingDeltaEvent(
    int ContentIndex,
    string Delta,
    AssistantMessage Partial
) : AssistantMessageEvent("thinking_delta");

/// <summary>
/// Represents thinking end event.
/// </summary>
public sealed record ThinkingEndEvent(
    int ContentIndex,
    string Content,
    AssistantMessage Partial
) : AssistantMessageEvent("thinking_end");

/// <summary>
/// Represents tool call start event.
/// </summary>
/// <remarks>
/// <para>
/// <paramref name="ToolCallId"/> and <paramref name="ToolName"/> carry the identity of the call this
/// event opens so a consumer can correlate the streaming call before it completes, instead of
/// reverse-engineering identity by indexing into the partial message (#3290). <c>ContentIndex</c> is
/// not a position in <c>AssistantMessage.ToolCalls</c> - producers allocate it over all content
/// blocks, including text and thinking - so an index-based lookup can resolve to the wrong call, or
/// to none, whenever more than one call is in flight.
/// </para>
/// <para>
/// Both are nullable rather than required: a producer that has genuinely not learned the id yet
/// reports <c>null</c>, which honestly means "not known at emit time" rather than a fabricated value.
/// </para>
/// </remarks>
public sealed record ToolCallStartEvent(
    int ContentIndex,
    AssistantMessage Partial,
    string? ToolCallId = null,
    string? ToolName = null
) : AssistantMessageEvent("toolcall_start");

/// <summary>
/// Represents tool call delta event.
/// </summary>
/// <remarks>
/// <paramref name="ToolCallId"/> and <paramref name="ToolName"/> identify the call this argument
/// fragment belongs to (#3290). Without them a consumer must guess from <c>ContentIndex</c>, which
/// can attribute a fragment to the wrong call when several calls stream concurrently. Nullable for
/// the same reason as on <see cref="ToolCallStartEvent"/>.
/// </remarks>
public sealed record ToolCallDeltaEvent(
    int ContentIndex,
    string Delta,
    AssistantMessage Partial,
    string? ToolCallId = null,
    string? ToolName = null
) : AssistantMessageEvent("toolcall_delta");

/// <summary>
/// Represents tool call end event.
/// </summary>
public sealed record ToolCallEndEvent(
    int ContentIndex,
    ToolCallContent ToolCall,
    AssistantMessage Partial
) : AssistantMessageEvent("toolcall_end");

/// <summary>
/// Represents done event.
/// </summary>
public sealed record DoneEvent(
    StopReason Reason,
    AssistantMessage Message
) : AssistantMessageEvent("done");

/// <summary>
/// Represents a non-terminal warning: an abnormal but survivable condition observed by a producer,
/// reported to the consumer without ending the turn or the stream (#3291).
/// </summary>
/// <param name="Code">
/// Stable machine-readable discriminator (e.g. <c>stream_assembly_mismatch</c>,
/// <c>malformed_chunk_skipped</c>). A consumer branches on this rather than substring-matching
/// <paramref name="Message"/>, so improving the prose cannot silently break a consumer or a test.
/// </param>
/// <param name="Message">
/// Human-readable detail. It must carry <b>no model or user content</b> - only lengths, indices and
/// identifiers - matching the discipline already enforced in <see cref="StreamAssemblyConformance"/>,
/// because this string flows to consumers and into persisted transcripts.
/// </param>
/// <param name="Partial">The message as assembled so far, unchanged by the warning.</param>
/// <remarks>
/// <para>
/// This is the contract's only abnormal-condition event that does not complete the stream. Before it
/// existed a producer that observed something wrong but recoverable had exactly two options: stay
/// silent, or escalate to a terminal <see cref="ErrorEvent"/> and kill the turn. Both known sites
/// chose silence, so a degraded turn was indistinguishable from a clean one to every consumer.
/// </para>
/// <para>
/// <see cref="LlmStream.Push"/> deliberately does not treat this case as terminal. A warning that
/// ends the stream is an error with a friendlier name, which would defeat the entire purpose.
/// </para>
/// </remarks>
public sealed record WarningEvent(
    string Code,
    string Message,
    AssistantMessage Partial
) : AssistantMessageEvent("warning");

/// <summary>
/// Well-known <see cref="WarningEvent.Code"/> values. Constants rather than literals so a producer
/// and the consumer asserting on it cannot drift apart by a typo.
/// </summary>
public static class WarningCodes
{
    /// <summary>
    /// Text assembled from streamed deltas disagreed with the provider's own authoritative final
    /// text for the block; the provider's text was preferred as canonical.
    /// </summary>
    public const string StreamAssemblyMismatch = "stream_assembly_mismatch";

    /// <summary>
    /// An SSE chunk could not be parsed and was skipped. The turn continues, but content may have
    /// been lost.
    /// </summary>
    public const string MalformedChunkSkipped = "malformed_chunk_skipped";
}

/// <summary>
/// Represents error event.
/// </summary>
public sealed record ErrorEvent(
    StopReason Reason,
    AssistantMessage Error
) : AssistantMessageEvent("error");
