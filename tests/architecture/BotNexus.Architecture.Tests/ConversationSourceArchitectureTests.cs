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
///     <b>Rule 2 — no origin inference.</b> No production code may re-derive conversation origin
///     from an id substring (a <c>cron:</c>/<c>cronconv:</c>-style prefix probe). Origin is a
///     modelled field; inference is per-surface, lossy and drifts.
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

        foreach (var member in new[] { "Source", "Kind" })
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
            "conversation.ConversationId.StartsWith(\"internal:\", StringComparison.OrdinalIgnoreCase)",
            "var convId = $\"subagent-session:{subAgentId}\";",
        })
        {
            OriginPrefixProbe().IsMatch(benign).ShouldBeFalse(
                $"Rule 2's detector must not fire on unrelated id handling: {benign}");
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
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
            current = current.Parent;

        current.ShouldNotBeNull("Could not locate repo root from " + AppContext.BaseDirectory);
        return current!.FullName;
    }
}
