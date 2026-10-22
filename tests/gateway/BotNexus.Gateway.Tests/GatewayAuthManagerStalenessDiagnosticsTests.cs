using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Reflection;
using System.Text.Json;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace BotNexus.Gateway.Tests;

public sealed class GatewayAuthManagerStalenessDiagnosticsTests
{
    private const string SafeDiagnostic = "Failed to stat auth file for staleness detection.";
    private const string PathSentinel = "PRIVATE_AUTH_PATH_4815";
    private const string ExceptionSentinel = "PRIVATE_STAT_EXCEPTION_4815";
    private const string TokenSentinel = "PRIVATE_AUTH_TOKEN_4815";

    [Theory]
    [InlineData(false, "message")]
    [InlineData(true, "message")]
    [InlineData(false, "state")]
    [InlineData(true, "state")]
    [InlineData(false, "exception")]
    [InlineData(true, "exception")]
    public async Task GetApiKeyAsync_WhenCandidateStatFails_LogsOnlySafeDiagnostic(bool legacy, string surface)
    {
        var fixture = new StatFailureFixture(legacy);
        fixture.WriteCredentials(TokenSentinel, "legacy-token");

        (await fixture.Manager.GetApiKeyAsync("openai")).ShouldBe(TokenSentinel);

        var record = fixture.Logger.Records.ShouldHaveSingleItem();
        record.Level.ShouldBe(LogLevel.Debug);
        // Separate rows ensure RED proves each logging surface independently, rather than
        // stopping at the rendered message and never exercising structured state/exception.
        switch (surface)
        {
            case "message":
                record.Message.ShouldBe(SafeDiagnostic);
                break;
            case "state":
                record.State.ShouldHaveSingleItem()
                    .ShouldBe(new KeyValuePair<string, object?>("{OriginalFormat}", SafeDiagnostic));
                break;
            case "exception":
                record.Exception.ShouldBeNull();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(surface));
        }

        var allSurfaces = string.Join("\n", record.Message,
            string.Join("\n", record.State.Select(pair => $"{pair.Key}={pair.Value}")),
            record.Exception?.ToString());
        foreach (var sentinel in new[] { PathSentinel, ExceptionSentinel, TokenSentinel,
                     fixture.PrimaryPath, fixture.LegacyPath })
        {
            allSurfaces.ShouldNotContain(sentinel);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetApiKeyAsync_WhenStatFailureIsStable_CachesReadsAndReloadsOnRecovery(bool legacy)
    {
        var fixture = new StatFailureFixture(legacy);
        fixture.WriteCredentials("primary-first", "legacy-first");

        (await fixture.Manager.GetApiKeyAsync("openai")).ShouldBe("primary-first");
        fixture.ReadPaths.ShouldBe(new[] { fixture.LegacyPath, fixture.PrimaryPath });

        // Rewrite only the failing candidate. Its unknown signature must remain stable,
        // even though the next full read would return a different credential.
        if (legacy)
            fixture.WriteCredential(fixture.LegacyPath, "legacy-rotated-longer");
        else
            fixture.WriteCredential(fixture.PrimaryPath, "primary-rotated-longer");

        (await fixture.Manager.GetApiKeyAsync("openai")).ShouldBe("primary-first");
        (await fixture.Manager.GetApiKeyAsync("openai")).ShouldBe("primary-first");
        fixture.ReadPaths.Count.ShouldBe(2);
        fixture.FailedStats.ShouldBe(3);

        fixture.StatFails = false;
        var recoveredToken = legacy ? "primary-first" : "primary-rotated-longer";
        (await fixture.Manager.GetApiKeyAsync("openai")).ShouldBe(recoveredToken);
        fixture.ReadPaths.ShouldBe(new[] { fixture.LegacyPath, fixture.PrimaryPath,
            fixture.LegacyPath, fixture.PrimaryPath });

        (await fixture.Manager.GetApiKeyAsync("openai")).ShouldBe(recoveredToken);
        fixture.ReadPaths.Count.ShouldBe(4);

        // A real metadata change after recovery still reloads, and primary continues to
        // override the legacy entry even when both contain the same provider.
        fixture.WriteCredential(fixture.PrimaryPath, "primary-after-recovery-even-longer");
        (await fixture.Manager.GetApiKeyAsync("openai")).ShouldBe("primary-after-recovery-even-longer");
        fixture.ReadPaths.Count.ShouldBe(6);
    }

    private sealed class StatFailureFixture
    {
        private readonly MockFileSystem _storage = new();
        private readonly DateTime _stamp = new(2026, 8, 29, 15, 40, 0, DateTimeKind.Utc);
        public string PrimaryPath { get; }
        public string LegacyPath { get; }
        public GatewayAuthManager Manager { get; }
        public CapturingLogger Logger { get; } = new();
        public List<string> ReadPaths { get; } = [];
        public bool StatFails { get; set; } = true;
        public int FailedStats { get; private set; }

        public StatFailureFixture(bool legacy)
        {
            var root = Path.Combine(Path.GetTempPath(), "botnexus", PathSentinel);
            PrimaryPath = Path.Combine(root, "current-auth.json");
            LegacyPath = Path.Combine(root, "legacy-auth.json");
            _storage.Directory.CreateDirectory(root);
            var failingPath = legacy ? LegacyPath : PrimaryPath;
            var file = Substitute.For<IFile>();
            file.Exists(Arg.Any<string>()).Returns(call => _storage.File.Exists(call.Arg<string>()));
            file.ReadAllText(Arg.Any<string>()).Returns(call =>
            {
                var path = call.Arg<string>();
                ReadPaths.Add(path);
                return _storage.File.ReadAllText(path);
            });
            var factory = Substitute.For<IFileInfoFactory>();
            factory.New(Arg.Any<string>()).Returns(call =>
            {
                var path = call.Arg<string>();
                if (StatFails && path == failingPath)
                {
                    FailedStats++;
                    throw new IOException($"{ExceptionSentinel}: {path}; {TokenSentinel}",
                        new UnauthorizedAccessException($"inner {ExceptionSentinel}: {path}"));
                }
                return _storage.FileInfo.New(path);
            });
            var fileSystem = Substitute.For<IFileSystem>();
            fileSystem.File.Returns(file);
            fileSystem.FileInfo.Returns(factory);
            fileSystem.Path.Returns(_storage.Path);
            fileSystem.Directory.Returns(_storage.Directory);
            var monitor = Substitute.For<IOptionsMonitor<PlatformConfig>>();
            monitor.CurrentValue.Returns(new PlatformConfig());
            Manager = new GatewayAuthManager(monitor, Logger, fileSystem);
            SetPath("_authFilePath", PrimaryPath);
            SetPath("_legacyAuthFilePath", LegacyPath);
        }

        public void WriteCredentials(string primary, string legacy)
        {
            WriteCredential(PrimaryPath, primary);
            WriteCredential(LegacyPath, legacy);
        }

        public void WriteCredential(string path, string token)
        {
            _storage.File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["openai"] = new { type = "token", access = token, expires = 4102444800000L }
            }));
            _storage.File.SetLastWriteTimeUtc(path, _stamp);
        }

        private void SetPath(string name, string path)
        {
            var field = typeof(GatewayAuthManager).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"Missing auth field {name}");
            field.SetValue(Manager, path);
        }
    }

    private sealed record LogRecord(LogLevel Level, string Message,
        IReadOnlyList<KeyValuePair<string, object?>> State, Exception? Exception);

    private sealed class CapturingLogger : ILogger<GatewayAuthManager>
    {
        public List<LogRecord> Records { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var structured = state as IEnumerable<KeyValuePair<string, object?>>
                ?? throw new InvalidOperationException("Expected structured logger state");
            Records.Add(new LogRecord(logLevel, formatter(state, exception), structured.ToArray(), exception));
        }
    }
}
