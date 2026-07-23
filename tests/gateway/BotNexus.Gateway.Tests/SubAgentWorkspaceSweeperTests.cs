using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using BotNexus.Gateway.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Acceptance tests for the age-based sweep of completed sub-agent workspace directories (#2237):
/// TTL expiry removal, grace-window skip, top-level / registered protection, and reparse-point
/// (symlink) escape prevention.
/// </summary>
public sealed class SubAgentWorkspaceSweeperTests
{
    private readonly MockFileSystem _fileSystem = new();
    private readonly string _agentsRoot;
    private readonly SubAgentWorkspaceSweeper _sweeper;
    private static readonly DateTime NowUtc = new(2025, 1, 10, 12, 0, 0, DateTimeKind.Utc);

    public SubAgentWorkspaceSweeperTests()
    {
        _agentsRoot = _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), "botnexus-sweep-tests", "agents");
        _fileSystem.Directory.CreateDirectory(_agentsRoot);
        _sweeper = new SubAgentWorkspaceSweeper(_fileSystem, NullLogger.Instance);
    }

    private string AddSubAgentDir(string name, DateTime lastWriteUtc, long fileBytes = 0)
    {
        var dir = _fileSystem.Path.Combine(_agentsRoot, name);
        _fileSystem.Directory.CreateDirectory(dir);
        if (fileBytes > 0)
        {
            var file = _fileSystem.Path.Combine(dir, "payload.bin");
            _fileSystem.File.WriteAllBytes(file, new byte[fileBytes]);
        }

        _fileSystem.Directory.SetLastWriteTimeUtc(dir, lastWriteUtc);
        return dir;
    }

    [Fact]
    public void Sweep_RemovesSubAgentDirectory_OlderThanRetention()
    {
        var dir = AddSubAgentDir(
            "farnsworth--subagent--coder--abc123",
            NowUtc - TimeSpan.FromHours(48),
            fileBytes: 1024);

        var result = _sweeper.Sweep(_agentsRoot, TimeSpan.FromHours(24), TimeSpan.FromHours(1), NowUtc);

        result.Removed.ShouldBe(1);
        result.BytesReclaimed.ShouldBe(1024);
        result.SkippedRecent.ShouldBe(0);
        _fileSystem.Directory.Exists(dir).ShouldBeFalse();
    }

    [Fact]
    public void Sweep_SkipsDirectory_ModifiedWithinGraceWindow()
    {
        // Older than retention would be, but touched 10 minutes ago: a possibly-live worker.
        var dir = AddSubAgentDir(
            "farnsworth--subagent--coder--live999",
            NowUtc - TimeSpan.FromMinutes(10));

        var result = _sweeper.Sweep(_agentsRoot, TimeSpan.FromHours(24), TimeSpan.FromHours(1), NowUtc);

        result.Removed.ShouldBe(0);
        result.SkippedRecent.ShouldBe(1);
        _fileSystem.Directory.Exists(dir).ShouldBeTrue();
    }

    [Fact]
    public void Sweep_SkipsDirectory_YoungerThanRetentionButOutsideGrace()
    {
        var dir = AddSubAgentDir(
            "farnsworth--subagent--coder--recent",
            NowUtc - TimeSpan.FromHours(3));

        var result = _sweeper.Sweep(_agentsRoot, TimeSpan.FromHours(24), TimeSpan.FromHours(1), NowUtc);

        result.Removed.ShouldBe(0);
        result.SkippedRecent.ShouldBe(1);
        _fileSystem.Directory.Exists(dir).ShouldBeTrue();
    }

    [Fact]
    public void Sweep_NeverTouchesTopLevelRegisteredAgentWorkspaces()
    {
        // A registered top-level agent directory: no --subagent-- marker, very old. Must survive.
        var registered = _fileSystem.Path.Combine(_agentsRoot, "farnsworth");
        _fileSystem.Directory.CreateDirectory(registered);
        _fileSystem.Directory.SetLastWriteTimeUtc(registered, NowUtc - TimeSpan.FromDays(365));

        var husk = AddSubAgentDir("farnsworth--subagent--coder--old", NowUtc - TimeSpan.FromDays(2));

        var result = _sweeper.Sweep(_agentsRoot, TimeSpan.FromHours(24), TimeSpan.FromHours(1), NowUtc);

        result.Removed.ShouldBe(1);
        _fileSystem.Directory.Exists(registered).ShouldBeTrue();
        _fileSystem.Directory.Exists(husk).ShouldBeFalse();
    }

    [Fact]
    public void Sweep_DoesNotFollowOrDeleteThroughReparsePoint()
    {
        var dir = AddSubAgentDir("farnsworth--subagent--coder--symlinked", NowUtc - TimeSpan.FromDays(3));

        // Mark the directory as a reparse point (symlink / junction). The sweep must refuse to
        // delete through it so a recursive delete can never escape the agents root.
        var data = _fileSystem.GetFile(dir);
        data.Attributes |= FileAttributes.ReparsePoint;

        var result = _sweeper.Sweep(_agentsRoot, TimeSpan.FromHours(24), TimeSpan.FromHours(1), NowUtc);

        result.Removed.ShouldBe(0);
        _fileSystem.Directory.Exists(dir).ShouldBeTrue();
    }

    [Fact]
    public void Sweep_WithNonPositiveRetention_IsNoOp()
    {
        var dir = AddSubAgentDir("farnsworth--subagent--coder--x", NowUtc - TimeSpan.FromDays(30));

        var result = _sweeper.Sweep(_agentsRoot, TimeSpan.Zero, TimeSpan.FromHours(1), NowUtc);

        result.Removed.ShouldBe(0);
        _fileSystem.Directory.Exists(dir).ShouldBeTrue();
    }

    [Fact]
    public void Sweep_MissingAgentsRoot_IsNoOp()
    {
        var missing = _fileSystem.Path.Combine(_agentsRoot, "does-not-exist");

        var result = _sweeper.Sweep(missing, TimeSpan.FromHours(24), TimeSpan.FromHours(1), NowUtc);

        result.Removed.ShouldBe(0);
        result.BytesReclaimed.ShouldBe(0);
        result.SkippedRecent.ShouldBe(0);
    }

    [Fact]
    public void Sweep_RemovesMultipleHusks_AndAggregatesCounts()
    {
        AddSubAgentDir("a--subagent--coder--1", NowUtc - TimeSpan.FromDays(2), fileBytes: 500);
        AddSubAgentDir("b--subagent--coder--2", NowUtc - TimeSpan.FromDays(2), fileBytes: 700);
        AddSubAgentDir("c--subagent--coder--live", NowUtc - TimeSpan.FromMinutes(5));

        var result = _sweeper.Sweep(_agentsRoot, TimeSpan.FromHours(24), TimeSpan.FromHours(1), NowUtc);

        result.Removed.ShouldBe(2);
        result.BytesReclaimed.ShouldBe(1200);
        result.SkippedRecent.ShouldBe(1);
    }
}
