using System.IO.Abstractions;
using BotNexus.Domain.Paths;

namespace BotNexus.Gateway.Configuration;

/// <summary>Reads configuration values from owner-protected files.</summary>
public sealed class FileConfigurationReferenceResolver(IFileSystem fileSystem) : IConfigurationReferenceResolver
{
    /// <inheritdoc />
    public string Scheme => "file";

    /// <inheritdoc />
    public async Task<string?> ResolveAsync(string identifier, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var path = HomePathExpander.ExpandRequired(identifier);
            if (!fileSystem.Path.IsPathFullyQualified(path))
                return null;

            path = fileSystem.Path.GetFullPath(path);
            if (!fileSystem.File.Exists(path) || SecureFilePermissions.IsReadableByOthers(fileSystem, path, failClosed: true))
                return null;

            return (await fileSystem.File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false))
                .TrimEnd('\r', '\n');
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or InvalidOperationException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
