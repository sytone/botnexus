using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;
using NSubstitute;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>Bootstrap cleanup cannot change the outcome of publishing a verified authority.</summary>
public sealed class BootstrapCleanupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"bn-bootstrap-cleanup-{Guid.NewGuid():N}");
    private readonly IFileSystem _real = new FileSystem();
    private string ConfigPath => Path.Combine(_directory, "config.json");
    private string StorePath => Path.Combine(_directory, ConfigStoreBootstrap.StoreFileName);

    public BootstrapCleanupTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsureAuthority_FirstBootstrapCleansOnlyOwnedDirectoryAndReopensStably(bool legacy)
    {
        var foreign = CreateForeignResidue();
        if (legacy)
            await File.WriteAllTextAsync(ConfigPath, "{\"version\":7,\"unknown\":{\"explicitNull\":null}}");

        var first = await ConfigStoreBootstrap.EnsureAuthorityAsync(ConfigPath, _real);
        var expected = legacy
            ? JsonNode.Parse("{\"version\":7,\"unknown\":{\"explicitNull\":null}}")
            : new JsonObject();
        JsonNode.DeepEquals(ConfigDocumentRehydrator.Rehydrate(await first.ReadEntriesAsync()), expected).ShouldBeTrue();
        File.Exists(StorePath).ShouldBeTrue();
        Directory.GetDirectories(_directory, ".config-bootstrap-*").ShouldBe(new[] { foreign });
        (await File.ReadAllTextAsync(Path.Combine(foreign, ConfigStoreBootstrap.StoreFileName))).ShouldBe("foreign-incomplete-store");
        File.Exists(ConfigPath).ShouldBe(legacy);

        // A subsequent open must use the published database, not try another JSON import.
        await File.WriteAllTextAsync(ConfigPath, "not-json");
        var reopened = await ConfigStoreBootstrap.EnsureAuthorityAsync(ConfigPath, _real);
        JsonNode.DeepEquals(ConfigDocumentRehydrator.Rehydrate(await reopened.ReadEntriesAsync()), expected).ShouldBeTrue();
        Directory.GetDirectories(_directory, ".config-bootstrap-*").ShouldBe(new[] { foreign });
        if (legacy)
        {
            var backup = new ConfigBackupService(Path.Combine(_directory, "backups"), _real).List().ShouldHaveSingleItem();
            (await File.ReadAllTextAsync(backup.FullPath)).ShouldBe("{\"version\":7,\"unknown\":{\"explicitNull\":null}}");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsureAuthority_PublicationFailureRemainsPrimaryWhenCleanupAlsoFails(bool accessDenied)
    {
        var primary = new IOException("publication rejected");
        Exception cleanup = accessDenied ? new UnauthorizedAccessException("cleanup rejected") : new IOException("cleanup rejected");
        var fileSystem = WithCleanupFailure(cleanup);
        var file = Substitute.For<IFile>();
        file.Exists(Arg.Any<string>()).Returns(call => File.Exists(call.Arg<string>()));
        file.When(value => value.Move(Arg.Any<string>(), Arg.Any<string>())).Do(_ => throw primary);
        file.When(value => value.Delete(Arg.Any<string>())).Do(_ => throw cleanup);
        fileSystem.File.Returns(file);

        var actual = await Should.ThrowAsync<IOException>(() => ConfigStoreBootstrap.EnsureAuthorityAsync(ConfigPath, fileSystem));

        actual.ShouldBeSameAs(primary);
        File.Exists(StorePath).ShouldBeFalse();
        var residue = Directory.GetDirectories(_directory, ".config-bootstrap-*").ShouldHaveSingleItem();
        // Real guarded SQLite initialization ran before the injected publication failure.
        Directory.GetFiles(residue, "*.botnexus-fk-validation*").ShouldNotBeEmpty();
        Directory.GetFiles(residue, ConfigStoreBootstrap.StoreFileName).ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsureAuthority_CleanupFailureAfterPublicationReturnsUsableAuthority(bool accessDenied)
    {
        Exception cleanup = accessDenied ? new UnauthorizedAccessException("cleanup rejected") : new IOException("cleanup rejected");
        var fileSystem = WithCleanupFailure(cleanup);

        var published = await ConfigStoreBootstrap.EnsureAuthorityAsync(ConfigPath, fileSystem);

        File.Exists(StorePath).ShouldBeTrue();
        (await published.ReadEntriesAsync()).ShouldBeEmpty();
        var residue = Directory.GetDirectories(_directory, ".config-bootstrap-*").ShouldHaveSingleItem();
        Directory.GetFiles(residue, "*.botnexus-fk-validation*").ShouldNotBeEmpty();
        File.Exists(Path.Combine(residue, ConfigStoreBootstrap.StoreFileName)).ShouldBeFalse();
        // Reopening never needs to clean another attempt's residue or consult a projection.
        var reopened = await ConfigStoreBootstrap.EnsureAuthorityAsync(ConfigPath, _real);
        (await reopened.ReadEntriesAsync()).ShouldBeEmpty();
        Directory.Exists(residue).ShouldBeTrue();
        File.Exists(ConfigPath).ShouldBeFalse();
    }

    [Fact]
    public async Task EnsureAuthority_PublicationFailureCleansOwnedGuardSidecarsAndPreservesForeignResidue()
    {
        var foreign = CreateForeignResidue();
        var primary = new IOException("publication rejected");
        var fileSystem = Substitute.For<IFileSystem>();
        fileSystem.Path.Returns(_real.Path);
        fileSystem.Directory.Returns(_real.Directory);
        fileSystem.FileStream.Returns(_real.FileStream);
        var file = Substitute.For<IFile>();
        file.Exists(Arg.Any<string>()).Returns(call => File.Exists(call.Arg<string>()));
        file.When(value => value.Move(Arg.Any<string>(), Arg.Any<string>())).Do(_ => throw primary);
        file.When(value => value.Delete(Arg.Any<string>())).Do(call => File.Delete(call.Arg<string>()));
        fileSystem.File.Returns(file);

        var actual = await Should.ThrowAsync<IOException>(() => ConfigStoreBootstrap.EnsureAuthorityAsync(ConfigPath, fileSystem));

        actual.ShouldBeSameAs(primary);
        File.Exists(StorePath).ShouldBeFalse();
        Directory.GetDirectories(_directory, ".config-bootstrap-*").ShouldBe(new[] { foreign });
        (await File.ReadAllTextAsync(Path.Combine(foreign, ConfigStoreBootstrap.StoreFileName))).ShouldBe("foreign-incomplete-store");
    }

    private IFileSystem WithCleanupFailure(Exception error)
    {
        var fileSystem = Substitute.For<IFileSystem>();
        fileSystem.Path.Returns(_real.Path);
        fileSystem.File.Returns(_real.File);
        fileSystem.FileStream.Returns(_real.FileStream);
        var directory = Substitute.For<IDirectory>();
        directory.CreateDirectory(Arg.Any<string>()).Returns(call => _real.Directory.CreateDirectory(call.Arg<string>()));
        directory.Exists(Arg.Any<string>()).Returns(call => Directory.Exists(call.Arg<string>()));
        directory.EnumerateFiles(Arg.Any<string>()).Returns(call => _real.Directory.EnumerateFiles(call.Arg<string>()));
        directory.When(value => value.Delete(Arg.Any<string>())).Do(_ => throw error);
        directory.When(value => value.Delete(Arg.Any<string>(), Arg.Any<bool>())).Do(_ => throw error);
        fileSystem.Directory.Returns(directory);
        return fileSystem;
    }

    private string CreateForeignResidue()
    {
        var foreign = Path.Combine(_directory, ".config-bootstrap-foreign");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, ConfigStoreBootstrap.StoreFileName), "foreign-incomplete-store");
        return foreign;
    }

    public void Dispose()
    {
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        Directory.Delete(_directory, recursive: true);
    }
}
