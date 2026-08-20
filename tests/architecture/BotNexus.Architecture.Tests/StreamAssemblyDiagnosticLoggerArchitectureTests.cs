using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Fitness fence for the stream-assembly DIAGNOSTIC (#3443): no call site of
/// <c>StreamAssemblyConformance.Reconcile</c> may pass a null literal for its <c>logger</c>
/// argument.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StreamAssemblyReconciliationArchitectureTests"/> already fences the CALL - every
/// parser that observes a terminal block value must route through the shared seam. That fence was
/// satisfied by a call site that passed <c>logger: null</c> and <c>deltaCount: 0</c>, because
/// <c>Reconcile</c> guards its whole diagnostic behind <c>logger?.LogWarning(...)</c>. The call
/// existed, the checksum ran, the verdict was discarded. A structural fence that only checks
/// whether a mechanism is invoked cannot see a mechanism invoked in a muted state.
/// </para>
/// <para>
/// That distinction is not academic. During #3425, 1,033 assistant messages were corrupted across
/// 15 agents over six weeks with zero <c>Stream assembly mismatch</c> lines in the logs, and that
/// silence was read as evidence the checksum had nothing to report. A checksum that cannot report
/// is indistinguishable from a checksum reporting all-clear. This fence removes the ambiguity by
/// construction, so a fourth transport cannot be added muted the way the third was.
/// </para>
/// <para>
/// The fence scans <c>src/</c> only. Test code may legitimately pass a null logger to exercise the
/// documented optional-logger contract of the helper itself.
/// </para>
/// </remarks>
public class StreamAssemblyDiagnosticLoggerArchitectureTests
{
    /// <summary>
    /// A <c>Reconcile(...)</c> invocation together with its argument list, up to the closing
    /// parenthesis. Arguments are simple identifiers, member accesses and literals at every known
    /// call site, so a non-nesting match is sufficient and cannot silently swallow a later call.
    /// </summary>
    private static readonly Regex ReconcileCall = new(
        @"StreamAssemblyConformance\s*\.\s*Reconcile\s*\(([^()]*(?:\([^()]*\)[^()]*)*)\)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// The muted shapes: an explicitly named <c>logger: null</c>, or a bare <c>null</c> in the
    /// trailing (logger) position of the argument list.
    /// </summary>
    private static readonly Regex NamedNullLogger = new(
        @"\blogger\s*:\s*null\b",
        RegexOptions.Compiled);

    private static readonly Regex TrailingNullArgument = new(
        @",\s*null\s*$",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// AC5: no production call site may mute the diagnostic.
    /// </summary>
    [Fact]
    public void NoReconcileCallSite_PassesANullLogger()
    {
        var violations = new List<string>();

        foreach (var file in ProductionSourceFiles())
        {
            var source = StripComments(File.ReadAllText(file));

            foreach (Match call in ReconcileCall.Matches(source))
            {
                var arguments = call.Groups[1].Value;
                if (IsMuted(arguments))
                    violations.Add($"{Relative(file)}: Reconcile({Collapse(arguments)})");
            }
        }

        violations.ShouldBeEmpty(
            "StreamAssemblyConformance.Reconcile guards its entire diagnostic behind " +
            "logger?.LogWarning(...), so passing null does not degrade the warning - it deletes " +
            "it. The call site then computes a verdict and discards its own finding, which is " +
            "exactly the state #3443 found on the highest-volume live transport. Pass the real " +
            "logger; a static helper with none injected can take the ambient " +
            "ProviderDiagnostics.CreateLogger factory. Offenders: " + string.Join("; ", violations));
    }

    /// <summary>
    /// The scan must actually find call sites. A fence whose candidate set is empty passes for the
    /// wrong reason, which is the precise failure mode it exists to prevent.
    /// </summary>
    [Fact]
    public void Fence_FindsEveryKnownReconcileCallSite()
    {
        var callSites = ProductionSourceFiles()
            .SelectMany(file => ReconcileCall
                .Matches(StripComments(File.ReadAllText(file)))
                .Select(_ => Relative(file)))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        // The three stream parsers pinned by StreamAssemblyReconciliationArchitectureTests.
        callSites.Count.ShouldBeGreaterThanOrEqualTo(
            3,
            "Expected a Reconcile call site in each of the three stream parsers. Fewer means the " +
            "regex or the path resolution broke and this fence is scanning nothing. Found: " +
            string.Join("; ", callSites));
    }

    /// <summary>
    /// Proven-red: the detector must fire on both muted shapes and stay quiet on a real logger.
    /// Without this, a regex that matched nothing would satisfy the fence above forever.
    /// </summary>
    [Fact]
    public void Detector_FiresOnMutedCallSitesAndNotOnWiredOnes()
    {
        const string namedNull = """
            StreamAssemblyConformance.Reconcile(
                accumulated, final, model.Provider, model.Id, "api", "sse",
                deltaCount: 0,
                logger: null);
            """;
        const string positionalNull = """
            StreamAssemblyConformance.Reconcile(accumulated, final, p, m, a, t, 0, null);
            """;
        const string wired = """
            StreamAssemblyConformance.Reconcile(
                accumulated, final, model.Provider, model.Id, "api", "sse",
                textDeltaCounts.GetValueOrDefault(index),
                logger);
            """;

        ExtractArguments(namedNull).ShouldSatisfyAllConditions(
            args => IsMuted(args).ShouldBeTrue("A named logger: null must be detected."));
        ExtractArguments(positionalNull).ShouldSatisfyAllConditions(
            args => IsMuted(args).ShouldBeTrue("A positional trailing null must be detected."));
        ExtractArguments(wired).ShouldSatisfyAllConditions(
            args => IsMuted(args).ShouldBeFalse(
                "Positive pin: a wired call site must be accepted, else the fence over-tightens " +
                "and the only way to go green is to stop calling Reconcile at all."));
    }

    /// <summary>
    /// The guarded statement in the helper must still be conditional on the logger. If someone made
    /// it unconditional the null-argument shape would become harmless and this fence would be
    /// enforcing a rule that no longer protects anything - a reader deserves to be told.
    /// </summary>
    [Fact]
    public void SharedConformanceSeam_StillGuardsItsDiagnosticBehindTheOptionalLogger()
    {
        var path = Path.Combine(
            RepoRoot,
            "src/agent/BotNexus.Agent.Providers.Core/Streaming/StreamAssemblyConformance.cs"
                .Replace('/', Path.DirectorySeparatorChar));

        File.Exists(path).ShouldBeTrue("The shared stream-assembly conformance seam is missing.");

        File.ReadAllText(path)
            .Contains("logger?.LogWarning(", StringComparison.Ordinal)
            .ShouldBeTrue(
                "Reconcile still emits its only diagnostic through an optional logger, which is " +
                "why a null argument silently deletes the warning rather than degrading it. If " +
                "this seam changed shape, revisit this fence deliberately rather than deleting it.");
    }

    private static bool IsMuted(string arguments)
        => NamedNullLogger.IsMatch(arguments) || TrailingNullArgument.IsMatch(arguments.TrimEnd());

    private static string ExtractArguments(string source)
    {
        var match = ReconcileCall.Match(StripComments(source));
        match.Success.ShouldBeTrue("The synthetic sample must parse, else the detector test is vacuous.");
        return match.Groups[1].Value;
    }

    private static string Collapse(string value)
        => Regex.Replace(value, @"\s+", " ").Trim();

    private static IEnumerable<string> ProductionSourceFiles()
        => Directory
            .EnumerateFiles(Path.Combine(RepoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static string Relative(string path)
        => Path.GetRelativePath(RepoRoot, path).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// Comments in the parsers legitimately discuss the muted call site and quote its old shape.
    /// Scanning them would make this fence report a violation that does not exist.
    /// </summary>
    private static string StripComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"//[^\n]*", "");
    }

    private static string RepoRoot => FindRepoRoot();

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
