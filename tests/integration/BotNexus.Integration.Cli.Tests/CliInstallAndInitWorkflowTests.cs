namespace BotNexus.Integration.Cli.Tests;

/// <summary>
/// Test 3: drive the installed CLI through the <c>install</c> and <c>init</c> flow.
///
/// Uses a fresh temp directory as a self-contained sandbox:
///   - <c>&lt;tmp&gt;/source</c>     → target for <c>botnexus install --source</c> (git clones the current repo here)
///   - <c>&lt;tmp&gt;/.botnexus</c>  → target for <c>botnexus init --target</c>     (config home)
///
/// The <c>--repo</c> argument points at the current repository on disk so the test
/// does not require external network for the git clone step (the CLI itself was
/// installed from nuget.org by the fixture).
/// </summary>
[Collection(CliCollection.Name)]
public sealed class CliInstallAndInitWorkflowTests : IAsyncLifetime
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    private readonly CliInstallFixture _fixture;
    private string _sandbox = string.Empty;

    public CliInstallAndInitWorkflowTests(CliInstallFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "botnexus-cli-flow", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_sandbox))
                Directory.Delete(_sandbox, recursive: true);
        }
        catch
        {
            // Cloned .git directories can have read-only files on Windows; best-effort cleanup.
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Cli_Install_ClonesCurrentRepoIntoSandbox()
    {
        _fixture.InstallSucceeded.ShouldBeTrue(
            "CLI install fixture did not complete successfully — see CliInstallationTests for the install failure.");

        var repoRoot = RepoLocator.FindRepoRoot();
        var sourceDir = Path.Combine(_sandbox, "source");

        var result = await ProcessRunner.RunAsync(
            _fixture.CliExecutablePath,
            $"install --source \"{sourceDir}\" --repo \"{repoRoot}\"",
            timeout: CommandTimeout);

        result.ExitCode.ShouldBe(
            0,
            $"botnexus install failed.\nStdOut:\n{result.StdOut}\nStdErr:\n{result.StdErr}");

        Directory.Exists(sourceDir).ShouldBeTrue(
            $"Expected source directory at {sourceDir} after install.");
        Directory.Exists(Path.Combine(sourceDir, ".git")).ShouldBeTrue(
            "Cloned source should contain a .git directory.");
        File.Exists(Path.Combine(sourceDir, "BotNexus.slnx")).ShouldBeTrue(
            "Cloned source should contain BotNexus.slnx (proves the clone is of the current repo).");
    }

    [Fact]
    public async Task Cli_Init_CreatesConfigInIsolatedHome()
    {
        _fixture.InstallSucceeded.ShouldBeTrue(
            "CLI install fixture did not complete successfully — see CliInstallationTests for the install failure.");

        var configHome = Path.Combine(_sandbox, ".botnexus");

        // Explicit --target is the contract this test pins; BOTNEXUS_HOME is also cleared
        // to make sure nothing leaks from the test host into the CLI invocation.
        var env = new Dictionary<string, string?>
        {
            ["BOTNEXUS_HOME"] = null
        };

        var result = await ProcessRunner.RunAsync(
            _fixture.CliExecutablePath,
            $"init --target \"{configHome}\"",
            environment: env,
            timeout: CommandTimeout);

        result.ExitCode.ShouldBe(
            0,
            $"botnexus init failed.\nStdOut:\n{result.StdOut}\nStdErr:\n{result.StdErr}");

        // ── Directory layout ──────────────────────────────────────────────
        // With an explicit --target (non-default home), the CLI seeds only the
        // config file. The richer layout (auth.json, agents/, sessions/, logs/)
        // is reserved for BotNexusHome.Initialize() on the default home path,
        // so we assert that nothing extra leaks into the sandbox.
        Directory.Exists(configHome).ShouldBeTrue(
            $"Expected init to create config home at {configHome}.");

        var configPath = Path.Combine(configHome, "config.json");
        File.Exists(configPath).ShouldBeTrue(
            $"Expected init to create config.json at {configPath}.");

        var actualEntries = Directory.GetFileSystemEntries(configHome)
            .Select(p => Path.GetFileName(p)!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        actualEntries.ShouldBe(
            new[] { "config.json" },
            $"Unexpected entries in {configHome}. Got: {string.Join(", ", actualEntries)}");

        // ── config.json content ───────────────────────────────────────────
        await using var stream = File.OpenRead(configPath);
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;

        root.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Object);

        // version
        root.TryGetProperty("version", out var version).ShouldBeTrue("config.json missing 'version'.");
        version.GetInt32().ShouldBe(1);

        // gateway block
        root.TryGetProperty("gateway", out var gateway).ShouldBeTrue("config.json missing 'gateway'.");
        gateway.GetProperty("listenUrl").GetString().ShouldBe("http://0.0.0.0:5005");
        gateway.GetProperty("defaultAgentId").GetString().ShouldBe("assistant");
        gateway.GetProperty("enableProviderRequestLogging").GetBoolean().ShouldBeFalse();

        var sessionStore = gateway.GetProperty("sessionStore");
        sessionStore.GetProperty("type").GetString().ShouldBe("Sqlite");
        var connectionString = sessionStore.GetProperty("connectionString").GetString();
        connectionString.ShouldNotBeNullOrWhiteSpace();
        connectionString!.ShouldStartWith("Data Source=");
        // Connection string must point inside the sandboxed home (not the user's real ~/.botnexus).
        connectionString.ShouldContain("sessions.sqlite");
        var expectedSqlitePath = Path.Combine(configHome, "sessions.sqlite");
        connectionString.ShouldContain(
            expectedSqlitePath,
            customMessage: $"sessionStore.connectionString must point at the sandboxed home. Got: {connectionString}");

        // cron block
        root.TryGetProperty("cron", out var cron).ShouldBeTrue("config.json missing 'cron'.");
        cron.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        cron.GetProperty("tickIntervalSeconds").GetInt32().ShouldBe(60);

        // agents block + defaults + seeded assistant
        root.TryGetProperty("agents", out var agents).ShouldBeTrue("config.json missing 'agents'.");

        agents.TryGetProperty("defaults", out var defaults).ShouldBeTrue("agents.defaults missing.");
        var memory = defaults.GetProperty("memory");
        memory.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        memory.GetProperty("indexing").GetString().ShouldBe("auto");

        agents.TryGetProperty("assistant", out var assistant).ShouldBeTrue("agents.assistant missing.");
        assistant.GetProperty("provider").GetString().ShouldBe("github-copilot");
        assistant.GetProperty("model").GetString().ShouldBe("gpt-4.1");
        assistant.GetProperty("enabled").GetBoolean().ShouldBeTrue();
    }
}
