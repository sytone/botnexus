using System.Diagnostics;
using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BotNexus.Cli.Tests;

/// <summary>
/// Exercises collection resets across the long-lived configuration composition and real,
/// short-lived CLI processes. A conflicting legacy JSON file deliberately remains present:
/// deleting it would hide resurrection of values from the lower-precedence provider (#4975).
/// No gateway host, network listener, or cron store is started by this fixture.
/// </summary>
public sealed class CollectionLifecycleTests
{
    /// <summary>List, tool-list, and extension-map reset boundaries shared by both lifecycles.</summary>
    public static TheoryData<string, string, string> Collections => new()
    {
        { "gateway.fileAccess.allowedReadPaths", "[\"read-a\",\"read-b\"]", "[]" },
        { "agents.alpha.fileAccess.allowedReadPaths", "[\"read-a\",\"read-b\"]", "[]" },
        { "agents.defaults.fileAccess.allowedReadPaths", "[\"read-a\",\"read-b\"]", "[]" },
        { "agents.alpha.toolIds", "[\"tool-a\",\"tool-b\"]", "[]" },
        { "agents.defaults.toolIds", "[\"tool-a\",\"tool-b\"]", "[]" },
        { "world.extensions", "{\"sample\":{\"items\":[],\"settings\":{},\"disabled\":null,\"enabled\":true}}", "{}" },
        { "gateway.extensions", "{\"sample\":{\"items\":[],\"settings\":{},\"disabled\":null,\"enabled\":true}}", "{}" },
        { "agents.alpha.extensions", "{\"sample\":{\"items\":[],\"settings\":{},\"disabled\":null,\"enabled\":true}}", "{}" },
        { "agents.defaults.extensions", "{\"sample\":{\"items\":[],\"settings\":{},\"disabled\":null,\"enabled\":true}}", "{}" }
    };

    [Theory]
    [MemberData(nameof(Collections))]
    public async Task AutomaticReload_WithStaleJson_PreservesCollectionShapeAndSiblings(
        string path, string populated, string empty)
    {
        await using var home = await CollectionHome.CreateAsync(path, populated);
        // Use the same source composition as the gateway/CLI, including its provider-owned
        // change detector. Do not call Reload or CheckForChangesAsync to drive notifications.
        using var root = (ConfigurationRoot)new ConfigurationBuilder()
            .AddPlatformConfiguration(home.ConfigPath).Build();
        var services = new ServiceCollection();
        services.Configure<PlatformConfig>(root);
        services.AddSingleton<IPostConfigureOptions<PlatformConfig>>(
            new PlatformConfigPostConfigure(root, home.ConfigPath));
        using var container = services.BuildServiceProvider();
        var monitor = container.GetRequiredService<IOptionsMonitor<PlatformConfig>>();
        AssertOptions(monitor.CurrentValue, path, populated);

        foreach (var shape in new[] { empty, "null", populated })
        {
            var changed = new TaskCompletionSource<PlatformConfig>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = monitor.OnChange(config => changed.TrySetResult(config));
            home.SetExpected(path, shape);
            // A separate store instance models an external writer, not a provider-local cache.
            await home.Store().WriteDocumentAsync(home.Expected);
            var notified = await changed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            AssertOptions(notified, path, shape);
            AssertOptions(monitor.CurrentValue, path, shape);
            await home.AssertStoreAsync();
            (await File.ReadAllTextAsync(home.ConfigPath)).ShouldBe(home.StaleJson);
        }
    }

    [Theory]
    [MemberData(nameof(Collections))]
    public async Task CliProcessRestart_WithStaleJson_PreservesCollectionShapeAndSiblings(
        string path, string populated, string empty)
    {
        await using var home = await CollectionHome.CreateAsync(path, populated);
        await AssertCliGetAsync(home, path, populated);
        foreach (var shape in new[] { empty, "null", populated })
        {
            // Every command is a new OS process, including the read after the writer exits.
            // This cannot be satisfied by PlatformConfigAccessor's in-process monitor cache.
            await RunCliAsync(home.DirectoryPath, "config", "set", path, shape);
            home.SetExpected(path, shape);
            await home.AssertStoreAsync();

            // Canonical writes may update the compatibility file. Restore the original populated
            // legacy content before restarting the reader, rather than removing the JSON source.
            await File.WriteAllTextAsync(home.ConfigPath, home.StaleJson);
            await AssertCliGetAsync(home, path, shape);
            await AssertCliGetAsync(home, "gateway.fileAccess.allowedWritePaths", "[\"write-sibling\"]");
            await AssertCliGetAsync(home, "agents.beta.toolIds", "[\"beta-tool\"]");
            await home.AssertStoreAsync();
            (await File.ReadAllTextAsync(home.ConfigPath)).ShouldBe(home.StaleJson);
        }
    }

    private static async Task AssertCliGetAsync(CollectionHome home, string path, string expected)
    {
        var output = await RunCliAsync(home.DirectoryPath, "config", "get", ReadPath(path));
        AssertJson(output.Trim(), expected, path);
    }

    private static async Task<string> RunCliAsync(string home, params string[] arguments)
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, "BotNexus.Cli.dll");
        File.Exists(assembly).ShouldBeTrue($"CLI assembly missing: {assembly}");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = home,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(assembly);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["BOTNEXUS_HOME"] = home;
        // Never inherit a caller-selected remote target from the test runner.
        start.ArgumentList.Add("--target");
        start.ArgumentList.Add(home);
        using var process = Process.Start(start).ShouldNotBeNull();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            try
            {
                await Task.WhenAll(process.WaitForExitAsync(), stdout, stderr)
                    .WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                // Preserve the original command deadline even if pipe draining or reaping stalls.
            }
            var capturedOutput = stdout.IsCompletedSuccessfully ? await stdout : "<incomplete>";
            var capturedErrors = stderr.IsCompletedSuccessfully ? await stderr : "<incomplete>";
            throw new TimeoutException($"CLI {string.Join(' ', arguments)} exceeded its deadline. "
                + $"stdout: {capturedOutput} stderr: {capturedErrors}");
        }
        var output = await stdout.WaitAsync(TimeSpan.FromSeconds(10));
        var errors = await stderr.WaitAsync(TimeSpan.FromSeconds(10));
        process.ExitCode.ShouldBe(0, $"CLI {string.Join(' ', arguments)}: {output}\n{errors}");
        return output;
    }

    private static void AssertOptions(PlatformConfig config, string path, string shape)
    {
        AssertValue(config, path, shape);
        AssertValue(config, "gateway.fileAccess.allowedWritePaths", "[\"write-sibling\"]");
        AssertValue(config, "agents.alpha.fileAccess.allowedWritePaths", "[\"agent-write\"]");
        AssertValue(config, "agents.defaults.fileAccess.allowedWritePaths", "[\"default-write\"]");
        AssertValue(config, "agents.beta.toolIds", "[\"beta-tool\"]");
        AssertValue(config, "agents.beta.extensions", "{\"opaque\":{\"items\":[],\"options\":{},\"disabled\":null}}");
        config.Agents.ShouldNotBeNull()["alpha"].DisplayName.ShouldBe("Alpha");
        config.AgentDefaults.ShouldNotBeNull().ToolTimeoutSeconds.ShouldBe(123);
    }

    private static void AssertValue(PlatformConfig config, string path, string expected)
    {
        new ConfigPathResolver().TryGetValue(config, ReadPath(path), out var value, out var error).ShouldBeTrue(error);
        AssertJson(value is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(value), expected, path);
    }

    private static string ReadPath(string path) => path.StartsWith("agents.defaults.", StringComparison.Ordinal)
        ? "agentDefaults." + path["agents.defaults.".Length..] : path;

    private static void AssertJson(string actual, string expected, string path) =>
        JsonNode.DeepEquals(JsonNode.Parse(actual), JsonNode.Parse(expected)).ShouldBeTrue(
            $"{path}: expected {expected}, actual {actual}");

    private sealed class CollectionHome : IAsyncDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"botnexus-collection-lifecycle-{Guid.NewGuid():N}");
        public string ConfigPath => Path.Combine(DirectoryPath, "config.json");
        private string StorePath => ConfigStoreBootstrap.ResolveStorePath(ConfigPath, new FileSystem());
        public JsonObject Expected { get; } = JsonNode.Parse("""
            {
              "world":{"futureWorld":{"items":[],"options":{},"disabled":null}},
              "gateway":{"fileAccess":{"allowedWritePaths":["write-sibling"]},"futureGateway":42},
              "agents":{
                "defaults":{"toolTimeoutSeconds":123,"fileAccess":{"allowedWritePaths":["default-write"]}},
                "alpha":{"provider":"copilot","model":"gpt-4.1","displayName":"Alpha","fileAccess":{"allowedWritePaths":["agent-write"]},"futureAgent":{"keep":true}},
                "beta":{"provider":"copilot","model":"gpt-4.1","displayName":"Beta","toolIds":["beta-tool"],"extensions":{"opaque":{"items":[],"options":{},"disabled":null}}}
              },
              "futureRoot":{"array":[1,2],"object":{},"explicitNull":null}
            }
            """).ShouldBeOfType<JsonObject>();
        public string StaleJson { get; private set; } = "";

        public static async Task<CollectionHome> CreateAsync(string path, string populated)
        {
            var home = new CollectionHome();
            Directory.CreateDirectory(home.DirectoryPath);
            try
            {
                home.SetExpected(path, populated);
                home.StaleJson = home.Expected.ToJsonString();
                await File.WriteAllTextAsync(home.ConfigPath, home.StaleJson);
                await ConfigStoreBootstrap.PopulateAsync(home.StorePath, home.Expected);
                return home;
            }
            catch
            {
                await home.DisposeAsync();
                throw;
            }
        }

        public SqliteConfigStore Store() => new($"Data Source={StorePath};Pooling=False");

        public void SetExpected(string path, string shape)
        {
            var segments = path.Split('.');
            var parent = Expected;
            foreach (var segment in segments[..^1])
            {
                if (parent[segment] is not JsonObject child)
                {
                    child = new JsonObject();
                    parent[segment] = child;
                }
                parent = child;
            }
            parent[segments[^1]] = JsonNode.Parse(shape);
        }

        public async Task AssertStoreAsync()
        {
            var actual = await Store().ReadEntriesAsync();
            var expected = ConfigDocumentFlattener.Flatten(Expected);
            actual.Keys.OrderBy(key => key, StringComparer.Ordinal).ShouldBe(
                expected.Keys.OrderBy(key => key, StringComparer.Ordinal));
            foreach (var (key, entry) in expected)
                actual[key].ShouldBe(entry, $"Unexpected persisted sibling or collection entry: {key}");
        }

        public async ValueTask DisposeAsync()
        {
            ConfigStoreBootstrap.ReleaseConnections(StorePath);
            // ConfigurationRoot cancels the detector on disposal, but a physical JSON watcher
            // or an already-running SQLite read can still be releasing its final handle.
            await TestAwait.EventuallyAsync(() =>
            {
                try
                {
                    if (Directory.Exists(DirectoryPath))
                        Directory.Delete(DirectoryPath, recursive: true);
                    return !Directory.Exists(DirectoryPath);
                }
                catch (IOException)
                {
                    return false;
                }
            }, "collection lifecycle home to be removed after configuration readers finish");
        }
    }
}
