using BotNexus.Gateway.Abstractions.Models;
using AgentCoreImageContent = BotNexus.Agent.Core.Types.AgentImageContent;
using AgentCoreMessage = BotNexus.Agent.Core.Types.AgentMessage;
using AgentCoreSubAgentCompletionMessage = BotNexus.Agent.Core.Types.SubAgentCompletionMessage;
using AgentCoreUserMessage = BotNexus.Agent.Core.Types.UserMessage;

namespace BotNexus.Gateway.Isolation;

/// <summary>
/// The single mapping site between the gateway's own message contract
/// (<see cref="AgentUserMessage"/>) and the agent-core message type the agent loop consumes (#3040).
/// </summary>
/// <remarks>
/// <para>
/// The isolation layer is precisely the layer that is <em>entitled</em> to know about agent-core:
/// its whole job is to bridge the gateway to a concrete execution environment. Keeping the
/// conversion here - rather than letting the core type travel outward through
/// <c>BotNexus.Gateway.Contracts</c> - is what lets ten downstream extension projects depend on the
/// gateway abstraction without inheriting a dependency on the agent implementation.
/// </para>
/// <para>
/// The mapping is total and field-for-field: text, image payloads and the <c>DeferWhileBusy</c>
/// side-turn flag all survive the round trip, so no dispatch path (prompt, stream, steer, redirect,
/// follow-up) observes a behavioural difference. If a field is ever added to one side and not the
/// other, that silently drops data - add it here in the same change.
/// </para>
/// </remarks>
internal static class AgentMessageMapping
{
    /// <summary>Projects a gateway user message onto the agent-core type the loop consumes.</summary>
    /// <param name="message">The gateway-owned composed message.</param>
    /// <returns>The equivalent agent-core user message.</returns>
    public static AgentCoreUserMessage ToCore(this AgentUserMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var images = message.Images is { Count: > 0 }
            ? message.Images.Select(i => new AgentCoreImageContent(i.Value)).ToArray()
            : null;

        return new AgentCoreUserMessage(message.Content, images)
        {
            DeferWhileBusy = message.DeferWhileBusy
        };
    }

    /// <summary>Projects an agent-core user message back onto the gateway contract type.</summary>
    /// <param name="message">The agent-core message.</param>
    /// <returns>The equivalent gateway user message.</returns>
    public static AgentUserMessage ToGateway(this AgentCoreUserMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var images = message.Images is { Count: > 0 }
            ? message.Images.Select(i => new AgentImageContent(i.Value)).ToArray()
            : null;

        return new AgentUserMessage(message.Content, images)
        {
            DeferWhileBusy = message.DeferWhileBusy
        };
    }

    /// <summary>
    /// Projects a gateway transcript message onto the agent-core message the follow-up queue holds.
    /// </summary>
    /// <param name="message">The gateway-owned transcript message.</param>
    /// <returns>The equivalent agent-core message.</returns>
    /// <remarks>
    /// <para>
    /// Total over the closed <see cref="AgentTranscriptMessage"/> union and lossless in both arms:
    /// the user arm delegates to <see cref="ToCore(AgentUserMessage)"/> (text, images and the
    /// <c>DeferWhileBusy</c> side-turn flag), and the completion arm carries all four structured
    /// fields rather than a pre-rendered string, so no field is dropped.
    /// </para>
    /// <para>
    /// <b>Identity hazard (#3040/#2438).</b> The follow-up enqueue/reclaim pair matches by reference
    /// identity, so any call site spanning the boundary must map ONCE into a local and reuse that
    /// same instance for both the enqueue and the reclaim. Never call this twice for one message.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The union gained a case with no mapping. Failing loudly is deliberate: silently degrading an
    /// unmapped kind to text would drop data at exactly the seam this mapping exists to keep total.
    /// </exception>
    public static AgentCoreMessage ToCore(this AgentTranscriptMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message switch
        {
            AgentUserTranscriptMessage user => user.Message.ToCore(),
            AgentSubAgentCompletionTranscriptMessage completion => new AgentCoreSubAgentCompletionMessage
            {
                SubAgentId = completion.SubAgentId,
                Status = completion.Status,
                Summary = completion.Summary,
                CompletedAt = completion.CompletedAt
            },
            _ => throw new ArgumentOutOfRangeException(
                nameof(message),
                message.GetType().FullName,
                "Unmapped gateway transcript message kind; add its arm here rather than degrading it.")
        };
    }
}
