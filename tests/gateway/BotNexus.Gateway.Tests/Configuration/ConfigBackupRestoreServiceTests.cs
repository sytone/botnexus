using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Acceptance tests for issue #2884: <c>config.json</c> backups gain a <em>validated</em> restore
/// path.
/// </summary>
/// <remarks>
/// <para>
/// The defect being closed is that <see cref="ConfigBackupService"/> was write-only. The dangerous
/// workaround it forced - copying a backup over <c>config.json</c> by hand - has two failure modes,
/// and the tests here are structured around proving both are now impossible through the supported
/// path:
/// </para>
/// <list type="number">
///   <item>A snapshot that does not parse, or does not validate against the current schema, must be
///   <b>refused</b> - not written and discovered at next gateway start.</item>
///   <item>A snapshot carrying redaction placeholders must not overwrite live secrets, which means
///   the restore has to go through <see cref="PlatformConfigWriter"/> rather than copy bytes.</item>
/// </list>
/// <para>
/// <b>Non-vacuity.</b> These are not smoke tests. <c>Restore_CorruptBackup_IsRefusedAndConfigUnchanged</c>
/// and <c>Restore_SchemaInvalidBackup_IsRefusedAndConfigUnchanged</c> both assert the live file is
/// <em>byte-for-byte</em> unchanged after the refusal, so deleting the validation gate in
/// <c>ConfigBackupRestoreService.RestoreAsync</c> reddens them immediately: without the gate the
/// corrupt document reaches the writer and the bytes change (or the write throws), and either way
/// the assertion fails. Likewise <c>Restore_PlaceholderSecret_DoesNotOverwriteLiveSecret</c> fails
/// the moment the <see cref="ConfigSecretMerge.RestoreSecrets"/> call is removed, because the
/// placeholder then lands on disk.
/// </para>
/// </remarks>
public sealed class ConfigBackupRestoreServiceTests
{
    // A minimal but genuinely valid platform config document. Carries a provider secret so the
    // redaction-placeholder acceptance criterion has something real to protect.
    private const string LiveConfig = """
        {
          "version": 1,
          "gateway": {
            "listenUrl": "http://localhost:5099",
            "defaultAgentId": "assistant"
          },
          "providers": {
            "anthropic": {
              "api": "anthropic",
              "apiKey": "sk-live-real-secret"
            }
          }
        }
        """;

    private sealed record Harness(
        MockFileSystem FileSystem,
        string ConfigPath,
        string BackupsDir,
        ConfigBackupService Backups,
        PlatformConfigWriter Writer,
        ConfigBackupRestoreService Service);

    private static Harness CreateHarness(string liveConfig = LiveConfig)
    {
        var fs = new MockFileSystem();
        var home = Path.Combine(Path.GetTempPath(), "botnexus-tests", Guid.NewGuid().ToString("N"));
        var backupsDir = Path.Combine(home, "backups");
        var configPath = Path.Combine(home, "config.json");

        fs.Directory.CreateDirectory(home);
        fs.Directory.CreateDirectory(backupsDir);
        fs.File.WriteAllText(configPath, liveConfig);

        var backups = new ConfigBackupService(backupsDir, fs);
        var writer = new PlatformConfigWriter(configPath, fs, backups);
        return new Harness(fs, configPath, backupsDir, backups, writer,
            new ConfigBackupRestoreService(backups, writer, fs));
    }

    private static string SeedBackup(Harness h, string stamp, string reason, string content)
    {
        var name = $"config-{stamp}-{reason}.json";
        h.FileSystem.File.WriteAllText(Path.Combine(h.BackupsDir, name), content);
        return Path.GetFileNameWithoutExtension(name);
    }

    // ── AC1: listing ─────────────────────────────────────────────────────────

    [Fact]
    public void List_ReportsTimestampReasonAndSize_ForEachRetainedBackup()
    {
        var h = CreateHarness();
        SeedBackup(h, "20260101-101500", "before-agent-create", LiveConfig);
        SeedBackup(h, "20260102-090000", "before-provider-update", LiveConfig);

        var entries = h.Backups.List();

        entries.Count.ShouldBe(2);

        // Newest first.
        entries[0].Timestamp.ShouldBe(new DateTime(2026, 1, 2, 9, 0, 0));
        entries[0].Reason.ShouldBe("before-provider-update");
        entries[1].Timestamp.ShouldBe(new DateTime(2026, 1, 1, 10, 15, 0));
        entries[1].Reason.ShouldBe("before-agent-create");

        entries.ShouldAllBe(e => e.SizeBytes > 0);
    }

    [Fact]
    public void ListWithVerdicts_MarksLoadableBackupValid()
    {
        var h = CreateHarness();
        SeedBackup(h, "20260101-101500", "before-agent-create", LiveConfig);

        var inspections = h.Service.ListWithVerdicts();

        inspections.Count.ShouldBe(1);
        inspections[0].Verdict.ShouldBe(ConfigBackupVerdict.Valid);
        inspections[0].Errors.ShouldBeEmpty();
    }

    [Fact]
    public void ListWithVerdicts_MarksOldSchemaBackupNeedsMigration()
    {
        var h = CreateHarness();

        // A pre-migration document: gateway settings still at the document root. This loads only
        // because MigrateLegacyGatewaySettings lifts them into 'gateway'.
        SeedBackup(h, "20250101-101500", "before-agent-create", """
            {
              "version": 1,
              "listenUrl": "http://localhost:5099",
              "defaultAgentId": "assistant"
            }
            """);

        var inspection = h.Service.ListWithVerdicts().ShouldHaveSingleItem();

        inspection.Verdict.ShouldBe(ConfigBackupVerdict.NeedsMigration);
        inspection.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void List_WhenBackupsDirectoryMissing_ReturnsEmpty()
    {
        var fs = new MockFileSystem();
        var backups = new ConfigBackupService(
            Path.Combine(Path.GetTempPath(), "botnexus-tests", Guid.NewGuid().ToString("N"), "backups"), fs);

        backups.List().ShouldBeEmpty();
    }

    // ── AC2: a corrupt or schema-invalid backup is unloadable and cannot be restored ──

    [Fact]
    public void ListWithVerdicts_MarksCorruptBackupUnloadable()
    {
        var h = CreateHarness();
        SeedBackup(h, "20260101-101500", "corrupt", "{ this is not valid json ");

        var inspection = h.Service.ListWithVerdicts().ShouldHaveSingleItem();

        inspection.Verdict.ShouldBe(ConfigBackupVerdict.Unloadable);
        inspection.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Restore_CorruptBackup_IsRefusedAndConfigUnchanged()
    {
        var h = CreateHarness();
        var id = SeedBackup(h, "20260101-101500", "corrupt", "{ this is not valid json ");
        var before = h.FileSystem.File.ReadAllText(h.ConfigPath);

        var result = await h.Service.RestoreAsync(id, commit: true);

        result.Restored.ShouldBeFalse();
        result.Verdict.ShouldBe(ConfigBackupVerdict.Unloadable);
        result.Errors.ShouldNotBeEmpty();

        // The whole point of the gate: a refused restore leaves the live file byte-for-byte intact.
        h.FileSystem.File.ReadAllText(h.ConfigPath).ShouldBe(before);
    }

    [Fact]
    public async Task Restore_SchemaInvalidBackup_IsRefusedAndConfigUnchanged()
    {
        var h = CreateHarness();

        // Parses fine as JSON, but violates the config schema: listenUrl must be a URL.
        var id = SeedBackup(h, "20260101-101500", "schema-invalid", """
            {
              "version": 1,
              "gateway": { "listenUrl": "not-a-url" }
            }
            """);
        var before = h.FileSystem.File.ReadAllText(h.ConfigPath);

        var result = await h.Service.RestoreAsync(id, commit: true);

        result.Restored.ShouldBeFalse();
        result.Verdict.ShouldBe(ConfigBackupVerdict.Unloadable);
        h.FileSystem.File.ReadAllText(h.ConfigPath).ShouldBe(before);
    }

    [Fact]
    public async Task Restore_UnknownId_IsRefusedAndConfigUnchanged()
    {
        var h = CreateHarness();
        var before = h.FileSystem.File.ReadAllText(h.ConfigPath);

        var result = await h.Service.RestoreAsync("config-19700101-000000-nope", commit: true);

        result.Restored.ShouldBeFalse();
        result.Errors.ShouldNotBeEmpty();
        h.FileSystem.File.ReadAllText(h.ConfigPath).ShouldBe(before);
    }

    [Fact]
    public async Task Restore_TraversalId_CannotEscapeTheBackupsDirectory()
    {
        var h = CreateHarness();
        var before = h.FileSystem.File.ReadAllText(h.ConfigPath);

        // An id is operator input. It must not be able to address the live config (or anything else)
        // by path traversal - resolution matches against the enumerated listing, never a path join.
        var result = await h.Service.RestoreAsync("../config", commit: true);

        result.Restored.ShouldBeFalse();
        h.FileSystem.File.ReadAllText(h.ConfigPath).ShouldBe(before);
    }

    // ── AC3: restore routes through PlatformConfigWriter so secrets are reconciled ──

    [Fact]
    public async Task Restore_PlaceholderSecret_DoesNotOverwriteLiveSecret()
    {
        var h = CreateHarness();

        // The snapshot was taken from a redacted view, so the provider key is the placeholder.
        // A hand-copy would write "***" over the real key; the supported path must not.
        var id = SeedBackup(h, "20260101-101500", "redacted", $$"""
            {
              "version": 1,
              "gateway": {
                "listenUrl": "http://localhost:6001",
                "defaultAgentId": "assistant"
              },
              "providers": {
                "anthropic": {
                  "api": "anthropic",
                  "apiKey": "{{ConfigSecretMerge.Placeholder}}"
                }
              }
            }
            """);

        var result = await h.Service.RestoreAsync(id, commit: true);
        result.Restored.ShouldBeTrue();

        var written = JsonNode.Parse(h.FileSystem.File.ReadAllText(h.ConfigPath))!.AsObject();

        // The live secret survived...
        written["providers"]!["anthropic"]!["apiKey"]!.GetValue<string>()
            .ShouldBe("sk-live-real-secret");
        // ...and the rest of the snapshot was genuinely applied, so this is not a no-op pass.
        written["gateway"]!["listenUrl"]!.GetValue<string>().ShouldBe("http://localhost:6001");
    }

    [Fact]
    public async Task Restore_RealSecretInBackup_IsRestoredVerbatim()
    {
        var h = CreateHarness();

        // The mirror case: a genuine (non-placeholder) secret in the snapshot must win. Otherwise
        // "protect live secrets" would silently become "secrets can never be rolled back".
        var id = SeedBackup(h, "20260101-101500", "real-secret", """
            {
              "version": 1,
              "gateway": { "listenUrl": "http://localhost:5099", "defaultAgentId": "assistant" },
              "providers": {
                "anthropic": { "api": "anthropic", "apiKey": "sk-older-but-real" }
              }
            }
            """);

        (await h.Service.RestoreAsync(id, commit: true)).Restored.ShouldBeTrue();

        var written = JsonNode.Parse(h.FileSystem.File.ReadAllText(h.ConfigPath))!.AsObject();
        written["providers"]!["anthropic"]!["apiKey"]!.GetValue<string>().ShouldBe("sk-older-but-real");
    }

    // ── AC4: a restore backs up the pre-restore document first ───────────────

    [Fact]
    public async Task Restore_TakesFreshBackupOfPreRestoreConfig()
    {
        var h = CreateHarness();
        var id = SeedBackup(h, "20260101-101500", "before-agent-create", """
            {
              "version": 1,
              "gateway": { "listenUrl": "http://localhost:6001", "defaultAgentId": "assistant" },
              "providers": {
                "anthropic": { "api": "anthropic", "apiKey": "sk-live-real-secret" }
              }
            }
            """);

        var countBefore = h.FileSystem.Directory.GetFiles(h.BackupsDir).Length;

        (await h.Service.RestoreAsync(id, commit: true)).Restored.ShouldBeTrue();

        var after = h.FileSystem.Directory.GetFiles(h.BackupsDir);
        after.Length.ShouldBe(countBefore + 1);

        // The new artefact is the pre-restore document, tagged with the restore that caused it, so
        // the restore is itself undoable.
        var fresh = after.Select(Path.GetFileName).Single(n => n!.Contains("before-restore"));
        h.FileSystem.File.ReadAllText(Path.Combine(h.BackupsDir, fresh!))
            .ShouldContain("localhost:5099");
    }

    [Fact]
    public async Task Restore_WhenRefused_TakesNoBackup()
    {
        var h = CreateHarness();
        var id = SeedBackup(h, "20260101-101500", "corrupt", "{ nope ");
        var countBefore = h.FileSystem.Directory.GetFiles(h.BackupsDir).Length;

        (await h.Service.RestoreAsync(id, commit: true)).Restored.ShouldBeFalse();

        h.FileSystem.Directory.GetFiles(h.BackupsDir).Length.ShouldBe(countBefore);
    }

    // ── AC5: dry-run by default ──────────────────────────────────────────────

    [Fact]
    public async Task Restore_WithoutCommitFlag_WritesNothing()
    {
        var h = CreateHarness();
        var id = SeedBackup(h, "20260101-101500", "before-agent-create", """
            {
              "version": 1,
              "gateway": { "listenUrl": "http://localhost:6001", "defaultAgentId": "assistant" }
            }
            """);
        var before = h.FileSystem.File.ReadAllText(h.ConfigPath);
        var backupsBefore = h.FileSystem.Directory.GetFiles(h.BackupsDir).Length;

        var result = await h.Service.RestoreAsync(id);

        result.DryRun.ShouldBeTrue();
        result.Restored.ShouldBeFalse();
        result.Errors.ShouldBeEmpty();
        result.Verdict.ShouldBe(ConfigBackupVerdict.Valid);

        h.FileSystem.File.ReadAllText(h.ConfigPath).ShouldBe(before);
        h.FileSystem.Directory.GetFiles(h.BackupsDir).Length.ShouldBe(backupsBefore);
    }

    [Fact]
    public async Task Restore_DryRun_StillReportsAnUnloadableSnapshotAsRefused()
    {
        var h = CreateHarness();
        var id = SeedBackup(h, "20260101-101500", "corrupt", "{ nope ");

        var result = await h.Service.RestoreAsync(id);

        // A preview that reported "would restore" for a snapshot the commit would refuse would be
        // worse than no preview at all.
        result.Restored.ShouldBeFalse();
        result.Verdict.ShouldBe(ConfigBackupVerdict.Unloadable);
        result.Errors.ShouldNotBeEmpty();
    }

    // ── Migration/verdict coherence ──────────────────────────────────────────

    [Fact]
    public async Task Restore_OldSchemaBackup_IsRestorableAndMigratesOnLoad()
    {
        var h = CreateHarness();
        var id = SeedBackup(h, "20250101-101500", "legacy", """
            {
              "version": 1,
              "listenUrl": "http://localhost:7001",
              "defaultAgentId": "assistant"
            }
            """);

        var result = await h.Service.RestoreAsync(id, commit: true);

        result.Restored.ShouldBeTrue();
        result.Verdict.ShouldBe(ConfigBackupVerdict.NeedsMigration);

        // And it genuinely loads: the legacy root key is lifted into gateway by the loader.
        var loaded = PlatformConfigLoader.ValidateRawJson(h.FileSystem.File.ReadAllText(h.ConfigPath));
        loaded.ShouldBeEmpty();
    }

    [Fact]
    public void LegacyRootSettingKeys_CoverEveryKeyTheMigrationLifts()
    {
        // The NeedsMigration verdict is derived from this list, so a key added to the migration
        // without being added here would silently start reporting old-schema snapshots as Valid.
        PlatformConfigLoader.LegacyRootSettingKeys.ShouldContain("listenUrl");
        PlatformConfigLoader.LegacyRootSettingKeys.ShouldContain("crossWorld");
        PlatformConfigLoader.LegacyRootSettingKeys.Distinct(StringComparer.Ordinal).Count()
            .ShouldBe(PlatformConfigLoader.LegacyRootSettingKeys.Count);
    }

    // ── Resolution ───────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_AcceptsIdWithOrWithoutJsonSuffix()
    {
        var h = CreateHarness();
        var id = SeedBackup(h, "20260101-101500", "before-agent-create", LiveConfig);

        h.Backups.Resolve(id).ShouldNotBeNull();
        h.Backups.Resolve(id + ".json").ShouldNotBeNull();
        h.Backups.Resolve("no-such-backup").ShouldBeNull();
        h.Backups.Resolve("   ").ShouldBeNull();
    }
}
