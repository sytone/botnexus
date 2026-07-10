using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Agents.Core.Models;

namespace BotNexus.Extensions.Channels.Agent365;

/// <summary>
/// Transport seam for delivering an outbound reply <see cref="Activity"/> to the Agent 365 channel
/// service. Isolated behind an interface so the adapter's <c>SendAsync</c> translation path can be
/// unit-tested without a live Agents SDK connector, and so the real connector construction
/// (endpoint + MSAL client-credential token acquisition) lives in one place.
/// </summary>
public interface IAgent365ConnectorSender
{
    /// <summary>
    /// Sends a reply activity to the conversation identified by <paramref name="serviceUrl"/> /
    /// <paramref name="conversationId"/> via the Agents SDK connector's <c>ReplyToActivityAsync</c>.
    /// </summary>
    /// <param name="serviceUrl">
    /// Base URL of the channel service to post the reply to. When null the sender falls back to its
    /// configured <c>channelServiceEndpoint</c>; if neither is present the send is a no-op.
    /// </param>
    /// <param name="conversationId">Target conversation id.</param>
    /// <param name="activity">The reply activity to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendReplyAsync(string? serviceUrl, string conversationId, Activity activity, CancellationToken cancellationToken);
}
