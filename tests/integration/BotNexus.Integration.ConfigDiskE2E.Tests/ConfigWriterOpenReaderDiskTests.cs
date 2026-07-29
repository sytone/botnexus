using System.Text.Json.Nodes;

namespace BotNexus.Integration.ConfigDiskE2E.Tests;

/// <summary>
/// Issue #2357: a config save must not fail merely because another process (or the gateway's
/// own <c>AddJsonFile(..., reloadOnChange: true)</c> watcher) currently holds <c>config.json</c>
/// open for reading.
/// </summary>
/// <remarks>
/// On Windows, <c>File.Move(..., overwrite: true)</c> throws
/// <see cref="UnauthorizedAccessException"/> when any other handle is open on the destination,
/// even one opened with <c>FileShare.ReadWrite | FileShare.Delete</c>. That made config saves
/// fail intermittently (29 of 40 measured) and lose the user's edit. These tests pin the
/// availability property the writer must now provide: the replace step tolerates cooperative
/// readers, and every write completes.
/// </remarks>
public sealed class ConfigWriterOpenReaderDiskTests
{
    /// <summary>
    /// A single reader holding the file for the whole write, with the same maximal sharing the
    /// configuration provider's watcher uses, must not be able to fail the write. This is the
    /// deterministic form of the defect: with the old <c>File.Move</c> replace this throws on
    /// every attempt on Windows.
    /// </summary>
    [Fact]
    public async Task Write_WhileAReaderHoldsTheFileOpen_Succeeds()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);

        using (var held = new FileStream(
            home.ConfigPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete))
        {
            using var reader = new StreamReader(held);
            _ = await reader.ReadToEndAsync();

            // The handle is still open here, on purpose.
            await home.Writer.MutateAsync(
                root => root["cron"]!["tickIntervalSeconds"] = 4242,
                "test-open-reader");
        }

        home.ReadFromDisk()["cron"]!["tickIntervalSeconds"]!.GetValue<int>().ShouldBe(4242);
        home.ReadFromDisk()["providers"]!["github-copilot"]!["apiKey"]!.GetValue<string>()
            .ShouldBe("sk-copilot-REAL-secret");
    }

    /// <summary>
    /// The reproduction from the issue: a background reader churning open/read/close against the
    /// file while the production writer performs a run of sequential saves. Every save must
    /// complete. The measured failure rate before the fix was 29 of 40.
    /// </summary>
    [Fact]
    public async Task SequentialWrites_UnderAChurningReader_NeverFail()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        using var stop = new CancellationTokenSource();
        var reads = 0;

        var readerLoop = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var stream = new FileStream(
                        home.ConfigPath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var streamReader = new StreamReader(stream);
                    _ = streamReader.ReadToEnd();
                    Interlocked.Increment(ref reads);

                    // Hold the handle briefly so the writer's replace window overlaps a live
                    // reader; without this the race is too narrow to reproduce reliably.
                    Thread.Sleep(1);
                }
                catch (IOException)
                {
                    // Reader-side contention is not what this test measures.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        });

        var failures = new List<Exception>();
        const int writeCount = 40;
        for (var i = 0; i < writeCount; i++)
        {
            try
            {
                await home.Writer.MutateAsync(
                    root => root["cron"]!["tickIntervalSeconds"] = 60 + i,
                    $"test-open-reader-{i:D2}");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                failures.Add(ex);
            }
        }

        await stop.CancelAsync();
        await readerLoop;

        failures.Count.ShouldBe(0,
            $"#2357: every config save must survive an open reader; {failures.Count}/{writeCount} failed "
            + $"(first: {(failures.Count > 0 ? failures[0].GetType().Name + ": " + failures[0].Message : "none")})");
        reads.ShouldBeGreaterThan(0, "the reader must have actually held the file during the run");

        var after = home.ReadFromDisk();
        after["cron"]!["tickIntervalSeconds"]!.GetValue<int>().ShouldBe(60 + writeCount - 1);
        after["providers"]!["github-copilot"]!["apiKey"]!.GetValue<string>()
            .ShouldBe("sk-copilot-REAL-secret");
        home.ListConfigDirectoryFiles().ShouldBe(["config.json"]);
    }

    /// <summary>
    /// The replace must stay atomic-or-throw: a reader that cannot be tolerated within the retry
    /// budget must surface as an exception to the caller, never as a silently dropped edit, and
    /// never as a truncated or missing config file.
    /// </summary>
    [Fact]
    public async Task Write_WhenAnExclusiveHolderNeverYields_ThrowsAndLeavesTheOriginalIntact()
    {
        using var home = new ConfigHomeFixture(MaximalConfig.Json);
        var before = home.ReadRawText();

        // FileShare.None: no cooperative sharing at all, so no replace strategy can succeed.
        using (new FileStream(home.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Should.ThrowAsync<Exception>(async () =>
                await home.Writer.MutateAsync(
                    root => root["cron"]!["tickIntervalSeconds"] = 9999,
                    "test-exclusive-holder"));
        }

        home.ReadRawText().ShouldBe(before, "a failed replace must leave the original bytes untouched");
        home.ListConfigDirectoryFiles().ShouldBe(["config.json"], "a failed replace must not leave temp residue");
    }
}
