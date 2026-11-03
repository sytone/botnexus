using System.IO.Abstractions;

namespace BotNexus.Gateway.Configuration.Writers;

/// <summary>Constructs mandatory authority writers, with explicit nonphysical host injection.</summary>
public static class ConfigWriterFactory
{
    /// <summary>Opens selected authority, never a JSON-only or fallback writer.</summary>
    public static PlatformConfigWriter Create(string configPath, IFileSystem fileSystem,
        ConfigBackupService? backup = null, IConfigAuthorityFactory? authorityFactory = null)
    {
        var binding = CreateBinding(configPath, fileSystem, backup, authorityFactory);
        return new PlatformConfigWriter(configPath, fileSystem, backup, binding.Writer, binding.Authority.Store);
    }

    internal static (AuthorityConfigurationWriter Writer, ConfigAuthorityBinding Authority) CreateBinding(
        string configPath, IFileSystem fileSystem, ConfigBackupService? backup,
        IConfigAuthorityFactory? authorityFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentNullException.ThrowIfNull(fileSystem);
        var directory = fileSystem.Path.GetDirectoryName(configPath);
        if (string.IsNullOrWhiteSpace(directory))
            directory = ".";
        backup ??= new ConfigBackupService(fileSystem.Path.Combine(directory, "backups"), fileSystem);
        var authority = (authorityFactory ?? SqliteConfigAuthorityFactory.Instance)
            .OpenAsync(configPath, fileSystem).GetAwaiter().GetResult();
        ArgumentNullException.ThrowIfNull(authority.Store);
        var writer = new AuthorityConfigurationWriter(authority.Store, authority.Location, configPath, fileSystem, backup);
        return (writer, authority);
    }
}
