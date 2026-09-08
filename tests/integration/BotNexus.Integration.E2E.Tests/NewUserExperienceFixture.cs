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
    /// <summary>Per-run NuGet PACKAGE version. Never an assembly version (issue #3388).</summary>
    public string PackVersion { get; private set; } = string.Empty;

    /// <summary>
    /// Assembly version every assembly in the installed layout must carry - the repo's real
    /// version, matching the Release output the runner already built.
    /// </summary>
    public Version AssemblyVersion { get; private set; } = new(0, 0, 0, 0);

    /// <summary>The exact <c>dotnet pack</c> arguments used, captured so the contract is assertable.</summary>
    public string PackArguments { get; private set; } = string.Empty;

    /// <summary>
    /// Missing or version-skewed startup assemblies in the installed tool layout. Non-empty means
    /// the CLI would have failed at assembly-load time during <c>init</c> (issue #3388).
    /// </summary>
    public IReadOnlyList<string> LayoutFaults { get; private set; } = [];

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
            PackVersion = E2ECliPack.BuildPackageVersion(runId);
            var sandboxFamilyRoot = Path.Combine(Path.GetTempPath(), "botnexus-e2e");

            // Reclaim sandboxes abandoned by a run that was killed rather than finished. Nothing
            // else reaps them: a gigabyte of these had accumulated before this was added.
            BotNexus.Integration.Testing.SandboxProcessGuard.ReapStaleSandboxes(sandboxFamilyRoot);

            SandboxRoot = Path.Combine(sandboxFamilyRoot, runId);
            Home = Path.Combine(SandboxRoot, "home");
            var packDir = Path.Combine(SandboxRoot, "pack");
            var packArtifactsDir = Path.Combine(SandboxRoot, "build");
            var toolDir = Path.Combine(SandboxRoot, "tool");
            // Marked before anything expensive goes in, so an interrupted run still leaves a
            // sandbox the next one can recognise as abandoned.
            BotNexus.Integration.Testing.SandboxProcessGuard.MarkSandboxOwner(SandboxRoot);
            Directory.CreateDirectory(Home);
            Directory.CreateDirectory(packDir);
            Directory.CreateDirectory(packArtifactsDir);
            Directory.CreateDirectory(toolDir);

            var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "botnexus.exe" : "botnexus";
            CliExecutablePath = Path.Combine(toolDir, exeName);

            var repoRoot = RepoLocator.FindRepoRoot();
            var cliProject = Path.Combine(repoRoot, "src", "gateway", "BotNexus.Cli", "BotNexus.Cli.csproj");

            // 1 ─ pack -----------------------------------------------------
            // The command line is built by E2ECliPack so the #3388 contract lives in one place
            // and is unit-assertable: the synthetic 99.99.99 stamp identifies the PACKAGE only,
            // while MSBuild `Version` stays at the repo's real assembly version. Stamping
            // `Version` too propagated the synthetic value through every ProjectReference, which
            // both rebuilt the CLI's entire dependency closure from inside this testhost (the
            // startup cost #3314 attributed) and produced a package whose CLI bound against
            // 99.99.99.0 while the dependencies copied out of the shared bin/Release tree carried
            // the repo version - hence `Could not load file or assembly
            // 'BotNexus.Agent.Providers.Core, Version=99.99.99.0'` during `init`.
            //
            // Serialised behind the SAME machine-wide mutex as the prebuild (#2739): a pack of
            // src/**/bin/Release and a build of it are both writers of that tree, so overlapping
            // them is the torn read #3255 documented. ArtifactsPath (from main) additionally keeps
            // this pack's own intermediates out of that shared tree entirely.
            AssemblyVersion = E2ECliPack.RepoAssemblyVersion;
            PackArguments = E2ECliPack.BuildPackArguments(
                cliProject, AssemblyVersion, PackVersion, packDir, packArtifactsDir);
            Log.Add($"[pack] dotnet pack {cliProject} → {packDir} " +
                    $"(PackageVersion={PackVersion}, Version={AssemblyVersion.ToString(3)})");
            var pack = await RunUnderPrebuildMutexAsync(() => ProcessRunner.RunAsync(
                "dotnet",
                PackArguments,
                environment: new Dictionary<string, string?> { ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0" },
                timeout: PackTimeout));
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

            // 2b ─ verify the install layout BEFORE any CLI verb runs (#3388) --
            // The packed CLI is about to be invoked for `init`. If a required assembly is absent
            // or carries a version other than the one the CLI binds against, that invocation dies
            // with `Could not load file or assembly` and the fixture reports only "exited 1" -
            // the uninformative evidence #3388 was filed on. Check by name here so the failure
            // states the assembly and both versions at the step that produced it.
            LayoutFaults = E2ECliPack.FindLayoutFaults(
                toolDir, E2ECliPack.ExpectedBoundVersion(AssemblyVersion));
            if (LayoutFaults.Count > 0)
            {
                Error =
                    "Packed CLI install layout will not bind (issue #3388). " +
                    $"Expected assembly version {E2ECliPack.ExpectedBoundVersion(AssemblyVersion)}; faults: " +
                    string.Join("; ", LayoutFaults) + ".\n" +
                    "The binary would start and then fail at assembly-load time inside `init`, so " +
                    "initialization fails here by name instead.\n" +
                    $"Pack arguments were: {PackArguments}";
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

            // Recorded so a later run can kill this gateway if this one is stopped before disposing.
            BotNexus.Integration.Testing.SandboxProcessGuard.RecordSandboxGateway(SandboxRoot, _gateway.ProcessId);

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
    private Task<ProcessRunner.ProcessResult> EnsureSolutionBuiltAsync(string repoRoot) =>
        RunUnderPrebuildMutexAsync(() => ProcessRunner.RunAsync(
            "dotnet",
            "build src/dirs.proj --configuration Release --nologo --tl:off /nodeReuse:false /p:UseSharedCompilation=false",
            workingDirectory: repoRoot,
            environment: new Dictionary<string, string?> { ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0" },
            timeout: SolutionBuildTimeout),
            description: "dotnet build src/dirs.proj -c Release (prebuild, deployment closure)");

    /// <summary>
    /// Runs <paramref name="work"/> while holding the machine-wide prebuild mutex.
    ///
    /// Both writers of the shared <c>src/**/bin/Release</c> tree go through here: the prebuild
    /// (#2739) and, since #3388, the CLI pack. The pack READS that tree to assemble the package,
    /// so overlapping it with a build yields an internally inconsistent nupkg whose CLI fails at
    /// assembly-load time much later, in an unrelated test (#3255, #3237).
    ///
    /// The entire acquire/run/release cycle executes on ONE dedicated thread because a Win32
    /// mutex has THREAD AFFINITY: only the thread that acquired it may release it. Awaiting the
    /// work inline resumed the continuation on a different thread-pool thread and ReleaseMutex
    /// threw "Object synchronization method was called from an unsynchronized block of code",
    /// failing initialization outright. Do not re-inline this await.
    /// </summary>
    private Task<ProcessRunner.ProcessResult> RunUnderPrebuildMutexAsync(
        Func<Task<ProcessRunner.ProcessResult>> work,
        string description = "dotnet pack (CLI, shared Release tree)") => Task.Run(() =>
    {
        // "Global\" so the mutex is visible across sessions, not just the current one.
        using var mutex = new Mutex(initiallyOwned: false, name: E2ECliPack.PrebuildMutexName);
        var held = false;
        try
        {
            try
            {
                held = mutex.WaitOne(SolutionBuildTimeout);
            }
            catch (AbandonedMutexException)
            {
                // A previous holder died mid-build. We now own the mutex; the work
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

            Log.Add($"[build] {description} (mutex held)");
            return work().GetAwaiter().GetResult();
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
