using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Fitness fence for the provider newline seam (#2443): nothing outside the provider seam should
/// know that newlines in a stream are a thing.
/// </summary>
/// <remarks>
/// The CRLF corruption family (#2049 -> #2119 -> #2170) recurred three times because the knowledge
/// "streamed text may carry transport framing" was scattered per transport instead of owned by one
/// declared seam. Behaviour tests cannot prevent that: a fourth transport, or a downstream consumer
/// that "helpfully" trims a stray carriage return, keeps every existing test green while recreating
/// the defect. This fence pins the STRUCTURE - CR-aware mutation of streamed assistant content is
/// allowed in exactly two named provider-seam types and nowhere else.
/// </remarks>
public class ProviderNewlineSeamArchitectureTests : ArchitectureTest
{
    /// <summary>The only files permitted to perform CR-aware mutation of streamed text.</summary>
    private static readonly string[] SeamFiles =
    [
        // CopilotTextDeltaNormalizer.cs was removed from this allow-list by #3442: mitm captures
        // showed 0 raw CR bytes across 3,025 Copilot deltas, so the CRLF it stripped was never on
        // the wire. The real defect was our own separator injection (#3425, fixed by #3428), which
        // the FinalAssistantText_IsConcatenatedWithoutASeparator fence below now pins. One seam
        // remains, and it is the origin-agnostic one.
        "src/agent/BotNexus.Agent.Providers.Core/Streaming/StreamAssemblyConformance.cs",
    ];

    /// <summary>
    /// Matches text mutation that is aware of a carriage return - stripping, replacing or trimming
    /// it. This is the operation that silently deletes model content when it is wrong.
    /// </summary>
    private static readonly Regex CarriageReturnMutation = new(
        @"(Replace|Trim|TrimStart|TrimEnd|Split|StartsWith|EndsWith|Contains|IndexOf)\s*\(\s*[^)]*\\r",
        RegexOptions.Compiled);

    /// <summary>
    /// Matches joining a projection of assistant text blocks with a separator, i.e. the operation
    /// that INSERTS content the model never emitted (the inverse of the mutation regex above, which
    /// deletes it). Anchored to a statement that also projects <c>OfType&lt;TextContent&gt;()</c>, so it
    /// targets stream assembly specifically.
    /// </summary>
    private static readonly Regex SeparatorJoinOverTextBlocks = new(
        @"string\.Join\s*\(\s*(?<sep>Environment\.NewLine|""(?!""\s*\))[^""]+"")[^;]*?OfType<TextContent>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// A tool RESULT is a list of independent output blocks we ourselves produced, not a single
    /// model utterance split by transport chunking. Separating those with a newline is correct and
    /// must not be flagged - only assistant/completion assembly is in scope for #3425.
    /// </summary>
    private static bool IsToolResultAssembly(string statement) =>
        statement.Contains("toolResult", StringComparison.OrdinalIgnoreCase) ||
        statement.Contains("textBlocks", StringComparison.Ordinal);


    /// <summary>
    /// A file participates in stream assembly if it constructs or consumes streamed assistant text
    /// events. Those are the files where a stray CR-aware mutation would corrupt model content.
    /// </summary>
    private static bool ParticipatesInStreamAssembly(string source) =>
        source.Contains("TextDeltaEvent", StringComparison.Ordinal) ||
        source.Contains("TextEndEvent", StringComparison.Ordinal);

    [Fact]
    public void CarriageReturnAwareMutation_OccursOnlyInTheDeclaredProviderSeam()
    {
        var violations = new List<string>();

        foreach (var file in EnumerateSourceFiles())
        {
            var relative = Relative(file);
            if (SeamFiles.Contains(relative, StringComparer.Ordinal))
                continue;

            var source = StripComments(File.ReadAllText(file));
            if (!ParticipatesInStreamAssembly(source))
                continue;

            foreach (Match match in CarriageReturnMutation.Matches(source))
                violations.Add($"{relative}: {match.Value.Trim()}");
        }

        violations.ShouldBeEmpty(
            "Carriage-return-aware mutation of streamed assistant text must live in the declared " +
            "provider seam (#2443), not in a stream-assembly participant. Route the reconciliation " +
            "through StreamAssemblyConformance. Note that the transport-quirk explanation was " +
            "falsified by #3442 - if you are here chasing a newline defect, the cause is assembly " +
            "(#3425), not framing. Violations: " +
            string.Join("; ", violations));
    }

    /// <summary>
    /// Non-vacuity: the fence is worthless if its candidate set is empty, and it would be empty if
    /// the repo-root resolution or the comment stripper silently ate everything.
    /// </summary>
    [Fact]
    public void Fence_ExaminesANonEmptyCandidateSet()
    {
        var candidates = EnumerateSourceFiles()
            .Where(f => ParticipatesInStreamAssembly(StripComments(File.ReadAllText(f))))
            .ToList();

        candidates.Count.ShouldBeGreaterThan(
            3,
            "The stream-assembly participant set must be non-trivial, otherwise this fence passes " +
            "by examining nothing.");
    }

    /// <summary>
    /// The seam must actually exist and actually contain the mutation, otherwise the allow-list is
    /// protecting an empty file and the fence proves nothing about where the knowledge lives.
    /// </summary>
    [Fact]
    public void DeclaredSeamFiles_ExistAndContainTheCarriageReturnKnowledge()
    {
        foreach (var seam in SeamFiles)
        {
            var path = ResolvePath(seam);
            File.Exists(path).ShouldBeTrue($"Declared newline seam file is missing: {seam}");

            var source = File.ReadAllText(path);
            source.Contains(@"\r", StringComparison.Ordinal).ShouldBeTrue(
                $"Declared newline seam {seam} no longer contains carriage-return handling - either " +
                "the seam moved (update this fence deliberately) or the knowledge leaked elsewhere.");
        }
    }

    /// <summary>
    /// Proven-red: the detector must fire on a synthetic violation. Without this, a regex that
    /// matches nothing would pass the main fence forever.
    /// </summary>
    [Fact]
    public void Detector_FiresOnASyntheticViolationAndNotOnCleanCode()
    {
        const string violating = """
            stream.Push(new TextDeltaEvent(index, text.Replace("\r\n", ""), partial));
            """;
        const string clean = """
            stream.Push(new TextDeltaEvent(index, text, partial));
            """;

        ParticipatesInStreamAssembly(violating).ShouldBeTrue();
        CarriageReturnMutation.IsMatch(violating).ShouldBeTrue(
            "The detector must match CR-aware mutation, else the fence is vacuous.");
        CarriageReturnMutation.IsMatch(clean).ShouldBeFalse(
            "Positive pin: pushing an unmodified delta must be accepted, else the fence over-tightens.");
    }

    /// <summary>
    /// A file assembles final assistant text if it projects <c>TextContent</c> blocks into one
    /// string. Those are the files where inserting a separator fabricates model output.
    /// </summary>
    private static bool AssemblesTextBlocks(string source) =>
        source.Contains("OfType<TextContent>", StringComparison.Ordinal);

    /// <summary>
    /// Streamed text blocks must be concatenated with NO separator (#3425).
    /// </summary>
    /// <remarks>
    /// A stream chunk boundary is transport metadata: the provider may split a response anywhere,
    /// including mid-word, so a separator inserted between blocks is text the model never emitted.
    /// <c>string.Join(Environment.NewLine, ...)</c> in <c>MessageConverter.ToAgentMessage</c> and
    /// <c>LlmSessionCompactor</c> injected a literal CRLF between every block on Windows and
    /// corrupted 1,033 persisted assistant messages across 15 agents into one-token-per-line output.
    /// <para>
    /// This is the INVERSE of the mutation fence above and is why that fence did not catch it: the
    /// defect added characters rather than removing them, so no CR-aware call ever appeared in the
    /// source. Both directions must be fenced or the family recurs a seventh time.
    /// </para>
    /// </remarks>
    [Fact]
    public void FinalAssistantText_IsConcatenatedWithoutASeparator()
    {
        var violations = new List<string>();

        foreach (var file in EnumerateSourceFiles())
        {
            var source = StripComments(File.ReadAllText(file));
            if (!AssemblesTextBlocks(source))
                continue;

            foreach (Match match in SeparatorJoinOverTextBlocks.Matches(source))
            {
                if (IsToolResultAssembly(match.Value))
                    continue;

                violations.Add($"{Relative(file)}: {Collapse(match.Value)}");
            }
        }

        violations.ShouldBeEmpty(
            "Streamed assistant text blocks must be joined with string.Concat, never a separator " +
            "(#3425). A chunk boundary carries no implied newline: joining with Environment.NewLine " +
            "injects a literal CRLF between every token and corrupts persisted history. Violations: " +
            string.Join("; ", violations));
    }

    /// <summary>
    /// Proven-red for the separator fence: it must fire on the exact shape of the #3425 defect and
    /// stay silent on the corrected form, otherwise it is decoration.
    /// </summary>
    [Fact]
    public void SeparatorDetector_FiresOnTheDefectShapeAndNotOnConcat()
    {
        const string violating = """
            var text = string.Join(Environment.NewLine, msg.Content.OfType<TextContent>().Select(c => c.Text));
            """;
        const string alsoViolating = """
            var text = string.Join("\n", msg.Content.OfType<TextContent>().Select(c => c.Text));
            """;
        const string clean = """
            var text = string.Concat(msg.Content.OfType<TextContent>().Select(c => c.Text));
            """;
        const string toolResult = """
            var textResult = string.Join("\n", toolResult.Content.OfType<TextContent>().Select(t => t.Text));
            """;

        AssemblesTextBlocks(violating).ShouldBeTrue();
        SeparatorJoinOverTextBlocks.IsMatch(violating).ShouldBeTrue(
            "The detector must match the Environment.NewLine join that caused #3425.");
        SeparatorJoinOverTextBlocks.IsMatch(alsoViolating).ShouldBeTrue(
            "A literal separator is the same defect on non-Windows hosts.");
        SeparatorJoinOverTextBlocks.IsMatch(clean).ShouldBeFalse(
            "Positive pin: separator-free concatenation must be accepted.");
        IsToolResultAssembly(toolResult).ShouldBeTrue(
            "Tool-result blocks are independent outputs we produced, not one chunk-split utterance; " +
            "flagging them would over-tighten the fence into a false positive.");
    }

    private static string Collapse(string value) =>
        Regex.Replace(value, @"\s+", " ").Trim();

    /// <summary>
    /// Comments legitimately discuss <c>\r\n</c> - this whole subsystem is documented in terms of it.
    /// Scanning them would make the fence fire on its own explanations.
    /// </summary>
    private static string StripComments(string source)
    {
        var withoutBlock = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(withoutBlock, @"//[^\n]*", "");
    }

    private List<string> EnumerateSourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(Repository.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal)
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal))
            .ToList();

    private string Relative(string absolute) =>
        Path.GetRelativePath(Repository.Root, absolute).Replace(Path.DirectorySeparatorChar, '/');

    private string ResolvePath(string relative) =>
        Path.Combine(Repository.Root, relative.Replace('/', Path.DirectorySeparatorChar));

}
