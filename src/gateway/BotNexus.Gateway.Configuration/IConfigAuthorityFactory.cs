using System.IO.Abstractions;
using BotNexus.Gateway.Configuration.Store;

namespace BotNexus.Gateway.Configuration;

/// <summary>A complete authority binding; location identifies the store, not an input projection.</summary>
public sealed record ConfigAuthorityBinding(IConfigStore Store, string Location);

/// <summary>
/// Explicit host seam for opening or bootstrapping configuration authority. Implementations must
/// return one stable authority per selected home, including initialized empty documents, and fail
/// closed on read/bootstrap errors. A projection must never become a runtime fallback.
/// </summary>
public interface IConfigAuthorityFactory
{
    /// <summary>Returns the selected home's initialized authority, or throws without fallback.</summary>
    Task<ConfigAuthorityBinding> OpenAsync(string configPath, IFileSystem fileSystem,
        CancellationToken cancellationToken = default);
}

/// <summary>The production default: mandatory physical SQLite with verified legacy bootstrap.</summary>
public sealed class SqliteConfigAuthorityFactory : IConfigAuthorityFactory
{
    /// <summary>Shared stateless factory; authority state is durable SQLite, not this singleton.</summary>
    public static SqliteConfigAuthorityFactory Instance { get; } = new();

    /// <inheritdoc />
    public async Task<ConfigAuthorityBinding> OpenAsync(string configPath, IFileSystem fileSystem,
        CancellationToken cancellationToken = default)
    {
        var store = await ConfigStoreBootstrap.EnsureAuthorityAsync(configPath, fileSystem, cancellationToken).ConfigureAwait(false);
        return new(store, ConfigStoreBootstrap.ResolveStorePath(configPath, fileSystem));
    }
}
