using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Isolation;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Issue #2134: configuration callers must not build a broad section/document snapshot outside the
/// <see cref="PlatformConfigWriter"/> lock and then replace configuration state with it. The
/// read-modify-write window has to sit inside the mutual exclusion, otherwise a concurrent writer's
/// committed change is silently discarded.
/// </summary>
public sealed class PlatformConfigConcurrentWriteTests : IDisposable
{
    private readonly string _rootPath;
    private readonly IFileSystem _fileSystem = new FileSystem();

    public PlatformConfigConcurrentWriteTests()
    {
        _rootPath = Path.Combine(
            Path.GetTempPath(), "botnexus-config-concurrency", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
    }

    /// <summary>
    /// The observable property: concurrent location creates must all survive. Every writer is
    /// released from the same <see cref="Barrier"/> so they contend for the writer lock in a single
    /// burst rather than being spaced out by a sleep; whichever order they win the lock in, the
    /// committed document must contain every entry.
    /// </summary>
    [Fact]
    public async Task ConcurrentLocationCreates_AllEntriesSurvive()
    {
        const int writerCount = 8;
        var configPath = WriteConfig("""{"gateway":{"locations":{}}}""");

        using var startBarrier = new Barrier(writerCount);
        var tasks = Enumerable.Range(0, writerCount).Select(index => Task.Run(async () =>
        {
            var controller = CreateController(configPath);
            startBarrier.SignalAndWait();
            return await controller.Create(new UpsertLocationRequest
            {
                Name = $"repo-{index}",
                Type = "filesystem",
                Value = Path.Combine(_rootPath, $"repo-{index}")
            }, CancellationToken.None);
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        foreach (var result in results)
            result.Result.ShouldBeOfType<CreatedAtActionResult>();

        var persisted = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
        var locations = persisted.Gateway?.Locations;
        locations.ShouldNotBeNull();

        for (var index = 0; index < writerCount; index++)
        {
            locations.ShouldContainKey($"repo-{index}");
            locations[$"repo-{index}"].Path.ShouldBe(Path.Combine(_rootPath, $"repo-{index}"));
        }

        locations.Count.ShouldBe(writerCount);
    }

    /// <summary>
    /// Concurrent deletes must not resurrect each other's entries: the same lost-update defect in
    /// the opposite direction (a stale snapshot re-writes a location another request removed).
    /// </summary>
    [Fact]
    public async Task ConcurrentLocationDeletes_AllRemovalsSurvive()
    {
        const int writerCount = 6;
        var seed = new JsonObject();
        for (var index = 0; index < writerCount; index++)
        {
            seed[$"repo-{index}"] = new JsonObject
            {
                ["type"] = "filesystem",
                ["path"] = Path.Combine(_rootPath, $"repo-{index}")
            };
        }

        var configPath = WriteConfig(new JsonObject
        {
            ["gateway"] = new JsonObject { ["locations"] = seed }
        }.ToJsonString());

        using var startBarrier = new Barrier(writerCount);
        var tasks = Enumerable.Range(0, writerCount).Select(index => Task.Run(async () =>
        {
            var controller = CreateController(configPath);
            startBarrier.SignalAndWait();
            return await controller.Delete($"repo-{index}", CancellationToken.None);
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        foreach (var result in results)
            result.ShouldBeOfType<NoContentResult>();

        var persisted = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
        (persisted.Gateway?.Locations is null || persisted.Gateway.Locations.Count == 0)
            .ShouldBeTrue("Every concurrent delete must survive; a stale snapshot must not resurrect a removed location.");
    }

    /// <summary>
    /// A location create must not clobber an unrelated section a concurrent writer committed in
    /// between: the mutation applies to the live document under the lock, not to a snapshot the
    /// caller read earlier.
    /// </summary>
    [Fact]
    public async Task LocationCreate_DoesNotClobberConcurrentUnrelatedSectionWrite()
    {
        var configPath = WriteConfig("""{"gateway":{"locations":{}}}""");
        var controller = CreateController(configPath);
        var writer = new PlatformConfigWriter(configPath, _fileSystem);

        // Commit an unrelated root section AFTER the controller was constructed (i.e. after any
        // snapshot a stale implementation would have taken) but BEFORE the create is issued.
        await writer.UpdateSectionEntryAsync(
            "providers",
            "github-copilot",
            new JsonObject { ["type"] = "github-copilot" });

        var create = await controller.Create(new UpsertLocationRequest
        {
            Name = "repo",
            Type = "filesystem",
            Value = Path.Combine(_rootPath, "repo")
        }, CancellationToken.None);
        create.Result.ShouldBeOfType<CreatedAtActionResult>();

        var root = await writer.ReadAsync();
        root["providers"]!["github-copilot"]!["type"]!.GetValue<string>().ShouldBe("github-copilot");
        root["gateway"]!["locations"]!["repo"].ShouldNotBeNull();
    }

    /// <summary>
    /// Sad path: a whole-document replace built from a snapshot that is no longer current must be
    /// rejected loudly rather than silently discarding the other writer's committed change.
    /// </summary>
    [Fact]
    public async Task UpdatePlatformConfig_WithStaleRevision_ThrowsConcurrencyException()
    {
        var configPath = WriteConfig("""{"gateway":{"locations":{}}}""");
        var writer = new PlatformConfigWriter(configPath, _fileSystem);

        var (snapshot, revision) = await writer.ReadPlatformConfigWithRevisionAsync();

        // A different writer commits between the read and the replace.
        await writer.UpdateSectionEntryAsync(
            "providers",
            "github-copilot",
            new JsonObject { ["type"] = "github-copilot" });

        snapshot.Gateway ??= new GatewaySettingsConfig();
        snapshot.Gateway.Locations = new Dictionary<string, LocationConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["repo"] = new() { Type = "filesystem", Path = Path.Combine(_rootPath, "repo") }
        };

        var exception = await Should.ThrowAsync<PlatformConfigConcurrencyException>(
            () => writer.UpdatePlatformConfigAsync(snapshot, "stale-replace", CancellationToken.None, revision));

        exception.ExpectedRevision.ShouldBe(revision);
        exception.ActualRevision.ShouldNotBe(revision);

        // The rejected write must have left the other writer's change intact and must not have
        // applied its own.
        var root = await writer.ReadAsync();
        root["providers"]!["github-copilot"].ShouldNotBeNull();
        (root["gateway"]?["locations"]?["repo"]).ShouldBeNull();
    }

    /// <summary>
    /// Happy path for the compare-and-swap guard: an up-to-date revision is accepted, so the guard
    /// is a genuine conflict detector and not a blanket rejection.
    /// </summary>
    [Fact]
    public async Task UpdatePlatformConfig_WithCurrentRevision_Succeeds()
    {
        var configPath = WriteConfig("""{"gateway":{"locations":{}}}""");
        var writer = new PlatformConfigWriter(configPath, _fileSystem);

        var (snapshot, revision) = await writer.ReadPlatformConfigWithRevisionAsync();
        snapshot.Gateway ??= new GatewaySettingsConfig();
        snapshot.Gateway.Locations = new Dictionary<string, LocationConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["repo"] = new() { Type = "filesystem", Path = Path.Combine(_rootPath, "repo") }
        };

        await writer.UpdatePlatformConfigAsync(snapshot, "fresh-replace", CancellationToken.None, revision);

        var persisted = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
        persisted.Gateway!.Locations!.ShouldContainKey("repo");
    }

    /// <summary>
    /// A section mutation that aborts must leave the document byte-for-byte unchanged.
    /// </summary>
    [Fact]
    public async Task MutateSectionAsync_WhenMutationAborts_LeavesDocumentUntouched()
    {
        var configPath = WriteConfig("""{"gateway":{"locations":{"repo":{"type":"filesystem","path":"C:\\repos"}}}}""");
        var writer = new PlatformConfigWriter(configPath, _fileSystem);
        var before = await File.ReadAllBytesAsync(configPath);

        var errors = await writer.MutateSectionAsync(
            "gateway",
            gateway =>
            {
                gateway["locations"] = new JsonObject();
                return "aborted on purpose";
            },
            "abort-test");

        errors.ShouldContain("aborted on purpose");
        (await File.ReadAllBytesAsync(configPath)).ShouldBe(before);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_rootPath, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    private LocationsController CreateController(string configPath)
        => new(
            new PlatformConfigWriter(configPath, _fileSystem),
            new TestOptionsMonitor<PlatformConfig>(new PlatformConfig()),
            new EmptyAgentRegistry(),
            Array.Empty<IIsolationStrategy>(),
            new StubHttpClientFactory());

    private sealed class EmptyAgentRegistry : IAgentRegistry
    {
        public void Register(AgentDescriptor descriptor) { }
        public void Unregister(AgentId agentId) { }
        public bool Update(AgentId agentId, AgentDescriptor descriptor) => false;
        public AgentDescriptor? Get(AgentId agentId) => null;
        public IReadOnlyList<AgentDescriptor> GetAll() => [];
        public bool Contains(AgentId agentId) => false;
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
