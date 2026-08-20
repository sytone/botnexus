using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for the actor-pseudonym single source of truth (#2442).
///
/// <para>Before #2442 the codebase carried FIVE byte-identical private <c>HashActor</c> helpers
/// (SHA-256 -&gt; truncate to 8 bytes -&gt; lowercase hex). Every copy is a chance for one of
/// them to drift, and a drifted pseudonym silently breaks correlation of historical security
/// events - it produces no error, just quietly wrong data. The fix centralised the scheme in
/// <c>BotNexus.Domain</c>'s <c>ActorPseudonym.For</c> and exposed it on
/// <c>AgentDescriptor.Pseudonym</c> for the agent case.</para>
///
/// <para>This fence keeps it centralised. It scans all of <c>src/</c> and fails, <b>naming the
/// offending file</b>, if a private SHA-256-truncate-to-hex copy is reintroduced anywhere
/// outside the sanctioned implementation. It is source-text based (like
/// <see cref="SecretRedactionFenceArchitectureTests"/>) because "this method re-implements the
/// scheme" is a syntactic property that reflection cannot see.</para>
/// </summary>
public sealed class ActorPseudonymCentralizationArchitectureTests : ArchitectureTest
{

    /// <summary>The one file allowed to contain the actor-pseudonym truncating hex digest.</summary>
    private const string CanonicalImplementation =
        "src/domain/BotNexus.Domain/Gateway/Security/ActorPseudonym.cs";

    /// <summary>
    /// Truncated-hex digests that are deliberately NOT actor pseudonyms, each with its
    /// justification. They hash content, not identities, and their digest length/format is pinned
    /// to a different external contract, so folding them into <see cref="CanonicalImplementation"/>
    /// would be wrong. Listed explicitly so a NEW truncated-hex helper still has to be a conscious
    /// decision rather than a silent sixth copy.
    /// </summary>
    private static readonly Dictionary<string, string> NonActorDigestExemptions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["src/gateway/BotNexus.Tools/Utils/ContentToken.cs"] =
                "Hashes FILE CONTENT (6 bytes) for read/write staleness detection, not an identity.",
            ["src/agent/BotNexus.Agent.Providers.Core/Streaming/ResponsesMessageConverter.cs"] =
                "Provider wire-parity message-id hash; pinned to the provider's payload format.",
        };

    /// <summary>
    /// Detects the truncating-hex-digest loop: a bounded loop over a hash byte array appending
    /// <c>ToString("x2")</c>. This is the exact body every removed <c>HashActor</c> copy had.
    /// Deliberately shape-based rather than name-based - renaming <c>HashActor</c> to
    /// <c>Fingerprint</c> must not let a copy through.
    /// </summary>
    private static readonly Regex TruncatedHexLoop = new(
        @"for\s*\(\s*var\s+\w+\s*=\s*0\s*;[^)]*<\s*\d+\s*;[^)]*\)\s*(\r?\n\s*\{)?\s*\r?\n?\s*\w+\.Append\(\s*\w+\[\w+\]\.ToString\(\s*""x2""",
        RegexOptions.Compiled);

    /// <summary>Detects the <c>Convert.ToHexString(hash)[..N].ToLowerInvariant()</c> variant.</summary>
    private static readonly Regex TruncatedHexSlice = new(
        @"Convert\.ToHexString\(.*\)\s*\[\s*\.\.\s*\d+\s*\]\s*\.ToLowerInvariant\(\)",
        RegexOptions.Compiled);

    /// <summary>Detects a named private helper that hashes an actor/agent/session id.</summary>
    private static readonly Regex PrivateHashActorHelper = new(
        @"private\s+static\s+string\s+Hash(Actor|Agent|Id|Session)\s*\(",
        RegexOptions.Compiled);

    [Fact]
    public void CanonicalImplementation_Exists()
    {
        var path = ResolvePath(CanonicalImplementation);
        File.Exists(path).ShouldBeTrue(
            $"The single source of truth for the actor pseudonym must live at {CanonicalImplementation}. " +
            "If it moved, update this fence deliberately - do not delete it.");
        File.ReadAllText(path).ShouldContain(
            "public static string For(",
            customMessage: "ActorPseudonym must expose the public entry point the five removed copies now call.");
    }

    [Fact]
    public void NoFileOutsideCanonicalImplementation_ReimplementsTheTruncatedHexDigest()
    {
        var offenders = new List<string>();

        foreach (var file in EnumerateSourceFiles())
        {
            var rel = Relative(file);
            if (string.Equals(rel, CanonicalImplementation, StringComparison.OrdinalIgnoreCase))
                continue;
            if (NonActorDigestExemptions.ContainsKey(rel))
                continue;

            var text = File.ReadAllText(file);
            if (TruncatedHexLoop.IsMatch(text))
                offenders.Add($"{rel} (truncated-hex digest loop)");
            else if (TruncatedHexSlice.IsMatch(text))
                offenders.Add($"{rel} (Convert.ToHexString truncate+lowercase)");
            else if (PrivateHashActorHelper.IsMatch(text))
                offenders.Add($"{rel} (private Hash* actor helper)");
        }

        offenders.ShouldBeEmpty(
            "#2442 fence: the actor pseudonym has ONE implementation, " + CanonicalImplementation +
            " (exposed on the agent model as AgentDescriptor.Pseudonym). The following file(s) " +
            "reintroduce a private SHA-256-truncate-to-hex copy; replace the local helper with " +
            "ActorPseudonym.For(...) or AgentDescriptor.Pseudonym: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void AllExemptedFiles_StillExist()
    {
        // A stale exemption would silently widen the fence.
        foreach (var (rel, justification) in NonActorDigestExemptions)
        {
            File.Exists(ResolvePath(rel)).ShouldBeTrue(
                $"Stale #2442 fence exemption for '{rel}' ({justification}) - the file no longer " +
                "exists; remove the exemption.");
        }
    }

    [Fact]
    public void ScanFoundSourceFiles() =>
        // Non-vacuity guard for the scan itself: if the src tree could not be enumerated the
        // "no offenders" assertion above would pass trivially.
        EnumerateSourceFiles().Count.ShouldBeGreaterThan(500,
            "Expected to scan the whole src/ tree. A tiny count means the scan is misrooted and " +
            "the centralisation fence is passing vacuously.");

    [Fact]
    public void Fence_NegativePin_DetectsAReintroducedCopy()
    {
        // Synthetic regression: exactly the body of the five removed HashActor copies.
        const string offending = """
            private static string HashActor(string id)
            {
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id ?? string.Empty));
                var sb = new StringBuilder(16);
                for (var i = 0; i < 8; i++)
                    sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
            """;

        TruncatedHexLoop.IsMatch(offending).ShouldBeTrue(
            "Vacuity guard: the detector MUST match a verbatim reintroduced HashActor copy. " +
            "If this fails, the fence above passes vacuously.");
        PrivateHashActorHelper.IsMatch(offending).ShouldBeTrue(
            "Vacuity guard: the named-helper detector MUST match a reintroduced HashActor.");

        // Braced-body variant must be caught too.
        const string offendingBraced = """
            private static string Fingerprint(string id)
            {
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
                var sb = new StringBuilder(16);
                for (var i = 0; i < 8; i++)
                {
                    sb.Append(hash[i].ToString("x2"));
                }
                return sb.ToString();
            }
            """;

        TruncatedHexLoop.IsMatch(offendingBraced).ShouldBeTrue(
            "Vacuity guard: a renamed, braced-body copy must still be caught - the fence is " +
            "shape-based, not name-based.");

        const string offendingSlice =
            """var id = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();""";

        TruncatedHexSlice.IsMatch(offendingSlice).ShouldBeTrue(
            "Vacuity guard: the Convert.ToHexString truncate variant must be caught.");
    }

    [Fact]
    public void Fence_PositivePin_AcceptsTheCentralisedCallSite()
    {
        // Synthetic positive: a call site that delegates must NOT be flagged.
        const string compliant = """
            private void Emit(string agentId)
            {
                var actor = new SecurityEventActor(SecurityActorKind.Agent, ActorPseudonym.For(agentId));
                _sink.Record(actor);
            }
            """;

        TruncatedHexLoop.IsMatch(compliant).ShouldBeFalse(
            "Positive pin: delegating to ActorPseudonym.For must be accepted, else the fence over-tightens.");
        TruncatedHexSlice.IsMatch(compliant).ShouldBeFalse(
            "Positive pin: delegating to ActorPseudonym.For must be accepted.");
        PrivateHashActorHelper.IsMatch(compliant).ShouldBeFalse(
            "Positive pin: delegating to ActorPseudonym.For must be accepted.");
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
