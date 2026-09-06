using System.Reflection;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Extensions.Plugins.Agents;

/// <summary>
/// The privilege fence applied to every agent descriptor a plugin ships (#2685).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this governs.</b> A plugin arrives from a marketplace, so its agent descriptor is
/// attacker-controlled input. The fence decides what such a descriptor may <i>declare</i>;
/// runtime sandboxing of the resulting agent is explicitly out of scope for this slice.
/// </para>
/// <para>
/// <b>Why the classification is structural, and deny by default.</b> The obvious implementation is
/// a list of forbidden member names. That list is correct only on the day it is written: the next
/// property added to <see cref="AgentDescriptor"/> is not on it, so it is permitted the instant it
/// exists - silently, with no error and no log, which is precisely how a privilege surface opens
/// without anyone deciding to open it. So the fence enumerates the descriptor's settable members
/// by reflection and treats "not explicitly permitted" as fenced.
/// <see cref="FencedMembers"/> is therefore a computed complement, never a literal list, and a
/// member added tomorrow is rejected tomorrow.
/// </para>
/// <para>
/// The classification is pinned by <c>PluginAgentPrivilegeFenceArchitectureTests</c>, in the shape
/// of the #2588 fingerprint fence: widening <see cref="DeclarableMembers"/> fails that test until
/// the same decision is mirrored in it, so growing the plugin privilege surface cannot happen as a
/// quiet one-line edit.
/// </para>
/// <para>
/// <b>Three outcomes, not two.</b> Most fenced members are rejected outright, because a plugin
/// declaring an isolation strategy or a shell command has no legitimate reduced form - either it
/// runs privileged or it does not. <see cref="AgentDescriptor.FileAccess"/> is the exception: a
/// path grant is meaningful at reduced scope, so it is <i>narrowed</i> to the installing user's own
/// ceiling rather than refused (#2685 clause 3).
/// </para>
/// </remarks>
public static class PluginAgentDescriptorFence
{
    /// <summary>
    /// Members a plugin-shipped descriptor may populate. Each is identity, presentation, prompt
    /// text, or model selection - none of them grants access the installing user does not already
    /// have, and each is constrained downstream by host-owned registries.
    /// </summary>
    /// <remarks>
    /// <see cref="AgentDescriptor.ToolIds"/> is here because a tool id names a tool the HOST has
    /// registered; an id the host does not know resolves to nothing. Declaring one cannot conjure
    /// a capability that is not already installed and enabled by the user.
    /// </remarks>
    public static IReadOnlyList<string> DeclarableMembers { get; } =
    [
        nameof(AgentDescriptor.AllowedModelIds),
        nameof(AgentDescriptor.ApiProvider),
        nameof(AgentDescriptor.CacheRetentionMode),
        nameof(AgentDescriptor.ContextWindow),
        nameof(AgentDescriptor.ConversationRetention),
        nameof(AgentDescriptor.DateTimeInjection),
        nameof(AgentDescriptor.Description),
        nameof(AgentDescriptor.DisplayName),
        nameof(AgentDescriptor.Emoji),
        nameof(AgentDescriptor.Heartbeat),
        nameof(AgentDescriptor.MaxConcurrentSessions),
        nameof(AgentDescriptor.Memory),
        nameof(AgentDescriptor.Metadata),
        nameof(AgentDescriptor.ModelId),
        nameof(AgentDescriptor.Order),
        nameof(AgentDescriptor.Soul),
        nameof(AgentDescriptor.Summary),
        nameof(AgentDescriptor.SystemPrompt),
        nameof(AgentDescriptor.SystemPromptFile),
        nameof(AgentDescriptor.SystemPromptFiles),
        nameof(AgentDescriptor.Thinking),
        nameof(AgentDescriptor.ToolIds),
    ];

    /// <summary>
    /// Members accepted at reduced scope rather than rejected. Only file access qualifies: a
    /// declared path set has a coherent meaning when clamped to the installing user's ceiling,
    /// where an isolation strategy or a shell command does not.
    /// </summary>
    public static IReadOnlyList<string> NarrowedMembers { get; } =
    [
        nameof(AgentDescriptor.FileAccess),
    ];

    /// <summary>
    /// The fenced set: every settable descriptor member that is neither declarable nor narrowed.
    /// <b>Computed, never enumerated</b> - that is what makes a member added to
    /// <see cref="AgentDescriptor"/> tomorrow fenced by default rather than silently permitted
    /// (#2685 clause 4).
    /// </summary>
    public static IReadOnlyList<string> FencedMembers { get; } = SettableDescriptorMembers()
        .Where(name => !DeclarableMembers.Contains(name, StringComparer.Ordinal)
                       && !NarrowedMembers.Contains(name, StringComparer.Ordinal))
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray();

    /// <summary>
    /// Whether a plugin descriptor may declare <paramref name="memberName"/>. Unknown names are
    /// <b>not</b> declarable: the default is deny, so a caller asking about a member the fence has
    /// never heard of gets the safe answer.
    /// </summary>
    /// <param name="memberName">Descriptor member name.</param>
    public static bool IsDeclarable(string memberName) =>
        DeclarableMembers.Contains(memberName, StringComparer.Ordinal);

    /// <summary>
    /// Applies the fence to a candidate descriptor.
    /// </summary>
    /// <param name="candidate">Descriptor as the plugin declared it.</param>
    /// <param name="ceiling">
    /// The installing user's own file-access ceiling. <c>null</c> means workspace-only access, in
    /// which case a plugin-declared path grant narrows to nothing - a plugin cannot be granted
    /// more than the user who installed it has.
    /// </param>
    /// <returns>
    /// An accepted result carrying the narrowed descriptor, or a rejected result whose
    /// <see cref="PluginAgentFenceResult.Rejections"/> name every offending field.
    /// </returns>
    public static PluginAgentFenceResult Apply(AgentDescriptor candidate, FileAccessPolicy? ceiling)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var rejections = new List<string>();
        var narrowings = new List<string>();

        // A fenced member is only an escalation when it actually carries a declaration. Left at
        // its default it says nothing, and rejecting it would make every plugin agent unloadable.
        var reference = ReferenceDescriptor(candidate);
        foreach (var member in FencedMembers)
        {
            var property = typeof(AgentDescriptor).GetProperty(
                member,
                BindingFlags.Public | BindingFlags.Instance);
            if (property is null)
                continue;

            if (IsDefaultValued(property.GetValue(candidate), property.GetValue(reference)))
                continue;

            rejections.Add(
                $"{member}: a plugin-shipped agent may not declare '{member}'. Plugin agent "
                + "descriptors are fenced to identity, prompt and model selection; declaring "
                + "isolation, sub-agent grants, session or conversation access, shell commands or "
                + "extension configuration (hooks, MCP servers) would let a marketplace bundle "
                + "escalate its own privileges past the installing user. Remove the field from "
                + "the plugin's agent descriptor.");
        }

        // A denial cannot be narrowed by dropping it: that would widen access. Without an
        // explicit owner workspace, a relative directory denial cannot be transferred safely.
        // Globs are the exception: DefaultPathValidator matches them without workspace anchoring.
        foreach (var denied in (candidate.FileAccess?.DeniedPaths ?? []).Concat(ceiling?.DeniedPaths ?? []))
        {
            if (!string.IsNullOrWhiteSpace(denied)
                && !denied.Contains('*') && !denied.Contains('?')
                && !Path.IsPathFullyQualified(denied.Trim()))
            {
                rejections.Add(
                    $"{nameof(AgentDescriptor.FileAccess)}.{nameof(FileAccessPolicy.DeniedPaths)}: "
                    + "non-glob denials must be fully qualified absolute paths; a relative "
                    + "denial has no unambiguous owner workspace and cannot be transplanted.");
            }
        }

        if (rejections.Count > 0)
            return PluginAgentFenceResult.Rejected(rejections);

        var (fileAccess, narrowed) = NarrowFileAccess(candidate.FileAccess, ceiling);
        if (narrowed)
        {
            narrowings.Add(
                $"{nameof(AgentDescriptor.FileAccess)}: the plugin declared paths outside the "
                + "installing user's file-access ceiling; the policy was narrowed to that ceiling "
                + "rather than rejected. The agent will see fewer paths than the plugin asked for.");
        }

        return PluginAgentFenceResult.Accepted(candidate with { FileAccess = fileAccess }, narrowings);
    }

    /// <summary>
    /// Narrows a plugin-declared file-access policy to <paramref name="ceiling"/>.
    /// </summary>
    /// <remarks>
    /// Grants are intersected by containment, not by string equality: a declared path is kept when
    /// it is at or beneath a path the ceiling already allows, so a plugin may legitimately ask for
    /// a subdirectory of a granted tree. Denials are <b>unioned</b> in the other direction - the
    /// ceiling's denials always apply, because a plugin must not be able to un-deny a path merely
    /// by omitting it from its own list.
    /// </remarks>
    private static (FileAccessPolicy? Policy, bool Narrowed) NarrowFileAccess(
        FileAccessPolicy? declared,
        FileAccessPolicy? ceiling)
    {
        // Omission grants no extra paths, but must not erase ceiling restrictions inside the
        // workspace. Carry denials even when there is no plugin policy to intersect.
        var reads = ClampPaths(declared?.AllowedReadPaths ?? [], ceiling?.AllowedReadPaths);
        var writes = ClampPaths(declared?.AllowedWritePaths ?? [], ceiling?.AllowedWritePaths);

        var denies = (declared?.DeniedPaths ?? [])
            .Concat(ceiling?.DeniedPaths ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        if (declared is null && denies.Length == 0)
            return (null, false);

        var narrowed = reads.Count != (declared?.AllowedReadPaths.Count ?? 0)
                       || writes.Count != (declared?.AllowedWritePaths.Count ?? 0);

        return (
            new FileAccessPolicy
            {
                AllowedReadPaths = reads,
                AllowedWritePaths = writes,
                DeniedPaths = denies
            },
            narrowed);
    }

    private static IReadOnlyList<string> ClampPaths(
        IReadOnlyList<string> declared,
        IReadOnlyList<string>? ceiling)
    {
        // No ceiling means the installing user has no path grants of their own beyond the
        // workspace default, so there is nothing for a plugin grant to be a subset of.
        if (ceiling is null || ceiling.Count == 0)
            return [];

        return declared
            .Where(path => !string.IsNullOrWhiteSpace(path)
                           && Path.IsPathFullyQualified(path.Trim())
                           && ceiling.Any(allowed => IsWithin(path, allowed)))
            .ToArray();
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is at or beneath <paramref name="allowed"/>. Compared
    /// on normalised full paths so a declaration cannot escape the ceiling through a relative
    /// segment or a trailing separator.
    /// </summary>
    private static bool IsWithin(string candidate, string allowed)
    {
        // Never infer the installing ceiling's origin from this process's cwd. The consumer
        // resolves relative policy entries against an agent workspace, which can be elsewhere.
        if (string.IsNullOrWhiteSpace(allowed) || !Path.IsPathFullyQualified(allowed.Trim()))
            return false;

        string candidateFull;
        string allowedFull;
        try
        {
            candidateFull = Normalise(candidate);
            allowedFull = Normalise(allowed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An unparseable path cannot be proven to be inside the ceiling, so it is outside it.
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(candidateFull, allowedFull, comparison))
            return true;

        var prefix = allowedFull.EndsWith(Path.DirectorySeparatorChar)
            ? allowedFull
            : allowedFull + Path.DirectorySeparatorChar;

        return candidateFull.StartsWith(prefix, comparison);
    }

    private static string Normalise(string path)
    {
        var full = Path.GetFullPath(path.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
        // Keep a drive/UNC/filesystem root intact rather than turning C:\ into drive-relative C:.
        var root = Path.GetPathRoot(full);
        return string.Equals(full, root, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? full
            : full.TrimEnd(Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// A pristine descriptor carrying the same required members as <paramref name="candidate"/>,
    /// used to read each fenced member's default value off the type itself rather than restating
    /// the defaults here. Restating them is how a default silently drifts out of sync with the
    /// descriptor and turns "unset" into "escalation" (or worse, the reverse).
    /// </summary>
    private static AgentDescriptor ReferenceDescriptor(AgentDescriptor candidate) => new()
    {
        AgentId = candidate.AgentId,
        DisplayName = candidate.DisplayName,
        ModelId = candidate.ModelId,
        ApiProvider = candidate.ApiProvider
    };

    private static bool IsDefaultValued(object? actual, object? reference)
    {
        if (actual is null)
            return true;

        // Empty collections are the absence of a declaration, whatever instance carries them.
        if (actual is System.Collections.IEnumerable actualSequence and not string)
        {
            var any = false;
            foreach (var _ in actualSequence)
            {
                any = true;
                break;
            }

            return !any;
        }

        if (actual is AgentKind kind)
            return kind == AgentKind.Named;

        return reference is not null && actual.Equals(reference);
    }

    /// <summary>
    /// The descriptor members a configuration source can populate: public, instance, settable
    /// properties declared on <see cref="AgentDescriptor"/>. Settability is the discriminator - a
    /// get-only member is derived from these and cannot be declared independently.
    /// </summary>
    private static IEnumerable<string> SettableDescriptorMembers() =>
        typeof(AgentDescriptor)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.SetMethod is not null && p.SetMethod.IsPublic)
            .Select(p => p.Name);
}

/// <summary>
/// Outcome of applying <see cref="PluginAgentDescriptorFence"/> to one plugin-shipped descriptor.
/// </summary>
/// <remarks>
/// Rejections and narrowings are kept apart because they are different security events. A
/// rejection means the descriptor never loads and the plugin author must change it; a narrowing
/// means the agent loads with less than it asked for, which the author needs told but the user
/// does not need to act on.
/// </remarks>
public sealed record PluginAgentFenceResult
{
    private PluginAgentFenceResult()
    {
    }

    /// <summary>Whether the descriptor survived the fence.</summary>
    public bool IsAccepted { get; private init; }

    /// <summary>The fenced descriptor, or <c>null</c> when the candidate was rejected.</summary>
    public AgentDescriptor? Descriptor { get; private init; }

    /// <summary>
    /// One message per offending field, each naming that field (#2685 clause 2). Empty on
    /// acceptance.
    /// </summary>
    public IReadOnlyList<string> Rejections { get; private init; } = [];

    /// <summary>
    /// One message per member whose declaration was clamped. Empty when nothing was narrowed.
    /// </summary>
    public IReadOnlyList<string> Narrowings { get; private init; } = [];

    internal static PluginAgentFenceResult Accepted(
        AgentDescriptor descriptor,
        IReadOnlyList<string> narrowings) => new()
        {
            IsAccepted = true,
            Descriptor = descriptor,
            Narrowings = narrowings
        };

    internal static PluginAgentFenceResult Rejected(IReadOnlyList<string> rejections) => new()
    {
        IsAccepted = false,
        Rejections = rejections
    };
}
