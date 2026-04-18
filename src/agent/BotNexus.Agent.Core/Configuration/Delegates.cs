using BotNexus.Agent.Core.Hooks;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Configuration;

/// <summary>
/// Converts agent messages to provider-level chat messages.
/// </summary>
/// <param name="messages">The agent messages to convert.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>A provider-level chat message list.</returns>
/// <remarks>
/// <para>
/// Each AgentMessage must be converted to a UserMessage, AssistantMessage, or ToolResultMessage
/// that the LLM can understand. AgentMessages that cannot be converted (e.g., UI-only notifications,
/// status messages) should be filtered out.
/// </para>
/// <para>
/// Contract: must not throw or reject. Return a safe fallback value instead.
/// Throwing interrupts the low-level agent loop without producing a normal event sequence.
/// </para>
/// </remarks>
public delegate Task<IReadOnlyList<Message>> ConvertToLlmDelegate(
    IReadOnlyList<AgentMessage> messages,
    CancellationToken cancellationToken);

/// <summary>
/// Transforms agent context messages before provider invocation.
/// </summary>
/// <param name="messages">The source message list.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>A transformed message list.</returns>
/// <remarks>
/// Use to filter, summarize, or rewrite messages before they reach the LLM.
/// Contract: must not throw. Return the original list or a safe fallback.
/// </remarks>
public delegate Task<IReadOnlyList<AgentMessage>> TransformContextDelegate(
    IReadOnlyList<AgentMessage> messages,
    CancellationToken cancellationToken);

/// <summary>
/// Resolves an API key for the requested provider identifier.
/// </summary>
/// <param name="provider">The provider identifier.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>The API key when available.</returns>
/// <remarks>
/// Called before each LLM invocation. Return null if no key is available or the provider
/// does not require authentication. Must not throw.
/// </remarks>
public delegate Task<string?> GetApiKeyDelegate(string provider, CancellationToken cancellationToken);

/// <summary>
/// Produces contextual message lists such as steering or follow-up messages.
/// </summary>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>The produced message list.</returns>
/// <remarks>
/// Called at turn boundaries (steering) or run completion (follow-up).
/// Return an empty list if no messages are available. Must not throw.
/// </remarks>
public delegate Task<IReadOnlyList<AgentMessage>> GetMessagesDelegate(CancellationToken cancellationToken);

/// <summary>
/// Runs before a tool call executes.
/// </summary>
/// <param name="context">The before-tool-call context.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>An optional interception result.</returns>
/// <remarks>
/// Use to validate, block, or log tool calls before execution.
/// Return BeforeToolCallResult with Block=true to prevent execution.
/// Must not throw — exceptions are logged and ignored.
/// </remarks>
public delegate Task<BeforeToolCallResult?> BeforeToolCallDelegate(
    BeforeToolCallContext context,
    CancellationToken cancellationToken);

/// <summary>
/// Runs after a tool call executes.
/// </summary>
/// <param name="context">The after-tool-call context.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>An optional post-processing result.</returns>
/// <remarks>
/// Use to transform, filter, or override tool results before they reach the LLM.
/// Return AfterToolCallResult to replace Content, Details, or IsError.
/// Must not throw — exceptions are logged and ignored.
/// </remarks>
public delegate Task<AfterToolCallResult?> AfterToolCallDelegate(
    AfterToolCallContext context,
    CancellationToken cancellationToken);
