using System.Text.Json.Nodes;

namespace BotNexus.Integration.ConfigDiskE2E.Tests;

/// <summary>
/// Filesystem-side effects of a config write that only a real disk can prove: the backup copy is
/// physically created before replacement, the atomic temp file leaves no residue, and the
/// replacement does not corrupt or truncate the target.
/// </summary>
public sealed class ConfigWriteDurabilityDiskTests
{
    /// <summary>
    /// Every effective write must first copy the pre-write document into the backups directory,
    /// and that backup must be the <em>old</em> content - the recovery guarantee
    /// <c>PlatformConfigLoader.TryRecoverFromBackup</c> depends on.
    /// </summary>
    [Fact]
    public async Task EffectiveWrite_CreatesBackupContainingPreWriteContent()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        var originalText = home.ReadRawText();

        await home.Writer.UpdateSectionAsync(
            "gateway",
            new JsonObject { ["logLevel"] = "Debug" });

        var backups = home.ListBackups();
        backups.Count.ShouldBe(1);
        backups[0].ShouldContain("before-gateway-update");

        var backupText = File.ReadAllText(Path.Combine(home.BackupsPath, backups[0]));
        backupText.ShouldBe(originalText);
        home.ReadRawText().ShouldNotBe(originalText);
    }

    /// <summary>
    /// The writer stages into a <c>.tmp</c> sibling before an atomic move. After a successful
    /// write no temp file may remain: leftover temp files accumulate in the user's home directory
    /// and, worse, can be picked up by directory scans as if they were real config.
    /// </summary>
    [Fact]
    public async Task SuccessfulWrite_LeavesNoTempFilesInTheConfigDirectory()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);

        for (var i = 0; i < 5; i++)
        {
            await home.Writer.MutateAsync(
                root => root["cron"]!["tickIntervalSeconds"] = 60 + i + 1,
                $"test-temp-cleanup-{i}");
        }

        home.ListConfigDirectoryFiles().ShouldBe(["config.json"]);
        Directory.GetFiles(home.RootPath, "*.tmp").ShouldBeEmpty();
    }

    /// <summary>
    /// The replacement must be atomic from a reader's perspective: after the write the file is
    /// complete, well-formed JSON with no truncation and no duplicated tail from the previous
    /// (longer) document.
    /// </summary>
    [Fact]
    public async Task Write_ReplacesFileWholesaleWithoutTruncationResidue()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        var originalLength = new FileInfo(home.ConfigPath).Length;

        // Shrink the document substantially so a non-atomic in-place rewrite would leave a tail.
        await home.Writer.MutateAsync(
            root =>
            {
                root.Remove("customVendorBlock");
                root.Remove("channels");
                root.Remove("agents");
            },
            "test-shrink");

        var afterLength = new FileInfo(home.ConfigPath).Length;
        afterLength.ShouldBeLessThan(originalLength);

        var text = home.ReadRawText();
        Should.NotThrow(() => JsonNode.Parse(text));
        text.TrimEnd().ShouldEndWith("}");
        text.ShouldNotContain("preserve-me");
    }

    /// <summary>
    /// Backups are retained up to a cap and then pruned oldest-first. Exercised against real
    /// files because the prune orders by filesystem creation time, which an in-memory filesystem
    /// models only approximately.
    /// </summary>
    [Fact]
    public void BackupService_PrunesToTheRetentionCapOnDisk()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);

        Directory.CreateDirectory(home.BackupsPath);
        for (var i = 0; i < BotNexus.Gateway.Configuration.ConfigBackupService.MaxBackups + 5; i++)
        {
            var path = Path.Combine(home.BackupsPath, $"config-2020010{i % 10}-00000{i % 10}-seed-{i}.json");
            File.WriteAllText(path, MaximalConfig.Json);
            File.SetCreationTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i));
        }

        home.BackupService.Backup(home.ConfigPath, "test-prune");

        home.ListBackups().Count.ShouldBeLessThanOrEqualTo(
            BotNexus.Gateway.Configuration.ConfigBackupService.MaxBackups);
    }

    /// <summary>
    /// Writing to a home directory whose config file does not yet exist must create it (and its
    /// directory) rather than throwing - the first-run path. No backup is possible or expected.
    /// </summary>
    [Fact]
    public async Task Write_WhenConfigFileDoesNotExist_CreatesItOnDisk()
    {
        using var home = new ConfigHomeFixture(seedJson: null);
        File.Exists(home.ConfigPath).ShouldBeFalse();

        await home.Writer.UpdateSectionAsync(
            "gateway",
            new JsonObject { ["listenUrl"] = "http://localhost:5005" });

        File.Exists(home.ConfigPath).ShouldBeTrue();
        home.ReadFromDisk()["gateway"]!["listenUrl"]!.GetValue<string>()
            .ShouldBe("http://localhost:5005");
        home.ListBackups().ShouldBeEmpty();
    }
}
