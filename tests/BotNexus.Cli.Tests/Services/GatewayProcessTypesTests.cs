using BotNexus.Cli.Services;

namespace BotNexus.Cli.Tests.Services;

/// <summary>
/// Tests for the GatewayProcessTypes record types and enum values.
/// </summary>
public sealed class GatewayProcessTypesTests
{
    [Fact]
    public void GatewayStartOptions_DefaultAttached_IsFalse()
    {
        var options = new GatewayStartOptions(
            ExecutablePath: "test.dll");

        options.Attached.ShouldBeFalse();
        options.Arguments.ShouldBeNull();
    }

    [Fact]
    public void GatewayStartOptions_WithAllParameters_SetsCorrectly()
    {
        var options = new GatewayStartOptions(
            ExecutablePath: "test.dll",
            Arguments: "--verbose",
            Attached: true);

        options.ExecutablePath.ShouldBe("test.dll");
        options.Arguments.ShouldBe("--verbose");
        options.Attached.ShouldBeTrue();
    }

    [Fact]
    public void GatewayStartResult_WithSuccess_HasPid()
    {
        var result = new GatewayStartResult(
            Success: true,
            Pid: 12345,
            Message: "Started successfully");

        result.Success.ShouldBeTrue();
        result.Pid.ShouldBe(12345);
        result.Message.ShouldBe("Started successfully");
    }

    [Fact]
    public void GatewayStartResult_WithFailure_HasNullPid()
    {
        var result = new GatewayStartResult(
            Success: false,
            Pid: null,
            Message: "Failed to start");

        result.Success.ShouldBeFalse();
        result.Pid.ShouldBeNull();
        result.Message.ShouldBe("Failed to start");
    }

    [Fact]
    public void GatewayStopResult_WithSuccess_HasMessage()
    {
        var result = new GatewayStopResult(
            Success: true,
            Message: "Stopped successfully");

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe("Stopped successfully");
    }

    [Fact]
    public void GatewayStatus_NotRunning_HasNullPidAndUptime()
    {
        var status = new GatewayStatus(
            State: GatewayState.NotRunning,
            Pid: null,
            Uptime: null,
            Message: "Not running");

        status.State.ShouldBe(GatewayState.NotRunning);
        status.Pid.ShouldBeNull();
        status.Uptime.ShouldBeNull();
        status.Message.ShouldBe("Not running");
    }

    [Fact]
    public void GatewayStatus_Running_HasPidAndUptime()
    {
        var uptime = TimeSpan.FromMinutes(5);
        var status = new GatewayStatus(
            State: GatewayState.Running,
            Pid: 12345,
            Uptime: uptime,
            Message: "Running for 00:05:00");

        status.State.ShouldBe(GatewayState.Running);
        status.Pid.ShouldBe(12345);
        status.Uptime.ShouldBe(uptime);
        status.Message.ShouldContain("Running");
    }

    [Fact]
    public void GatewayStatus_Unknown_HasPidButNoUptime()
    {
        var status = new GatewayStatus(
            State: GatewayState.Unknown,
            Pid: 12345,
            Uptime: null,
            Message: "Process exists but cannot read details");

        status.State.ShouldBe(GatewayState.Unknown);
        status.Pid.ShouldBe(12345);
        status.Uptime.ShouldBeNull();
    }

    [Fact]
    public void GatewayState_HasExpectedValues()
    {
        // Verify enum has the expected values
        var values = Enum.GetValues<GatewayState>();
        values.ShouldContain(GatewayState.NotRunning);
        values.ShouldContain(GatewayState.Running);
        values.ShouldContain(GatewayState.Unknown);
        values.Length.ShouldBe(3);
    }

    [Fact]
    public void GatewayStartOptions_RecordEquality_WorksCorrectly()
    {
        var options1 = new GatewayStartOptions(
            ExecutablePath: "test.dll",
            Arguments: "--verbose");

        var options2 = new GatewayStartOptions(
            ExecutablePath: "test.dll",
            Arguments: "--verbose");

        var options3 = new GatewayStartOptions(
            ExecutablePath: "other.dll",
            Arguments: "--verbose");

        options1.ShouldBe(options2); // Should be equal
        options1.ShouldNotBe(options3); // Should not be equal
    }

    [Fact]
    public void GatewayStartResult_RecordEquality_WorksCorrectly()
    {
        var result1 = new GatewayStartResult(true, 123, "Success");
        var result2 = new GatewayStartResult(true, 123, "Success");
        var result3 = new GatewayStartResult(false, null, "Failure");

        result1.ShouldBe(result2);
        result1.ShouldNotBe(result3);
    }

    [Fact]
    public void GatewayStatus_RecordEquality_WorksCorrectly()
    {
        var uptime = TimeSpan.FromMinutes(5);
        var status1 = new GatewayStatus(GatewayState.Running, 123, uptime, "Running");
        var status2 = new GatewayStatus(GatewayState.Running, 123, uptime, "Running");
        var status3 = new GatewayStatus(GatewayState.NotRunning, null, null, "Not running");

        status1.ShouldBe(status2);
        status1.ShouldNotBe(status3);
    }
}
