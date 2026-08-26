using System.IO.Abstractions;
using BotNexus.Gateway.Configuration.Store;

namespace BotNexus.Gateway.Configuration.Writers;

/// <summary>
/// Builds configuration writers that persist to every store backing a given config path (#3527).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only correct way to construct a config writer.</b> Calling the
/// <see cref="PlatformConfigWriter"/> constructor directly yields a JSON-only writer, which is wrong
/// for any installation that has enabled the store: the store wins on read, so a file-only write
/// leaves the operator's change invisible while the caller reports success.
/// </para>
/// <para>
/// That is not hypothetical. Seven call sites across the CLI and the API constructed writers
/// directly, and a registration test caught the resulting split - the file updated while the store
/// kept serving the previous value. A shared factory is what stops the eighth.
/// </para>
/// </remarks>
public static class ConfigWriterFactory
{
    /// <summary>
    /// Creates a writer for <paramref name="configPath"/>, including the SQLite backend when the
    /// store exists.
    /// </summary>
    /// <param name="configPath">Absolute path to <c>config.json</c>.</param>
    /// <param name="fileSystem">Filesystem abstraction.</param>
    /// <param name="backup">
    /// Backup service for the JSON backend. When null, one is created under <c>backups/</c> beside
    /// the config file; pass an instance to control that location.
    /// </param>
    /// <remarks>
    /// The SQLite backend is added only when <c>config.db</c> exists, matching the read side exactly -
    /// the file existing IS the opt-in, created by <c>botnexus config store enable</c> (#3514). With
    /// no store this returns a JSON-only writer, byte-identical to the previous behaviour.
    /// </remarks>
    public static PlatformConfigWriter Create(
        string configPath,
        IFileSystem fileSystem,
        ConfigBackupService? backup = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentNullException.ThrowIfNull(fileSystem);

        var directory = fileSystem.Path.GetDirectoryName(configPath);
        if (string.IsNullOrWhiteSpace(directory))
            directory = PlatformConfigLoader.GetDefaultConfigDirectory(fileSystem);

        backup ??= new ConfigBackupService(fileSystem.Path.Combine(directory, "backups"), fileSystem);

        var writers = new List<IConfigurationWriter>
        {
            new JsonConfigurationWriter(configPath, fileSystem, backup),
        };

        var storePath = fileSystem.Path.Combine(directory, ConfigStoreBootstrap.StoreFileName);
        if (fileSystem.File.Exists(storePath))
            writers.Add(new SqliteConfigurationWriter(new SqliteConfigStore($"Data Source={storePath}")));

        var writer = writers.Count == 1 ? writers[0] : new FanOutConfigurationWriter(writers);
        return new PlatformConfigWriter(configPath, fileSystem, backup, writer);
    }
}
