namespace BotNexus.Extensions.ExecTool.Tests;

/// <summary>
/// Serializes test classes that mutate the static <c>ExecTool.BackgroundProcesses</c> registry.
/// xUnit runs separate collections in parallel; placing these classes in one collection prevents
/// cross-test contamination of the shared static dictionary.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ExecToolBackgroundRegistryCollection
{
    public const string Name = "ExecTool background registry (static state)";
}
