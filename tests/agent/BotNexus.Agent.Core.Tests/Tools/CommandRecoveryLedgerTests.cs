using BotNexus.Agent.Core.Tools;

namespace BotNexus.AgentCore.Tests.Tools;

/// <summary>Command uncertainty survives tool recreation and later unrelated successes.</summary>
public sealed class CommandRecoveryLedgerTests
{
    [Fact]
    public void RecreatedScope_SeesUnfinishedCommand_AndSuccessCannotClearOlderUncertainty()
    {
        var owner = "synthetic-" + Guid.NewGuid().ToString("N");
        var earlier = CommandRecoveryLedger.Begin(owner);
        CommandRecoveryLedger.GetStatus(owner).ShouldBe("commands-unresolved");
        var later = CommandRecoveryLedger.Begin(owner);
        later.ConfirmCompleted();
        later.ConfirmCompleted();
        CommandRecoveryLedger.GetStatus(owner).ShouldBe("commands-unresolved");
        earlier.ConfirmCompleted();
        CommandRecoveryLedger.GetStatus(owner).ShouldBe("safe");
    }

    [Fact]
    public void DifferentOwners_DoNotShareUncertainty_AndAbsentScopeIsNotAnOsProcessCensus()
    {
        var owner = "synthetic-" + Guid.NewGuid().ToString("N");
        var command = CommandRecoveryLedger.Begin(owner);
        CommandRecoveryLedger.GetStatus(owner + "-other").ShouldBe("safe");
        CommandRecoveryLedger.GetStatus(owner).ShouldBe("commands-unresolved");
        command.ConfirmCompleted();
    }
}
