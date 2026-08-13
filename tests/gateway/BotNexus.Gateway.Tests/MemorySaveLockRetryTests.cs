using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Covers issue #2909: the daily-note append lost the durable write outright when another process
/// held the file. These pin the bounded-retry contract, the sharing mode, and the exhaustion message.
/// </summary>
public sealed class MemorySaveLockRetryTests : IDisposable
{
    private readonly string _realRoot;

    public MemorySaveLockRetryTests()
    {
        _realRoot = Path.Combine(Path.GetTempPath(), "botnexus-2909-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_realRoot);
    }

    // ---- Clause 1 + 3: bounded retry semantics -------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_WhenNoContention_RunsExactlyOnceAndDoesNotDelay()
    {
        var attempts = 0;
        var delays = 0;

        await FileSharingViolationRetry.ExecuteAsync(
            (_, _) => { attempts++; return Task.CompletedTask; },
            "happy path",
            CancellationToken.None,
            delay: (_, _) => { delays++; return Task.CompletedTask; });

        attempts.ShouldBe(1);
        delays.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_WhenHandleReleasedAfterThreeAttempts_Succeeds()
    {
        var attempts = 0;
        var delays = 0;

        await FileSharingViolationRetry.ExecuteAsync(
            (attempt, _) =>
            {
                attempts++;
                if (attempt < 4)
                    throw SharingViolation();
                return Task.CompletedTask;
            },
            "contention path",
            CancellationToken.None,
            delay: (_, _) => { delays++; return Task.CompletedTask; });

        attempts.ShouldBe(4);
        delays.ShouldBe(3);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAllAttemptsFail_ThrowsStatingTheRetriesHappened()
    {
        var attempts = 0;

        var ex = await Should.ThrowAsync<IOException>(async () =>
            await FileSharingViolationRetry.ExecuteAsync(
                (_, _) => { attempts++; throw SharingViolation(); },
                "Appending memory note to 'C:/notes/today.md'",
                CancellationToken.None,
                delay: (_, _) => Task.CompletedTask));

        attempts.ShouldBe(FileSharingViolationRetry.DefaultMaxAttempts);
        ex.Message.Contains("after 5 attempts", StringComparison.Ordinal)
            .ShouldBeTrue("the failure must state that the retries happened, per clause 3.");
        ex.Message.Contains("Appending memory note", StringComparison.Ordinal)
            .ShouldBeTrue("the failure must name the operation that was lost.");
        ex.Message.Contains("was not applied", StringComparison.Ordinal)
            .ShouldBeTrue("the caller must be told the write did not land.");
        ex.InnerException.ShouldBeOfType<IOException>();
    }

    [Fact]
    public async Task ExecuteAsync_WhenFailureIsNotASharingViolation_DoesNotRetry()
    {
        var attempts = 0;

        await Should.ThrowAsync<IOException>(async () =>
            await FileSharingViolationRetry.ExecuteAsync(
                (_, _) => { attempts++; throw new IOException("disk full") { HResult = unchecked((int)0x80070070) }; },
                "non-transient",
                CancellationToken.None,
                delay: (_, _) => Task.CompletedTask));

        attempts.ShouldBe(1, "a non-transient IOException must surface immediately, not burn the retry budget.");
    }

    [Fact]
    public void IsSharingViolation_ClassifiesWin32CodesCorrectly()
    {
        FileSharingViolationRetry.IsSharingViolation(SharingViolation()).ShouldBeTrue();
        FileSharingViolationRetry.IsSharingViolation(
            new IOException("lock") { HResult = unchecked((int)0x80070021) }).ShouldBeTrue();
        FileSharingViolationRetry.IsSharingViolation(
            new IOException("not found") { HResult = unchecked((int)0x80070002) }).ShouldBeFalse();
    }

    // ---- Clause 1 + 2 through the real save path -----------------------------------------------

    [Fact]
    public async Task SaveMemoryAsync_WithNoContention_AppendsBothEntries()
    {
        var fileSystem = new MockFileSystem();
        var manager = new FileAgentWorkspaceManager(
            new BotNexusHome(fileSystem, Path.Combine(Path.GetTempPath(), "botnexus", "lock-retry")),
            fileSystem);

        await manager.SaveMemoryAsync("farnsworth", "first entry");
        await manager.SaveMemoryAsync("farnsworth", "second entry");

        var dailyPath = Path.Combine(
            manager.GetWorkspacePath("farnsworth"), "memory", $"{DateTime.UtcNow:yyyy-MM-dd}.md");
        var content = await fileSystem.File.ReadAllTextAsync(dailyPath);

        content.ShouldContain("first entry");
        content.ShouldContain("second entry");
    }

    [Fact]
    public async Task SaveMemoryAsync_WhileAConcurrentReaderHoldsTheFile_StillAppends()
    {
        var fileSystem = new FileSystem();
        var manager = new FileAgentWorkspaceManager(new BotNexusHome(fileSystem, _realRoot), fileSystem);

        await manager.SaveMemoryAsync("farnsworth", "existing entry");

        var dailyPath = Path.Combine(
            manager.GetWorkspacePath("farnsworth"), "memory", $"{DateTime.UtcNow:yyyy-MM-dd}.md");

        // A concurrent reader holding the file open must not defeat the append (clause 2).
        using (var reader = new FileStream(dailyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            await manager.SaveMemoryAsync("farnsworth", "appended under a live reader");
        }

        var content = await File.ReadAllTextAsync(dailyPath);
        content.ShouldContain("existing entry");
        content.ShouldContain("appended under a live reader");
    }

    private static IOException SharingViolation()
        => new("The process cannot access the file because it is being used by another process.")
        {
            HResult = unchecked((int)0x80070020)
        };

    public void Dispose()
    {
        if (Directory.Exists(_realRoot))
        {
            try { Directory.Delete(_realRoot, recursive: true); }
            catch (IOException) { /* best-effort temp cleanup */ }
        }
    }
}
