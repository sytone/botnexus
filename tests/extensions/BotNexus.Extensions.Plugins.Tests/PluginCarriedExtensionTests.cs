using BotNexus.Extensions.Plugins.Lifecycle;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>
/// Pins the carrying of a prebuilt gateway extension inside a plugin.
/// </summary>
/// <remarks>
/// The load-bearing tests are the refusals, not the happy path. A carried extension is
/// author-supplied content that names its own paths and its own destination directory, so the
/// cases that must hold are: a path cannot escape the plugin, an id cannot escape the extensions
/// root, a declared-but-absent assembly fails at install rather than at the next gateway start,
/// and an already-deployed extension is never overwritten - because install runs inside the
/// gateway process that has those assemblies loaded.
/// </remarks>
public sealed class PluginCarriedExtensionTests : IDisposable
{
    private readonly string _root;
    private readonly string _pluginRoot;
    private readonly string _extensionsRoot;
    private readonly FakePluginSourceFetcher _fetcher = new();
    private readonly PluginStateStore _store;
    private readonly PluginLifecycleManager _manager;

    public PluginCarriedExtensionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "botnexus-carried-tests", Guid.NewGuid().ToString("N"));
        _pluginRoot = Path.Combine(_root, "plugins");
        _extensionsRoot = Path.Combine(_root, "extensions");
        Directory.CreateDirectory(_pluginRoot);
        Directory.CreateDirectory(_extensionsRoot);
        _store = new PluginStateStore(_pluginRoot);
        _manager = new PluginLifecycleManager(
            _store, _fetcher, extensionsRoot: _extensionsRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test over.
        }
    }

    private static string PluginManifestJson(string name, string? extensionManifestPath) =>
        extensionManifestPath is null
            ? "{ \"name\": \"" + name + "\" }"
            : "{ \"name\": \"" + name + "\", \"extension\": { \"manifest\": \"" + extensionManifestPath + "\" } }";

    private static string ExtensionManifestJson(string id, string entryAssembly) =>
        "{ \"id\": \"" + id + "\", \"name\": \"Test Extension\", \"version\": \"1.0.0\", \"entryAssembly\": \""
        + entryAssembly + "\", \"extensionTypes\": [\"endpoint-contributor\"], \"enabled\": true,"
        + " \"endpointPhase\": \"after-authentication\","
        + " \"nav\": [{ \"id\": \"test\", \"label\": \"Test\", \"path\": \"/test\" }] }";

    /// <summary>Writes a plugin directory on disk, bypassing install, for deployer-only tests.</summary>
    private string WritePluginDirectory(string name, IReadOnlyDictionary<string, string> files)
    {
        var dir = Path.Combine(_pluginRoot, name);
        foreach (var (relative, content) in files)
        {
            var path = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        return dir;
    }

    private static Dictionary<string, string> CarriedPluginContent(
        string name = "code-plugin",
        string extensionId = "test-extension",
        string manifestPath = "botnexus-extension.json",
        string entryAssembly = "lib/Test.Extension.dll",
        bool includeEntryAssembly = true)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".botnexus-plugin/plugin.json"] = PluginManifestJson(name, manifestPath),
            [manifestPath] = ExtensionManifestJson(extensionId, entryAssembly),
        };

        if (includeEntryAssembly)
        {
            files[entryAssembly] = "not really a dll, but present";
        }

        return files;
    }

    // The happy path: the carried subtree lands where the gateway loader looks for it.
    [Fact]
    public void DeployCopiesCarriedExtensionIntoExtensionsRoot()
    {
        var pluginDir = WritePluginDirectory("code-plugin", CarriedPluginContent());

        var result = new PluginExtensionDeployer().Deploy(
            "code-plugin", pluginDir, new PluginExtensionRef { Manifest = "botnexus-extension.json" }, _extensionsRoot);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("test-extension", result.ExtensionId);

        var deployed = Path.Combine(_extensionsRoot, "test-extension");
        Assert.True(File.Exists(Path.Combine(deployed, "botnexus-extension.json")));
        Assert.True(File.Exists(Path.Combine(deployed, "lib", "Test.Extension.dll")));
        Assert.Contains("botnexus-extension.json", result.Files);
        Assert.Contains("lib/Test.Extension.dll", result.Files);
    }

    // Plugin-domain content must not be dragged into the extensions tree, where nothing reads it
    // and a stale copy would outlive the plugin.
    [Fact]
    public void DeployExcludesPluginDomainDirectoriesWhenManifestSitsAtPluginRoot()
    {
        var files = CarriedPluginContent();
        files["skills/greet/SKILL.md"] = "# greet";
        var pluginDir = WritePluginDirectory("code-plugin", files);

        var result = new PluginExtensionDeployer().Deploy(
            "code-plugin", pluginDir, new PluginExtensionRef { Manifest = "botnexus-extension.json" }, _extensionsRoot);

        Assert.True(result.Succeeded, result.Message);

        var deployed = Path.Combine(_extensionsRoot, "test-extension");
        Assert.False(Directory.Exists(Path.Combine(deployed, "skills")));
        Assert.False(Directory.Exists(Path.Combine(deployed, ".botnexus-plugin")));
        Assert.DoesNotContain(result.Files, f => f.StartsWith("skills/", StringComparison.Ordinal));
    }

    // A manifest path is author-supplied and arrives straight from a cloned repository.
    [Fact]
    public void DeployRejectsManifestPathEscapingThePluginDirectory()
    {
        var pluginDir = WritePluginDirectory("code-plugin", CarriedPluginContent());

        var result = new PluginExtensionDeployer().Deploy(
            "code-plugin", pluginDir, new PluginExtensionRef { Manifest = "../../escape.json" }, _extensionsRoot);

        Assert.False(result.Succeeded);
        Assert.Equal("extension.manifest", result.Field);
        Assert.Contains("outside the plugin directory", result.Message);
    }

    // The id names a directory under the extensions root, so it must be a single plain segment.
    [Fact]
    public void DeployRejectsAnExtensionIdThatIsNotASafeDirectoryName()
    {
        var files = CarriedPluginContent(extensionId: "../evil");
        var pluginDir = WritePluginDirectory("code-plugin", files);

        var result = new PluginExtensionDeployer().Deploy(
            "code-plugin", pluginDir, new PluginExtensionRef { Manifest = "botnexus-extension.json" }, _extensionsRoot);

        Assert.False(result.Succeeded);
        Assert.Equal("extension.id", result.Field);
        Assert.False(Directory.Exists(Path.Combine(_root, "evil")));
    }

    // Prebuilt means prebuilt. Catching this at install beats a mystery at the next gateway start.
    [Fact]
    public void DeployRejectsADeclaredEntryAssemblyThatWasNeverCommitted()
    {
        var files = CarriedPluginContent(includeEntryAssembly: false);
        var pluginDir = WritePluginDirectory("code-plugin", files);

        var result = new PluginExtensionDeployer().Deploy(
            "code-plugin", pluginDir, new PluginExtensionRef { Manifest = "botnexus-extension.json" }, _extensionsRoot);

        Assert.False(result.Succeeded);
        Assert.Equal("extension.entryAssembly", result.Field);
        Assert.Contains("prebuilt and committed", result.Message);
        Assert.False(Directory.Exists(Path.Combine(_extensionsRoot, "test-extension")));
    }

    // Contributors map ahead of authentication by default, so a plugin that says nothing would
    // inherit an unauthenticated position by accident. Requiring the declaration makes the
    // placement a decision rather than an inheritance.
    [Fact]
    public void DeployRefusesACarriedExtensionThatDoesNotSitBehindAuthentication()
    {
        var files = CarriedPluginContent();
        files["botnexus-extension.json"] =
            "{ \"id\": \"test-extension\", \"entryAssembly\": \"lib/Test.Extension.dll\" }";
        var pluginDir = WritePluginDirectory("code-plugin", files);

        var result = new PluginExtensionDeployer().Deploy(
            "code-plugin", pluginDir, new PluginExtensionRef { Manifest = "botnexus-extension.json" }, _extensionsRoot);

        Assert.False(result.Succeeded);
        Assert.Equal("extension.endpointPhase", result.Field);
        Assert.Contains("after-authentication", result.Message);
        Assert.False(Directory.Exists(Path.Combine(_extensionsRoot, "test-extension")));
    }

    // An explicit pre-auth declaration is refused just as firmly as an absent one - the point is
    // where the code runs, not whether the author wrote something.
    [Fact]
    public void DeployRefusesAnExplicitBeforeAuthenticationDeclaration()
    {
        var files = CarriedPluginContent();
        files["botnexus-extension.json"] =
            "{ \"id\": \"test-extension\", \"entryAssembly\": \"lib/Test.Extension.dll\","
            + " \"endpointPhase\": \"before-authentication\" }";
        var pluginDir = WritePluginDirectory("code-plugin", files);

        var result = new PluginExtensionDeployer().Deploy(
            "code-plugin", pluginDir, new PluginExtensionRef { Manifest = "botnexus-extension.json" }, _extensionsRoot);

        Assert.False(result.Succeeded);
        Assert.Equal("extension.endpointPhase", result.Field);
    }

    // Install runs inside the gateway process, which has loaded extensions' assemblies mapped.
    // Overwriting one in place fails, so it is refused outright rather than half-applied.
    [Fact]
    public void DeployRefusesToOverwriteAnAlreadyDeployedExtension()
    {
        Directory.CreateDirectory(Path.Combine(_extensionsRoot, "test-extension"));
        File.WriteAllText(Path.Combine(_extensionsRoot, "test-extension", "marker.txt"), "existing");
        var pluginDir = WritePluginDirectory("code-plugin", CarriedPluginContent());

        var result = new PluginExtensionDeployer().Deploy(
            "code-plugin", pluginDir, new PluginExtensionRef { Manifest = "botnexus-extension.json" }, _extensionsRoot);

        Assert.False(result.Succeeded);
        Assert.Equal("extension.id", result.Field);
        Assert.Contains("was not overwritten", result.Message);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(_extensionsRoot, "test-extension", "marker.txt")));
    }

    // Provenance: without a recorded owner, a deployed extension outlives the plugin that installed it.
    [Fact]
    public async Task InstallRecordsTheDeployedExtensionAgainstThePlugin()
    {
        _fetcher.Enqueue("sha-1", CarriedPluginContent());

        var result = await _manager.InstallAsync(new PluginInstallRequest
        {
            Source = "https://example.com/code-plugin.git",
            AllowCarriedExtension = true,
        });

        Assert.Equal(PluginOperationOutcome.Installed, result.Outcome);

        var record = _store.Find("code-plugin");
        Assert.NotNull(record);
        Assert.Equal("test-extension", record!.DeployedExtensionId);
        Assert.Contains("lib/Test.Extension.dll", record.ExtensionFiles);
        Assert.True(File.Exists(Path.Combine(_extensionsRoot, "test-extension", "botnexus-extension.json")));
    }

    // A code plugin whose code did not deploy is not the same artefact with an optional part
    // missing, so the plugin half must not survive on its own.
    [Fact]
    public async Task InstallRollsBackThePluginWhenItsCarriedExtensionCannotDeploy()
    {
        _fetcher.Enqueue("sha-1", CarriedPluginContent(includeEntryAssembly: false));

        var result = await _manager.InstallAsync(new PluginInstallRequest
        {
            Source = "https://example.com/code-plugin.git",
            AllowCarriedExtension = true,
        });

        Assert.Equal(PluginOperationOutcome.Failed, result.Outcome);
        Assert.False(Directory.Exists(Path.Combine(_pluginRoot, "code-plugin")));
        Assert.Null(_store.Find("code-plugin"));
    }

    // Silently installing the skills half of a code plugin would install something materially
    // different from what the author published.
    [Fact]
    public async Task InstallRefusesACodePluginWhenNoExtensionsRootIsConfigured()
    {
        var manager = new PluginLifecycleManager(_store, _fetcher);
        _fetcher.Enqueue("sha-1", CarriedPluginContent());

        var result = await manager.InstallAsync(new PluginInstallRequest
        {
            Source = "https://example.com/code-plugin.git",
            AllowCarriedExtension = true,
        });

        Assert.Equal(PluginOperationOutcome.Failed, result.Outcome);
        Assert.Contains("no extensions root is configured", result.Errors[0].Message);
        Assert.False(Directory.Exists(Path.Combine(_pluginRoot, "code-plugin")));
    }

    // The extensions root holds a separate copy, so removing the plugin directory alone would
    // leave a deployed extension nothing claims to own.
    [Fact]
    public async Task RemoveDeletesTheDeployedExtensionAlongsideThePlugin()
    {
        _fetcher.Enqueue("sha-1", CarriedPluginContent());
        await _manager.InstallAsync(new PluginInstallRequest
        {
            Source = "https://example.com/code-plugin.git",
            AllowCarriedExtension = true,
        });
        Assert.True(Directory.Exists(Path.Combine(_extensionsRoot, "test-extension")));

        var result = _manager.Remove("code-plugin");

        Assert.Equal(PluginOperationOutcome.Removed, result.Outcome);
        Assert.False(Directory.Exists(Path.Combine(_extensionsRoot, "test-extension")));
        Assert.Null(_store.Find("code-plugin"));
    }

    // Whether a source carries code is only knowable after it is fetched, so consent cannot be
    // resolved from the URL. Refusal must happen before anything reaches disk.
    [Fact]
    public async Task InstallRefusesACarriedExtensionThatWasNotAcknowledged()
    {
        _fetcher.Enqueue("sha-1", CarriedPluginContent());

        var result = await _manager.InstallAsync(new PluginInstallRequest
        {
            Source = "https://example.com/code-plugin.git",
        });

        Assert.Equal(PluginOperationOutcome.Failed, result.Outcome);
        Assert.Equal("extension.consent", result.Errors[0].Field);
        Assert.Contains("runs code in the gateway process", result.Errors[0].Message);
        Assert.False(Directory.Exists(Path.Combine(_pluginRoot, "code-plugin")));
        Assert.False(Directory.Exists(Path.Combine(_extensionsRoot, "test-extension")));
        Assert.Null(_store.Find("code-plugin"));
    }

    // "Runs code at full trust" states the risk; naming what the extension contributes is what
    // lets an operator judge whether THIS plugin is worth it. A generic warning teaches nothing and
    // gets click-through.
    [Fact]
    public async Task The_consent_refusal_discloses_what_the_extension_contributes()
    {
        _fetcher.Enqueue("sha-1", CarriedPluginContent());

        var result = await _manager.InstallAsync(new PluginInstallRequest
        {
            Source = "https://example.com/code-plugin.git",
        });

        var message = result.Errors[0].Message;
        Assert.Contains("Test Extension", message);
        Assert.Contains("endpoint-contributor", message);
        Assert.Contains("/test", message);
        Assert.Contains("no sandbox", message);
    }

    // Disclosure is best effort: a manifest too malformed to describe must still refuse for
    // consent, and must not fail differently. The deploy step rejects it properly a moment later.
    [Fact]
    public async Task An_undescribable_extension_still_refuses_for_consent()
    {
        var files = CarriedPluginContent();
        files["botnexus-extension.json"] = "{ not valid json";
        _fetcher.Enqueue("sha-1", files);

        var result = await _manager.InstallAsync(new PluginInstallRequest
        {
            Source = "https://example.com/code-plugin.git",
        });

        Assert.Equal(PluginOperationOutcome.Failed, result.Outcome);
        Assert.Equal("extension.consent", result.Errors[0].Field);
        Assert.Contains("full trust", result.Errors[0].Message);
    }

    // A skills-only plugin must stay low-friction: the gate is for code, not for every install.
    [Fact]
    public async Task InstallOfASkillsOnlyPluginNeedsNoAcknowledgement()
    {
        _fetcher.Enqueue("sha-1", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".botnexus-plugin/plugin.json"] = PluginManifestJson("skills-plugin", extensionManifestPath: null),
            ["skills/greet/SKILL.md"] = "# greet",
        });

        var result = await _manager.InstallAsync(new PluginInstallRequest
        {
            Source = "https://example.com/skills-plugin.git",
        });

        Assert.Equal(PluginOperationOutcome.Installed, result.Outcome);
        Assert.Null(_store.Find("skills-plugin")!.DeployedExtensionId);
    }

    // A partial update that replaced the plugin but not its loaded extension would leave the two
    // disagreeing about which build is installed.
    [Fact]
    public async Task UpdateRefusesAPluginThatCarriesADeployedExtension()
    {
        _fetcher.Enqueue("sha-1", CarriedPluginContent());
        await _manager.InstallAsync(new PluginInstallRequest
        {
            Source = "https://example.com/code-plugin.git",
            AllowCarriedExtension = true,
        });

        var result = await _manager.UpdateAsync("code-plugin");

        Assert.Equal(PluginOperationOutcome.Failed, result.Outcome);
        Assert.Contains("cannot be replaced in place", result.Errors[0].Message);
        Assert.Empty(_fetcher.Calls.Skip(1));
    }
}
