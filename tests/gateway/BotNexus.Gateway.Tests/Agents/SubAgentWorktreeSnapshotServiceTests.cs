using System.IO.Abstractions;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class SubAgentWorktreeSnapshotServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "bnx-4288", Guid.NewGuid().ToString("N"));

    public SubAgentWorktreeSnapshotServiceTests() => Directory.CreateDirectory(_tempRoot);

    [Fact]
    public async Task CaptureAsync_NoWriteGrant_ReportsExplicitOutcome()
    {
        var runner = new FakeGitRunner();
        var service = CreateService(runner);

        var result = await service.CaptureAsync("sub-id", [], CancellationToken.None);

        result.Outcome.ShouldBe(SubAgentWorktreeSnapshotOutcome.NoWriteGrant);
        result.ArtifactPath.ShouldBeNull();
        runner.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task CaptureAsync_InaccessibleGrant_ReportsExplicitOutcome()
    {
        var runner = new FakeGitRunner();

        var result = await CreateService(runner).CaptureAsync(
            "sub-id", [Path.Combine(_tempRoot, "missing")], CancellationToken.None);

        result.Outcome.ShouldBe(SubAgentWorktreeSnapshotOutcome.NoAccessibleGrant);
        runner.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task CaptureAsync_NonGitGrant_ReportsExplicitOutcome()
    {
        var root = CreateDirectory("plain");
        var runner = new FakeGitRunner(_ => GitSnapshotProcessResult.Failed(128));

        var result = await CreateService(runner).CaptureAsync("sub-id", [root], CancellationToken.None);

        result.Outcome.ShouldBe(SubAgentWorktreeSnapshotOutcome.NoGitWorktree);
        result.ArtifactPath.ShouldBeNull();
    }

    [Fact]
    public async Task CaptureAsync_MultipleGitGrants_ChoosesLexicallyFirstRoot()
    {
        var aRoot = CreateDirectory("a-repo");
        var zRoot = CreateDirectory("z-repo");
        var runner = new FakeGitRunner(call => call.Arguments[0] switch
        {
            "rev-parse" => GitSnapshotProcessResult.Succeed(call.WorkingDirectory + Environment.NewLine),
            "ls-files" => GitSnapshotProcessResult.Succeed("new.txt\0"),
            "diff" => GitSnapshotProcessResult.Succeed("diff --git a/a.txt b/a.txt\n+changed\n"),
            _ => throw new InvalidOperationException()
        });

        var result = await CreateService(runner).CaptureAsync("sub-id", [zRoot, aRoot], CancellationToken.None);

        result.Outcome.ShouldBe(SubAgentWorktreeSnapshotOutcome.Captured);
        Path.GetFullPath(result.WorktreePath!).ShouldBe(Path.GetFullPath(aRoot));
        result.UntrackedPaths.ShouldBe(["new.txt"]);
        File.ReadAllText(result.ArtifactPath!).ShouldContain("+changed");
        runner.Calls[0].WorkingDirectory.ShouldBe(aRoot);
        runner.Calls.Single(call => call.Arguments[0] == "diff").Arguments
            .ShouldBe(["diff", "HEAD", "--no-ext-diff", "--no-textconv", "--binary"]);
    }

    [Fact]
    public async Task CaptureAsync_CleanRepo_KeepsBoundedUntrackedMetadataWithoutArtifact()
    {
        var root = CreateDirectory("repo");
        var runner = new FakeGitRunner(call => call.Arguments[0] switch
        {
            "rev-parse" => GitSnapshotProcessResult.Succeed(root + Environment.NewLine),
            "ls-files" => GitSnapshotProcessResult.Succeed("a.txt\0b.txt\0c.txt\0"),
            "diff" => GitSnapshotProcessResult.Succeed(string.Empty),
            _ => throw new InvalidOperationException()
        });
        var options = NewOptions();
        options.MaxUntrackedPaths = 2;

        var result = await CreateService(runner, options).CaptureAsync("sub-id", [root], CancellationToken.None);

        result.Outcome.ShouldBe(SubAgentWorktreeSnapshotOutcome.Clean);
        result.UntrackedPaths.ShouldBe(["a.txt", "b.txt"]);
        result.UntrackedPathsTruncated.ShouldBeTrue();
        result.ArtifactPath.ShouldBeNull();
    }

    [Fact]
    public async Task CaptureAsync_DiffExceedsCeiling_ReportsOversizedWithoutWritingArtifact()
    {
        var root = CreateDirectory("repo");
        var runner = new FakeGitRunner(call => call.Arguments[0] switch
        {
            "rev-parse" => GitSnapshotProcessResult.Succeed(root + Environment.NewLine),
            "ls-files" => GitSnapshotProcessResult.Succeed(string.Empty),
            "diff" => GitSnapshotProcessResult.OutputLimit(),
            _ => throw new InvalidOperationException()
        });

        var result = await CreateService(runner).CaptureAsync("sub-id", [root], CancellationToken.None);

        result.Outcome.ShouldBe(SubAgentWorktreeSnapshotOutcome.Oversized);
        result.ArtifactPath.ShouldBeNull();
    }

    [Fact]
    public async Task CaptureAsync_DiffTimesOut_ReportsTimedOut()
    {
        var root = CreateDirectory("repo-timeout");
        var runner = new FakeGitRunner(call => call.Arguments[0] switch
        {
            "rev-parse" => GitSnapshotProcessResult.Succeed(root + Environment.NewLine),
            "ls-files" => GitSnapshotProcessResult.Succeed(string.Empty),
            "diff" => GitSnapshotProcessResult.TimedOut(),
            _ => throw new InvalidOperationException()
        });

        var result = await CreateService(runner).CaptureAsync("sub-id", [root], CancellationToken.None);

        result.Outcome.ShouldBe(SubAgentWorktreeSnapshotOutcome.TimedOut);
        result.ArtifactPath.ShouldBeNull();
    }

    [Fact]
    public async Task CaptureAsync_RetentionCap_PrunesOldArtifactsBeforeWriting()
    {
        var root = CreateDirectory("repo-retention");
        var options = NewOptions();
        options.MaxRetainedArtifacts = 2;
        Directory.CreateDirectory(options.ArtifactRoot);
        File.WriteAllText(Path.Combine(options.ArtifactRoot, "old.patch"), "old");
        File.WriteAllText(Path.Combine(options.ArtifactRoot, "new.patch"), "new");
        File.SetLastWriteTimeUtc(Path.Combine(options.ArtifactRoot, "old.patch"), DateTime.UtcNow.AddMinutes(-2));
        File.SetLastWriteTimeUtc(Path.Combine(options.ArtifactRoot, "new.patch"), DateTime.UtcNow.AddMinutes(-1));
        var runner = new FakeGitRunner(call => call.Arguments[0] switch
        {
            "rev-parse" => GitSnapshotProcessResult.Succeed(root + Environment.NewLine),
            "ls-files" => GitSnapshotProcessResult.Succeed(string.Empty),
            "diff" => GitSnapshotProcessResult.Succeed("diff --git a/a b/a\n+change\n"),
            _ => throw new InvalidOperationException()
        });

        var result = await CreateService(runner, options).CaptureAsync("sub-id", [root], CancellationToken.None);

        result.Outcome.ShouldBe(SubAgentWorktreeSnapshotOutcome.Captured);
        Directory.GetFiles(options.ArtifactRoot, "*.patch").Length.ShouldBe(2);
        File.Exists(Path.Combine(options.ArtifactRoot, "old.patch")).ShouldBeFalse();
    }


    [Fact]
    public async Task CaptureAsync_RedactsPatchBeforePersistenceAndMeasuresPersistedContent()
    {
        var root = CreateDirectory("repo-redaction");
        var runner = SuccessfulRunner(root, "diff --git a/a b/a\n+token=SENTINEL-SECRET\n");

        var result = await CreateService(runner).CaptureAsync("sub-id", [root], CancellationToken.None);

        result.Outcome.ShouldBe(SubAgentWorktreeSnapshotOutcome.Captured);
        var persisted = File.ReadAllText(result.ArtifactPath!);
        persisted.ShouldNotContain("SENTINEL-SECRET");
        persisted.ShouldContain("[REDACTED-SENTINEL-LONGER-THAN-SECRET]");
        result.PatchBytes.ShouldBe(System.Text.Encoding.UTF8.GetByteCount(persisted));
        Directory.GetFiles(Path.GetDirectoryName(result.ArtifactPath!)!, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public async Task CaptureAsync_RedactedPatchAboveCeiling_IsRejectedBeforePersistence()
    {
        var root = CreateDirectory("repo-redaction-size");
        var options = NewOptions();
        options.MaxPatchBytes = 30;
        var runner = SuccessfulRunner(root, "+SENTINEL-SECRET\n");

        var result = await CreateService(runner, options).CaptureAsync("sub-id", [root], CancellationToken.None);

        result.Outcome.ShouldBe(SubAgentWorktreeSnapshotOutcome.Oversized);
        Directory.Exists(options.ArtifactRoot).ShouldBeFalse();
    }

    [Fact]
    public async Task CaptureAsync_NestedGrant_IsRejectedAsNotAWorktreeRoot()
    {
        var root = CreateDirectory("repo-root");
        var nested = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
        var runner = new FakeGitRunner(call => call.Arguments[0] == "rev-parse"
            ? GitSnapshotProcessResult.Succeed(root + Environment.NewLine)
            : throw new InvalidOperationException());

        var result = await CreateService(runner).CaptureAsync("sub-id", [nested], CancellationToken.None);

        result.Outcome.ShouldBe(SubAgentWorktreeSnapshotOutcome.NoGitWorktree);
        runner.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SweepArtifactsAsync_RemovesExpiredArtifactsWithoutCapture()
    {
        var options = NewOptions();
        options.Retention = TimeSpan.FromMinutes(30);
        Directory.CreateDirectory(options.ArtifactRoot);
        var expired = Path.Combine(options.ArtifactRoot, "expired.patch");
        var current = Path.Combine(options.ArtifactRoot, "current.patch");
        File.WriteAllText(expired, "old");
        File.WriteAllText(current, "new");
        File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddHours(-2));

        var removed = await CreateService(new FakeGitRunner(), options).SweepArtifactsAsync(CancellationToken.None);

        removed.ShouldBe(1);
        File.Exists(expired).ShouldBeFalse();
        File.Exists(current).ShouldBeTrue();
    }

    [Fact]
    public async Task SweepArtifactsAsync_IsBoundedByConfiguredFileCount()
    {
        var options = NewOptions();
        options.Retention = TimeSpan.FromMinutes(1);
        options.MaxSweepFiles = 1;
        Directory.CreateDirectory(options.ArtifactRoot);
        for (var index = 0; index < 3; index++)
        {
            var path = Path.Combine(options.ArtifactRoot, $"old-{index}.patch");
            File.WriteAllText(path, "old");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-2));
        }

        var removed = await CreateService(new FakeGitRunner(), options).SweepArtifactsAsync(CancellationToken.None);

        removed.ShouldBe(1);
        Directory.GetFiles(options.ArtifactRoot, "*.patch").Length.ShouldBe(2);
    }

    [Fact]
    public async Task SweepArtifactsAsync_RejectsVerifiedBotNexusHomeRoot()
    {
        var options = NewOptions();
        options.ArtifactRoot = Path.Combine(_tempRoot, "verified-home", "recovery");
        Directory.CreateDirectory(options.ArtifactRoot);
        var artifact = Path.Combine(options.ArtifactRoot, "old.patch");
        File.WriteAllText(artifact, "old");
        File.SetLastWriteTimeUtc(artifact, DateTime.UtcNow.AddDays(-2));

        var removed = await CreateService(new FakeGitRunner(), options).SweepArtifactsAsync(CancellationToken.None);

        removed.ShouldBe(0);
        File.Exists(artifact).ShouldBeTrue();
    }

    [Fact]
    public async Task CaptureAsync_DiffProcessFails_ReportsProcessFailed()
    {
        var root = CreateDirectory("repo");
        var runner = new FakeGitRunner(call => call.Arguments[0] switch
        {
            "rev-parse" => GitSnapshotProcessResult.Succeed(root + Environment.NewLine),
            "ls-files" => GitSnapshotProcessResult.Succeed(string.Empty),
            "diff" => GitSnapshotProcessResult.Failed(5),
            _ => throw new InvalidOperationException()
        });

        var result = await CreateService(runner).CaptureAsync("sub-id", [root], CancellationToken.None);

        result.Outcome.ShouldBe(SubAgentWorktreeSnapshotOutcome.ProcessFailed);
        result.ArtifactPath.ShouldBeNull();
    }


    private static FakeGitRunner SuccessfulRunner(string root, string diff) => new(call => call.Arguments[0] switch
    {
        "rev-parse" => GitSnapshotProcessResult.Succeed(root + Environment.NewLine),
        "ls-files" => GitSnapshotProcessResult.Succeed(string.Empty),
        "diff" => GitSnapshotProcessResult.Succeed(diff),
        _ => throw new InvalidOperationException()
    });

    private sealed class SentinelRedactor : ISecretRedactor
    {
        public string Redact(string input) => input.Replace("SENTINEL-SECRET", "[REDACTED-SENTINEL-LONGER-THAN-SECRET]", StringComparison.Ordinal);
        public string RedactForExternalDelivery(string input) => Redact(input);
    }

    private string CreateDirectory(string name)
    {
        var path = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private SubAgentWorktreeSnapshotService CreateService(IGitSnapshotProcessRunner runner, SubAgentWorktreeSnapshotOptions? options = null)
        => new(
            new FileSystem(),
            Options.Create(options ?? NewOptions()),
            runner,
            new SentinelRedactor(),
            new BotNexusHome(new FileSystem(), Path.Combine(_tempRoot, "verified-home")),
            TimeProvider.System,
            NullLogger<SubAgentWorktreeSnapshotService>.Instance);

    private SubAgentWorktreeSnapshotOptions NewOptions() => new()
    {
        ArtifactRoot = Path.Combine(_tempRoot, "artifacts"),
        Deadline = TimeSpan.FromSeconds(2),
        MaxPatchBytes = 1024,
        MaxUntrackedPaths = 10,
        Retention = TimeSpan.FromHours(1),
        MaxRetainedArtifacts = 10,
        MaxSweepFiles = 100
    };

    public void Dispose() => Directory.Delete(_tempRoot, recursive: true);

    private sealed class FakeGitRunner(Func<GitSnapshotProcessCall, GitSnapshotProcessResult>? responder = null) : IGitSnapshotProcessRunner
    {
        public List<GitSnapshotProcessCall> Calls { get; } = [];

        public Task<GitSnapshotProcessResult> RunAsync(GitSnapshotProcessCall call, CancellationToken ct)
        {
            Calls.Add(call);
            return Task.FromResult(responder?.Invoke(call) ?? throw new InvalidOperationException());
        }
    }
}
