using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace BotNexus.Integration.ExtensionBoot.Tests;

/// <summary>
/// xUnit collection fixture for the extension-boot smoke gate (issue #2220).
///
/// Lifecycle:
///   1. Locate the repo and build the solution in Release (this also builds the
///      full extension set under src/extensions/*/bin/Release).
///   2. Provision a clean temp BOTNEXUS_HOME with a config that enables extension
///      loading and pins a free listen port.
///   3. Boot the gateway through the real CLI `gateway start --attached --skip-build`,
///      which runs the production <c>ServeCommand.DeployExtensions</c> path and then
///      loads every extension through the isolated ExtensionAssemblyLoadContext.
///   4. Wait for GET /health to return 200.
///
/// All initialization failures are captured (not thrown) so the tests can skip
/// gracefully with full diagnostics rather than surfacing opaque collection-fixture
/// exceptions. The whole point of the gate is to exercise the load path that
/// `botnexus validate`, unit tests, and the default/config/wildcard Docker boot
/// check never take.
/// </summary>
public sealed class ExtensionBootFixture : IAsyncLifetime
{
    private static readonly TimeSpan SolutionBuildTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan GatewayReadyTimeout = TimeSpan.FromMinutes(3);

    public string RepoRoot { get; private set; } = string.Empty;
    public string SandboxRoot { get; private set; } = string.Empty;
    public string Home { get; private set; } = string.Empty;
    public int GatewayPort { get; private set; }
    public string GatewayBaseUrl => $"http://127.0.0.1:{GatewayPort}";

    public bool Succeeded { get; private set; }
    public string? Error { get; private set; }
    public List<string> Log { get; } = new();

    private ProcessRunner.BackgroundProcess? _gateway;

    public async Task InitializeAsync()
    {
        try
        {
            RepoRoot = RepoLocator.FindRepoRoot();

            var runId = Guid.NewGuid().ToString("N");
            SandboxRoot = Path.Combine(Path.GetTempPath(), "botnexus-extboot", runId);
            Home = Path.Combine(SandboxRoot, "home");
            Directory.CreateDirectory(Home);

            // 1 - build the solution in Release. This produces both the gateway dll
            //     and the extension bin outputs that DeployExtensions copies. We pre-build
            //     (with --skip-build passed to the gateway) so MSBuild never fights the
            //     running testhost for locked, already-loaded dlls. /nodeReuse:false and
            //     UseSharedCompilation=false force MSBuild + Roslyn to exit cleanly so this
            //     subprocess returns control instead of leaving build nodes attached.
            Log.Add("[build] dotnet build BotNexus.slnx -c Release (prebuild)");
            var build = await ProcessRunner.RunAsync(
                "dotnet",
                "build BotNexus.slnx --configuration Release --nologo --tl:off /nodeReuse:false /p:UseSharedCompilation=false",
                workingDirectory: RepoRoot,
                environment: new Dictionary<string, string?> { ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0" },
                timeout: SolutionBuildTimeout);
            if (build.ExitCode != 0)
            {
                Error = $"Solution prebuild exit {build.ExitCode}.\n{build.Combined}";
                return;
            }

            var cliDll = Path.Combine(
                RepoRoot, "src", "gateway", "BotNexus.Cli", "bin", "Release", "net10.0", "BotNexus.Cli.dll");
            if (!File.Exists(cliDll))
            {
                Error = $"CLI dll not found after build at: {cliDll}";
                return;
            }

            // 2 - provision a config that enables extension loading and pins the port.
            //     Extensions default to {home}/extensions, which is exactly where
            //     DeployExtensions writes them. The gateway honours gateway.listenUrl
            //     over ASPNETCORE_URLS / --port, so we set it explicitly.
            GatewayPort = PickFreePort();
            Log.Add($"[gateway] picked port {GatewayPort}");
            WriteConfig(Path.Combine(Home, "config.json"), GatewayPort);

            // 3 - boot the gateway through the real CLI. --skip-build reuses the
            //     prebuild above; the CLI still runs DeployExtensions and the isolated
            //     ExtensionAssemblyLoadContext load path for the full extension set.
            var env = new Dictionary<string, string?> { ["BOTNEXUS_HOME"] = Home };
            _gateway = ProcessRunner.StartBackground(
                "dotnet",
                $"\"{cliDll}\" gateway start --attached --skip-build --source \"{RepoRoot}\" --target \"{Home}\" --port {GatewayPort}",
                environment: env);

            // 4 - wait for /health.
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
            // Best-effort cleanup - locked SQLite/extension dlls on Windows are not
            // worth failing the suite for.
        }
    }

    /// <summary>Snapshot of the gateway process output, for diagnostic assertions.</summary>
    public string GatewayOutput() =>
        _gateway is null ? string.Empty : _gateway.SnapshotCombined();

    private static void WriteConfig(string path, int port)
    {
        // Minimal config: enable extension loading (also the default) and pin the
        // listen URL to the chosen test port. No provider/agent is required to boot
        // and load extensions - the gate is about the assembly-load path, not the LLM.
        var json =
            "{\n" +
            "  \"gateway\": {\n" +
            $"    \"listenUrl\": \"http://127.0.0.1:{port}\",\n" +
            "    \"world\": { \"id\": \"extboot-world\", \"name\": \"Extension Boot World\" },\n" +
            "    \"extensions\": { \"enabled\": true }\n" +
            "  }\n" +
            "}\n";
        File.WriteAllText(path, json, Encoding.UTF8);
    }

    private static int PickFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static async Task<bool> WaitForGatewayReadyAsync(
        string baseUrl, TimeSpan timeout, ProcessRunner.BackgroundProcess process)
    {
        var uri = new Uri(baseUrl);
        var tcpReady = await TcpReadinessProbe.WaitForTcpReadyAsync(uri.Host, uri.Port, timeout / 2);
        if (!tcpReady || process.HasExited)
            return false;

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

[CollectionDefinition(ExtensionBootCollection.Name)]
public sealed class ExtensionBootCollection : ICollectionFixture<ExtensionBootFixture>
{
    public const string Name = "Extension boot smoke gate";
}
