using BotNexus.Domain.Security;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

public sealed class ConfigurationValueReaderTests
{
    private sealed class StubSecretResolver(
        IReadOnlyDictionary<string, string?> values) : IConfigurationSecretReferenceResolver
    {
        public List<SecretRef> ResolvedReferences { get; } = [];

        public Task<string?> ResolveAsync(SecretRef reference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolvedReferences.Add(reference);
            return Task.FromResult(values.GetValueOrDefault(reference.ToString()));
        }
    }

    [Fact]
    public async Task RawAndExpandedReads_AreSeparateAndSupportEmbeddedReferences()
    {
        var configuration = BuildConfiguration(("endpoint", "https://[[env:HOST]]/[[vault:service/key]]"));
        var resolver = new StubSecretResolver(new Dictionary<string, string?>
        {
            ["env:HOST"] = "matrix.example.com",
            ["vault:service/key"] = "secret-value",
        });
        var reader = new ConfigurationValueReader(resolver);

        reader.GetRawValue(configuration, "endpoint")
            .ShouldBe("https://[[env:HOST]]/[[vault:service/key]]");

        (await reader.GetExpandedValueAsync(configuration, "endpoint"))
            .ShouldBe("https://matrix.example.com/secret-value");
        resolver.ResolvedReferences.Select(reference => reference.Scheme)
            .ShouldBe(["env", "vault"]);
    }

    [Fact]
    public async Task ExpandedRead_ReplacesMalformedAndUnavailableReferencesWithSentinel()
    {
        var configuration = BuildConfiguration(
            ("value", "prefix [[bad reference]] [[env:MISSING]] suffix [[env:UNCLOSED"));
        var reader = new ConfigurationValueReader(new StubSecretResolver(new Dictionary<string, string?>()));

        (await reader.GetExpandedValueAsync(configuration, "value"))
            .ShouldBe("prefix INVALID_SCHEMA_IDENTIFIER INVALID_SCHEMA_IDENTIFIER suffix INVALID_SCHEMA_IDENTIFIER");
    }

    [Fact]
    public async Task ExpandedRead_DoesNotExpandAResolvedValueRecursively()
    {
        var configuration = BuildConfiguration(("value", "[[env:FIRST]]"));
        var resolver = new StubSecretResolver(new Dictionary<string, string?>
        {
            ["env:FIRST"] = "[[env:SECOND]]",
            ["env:SECOND"] = "must-not-be-read",
        });
        var reader = new ConfigurationValueReader(resolver);

        (await reader.GetExpandedValueAsync(configuration, "value")).ShouldBe("[[env:SECOND]]");
        resolver.ResolvedReferences.Select(reference => reference.Identifier).ShouldBe(["FIRST"]);
    }

    [Fact]
    public async Task ExpandedRead_WithoutReferenceReturnsRawValueWithoutResolving()
    {
        var configuration = BuildConfiguration(("value", "literal"));
        var resolver = new StubSecretResolver(new Dictionary<string, string?>());
        var reader = new ConfigurationValueReader(resolver);

        (await reader.GetExpandedValueAsync(configuration, "value")).ShouldBe("literal");
        resolver.ResolvedReferences.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExpandedRead_PropagatesCancellation()
    {
        var configuration = BuildConfiguration(("value", "[[env:HOST]]"));
        var reader = new ConfigurationValueReader(new StubSecretResolver(new Dictionary<string, string?>()));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => reader.GetExpandedValueAsync(configuration, "value", cancellation.Token));
    }

    private static IConfiguration BuildConfiguration(params (string Key, string Value)[] entries)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(entry => entry.Key, entry => (string?)entry.Value))
            .Build();
}
