namespace BotNexus.Agent.Providers.Core.Registry;

/// <summary>
/// Where a provider expects the system prompt to travel on the wire.
/// </summary>
/// <remarks>
/// This is observable in the repo today, not inferred: <c>AnthropicRequestBuilder</c> and
/// <c>CopilotMessagesRequestBuilder</c> both write <c>body["system"]</c>, a dedicated top-level
/// field alongside <c>messages</c>, while <c>OpenAIResponsesRequestBuilder</c>,
/// <c>OpenAICompletionsRequestBuilder</c> and <c>OpenAICompatProvider</c> all prepend a message
/// carrying a <c>system</c>/<c>developer</c> role. Declaring which shape a provider uses is what
/// lets a caller answer the question without sending a request and reading the 400.
/// </remarks>
public enum SystemPromptPlacement
{
    /// <summary>
    /// The system prompt is the first entry of the ordinary message list, carrying a
    /// <c>system</c> (or <c>developer</c>) role. OpenAI Responses/Completions and OpenAI-compatible
    /// endpoints work this way.
    /// </summary>
    FirstMessage = 0,

    /// <summary>
    /// The system prompt travels in a dedicated top-level request field, separate from the message
    /// list. The Anthropic Messages wire protocol -- served directly and via Copilot -- works this
    /// way.
    /// </summary>
    DedicatedField = 1,
}

/// <summary>
/// The behavioural contract a provider DECLARES about itself (issue #2432).
/// <para>
/// Before this existed, provider differences were handled ad hoc in the shared agent loop and
/// discovered by failure: <c>LeakedToolCallRecovery</c> ran speculatively against EVERY provider's
/// assistant text because one model on one transport was once observed leaking tool-call markup
/// (#1709). Nothing in the platform could answer "does this provider do X?" without issuing a
/// request and reading what came back.
/// </para>
/// <para>
/// <b>Deliberately small.</b> Issue #2432 lists eight candidate flags. Only the ones that trace to
/// behaviour actually observable in THIS repository are declared here. A flag that cannot be
/// grounded in a request builder or a recorded defect would be a guess wearing a type, and a
/// confidently wrong capability declaration is worse than no declaration at all -- callers would
/// stop probing and start trusting it. The record is additive by design: grounding a further flag
/// later is a non-breaking change.
/// </para>
/// <para>
/// <b>No model-id gating lives here.</b> Capabilities are declared per API provider, not sniffed
/// from a model id substring. Where version gating is genuinely needed it must reuse
/// <see cref="ModelFamilyVersion"/> (#2374); this type deliberately introduces no second parser.
/// </para>
/// </summary>
/// <param name="RecoversLeakedToolCallMarkup">
/// True when the transport is known to deliver a tool call as Anthropic <c>invoke</c>/<c>tool_use</c>
/// XML inside the assistant TEXT channel, with a finish reason that is not <c>ToolUse</c>, so the
/// agent loop must parse and promote it (#1709). Only the Copilot transports declare this: that is
/// where the leak was observed, and Copilot model discovery may route a Claude model to any of its
/// three transports, so all three declare it rather than betting on which one served the capture.
/// A provider that does not declare it gets its assistant text left exactly as the model wrote it.
/// </param>
/// <param name="SystemPromptPlacement">
/// How this provider transmits the system prompt. See <see cref="Registry.SystemPromptPlacement"/>.
/// </param>
public sealed record ProviderCapabilities(
    bool RecoversLeakedToolCallMarkup = false,
    SystemPromptPlacement SystemPromptPlacement = SystemPromptPlacement.FirstMessage)
{
    /// <summary>
    /// The capabilities assumed for a provider that declares nothing -- an out-of-tree extension
    /// provider, or a test double.
    /// <para>
    /// Every quirk workaround defaults to OFF. That is the entire point of #2432: a quirk fires
    /// from a DECLARED flag, never speculatively. The failure mode of defaulting off is that a new
    /// provider exhibiting a known quirk is not compensated for until it says so -- loud, local and
    /// fixable in one line. The failure mode of defaulting on is that every provider in the
    /// platform silently pays for one provider's defect forever, which is the state this issue
    /// exists to end.
    /// </para>
    /// </summary>
    public static ProviderCapabilities Default { get; } = new();
}
