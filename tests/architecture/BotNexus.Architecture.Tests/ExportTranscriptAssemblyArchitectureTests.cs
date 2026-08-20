using System.Reflection;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Api.Export;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the anti-drift invariant of issue #3278 acceptance
/// criterion 9: <b>no second transcript assembly path exists</b>. Every consumer that turns stored
/// session history into a chronological transcript - the portal history endpoint and both export
/// scopes - must go through <see cref="ConversationHistoryProjection"/>.
/// </summary>
/// <remarks>
/// <para>
/// The defect this prevents is the one the issue names explicitly: an exporter that re-derives
/// boundary-marker insertion, <c>NO_REPLY</c> filtering, crash-sentinel exclusion and compaction
/// projection for itself. Such a duplicate starts correct and then rots, because a fix applied to
/// the history endpoint (as #773, #2921 and #2936 each were) silently fails to reach the download.
/// The duplication is invisible in review precisely because both copies look right in isolation.
/// </para>
/// <para>
/// A behavioural parity test lives alongside these in <c>ExportDocumentAssemblerTests</c>; this
/// fence is the structural half, and catches a NEW consumer that never had a parity test written
/// for it.
/// </para>
/// </remarks>
public sealed class ExportTranscriptAssemblyArchitectureTests : ArchitectureTest
{
    /// <summary>
    /// The projection is the single home of the transcript state machine, so the marker strings and
    /// filter conditions that make it up must appear in exactly one production file.
    /// </summary>
    [Fact]
    public void TranscriptAssemblyStateMachine_LivesInExactlyOneProductionFile()
    {
        var gatewayRoot = Repository.Path("src", "gateway");

        // The conjunction is the signal: any file that both inserts session boundary markers AND
        // filters NO_REPLY is, by definition, assembling a transcript.
        var offenders = Directory
            .EnumerateFiles(gatewayRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !string.Equals(Path.GetFileName(path), "ConversationHistoryProjection.cs", StringComparison.Ordinal))
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                return text.Contains("\"session_end\"", StringComparison.Ordinal)
                    && text.Contains("\"NO_REPLY\"", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(gatewayRoot, path))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            "These files assemble a transcript themselves (they both insert a \"session_end\" " +
            "boundary marker and filter \"NO_REPLY\"), which is the second assembly path #3278 " +
            "acceptance criterion 9 forbids. Consume ConversationHistoryProjection.Project instead " +
            "so a fix to the filtering rules reaches the portal and every export at once.\n" +
            "Offenders:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The export document assembler must actually call the shared projection. Verified from IL, so
    /// deleting the call and inlining a copy of the state machine reddens this test even if the
    /// duplicated code is spelled differently enough to slip past the text fence above.
    /// </summary>
    [Fact]
    public void ExportDocumentAssembler_ReferencesTheSharedProjection()
        => AssertCallsProjection(typeof(ExportDocumentAssembler),
            $"{nameof(ExportDocumentAssembler)} does not call {nameof(ConversationHistoryProjection)}.Project. " +
            "Both export scopes must consume the same projection the portal history endpoint uses " +
            "(#3278 AC9); assembling entries directly reintroduces the drift the issue exists to prevent.");

    /// <summary>
    /// The history assembler that serves the portal must consume the same projection, so the two
    /// consumers cannot diverge by one of them being "fixed" independently.
    /// </summary>
    [Fact]
    public void ConversationHistoryAssembler_ReferencesTheSharedProjection()
        => AssertCallsProjection(typeof(ConversationHistoryAssembler),
            $"{nameof(ConversationHistoryAssembler)} no longer calls " +
            $"{nameof(ConversationHistoryProjection)}.Project, so the portal history endpoint and the " +
            "export routes have diverged onto separate transcript assembly paths (#3278 AC9).");

    /// <summary>
    /// Asserts that <paramref name="consumer"/> contains an IL call to
    /// <see cref="ConversationHistoryProjection"/>.
    /// </summary>
    /// <remarks>
    /// Both call sites are in <c>async</c> methods, whose bodies the compiler moves into a nested
    /// state-machine type - the <c>MoveNext</c> of a generated struct, not the method that declares
    /// them. Scanning only the declared methods therefore finds nothing and produces a false
    /// failure, which is exactly what the first version of this fence did. Nested types must be
    /// walked too.
    /// </remarks>
    private static void AssertCallsProjection(Type consumer, string because)
    {
        var projectionType = typeof(ConversationHistoryProjection);

        var candidates = new List<Type> { consumer };
        candidates.AddRange(consumer.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));

        var found = candidates
            .SelectMany(t => t.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => m.GetMethodBody() is not null)
            .Any(m =>
            {
                var il = m.GetMethodBody()!.GetILAsByteArray();
                return il is not null && ContainsCallTo(m.Module, il, projectionType);
            });

        found.ShouldBeTrue(because);
    }

    /// <summary>
    /// Scans an IL byte array for a <c>call</c> / <c>callvirt</c> whose resolved method is declared
    /// on <paramref name="target"/>.
    /// </summary>
    private static bool ContainsCallTo(Module module, byte[] il, Type target)
    {
        // 0x28 = call, 0x6F = callvirt; both are followed by a 4-byte metadata token.
        for (var i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] is not (0x28 or 0x6F))
                continue;

            var token = BitConverter.ToInt32(il, i + 1);
            try
            {
                var resolved = module.ResolveMethod(token);
                if (resolved?.DeclaringType == target)
                    return true;
            }
            catch (ArgumentException)
            {
                // Not every byte that looks like an opcode is one - a token operand can contain
                // 0x28. An unresolvable token simply means this position was not a real call.
            }
        }

        return false;
    }

}
