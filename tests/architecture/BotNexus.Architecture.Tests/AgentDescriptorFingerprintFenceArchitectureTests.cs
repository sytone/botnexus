using System.Reflection;
using System.Text.RegularExpressions;

using BotNexus.Gateway.Abstractions.Models;

using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for <c>#2588</c>: the hand-maintained descriptor member list in
/// <c>AgentDescriptorFingerprint.AppendDescriptor</c> may not silently go stale.
/// <para>
/// <c>#2383</c> was caused by exactly this failure mode - a change-detection field list that
/// omitted <c>FileAccess</c>, so an edited <c>fileAccess</c> policy was judged "unchanged" and
/// never re-registered. <c>#2565</c> collapsed two lists into one, but one hand-maintained list is
/// still a list that can be missed, and the next omission fails the same silent way: no error, no
/// log, the value simply stops mattering.
/// </para>
/// <para>
/// This fence converts that silent miss into a build failure. It reflects over
/// <see cref="AgentDescriptor"/>'s settable public instance properties - the ones that carry
/// configuration and can therefore change between reloads - and asserts every one of them is
/// referenced inside the body of <c>AppendDescriptor</c>.
/// </para>
/// <para>
/// <b>Why the fence and not reflection-in-production (option 1 of #2588).</b> The fingerprint's
/// determinism is load-bearing: it suppresses no-op <c>IOptionsMonitor</c> callbacks, so a
/// fingerprint that varies between processes would cause spurious re-registration on every
/// reload - worse than the bug being fixed. <c>AppendDescriptor</c> today hand-picks a *stable
/// serialisation* per member shape: ordinal-ordered key iteration for the
/// <c>IReadOnlyDictionary&lt;string, JsonElement&gt;</c> extension bag, raw-text for
/// <see cref="System.Text.Json.JsonElement"/> values (whose default <c>ToString</c> is not a
/// stable canonical form), delimiter-separated element walks for lists, and
/// <c>JsonSerializer</c> for the nested config records. A generic reflective walk would have to
/// re-derive all of that or regress into <c>object.ToString()</c>/default-serialiser behaviour on
/// the <c>object?</c>-valued <c>Metadata</c> and <c>IsolationOptions</c> bags, where reference
/// types stringify to their type name and are therefore *not* value-stable. The fence keeps the
/// audited, deterministic per-shape serialisation and removes only the thing that was actually
/// dangerous - the possibility of a silent omission.
/// </para>
/// <para>
/// <b>Vacuity.</b> A fence that reflects over nothing is green. Every assertion below is
/// preceded by a guard that the scan actually found its subject, and the token detector itself is
/// pinned with positive and negative cases so a regex that matches everything (or nothing) cannot
/// masquerade as coverage.
/// </para>
/// </summary>
public sealed class AgentDescriptorFingerprintFenceArchitectureTests
{
    private const string FingerprintSource =
        "src/gateway/BotNexus.Gateway.Configuration/AgentDescriptorFingerprint.cs";

    private const string AppendDescriptorSignature =
        "private static void AppendDescriptor(StringBuilder builder, AgentDescriptor d)";

    /// <summary>
    /// Members deliberately excluded from the fingerprint, each with the reason it cannot
    /// participate. This list is intentionally empty of anything settable: a settable member that
    /// needed excluding would be a real design decision requiring review, and the fence forces
    /// that conversation instead of allowing a silent drop.
    /// <para>
    /// Get-only computed members are excluded *structurally* rather than by name - see
    /// <see cref="FingerprintCandidates"/> - because they are pure functions of members that are
    /// already fingerprinted:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>Pseudonym</c> - pure function of <c>AgentId</c>, which is appended first.</item>
    ///   <item><c>IsBuiltIn</c> - reads <c>Metadata["builtin"]</c>; <c>Metadata</c> is appended.</item>
    ///   <item><c>ICitizen.Id</c> - explicit interface implementation, not on the public surface.</item>
    /// </list>
    /// <para>
    /// <b>Secrets/volatile audit (#2588 constraint 2).</b> Every settable member of
    /// <see cref="AgentDescriptor"/> was inspected: there is no API key, token, credential or
    /// timestamp on the type, and none on the nested config records it serialises
    /// (<c>MemoryAgentConfig</c>, <c>SoulAgentConfig</c>, <c>HeartbeatAgentConfig</c>,
    /// <c>DateTimeInjectionConfig</c>, <c>AgentConversationRetentionConfig</c>,
    /// <c>FileAccessPolicy</c>). Provider credentials live on the API-provider registry, not on
    /// the descriptor. The fingerprint is also never persisted - it is an in-memory
    /// <c>string</c> field compared across reload callbacks - so no secret would reach disk even
    /// if one were added. Hence the exclusion list below is empty; if a secret-bearing member is
    /// ever added, add it here <i>with its reason</i> and the fence stays honest.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> DeliberateExclusions = new(StringComparer.Ordinal);

    /// <summary>
    /// The members the fingerprint must account for: public, instance, settable (i.e. <c>init</c>
    /// or <c>set</c>) properties declared on <see cref="AgentDescriptor"/>. Settability is the
    /// right discriminator - a member a config source can populate is a member whose change must
    /// be detected; a get-only member is by construction derived from one of these.
    /// </summary>
    private static IReadOnlyList<string> FingerprintCandidates =>
        typeof(AgentDescriptor)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.SetMethod is not null && p.SetMethod.IsPublic)
            .Select(p => p.Name)
            .Where(name => !DeliberateExclusions.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// The primary fence. Every settable descriptor member must be referenced by
    /// <c>AppendDescriptor</c>. Adding a property to <see cref="AgentDescriptor"/> without
    /// touching the fingerprint fails here - loudly, at build time - instead of silently
    /// disabling change detection for that property.
    /// </summary>
    [Fact]
    public void AppendDescriptor_References_EverySettableDescriptorMember()
    {
        var candidates = FingerprintCandidates;

        // Anti-vacuity: a reflection scan that found no type or no members is green for the
        // wrong reason. AgentDescriptor has well over twenty settable members today; a scan
        // returning a handful means the reflection query itself broke.
        candidates.Count.ShouldBeGreaterThan(
            20,
            "Reflection over AgentDescriptor's settable public properties returned "
            + $"{candidates.Count} members, which is implausibly few. The fence is vacuous - fix "
            + "the reflection query before trusting a green result.");

        var body = ReadAppendDescriptorBody();
        var referenced = ExtractReferencedMembers(body);

        // Anti-vacuity: if the body extraction or the token regex broke, `referenced` would be
        // empty and the fence would report *every* member as missing (loud) - but if the regex
        // were instead too permissive it could report everything as present (silent). Pin a
        // lower bound so a degenerate "matches everything" detector is still caught by the
        // detector self-tests below, and a degenerate empty body is caught here.
        referenced.Count.ShouldBeGreaterThan(
            20,
            $"Only {referenced.Count} member references were extracted from the body of "
            + $"{AppendDescriptorSignature} in {FingerprintSource}. The body-extraction or "
            + "token-detection logic has broken; the fence cannot be trusted.");

        var missing = candidates
            .Where(name => !referenced.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        missing.ShouldBeEmpty(
            "AgentDescriptor declares settable members that AgentDescriptorFingerprint."
            + "AppendDescriptor never reads. A member the fingerprint ignores cannot trigger "
            + "re-registration on config hot-reload: edits to it will propagate from the config "
            + "source, be judged 'unchanged', and silently never apply - which is exactly the "
            + "#2383 fileAccess defect. Append each member below in AppendDescriptor (using the "
            + "serialisation appropriate to its shape: AppendList for ordered string lists, "
            + "SerializeStable for nested config records, SerializeExtensions for JsonElement "
            + "bags), or - if it genuinely must not participate - add it to "
            + "DeliberateExclusions in this test with a written reason.\n"
            + "Unreferenced members:\n  " + string.Join("\n  ", missing));
    }

    /// <summary>
    /// Anti-vacuity self-test: the fence's subject must exist. If
    /// <c>AgentDescriptorFingerprint.cs</c> is moved, renamed, or <c>AppendDescriptor</c> is
    /// restructured, the primary fence must fail here rather than quietly scan an empty string.
    /// </summary>
    [Fact]
    public void Fence_Subject_Exists_AndIsLocatable()
    {
        var path = ResolvePath(FingerprintSource);

        File.Exists(path).ShouldBeTrue(
            $"{FingerprintSource} not found at {path}. The #2588 fence is anchored to that file; "
            + "if the fingerprint helper moved, update FingerprintSource here so the fence keeps "
            + "guarding it rather than silently guarding nothing.");

        var text = File.ReadAllText(path);
        text.Contains(AppendDescriptorSignature, StringComparison.Ordinal).ShouldBeTrue(
            $"Could not find '{AppendDescriptorSignature}' in {FingerprintSource}. The fence "
            + "locates the method by its exact signature; a rename or signature change makes it "
            + "vacuous. Update AppendDescriptorSignature to match.");

        typeof(AgentDescriptor).ShouldNotBeNull();
    }

    /// <summary>
    /// Anti-vacuity self-test (positive cases): the member detector must actually recognise the
    /// reference forms <c>AppendDescriptor</c> uses - direct append, nested property access,
    /// and pass-through to a helper.
    /// </summary>
    [Fact]
    public void MemberDetector_Recognises_EveryReferenceFormUsedByAppendDescriptor()
    {
        const string sample = """
            builder.Append(d.DisplayName).Append('\u001f');
            builder.Append(d.AgentId.Value).Append('\u001f');
            AppendList(builder, d.ToolIds);
            builder.Append(SerializeStable(d.FileAccess)).Append('\u001e');
            builder.Append(SerializeExtensions(d.ExtensionConfig)).Append('\u001f');
            """;

        var referenced = ExtractReferencedMembers(sample);

        referenced.ShouldContain("DisplayName");
        referenced.ShouldContain("AgentId");
        referenced.ShouldContain("ToolIds");
        referenced.ShouldContain("FileAccess");
        referenced.ShouldContain("ExtensionConfig");
    }

    /// <summary>
    /// Anti-vacuity self-test (negative cases): the detector must not report a member as
    /// referenced when it is not. A detector that matches everything would make the primary fence
    /// permanently green and therefore worthless. Also pins that unrelated identifiers ending in
    /// <c>d</c>, and appends off other locals, do not produce false positives.
    /// </summary>
    [Fact]
    public void MemberDetector_DoesNotReport_UnreferencedOrUnrelatedMembers()
    {
        const string sample = """
            builder.Append(d.DisplayName).Append('\u001f');
            builder.Append(descriptor.Soul).Append('\u001f');
            builder.Append(added.Heartbeat).Append('\u001f');
            AppendList(builder, values);
            """;

        var referenced = ExtractReferencedMembers(sample);

        referenced.ShouldContain("DisplayName");

        // FileAccess is nowhere in the sample: the exact omission shape #2383 was.
        referenced.ShouldNotContain(
            "FileAccess",
            "The detector reported a member that does not appear in the source at all. A "
            + "detector that over-matches makes the #2588 fence permanently green and unable to "
            + "catch the omission it exists to catch.");

        // `descriptor.` and `added.` are not the fingerprint's parameter `d`; matching them
        // would let a member be 'referenced' by unrelated code and defeat the fence.
        referenced.ShouldNotContain("Soul");
        referenced.ShouldNotContain("Heartbeat");
    }

    /// <summary>
    /// Determinism pin (#2588 constraint 1): the candidate enumeration this fence depends on is
    /// sorted with <see cref="StringComparer.Ordinal"/>. Reflection member order is not
    /// guaranteed stable across runtimes, and an order-dependent fence would report different
    /// "missing" sets on different machines.
    /// </summary>
    [Fact]
    public void FingerprintCandidates_AreOrdinallySorted_AndStableAcrossCalls()
    {
        var first = FingerprintCandidates;
        var second = FingerprintCandidates;

        first.ShouldBe(second, "Candidate enumeration must be stable across calls.");
        first.ShouldBe(
            first.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
            "Candidate enumeration must be ordinally sorted; reflection's native member order is "
            + "not stable across runtimes (#2588 constraint 1).");
    }

    /// <summary>
    /// Extracts the set of <c>AgentDescriptor</c> member names read off the <c>AppendDescriptor</c>
    /// parameter <c>d</c>. Anchored to <c>d.</c> specifically (with a preceding non-identifier
    /// character so <c>added.</c> or <c>descriptor.</c> cannot match) and captures only the first
    /// segment, so <c>d.AgentId.Value</c> yields <c>AgentId</c>.
    /// </summary>
    private static HashSet<string> ExtractReferencedMembers(string body)
    {
        var matches = Regex.Matches(body, @"(?<![A-Za-z0-9_.])d\.([A-Za-z_][A-Za-z0-9_]*)");
        return matches
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Reads the body of <c>AppendDescriptor</c> by locating its exact signature and
    /// brace-matching to the closing brace. Scanning the whole file would let a reference in
    /// <c>ComputeEffective</c>'s <c>OrderBy(d =&gt; d.AgentId.Value, ...)</c> satisfy the fence
    /// for a member the fingerprint never actually appends.
    /// </summary>
    private static string ReadAppendDescriptorBody()
    {
        var text = File.ReadAllText(ResolvePath(FingerprintSource));

        var signatureIndex = text.IndexOf(AppendDescriptorSignature, StringComparison.Ordinal);
        signatureIndex.ShouldBeGreaterThanOrEqualTo(
            0,
            $"'{AppendDescriptorSignature}' not found in {FingerprintSource}.");

        var openBrace = text.IndexOf('{', signatureIndex);
        openBrace.ShouldBeGreaterThanOrEqualTo(
            0,
            $"No opening brace after '{AppendDescriptorSignature}' in {FingerprintSource}.");

        var depth = 0;
        for (var i = openBrace; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return text[(openBrace + 1)..i];
            }
        }

        throw new InvalidOperationException(
            $"Unbalanced braces while extracting AppendDescriptor from {FingerprintSource}.");
    }

    private static string ResolvePath(string relative) =>
        Path.Combine(FindRepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")))
            current = current.Parent;

        current.ShouldNotBeNull("Could not locate repo root (Directory.Packages.props) from " + AppContext.BaseDirectory);
        return current!.FullName;
    }
}
