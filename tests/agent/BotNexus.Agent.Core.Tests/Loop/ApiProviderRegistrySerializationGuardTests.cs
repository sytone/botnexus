using BotNexus.Agent.Core.Tests.TestUtils;
using Xunit;

namespace BotNexus.Agent.Core.Tests.Loop;

/// <summary>
/// Guards against reintroducing the static <c>ApiProviderRegistry</c> race (#1420).
/// </summary>
/// <remarks>
/// <see cref="TestHelpers.RegisterProvider"/> mutates an assembly-wide <c>private static</c>
/// registry. Every test class that calls it MUST sit in <see cref="ApiProviderRegistryCollection"/>,
/// which is declared with <c>DisableParallelization = true</c> so xUnit never runs those classes in
/// parallel with the rest of the assembly — otherwise one class's scope disposal can unregister
/// <c>test-api</c> mid-resolution in another class, producing intermittent
/// <c>No API provider registered for api: test-api</c> failures.
/// The list below is the complete set of <c>RegisterProvider</c> consumers; add to it (and apply the
/// <c>[Collection]</c> attribute) when a new test class starts registering providers.
/// </remarks>
public sealed class ApiProviderRegistrySerializationGuardTests
{
    private static readonly Type[] RegistryConsumingTestClasses =
    [
        typeof(AgentLoopRunnerTests),
        typeof(AgentLoopRunnerEdgeCaseTests),
        typeof(AgentLoopSafetyTests),
        typeof(ProviderRetryAfterTests),
        typeof(RunMetricsTests),
    ];

    public static IEnumerable<object[]> RegistryConsumers()
        => RegistryConsumingTestClasses.Select(type => new object[] { type });

    [Theory]
    [MemberData(nameof(RegistryConsumers))]
    public void RegistryConsumingTestClass_IsInTheSerializedCollection(Type testClass)
    {
        var collectionName = GetCollectionName(testClass);

        Assert.True(
            collectionName is not null,
            $"{testClass.Name} registers providers via TestHelpers.RegisterProvider but has no "
                + $"[Collection] attribute, so it can race on the shared static provider registry (#1420).");

        Assert.Equal(ApiProviderRegistryCollection.Name, collectionName);
    }

    [Fact]
    public void RegistryConsumerList_IsNonEmpty()
    {
        // The curated list (see the grep for TestHelpers.RegisterProvider that seeded it) is the
        // single source of truth for which classes must be serialized. Guard against it being
        // accidentally emptied, which would silently disable the protection.
        Assert.NotEmpty(RegistryConsumingTestClasses);
    }

    [Fact]
    public void Collection_DisablesParallelization()
    {
        // This is the property that actually fixes the race: a plain collection only serializes its
        // own members against each other, which is insufficient because the static registry is shared
        // with every other collection in the assembly. DisableParallelization = true stops this
        // collection from running concurrently with anything else.
        var data = typeof(ApiProviderRegistryCollection)
            .GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType == typeof(CollectionDefinitionAttribute));

        Assert.NotNull(data);

        var disableParallelization = data!.NamedArguments
            .FirstOrDefault(a => a.MemberName == nameof(CollectionDefinitionAttribute.DisableParallelization));

        Assert.True(
            disableParallelization.TypedValue.Value is true,
            $"{nameof(ApiProviderRegistryCollection)} must set DisableParallelization = true, otherwise "
                + "the registry-mutating classes can still race against other test collections (#1420).");
    }

    /// <summary>
    /// Reads the collection name from the <c>[Collection]</c> attribute's constructor argument.
    /// xUnit 2.x does not expose a public <c>Name</c> property on <see cref="CollectionAttribute"/>,
    /// so we read it from the attribute metadata instead.
    /// </summary>
    private static string? GetCollectionName(Type testClass)
    {
        var data = testClass
            .GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType == typeof(CollectionAttribute));

        if (data is null || data.ConstructorArguments.Count == 0)
        {
            return null;
        }

        return data.ConstructorArguments[0].Value as string;
    }
}
