using System.Text.Json;
using System.Text.RegularExpressions;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Extensions;
using BotNexus.Gateway.Hooks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Tests.Extensions;

/// <summary>
/// Pins the log LEVEL of the routine extension discovery/load/prune banners (#2751).
/// </summary>
/// <remarks>
/// <para>
/// These three banners are emitted once per discovered extension (and once per pruned
/// registration) on every single boot. At Warning they put a permanent ~34-line noise floor on an
/// otherwise clean container start, which destroys the WRN=0 clean-boot property that daily build
/// validation uses as a threshold: with a fixed floor, a genuinely new warning is invisible and
/// warning triage reverts from "is the count non-zero?" to eyeballing 34 lines.
/// </para>
/// <para>
/// The level is therefore a load-bearing behaviour, not a cosmetic detail, and it has silently
/// regressed before - <c>b78ff8a1</c> ("debug: extension loading warnings for discovery
/// diagnostics") raised two of them from Information to Warning as a temporary diagnostic and they
/// were never lowered again. Nothing reddened, because no test named the level. These tests are
/// that missing guard: <see cref="LoadAsync_EmitsDiscoveryAndLoadBanners_AtInformation"/> pins the
/// levels behaviourally from a real load, and
/// <see cref="RoutineExtensionBanners_AreNotEmittedAtWarningOrAbove"/> fences the source so the
/// prune banner - whose emit path needs an un-activatable registration to reach - cannot be raised
/// back to Warning either.
/// </para>
/// </remarks>
public sealed class ExtensionLoadLogLevelTests : IDisposable
{
    /// <summary>
    /// The literal message templates whose level this fence owns, keyed to the acceptance criteria
    /// of #2751. Matching on the template text (not the call site) is deliberate: a future refactor
    /// may move or rename the emitting method, but moving the message keeps the fence attached to
    /// the thing that actually shows up in the boot log.
    /// </summary>
    private static readonly string[] RoutineBannerTemplates =
    [
        // AC1 - discovery banner.
        "': discovered {Count} implementation(s){Details}",
        // AC2 - load banner.
        "Loaded extension '{ExtensionId}' ({Name} v{Version}) with {ServiceCount} service registration(s).",
        // AC3 - prune banner.
        "Pruned extension service registration '{Contract}->{Implementation}'"
    ];

    private readonly string _rootPath;

    public ExtensionLoadLogLevelTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "botnexus-extension-loglevel-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
    }

    /// <summary>
    /// AC1/AC2: a real discover+load emits both banners, and emits them at Information.
    /// </summary>
    /// <remarks>
    /// The banners are asserted to be PRESENT before their level is checked. Without that, the test
    /// would pass vacuously if the messages were renamed or the load silently failed - "no banner
    /// was logged at Warning" is trivially true when no banner was logged at all.
    /// </remarks>
    [Fact]
    public async Task LoadAsync_EmitsDiscoveryAndLoadBanners_AtInformation()
    {
        var recorder = new RecordingLogger();
        var services = new ServiceCollection();
        var loader = new AssemblyLoadContextExtensionLoader(services, new HookDispatcher(), recorder, new FileSystem());

        var extensionDirectory = Path.Combine(_rootPath, "telegram-extension");
        Directory.CreateDirectory(extensionDirectory);

        var telegramAssembly = ResolveTelegramAssemblyPath();
        var copiedAssemblyName = Path.GetFileName(telegramAssembly);
        File.Copy(telegramAssembly, Path.Combine(extensionDirectory, copiedAssemblyName), overwrite: true);

        await File.WriteAllTextAsync(Path.Combine(extensionDirectory, "botnexus-extension.json"), JsonSerializer.Serialize(new ExtensionManifest
        {
            Id = "telegram-extension",
            Name = "Telegram Extension",
            Version = "1.0.0",
            EntryAssembly = copiedAssemblyName,
            ExtensionTypes = ["channel"]
        }));

        var discovered = await loader.DiscoverAsync(_rootPath);
        var result = await loader.LoadAsync(discovered.Single());

        result.Success.ShouldBeTrue();
        services.ShouldContain(d => d.ServiceType == typeof(IChannelAdapter));

        var discoveryBanner = recorder.Records.Where(r => r.Message.Contains("discovered", StringComparison.Ordinal)
            && r.Message.Contains("implementation(s)", StringComparison.Ordinal)).ToList();
        var loadBanner = recorder.Records.Where(r => r.Message.StartsWith("Loaded extension '", StringComparison.Ordinal)).ToList();

        // Non-vacuity: both banners must actually have been emitted by this load.
        discoveryBanner.ShouldNotBeEmpty("The discovery banner was not emitted at all, so this test cannot be pinning its level.");
        loadBanner.ShouldNotBeEmpty("The load banner was not emitted at all, so this test cannot be pinning its level.");

        discoveryBanner.ShouldAllBe(r => r.Level == LogLevel.Information);
        loadBanner.ShouldAllBe(r => r.Level == LogLevel.Information);
    }

    /// <summary>
    /// AC4 (test-level substitute): a successful discover+load of a well-formed extension emits
    /// NOTHING at Warning or above, so N extensions contribute exactly zero to the boot warning
    /// count.
    /// </summary>
    /// <remarks>
    /// AC4 as written asks for a <c>docker logs | grep '[WRN]'</c> count on a real container boot.
    /// That is not reproducible inside the test suite, so this asserts the property the container
    /// count would be measuring - the per-extension warning contribution is zero - at the level
    /// where it can be enforced on every run rather than once by hand.
    /// </remarks>
    [Fact]
    public async Task LoadAsync_OfAWellFormedExtension_ContributesNoWarnings()
    {
        var recorder = new RecordingLogger();
        var services = new ServiceCollection();
        var loader = new AssemblyLoadContextExtensionLoader(services, new HookDispatcher(), recorder, new FileSystem());

        var extensionDirectory = Path.Combine(_rootPath, "telegram-extension");
        Directory.CreateDirectory(extensionDirectory);

        var telegramAssembly = ResolveTelegramAssemblyPath();
        var copiedAssemblyName = Path.GetFileName(telegramAssembly);
        File.Copy(telegramAssembly, Path.Combine(extensionDirectory, copiedAssemblyName), overwrite: true);

        await File.WriteAllTextAsync(Path.Combine(extensionDirectory, "botnexus-extension.json"), JsonSerializer.Serialize(new ExtensionManifest
        {
            Id = "telegram-extension",
            Name = "Telegram Extension",
            Version = "1.0.0",
            EntryAssembly = copiedAssemblyName,
            ExtensionTypes = ["channel"]
        }));

        var discovered = await loader.DiscoverAsync(_rootPath);
        var result = await loader.LoadAsync(discovered.Single());

        result.Success.ShouldBeTrue();

        // Non-vacuity: the loader must have logged SOMETHING, or "no warnings" proves nothing.
        recorder.Records.ShouldNotBeEmpty("The loader logged nothing at all, so a zero warning count is meaningless.");

        var warnings = recorder.Records.Where(r => r.Level >= LogLevel.Warning).ToList();
        warnings.ShouldBeEmpty(
            "Loading a well-formed extension must contribute zero warnings to a clean boot (#2751). " +
            "Offending records:" + Environment.NewLine +
            string.Join(Environment.NewLine, warnings.Select(w => $"[{w.Level}] {w.Message}")));
    }

    /// <summary>
    /// AC3 (and a belt-and-braces fence for AC1/AC2): none of the routine banners is emitted
    /// through <c>LogWarning</c>/<c>LogError</c>/<c>LogCritical</c> in the loader source.
    /// </summary>
    /// <remarks>
    /// The prune banner only fires when an extension registers a type the host container cannot
    /// activate, which no well-formed fixture extension does. Rather than fabricate an
    /// un-activatable extension assembly just to observe one log line, this reads the emit site
    /// directly. It also means a reversion is caught even if someone deletes the behavioural tests
    /// above.
    /// </remarks>
    [Fact]
    public void RoutineExtensionBanners_AreNotEmittedAtWarningOrAbove()
    {
        var loaderSource = Path.Combine(
            FindRepositoryRoot(), "src", "gateway", "BotNexus.Gateway", "Extensions", "AssemblyLoadContextExtensionLoader.cs");

        File.Exists(loaderSource).ShouldBeTrue($"Expected the extension loader source at '{loaderSource}'. If it moved, move this fence with it.");
        var text = File.ReadAllText(loaderSource);

        var violations = new List<string>();
        foreach (var template in RoutineBannerTemplates)
        {
            var index = text.IndexOf(template, StringComparison.Ordinal);

            // Non-vacuity: a template that is no longer present cannot be fenced, and silently
            // matching nothing is exactly how this regressed unnoticed the first time.
            index.ShouldBeGreaterThanOrEqualTo(0,
                $"Message template '{template}' was not found in the loader source. It was renamed or removed - " +
                "update this fence deliberately rather than letting it degrade into asserting nothing.");

            // Walk back to the logger call that owns this template.
            var callIndex = text.LastIndexOf("_logger.Log", index, StringComparison.Ordinal);
            callIndex.ShouldBeGreaterThanOrEqualTo(0, $"Could not find the logger call emitting '{template}'.");

            var call = Regex.Match(text[callIndex..], @"^_logger\.Log(?<level>\w+)").Groups["level"].Value;
            if (call is "Warning" or "Error" or "Critical")
            {
                var line = text[..callIndex].Count(c => c == '\n') + 1;
                violations.Add($"AssemblyLoadContextExtensionLoader.cs:{line} emits '{template}' at {call}");
            }
        }

        violations.ShouldBeEmpty(
            "Extension discovery/load/prune banners are routine startup chatter emitted once per extension on " +
            "every boot. At Warning they impose a fixed warning floor (34 lines on a default boot) that hides " +
            "genuinely new warnings and destroys the WRN=0 clean-boot signal daily validation thresholds on " +
            "(#2751). Log them at Information or Debug." +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    private static string ResolveTelegramAssemblyPath()
    {
        var localCopy = Path.Combine(AppContext.BaseDirectory, "BotNexus.Extensions.Channels.Telegram.dll");
        if (File.Exists(localCopy))
            return localCopy;

        var fallback = Path.Combine(FindRepositoryRoot(), "src", "extensions", "BotNexus.Extensions.Channels.Telegram", "bin", "Debug", "net10.0", "BotNexus.Extensions.Channels.Telegram.dll");
        if (File.Exists(fallback))
            return fallback;

        throw new FileNotFoundException("Unable to locate BotNexus.Extensions.Channels.Telegram.dll for extension log-level tests.");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")))
                return current.FullName;
            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test base directory.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private sealed record LogRecord(LogLevel Level, string Message);

    /// <summary>
    /// Captures level + rendered message for every record. Deliberately not a Moq mock: the whole
    /// point is to observe the level the production code actually passes, and a mock configured per
    /// level would re-encode the expectation in the fixture instead of reading it from the code.
    /// </summary>
    private sealed class RecordingLogger : ILogger<AssemblyLoadContextExtensionLoader>
    {
        private readonly List<LogRecord> _records = [];

        public IReadOnlyList<LogRecord> Records
        {
            get
            {
                lock (_records)
                    return _records.ToList();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_records)
                _records.Add(new LogRecord(logLevel, formatter(state, exception)));
        }
    }
}
