using BotNexus.AgentCore.Hooks;
using BotNexus.AgentCore.Types;
using BotNexus.Providers.Core.Models;

namespace BotNexus.AgentCore.Configuration;

/// <summary>
/// Converts agent messages to provider-level chat messages.
/// </summary>
/// <param name="messages">The agent messages to convert.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>A provider-level chat message list.</returns>
public delegate Task<IReadOnlyList<Message>> ConvertToLlmDelegate(
    IReadOnlyList<AgentMessage> messages,
    CancellationToken cancellationToken);

/// <summary>
/// Transforms agent context messages before provider invocation.
/// </summary>
/// <param name="messages">The source message list.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>A transformed message list.</returns>
public delegate Task<IReadOnlyList<AgentMessage>> TransformContextDelegate(
    IReadOnlyList<AgentMessage> messages,
    CancellationToken cancellationToken);

/// <summary>
/// Resolves an API key for the requested provider identifier.
/// </summary>
/// <param name="provider">The provider identifier.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>The API key when available.</returns>
public delegate Task<string?> GetApiKeyDelegate(string provider, CancellationToken cancellationToken);

/// <summary>
/// Produces contextual message lists such as steering or follow-up messages.
/// </summary>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>The produced message list.</returns>
public delegate Task<IReadOnlyList<AgentMessage>> GetMessagesDelegate(CancellationToken cancellationToken);

/// <summary>
/// Runs before a tool call executes.
/// </summary>
/// <param name="context">The before-tool-call context.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>An optional interception result.</returns>
public delegate Task<BeforeToolCallResult?> BeforeToolCallDelegate(
    BeforeToolCallContext context,
    CancellationToken cancellationToken);

/// <summary>
/// Runs after a tool call executes.
/// </summary>
/// <param name="context">The after-tool-call context.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>An optional post-processing result.</returns>
public delegate Task<AfterToolCallResult?> AfterToolCallDelegate(
    AfterToolCallContext context,
    CancellationToken cancellationToken);
