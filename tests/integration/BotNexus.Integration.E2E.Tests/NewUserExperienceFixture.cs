using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace BotNexus.Integration.E2E.Tests;

/// <summary>
/// xUnit collection fixture for the new-user E2E suite.
///
/// Lifecycle:
///   1. Pack + install the in-tree CLI as a global tool into a per-run sandbox.
///   2. Provision a clean tmp <c>BOTNEXUS_HOME</c> end-to-end via the CLI:
///      <c>init</c> → <c>provider add</c> (integration-mock) → <c>agent add</c> x3
///      → <c>locations add</c> x2 → world identity via <c>config set</c>
///      → extensions config via <c>config set</c>.
///   3. Start the gateway as a subprocess pointed at the tmp home.
///   4. Wait for <c>GET /health</c> to return 200 on the chosen port.
///
/// Tests assert against the published config (via the CLI itself) and against
/// the live gateway. The Playwright UI flow is layered on top in the
/// <c>PortalUserJourneyTests</c> file.
///
/// All failures during initialization are captured (not thrown) so dependent
/// tests can skip gracefully and the install/provisioning diagnostics surface
/// in test output rather than as opaque collection-fixture exceptions.
/// </summary>
public sealed class NewUserExperienceFixture : IAsyncLifetime
{
    private const string PackageId = "BotNexus.Cli";
    private static readonly TimeSpan PackTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan CliTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan GatewayReadyTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan SolutionBuildTimeout = TimeSpan.FromMinutes(10);

    // ─── pack/install artifacts ────────────────────────────────────────────
    public string PackVersion { get; private set; } = string.Empty;
    public string CliExecutablePath { get; private set; } = string.Empty;

    // ─── per-run sandbox ───────────────────────────────────────────────────
    public string SandboxRoot { get; private set; } = string.Empty;
    public string Home { get; private set; } = string.Empty;
    public string CatalogPath { get; private set; } = string.Empty;
    public int GatewayPort { get; private set; }
    public string GatewayBaseUrl => $"http://127.0.0.1:{GatewayPort}";

    // ─── outcomes ──────────────────────────────────────────────────────────
    public bool Succeeded { get; private set; }
    public string? Error { get; private set; }
    public List<string> Log { get; } = new();

    // "assistant" is the default agent seeded by `botnexus init`. The fixture overrides
    // gateway.defaultAgentId to AgentIds[0] ("alpha"), but "assistant" remains in the
    // agent list and is rendered by the portal. Include it here so tests that assert
    // agent presence or iterate over all agents (e.g.
    // ProvisioningSmokeTests.ConfigJsonContainsExpectedShape) see the full list.
    //
    // Collision note (issue #2491): because `init` already seeds "assistant",
    // `agent add assistant` fails with "Agent 'assistant' already exists." and exits 1.
    // RunCliAsync throws on a non-zero exit, which aborted InitializeAsync, left
    // Succeeded == false, and silently skipped every E2E test class via Skip.IfNot.
    // The provisioning loop below is therefore add-if-absent: it consults
    // AgentExistsAsync (backed by `agent show`) and skips the add for agents that
    // `init` already created.
    public IReadOnlyList<string> AgentIds { get; } = new[] { "alpha", "bravo", "charlie", "assistant" };
    public IReadOnlyList<string> LocationNames { get; } = new[] { "workspace-tmp", "scratch" };

    private ProcessRunner.BackgroundProcess? _gateway;

    public async Task InitializeAsync()
    {
        try
        {
            var runId = Guid.NewGuid().ToString("N");
            PackVersion = $"99.99.99-e2e-{runId[..8]}";
            SandboxRoot = Path.Combine(Path.GetTempPath(), "botnexus-e2e", runId);
            Home = Path.Combine(SandboxRoot, "home");
            var packDir = Path.Combine(SandboxRoot, "pack");
            var packArtifactsDir = Path.Combine(SandboxRoot, "build");
            var toolDir = Path.Combine(SandboxRoot, "tool");
            Directory.CreateDirectory(Home);
            Directory.CreateDirectory(packDir);
            Directory.CreateDirectory(packArtifactsDir);
            Directory.CreateDirectory(toolDir);

            var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "botnexus.exe" : "botnexus";
            CliExecutablePath = Path.Combine(toolDir, exeName);

            var repoRoot = RepoLocator.FindRepoRoot();
            var cliProject = Path.Combine(repoRoot, "src", "gateway", "BotNexus.Cli", "BotNexus.Cli.csproj");

            // 1 ─ pack -----------------------------------------------------
            // ArtifactsPath isolates this pack from concurrent Release builds and the CLI
            // integration fixture. Without it, shared bin/obj writes can produce a torn tool
            // package that fails later while loading Providers.Core (issue #3255).
            Log.Add($"[pack] dotnet pack {cliProject} → {packDir} (Version={PackVersion})");
            var pack = await ProcessRunner.RunAsync(
                "dotnet",
                $"pack \"{cliProject}\" --configuration Release --output \"{packDir}\" " +
                $"/p:Version={PackVersion} /p:PackageVersion={PackVersion} " +
                $"/p:ArtifactsPath=\"{packArtifactsDir}\" " +
                $"/nodeReuse:false /p:UseSharedCompilation=false --nologo",
                environment: new Dictionary<string, string?> { ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0" },
                timeout: PackTimeout);
            if (pack.ExitCode != 0)
            {
                Error = $"dotnet pack exit {pack.ExitCode}.\n{pack.Combined}";
                return;
            }

            // 2 ─ tool install ---------------------------------------------
            Log.Add($"[install] dotnet tool install {PackageId} --tool-path {toolDir}");
            var install = await ProcessRunner.RunAsync(
                "dotnet",
                $"tool install --tool-path \"{toolDir}\" --add-source \"{packDir}\" --version {PackVersion} {PackageId}",
                environment: new Dictionary<string, string?> { ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0" },
                timeout: InstallTimeout);
            if (install.ExitCode != 0 || !File.Exists(CliExecutablePath))
            {
                Error = $"dotnet tool install exit {install.ExitCode}, exe-exists={File.Exists(CliExecutablePath)}.\n{install.Combined}";
                return;
            }

            // 3 ─ copy mock catalog into sandbox ---------------------------
            var srcCatalog = Path.Combine(AppContext.BaseDirectory, "MockCatalogs", "e2e-catalog.json");
            CatalogPath = Path.Combine(SandboxRoot, "e2e-catalog.json");
            File.Copy(srcCatalog, CatalogPath, overwrite: true);

            // 4 ─ green-field CLI provisioning -----------------------------
            await RunCliAsync("init", $"--target \"{Home}\"");
            await RunCliAsync("provider", $"add --name integration-mock --api integration-mock " +
                $"--default-model integration-mock-echo --base-url \"{CatalogPath}\" --target \"{Home}\"");

            foreach (var id in AgentIds)
            {
                // Re-provision-if-present: `init` already seeds the default "assistant"
                // agent and `agent add` on an existing id exits 1 (issue #2491).
                //
                // Skipping the add is NOT sufficient. init seeds "assistant" against the
                // real default provider/model, but every E2E test drives the
                // integration-mock provider, so a merely-skipped assistant is present in
                // the agent list yet cannot answer a prompt. There is no `agent update`
                // verb, so the only way to re-point an existing agent is remove-then-add.
                if (await AgentExistsAsync(id))
                {
                    Log.Add($"[cli] agent {id} exists (seeded by init); re-provisioning onto integration-mock");
                    await RunCliAsync("agent", $"remove {id} --target \"{Home}\"");
                }

                await RunCliAsync("agent",
                    $"add {id} --provider integration-mock --model integration-mock-echo --target \"{Home}\"");
            }

            foreach (var loc in LocationNames)
            {
                var locPath = Path.Combine(SandboxRoot, "locations", loc);
                Directory.CreateDirectory(locPath);
                await RunCliAsync("locations",
                    $"add {loc} --type filesystem --path \"{locPath}\" --target \"{Home}\"");
            }

            // World identity + extension toggles via the generic config setter
            // (issue #599 tracks dedicated `world` and `extension` commands).
            // All these live under GatewaySettingsConfig, hence the `gateway.*` prefix.
            await RunCliAsync("config", $"set gateway.world \"{{\\\"id\\\":\\\"e2e-world\\\",\\\"name\\\":\\\"E2E World\\\"}}\" --target \"{Home}\"");
            await RunCliAsync("config", $"set gateway.extensions.enabled true --target \"{Home}\"");

            // Default agent → first provisioned agent.
            await RunCliAsync("config", $"set gateway.defaultAgentId {AgentIds[0]} --target \"{Home}\"");

            // 5 ─ pre-build the deployment closure then start the gateway via the CLI
            //     with --skip-build. We must pre-build (a) so the gateway dll exists and
            //     (b) so the in-test build can't collide with the running testhost
            //     that has many of the same dlls loaded for the test process itself
            //     (BotNexus.Domain, BotNexus.Gateway.Contracts, etc.) - MSBuild
            //     would otherwise try to overwrite those locked dlls. /nodeReuse:false
            //     + UseSharedCompilation=false force MSBuild and the Roslyn server
            //     to exit cleanly so this subprocess returns.
            //
            //     SCOPE (#2910): src/dirs.proj, NOT Directory.Packages.props. The solution carries 112
            //     projects, 57 of them test projects that this fixture never deploys or loads;
            //     building them in Release from inside the test phase is pure waste. Release
            //     itself is load-bearing and must stay - GatewayCommand resolves the host from
            //     a hardcoded bin/Release path - so narrow the SET, never the CONFIGURATION.
            //     The build command itself now lives in EnsureSolutionBuiltAsync, which
            //     serialises it behind a machine-wide mutex (#2739).
            var build = await EnsureSolutionBuiltAsync(repoRoot);
            if (build.ExitCode != 0)
            {
                Error = $"Solution prebuild exit {build.ExitCode}.\n{build.Combined}";
                return;
            }

            GatewayPort = PickFreePort();
            Log.Add($"[gateway] picked port {GatewayPort}");

            // The gateway honours platformConfig.Gateway.ListenUrl OVER ASPNETCORE_URLS / --port.
            // Set it explicitly so the chosen test port wins on the bind.
            await RunCliAsync("config", $"set gateway.listenUrl http://127.0.0.1:{GatewayPort} --target \"{Home}\"");

            var env = new Dictionary<string, string?>
            {
                ["BOTNEXUS_HOME"] = Home,
                ["BOTNEXUS_MOCK_CATALOG"] = CatalogPath,
            };
            _gateway = ProcessRunner.StartBackground(
                CliExecutablePath,
                $"gateway start --attached --skip-build --source \"{repoRoot}\" --target \"{Home}\" --port {GatewayPort}",
                environment: env);

            var ready = await WaitForGatewayReadyAsync(GatewayBaseUrl, GatewayReadyTimeout, _gateway);
            if (!ready)
            {
                Error = $"Gateway did not become ready within {GatewayReadyTimeout} on {GatewayBaseUrl}.\n" +
                        $"StdOut:\n{_gateway.SnapshotStdOut()}\nStdErr:\n{_gateway.SnapshotStdErr()}";
                return;
            }

            Succeeded = true;
        }
        catch (Exception ex)
        {
            Error = ex.ToString();
            Succeeded = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_gateway is not null)
            await _gateway.DisposeAsync();
        try
        {
            if (!string.IsNullOrEmpty(SandboxRoot) && Directory.Exists(SandboxRoot))
                Directory.Delete(SandboxRoot, recursive: true);
        }
        catch
        {
            // Best-effort cleanup. SQLite write-ahead files and locked .nupkg blobs on
            // Windows are not worth failing the suite for.
        }
    }

    /// <summary>
    /// Serialise the solution prebuild across every process on the machine (issue #2739).
    ///
    /// The prebuild writes the shared <c>bin/Release</c> and <c>obj</c> trees. When two
    /// xUnit test hosts for this project run concurrently, both fixtures entered this
    /// build at the same time and raced those outputs, producing CS2012 / MSB3883
    /// file-lock failures, <c>Solution prebuild exit 1</c>, and - because every test
    /// class guards on <c>Skip.IfNot(_fx.Succeeded, ...)</c> - a suite that degraded
    /// into ~265 silent skips while still exiting 0. A vacuously green gate is worse
    /// than a red one.
    ///
    /// A machine-wide named <see cref="Mutex"/> is used rather than an in-process lock
    /// because the contending builds live in DIFFERENT PROCESSES; an in-process lock
    /// cannot see them. The second holder finds the outputs already current and its
    /// build is a cheap no-op, so serialising costs one build, not two.
    ///
    /// The entire acquire/build/release cycle runs on ONE dedicated thread via
    /// <see cref="Task.Run(Action)"/> because a Win32 mutex has THREAD AFFINITY: only
    /// the thread that acquired it may release it. Awaiting the build inline resumed
    /// the continuation on a different thread-pool thread, and ReleaseMutex then threw
    /// "Object synchronization method was called from an unsynchronized block of code",
    /// failing initialization outright. Do not re-inline this await.
    /// </summary>
    private Task<ProcessRunner.ProcessResult> EnsureSolutionBuiltAsync(string repoRoot) => Task.Run(() =>
    {
        // "Global\" so the mutex is visible across sessions, not just the current one.
        using var mutex = new Mutex(initiallyOwned: false, name: @"Global\botnexus-e2e-prebuild");
        var held = false;
        try
        {
            try
            {
                held = mutex.WaitOne(SolutionBuildTimeout);
            }
            catch (AbandonedMutexException)
            {
                // A previous holder died mid-build. We now own the mutex; the build
                // below re-derives whatever that process left half-written.
                held = true;
            }

            if (!held)
            {
                Log.Add($"[build] timed out after {SolutionBuildTimeout} waiting for the prebuild mutex");
                return new ProcessRunner.ProcessResult(
                    ExitCode: 1,
                    StdOut: string.Empty,
                    StdErr: $"Solution prebuild mutex not acquired within {SolutionBuildTimeout}.");
            }

            Log.Add("[build] dotnet build src/dirs.proj -c Release (prebuild, deployment closure, mutex held)");
            return ProcessRunner.RunAsync(
                "dotnet",
                "build src/dirs.proj --configuration Release --nologo --tl:off /nodeReuse:false /p:UseSharedCompilation=false",
                workingDirectory: repoRoot,
                environment: new Dictionary<string, string?> { ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0" },
                timeout: SolutionBuildTimeout).GetAwaiter().GetResult();
        }
        finally
        {
            if (held) mutex.ReleaseMutex();
        }
    });

    /// <summary>
    /// Invoke the installed CLI with a sandboxed environment so it cannot leak into
    /// the developer's real <c>~/.botnexus</c>.
    /// </summary>
    public async Task<ProcessRunner.ProcessResult> RunCliAsync(string verb, string args)
    {
        var env = new Dictionary<string, string?>
        {
            ["BOTNEXUS_HOME"] = null,
        };
        Log.Add($"[cli] {verb} {args}");
        var result = await ProcessRunner.RunAsync(
            CliExecutablePath, $"{verb} {args}", environment: env, timeout: CliTimeout);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"CLI command '{verb} {args}' exited {result.ExitCode}.\n{result.Combined}");
        }
        return result;
    }
    /// <summary>
    /// True when <paramref name="id"/> is already present in the sandbox config.
    /// Uses <c>agent show</c>, which exits 0 when the agent resolves and 1 when it
    /// does not, so this never throws for the "absent" case (unlike RunCliAsync).
    /// </summary>
    private async Task<bool> AgentExistsAsync(string id)
    {
        var env = new Dictionary<string, string?> { ["BOTNEXUS_HOME"] = null };
        var result = await ProcessRunner.RunAsync(
            CliExecutablePath, $"agent show {id} --json --target \"{Home}\"",
            environment: env, timeout: CliTimeout);
        return result.ExitCode == 0;
    }

    private static int PickFreePort()
    {
        // Bind to port 0 to let the OS pick an unused port, then release it.
        // There's a tiny race window between release and gateway bind, but the
        // chance of collision in CI is negligible compared to hard-coding a port.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static async Task<bool> WaitForGatewayReadyAsync(string baseUrl, TimeSpan timeout, ProcessRunner.BackgroundProcess process)
    {
        // Phase 1: TCP-level probe — wait for Kestrel to bind the port before making HTTP calls.
        // This prevents spurious connection-refused errors on slow CI runners.
        var uri = new Uri(baseUrl);
        var tcpReady = await TcpReadinessProbe.WaitForTcpReadyAsync(
            uri.Host, uri.Port, timeout / 2);
        if (!tcpReady || process.HasExited)
            return false;

        // Phase 2: HTTP health check — port is accepting TCP but app may still be initializing.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTime.UtcNow + (timeout / 2);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                return false;
            try
            {
                var resp = await http.GetAsync($"{baseUrl}/health");
                if (resp.StatusCode == HttpStatusCode.OK)
                    return true;
            }
            catch
            {
                // Gateway not up yet; retry until deadline.
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
        return false;
    }
}

[CollectionDefinition(Name)]
public sealed class NewUserExperienceCollection : ICollectionFixture<NewUserExperienceFixture>
{
    public const string Name = "New user E2E";
}

/// <summary>
/// Isolated collection for MobileScrollTests — uses its own gateway instance so
/// mobile scroll tests cannot pollute the shared mock-provider state of the main
/// NewUserExperienceCollection.
/// </summary>
[CollectionDefinition(MobileScrollCollection.Name)]
public sealed class MobileScrollCollection : ICollectionFixture<NewUserExperienceFixture>
{
    public const string Name = "Mobile scroll E2E";
}
