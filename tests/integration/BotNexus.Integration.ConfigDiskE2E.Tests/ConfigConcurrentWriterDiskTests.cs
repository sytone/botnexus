using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Integration.ConfigDiskE2E.Tests;

/// <summary>
/// Concurrent-writer behaviour against one physical config file.
/// </summary>
/// <remarks>
/// This is the scenario an in-memory filesystem is least able to simulate honestly: real
/// read-modify-write races end in lost updates or a torn file, and the only thing standing
/// between the platform and that outcome is the writer's serialisation plus the atomic
/// temp-file-and-move replacement. These tests assert both the safety property (the file is
/// always parseable) and the liveness property (no update is silently lost).
/// </remarks>
public sealed class ConfigConcurrentWriterDiskTests
{
    /// <summary>
    /// Many concurrent writers each adding a distinct key must all be represented in the final
    /// document. A lost update here means one user's config edit vanished with no error.
    /// </summary>
    [Fact]
    public async Task ConcurrentWriters_AllMutationsSurvive()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        const int writerCount = 24;

        var writes = Enumerable.Range(0, writerCount).Select(i =>
            home.Writer.MutateAsync(
                root =>
                {
                    var bag = root["providers"]!.AsObject();
                    bag[$"synthetic-{i:D2}"] = new JsonObject
                    {
                        ["enabled"] = false,
                        ["defaultModel"] = $"model-{i:D2}"
                    };
                },
                $"test-concurrent-{i:D2}"));

        await Task.WhenAll(writes);

        var providers = home.ReadFromDisk()["providers"]!.AsObject();
        for (var i = 0; i < writerCount; i++)
        {
            providers.ContainsKey($"synthetic-{i:D2}")
                .ShouldBeTrue($"writer {i} lost its update under contention");
        }

        providers["github-copilot"]!["apiKey"]!.GetValue<string>().ShouldBe("sk-copilot-REAL-secret");
    }

    /// <summary>
    /// While writers contend, an independent reader polling the physical file must never observe
    /// a partially written or truncated document. Atomic replacement is the guarantee; this test
    /// is what makes it a tested guarantee rather than a comment.
    /// </summary>
    /// <remarks>
    /// The reader here also covers #2357: on Windows, <c>File.Move(..., overwrite: true)</c> threw
    /// <see cref="UnauthorizedAccessException"/> when any handle was open on the destination, even
    /// one opened with <c>FileShare.ReadWrite | FileShare.Delete</c>. Now that the writer replaces
    /// via <c>File.Replace</c> under a bounded retry, this test asserts the availability property
    /// alongside the integrity one: no write may fail under a concurrent reader.
    /// </remarks>
    [Fact]
    public async Task ConcurrentWriters_NeverExposeATornFileToReaders()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        using var stop = new CancellationTokenSource();
        var tornReads = 0;
        var successfulReads = 0;

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    // Open with maximal sharing, exactly as the configuration provider's file
                    // watcher does; a plain File.ReadAllText would report ordinary write-time
                    // contention as a failure and mask the property actually under test.
                    using var stream = new FileStream(
                        home.ConfigPath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    var text = reader.ReadToEnd();

                    if (text.Length == 0)
                    {
                        // The move window can expose a zero-length view on some filesystems; that
                        // is not a torn *document*, so re-read rather than counting it.
                        continue;
                    }

                    if (JsonNode.Parse(text) is null)
                        Interlocked.Increment(ref tornReads);
                    else
                        Interlocked.Increment(ref successfulReads);
                }
                catch (System.Text.Json.JsonException)
                {
                    Interlocked.Increment(ref tornReads);
                }
                catch (IOException)
                {
                    // A transient sharing violation during the atomic move is expected on Windows
                    // and is NOT a torn read: the reader simply could not open the file yet.
                }
                catch (UnauthorizedAccessException)
                {
                    // Same class of transient contention as IOException, surfaced with a different
                    // exception type on Windows while the replacement is in flight.
                }
            }
        });

        var writeFailures = 0;
        for (var i = 0; i < 30; i++)
        {
            try
            {
                await home.Writer.MutateAsync(
                    root => root["cron"]!["tickIntervalSeconds"] = 60 + i,
                    $"test-torn-{i:D2}");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // #2357 regression guard: counted, then asserted to be zero below.
                writeFailures++;
            }
        }

        await stop.CancelAsync();
        await reader;

        tornReads.ShouldBe(0, "atomic replacement must never expose a partially written config");
        successfulReads.ShouldBeGreaterThan(0, "the reader must have actually observed the file");
        writeFailures.ShouldBe(0, "#2357: no config save may fail because a reader holds the file");

        // The surviving document must still be intact regardless of how many writes lost the race.
        home.ReadFromDisk()["providers"]!["github-copilot"]!["apiKey"]!.GetValue<string>()
            .ShouldBe("sk-copilot-REAL-secret");
    }

    /// <summary>
    /// Contention must not leave temp-file residue behind, even when many writes interleave.
    /// </summary>
    [Fact]
    public async Task ConcurrentWriters_LeaveNoTempResidue()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);

        var writes = Enumerable.Range(0, 20).Select(i =>
            home.Writer.MutateAsync(
                root => root["cron"]!["tickIntervalSeconds"] = 60 + i,
                $"test-residue-{i:D2}"));
        await Task.WhenAll(writes);

        home.ListConfigDirectoryFiles().ShouldBe(["config.json"]);
    }

    /// <summary>
    /// Two writers targeting the same physical file through separate
    /// <see cref="PlatformConfigWriter"/> instances (the shape of a CLI process and the gateway
    /// racing on one home directory) must still serialise: neither instance may clobber the
    /// other's section.
    /// </summary>
    [Fact]
    public async Task TwoWriterInstances_OnTheSameFile_DoNotClobberEachOther()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        var second = new PlatformConfigWriter(home.ConfigPath, home.FileSystem, home.BackupService);

        var first = Task.Run(async () =>
        {
            for (var i = 0; i < 15; i++)
                await home.Writer.MutateAsync(root => root["cron"]!["tickIntervalSeconds"] = 60 + i, "test-a");
        });

        var other = Task.Run(async () =>
        {
            for (var i = 0; i < 15; i++)
                await second.MutateAsync(root => root["gateway"]!["logLevel"] = i % 2 == 0 ? "Debug" : "Warning", "test-b");
        });

        await Task.WhenAll(first, other);

        var after = home.ReadFromDisk();
        after["cron"]!["tickIntervalSeconds"]!.GetValue<int>().ShouldBe(74);
        after["gateway"]!["logLevel"]!.GetValue<string>().ShouldBe("Debug");
        after["providers"]!["github-copilot"]!["apiKey"]!.GetValue<string>().ShouldBe("sk-copilot-REAL-secret");
    }
}
