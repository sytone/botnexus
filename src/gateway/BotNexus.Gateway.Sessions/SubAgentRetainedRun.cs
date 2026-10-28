using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Sessions;

// Persistence-only envelope. Public list/detail readers intentionally deserialize the bounded base.
internal sealed record SubAgentRetainedRun : SubAgentRunDetail
{
    public string? FullResultSummary { get; init; }
    public SubAgentRetainedRun() { }
    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    private SubAgentRetainedRun(SubAgentRunDetail detail) : base(detail) { }
    public static SubAgentRetainedRun Create(SubAgentInfo info) => new(SubAgentRunDetail.FromLive(info))
        { FullResultSummary = info.RetainedResultFullCopyUnavailable ? null : info.ResultSummary };
}
