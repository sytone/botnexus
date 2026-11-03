using System.IO.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Configuration;

/// <summary>The single SQLite-only composition root for platform configuration.</summary>
public static class PlatformConfigurationSources
{
    /// <summary>Builds runtime options for the selected home, never from its JSON projection.</summary>
    public static IOptionsMonitor<PlatformConfig> BuildMonitorForHome(
        string homePath, Action<string, Exception?>? onLoadFailure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homePath);
        return BuildMonitor(Path.Combine(homePath, "config.json"), onLoadFailure);
    }

    /// <summary>Bootstraps verified authority and adds its sole runtime provider.</summary>
    public static IConfigurationBuilder AddPlatformConfiguration(
        this IConfigurationBuilder builder,
        string configPath,
        Action<string, Exception?>? onLoadFailure = null,
        IFileSystem? fileSystem = null,
        IConfigAuthorityFactory? authorityFactory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        var fs = fileSystem ?? new FileSystem();
        var authority = (authorityFactory ?? SqliteConfigAuthorityFactory.Instance)
            .OpenAsync(configPath, fs).GetAwaiter().GetResult();
        return builder.AddSqliteConfigStore(authority.Store, onLoadFailure);
    }

    /// <summary>Builds bound options through the authority provider.</summary>
    public static IOptionsMonitor<PlatformConfig> BuildMonitor(
        string configPath, Action<string, Exception?>? onLoadFailure = null)
        => BuildMonitor(configPath, new FileSystem(), onLoadFailure);

    /// <summary>Uses the supplied filesystem consistently for bootstrap and authority resolution.</summary>
    public static IOptionsMonitor<PlatformConfig> BuildMonitor(
        string configPath, IFileSystem fileSystem, Action<string, Exception?>? onLoadFailure = null,
        IConfigAuthorityFactory? authorityFactory = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        var configuration = new ConfigurationBuilder()
            .AddPlatformConfiguration(configPath, onLoadFailure, fileSystem, authorityFactory).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.Configure<PlatformConfig>(configuration);
        services.AddSingleton<IPostConfigureOptions<PlatformConfig>>(new PlatformConfigPostConfigure(configuration));
        return services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<PlatformConfig>>();
    }
}
