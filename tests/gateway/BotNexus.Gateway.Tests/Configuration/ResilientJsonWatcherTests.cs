using System.Text;
using System.Text.Json;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace BotNexus.Gateway.Tests.Configuration;

public sealed class ResilientJsonWatcherTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reload_AcceptsRawDocumentBeforeOptionsNotification(bool watcher)
    {
        using var file = new ControlledFileProvider(Document("repo-a", "first"));
        using var root = Build(file);
        using var services = OptionsServices(root);
        var monitor = services.GetRequiredService<IOptionsMonitor<PlatformConfig>>();
        var resolver = new DefaultLocationResolver(monitor);
        resolver.ResolvePath("repo-root").ShouldBe(Path.GetFullPath("repo-a"));
        List<(string? Bound, string? Resolved, string? Raw)> observations = [];
        using var subscription = monitor.OnChange((config, _) => observations.Add((
            root["gateway:locations:repo-root:path"],
            resolver.ResolvePath("repo-root"),
            RawValue(config))));

        file.Set(Document("repo-b", "second"));
        if (watcher)
            file.SignalChange(); // Framework's registered watcher callback, NOT root.Reload().
        else
            root.Reload();

        observations.ShouldNotBeEmpty();
        observations.ShouldAllBe(value => value.Bound == "repo-b" && value.Resolved == Path.GetFullPath("repo-b") && value.Raw == "second");
        RawValue(monitor.CurrentValue).ShouldBe("second");
        resolver.ResolvePath("repo-root").ShouldBe(Path.GetFullPath("repo-b"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Watcher_RejectsBadCandidateBeforeNotification_AndRecovers(bool malformed)
    {
        using var file = new ControlledFileProvider(Document("repo-a", "first"));
        List<(string Message, Exception? Error)> failures = [];
        using var root = Build(file, (message, error) => failures.Add((message, error)));
        using var services = OptionsServices(root);
        var monitor = services.GetRequiredService<IOptionsMonitor<PlatformConfig>>();
        _ = monitor.CurrentValue;
        List<(string? Bound, string? Raw)> observations = [];
        using var subscription = monitor.OnChange((config, _) => observations.Add((
            root["gateway:locations:repo-root:path"], RawValue(config))));

        file.Set(Document("repo-b", "second"));
        file.SignalChange();
        observations.Clear();
        file.Set(malformed ? "{{ broken" : Document("rejected", "rejected", "ftp://not-http"));
        Should.NotThrow(file.SignalChange);

        root["gateway:locations:repo-root:path"].ShouldBe("repo-b");
        RawValue(monitor.CurrentValue).ShouldBe("second");
        observations.ShouldAllBe(value => value.Bound == "repo-b" && value.Raw == "second");
        var failure = failures.ShouldHaveSingleItem();
        failure.Message.ShouldContain("previous configuration is being retained", Case.Insensitive);
        if (malformed)
            failure.Error.ShouldNotBeNull();
        else
            failure.Error.ShouldBeNull();

        observations.Clear();
        file.Set(Document("repo-c", "third"));
        file.SignalChange();
        observations.ShouldNotBeEmpty();
        observations.ShouldAllBe(value => value.Bound == "repo-c" && value.Raw == "third");
        RawValue(monitor.CurrentValue).ShouldBe("third");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"quoted string\"")]
    [InlineData("42")]
    public void Watcher_RejectsNonObjectCandidateBeforeNotification_AndRecovers(string json)
    {
        using var file = new ControlledFileProvider(Document("repo-a", "first"));
        List<(string Message, Exception? Error)> failures = [];
        using var root = Build(file, (message, error) => failures.Add((message, error)));
        using var services = OptionsServices(root);
        var monitor = services.GetRequiredService<IOptionsMonitor<PlatformConfig>>();
        var resolver = new DefaultLocationResolver(monitor);
        resolver.ResolvePath("repo-root").ShouldBe(Path.GetFullPath("repo-a"));
        List<(string? Bound, string? Resolved, string? Raw)> observations = [];
        using var subscription = monitor.OnChange((config, _) => observations.Add((
            root["gateway:locations:repo-root:path"], resolver.ResolvePath("repo-root"), RawValue(config))));

        file.Set(Document("repo-b", "second"));
        file.SignalChange();
        observations.Clear();
        file.Set(json);
        Should.NotThrow(file.SignalChange);

        root["gateway:locations:repo-root:path"].ShouldBe("repo-b");
        resolver.ResolvePath("repo-root").ShouldBe(Path.GetFullPath("repo-b"));
        RawValue(monitor.CurrentValue).ShouldBe("second");
        observations.ShouldAllBe(value => value.Bound == "repo-b" && value.Resolved == Path.GetFullPath("repo-b") && value.Raw == "second");
        var failure = failures.ShouldHaveSingleItem();
        failure.Message.ShouldContain("previous configuration is being retained", Case.Insensitive);
        failure.Error.ShouldBeOfType<InvalidOperationException>();

        observations.Clear();
        file.Set(Document("repo-c", "third"));
        file.SignalChange();
        observations.ShouldNotBeEmpty();
        observations.ShouldAllBe(value => value.Bound == "repo-c" && value.Resolved == Path.GetFullPath("repo-c") && value.Raw == "third");
        root["gateway:locations:repo-root:path"].ShouldBe("repo-c");
        resolver.ResolvePath("repo-root").ShouldBe(Path.GetFullPath("repo-c"));
        RawValue(monitor.CurrentValue).ShouldBe("third");
        failures.Count.ShouldBe(1);
    }

    // A non-raw consumer pins the issue's exact canonical locations shape independently of
    // extension raw-shape normalization. This must remain the real options/resolver pipeline.
    [Fact]
    public void Watcher_CanonicalLocationsReachResolver()
    {
        using var file = new ControlledFileProvider(Document("repo-a", "first"));
        using var root = Build(file);
        using var services = OptionsServices(root);
        var monitor = services.GetRequiredService<IOptionsMonitor<PlatformConfig>>();
        var resolver = new DefaultLocationResolver(monitor);
        resolver.ResolvePath("repo-root").ShouldBe(Path.GetFullPath("repo-a"));
        List<string?> observed = [];
        using var subscription = monitor.OnChange((config, _) =>
            observed.Add(config.Gateway?.Locations?["repo-root"].Path));
        file.Set(Document("repo-b", "second"));
        file.SignalChange();
        observed.ShouldHaveSingleItem().ShouldBe("repo-b");
        resolver.ResolvePath("repo-root").ShouldBe(Path.GetFullPath("repo-b"));
    }

    private static string Document(string path, string value, string listenUrl = "http://localhost:5099") =>
        JsonSerializer.Serialize(new
        {
            version = 1,
            gateway = new
            {
                listenUrl,
                locations = new Dictionary<string, object> { ["repo-root"] = new { type = "filesystem", path } }
            },
            world = new { extensions = new { sample = new { value } } }
        });

    private static string? RawValue(PlatformConfig config) =>
        config.World?.Extensions?["sample"].GetProperty("value").GetString();

    private static ConfigurationRoot Build(ControlledFileProvider file, Action<string, Exception?>? failure = null) =>
        (ConfigurationRoot)new ConfigurationBuilder().Add(new ResilientJsonConfigurationSource
        {
            FileProvider = file,
            Path = "config.json",
            Optional = false,
            ReloadOnChange = true,
            ReloadDelay = 0,
            OnLoadFailure = failure
        }).Build();

    private static ServiceProvider OptionsServices(IConfigurationRoot root)
    {
        var services = new ServiceCollection();
        services.AddOptions<PlatformConfig>().Bind(root);
        services.AddSingleton<IPostConfigureOptions<PlatformConfig>>(new PlatformConfigPostConfigure(root));
        services.AddSingleton<IValidateOptions<PlatformConfig>, PlatformConfigOptionsValidator>();
        return services.BuildServiceProvider();
    }

    // CancellationTokenSource.Cancel runs the REAL FileConfigurationProvider callback
    // synchronously. Replacing the token first permits ChangeToken.OnChange to re-arm.
    private sealed class ControlledFileProvider(string content) : IFileProvider, IDisposable
    {
        private CancellationTokenSource _changes = new();
        public void Set(string value) => content = value;
        public void SignalChange()
        {
            using var previous = _changes;
            _changes = new CancellationTokenSource();
            previous.Cancel();
        }
        public IChangeToken Watch(string filter) => new CancellationChangeToken(_changes.Token);
        public IFileInfo GetFileInfo(string subpath) => new MemoryFile(content);
        public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;
        public void Dispose() => _changes.Dispose();
    }

    private sealed class MemoryFile(string content) : IFileInfo
    {
        public bool Exists => true;
        public long Length => Encoding.UTF8.GetByteCount(content);
        public string? PhysicalPath => null;
        public string Name => "config.json";
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public bool IsDirectory => false;
        public Stream CreateReadStream() => new MemoryStream(Encoding.UTF8.GetBytes(content));
    }
}
