namespace BotNexus.Gateway.Tests.Diagnostics;

/// <summary>
/// Serialises every test class in <c>BotNexus.Gateway.Tests</c> that mutates the ambient static
/// <c>ProviderDiagnostics.LoggerFactory</c>. Without this, two such classes run in parallel xUnit
/// collections and one restores the factory mid-flight through the other, producing a flaky
/// "expected one warning, got zero" failure that has nothing to do with the code under test.
/// </summary>
/// <remarks>
/// An identical definition exists in <c>BotNexus.Agent.Providers.Core.Tests</c> and covers the two
/// mutating classes there. It cannot cover these: a <c>[CollectionDefinition]</c> is resolved by the
/// xUnit test framework per test ASSEMBLY, so a collection name declared in one assembly does not
/// serialise classes in another. Each assembly that touches the shared static therefore needs its
/// own definition -- which is precisely why this flake (#3018) survived the original fix (#2988).
/// The duplication is load-bearing, not accidental; do not "de-duplicate" it into a shared project.
/// </remarks>
[CollectionDefinition(ProviderDiagnosticsCollection.Name, DisableParallelization = true)]
public sealed class ProviderDiagnosticsCollection
{
    /// <summary>The collection name to apply with <c>[Collection]</c>.</summary>
    public const string Name = "ProviderDiagnostics.LoggerFactory";
}
