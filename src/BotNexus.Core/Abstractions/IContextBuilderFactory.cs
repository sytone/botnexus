namespace BotNexus.Core.Abstractions;

/// <summary>
/// Creates context builders for a specific agent.
/// </summary>
public interface IContextBuilderFactory
{
    /// <summary>
    /// Creates a context builder bound to the supplied agent name.
    /// </summary>
    IContextBuilder Create(string agentName);
}
