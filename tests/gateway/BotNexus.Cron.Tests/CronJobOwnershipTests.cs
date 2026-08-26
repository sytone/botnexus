using BotNexus.Cron;
using BotNexus.Domain.Primitives;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #3575: the ownership rule lifted out of <c>CronTool.CanManage</c> so both the tool seam and the
/// REST seam derive their decision from one definition. <c>CronToolLifecycleTests</c> already covers
/// the tool's behaviour through the delegate; these assert the hoisted predicate directly so a
/// future edit to it cannot silently change both seams at once without a failing test.
/// </summary>
public sealed class CronJobOwnershipTests
{
    [Fact]
    public void CanManage_TargetAgent_IsTrue()
        => CronJobOwnership.CanManage(Job(agentId: "agent-a", createdBy: "someone-else"), AgentId.From("agent-a"))
            .ShouldBeTrue();

    [Fact]
    public void CanManage_CreatorAgent_IsTrue()
        => CronJobOwnership.CanManage(Job(agentId: "agent-other", createdBy: "agent-a"), AgentId.From("agent-a"))
            .ShouldBeTrue();

    [Fact]
    public void CanManage_UnrelatedAgent_IsFalse()
        => CronJobOwnership.CanManage(Job(agentId: "agent-a", createdBy: "tester"), AgentId.From("agent-b"))
            .ShouldBeFalse();

    [Fact]
    public void CanManage_CrossAgentCronAllowed_IsTrue()
        => CronJobOwnership.CanManage(Job(agentId: "agent-a", createdBy: "tester"), AgentId.From("agent-b"), allowCrossAgentCron: true)
            .ShouldBeTrue();

    [Fact]
    public void CanManageAsAny_MatchesOnOneScopedAgent()
        => CronJobOwnership.CanManageAsAny(Job(agentId: "agent-a", createdBy: "tester"), ["agent-x", "agent-a"])
            .ShouldBeTrue();

    /// <summary>
    /// An empty scope is NOT "everything": the caller-side unscoped/admin allowance is a separate,
    /// explicitly documented decision in the controller, and folding it in here would make this
    /// predicate silently permissive for every future caller.
    /// </summary>
    [Fact]
    public void CanManageAsAny_EmptyScope_IsFalse()
        => CronJobOwnership.CanManageAsAny(Job(agentId: "agent-a", createdBy: "tester"), []).ShouldBeFalse();

    [Fact]
    public void CanManageAsAny_BlankEntriesAreIgnored()
        => CronJobOwnership.CanManageAsAny(Job(agentId: "agent-a", createdBy: "tester"), ["", "   "]).ShouldBeFalse();

    private static CronJob Job(string agentId, string createdBy)
        => new()
        {
            Id = JobId.From("job-1"),
            Name = "Test Job",
            Schedule = "*/1 * * * *",
            ActionType = "agent-prompt",
            AgentId = AgentId.From(agentId),
            CreatedBy = createdBy,
            CreatedAt = DateTimeOffset.UtcNow
        };
}
