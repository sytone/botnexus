using System.Diagnostics;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>Runs ownership/default-handling contracts against the copied production helper on the validation runner.</summary>
public sealed class ConversationActionMenuJavaScriptTests
{
    [Fact]
    public async Task ActualHelper_EnforcesOwnerLifetimesAndSynchronousNativeKeyboardContract()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "wwwroot", "js");
        var helper = Path.Combine(directory, "conversationActionMenu.js");
        var harness = Path.Combine(directory, "conversation-action-menu.test.mjs");
        File.Exists(helper).ShouldBeTrue("the actual production helper must be copied to test output");
        File.Exists(harness).ShouldBeTrue("the deterministic harness must be copied to test output");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "node",
            ArgumentList = { harness, helper },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        });
        process.ShouldNotBeNull();
        // Drain both streams concurrently so a failing harness cannot deadlock on a full pipe.
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        process.ExitCode.ShouldBe(0, $"Node output: {await output}{Environment.NewLine}{await error}");
    }
}
