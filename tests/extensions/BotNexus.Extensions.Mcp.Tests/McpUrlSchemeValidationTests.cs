using System.Net;
using BotNexus.Extensions.Mcp.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Extensions.Mcp.Tests;

/// <summary>
/// Issue #3012 — a resolved BotNexus provider API key is injected as
/// <c>Authorization: Bearer &lt;token&gt;</c> into an MCP transport whose URL is a free-form
/// per-agent config string. These tests pin the single scheme-validation seam that stops the
/// credential leaving the process over a non-TLS, non-loopback transport, and the
/// redirect posture that stops it being replayed to a different host.
/// </summary>
public sealed class McpUrlSchemeValidationTests
{
    // ---------------------------------------------------------------------
    // McpUrlSecurity — the single helper (AC5)
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("https://api.example.com/mcp")]
    [InlineData("https://api.githubcopilot.com/mcp/")]
    public void TryValidate_AllowsHttps_WhenCarryingCredentials(string url)
    {
        var ok = McpUrlSecurity.TryValidate(url, carriesCredentials: true, out var endpoint, out var error);

        ok.ShouldBeTrue();
        endpoint.ShouldNotBeNull();
        endpoint!.Scheme.ShouldBe(Uri.UriSchemeHttps);
        error.ShouldBeNull();
    }

    [Theory]
    [InlineData("http://127.0.0.1:3000/mcp")]
    [InlineData("http://localhost:3000/mcp")]
    [InlineData("http://[::1]:3000/mcp")]
    public void TryValidate_AllowsLoopbackHttp_WhenCarryingCredentials(string url)
    {
        var ok = McpUrlSecurity.TryValidate(url, carriesCredentials: true, out var endpoint, out var error);

        ok.ShouldBeTrue();
        endpoint.ShouldNotBeNull();
        endpoint!.IsLoopback.ShouldBeTrue();
        error.ShouldBeNull();
    }

    [Theory]
    [InlineData("http://api.example.com/mcp")]
    [InlineData("http://10.0.0.5:3000/mcp")]
    [InlineData("http://evil.invalid/mcp")]
    public void TryValidate_RejectsNonLoopbackHttp_WhenCarryingCredentials(string url)
    {
        var ok = McpUrlSecurity.TryValidate(url, carriesCredentials: true, out var endpoint, out var error);

        ok.ShouldBeFalse();
        endpoint.ShouldBeNull();
        error.ShouldNotBeNull();
        error!.ShouldContain("https is required");
    }

    [Fact]
    public void TryValidate_AllowsNonLoopbackHttp_WhenNoCredentialsAreCarried()
    {
        // The rule is scoped to credential disclosure. An unauthenticated plaintext MCP server
        // is unchanged behaviour, so this fix cannot break existing non-auth deployments.
        var ok = McpUrlSecurity.TryValidate(
            "http://api.example.com/mcp", carriesCredentials: false, out var endpoint, out var error);

        ok.ShouldBeTrue();
        endpoint.ShouldNotBeNull();
        error.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not-a-url")]
    [InlineData("/relative/mcp")]
    public void TryValidate_RejectsEmptyOrNonAbsoluteUrls(string? url)
    {
        McpUrlSecurity.TryValidate(url, carriesCredentials: true, out var endpoint, out var error)
            .ShouldBeFalse();
        endpoint.ShouldBeNull();
        error.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("ftp://example.com/mcp")]
    [InlineData("file:///tmp/mcp")]
    public void TryValidate_RejectsNonHttpSchemes(string url)
    {
        McpUrlSecurity.TryValidate(url, carriesCredentials: false, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
        error!.ShouldContain("not supported");
    }

    [Fact]
    public void HeadersCarryCredentials_DetectsAuthorization_CaseInsensitively()
    {
        McpUrlSecurity.HeadersCarryCredentials(
            new Dictionary<string, string> { ["authorization"] = "Bearer x" }).ShouldBeTrue();
        McpUrlSecurity.HeadersCarryCredentials(
            new Dictionary<string, string> { ["AUTHORIZATION"] = "Bearer x" }).ShouldBeTrue();
        McpUrlSecurity.HeadersCarryCredentials(
            new Dictionary<string, string> { ["X-Api-Key"] = "x" }).ShouldBeFalse();
        McpUrlSecurity.HeadersCarryCredentials(null).ShouldBeFalse();
    }

    // ---------------------------------------------------------------------
    // McpServerManager.CreateTransport consumes the same seam
    // ---------------------------------------------------------------------

    [Fact]
    public void CreateTransport_ReturnsNull_ForPlaintextUrlWithAuthorizationHeader()
    {
        var config = new McpServerConfig
        {
            Url = "http://api.example.com/mcp",
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer leaked-token" },
        };

        McpServerManager.CreateTransport(config).ShouldBeNull();
    }

    [Fact]
    public void CreateTransport_ReturnsNull_ForPlaintextUrlWithAuthProviderReference()
    {
        var config = new McpServerConfig { Url = "http://api.example.com/mcp", Auth = "github-copilot" };

        McpServerManager.CreateTransport(config).ShouldBeNull();
    }

    [Fact]
    public void TryCreateTransport_ReportsReason_ForPlaintextCredentialedUrl()
    {
        var config = new McpServerConfig { Url = "http://api.example.com/mcp", Auth = "github-copilot" };

        McpServerManager.TryCreateTransport(config, out var transport, out var error).ShouldBeFalse();
        transport.ShouldBeNull();
        error.ShouldNotBeNull();
        error!.ShouldContain("api.example.com");
    }

    [Fact]
    public void CreateTransport_AllowsHttpsUrlWithAuthorizationHeader()
    {
        var config = new McpServerConfig
        {
            Url = "https://api.example.com/mcp",
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer token" },
        };

        McpServerManager.CreateTransport(config).ShouldBeOfType<HttpSseMcpTransport>();
    }

    [Fact]
    public void CreateTransport_AllowsLoopbackHttpUrlWithAuthorizationHeader()
    {
        var config = new McpServerConfig
        {
            Url = "http://127.0.0.1:3000/mcp",
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer token" },
        };

        McpServerManager.CreateTransport(config).ShouldBeOfType<HttpSseMcpTransport>();
    }

    [Fact]
    public void CreateTransport_StillAllowsPlaintextUrlWithoutCredentials()
    {
        var config = new McpServerConfig { Url = "http://api.example.com/mcp" };

        McpServerManager.CreateTransport(config).ShouldBeOfType<HttpSseMcpTransport>();
    }

    [Fact]
    public async Task StartSingleServerAsync_SkipsServer_AndWarnsNamingServerId_ForPlaintextCredentialedUrl()
    {
        var logger = new CapturingLogger();
        await using var manager = new McpServerManager(logger);

        var tools = await manager.StartSingleServerAsync(
            "leaky-server",
            new McpServerConfig
            {
                Url = "http://api.example.com/mcp",
                Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer leaked-token" },
            });

        tools.ShouldBeEmpty();
        logger.Warnings.ShouldContain(w => w.Contains("leaky-server") && w.Contains("insecure"));
    }

    // ---------------------------------------------------------------------
    // AC1 / AC2 / AC3 — McpToolContributor end to end
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ContributeAsync_PlaintextAuthServer_IsSkipped_AndTokenIsNeverResolved()
    {
        var tokenCalls = 0;
        var config = BuildConfig("leaky-server", "http://api.example.com/mcp", "github-copilot");
        var context = BuildContext(BuildDescriptor(config), (_, _) =>
        {
            tokenCalls++;
            return Task.FromResult<string?>("live-provider-key");
        });

        var contribution = await new McpToolContributor(NullLoggerFactory.Instance)
            .ContributeAsync(context, CancellationToken.None);

        // AC1: the server contributes no tools...
        contribution.Tools.ShouldBeEmpty();
        // ...and the credential is never even resolved for it.
        tokenCalls.ShouldBe(0);
    }

    [Fact]
    public async Task ContributeAsync_PlaintextAuthServer_LogsWarningNamingServerId()
    {
        var logger = new CapturingLogger();
        var config = BuildConfig("leaky-server", "http://api.example.com/mcp", "github-copilot");
        var context = BuildContext(BuildDescriptor(config), (_, _) => Task.FromResult<string?>("k"));

        await new McpToolContributor(new CapturingLoggerFactory(logger))
            .ContributeAsync(context, CancellationToken.None);

        logger.Warnings.ShouldContain(w => w.Contains("leaky-server"));
    }

    [Fact]
    public async Task ContributeAsync_HttpsAuthServer_StillResolvesTheProviderKey()
    {
        // AC2: https behaviour is byte-identical — the token is resolved and injection proceeds.
        string? resolvedProvider = null;
        var config = BuildConfig("secure-server", "https://example-mcp.invalid/mcp", "github-copilot");
        var context = BuildContext(BuildDescriptor(config), (p, _) =>
        {
            resolvedProvider = p;
            return Task.FromResult<string?>("test-bearer-token");
        });

        await new McpToolContributor(NullLoggerFactory.Instance)
            .ContributeAsync(context, CancellationToken.None);

        resolvedProvider.ShouldBe("github-copilot");
    }

    [Theory]
    [InlineData("http://127.0.0.1:3000/mcp")]
    [InlineData("http://localhost:3000/mcp")]
    public async Task ContributeAsync_LoopbackAuthServer_IsPermitted(string url)
    {
        // AC3: the developer-affordance carve-out. The token IS resolved for loopback.
        string? resolvedProvider = null;
        var config = BuildConfig("local-server", url, "github-copilot");
        var context = BuildContext(BuildDescriptor(config), (p, _) =>
        {
            resolvedProvider = p;
            return Task.FromResult<string?>("test-bearer-token");
        });

        await new McpToolContributor(NullLoggerFactory.Instance)
            .ContributeAsync(context, CancellationToken.None);

        resolvedProvider.ShouldBe("github-copilot");
    }

    // ---------------------------------------------------------------------
    // AC4 — the internally-created HttpClient must not replay Authorization
    // ---------------------------------------------------------------------

    [Fact]
    public void DefaultHandler_DisablesAutoRedirect()
    {
        using var handler = HttpSseMcpTransport.CreateDefaultHandler();

        handler.AllowAutoRedirect.ShouldBeFalse();
    }

    [Fact]
    public async Task DefaultHandler_DoesNotReplayAuthorizationHeaderToRedirectTarget()
    {
        // Behavioural proof, not just a property assertion: stand up two real loopback listeners
        // on different hosts. The first 302s to the second; with the framework default
        // (AllowAutoRedirect = true) the handler would follow it and resend Authorization.
        using var origin = new HttpListener();
        using var target = new HttpListener();
        var originPort = GetFreePort();
        var targetPort = GetFreePort();
        origin.Prefixes.Add($"http://127.0.0.1:{originPort}/");
        target.Prefixes.Add($"http://localhost:{targetPort}/");

        try
        {
            origin.Start();
            target.Start();
        }
        catch (HttpListenerException)
        {
            return; // No permission to bind — environment limitation, not a contract failure.
        }

        string? replayedAuthorization = null;
        var targetHit = false;

        var originLoop = Task.Run(async () =>
        {
            var ctx = await origin.GetContextAsync();
            ctx.Response.StatusCode = 302;
            ctx.Response.Headers["Location"] = $"http://localhost:{targetPort}/mcp";
            ctx.Response.Close();
        });

        var targetLoop = Task.Run(async () =>
        {
            var ctx = await target.GetContextAsync();
            targetHit = true;
            replayedAuthorization = ctx.Request.Headers["Authorization"];
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        });

        using var client = new HttpClient(HttpSseMcpTransport.CreateDefaultHandler(), disposeHandler: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{originPort}/mcp");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer super-secret-provider-key");

        var response = await client.SendAsync(request);

        // The redirect is surfaced, not followed.
        response.StatusCode.ShouldBe(HttpStatusCode.Found);

        await originLoop;
        // Give any (incorrect) follow-up request a chance to land before asserting it did not.
        var raced = await Task.WhenAny(targetLoop, Task.Delay(TimeSpan.FromSeconds(2)));

        origin.Stop();
        target.Stop();

        raced.ShouldNotBe((Task)targetLoop, "the redirect target must never be contacted");
        targetHit.ShouldBeFalse();
        replayedAuthorization.ShouldBeNull();
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static McpExtensionConfig BuildConfig(string id, string url, string auth)
        => new()
        {
            Servers = new Dictionary<string, McpServerConfig>
            {
                [id] = new McpServerConfig { Url = url, Auth = auth },
            },
        };

    private static AgentDescriptor BuildDescriptor(McpExtensionConfig config)
    {
        var element = JsonSerializer.SerializeToElement(config, JsonContext.Default.McpExtensionConfig);
        return new AgentDescriptor
        {
            AgentId = AgentId.From("test-agent"),
            DisplayName = "Test Agent",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            ExtensionConfig = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["botnexus-mcp"] = element,
            },
        };
    }

    private static AgentToolContributionContext BuildContext(
        AgentDescriptor descriptor,
        Func<string, CancellationToken, Task<string?>> getApiKey)
        => new(
            descriptor,
            new AgentExecutionContext { SessionId = SessionId.Create() },
            Path.GetTempPath(),
            new AllowAllPathValidator(),
            null,
            getApiKey);

    private sealed class AllowAllPathValidator : IPathValidator
    {
        public bool CanRead(string absolutePath) => true;
        public bool CanWrite(string absolutePath) => true;
        public string? ValidateAndResolve(string rawPath, FileAccessMode mode) => rawPath;
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    private sealed class CapturingLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => logger;
        public void Dispose() { }
    }
}
