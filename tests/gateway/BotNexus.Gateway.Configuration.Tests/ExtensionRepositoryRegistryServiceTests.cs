using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Gateway.Configuration.Writers;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// Defines the configuration-only contract for extension repository registration (#3899).
/// Registration deliberately stops before clone, build, deployment, or third-party execution.
/// </summary>
public sealed class ExtensionRepositoryRegistryServiceTests : IDisposable
{
    private readonly string _directory;
    private readonly string _configPath;
    private readonly string _storePath;
    private readonly IFileSystem _fileSystem = new FileSystem();

    public ExtensionRepositoryRegistryServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"botnexus-extension-repositories-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _configPath = Path.Combine(_directory, "config.json");
        _storePath = ConfigStoreBootstrap.ResolveStorePath(_configPath, _fileSystem);
    }

    private ExtensionRepositoryRegistryService CreateService()
        => new(_configPath, _fileSystem);

    [Fact]
    public async Task AddAsync_ValidRegistration_PersistsAllFieldsWithEnabledDefaults()
    {
        var service = CreateService();

        await service.AddAsync("community-tools", "https://github.com/example/tools.git", "main");

        var registration = (await service.ListAsync()).ShouldHaveSingleItem();
        registration.Id.ShouldBe("community-tools");
        registration.RepositoryUrl.ShouldBe("https://github.com/example/tools.git");
        registration.RequestedRef.ShouldBe("main");
        registration.Enabled.ShouldBeTrue();
        registration.UpdatesEnabled.ShouldBeTrue();

        var config = await ConfigWriterFactory.Create(_configPath, _fileSystem).ReadPlatformConfigAsync();
        config.ExtensionRepositories.ShouldNotBeNull();
        config.ExtensionRepositories.ShouldContainKey("community-tools");
        config.ExtensionRepositories["community-tools"].RepositoryUrl.ShouldBe(registration.RepositoryUrl);
        config.ExtensionRepositories["community-tools"].RequestedRef.ShouldBe(registration.RequestedRef);
        config.ExtensionRepositories["community-tools"].Enabled.ShouldBeTrue();
        config.ExtensionRepositories["community-tools"].UpdatesEnabled.ShouldBeTrue();
    }

    [Fact]
    public async Task ListAsync_MultipleRegistrations_ReturnsDeterministicIdOrder()
    {
        var service = CreateService();
        await service.AddAsync("zeta-tools", "ssh://git@example.test/zeta.git", "release/1");
        await service.AddAsync("alpha", "http://example.test/alpha.git", "v1.2.3", enabled: false, updatesEnabled: false);

        var registrations = await service.ListAsync();

        registrations.Select(item => item.Id).ShouldBe(["alpha", "zeta-tools"]);
        registrations[0].Enabled.ShouldBeFalse();
        registrations[0].UpdatesEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task UpdateAndSetEnabledAsync_OnlySpecifiedFieldsChange()
    {
        var service = CreateService();
        await service.AddAsync("community-tools", "https://example.test/old.git", "main");

        await service.UpdateAsync(
            "community-tools",
            repositoryUrl: null,
            requestedRef: "release/2",
            updatesEnabled: false);
        await service.SetEnabledAsync("community-tools", enabled: false);

        var registration = (await service.ListAsync()).ShouldHaveSingleItem();
        registration.RepositoryUrl.ShouldBe("https://example.test/old.git");
        registration.RequestedRef.ShouldBe("release/2");
        registration.Enabled.ShouldBeFalse();
        registration.UpdatesEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task AddListAndUpdate_StoreOnlyInstallation_PersistThroughSqliteConfigStore()
    {
        await ConfigStoreBootstrap.PopulateAsync(
            _storePath,
            JsonNode.Parse("""{"version":1,"gateway":{"defaultTimezone":"UTC"}}""")!.AsObject());
        ConfigStoreBootstrap.ReleaseConnections(_storePath);
        File.Exists(_configPath).ShouldBeFalse();

        var service = CreateService();
        await service.AddAsync("store-only", "https://example.test/extensions.git", "main");
        (await service.ListAsync()).ShouldHaveSingleItem().Id.ShouldBe("store-only");
        await service.UpdateAsync("store-only", "ssh://git@example.test/extensions.git", "stable", false);

        File.Exists(_configPath).ShouldBeTrue(
            "the existing configuration writer may recreate its synchronized JSON projection; " +
            "the command must still succeed when the operation starts with no config.json");
        var stored = await new SqliteConfigStore($"Data Source={_storePath}").ReadEntriesAsync();
        stored["extensionRepositories.store-only.repositoryUrl"].Value.ShouldBe("\"ssh://git@example.test/extensions.git\"");
        stored["extensionRepositories.store-only.requestedRef"].Value.ShouldBe("\"stable\"");
        stored["extensionRepositories.store-only.enabled"].Value.ShouldBe("true");
        stored["extensionRepositories.store-only.updatesEnabled"].Value.ShouldBe("false");
        stored["gateway.defaultTimezone"].Value.ShouldBe("\"UTC\"");
    }

    [Theory]
    [InlineData("")]
    [InlineData("UPPER")]
    [InlineData("two_words")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("two--hyphens")]
    [InlineData("has space")]
    public async Task AddAsync_InvalidId_IsActionableAndDoesNotMutate(string id)
    {
        var service = CreateService();
        var before = await ConfigWriterFactory.Create(_configPath, _fileSystem).ReadAsync();

        Func<Task> act = () => service.AddAsync(id, "https://example.test/repo.git", "main");

        var error = await Should.ThrowAsync<ArgumentException>(act);
        error.Message.ShouldContain("id");
        error.Message.ShouldContain("lowercase");
        (await service.ListAsync()).ShouldBeEmpty();
        (await ConfigWriterFactory.Create(_configPath, _fileSystem).ReadAsync()).ToJsonString().ShouldBe(before.ToJsonString());
    }

    [Theory]
    [InlineData("example.test/repo.git")]
    [InlineData("file:///tmp/repo")]
    [InlineData("ftp://example.test/repo.git")]
    [InlineData("git@example.test:repo.git")]
    public async Task AddAsync_InvalidRepositoryUrl_IsActionableAndDoesNotMutate(string url)
    {
        var service = CreateService();

        Func<Task> act = () => service.AddAsync("valid-id", url, "main");

        var error = await Should.ThrowAsync<ArgumentException>(act);
        error.Message.ShouldContain("URL");
        error.Message.ShouldContain("http");
        error.Message.ShouldContain("ssh");
        (await service.ListAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("two words")]
    [InlineData("line\nbreak")]
    [InlineData("-dangerous")]
    public async Task AddAsync_InvalidRequestedRef_IsActionableAndDoesNotMutate(string requestedRef)
    {
        var service = CreateService();

        Func<Task> act = () => service.AddAsync("valid-id", "https://example.test/repo.git", requestedRef);

        var error = await Should.ThrowAsync<ArgumentException>(act);
        error.Message.ShouldContain("ref");
        (await service.ListAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task DuplicateAndMissingIds_ReturnActionableErrorsWithoutMutation()
    {
        var service = CreateService();
        await service.AddAsync("existing", "https://example.test/repo.git", "main");
        var before = (await ConfigWriterFactory.Create(_configPath, _fileSystem).ReadAsync()).ToJsonString();

        Func<Task> duplicate = () => service.AddAsync("existing", "https://example.test/other.git", "next");
        (await Should.ThrowAsync<InvalidOperationException>(duplicate)).Message.ShouldContain("already exists");

        Func<Task> updateMissing = () => service.UpdateAsync("missing", null, "next", null);
        (await Should.ThrowAsync<KeyNotFoundException>(updateMissing)).Message.ShouldContain("missing");
        Func<Task> enableMissing = () => service.SetEnabledAsync("missing", true);
        (await Should.ThrowAsync<KeyNotFoundException>(enableMissing)).Message.ShouldContain("missing");
        Func<Task> removeMissing = () => service.RemoveAsync("missing");
        (await Should.ThrowAsync<KeyNotFoundException>(removeMissing)).Message.ShouldContain("missing");

        (await ConfigWriterFactory.Create(_configPath, _fileSystem).ReadAsync()).ToJsonString().ShouldBe(before);
    }

    [Fact]
    public async Task RecordReconciliationStateAsync_PersistsAttemptSuccessAndNamedFailure()
    {
        var service = CreateService();
        await service.AddAsync("community-tools", "https://example.test/repo.git", "main");
        var attempted = new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);
        var succeeded = attempted.AddMinutes(1);

        await service.RecordReconciliationAttemptAsync("community-tools", Path.Combine(_directory, "clone"), attempted);
        await service.RecordReconciliationSuccessAsync("community-tools", new string('a', 40), succeeded);
        await service.RecordReconciliationFailureAsync("community-tools", "dirty", "operator changes");

        var registration = (await service.ListAsync()).ShouldHaveSingleItem();
        registration.ReconciliationStatus.ShouldBe("failed");
        registration.ResolvedCommit.ShouldBe(new string('a', 40));
        registration.ClonePath.ShouldBe(Path.Combine(_directory, "clone"));
        registration.LastAttemptUtc.ShouldBe(attempted);
        registration.LastSuccessUtc.ShouldBe(succeeded);
        registration.LatestFailure.ShouldBe("dirty: operator changes");
    }

    [Fact]
    public async Task RemoveAsync_DeletesOnlyRegistration_AndDoesNotTouchCloneSentinel()
    {
        var cloneDirectory = Path.Combine(_directory, "extensions", "community-tools");
        Directory.CreateDirectory(cloneDirectory);
        var sentinel = Path.Combine(cloneDirectory, "do-not-delete.txt");
        await File.WriteAllTextAsync(sentinel, "unrelated clone contents");
        var service = CreateService();
        await service.AddAsync("community-tools", "https://example.test/repo.git", "main");

        await service.RemoveAsync("community-tools");

        (await service.ListAsync()).ShouldBeEmpty();
        File.Exists(sentinel).ShouldBeTrue("registration removal must never delete a clone or any other directory");
        (await File.ReadAllTextAsync(sentinel)).ShouldBe("unrelated clone contents");
    }

    public void Dispose()
    {
        ConfigStoreBootstrap.ReleaseConnections(_storePath);
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }
}
