using System.Text.Json.Serialization;

namespace BotNexus.Agent.Providers.Core.Models;

/// <summary>
/// Base content block for message content arrays.
/// Uses "type" discriminator for polymorphic JSON serialization.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextContent), "text")]
[JsonDerivedType(typeof(RefusalContent), "refusal")]
[JsonDerivedType(typeof(ThinkingContent), "thinking")]
[JsonDerivedType(typeof(ImageContent), "image")]
[JsonDerivedType(typeof(ToolCallContent), "toolCall")]
/// <summary>
/// Represents content block.
/// </summary>
public abstract record ContentBlock;

/// <summary>
/// Represents text content.
/// </summary>
public record TextContent(
    string Text,
    string? TextSignature = null
) : ContentBlock;

/// <summary>
/// Safety-refusal output: text the model emitted to decline a request, rather than text it
/// authored in its ordinary voice (#3295).
/// </summary>
/// <remarks>
/// <para>
/// Refusal previously had nowhere to live in this union, and both OpenAI-family parsers degraded
/// in a different direction as a result: the Responses parser pushed refusal deltas through the
/// ordinary text channel (so a refusal rendered as if the model had said it normally), and the
/// Chat Completions parser read the delta's refusal property only to set the stop reason and
/// discarded the string entirely (so a refused turn rendered as an empty assistant message). A
/// terminal-only signal such as StopReason.Refusal cannot repair either case, because it arrives
/// after the content has already been streamed to the user.
/// </para>
/// <para>
/// This deliberately derives from <see cref="TextContent"/> rather than sitting beside it as a
/// fourth peer kind. Refusal <em>is</em> displayable text, and roughly thirty call sites across
/// the gateway, channel adapters and provider message converters project assistant content via
/// <c>OfType&lt;TextContent&gt;()</c> or <c>case TextContent</c>. Subtyping makes the change purely
/// additive: every one of those consumers keeps rendering and round-tripping refusal text with no
/// edit, while a consumer that needs to treat safety output differently can test for
/// <see cref="RefusalContent"/> the moment the block is emitted. Record equality still separates
/// the two kinds, so a refusal never compares equal to prose with the same characters.
/// </para>
/// </remarks>
public sealed record RefusalContent(
    string Text
) : TextContent(Text);

/// <summary>
/// Represents thinking content.
/// </summary>
/// <param name="Thinking">
/// The reasoning text. For a redacted block this is a placeholder rather than model output — the
/// real reasoning was withheld by the provider and is not recoverable.
/// </param>
/// <param name="ThinkingSignature">
/// The provider's opaque verification payload. For a redacted block this carries the wire
/// <c>data</c> field, which the request converter must replay verbatim.
/// </param>
/// <param name="Redacted">
/// True when the provider withheld the reasoning (an Anthropic Messages <c>redacted_thinking</c>
/// block); null or false for reasoning the model actually produced. This is not decoration:
/// <c>AnthropicMessageConverter</c> branches on it to re-emit the block as a wire-level
/// <c>redacted_thinking</c>, so losing the bit would replay a redacted block as ordinary visible
/// reasoning whose text is only the placeholder. Pinned by <c>AnthropicRedactedThinkingTests</c>
/// and <c>CopilotMessagesRedactedThinkingTests</c> (#3299) — do not collapse the two producer arms.
/// </param>
public sealed record ThinkingContent(
    string Thinking,
    string? ThinkingSignature = null,
    bool? Redacted = null
) : ContentBlock;

/// <summary>
/// Represents image content.
/// </summary>
public sealed record ImageContent(
    string Data,
    string MimeType
) : ContentBlock;

/// <summary>
/// Represents tool call content.
/// </summary>
public sealed record ToolCallContent(
    string Id,
    string Name,
    Dictionary<string, object?> Arguments,
    string? ThoughtSignature = null
) : ContentBlock;
