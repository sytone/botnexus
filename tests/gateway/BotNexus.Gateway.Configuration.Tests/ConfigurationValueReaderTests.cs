using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

public sealed class ConfigurationValueReaderTests
{
    private sealed class StubResolver(
        string scheme,
        IReadOnlyDictionary<string, string?> values,
        List<string> resolvedReferences) : IConfigurationReferenceResolver
    {
        public string Scheme => scheme;

        public Task<string?> ResolveAsync(string identifier, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            resolvedReferences.Add($"{scheme}:{identifier}");
            return Task.FromResult(values.GetValueOrDefault($"{scheme}:{identifier}"));
        }
    }

    [Fact]
    public async Task RawAndExpandedReads_AreSeparateAndSupportEmbeddedReferences()
    {
        var configuration = BuildConfiguration(("endpoint", "https://[[env:HOST]]/[[vault:service/key]]"));
        var references = new List<string>();
        var values = new Dictionary<string, string?>
        {
            ["env:HOST"] = "matrix.example.com",
            ["vault:service/key"] = "secret-value",
        };
        var reader = new ConfigurationValueReader([
            new StubResolver("env", values, references),
            new StubResolver("vault", values, references),
        ]);

        reader.GetRawValue(configuration, "endpoint")
            .ShouldBe("https://[[env:HOST]]/[[vault:service/key]]");

        (await reader.GetExpandedValueAsync(configuration, "endpoint"))
            .ShouldBe("https://matrix.example.com/secret-value");
        references.ShouldBe(["env:HOST", "vault:service/key"]);
    }

    [Fact]
    public async Task ExpandedRead_ReplacesMalformedAndUnavailableReferencesWithSentinel()
    {
        var configuration = BuildConfiguration(
            ("value", "prefix [[bad reference]] [[env:MISSING]] suffix [[env:UNCLOSED"));
        var reader = new ConfigurationValueReader([]);

        (await reader.GetExpandedValueAsync(configuration, "value"))
            .ShouldBe("prefix INVALID_SCHEMA_IDENTIFIER INVALID_SCHEMA_IDENTIFIER suffix INVALID_SCHEMA_IDENTIFIER");
    }

    [Fact]
    public async Task ExpandedRead_DoesNotExpandAResolvedValueRecursively()
    {
        var configuration = BuildConfiguration(("value", "[[env:FIRST]]"));
        var references = new List<string>();
        var values = new Dictionary<string, string?>
        {
            ["env:FIRST"] = "[[env:SECOND]]",
            ["env:SECOND"] = "must-not-be-read",
        };
        var reader = new ConfigurationValueReader([new StubResolver("env", values, references)]);

        (await reader.GetExpandedValueAsync(configuration, "value")).ShouldBe("[[env:SECOND]]");
        references.ShouldBe(["env:FIRST"]);
    }

    [Fact]
    public async Task ExpandedRead_WithoutReferenceReturnsRawValueWithoutResolving()
    {
        var configuration = BuildConfiguration(("value", "literal"));
        var references = new List<string>();
        var reader = new ConfigurationValueReader([new StubResolver("env", new Dictionary<string, string?>(), references)]);

        (await reader.GetExpandedValueAsync(configuration, "value")).ShouldBe("literal");
        references.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExpandedRead_PropagatesCancellation()
    {
        var configuration = BuildConfiguration(("value", "[[env:HOST]]"));
        var reader = new ConfigurationValueReader([]);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => reader.GetExpandedValueAsync(configuration, "value", cancellation.Token));
    }

    [Fact]
    public async Task ExpandedRead_ResolvesEnvironmentVariables()
    {
        var name = "BOTNEXUS_TEST_REFERENCE_" + Guid.NewGuid().ToString("N");
        var previousValue = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, "resolved-value");
        try
        {
            var reader = new ConfigurationValueReader([new EnvironmentConfigurationReferenceResolver()]);

            (await reader.ExpandValueAsync($"before [[env:{name}]] after"))
                .ShouldBe("before resolved-value after");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previousValue);
        }
    }

    private static IConfiguration BuildConfiguration(params (string Key, string Value)[] entries)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(entry => entry.Key, entry => (string?)entry.Value))
            .Build();
}
