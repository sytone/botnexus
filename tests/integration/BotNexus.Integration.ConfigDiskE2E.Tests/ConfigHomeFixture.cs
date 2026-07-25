using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BotNexus.Integration.ConfigDiskE2E.Tests;

/// <summary>
/// A disposable, physically real BotNexus home: a temporary directory containing an actual
/// <c>config.json</c>, an actual <c>backups/</c> directory, and the production writer/loader
/// stack bound to it through the real <see cref="FileSystem"/>.
/// </summary>
/// <remarks>
/// This deliberately does NOT use <c>MockFileSystem</c>. The entire point of #2066 is that an
/// in-memory filesystem cannot observe OS replacement semantics, temp-file residue, backup
/// creation, file-watcher-driven <see cref="IOptionsMonitor{TOptions}"/> reload, or two writers
/// racing on the same inode. Every test in this suite therefore drives the production writer at
/// a genuine path under <see cref="Path.GetTempPath"/>, with <c>BOTNEXUS_HOME</c> pointed at it
/// so any code that resolves the home directory (rather than taking an explicit path) also lands
/// inside the sandbox.
/// </remarks>
public sealed class ConfigHomeFixture : IDisposable
{
    private readonly string? _previousHome;
    private readonly List<IDisposable> _disposables = [];

    /// <summary>
    /// Creates the temporary home, writes <paramref name="seedJson"/> as the initial
    /// <c>config.json</c>, and points <c>BOTNEXUS_HOME</c> at it for the lifetime of the fixture.
    /// </summary>
    public ConfigHomeFixture(string? seedJson = null)
    {
        FileSystem = new FileSystem();
        RootPath = Path.Combine(
            Path.GetTempPath(), "botnexus-config-disk-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);

        BackupsPath = Path.Combine(RootPath, "backups");
        ConfigPath = Path.Combine(RootPath, "config.json");

        _previousHome = Environment.GetEnvironmentVariable(BotNexusHome.HomeOverrideEnvVar);
        Environment.SetEnvironmentVariable(BotNexusHome.HomeOverrideEnvVar, RootPath);

        if (seedJson is not null)
            File.WriteAllText(ConfigPath, seedJson);

        BackupService = new ConfigBackupService(BackupsPath, FileSystem);
        Writer = new PlatformConfigWriter(ConfigPath, FileSystem, BackupService);
    }

    /// <summary>The real (non-mocked) filesystem the production types write through.</summary>
    public IFileSystem FileSystem { get; }

    /// <summary>Temporary BOTNEXUS_HOME root for this test.</summary>
    public string RootPath { get; }

    /// <summary>Physical path of the config file under test.</summary>
    public string ConfigPath { get; }

    /// <summary>Physical path of the backups directory the writer prunes into.</summary>
    public string BackupsPath { get; }

    /// <summary>Production backup service wired to <see cref="BackupsPath"/>.</summary>
    public ConfigBackupService BackupService { get; }

    /// <summary>Production writer under test, bound to <see cref="ConfigPath"/>.</summary>
    public PlatformConfigWriter Writer { get; }

    /// <summary>Reads the raw bytes-as-text currently on disk (no parsing, no normalisation).</summary>
    public string ReadRawText() => File.ReadAllText(ConfigPath);

    /// <summary>Reads and parses the physical file, bypassing the writer's own read path.</summary>
    public JsonObject ReadFromDisk()
        => JsonNode.Parse(ReadRawText())?.AsObject()
           ?? throw new InvalidOperationException($"'{ConfigPath}' did not contain a JSON object.");

    /// <summary>Files currently present in the config directory (used to assert temp cleanup).</summary>
    public IReadOnlyList<string> ListConfigDirectoryFiles()
        => [.. Directory.GetFiles(RootPath).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];

    /// <summary>Backup files currently retained, oldest name first.</summary>
    public IReadOnlyList<string> ListBackups()
        => Directory.Exists(BackupsPath)
            ? [.. Directory.GetFiles(BackupsPath, "config-*.json").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)]
            : [];

    /// <summary>
    /// Builds the real host configuration pipeline over the physical file exactly as
    /// <c>BotNexus.Gateway.Api/Program.cs</c> does (<c>AddJsonFile(..., reloadOnChange: true)</c>)
    /// and returns a runtime consumer view: an <see cref="IOptionsMonitor{TOptions}"/> of
    /// <see cref="PlatformConfig"/> including the production <see cref="PlatformConfigPostConfigure"/>
    /// normalisation and the production <see cref="PlatformConfigOptionsValidator"/>.
    /// </summary>
    public RuntimeConsumer BuildRuntimeConsumer()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(ConfigPath, optional: true, reloadOnChange: true)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddOptions<PlatformConfig>().Bind(configuration);
        services.AddSingleton<IPostConfigureOptions<PlatformConfig>>(
            new PlatformConfigPostConfigure(configuration, ConfigPath));
        services.AddSingleton<IValidateOptions<PlatformConfig>, PlatformConfigOptionsValidator>();

        var provider = services.BuildServiceProvider();
        var consumer = new RuntimeConsumer(configuration, provider);
        _disposables.Add(consumer);
        return consumer;
    }

    /// <summary>Serialises a JSON node with the same indentation the writer persists.</summary>
    public static string Pretty(JsonNode node)
        => node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var disposable in _disposables)
            disposable.Dispose();

        Environment.SetEnvironmentVariable(BotNexusHome.HomeOverrideEnvVar, _previousHome);

        try
        {
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
        }
        catch (IOException)
        {
            // A file watcher may still hold a handle on some platforms; leaking a temp directory
            // is preferable to failing an otherwise-green test on teardown.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
