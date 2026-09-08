using System.IO.Abstractions;
using BotNexus.Gateway.Configuration.Store;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// The single composition root for the platform configuration read pipeline (#3504).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Before it, the gateway built the provider pipeline in <c>Program.cs</c>
/// while seventeen other call sites - fourteen of them CLI commands - read and bound
/// <c>config.json</c> by hand through <c>PlatformConfigLoader</c>. Those hand-loads could not see
/// the SQLite store, got no hot reload, and did not benefit from the last-known-good protection in
/// <see cref="ResilientJsonConfigurationSource"/> (#2358), because all three of those live in the
/// provider pipeline rather than in the file read.
/// </para>
/// <para>
/// <b>Provider order is the precedence rule.</b> JSON first, store second, so any key the store
/// holds wins. That is the whole of the rule - there is no flag, no gate, and no BotNexus code that
/// decides which source is authoritative. Registering the store only when <c>config.db</c> exists
/// keeps an installation that never enabled it byte-identical to file-only behaviour.
/// </para>
/// <para>
/// <b>Everything after this point is framework.</b> Binding, change tokens, reload, and
/// <c>IOptionsMonitor</c> notification are all <c>Microsoft.Extensions.Configuration</c>'s job. This
/// class adds sources and stops.
/// </para>
/// </remarks>
public static class PlatformConfigurationSources
{
    /// <summary>
    /// Adds the platform configuration sources for <paramref name="configPath"/>, in precedence
    /// order.
    /// </summary>
    /// <param name="builder">The configuration builder.</param>
    /// <param name="configPath">Absolute path to <c>config.json</c>.</param>
    /// <param name="onLoadFailure">
    /// Invoked with a human-readable reason when a source rejects a load and retains its previously
    /// loaded values. Null discards the diagnostic, which is appropriate for short-lived processes
    /// with no logging pipeline yet.
    /// </param>
    /// <param name="fileSystem">
    /// Filesystem used for the store-existence probe. Callers that inject a filesystem - the CLI
    /// commands under test do - must pass it, or the probe hits the real disk while everything else
    /// reads the mock, and the resulting configuration is neither.
    /// </param>
    public static IConfigurationBuilder AddPlatformConfiguration(
        this IConfigurationBuilder builder,
        string configPath,
        Action<string, Exception?>? onLoadFailure = null,
        IFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        var fs = fileSystem ?? new FileSystem();

        builder.AddResilientJsonFile(
            configPath,
            optional: true,
            reloadOnChange: true,
            onLoadFailure: onLoadFailure);

        // #3514: registered whenever the store FILE exists, and the file is created by an explicit
        // operator action (`botnexus config store enable`) rather than by a startup side effect.
        //
        // The existence check is a genuine signal, not a guard against cost: the store file existing
        // IS the opt-in. What was broken was that nothing could ever create it - the only writer was
        // the shadow migration deleted in #3510 - so this branch was unreachable by any supported
        // action. Providing the writer is what makes the check meaningful.
        var directory = fs.Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(directory))
        {
            var storePath = fs.Path.Combine(directory, ConfigStoreBootstrap.StoreFileName);
            if (fs.File.Exists(storePath))
            {
                builder.AddSqliteConfigStore(
                    new SqliteConfigStore($"Data Source={storePath}"),
                    onLoadFailure);
            }
        }

        return builder;
    }

    /// <summary>
    /// Builds a bound <see cref="PlatformConfig"/> for a short-lived process that has no host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the CLI, whose configuration path is chosen per invocation by <c>--target</c> and is
    /// therefore not known when the service container is built. It returns an
    /// <see cref="IOptionsMonitor{T}"/> rather than a bare instance so callers use the same type the
    /// gateway does; a CLI process is short-lived and will rarely observe a reload, but having one
    /// read type across both hosts is what stops a second read path re-appearing.
    /// </para>
    /// <para>
    /// This is composition, not loading. Every step - parse, bind, validate, fall back to
    /// last-known-good - is performed by the framework and by the sources added above.
    /// </para>
    /// </remarks>
    public static IOptionsMonitor<PlatformConfig> BuildMonitor(
        string configPath,
        Action<string, Exception?>? onLoadFailure = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddPlatformConfiguration(configPath, onLoadFailure)
            .Build();

        return BuildMonitor(configuration, configPath);
    }

    /// <summary>
    /// Builds a bound <see cref="PlatformConfig"/> reading through an injected
    /// <paramref name="fileSystem"/> rather than the physical disk (#3824).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a second overload rather than a parameter on the first.</b> The framework's JSON file
    /// provider is backed by a PHYSICAL file provider and cannot read an
    /// <see cref="IFileSystem"/> abstraction. Two CLI call sites take an injected filesystem, and
    /// that is precisely why they were left behind when the other twelve migrated: routing them
    /// through the physical pipeline would read the real disk while the caller supplied a mock.
    /// </para>
    /// <para>
    /// The fix is to feed the framework a STREAM obtained from the injected filesystem
    /// (<c>AddJsonStream</c>), then add the SQLite store in the same precedence order as everywhere
    /// else. Parsing, binding and precedence remain the framework's job; this still adds sources and
    /// stops. What it gives up relative to the physical overload is hot reload and the
    /// last-known-good retention of <see cref="ResilientJsonConfigurationSource"/>, neither of which
    /// is reachable for a stream in a short-lived CLI process anyway - and unlike the hand-load it
    /// replaces, it can see the store.
    /// </para>
    /// </remarks>
    public static IOptionsMonitor<PlatformConfig> BuildMonitor(
        string configPath,
        IFileSystem fileSystem,
        Action<string, Exception?>? onLoadFailure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentNullException.ThrowIfNull(fileSystem);

        var builder = new ConfigurationBuilder();

        if (fileSystem.File.Exists(configPath))
        {
            // Copied into memory deliberately. AddJsonStream does not read at Add time - the provider
            // reads when the builder is built, which happens below - so handing it a stream we then
            // close yields an empty, silently wrong configuration.
            var buffer = new MemoryStream(fileSystem.File.ReadAllBytes(configPath));
            builder.AddJsonStream(buffer);
        }

        var directory = fileSystem.Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(directory))
        {
            var storePath = fileSystem.Path.Combine(directory, ConfigStoreBootstrap.StoreFileName);
            if (fileSystem.File.Exists(storePath))
            {
                builder.AddSqliteConfigStore(
                    new SqliteConfigStore($"Data Source={storePath}"),
                    onLoadFailure);
            }
        }

        // The raw-JSON post-configure step reads the physical file directly, so it is given no path
        // here: an injected filesystem's file may not exist on disk at all. It falls back to the
        // provider scan, finds no physical JSON provider, and leaves the bound values alone.
        return BuildMonitor(builder.Build(), configFilePath: null);
    }

    private static IOptionsMonitor<PlatformConfig> BuildMonitor(
        IConfiguration configuration,
        string? configFilePath)
    {
        var services = new ServiceCollection();
        // IConfiguration must be registered, not merely used to bind: PlatformConfigPostConfigure
        // resolves it to re-read raw JSON for the parts of the document a POCO binding cannot carry
        // (agent raw elements, and the absent-vs-explicit-null distinction). Omitting it produced
        // "Unable to resolve service for type 'IConfiguration'" at the first options access, which
        // surfaced to the operator as "Unable to load config".
        services.AddSingleton<IConfiguration>(configuration);
        services.Configure<PlatformConfig>(configuration);
        services.AddSingleton<IPostConfigureOptions<PlatformConfig>>(
            new PlatformConfigPostConfigure(configuration, configFilePath));

        return services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<PlatformConfig>>();
    }
}
