using BotNexus.Cli.Services;

namespace BotNexus.Cli.Tests;

/// <summary>
/// Regression coverage for issue #2882 -- service (re)installation must not destroy
/// operator-set environment entries, must fail on a non-zero reg.exe exit, and must not be
/// breakable by a quote character in the home path.
/// </summary>
public class ServiceEnvironmentPreservationTests
{
    private sealed record RecordedInvocation(string FileName, IReadOnlyList<string> Arguments, string? RawArgumentLine);

    /// <summary>Fake process runner: canned results by matcher, full invocation recording.</summary>
    private sealed class FakeProcessRunner : IServiceProcessRunner
    {
        private readonly List<(Func<string, string, bool> Match, ProcessRunResult Result)> _canned = [];

        public List<RecordedInvocation> Invocations { get; } = [];

        public void When(string fileName, string containsArgument, ProcessRunResult result)
            => _canned.Add(((f, a) => f == fileName && a.Contains(containsArgument, StringComparison.OrdinalIgnoreCase), result));

        public Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Invocations.Add(new RecordedInvocation(fileName, arguments, null));
            return Task.FromResult(Resolve(fileName, string.Join(' ', arguments)));
        }

        public Task<ProcessRunResult> RunRawAsync(string fileName, string argumentLine, CancellationToken cancellationToken)
        {
            Invocations.Add(new RecordedInvocation(fileName, [], argumentLine));
            return Task.FromResult(Resolve(fileName, argumentLine));
        }

        private ProcessRunResult Resolve(string fileName, string argumentText)
        {
            foreach (var (match, result) in _canned)
            {
                if (match(fileName, argumentText))
                    return result;
            }

            return new ProcessRunResult(0, string.Empty);
        }

        public RecordedInvocation RegAdd()
            => Invocations.Single(i => i.FileName == "reg" && i.Arguments.Contains("add"));
    }

    private static FakeProcessRunner RunnerWithExistingEnvironment(string regQueryOutput)
    {
        var runner = new FakeProcessRunner();
        runner.When("reg", "query", new ProcessRunResult(0, regQueryOutput));
        return runner;
    }

    private static string RegQueryOutput(string multiSzPayload) => $"""

        HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\BotNexus
            Environment    REG_MULTI_SZ    {multiSzPayload}

        """;

    // AC1: an entry BotNexus does not own survives the write.
    [Fact]
    public async Task SetServiceEnvironment_PreservesPreSeededOperatorEntry()
    {
        var runner = RunnerWithExistingEnvironment(RegQueryOutput(@"MY_SECRET=abc\0BOTNEXUS_HOME=C:\old"));
        var manager = new WindowsServiceManager(runner);

        var result = await manager.SetServiceEnvironmentAsync(@"C:\new-home", 8080, CancellationToken.None);

        Assert.True(result.Success);
        var payload = PayloadOf(runner.RegAdd());
        Assert.Contains("MY_SECRET=abc", payload);
    }

    // AC1: the existing value is actually READ, not blindly overwritten.
    [Fact]
    public async Task SetServiceEnvironment_QueriesExistingValueBeforeWriting()
    {
        var runner = RunnerWithExistingEnvironment(RegQueryOutput(@"MY_SECRET=abc"));
        var manager = new WindowsServiceManager(runner);

        await manager.SetServiceEnvironmentAsync(@"C:\home", 8080, CancellationToken.None);

        var queryIndex = runner.Invocations.FindIndex(i => i.FileName == "reg" && i.Arguments.Contains("query"));
        var addIndex = runner.Invocations.FindIndex(i => i.FileName == "reg" && i.Arguments.Contains("add"));
        Assert.True(queryIndex >= 0, "reg query was never issued -- the existing environment was not read.");
        Assert.True(queryIndex < addIndex, "reg query must precede reg add.");
    }

    // AC2: owned keys already present are REPLACED, not duplicated.
    [Fact]
    public async Task SetServiceEnvironment_ReplacesOwnedKeysWithoutDuplicating()
    {
        var runner = RunnerWithExistingEnvironment(
            RegQueryOutput(@"BOTNEXUS_HOME=C:\old\0ASPNETCORE_URLS=http://localhost:1111\0MY_SECRET=abc"));
        var manager = new WindowsServiceManager(runner);

        await manager.SetServiceEnvironmentAsync(@"C:\new-home", 8080, CancellationToken.None);

        var entries = PayloadOf(runner.RegAdd()).Split(@"\0", StringSplitOptions.RemoveEmptyEntries);

        Assert.Single(entries, e => e.StartsWith("BOTNEXUS_HOME=", StringComparison.Ordinal));
        Assert.Single(entries, e => e.StartsWith("ASPNETCORE_URLS=", StringComparison.Ordinal));
        Assert.Contains(@"BOTNEXUS_HOME=C:\new-home", entries);
        Assert.Contains("ASPNETCORE_URLS=http://localhost:8080", entries);
        Assert.Contains("MY_SECRET=abc", entries);
        Assert.DoesNotContain(@"BOTNEXUS_HOME=C:\old", entries);
        Assert.DoesNotContain("ASPNETCORE_URLS=http://localhost:1111", entries);
    }

    // AC2 (merge unit): duplicate occurrences of an owned key collapse into one.
    [Fact]
    public void MergeEnvironment_CollapsesDuplicateOwnedKeys()
    {
        var merged = WindowsServiceManager.MergeEnvironment(
            [@"BOTNEXUS_HOME=one", "KEEP=yes", @"BOTNEXUS_HOME=two"],
            new Dictionary<string, string> { ["BOTNEXUS_HOME"] = "final" });

        Assert.Equal(["BOTNEXUS_HOME=final", "KEEP=yes"], merged);
    }

    // AC3: a non-zero reg.exe exit fails the install rather than being swallowed.
    [Fact]
    public async Task SetServiceEnvironment_NonZeroRegExit_ReturnsFailure()
    {
        var runner = RunnerWithExistingEnvironment(RegQueryOutput("MY_SECRET=abc"));
        runner.When("reg", "add", new ProcessRunResult(1, "ERROR: Access is denied."));
        var manager = new WindowsServiceManager(runner);

        var result = await manager.SetServiceEnvironmentAsync(@"C:\home", 8080, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Access is denied", result.Message, StringComparison.Ordinal);
    }

    // AC3: and that failure propagates out of InstallAsync.
    [Fact]
    public async Task InstallAsync_NonZeroRegExit_ReturnsUnsuccessfulResult()
    {
        var runner = new FakeProcessRunner();
        runner.When("sc.exe", "query", new ProcessRunResult(1, "not installed")); // so install proceeds
        runner.When("reg", "query", new ProcessRunResult(1, string.Empty));       // no existing value
        runner.When("reg", "add", new ProcessRunResult(5, "ERROR: Access is denied."));
        var manager = new WindowsServiceManager(runner);

        var result = await manager.InstallAsync(@"C:\bin\gateway.exe", @"C:\home", 8080, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("environment", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            runner.Invocations,
            i => i.FileName == "sc.exe" && (i.RawArgumentLine ?? string.Empty).StartsWith("start ", StringComparison.Ordinal));
    }

    // AC5: a quote character in homePath must not produce a malformed reg.exe argument.
    [Fact]
    public async Task SetServiceEnvironment_QuotedHomePath_ProducesWellFormedArgument()
    {
        var runner = RunnerWithExistingEnvironment(RegQueryOutput("MY_SECRET=abc"));
        var manager = new WindowsServiceManager(runner);
        var homePath = @"C:\home""with quote\bn";

        var result = await manager.SetServiceEnvironmentAsync(homePath, 8080, CancellationToken.None);

        Assert.True(result.Success);
        var add = runner.RegAdd();

        // The value must arrive as ONE discrete argument token carrying the exact path,
        // so quoting cannot be terminated early by the embedded quote character.
        var payload = PayloadOf(add);
        Assert.Contains($"BOTNEXUS_HOME={homePath}", payload);
        Assert.Equal(["add", WindowsServiceManager.RegistryKeyPath, "/v", "Environment", "/t", "REG_MULTI_SZ", "/d", payload, "/f"], add.Arguments);
    }

    [Fact]
    public void ParseMultiSz_MissingValue_ReturnsEmpty()
    {
        Assert.Empty(WindowsServiceManager.ParseMultiSz("ERROR: The system was unable to find the specified registry key or value."));
        Assert.Empty(WindowsServiceManager.ParseMultiSz(string.Empty));
    }

    // AC4: an existing systemd unit's foreign Environment= lines survive regeneration.
    [Fact]
    public async Task SystemdInstall_PreservesForeignEnvironmentLines()
    {
        var unitPath = Path.Combine(Path.GetTempPath(), $"botnexus-2882-{Guid.NewGuid():N}.service");
        await File.WriteAllTextAsync(unitPath, """
            [Service]
            ExecStart=/old/gateway
            Environment=ASPNETCORE_URLS=http://localhost:1111
            Environment=BOTNEXUS_HOME=/old/home
            Environment=MY_SECRET=abc
            Environment=OPERATOR_TOKEN=t0ken
            """);

        try
        {
            var runner = new FakeProcessRunner();
            runner.When("systemctl", "is-enabled", new ProcessRunResult(1, "disabled"));
            var manager = new SystemdServiceManager(runner, unitPath);

            var result = await manager.InstallAsync("/opt/botnexus/gateway", "/new/home", 8080, CancellationToken.None);

            Assert.True(result.Success);
            var written = await File.ReadAllTextAsync(unitPath);

            Assert.Contains("Environment=MY_SECRET=abc", written, StringComparison.Ordinal);
            Assert.Contains("Environment=OPERATOR_TOKEN=t0ken", written, StringComparison.Ordinal);
            Assert.Contains("Environment=BOTNEXUS_HOME=/new/home", written, StringComparison.Ordinal);
            Assert.Contains("Environment=ASPNETCORE_URLS=http://localhost:8080", written, StringComparison.Ordinal);
            Assert.DoesNotContain("BOTNEXUS_HOME=/old/home", written, StringComparison.Ordinal);
            Assert.DoesNotContain("http://localhost:1111", written, StringComparison.Ordinal);

            // Owned keys appear exactly once each -- regeneration must not duplicate them.
            var lines = written.Split('\n').Select(l => l.Trim('\r')).ToArray();
            Assert.Single(lines, l => l.StartsWith("Environment=BOTNEXUS_HOME=", StringComparison.Ordinal));
            Assert.Single(lines, l => l.StartsWith("Environment=ASPNETCORE_URLS=", StringComparison.Ordinal));
            Assert.Single(lines, l => l.StartsWith("Environment=DOTNET_ENVIRONMENT=", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(unitPath))
                File.Delete(unitPath);
        }
    }

    [Fact]
    public void ExtractForeignEnvironmentLines_DropsOwnedKeysOnly()
    {
        var preserved = SystemdServiceManager.ExtractForeignEnvironmentLines("""
            [Service]
            Environment=BOTNEXUS_HOME=/x
            Environment=DOTNET_ENVIRONMENT=Production
            Environment=ASPNETCORE_URLS=http://localhost:1
            Environment=CUSTOM=1
            ExecStart=/x
            """);

        Assert.Equal(["Environment=CUSTOM=1"], preserved);
    }

    private static string PayloadOf(RecordedInvocation regAdd)
    {
        var index = regAdd.Arguments.ToList().IndexOf("/d");
        Assert.True(index >= 0, "reg add did not carry a /d payload argument.");
        return regAdd.Arguments[index + 1];
    }
}
