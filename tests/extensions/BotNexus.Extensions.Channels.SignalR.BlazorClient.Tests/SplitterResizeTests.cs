using System.Diagnostics;
using System.Reflection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class SplitterResizeTests
{
    private static readonly string s_outputPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;

    [Fact]
    public async Task Container_resize_reapplies_bounds_without_losing_preferred_width()
    {
        var harnessPath = Path.Combine(s_outputPath, "wwwroot", "js", "splitter.resize.test.mjs");
        var splitterPath = Path.Combine(s_outputPath, "wwwroot", "js", "splitter.js");
        var startInfo = new ProcessStartInfo("node")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(harnessPath);
        startInfo.ArgumentList.Add(splitterPath);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(
            process.ExitCode == 0,
            $"splitter resize regression failed (exit {process.ExitCode}){Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
    }
}
