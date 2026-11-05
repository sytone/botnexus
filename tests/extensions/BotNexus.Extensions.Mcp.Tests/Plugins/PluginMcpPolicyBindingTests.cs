using System.Text.Json;
using BotNexus.Extensions.Mcp.Plugins;
using BotNexus.Extensions.Plugins.Lifecycle;

namespace BotNexus.Extensions.Mcp.Tests.Plugins;

public sealed class PluginMcpPolicyBindingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcp-policy-binding", Guid.NewGuid().ToString("N"));
    private readonly RecordingMcpServerHost _host = new();
    private readonly CountingTrust _trust = new();
    private readonly CountingReader _reader = new();

    [Theory]
    [InlineData("alternate")]
    [InlineData("native-sibling")]
    [InlineData("trailing")]
    [InlineData("nested")]
    [InlineData("wrong-root")]
    [InlineData("unconfigured")]
    [InlineData("foreign-false")]
    [InlineData("marketplace-false")]
    [InlineData("duplicate")]
    public async Task InstalledAdmission_DeniedBindingsNeverReachTrustReadOrStart(string scenario)
    {
        var directory = Create("foreign");
        var foreign = Record("foreign") with { ManifestFormat = PluginManifestFormat.Claude };
        var store = new PluginStateStore(_root);
        store.Write([foreign, Record("native")]);
        var name = "foreign";
        PluginStateStore? configured = store;
        switch (scenario)
        {
            case "alternate": name = "alternate"; break;
            case "native-sibling": name = "native"; break;
            case "trailing": directory += Path.DirectorySeparatorChar; break;
            case "nested": directory = Create("foreign/nested"); break;
            case "wrong-root": configured = new PluginStateStore(Path.Combine(_root, "other")); break;
            case "unconfigured": configured = null; break;
            case "foreign-false": WriteRaw("Claude", false); break;
            case "marketplace-false": WriteRaw("Native", false, ",\"marketplacePluginName\":\"foreign\""); break;
            case "duplicate": store.Write([Record("foreign"), foreign]); break;
        }
        var result = await Registrar(configured).RegisterAsync(name, directory);
        AssertDenied(result);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("blank")]
    [InlineData("null")]
    [InlineData("corrupt")]
    [InlineData("directory")]
    [InlineData("locked")]
    [InlineData("empty")]
    [InlineData("null-entry")]
    [InlineData("duplicate-field")]
    [InlineData("linked-state")]
    public async Task InstalledAdmission_UnavailableStateNeverReachesTrustReadOrStart(string state)
    {
        var directory = Create("foreign");
        var store = new PluginStateStore(_root);
        var path = store.StatePath;
        FileStream? locked = null;
        try
        {
            switch (state)
            {
                case "blank": File.WriteAllText(path, " "); break;
                case "null": File.WriteAllText(path, "null"); break;
                case "corrupt": File.WriteAllText(path, "{"); break;
                case "directory": Directory.CreateDirectory(path); break;
                case "empty": File.WriteAllText(path, "[]"); break;
                case "null-entry": File.WriteAllText(path, "[null]"); break;
                case "duplicate-field":
                    WriteRaw("Native", false, ",\"skillsOnly\":true");
                    break;
                case "linked-state":
                    var target = Path.Combine(_root, "other-policy.json");
                    File.WriteAllText(target, "[]");
                    File.CreateSymbolicLink(path, target);
                    break;
                case "locked":
                    store.Write([Record("foreign")]);
                    locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    break;
            }
            AssertDenied(await Registrar(store).RegisterAsync("foreign", directory));
        }
        finally { locked?.Dispose(); }
    }

    [Fact]
    public async Task InstalledNative_OlderRecordAndTrailingSeparatorReachOriginalTrustAndReader()
    {
        var directory = Create("foreign");
        WriteRaw("Native", null);
        var result = await Registrar(new PluginStateStore(_root)).RegisterAsync("foreign", directory + Path.DirectorySeparatorChar);
        result.Registered.ShouldBeTrue();
        _trust.Calls.ShouldBe(1);
        _reader.Calls.ShouldBe(1);
        _host.Started.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task StandaloneNative_RequiresValidatedAdmissionAndRejectsForeignEnvelope()
    {
        var directory = Create("standalone-root/standalone");
        File.WriteAllText(Path.Combine(directory, ".botnexus-plugin", "plugin.json"), """{"name":"standalone"}""");
        var store = new PluginStateStore(Path.Combine(_root, "installed"));
        var authority = new NativePluginMcpAuthority(store, Path.Combine(_root, "standalone-root"));
        var admission = NativePluginMcpAdmission.Admit(directory, authority);
        var registrar = new PluginMcpServerRegistrar(_host, _trust, installedStore: store, declarationReader: _reader, standaloneAuthority: authority);
        (await registrar.RegisterNativeAsync(admission)).Registered.ShouldBeTrue();
        _host.Started.ShouldHaveSingleItem();
        Directory.CreateDirectory(Path.Combine(directory, ".claude-plugin"));
        File.WriteAllText(Path.Combine(directory, ".claude-plugin", "plugin.json"), """{"name":"standalone"}""");
        Should.Throw<InvalidDataException>(() => NativePluginMcpAdmission.Admit(directory, authority));
        var trustBefore = _trust.Calls;
        var readBefore = _reader.Calls;
        (await registrar.RegisterNativeAsync(admission)).Registered.ShouldBeFalse();
        _trust.Calls.ShouldBe(trustBefore);
        _reader.Calls.ShouldBe(readBefore);
        _host.Started.Count.ShouldBe(1);
    }

    private PluginMcpServerRegistrar Registrar(PluginStateStore? store) =>
        new(_host, _trust, installedStore: store, declarationReader: _reader);

    private void AssertDenied(PluginMcpRegistration result)
    {
        result.Registered.ShouldBeFalse();
        result.SkippedReason.ShouldNotBeNull();
        _trust.Calls.ShouldBe(0);
        _reader.Calls.ShouldBe(0);
        _host.Started.ShouldBeEmpty();
    }

    private string Create(string name)
    {
        var directory = Path.Combine(_root, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.Combine(directory, ".botnexus-plugin"));
        File.WriteAllText(Path.Combine(directory, ".botnexus-plugin", "mcp.json"), "opaque and deliberately invalid JSON");
        return directory;
    }

    private void WriteRaw(string format, bool? policy, string extra = "")
    {
        var field = policy is null ? "" : ",\"skillsOnly\":" + JsonSerializer.Serialize(policy);
        File.WriteAllText(Path.Combine(_root, PluginStateStore.StateFileName), $$"""[{"name":"foreign","source":"https://example.invalid/repo.git","resolvedVersion":"v1","manifestFormat":"{{format}}","installedAtUtc":"2026-01-01T00:00:00Z"{{field}}{{extra}}}]""");
    }

    private static InstalledPlugin Record(string name) => new()
    {
        Name = name, Source = "https://example.invalid/repo.git", ResolvedVersion = "v1", InstalledAtUtc = DateTimeOffset.UtcNow,
    };

    private sealed class CountingTrust : IPluginTrustEvaluator
    {
        public int Calls { get; private set; }
        public PluginTrustMode Mode => PluginTrustMode.Disabled;
        public PluginTrustDecision Evaluate(string pluginName, string pluginDirectory)
        {
            Calls++;
            return PluginTrustDecision.Trust;
        }
    }

    private sealed class CountingReader : IPluginMcpDeclarationReader
    {
        public int Calls { get; private set; }
        public PluginMcpDeclaration Read(string directory, string? declaredPath)
        {
            Calls++;
            return new(new Dictionary<string, McpServerConfig> { ["server"] = new() { Command = "never-executed-recording-host" } }, null);
        }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
