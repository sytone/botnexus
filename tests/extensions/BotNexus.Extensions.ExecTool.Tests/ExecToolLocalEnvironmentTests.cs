using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;

namespace BotNexus.Extensions.ExecTool.Tests;

public sealed class ExecToolLocalEnvironmentTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Execute_ChildReceivesOnlyApprovedAmbientAndExplicitValues(bool background, bool passThrough)
    {
        var name = "BN4749_CUSTOM_" + Guid.NewGuid().ToString("N");
        var provider = "BN4749_PROVIDER_" + Guid.NewGuid().ToString("N");
        var gateway = "BN4749_GATEWAY_" + Guid.NewGuid().ToString("N");
        var github = "BN4749_GITHUB_" + Guid.NewGuid().ToString("N");
        var registry = new BackgroundProcessRegistry();
        Environment.SetEnvironmentVariable(name, "synthetic-custom");
        Environment.SetEnvironmentVariable(provider, "synthetic-provider");
        Environment.SetEnvironmentVariable(gateway, "synthetic-gateway");
        Environment.SetEnvironmentVariable(github, "synthetic-github");
        try
        {
            var policy = passThrough ? new LocalChildEnvironmentPolicy([name]) : null;
            var tool = new ExecTool(null, null, "environment-test", registry, environmentPolicy: policy);
            string[] command = OperatingSystem.IsWindows() ? ["cmd.exe", "/d", "/c", "set"] : ["/usr/bin/env"];
            var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
            {
                ["command"] = command, ["background"] = background,
                ["env"] = new Dictionary<string, string> { ["BN4749_EXPLICIT"] = "synthetic-explicit" }
            });
            var result = await tool.ExecuteAsync("environment", args);
            string output;
            if (background)
            {
                var details = result.Details.ShouldBeOfType<ExecTool.ExecToolDetails>();
                var child = registry.Get("environment-test", details.Pid ?? throw new InvalidOperationException("Missing child PID"));
                child.ShouldNotBeNull();
                await child.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(30));
                child.ExitCode.ShouldBe(0);
                output = child.GetOutput();
            }
            else
            {
                result.Details.ShouldBeOfType<ExecTool.ExecToolDetails>().ExitCode.ShouldBe(0);
                output = string.Join("\n", result.Content.Select(c => c.Value));
            }
            output.ShouldContain("BN4749_EXPLICIT=synthetic-explicit");
            output.ShouldNotContain("synthetic-provider");
            output.ShouldNotContain("synthetic-gateway");
            output.ShouldNotContain("synthetic-github");
            output.Contains("synthetic-custom", StringComparison.Ordinal).ShouldBe(passThrough);
        }
        finally
        {
            registry.Clear("environment-test");
            Environment.SetEnvironmentVariable(name, null);
            Environment.SetEnvironmentVariable(provider, null);
            Environment.SetEnvironmentVariable(gateway, null);
            Environment.SetEnvironmentVariable(github, null);
        }
    }
}
