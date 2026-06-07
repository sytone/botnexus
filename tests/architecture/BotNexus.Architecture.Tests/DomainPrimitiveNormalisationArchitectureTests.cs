using System.Reflection;
using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Prevents redundant normalisation of domain primitive IDs at call sites.
/// <para>
/// Domain primitives (<c>AgentId</c>, <c>SessionId</c>, <c>ConversationId</c>) implement
/// <c>NormalizeInput</c> which already trims whitespace. Callers must not manually call
/// <c>.Trim()</c> before <c>From()</c> — it's redundant and masks the primitive's contract.
/// </para>
/// </summary>
public sealed partial class DomainPrimitiveNormalisationArchitectureTests
{
    /// <summary>
    /// Patterns that represent a redundant <c>.Trim()</c> immediately before a domain primitive
    /// <c>From()</c> call. These are caught at the architecture level to prevent regression.
    /// </summary>
    private static readonly Regex[] RedundantTrimBeforeFromPatterns =
    [
        TrimBeforeAgentIdFrom(),
        TrimBeforeSessionIdFrom(),
        TrimBeforeConversationIdFrom(),
    ];

    /// <summary>
    /// Source files excluded from this check (e.g., the NormalizeInput implementation itself,
    /// or test files that explicitly test normalisation behaviour).
    /// </summary>
    private static readonly string[] ExcludedFilePatterns =
    [
        "AgentId.cs",
        "SessionId.cs",
        "ConversationId.cs",
        "DomainPrimitiveNormalisationArchitectureTests.cs",
    ];

    [Fact]
    public void No_Redundant_Trim_Before_DomainPrimitive_From()
    {
        var srcRoot = FindSrcRoot();
        var violations = new List<string>();

        var csFiles = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !ExcludedFilePatterns.Any(ex => f.EndsWith(ex, StringComparison.OrdinalIgnoreCase)))
            .Where(f => !f.Contains("obj", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains("bin", StringComparison.OrdinalIgnoreCase));

        foreach (var file in csFiles)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                foreach (var pattern in RedundantTrimBeforeFromPatterns)
                {
                    if (pattern.IsMatch(line))
                    {
                        var relativePath = Path.GetRelativePath(srcRoot, file);
                        violations.Add($"{relativePath}:{i + 1} — {line.Trim()}");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} redundant .Trim() calls before domain primitive .From():\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void No_ToLower_On_DomainPrimitive_Value()
    {
        var srcRoot = FindSrcRoot();
        var violations = new List<string>();
        var pattern = ToLowerOnPrimitiveValue();

        var csFiles = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !ExcludedFilePatterns.Any(ex => f.EndsWith(ex, StringComparison.OrdinalIgnoreCase)))
            .Where(f => !f.Contains("obj", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains("bin", StringComparison.OrdinalIgnoreCase));

        foreach (var file in csFiles)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (pattern.IsMatch(lines[i]))
                {
                    var relativePath = Path.GetRelativePath(srcRoot, file);
                    violations.Add($"{relativePath}:{i + 1} — {lines[i].Trim()}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} .ToLower() / .ToLowerInvariant() calls on .Value of domain primitives:\n" +
            string.Join("\n", violations));
    }

    private static string FindSrcRoot()
    {
        // Walk up from the test assembly location to find the repo src/ directory.
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        while (dir is not null)
        {
            var srcCandidate = Path.Combine(dir, "src");
            if (Directory.Exists(srcCandidate) && File.Exists(Path.Combine(dir, "BotNexus.slnx")))
                return srcCandidate;
            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException("Could not locate repository src/ root from test assembly location.");
    }

    // Matches: .Trim()) where .From( precedes it on the same line, covering patterns like:
    //   AgentId.From(value.Trim())
    //   AgentId.From(parts[0].Trim())
    //   ConversationId.From(conversationId.Trim())
    [GeneratedRegex(@"AgentId\.From\([^)]*\.Trim\(\)", RegexOptions.Compiled)]
    private static partial Regex TrimBeforeAgentIdFrom();

    [GeneratedRegex(@"SessionId\.From\([^)]*\.Trim\(\)", RegexOptions.Compiled)]
    private static partial Regex TrimBeforeSessionIdFrom();

    [GeneratedRegex(@"ConversationId\.From\([^)]*\.Trim\(\)", RegexOptions.Compiled)]
    private static partial Regex TrimBeforeConversationIdFrom();

    // Matches: .Value.ToLower() or .Value.ToLowerInvariant() on agentId/sessionId/conversationId
    [GeneratedRegex(@"\.(AgentId|SessionId|ConversationId)\.Value\.(ToLower|ToLowerInvariant)\(\)", RegexOptions.Compiled)]
    private static partial Regex ToLowerOnPrimitiveValue();
}
