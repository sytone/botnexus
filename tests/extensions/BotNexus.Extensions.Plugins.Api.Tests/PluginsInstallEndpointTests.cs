using BotNexus.Extensions.Plugins.Lifecycle;
using BotNexus.Extensions.Plugins.Portal;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace BotNexus.Extensions.Plugins.Api.Tests;

/// <summary>
/// Pins the install / update / remove routes.
/// </summary>
/// <remarks>
/// These drive a lifecycle manager over a scripted fetcher rather than a git binary, following the
/// existing fetcher tests. What is under test here is the route layer - validation, status-code
/// choice, and the restart-required signal - not the lifecycle semantics, which their own tests
/// already pin.
/// </remarks>
public sealed class PluginsInstallEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "botnexus-plugins-install-api", Guid.NewGuid().ToString("N"));

    private readonly string _pluginRoot;
    private readonly string _extensionsRoot;
    private readonly ScriptedFetcher _fetcher = new();

    public PluginsInstallEndpointTests()
    {
        _pluginRoot = Path.Combine(_root, "plugins");
        _extensionsRoot = Path.Combine(_root, "extensions");
        Directory.CreateDirectory(_pluginRoot);
        Directory.CreateDirectory(_extensionsRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private PluginLifecycleManager Manager() =>
        new(new PluginStateStore(_pluginRoot), _fetcher, extensionsRoot: _extensionsRoot);

    private static Dictionary<string, string> SkillsPlugin(string name) => new(StringComparer.Ordinal)
    {
        [".botnexus-plugin/plugin.json"] = "{ \"name\": \"" + name + "\", \"version\": \"1.0.0\" }",
        ["skills/greet/SKILL.md"] = "# greet",
    };

    private static Dictionary<string, string> CodePlugin(string name) => new(StringComparer.Ordinal)
    {
        [".botnexus-plugin/plugin.json"] =
            "{ \"name\": \"" + name + "\", \"extension\": { \"manifest\": \"botnexus-extension.json\" } }",
        // A carried extension must declare that it sits behind authentication, or the deployer
        // refuses it - third-party code does not inherit a pre-auth position by default.
        ["botnexus-extension.json"] =
            "{ \"id\": \"carried-ext\", \"entryAssembly\": \"lib/Carried.dll\","
            + " \"endpointPhase\": \"after-authentication\" }",
        ["lib/Carried.dll"] = "prebuilt",
    };

    [Fact]
    public async Task Install_rejects_a_request_with_no_source()
    {
        var result = await PluginsEndpointContributor.InstallAsync(
            new PluginInstallApiRequest(Source: "   "), Manager(), _pluginRoot);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task Install_returns_the_installed_plugin_row()
    {
        _fetcher.Enqueue("sha-aaa", SkillsPlugin("hello-world"));

        var result = await PluginsEndpointContributor.InstallAsync(
            new PluginInstallApiRequest(Source: "https://example.com/hello.git"), Manager(), _pluginRoot);

        var ok = Assert.IsType<Ok<PluginOperationResponse>>(result);
        Assert.Equal("Installed", ok.Value!.Outcome);
        Assert.Equal("hello-world", ok.Value.Name);
        Assert.Equal("sha-aaa", ok.Value.ResolvedVersion);
        Assert.NotNull(ok.Value.Plugin);
        Assert.False(ok.Value.RestartRequired);
    }

    // The reference is what update re-resolves, so the route must pass it through rather than
    // silently installing the default branch.
    [Fact]
    public async Task Install_passes_the_requested_reference_through()
    {
        _fetcher.Enqueue("sha-bbb", SkillsPlugin("hello-world"));

        await PluginsEndpointContributor.InstallAsync(
            new PluginInstallApiRequest(Source: "https://example.com/hello.git", Reference: "v1.0.0"),
            Manager(),
            _pluginRoot);

        Assert.Equal(("https://example.com/hello.git", "v1.0.0"), _fetcher.Calls[0]);
    }

    // A failure must carry the field the lifecycle manager named; flattening it to a string throws
    // away the only thing that tells an author what to fix.
    [Fact]
    public async Task Install_surfaces_a_failure_as_a_bad_request()
    {
        _fetcher.Enqueue("sha-ccc", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["readme.md"] = "no manifest here",
        });

        var result = await PluginsEndpointContributor.InstallAsync(
            new PluginInstallApiRequest(Source: "https://example.com/not-a-plugin.git"), Manager(), _pluginRoot);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Null(new PluginStateStore(_pluginRoot).Find("not-a-plugin"));
    }

    // Code is opt-in at the route as well as in the domain.
    [Fact]
    public async Task Install_refuses_an_unacknowledged_carried_extension()
    {
        _fetcher.Enqueue("sha-ddd", CodePlugin("code-plugin"));

        var result = await PluginsEndpointContributor.InstallAsync(
            new PluginInstallApiRequest(Source: "https://example.com/code.git"), Manager(), _pluginRoot);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_extensionsRoot, "carried-ext")));
    }

    // "Installed" and "installed and working" are different claims: a carried extension is inert
    // until the gateway restarts, and the response has to say so.
    [Fact]
    public async Task Install_of_a_code_plugin_reports_that_a_restart_is_required()
    {
        _fetcher.Enqueue("sha-eee", CodePlugin("code-plugin"));

        var result = await PluginsEndpointContributor.InstallAsync(
            new PluginInstallApiRequest(Source: "https://example.com/code.git", AcknowledgeCarriedExtension: true),
            Manager(),
            _pluginRoot);

        var ok = Assert.IsType<Ok<PluginOperationResponse>>(result);
        Assert.Equal("Installed", ok.Value!.Outcome);
        Assert.True(ok.Value.RestartRequired);
        Assert.True(File.Exists(Path.Combine(_extensionsRoot, "carried-ext", "lib", "Carried.dll")));
    }

    [Fact]
    public void Remove_returns_not_found_for_a_plugin_that_is_not_installed()
    {
        var result = PluginsEndpointContributor.Remove("ghost", Manager());

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task Remove_deletes_an_installed_plugin()
    {
        _fetcher.Enqueue("sha-fff", SkillsPlugin("hello-world"));
        await PluginsEndpointContributor.InstallAsync(
            new PluginInstallApiRequest(Source: "https://example.com/hello.git"), Manager(), _pluginRoot);

        var result = PluginsEndpointContributor.Remove("hello-world", Manager());

        var ok = Assert.IsType<Ok<PluginOperationResponse>>(result);
        Assert.Equal("Removed", ok.Value!.Outcome);
        Assert.Null(new PluginStateStore(_pluginRoot).Find("hello-world"));
    }

    [Fact]
    public async Task Update_reports_when_the_source_has_not_moved()
    {
        _fetcher.Enqueue("sha-ggg", SkillsPlugin("hello-world"));
        await PluginsEndpointContributor.InstallAsync(
            new PluginInstallApiRequest(Source: "https://example.com/hello.git"), Manager(), _pluginRoot);
        _fetcher.Enqueue("sha-ggg", SkillsPlugin("hello-world"));

        var result = await PluginsEndpointContributor.UpdateAsync("hello-world", Manager(), _pluginRoot);

        var ok = Assert.IsType<Ok<PluginOperationResponse>>(result);
        Assert.Equal("AlreadyCurrent", ok.Value!.Outcome);
    }

    /// <summary>Writes a scripted file set into the staging directory; no git, no network.</summary>
    private sealed class ScriptedFetcher : IPluginSourceFetcher
    {
        private readonly Queue<(string Version, IReadOnlyDictionary<string, string> Files)> _queued = new();

        public List<(string Source, string? Reference)> Calls { get; } = [];

        public void Enqueue(string resolvedVersion, IReadOnlyDictionary<string, string> files) =>
            _queued.Enqueue((resolvedVersion, files));

        public Task<PluginFetchResult> FetchAsync(
            string source,
            string? reference,
            string stagingDirectory,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((source, reference));
            var (version, files) = _queued.Dequeue();

            foreach (var (relative, content) in files)
            {
                var path = Path.Combine(stagingDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
            }

            return Task.FromResult(new PluginFetchResult(version));
        }
    }
}
