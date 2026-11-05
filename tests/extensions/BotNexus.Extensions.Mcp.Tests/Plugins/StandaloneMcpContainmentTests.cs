using BotNexus.Extensions.Mcp.Plugins;
using BotNexus.Extensions.Plugins.Lifecycle;

namespace BotNexus.Extensions.Mcp.Tests.Plugins;

public sealed class StandaloneMcpContainmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "standalone-containment", Guid.NewGuid().ToString("N"));
    private string Installed => Path.Combine(_root, "installed");
    private string Standalone => Path.Combine(_root, "standalone");
    private readonly RecordingMcpServerHost _host = new();
    private readonly CountingTrust _trust = new();
    private readonly CountingReader _reader = new();

    [Theory]
    [InlineData("Claude", "valid")]
    [InlineData("Legacy", "valid")]
    [InlineData("Native", "valid")]
    [InlineData("Claude", "missing")]
    [InlineData("Legacy", "null")]
    [InlineData("Native", "blank")]
    [InlineData("Native", "locked")]
    [InlineData("Claude", "directory")]
    public async Task InstalledDescendants_CannotBeReRootedAsStandaloneRegardlessState(string format, string state)
    {
        var store = new PluginStateStore(Installed);
        var owner = Path.Combine(Installed, "foreign");
        var payload = Native(Path.Combine(owner, "payload"));
        if (format == "Claude")
        {
            Directory.CreateDirectory(Path.Combine(owner, ".claude-plugin"));
            File.WriteAllText(Path.Combine(owner, ".claude-plugin", "plugin.json"), "{}");
        }
        if (format == "Legacy") File.WriteAllText(Path.Combine(owner, "plugin.json"), "{}");
        store.Write([new InstalledPlugin { Name = "foreign", Source = "https://example.invalid/repo.git", ResolvedVersion = "v1",
            InstalledAtUtc = DateTimeOffset.UtcNow, ManifestFormat = Enum.Parse<PluginManifestFormat>(format),
            MarketplacePluginName = format == "Native" ? "foreign" : null }]);
        FileStream? locked = null;
        try
        {
            switch (state)
            {
                case "missing": File.Delete(store.StatePath); break;
                case "null": File.WriteAllText(store.StatePath, "null"); break;
                case "blank": File.WriteAllText(store.StatePath, " "); break;
                case "locked": locked = new FileStream(store.StatePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); break;
                case "directory": File.Delete(store.StatePath); Directory.CreateDirectory(store.StatePath); break;
            }
            var authority = Authority(store);
            Should.Throw<InvalidDataException>(() => NativePluginMcpAdmission.Admit(payload, authority));
            Should.Throw<InvalidDataException>(() => NativePluginMcpAdmission.Admit(owner, authority));
            Should.Throw<InvalidDataException>(() => new NativePluginMcpAuthority(store, owner));
            NoExecution();
        }
        finally { locked?.Dispose(); }
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("claude")]
    [InlineData("native-owner")]
    [InlineData("installed-state")]
    public async Task ForeignOrInstalledAncestry_RejectsBothFreshAndStaleAdmission(string envelope)
    {
        var store = new PluginStateStore(Installed);
        var directory = Native(Path.Combine(Standalone, "payload"));
        var authority = Authority(store);
        var admission = NativePluginMcpAdmission.Admit(directory, authority);
        switch (envelope)
        {
            case "legacy": File.WriteAllText(Path.Combine(_root, "plugin.json"), "{}"); break;
            case "claude": Directory.CreateDirectory(Path.Combine(_root, ".claude-plugin")); break;
            case "native-owner": Directory.CreateDirectory(Path.Combine(_root, ".botnexus-plugin")); break;
            case "installed-state": File.WriteAllText(Path.Combine(_root, PluginStateStore.StateFileName), "null"); break;
        }
        Should.Throw<InvalidDataException>(() => NativePluginMcpAdmission.Admit(directory, authority));
        (await Registrar(store, authority).RegisterNativeAsync(admission)).Registered.ShouldBeFalse();
        NoExecution();
    }

    [Fact]
    public async Task StaleAdmission_MovedUnderInstalledOwnershipCannotExecute()
    {
        var store = new PluginStateStore(Installed);
        var original = Native(Path.Combine(Standalone, "payload"));
        var authority = Authority(store);
        var admission = NativePluginMcpAdmission.Admit(original, authority);
        var destination = Path.Combine(Installed, "foreign", "payload");
        Directory.CreateDirectory(Path.GetDirectoryName(destination).ShouldNotBeNull());
        Directory.Move(original, destination);
        (await Registrar(store, authority).RegisterNativeAsync(admission)).Registered.ShouldBeFalse();
        Should.Throw<InvalidDataException>(() => NativePluginMcpAdmission.Admit(destination, authority));
        NoExecution();
    }

    [Theory]
    [InlineData("none")]
    [InlineData("different-authority")]
    [InlineData("different-installed-root")]
    public async Task Admission_RequiresTheConfiguredAuthorityAndInstalledBoundary(string mismatch)
    {
        var store = new PluginStateStore(Installed);
        var directory = Native(Path.Combine(Standalone, "payload"));
        var authority = Authority(store);
        var admission = NativePluginMcpAdmission.Admit(directory, authority);
        NativePluginMcpAuthority? configured = mismatch == "none" ? null : mismatch == "different-authority"
            ? new NativePluginMcpAuthority(store, Standalone) : authority;
        var configuredStore = mismatch == "different-installed-root" ? new PluginStateStore(Path.Combine(_root, "other-installed")) : store;
        (await Registrar(configuredStore, configured).RegisterNativeAsync(admission)).Registered.ShouldBeFalse();
        NoExecution();
    }

    [Fact]
    public async Task GenuineConfiguredStandalone_SucceedsWithoutInferringFromInstalledState()
    {
        var store = new PluginStateStore(Installed);
        var directory = Native(Path.Combine(Standalone, "payload"));
        var authority = Authority(store);
        var admission = NativePluginMcpAdmission.Admit(directory, authority);
        (await Registrar(store, authority).RegisterNativeAsync(admission)).Registered.ShouldBeTrue();
        _trust.Calls.ShouldBe(1);
        _reader.Calls.ShouldBe(1);
        _host.Started.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("equal")]
    [InlineData("standalone-inside")]
    [InlineData("installed-inside")]
    public void OverlappingAuthorityRoots_AreNeverAccepted(string relationship)
    {
        Directory.CreateDirectory(Installed);
        var standalone = relationship == "equal" ? Installed : relationship == "standalone-inside"
            ? Path.Combine(Installed, "nested") : _root;
        Should.Throw<InvalidDataException>(() => new NativePluginMcpAuthority(new PluginStateStore(Installed), standalone));
        NoExecution();
    }

    [Fact]
    public async Task StaleAuthority_InstalledRootAliasCannotDisguiseOverlap()
    {
        var store = new PluginStateStore(Installed);
        var directory = Native(Path.Combine(Standalone, "payload"));
        var authority = Authority(store);
        var admission = NativePluginMcpAdmission.Admit(directory, authority);
        Directory.CreateSymbolicLink(Installed, Standalone);
        try
        {
            (await Registrar(store, authority).RegisterNativeAsync(admission)).Registered.ShouldBeFalse();
            NoExecution();
        }
        finally { Directory.Delete(Installed); }
    }

    private NativePluginMcpAuthority Authority(PluginStateStore store)
    {
        Directory.CreateDirectory(Standalone);
        return new(store, Standalone);
    }
    private PluginMcpServerRegistrar Registrar(PluginStateStore store, NativePluginMcpAuthority? authority) =>
        new(_host, _trust, installedStore: store, declarationReader: _reader, standaloneAuthority: authority);
    private static string Native(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, ".botnexus-plugin"));
        File.WriteAllText(Path.Combine(directory, ".botnexus-plugin", "plugin.json"), """{"name":"payload"}""");
        File.WriteAllText(Path.Combine(directory, ".botnexus-plugin", "mcp.json"), "opaque; the injected reader records interpretation");
        return directory;
    }
    private void NoExecution()
    {
        _trust.Calls.ShouldBe(0);
        _reader.Calls.ShouldBe(0);
        _host.Started.ShouldBeEmpty();
    }
    private sealed class CountingTrust : IPluginTrustEvaluator
    {
        public int Calls { get; private set; }
        public PluginTrustMode Mode => PluginTrustMode.Disabled;
        public PluginTrustDecision Evaluate(string name, string directory) { Calls++; return PluginTrustDecision.Trust; }
    }
    private sealed class CountingReader : IPluginMcpDeclarationReader
    {
        public int Calls { get; private set; }
        public PluginMcpDeclaration Read(string directory, string? path)
        {
            Calls++;
            return new(new Dictionary<string, McpServerConfig> { ["server"] = new() { Command = "recording-host-only" } }, null);
        }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
