namespace BotNexus.Agent.Providers.Core.Tests;

/// <summary>
/// Serialises every test class that mutates process-wide environment variables
/// (<c>COPILOT_GITHUB_TOKEN</c>, <c>GH_TOKEN</c>, <c>GITHUB_TOKEN</c>, <c>OPENAI_API_KEY</c>, ...).
/// <see cref="Environment.SetEnvironmentVariable(string, string?)"/> writes to a single per-PROCESS
/// block, so a set/restore pair in one xUnit collection is visible to — and clobbered by — a
/// concurrently running pair in another. That produced the flake in #3151, where
/// <c>GetApiKey_GithubCopilot_WhitespacePrimaryAndSecondary_FallsThroughToLast</c> read <c>null</c>
/// instead of <c>"github-last"</c> because a sibling class had already restored the variable this
/// test had just set. The failure lands on whichever PR happens to be running, regardless of its
/// diff.
/// </summary>
/// <remarks>
/// <c>parallelizeTestCollections</c> is <c>true</c> in this assembly's <c>xunit.runner.json</c>, and
/// deliberately stays that way; this definition fences only the env-var mutators rather than
/// serialising the whole assembly. A <c>[CollectionDefinition]</c> is resolved per test ASSEMBLY, so
/// this one covers only <c>BotNexus.Agent.Providers.Core.Tests</c> — the same scoping caveat that
/// applies to <c>ProviderDiagnosticsCollection</c> (#3018). Any other assembly mutating environment
/// variables needs its own sibling definition.
/// </remarks>
[CollectionDefinition(EnvironmentVariableCollection.Name, DisableParallelization = true)]
public sealed class EnvironmentVariableCollection
{
    /// <summary>The collection name to apply with <c>[Collection]</c>.</summary>
    public const string Name = "Environment.Variables";
}
