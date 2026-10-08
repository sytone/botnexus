using BotNexus.Agent.Core.Tools;

namespace BotNexus.Extensions.ExecTool.Tests;

/// <summary>Observes executable lookup and runtime temp-directory use in real foreground/background children.</summary>
public sealed class ExecToolLocalEnvironmentLaunchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_DefaultBoundaryKeepsPathAndTemporaryDirectoryUsable(bool background)
    {
        var registry = new BackgroundProcessRegistry();
        const string owner = "environment-launch-test";
        var marker = "BN4749_EXEC_TEMP_" + Guid.NewGuid().ToString("N");
        var commandText = "$p = [IO.Path]::Combine([IO.Path]::GetTempPath(), '" + marker
            + "'); try { [IO.File]::WriteAllText($p, 'ok'); [IO.File]::ReadAllText($p) } finally { [IO.File]::Delete($p) }";
        try
        {
            var tool = new ExecTool(null, null, owner, registry);
            var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
            {
                ["command"] = new[] { "pwsh", "-NoProfile", "-NonInteractive", "-Command", commandText },
                ["background"] = background
            });
            var result = await tool.ExecuteAsync("temp-probe", args);
            var details = result.Details.ShouldBeOfType<ExecTool.ExecToolDetails>();
            string output;
            if (background)
            {
                var child = registry.Get(owner, details.Pid ?? throw new InvalidOperationException("Missing child PID"));
                child.ShouldNotBeNull();
                await child.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(30));
                child.ExitCode.ShouldBe(0);
                output = child.GetOutput();
            }
            else
            {
                details.ExitCode.ShouldBe(0);
                output = string.Join("\n", result.Content.Select(c => c.Value));
            }
            output.Trim().ShouldBe("ok");
        }
        finally { registry.Clear(owner); }
    }
}
