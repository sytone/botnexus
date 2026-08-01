namespace BotNexus.Agent.Providers.Core.Tests.Diagnostics;

/// <summary>
/// Serialises every test class that mutates the ambient static
/// <c>ProviderDiagnostics.LoggerFactory</c>. Without this, two such classes run in parallel xUnit
/// collections and one clears the factory mid-flight through the other, producing a flaky
/// "expected one warning, got zero" failure that has nothing to do with the code under test.
/// </summary>
[CollectionDefinition(ProviderDiagnosticsCollection.Name, DisableParallelization = true)]
public sealed class ProviderDiagnosticsCollection
{
    /// <summary>The collection name to apply with <c>[Collection]</c>.</summary>
    public const string Name = "ProviderDiagnostics.LoggerFactory";
}
