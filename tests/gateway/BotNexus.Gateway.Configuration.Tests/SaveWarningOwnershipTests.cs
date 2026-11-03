using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Writers;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>Save outcomes must carry their own warnings, not consult mutable latest-save state.</summary>
public sealed class SaveWarningOwnershipTests : IDisposable
{
    private const string RefreshCode = "configuration-projection-refresh-failed";
    private const string RefreshMessage = "Configuration committed to SQLite; the existing JSON output projection could not be refreshed.";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bn-save-warning-" + Guid.NewGuid().ToString("N"));
    private readonly WarningFileSystem _fileSystem = new();
    private readonly TestConfigStore _store = new(new JsonObject { ["version"] = 1 });
    private string ConfigPath => Path.Combine(_directory, "config.json");
    private string BackupPath => Path.Combine(_directory, "backups");

    public SaveWarningOwnershipTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("empty")]
    [InlineData("no-op")]
    [InlineData("rejected")]
    public async Task ApplyPatchAsync_LaterNonWritingSave_RetainsFirstWarningOnFirstResultOnly(string laterSave)
    {
        var writer = CreateWriter();
        await File.WriteAllTextAsync(ConfigPath, "stale-output");
        _fileSystem.FailProjectionRead = true;
        var first = await writer.ApplyPatchAsync([new("version", JsonValue.Create(2))], "first-save");
        first.Success.ShouldBeTrue();
        first.Warnings.ShouldHaveSingleItem().Code.ShouldBe(RefreshCode);
        var beforeSecond = await writer.ReadWithRevisionAsync();
        var projectionReads = _fileSystem.ProjectionReadAttempts;
        var second = laterSave switch
        {
            "empty" => await writer.ApplyPatchAsync([], "empty-save"),
            "no-op" => await writer.ApplyPatchAsync([new("version", JsonValue.Create(2))], "no-op-save"),
            _ => await writer.ApplyPatchAsync([new("", JsonValue.Create(3))], "rejected-save")
        };

        second.Success.ShouldBe(laterSave != "rejected");
        if (laterSave == "rejected")
            second.Errors.ShouldNotBeEmpty();
        else
            second.Errors.ShouldBeEmpty();
        (await writer.ReadWithRevisionAsync()).Revision.ShouldBe(beforeSecond.Revision);
        (await writer.ReadAsync())["version"].ShouldNotBeNull().GetValue<int>().ShouldBe(2);
        (await File.ReadAllTextAsync(ConfigPath)).ShouldBe("stale-output");
        _fileSystem.ProjectionReadAttempts.ShouldBe(projectionReads);
        Directory.Exists(BackupPath).ShouldBeFalse();

        // Inspect the actual returned public result through its normal JSON representation.
        // The three-argument record contract remains compatible.
        // Read A only AFTER B: copying the writer's latest property in this helper would hide the bug.
        AssertRefreshWarning(ResultWarnings(first));
        ResultWarnings(second).ShouldBeEmpty();
    }

    [Fact]
    public async Task ApplyPatchAsync_SecondMutationPausedBeforeNoOp_FirstResultStillOwnsItsWarning()
    {
        var writer = CreateWriter();
        await File.WriteAllTextAsync(ConfigPath, "stale-output");
        _fileSystem.FailProjectionRead = true;
        var first = await writer.ApplyPatchAsync([new("version", JsonValue.Create(2))], "first-save");
        first.Success.ShouldBeTrue();
        first.Warnings.ShouldHaveSingleItem().Code.ShouldBe(RefreshCode);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = writer.MutateWithResultAsync(async _ =>
        {
            entered.SetResult(true);
            await release.Task;
        }, "paused-no-op");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // B is inside the mutation lock but has not finished.
            // A's returned result must remain sufficient to report A without reading that shared state.
            AssertRefreshWarning(ResultWarnings(first));
        }
        finally
        {
            release.TrySetResult(true);
            var secondOutcome = await second;
            secondOutcome.Errors.ShouldBeEmpty();
            secondOutcome.Warnings.ShouldBeEmpty();
        }
        (await writer.ReadAsync())["version"].ShouldNotBeNull().GetValue<int>().ShouldBe(2);
        _fileSystem.ProjectionReadAttempts.ShouldBe(1);
        Directory.Exists(BackupPath).ShouldBeFalse();
    }

    [Fact]
    public async Task MutateAsync_BackupPermissionFailureOnly_ReportsWarningAfterSuccessfulProjection()
    {
        var writer = CreateWriter();
        await File.WriteAllTextAsync(ConfigPath, "{\"version\":1}");
        _fileSystem.FailBackupPermission = true;
        var result = await writer.MutateWithResultAsync(root => root["version"] = 2, "backup-permission");

        _fileSystem.BackupPermissionAttempts.ShouldBe(1);
        (await writer.ReadAsync())["version"].ShouldNotBeNull().GetValue<int>().ShouldBe(2);
        JsonNode.DeepEquals(JsonNode.Parse(await File.ReadAllTextAsync(ConfigPath)), await writer.ReadAsync()).ShouldBeTrue();
        var backup = Directory.GetFiles(BackupPath).ShouldHaveSingleItem();
        (await File.ReadAllTextAsync(backup)).ShouldBe("{\"version\":1}");
        result.Warnings.ShouldContain(warning => warning.Code == "configuration-backup-permissions-failed");
        result.Warnings.First().Message.ShouldBe("Configuration committed to SQLite; permissions on a JSON backup could not be restricted.");
        AssertSafeWarnings(result.Warnings);
    }

    [Fact]
    public async Task MutateAsync_BackupPermissionFailureThenReplaceFailure_PreservesBothWarnings()
    {
        var writer = CreateWriter();
        await File.WriteAllTextAsync(ConfigPath, "{\"version\":1}");
        _fileSystem.FailBackupPermission = true;
        _fileSystem.FailReplace = true;
        var result = await writer.MutateWithResultAsync(root => root["version"] = 2, "backup-and-replace");

        _fileSystem.BackupPermissionAttempts.ShouldBe(1);
        _fileSystem.ReplaceAttempts.ShouldBe(1);
        (await writer.ReadAsync())["version"].ShouldNotBeNull().GetValue<int>().ShouldBe(2);
        (await File.ReadAllTextAsync(ConfigPath)).ShouldBe("{\"version\":1}");
        Directory.GetFiles(_directory, "*.tmp").ShouldBeEmpty();
        var backup = Directory.GetFiles(BackupPath).ShouldHaveSingleItem();
        (await File.ReadAllTextAsync(backup)).ShouldBe("{\"version\":1}");
        result.Warnings.Select(warning => warning.Code).ShouldBe(new[] { "configuration-backup-permissions-failed", RefreshCode });
        result.Warnings.ShouldContain(warning => warning.Code == RefreshCode);
        result.Warnings.ShouldContain(warning => warning.Code == "configuration-backup-permissions-failed");
        result.Warnings.First().Message.ShouldBe("Configuration committed to SQLite; permissions on a JSON backup could not be restricted.");
        AssertSafeWarnings(result.Warnings);
    }

    [Fact]
    public async Task MutateWithResultAsync_LaterSuccessfulSave_DoesNotChangeFirstWarning()
    {
        var writer = CreateWriter();
        await File.WriteAllTextAsync(ConfigPath, "stale-output");
        _fileSystem.FailProjectionRead = true;
        var first = await writer.MutateWithResultAsync(root => root["version"] = 2, "first");
        var beforeSecond = await _store.ReadSnapshotAsync();
        _fileSystem.FailProjectionRead = false;

        var second = await writer.MutateWithResultAsync(root => root["version"] = 3, "second");

        first.Errors.ShouldBeEmpty();
        first.Warnings.ShouldHaveSingleItem().Code.ShouldBe(RefreshCode);
        second.Errors.ShouldBeEmpty();
        second.Warnings.ShouldBeEmpty();
        (await _store.ReadSnapshotAsync()).Revision.ShouldBe(beforeSecond.Revision + 1);
        (await writer.ReadAsync())["version"].ShouldNotBeNull().GetValue<int>().ShouldBe(3);
        JsonNode.DeepEquals(JsonNode.Parse(await File.ReadAllTextAsync(ConfigPath)), await writer.ReadAsync()).ShouldBeTrue();
        (await File.ReadAllTextAsync(Directory.GetFiles(BackupPath).ShouldHaveSingleItem())).ShouldBe("stale-output");
    }

    [Fact]
    public void ResultCollections_CopyInputsAndPreservePatchConstructionAndDeconstruction()
    {
        var warning = new ConfigurationProjectionWarning(RefreshCode, RefreshMessage);
        var warnings = new List<ConfigurationProjectionWarning> { warning };
        var errors = new List<string> { "rejected" };
        var mutation = new ConfigMutationResult(errors, warnings);
        var write = new ConfigurationWriteResult(warnings);
        var patch = new ConfigPatchResult(true, "revision", []) { Warnings = write.Warnings };
        warnings.Clear();
        errors.Clear();

        mutation.Errors.ShouldHaveSingleItem().ShouldBe("rejected");
        mutation.Warnings.ShouldHaveSingleItem().ShouldBe(warning);
        write.Warnings.ShouldHaveSingleItem().ShouldBe(warning);
        patch.Warnings.ShouldHaveSingleItem().ShouldBe(warning);
        var changedCopy = patch.Warnings.SetItem(0, new("other", "other"));
        changedCopy[0].Code.ShouldBe("other");
        patch.Warnings[0].Code.ShouldBe(RefreshCode);
        var (success, revision, patchErrors) = patch;
        success.ShouldBeTrue();
        revision.ShouldBe("revision");
        patchErrors.ShouldBeEmpty();
        var empty = new ConfigPatchResult(true, null, []) { Warnings = default };
        empty.Warnings.IsDefault.ShouldBeFalse();
        ResultWarnings(empty).ShouldBeEmpty();
        ConfigurationWriteResult.Empty.Warnings.IsDefault.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MutateWithResultAsync_CancellationDuringPostCommitProjection_ReturnsCommittedOutcome(bool failProjection)
    {
        var writer = CreateWriter();
        await File.WriteAllTextAsync(ConfigPath, "stale-output");
        using var cancellation = new CancellationTokenSource();
        _fileSystem.OnProjectionRead = cancellation.Cancel;
        _fileSystem.FailProjectionRead = failProjection;

        var outcome = await writer.MutateWithResultAsync(root => root["version"] = 2, "cancel-after-commit", cancellation.Token);

        cancellation.IsCancellationRequested.ShouldBeTrue();
        outcome.Errors.ShouldBeEmpty();
        (await writer.ReadAsync())["version"].ShouldNotBeNull().GetValue<int>().ShouldBe(2);
        if (failProjection)
        {
            outcome.Warnings.ShouldHaveSingleItem().Code.ShouldBe(RefreshCode);
            (await File.ReadAllTextAsync(ConfigPath)).ShouldBe("stale-output");
        }
        else
        {
            outcome.Warnings.ShouldBeEmpty();
            JsonNode.DeepEquals(JsonNode.Parse(await File.ReadAllTextAsync(ConfigPath)), await writer.ReadAsync()).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task MutateValidatedWithResultAsync_RejectionOwnsErrorsAndDoesNotPersist()
    {
        var writer = CreateWriter();
        await File.WriteAllTextAsync(ConfigPath, "stale-output");
        _fileSystem.FailProjectionRead = true;
        var first = await writer.MutateWithResultAsync(root => root["version"] = 2, "first");
        var before = await _store.ReadSnapshotAsync();
        var rejected = await writer.MutateValidatedWithResultAsync(root =>
        {
            root["version"] = 3;
            return "caller rejection";
        }, "reject");
        var noOp = await writer.MutateValidatedWithResultAsync(_ => null, "no-op");

        rejected.Errors.ShouldHaveSingleItem().ShouldBe("caller rejection");
        rejected.Warnings.ShouldBeEmpty();
        noOp.Errors.ShouldBeEmpty();
        noOp.Warnings.ShouldBeEmpty();
        first.Warnings.ShouldHaveSingleItem().Code.ShouldBe(RefreshCode);
        (await _store.ReadSnapshotAsync()).Revision.ShouldBe(before.Revision);
        (await writer.ReadAsync())["version"].ShouldNotBeNull().GetValue<int>().ShouldBe(2);
        _fileSystem.ProjectionReadAttempts.ShouldBe(1);
        Directory.Exists(BackupPath).ShouldBeFalse();
    }

    [Fact]
    public async Task AuthorityWithResultAsync_EmptyChangeSetLeavesHeldResultAndAuthorityUntouched()
    {
        _fileSystem.ConfigPath = ConfigPath;
        await File.WriteAllTextAsync(ConfigPath, "stale-output");
        _fileSystem.FailProjectionRead = true;
        var writer = new AuthorityConfigurationWriter(_store,
            Path.Combine(_directory, ConfigStoreBootstrap.StoreFileName), ConfigPath, _fileSystem, null);
        var first = await writer.WriteWithResultAsync(new JsonObject { ["version"] = 2 }, "first");
        var before = await _store.ReadSnapshotAsync();
        var emptyChanges = Store.ConfigDocumentDiffer.Diff(new JsonObject(), new JsonObject());
        var second = await writer.ApplyChangeSetWithResultAsync(emptyChanges, "empty");

        first.Warnings.ShouldHaveSingleItem().Code.ShouldBe(RefreshCode);
        second.Warnings.ShouldBeEmpty();
        (await _store.ReadSnapshotAsync()).Revision.ShouldBe(before.Revision);
        _fileSystem.ProjectionReadAttempts.ShouldBe(1);
        (await File.ReadAllTextAsync(ConfigPath)).ShouldBe("stale-output");
    }

    [Fact]
    public async Task MutateWithResultAsync_AllPermissionFailuresThenReplaceFailure_AreOrderedAndDeduplicated()
    {
        var writer = CreateWriter();
        await File.WriteAllTextAsync(ConfigPath, "{\"version\":1}");
        _fileSystem.FailBackupPermission = true;
        _fileSystem.FailTempPermission = true;
        _fileSystem.FailReplace = true;

        var outcome = await writer.MutateWithResultAsync(root => root["version"] = 2, "all-failures");

        outcome.Warnings.Select(warning => warning.Code).ShouldBe(new[]
        {
            "configuration-backup-permissions-failed", "configuration-projection-permissions-failed", RefreshCode
        });
        AssertSafeWarnings(outcome.Warnings);
        (await writer.ReadAsync())["version"].ShouldNotBeNull().GetValue<int>().ShouldBe(2);
        (await File.ReadAllTextAsync(ConfigPath)).ShouldBe("{\"version\":1}");
        _fileSystem.BackupPermissionAttempts.ShouldBe(1);
        _fileSystem.TempPermissionAttempts.ShouldBe(1);
        _fileSystem.ReplaceAttempts.ShouldBe(1);
        Directory.GetFiles(_directory, "*.tmp").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MutateWithResultAsync_TempAndFinalPermissionFailures_ReturnsOneProjectionWarning(bool failTemp, bool failFinal)
    {
        var writer = CreateWriter();
        await File.WriteAllTextAsync(ConfigPath, "{\"version\":1}");
        _fileSystem.FailTempPermission = failTemp;
        _fileSystem.FailFinalPermission = failFinal;

        var outcome = await writer.MutateWithResultAsync(root => root["version"] = 2, "permissions");

        outcome.Warnings.ShouldHaveSingleItem().Code.ShouldBe("configuration-projection-permissions-failed");
        _fileSystem.TempPermissionAttempts.ShouldBe(failTemp ? 1 : 0);
        _fileSystem.FinalPermissionAttempts.ShouldBe(failFinal ? 1 : 0);
        JsonNode.DeepEquals(JsonNode.Parse(await File.ReadAllTextAsync(ConfigPath)), await writer.ReadAsync()).ShouldBeTrue();
        AssertSafeWarnings(outcome.Warnings);
    }

    [Fact]
    public async Task JsonWriteAsync_ReplaceFailureStillThrowsAndCleansTemporaryFile()
    {
        _fileSystem.ConfigPath = ConfigPath;
        await File.WriteAllTextAsync(ConfigPath, "{\"version\":1}");
        _fileSystem.FailReplace = true;
        var writer = new JsonConfigurationWriter(ConfigPath, _fileSystem);

        await Should.ThrowAsync<InvalidOperationException>(() => writer.WriteAsync(new JsonObject { ["version"] = 2 }, "raw"));

        (await File.ReadAllTextAsync(ConfigPath)).ShouldBe("{\"version\":1}");
        _fileSystem.ReplaceAttempts.ShouldBe(1);
        Directory.GetFiles(_directory, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public void Backup_AbsentSource_ReturnsSkippedWithoutCreatingBackupDirectory()
    {
        var backup = new ConfigBackupService(BackupPath, _fileSystem);
        backup.Backup(ConfigPath, "absent").ShouldBe(FilePermissionOutcome.Skipped);
        Directory.Exists(BackupPath).ShouldBeFalse();
    }

    [Theory]
    [InlineData("enumeration")]
    [InlineData("metadata")]
    [InlineData("delete")]
    public async Task MutateWithResultAsync_BackupPermissionThenPruneFailure_PreservesBothWarnings(string fault)
    {
        var writer = CreateWriter();
        await File.WriteAllTextAsync(ConfigPath, "{\"version\":1}");
        SeedPruneFailure(fault);
        _fileSystem.FailBackupPermission = true;
        var before = await _store.ReadSnapshotAsync();

        var outcome = await writer.MutateWithResultAsync(root => root["version"] = 2, "prune-failure");

        outcome.Errors.ShouldBeEmpty();
        outcome.Warnings.Select(warning => warning.Code).ShouldBe(new[]
        {
            "configuration-backup-permissions-failed", RefreshCode
        });
        outcome.Warnings[0].Message.ShouldBe("Configuration committed to SQLite; permissions on a JSON backup could not be restricted.");
        outcome.Warnings[1].Message.ShouldBe(RefreshMessage);
        AssertSafeWarnings(outcome.Warnings);
        _fileSystem.BackupPermissionAttempts.ShouldBe(1);
        _fileSystem.PruneFaultAttempts.ShouldBe(1);
        _fileSystem.ReplaceAttempts.ShouldBe(0);
        (await _store.ReadSnapshotAsync()).Revision.ShouldBe(before.Revision + 1);
        (await writer.ReadAsync())["version"].ShouldNotBeNull().GetValue<int>().ShouldBe(2);
        (await File.ReadAllTextAsync(ConfigPath)).ShouldBe("{\"version\":1}");
        var backup = _fileSystem.CopiedBackupPath.ShouldNotBeNull();
        File.Exists(backup).ShouldBeTrue();
        (await File.ReadAllTextAsync(backup)).ShouldBe("{\"version\":1}");
        Directory.GetFiles(BackupPath).Length.ShouldBe(ConfigBackupService.MaxBackups + 1);
        Directory.GetFiles(_directory, "*.tmp").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("enumeration")]
    [InlineData("metadata")]
    [InlineData("delete")]
    public async Task JsonWriteAsync_BackupPermissionThenPruneFailure_StillThrows(string fault)
    {
        _fileSystem.ConfigPath = ConfigPath;
        _fileSystem.BackupDirectory = BackupPath;
        await File.WriteAllTextAsync(ConfigPath, "{\"version\":1}");
        SeedPruneFailure(fault);
        _fileSystem.FailBackupPermission = true;
        var backup = new ConfigBackupService(BackupPath, _fileSystem);
        var writer = new JsonConfigurationWriter(ConfigPath, _fileSystem, backup);

        var error = await Should.ThrowAsync<IOException>(() => writer.WriteAsync(new JsonObject { ["version"] = 2 }, "raw-prune"));

        error.Message.ShouldBe("private-test-detail: prune " + fault);
        _fileSystem.BackupPermissionAttempts.ShouldBe(1);
        _fileSystem.PruneFaultAttempts.ShouldBe(1);
        _fileSystem.ReplaceAttempts.ShouldBe(0);
        (await File.ReadAllTextAsync(ConfigPath)).ShouldBe("{\"version\":1}");
        (await File.ReadAllTextAsync(_fileSystem.CopiedBackupPath.ShouldNotBeNull())).ShouldBe("{\"version\":1}");
        Directory.GetFiles(_directory, "*.tmp").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("enumeration")]
    [InlineData("metadata")]
    [InlineData("delete")]
    public void Backup_PermissionThenPruneFailure_StillThrowsToPreCommitCaller(string fault)
    {
        _fileSystem.ConfigPath = ConfigPath;
        _fileSystem.BackupDirectory = BackupPath;
        File.WriteAllText(ConfigPath, "{\"version\":1}");
        SeedPruneFailure(fault);
        _fileSystem.FailBackupPermission = true;
        var backup = new ConfigBackupService(BackupPath, _fileSystem);

        var error = Should.Throw<IOException>(() => backup.Backup(ConfigPath, "before-sqlite-import"));

        error.Message.ShouldBe("private-test-detail: prune " + fault);
        _fileSystem.BackupPermissionAttempts.ShouldBe(1);
        _fileSystem.PruneFaultAttempts.ShouldBe(1);
        File.ReadAllText(ConfigPath).ShouldBe("{\"version\":1}");
        File.ReadAllText(_fileSystem.CopiedBackupPath.ShouldNotBeNull()).ShouldBe("{\"version\":1}");
    }

    [Fact]
    public async Task ApplyPatchAsync_CancelledEmptyPatch_PreservesCompatibilityAndHeldWarningWithoutIo()
    {
        var writer = CreateWriter();
        await File.WriteAllTextAsync(ConfigPath, "stale-output");
        _fileSystem.FailProjectionRead = true;
        var first = await writer.ApplyPatchAsync([new("version", JsonValue.Create(2))], "first");
        var before = await _store.ReadSnapshotAsync();
        var projectionReads = _fileSystem.ProjectionReadAttempts;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var validationCalled = false;

        var empty = await writer.ApplyPatchAsync([], "empty-cancelled", expectedRevision: "stale", ct: cancellation.Token,
            additionalValidation: (_, _) =>
            {
                validationCalled = true;
                return ["must not validate"];
            });

        empty.Success.ShouldBeTrue();
        empty.Revision.ShouldBeNull();
        empty.Errors.ShouldBeEmpty();
        empty.Warnings.IsDefault.ShouldBeFalse();
        empty.Warnings.ShouldBeEmpty();
        ResultWarnings(empty).ShouldBeEmpty();
        AssertRefreshWarning(ResultWarnings(first));
        validationCalled.ShouldBeFalse();
        (await _store.ReadSnapshotAsync()).Revision.ShouldBe(before.Revision);
        (await writer.ReadAsync())["version"].ShouldNotBeNull().GetValue<int>().ShouldBe(2);
        (await File.ReadAllTextAsync(ConfigPath)).ShouldBe("stale-output");
        _fileSystem.ProjectionReadAttempts.ShouldBe(projectionReads);
        _fileSystem.ReplaceAttempts.ShouldBe(0);
        Directory.Exists(BackupPath).ShouldBeFalse();
    }

    private void SeedPruneFailure(string fault)
    {
        Directory.CreateDirectory(BackupPath);
        for (var index = 0; index < ConfigBackupService.MaxBackups; index++)
            File.WriteAllText(Path.Combine(BackupPath, $"config-20000101-000000-old-{index:D2}.json"), "old-backup");
        _fileSystem.PruneFault = fault;
    }

    private PlatformConfigWriter CreateWriter()
    {
        _fileSystem.ConfigPath = ConfigPath;
        _fileSystem.BackupDirectory = BackupPath;
        var storePath = Path.Combine(_directory, ConfigStoreBootstrap.StoreFileName);
        var backend = new AuthorityConfigurationWriter(_store, storePath, ConfigPath, _fileSystem,
            new ConfigBackupService(BackupPath, _fileSystem));
        var writer = new PlatformConfigWriter(ConfigPath, _fileSystem, null, backend, _store);
        var descriptor = writer.Backends.ShouldHaveSingleItem();
        descriptor.Name.ShouldBe("sqlite");
        descriptor.Location.ShouldBe(storePath);
        descriptor.WinsOnRead.ShouldBeTrue();
        return writer;
    }

    private static JsonArray ResultWarnings(ConfigPatchResult result)
    {
        var json = JsonSerializer.SerializeToNode(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            .ShouldBeOfType<JsonObject>();
        json.ContainsKey("warnings").ShouldBeTrue("each returned save result must include its own warnings, including an empty array when none apply");
        return json["warnings"].ShouldBeOfType<JsonArray>();
    }

    private static void AssertRefreshWarning(JsonArray warnings)
    {
        var warning = warnings.ShouldHaveSingleItem().ShouldBeOfType<JsonObject>();
        warning["code"].ShouldNotBeNull().GetValue<string>().ShouldBe(RefreshCode);
        warning["message"].ShouldNotBeNull().GetValue<string>().ShouldBe(RefreshMessage);
    }

    private void AssertSafeWarnings(IReadOnlyList<ConfigurationProjectionWarning> warnings)
    {
        foreach (var warning in warnings)
        {
            warning.Message.ShouldNotContain(_directory);
            warning.Message.ShouldNotContain("private-test-detail");
            warning.Message.ShouldNotContain("version");
        }
    }

    private sealed class WarningFileSystem : FileSystem
    {
        private readonly IFile _file;
        private readonly IDirectory _directory;
        public WarningFileSystem()
        {
            _file = new WarningFile(this);
            _directory = new WarningDirectory(this);
        }
        public override IFile File => _file;
        public override IDirectory Directory => _directory;
        public string? PruneFault { get; set; }
        public string? CopiedBackupPath { get; private set; }
        public int PruneFaultAttempts { get; private set; }

        private sealed class WarningDirectory(WarningFileSystem owner) : DirectoryWrapper(owner)
        {
            public override string[] GetFiles(string path, string searchPattern)
            {
                if (path == owner.BackupDirectory && owner.PruneFault == "enumeration")
                {
                    owner.PruneFaultAttempts++;
                    throw new IOException("private-test-detail: prune enumeration");
                }
                return base.GetFiles(path, searchPattern);
            }
        }
        public string ConfigPath { get; set; } = string.Empty;
        public string BackupDirectory { get; set; } = string.Empty;
        public bool FailProjectionRead { get; set; }
        public bool FailBackupPermission { get; set; }
        public bool FailReplace { get; set; }
        public bool FailTempPermission { get; set; }
        public bool FailFinalPermission { get; set; }
        public Action? OnProjectionRead { get; set; }
        public int TempPermissionAttempts { get; private set; }
        public int FinalPermissionAttempts { get; private set; }
        private bool _replaceCompleted;
        public int ProjectionReadAttempts { get; private set; }
        public int BackupPermissionAttempts { get; private set; }
        public int ReplaceAttempts { get; private set; }

        private sealed class WarningFile(WarningFileSystem owner) : FileWrapper(owner)
        {
            public override void Copy(string sourceFileName, string destFileName, bool overwrite)
            {
                base.Copy(sourceFileName, destFileName, overwrite);
                if (owner.Path.GetDirectoryName(destFileName) == owner.BackupDirectory)
                    owner.CopiedBackupPath = destFileName;
            }

            public override DateTime GetCreationTimeUtc(string path)
            {
                if (owner.Path.GetDirectoryName(path) == owner.BackupDirectory && owner.PruneFault is not null)
                {
                    if (owner.PruneFault == "metadata")
                    {
                        owner.PruneFaultAttempts++;
                        throw new IOException("private-test-detail: prune metadata");
                    }
                    // The newly copied backup is deterministically newest; no wall-clock ordering.
                    return path == owner.CopiedBackupPath ? DateTime.MaxValue : DateTime.MinValue;
                }
                return base.GetCreationTimeUtc(path);
            }

            public override void Delete(string path)
            {
                if (owner.Path.GetDirectoryName(path) == owner.BackupDirectory && owner.PruneFault == "delete")
                {
                    owner.PruneFaultAttempts++;
                    throw new IOException("private-test-detail: prune delete");
                }
                base.Delete(path);
            }

            public override Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
            {
                if (path == owner.ConfigPath)
                {
                    owner.ProjectionReadAttempts++;
                    owner.OnProjectionRead?.Invoke();
                    if (owner.FailProjectionRead)
                        throw new IOException("private-test-detail: projection read failed");
                }
                return base.ReadAllTextAsync(path, cancellationToken);
            }

            public override bool Exists(string? path)
            {
                // RestrictToOwner checks existence inside its catch boundary before either OS branch.
                // Fail only that backup check: source read/copy and temp/live restrictions still work.
                if (owner.FailBackupPermission && path is not null && owner.Path.GetDirectoryName(path) == owner.BackupDirectory)
                {
                    owner.BackupPermissionAttempts++;
                    throw new UnauthorizedAccessException("private-test-detail: backup permission check failed");
                }
                if (owner.FailTempPermission && path is not null && path.EndsWith(".tmp", StringComparison.Ordinal))
                {
                    if (owner.TempPermissionAttempts == 0)
                    {
                        owner.TempPermissionAttempts++;
                        throw new UnauthorizedAccessException("private-test-detail: temp permission check failed");
                    }
                }
                if (owner.FailFinalPermission && path == owner.ConfigPath && owner._replaceCompleted)
                {
                    owner.FinalPermissionAttempts++;
                    throw new UnauthorizedAccessException("private-test-detail: final permission check failed");
                }
                return base.Exists(path);
            }

            public override void Replace(string sourceFileName, string destinationFileName, string? destinationBackupFileName, bool ignoreMetadataErrors)
            {
                owner.ReplaceAttempts++;
                if (owner.FailReplace)
                    throw new InvalidOperationException("private-test-detail: replace failed without entering the I/O retry loop");
                base.Replace(sourceFileName, destinationFileName, destinationBackupFileName, ignoreMetadataErrors);
                owner._replaceCompleted = true;
            }
        }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
