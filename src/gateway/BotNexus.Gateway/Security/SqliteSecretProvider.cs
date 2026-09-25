using BotNexus.Domain.Security;
using BotNexus.Gateway.Configuration;
using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Security;

/// <summary>
/// Resolves <c>sqlite:name</c> from a secrets table beside the rest of the BotNexus home.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not encrypted at rest, and it is not a security improvement over
/// <c>file:</c>.</b> SQLite stores the value in plaintext in the database file; what this buys is
/// operational convenience - one artifact to back up and move rather than a directory of
/// single-secret files, and a place to keep secrets that survives a home directory being synced
/// around. The file is restricted to its owner on write, which is the same protection a
/// <c>file:</c> secret gets, and the same as its ceiling.
/// </para>
/// <para>
/// Populated with <c>botnexus secret set</c>. A store nobody can write to would make this backend
/// decorative, so the CLI verb is part of the same change.
/// </para>
/// </remarks>
public sealed class SqliteSecretProvider : ISecretProvider
{
    /// <summary>Canonical file name of the secret store inside the BotNexus home.</summary>
    public const string StoreFileName = "secrets" + SqliteStorePathPolicy.CanonicalExtension;

    private readonly Func<string> _resolveStorePath;

    /// <summary>Creates a provider reading the store in the resolved BotNexus home.</summary>
    public SqliteSecretProvider(BotNexusHome home)
    {
        ArgumentNullException.ThrowIfNull(home);
        _resolveStorePath = () => SqliteStorePathPolicy.ResolveOwnedStorePath(home.RootPath, "secrets");
    }

    /// <summary>Creates a provider over an explicit store path, for tests.</summary>
    public SqliteSecretProvider(string storePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        _resolveStorePath = () => storePath;
    }

    /// <inheritdoc />
    public string Scheme => "sqlite";

    /// <inheritdoc />
    public async Task<Secret> ResolveAsync(SecretRef reference, CancellationToken cancellationToken = default)
    {
        var storePath = _resolveStorePath();

        // A missing store is a configuration mistake with an obvious remedy, so say the remedy
        // rather than letting SQLite report a file it could not open.
        if (!File.Exists(storePath))
        {
            throw new SecretResolutionException(
                reference,
                $"no secret store at '{storePath}'. Create one with: botnexus secret set {reference.Identifier}");
        }

        string? value;
        try
        {
            await using var connection = SqliteConnectionFactory.Create(
                new SqliteConnectionStringBuilder { DataSource = storePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM secrets WHERE name = $name LIMIT 1;";
            command.Parameters.AddWithValue("$name", reference.Identifier);

            value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }
        catch (SqliteException ex)
        {
            // Includes "no such table", which is what an empty or foreign database looks like.
            throw new SecretResolutionException(reference, $"'{storePath}' could not be read: {ex.Message}", ex);
        }

        if (string.IsNullOrEmpty(value))
        {
            throw new SecretResolutionException(
                reference,
                $"'{reference.Identifier}' is not in the secret store. Add it with: botnexus secret set {reference.Identifier}");
        }

        if (!Secret.TryCreate(value, out var secret))
        {
            throw new SecretResolutionException(
                reference,
                $"'{reference.Identifier}' holds a value longer than the {Secret.MaxLength} character limit.");
        }

        return secret;
    }
}
