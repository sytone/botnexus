using System.Diagnostics;

namespace BotNexus.Architecture.Tests;

/// <summary>Exercises SDK initialization in a fresh process with the production host dependency graph.</summary>
public sealed class Agent365HostInitializationArchitectureTests
{
    [Fact]
    public async Task Agent365_auth_initializes_through_the_real_loader_without_network_or_credentials()
    {
        var host = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "compatibility-host-artifact.txt"))
            .Where(line => !string.IsNullOrWhiteSpace(line)).ShouldHaveSingleItem();
        var extension = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "compatibility-extension-artifacts.txt"))
            .Single(path => Path.GetFileName(path) == "BotNexus.Extensions.Channels.Agent365.dll");
        var directory = Path.GetDirectoryName(host) ?? throw new InvalidOperationException("Missing host directory.");
        var probe = Path.Combine(directory, "BotNexus.ExtensionCompatibilityProbe.dll");
        File.Exists(probe).ShouldBeTrue("Build the production-graph initialization probe.");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(host, ".runtimeconfig.json"),
                     "--depsfile", Path.ChangeExtension(host, ".deps.json"), probe, extension })
            start.ArgumentList.Add(argument);
        // Keep inherited test/runtime injection out of the production dependency graph.
        start.Environment.Remove("DOTNET_STARTUP_HOOKS");
        start.Environment.Remove("DOTNET_ADDITIONAL_DEPS");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start initialization probe.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        var output = await stdout;
        var errors = await stderr;
        process.ExitCode.ShouldBe(0, output + "\n" + errors);
        output.ShouldContain("Agent365 SDK auth initialized; host identities verified; network requests: 0");
    }
}
