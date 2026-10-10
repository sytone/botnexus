using System.Reflection;

namespace BotNexus.Cron.Tests;

/// <summary>
/// Prevents the three ambient-home fixtures from racing each other's reads and restores (#4817).
/// Collection definitions are assembly-local, so a fence in another test project cannot help.
/// </summary>
public sealed class BotNexusHomeIsolationTests
{
    [Theory]
    [InlineData(typeof(CronOptionsPromptTemplateResolverTests))]
    [InlineData(typeof(CronStoreRootPathTests))]
    [InlineData(typeof(MemoryDreamingWorkspacePathTests))]
    public void HomeMutatingTestClass_JoinsTheSharedNonparallelCollection(Type testClass)
    {
        var collection = testClass.GetCustomAttributesData()
            .SingleOrDefault(attribute => attribute.AttributeType == typeof(CollectionAttribute));
        collection.ShouldNotBeNull($"{testClass.Name} mutates process-wide BOTNEXUS_HOME and must be isolated.");
        collection.ConstructorArguments.ShouldHaveSingleItem().Value.ShouldBe("BOTNEXUS_HOME");

        var definitions = testClass.Assembly.GetTypes()
            .Where(type => type.GetCustomAttributesData().Any(attribute =>
                attribute.AttributeType == typeof(CollectionDefinitionAttribute) &&
                attribute.ConstructorArguments.Count == 1 &&
                Equals(attribute.ConstructorArguments[0].Value, "BOTNEXUS_HOME")))
            .ToArray();

        var definitionType = definitions.ShouldHaveSingleItem();
        var definition = definitionType.GetCustomAttribute<CollectionDefinitionAttribute>();
        definition.ShouldNotBeNull();
        definition.DisableParallelization.ShouldBeTrue(
            "home-mutating fixtures must not overlap each other or other test collections");
    }
}
