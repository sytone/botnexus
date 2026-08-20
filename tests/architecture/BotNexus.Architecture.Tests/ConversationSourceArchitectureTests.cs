using System.Reflection;
using System.Text.RegularExpressions;
using BotNexus.Gateway.Abstractions.Models;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Build-failing architecture fitness functions pinning the final shape of epic #2300
/// (child F, issue #2306). The epic replaced three lossy proxies for "why does this conversation
/// exist?" — a <c>cron:</c>/<c>cronconv:</c> session-id/conversation-id prefix probe,
/// <c>Conversation.Initiator</c>, and a mutable client-side virtual-session flag — with a single
/// write-once <see cref="ConversationSource"/> stamped by the server at creation.
///
/// <para>Three rules, each a distinct regression this epic exists to prevent:</para>
/// <list type="number">
///   <item>
///     <b>Rule 1 — write-once.</b> <see cref="Conversation.Source"/> (server) and the client's
///     <c>ConversationState.Source</c>/<c>.Kind</c> expose only an <c>init</c> setter. A plain
///     <c>set</c> would let an inbound SignalR event poison the origin mid-render — the exact
///     defect class fixed for agents in #2248, where a mutable flag made an agent vanish from
///     the dropdown. It would also silently make cron conversations writable.
///   </item>
///   <item>
///     <b>Rule 2 - no origin inference.</b> No production code may re-derive conversation origin
///     from an id substring (a <c>cron:</c>/<c>cronconv:</c>-style prefix probe). Origin is a
///     modelled field; inference is per-surface, lossy and drifts.
///   </item>
///   <item>
///     <b>Rule 2b - no visibility inference (#2340).</b> No client surface may decide whether a
///     conversation is user-visible from an <c>internal:</c> id prefix. Visibility is the
///     write-once, server-stamped <c>ConversationVisibility</c>. This rule replaced the single
///     allowlisted exception that previously kept Rule 2 from being absolute; the fence now has
///     ZERO inference exceptions.
///   </item>
///   <item>
///     <b>Rule 3 — no reintroduction.</b> The identifiers <c>IsVirtualSession</c> and
///     <c>VirtualSessionKind</c> must not exist anywhere in <c>src/</c> or <c>tests/</c>.
///   </item>
/// </list>
///
/// <para>
/// <b>Scope note for Rule 2.</b> The ban is on inferring <em>conversation origin</em>. It is
/// deliberately NOT a blanket ban on the string <c>"cron:"</c>: the cron subsystem legitimately
/// mints and matches its own <em>session</em> ids with that prefix (<c>SessionId.IsCron</c>,
/// <c>CronScheduler</c>, <c>CronTrigger</c>), and session-id namespacing is a naming convention
/// the cron subsystem owns, not a re-derivation of a conversation's origin by an unrelated
/// surface. The fence therefore targets the CLIENT/render surfaces plus the general shape
/// "probe an id string, then decide how to render/gate a conversation".
/// </para>
/// </summary>
public sealed class ConversationSourceArchitectureTests
{
    // ── Rule 1: write-once origin ────────────────────────────────────────────────

    /// <summary>
    /// The server-side authoritative field. Consolidates (rather than duplicates) the modreq
    /// reflection check shipped alongside slice D: this is the single place the write-once
    /// contract for conversation ORIGIN is asserted.
    /// </summary>
    [Fact]
    public void Conversation_Source_IsInitOnly()
    {
        AssertInitOnly(
            typeof(Conversation),
            nameof(Conversation.Source),
            "Conversation.Source is the authoritative origination trigger stamped once at creation " +
            "(epic #2300). A `set` setter would let any later write re-stamp a persisted " +
            "conversation's origin, which is precisely the mutable-flag defect class #2248 fixed " +
            "for agents. Critically, cron conversations are read-only BECAUSE Source == Cron: make " +
            "Source mutable and 'cron conversations became writable' becomes a one-line regression.");
    }

    /// <summary>
    /// The visibility axis added by #2340, held to the same write-once contract as
    /// <see cref="Conversation.Source"/>.
    /// </summary>
    [Fact]
    public void Conversation_Visibility_IsInitOnly()
    {
        AssertInitOnly(
            typeof(Conversation),
            nameof(Conversation.Visibility),
            "Conversation.Visibility is stamped once at creation by ConversationFactory (#2340) and " +
            "decides whether a row may ever be rendered to a user. A `set` setter would let a later " +
            "write - or an inbound event - surface a runtime bookkeeping thread in the user's " +
            "sidebar, or silently vanish a real conversation from it. Both failure modes are silent, " +
            "which is exactly why the id-prefix probe this field replaced had to go.");
    }

    /// <summary>
    /// The client mirror. Reflected over by name because the architecture test project does not
    /// (and should not) reference the Blazor client assembly — the client type is located through
    /// the already-referenced client Core assembly only if present, otherwise the source-level
    /// assertion below carries the rule.
    /// </summary>
    [Fact]
    public void ClientConversationState_SourceAndKind_AreInitOnly()
    {
        var file = Path.Combine(
            RepoRoot(),
            "src",
            "extensions",
            "BotNexus.Extensions.Channels.SignalR.BlazorClient.Core",
            "Services",
            "Abstractions",
            "IClientStateStore.cs");

        File.Exists(file).ShouldBeTrue($"Expected the client state model at {file}.");

        var source = StripComments(File.ReadAllText(file));

        foreach (var member in new[] { "Source", "Kind", "Visibility" })
        {
            Regex.IsMatch(source, $@"public\s+Conversation{member}\s+{member}\s*{{\s*get;\s*init;")
                .ShouldBeTrue(
                    $"ConversationState.{member} must be declared `{{ get; init; }}`. It is seeded " +
                    "straight from the server payload and is the sole input to " +
                    "ConversationRenderProjection (read-only? show composer? which group/badge?). A " +
                    "`set` setter would let an inbound SignalR event flip a user's own conversation " +
                    "to read-only, or flip a cron conversation to writable, mid-render.");
        }
    }

    // ── Rule 2: no origin inference from id substrings ───────────────────────────

    /// <summary>
    /// Scans every client (portal / mobile / shared Core) source file for a conversation-origin
    /// prefix probe. These are the surfaces that RENDER conversations, so they are exactly where
    /// origin inference would silently return.
    /// </summary>
    [Fact]
    public void ClientSurfaces_DoNotInferConversationOriginFromIdSubstrings()
    {
        var violations = new List<string>();

        foreach (var file in ClientSourceFiles())
        {
            var stripped = StripComments(File.ReadAllText(file));
            foreach (Match match in OriginPrefixProbe().Matches(stripped))
            {
                violations.Add($"  {Rel(file)}: {match.Value.Trim()}");
            }
        }

        violations.ShouldBeEmpty(
            "Client surfaces must not infer a conversation's origin from a session-id or " +
            "conversation-id substring (a `cron:` / `cronconv:` prefix probe). Origin is the " +
            "server-stamped, write-once ConversationSource, surfaced on every conversation payload " +
            "and projected through ConversationRenderProjection (epic #2300). Prefix sniffing is " +
            "per-surface, lossy, and silently disagrees between the portal, mobile, and any future " +
            "rich-render channel — which is the entire defect this epic deleted.\nViolations:\n"
            + string.Join("\n", violations));
    }

    /// <summary>
    /// Anti-vacuity for Rule 2: proves the scan actually reads real files AND that the detector
    /// recognises the exact probe shapes that were deleted by #2305. A fence that matches nothing
    /// is not a fence.
    /// </summary>
    [Fact]
    public void OriginInferenceDetector_IsNotVacuous()
    {
        ClientSourceFiles().Count.ShouldBeGreaterThan(
            50,
            "Rule 2's scan should be reading the whole client tree; a near-empty file set means " +
            "the path resolution broke and the fence silently stopped guarding anything.");

        // The literal shapes deleted by #2305 must all be detected.
        foreach (var deleted in new[]
        {
            "c.ConversationId.StartsWith(\"cronconv:\", StringComparison.OrdinalIgnoreCase)",
            "sid.StartsWith(\"cron:\", StringComparison.OrdinalIgnoreCase)",
            "sessionId.StartsWith(\"cron:\", StringComparison.Ordinal)",
            "conversationId.Contains(\"cron:\")",
        })
        {
            OriginPrefixProbe().IsMatch(deleted).ShouldBeTrue(
                $"Rule 2's detector must match the deleted probe shape: {deleted}");
        }

        // And it must NOT fire on unrelated id handling, so the fence stays usable.
        foreach (var benign in new[]
        {
            "var convId = $\"subagent-session:{subAgentId}\";",
            "if (conversationId.Length == 0) return;",
        })
        {
            OriginPrefixProbe().IsMatch(benign).ShouldBeFalse(
                $"Rule 2's detector must not fire on unrelated id handling: {benign}");
        }
    }

    // ── Rule 2b: no visibility inference from id substrings (#2340) ────────────

    /// <summary>
    /// The companion to Rule 2 for the <em>visibility</em> axis. Until #2340 the portal decided
    /// whether to hide a runtime-internal bookkeeping thread by probing the conversation id for an
    /// <c>internal:</c> prefix - the last id-substring probe in client rendering code, and the sole
    /// allowlisted exception that stopped the inference fence from being absolute. It is now the
    /// write-once, server-stamped <c>ConversationVisibility</c>, and this rule makes the exception
    /// impossible to reintroduce.
    /// </summary>
    /// <remarks>
    /// Kept as a separate rule rather than folded into Rule 2's regex because the two bans have
    /// different rationales and must fail with different messages: Rule 2 is about "why does this
    /// conversation exist", this is about "who may see it". Merging them would produce a failure
    /// message that misdescribes whichever violation actually fired.
    /// </remarks>
    [Fact]
    public void ClientSurfaces_DoNotInferConversationVisibilityFromIdSubstrings()
    {
        var violations = new List<string>();

        foreach (var file in ClientSourceFiles())
        {
            var stripped = StripComments(File.ReadAllText(file));
            foreach (Match match in VisibilityPrefixProbe().Matches(stripped))
            {
                violations.Add($"  {Rel(file)}: {match.Value.Trim()}");
            }
        }

        violations.ShouldBeEmpty(
            "Client surfaces must not decide whether a conversation is user-visible by probing a " +
            "conversation-id substring (an `internal:` prefix test). Visibility is the server-" +
            "stamped, write-once ConversationVisibility, surfaced on every conversation payload " +
            "(#2340). A conversation id is an OPAQUE identifier: keying rendering on its text is a " +
            "hidden coupling between id-minting code and rendering code that nothing enforces, and " +
            "it fails silently in BOTH directions - an internal bookkeeping thread appears in the " +
            "user's sidebar, or a real conversation vanishes from it.\nViolations:\n"
            + string.Join("\n", violations));
    }

    /// <summary>
    /// Anti-vacuity for Rule 2b, mirroring <see cref="OriginInferenceDetector_IsNotVacuous"/>: the
    /// detector must match the exact probe shape #2340 deleted, and must not fire on benign code.
    /// </summary>
    [Fact]
    public void VisibilityInferenceDetector_IsNotVacuous()
    {
        ClientSourceFiles().Count.ShouldBeGreaterThan(
            50,
            "Rule 2b's scan should be reading the whole client tree; a near-empty file set means " +
            "the path resolution broke and the fence silently stopped guarding anything.");

        foreach (var deleted in new[]
        {
            "conversation.ConversationId.StartsWith(\"internal:\", StringComparison.OrdinalIgnoreCase)",
            "c.ConversationId.StartsWith(\"internal:\", StringComparison.Ordinal)",
            "conversationId.Contains(\"internal:\")",
            "id.IndexOf(\"internal:\", StringComparison.Ordinal)",
        })
        {
            VisibilityPrefixProbe().IsMatch(deleted).ShouldBeTrue(
                $"Rule 2b's detector must match the deleted probe shape: {deleted}");
        }

        // The bare channel key "internal" (no colon) is unrelated and must stay usable.
        foreach (var benign in new[]
        {
            "ChannelType = ChannelKey.From(\"internal\");",
            "var convId = $\"subagent-session:{subAgentId}\";",
            "return conversation.Visibility != ConversationVisibility.InternalHidden;",
        })
        {
            VisibilityPrefixProbe().IsMatch(benign).ShouldBeFalse(
                $"Rule 2b's detector must not fire on legitimate code: {benign}");
        }
    }

    // ── Rule 3: the inference flags do not exist ─────────────────────────────────

    /// <summary>
    /// Regression fence against reintroduction. <c>IsVirtualSession</c> / <c>VirtualSessionKind</c>
    /// were the mutable, portal-only, hand-synthesized re-derivation of conversation origin that
    /// epic #2300 deleted. They must not come back — not as a property, not as a local, not as a
    /// fixture field, and not in a comment that invites someone to re-add them.
    /// </summary>
    [Fact]
    public void VirtualSessionFlags_DoNotExistAnywhere()
    {
        var violations = new List<string>();

        foreach (var file in AllProductionAndTestSourceFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("IsVirtualSession", StringComparison.Ordinal)
                    || lines[i].Contains("VirtualSessionKind", StringComparison.Ordinal))
                {
                    violations.Add($"  {Rel(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        violations.ShouldBeEmpty(
            "IsVirtualSession / VirtualSessionKind must not exist (epic #2300, child E/#2305). They " +
            "were a MUTABLE client-side re-derivation of conversation origin: an inbound event could " +
            "poison them and change read-only gating, composer visibility, and list grouping out " +
            "from under the user. Their replacement is the immutable, server-stamped " +
            "(ConversationKind, ConversationSource) pair projected via ConversationRenderProjection. " +
            "If you need to know whether a row was minted locally by the client rather than " +
            "enumerated by the server, use the init-only ConversationState.IsLocallySynthesised — " +
            "that is row PROVENANCE, not conversation ORIGIN.\nViolations:\n"
            + string.Join("\n", violations));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Matches "probe an id-ish expression for a cron origin prefix". Requires BOTH an
    /// id-shaped receiver (something ending in <c>Id</c>, or the common short locals
    /// <c>sid</c>/<c>id</c>) AND a cron-origin literal, so unrelated string work does not trip it.
    /// </summary>
    private static Regex OriginPrefixProbe() => s_originPrefixProbe;

    private static readonly Regex s_originPrefixProbe = new(
        @"\b\w*(?:[Ii]d|sid)\b\s*(?:is\s+\{[^}]*\}\s*\w+\s*)?\.\s*(?:StartsWith|Contains|IndexOf|TrimStart)\s*\(\s*""cron(?:conv)?:",
        RegexOptions.Compiled);

    /// <summary>
    /// Matches "probe an id-ish expression for the runtime-internal namespace prefix" (#2340).
    /// Requires BOTH an id-shaped receiver AND the <c>"internal:"</c> literal <em>with</em> its
    /// colon, so the many legitimate uses of the bare channel key <c>"internal"</c> are untouched.
    /// </summary>
    private static Regex VisibilityPrefixProbe() => s_visibilityPrefixProbe;

    private static readonly Regex s_visibilityPrefixProbe = new(
        @"\b\w*(?:[Ii]d|sid)\b\s*(?:is\s+\{[^}]*\}\s*\w+\s*)?\.\s*(?:StartsWith|Contains|IndexOf|TrimStart)\s*\(\s*""internal:",
        RegexOptions.Compiled);

    private static void AssertInitOnly(Type type, string propertyName, string because)
    {
        var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        prop.ShouldNotBeNull($"{type.Name}.{propertyName} must exist. {because}");

        var setter = prop!.GetSetMethod(nonPublic: false);
        setter.ShouldNotBeNull(
            $"{type.Name}.{propertyName} must expose an init setter so the record `with` builder " +
            $"pattern keeps working. {because}");

        setter!.ReturnParameter.GetRequiredCustomModifiers().ShouldContain(
            t => t.FullName == "System.Runtime.CompilerServices.IsExternalInit",
            $"{type.Name}.{propertyName} must be `init`-only, not `set`. {because}");
    }

    private static IReadOnlyList<string> ClientSourceFiles()
    {
        var extensions = Path.Combine(RepoRoot(), "src", "extensions");
        return Directory
            .EnumerateDirectories(extensions, "BotNexus.Extensions.Channels.SignalR.BlazorClient*")
            .SelectMany(d => Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
    }

    private static IReadOnlyList<string> AllProductionAndTestSourceFiles()
    {
        var root = RepoRoot();
        return new[] { Path.Combine(root, "src"), Path.Combine(root, "tests") }
            .SelectMany(d => Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            // This fence names the banned identifiers in its own failure messages.
            .Where(f => !Path.GetFileName(f).Equals(
                "ConversationSourceArchitectureTests.cs", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static string Rel(string file) =>
        Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/');

    private static string StripComments(string source)
    {
        var noBlock = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(noBlock, @"//[^\r\n]*", string.Empty);
    }

    private static string RepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")))
            current = current.Parent;

        current.ShouldNotBeNull("Could not locate repo root from " + AppContext.BaseDirectory);
        return current!.FullName;
    }
}
