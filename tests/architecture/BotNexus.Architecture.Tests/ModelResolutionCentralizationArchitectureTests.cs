using System.Text;
using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for the epic's PBI3 override resolver (issue #1704):
/// the effective model must be produced by the centralized
/// <c>ModelOverrideResolver</c>, never by resolving a raw descriptor <c>ModelId</c>
/// directly into an <c>LlmModel</c> at a spawn / cron / isolation / supervisor site.
/// </summary>
/// <remarks>
/// <para>
/// The banned shape is a <c>GetModel(...)</c> call that passes a raw <c>.ModelId</c>
/// read as its model argument - that is exactly the "ad-hoc ModelId read" the resolver
/// replaces. Execution paths must first run the three layers
/// (model defaults -&gt; agent -&gt; conversation) through <c>ModelOverrideResolver.Resolve</c>
/// and resolve the model from the effective selection, so precedence stays centralized.
/// </para>
/// <para>
/// The fence strips comments/strings before matching and ships vacuity, false-positive,
/// and comment-mention self-tests per the "every regex-based architecture fence must
/// include a self-test" convention.
/// </para>
/// </remarks>
public sealed class ModelResolutionCentralizationArchitectureTests
{
    [Fact]
    public void NoProductionSourceFile_ResolvesModelFromRawModelId_OutsideResolver()
    {
        var srcRoot = FindSourceRoot();
        var violations = new List<string>();

        foreach (var path in EnumerateProductionCsFiles(srcRoot))
        {
            var relative = ToRelative(srcRoot, path);
            if (s_allowlist.Contains(relative)) continue;
            var stripped = StripComments(File.ReadAllText(path));
            if (s_directResolutionPattern.IsMatch(stripped))
                violations.Add(relative);
        }

        violations.ShouldBeEmpty(
            "PBI3 (#1704): the effective model must be produced by ModelOverrideResolver, " +
            "not by resolving a raw descriptor `.ModelId` directly via GetModel(...). Build the " +
            "three override layers and call ModelOverrideResolver.Resolve first.\n" +
            "Violations:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void Allowlist_OnlyContains_FilesThat_StillExist_AndStill_TripTheFence()
    {
        var srcRoot = FindSourceRoot();
        var stale = new List<string>();

        foreach (var relative in s_allowlist)
        {
            var full = Path.Combine(srcRoot, relative);
            if (!File.Exists(full))
            {
                stale.Add($"{relative} - file does not exist (delete this allowlist entry)");
                continue;
            }
            if (!s_directResolutionPattern.IsMatch(StripComments(File.ReadAllText(full))))
                stale.Add($"{relative} - file no longer trips the fence (delete this allowlist entry)");
        }

        stale.ShouldBeEmpty(
            "Allowlist hygiene: every entry must still exist AND still trip the fence.\n" +
            "Stale entries:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void Fence_IsNotVacuous_AgainstSyntheticReintroduction()
    {
        s_directResolutionPattern.IsMatch("var m = _llmClient.Models.GetModel(descriptor.ApiProvider, descriptor.ModelId);")
            .ShouldBeTrue("Vacuity guard: GetModel(provider, descriptor.ModelId) must trip the fence.");
        s_directResolutionPattern.IsMatch("registry.GetModel(provider, baseDescriptor.ModelId)")
            .ShouldBeTrue("Vacuity guard: GetModel(provider, baseDescriptor.ModelId) must trip the fence.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnResolvedModel()
    {
        // Resolving from the resolver output (effective.Model) is the sanctioned path.
        s_directResolutionPattern.IsMatch("var m = registry.GetModel(provider, effective.Model);")
            .ShouldBeFalse("False-positive guard: GetModel with the resolved effective model must NOT trip.");
        s_directResolutionPattern.IsMatch("var m = registry.GetModel(agentConfig.Provider, agentConfig.Model);")
            .ShouldBeFalse("False-positive guard: GetModel with a non-ModelId argument must NOT trip.");
        // A bare ModelId read (equality checks, config diff, serialization) is not resolution.
        s_directResolutionPattern.IsMatch("if (a.ModelId != b.ModelId) return false;")
            .ShouldBeFalse("False-positive guard: bare ModelId comparison must NOT trip.");
        s_directResolutionPattern.IsMatch("entry[\"model\"] = descriptor.ModelId;")
            .ShouldBeFalse("False-positive guard: serializing ModelId must NOT trip.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnCommentMentions()
    {
        const string syntheticClean = """
            /// <summary>
            /// Do NOT call GetModel(descriptor.ApiProvider, descriptor.ModelId) directly - use
            /// ModelOverrideResolver.Resolve first. This comment must not trip the fence.
            /// </summary>
            // Legacy: GetModel(provider, baseDescriptor.ModelId) was the pre-PBI3 shape.
            public void Documented() { }
            """;

        s_directResolutionPattern.IsMatch(StripComments(syntheticClean)).ShouldBeFalse(
            "False-positive guard: comment mentions of the banned shape must not trip.");
    }

    // Matches a GetModel(...) call whose argument list contains a `.ModelId` read - i.e.
    // resolving a raw descriptor ModelId directly into an LlmModel. The `[^)]*` keeps the
    // match within a single call's argument list so unrelated later `.ModelId` reads do
    // not create false positives across statements.
    private static readonly Regex s_directResolutionPattern = new(
        @"\.GetModel\s*\([^)]*\.ModelId\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // The resolver is the one sanctioned home for producing the *effective* turn model.
    // The allowlist holds files that call GetModel(...ModelId) for a DIFFERENT, legitimate
    // reason - capability validation, not effective-model resolution - and the hygiene test
    // keeps every entry honest (must still exist AND still trip the fence).
    private static readonly HashSet<string> s_allowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        // AgentDescriptorValidator looks up the model by its raw ModelId solely to read the
        // model's capability set (supported thinking levels / context sizes) so it can REJECT
        // an agent-level thinking/context value the model does not support (PBI4, #1705). It
        // never resolves an effective turn model - that stays with ModelOverrideResolver.
        Path.Combine("gateway", "BotNexus.Gateway", "Agents", "AgentDescriptorValidator.cs"),
    };

    private static IEnumerable<string> EnumerateProductionCsFiles(string srcRoot)
    {
        return Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
    }

    private static string ToRelative(string srcRoot, string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        var root = Path.GetFullPath(srcRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? full.Substring(root.Length)
            : full;
    }

    private static string FindSourceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }
        current.ShouldNotBeNull("Could not locate repo root from " + AppContext.BaseDirectory);
        var srcRoot = Path.Combine(current.FullName, "src");
        Directory.Exists(srcRoot).ShouldBeTrue("Expected src/ under " + current.FullName);
        return srcRoot;
    }

    /// <summary>
    /// Removes single-line and block C# comments while preserving string and char literals.
    /// Same lexer pattern used by other architecture fences.
    /// </summary>
    private static string StripComments(string source)
    {
        var sb = new StringBuilder(source.Length);
        var i = 0;
        var n = source.Length;

        while (i < n)
        {
            var c = source[i];

            if (c == '@' && i + 1 < n && source[i + 1] == '"')
            {
                sb.Append(source, i, 2);
                i += 2;
                while (i < n)
                {
                    if (source[i] == '"')
                    {
                        if (i + 1 < n && source[i + 1] == '"')
                        {
                            sb.Append("\"\"");
                            i += 2;
                            continue;
                        }
                        sb.Append('"');
                        i++;
                        break;
                    }
                    sb.Append(source[i++]);
                }
                continue;
            }

            if (c == '"')
            {
                sb.Append('"');
                i++;
                while (i < n)
                {
                    if (source[i] == '\\' && i + 1 < n)
                    {
                        sb.Append(source[i]);
                        sb.Append(source[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (source[i] == '"')
                    {
                        sb.Append('"');
                        i++;
                        break;
                    }
                    sb.Append(source[i++]);
                }
                continue;
            }

            if (c == '\'')
            {
                sb.Append('\'');
                i++;
                while (i < n)
                {
                    if (source[i] == '\\' && i + 1 < n)
                    {
                        sb.Append(source[i]);
                        sb.Append(source[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (source[i] == '\'')
                    {
                        sb.Append('\'');
                        i++;
                        break;
                    }
                    sb.Append(source[i++]);
                }
                continue;
            }

            if (c == '/' && i + 1 < n && source[i + 1] == '/')
            {
                i += 2;
                while (i < n && source[i] != '\n' && source[i] != '\r')
                {
                    i++;
                }
                continue;
            }

            if (c == '/' && i + 1 < n && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    i++;
                }
                i = Math.Min(n, i + 2);
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }
}
