using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the Phase 9 / P9-F contract: participants
/// live on <see cref="Conversation"/>, never on <see cref="Session"/> or
/// <see cref="GatewaySession"/>. The pre-P9-F duplication — every session carrying its
/// own <c>Participants</c> list and producers writing both shapes — must never reappear
/// in production code.
/// </summary>
/// <remarks>
/// <para>
/// Before P9-F, <c>Session.Participants</c> was the durable list and 6 producer sites
/// mutated it directly. The conversation was the natural owner (long-lived; one
/// participant list per topic); the session is just an active window of work. Channel
/// queries like "what conversations is this citizen in?" became O(sessions) scans.
/// P9-F deletes the field on Session + GatewaySession, adds
/// <see cref="Conversation.Participants"/>, and routes all writes through
/// <c>IConversationStore.AddParticipantsAsync</c> for atomic merge.
/// </para>
/// <para>
/// Three reflection pins (field-existence) plus a source-text fence (call-site shape)
/// defend the invariant. Each fence ships vacuity / false-positive / comment-mention
/// self-tests, per the stored memory "every regex-based architecture fence must include
/// a self-test asserting it matches its target methods/symbols".
/// </para>
/// </remarks>
public sealed class SessionParticipantsRemovedArchitectureTests
{
    [Fact]
    public void Session_DoesNotExpose_ParticipantsProperty()
    {
        var sessionType = typeof(Session);
        var participants = sessionType.GetProperty("Participants", BindingFlags.Public | BindingFlags.Instance);
        participants.ShouldBeNull(
            "P9-F: Session.Participants is deleted. The list lives on Conversation now and " +
            "is mutated via IConversationStore.AddParticipantsAsync. If this fails, either " +
            "the field was re-added (regression — re-delete) or the ownership model has " +
            "shifted (update this fence and re-write the contract).");
    }

    [Fact]
    public void GatewaySession_DoesNotExpose_ParticipantsFacade()
    {
        var gatewaySessionType = typeof(GatewaySession);
        var participants = gatewaySessionType.GetProperty("Participants", BindingFlags.Public | BindingFlags.Instance);
        participants.ShouldBeNull(
            "P9-F: GatewaySession.Participants facade is deleted alongside Session.Participants. " +
            "Producers must resolve the conversation by id and call AddParticipantsAsync " +
            "instead of pretending the session owns the list.");
    }

    [Fact]
    public void Conversation_ExposesParticipantsList_OfTypeListOfSessionParticipant()
    {
        var conversationType = typeof(Conversation);
        var participants = conversationType.GetProperty("Participants", BindingFlags.Public | BindingFlags.Instance);
        participants.ShouldNotBeNull(
            "P9-F: Conversation.Participants is the canonical participant list. If this fails, " +
            "either the field was deleted (regression — restore) or the P9-F migration has been " +
            "rolled back. Either way: stop and reconcile before continuing.");
        participants!.PropertyType.ShouldBe(
            typeof(List<SessionParticipant>),
            "Conversation.Participants must be List<SessionParticipant> so stores can replay " +
            "the list when materialising a Conversation from a join.");
    }

    [Fact]
    public void NoProductionSourceFile_AccessesLegacy_SessionParticipantsProperty()
    {
        var srcRoot = FindSourceRoot();
        var violations = new List<string>();

        foreach (var path in EnumerateProductionCsFiles(srcRoot))
        {
            var relative = ToRelative(srcRoot, path);
            var stripped = StripComments(File.ReadAllText(path));
            if (s_sessionParticipantsAccessPattern.IsMatch(stripped))
                violations.Add(relative);
        }

        violations.ShouldBeEmpty(
            "P9-F: production code must not read or write `Session.Participants` / " +
            "`GatewaySession.Participants` / `session.Participants`. The field is deleted; " +
            "mutate the list through IConversationStore.AddParticipantsAsync against the " +
            "owning Conversation, and read by materialising the Conversation.\n" +
            "Violations:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void Fence_IsNotVacuous_AgainstSyntheticReintroduction()
    {
        // If a regression PR adds either shape back, the fence MUST trip. Vacuity guard.
        s_sessionParticipantsAccessPattern.IsMatch("session.Participants.Add(p);")
            .ShouldBeTrue("Vacuity guard: instance access `session.Participants` must trip the fence.");
        s_sessionParticipantsAccessPattern.IsMatch("var p = gatewaySession.Participants;")
            .ShouldBeTrue("Vacuity guard: instance access `gatewaySession.Participants` must trip the fence.");
        s_sessionParticipantsAccessPattern.IsMatch("Session.Participants = new List<SessionParticipant>();")
            .ShouldBeTrue("Vacuity guard: static-style access `Session.Participants` must trip the fence.");
        s_sessionParticipantsAccessPattern.IsMatch("var x = GatewaySession.Participants.Count;")
            .ShouldBeTrue("Vacuity guard: static-style access `GatewaySession.Participants` must trip the fence.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnUnrelatedAccess()
    {
        // The fence is keyed on `(GatewaySession|Session|session|gatewaySession).Participants` —
        // accesses against other identifiers (Conversation, conversation, conv) must not match.
        s_sessionParticipantsAccessPattern.IsMatch("var list = conversation.Participants;")
            .ShouldBeFalse("False-positive guard: `conversation.Participants` is the canonical access — must NOT match.");
        s_sessionParticipantsAccessPattern.IsMatch("var list = Conversation.Participants;")
            .ShouldBeFalse("False-positive guard: `Conversation.Participants` (the new canonical owner) must NOT match.");
        s_sessionParticipantsAccessPattern.IsMatch("var conv = await store.GetAsync(id); var count = conv.Participants.Count;")
            .ShouldBeFalse("False-positive guard: `conv.Participants` (Conversation alias) must NOT match.");
        s_sessionParticipantsAccessPattern.IsMatch("var participantsList = new List<SessionParticipant>();")
            .ShouldBeFalse("False-positive guard: declaring a local list of SessionParticipant must NOT match.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnCommentMentions()
    {
        // Comments that historically reference the deleted shape (e.g. the P9-F migration
        // marker in Session.cs / GatewaySession.cs) must not trip the fence.
        const string syntheticClean = """
            /// <summary>
            /// Pre-P9-F, Session.Participants was the durable list. The field is deleted;
            /// participants live on Conversation.Participants now. See gatewaySession.Participants
            /// references for the migration markers in Session.cs and GatewaySession.cs.
            /// </summary>
            // Legacy: session.Participants was migrated to Conversation.Participants in P9-F.
            /* Block comment: Session.Participants / GatewaySession.Participants were removed in P9-F. */
            public void Documented() { }
            """;

        s_sessionParticipantsAccessPattern.IsMatch(StripComments(syntheticClean)).ShouldBeFalse(
            "False-positive guard: comment mentions of the deleted `Session.Participants` shape for " +
            "historical context must not trip — the lexer strips them.");
    }

    // Match `(Session|GatewaySession|session|gatewaySession).Participants` where the dot
    // is immediately followed by `Participants` (word-boundary), so adjacent identifiers
    // like `Participants` as a parameter name don't trigger. Lexer-stripped at call time
    // so comments don't match.
    private static readonly Regex s_sessionParticipantsAccessPattern = new(
        @"\b(?:GatewaySession|Session|session|gatewaySession)\.Participants\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static IEnumerable<string> EnumerateProductionCsFiles(string srcRoot)
    {
        return Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
    }

    private static string ToRelative(string srcRoot, string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        var root = Path.GetFullPath(srcRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? full.Substring(root.Length)
            : full;
    }

    private static string FindSourceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }
        current.ShouldNotBeNull("Could not locate repo root from " + AppContext.BaseDirectory);
        var srcRoot = Path.Combine(current.FullName, "src");
        Directory.Exists(srcRoot).ShouldBeTrue("Expected src/ under " + current.FullName);
        return srcRoot;
    }

    /// <summary>
    /// Removes single-line (<c>//</c>, <c>///</c>) and block (<c>/* … */</c>) C# comments
    /// while preserving string and char literals. Same lexer pattern used by other
    /// architecture fences (e.g. <see cref="CronConversationOwnershipArchitectureTests"/>).
    /// </summary>
    private static string StripComments(string source)
    {
        var sb = new StringBuilder(source.Length);
        var i = 0;
        var n = source.Length;

        while (i < n)
        {
            var c = source[i];

            if (c == '@' && i + 1 < n && source[i + 1] == '"')
            {
                sb.Append(source, i, 2);
                i += 2;
                while (i < n)
                {
                    if (source[i] == '"')
                    {
                        if (i + 1 < n && source[i + 1] == '"')
                        {
                            sb.Append("\"\"");
                            i += 2;
                            continue;
                        }
                        sb.Append('"');
                        i++;
                        break;
                    }
                    sb.Append(source[i++]);
                }
                continue;
            }

            if (c == '"')
            {
                sb.Append('"');
                i++;
                while (i < n)
                {
                    if (source[i] == '\\' && i + 1 < n)
                    {
                        sb.Append(source[i]);
                        sb.Append(source[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (source[i] == '"')
                    {
                        sb.Append('"');
                        i++;
                        break;
                    }
                    sb.Append(source[i++]);
                }
                continue;
            }

            if (c == '\'')
            {
                sb.Append('\'');
                i++;
                while (i < n)
                {
                    if (source[i] == '\\' && i + 1 < n)
                    {
                        sb.Append(source[i]);
                        sb.Append(source[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (source[i] == '\'')
                    {
                        sb.Append('\'');
                        i++;
                        break;
                    }
                    sb.Append(source[i++]);
                }
                continue;
            }

            if (c == '/' && i + 1 < n && source[i + 1] == '/')
            {
                i += 2;
                while (i < n && source[i] != '\n' && source[i] != '\r')
                {
                    i++;
                }
                continue;
            }

            if (c == '/' && i + 1 < n && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    i++;
                }
                i = Math.Min(n, i + 2);
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }
}
