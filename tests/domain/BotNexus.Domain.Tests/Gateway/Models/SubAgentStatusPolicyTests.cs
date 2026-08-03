using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Domain.Tests.Gateway.Models;

/// <summary>
/// Pins the shared terminal-status predicate introduced for #2677. Before it existed the answer
/// lived in two independently-maintained lists and #2656's <c>BudgetExhausted</c> was added to
/// only one of them, so budget-exhausted sub-agent workspaces were never reclaimed.
/// <para>
/// These tests are deliberately value-by-value rather than "the enum has six members": the
/// non-vacuity requirement (#2677 AC5) is that removing an arm from
/// <see cref="SubAgentStatusPolicy.IsTerminal"/> fails a test <b>by name</b>.
/// </para>
/// </summary>
public sealed class SubAgentStatusPolicyTests
{
    /// <summary>
    /// The regression this issue was filed for. #2656 added <c>BudgetExhausted</c>; a sub-agent
    /// that exhausted its turn budget is dead and must classify identically to Completed/Failed.
    /// </summary>
    [Fact]
    public void IsTerminal_BudgetExhausted_IsTerminal()
    {
        SubAgentStatusPolicy.IsTerminal(SubAgentStatus.BudgetExhausted).ShouldBeTrue(
            "A sub-agent that exhausted its turn budget (#2656) has ended - its workspace and "
            + "lifecycle registrations must be reclaimable (#2677).");
    }

    [Theory]
    [InlineData(SubAgentStatus.Completed)]
    [InlineData(SubAgentStatus.Failed)]
    [InlineData(SubAgentStatus.Killed)]
    [InlineData(SubAgentStatus.TimedOut)]
    [InlineData(SubAgentStatus.BudgetExhausted)]
    public void IsTerminal_EveryEndedStatus_IsTerminal(SubAgentStatus status)
    {
        SubAgentStatusPolicy.IsTerminal(status).ShouldBeTrue();
    }

    /// <summary>
    /// The second hand-maintained decision promoted by #2677: <c>DefaultSubAgentManager</c> chose
    /// between the SubAgentCompleted and SubAgentFailed lifecycle activities with its own
    /// <c>is Failed or TimedOut or BudgetExhausted</c> chain. Same drift exposure, same fix.
    /// </summary>
    [Theory]
    [InlineData(SubAgentStatus.Failed)]
    [InlineData(SubAgentStatus.TimedOut)]
    [InlineData(SubAgentStatus.BudgetExhausted)]
    public void IsUnsuccessfulTermination_EveryFaultStatus_IsUnsuccessful(SubAgentStatus status)
    {
        SubAgentStatusPolicy.IsUnsuccessfulTermination(status).ShouldBeTrue(
            "A run that ended in this state did not complete its work, so the parent must be "
            + "told it failed rather than silently seeing no lifecycle activity at all.");
    }

    /// <summary>
    /// Negative space for the fault predicate. <c>Completed</c> is a success, <c>Killed</c> is a
    /// deliberate operator action rather than a fault, and <c>Running</c> is not terminal at all
    /// - callers gate on <see cref="SubAgentStatusPolicy.IsTerminal"/> first. Pinned separately
    /// so widening the fault predicate cannot pass unnoticed.
    /// </summary>
    [Theory]
    [InlineData(SubAgentStatus.Running)]
    [InlineData(SubAgentStatus.Completed)]
    [InlineData(SubAgentStatus.Killed)]
    public void IsUnsuccessfulTermination_SuccessKilledAndRunning_AreNotFaults(SubAgentStatus status)
    {
        SubAgentStatusPolicy.IsUnsuccessfulTermination(status).ShouldBeFalse();
    }

    /// <summary>
    /// The two predicates answer different questions and must not be conflated: every
    /// unsuccessful termination is terminal, but not every terminal status is a fault.
    /// </summary>
    [Fact]
    public void UnsuccessfulTermination_IsAlwaysAStrictSubsetOfTerminal()
    {
        foreach (var status in Enum.GetValues<SubAgentStatus>())
        {
            if (SubAgentStatusPolicy.IsUnsuccessfulTermination(status))
            {
                SubAgentStatusPolicy.IsTerminal(status).ShouldBeTrue(
                    $"{status} is classified a fault but not terminal, which is incoherent.");
            }
        }

        SubAgentStatusPolicy.IsTerminal(SubAgentStatus.Completed).ShouldBeTrue();
        SubAgentStatusPolicy.IsUnsuccessfulTermination(SubAgentStatus.Completed).ShouldBeFalse(
            "Completed proves the subset is strict - if these two predicates ever agree on "
            + "every value, one of them is redundant and the seam has collapsed.");
    }

    [Fact]
    public void IsTerminal_Running_IsNotTerminal()
    {
        SubAgentStatusPolicy.IsTerminal(SubAgentStatus.Running).ShouldBeFalse(
            "A live sub-agent must never be classified terminal - that would let the reaper "
            + "delete a running sub-agent's files.");
    }

    /// <summary>
    /// Anti-vacuity guard for the two tests above: exactly one declared member (Running) is
    /// non-terminal. A predicate that returned <c>true</c> for everything, or <c>false</c> for
    /// everything, is caught here as well as by the value-by-value cases.
    /// </summary>
    [Fact]
    public void IsTerminal_ClassifiesExactlyOneDeclaredMember_AsNonTerminal()
    {
        var all = Enum.GetValues<SubAgentStatus>();
        all.Length.ShouldBeGreaterThan(1, "Reflection over SubAgentStatus returned too few members.");

        var nonTerminal = all.Where(s => !SubAgentStatusPolicy.IsTerminal(s)).ToArray();

        nonTerminal.ShouldBe([SubAgentStatus.Running]);
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("completed")]
    [InlineData("BUDGETEXHAUSTED")]
    [InlineData("BudgetExhausted")]
    [InlineData(" TimedOut ")]
    public void IsTerminalStatusName_KnownTerminalNames_AreTerminal_CaseInsensitively(string status)
    {
        SubAgentStatusPolicy.IsTerminalStatusName(status).ShouldBeTrue();
    }

    /// <summary>
    /// #2677 AC4 - fail-safe. A persisted status the current binary cannot interpret must never
    /// be treated as terminal: deleting a workspace whose state cannot be established is the one
    /// unrecoverable mistake this code can make.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Active")]
    [InlineData("Unknown")]
    [InlineData("SomeFutureStatus")]
    [InlineData("9")]
    [InlineData("1")]
    [InlineData("Completed,Failed")]
    [InlineData("Completed ")]
    public void IsTerminalStatusName_UnparseableOrUnknown_IsNotTerminal(string? status)
    {
        // "Completed " is included with a trailing space to pin that trimming is intentional and
        // does not accidentally admit padded junk; it is expected to trim to a valid name.
        var expected = status is "Completed ";

        SubAgentStatusPolicy.IsTerminalStatusName(status).ShouldBe(
            expected,
            $"'{status ?? "<null>"}' must not be treated as terminal unless it parses to a "
            + "declared terminal SubAgentStatus - fail-safe: never delete what you cannot "
            + "identify (#2677 AC4).");
    }

    [Fact]
    public void IsTerminalStatusName_Running_IsNotTerminal()
    {
        SubAgentStatusPolicy.IsTerminalStatusName("Running").ShouldBeFalse();
    }

    /// <summary>
    /// The string overload must agree with the enum overload for every declared member, so the
    /// reaper (which reads persisted text) and the manager (which holds the enum) can never
    /// diverge again.
    /// </summary>
    [Fact]
    public void IsTerminalStatusName_AgreesWithEnumOverload_ForEveryDeclaredMember()
    {
        foreach (var status in Enum.GetValues<SubAgentStatus>())
        {
            SubAgentStatusPolicy.IsTerminalStatusName(status.ToString())
                .ShouldBe(
                    SubAgentStatusPolicy.IsTerminal(status),
                    $"The string and enum overloads disagree for {status}.");
        }
    }
}
