using System.Text.Json.Nodes;
using BotNexus.Cli.Commands;
using BotNexus.Gateway.Configuration;
using Shouldly;
using Spectre.Console;

namespace BotNexus.Cli.Tests;

/// <summary>
/// Command-boundary acceptance tests for issue #2884: <c>botnexus config backups list</c> and
/// <c>botnexus config restore</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>ConfigBackupRestoreServiceTests</c> covers the restore algebra against the service directly.
/// These tests exist for the part that only fails at the command boundary: that the CLI resolves the
/// backups directory to the same place the writer backs up <em>into</em>, that the exit codes are
/// right, and above all that <b>the commit is opt-in</b>. A restore that defaulted to committing
/// would pass every service-level test and still be the defect.
/// </para>
/// <para>
/// Each test uses a REAL temporary home directory rather than a mock filesystem, because the
/// directory-resolution behaviour under test is precisely the thing a mock would paper over.
/// </para>
/// </remarks>
[Collection("AnsiConsole")]
public sealed class ConfigBackupRestoreCommandTests : IDisposable
{
    private const string SeedConfig = """
        {
          "version": 1,
          "gateway": {
            "listenUrl": "http://localhost:5099",
            "defaultAgentId": "assistant"
          },
          "providers": {
            "anthropic": { "api": "anthropic", "apiKey": "sk-live-real-secret" }
          }
        }
        """;

    private readonly string _rootPath;
    private readonly string _configPath;
    private readonly string _backupsDir;
    private readonly IAnsiConsole _originalConsole;
    private readonly StringWriter _output;

    public ConfigBackupRestoreCommandTests()
    {
        _originalConsole = AnsiConsole.Console;
        _output = new StringWriter();
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(_output),
            Interactive = InteractionSupport.No
        });

        // Spectre truncates table cells to the console width, and the default width under a
        // StringWriter is narrow enough to elide the reason column. Widen it so the assertions can
        // stay strict about the actual rendered content rather than being loosened to accommodate
        // an artefact of the test harness.
        AnsiConsole.Console.Profile.Width = 300;

        _rootPath = Path.Combine(Path.GetTempPath(), "botnexus-2884-tests", Guid.NewGuid().ToString("N"));
        _backupsDir = Path.Combine(_rootPath, "backups");
        Directory.CreateDirectory(_backupsDir);
        _configPath = Path.Combine(_rootPath, "config.json");
        File.WriteAllText(_configPath, SeedConfig);
    }

    public void Dispose()
    {
        AnsiConsole.Console = _originalConsole;
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private string SeedBackup(string stamp, string reason, string content)
    {
        var name = $"config-{stamp}-{reason}.json";
        File.WriteAllText(Path.Combine(_backupsDir, name), content);
        return Path.GetFileNameWithoutExtension(name);
    }

    private static ConfigCommands Commands() => new(new ConfigPathResolver());

    // ── AC1 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Backups_list_reports_each_backup_with_reason_and_verdict()
    {
        SeedBackup("20260101-101500", "before-agent-create", SeedConfig);
        SeedBackup("20260102-090000", "corrupt", "{ not json ");

        var exitCode = Commands().ExecuteBackupsList(_configPath);

        exitCode.ShouldBe(0);
        var rendered = _output.ToString();
        rendered.ShouldContain("before-agent-create");
        rendered.ShouldContain("valid");
        rendered.ShouldContain("unloadable");
    }

    [Fact]
    public void Backups_list_with_no_backups_succeeds()
    {
        // "No backups yet" is a normal state, not a command failure.
        Commands().ExecuteBackupsList(_configPath).ShouldBe(0);
        _output.ToString().ShouldContain("No config backups");
    }

    // ── AC5: dry-run unless --commit ─────────────────────────────────────────

    [Fact]
    public async Task Restore_without_commit_flag_leaves_the_config_untouched()
    {
        var id = SeedBackup("20260101-101500", "before-agent-create", """
            {
              "version": 1,
              "gateway": { "listenUrl": "http://localhost:6001", "defaultAgentId": "assistant" }
            }
            """);
        var before = File.ReadAllText(_configPath);

        var exitCode = await Commands().ExecuteRestoreAsync(id, _configPath, commit: false, verbose: false, CancellationToken.None);

        exitCode.ShouldBe(0);
        File.ReadAllText(_configPath).ShouldBe(before);
        _output.ToString().ShouldContain("Dry run");
    }

    [Fact]
    public async Task Restore_with_commit_flag_replaces_the_config()
    {
        var id = SeedBackup("20260101-101500", "before-agent-create", """
            {
              "version": 1,
              "gateway": { "listenUrl": "http://localhost:6001", "defaultAgentId": "assistant" },
              "providers": {
                "anthropic": { "api": "anthropic", "apiKey": "sk-live-real-secret" }
              }
            }
            """);

        var exitCode = await Commands().ExecuteRestoreAsync(id, _configPath, commit: true, verbose: false, CancellationToken.None);

        exitCode.ShouldBe(0);
        var root = JsonNode.Parse(File.ReadAllText(_configPath))!.AsObject();
        root["gateway"]!["listenUrl"]!.GetValue<string>().ShouldBe("http://localhost:6001");

        // And the restored document genuinely reloads through the real loader.
        PlatformConfigLoader.Validate(PlatformConfigLoader.Load(_configPath, validateOnLoad: false)).ShouldBeEmpty();
    }

    // ── AC2: refusal exits non-zero and changes nothing ──────────────────────

    [Fact]
    public async Task Restore_of_a_corrupt_backup_exits_non_zero_and_changes_nothing()
    {
        var id = SeedBackup("20260101-101500", "corrupt", "{ not json ");
        var before = File.ReadAllText(_configPath);

        var exitCode = await Commands().ExecuteRestoreAsync(id, _configPath, commit: true, verbose: false, CancellationToken.None);

        exitCode.ShouldBe(1);
        File.ReadAllText(_configPath).ShouldBe(before);
        _output.ToString().ShouldContain("Restore refused");
    }

    [Fact]
    public async Task Restore_of_an_unknown_id_exits_non_zero_and_changes_nothing()
    {
        var before = File.ReadAllText(_configPath);

        var exitCode = await Commands().ExecuteRestoreAsync(
            "config-19700101-000000-nope", _configPath, commit: true, verbose: false, CancellationToken.None);

        exitCode.ShouldBe(1);
        File.ReadAllText(_configPath).ShouldBe(before);
    }

    // ── AC4: the pre-restore document is backed up ───────────────────────────

    [Fact]
    public async Task Restore_backs_up_the_pre_restore_config_into_the_same_directory_it_lists()
    {
        var id = SeedBackup("20260101-101500", "before-agent-create", """
            {
              "version": 1,
              "gateway": { "listenUrl": "http://localhost:6001", "defaultAgentId": "assistant" },
              "providers": {
                "anthropic": { "api": "anthropic", "apiKey": "sk-live-real-secret" }
              }
            }
            """);
        var countBefore = Directory.GetFiles(_backupsDir).Length;

        await Commands().ExecuteRestoreAsync(id, _configPath, commit: true, verbose: false, CancellationToken.None);

        // Same directory the list command enumerates - this is what proves CreateRestoreService and
        // CreateWriter agree about where backups live.
        Directory.GetFiles(_backupsDir).Length.ShouldBe(countBefore + 1);
        Directory.GetFiles(_backupsDir)
            .Select(Path.GetFileName)
            .ShouldContain(n => n!.Contains("before-restore"));
    }
}
