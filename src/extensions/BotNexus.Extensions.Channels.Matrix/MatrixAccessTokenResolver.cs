using System.IO.Abstractions;
using BotNexus.Domain.Paths;
using BotNexus.Domain.Security;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Extensions.Channels.Matrix;

/// <summary>Resolves file-backed credentials at the client boundary without mutating bound options.</summary>
internal sealed class MatrixAccessTokenResolver(IFileSystem fileSystem)
{
    public bool TryResolve(string? configured, out MatrixAccessToken token, out string? error)
    {
        token = default;
        error = null;
        if (!IsFileReference(configured))
            return MatrixAccessToken.TryCreate(configured, out token);

        // Never fall back to a literal after recognizing the scheme, including malformed references.
        if (!SecretRef.TryParse(configured, out var reference, out _))
        {
            error = "invalid secret file reference; an absolute path or home-relative path is required";
            return false;
        }

        try
        {
            var path = HomePathExpander.ExpandRequired(reference.Identifier);
            if (!fileSystem.Path.IsPathFullyQualified(path))
            {
                error = "secret file path must be absolute or start with ~/";
                return false;
            }

            path = fileSystem.Path.GetFullPath(path);
            if (!fileSystem.File.Exists(path))
            {
                error = "secret file is missing or inaccessible";
                return false;
            }

            if (SecureFilePermissions.IsReadableByOthers(fileSystem, path, failClosed: true))
            {
                error = "secret file must have verified owner-only permissions (chmod 600 on Unix)";
                return false;
            }

            var raw = fileSystem.File.ReadAllText(path).TrimEnd('\r', '\n');
            // Resolve once only. A pointer accidentally written to the file must never authenticate.
            if (IsFileReference(raw) || !MatrixAccessToken.TryCreate(raw, out token))
            {
                error = "secret file must contain a non-empty access token, not another file reference";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                   or NotSupportedException or InvalidOperationException
                                   or System.Security.SecurityException)
        {
            // Do not retain/log filesystem exceptions: messages can include attacker-controlled paths.
            error = "secret file could not be read or its permissions could not be verified";
            return false;
        }
    }

    private static bool IsFileReference(string? value) =>
        value?.TrimStart().StartsWith("file:", StringComparison.OrdinalIgnoreCase) == true;
}
