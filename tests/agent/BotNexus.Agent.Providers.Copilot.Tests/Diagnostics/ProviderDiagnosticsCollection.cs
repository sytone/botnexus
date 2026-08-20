namespace BotNexus.Agent.Providers.Copilot.Tests.Diagnostics;

/// <summary>
/// Serialises every test class in THIS assembly that mutates the ambient static
/// <c>ProviderDiagnostics.LoggerFactory</c>. Without it, two such classes run in parallel xUnit
/// collections and one clears the factory mid-flight through the other, producing a flaky
/// "expected one warning, got zero" failure unrelated to the code under test (#3018).
/// </summary>
/// <remarks>
/// A <c>[CollectionDefinition]</c> is resolved per test ASSEMBLY, so the sibling definitions in
/// <c>BotNexus.Agent.Providers.Core.Tests</c> and <c>BotNexus.Gateway.Tests</c> cannot serialise
/// anything here no matter how correct they are. #3443 made this assembly a mutator for the first
/// time - the Copilot Messages stream-assembly diagnostic reaches its logger through that ambient
/// factory - so it needs its own definition.
/// </remarks>
[CollectionDefinition(ProviderDiagnosticsCollection.Name, DisableParallelization = true)]
public sealed class ProviderDiagnosticsCollection
{
    /// <summary>The collection name to apply with <c>[Collection]</c>.</summary>
    public const string Name = "ProviderDiagnostics.LoggerFactory";
}
