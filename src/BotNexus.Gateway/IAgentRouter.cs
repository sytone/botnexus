using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;

namespace BotNexus.Gateway;

/// <summary>Resolves which agent runners should handle an inbound message.</summary>
public interface IAgentRouter
{
    /// <summary>Resolves target agent runners for an inbound message.</summary>
    IReadOnlyList<IAgentRunner> ResolveTargets(InboundMessage message);
}
