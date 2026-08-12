using System.Text.Json;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// The identity of the BotNexus world this process belongs to, resolved <b>once</b> at startup and
/// injected wherever it is needed.
/// </summary>
/// <remarks>
/// <para><b>Why a type and not a config read.</b> This mirrors <see cref="BotNexusHome"/>: the home
/// path is resolved once and handed out as a dependency rather than re-derived by every consumer.
/// The same discipline is load-bearing here for a sharper reason (#2834). A world ID exists so a
/// store can assert "you are not my world". If each consumer re-derived the ID from configuration
/// independently, a broken resolver would produce the same wrong answer in the identity path and in
/// the path-resolution path simultaneously - both would agree, the guard would pass, and the data
/// would still be wrong. That one-value-two-derivations shape is the recurring defect family behind
/// #2796, #2792, #2748 and #2793, and it would silently defeat the identity guard entirely.</para>
/// <para>Consumers therefore take a <see cref="WorldId"/> dependency. They do not read
/// <c>worldId</c> from <c>IConfiguration</c> or from <see cref="PlatformConfig"/>; an architecture
/// fence enforces that.</para>
/// </remarks>
public sealed class WorldId
{
    /// <summary>Creates a world identity for an already-resolved GUID.</summary>
    public WorldId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("World identity must not be the empty GUID.", nameof(value));

        Id = value;
    }

    /// <summary>The world's stable GUID.</summary>
    public Guid Id { get; }

    /// <summary>The canonical string form persisted to <c>config.json</c> (lowercase, hyphenated).</summary>
    public string Value => Id.ToString("D");

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// The single place a world ID is derived from a home's <c>config.json</c>.
/// </summary>
/// <remarks>
/// Reading the raw document rather than a bound <see cref="PlatformConfig"/> keeps this usable from
/// the CLI (<c>botnexus doctor</c>) and from gateway startup before the options pipeline exists,
/// without a second derivation appearing in either.
/// </remarks>
public static class WorldIdResolver
{
    /// <summary>The <c>config.json</c> property name carrying the world ID.</summary>
    public const string ConfigPropertyName = "worldId";

    /// <summary>
    /// Reads the world ID already persisted in <paramref name="configPath"/>, or <see langword="null"/>
    /// when the file is missing, unreadable, or carries no usable <c>worldId</c>. Never writes.
    /// </summary>
    public static WorldId? TryRead(string configPath, IFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentNullException.ThrowIfNull(fileSystem);

        if (!fileSystem.File.Exists(configPath))
            return null;

        string raw;
        try
        {
            raw = fileSystem.File.ReadAllText(configPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            if (!document.RootElement.TryGetProperty(ConfigPropertyName, out var element))
                return null;
            if (element.ValueKind != JsonValueKind.String)
                return null;

            var text = element.GetString();
            return Guid.TryParse(text, out var parsed) && parsed != Guid.Empty
                ? new WorldId(parsed)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves the world identity for a home: the persisted value when present, otherwise a freshly
    /// generated one. This performs <b>no</b> write - persistence is a separate, explicit step
    /// (<see cref="WorldIdPersistenceService"/>) so the value handed to DI and the value written
    /// to disk can never be two different derivations.
    /// </summary>
    /// <param name="generated">
    /// <see langword="true"/> when no usable ID existed and a new one was minted.
    /// </param>
    public static WorldId Resolve(string configPath, IFileSystem fileSystem, out bool generated)
    {
        var existing = TryRead(configPath, fileSystem);
        if (existing is not null)
        {
            generated = false;
            return existing;
        }

        generated = true;
        return new WorldId(Guid.NewGuid());
    }
}
