using BotNexus.Gateway.Configuration.Store;
using Microsoft.Extensions.Configuration;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Registers the SQLite configuration store as an ordinary <see cref="IConfigurationSource"/>
/// (#3485 D1).
/// </summary>
/// <remarks>
/// <para>
/// Precedence is <em>registration order</em>: add this after the JSON source and store values win,
/// add it before and the file wins. That replaces the <c>ConfigStoreAuthoritative</c> feature flag,
/// which had to be consulted inside a bespoke document source and consequently only affected the one
/// read that consulted it. A provider needs no flag because the pipeline already has an
/// answer for "which source wins".
/// </para>
/// </remarks>
public sealed class SqliteConfigurationSource : IConfigurationSource
{
    /// <summary>The store to read. Required.</summary>
    public IConfigStore? Store { get; init; }

    /// <summary>
    /// Invoked with a human-readable reason whenever a load is rejected and the previously loaded
    /// values are retained.
    /// </summary>
    public Action<string, Exception?>? OnLoadFailure { get; init; }

    /// <inheritdoc />
    public IConfigurationProvider Build(IConfigurationBuilder builder)
        => new SqliteConfigurationProvider(
            Store ?? throw new InvalidOperationException(
                $"{nameof(SqliteConfigurationSource)}.{nameof(Store)} must be set before the source is built."),
            OnLoadFailure);
}

/// <summary>
/// Registration helpers for the SQLite configuration source.
/// </summary>
public static class SqliteConfigurationExtensions
{
    /// <summary>
    /// Adds the SQLite configuration store to <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">The configuration builder.</param>
    /// <param name="store">The store to read.</param>
    /// <param name="onLoadFailure">Invoked when a load is rejected and previous values retained.</param>
    public static IConfigurationBuilder AddSqliteConfigStore(
        this IConfigurationBuilder builder,
        IConfigStore store,
        Action<string, Exception?>? onLoadFailure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(store);

        return builder.Add(new SqliteConfigurationSource
        {
            Store = store,
            OnLoadFailure = onLoadFailure,
        });
    }
}
