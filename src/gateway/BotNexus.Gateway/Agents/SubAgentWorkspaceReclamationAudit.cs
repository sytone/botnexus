namespace BotNexus.Gateway.Agents;

/// <summary>
/// The single audit vocabulary shared by both sub-agent workspace reclamation routes (issue #3670
/// AC4): the lifecycle reclamation in <see cref="DefaultSubAgentManager"/> and the backstop sweep in
/// <see cref="SubAgentWorkspaceSweeper"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a shared constant rather than two similar log calls.</b> After #3569 there are two ways a
/// workspace can disappear, and an operator investigating a missing workspace must be able to answer
/// "what removed it, and which mechanism?" with ONE query. Two independently-worded messages make
/// that a two-query problem, and - as #3569's investigation showed, where 66 failures left no trace
/// naming the remover at all - the query nobody runs is the evidence nobody has.
/// </para>
/// <para>
/// The prefix is therefore compiled into both call sites from here. Each route appends its own
/// reason clause, so the shared prefix locates every reclamation and the suffix discriminates
/// lifecycle from backstop.
/// </para>
/// </remarks>
public static class SubAgentWorkspaceReclamationAudit
{
    /// <summary>
    /// The common leading text of every reclamation audit line, from either route. Operators grep
    /// for exactly this string; changing it is a breaking change to the audit trail.
    /// </summary>
    public const string MessagePrefix = "Sub-agent workspace reclaimed";

    /// <summary>
    /// Structured-logging template for the lifecycle route: reclamation performed as part of the
    /// run's own terminal transition, with no clock involved.
    /// </summary>
    public const string LifecycleTemplate =
        MessagePrefix + " for child agent '{ChildAgentId}' (route: lifecycle): "
        + "its run reached terminal state '{TerminalStatus}'.";

    /// <summary>
    /// Structured-logging template for the backstop route: an age-eligible husk whose sub-agent the
    /// liveness probe positively reported as not running.
    /// </summary>
    public const string BackstopTemplate =
        MessagePrefix + " for child agent '{ChildAgentId}' (route: backstop-sweep, {BytesReclaimed} bytes): "
        + "idle for {AgeHours:F1}h, exceeding the {RetentionHours:F1}h retention, "
        + "and its sub-agent is not registered as running.";
}
