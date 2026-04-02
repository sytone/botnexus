using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Diagnostics;
using BotNexus.Diagnostics.Checkups.Configuration;
using BotNexus.Diagnostics.Checkups.Connectivity;
using BotNexus.Diagnostics.Checkups.Extensions;
using BotNexus.Diagnostics.Checkups.Permissions;
using BotNexus.Diagnostics.Checkups.Resources;
using BotNexus.Diagnostics.Checkups.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace BotNexus.Tests.Unit.Tests;

[Collection("BotNexusHomeEnvVar")]
public sealed class DiagnosticsCheckupsTests
{
    [Fact]
    public async Task ConfigValidCheckup_ReturnsPass_ForValidConfigJson()
    {
        using var sandbox = new TempSandbox();
        File.WriteAllText(sandbox.ConfigPath, """{"BotNexus":{"Providers":{}}}""");

        var result = await new ConfigValidCheckup(sandbox.Paths).RunAsync();

        result.Status.Should().Be(CheckupStatus.Pass);
    }

    [Fact]
    public async Task ConfigValidCheckup_ReturnsFail_ForInvalidJson()
    {
        using var sandbox = new TempSandbox();
        File.WriteAllText(sandbox.ConfigPath, """{"BotNexus":""");

        var result = await new ConfigValidCheckup(sandbox.Paths).RunAsync();

        result.Status.Should().Be(CheckupStatus.Fail);
    }

    [Fact]
    public async Task AgentConfigCheckup_ReturnsPass_ForValidNamedAgents()
    {
        var config = CreateConfig();
        config.Agents.Named["default"] = new AgentConfig { Name = "Default", Provider = "openai" };

        var result = await new AgentConfigCheckup(Options.Create(config)).RunAsync();

        result.Status.Should().Be(CheckupStatus.Pass);
    }

    [Fact]
    public async Task AgentConfigCheckup_ReturnsFail_WhenRequiredFieldsMissing()
    {
        var config = CreateConfig();
        config.Agents.Named["broken"] = new AgentConfig { Name = "Broken", Provider = "" };

        var result = await new AgentConfigCheckup(Options.Create(config)).RunAsync();

        result.Status.Should().Be(CheckupStatus.Fail);
    }

    [Fact]
    public async Task ProviderConfigCheckup_ReturnsPass_ForValidProviderAuth()
    {
        var config = CreateConfig();
        config.Providers["openai"] = new ProviderConfig { Auth = "apikey", ApiKey = "1234567890abcdef" };
        config.Providers["copilot"] = new ProviderConfig { Auth = "oauth" };
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["BotNexus:Providers:copilot:OAuthClientId"] = "client-id"
        });

        var result = await new ProviderConfigCheckup(Options.Create(config), configuration).RunAsync();

        result.Status.Should().Be(CheckupStatus.Pass);
    }

    [Fact]
    public async Task ProviderConfigCheckup_ReturnsFail_WhenAuthFieldsMissing()
    {
        var config = CreateConfig();
        config.Providers["openai"] = new ProviderConfig { Auth = "apikey", ApiKey = "" };
        config.Providers["copilot"] = new ProviderConfig { Auth = "oauth" };

        var result = await new ProviderConfigCheckup(Options.Create(config), BuildConfiguration()).RunAsync();

        result.Status.Should().Be(CheckupStatus.Fail);
    }

    [Fact]
    public async Task ApiKeyStrengthCheckup_ReturnsPass_ForStrongKeys()
    {
        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Production");
        var config = CreateConfig();
        config.Providers["openai"] = new ProviderConfig { Auth = "apikey", ApiKey = "1234567890abcdef" };

        var result = await new ApiKeyStrengthCheckup(Options.Create(config)).RunAsync();

        result.Status.Should().Be(CheckupStatus.Pass);
    }

    [Fact]
    public async Task ApiKeyStrengthCheckup_ReturnsWarn_ForWeakKeys()
    {
        using var env = new EnvironmentVariableScope("DOTNET_ENVIRONMENT", "Production");
        var config = CreateConfig();
        config.Providers["openai"] = new ProviderConfig { Auth = "apikey", ApiKey = "short" };

        var result = await new ApiKeyStrengthCheckup(Options.Create(config)).RunAsync();

        result.Status.Should().Be(CheckupStatus.Warn);
    }

    [Fact]
    public async Task TokenPermissionsCheckup_ReturnsPass_ForLocalTokensDirectory()
    {
        using var sandbox = new TempSandbox();
        Directory.CreateDirectory(sandbox.Paths.TokensPath);

        var result = await new TokenPermissionsCheckup(sandbox.Paths).RunAsync();

        result.Status.Should().Be(CheckupStatus.Pass);
    }

    [Fact]
    public async Task TokenPermissionsCheckup_ReturnsWarn_WhenTokensDirectoryMissing()
    {
        using var sandbox = new TempSandbox();

        var result = await new TokenPermissionsCheckup(sandbox.Paths).RunAsync();

        result.Status.Should().Be(CheckupStatus.Warn);
    }

    [Fact]
    public async Task TokenPermissionsCheckup_ReturnsFail_WhenWorldReadIsAllowedOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var sandbox = new TempSandbox();
        Directory.CreateDirectory(sandbox.Paths.TokensPath);
        var directoryInfo = new DirectoryInfo(sandbox.Paths.TokensPath);
        var security = directoryInfo.GetAccessControl();
        var everyoneSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        security.AddAccessRule(new FileSystemAccessRule(
            everyoneSid,
            FileSystemRights.ReadData | FileSystemRights.ListDirectory,
            AccessControlType.Allow));
        directoryInfo.SetAccessControl(security);

        var result = await new TokenPermissionsCheckup(sandbox.Paths).RunAsync();

        result.Status.Should().Be(CheckupStatus.Fail);
    }

    [Fact]
    public async Task ExtensionSignedCheckup_ReturnsPass_WhenSigningNotRequired()
    {
        var config = CreateConfig();
        config.Extensions.RequireSignedAssemblies = false;

        var result = await new ExtensionSignedCheckup(Options.Create(config)).RunAsync();

        result.Status.Should().Be(CheckupStatus.Pass);
    }

    [Fact]
    public async Task ExtensionSignedCheckup_ReturnsWarn_WhenRequiredButNoLoadedExtensions()
    {
        using var sandbox = new TempSandbox();
        var config = CreateConfig();
        config.Extensions.RequireSignedAssemblies = true;
        config.ExtensionsPath = Path.Combine(sandbox.RootPath, "extensions");

        var result = await new ExtensionSignedCheckup(Options.Create(config)).RunAsync();

        result.Status.Should().Be(CheckupStatus.Warn);
    }

    [Fact]
    public async Task ExtensionSignedCheckup_ReturnsFail_WhenUnsignedAssemblyLoadedFromExtensionsPath()
    {
        using var sandbox = new TempSandbox();
        var extensionRoot = Path.Combine(sandbox.RootPath, "extensions");
        var providerPath = Path.Combine(extensionRoot, "providers", "openai");
        Directory.CreateDirectory(providerPath);
        File.Copy(typeof(DiagnosticsCheckupsTests).Assembly.Location, Path.Combine(providerPath, "UnsignedDiagnosticExtension.dll"), overwrite: true);

        var config = CreateConfig();
        config.Extensions.RequireSignedAssemblies = true;
        config.ExtensionsPath = extensionRoot;

        var result = await new ExtensionSignedCheckup(Options.Create(config)).RunAsync();

        result.Status.Should().Be(CheckupStatus.Fail);
    }

    [Fact]
    public async Task ProviderReachableCheckup_ReturnsPass_WhenProviderResponds()
    {
        var config = CreateConfig();
        config.Providers["openai"] = new ProviderConfig { ApiBase = "https://provider.local" };
        using var httpClient = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var checkup = new ProviderReachableCheckup(Options.Create(config), () => httpClient);

        var result = await checkup.RunAsync();

        result.Status.Should().Be(CheckupStatus.Pass);
    }

    [Fact]
    public async Task ProviderReachableCheckup_ReturnsFail_WhenProviderUnreachable()
    {
        var config = CreateConfig();
        config.Providers["openai"] = new ProviderConfig { ApiBase = "https://provider.local" };
        using var httpClient = CreateHttpClient(_ =>
            throw new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused)));
        var checkup = new ProviderReachableCheckup(Options.Create(config), () => httpClient);

        var result = await checkup.RunAsync();

        result.Status.Should().Be(CheckupStatus.Fail);
    }

    [Fact]
    public async Task PortAvailableCheckup_ReturnsPass_WhenPortIsAvailable()
    {
        var config = CreateConfig();
        config.Gateway.Port = GetFreeTcpPort();

        var result = await new PortAvailableCheckup(Options.Create(config)).RunAsync();

        result.Status.Should().Be(CheckupStatus.Pass);
    }

    [Fact]
    public async Task PortAvailableCheckup_ReturnsFail_WhenPortIsAlreadyInUseWithoutListener()
    {
        var port = GetFreeTcpPort();
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
        var config = CreateConfig();
        config.Gateway.Port = port;

        var result = await new PortAvailableCheckup(Options.Create(config)).RunAsync();

        result.Status.Should().Be(CheckupStatus.Fail);
    }

    [Fact]
    public async Task ExtensionsFolderExistsCheckup_ReturnsPass_WhenConfiguredFoldersExist()
    {
        using var sandbox = new TempSandbox();
        var config = CreateConfig();
        var extensionRoot = Path.Combine(sandbox.RootPath, "extensions");
        config.ExtensionsPath = extensionRoot;
        config.Providers["openai"] = new ProviderConfig();
        config.Channels.Instances["slack"] = new ChannelConfig { Enabled = true };
        config.Tools.Extensions["gh"] = new Dictionary<string, object>();
        Directory.CreateDirectory(Path.Combine(extensionRoot, "providers", "openai"));
        Directory.CreateDirectory(Path.Combine(extensionRoot, "channels", "slack"));
        Directory.CreateDirectory(Path.Combine(extensionRoot, "tools", "gh"));

        var result = await new ExtensionsFolderExistsCheckup(Options.Create(config)).RunAsync();

        result.Status.Should().Be(CheckupStatus.Pass);
    }

    [Fact]
    public async Task ExtensionsFolderExistsCheckup_ReturnsFail_WhenConfiguredFolderIsMissing()
    {
        using var sandbox = new TempSandbox();
        var config = CreateConfig();
        config.ExtensionsPath = Path.Combine(sandbox.RootPath, "extensions");
        config.Providers["openai"] = new ProviderConfig();

        var result = await new ExtensionsFolderExistsCheckup(Options.Create(config)).RunAsync();

        result.Status.Should().Be(CheckupStatus.Fail);
    }

    [Fact]
    public async Task ExtensionAssembliesValidCheckup_ReturnsPass_WhenDllsAreValidAssemblies()
    {
        using var sandbox = new TempSandbox();
        var extensionRoot = Path.Combine(sandbox.RootPath, "extensions");
        var providerPath = Path.Combine(extensionRoot, "providers", "openai");
        Directory.CreateDirectory(providerPath);
        File.Copy(typeof(DiagnosticsCheckupsTests).Assembly.Location, Path.Combine(providerPath, "Valid.dll"), overwrite: true);

        var config = CreateConfig();
        config.ExtensionsPath = extensionRoot;
        config.Providers["openai"] = new ProviderConfig();

        var result = await new ExtensionAssembliesValidCheckup(Options.Create(config)).RunAsync();

        result.Status.Should().Be(CheckupStatus.Pass);
    }

    [Fact]
    public async Task ExtensionAssembliesValidCheckup_ReturnsFail_WhenDllIsInvalid()
    {
        using var sandbox = new TempSandbox();
        var extensionRoot = Path.Combine(sandbox.RootPath, "extensions");
        var providerPath = Path.Combine(extensionRoot, "providers", "openai");
        Directory.CreateDirectory(providerPath);
        File.WriteAllText(Path.Combine(providerPath, "Invalid.dll"), "not-a-dotnet-assembly");

        var config = CreateConfig();
        config.ExtensionsPath = extensionRoot;
        config.Providers["openai"] = new ProviderConfig();

        var result = await new ExtensionAssembliesValidCheckup(Options.Create(config)).RunAsync();

        result.Status.Should().Be(CheckupStatus.Fail);
    }

    [Fact]
    public async Task HomeDirWritableCheckup_ReturnsPass_ForWritableDirectory()
    {
        using var sandbox = new TempSandbox();

        var result = await new HomeDirWritableCheckup(sandbox.Paths).RunAsync();

        result.Status.Should().Be(CheckupStatus.Pass);
    }

    [Fact]
    public async Task HomeDirWritableCheckup_ReturnsFail_ForNonWritablePath()
    {
        using var sandbox = new TempSandbox();
        var blockedPath = Path.Combine(sandbox.RootPath, "blocked-home");
        File.WriteAllText(blockedPath, "file-blocker");
        var paths = new DiagnosticsPaths(blockedPath, sandbox.ConfigPath);

        var result = await new HomeDirWritableCheckup(paths).RunAsync();

        result.Status.Should().Be(CheckupStatus.Fail);
    }

    [Fact]
    public async Task LogDirWritableCheckup_ReturnsPass_ForWritableDirectory()
    {
        using var sandbox = new TempSandbox();

        var result = await new LogDirWritableCheckup(sandbox.Paths).RunAsync();

        result.Status.Should().Be(CheckupStatus.Pass);
    }

    [Fact]
    public async Task LogDirWritableCheckup_ReturnsFail_ForMissingDrivePath()
    {
        var missingDrivePath = GetMissingDriveRootPath();
        var paths = new DiagnosticsPaths(missingDrivePath, Path.Combine(missingDrivePath, "config.json"));

        var result = await new LogDirWritableCheckup(paths).RunAsync();

        result.Status.Should().Be(CheckupStatus.Fail);
    }

    [Fact]
    public async Task DiskSpaceCheckup_ReturnsPass_WhenSpaceIsSufficient()
    {
        using var sandbox = new TempSandbox();
        var checkup = new DiskSpaceCheckup(sandbox.Paths, _ => 600L * 1024 * 1024);

        var result = await checkup.RunAsync();

        result.Status.Should().Be(CheckupStatus.Pass);
    }

    [Fact]
    public async Task DiskSpaceCheckup_ReturnsWarn_WhenSpaceIsLow()
    {
        using var sandbox = new TempSandbox();
        var checkup = new DiskSpaceCheckup(sandbox.Paths, _ => 200L * 1024 * 1024);

        var result = await checkup.RunAsync();

        result.Status.Should().Be(CheckupStatus.Warn);
    }

    [Fact]
    public async Task DiskSpaceCheckup_ReturnsFail_WhenSpaceIsCritical()
    {
        using var sandbox = new TempSandbox();
        var checkup = new DiskSpaceCheckup(sandbox.Paths, _ => 50L * 1024 * 1024);

        var result = await checkup.RunAsync();

        result.Status.Should().Be(CheckupStatus.Fail);
    }

    [Fact]
    public async Task CheckupRunner_FiltersByCategory_AndCollectsResults()
    {
        var runner = new CheckupRunner(
        [
            new TestCheckup("A", "Configuration", CheckupStatus.Pass),
            new TestCheckup("B", "Security", CheckupStatus.Warn),
            new TestCheckup("C", "configuration", CheckupStatus.Fail)
        ]);

        var results = await runner.RunAllAsync("CONFIGURATION");

        results.Should().HaveCount(2);
        results.Select(r => r.Status).Should().ContainInOrder(CheckupStatus.Pass, CheckupStatus.Fail);
    }

    [Fact]
    public async Task CheckupRunner_ExecutesSequentially()
    {
        var order = new List<string>();
        var runner = new CheckupRunner(
        [
            new TestCheckup("First", "Configuration", CheckupStatus.Pass, order),
            new TestCheckup("Second", "Configuration", CheckupStatus.Pass, order)
        ]);

        _ = await runner.RunAllAsync();

        order.Should().Equal("First", "Second");
    }

    private static BotNexusConfig CreateConfig() => new();

    private static IConfiguration BuildConfiguration(Dictionary<string, string?>? values = null)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(values ?? new Dictionary<string, string?>()).Build();
    }

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        return new HttpClient(new TestHttpMessageHandler(request => Task.FromResult(responder(request))));
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string GetMissingDriveRootPath()
    {
        if (!OperatingSystem.IsWindows())
            return Path.Combine(Path.GetTempPath(), $"missing-root-{Guid.NewGuid():N}");

        var used = DriveInfo.GetDrives()
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .ToHashSet();
        var missingLetter = Enumerable.Range('D', 'Z' - 'D' + 1)
            .Select(i => (char)i)
            .First(c => !used.Contains(c));
        return $"{missingLetter}:\\botnexus-missing-{Guid.NewGuid():N}";
    }

    private sealed class TempSandbox : IDisposable
    {
        private readonly string? _originalHome;
        public TempSandbox()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"botnexus-diagnostics-tests-{Guid.NewGuid():N}");
            HomePath = Path.Combine(RootPath, "home");
            ConfigPath = Path.Combine(HomePath, "config.json");
            Directory.CreateDirectory(HomePath);
            _originalHome = Environment.GetEnvironmentVariable("BOTNEXUS_HOME");
            Environment.SetEnvironmentVariable("BOTNEXUS_HOME", HomePath);
            Paths = new DiagnosticsPaths(HomePath, ConfigPath);
        }

        public string RootPath { get; }
        public string HomePath { get; }
        public string ConfigPath { get; }
        public DiagnosticsPaths Paths { get; }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("BOTNEXUS_HOME", _originalHome);
            try
            {
                if (Directory.Exists(RootPath))
                    Directory.Delete(RootPath, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _originalValue;

        public EnvironmentVariableScope(string name, string value)
        {
            _name = name;
            _originalValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _originalValue);
    }

    private sealed class TestHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _responder(request);
    }

    private sealed class TestCheckup(
        string name,
        string category,
        CheckupStatus status,
        List<string>? order = null) : IHealthCheckup
    {
        public string Name { get; } = name;
        public string Category { get; } = category;
        public string Description => "test";

        public Task<CheckupResult> RunAsync(CancellationToken ct = default)
        {
            order?.Add(Name);
            return Task.FromResult(new CheckupResult(status, Name));
        }
    }
}
