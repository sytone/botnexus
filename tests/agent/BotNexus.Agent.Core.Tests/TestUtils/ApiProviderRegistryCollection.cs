namespace BotNexus.Agent.Core.Tests.TestUtils;

/// <summary>
/// Serializes test classes that register/unregister providers into the assembly-wide static
/// <c>TestHelpers.SharedApiProviderRegistry</c> (via <see cref="TestHelpers.RegisterProvider"/>).
/// </summary>
/// <remarks>
/// The shared registry is a <c>private static</c> field keyed by <c>provider.Api</c> (<c>"test-api"</c>),
/// so it is process-global within the test assembly. With <c>parallelizeTestCollections: true</c>,
/// xUnit runs separate collections in parallel; one class's <c>ApiProviderScope.Dispose()</c> can then
/// <c>Unregister</c> the <c>"test-api"</c> entry while another class's multi-turn agent loop is still
/// resolving it across <c>await</c> points, producing intermittent
/// <c>InvalidOperationException: No API provider registered for api: test-api</c> failures
/// (most visible in <c>RunMetricsTests</c>, which keeps the registry resolved across several awaits).
/// <para>
/// <c>DisableParallelization = true</c> is required: it stops this collection from running in parallel
/// with the rest of the assembly. A plain collection only serializes its own members against each other,
/// which is insufficient here — the race is between these classes and every other test collection that
/// shares the static registry. This is the targeted alternative to disabling assembly-wide
/// parallelization, keeping the rest of the (large) suite parallel.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ApiProviderRegistryCollection
{
    public const string Name = "API provider registry (static state)";
}
