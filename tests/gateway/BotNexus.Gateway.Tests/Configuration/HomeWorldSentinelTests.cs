using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using BotNexus.Domain.World;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Behaviour pins for the home-root world sentinel (#2836).
/// </summary>
/// <remarks>
/// <para><b>Why the home root and not each store.</b> The SQLite guard (#2833) hangs off the single
/// place a connection is opened. File-backed state has no such chokepoint - sessions, memory and
/// workspaces each resolve a directory and write into it. The one value they all derive from is the
/// home root, so that is where the identity is asserted: one check, not one per store.</para>
/// <para><b>Why the assertions look at disk.</b> Clause 5 of the issue is explicit that a refusal
/// must be proven by the <i>absence of files</i> in the foreign home, not by the resolver's return
/// value. A resolver that throws after having already scaffolded directories would satisfy a
/// return-value assertion and still have written into another world's data.</para>
/// </remarks>
public sealed class HomeWorldSentinelTests
{
    private const string WorldA = "11111111-1111-1111-1111-111111111111";
    private const string WorldB = "22222222-2222-2222-2222-222222222222";

    // Built from the platform temp root rather than a drive-letter literal: the validation gate runs
    // on Linux, where "C:\worlds\home" is a relative path and MockFileSystem resolves it somewhere
    // else entirely.
    private static readonly string HomePath = Path.Combine(Path.GetTempPath(), "botnexus-2836-home");

    /// <summary>AC1: a newly created home carries a sentinel naming the resolved world.</summary>
    [Fact]
    public void NewHome_IsStampedWithTheResolvedWorldId()
    {
        var fileSystem = new MockFileSystem();

        var home = new BotNexusHome(fileSystem, HomePath, dataPath: null, worldId: WorldA);
        home.Initialize();

        var sentinelPath = Path.Combine(HomePath, HomeWorldSentinel.FileName);
        fileSystem.File.Exists(sentinelPath).ShouldBeTrue(
            "a home created by a world-aware process must declare which world it belongs to, or the " +
            "next process has nothing to disagree with.");

        var sentinel = HomeWorldSentinel.Read(fileSystem, HomePath);
        sentinel.ShouldNotBeNull();
        sentinel!.WorldId.ShouldBe(WorldA);
        sentinel.CreatedAt.ShouldNotBeNullOrWhiteSpace();
        sentinel.CreatedByVersion.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// AC2: a sentinel naming a different world is fatal, and the message names both IDs and the path.
    /// </summary>
    [Fact]
    public void ForeignSentinel_ThrowsNamingBothWorldsAndThePath()
    {
        var fileSystem = new MockFileSystem();
        StampForeignHome(fileSystem, WorldB);

        var exception = Should.Throw<HomeWorldIdentityMismatchException>(
            () => new BotNexusHome(fileSystem, HomePath, dataPath: null, worldId: WorldA));

        exception.ExpectedWorldId.ShouldBe(WorldA);
        exception.ActualWorldId.ShouldBe(WorldB);
        exception.HomePath.ShouldBe(Path.GetFullPath(HomePath));

        exception.Message.ShouldContain(WorldA);
        exception.Message.ShouldContain(WorldB);
        exception.Message.ShouldContain(HomePath);
    }

    /// <summary>
    /// AC5: the refusal is proven on disk. A world-A process pointed at world B's home writes
    /// nothing at all into it - not a directory, not a scaffold file, not a replacement sentinel.
    /// </summary>
    [Fact]
    public void ForeignHome_ReceivesNoWrites()
    {
        var fileSystem = new MockFileSystem();
        StampForeignHome(fileSystem, WorldB);
        var before = SnapshotHome(fileSystem);

        Should.Throw<HomeWorldIdentityMismatchException>(
            () => new BotNexusHome(fileSystem, HomePath, dataPath: null, worldId: WorldA));

        var after = SnapshotHome(fileSystem);
        after.ShouldBe(before,
            "refusing to adopt a foreign home is only meaningful if nothing was written into it. " +
            "A guard that throws after scaffolding has already corrupted the other world.");

        // And specifically: the foreign sentinel is intact, not overwritten with ours.
        HomeWorldSentinel.Read(fileSystem, HomePath)!.WorldId.ShouldBe(WorldB);
    }

    /// <summary>
    /// AC3: a populated home with no sentinel is adopted, stamped, and warned about exactly once.
    /// </summary>
    [Fact]
    public void PopulatedHomeWithoutSentinel_IsAdoptedStampedAndWarnedOnce()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(Path.Combine(HomePath, "config.json"), new MockFileData("{}"));
        var logger = new CountingLogger();

        var home = new BotNexusHome(fileSystem, HomePath, dataPath: null, worldId: WorldA, logger: logger);
        home.Initialize();

        HomeWorldSentinel.Read(fileSystem, HomePath)!.WorldId.ShouldBe(WorldA);

        logger.Warnings.Count.ShouldBe(1,
            "adoption is a one-time event per home; repeating the warning on every resolve trains " +
            "operators to ignore the one case where it means their data is in the wrong place.");
        logger.Warnings[0].ShouldContain(HomePath);
    }

    /// <summary>
    /// AC3 complement: an empty directory is a fresh home, not an adoption - it stamps silently.
    /// </summary>
    [Fact]
    public void EmptyHome_IsStampedWithoutAWarning()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(HomePath);
        var logger = new CountingLogger();

        var home = new BotNexusHome(fileSystem, HomePath, dataPath: null, worldId: WorldA, logger: logger);
        home.Initialize();

        HomeWorldSentinel.Read(fileSystem, HomePath)!.WorldId.ShouldBe(WorldA);
        logger.Warnings.ShouldBeEmpty(
            "an empty directory has no data that could belong to another world; warning here would " +
            "make the adoption warning worthless by drowning it in noise.");
    }

    /// <summary>
    /// A matching sentinel is the normal case: it proceeds, and it does not rewrite the file
    /// (a rewrite would churn <c>created_at</c> and destroy the forensic value of the stamp).
    /// </summary>
    [Fact]
    public void MatchingSentinel_ProceedsAndPreservesTheOriginalStamp()
    {
        var fileSystem = new MockFileSystem();
        new BotNexusHome(fileSystem, HomePath, dataPath: null, worldId: WorldA).Initialize();
        var original = fileSystem.File.ReadAllText(Path.Combine(HomePath, HomeWorldSentinel.FileName));

        var reopened = new BotNexusHome(fileSystem, HomePath, dataPath: null, worldId: WorldA);
        reopened.Initialize();

        fileSystem.File.ReadAllText(Path.Combine(HomePath, HomeWorldSentinel.FileName))
            .ShouldBe(original,
                "re-stamping an already-matching home would reset created_at, erasing the record of " +
                "when the home was actually created.");
    }

    /// <summary>
    /// A malformed or truncated sentinel is an adoption, not a mismatch: there is no competing
    /// identity to disagree with. Mirrors the SQLite guard's handling of a meta table with no
    /// <c>world_id</c> row (#2833).
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"world_id\":\"\"}")]
    public void UnreadableSentinel_IsAdoptedRatherThanRefused(string contents)
    {
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(Path.Combine(HomePath, HomeWorldSentinel.FileName), new MockFileData(contents));

        var home = new BotNexusHome(fileSystem, HomePath, dataPath: null, worldId: WorldA);
        home.Initialize();

        HomeWorldSentinel.Read(fileSystem, HomePath)!.WorldId.ShouldBe(WorldA);
    }

    /// <summary>
    /// Concurrent first-creation: many processes/threads racing to stamp the same fresh home must
    /// end with one coherent sentinel and no exception. The losers of the race see the winner's
    /// stamp, which matches their own world, so the outcome is a match rather than a mismatch.
    /// </summary>
    [Fact]
    public void ConcurrentCreation_ConvergesOnASingleSentinel()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.AddDirectory(HomePath);

        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        Parallel.For(0, 16, _ =>
        {
            try
            {
                new BotNexusHome(fileSystem, HomePath, dataPath: null, worldId: WorldA).Initialize();
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        });

        failures.ShouldBeEmpty(
            "concurrent first-start is ordinary (gateway + CLI + cron), and a stamping race must not " +
            "surface as a startup failure: " + string.Join(" | ", failures.Select(f => f.Message)));
        HomeWorldSentinel.Read(fileSystem, HomePath)!.WorldId.ShouldBe(WorldA);
    }

    /// <summary>
    /// The guard is opt-in. A host that has not resolved a world ID keeps working exactly as before -
    /// same posture as <c>SqliteStoreIdentityGuard</c> with no identity configured.
    /// </summary>
    [Fact]
    public void WithoutAWorldId_TheGuardIsInert()
    {
        var fileSystem = new MockFileSystem();
        StampForeignHome(fileSystem, WorldB);

        var home = new BotNexusHome(fileSystem, HomePath);
        home.RootPath.ShouldBe(Path.GetFullPath(HomePath));
        HomeWorldSentinel.Read(fileSystem, HomePath)!.WorldId.ShouldBe(WorldB);
    }

    /// <summary>
    /// AC6 (non-vacuity): the mismatch is produced by comparing the two IDs, so a sentinel whose ID
    /// differs only in case is NOT a mismatch, while any genuinely different ID is. If the comparison
    /// were removed, this test's mismatch half fails by name alongside clauses 2 and 5.
    /// </summary>
    [Fact]
    public void SentinelComparison_IsCaseInsensitiveButOtherwiseExact()
    {
        var upper = new MockFileSystem();
        StampForeignHome(upper, WorldA.ToUpperInvariant());
        Should.NotThrow(() => new BotNexusHome(upper, HomePath, dataPath: null, worldId: WorldA));

        var different = new MockFileSystem();
        StampForeignHome(different, WorldA[..^1] + "2");
        Should.Throw<HomeWorldIdentityMismatchException>(
            () => new BotNexusHome(different, HomePath, dataPath: null, worldId: WorldA));
    }

    private static void StampForeignHome(MockFileSystem fileSystem, string worldId)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["world_id"] = worldId,
            ["created_at"] = "2020-01-01T00:00:00.0000000+00:00",
            ["created_by_version"] = "0.0.0.0"
        });
        fileSystem.AddFile(Path.Combine(HomePath, HomeWorldSentinel.FileName), new MockFileData(payload));
        fileSystem.AddFile(Path.Combine(HomePath, "config.json"), new MockFileData("{}"));
        fileSystem.AddFile(Path.Combine(HomePath, "agents", "other", "workspace", "SOUL.md"), new MockFileData("x"));
    }

    private static IReadOnlyList<string> SnapshotHome(MockFileSystem fileSystem)
        => fileSystem.AllFiles
            .Select(path => path + "\u0000" + fileSystem.File.ReadAllText(path))
            .Concat(fileSystem.AllDirectories.Select(d => "dir:" + d))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

    private sealed class CountingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
