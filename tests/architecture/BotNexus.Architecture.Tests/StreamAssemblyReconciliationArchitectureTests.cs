using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Fitness fence for stream-assembly reconciliation (#3336): every stream parser that observes a
/// provider's terminal block text must reconcile the text it assembled against it.
/// </summary>
/// <remarks>
/// <para>
/// #2443 added <c>StreamAssemblyConformance.Reconcile</c> - the mechanism that turns a silent
/// assembly defect into a self-reporting event by preferring the provider's own final text as
/// canonical - and wired it into exactly ONE of the three parsers. Nothing failed. Every behaviour
/// test stayed green while the Copilot-Messages and Anthropic parsers accumulated deltas and
/// finalised a block without ever consulting the free checksum the protocol hands them, which is
/// why the CRLF corruption family reached a FOURTH recurrence (#2049 -> #2119 -> #2170 -> #2140).
/// </para>
/// <para>
/// That asymmetry is structural, so the fence is structural. A behaviour test can only cover the
/// parsers it knows about; parser number four would be born unreconciled and every test would still
/// pass. This fence fails the build instead.
/// </para>
/// </remarks>
public class StreamAssemblyReconciliationArchitectureTests
{
    /// <summary>
    /// The stream parsers required to reconcile. Named explicitly rather than discovered by
    /// convention: a discovery predicate that silently matched nothing would make this fence pass
    /// by examining an empty set, which is the failure mode it exists to prevent.
    /// </summary>
    private static readonly string[] StreamParserFiles =
    [
        "src/agent/BotNexus.Agent.Providers.Core/Streaming/ResponsesStreamParser.cs",
        "src/agent/BotNexus.Agent.Providers.Copilot/Messages/CopilotMessagesStreamParser.cs",
        "src/agent/BotNexus.Agent.Providers.Anthropic/AnthropicStreamParser.cs",
    ];

    /// <summary>
    /// A parser observes a terminal block value when it handles the frame that carries the
    /// provider's final text for a block: <c>content_block_stop</c> on the Messages-shaped
    /// protocols, <c>response.output_text.done</c> on Responses.
    /// </summary>
    private static readonly Regex ObservesTerminalBlockValue = new(
        @"""(content_block_stop|response\.output_text\.done)""",
        RegexOptions.Compiled);

    /// <summary>A parser reconciles when it routes through the one shared conformance seam.</summary>
    private static readonly Regex Reconciles = new(
        @"StreamAssemblyConformance\s*\.\s*Reconcile\s*\(",
        RegexOptions.Compiled);

    private static string RepoRoot => FindRepoRoot();

    /// <summary>
    /// AC5: a stream parser that observes a terminal block text value must reconcile against it.
    /// </summary>
    [Fact]
    public void EveryStreamParser_ThatObservesATerminalBlockValue_Reconciles()
    {
        var violations = new List<string>();

        foreach (var relative in StreamParserFiles)
        {
            var path = ResolvePath(relative);
            File.Exists(path).ShouldBeTrue(
                $"Expected stream parser source not found: {relative}. If a parser moved, update " +
                "this fence deliberately rather than deleting the entry.");

            var source = StripComments(File.ReadAllText(path));
            if (!ObservesTerminalBlockValue.IsMatch(source))
                continue;

            if (!Reconciles.IsMatch(source))
                violations.Add(relative);
        }

        violations.ShouldBeEmpty(
            "A stream parser that handles the provider's terminal block frame must reconcile its " +
            "assembled text against the provider's own final value via " +
            "StreamAssemblyConformance.Reconcile (#3336). The protocol hands over a free per-block " +
            "checksum and discarding it is how the CRLF corruption family survived four issues " +
            "across three transports. Offenders: " + string.Join("; ", violations));
    }

    /// <summary>
    /// The scan must actually reach parsers that observe the terminal frame. If the detector or the
    /// path resolution broke, the fence above would pass by skipping every file.
    /// </summary>
    [Fact]
    public void Fence_ExaminesEveryDeclaredParserAndAllObserveATerminalBlockValue()
    {
        var observing = StreamParserFiles
            .Where(f => ObservesTerminalBlockValue.IsMatch(StripComments(File.ReadAllText(ResolvePath(f)))))
            .ToList();

        observing.Count.ShouldBe(
            StreamParserFiles.Length,
            "All declared stream parsers handle a terminal block frame today. If one genuinely " +
            "stopped doing so, that is a deliberate protocol change - update this fence. Silently " +
            "passing because the detector stopped matching is how #3336 stayed invisible. Matched: " +
            string.Join("; ", observing));
    }

    /// <summary>
    /// Proven-red: both detectors must fire on synthetic sources and stay quiet on their negatives.
    /// A regex that matched nothing would satisfy the main fence forever.
    /// </summary>
    [Fact]
    public void Detectors_FireOnSyntheticSourcesAndNotOnTheirNegatives()
    {
        const string observesButDoesNotReconcile = """
            case "content_block_stop":
                contentBlocks.Add(new TextContent(accumulated, signature));
            """;
        const string observesAndReconciles = """
            case "content_block_stop":
                accumulated = StreamAssemblyConformance.Reconcile(accumulated, finalText, p, m, a, t, 0, null);
            """;
        const string observesNothing = """
            case "message_delta":
                stopReason = mapStopReason(sr.GetString());
            """;

        ObservesTerminalBlockValue.IsMatch(observesButDoesNotReconcile).ShouldBeTrue();
        Reconciles.IsMatch(observesButDoesNotReconcile).ShouldBeFalse(
            "The violation shape must be detectable, else the fence is vacuous.");

        ObservesTerminalBlockValue.IsMatch(observesAndReconciles).ShouldBeTrue();
        Reconciles.IsMatch(observesAndReconciles).ShouldBeTrue(
            "Positive pin: a reconciling parser must be accepted, else the fence over-tightens.");

        ObservesTerminalBlockValue.IsMatch(observesNothing).ShouldBeFalse(
            "A parser that never sees a terminal block value has nothing to reconcile against.");
    }

    /// <summary>
    /// The shared seam must exist and must still prefer the provider's final text. If it were
    /// gutted into a passthrough, every parser above would still "reconcile" while reconciling
    /// nothing.
    /// </summary>
    [Fact]
    public void SharedConformanceSeam_ExistsAndPrefersTheProviderFinalText()
    {
        var path = ResolvePath(
            "src/agent/BotNexus.Agent.Providers.Core/Streaming/StreamAssemblyConformance.cs");

        File.Exists(path).ShouldBeTrue("The shared stream-assembly conformance seam is missing.");
        var source = File.ReadAllText(path);
        source.Contains("return finalText;", StringComparison.Ordinal).ShouldBeTrue(
            "StreamAssemblyConformance.Reconcile must still return the provider's final text on a " +
            "mismatch. Without that, calling it is a no-op and #3336 recurs with the fence green.");
    }

    /// <summary>
    /// Comments in these files legitimately discuss reconciliation and the frame names. Scanning
    /// them would let a parser satisfy the fence with a comment mentioning <c>Reconcile</c>.
    /// </summary>
    private static string StripComments(string source)
    {
        var withoutBlock = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(withoutBlock, @"//[^\n]*", "");
    }

    private static string ResolvePath(string relative) =>
        Path.Combine(RepoRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")))
        {
            current = current.Parent;
        }

        current.ShouldNotBeNull("Could not locate repo root (Directory.Packages.props) from " + AppContext.BaseDirectory);
        return current!.FullName;
    }
}
