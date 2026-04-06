namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Optional contract for agent handles that can report their runtime health.
/// </summary>
public interface IHealthCheckable
{
    /// <summary>
    /// Performs a lightweight health check for this running agent handle.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if healthy; otherwise <c>false</c>.</returns>
    Task<bool> PingAsync(CancellationToken cancellationToken = default);
}
