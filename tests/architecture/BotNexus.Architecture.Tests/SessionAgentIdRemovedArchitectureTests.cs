using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness functions enforcing the Phase 9 / P9-H (#662) "Conversation
/// owns agent identity" contract:
/// <list type="bullet">
///   <item>
///     <see cref="Session"/> has no <c>AgentId</c> property at all. Agent ownership
///     lives on <see cref="Conversation"/>.<see cref="Conversation.AgentId"/>; the
///     session links via <see cref="Session.ConversationId"/> and looks the agent
///     up through that.
///   </item>
///   <item>
///     <see cref="GatewaySession.AgentId"/> exists as a <em>hydrated runtime cache</em>
///     populated by <see cref="ISessionStore"/> implementations from
///     <c>Conversation.AgentId</c>. The cache is structurally safe because
///     <c>Conversation.AgentId</c> is <c>init</c>-only (see
///     <see cref="ConversationAgentIdImmutabilityArchitectureTests"/>). The property
///     must expose only an <c>init</c>-style setter (no public <c>set</c>) so it
///     cannot drift after construction; the sole mutator past construction is
///     <c>GatewaySession.HydrateAgentId(AgentId)</c> for store-load paths.
///   </item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Ships vacuity / false-positive / comment-mention self-tests per the "every
/// regex-based architecture fence must include a self-test" memory.
/// </para>
/// </remarks>
public sealed class SessionAgentIdRemovedArchitectureTests
{
    [Fact]
    public void Session_HasNoAgentIdProperty()
    {
        var prop = typeof(Session).GetProperty(
            "AgentId",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

        prop.ShouldBeNull(
            "P9-H contract: Session.AgentId is deleted. Agent ownership is durably owned " +
            "by Conversation.AgentId — sessions look it up via Session.ConversationId.");
    }

    [Fact]
    public void Session_HasNoAgentIdField()
    {
        var field = typeof(Session).GetField(
            "AgentId",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

        field.ShouldBeNull(
            "P9-H contract: Session has no AgentId backing field. The field was removed " +
            "with the property to prevent silent reintroduction.");
    }

    [Fact]
    public void GatewaySession_AgentId_IsInitOnly()
    {
        var prop = typeof(GatewaySession).GetProperty(
            nameof(GatewaySession.AgentId),
            BindingFlags.Public | BindingFlags.Instance);

        prop.ShouldNotBeNull(
            "P9-H contract: GatewaySession.AgentId is retained as the hydrated runtime " +
            "cache populated by stores from Conversation.AgentId.");
        prop.CanRead.ShouldBeTrue();

        var setter = prop.GetSetMethod(nonPublic: false);
        setter.ShouldNotBeNull("P9-H contract: GatewaySession.AgentId must expose an init setter.");

        // An `init` setter is a public method whose return-parameter modreqs include
        // System.Runtime.CompilerServices.IsExternalInit. A `set` setter does not.
        var requiredModifiers = setter.ReturnParameter.GetRequiredCustomModifiers();
        requiredModifiers.ShouldContain(
            t => t.FullName == "System.Runtime.CompilerServices.IsExternalInit",
            "P9-H contract: GatewaySession.AgentId must be `init`-only so a hydrated " +
            "AgentId cannot drift after construction. A regular `set` setter would " +
            "allow write-through facades to creep back in.");
    }

    [Fact]
    public void GatewaySession_HydrateAgentId_IsTheOnlyPostInitMutator()
    {
        var method = typeof(GatewaySession).GetMethod(
            "HydrateAgentId",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: [typeof(AgentId)],
            modifiers: null);

        method.ShouldNotBeNull(
            "P9-H contract: GatewaySession.HydrateAgentId(AgentId) is the only sanctioned " +
            "post-construction mutator. Used by FromSession + store load paths that build " +
            "the wrapper first and look up the conversation second.");
    }

    [Fact]
    public void NoProductionSourceFile_AssignsAgentIdAfterConstruction_OutsideAllowlist()
    {
        var srcRoot = FindSourceRoot();
        var violations = new List<string>();

        foreach (var path in EnumerateProductionCsFiles(srcRoot))
        {
            var relative = ToRelative(srcRoot, path);
            if (s_assignAllowlist.Contains(relative)) continue;

            var stripped = StripComments(File.ReadAllText(path));
            if (s_assignPattern.IsMatch(stripped))
                violations.Add(relative);
        }

        violations.ShouldBeEmpty(
            "P9-H: GatewaySession.AgentId / session.AgentId may only be assigned inside " +
            "object initializers in the sanctioned store-load sites (which receive the " +
            "hydrated value from Conversation.AgentId). Post-construction reassignment " +
            "must go through HydrateAgentId.\n" +
            "Violations:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void AssignAllowlist_OnlyContains_FilesThat_StillExist_AndStill_TripTheFence()
    {
        var srcRoot = FindSourceRoot();
        var stale = new List<string>();

        foreach (var relative in s_assignAllowlist)
        {
            var full = Path.Combine(srcRoot, relative);
            if (!File.Exists(full))
            {
                stale.Add($"{relative} — file does not exist (delete this allowlist entry)");
                continue;
            }
            if (!s_assignPattern.IsMatch(StripComments(File.ReadAllText(full))))
            {
                stale.Add($"{relative} — file no longer trips the fence (delete this allowlist entry)");
            }
        }

        stale.ShouldBeEmpty(
            "Allowlist hygiene: every entry must still match an existing file AND still trip the fence; " +
            "otherwise it grants a permanent exemption to future regressions in the same file.\n" +
            "Stale entries:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void Fence_IsNotVacuous_AgainstSyntheticReintroduction()
    {
        s_assignPattern.IsMatch("session.AgentId = AgentId.From(\"x\");")
            .ShouldBeTrue("Vacuity guard: a real post-init write must trip the fence.");
        s_assignPattern.IsMatch("gatewaySession.AgentId = id;")
            .ShouldBeTrue("Vacuity guard: gatewaySession.AgentId writes must trip.");
        s_assignPattern.IsMatch("    session.AgentId   =   value;")
            .ShouldBeTrue("Vacuity guard: extra whitespace must trip.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnUnrelatedSymbols()
    {
        // Reads, comparisons and object-initializer-shape writes outside the matched
        // identifier prefix must NOT match.
        s_assignPattern.IsMatch("var id = session.AgentId;")
            .ShouldBeFalse("False-positive guard: reads must not match.");
        s_assignPattern.IsMatch("if (session.AgentId == agentId) { }")
            .ShouldBeFalse("False-positive guard: comparisons must not match.");
        s_assignPattern.IsMatch("var match = session.AgentId == agentId;")
            .ShouldBeFalse("False-positive guard: comparison assigned to a local must not match.");
        s_assignPattern.IsMatch("predicate: session => session.AgentId == id")
            .ShouldBeFalse("False-positive guard: lambda body comparison must not match.");
        s_assignPattern.IsMatch("AgentId = AgentId.From(\"x\")")
            .ShouldBeFalse("False-positive guard: bare object-initializer `AgentId =` " +
                "(without a `session.` or `gatewaySession.` qualifier) must not match. " +
                "Object initializers are the sanctioned construction path.");
        s_assignPattern.IsMatch("envelope.AgentId = id;")
            .ShouldBeFalse("False-positive guard: writes to other `.AgentId` properties " +
                "on unrelated types (envelopes, DTOs) must not match.");
    }

    [Fact]
    public void Fence_DoesNotFalsePositive_OnCommentMentions()
    {
        const string syntheticClean = """
            /// <summary>
            /// Callers must NOT do `session.AgentId = id` post-construction — use HydrateAgentId.
            /// </summary>
            // Legacy: pre-P9-H code did `session.AgentId = someValue;` — that shape is dead.
            /* Block comment: gatewaySession.AgentId = ... outside HydrateAgentId is banned. */
            public void Documented() { }
            """;

        s_assignPattern.IsMatch(StripComments(syntheticClean)).ShouldBeFalse(
            "False-positive guard: comment mentions of the write shape for historical " +
            "context or doc warnings must not trip — the lexer strips them.");
    }

    // Matches post-construction reassignment shapes: `session.AgentId = ...` or
    // `gatewaySession.AgentId = ...`. The negative-lookahead `(?![=>])` rules out
    // comparisons (`==`) and lambda arrows (`=>`) so the fence only trips on real
    // single-`=` assignments. Object-initializer shape (`AgentId = ...` inside a
    // `new GatewaySession { ... }` block) is intentionally excluded — that's the
    // sanctioned construction path used by stores.
    private static readonly Regex s_assignPattern = new(
        @"\b(?:session|gatewaySession)\.AgentId\s*=(?![=>])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Sanctioned files: none (currently). All construction goes through object
    // initializers; HydrateAgentId is a method call not an assignment, so it's
    // structurally outside the regex. If a legitimate post-construction reassignment
    // ever needs to exist, add the file here with a comment justifying it.
    private static readonly HashSet<string> s_assignAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
    };

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
    /// architecture fences (e.g. <see cref="ConversationsControllerCitizenListingArchitectureTests"/>).
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
