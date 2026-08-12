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
public class ProviderNewlineSeamArchitectureTests
{
    /// <summary>The only files permitted to perform CR-aware mutation of streamed text.</summary>
    private static readonly string[] SeamFiles =
    [
        "src/agent/BotNexus.Agent.Providers.Copilot/CopilotTextDeltaNormalizer.cs",
        "src/agent/BotNexus.Agent.Providers.Core/Streaming/StreamAssemblyConformance.cs",
    ];

    /// <summary>
    /// Matches text mutation that is aware of a carriage return - stripping, replacing or trimming
    /// it. This is the operation that silently deletes model content when it is wrong.
    /// </summary>
    private static readonly Regex CarriageReturnMutation = new(
        @"(Replace|Trim|TrimStart|TrimEnd|Split|StartsWith|EndsWith|Contains|IndexOf)\s*\(\s*[^)]*\\r",
        RegexOptions.Compiled);

    private static string RepoRoot => FindRepoRoot();

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
            "provider seam (#2443), not in a stream-assembly participant. Route the transport quirk " +
            "through CopilotTextDeltaNormalizer, or the reconciliation through " +
            "StreamAssemblyConformance, so a fourth transport cannot reintroduce #2170. Violations: " +
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
    /// Comments legitimately discuss <c>\r\n</c> - this whole subsystem is documented in terms of it.
    /// Scanning them would make the fence fire on its own explanations.
    /// </summary>
    private static string StripComments(string source)
    {
        var withoutBlock = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(withoutBlock, @"//[^\n]*", "");
    }

    private static List<string> EnumerateSourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal)
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal))
            .ToList();

    private static string Relative(string absolute) =>
        Path.GetRelativePath(RepoRoot, absolute).Replace(Path.DirectorySeparatorChar, '/');

    private static string ResolvePath(string relative) =>
        Path.Combine(RepoRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }

        current.ShouldNotBeNull("Could not locate repo root (BotNexus.slnx) from " + AppContext.BaseDirectory);
        return current!.FullName;
    }
}
