namespace BotNexus.Agent.Providers.Core.Tests.Diagnostics;

/// <summary>
/// Serialises every test class that mutates the ambient static
/// <c>ProviderDiagnostics.LoggerFactory</c>. Without this, two such classes run in parallel xUnit
/// collections and one clears the factory mid-flight through the other, producing a flaky
/// "expected one warning, got zero" failure that has nothing to do with the code under test.
/// </summary>
/// <remarks>
/// A <c>[CollectionDefinition]</c> is resolved per test ASSEMBLY, so this one covers only the
/// mutators in <c>BotNexus.Agent.Providers.Core.Tests</c>. Two classes in
/// <c>BotNexus.Gateway.Tests</c> mutate the same static and were left racing for exactly that
/// reason (#3018); a sibling definition now lives in that assembly, fenced by
/// <c>ProviderDiagnosticsIsolationTests</c>. Any new assembly touching this static needs its own.
/// </remarks>
[CollectionDefinition(ProviderDiagnosticsCollection.Name, DisableParallelization = true)]
public sealed class ProviderDiagnosticsCollection
{
    /// <summary>The collection name to apply with <c>[Collection]</c>.</summary>
    public const string Name = "ProviderDiagnostics.LoggerFactory";
}
