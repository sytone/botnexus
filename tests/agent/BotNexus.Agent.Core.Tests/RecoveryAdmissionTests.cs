using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Types;

namespace BotNexus.Agent.Core.Tests;

/// <summary>Maintenance cannot hide queued work or accept new steering while reserved.</summary>
public sealed class RecoveryAdmissionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QueuedInput_RefusesMaintenanceWithoutDroppingInput(bool steering)
    {
        var agent = new Agent(TestHelpers.CreateTestOptions());
        if (steering) agent.Steer(new UserMessage("preserve steering"));
        else agent.FollowUp(new UserMessage("preserve followup"));
        agent.TryReserveMaintenance().ShouldBeNull();
        agent.HasQueuedMessages.ShouldBeTrue();
    }

    [Fact]
    public void HeldMaintenance_RefusesNewInput_AndDisposalRestoresAdmission()
    {
        var agent = new Agent(TestHelpers.CreateTestOptions());
        var reservation = agent.TryReserveMaintenance();
        reservation.ShouldNotBeNull();
        Should.Throw<InvalidOperationException>(() => agent.Steer(new UserMessage("not admitted")));
        Should.Throw<InvalidOperationException>(() => agent.FollowUp(new UserMessage("not admitted")));
        agent.HasQueuedMessages.ShouldBeFalse();
        reservation.Dispose();
        agent.FollowUp(new UserMessage("admitted after release"));
        agent.HasQueuedMessages.ShouldBeTrue();
    }
}
