using System.Text.Json;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Regression coverage for #2358: a malformed or invalid <c>config.json</c> written AFTER the host
/// has started must not crash the running host on the reload path. The startup guard in
/// <c>Program.cs</c> is startup-only; these tests exercise the reload path directly and assert the
/// host survives and retains the previous (last-known-good) configuration.
/// </summary>
public sealed class ResilientJsonConfigurationReloadTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "bn-2358-" + Guid.NewGuid().ToString("N"));

    private string ConfigPath => Path.Combine(_directory, "config.json");

    public ResilientJsonConfigurationReloadTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }

    private const string GoodConfig = """
        {
          "version": 1,
          "gateway": { "listenUrl": "http://localhost:5099", "logLevel": "Information" }
        }
        """;

    /// <summary>
    /// The exact crash from #2358: the stock <see cref="JsonConfigurationProvider"/> rethrows the
    /// parse failure as <see cref="InvalidDataException"/> when a reload sees malformed JSON (and on
    /// a watcher-driven reload that is raised on a background thread, terminating the host). This
    /// pins the framework behaviour the fix guards against, so the regression cannot silently
    /// evaporate.
    /// </summary>
    [Fact]
    public void StockJsonProvider_ThrowsOnMalformedReload()
    {
        File.WriteAllText(ConfigPath, GoodConfig);

        var root = new ConfigurationBuilder()
            .AddJsonFile(ConfigPath, optional: true, reloadOnChange: false)
            .Build();

        Assert.Equal("http://localhost:5099", root["gateway:listenUrl"]);

        File.WriteAllText(ConfigPath, "{ \"gateway\": { \"listenUrl\": ");

        Assert.Throws<InvalidDataException>(() => root.Reload());
    }

    /// <summary>
    /// Malformed JSON arriving through the RELOAD path must not throw, and the previously loaded
    /// values must still be served.
    /// </summary>
    [Fact]
    public void MalformedJsonOnReload_DoesNotThrow_AndRetainsLastKnownGood()
    {
        File.WriteAllText(ConfigPath, GoodConfig);

        var failures = new List<string>();
        var root = new ConfigurationBuilder()
            .AddResilientJsonFile(
                ConfigPath,
                optional: true,
                reloadOnChange: false,
                onLoadFailure: (message, _) => failures.Add(message))
            .Build();

        Assert.Equal("http://localhost:5099", root["gateway:listenUrl"]);

        File.WriteAllText(ConfigPath, "{ \"gateway\": { \"listenUrl\": ");

        var exception = Record.Exception(() => root.Reload());

        Assert.Null(exception);
        Assert.Equal("http://localhost:5099", root["gateway:listenUrl"]);
        Assert.Equal("Information", root["gateway:logLevel"]);
        Assert.NotEmpty(failures);
        Assert.Contains("previous configuration is being retained", failures[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// Repeated malformed reloads keep returning the same last-known-good values (the rollback is
    /// not a one-shot).
    /// </summary>
    [Fact]
    public void RepeatedMalformedReloads_KeepServingLastKnownGood()
    {
        File.WriteAllText(ConfigPath, GoodConfig);

        var root = new ConfigurationBuilder()
            .AddResilientJsonFile(ConfigPath, optional: true, reloadOnChange: false)
            .Build();

        for (var i = 0; i < 3; i++)
        {
            File.WriteAllText(ConfigPath, "not json at all {{{");
            Assert.Null(Record.Exception(() => root.Reload()));
            Assert.Equal("http://localhost:5099", root["gateway:listenUrl"]);
        }
    }

    /// <summary>
    /// Syntactically valid but semantically invalid config (a listenUrl that is not an absolute
    /// http/https URL - an existing <see cref="PlatformConfigValidator"/> rule) is rejected before
    /// being applied, and the last-known-good configuration is retained. This is the
    /// "validate before applying" half of the fix, reusing the existing startup validation.
    /// </summary>
    [Fact]
    public void InvalidButParseableConfigOnReload_IsRejected_AndRetainsLastKnownGood()
    {
        File.WriteAllText(ConfigPath, GoodConfig);

        var failures = new List<string>();
        var root = new ConfigurationBuilder()
            .AddResilientJsonFile(
                ConfigPath,
                optional: true,
                reloadOnChange: false,
                onLoadFailure: (message, _) => failures.Add(message))
            .Build();

        Assert.Equal("http://localhost:5099", root["gateway:listenUrl"]);

        File.WriteAllText(ConfigPath, """
            {
              "version": 1,
              "gateway": { "listenUrl": "ftp://not-http", "logLevel": "Information" }
            }
            """);

        Assert.Null(Record.Exception(() => root.Reload()));

        Assert.Equal("http://localhost:5099", root["gateway:listenUrl"]);
        Assert.NotEmpty(failures);
        Assert.Contains("failed validation and was rejected", failures[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// A GOOD reload must still be applied - the guard rejects bad candidates, it does not freeze
    /// configuration.
    /// </summary>
    [Fact]
    public void ValidReload_IsApplied()
    {
        File.WriteAllText(ConfigPath, GoodConfig);

        var root = new ConfigurationBuilder()
            .AddResilientJsonFile(ConfigPath, optional: true, reloadOnChange: false)
            .Build();

        File.WriteAllText(ConfigPath, """
            {
              "version": 1,
              "gateway": { "listenUrl": "http://localhost:6001", "logLevel": "Warning" }
            }
            """);

        root.Reload();

        Assert.Equal("http://localhost:6001", root["gateway:listenUrl"]);
        Assert.Equal("Warning", root["gateway:logLevel"]);
    }

    /// <summary>
    /// The end-to-end host-facing shape of the bug: an <see cref="IOptionsMonitor{T}"/> consumer
    /// resolving <c>CurrentValue</c> after a malformed reload must keep working on the
    /// last-known-good <see cref="PlatformConfig"/> rather than seeing an exception.
    /// </summary>
    [Fact]
    public void OptionsMonitorConsumer_SurvivesMalformedReload()
    {
        File.WriteAllText(ConfigPath, GoodConfig);

        var root = new ConfigurationBuilder()
            .AddResilientJsonFile(ConfigPath, optional: true, reloadOnChange: false)
            .Build();

        PlatformConfig Bind()
        {
            var config = new PlatformConfig();
            root.Bind(config);
            return config;
        }

        Assert.Equal("http://localhost:5099", Bind().Gateway?.ListenUrl);

        File.WriteAllText(ConfigPath, "{{{ broken");
        Assert.Null(Record.Exception(() => root.Reload()));

        Assert.Equal("http://localhost:5099", Bind().Gateway?.ListenUrl);
    }

    /// <summary>
    /// Sanity: the fixture JSON really is malformed, so a failing assertion above cannot be an
    /// artefact of an accidentally-valid document.
    /// </summary>
    [Fact]
    public void MalformedFixture_IsActuallyMalformed()
        => Assert.ThrowsAny<JsonException>(
            () => JsonDocument.Parse("{ \"gateway\": { \"listenUrl\": "));
}
