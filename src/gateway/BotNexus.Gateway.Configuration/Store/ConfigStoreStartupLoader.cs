using System.IO.Abstractions;
using BotNexus.Gateway.Configuration.Shadow;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Configuration.Store;

/// <summary>
/// Loads the startup <see cref="PlatformConfig"/> through the cutover seam, before DI exists (#3180).
///
/// <para>
/// <b>Why this exists at all.</b> The gateway needs a <see cref="PlatformConfig"/> while building its
/// host, which is strictly earlier than the service provider that would normally supply an
/// <see cref="IConfigDocumentSource"/>. Without this, the startup read is the one read in the process
/// that cannot honour <c>ConfigStoreAuthoritative</c> - and it is also the most important one, because
/// it decides what the platform actually runs as. A flag that every consumer respects except the
/// startup path is a flag that does not work.
/// </para>
///
/// <para>
/// <b>Deliberately not a second implementation.</b> This composes the same
/// <see cref="StoreBackedConfigDocumentSource"/>, <see cref="FileConfigShadowSource"/> and
/// <see cref="SqliteConfigStore"/> the DI graph registers, and defers every decision to them. Only the
/// flag evaluation differs: <see cref="IFeatureManager"/> is not available this early, so the flag is
/// read directly out of the configuration file. Re-implementing the fallback or precedence rules here
/// would create exactly the drift the single-seam design exists to prevent.
/// </para>
/// </summary>
public static class ConfigStoreStartupLoader
{
    /// <summary>
    /// Loads the platform configuration from whichever source the flag selects.
    /// </summary>
    /// <remarks>
    /// Synchronous by necessity - the host builder is not async at this point - and safe to block on
    /// because nothing else is running in the process yet, so there is no synchronization context to
    /// deadlock against.
    /// </remarks>
    /// <param name="configPath">Path to <c>config.json</c>; the fallback source and the flag source.</param>
    /// <param name="validateOnLoad">Whether to run schema and cross-field validation.</param>
    /// <param name="fileSystem">Injectable for tests.</param>
    public static PlatformConfig Load(
        string? configPath = null,
        bool validateOnLoad = true,
        IFileSystem? fileSystem = null)
    {
        var fs = fileSystem ?? new FileSystem();
        var path = configPath ?? PlatformConfigLoader.GetDefaultConfigPath(fs);

        var source = new StoreBackedConfigDocumentSource(
            new SqliteConfigStore($"Data Source={Path.Combine(GetDirectory(fs, path), "config.db")}"),
            new FileConfigShadowSource(fs, path),
            new StartupFlagAuthoritativeGate(fs, path),
            NullLogger<StoreBackedConfigDocumentSource>.Instance);

        return PlatformConfigLoader
            .LoadFromSourceAsync(source, path, validateOnLoad)
            .GetAwaiter()
            .GetResult();
    }

    private static string GetDirectory(IFileSystem fs, string path)
    {
        var dir = fs.Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(dir) ? PlatformConfigLoader.GetDefaultConfigDirectory(fs) : dir;
    }
}
