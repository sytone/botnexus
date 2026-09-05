using System.IO.Abstractions.TestingHelpers;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Agents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Acceptance coverage for issue #3569 AC4: the backstop age-based sweep must skip any workspace
/// whose sub-agent is still live, and must log each removal it performs.
/// <para>
/// The pre-#3569 sweep decided eligibility from last-write time alone. A live sub-agent that spends
/// minutes on a provider call writes nothing to its workspace, so its directory aged past the TTL
/// while the run was healthy and the sweep deleted the working directory out from under it. These
/// tests pin the liveness consultation and the fail-safe direction, so no future change can
/// reintroduce a purely time-derived deletion decision.
/// </para>
/// </summary>
public sealed class SubAgentWorkspaceSweeperLivenessTests
{
    private static readonly DateTime NowUtc = new(2025, 1, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private static readonly TimeSpan Grace = TimeSpan.FromHours(1);

    private readonly MockFileSystem _fileSystem = new();
    private readonly string _agentsRoot;

    public SubAgentWorkspaceSweeperLivenessTests()
    {
        _agentsRoot = _fileSystem.Path.Combine(
            _fileSystem.Path.GetTempPath(),
            "botnexus-3569-tests",
            "agents");
        _fileSystem.Directory.CreateDirectory(_agentsRoot);
    }

    /// <summary>Creates a sub-agent husk aged well beyond the retention TTL.</summary>
    private string AddExpiredSubAgentDir(string name)
    {
        var dir = _fileSystem.Path.Combine(_agentsRoot, name);
        _fileSystem.Directory.CreateDirectory(dir);
        _fileSystem.File.WriteAllText(_fileSystem.Path.Combine(dir, "scratch.txt"), "data");
        _fileSystem.Directory.SetLastWriteTimeUtc(dir, NowUtc - TimeSpan.FromDays(3));
        return dir;
    }

    private sealed class StubProbe(Func<string, bool> isLive) : ISubAgentWorkspaceLivenessProbe
    {
        public List<string> Queried { get; } = [];

        public bool IsLive(string workspaceDirectoryName)
        {
            Queried.Add(workspaceDirectoryName);
            return isLive(workspaceDirectoryName);
        }
    }

    /// <summary>
    /// AC4, the core regression. A directory aged far past the TTL whose sub-agent is STILL LIVE
    /// must survive. This is exactly the shape that killed 37 sub-agents: healthy run, idle
    /// workspace, expired timestamp.
    /// </summary>
    [Fact]
    public void Sweep_RetainsExpiredWorkspace_WhenSubAgentIsStillLive()
    {
        var dir = AddExpiredSubAgentDir("tinker--subagent--warden--7be71aa4");
        var probe = new StubProbe(_ => true);
        var sweeper = new SubAgentWorkspaceSweeper(_fileSystem, NullLogger.Instance, probe);

        var result = sweeper.Sweep(_agentsRoot, Retention, Grace, NowUtc);

        result.Removed.ShouldBe(0);
        result.SkippedLive.ShouldBe(1);
        _fileSystem.Directory.Exists(dir).ShouldBeTrue();
        probe.Queried.ShouldContain("tinker--subagent--warden--7be71aa4");
    }

    /// <summary>
    /// The complementary half: a genuinely dead sub-agent's expired workspace is still reclaimed,
    /// so the fix is a liveness gate and not a disabling of the sweep.
    /// </summary>
    [Fact]
    public void Sweep_RemovesExpiredWorkspace_WhenSubAgentIsNotLive()
    {
        var dir = AddExpiredSubAgentDir("tinker--subagent--warden--dead0001");
        var sweeper = new SubAgentWorkspaceSweeper(
            _fileSystem,
            NullLogger.Instance,
            new StubProbe(_ => false));

        var result = sweeper.Sweep(_agentsRoot, Retention, Grace, NowUtc);

        result.Removed.ShouldBe(1);
        result.SkippedLive.ShouldBe(0);
        _fileSystem.Directory.Exists(dir).ShouldBeFalse();
    }

    /// <summary>
    /// Sad path / fail-safe direction. A probe that throws must be read as "assume live", never as
    /// "assume dead": retaining a dead workspace costs disk, deleting a live one destroys a run.
    /// </summary>
    [Fact]
    public void Sweep_RetainsWorkspace_WhenLivenessProbeThrows()
    {
        var dir = AddExpiredSubAgentDir("nova--subagent--coder--ca861848");
        var sweeper = new SubAgentWorkspaceSweeper(
            _fileSystem,
            NullLogger.Instance,
            new StubProbe(_ => throw new InvalidOperationException("registry unavailable")));

        var result = sweeper.Sweep(_agentsRoot, Retention, Grace, NowUtc);

        result.Removed.ShouldBe(0);
        result.SkippedLive.ShouldBe(1);
        _fileSystem.Directory.Exists(dir).ShouldBeTrue();
    }

    /// <summary>
    /// Live and dead husks in one pass: only the dead one goes. Guards against an implementation
    /// that short-circuits the whole pass on the first live directory it meets.
    /// </summary>
    [Fact]
    public void Sweep_RemovesOnlyTheDeadHusk_WhenLiveAndDeadCoexist()
    {
        var live = AddExpiredSubAgentDir("quill--subagent--general--live0001");
        var dead = AddExpiredSubAgentDir("quill--subagent--general--dead0002");
        var sweeper = new SubAgentWorkspaceSweeper(
            _fileSystem,
            NullLogger.Instance,
            new StubProbe(name => name.Contains("live", StringComparison.Ordinal)));

        var result = sweeper.Sweep(_agentsRoot, Retention, Grace, NowUtc);

        result.Removed.ShouldBe(1);
        result.SkippedLive.ShouldBe(1);
        _fileSystem.Directory.Exists(live).ShouldBeTrue();
        _fileSystem.Directory.Exists(dead).ShouldBeFalse();
    }

    /// <summary>
    /// AC4's second clause: each removal is logged individually at Information, naming the
    /// directory. A silent bulk count made the original defect invisible in the logs - the 37 lost
    /// sub-agents left no record of what removed them.
    /// </summary>
    [Fact]
    public void Sweep_LogsEachRemoval_NamingTheDirectory()
    {
        AddExpiredSubAgentDir("aurum--subagent--reviewer--logme01");
        var logger = new CapturingLogger();
        var sweeper = new SubAgentWorkspaceSweeper(_fileSystem, logger, new StubProbe(_ => false));

        sweeper.Sweep(_agentsRoot, Retention, Grace, NowUtc);

        logger.Entries.ShouldContain(entry =>
            entry.Level == LogLevel.Information
            && entry.Message.Contains("aurum--subagent--reviewer--logme01", StringComparison.Ordinal));
    }

    /// <summary>
    /// #3670 AC4: the backstop line must carry the SAME audit prefix the lifecycle route emits, so
    /// an operator investigating a vanished workspace finds both reclamation routes with one query
    /// rather than needing to know two separately-worded messages exist.
    /// </summary>
    [Fact]
    public void Sweep_RemovalAudit_UsesTheSharedReclamationPrefix()
    {
        AddExpiredSubAgentDir("aurum--subagent--reviewer--shared1");
        var logger = new CapturingLogger();
        var sweeper = new SubAgentWorkspaceSweeper(_fileSystem, logger, new StubProbe(_ => false));

        sweeper.Sweep(_agentsRoot, Retention, Grace, NowUtc);

        logger.Entries.ShouldContain(entry =>
            entry.Level == LogLevel.Information
            && entry.Message.StartsWith(
                SubAgentWorkspaceReclamationAudit.MessagePrefix,
                StringComparison.Ordinal));
    }

    /// <summary>
    /// The two routes must remain distinguishable within that shared prefix. A single query finds
    /// both, but the operator still has to know WHICH mechanism removed the workspace: a backstop
    /// removal means the lifecycle path failed to fire, which is itself the signal worth acting on.
    /// </summary>
    [Fact]
    public void Sweep_RemovalAudit_NamesTheBackstopRoute()
    {
        AddExpiredSubAgentDir("aurum--subagent--reviewer--route01");
        var logger = new CapturingLogger();
        var sweeper = new SubAgentWorkspaceSweeper(_fileSystem, logger, new StubProbe(_ => false));

        sweeper.Sweep(_agentsRoot, Retention, Grace, NowUtc);

        logger.Entries.ShouldContain(entry =>
            entry.Level == LogLevel.Information
            && entry.Message.Contains("route: backstop-sweep", StringComparison.Ordinal));
    }

    /// <summary>
    /// The probe is a required collaborator. Construction must fail loudly rather than allow a
    /// probe-less sweeper to exist at all: an optional probe would let a misconfigured DI graph
    /// silently revert to the time-only deletion that caused #3569, undetected until it destroyed
    /// another live run. Refusing at construction makes that misconfiguration a startup failure.
    /// </summary>
    [Fact]
    public void Constructor_WithoutLivenessProbe_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => new SubAgentWorkspaceSweeper(_fileSystem, NullLogger.Instance, livenessProbe: null!));
    }

    /// <summary>
    /// The grace window still short-circuits ahead of the probe, so a recently-touched directory is
    /// never even asked about. Preserves the #2237 behaviour the liveness gate is layered on top of.
    /// </summary>
    [Fact]
    public void Sweep_DoesNotConsultProbe_ForDirectoryInsideGraceWindow()
    {
        var dir = _fileSystem.Path.Combine(_agentsRoot, "farnsworth--subagent--coder--fresh001");
        _fileSystem.Directory.CreateDirectory(dir);
        _fileSystem.Directory.SetLastWriteTimeUtc(dir, NowUtc - TimeSpan.FromMinutes(5));

        var probe = new StubProbe(_ => false);
        var sweeper = new SubAgentWorkspaceSweeper(_fileSystem, NullLogger.Instance, probe);

        var result = sweeper.Sweep(_agentsRoot, Retention, Grace, NowUtc);

        result.Removed.ShouldBe(0);
        result.SkippedRecent.ShouldBe(1);
        probe.Queried.ShouldBeEmpty();
        _fileSystem.Directory.Exists(dir).ShouldBeTrue();
    }

    /// <summary>Minimal capturing logger so the removal-logging assertion reads real log records.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
