namespace BotNexus.Gateway.Configuration;

/// <summary>Reads configuration values from the process environment.</summary>
public sealed class EnvironmentConfigurationReferenceResolver : IConfigurationReferenceResolver
{
    /// <inheritdoc />
    public string Scheme => "env";

    /// <inheritdoc />
    public Task<string?> ResolveAsync(string identifier, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(Environment.GetEnvironmentVariable(identifier));
        }
        catch (ArgumentException)
        {
            return Task.FromResult<string?>(null);
        }
    }
}
