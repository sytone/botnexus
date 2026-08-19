using BotNexus.Extensions.Mcp.Plugins;

namespace BotNexus.Extensions.Mcp.Tests.Plugins;

/// <summary>
/// Pins the four acceptance criteria of #2686: plugin-declared MCP servers register with the
/// existing manager (AC1), under a plugin-scoped name so two plugins cannot collide (AC2), removal
/// unregisters exactly that plugin's servers (AC3), and an untrusted plugin under Enforce registers
/// nothing (AC4).
/// </summary>
/// <remarks>
/// The load-bearing assertions are the negative ones. AC2 is only meaningful if BOTH colliding
/// servers survive - asserting one registered would pass for an implementation that silently
/// overwrote. AC3 is only meaningful if the OTHER plugin's servers are still running afterwards.
/// AC4 is only meaningful if the server was never started, not merely stopped again.
/// </remarks>
public sealed class PluginMcpServerRegistrarTests : IDisposable
{
    private readonly string _root;

    public PluginMcpServerRegistrarTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "botnexus-plugin-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test over.
        }
    }

    private string CreatePlugin(string name, string declarationJson, string relativePath = ".botnexus-plugin/mcp.json")
    {
        var dir = Path.Combine(_root, name);
        var file = Path.Combine(dir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, declarationJson);
        return dir;
    }

    private const string GithubServer = """
        { "mcpServers": { "github": { "command": "node", "args": ["github.js"] } } }
        """;

    // ---- AC1: declared servers are registered with the existing manager ----

    [Fact]
    public async Task AC1_DeclaredServers_AreRegisteredWithTheServerManager()
    {
        var host = new RecordingMcpServerHost();
        var registrar = new PluginMcpServerRegistrar(host);
        var dir = CreatePlugin("alpha", GithubServer);

        var result = await registrar.RegisterAsync("alpha", dir);

        result.Registered.ShouldBeTrue();
        result.ScopedServerNames.Count.ShouldBe(1);
        host.Started.Count.ShouldBe(1, "the declared server must reach the existing server manager");
    }

    [Fact]
    public async Task AC1_ServerConfiguration_IsPassedThroughUnchanged()
    {
        var host = new CapturingHost();
        var registrar = new PluginMcpServerRegistrar(host);
        var dir = CreatePlugin("alpha", """
            { "mcpServers": { "github": { "command": "node", "args": ["github.js"], "initTimeoutMs": 1234 } } }
            """);

        await registrar.RegisterAsync("alpha", dir);

        var config = host.Configs.Single().Value;
        config.Command.ShouldBe("node");
        config.Args.ShouldNotBeNull().ShouldContain("github.js");
        config.InitTimeoutMs.ShouldBe(1234, "a plugin's declared timeout must not be silently replaced");
    }

    [Fact]
    public async Task AC1_PluginWithNoDeclaration_RegistersNothingAndIsNotAnError()
    {
        var host = new RecordingMcpServerHost();
        var registrar = new PluginMcpServerRegistrar(host);
        var dir = Path.Combine(_root, "bare");
        Directory.CreateDirectory(dir);

        var result = await registrar.RegisterAsync("bare", dir);

        result.Registered.ShouldBeTrue();
        result.ScopedServerNames.ShouldBeEmpty();
        host.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task AC1_ExplicitManifestPath_IsHonouredOverConvention()
    {
        var host = new RecordingMcpServerHost();
        var registrar = new PluginMcpServerRegistrar(host);
        var dir = CreatePlugin("alpha", GithubServer, "config/servers.json");

        var result = await registrar.RegisterAsync("alpha", dir, declaredPath: "config/servers.json");

        result.ScopedServerNames.ShouldBe(["plugin:alpha:github"]);
    }

    [Fact]
    public async Task AC1_ManifestPathEscapingThePluginDirectory_IsRefused()
    {
        var host = new RecordingMcpServerHost();
        var registrar = new PluginMcpServerRegistrar(host);
        var dir = CreatePlugin("alpha", GithubServer);

        var result = await registrar.RegisterAsync("alpha", dir, declaredPath: "../../etc/servers.json");

        result.Registered.ShouldBeFalse("a manifest must not be able to point the loader outside the plugin");
        host.Started.ShouldBeEmpty();
    }

    // ---- AC2: the registered name is scoped by plugin identity ----

    [Fact]
    public async Task AC2_TwoPluginsDeclaringTheSameServerName_BothResolve()
    {
        var host = new RecordingMcpServerHost();
        var registrar = new PluginMcpServerRegistrar(host);
        var alpha = CreatePlugin("alpha", GithubServer);
        var beta = CreatePlugin("beta", GithubServer);

        var alphaResult = await registrar.RegisterAsync("alpha", alpha);
        var betaResult = await registrar.RegisterAsync("beta", beta);

        alphaResult.ScopedServerNames.ShouldBe(["plugin:alpha:github"]);
        betaResult.ScopedServerNames.ShouldBe(["plugin:beta:github"]);

        // The negative half: BOTH are running. An implementation that let the second overwrite the
        // first would still satisfy the two assertions above.
        host.Running.ShouldBe(["plugin:alpha:github", "plugin:beta:github"], ignoreOrder: true);
        host.Started.Count.ShouldBe(2);
    }

    [Fact]
    public void AC2_ScopedName_RoundTripsBackToItsPluginAndServer()
    {
        var scoped = PluginScopedServerName.Scope("alpha", "github");

        PluginScopedServerName.TryParse(scoped, out var plugin, out var server).ShouldBeTrue();
        plugin.ShouldBe("alpha");
        server.ShouldBe("github");
    }

    [Fact]
    public void AC2_AnUnscopedUserConfiguredServerId_IsNotClaimedByAnyPlugin()
    {
        PluginScopedServerName.TryParse("github", out _, out _).ShouldBeFalse();
        PluginScopedServerName.TryParse("plugin:alpha", out _, out _).ShouldBeFalse();
        PluginScopedServerName.BelongsTo("github", "alpha").ShouldBeFalse();
        PluginScopedServerName.BelongsTo("plugin:beta:github", "alpha").ShouldBeFalse();
    }

    // ---- AC3: removing a plugin unregisters its servers ----

    [Fact]
    public async Task AC3_Unregister_StopsOnlyTheRemovedPluginsServers()
    {
        var host = new RecordingMcpServerHost();
        var registrar = new PluginMcpServerRegistrar(host);
        await registrar.RegisterAsync("alpha", CreatePlugin("alpha", GithubServer));
        await registrar.RegisterAsync("beta", CreatePlugin("beta", GithubServer));

        var removed = await registrar.UnregisterAsync("alpha");

        removed.ShouldBe(["plugin:alpha:github"]);
        host.Stopped.ShouldBe(["plugin:alpha:github"]);

        // The negative half: the other plugin's identically-named server survives.
        host.Running.ShouldBe(["plugin:beta:github"]);
        registrar.GetRegisteredServerNames("alpha").ShouldBeEmpty();
        registrar.GetRegisteredServerNames("beta").ShouldBe(["plugin:beta:github"]);
    }

    [Fact]
    public async Task AC3_UnregisteringAnUnknownPlugin_StopsNothing()
    {
        var host = new RecordingMcpServerHost();
        var registrar = new PluginMcpServerRegistrar(host);
        await registrar.RegisterAsync("alpha", CreatePlugin("alpha", GithubServer));

        var removed = await registrar.UnregisterAsync("never-installed");

        removed.ShouldBeEmpty();
        host.Stopped.ShouldBeEmpty();
        host.Running.ShouldBe(["plugin:alpha:github"]);
    }

    // ---- AC4: an untrusted plugin under Enforce is not registered ----

    [Fact]
    public async Task AC4_UntrustedPluginUnderEnforce_RegistersNothing()
    {
        var host = new RecordingMcpServerHost();
        var trust = new StubPluginTrustEvaluator(PluginTrustMode.Enforce, trusted: false);
        var registrar = new PluginMcpServerRegistrar(host, trust);

        var result = await registrar.RegisterAsync("alpha", CreatePlugin("alpha", GithubServer));

        result.Registered.ShouldBeFalse();
        result.SkippedReason.ShouldNotBeNull().ShouldContain("content hash mismatch");
        result.ScopedServerNames.ShouldBeEmpty();

        // The load-bearing assertion: the server was never STARTED, not started-then-stopped. An
        // MCP server start is a process spawn or a credentialled outbound connection.
        host.Started.ShouldBeEmpty();
        host.Running.ShouldBeEmpty();
    }

    [Fact]
    public async Task AC4_UntrustedPluginUnderWarn_IsStillRegistered()
    {
        var host = new RecordingMcpServerHost();
        var trust = new StubPluginTrustEvaluator(PluginTrustMode.Warn, trusted: false);
        var registrar = new PluginMcpServerRegistrar(host, trust);

        var result = await registrar.RegisterAsync("alpha", CreatePlugin("alpha", GithubServer));

        result.Registered.ShouldBeTrue("Warn logs but permits — otherwise Warn and Enforce are the same mode");
        host.Running.ShouldBe(["plugin:alpha:github"]);
    }

    [Fact]
    public async Task AC4_TrustedPluginUnderEnforce_IsRegistered()
    {
        var host = new RecordingMcpServerHost();
        var trust = new StubPluginTrustEvaluator(PluginTrustMode.Enforce, trusted: true);
        var registrar = new PluginMcpServerRegistrar(host, trust);

        var result = await registrar.RegisterAsync("alpha", CreatePlugin("alpha", GithubServer));

        result.Registered.ShouldBeTrue("Enforce must block only the untrusted case");
        host.Running.ShouldBe(["plugin:alpha:github"]);
        trust.Evaluated.ShouldBe(["alpha"]);
    }

    [Fact]
    public async Task AC4_TrustIsEvaluatedBeforeTheDeclarationIsEvenRead()
    {
        // A plugin whose declaration is unreadable must still be refused for the TRUST reason under
        // Enforce, proving the trust gate sits in front of the read rather than beside it.
        var host = new RecordingMcpServerHost();
        var trust = new StubPluginTrustEvaluator(PluginTrustMode.Enforce, trusted: false);
        var registrar = new PluginMcpServerRegistrar(host, trust);
        var dir = CreatePlugin("alpha", "{ this is not json ");

        var result = await registrar.RegisterAsync("alpha", dir);

        result.SkippedReason.ShouldNotBeNull().ShouldContain("content hash mismatch");
    }

    private sealed class CapturingHost : IMcpServerHost
    {
        public Dictionary<string, McpServerConfig> Configs { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<Agent.Core.Tools.IAgentTool>> StartServerAsync(
            string serverId,
            McpServerConfig serverConfig,
            bool useToolPrefix,
            CancellationToken cancellationToken = default)
        {
            Configs[serverId] = serverConfig;
            return Task.FromResult<IReadOnlyList<Agent.Core.Tools.IAgentTool>>([]);
        }

        public Task<IReadOnlyList<string>> StopServersAsync(
            Func<string, bool> predicate,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
