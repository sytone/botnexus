using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Configuration.Store;
using Xunit;

namespace BotNexus.Gateway.Tests;

/// <summary>Revision checks belong after lock acquisition and before any edit callback or output work.</summary>
public sealed class AgentConfigurationRevisionBoundaryTests
{
    /// <summary>A request queued with a current token must recheck after an intervening commit.</summary>
    [Theory]
    [InlineData("absent")]
    [InlineData("corrupt")]
    [InlineData("conflicting")]
    public async Task MutateValidatedWithResultAsync_QueuedRevisionBecomesStale_DoesNotInvokeCallbackOrWrite(string projection)
    {
        await WithAuthorityAsync(projection, async (writer, store, path) =>
        {
            var before = await writer.ReadWithRevisionAsync();
            var rowsBefore = await store.ReadSnapshotAsync();
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var holder = writer.MutateWithResultAsync(async root =>
            {
                entered.SetResult(true);
                await release.Task;
                root["opaque"].ShouldBeOfType<JsonObject>()["secret"] = "intervening-secret";
            }, "intervening-save");
            Task<ConfigMutationResult>? queued = null;
            var calls = 0;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                // The holder is inside both locks. This call runs synchronously up to the
                // occupied semaphore, so it is deterministically queued before release.
                queued = writer.MutateValidatedWithResultAsync(_ =>
                {
                    calls++;
                    return "invalid input must not be evaluated";
                }, "stale-save", expectedRevision: before.Revision);
                queued.IsCompleted.ShouldBeFalse();
            }
            finally
            {
                release.TrySetResult(true);
                (await holder).Errors.ShouldBeEmpty();
            }
            var pending = queued.ShouldNotBeNull();
            await Should.ThrowAsync<PlatformConfigConcurrencyException>(async () => await pending);
            calls.ShouldBe(0);
            var rowsAfter = await store.ReadSnapshotAsync();
            rowsAfter.Revision.ShouldBe(rowsBefore.Revision + 1);
            before.Config["opaque"].ShouldBeOfType<JsonObject>()["secret"] = "intervening-secret";
            JsonNode.DeepEquals(before.Config, await writer.ReadAsync()).ShouldBeTrue();
            var outputs = OutputHashes(path);
            var snapshot = await writer.ReadWithRevisionAsync();
            // Even empty or invalid editor batches must conflict before running the editor.
            foreach (var patch in new[] { new AgentConfigurationPatch(), new AgentConfigurationPatch { ToolTimeoutSeconds = 0 } })
            {
                await Should.ThrowAsync<PlatformConfigConcurrencyException>(async () =>
                    await AgentConfigurationEditor.ApplyWithResultAsync(writer, AgentId.From("sample"), patch,
                        CancellationToken.None, expectedRevision: before.Revision));
            }
            await AssertUnchangedAsync(writer, store, path, rowsAfter, snapshot.Revision, outputs);
        });
    }

    /// <summary>A queued current token still permits validation, and no-op edits produce no output work.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MutateValidatedWithResultAsync_QueuedCurrentRevision_InvokesCallbackExactlyOnce(bool reject)
    {
        await WithAuthorityAsync("conflicting", async (writer, store, path) =>
        {
            var before = await writer.ReadWithRevisionAsync();
            var rows = await store.ReadSnapshotAsync();
            var outputs = OutputHashes(path);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var holder = writer.MutateWithResultAsync(async _ =>
            {
                entered.SetResult(true);
                await release.Task;
            }, "holding-no-op");
            Task<ConfigMutationResult>? queued = null;
            var calls = 0;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                queued = writer.MutateValidatedWithResultAsync(_ =>
                {
                    calls++;
                    return reject ? "rejected" : null;
                }, "current-save", expectedRevision: before.Revision);
                queued.IsCompleted.ShouldBeFalse();
            }
            finally
            {
                release.TrySetResult(true);
                (await holder).Errors.ShouldBeEmpty();
            }
            var outcome = await queued.ShouldNotBeNull();
            calls.ShouldBe(1);
            outcome.Errors.ShouldBe(reject ? new[] { "rejected" } : Array.Empty<string>());
            outcome.Warnings.ShouldBeEmpty();
            await AssertUnchangedAsync(writer, store, path, rows, before.Revision, outputs);
        });
    }

    /// <summary>Cancellation while queued never invokes the callback and does not strand either lock.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MutateValidatedWithResultAsync_CancelledWhileQueued_NoCallbackOrPersistenceAndLocksRemainUsable(bool stale)
    {
        await WithAuthorityAsync("corrupt", async (writer, store, path) =>
        {
            var before = await writer.ReadWithRevisionAsync();
            var rows = await store.ReadSnapshotAsync();
            var outputs = OutputHashes(path);
            using var cancellation = new CancellationTokenSource();
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var holder = writer.MutateWithResultAsync(async _ =>
            {
                entered.SetResult(true);
                await release.Task;
            }, "holding-no-op");
            var calls = 0;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var queued = writer.MutateValidatedWithResultAsync(_ =>
                {
                    calls++;
                    return null;
                }, "cancelled-save", cancellation.Token, expectedRevision: stale ? "opaque-stale" : before.Revision);
                queued.IsCompleted.ShouldBeFalse();
                cancellation.Cancel();
                await Should.ThrowAsync<OperationCanceledException>(async () => await queued);
                calls.ShouldBe(0);
            }
            finally
            {
                release.TrySetResult(true);
                (await holder).Errors.ShouldBeEmpty();
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var outcome = await AgentConfigurationEditor.ApplyWithResultAsync(writer, AgentId.From("sample"),
                new AgentConfigurationPatch(), deadline.Token, expectedRevision: before.Revision);
            outcome.Errors.ShouldBeEmpty();
            outcome.Warnings.ShouldBeEmpty();
            await AssertUnchangedAsync(writer, store, path, rows, before.Revision, outputs);
        });
    }

    private static async Task AssertUnchangedAsync(PlatformConfigWriter writer, IConfigStore store, string path,
        ConfigStoreSnapshot rows, string revision, IReadOnlyDictionary<string, string> outputs)
    {
        var after = await store.ReadSnapshotAsync();
        after.Revision.ShouldBe(rows.Revision);
        JsonSerializer.Serialize(after.Entries.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            .ShouldBe(JsonSerializer.Serialize(rows.Entries.OrderBy(entry => entry.Key, StringComparer.Ordinal)));
        (await writer.ReadWithRevisionAsync()).Revision.ShouldBe(revision);
        OutputHashes(path).OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ShouldBe(outputs.OrderBy(entry => entry.Key, StringComparer.Ordinal));
    }

    private static Dictionary<string, string> OutputHashes(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException();
        return Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(file => file == path || file.StartsWith(Path.Combine(directory, "backups") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToDictionary(file => Path.GetRelativePath(directory, file), file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
    }

    private static async Task WithAuthorityAsync(string projection, Func<PlatformConfigWriter, IConfigStore, string, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bn-agent-revision-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.json");
        try
        {
            var fileSystem = new FileSystem();
            var store = new SqliteConfigStore(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = ConfigStoreBootstrap.ResolveStorePath(path, fileSystem), Pooling = false
            }.ToString());
            await store.WriteDocumentAsync(JsonNode.Parse("""
                {"agents":{"sample":{"provider":"test","model":"test","toolTimeoutSeconds":null}},
                 "opaque":{"secret":"original-secret","explicitNull":null,"empty":[]}}
                """).ShouldBeOfType<JsonObject>());
            if (projection != "absent")
                await File.WriteAllTextAsync(path, projection == "corrupt" ? "not-json" : "{\"opaque\":\"conflicting\"}");
            var backups = Path.Combine(directory, "backups");
            Directory.CreateDirectory(backups);
            await File.WriteAllTextAsync(Path.Combine(backups, "retained.json"), "retained-output");
            var writer = new PlatformConfigWriter(path, fileSystem, new ConfigBackupService(backups, fileSystem));
            await action(writer, store, path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
