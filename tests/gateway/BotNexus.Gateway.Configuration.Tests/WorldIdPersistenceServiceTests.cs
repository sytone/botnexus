using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Gateway.Configuration.Writers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BotNexus.Gateway.Configuration.Tests;

public sealed class WorldIdPersistenceServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"botnexus-world-bootstrap-{Guid.NewGuid():N}");
    private readonly FileSystem _real = new();
    private string ConfigPath => Path.Combine(_directory, "config.json");
    private static readonly WorldId Identity = new(Guid.Parse("478420b7-2822-496e-85fd-20b18ad7d987"));

    public WorldIdPersistenceServiceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task StartAsync_UnwritableConfig_FailsWithGuidanceBeforeAcquiringLock()
    {
        File.WriteAllText(ConfigPath, "{}");
        var opened = new List<string>();
        var fileSystem = Substitute.For<IFileSystem>();
        fileSystem.Path.Returns(_real.Path);
        fileSystem.File.Returns(_real.File);
        fileSystem.Directory.Returns(_real.Directory);
        var streams = Substitute.For<IFileStreamFactory>();
        fileSystem.FileStream.Returns(streams);
        streams.New(Arg.Any<string>(), Arg.Any<FileMode>(), Arg.Any<FileAccess>(), Arg.Any<FileShare>())
            .Returns(call =>
            {
                var path = call.ArgAt<string>(0);
                opened.Add(path);
                if (path.EndsWith(".lock", StringComparison.Ordinal))
                    throw new InvalidOperationException("Lock must not be attempted on an unwritable config mount.");
                throw new UnauthorizedAccessException("read-only configuration mount");
            });

        var service = Create(fileSystem, generated: true);
        var error = await Should.ThrowAsync<InvalidOperationException>(() => service.StartAsync(default));

        error.Message.ShouldContain("worldId");
        error.Message.ShouldContain("pre-provision");
        error.InnerException.ShouldBeOfType<UnauthorizedAccessException>();
        opened.ShouldHaveSingleItem().ShouldStartWith(ConfigPath + ".");
        File.ReadAllText(ConfigPath).ShouldBe("{}");
        Directory.GetFiles(_directory).ShouldBe([ConfigPath]);
        Directory.Exists(Path.Combine(_directory, "locks")).ShouldBeFalse();
    }

    [Fact]
    public async Task StartAsync_ExistingIdentity_DoesNotProbeLockOrWrite()
    {
        var fileSystem = Substitute.For<IFileSystem>();
        await Create(fileSystem, generated: false).StartAsync(default);
        fileSystem.ReceivedCalls().ShouldBeEmpty();
        Directory.GetFileSystemEntries(_directory).ShouldBeEmpty();
    }

    [Fact]
    public async Task StartAsync_WritableFirstStart_PersistsResolvedIdentityAndCleansProbe()
    {
        var identity = WorldIdResolver.Resolve(ConfigPath, _real, out var generated);
        generated.ShouldBeTrue();
        var backend = new CountingWriter(new JsonConfigurationWriter(ConfigPath, _real));
        var writer = new PlatformConfigWriter(ConfigPath, _real, backup: null, writer: backend);
        await new WorldIdPersistenceService(writer, identity, new WorldIdOrigin(generated),
            NullLogger<WorldIdPersistenceService>.Instance).StartAsync(default);

        File.Exists(ConfigPath).ShouldBeTrue();
        backend.Calls.ShouldBe(1);
        WorldIdResolver.Resolve(ConfigPath, _real, out generated).Value.ShouldBe(identity.Value);
        generated.ShouldBeFalse();
        var before = File.ReadAllBytes(ConfigPath);
        await new WorldIdPersistenceService(writer, identity, new WorldIdOrigin(generated),
            NullLogger<WorldIdPersistenceService>.Instance).StartAsync(default);
        File.ReadAllBytes(ConfigPath).ShouldBe(before);
        backend.Calls.ShouldBe(1);
        Directory.GetFiles(_directory).ShouldBe([ConfigPath]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_BackendPersistenceFailure_AbortsWithGuidance(bool unauthorized)
    {
        Exception cause = unauthorized ? new UnauthorizedAccessException("backend denied") : new IOException("disk full");
        var backend = new FailingWriter(cause);
        var service = Create(_real, generated: true, backend);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => service.StartAsync(default));

        error.Message.ShouldContain("worldId");
        error.Message.ShouldContain("pre-provision");
        error.InnerException.ShouldBeSameAs(cause);
        backend.Calls.ShouldBe(1);
        File.Exists(ConfigPath).ShouldBeFalse();
        Directory.GetFiles(_directory).ShouldBeEmpty();
    }

    [Fact]
    public async Task StartAsync_BackendCancellation_IsNotWrapped()
    {
        using var cancellation = new CancellationTokenSource();
        var cause = new OperationCanceledException(cancellation.Token);
        OperationCanceledException? error = null;
        try
        {
            await Create(_real, generated: true, new FailingWriter(cause)).StartAsync(cancellation.Token);
        }
        catch (OperationCanceledException caught)
        {
            error = caught;
        }
        error.ShouldBeSameAs(cause);
        Directory.GetFiles(_directory).ShouldBeEmpty();
    }

    [Fact]
    public async Task StartAsync_AlreadyCancelled_DoesNotCreateProbeOrLock()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => Create(_real, generated: true).StartAsync(cancellation.Token));
        Directory.GetFileSystemEntries(_directory).ShouldBeEmpty();
    }

    private WorldIdPersistenceService Create(IFileSystem fileSystem, bool generated, IConfigurationWriter? backend = null)
        => new(new PlatformConfigWriter(ConfigPath, fileSystem, backup: null, writer: backend), Identity,
            new WorldIdOrigin(generated), NullLogger<WorldIdPersistenceService>.Instance);

    private sealed class CountingWriter(IConfigurationWriter inner) : IConfigurationWriter
    {
        public string Name => inner.Name;
        public int Calls { get; private set; }
        public Task WriteAsync(JsonObject document, string reason, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Bootstrap must use the canonical change-set pipeline.");
        public Task ApplyChangeSetAsync(ConfigChangeSet changes, string reason, CancellationToken cancellationToken = default)
        {
            changes.Upserts.ShouldHaveSingleItem();
            Calls++;
            return inner.ApplyChangeSetAsync(changes, reason, cancellationToken);
        }
    }

    private sealed class FailingWriter(Exception cause) : IConfigurationWriter
    {
        public string Name => "failing-test-backend";
        public int Calls { get; private set; }
        public Task WriteAsync(JsonObject document, string reason, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Bootstrap must use the canonical change-set pipeline.");
        public Task ApplyChangeSetAsync(ConfigChangeSet changes, string reason, CancellationToken cancellationToken = default)
        {
            changes.Upserts.ShouldHaveSingleItem();
            Calls++;
            return Task.FromException(cause);
        }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
