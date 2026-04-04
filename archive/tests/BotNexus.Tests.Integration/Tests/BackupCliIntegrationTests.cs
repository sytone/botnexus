using System.IO.Compression;
using System.Text;
using FluentAssertions;

namespace BotNexus.Tests.Integration.Tests;

[Collection("cli-integration")]
public sealed class BackupCliIntegrationTests
{
    private static string BackupsDir(string homePath) =>
        homePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "-backups";

    [Fact]
    [Trait("Category", "CLI")]
    public async Task BackupCreate_CreatesZipFile_AndReturnsZero()
    {
        await using var home = await CliHomeScope.CreateAsync();

        var result = await CliTestHost.RunCliAsync("backup create", home.Path);

        result.ExitCode.Should().Be(0);
        Directory.Exists(BackupsDir(home.Path)).Should().BeTrue();
        Directory.GetFiles(BackupsDir(home.Path), "*.zip").Should().ContainSingle();
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task BackupCreate_IncludesConfigAndAgents_InZip()
    {
        await using var home = await CliHomeScope.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(home.Path, "config.json"), """{"BotNexus":{"Name":"test"}}""");
        var agentPath = Path.Combine(home.Path, "agents", "alpha");
        Directory.CreateDirectory(agentPath);
        await File.WriteAllTextAsync(Path.Combine(agentPath, "SOUL.md"), "Alpha");

        var result = await CliTestHost.RunCliAsync("backup create", home.Path);

        result.ExitCode.Should().Be(0);
        var backupPath = GetMostRecentBackup(home.Path);
        using var archive = ZipFile.OpenRead(backupPath);
        var names = archive.Entries.Select(e => NormalizeZipEntry(e.FullName)).ToArray();
        names.Should().Contain("config.json");
        names.Should().Contain("agents/alpha/SOUL.md");
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task BackupCreate_ExcludesLogs_FromZip()
    {
        await using var home = await CliHomeScope.CreateAsync();
        Directory.CreateDirectory(Path.Combine(home.Path, "logs"));
        await File.WriteAllTextAsync(Path.Combine(home.Path, "logs", "gateway.log"), "test");
        await File.WriteAllTextAsync(Path.Combine(home.Path, "keep.txt"), "keep");

        var result = await CliTestHost.RunCliAsync("backup create", home.Path);

        result.ExitCode.Should().Be(0);
        var backupPath = GetMostRecentBackup(home.Path);
        using var archive = ZipFile.OpenRead(backupPath);
        var names = archive.Entries.Select(e => NormalizeZipEntry(e.FullName)).ToArray();
        names.Should().NotContain(n => n.StartsWith("logs/", StringComparison.OrdinalIgnoreCase));
        names.Should().Contain("keep.txt");
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task BackupCreate_WithOutputOption_WritesToSpecifiedPath()
    {
        await using var home = await CliHomeScope.CreateAsync();
        var outputDir = Path.Combine(Path.GetTempPath(), $"botnexus-backup-output-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, "my-backup.zip");

        try
        {
            var result = await CliTestHost.RunCliAsync($"backup create --output \"{outputPath}\"", home.Path);

            result.ExitCode.Should().Be(0);
            File.Exists(outputPath).Should().BeTrue();
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task BackupCreate_EmptyHome_StillSucceeds()
    {
        await using var home = await CliHomeScope.CreateAsync();

        var result = await CliTestHost.RunCliAsync("backup create", home.Path);

        result.ExitCode.Should().Be(0);
        var backupPath = GetMostRecentBackup(home.Path);
        using var archive = ZipFile.OpenRead(backupPath);
        archive.Entries.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task BackupList_ShowsAvailableBackups_InTable()
    {
        await using var home = await CliHomeScope.CreateAsync();
        var backupsPath = BackupsDir(home.Path);
        Directory.CreateDirectory(backupsPath);
        CreateZipWithEntry(Path.Combine(backupsPath, "backup-a.zip"), "a.txt", "a");
        CreateZipWithEntry(Path.Combine(backupsPath, "backup-b.zip"), "b.txt", "b");

        var result = await CliTestHost.RunCliAsync("backup list", home.Path);

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Contain("backup-a.zip").And.Contain("backup-b.zip");
        result.StdOut.Should().Match(s =>
            s.Contains("name", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("size", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task BackupList_NoBackups_ShowsMessage()
    {
        await using var home = await CliHomeScope.CreateAsync();

        var result = await CliTestHost.RunCliAsync("backup list", home.Path);

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Contain("No backups found");
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task BackupRestore_RestoresFiles_FromZip()
    {
        await using var home = await CliHomeScope.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(home.Path, "config.json"), """{"before":true}""");
        Directory.CreateDirectory(Path.Combine(home.Path, "agents", "alpha"));
        await File.WriteAllTextAsync(Path.Combine(home.Path, "agents", "alpha", "SOUL.md"), "Before");
        _ = await CliTestHost.RunCliAsync("backup create", home.Path);
        var backupPath = GetMostRecentBackup(home.Path);

        File.Delete(Path.Combine(home.Path, "config.json"));
        Directory.Delete(Path.Combine(home.Path, "agents"), recursive: true);

        var restoreResult = await CliTestHost.RunCliAsync($"backup restore \"{backupPath}\"", home.Path, "y\n");

        restoreResult.ExitCode.Should().Be(0);
        File.Exists(Path.Combine(home.Path, "config.json")).Should().BeTrue();
        File.Exists(Path.Combine(home.Path, "agents", "alpha", "SOUL.md")).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task BackupRestore_WithForce_SkipsConfirmation()
    {
        await using var home = await CliHomeScope.CreateAsync();
        var sourceBackupPath = Path.Combine(Path.GetTempPath(), $"botnexus-restore-src-{Guid.NewGuid():N}.zip");
        CreateZipWithEntry(sourceBackupPath, "config.json", """{"restored":true}""");

        try
        {
            var result = await CliTestHost.RunCliAsync($"backup restore \"{sourceBackupPath}\" --force", home.Path);

            result.ExitCode.Should().Be(0);
            File.Exists(Path.Combine(home.Path, "config.json")).Should().BeTrue();
            (await File.ReadAllTextAsync(Path.Combine(home.Path, "config.json"))).Should().Contain("restored");
        }
        finally
        {
            try { File.Delete(sourceBackupPath); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task BackupRestore_CreatesPreRestoreBackup()
    {
        await using var home = await CliHomeScope.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(home.Path, "marker.txt"), "before-restore");
        var sourceBackupPath = Path.Combine(Path.GetTempPath(), $"botnexus-restore-src-{Guid.NewGuid():N}.zip");
        CreateZipWithEntry(sourceBackupPath, "marker.txt", "after-restore");

        try
        {
            var result = await CliTestHost.RunCliAsync($"backup restore \"{sourceBackupPath}\" --force", home.Path);

            result.ExitCode.Should().Be(0);
            var preRestoreBackup = Directory.GetFiles(BackupsDir(home.Path), "*.zip")
                .FirstOrDefault(path => ContainsEntryWithContent(path, "marker.txt", "before-restore"));
            preRestoreBackup.Should().NotBeNull();
        }
        finally
        {
            try { File.Delete(sourceBackupPath); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "CLI")]
    public async Task BackupRestore_MissingFile_ReturnsError()
    {
        await using var home = await CliHomeScope.CreateAsync();
        var missingPath = Path.Combine(Path.GetTempPath(), "botnexus-missing-backup.zip");

        var result = await CliTestHost.RunCliAsync($"backup restore \"{missingPath}\" --force", home.Path);

        result.ExitCode.Should().Be(1);
    }

    private static string GetMostRecentBackup(string homePath)
    {
        return Directory.GetFiles(BackupsDir(homePath), "*.zip")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();
    }

    private static string NormalizeZipEntry(string entryPath) => entryPath.Replace('\\', '/');

    private static void CreateZipWithEntry(string zipPath, string entryName, string content)
    {
        var parent = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static bool ContainsEntryWithContent(string zipPath, string entryName, string expectedContent)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.FirstOrDefault(e =>
            string.Equals(NormalizeZipEntry(e.FullName), NormalizeZipEntry(entryName), StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return false;

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return string.Equals(reader.ReadToEnd(), expectedContent, StringComparison.Ordinal);
    }
}
