namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// A message the gateway can queue onto an agent's follow-up seam, owned by the gateway (#3251).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists.</b> <c>IAgentHandle.FollowUpAsync</c> typed its parameter on
/// <c>BotNexus.Agent.Core.Types.AgentMessage</c>. The abstraction assemblies exist precisely so a
/// downstream component can reference a gateway contract <em>without</em> inheriting a dependency on
/// the agent implementation, so publishing a core type through them defeated their only purpose -
/// the same defect #3040 closed for the user-message cluster, of which this was the deferred
/// remainder.
/// </para>
/// <para>
/// <b>Why it is a closed union rather than a mirror of the whole core hierarchy.</b> Core's
/// <c>AgentMessage</c> spans assistant, tool-result and system entries, whose faithful mirrors would
/// drag <c>ContentBlock</c>, <c>ToolCallContent</c>, <c>StopReason</c> and <c>AgentUsage</c> across
/// the same seam - re-opening the provider-model coupling this issue is trying to remove. The
/// follow-up seam has only ever been given user-authored content: a plain user message, or a
/// sub-agent completion notice. Modelling exactly those two makes the mapping in
/// <c>AgentMessageMapping</c> <b>total and lossless</b>, which is the parity standard #3040 set,
/// instead of a partial mapping that would quietly drop fields for kinds nothing ever sends.
/// </para>
/// </remarks>
/// <param name="Role">The canonical message role carried across the seam.</param>
public abstract record AgentTranscriptMessage(string Role);

/// <summary>
/// A user-authored follow-up, carrying the gateway's own <see cref="AgentUserMessage"/> so text,
/// image payloads and the <c>DeferWhileBusy</c> side-turn flag all survive the seam intact.
/// </summary>
/// <param name="Message">The composed user message to queue.</param>
public sealed record AgentUserTranscriptMessage(AgentUserMessage Message)
    : AgentTranscriptMessage("user");

/// <summary>
/// A sub-agent completion notice queued onto the parent agent's follow-up seam.
/// </summary>
/// <remarks>
/// Kept structured rather than pre-rendered to text so the mapping stays lossless: the rendered
/// form is derivable from these fields, but the fields are not recoverable from the rendered form.
/// </remarks>
/// <param name="SubAgentId">The completed sub-agent's identifier.</param>
/// <param name="Status">The terminal status description.</param>
/// <param name="Summary">The completion summary text.</param>
/// <param name="CompletedAt">When the sub-agent completed.</param>
public sealed record AgentSubAgentCompletionTranscriptMessage(
    string SubAgentId,
    string Status,
    string Summary,
    DateTimeOffset CompletedAt) : AgentTranscriptMessage("user");
