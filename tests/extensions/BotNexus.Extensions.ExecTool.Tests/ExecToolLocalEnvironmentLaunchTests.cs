using BotNexus.Agent.Core.Tools;

namespace BotNexus.Extensions.ExecTool.Tests;

/// <summary>Observes executable lookup and runtime temp-directory use in real foreground/background children.</summary>
public sealed class ExecToolLocalEnvironmentLaunchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_DefaultBoundaryPreservesWindowsRuntimeEssentialsWithoutPrefixSecrets(bool background)
    {
        if (!OperatingSystem.IsWindows()) return;
        string[] names = ["PROCESSOR_ARCHITECTURE", "ProgramFiles", "ProgramFiles(x86)"];
        var secretName = "PROCESSOR_ARCHITECTURE_TOKEN_" + Guid.NewGuid().ToString("N");
        var registry = new BackgroundProcessRegistry();
        const string owner = "windows-runtime-environment-test";
        Environment.SetEnvironmentVariable(secretName, "synthetic-prefix-secret");
        try
        {
            var command = "@('PROCESSOR_ARCHITECTURE', 'ProgramFiles', 'ProgramFiles(x86)') | ForEach-Object { "
                + "[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string][Environment]::GetEnvironmentVariable($_))) }; "
                + "if ([Environment]::GetEnvironmentVariable('" + secretName + "')) { throw 'Prefix secret leaked' }";
            var tool = new ExecTool(null, null, owner, registry);
            var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
            {
                ["command"] = new[] { "pwsh", "-NoProfile", "-NonInteractive", "-Command", command },
                ["background"] = background, ["timeoutMs"] = 30000
            });
            var result = await tool.ExecuteAsync("windows-runtime-environment", args);
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
            var expected = names.Select(name => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                Environment.GetEnvironmentVariable(name) ?? string.Empty)));
            output.Replace("\r\n", "\n", StringComparison.Ordinal).Trim().ShouldBe(string.Join("\n", expected).Trim());
        }
        finally
        {
            registry.Clear(owner);
            Environment.SetEnvironmentVariable(secretName, null);
        }
    }

    [Fact]
    public async Task Execute_WindowsDotnetInfoCompletesWhenAvailableWithoutReturningSdkOutput()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string command = "if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { 'dotnet-unavailable' } else { "
            + "$info = & dotnet --info 2>&1; if ($LASTEXITCODE -ne 0) { throw 'dotnet --info failed' }; "
            + "if (-not ($info -match 'SDK')) { throw 'Missing SDK diagnostic' }; 'dotnet-info-ok' }";
        var tool = new ExecTool(null);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["command"] = new[] { "pwsh", "-NoProfile", "-NonInteractive", "-Command", command },
            ["timeoutMs"] = 30000
        });
        var result = await tool.ExecuteAsync("dotnet-info", args);
        result.Details.ShouldBeOfType<ExecTool.ExecToolDetails>().ExitCode.ShouldBe(0);
        var output = string.Join("\n", result.Content.Select(c => c.Value)).Trim();
        new[] { "dotnet-info-ok", "dotnet-unavailable" }.ShouldContain(output);
    }

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
