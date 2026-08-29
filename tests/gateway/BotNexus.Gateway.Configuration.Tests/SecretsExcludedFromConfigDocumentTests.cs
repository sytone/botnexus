using System.IO.Abstractions.TestingHelpers;
using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// Pins the separation between the file-per-secret store and the config document (#3528 AC8): a
/// secret written through <see cref="FileSecretStore"/> must not reach the config file, the backup
/// set, or anything derived from either.
/// </summary>
/// <remarks>
/// The separation is structural - secrets are files in their own directory, not nodes in the config
/// graph - which is precisely why it deserves a test. A structural property with no assertion is one
/// refactor away from being a coupling nobody noticed, and #3469 makes the config revision a digest
/// of the unredacted document, so a secret that leaked into it would leak into the revision too.
/// </remarks>
public sealed class SecretsExcludedFromConfigDocumentTests
{
    private const string Sentinel = "SENTINEL-c8f2a71d-DO-NOT-LEAK-THIS-VALUE";

    [Fact]
    public void A_written_secret_appears_in_no_config_backup()
    {
        var fs = new MockFileSystem();
        var configPath = @"C:\home\config.json";
        var backupsDir = @"C:\home\backups";
        fs.AddFile(configPath, new MockFileData("""{"gateway":{"listenUrl":"http://localhost:5000"}}"""));

        var secrets = new FileSecretStore(@"C:\home\secrets", fs);
        secrets.Set("third-party-token", Sentinel);

        new ConfigBackupService(backupsDir, fs).Backup(configPath, "test");

        var backups = fs.Directory.EnumerateFiles(backupsDir).ToList();
        backups.ShouldNotBeEmpty("Vacuity guard: with no backup written the leak assertion below " +
                                 "would pass over an empty set.");
        foreach (var backup in backups)
            fs.File.ReadAllText(backup).ShouldNotContain(Sentinel);
    }

    [Fact]
    public void A_written_secret_does_not_alter_the_config_file_on_disk()
    {
        var fs = new MockFileSystem();
        var configPath = @"C:\home\config.json";
        const string original = """{"gateway":{"listenUrl":"http://localhost:5000"}}""";
        fs.AddFile(configPath, new MockFileData(original));

        new FileSecretStore(@"C:\home\secrets", fs).Set("third-party-token", Sentinel);

        // Byte-for-byte, not merely "does not contain the sentinel": the claim is that the secret
        // store does not touch the config document at all, and an unrelated rewrite would be a
        // coupling worth failing on even if it happened not to leak this time.
        fs.File.ReadAllText(configPath).ShouldBe(original);
    }

    [Fact]
    public void The_secrets_directory_is_not_inside_the_config_backup_directory()
    {
        // If secrets lived under the backups directory the backup sweep would copy them, and the
        // exclusion above would hold only by accident of the current file layout.
        var home = new BotNexusHome(new MockFileSystem(), homePath: @"C:\home", dataPath: null);
        var backupsDir = Path.Combine(home.DataPath, "backups");

        home.SecretsPath.ShouldNotStartWith(backupsDir);
        backupsDir.ShouldNotStartWith(home.SecretsPath);
    }
}
