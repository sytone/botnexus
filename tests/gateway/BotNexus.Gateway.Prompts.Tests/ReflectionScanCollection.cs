namespace BotNexus.Gateway.Prompts.Tests;

/// <summary>
/// Serialises every test class that touches <c>PromptVariantRegistry.ReflectionScans</c> (#3309).
/// </summary>
/// <remarks>
/// <para>
/// <c>ReflectionScans</c> is a single PROCESS-WIDE static counter, and the #2433 constraint is
/// asserted as a DELTA across a measured window: read the counter, build prompts, assert the delta
/// is zero. That shape is only sound while nothing else in the process calls
/// <c>FreezeTypes</c> concurrently -- and roughly two dozen call sites do, spread across this class
/// and <see cref="PromptVariantConformanceTests"/>.
/// </para>
/// <para>
/// xUnit runs distinct test CLASSES in parallel by default, so a conformance probe freezing a
/// malformed fixture on another thread lands inside the registry test's measured window and
/// increments the counter it is asserting has not moved. The failure is therefore load- and
/// order-dependent: the assertion is CORRECT and the production claim it encodes still holds --
/// <c>Resolve</c> really does not reflect -- but the measurement is not isolated from its own
/// test assembly.
/// </para>
/// <para>
/// Putting both classes in one collection restores that isolation without touching the assertion.
/// Widening the tolerance to "delta &lt;= N" was the alternative and was rejected: it would make the
/// test pass while a genuine per-turn reflection regression hid inside the slack, which is exactly
/// the signal #2433 added the counter to protect.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ReflectionScanCollection
{
    /// <summary>The collection name shared by every class that reads or moves the scan counter.</summary>
    public const string Name = "prompt-variant-reflection-scans";
}
