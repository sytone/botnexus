namespace BotNexus.Extensions.Channels.Matrix;

/// <summary>
/// Creates one <see cref="IMatrixClient"/> per configured Matrix account.
/// </summary>
/// <remarks>
/// Exists so tests can substitute a fake homeserver for every account the adapter starts, in the
/// same shape the Service Bus adapter uses for its client factory.
/// </remarks>
public interface IMatrixClientFactory
{
    /// <summary>
    /// Creates a client bound to one account's homeserver and access token.
    /// </summary>
    /// <param name="accountName">Configuration key of the account (typically the agent name).</param>
    /// <param name="homeserver">Base URL of the homeserver.</param>
    /// <param name="userId">The account's fully-qualified Matrix user ID.</param>
    /// <param name="accessToken">
    /// The account's access token, wrapped so it cannot be logged or serialised by accident. The
    /// implementation unwraps it exactly once, when setting the <c>Authorization</c> header.
    /// </param>
    /// <returns>A client for that account.</returns>
    IMatrixClient Create(string accountName, string homeserver, string userId, MatrixAccessToken accessToken);
}
