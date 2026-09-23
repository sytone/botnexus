using System.IO.Abstractions;
using System.Text.Json;
using BotNexus.Extensions.Plugins.Cron;
using BotNexus.Extensions.Plugins.Lifecycle;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Plugins.Api.Tests;

/// <summary>
/// Pins the production composition and write API needed to make the existing plugin lifecycle
/// reachable without constructing a second store or manager at the HTTP boundary (#4150).
/// </summary>
public sealed class PluginLifecycleApiCompositionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "botnexus-plugin-lifecycle-api-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Production_contributor_registers_one_shared_lifecycle_graph()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IFileSystem, FileSystem>();
        services.AddSingleton(new BotNexusHome(new FileSystem(), _root, _root));

        new PluginLifecycleServiceContributor().ConfigureServices(services);

        using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<PluginLifecycleManager>();

        Assert.Same(manager, provider.GetRequiredService<PluginLifecycleManager>());
        Assert.Same(manager, provider.GetRequiredService<IPluginUpdateService>());
        Assert.Same(
            provider.GetRequiredService<PluginStateStore>(),
            provider.GetRequiredService<PluginStateStore>());
        Assert.Equal(
            Path.Combine(_root, PluginSkillRootResolver.PluginRootDirectoryName),
            provider.GetRequiredService<PluginStateStore>().PluginRoot);
    }

    [Fact]
    public async Task Write_endpoints_route_every_operation_through_the_injected_manager_and_return_structured_results()
    {
        var fetcher = new ScriptedFetcher();
        fetcher.Enqueue("v1", PluginContent("alpha", "old"));
        fetcher.Enqueue("v2", PluginContent("alpha", "new"));
        var store = new PluginStateStore(_root);
        var manager = new PluginLifecycleManager(store, fetcher);

        var install = Assert.IsType<Ok<PluginOperationReceipt>>(await PluginsEndpointContributor.Install(
            new PluginInstallRequest { Source = "https://example.test/alpha.git", Reference = "main" },
            manager,
            CancellationToken.None));
        Assert.Equal(PluginLifecycleOperation.Install, install.Value!.Operation);
        Assert.Equal(PluginOperationOutcome.Installed, install.Value.Outcome);
        Assert.Equal("alpha", install.Value.Name);
        Assert.Equal("main", install.Value.RequestedReference);
        Assert.Equal("v1", install.Value.ResolvedCommit);
        Assert.True(install.Value.UpdatesEnabled);
        Assert.Null(install.Value.PreviousVersion);
        Assert.Empty(install.Value.Errors);

        var update = Assert.IsType<Ok<PluginOperationReceipt>>(await PluginsEndpointContributor.Update(
            "alpha",
            manager,
            CancellationToken.None));
        Assert.Equal(PluginLifecycleOperation.Update, update.Value!.Operation);
        Assert.Equal(PluginOperationOutcome.Updated, update.Value.Outcome);
        Assert.Equal("alpha", update.Value.Name);
        Assert.Equal("main", update.Value.RequestedReference);
        Assert.Equal("v2", update.Value.ResolvedCommit);
        Assert.True(update.Value.UpdatesEnabled);
        Assert.Equal("v1", update.Value.PreviousVersion);
        Assert.Empty(update.Value.Errors);
        Assert.Equal("new", File.ReadAllText(Path.Combine(_root, "alpha", "payload.txt")));

        var pin = Assert.IsType<Ok<PluginOperationReceipt>>(PluginsEndpointContributor.Pin("alpha", manager));
        Assert.Equal(PluginLifecycleOperation.SetUpdatePreference, pin.Value!.Operation);
        Assert.Equal(PluginOperationOutcome.SkippedPinned, pin.Value.Outcome);
        Assert.False(pin.Value.UpdatesEnabled);
        Assert.False(store.Find("alpha")!.UpdatesEnabled);

        var unpin = Assert.IsType<Ok<PluginOperationReceipt>>(PluginsEndpointContributor.Unpin("alpha", manager));
        Assert.Equal(PluginLifecycleOperation.SetUpdatePreference, unpin.Value!.Operation);
        Assert.Equal(PluginOperationOutcome.Installed, unpin.Value.Outcome);
        Assert.True(unpin.Value.UpdatesEnabled);
        Assert.True(store.Find("alpha")!.UpdatesEnabled);

        var remove = Assert.IsType<Ok<PluginOperationReceipt>>(PluginsEndpointContributor.Remove("alpha", manager));
        Assert.Equal(PluginLifecycleOperation.Remove, remove.Value!.Operation);
        Assert.Equal(PluginOperationOutcome.Removed, remove.Value.Outcome);
        Assert.Equal("v2", remove.Value.PreviousVersion);
        Assert.Null(remove.Value.ResolvedCommit);
        Assert.Null(remove.Value.UpdatesEnabled);
        Assert.Null(store.Find("alpha"));
    }

    [Fact]
    public async Task CredentialBearingFetchFailureReturnsOnlyASanitizedBoundedReceipt()
    {
        const string source = "https://alice:secret@example.test/private.git?token=query-secret";
        var fetcher = new ThrowingFetcher("fatal: could not read from https://alice:secret@example.test/private.git?token=query-secret");
        var manager = new PluginLifecycleManager(new PluginStateStore(_root), fetcher);

        var response = Assert.IsType<BadRequest<PluginOperationReceipt>>(await PluginsEndpointContributor.Install(
            new PluginInstallRequest { Source = source, Name = "private", Reference = "main" },
            manager,
            CancellationToken.None));
        var receipt = response.Value!;
        var json = JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(PluginLifecycleOperation.Install, receipt.Operation);
        Assert.Equal(PluginOperationOutcome.Failed, receipt.Outcome);
        Assert.Equal("private", receipt.Name);
        Assert.Equal("main", receipt.RequestedReference);
        Assert.Single(receipt.Errors);
        Assert.DoesNotContain(source, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alice", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("query-secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fatal:", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private.git", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(receipt.Errors[0].Message.Length <= 256);
    }

    private static IReadOnlyDictionary<string, string> PluginContent(string name, string payload) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".botnexus-plugin/plugin.json"] = $$"""{ "name": "{{name}}" }""",
            ["payload.txt"] = payload,
        };

    private sealed class ThrowingFetcher(string diagnostic) : IPluginSourceFetcher
    {
        public Task<PluginFetchResult> FetchAsync(
            string source,
            string? reference,
            string stagingDirectory,
            CancellationToken cancellationToken = default) =>
            throw new IOException(diagnostic);
    }

    private sealed class ScriptedFetcher : IPluginSourceFetcher
    {
        private readonly Queue<(string Version, IReadOnlyDictionary<string, string> Files)> _fetches = new();

        public void Enqueue(string version, IReadOnlyDictionary<string, string> files) =>
            _fetches.Enqueue((version, files));

        public Task<PluginFetchResult> FetchAsync(
            string source,
            string? reference,
            string stagingDirectory,
            CancellationToken cancellationToken = default)
        {
            var fetch = _fetches.Dequeue();
            foreach (var (relativePath, content) in fetch.Files)
            {
                var path = Path.Combine(
                    stagingDirectory,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
            }

            return Task.FromResult(new PluginFetchResult(fetch.Version));
        }
    }
}
