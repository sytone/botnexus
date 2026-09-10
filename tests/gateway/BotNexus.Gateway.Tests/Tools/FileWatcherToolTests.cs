using System.Diagnostics;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Tools;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Tools;

public sealed class FileWatcherToolTests : IDisposable
{
    private readonly List<string> _pathsToDelete = [];

    [Fact]
    public void FileWatcherTool_HasCorrectNameAndLabel()
    {
        var tool = CreateTool();

        tool.Name.ShouldBe("watch_file");
        tool.Label.ShouldBe("Watch File");
    }

    /// <summary>
    /// The one test in this file that drives a REAL <see cref="FileSystemWatcher"/> end to end.
    /// </summary>
    /// <remarks>
    /// Its siblings raise the change through <see cref="FakeFileChangeWatcher"/> so they test this
    /// tool rather than the operating system's event delivery. That would leave the default
    /// <see cref="FileSystemChangeWatcherFactory"/> — the wiring every real caller uses — with no
    /// coverage at all, so exactly one test keeps the real path: a broken <c>Arm()</c>, a wrong
    /// <c>NotifyFilter</c>, or an unsubscribed event still fails here.
    /// <para>
    /// It is the only test whose budget is spent waiting on something outside the process, so it is
    /// also the only one that needs a generous budget rather than a deterministic signal. See
    /// <see cref="WatchBudgetSeconds"/>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task FileWatcherTool_DetectsFileModification()
    {
        var tool = CreateTool();
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "watched.txt");
        await File.WriteAllTextAsync(path, "initial");

        var (watchTask, ready) = StartWatch(tool, new Dictionary<string, object?>
        {
            ["path"] = path,
            ["event"] = "modified",
            ["timeout"] = WatchBudgetSeconds
        });
        await TestAwait.SignaledAsync(ready, "the real FileSystemWatcher to report itself armed");
        await File.WriteAllTextAsync(path, "updated");

        var result = await watchTask;
        ReadText(result).ShouldContain("File modified:");
    }

    [Fact]
    public async Task FileWatcherTool_DetectsFileCreation()
    {
        var watchers = new FakeFileChangeWatcherFactory();
        var tool = CreateTool(watcherFactory: watchers);
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "created.txt");

        var watchTask = ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["path"] = path,
            ["event"] = "created",
            ["timeout"] = WatchBudgetSeconds
        });

        var watcher = await ArmedWatcherAsync(watchers, watchTask);
        watcher.RequestedKinds.ShouldBe([FileChangeKind.Created]);

        // The tool splits the requested path into the directory to watch and the file to filter on;
        // getting that wrong is invisible to a real-filesystem test that watches a whole directory.
        watcher.Directory.ShouldBe(root);
        watcher.FileName.ShouldBe("created.txt");

        watcher.Raise(FileChangeKind.Created);

        var result = await watchTask;
        ReadText(result).ShouldContain("File created:");
    }

    [Fact]
    public async Task FileWatcherTool_DetectsFileDeletion()
    {
        var watchers = new FakeFileChangeWatcherFactory();
        var tool = CreateTool(watcherFactory: watchers);
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "deleted.txt");
        await File.WriteAllTextAsync(path, "delete me");

        var watchTask = ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["path"] = path,
            ["event"] = "deleted",
            ["timeout"] = WatchBudgetSeconds
        });

        var watcher = await ArmedWatcherAsync(watchers, watchTask);
        watcher.RequestedKinds.ShouldBe([FileChangeKind.Deleted]);
        watcher.Raise(FileChangeKind.Deleted);

        var result = await watchTask;
        ReadText(result).ShouldContain("File deleted:");
        watcher.Disposed.ShouldBeTrue("the tool must dispose the watcher it armed");
    }

    /// <summary>
    /// Pins the ordering contract of #2988: the readiness notice must not be emitted until the watcher is
    /// actually raising events. This raises the deletion synchronously ON the callback thread, so the
    /// event is strictly ordered between the notice and whatever the tool does next. With the notice
    /// emitted before <see cref="IFileChangeWatcher.Arm"/> the event is provably unobservable — the fake
    /// drops pre-arm events exactly as <c>EnableRaisingEvents = false</c> does — and the watch always
    /// times out; the assertion therefore fails 100% of the time against the unfixed code rather than
    /// merely narrowing a window.
    /// </summary>
    [Fact]
    public async Task FileWatcherTool_ReadinessNotice_IsEmittedOnlyAfterWatcherIsArmed()
    {
        var watchers = new FakeFileChangeWatcherFactory();
        var tool = CreateTool(watcherFactory: watchers);
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "armed.txt");
        await File.WriteAllTextAsync(path, "delete me");

        var notified = false;

        var result = await ExecuteAsync(
            tool,
            new Dictionary<string, object?>
            {
                ["path"] = path,
                ["event"] = "deleted",
                ["timeout"] = WatchBudgetSeconds
            },
            CancellationToken.None,
            update =>
            {
                var text = update.Content
                    .FirstOrDefault(c => c.Type == AgentToolContentType.Text)?.Value;

                if (notified || text is null || !text.Contains("Watching '", StringComparison.Ordinal))
                    return;

                // Acting on the notice the instant it arrives is precisely what a real caller does.
                notified = true;
                watchers.Watcher.ShouldNotBeNull("the notice must not precede the watcher's creation");
                watchers.Watcher!.Raise(FileChangeKind.Deleted);
            });

        notified.ShouldBeTrue("the tool must emit a readiness notice");
        watchers.Watcher!.RaiseCount.ShouldBe(
            1,
            "the change raised on the notice was dropped, so the notice arrived before the watcher was armed");
        ReadText(result).ShouldContain("File deleted:");
    }

    /// <summary>
    /// The timeout IS the assertion here, so — unlike <see cref="WatchBudgetSeconds"/> — a short
    /// budget is correct: it is spent on every passing run, and load can only make the tool MORE
    /// likely to report a timeout, never less.
    /// </summary>
    [Fact]
    public async Task FileWatcherTool_TimesOut()
    {
        var tool = CreateTool(maxTimeoutSeconds: 5);
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "timeout.txt");
        await File.WriteAllTextAsync(path, "unchanged");

        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["path"] = path,
            ["event"] = "modified",
            ["timeout"] = 2
        });

        ReadText(result).ShouldContain("Timeout after 2 seconds");
    }

    [Fact]
    public async Task FileWatcherTool_CancellationReturnsInfo()
    {
        var tool = CreateTool();
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "cancelled.txt");
        await File.WriteAllTextAsync(path, "unchanged");

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        var result = await ExecuteAsync(
            tool,
            new Dictionary<string, object?>
            {
                ["path"] = path,
                ["event"] = "modified",
                ["timeout"] = 10
            },
            cts.Token);

        ReadText(result).ToLowerInvariant().ShouldContain("cancel");
    }

    [Fact]
    public async Task FileWatcherTool_RequiresPath()
    {
        var tool = CreateTool();

        Func<Task> act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>());

        await act.ShouldThrowAsync<ArgumentException>();
    }

    /// <summary>
    /// Proves the 999s request is clamped to the configured 2s maximum.
    /// </summary>
    /// <remarks>
    /// This assertion used to bound <see cref="Stopwatch.ElapsedMilliseconds"/> above by 4000ms for a
    /// 2-second clamp. That measured runner scheduling and <see cref="FileSystemWatcher"/> teardown, not
    /// the clamp, and a contended hosted runner measured 7771ms — a red gate on a diff that cannot reach
    /// this tool (#3333). The upper bound is now expressed two ways that a loaded machine cannot breach
    /// but an unclamped tool cannot satisfy:
    /// <list type="number">
    /// <item>The tool's own readiness notice reports the EFFECTIVE timeout it is about to wait for, so
    /// <c>timeout: 2s</c> observes the clamped value directly with no wall clock involved. A mutant that
    /// honours the requested 999s announces <c>timeout: 999s</c> and fails here within milliseconds.</item>
    /// <item>A generous <see cref="ClampWatchdog"/> ceiling replaces the tight 4000ms bound. It is ~15x
    /// the clamped wait, so scheduling noise cannot trip it, yet a mutant that waits 999s never completes
    /// and fails deterministically instead of hanging the suite until the test host is killed.</item>
    /// </list>
    /// The lower bound is retained: it is the non-vacuity guard proving the tool actually waited out the
    /// clamped timeout rather than returning immediately, and no amount of load can make a wait too fast.
    /// </remarks>
    [Fact]
    public async Task FileWatcherTool_ClampsTimeout()
    {
        var tool = CreateTool(maxTimeoutSeconds: 2);
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "clamped.txt");
        await File.WriteAllTextAsync(path, "unchanged");

        string? readinessNotice = null;

        var stopwatch = Stopwatch.StartNew();
        var watch = ExecuteAsync(
            tool,
            new Dictionary<string, object?>
            {
                ["path"] = path,
                ["event"] = "modified",
                ["timeout"] = 999
            },
            CancellationToken.None,
            update =>
            {
                var text = update.Content
                    .FirstOrDefault(c => c.Type == AgentToolContentType.Text)?.Value;

                if (text is not null && text.Contains("Watching '", StringComparison.Ordinal))
                    readinessNotice = text;
            });

        var completed = await Task.WhenAny(watch, Task.Delay(ClampWatchdog));
        completed.ShouldBeSameAs(
            watch,
            $"The watch did not return within {ClampWatchdog.TotalSeconds:0}s, so the 999-second request " +
            "was not clamped to the configured 2-second maximum.");

        var result = await watch;

        // The effective timeout the tool armed itself with — the clamp, observed directly.
        readinessNotice.ShouldNotBeNull();
        readinessNotice.ShouldContain("timeout: 2s");

        stopwatch.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(1800);
        ReadText(result).ShouldContain("Timeout after 2 seconds");
    }

    /// <summary>
    /// Upper bound for <see cref="FileWatcherTool_ClampsTimeout"/>. Generous enough that runner
    /// contention cannot breach it (the observed worst case for a 2s clamp was 7.8s), tight enough that
    /// an unclamped 999-second wait fails the test rather than stalling the run.
    /// </summary>
    private static readonly TimeSpan ClampWatchdog = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task FileWatcherTool_ReportsElapsedTime()
    {
        var watchers = new FakeFileChangeWatcherFactory();
        var tool = CreateTool(watcherFactory: watchers);
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "elapsed.txt");
        await File.WriteAllTextAsync(path, "initial");

        var watchTask = ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["path"] = path,
            ["event"] = "modified",
            ["timeout"] = WatchBudgetSeconds
        });

        var watcher = await ArmedWatcherAsync(watchers, watchTask);
        watcher.Raise(FileChangeKind.Modified);

        var result = await watchTask;
        ReadText(result).ShouldMatch(@"after \d+ seconds");
    }

    /// <summary>
    /// Five changes inside one debounce window must coalesce into a single result.
    /// </summary>
    /// <remarks>
    /// The events were previously produced by writing the file five times with a 40ms sleep between
    /// each. The sleep was never what made the changes "rapid" — the 500ms debounce window is — so
    /// raising the events back to back exercises the same coalescing path (timer replacement under
    /// <see cref="Interlocked"/>, which is where a debounce bug would actually live) without a clock.
    /// </remarks>
    [Fact]
    public async Task FileWatcherTool_DebouncesProdRapidChanges()
    {
        var watchers = new FakeFileChangeWatcherFactory();
        var tool = CreateTool(watcherFactory: watchers);
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "debounced.txt");
        await File.WriteAllTextAsync(path, "start");

        var watchTask = ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["path"] = path,
            ["event"] = "modified",
            ["timeout"] = WatchBudgetSeconds
        });

        var watcher = await ArmedWatcherAsync(watchers, watchTask);
        for (var i = 0; i < 5; i++)
            watcher.Raise(FileChangeKind.Modified);

        var result = await watchTask;
        ReadText(result).ShouldContain("File modified:");
        watcher.RaiseCount.ShouldBe(5, "the tool must have seen every change, not just the last");
    }

    public void Dispose()
    {
        foreach (var path in _pathsToDelete.Where(Directory.Exists))
            Directory.Delete(path, recursive: true);
    }

    private string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "botnexus-file-watcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _pathsToDelete.Add(path);
        return path;
    }

    private static async Task<AgentToolResult> ExecuteAsync(
        IAgentTool tool,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var prepared = await tool.PrepareArgumentsAsync(args, cancellationToken);
        return await tool.ExecuteAsync("call-watch-file-test", prepared, cancellationToken, onUpdate);
    }

    /// <summary>
    /// Starts a watch against the REAL <see cref="FileSystemWatcher"/> and returns once the tool has
    /// reported that it is armed, so the caller can mutate the file knowing the event will be
    /// observed (#2988). Used only by <see cref="FileWatcherTool_DetectsFileModification"/>; every
    /// other test drives <see cref="FakeFileChangeWatcher"/> instead.
    /// </summary>
    /// <remarks>
    /// These tests previously slept for a fixed second between starting the watch and touching the file.
    /// That is a guess, not a synchronisation primitive: the watcher is armed inside the background task,
    /// so on a loaded CI runner the mutation could land BEFORE <c>EnableRaisingEvents = true</c>, the
    /// event was never raised, and the test then sat out its full timeout and failed with
    /// "Timeout after 5 seconds - no change detected". The tool already announces readiness through its
    /// onUpdate callback, which is a real happens-before edge -- use it instead of sleeping.
    /// </remarks>
    private static (Task<AgentToolResult> Watch, Task Ready) StartWatch(
        IAgentTool tool,
        IReadOnlyDictionary<string, object?> args)
    {
        var armed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var watch = ExecuteAsync(
            tool,
            args,
            CancellationToken.None,
            update =>
            {
                // The tool emits exactly one "Watching '<path>' for <event> event..." notice, and only
                // once EnableRaisingEvents is true. Anything else is ignored.
                var text = update.Content
                    .FirstOrDefault(c => c.Type == AgentToolContentType.Text)?.Value;

                if (text is not null && text.Contains("Watching '", StringComparison.Ordinal))
                    armed.TrySetResult();
            });

        // If the watch faults before ever arming, surface that instead of hanging on the gate.
        _ = watch.ContinueWith(
            t => armed.TrySetException(t.Exception!.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return (watch, armed.Task);
    }

    private static IAgentTool CreateTool(
        int? maxTimeoutSeconds = null,
        int? defaultTimeoutSeconds = null,
        int? debounceMilliseconds = null,
        IFileChangeWatcherFactory? watcherFactory = null)
        => new FileWatcherTool(
            Options.Create(new FileWatcherToolOptions
            {
                MaxTimeoutSeconds = maxTimeoutSeconds ?? 1800,
                DefaultTimeoutSeconds = defaultTimeoutSeconds ?? 300,
                DebounceMilliseconds = debounceMilliseconds ?? 500
            }),
            pathValidator: null,
            watcherFactory: watcherFactory);

    /// <summary>
    /// The tool-side <c>timeout</c> argument for a watch whose change is raised by the test itself.
    /// </summary>
    /// <remarks>
    /// This is not a budget the test spends: the event is guaranteed, so the watch returns as soon as
    /// the debounce window closes. It exists only so a broken tool fails instead of hanging the run,
    /// which means it is never reached on the passing path and there is nothing to buy by keeping it
    /// tight. The five seconds it replaces is precisely what took an unrelated CLI PR red (#103) —
    /// a literal second-budget passed as a tool argument is the same wall-clock deadline as
    /// <c>WaitAsync(TimeSpan.FromSeconds(5))</c>, just spelled where no fence can see it.
    /// </remarks>
    private const int WatchBudgetSeconds = 30;

    /// <summary>
    /// Waits for the tool to arm its watcher and hands back the fake, so the caller can raise a change
    /// knowing it will be observed. Fails fast with the tool's own message if the watch returned
    /// before arming — otherwise a rejected path or a missing file reads as a mysterious hang.
    /// </summary>
    private static async Task<FakeFileChangeWatcher> ArmedWatcherAsync(
        FakeFileChangeWatcherFactory watchers,
        Task<AgentToolResult> watch)
    {
        var finished = await TestAwait.SignaledAsync<Task>(
            Task.WhenAny(watchers.Armed, watch),
            "the tool to arm its file watcher");

        if (ReferenceEquals(finished, watch))
        {
            throw new InvalidOperationException(
                $"The watch returned before arming a watcher: {ReadText(await watch)}");
        }

        return watchers.Watcher.ShouldNotBeNull();
    }

    /// <summary>Hands each execution a watcher the test can raise changes on directly.</summary>
    private sealed class FakeFileChangeWatcherFactory : IFileChangeWatcherFactory
    {
        private readonly TaskCompletionSource _armed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The watcher created by the most recent execution, if any.</summary>
        public FakeFileChangeWatcher? Watcher { get; private set; }

        /// <summary>Completes once the tool has armed its watcher.</summary>
        public Task Armed => _armed.Task;

        /// <inheritdoc />
        public IFileChangeWatcher Create(string directory, string fileName, IReadOnlyCollection<FileChangeKind> kinds)
            => Watcher = new FakeFileChangeWatcher(directory, fileName, kinds, () => _armed.TrySetResult());
    }

    /// <summary>
    /// A watcher whose changes the test raises, standing in for the operating system.
    /// </summary>
    /// <remarks>
    /// It drops changes raised before <see cref="Arm"/>, exactly as a <see cref="FileSystemWatcher"/>
    /// with <c>EnableRaisingEvents = false</c> does. That is what keeps
    /// <see cref="FileWatcherTool_ReadinessNotice_IsEmittedOnlyAfterWatcherIsArmed"/> a real assertion
    /// rather than a race the fake happens to win.
    /// </remarks>
    private sealed class FakeFileChangeWatcher(
        string directory,
        string fileName,
        IReadOnlyCollection<FileChangeKind> kinds,
        Action onArmed) : IFileChangeWatcher
    {
        private int _raiseCount;
        private volatile bool _armed;

        /// <inheritdoc />
        public event Action<FileChangeKind>? Changed;

        /// <summary>The directory the tool asked to watch.</summary>
        public string Directory { get; } = directory;

        /// <summary>The file name the tool asked to watch.</summary>
        public string FileName { get; } = fileName;

        /// <summary>The change kinds the tool subscribed to.</summary>
        public IReadOnlyCollection<FileChangeKind> RequestedKinds { get; } = kinds;

        /// <summary>Whether the tool disposed the watcher when the execution finished.</summary>
        public bool Disposed { get; private set; }

        /// <summary>How many changes were delivered, i.e. raised after arming.</summary>
        public int RaiseCount => Volatile.Read(ref _raiseCount);

        /// <inheritdoc />
        public void Arm()
        {
            _armed = true;
            onArmed();
        }

        /// <summary>Delivers a change, or drops it if the watcher has not been armed yet.</summary>
        public void Raise(FileChangeKind kind)
        {
            if (!_armed)
                return;

            Interlocked.Increment(ref _raiseCount);
            Changed?.Invoke(kind);
        }

        /// <inheritdoc />
        public void Dispose() => Disposed = true;
    }

    private static string ReadText(AgentToolResult result)
        => result.Content.Single(c => c.Type == AgentToolContentType.Text).Value;
}
