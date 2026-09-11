namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// The operator's decision about which environment variables tool subprocesses may see, shared by
/// every tool that spawns a child process.
/// </summary>
/// <remarks>
/// <para>
/// Tool subprocesses receive an environment built from a fixed allow-list rather than inherited
/// from the gateway, so that an agent holding <c>bash</c> or <c>exec</c> cannot read the provider
/// keys and <c>env:</c> credentials the gateway runs under. This contract carries the one part an
/// operator can change: extra variable names to expose on top of that list.
/// </para>
/// <para>
/// It lives in Abstractions, and is an interface rather than a configuration type, so that the
/// built-in <c>shell</c> tool and the <c>exec</c> extension read the SAME answer. Those two tools
/// have diverged before - see the remarks on <c>ExecToolContributor</c> for issue #2416, where
/// <c>exec</c> silently used a different working directory from <c>shell</c> for months. A
/// security control that applies to one of them and not the other would fail the same way, and
/// would be harder to notice.
/// </para>
/// </remarks>
public interface IToolEnvironmentPolicy
{
    /// <summary>
    /// Extra environment variable names exposed to tool subprocesses, on top of the built-in
    /// allow-list. Empty by default.
    /// </summary>
    IReadOnlyList<string> PassThroughVariables { get; }
}
