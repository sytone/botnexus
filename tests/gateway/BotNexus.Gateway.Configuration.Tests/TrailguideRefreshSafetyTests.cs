using System.Diagnostics;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Configuration.Tests;

public sealed class TrailguideRefreshSafetyTests
{
    private static readonly string[] ManagedFiles =
    [
        "AGENTS.md", "SOUL.md", "skills/trailguide-documentation/SKILL.md",
        "skills/trailguide-troubleshooting/SKILL.md", "DOCUMENTATION_ROOT.md"
    ];

    [Fact]
    public void GetAgentDirectory_FirstCreationPublishesCompleteSiblingsForEveryManagedFile()
    {
        using var fixture = new Fixture();
        var published = new List<string>();
        fixture.FileSystem.BeforeMove = (source, destination) =>
        {
            Path.GetDirectoryName(source).ShouldBe(Path.GetDirectoryName(destination));
            File.ReadAllText(source).Length.ShouldBeGreaterThan(10);
            published.Add(Path.GetRelativePath(fixture.Workspace, destination).Replace('\\', '/'));
        };
        fixture.Home.GetAgentDirectory("nexus-trailguide");
        published.Order().ShouldBe(ManagedFiles.Order());
        Directory.Exists(Path.Combine(fixture.Root, "agents", "nexus-trailguide", "data", "sessions")).ShouldBeTrue();
        fixture.AssertNoTemps();
    }

    [Fact]
    public void GetAgentDirectory_UnchangedManagedFilesAreNotWritten()
    {
        using var fixture = new Fixture();
        fixture.Home.GetAgentDirectory("nexus-trailguide");
        fixture.FileSystem.RejectWrites = true;
        fixture.Home.GetAgentDirectory("nexus-trailguide");
        fixture.AssertNoTemps();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetAgentDirectory_FailedPublicationPreservesOldContentAndCleansSibling(bool firstCreation)
    {
        using var fixture = new Fixture();
        if (!firstCreation) fixture.Home.GetAgentDirectory("nexus-trailguide");
        var soul = Path.Combine(fixture.Workspace, "SOUL.md");
        if (!firstCreation) File.WriteAllText(soul, "complete old soul");
        fixture.FileSystem.BeforeMove = (_, destination) =>
        {
            if (destination == soul) throw new IOException("disk failure", unchecked((int)0x80070070));
        };
        var error = Should.Throw<IOException>(() => fixture.Home.GetAgentDirectory("nexus-trailguide"));
        error.Message.ShouldBe("disk failure");
        if (firstCreation) File.Exists(soul).ShouldBeFalse();
        else File.ReadAllText(soul).ShouldBe("complete old soul");
        fixture.AssertNoTemps();
    }

    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    public void GetAgentDirectory_SharingAndLockViolationsHaveBoundedTypedFailure(int code)
    {
        using var fixture = new Fixture();
        fixture.Home.GetAgentDirectory("nexus-trailguide");
        File.WriteAllText(Path.Combine(fixture.Workspace, "SOUL.md"), "old soul");
        var attempts = 0;
        fixture.FileSystem.BeforeMove = (_, destination) =>
        {
            if (!destination.EndsWith("SOUL.md", StringComparison.Ordinal)) return;
            attempts++;
            throw new IOException("locked", unchecked((int)(0x80070000u | (uint)code)));
        };
        var watch = Stopwatch.StartNew();
        var error = Should.Throw<IOException>(() => fixture.Home.GetAgentDirectory("nexus-trailguide"));
        error.GetType().Name.ShouldBe("TrailguideRefreshException");
        attempts.ShouldBe(5);
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        File.ReadAllText(Path.Combine(fixture.Workspace, "SOUL.md")).ShouldBe("old soul");
        fixture.AssertNoTemps();
    }

    [Theory]
    [InlineData("AGENTS.md")]
    [InlineData("SOUL.md")]
    [InlineData("skills/trailguide-documentation/SKILL.md")]
    [InlineData("skills/trailguide-troubleshooting/SKILL.md")]
    [InlineData("DOCUMENTATION_ROOT.md")]
    public void GetAgentDirectory_ReadersKeepCompleteOldSnapshotAcrossPublication(string relativePath)
    {
        using var fixture = new Fixture();
        fixture.Home.GetAgentDirectory("nexus-trailguide");
        var soul = Path.Combine(fixture.Workspace, relativePath);
        var expected = File.ReadAllText(soul);
        File.WriteAllText(soul, "complete old soul");
        using var snapshot = new FileStream(soul, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fixture.FileSystem.BeforeMove = (source, destination) =>
        {
            if (destination != soul) return;
            File.ReadAllText(destination).ShouldBe("complete old soul");
            File.ReadAllText(source).ShouldBe(expected);
        };
        fixture.Home.GetAgentDirectory("nexus-trailguide");
        using var reader = new StreamReader(snapshot);
        reader.ReadToEnd().ShouldBe("complete old soul");
        File.ReadAllText(soul).ShouldBe(expected);
        fixture.AssertNoTemps();
    }

    [Fact]
    public void GetAgentDirectory_RealHandleContentionRecoversAndPersistentContentionIsBounded()
    {
        using var fixture = new Fixture();
        fixture.Home.GetAgentDirectory("nexus-trailguide");
        var soul = Path.Combine(fixture.Workspace, "SOUL.md");
        var expected = File.ReadAllText(soul);
        File.WriteAllText(soul, "old soul");
        // Linux permits rename of open files; assert that platform's real semantics too.
        using (var held = new FileStream(soul, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var attempts = 0;
            fixture.FileSystem.BeforeMove = (_, destination) =>
            {
                if (destination == soul && ++attempts == 2) held.Dispose();
            };
            fixture.Home.GetAgentDirectory("nexus-trailguide");
            attempts.ShouldBe(OperatingSystem.IsWindows() ? 2 : 1);
        }
        File.ReadAllText(soul).ShouldBe(expected);
        File.WriteAllText(soul, "old soul");
        fixture.FileSystem.BeforeMove = null;
        using (var held = new FileStream(soul, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var watch = Stopwatch.StartNew();
            if (OperatingSystem.IsWindows())
            {
                var error = Should.Throw<IOException>(() => fixture.Home.GetAgentDirectory("nexus-trailguide"));
                error.GetType().Name.ShouldBe("TrailguideRefreshException");
                File.ReadAllText(soul).ShouldBe("old soul");
            }
            else
            {
                fixture.Home.GetAgentDirectory("nexus-trailguide");
                File.ReadAllText(soul).ShouldBe(expected);
            }
            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        }
        fixture.AssertNoTemps();
    }

    [Fact]
    public async Task GetAgentDirectory_ConcurrentInstancesCoordinateFirstCreationAndRefresh()
    {
        using var fixture = new Fixture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var active = 0;
        var maximum = 0;
        fixture.FileSystem.BeforeMove = (_, _) =>
        {
            var current = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, current);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
            Interlocked.Decrement(ref active);
        };
        var first = Task.Run(() => fixture.Home.GetAgentDirectory("nexus-trailguide"));
        try
        {
            entered.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
            var secondHome = new BotNexusHome(fixture.FileSystem, fixture.Root, fixture.Root);
            var second = Task.Run(() => secondHome.GetAgentDirectory("nexus-trailguide"));
            release.Set();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            maximum.ShouldBe(1);
        }
        finally
        {
            release.Set();
            await first;
        }
        var custom = Path.Combine(fixture.Workspace, "TRAILGUIDE.custom.md");
        File.WriteAllText(custom, "custom overlay");
        fixture.Home.GetAgentDirectory("nexus-trailguide");
        File.ReadAllText(custom).ShouldBe("custom overlay");
        fixture.AssertNoTemps();
    }

    [Fact]
    public void GetAgentDirectory_TrailguidePreservesLegacyUserFilesAndOrdinaryScaffolding()
    {
        using var fixture = new Fixture();
        var agent = Path.Combine(fixture.Root, "agents", "nexus-trailguide");
        Directory.CreateDirectory(agent);
        File.WriteAllText(Path.Combine(agent, "MEMORY.md"), "legacy user memory");
        fixture.Home.GetAgentDirectory("nexus-trailguide");
        File.ReadAllText(Path.Combine(fixture.Workspace, "MEMORY.md")).ShouldBe("legacy user memory");
        File.Exists(Path.Combine(fixture.Workspace, "BOOTSTRAP.md")).ShouldBeFalse();
        var ordinary = Path.Combine(fixture.Home.GetAgentDirectory("ordinary"), "workspace");
        File.Exists(Path.Combine(ordinary, "BOOTSTRAP.md")).ShouldBeTrue();
        File.Exists(Path.Combine(ordinary, "IDENTITY.md")).ShouldBeTrue();
    }

    [Fact]
    public void GetAgentDirectory_MockFilesystemSupportsAtomicRefresh()
    {
        var fs = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
        var root = Path.GetFullPath(Path.Combine("artifacts", "tmp", "mock-home"));
        var home = new BotNexusHome(fs, root, root);
        var workspace = Path.Combine(home.GetAgentDirectory("nexus-trailguide"), "workspace");
        fs.File.WriteAllText(Path.Combine(workspace, "SOUL.md"), "stale");
        home.GetAgentDirectory("nexus-trailguide");
        fs.File.ReadAllText(Path.Combine(workspace, "SOUL.md")).ShouldContain("Trailguide's Identity and Conduct");
        fs.Directory.EnumerateFiles(workspace, "*.tmp", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAgentDirectory_QueuedRefreshHasBoundedCoordinationFailure()
    {
        using var fixture = new Fixture();
        fixture.Home.GetAgentDirectory("nexus-trailguide");
        var soul = Path.Combine(fixture.Workspace, "SOUL.md");
        var canonical = File.ReadAllBytes(soul);
        File.WriteAllText(soul, "complete old soul");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.FileSystem.BeforeMove = (_, destination) =>
        {
            if (destination != soul) return;
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
        };
        var first = Task.Factory.StartNew(() => fixture.Home.GetAgentDirectory("nexus-trailguide"), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task<Exception>? second = null;
        try
        {
            entered.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
            var secondHome = new BotNexusHome(fixture.FileSystem, fixture.Root, fixture.Root);
            second = Task.Factory.StartNew(() => Record.Exception(() => secondHome.GetAgentDirectory("nexus-trailguide")), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            // The publication remains blocked until this caller has completed. No sleeping
            // or absence-of-progress observation is used to prove the contention bound.
            var error = await second.WaitAsync(TimeSpan.FromSeconds(3));
            var contention = error.ShouldBeOfType<TrailguideRefreshException>();
            contention.Reason.ShouldBe(TrailguideRefreshFailureReason.CoordinationTimeout);
            contention.InnerException.ShouldBeNull();
            contention.Message.ShouldContain("coordination");
            error.Message.ShouldNotContain("5 attempts");
            File.ReadAllText(soul).ShouldBe("complete old soul");
        }
        finally
        {
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(5));
        }
        File.ReadAllBytes(soul).ShouldBe(canonical);
        fixture.AssertNoTemps();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetAgentDirectory_DestinationRaceKeepsCompletePublicationAndCleansSibling(bool disappears)
    {
        using var fixture = new Fixture();
        fixture.Home.GetAgentDirectory("nexus-trailguide");
        var soul = Path.Combine(fixture.Workspace, "SOUL.md");
        var canonical = File.ReadAllBytes(soul);
        File.WriteAllText(soul, "old soul");
        fixture.FileSystem.BeforeMove = (_, destination) =>
        {
            if (destination != soul) return;
            if (disappears) File.Delete(soul);
            else File.WriteAllText(soul, "complete competing writer");
        };
        if (disappears)
        {
            // An external deletion between Exists and Replace is not contention: fail
            // without deleting anything else, clean the sibling, and recover next time.
            var error = Should.Throw<IOException>(() => fixture.Home.GetAgentDirectory("nexus-trailguide"));
            error.ShouldNotBeOfType<TrailguideRefreshException>();
            File.Exists(soul).ShouldBeFalse();
        }
        else
        {
            fixture.Home.GetAgentDirectory("nexus-trailguide");
            File.ReadAllBytes(soul).ShouldBe(canonical);
        }
        fixture.AssertNoTemps();
        fixture.FileSystem.BeforeMove = null;
        fixture.Home.GetAgentDirectory("nexus-trailguide");
        File.ReadAllBytes(soul).ShouldBe(canonical);
        fixture.AssertNoTemps();
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Environment.CurrentDirectory, "artifacts", "tmp", "trailguide-safety-" + Guid.NewGuid().ToString("N"));
        public string Workspace => Path.Combine(Root, "agents", "nexus-trailguide", "workspace");
        public ObservedFileSystem FileSystem { get; } = new();
        public BotNexusHome Home { get; }
        public Fixture() => Home = new BotNexusHome(FileSystem, Root, Root);
        public void AssertNoTemps() => Directory.EnumerateFiles(Workspace, "*.tmp", SearchOption.AllDirectories).ShouldBeEmpty();
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed class ObservedFileSystem : FileSystem
    {
        private readonly IFile _file;
        public ObservedFileSystem() => _file = new ObservedFile(this);
        public override IFile File => _file;
        public Action<string, string>? BeforeMove { get; set; }
        public bool RejectWrites { get; set; }
        private sealed class ObservedFile(ObservedFileSystem owner) : FileWrapper(owner)
        {
            public override void WriteAllText(string path, string? contents)
            {
                if (owner.RejectWrites) throw new InvalidOperationException("unchanged refresh attempted a write");
                base.WriteAllText(path, contents);
            }
            public override void Replace(string source, string destination, string? destinationBackupFileName)
            {
                owner.BeforeMove?.Invoke(source, destination);
                base.Replace(source, destination, destinationBackupFileName);
            }
            public override void Move(string source, string destination, bool overwrite)
            {
                owner.BeforeMove?.Invoke(source, destination);
                base.Move(source, destination, overwrite);
            }
        }
    }
}
