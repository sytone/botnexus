using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for <c>#3014</c>: there must be exactly ONE place in the provider
/// layer that drops an orphaned tool result (a <c>ToolResultMessage</c> whose originating tool call
/// is absent from the transcript), and it must be the shared
/// <c>MessageTransformer.TransformMessages</c> seam that every provider converter already calls.
/// </summary>
/// <remarks>
/// <para>
/// The defect this fence exists to prevent is not "the guard is missing" but "the guard exists in one
/// converter and not the other three". <c>ResponsesMessageConverter</c> had it; Anthropic, the
/// Copilot messages converter and the completions converter did not, so the two primary providers on
/// this instance turned a recoverable context overflow into a hard provider 400. A per-converter
/// guard is by construction the shape that drifts - the next converter added inherits nothing.
/// </para>
/// <para>
/// This is a <strong>textual smoke fence</strong>, not a correctness proof. It shows that the
/// drop-site marker appears exactly once across the provider source tree and that no converter has
/// grown a private copy. A regression that kept the marker but inverted the condition would pass here
/// and fail the behavioural tests in <c>MessageTransformerTests</c>,
/// <c>AnthropicOrphanToolResultTests</c>, <c>CopilotMessagesOrphanToolResultTests</c> and
/// <c>CompletionsOrphanToolResultTests</c>. The two layers complement; neither alone is sufficient.
/// </para>
/// </remarks>
public sealed class OrphanToolResultDropSiteArchitectureTests
{
    /// <summary>
    /// The marker comment that names the single sanctioned drop site. Any second occurrence means a
    /// converter re-grew its own copy.
    /// </summary>
    private const string DropSiteMarker = "ORPHAN-TOOL-RESULT-DROP-SITE";

    [Fact]
    public void ExactlyOneOrphanToolResultDropSite_ExistsInTheProviderTree()
    {
        var files = ProviderSourceFiles();

        // Non-vacuity: the fence is worthless if it scanned an empty candidate set.
        files.Count.ShouldBeGreaterThan(20, "expected the provider source tree to be discovered");

        var hits = files
            .Where(f => File.ReadAllText(f).Contains(DropSiteMarker, StringComparison.Ordinal))
            .ToList();

        hits.Count.ShouldBe(
            1,
            "exactly one orphan-tool-result drop site is allowed (#3014); found: " +
            string.Join(", ", hits.Select(Path.GetFileName)));

        Path.GetFileName(hits[0]).ShouldBe("MessageTransformer.cs");
    }

    [Fact]
    public void NoProviderConverter_HandRollsItsOwnOrphanToolResultDrop()
    {
        // A private copy would not carry the marker, so match the SHAPE instead: a converter that
        // consults a call-id set while handling a ToolResultMessage is re-implementing the seam.
        var pattern = new Regex(
            @"callIds\s*\.\s*Contains|ContainsToolCall|IsOrphan(ed)?ToolResult",
            RegexOptions.Compiled);

        var violations = ProviderSourceFiles()
            .Where(f => !string.Equals(Path.GetFileName(f), "MessageTransformer.cs", StringComparison.Ordinal))
            .Where(f => pattern.IsMatch(StripComments(File.ReadAllText(f))))
            .Select(Path.GetFileName)
            .ToList();

        violations.ShouldBeEmpty(
            "orphan-tool-result handling must live only in MessageTransformer (#3014); offenders: " +
            string.Join(", ", violations));
    }

    [Fact]
    public void Fence_IsNotVacuous_AgainstASyntheticSecondDropSite()
    {
        // Mutation check baked into the suite: the regex that guards against a hand-rolled copy must
        // actually flag the pre-#3014 ResponsesMessageConverter shape. If this ever stops matching,
        // the fence above has silently stopped fencing.
        const string preFixShape = """
            case ToolResultMessage toolResult:
                // Drop an orphan result whose originating call is absent from the transcript.
                if (!callIds.Contains(BaseCallId(toolResult.ToolCallId)))
                    break;
                result.Add(ConvertToolResultMessage(toolResult, model));
                break;
            """;

        var pattern = new Regex(
            @"callIds\s*\.\s*Contains|ContainsToolCall|IsOrphan(ed)?ToolResult",
            RegexOptions.Compiled);

        pattern.IsMatch(StripComments(preFixShape)).ShouldBeTrue(
            "the fence regex must flag the pre-#3014 per-converter drop shape");
    }

    /// <summary>
    /// Strips line and block comments so the fence reacts to code rather than to prose that merely
    /// discusses the drop site (the XML docs on the converters deliberately mention it).
    /// </summary>
    private static string StripComments(string source)
    {
        var withoutBlock = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlock, @"//.*?$", string.Empty, RegexOptions.Multiline);
    }

    private static List<string> ProviderSourceFiles()
    {
        var srcRoot = FindSourceRoot();
        var agentRoot = Path.Combine(srcRoot, "agent");
        Directory.Exists(agentRoot).ShouldBeTrue("Expected src/agent under " + srcRoot);

        return Directory
            .EnumerateDirectories(agentRoot, "BotNexus.Agent.Providers.*", SearchOption.TopDirectoryOnly)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
    }

    private static string FindSourceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")))
        {
            current = current.Parent;
        }

        current.ShouldNotBeNull("Could not locate repo root from " + AppContext.BaseDirectory);
        var srcRoot = Path.Combine(current!.FullName, "src");
        Directory.Exists(srcRoot).ShouldBeTrue("Expected src/ under " + current.FullName);
        return srcRoot;
    }
}
