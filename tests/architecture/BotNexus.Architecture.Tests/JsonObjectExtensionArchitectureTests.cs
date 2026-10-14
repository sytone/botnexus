using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// #3114: raw configuration operations belong on their <see cref="JsonObject"/> subject.
/// This source fence also makes the instance-member collision check repeatable.
/// </summary>
public sealed class JsonObjectExtensionArchitectureTests : ArchitectureTest
{
    private static readonly string[] s_expectedMethods =
    [
        "Exists",
        "TrySet",
        "TryRemove",
        "Get",
        "TryPatchObject",
        "GetEntry",
        "FindEntryKey",
        "TrySetEntry",
        "TryPatchEntry",
        "TryRemoveEntry",
        "ResolveKey",
    ];

    [Fact]
    public void RawConfigPath_IsReplacedByJsonObjectExtensions()
    {
        var project = Repository.Path("src", "gateway", "BotNexus.Gateway.Configuration");
        File.Exists(Path.Combine(project, "RawConfigPath.cs")).ShouldBeFalse();

        var source = File.ReadAllText(Path.Combine(project, "JsonObjectExtensions.cs"));
        source.ShouldContain("internal static class JsonObjectExtensions");

        foreach (var method in s_expectedMethods)
        {
            Regex.IsMatch(
                    source,
                    $@"\b{Regex.Escape(method)}\s*\(\s*this\s+JsonObject\s+\w+",
                    RegexOptions.CultureInvariant)
                .ShouldBeTrue($"{method} must remain an extension on JsonObject.");
        }
    }

    [Fact]
    public void ExtensionNames_DoNotCollideWithJsonObjectInstanceMembers()
    {
        var instanceNames = typeof(JsonObject)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        s_expectedMethods.Where(instanceNames.Contains).ShouldBeEmpty(
            "An instance member shadows an extension method without a compiler error. Rename the " +
            "extension before adding it, or retain the static helper with a documented reason.");
    }
}
