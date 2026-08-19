namespace BotNexus.Gateway.Contracts.Memory;

/// <summary>
/// Agent-scoped, live enablement check for the memory tools (issue #3361).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the tools need this at all.</b> Memory tool registration is gated once, at handle
/// creation, from <c>descriptor.Memory?.Enabled</c>. Agent handles are cached and are evicted only
/// by explicit lifecycle events, none of which is configuration-driven, so an operator who disables
/// memory live - plausibly <i>because</i> of a privacy concern or a poisoned store - gets no
/// enforcement at all until the handle happens to be torn down for an unrelated reason. Silent
/// non-enforcement of a security toggle is worse than an absent toggle, because the operator
/// believes the control worked.
/// </para>
/// <para>
/// <b>Why agent-scoped rather than taking an agent id.</b> The implementation is bound to one agent
/// at construction, so a tool cannot accidentally consult the wrong agent's configuration, and
/// <c>MemoryGetTool</c>-shaped tools that never carried an agent id do not have to grow one
/// purely to ask this question.
/// </para>
/// <para>
/// <b>Null is passthrough.</b> Every tool takes this as an optional dependency and treats
/// <see langword="null"/> as "enabled", matching the null-is-passthrough style used by the shared
/// store registry. That keeps registration-time gating as the fast path: a never-enabled agent
/// still gets no memory tools in its schema at all, and existing construction sites (tests,
/// embedders, satellite hosts) are unchanged.
/// </para>
/// </remarks>
public interface IMemoryEnablementProvider
{
    /// <summary>
    /// Returns whether memory is enabled for this provider's agent <i>right now</i>.
    /// </summary>
    /// <remarks>
    /// Called at the top of every memory tool invocation, so implementations must be cheap,
    /// thread-safe, and must read live configuration rather than a startup snapshot. An
    /// implementation that cannot determine the answer should fail closed and return
    /// <see langword="false"/>: refusing a call an operator may have intended to allow is
    /// recoverable, whereas permitting a call against a store they believe is switched off is not.
    /// </remarks>
    bool IsMemoryEnabled();
}
