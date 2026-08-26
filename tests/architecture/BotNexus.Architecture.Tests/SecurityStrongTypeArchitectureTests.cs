using System.Reflection;
using System.Text.RegularExpressions;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Fences the security strong types introduced by #2927 — <c>SkillPath</c> and <c>WebhookSecret</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Relationship to <see cref="DomainPrimitiveNormalisationArchitectureTests"/> (AC5 of #2927).</b>
/// That fence and this one are disjoint and neither is extended to cover the other's types.
/// <c>DomainPrimitiveNormalisationArchitectureTests</c> governs the <i>identity</i> primitives
/// (<c>AgentId</c>, <c>SessionId</c>, <c>ConversationId</c>) and asserts a <i>normalisation</i>
/// property: that callers do not redundantly <c>.Trim()</c> or <c>.ToLower()</c> around a primitive
/// that already normalises internally. Its patterns are keyed literally on those three type names
/// and on the <c>.From(</c> factory shape.
/// </para>
/// <para>
/// The #2927 types carry no normalisation contract at all — a skill path is normalised by the
/// filesystem and a webhook secret must be compared byte-for-byte, so trimming or lower-casing
/// either one would be a defect rather than a redundancy. Extending the normalisation fence to
/// them would therefore assert a property they do not have. What they need instead is a
/// <i>construction</i> fence: the privileged constructor must have exactly one caller, so that
/// "this path was contained" and "this secret was validated" remain facts the compiler enforces.
/// That is what this file asserts.
/// </para>
/// <para>
/// The identity primitives of group 2 in #2927 are the point at which the two fences would meet;
/// that group is explicitly out of scope for this change.
/// </para>
/// </remarks>
public sealed partial class SecurityStrongTypeArchitectureTests : ArchitectureTest
{
    /// <summary>
    /// The only file permitted to mint a contained <c>SkillPath</c> from a resolved string. Every
    /// other construction must go through <c>SkillPathValidator.TryValidate</c>, which is what
    /// performs the symlink resolution and containment check.
    /// </summary>
    private const string SkillPathMintingFile = "SkillPathValidator.cs";

    /// <summary>Declaration site of the privileged factory, which naturally contains its own name.</summary>
    private const string SkillPathDeclarationFile = "SkillPath.cs";

    [Fact]
    public void SkillPath_PrivilegedFactory_HasExactlyOneCallSite()
    {
        var srcRoot = Repository.SourceRoot;
        var violations = new List<string>();
        var pattern = SkillPathFromResolved();
        var callSites = new List<string>();

        foreach (var file in EnumerateSourceFiles(srcRoot))
        {
            var fileName = Path.GetFileName(file);
            if (string.Equals(fileName, SkillPathDeclarationFile, StringComparison.OrdinalIgnoreCase))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!pattern.IsMatch(lines[i]))
                    continue;

                var location = $"{Path.GetRelativePath(srcRoot, file)}:{i + 1} — {lines[i].Trim()}";
                callSites.Add(location);

                if (!string.Equals(fileName, SkillPathMintingFile, StringComparison.OrdinalIgnoreCase))
                    violations.Add(location);
            }
        }

        Assert.True(violations.Count == 0,
            $"SkillPath.FromResolved may only be called from {SkillPathMintingFile}, which performs the " +
            $"symlink resolution and containment check. Found {violations.Count} unauthorised call site(s):\n" +
            string.Join("\n", violations));

        // Non-vacuity: if the factory is ever renamed or removed, this fence must fail loudly rather
        // than pass by matching nothing at all.
        Assert.True(callSites.Count > 0,
            $"Expected at least one SkillPath.FromResolved call site in {SkillPathMintingFile}; found none. " +
            "The fence is no longer matching anything and is silently vacuous.");
    }

    [Fact]
    public void SkillSandboxBoundary_DoesNotAcceptBareStringsForValidatedPaths()
    {
        var srcRoot = Repository.SourceRoot;
        var validator = Path.Combine(srcRoot, "extensions", "BotNexus.Extensions.Skills", "Security", "SkillPathValidator.cs");
        var verifier = Path.Combine(srcRoot, "extensions", "BotNexus.Extensions.Skills", "Security", "SkillTrustVerifier.cs");

        Assert.True(File.Exists(validator), $"Expected {validator} to exist.");
        Assert.True(File.Exists(verifier), $"Expected {verifier} to exist.");

        var validatorSource = File.ReadAllText(validator);
        var verifierSource = File.ReadAllText(verifier);

        // The root argument and the resolved out-parameter are the sandbox contract; regressing
        // either back to string reopens the hole #2927 closed.
        Assert.Contains("SkillPath allowedRoot", validatorSource, StringComparison.Ordinal);
        Assert.Contains("out SkillPath resolvedPath", validatorSource, StringComparison.Ordinal);
        Assert.Contains("Verify(SkillPath skillDir", verifierSource, StringComparison.Ordinal);
    }

    [Fact]
    public void WebhookSecret_RawValue_IsOnlyReachedThroughTheExplicitRevealCall()
    {
        var srcRoot = Repository.SourceRoot;
        var secretFile = Path.Combine(srcRoot, "domain", "BotNexus.Domain", "Security", "WebhookSecret.cs");
        Assert.True(File.Exists(secretFile), $"Expected {secretFile} to exist.");

        var source = File.ReadAllText(secretFile);

        // ToString must not surface the backing field; Reveal must stay a method, not a property,
        // so unwrapping is greppable and is never picked up implicitly by a serialiser or log sink.
        Assert.Contains("public override string ToString() => RedactedMarker;", source, StringComparison.Ordinal);
        Assert.Contains("public string Reveal()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public static implicit operator", source, StringComparison.Ordinal);
    }

    private static IEnumerable<string> EnumerateSourceFiles(string srcRoot)
        => Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));


    [GeneratedRegex(@"SkillPath\.FromResolved\s*\(", RegexOptions.Compiled)]
    private static partial Regex SkillPathFromResolved();
}
