using BotNexus.Tools;

namespace BotNexus.Gateway.Tests.Tools;

/// <summary>Checks real launch, executable lookup and temp writes rather than only dictionary contents.</summary>
public sealed class LocalChildEnvironmentLaunchTests
{
    [Fact]
    public async Task Shell_DefaultBoundaryPreservesWindowsRuntimeEssentialsWithoutPrefixSecrets()
    {
        if (!OperatingSystem.IsWindows()) return;
        string[] names = ["PROCESSOR_ARCHITECTURE", "ProgramFiles", "ProgramFiles(x86)"];
        var secretName = "ProgramFiles_SECRET_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(secretName, "synthetic-prefix-secret");
        try
        {
            var command = "@('PROCESSOR_ARCHITECTURE', 'ProgramFiles', 'ProgramFiles(x86)') | ForEach-Object { "
                + "[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string][Environment]::GetEnvironmentVariable($_))) }; "
                + "if ([Environment]::GetEnvironmentVariable('" + secretName + "')) { throw 'Prefix secret leaked' }";
            var tool = new ShellTool(shellPreference: ShellPreference.Pwsh);
            var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["command"] = command, ["timeout"] = 30 });
            var result = await tool.ExecuteAsync("windows-runtime-environment", args);
            result.Details.ShouldBeOfType<ShellTool.ShellToolDetails>().ExitCode.ShouldBe(0);
            var expected = names.Select(name => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                Environment.GetEnvironmentVariable(name) ?? string.Empty)));
            string.Join("\n", result.Content.Select(c => c.Value))
                .Replace("\r\n", "\n", StringComparison.Ordinal).Trim().ShouldBe(string.Join("\n", expected).Trim());
        }
        finally { Environment.SetEnvironmentVariable(secretName, null); }
    }

    [Fact]
    public async Task Shell_WindowsDotnetInfoCompletesWhenAvailableWithoutReturningSdkOutput()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string command = "if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { 'dotnet-unavailable' } else { "
            + "$info = & dotnet --info 2>&1; if ($LASTEXITCODE -ne 0) { throw 'dotnet --info failed' }; "
            + "if (-not ($info -match 'SDK')) { throw 'Missing SDK diagnostic' }; 'dotnet-info-ok' }";
        var tool = new ShellTool(shellPreference: ShellPreference.Pwsh);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["command"] = command, ["timeout"] = 30 });
        var result = await tool.ExecuteAsync("dotnet-info", args);
        result.Details.ShouldBeOfType<ShellTool.ShellToolDetails>().ExitCode.ShouldBe(0);
        var output = string.Join("\n", result.Content.Select(c => c.Value)).Trim();
        new[] { "dotnet-info-ok", "dotnet-unavailable" }.ShouldContain(output);
    }

    [Fact]
    public async Task Shell_DefaultBoundaryKeepsPathAndTemporaryDirectoryUsable()
    {
        // The command uses a PATH-resolved runtime and its own OS temp-directory API.
        var marker = "BN4749_TEMP_" + Guid.NewGuid().ToString("N");
        var command = "$p = [IO.Path]::Combine([IO.Path]::GetTempPath(), '" + marker
            + "'); try { [IO.File]::WriteAllText($p, 'ok'); [IO.File]::ReadAllText($p) } finally { [IO.File]::Delete($p) }";
        var tool = new ShellTool(shellPreference: ShellPreference.Pwsh);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?> { ["command"] = command });
        var result = await tool.ExecuteAsync("temp-probe", args);
        result.Details.ShouldBeOfType<ShellTool.ShellToolDetails>().ExitCode.ShouldBe(0);
        string.Join("\n", result.Content.Select(c => c.Value)).Trim().ShouldBe("ok");
    }
}
