using BotNexus.Domain.Security;
using BotNexus.Gateway.Security;

namespace BotNexus.Gateway.Tests.Security;

public sealed class ConfigurationSecretReferenceResolverTests
{
    private sealed class InvalidReferenceResolver : ISecretResolver
    {
        public IReadOnlyCollection<string> SupportedSchemes => ["env"];

        public Task<Secret> ResolveAsync(SecretRef reference, CancellationToken cancellationToken = default)
            => throw new ArgumentException("Environment variable names cannot contain null characters.");
    }

    [Fact]
    public async Task ResolveAsync_InvalidIdentifier_ReturnsNull()
    {
        var resolver = new ConfigurationSecretReferenceResolver(new InvalidReferenceResolver());

        (await resolver.ResolveAsync(SecretRef.Parse("env:BAD\0NAME"))).ShouldBeNull();
    }
}
