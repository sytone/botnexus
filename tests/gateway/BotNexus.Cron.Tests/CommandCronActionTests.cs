using BotNexus.Cron.Actions;
using BotNexus.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace BotNexus.Cron.Tests;

public sealed class CommandCronActionTests
{
    private readonly CommandCronAction _action = new();

    [Fact]
    public void ActionType_IsCommand()
    {
        Assert.Equal("command", _action.ActionType);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsWhenShellCommandIsNull()
    {
        var context = BuildContext(shellCommand: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _action.ExecuteAsync(context));

        Assert.Contains("ShellCommand is null or empty", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsWhenShellCommandIsWhitespace()
    {
        var context = BuildContext(shellCommand: "   ");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _action.ExecuteAsync(context));

        Assert.Contains("ShellCommand is null or empty", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulCommand_DoesNotThrow()
    {
        var context = BuildContext(shellCommand: "Write-Output 'hello'");

        // Should complete without exception
        await _action.ExecuteAsync(context);
    }

    [Fact]
    public async Task ExecuteAsync_FailingCommand_ThrowsWithExitCode()
    {
        var context = BuildContext(shellCommand: "exit 42");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _action.ExecuteAsync(context));

        Assert.Contains("exited with code 42", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_TimedOutCommand_ThrowsTimeoutException()
    {
        // Command that sleeps longer than timeout
        var context = BuildContext(
            shellCommand: "Start-Sleep -Seconds 60",
            timeoutSeconds: 2);

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => _action.ExecuteAsync(context));

        Assert.Contains("timed out after 2s", ex.Message);
    }

    [Fact]
    public async Task RunProcessAsync_CapturesStdout()
    {
        var result = await CommandCronAction.RunProcessAsync(
            "Write-Output 'captured-output'",
            timeoutSeconds: 30,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains("captured-output", result.Output);
    }

    [Fact]
    public async Task RunProcessAsync_CapturesStderr()
    {
        var result = await CommandCronAction.RunProcessAsync(
            "Write-Error 'error-output'",
            timeoutSeconds: 30,
            CancellationToken.None);

        // Write-Error in pwsh exits with 0 but writes to stderr
        Assert.False(result.TimedOut);
        Assert.Contains("error-output", result.Output);
    }

    [Fact]
    public async Task RunProcessAsync_NonZeroExitCode()
    {
        var result = await CommandCronAction.RunProcessAsync(
            "exit 7",
            timeoutSeconds: 30,
            CancellationToken.None);

        Assert.Equal(7, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task RunProcessAsync_RespectsCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CommandCronAction.RunProcessAsync(
                "Start-Sleep -Seconds 60",
                timeoutSeconds: 120,
                cts.Token));
    }

    [Fact]
    public async Task ExecuteAsync_MetadataTimeoutOverridesDefault()
    {
        // Use a very short timeout via metadata
        var metadata = new Dictionary<string, object?> { ["timeoutSeconds"] = 1 };
        var context = BuildContext(
            shellCommand: "Start-Sleep -Seconds 60",
            metadata: metadata);

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => _action.ExecuteAsync(context));

        Assert.Contains("timed out after 1s", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_StringMetadataTimeout_ParsedCorrectly()
    {
        var metadata = new Dictionary<string, object?> { ["timeoutSeconds"] = "2" };
        var context = BuildContext(
            shellCommand: "Start-Sleep -Seconds 60",
            metadata: metadata);

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => _action.ExecuteAsync(context));

        Assert.Contains("timed out after 2s", ex.Message);
    }

    private static CronExecutionContext BuildContext(
        string? shellCommand,
        int? timeoutSeconds = null,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        var effectiveMetadata = metadata;
        if (timeoutSeconds.HasValue && metadata is null)
            effectiveMetadata = new Dictionary<string, object?> { ["timeoutSeconds"] = timeoutSeconds.Value };

        var job = new CronJob
        {
            Id = JobId.From("test-command-job"),
            Name = "Test Command",
            Schedule = "* * * * *",
            ActionType = "command",
            ShellCommand = shellCommand,
            Metadata = effectiveMetadata
        };

        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole())
            .BuildServiceProvider();

        return new CronExecutionContext
        {
            Job = job,
            RunId = RunId.From("run-1"),
            TriggeredAt = DateTimeOffset.UtcNow,
            TriggerType = CronTriggerType.Manual,
            Services = services
        };
    }
}
