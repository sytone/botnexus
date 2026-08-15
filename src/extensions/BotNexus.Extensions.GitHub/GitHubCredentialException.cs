namespace BotNexus.Extensions.GitHub;

/// <summary>
/// Raised when a GitHub App installation credential cannot be resolved.
/// </summary>
/// <remarks>
/// Messages are deliberately status/configuration-shaped and never carry token material or a raw
/// GitHub response body — an error path that echoes the secret is the leak AC4 (#2732) pins.
/// </remarks>
public sealed class GitHubCredentialException : Exception
{
    /// <summary>Creates the exception with a message that must not contain credential material.</summary>
    public GitHubCredentialException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner cause.</summary>
    public GitHubCredentialException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
