using System.Text.RegularExpressions;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for clause 6 of #3228: a conversation's per-conversation
/// overrides (<c>ModelOverride</c>, <c>ThinkingOverride</c>, <c>ContextWindowOverride</c>) may
/// only be written through the narrow transactional <c>IConversationStore.PatchOverrideAsync</c>.
/// Assigning an override field on a <c>Conversation</c> instance outside an
/// <c>IConversationStore</c> implementation is a build failure, because the only thing a caller
/// can then do with that mutated instance is hand it to a whole-record <c>SaveAsync</c>.
///
/// <para><b>Why a fence and not just the fix.</b> #2139 introduced <c>PatchOverrideAsync</c>
/// specifically so an override write could not revert a pin or metadata mutation committed
/// between another caller's read and its write. It converted <c>ConversationsController</c> - and
/// stopped there. The two override handlers in <c>BuiltInCommandContributor</c> stayed on
/// whole-record <c>SaveAsync</c> for the entire intervening period, silently retaining the exact
/// clobber the issue closed, and nothing failed. That is the migration-straggler shape: N-1 call
/// sites converted, no fence, and a reader who finds <c>PatchOverrideAsync</c> in the tree
/// reasonably concludes override writes are handled. Converting the last call site removes
/// today's instance; it does nothing about the third caller, whose author reaches for
/// <c>conversation.ModelOverride = x; SaveAsync(conversation)</c> because that is the obvious
/// spelling and because it matches what the rest of the codebase used to do.</para>
///
/// <para><b>The legitimate remedy is always the same:</b> build a
/// <c>ConversationOverridePatch</c> and call
/// <c>IConversationStore.PatchOverrideAsync(conversationId, patch, ct)</c>. It is strictly
/// narrower than the whole-record save and is never a behaviour regression for the field being
/// written. Store implementations are the one place that must assign these fields - they are
/// what <c>PatchOverrideAsync</c> is implemented in terms of - and are allow-listed by path WITH
/// A REASON. A stale entry fails loudly via
/// <see cref="EveryAllowListEntry_StillExists_AndStillAssignsAnOverrideField"/>, so the list
/// cannot rot into a blanket exemption; an allow-list entry expires loudly where a loosened
/// regex expires silently.</para>
///
/// <para>Source-text based, like <see cref="CliSafeDisplayFenceArchitectureTests"/>: "which write
/// primitive did this call site choose" is a property of the source. A compiled assembly shows a
/// property setter call either way and retains no trace of the decision.</para>
/// </summary>
public sealed class ConversationOverrideWriteFenceArchitectureTests
{
    /// <summary>Roots scanned by this fence - the whole production source tree.</summary>
    private const string SourceRoot = "src";

    /// <summary>The contract that declares the sanctioned write primitive.</summary>
    private const string ContractSource =
        "src/gateway/BotNexus.Gateway.Contracts/Conversations/IConversationStore.cs";

    /// <summary>
    /// Files permitted to assign a conversation override field directly, each with the reason.
    /// The only entry is an <c>IConversationStore</c> implementation: it is the definition of the
    /// sanctioned primitive, not a consumer of it.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AllowedOverrideWriteSites =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/gateway/BotNexus.Gateway.Conversations/FileConversationStore.cs"] =
                "IConversationStore implementation - PatchOverrideAsync is implemented here in " +
                "terms of these assignments, so the mutation IS the sanctioned primitive.",
        };

    /// <summary>
    /// An assignment to one of the three override fields on some instance
    /// (<c>conversation.ModelOverride = x</c>, <c>c.ThinkingOverride = null</c>). Requires a
    /// preceding member access on the SAME LINE so an object initialiser or <c>with</c>
    /// expression - which constructs a fresh value rather than mutating a stored one, and so
    /// cannot clobber a concurrently committed column - does not match. Excludes <c>==</c>.
    /// </summary>
    private static readonly Regex OverrideAssignment =
        new(@"\.[ \t]*(ModelOverride|ThinkingOverride|ContextWindowOverride)[ \t]*=(?!=)", RegexOptions.Compiled);

    private static readonly Regex PatchOverrideUse =
        new(@"\bPatchOverrideAsync\s*\(", RegexOptions.Compiled);

    private static string RepoRoot => FindRepoRoot();

    [Fact]
    public void SanctionedPrimitive_IsDeclaredOnTheContract()
    {
        var path = ResolvePath(ContractSource);
        File.Exists(path).ShouldBeTrue(
            "IConversationStore - which declares the only sanctioned override write primitive " +
            $"(#2139, #3228) - is missing. Expected at: {path}");

        PatchOverrideUse.IsMatch(File.ReadAllText(path)).ShouldBeTrue(
            "IConversationStore no longer declares PatchOverrideAsync. If the primitive was " +
            "renamed, update this fence - do not delete it; the clobber it prevents is #2139.");
    }

    [Fact]
    public void NoOverrideFieldAssignment_OutsideConversationStoreImplementations()
    {
        var offenders = EnumerateSources()
            .Where(file => OverrideAssignment.IsMatch(File.ReadAllText(file)))
            .Select(ToRepoRelative)
            .Where(relative => !AllowedOverrideWriteSites.ContainsKey(relative))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "These files assign a conversation override field directly instead of calling " +
            "IConversationStore.PatchOverrideAsync: " + string.Join(", ", offenders) +
            ".\nA mutated Conversation instance can only be persisted by a whole-record " +
            "SaveAsync, which writes back the caller's stale snapshot of EVERY other column - " +
            "reverting a pin, a title, or a participant list committed between the read and the " +
            "write. That is the defect #2139 closed for the REST path and #3228 found still " +
            "live in the /model and /reasoning slash commands. " +
            "REMEDY: build a ConversationOverridePatch with the fields you own and call " +
            "store.PatchOverrideAsync(conversationId, patch, ct). See #3228 clause 6.");
    }

    [Fact]
    public void EveryAllowListEntry_StillExists_AndStillAssignsAnOverrideField()
    {
        foreach (var (relative, reason) in AllowedOverrideWriteSites)
        {
            var path = ResolvePath(relative);
            File.Exists(path).ShouldBeTrue(
                $"Allow-listed file no longer exists: {relative} (reason on record: {reason}). " +
                "Remove the entry - a stale allow-list slowly becomes a blanket exemption. See #3228.");

            OverrideAssignment.IsMatch(File.ReadAllText(path)).ShouldBeTrue(
                $"'{relative}' is allow-listed to assign conversation override fields but no longer " +
                "does. Remove the entry so the exemption cannot silently cover a future write added " +
                "to this file. See #3228.");
        }
    }

    /// <summary>
    /// The positive half of the fence: the two known consumers must actually call the sanctioned
    /// primitive, so reverting either to <c>SaveAsync</c> fails here as well as in its own unit
    /// tests, and an emptied or renamed file cannot pass vacuously by simply containing no
    /// assignment.
    /// </summary>
    [Fact]
    public void EveryOverrideWritingCaller_UsesPatchOverrideAsync()
    {
        string[] callers =
        [
            "src/gateway/BotNexus.Gateway.Api/Controllers/ConversationsController.cs",
            "src/gateway/BotNexus.Gateway/Commands/BuiltInCommandContributor.cs",
        ];

        foreach (var relative in callers)
        {
            var path = ResolvePath(relative);
            File.Exists(path).ShouldBeTrue(
                $"Expected override-writing caller not found: {path}. If it was renamed, update " +
                "this list - do not delete the entry without confirming the write seam is gone.");

            PatchOverrideUse.IsMatch(File.ReadAllText(path)).ShouldBeTrue(
                $"'{relative}' writes conversation overrides but never calls PatchOverrideAsync, so " +
                "it is back on a clobbering whole-record save. See #3228 clause 4 and #2139.");
        }
    }

    [Fact]
    public void Fence_IsNotVacuous_DetectsAssignmentAndDoesNotFlagThePatch()
    {
        const string offendingWriteSite = """
            internal sealed class ThirdCaller
            {
                public async Task SetAsync(Conversation conversation, IConversationStore store)
                {
                    conversation.ModelOverride = "some-model";
                    conversation.UpdatedAt = DateTimeOffset.UtcNow;
                    await store.SaveAsync(conversation);
                }
            }
            """;

        OverrideAssignment.IsMatch(offendingWriteSite).ShouldBeTrue(
            "Vacuity guard: a direct override assignment MUST be detected. If this fails the fence " +
            "matches nothing and the third caller reintroduces #2139 unnoticed.");

        const string compliantWriteSite = """
            internal sealed class CompliantCaller
            {
                public async Task SetAsync(ConversationId id, IConversationStore store)
                {
                    var patch = new ConversationOverridePatch { Model = FieldUpdate<string?>.Set("some-model") };
                    await store.PatchOverrideAsync(id, patch);
                }
            }
            """;

        OverrideAssignment.IsMatch(compliantWriteSite).ShouldBeFalse(
            "Positive pin: the sanctioned remedy must NOT be flagged, otherwise correct code cannot " +
            "go green and authors will route around the fence.");
        PatchOverrideUse.IsMatch(compliantWriteSite).ShouldBeTrue(
            "Positive pin: the sanctioned remedy must satisfy the PatchOverrideAsync detector.");

        // Whitespace is the obvious evasion; an equality test is the obvious false positive.
        OverrideAssignment.IsMatch("conversation . ThinkingOverride  =  null;").ShouldBeTrue(
            "Vacuity guard: intra-line whitespace must not defeat the detector.");
        OverrideAssignment.IsMatch("if (conversation.ModelOverride == null)").ShouldBeFalse(
            "Positive pin: a comparison is a read, not a write, and must not be flagged.");

        // A DTO, object initialiser, or `with` expression constructs a fresh value; it mutates no
        // stored conversation and therefore cannot revert a concurrently committed column.
        OverrideAssignment.IsMatch("new Conversation { ModelOverride = requested }").ShouldBeFalse(
            "Positive pin: an object initialiser constructs a new instance and cannot clobber a " +
            "concurrently committed column, so it is out of scope for this fence.");
        OverrideAssignment.IsMatch("existing with\n{\n    ModelOverride = patch.Model.Value,\n}").ShouldBeFalse(
            "Positive pin: a `with` expression is construction, not mutation of a stored record.");
    }

    /// <summary>
    /// Guards the scan itself: if the source tree moves or empties, every offender query returns
    /// nothing and the fence would pass while enforcing nothing.
    /// </summary>
    [Fact]
    public void Fence_ScansANonEmptySourceTree()
        => EnumerateSources().Count().ShouldBeGreaterThan(
            200,
            "Vacuity guard: the source tree scanned by this fence is missing or nearly empty, so " +
            $"every assertion above would pass without inspecting anything. Check that '{SourceRoot}' " +
            "still exists relative to the repo root.");

    private static IEnumerable<string> EnumerateSources()
    {
        var root = Path.Combine(RepoRoot, SourceRoot);
        Directory.Exists(root).ShouldBeTrue($"Source root not found: {root}");
        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedOrBuildOutput(path));
    }

    private static bool IsGeneratedOrBuildOutput(string path)
    {
        var normalised = path.Replace('\\', '/');
        return normalised.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || normalised.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToRepoRelative(string absolutePath) =>
        Path.GetRelativePath(RepoRoot, absolutePath).Replace('\\', '/');

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
