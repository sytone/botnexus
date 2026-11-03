using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Gateway.Configuration.Writers;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>Deterministic commit/cancellation boundaries, not timing-based projection tests.</summary>
public sealed class AuthorityCommitBoundaryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"bn-commit-{Guid.NewGuid():N}");
    private readonly IFileSystem _fileSystem = new FileSystem();
    private string ConfigPath => Path.Combine(_directory, "config.json");

    public AuthorityCommitBoundaryTests() => Directory.CreateDirectory(_directory);

    private sealed class ControlledStore : IConfigStore
    {
        private JsonObject _document = new() { ["version"] = 1 };
        public bool RejectWrite { get; set; }
        public Action? AfterCommit { get; set; }
        public Task<ConfigStoreSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ConfigStoreSnapshot(1, ConfigDocumentFlattener.Flatten(_document)));
        }
        public Task WriteDocumentAsync(JsonObject document, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RejectWrite)
                throw new IOException("rejected-store-write");
            _document = document.DeepClone().AsObject();
            AfterCommit?.Invoke();
            return Task.CompletedTask;
        }
        public Task ApplyChangesAsync(ConfigChangeSet changes, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RejectWrite)
                throw new IOException("rejected-store-write");
            _document.Apply(changes);
            AfterCommit?.Invoke();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task StoreFailure_DoesNotProjectOrClaimCommittedWarning()
    {
        await File.WriteAllTextAsync(ConfigPath, "unchanged-projection");
        var store = new ControlledStore { RejectWrite = true };
        var backend = CreateBackend(store);
        var writer = new PlatformConfigWriter(ConfigPath, _fileSystem, null, backend, store);
        ConfigMutationResult? outcome = null;
        await Should.ThrowAsync<IOException>(async () => outcome = await writer.MutateWithResultAsync(root => root["version"] = 2, "reject"));
        (await writer.ReadAsync())["version"].ShouldNotBeNull().GetValue<int>().ShouldBe(1);
        (await File.ReadAllTextAsync(ConfigPath)).ShouldBe("unchanged-projection");
        outcome.ShouldBeNull();
    }

    [Fact]
    public async Task PostCommitCancellation_DoesNotReportCommittedEditAsCancelled()
    {
        await File.WriteAllTextAsync(ConfigPath, "stale");
        using var cancellation = new CancellationTokenSource();
        var store = new ControlledStore { AfterCommit = cancellation.Cancel };
        var writer = new PlatformConfigWriter(ConfigPath, _fileSystem, null, CreateBackend(store), store);
        var outcome = await writer.MutateWithResultAsync(root => root["version"] = 2, "commit", cancellation.Token);
        cancellation.IsCancellationRequested.ShouldBeTrue();
        (await writer.ReadAsync())["version"].ShouldNotBeNull().GetValue<int>().ShouldBe(2);
        JsonNode.DeepEquals(JsonNode.Parse(await File.ReadAllTextAsync(ConfigPath)), await writer.ReadAsync()).ShouldBeTrue();
        outcome.Warnings.ShouldBeEmpty();
        outcome.Errors.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeletedProjectionAtCommit_IsNotRecreated()
    {
        await File.WriteAllTextAsync(ConfigPath, "stale");
        var store = new ControlledStore { AfterCommit = () => File.Delete(ConfigPath) };
        var writer = new PlatformConfigWriter(ConfigPath, _fileSystem, null, CreateBackend(store), store);
        await writer.MutateAsync(root => root["version"] = 2, "commit");
        File.Exists(ConfigPath).ShouldBeFalse();
        (await writer.ReadAsync())["version"].ShouldNotBeNull().GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public async Task RawMutation_PreservesUnknownNullAndEmptyShapesWhileDeletingNamedKey()
    {
        var path = ConfigStoreBootstrap.ResolveStorePath(ConfigPath, _fileSystem);
        var seed = JsonNode.Parse("{\"version\":1,\"unknown\":{\"items\":[],\"empty\":{},\"null\":null,\"remove\":true},\"channels\":{\"token\":\"secret\"}}")
            .ShouldBeOfType<JsonObject>();
        await ConfigStoreBootstrap.PopulateAsync(path, seed);
        try
        {
            var writer = ConfigWriterFactory.Create(ConfigPath, _fileSystem);
            var before = await writer.ReadWithRevisionAsync();
            await writer.MutateAsync(root => root["unknown"].ShouldBeOfType<JsonObject>().Remove("remove"), "delete");
            var after = await writer.ReadAsync();
            var unknown = after["unknown"].ShouldBeOfType<JsonObject>();
            unknown.ContainsKey("remove").ShouldBeFalse();
            unknown.ContainsKey("null").ShouldBeTrue();
            unknown["null"].ShouldBeNull();
            unknown["empty"].ShouldBeOfType<JsonObject>().Count.ShouldBe(0);
            unknown["items"].ShouldBeOfType<JsonArray>().Count.ShouldBe(0);
            after["channels"].ShouldBeOfType<JsonObject>()["token"].ShouldNotBeNull().GetValue<string>().ShouldBe("secret");
            var next = await writer.ReadWithRevisionAsync();
            next.Revision.ShouldNotBe(before.Revision);
            File.Exists(ConfigPath).ShouldBeFalse();
        }
        finally
        {
            ConfigStoreBootstrap.ReleaseConnections(path);
        }
    }

    [Fact]
    public async Task MutateWithResultAsync_PreCancelledWhileAnotherMutationOwnsLock_NeverEntersMutation()
    {
        var store = new ControlledStore();
        var writer = new PlatformConfigWriter(ConfigPath, _fileSystem, null, CreateBackend(store), store);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = writer.MutateWithResultAsync(async _ =>
        {
            entered.SetResult(true);
            await release.Task;
        }, "held-no-op");
        var called = false;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => writer.MutateWithResultAsync(_ =>
            {
                called = true;
            }, "cancelled", cancellation.Token));
            await Should.ThrowAsync<OperationCanceledException>(() => writer.ApplyPatchAsync(
                [new("version", JsonValue.Create(2))], "nonempty-cancelled", ct: cancellation.Token));
            var empty = await writer.ApplyPatchAsync([], "empty-cancelled", ct: cancellation.Token);
            empty.Success.ShouldBeTrue();
            empty.Revision.ShouldBeNull();
            empty.Errors.ShouldBeEmpty();
            empty.Warnings.IsDefault.ShouldBeFalse();
            empty.Warnings.ShouldBeEmpty();
            called.ShouldBeFalse();
        }
        finally
        {
            release.TrySetResult(true);
            var firstOutcome = await first;
            firstOutcome.Errors.ShouldBeEmpty();
            firstOutcome.Warnings.ShouldBeEmpty();
        }
        (await writer.ReadAsync())["version"].ShouldNotBeNull().GetValue<int>().ShouldBe(1);
        File.Exists(ConfigPath).ShouldBeFalse();
        Directory.Exists(Path.Combine(_directory, "backups")).ShouldBeFalse();
    }

    private AuthorityConfigurationWriter CreateBackend(IConfigStore store) => new(store,
        Path.Combine(_directory, ConfigStoreBootstrap.StoreFileName), ConfigPath, _fileSystem,
        new ConfigBackupService(Path.Combine(_directory, "backups"), _fileSystem));

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
